#!/usr/bin/env python3
"""Regenerate edits.tsv for a samples/edit/<scene> folder from export file names.

Names: IMG_xxxx_<adjustment><value>.heic, e.g. IMG_8113_ekspozycja-25.heic (Polish adjustment names, no diacritics).
Usage: scripts/make-edits-tsv.py samples/edit/street
"""
import hashlib, pathlib, re, sys

NAMES = {'blask': 'brilliance', 'cienie': 'shadows', 'cieplo': 'warmth', 'dynamika': 'vibrance',
         'ekspozycja': 'exposure', 'jasnosc': 'brightness', 'kontrast': 'contrast', 'nasycenie': 'saturation',
         'ostrosc': 'sharpness', 'punktczerni': 'black_point', 'redukcjaszumu': 'noise_reduction',
         'swiatla': 'highlights', 'szczegolowosc': 'definition', 'tilta': 'tint', 'tinta': 'tint',
         'winieta': 'vignette'}

folder = pathlib.Path(sys.argv[1])
original = next(p for p in sorted(folder.glob('IMG_*.[Hh][Ee][Ii][Cc]'))
                if re.fullmatch(r'IMG_\d+', p.stem))
digest = lambda p: hashlib.md5(p.read_bytes()).hexdigest()
orig_hash = digest(original)
rows, seen, skipped = [], {}, []
for p in sorted(folder.glob(f'{original.stem}_*.[Hh][Ee][Ii][Cc]')):
    m = re.fullmatch(rf'{original.stem}_([a-z]+)(-?\d+)?', p.stem)
    if not m or m.group(1) not in NAMES or m.group(2) is None:
        skipped.append(p.name); continue
    h = digest(p)
    if h == orig_hash or h in seen:
        skipped.append(f'{p.name} (identical to {original.name if h == orig_hash else seen[h]})'); continue
    seen[h] = p.name
    rows.append((p.name, original.name, NAMES[m.group(1)], m.group(2)))
with open(folder / 'edits.tsv', 'w') as o:
    o.write('# value = slider position shown in Photos; one adjustment per export; sharpness, noise_reduction and '
            'definition run 0..100 only\n')
    o.write('export\toriginal\tadjustment\tvalue\n')
    for r in rows: o.write('\t'.join(r) + '\n')
print(len(rows), 'rows;', 'skipped:', *skipped, sep='\n  ')
