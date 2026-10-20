#!/usr/bin/env python3
"""Assess the opt-in capture-focus experiment without publishing coordinates or private images. Standard library only."""
import argparse
import csv
import hashlib
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path

REGIONS = ("background", "subject", "edge", "hair", "focus")


def read(path):
    with path.open() as stream:
        rows = list(csv.DictReader(stream, delimiter="\t"))
    indexed = {(r["scene"], r["variant"], r["aperture"]): r for r in rows}
    if len(indexed) != len(rows):
        raise ValueError(f"Duplicate measurement pair in {path}")
    return indexed


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def number(value):
    return "N/A" if value is None else format(value, ".9g")


def psnr(mse):
    return math.inf if mse == 0 else 10 * math.log10(65025 / mse)


def difference(after, before):
    return 0.0 if after == before else after - before


def write(path, rows):
    with path.open("x", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]), delimiter="\t", lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def analyze(root, output):
    docs, artifacts = root / "docs/measurements", root / "artifacts"
    paths = [docs / "portrait-0.6.7-baseline.tsv", docs / "portrait-0.6.7-foreground-candidate.tsv",
             artifacts / "portrait-0.6.7-capture-focus-candidate.tsv"]
    frozen, accepted, candidate = [read(p) for p in paths]
    if not frozen.keys() == accepted.keys() == candidate.keys():
        raise ValueError("The three pair sets differ")
    context_path = artifacts / "portrait-0.6.7-capture-focus-experiment.json"
    context = json.loads(context_path.read_text())
    points_path = artifacts / "portrait-focus-metadata/verified-points.json"
    if sha(points_path) != context["VerifiedPointsSha256"]:
        raise ValueError("Capture points differ from the measured experiment")
    # Bind the measurement to the accepted renderer, rather than an incidental newer working tree.
    accepted_context = json.loads((docs / "portrait-0.6.7-foreground-candidate.json").read_text())
    for name, digest in context["WorkingSources"].items():
        if digest != accepted_context["working_source_sha256"].get("src/PictureSharp.Heif/" + name, digest):
            raise ValueError("The experiment used a different renderer: " + name)
    old_manifest = json.loads((artifacts / "portrait-0.6.7-current-inputs.json").read_text())
    new_manifest = json.loads((artifacts / "portrait-0.6.7-capture-focus-candidate-inputs.json").read_text())
    def keyed(manifest):
        return {(p["Scene"], p["Variant"], float(p["Aperture"])): p for p in manifest["Pairs"]}
    old_pixels, new_pixels = keyed(old_manifest), keyed(new_manifest)
    if old_pixels.keys() != new_pixels.keys():
        raise ValueError("Private native pair sets differ")
    unchanged_refocus = 0
    for key, pair in new_pixels.items():
        before = old_pixels[key]
        for field in ("OriginalHash", "ExportHash", "RoiHash", "OriginalPixelsHash", "ReferencePixelsHash",
                      "DepthHash", "DepthXmpHash", "MatteHash", "HairHash", "Tone", "Focus"):
            if pair[field] != before[field]:
                raise ValueError(f"Frozen input changed: {key}, {field}")
        if key[1].startswith("refocus"):
            if pair["RenderHash"] != before["RenderHash"]:
                raise ValueError(f"Explicit refocus changed: {key}")
            unchanged_refocus += 1
    rows, scores, failures, failures_accepted = [], [], set(), set()
    for key, row in candidate.items():
        for region in REGIONS:
            mode = "norm" if row[f"{region}_render_norm_mse"] != "N/A" else "raw"
            column = f"{region}_render_{mode}_mse"
            values = [r[key][column] for r in (frozen, accepted, candidate)]
            if "N/A" in values:
                continue
            mse = [float(v) for v in values]
            p = [psnr(v) for v in mse]
            delta, delta_accepted = difference(p[2], p[0]), difference(p[2], p[1])
            qualifying = row["edited"] == "no" or region in ("background", "focus")
            if qualifying and delta < -.5 - 1e-9:
                failures.add(key)
            if qualifying and delta_accepted < -.5 - 1e-9:
                failures_accepted.add(key)
            rows.append(dict(scene=key[0], variant=key[1], aperture=key[2], edited=row["edited"], region=region,
                             mode="normalized" if mode == "norm" else "raw", frozen_psnr=number(p[0]), accepted_psnr=number(p[1]),
                             candidate_psnr=number(p[2]), delta_frozen_db=number(delta), delta_accepted_db=number(delta_accepted),
                             gate="regression" if qualifying and delta < -.5 - 1e-9 else "pass" if qualifying else "diagnostic"))
            scores.append((key, row["edited"], region, mode, mse))
    summary = []
    for cohort in ("clean-all", "clean-default", "clean-refocus", "edited"):
        for region in REGIONS:
            chosen = [s for s in scores if s[2] == region and (s[1] == "yes" if cohort == "edited" else
                      s[1] == "no" and (cohort == "clean-all" or s[0][1].startswith("refocus") == (cohort == "clean-refocus")))]
            for mode in ("raw", "norm"):
                matching = [s for s in chosen if s[3] == mode]
                if not matching:
                    continue
                by_scene = defaultdict(list)
                for key, _, _, _, mse in matching:
                    by_scene[key[0]].append(mse)
                pooled = [statistics.mean(statistics.mean(m[i] for m in values) for values in by_scene.values()) for i in range(3)]
                p = [psnr(m) for m in pooled]
                summary.append(dict(cohort=cohort, region=region, mode="normalized" if mode == "norm" else "raw",
                                    pairs=len(matching), scenes=len(by_scene), frozen_psnr=number(p[0]), accepted_psnr=number(p[1]),
                                    candidate_psnr=number(p[2]), delta_frozen_db=number(difference(p[2], p[0])),
                                    delta_accepted_db=number(difference(p[2], p[1]))))
    output.mkdir(parents=True, exist_ok=True)
    prefix = "portrait-0.6.7-capture-focus"
    targets = [output / (prefix + suffix) for suffix in (".tsv", "-summary.tsv", ".json")]
    if any(p.exists() for p in targets):
        raise FileExistsError("Existing reports are preserved; choose another --output directory")
    write(targets[0], rows)
    write(targets[1], summary)
    report = dict(stage="0.6.7 E3: capture-focus-only experiment", decision="rejected" if failures else "passes-regression-gate",
                  policy=context["Policy"], pairs=len(candidate), default_pairs=len(candidate)-unchanged_refocus,
                  native_refocus_byte_identical=unchanged_refocus, regressed_pairs_vs_frozen=len(failures),
                  regressed_pairs_vs_accepted=len(failures_accepted),
                  regressed_regions_vs_frozen=sum(r["gate"] == "regression" for r in rows),
                  working_source_sha256=context["WorkingSources"], measurement_source_sha256=context["MeasurementSources"],
                  input_sha256={p.name: sha(p) for p in paths + [context_path]}, verified_private_points_sha256=sha(points_path),
                  analyzer_sha256=sha(Path(__file__)), report_sha256={p.name: sha(p) for p in targets[:2]})
    targets[2].write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({k: v for k, v in report.items() if not k.endswith("sha256")}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    analyze(args.root, args.output or args.root / "docs/measurements")
