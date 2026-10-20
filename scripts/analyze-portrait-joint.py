#!/usr/bin/env python3
"""Joint focus/radius screening; whole leaf series held out together. NumPy/SciPy are analysis-only dependencies."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import numpy as np
from scipy.optimize import least_squares


def read(path):
    with path.open() as stream:
        return list(csv.DictReader(stream, delimiter="\t"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, rows):
    with path.open("x", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]), delimiter="\t", lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def analyze(root, output):
    folder = root / "docs/measurements"
    inputs = [folder / ("portrait-0.6.7-" + suffix) for suffix in
              ("radius.tsv", "focus-distribution.tsv", "capture-disparity.tsv")]
    rows = [r for r in read(inputs[0]) if r["edited"] == "False" and r["status"] == "ok"
            and r["tone_status"] == "ok" and float(r["matte_mean"]) < 26]
    distributions, captures = read(inputs[1]), read(inputs[2])
    capture = {r["scene"]: float(r["capture_focus"]) for r in captures}
    quantiles = {(r["scene"], float(r["extension"])): float(r["disparity"]) for r in distributions}
    # These descriptors must come from the same original/depth as the E1 measurements.
    baseline = json.loads((root / "samples/portrait/portrait-0.6.7-baseline-inputs.json").read_text())
    source = {r["Scene"]: r for r in baseline["Pairs"]}
    for feature in distributions + captures:
        original = source[feature["scene"]]
        if feature["original_sha256"] != original["OriginalHash"] or feature["depth_sha256"] != original["DepthHash"]:
            raise ValueError("Feature inputs differ from E1: " + feature["scene"])
    scene = np.array([r["scene"] for r in rows])
    group = np.array(["liscie" if s.startswith("liscie_") else s for s in scene])
    groups = sorted(set(group))
    observed, disparity, focus, phi, aperture, low, high = (
        np.array([float(r[key]) for r in rows]) for key in
        ("radius_px", "d50", "legacy_focus", "focal_ratio", "aperture", "radius_low_px", "radius_high_px"))
    explicit = np.array([r["variant"].startswith("refocus") for r in rows])
    change = np.array([capture[r["scene"]] for r in rows]) - focus
    change[explicit] = 0

    def predict(parameters, policy):
        scale, power, q, zone = parameters[:4]
        blend = parameters[4] if len(parameters) > 4 else int(policy == "capture")
        delta = change if policy != "near-blend" else np.maximum(change, 0)
        return np.clip(768 * scale * phi ** power * ((4.5 / aperture) ** q *
                       np.abs(disparity - focus - blend * delta) - zone), 0, 19.2)

    legacy = predict([.0055, 1.5, 1, .02], "legacy")

    def fit(mask, policy):
        weights = np.zeros(len(rows))
        names = sorted(set(scene[mask]))
        for name in names:
            here = mask & (scene == name)
            weights[here] = math.sqrt(mask.sum() / (len(names) * here.sum()))
        weights /= np.sqrt(1 + high - low)
        start, lower, upper = [.0055, 1.5, 1, .02], [.0001, .1, .1, 0], [.05, 3, 1.5, .1]
        if "blend" in policy:
            start += [.25]; lower += [0]; upper += [1]
        return least_squares(lambda p: ((predict(p, policy) - observed) * weights)[mask], start,
                             bounds=(lower, upper), loss="soft_l1", f_scale=.75, max_nfev=200).x

    def score(prediction, mask):
        return float(np.mean([np.mean(np.abs(prediction[mask & (scene == name)] - observed[mask & (scene == name)]))
                              for name in sorted(set(scene[mask]))]))

    all_rows = np.ones(len(rows), dtype=bool)
    results, models = [], {}

    def record(model, held, prediction, parameters):
        for name in sorted(set(scene[group == held])):
            mask = scene == name
            old, candidate = score(legacy, mask), score(prediction, mask)
            results.append(dict(model=model, held_group=str(held), scene=str(name), tiles=int(mask.sum()),
                                old_mae=old, candidate_mae=candidate, delta_mae=candidate-old,
                                parameters=json.dumps(parameters, separators=(",", ":"))))

    for policy in ("legacy", "capture", "blend", "near-blend"):
        models[policy] = fit(all_rows, policy).tolist()
        for held in groups:
            parameters = fit(group != held, policy)
            record(policy, held, predict(parameters, policy), parameters.tolist())

    # Extend only the near side of default focus. Keep the far anchor and the accepted radius law.
    # Exclude the maximum: a single depth outlier must not define the whole protected range.
    extensions = [0, .25, .5, .75, .9, .95]
    interval_predictions = []
    for extension in extensions:
        near = np.maximum(focus, np.array([quantiles[r["scene"], extension] for r in rows]))
        near[explicit] = focus[explicit]
        distance = np.maximum(focus - disparity, 0) + np.maximum(disparity - near, 0)
        interval_predictions.append(np.clip(768 * .0055 * phi ** 1.5 * (4.5 / aperture * distance - .02), 0, 19.2))
    best = min(range(len(extensions)), key=lambda i: score(interval_predictions[i], all_rows))
    models["near-interval"] = dict(extension=extensions[best], scale=.0055, focal_power=1.5, aperture_power=1, zone=.02)
    for held in groups:
        index = min(range(len(extensions)), key=lambda i: score(interval_predictions[i], group != held))
        record("near-interval", held, interval_predictions[index], dict(extension=extensions[index]))
    summary = []
    for model in models:
        selected = [r for r in results if r["model"] == model]
        summary.append(dict(model=model, held_groups=len(groups), scenes=len(selected),
                            old_mae=float(np.mean([r["old_mae"] for r in selected])),
                            candidate_mae=float(np.mean([r["candidate_mae"] for r in selected])),
                            worst_scene_delta=max(r["delta_mae"] for r in selected),
                            parameters=json.dumps(models[model], separators=(",", ":"))))
    output.mkdir(parents=True, exist_ok=True)
    paths = [output / ("portrait-0.6.7-joint" + suffix) for suffix in ("-holdout.tsv", "-summary.tsv", ".json")]
    if any(p.exists() for p in paths):
        raise FileExistsError("Existing joint calibration reports are preserved; choose another output directory")
    write(paths[0], results)
    write(paths[1], summary)
    provenance = dict(protocol="portrait-joint-v1", tiles=len(rows), clean_scenes=len(set(scene)), held_groups=len(groups),
                      filters="E2 status ok, valid frozen tone LUT, matte<26; include zero-radius tiles",
                      split="leave one whole scene out; liscie_1x/2x/4x held out together as one related family",
                      training="soft-L1 .75; equal scenes; uncertainty weight 1/sqrt(1+high-low); no held-out sharp ROI/export used to select focus",
                      parameter_order=["scale", "focal_power", "aperture_power", "zone", "capture_blend_if_used"],
                      interval="far=legacy; near=quantile(start+.95*(1-start)), start=.5 for a valid matte else .9; explicit refocus uses one plane",
                      interval_extensions=extensions, full_fit=models,
                      limitations="Screening after downsampling, not native image quality. Model-family selection is exploratory; no untouched test set. Only one front-camera scene.",
                      source_sha256=sha(Path(__file__)), input_sha256={p.name: sha(p) for p in inputs},
                      report_sha256={p.name: sha(p) for p in paths[:2]})
    paths[2].write_text(json.dumps(provenance, indent=2) + "\n")
    for row in summary:
        print(row)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    analyze(args.root, args.output or args.root / "docs/measurements")
