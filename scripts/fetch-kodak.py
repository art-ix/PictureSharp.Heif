#!/usr/bin/env python3
"""Download the Kodak Lossless True Color Image Suite (24 PNG images, 768x512, released by Eastman Kodak for
unrestricted usage) from r0k.us into tests/assets/external/kodak for encoder measurements.

Requires the user's approval (see AGENTS.md). Files are verified against scripts/kodak.sha256; --record writes the
hashes of a fresh download instead.
"""
import hashlib
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = 'http://r0k.us/graphics/kodak/kodak/'
OUTPUT = ROOT / 'tests/assets/external/kodak'
PINS = ROOT / 'scripts/kodak.sha256'


def main():
    if '--approved' not in sys.argv:
        sys.exit('Downloading external corpora requires approval: rerun with --approved.')
    record = '--record' in sys.argv
    pins = {}
    if PINS.exists() and not record:
        pins = {name: digest for digest, name in (line.split() for line in PINS.read_text().splitlines())}
    OUTPUT.mkdir(parents=True, exist_ok=True)
    lines = []
    for index in range(1, 25):
        name = f'kodim{index:02d}.png'
        path = OUTPUT / name
        if not path.exists():
            with urllib.request.urlopen(BASE + name, timeout=120) as response:
                path.write_bytes(response.read())
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if pins and pins.get(name) != digest:
            sys.exit(f'{name}: SHA-256 {digest} does not match the pinned value.')
        lines.append(f'{digest}  {name}')
    if record:
        PINS.write_text('\n'.join(lines) + '\n')


if __name__ == '__main__':
    main()
