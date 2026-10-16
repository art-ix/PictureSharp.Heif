#!/usr/bin/env python3
"""Record bit-exact references for the first picture of every JCT-VC HEVC v1 conformance bitstream.

Run scripts/fetch-jctvc-conformance.py --approved first. For each stream, the NAL units up to the second picture's first
slice segment are decoded by HM and libde265 with the arbitration of generate-hevc-corpus.py (HM plus the SEI picture
hash, libde265 when its own hash check passes). The manifest (committed) stores the stream SHA-256 and per-plane hashes.
"""
import hashlib
import importlib.util
import json
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
STREAMS = ROOT / 'tests/assets/external/jctvc/streams'
OUTPUT = ROOT / 'tests/assets/jctvc-reference.json'
spec = importlib.util.spec_from_file_location('corpus', ROOT / 'tests/assets/generate-hevc-corpus.py')
corpus = importlib.util.module_from_spec(spec)
spec.loader.exec_module(corpus)


def bitstream(directory):
    candidates = [p for p in directory.rglob('*') if p.is_file() and p.suffix.lower() in ('.bit', '.bin', '.hevc', '.265')]
    if len(candidates) != 1:
        raise SystemExit(f'{directory.name}: expected one bitstream, found {[c.name for c in candidates]}')
    return candidates[0]


def nal_units(data):
    units, start, i = [], -1, 0
    while i + 2 < len(data):
        if data[i] == 0 and data[i + 1] == 0 and data[i + 2] == 1:
            if start >= 0:
                units.append(data[start:i].rstrip(b'\0'))
            i += 3
            start = i
            continue
        i += 1
    if start >= 0:
        units.append(data[start:].rstrip(b'\0'))
    return units


def first_picture(data):
    """NAL units of the stream up to (excluding) the first slice segment of the second picture in decoding order."""
    kept, pictures = [], 0
    for unit in nal_units(data):
        kind, layer = (unit[0] >> 1) & 0x3f, ((unit[0] & 1) << 5) | (unit[1] >> 3)
        if layer == 0 and (kind <= 9 or 16 <= kind <= 21) and unit[2] & 0x80:
            pictures += 1
            if pictures == 2:
                break
        kept.append(unit)
    return b''.join(b'\0\0\0\1' + unit for unit in kept)


def main():
    entries = []
    with tempfile.TemporaryDirectory(prefix='jctvc-') as directory:
        for stream_directory in sorted(STREAMS.iterdir()):
            path = bitstream(stream_directory)
            data = path.read_bytes()
            first = Path(directory) / 'first.hevc'
            first.write_bytes(first_picture(data))
            entry = dict(name=stream_directory.name, file=str(path.relative_to(STREAMS)), sha256=hashlib.sha256(data).hexdigest())
            try:
                entry.update(corpus.reference(first, directory))
            except SystemExit as failure:
                entry['referenceError'] = str(failure)[:300]
            entries.append(entry)
            print(entry['name'], entry.get('oracles', entry.get('referenceError')))
    OUTPUT.write_text(json.dumps(entries, indent=2) + '\n')


if __name__ == '__main__':
    main()
