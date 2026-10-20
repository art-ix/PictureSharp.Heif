#!/usr/bin/env python3
"""Fit models of Photos adjustments from original/export pairs (0.6.8 analysis, not product code).

Usage: scripts/fit-photos-edits.py samples/edit/street [samples/edit/stadion ...] [--jobs 6] [--out docs/measurements/photos-edits-0.6.8]

Input: samples/edit/<scene>/edits.tsv (export, original, adjustment, value; one adjustment per export) and the HEIC
files next to it. A second export of the same edit named <export stem>_2.heic is used as a repeat (noise floor and an
extra test point). Files are decoded with the oracle heif-dec (reference/tools, override with
PICTURESHARP_HEIF_TOOLS) into a private cache under artifacts/photos-edits-fit/<scene>/ (git-ignored). Per-pair fits
and predictions are cached there too (fits/), keyed by the model code and the HEIC files, so adding a scene or a
pair recomputes only the new work; --no-cache forces a full run.

Output (numbers only, no pixels): <out>-fit.tsv (per-pair residuals), <out>-curves.tsv (fitted parameters per
adjustment and slider value) and <out>-select.tsv (model selection per adjustment).

Method, fixed before looking at held-out pairs:
- Error metric: RMS over 8x8 block means of the 8-bit sRGB difference (suppresses HEVC noise; the export-to-export
  floor is reported), plus full-resolution RMS for the spatial filters.
- Candidate models per family; the family's model is chosen per adjustment by the 256-px checkerboard holdout on the
  training pairs only (fit on black tiles, score on white tiles).
- Value holdout: for -100..100 sliders fit +-50 and +-100 and predict +-25 by linear interpolation of the parameters
  between the identity at 0 and +-50, and +-50 from +-100 the same way (midpoint); for sliders measured only at +-50
  predict each sign from the other by negating the parameters (sign/symmetry test); single-value sliders have only
  the spatial holdout. With several scenes, each pair is also predicted from the same slider value fitted on the
  other scenes (transfer test: a fixed, scene-independent curve would transfer).
"""
import argparse, csv, hashlib, inspect, os, pickle, re, subprocess, sys
from multiprocessing import Pool
import numpy as np
import cv2

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KNOTS = np.linspace(0, 255, 33)
SIGMAS = [1, 2, 4, 8, 16, 32, 64, 128]
TILE = 256
LUMA = np.array([0.2126, 0.7152, 0.0722], np.float32)

FAMILY = {
    'exposure': 'tone', 'brightness': 'tone', 'shadows': 'tone', 'highlights': 'tone', 'contrast': 'tone',
    'black_point': 'tone', 'brilliance': 'tone', 'saturation': 'colour', 'vibrance': 'colour', 'warmth': 'wb',
    'tint': 'wb', 'vignette': 'vignette', 'sharpness': 'filter', 'definition': 'filter', 'noise_reduction': 'filter',
}
CANDIDATES = {
    'tone': ['curve_rgb', 'curve_rgb3', 'luma_ratio_lin', 'luma_add', 'curve_rgb_bands'],
    'colour': ['chroma_srgb', 'chroma_lin', 'curve_rgb3'],
    'wb': ['matrix_lin', 'gain_lin', 'curve_rgb3'],
    'vignette': ['radial_lin_circ', 'radial_lin_ellip', 'radial_srgb_ellip'],
    'filter': ['curve_rgb_bands', 'curve_rgb'],
}

# ---------------------------------------------------------------- decoding


def decode_all(scene, cache, names):
    tools = os.environ.get('PICTURESHARP_HEIF_TOOLS', os.path.join(REPO, 'reference/tools'))
    env = dict(os.environ, LD_LIBRARY_PATH=os.path.join(tools, 'lib'))
    os.makedirs(cache, exist_ok=True)
    from PIL import Image
    for n in names:
        npy = os.path.join(cache, os.path.splitext(n)[0] + '.npy')
        if os.path.exists(npy):
            continue
        png = npy[:-4] + '.png'
        subprocess.run([os.path.join(tools, 'bin/heif-dec'), os.path.join(scene, n), png], env=env, check=True,
                       capture_output=True)
        np.save(npy, np.asarray(Image.open(png).convert('RGB')))
        os.remove(png)


# ---------------------------------------------------------------- colour helpers

def srgb_to_lin(x):
    x = np.asarray(x, np.float32) / 255
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4).astype(np.float32)


def lin_to_srgb(x):
    x = np.clip(x, 0, 1)
    return (255 * np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1 / 2.4) - 0.055)).astype(np.float32)


def blk(a, k=8):
    h, w = a.shape[0] // k * k, a.shape[1] // k * k
    return a[:h, :w].reshape(h // k, k, w // k, k, *a.shape[2:]).mean((1, 3))


# ---------------------------------------------------------------- curve fitting

_HAT = None


def hat():
    global _HAT
    if _HAT is None:
        x = np.arange(256, dtype=np.float64)
        _HAT = np.maximum(0, 1 - np.abs(x[:, None] - KNOTS[None, :]) / (KNOTS[1] - KNOTS[0]))
    return _HAT


def fit_knots(w, s, smooth=1e-6):
    """Piecewise-linear curve on KNOTS minimising sum_k w_k (f(k) - s_k / w_k)^2 + smoothness."""
    b = hat()
    a = b.T @ (w[:, None] * b)
    d2 = np.diff(np.eye(len(KNOTS)), 2, axis=0)
    a += smooth * max(w.sum(), 1) * d2.T @ d2 + 1e-12 * np.eye(len(KNOTS))
    return np.linalg.solve(a, b.T @ s)


def lut(c):
    return (hat() @ c).astype(np.float32)


def bc(key, val=None, mask=None):
    if mask is not None:
        key = key[mask]
        val = None if val is None else val[mask]
    return np.bincount(key.ravel(), None if val is None else val.ravel(), 256).astype(np.float64)


# ---------------------------------------------------------------- global state (set in main, shared by fork)

G = {}


def setup(o):
    G['o8'] = o
    G['o'] = o.astype(np.float32)
    G['olin'] = srgb_to_lin(o)
    yf = G['o'] @ LUMA
    G['yf'] = yf
    G['yk'] = np.clip(np.rint(yf), 0, 255).astype(np.int32)
    G['ylin'] = G['olin'] @ LUMA
    mx = o.max(-1).astype(np.float32)
    G['sk'] = np.clip(np.rint(255 * (mx - o.min(-1)) / np.maximum(mx, 1)), 0, 255).astype(np.int32)
    h, w = o.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    dy, dx = yy - (h - 1) / 2, xx - (w - 1) / 2
    G['rk_circ'] = np.clip(np.rint(255 * np.hypot(dy, dx) / np.hypot(h / 2, w / 2)), 0, 255).astype(np.int32)
    G['rk_ellip'] = np.clip(np.rint(255 * np.hypot(dy / (h / 2), dx / (w / 2)) / np.sqrt(2)), 0, 255).astype(np.int32)
    tiles = ((np.arange(h)[:, None] // TILE) + (np.arange(w)[None, :] // TILE)) % 2 == 0
    G['fitmask'] = tiles
    G['evalmask_blk'] = blk(tiles.astype(np.float32)[..., None])[..., 0] < 0.5
    q = o >> 3
    G['idx3'] = (q[..., 0].astype(np.int32) * 32 + q[..., 1]) * 32 + q[..., 2]
    # Gaussian band-pass stack per channel: b_i = G_{s(i-1)} - G_{s(i)}, G_0 = original
    prev = G['o']
    bands = []
    for s in SIGMAS:
        if s >= 16:  # blur large scales on a downsampled copy
            f = s // 8
            small = cv2.resize(G['o'], (w // f, h // f), interpolation=cv2.INTER_AREA)
            cur = cv2.resize(cv2.GaussianBlur(small, (0, 0), s / f), (w, h), interpolation=cv2.INTER_LINEAR)
        else:
            cur = cv2.GaussianBlur(G['o'], (0, 0), s)
        bands.append((prev - cur).astype(np.float32))
        prev = cur
    G['bands'] = bands


# ---------------------------------------------------------------- models
# Parameters are stored relative to the identity (0 = no change), so slider interpolation is linear and the
# antisymmetry test negates them.

def fit_curve_rgb(t, m, sep=False):
    o8, o = G['o8'], G['o']
    if sep:
        return {'d3': np.stack([fit_knots(bc(o8[..., c], None, m), bc(o8[..., c], t[..., c], m)) for c in range(3)])}
    w = sum(bc(o8[..., c], None, m) for c in range(3))
    s = sum(bc(o8[..., c], t[..., c], m) for c in range(3))
    return {'d': fit_knots(w, s)}


def pred_curve_rgb(p):
    o8, o = G['o8'], G['o']
    if 'd3' in p:
        return np.stack([o[..., c] + lut(p['d3'][c])[o8[..., c]] for c in range(3)], -1)
    return o + lut(p['d'])[o8]


def fit_bands(t, m):
    """Least squares gains per band (shared by the channels) for target t, on mask m."""
    n = len(G['bands'])
    a, r = np.zeros((n, n)), np.zeros(n)
    for y0 in range(0, t.shape[0], 256):  # chunked normal equations in float64
        mm = m[y0:y0 + 256]
        x = np.stack([b[y0:y0 + 256][mm] for b in G['bands']], -1).reshape(-1, n).astype(np.float64)
        a += x.T @ x
        r += x.T @ t[y0:y0 + 256][mm].reshape(-1).astype(np.float64)
    return np.linalg.solve(a + 1e-9 * np.trace(a) * np.eye(n), r)


def pred_bands(g):
    out = np.zeros_like(G['o'])
    for gi, b in zip(g, G['bands']):
        out += np.float32(gi) * b
    return out


def fit_model(name, e, m):
    """Fit model `name` to export e (float32 sRGB) on pixel mask m (None = all)."""
    o, o8 = G['o'], G['o8']
    t = e - o
    if name == 'curve_rgb':
        return fit_curve_rgb(t, m)
    if name == 'curve_rgb3':
        return fit_curve_rgb(t, m, sep=True)
    if name == 'curve_rgb_bands':
        p = fit_curve_rgb(t, m)
        for _ in range(2):  # alternate curve and band gains
            g = fit_bands(t - lut(p['d'])[o8], m if m is not None else np.ones(o8.shape[:2], bool))
            p = fit_curve_rgb(t - pred_bands(g), m)
        p['bands'] = g
        return p
    if name == 'luma_add':
        return {'d': fit_knots(bc(G['yk'], None, m), bc(G['yk'], t.mean(-1), m))}
    if name == 'luma_ratio_lin':
        el, ol = srgb_to_lin(np.clip(np.rint(e), 0, 255).astype(np.uint8)), G['olin']
        w = bc(G['yk'], (ol * ol).sum(-1), m)
        s = bc(G['yk'], (el * ol).sum(-1), m)
        r = np.where(w > 0, s / np.maximum(w, 1e-12), 1)
        return {'lr': fit_knots(w, w * np.log(np.maximum(r, 1e-3)))}
    if name in ('chroma_srgb', 'chroma_lin'):
        if name == 'chroma_srgb':
            yo, co = G['yf'], o - G['yf'][..., None]
            ye = e @ LUMA
            ce = e - ye[..., None]
            d = fit_knots(bc(G['yk'], None, m), bc(G['yk'], ye - yo, m))
        else:
            el = srgb_to_lin(np.clip(np.rint(e), 0, 255).astype(np.uint8))
            yo, co = G['ylin'], G['olin'] - G['ylin'][..., None]
            ye = el @ LUMA
            ce = el - ye[..., None]
            w = bc(G['yk'], yo * yo, m)
            d = fit_knots(w, w * np.log(np.maximum(bc(G['yk'], ye * yo, m) / np.maximum(w, 1e-12), 1e-3)))
        w = bc(G['sk'], (co * co).sum(-1), m)
        g = fit_knots(w, bc(G['sk'], (ce * co).sum(-1), m) - w)
        return {'d' if name == 'chroma_srgb' else 'lr': d, 'g': g, 'space': name}
    if name in ('matrix_lin', 'gain_lin'):
        el = srgb_to_lin(np.clip(np.rint(e), 0, 255).astype(np.uint8))
        ok = (G['o8'].max(-1) < 250) & (e.max(-1) < 250) & (G['o8'].min(-1) > 2)
        if m is not None:
            ok &= m
        x, y = G['olin'][ok].astype(np.float64), el[ok].astype(np.float64)
        if name == 'matrix_lin':
            mat = np.linalg.solve(x.T @ x, x.T @ y).T
        else:
            mat = np.diag((x * y).sum(0) / (x * x).sum(0))
        return {'M': mat - np.eye(3)}
    if name.startswith('radial_'):
        rk = G['rk_circ'] if name.endswith('circ') else G['rk_ellip']
        if '_lin_' in name:
            el, ol = srgb_to_lin(np.clip(np.rint(e), 0, 255).astype(np.uint8)), G['olin']
        else:
            el, ol = e, o
        w = bc(rk, (ol * ol).sum(-1), m)
        s = bc(rk, (el * ol).sum(-1), m)
        return {'lg': fit_knots(w, w * np.log(np.maximum(s / np.maximum(w, 1e-12), 1e-3)))}
    raise ValueError(name)


def predict(name, p):
    o, o8 = G['o'], G['o8']
    if name in ('curve_rgb', 'curve_rgb3'):
        out = pred_curve_rgb(p)
    elif name == 'curve_rgb_bands':
        out = pred_curve_rgb(p) + pred_bands(p['bands'])
    elif name == 'luma_add':
        out = o + lut(p['d'])[G['yk']][..., None]
    elif name == 'luma_ratio_lin':
        out = lin_to_srgb(G['olin'] * np.exp(lut(p['lr']))[G['yk']][..., None])
    elif name == 'chroma_srgb':
        out = (G['yf'] + lut(p['d'])[G['yk']])[..., None] + (1 + lut(p['g']))[G['sk']][..., None] * (o - G['yf'][..., None])
    elif name == 'chroma_lin':
        yl = G['ylin'][..., None]
        out = lin_to_srgb(yl * np.exp(lut(p['lr']))[G['yk']][..., None]
                          + (1 + lut(p['g']))[G['sk']][..., None] * (G['olin'] - yl))
    elif name in ('matrix_lin', 'gain_lin'):
        out = lin_to_srgb(G['olin'] @ (np.eye(3) + p['M']).T.astype(np.float32))
    elif name.startswith('radial_'):
        rk = G['rk_circ'] if name.endswith('circ') else G['rk_ellip']
        g = np.exp(lut(p['lg']))[rk][..., None]
        out = lin_to_srgb(G['olin'] * g) if '_lin_' in name else o * g
    else:
        raise ValueError(name)
    return np.clip(out, 0, 255)


def scale_params(p, k):
    return {a: (v * k if isinstance(v, np.ndarray) else v) for a, v in p.items()}


# ---------------------------------------------------------------- metrics

def metrics(pred, e, evalblk=None):
    d = pred - e
    db = blk(d)
    sq = (db ** 2).sum(-1) / 3
    out = {'rms8': float(np.sqrt(sq.mean())), 'rms1': float(np.sqrt((d.astype(np.float64) ** 2).mean()))}
    if evalblk is not None:
        out['rms8_tiles'] = float(np.sqrt(sq[evalblk].mean()))
    out['dY_pred'] = float((pred @ LUMA).mean() - G['yf'].mean())
    return out


def bound3d(e):
    idx = G['idx3']
    cnt = np.maximum(np.bincount(idx.ravel(), None, 32768), 1)
    pred = np.stack([(np.bincount(idx.ravel(), e[..., c].ravel(), 32768) / cnt)[idx] for c in range(3)], -1)
    return float(np.sqrt((blk(pred - e) ** 2).mean()))


# ---------------------------------------------------------------- per-pair work

def load_export(cache, n):
    return np.load(os.path.join(cache, os.path.splitext(n)[0] + '.npy')).astype(np.float32)


def work_pair(args):
    cache, row = args
    e = load_export(cache, row['export'])
    fam = FAMILY[row['adjustment']]
    res = {'baseline': metrics(G['o'], e, G['evalmask_blk']), 'bound3d': bound3d(e),
           'dY': float((e @ LUMA).mean() - G['yf'].mean()),
           'dY_p99': float(np.percentile(e @ LUMA, 99) - np.percentile(G['yf'], 99)), 'models': {}}
    for name in CANDIDATES[fam]:
        p_full = fit_model(name, e, None)
        p_half = fit_model(name, e, G['fitmask'])
        m_full = metrics(predict(name, p_full), e)
        m_half = metrics(predict(name, p_half), e, G['evalmask_blk'])
        res['models'][name] = {'params': p_full, 'rms8': m_full['rms8'], 'rms1': m_full['rms1'],
                               'rms8_tiles': m_half['rms8_tiles'], 'dY_pred': m_full['dY_pred']}
    print(f"  {row['adjustment']:16s}{row['value']:>5s} done", file=sys.stderr, flush=True)
    return res


def work_repeat(args):
    cache, row = args
    e = load_export(cache, row['export'])
    return {'baseline': metrics(G['o'], e), 'dY': float((e @ LUMA).mean() - G['yf'].mean()),
            'dY_p99': float(np.percentile(e @ LUMA, 99) - np.percentile(G['yf'], 99))}


def work_predict(args):
    cache, name, p, row = args
    e = load_export(cache, row['export'])
    return metrics(predict(name, p), e)


# ---------------------------------------------------------------- main

def load_scene(scene, only):
    with open(os.path.join(scene, 'edits.tsv')) as f:
        rows = list(csv.DictReader([l for l in f if not l.startswith('#')], delimiter='\t'))
    if only:
        rows = [r for r in rows if r['adjustment'] in only.split(',')]
    originals = sorted({r['original'] for r in rows})
    if len(originals) != 1:
        sys.exit(f'{scene}: one original per scene expected')
    name = os.path.basename(scene.rstrip('/'))
    repeats = []
    for r in rows:
        r['scene'], r['v'], r['kind'] = name, int(r['value']), 'export'
        stem = os.path.splitext(r['export'])[0]
        for ext in ('.heic', '.HEIC'):
            if os.path.exists(os.path.join(scene, stem + '_2' + ext)):
                repeats.append(dict(r, export=stem + '_2' + ext, repeat_of=r['export'], kind='repeat'))
    return {'name': name, 'path': scene, 'cache': os.path.join(REPO, 'artifacts/photos-edits-fit', name),
            'original': originals[0], 'rows': rows, 'repeats': repeats}


def activate(sc):
    if sc.get('active'):
        return
    for other in list(G.get('scenes', ())):
        other['active'] = False
    G.clear()
    setup(np.load(os.path.join(sc['cache'], os.path.splitext(sc['original'])[0] + '.npy')))
    G['scenes'] = [sc]
    sc['active'] = True


# ---------------------------------------------------------------- result cache
# Fits and predictions are cached per pair under artifacts/photos-edits-fit/<scene>/fits/. The key covers the
# source of every function that computes them, the constants, and size + mtime of the HEIC files, so editing the
# model code or replacing a sample recomputes only what it affects.

def code_key():
    fns = [srgb_to_lin, lin_to_srgb, blk, hat, fit_knots, lut, bc, setup, fit_curve_rgb, pred_curve_rgb, fit_bands,
           pred_bands, fit_model, predict, metrics, bound3d, work_pair, work_predict]
    src = ''.join(inspect.getsource(f) for f in fns) + repr((KNOTS.tolist(), SIGMAS, TILE, LUMA.tolist(), CANDIDATES))
    return hashlib.sha256(src.encode()).hexdigest()


def file_key(path):
    st = os.stat(path)
    return f'{os.path.basename(path)}:{st.st_size}:{st.st_mtime_ns}'


def params_key(p):
    h = hashlib.sha256()
    for k in sorted(p):
        v = p[k]
        h.update(k.encode() + (v.tobytes() if isinstance(v, np.ndarray) else repr(v).encode()))
    return h.hexdigest()


def cache_path(sc, kind, parts):
    d = os.path.join(sc['cache'], 'fits')
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, f"{kind}-{hashlib.sha256('|'.join(parts).encode()).hexdigest()[:24]}.pkl")


def run_cached(sc, kind, items, keyparts, fn, jobs, stats):
    """Map fn over items with a pickle cache per item; fn runs in a pool on the scene's globals."""
    base = [CODE, file_key(os.path.join(sc['path'], sc['original']))]
    paths = [cache_path(sc, kind, base + keyparts(it)) for it in items]
    out = [None] * len(items)
    todo = []
    for i, p in enumerate(paths):
        if os.path.exists(p):
            with open(p, 'rb') as f:
                out[i] = pickle.load(f)
        else:
            todo.append(i)
    stats[kind] = stats.get(kind, [0, 0])
    stats[kind][0] += len(items) - len(todo)
    stats[kind][1] += len(todo)
    if todo:
        activate(sc)
        with Pool(jobs) as pool:
            for i, res in zip(todo, pool.map(fn, [items[i] for i in todo])):
                out[i] = res
                with open(paths[i] + '.tmp', 'wb') as f:
                    pickle.dump(res, f)
                os.replace(paths[i] + '.tmp', paths[i])
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('scenes', nargs='+')
    ap.add_argument('--jobs', type=int, default=6)
    ap.add_argument('--only', help='comma-separated adjustments (quick runs)')
    ap.add_argument('--no-cache', action='store_true', help='ignore and overwrite cached fits')
    ap.add_argument('--out', default=os.path.join(REPO, 'docs/measurements/photos-edits-0.6.8'))
    a = ap.parse_args()
    scenes = [load_scene(os.path.abspath(s), a.only) for s in a.scenes]
    global CODE
    CODE = code_key() + ('-nocache' + str(os.getpid()) if a.no_cache else '')
    stats = {}
    ek = lambda sc, r: [file_key(os.path.join(sc['path'], r['export'])), r['adjustment']]

    floors = []
    for sc in scenes:
        decode_all(sc['path'], sc['cache'], [sc['original']] + [r['export'] for r in sc['rows'] + sc['repeats']])
        for r in sc['repeats']:
            x, y = load_export(sc['cache'], r['repeat_of']), load_export(sc['cache'], r['export'])
            floors.append({'scene': sc['name'], 'pair': r['export'], 'rms8': float(np.sqrt((blk(x - y) ** 2).mean())),
                           'rms1': float(np.sqrt(((x - y).astype(np.float64) ** 2).mean())),
                           'max': float(np.abs(x - y).max())})
        res = run_cached(sc, 'pair', [(sc['cache'], r) for r in sc['rows']], lambda it: ek(sc, it[1]), work_pair,
                         a.jobs, stats)
        for r, x in zip(sc['rows'], res):
            r['res'] = x
        res = run_cached(sc, 'repeat', [(sc['cache'], r) for r in sc['repeats']], lambda it: ek(sc, it[1]),
                         work_repeat, a.jobs, stats)
        for r, x in zip(sc['repeats'], res):
            r['res'] = x

    # slider-value holdout roles per scene and adjustment
    by_adj = {}
    for sc in scenes:
        for r in sc['rows']:
            by_adj.setdefault(r['adjustment'], {}).setdefault(sc['name'], []).append(r)
    schemes = {}
    for adj, per in by_adj.items():
        for sname, rs in per.items():
            vals = {r['v'] for r in rs}
            if vals == {-50, 50}:
                scheme = 'antisym'
            elif {50, 100} <= vals or {-50, -100} <= vals:
                scheme = 'interp'
            else:
                scheme = 'spatial'
            schemes[adj, sname] = scheme
            for r in rs:
                r['role'] = 'test' if scheme == 'interp' and abs(r['v']) == 25 else 'train'

    # model selection: checkerboard holdout on training pairs of every scene
    sel = {}
    for adj, per in sorted(by_adj.items()):
        fam = FAMILY[adj]
        train = [r for rs in per.values() for r in rs if r['role'] == 'train']
        score = {n: float(np.mean([r['res']['models'][n]['rms8_tiles'] for r in train])) for n in CANDIDATES[fam]}
        sel[adj] = {'family': fam, 'model': min(score, key=score.get), 'score': score,
                    'schemes': ','.join(f'{s}:{schemes[adj, s]}' for s in sorted(per))}

    # prediction tasks: (target row, kind, source label, params)
    tasks = []
    for adj, per in by_adj.items():
        best = sel[adj]['model']
        for sname, rs in per.items():
            bv = {r['v']: r for r in rs}
            reps = [x for sc in scenes if sc['name'] == sname for x in sc['repeats'] if x['adjustment'] == adj]
            prm = lambda v: bv[v]['res']['models'][best]['params']
            for r in rs + reps:
                v = r['v']
                sg = 1 if v > 0 else -1
                if schemes[adj, sname] == 'interp':
                    if abs(v) == 25 and 50 * sg in bv:
                        tasks.append((r, 'heldout', f'lerp(0,{50 * sg:+d})', scale_params(prm(50 * sg), 0.5)))
                    if abs(v) == 50 and 100 * sg in bv:
                        tasks.append((r, 'midpoint', f'lerp(0,{100 * sg:+d})', scale_params(prm(100 * sg), 0.5)))
                    # post-hoc hypothesis (formed after seeing exposure +-25, so not a clean holdout): a fixed
                    # even part E (any non-zero setting re-renders) plus an odd part linear in v below 50:
                    # p(25) = E + (p(50) - p(-50)) / 4, E = mean over 50 and 100 of (p(v) + p(-v)) / 2
                    if abs(v) == 25 and all(x in bv for x in (-100, -50, 50, 100)):
                        ev = {k: sum(prm(u)[k] + prm(-u)[k] for u in (50, 100)) / 4 for k in prm(50)
                              if isinstance(prm(50)[k], np.ndarray)}
                        p = {k: (ev[k] + sg * (prm(50)[k] - prm(-50)[k]) / 4 if k in ev else x)
                             for k, x in prm(50).items()}
                        tasks.append((r, 'posthoc_evenodd', 'E(50,100)+odd(50)/2', p))
                elif schemes[adj, sname] == 'antisym' and -v in bv:
                    tasks.append((r, 'antisym', f'neg({-v:+d})', scale_params(prm(-v), -1)))
                # cross-scene transfer: same adjustment and value fitted on another scene
                for other, ors in per.items():
                    if other == sname:
                        continue
                    src = {x['v']: x for x in ors}
                    if v in src:
                        tasks.append((r, 'transfer', f'{other}:{v:+d}', src[v]['res']['models'][best]['params']))
    for sc in scenes:
        mine = [t for t in tasks if t[0]['scene'] == sc['name']]
        if not mine:
            continue
        items = [(sc['cache'], sel[t[0]['adjustment']]['model'], t[3], t[0]) for t in mine]
        out = run_cached(sc, 'pred', items, lambda it: ek(sc, it[3]) + [it[1], params_key(it[2])], work_predict,
                         a.jobs, stats)
        for t, m in zip(mine, out):
            t[0].setdefault('preds', []).append((t[1], t[2], m))

    # ---------------------------------------------------------------- write outputs
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    allm = sorted({m for c in CANDIDATES.values() for m in c})
    with open(a.out + '-select.tsv', 'w') as f:
        f.write('# model selection per adjustment: mean checkerboard-holdout rms8 over training pairs of all scenes\n')
        f.write('adjustment\tfamily\tschemes\tchosen\t' + '\t'.join(allm) + '\n')
        for adj, s in sorted(sel.items()):
            f.write(f"{adj}\t{s['family']}\t{s['schemes']}\t{s['model']}\t" +
                    '\t'.join(f"{s['score'][m]:.3f}" if m in s['score'] else '' for m in allm) + '\n')

    with open(a.out + '-fit.tsv', 'w') as f:
        f.write('# rms8 = RMS of 8x8 block-mean sRGB difference (8-bit levels); rms1 = full-resolution RMS; '
                'baseline = unedited original vs export; bound3d = in-sample 32^3 RGB LUT (best global per-pixel '
                'map); fit = chosen model fitted on this pair; tiles = fitted on checkerboard tiles, scored on the '
                'others; pred = parameters not fitted on this pair: heldout = +-25 from +-50, midpoint = +-50 from '
                '+-100, antisym = negated opposite sign, transfer = same value fitted on another scene\n')
        for fl in floors:
            f.write(f"# noise floor ({fl['scene']}, repeat export {fl['pair']}): rms8 {fl['rms8']:.3f} "
                    f"rms1 {fl['rms1']:.3f} max {fl['max']:.0f}\n")
        f.write('scene\texport\tadjustment\tvalue\trole\tmodel\tdY\tdY_p99\tbaseline_rms8\tbaseline_rms1\t'
                'bound3d_rms8\tfit_rms8\tfit_rms1\ttiles_rms8\tpred_kind\tpred_from\tpred_rms8\tpred_rms1\t'
                'pred_dY\tpred_vs_baseline\n')
        for sc in scenes:
            for r in sorted(sc['rows'] + sc['repeats'], key=lambda x: (x['adjustment'], x['v'], x['kind'])):
                res, best = r['res'], sel[r['adjustment']]['model']
                if r['kind'] == 'repeat':
                    fit = '\t\t\t'
                    role = 'repeat'
                else:
                    mm = res['models'][best]
                    fit = f"{res['bound3d']:.3f}\t{mm['rms8']:.3f}\t{mm['rms1']:.3f}\t{mm['rms8_tiles']:.3f}"
                    role = r['role']
                base = (f"{sc['name']}\t{r['export']}\t{r['adjustment']}\t{r['v']}\t{role}\t{best}\t{res['dY']:.2f}\t"
                        f"{res['dY_p99']:.2f}\t{res['baseline']['rms8']:.3f}\t{res['baseline']['rms1']:.3f}\t{fit}")
                preds = r.get('preds', [])
                if not preds:
                    f.write(base + '\t\t\t\t\t\t\n')
                for kind, src, p in preds:
                    f.write(base + f"\t{kind}\t{src}\t{p['rms8']:.3f}\t{p['rms1']:.3f}\t{p['dY_pred']:.2f}\t"
                            f"{p['rms8'] / res['baseline']['rms8']:.3f}\n")

    with open(a.out + '-curves.tsv', 'w') as f:
        f.write('# fitted parameters of the chosen model per scene, adjustment and slider value, relative to the '
                'identity: d = additive delta (8-bit sRGB) at input knot x, pooled over channels; d_r/d_g/d_b per '
                'channel; lr = log ratio (linear light) keyed by sRGB luma; g = chroma gain - 1 keyed by '
                'saturation (max-min)/max * 255; lg = log gain keyed by normalised radius * 255; band_<sigma> = '
                'gain of the Gaussian band G(sigma/2) - G(sigma); M_ij = 3x3 linear-light matrix - I\n')
        f.write('scene\tadjustment\tvalue\tmodel\tparam\tx\ty\n')
        for sc in scenes:
            for r in sorted(sc['rows'], key=lambda x: (x['adjustment'], x['v'])):
                best = sel[r['adjustment']]['model']
                pre = f"{sc['name']}\t{r['adjustment']}\t{r['v']}\t{best}"
                for k, v in r['res']['models'][best]['params'].items():
                    if not isinstance(v, np.ndarray):
                        continue
                    if k == 'bands':
                        f.writelines(f"{pre}\tband_{s}\t\t{g:.5f}\n" for s, g in zip(SIGMAS, v))
                    elif k == 'M':
                        f.writelines(f"{pre}\tM_{i}{j}\t\t{v[i, j]:.5f}\n" for i in range(3) for j in range(3))
                    elif k == 'd3':
                        f.writelines(f"{pre}\td_{cn}\t{x:.2f}\t{y:.4f}\n" for c, cn in enumerate('rgb')
                                     for x, y in zip(KNOTS, v[c]))
                    else:
                        f.writelines(f"{pre}\t{k}\t{x:.2f}\t{y:.5f}\n" for x, y in zip(KNOTS, v))

    with open(a.out + '-candidates.tsv', 'w') as f:
        f.write('# every candidate model per pair: in-sample rms8, checkerboard-holdout rms8, full-res rms1\n')
        f.write('scene\texport\tadjustment\tvalue\tmodel\tfit_rms8\ttiles_rms8\tfit_rms1\n')
        for sc in scenes:
            for r in sc['rows']:
                for n, mm in r['res']['models'].items():
                    f.write(f"{sc['name']}\t{r['export']}\t{r['adjustment']}\t{r['v']}\t{n}\t{mm['rms8']:.3f}\t"
                            f"{mm['rms8_tiles']:.3f}\t{mm['rms1']:.3f}\n")
    summarise(a.out)
    print('cache (hit, computed):', ', '.join(f'{k} {v[0]}/{v[1]}' for k, v in stats.items()), file=sys.stderr)
    print('written', a.out + '-{fit,curves,select,candidates,summary}.tsv', file=sys.stderr)


def summarise(out):
    """Per scene and adjustment: mean rms8 of baseline, in-sample fit and each prediction kind (from -fit.tsv)."""
    with open(out + '-fit.tsv') as f:
        rows = list(csv.DictReader([l for l in f if not l.startswith('#')], delimiter='\t'))
    groups = {}
    for r in rows:
        g = groups.setdefault((r['adjustment'], r['scene']), {'model': r['model'], 'pairs': {}, 'pred': {}})
        if r['role'] != 'repeat':
            g['pairs'][r['export']] = (float(r['baseline_rms8']), float(r['fit_rms8']), float(r['tiles_rms8']),
                                       float(r['bound3d_rms8']))
        if r['pred_kind']:
            g['pred'].setdefault(r['pred_kind'], []).append((float(r['pred_rms8']), float(r['baseline_rms8'])))
    kinds = ['heldout', 'midpoint', 'antisym', 'transfer', 'posthoc_evenodd']
    with open(out + '-summary.tsv', 'w') as f:
        f.write('# mean rms8 per scene and adjustment; <kind>_ratio = sum(pred rms8) / sum(baseline rms8) over the '
                'predicted pairs, <kind>_worse = pairs where the prediction is farther from the export than the '
                'unedited original\n')
        f.write('adjustment\tscene\tmodel\tpairs\tbaseline\tbound3d\tfit\ttiles\t' +
                '\t'.join(f'{k}_n\t{k}_rms8\t{k}_ratio\t{k}_worse' for k in kinds) + '\n')
        for (adj, sc), g in sorted(groups.items()):
            p = np.array(list(g['pairs'].values()))
            line = f"{adj}\t{sc}\t{g['model']}\t{len(p)}\t{p[:, 0].mean():.3f}\t{p[:, 3].mean():.3f}\t" \
                   f"{p[:, 1].mean():.3f}\t{p[:, 2].mean():.3f}"
            for k in kinds:
                q = np.array(g['pred'].get(k, []))
                line += (f"\t{len(q)}\t{q[:, 0].mean():.3f}\t{q[:, 0].sum() / q[:, 1].sum():.3f}\t"
                         f"{int((q[:, 0] >= q[:, 1]).sum())}") if len(q) else '\t0\t\t\t'
            f.write(line + '\n')


if __name__ == '__main__':
    main()
