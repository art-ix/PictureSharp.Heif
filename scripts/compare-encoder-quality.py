#!/usr/bin/env python3
"""Compare two Speed 'quality' TSV reports using cubic Bjontegaard delta rate.

Usage: compare-encoder-quality.py baseline.tsv candidate.tsv
Positive BD-rate means larger files at equal measured quality. Times exclude decoding.
"""
import csv
import math
import statistics
import sys


def read(path):
    images = {}
    with open(path, encoding="utf-8") as stream:
        for row in csv.DictReader(stream, delimiter="\t"):
            images.setdefault(row["image"], []).append(row)
    return images


def integral(points, low, high):
    # Lagrange cubic interpolation, centered on the overlap to reduce cancellation.
    center = (low + high) / 2
    points = [(x - center, math.log(rate)) for x, rate in points]
    total = 0
    for i, (x, y) in enumerate(points):
        coefficients = [y]
        for j, (other, _) in enumerate(points):
            if i == j:
                continue
            product = [0.] * (len(coefficients) + 1)
            for k, coefficient in enumerate(coefficients):
                product[k] -= coefficient * other / (x - other)
                product[k + 1] += coefficient / (x - other)
            coefficients = product
        total += sum(c * ((high - center) ** (k + 1) - (low - center) ** (k + 1)) / (k + 1)
                     for k, c in enumerate(coefficients))
    return total


def bd_rate(a, b, metric):
    points = [[(float(r[metric]), int(r["bytes"])) for r in rows] for rows in (a, b)]
    low = max(min(x for x, _ in p) for p in points)
    high = min(max(x for x, _ in p) for p in points)
    if high <= low:
        raise ValueError("Quality curves do not overlap")
    return math.expm1((integral(points[1], low, high) - integral(points[0], low, high)) / (high - low)) * 100


def main():
    baseline, candidate = map(read, sys.argv[1:])
    if baseline.keys() != candidate.keys():
        raise ValueError("Reports must contain the same images")
    print("image\tbd_rate_psnr_percent\tbd_rate_ssim_percent\tspeedup\tmin_psnr_delta_db\tmin_ssim_delta_db")
    results = []
    for name, a in baseline.items():
        b = candidate[name]
        a.sort(key=lambda r: int(r["quality"]))
        b.sort(key=lambda r: int(r["quality"]))
        if [r["quality"] for r in a] != [r["quality"] for r in b] or len(a) != 4:
            raise ValueError("Four matching quality points required per image")
        row = [bd_rate(a, b, "psnr_rgb"), bd_rate(a, b, "ssim_db"),
               sum(float(r["ms"]) for r in a) / sum(float(r["ms"]) for r in b),
               min(float(y["psnr_rgb"]) - float(x["psnr_rgb"]) for x, y in zip(a, b)),
               min(float(y["ssim_db"]) - float(x["ssim_db"]) for x, y in zip(a, b))]
        results.append(row)
        print(name + "\t" + "\t".join(f"{v:.4f}" for v in row))
    print("average\t" + "\t".join(f"{statistics.mean(col):.4f}" for col in zip(*results)))


if __name__ == "__main__":
    main()
