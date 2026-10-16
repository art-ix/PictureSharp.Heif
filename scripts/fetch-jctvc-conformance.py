#!/usr/bin/env python3
"""Download the JCT-VC HEVC version 1 conformance bitstreams (ITU-T server) into tests/assets/external/jctvc.

Requires the user's approval (see AGENTS.md). Archives are verified against scripts/jctvc-conformance-v1.sha256; with
--record the hashes of a fresh download are written there instead. Each archive is extracted into its own directory with
path checks; nothing from the archives is executed.
"""
import hashlib
import re
import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = 'https://www.itu.int/wftp3/av-arch/jctvc-site/bitstream_exchange/draft_conformance/HEVC_v1/'
OUTPUT = ROOT / 'tests/assets/external/jctvc'
PINS = ROOT / 'scripts/jctvc-conformance-v1.sha256'


def fetch(url):
    with urllib.request.urlopen(url, timeout=120) as response:
        return response.read()


def extract(archive, destination):
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as bundle:
        for member in bundle.infolist():
            target = (destination / member.filename).resolve()
            if not str(target).startswith(str(destination.resolve()) + '/') or member.is_dir():
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(bundle.read(member))


def main():
    if '--approved' not in sys.argv:
        sys.exit('Downloading external corpora requires approval: rerun with --approved.')
    record = '--record' in sys.argv
    pins = {}
    if PINS.exists() and not record:
        for line in PINS.read_text().splitlines():
            digest, name = line.split()
            pins[name] = digest
    names = sorted(set(re.findall(r'HREF="[^"]*/HEVC_v1/([^"/]+\.zip)"', fetch(BASE).decode('latin-1'), re.I)))
    archives = OUTPUT / 'archives'
    archives.mkdir(parents=True, exist_ok=True)
    lines = []
    for name in names:
        path = archives / name
        if not path.exists():
            path.write_bytes(fetch(BASE + name))
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if pins and pins.get(name) != digest:
            sys.exit(f'{name}: SHA-256 {digest} does not match the pinned value.')
        lines.append(f'{digest}  {name}')
        extract(path, OUTPUT / 'streams' / name[:-4])
        print(name)
    if record:
        PINS.write_text('\n'.join(lines) + '\n')


if __name__ == '__main__':
    main()
