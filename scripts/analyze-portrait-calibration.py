#!/usr/bin/env python3
"""Summarize local E2 disc probes. Requires NumPy/SciPy for scene-held-out model fits, not for the plugin."""
from __future__ import annotations
import argparse
import csv
import hashlib
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path
import numpy as np
from scipy.optimize import least_squares


def read(path):
    with path.open() as stream:
        return list(csv.DictReader(stream, delimiter="\t"))


def write(path, columns, rows):
    with path.open("x", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=columns, delimiter="\t", lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def quantile(values, q):
    return float(np.quantile(values, q)) if len(values) else None


def analyze(root):
    folder = root / "docs" / "measurements"
    radii = read(folder / "portrait-0.6.7-radius.tsv")
    focus = {(r["scene"], r["variant"]): r for r in read(folder / "portrait-0.6.7-focus.tsv")}
    valid = [r for r in radii if r["status"] == "ok" and r["tone_status"] == "ok" and r["edited"] == "False"]
    aperture = []
    groups = defaultdict(list)
    for row in valid:
        groups[row["scene"], row["variant"], row["tile_id"]].append(row)
    laws = defaultdict(list)
    for (scene, variant, tile), values in groups.items():
        values.sort(key=lambda r: float(r["aperture"]))
        if len(values) < 3:
            continue
        monotone = all(float(b["radius_px"]) <= float(a["radius_px"]) + .25 for a, b in zip(values, values[1:]))
        large = [r for r in values if float(r["radius_px"]) >= 1]
        exponent = None
        if len(large) >= 3:
            x = np.log([float(r["aperture"]) for r in large])
            y = np.log([float(r["radius_px"]) for r in large])
            if np.var(x) > 0:
                exponent = float(-np.cov(x, y, ddof=0)[0, 1] / np.var(x))
        laws[scene].append((monotone, exponent))
    for scene, values in sorted(laws.items()):
        exponents = [v for _, v in values if v is not None]
        aperture.append(dict(scene=scene, tile_series=len(values), monotone_series=sum(m for m, _ in values),
                             exponent_count=len(exponents), exponent_q10=quantile(exponents, .1),
                             exponent_median=quantile(exponents, .5), exponent_q90=quantile(exponents, .9)))
    write(folder / "portrait-0.6.7-aperture.tsv", list(aperture[0]), aperture)

    # Use the original's depth under E1's fixed sharp patch, not a focus fitted to held-out blur measurements.
    # Background training avoids treating semantic person protection as a physical radius law.
    background = [r for r in valid if float(r["matte_mean"]) < 26 and float(r["radius_px"]) >= .75]
    scene_names = sorted({r["scene"] for r in background})
    scene = np.array([r["scene"] for r in background])
    observed = np.array([float(r["radius_px"]) for r in background])
    n = np.array([float(r["aperture"]) for r in background])
    phi = np.array([float(r["focal_ratio"]) for r in background])
    delta = np.array([abs(float(r["d50"]) - float(focus[r["scene"], r["variant"]]["sharp_d50"])) for r in background])
    width = np.array([float(r["radius_high_px"]) - float(r["radius_low_px"]) for r in background])

    def prediction(parameters, fixed):
        if fixed:
            scale, zone = parameters
            p, q = 1.5, 1.0
        else:
            scale, p, q, zone = parameters
        return np.clip(768 * scale * phi ** p * (np.power(4.5 / n, q) * delta - zone), 0, 19.2)

    def fit(indices, fixed):
        weights = np.zeros(len(background))
        for name in scene_names:
            here = indices & (scene == name)
            count = np.sum(here)
            if count:
                weights[here] = math.sqrt(np.sum(indices) / (len(set(scene[indices])) * count))
        weights /= np.sqrt(1 + width)
        start = [.0055, .02] if fixed else [.0055, 1.5, 1., .02]
        bounds = ([.0001, 0], [.1, .1]) if fixed else ([.0001, .1, .1, 0], [.1, 3., 1.5, .1])
        fitted = least_squares(lambda parameters: ((prediction(parameters, fixed) - observed) * weights)[indices],
                               start, bounds=bounds, loss="soft_l1", f_scale=.75, max_nfev=300)
        return fitted.x

    all_indices = np.ones(len(background), dtype=bool)
    candidate_models = []
    model_parameters = {}
    old = np.clip(768 * .0055 * phi ** 1.5 * (4.5 / n * np.array([
        abs(float(r["d50"]) - float(r["legacy_focus"])) for r in background]) - .02), 0, 19.2)
    sharp = prediction([.0055, .02], True)
    for name in scene_names:
        here = scene == name
        candidate_models.append(dict(held_out_scene=name, model="legacy-focus", tiles=int(np.sum(here)),
                                     radius_mae=float(np.mean(np.abs(old[here]-observed[here]))), parameters="fixed-0.6.6"))
        candidate_models.append(dict(held_out_scene=name, model="sharp-roi-focus", tiles=int(np.sum(here)),
                                     radius_mae=float(np.mean(np.abs(sharp[here]-observed[here]))), parameters="fixed-0.6.6;E1-sharp-ROI"))
    for fixed in [True, False]:
        model_name = "fit-scale-zone" if fixed else "fit-scale-focal-aperture-zone"
        model_parameters[model_name] = [float(v) for v in fit(all_indices, fixed)]
        for name in scene_names:
            train = scene != name
            parameters = fit(train, fixed)
            error = np.abs(prediction(parameters, fixed) - observed)[~train]
            candidate_models.append(dict(held_out_scene=name, model=model_name, tiles=int(np.sum(~train)),
                                         radius_mae=float(np.mean(error)), parameters=json.dumps(parameters.tolist(), separators=(",", ":"))))
    write(folder / "portrait-0.6.7-model-holdout.tsv", list(candidate_models[0]), candidate_models)

    # Record person-in-front separately: a constant disc is an effective proxy for the composited matte result.
    foreground = defaultdict(list)
    for row in valid:
        df = float(row["legacy_focus"])
        d = float(row["d50"])
        if d <= df + .03 or float(row["matte_mean"]) <= 230:
            continue
        expected = max(0., .0055 * 768 * float(row["focal_ratio"]) ** 1.5 * (4.5 / float(row["aperture"]) * (d - df) - .02))
        if expected < 1.25:
            continue
        foreground[row["scene"], row["variant"], row["aperture"]].append((float(row["radius_px"]), expected))
    rows = []
    for (name, variant, aperture), values in sorted(foreground.items()):
        ratios = [r/e for r, e in values]
        rows.append(dict(scene=name, variant=variant, aperture=aperture, tiles=len(values),
                         observed_radius_median=statistics.median(r for r, e in values),
                         legacy_geometric_radius_median=statistics.median(e for r, e in values),
                         ratio_q10=quantile(ratios, .1), ratio_median=quantile(ratios, .5), ratio_q90=quantile(ratios, .9)))
    if rows:
        write(folder / "portrait-0.6.7-foreground.tsv", list(rows[0]), rows)
    summaries = {}
    for model in sorted({r["model"] for r in candidate_models}):
        summaries[model] = statistics.mean(r["radius_mae"] for r in candidate_models if r["model"] == model)
    provenance = dict(protocol="portrait-e2-disc-v1", long_side=768, tile_size=32, stride=4,
                      radius_grid=[0,.75,1,1.25,1.5,2,2.5,3,4,5,6,8,10,12,16,20,24],
                      disc="weight=clamp(radius-distance+.5,0,1); linear sRGB; float64 prefix sums; linear16 encode LUT",
                      tone="reuse E1 LUT after disc blur; raw if E1 rejects normalization",
                      filters="std>=4; disparity q90-q10<=max(.01,.06*range); not radius-limit; uncertainty<=4 grid steps",
                      focus="depth median under the fixed E1 sharp ROI; no held-out blur fitted as focus",
                      fit="background matte<26 and nonzero blur; robust soft-L1; equal scenes; radii uncertainty weighted",
                      warning="equivalent-disc screening fit after downsampling; masked areas are composite proxies; not a native render quality gate",
                      input_hashes={name: hashlib.sha256((folder/name).read_bytes()).hexdigest()
                                    for name in ["portrait-0.6.7-radius.tsv","portrait-0.6.7-focus.tsv","portrait-0.6.7-baseline.json"]},
                      valid_clean_tiles=len(valid), background_model_tiles=len(background),
                      fitted_parameters=model_parameters, scene_balanced_held_out_radius_mae=summaries)
    (folder / "portrait-0.6.7-radius-analysis.json").write_text(json.dumps(provenance, indent=2) + "\n")
    print(json.dumps(provenance, indent=2))

def supplement(root):
    p=root / "docs" / "measurements"
    with (p/'portrait-0.6.7-radius.tsv').open() as f:r=list(csv.DictReader(f,delimiter='\t'))
    with (p/'portrait-0.6.7-focus.tsv').open() as f:fm={(x['scene'],x['variant']):x for x in csv.DictReader(f,delimiter='\t')}
    def write(name,rows):
     with (p/name).open('x',newline='') as f:
      w=csv.DictWriter(f,fieldnames=list(rows[0]),delimiter='\t',lineterminator='\n');w.writeheader();w.writerows(rows)
    g=defaultdict(list)
    for t in r:
     if t['status']=='ok' and t['tone_status']=='ok':g[(t['scene'],t['variant'])].append(t)
    rows=[]
    for key,a in sorted(g.items()):
     n=min(float(t['aperture']) for t in a);a=[t for t in a if float(t['aperture'])==n]
     m=min(float(t['radius_px']) for t in a);low=[float(t['d50']) for t in a if float(t['radius_px'])<=m+.25]
     sharp=float(fm[key]['sharp_d50']);span=float(fm[key]['disparity_max'])-float(fm[key]['disparity_min'])
     lo,mid,hi=np.quantile(low,[.1,.5,.9]);
     rows.append(dict(scene=key[0],variant=key[1],aperture=n,min_radius=m,plateau_tiles=len(low),focus_d10=lo,focus_d50=mid,focus_d90=hi,legacy_focus=fm[key]['legacy_focus'],sharp_d50=sharp,confidence='broad-plateau' if hi-lo>max(.02,.1*span) else 'localized-plateau'))
    write('portrait-0.6.7-focus-minimum.tsv',rows)
    fg=[]
    for t in r:
     if t['edited']!='False' or t['status']!='ok' or t['tone_status']!='ok' or not t['variant'].startswith('refocus') or float(t['matte_mean'])<=230:continue
     delta=float(t['d50'])-float(t['legacy_focus'])
     if delta<=.03:continue
     e=min(19.2,max(0,.0055*768*float(t['focal_ratio'])**1.5*((4.5/float(t['aperture']))*delta-.02)))
     if e>8*768/4032:fg.append((t['scene'],e,float(t['radius_px'])))
    rows=[]
    for scene in sorted({t[0] for t in fg}):
     train=[t for t in fg if t[0]!=scene];test=[t for t in fg if t[0]==scene]
     choices=np.linspace(.2,1,161)
     def score(data,c):
      return statistics.mean(statistics.mean(abs(t[2]-c*t[1]) for t in data if t[0]==s) for s in {t[0] for t in data})
     c=min(choices,key=lambda v:score(train,v))
     rows.append(dict(held_out_scene=scene,tiles=len(test),train_scale=c,legacy_radius_mae=score(test,1),fixed_half_radius_mae=score(test,.5),trained_radius_mae=score(test,c)))
    write('portrait-0.6.7-foreground-holdout.tsv',rows)



if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--supplement-only", action="store_true", help="Only focus-plateau and foreground held-out tables")
    args = parser.parse_args()
    if not args.supplement_only:
        analyze(args.root)
    supplement(args.root)
