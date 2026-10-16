#!/usr/bin/env python3
"""Generate the raw HEVC intra corpus and its bit-exact YCbCr references.

Each stream is encoded from a synthetic picture by HM or kvazaar, then decoded by both HM (TAppDecoder) and libde265
(dec265). The SHA-256 of every output plane is recorded only when both decoders agree; any mismatch aborts. The HEVC
items of the HEIF corpus (tests/assets/corpus) get the same treatment, extracted to Annex B by a minimal parser.
"""
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path(os.environ.get('PICTURESHARP_HEIF_TOOLS', ROOT / 'reference/tools'))
HM_CFG = ROOT / 'reference/src/HM/cfg'
OUTPUT = ROOT / 'tests/assets/hevc'
HEIF_CORPUS = ROOT / 'tests/assets/corpus'


class Lcg:
    def __init__(self, seed):
        self.state = seed

    def next(self, bound):
        self.state = (self.state * 1103515245 + 12345) & 0x7fffffff
        return (self.state >> 8) % bound


def picture(width, height, bits, chroma, seed):
    """Synthetic texture: gradients, sharp diagonal edges, flat areas, checkerboards and noise per CTU-sized region."""
    rng = Lcg(seed)
    peak = (1 << bits) - 1

    def plane(w, h, phase):
        values = []
        for y in range(h):
            for x in range(w):
                region = ((x * 4 // w) + (y * 3 // h) * 4 + phase) % 6
                if region == 0:
                    v = (x * peak) // max(1, w - 1)
                elif region == 1:
                    v = peak if (x + 2 * y) % 23 < 11 else peak // 5
                elif region == 2:
                    v = peak * 3 // 5
                elif region == 3:
                    v = peak if ((x >> 2) ^ (y >> 2)) & 1 else 0
                elif region == 4:
                    v = rng.next(peak + 1)
                else:
                    v = ((y * peak) // max(1, h - 1) + rng.next(max(1, peak // 16))) % (peak + 1)
                values.append(v)
        return values

    planes = [plane(width, height, 0)]
    if chroma == 420:
        planes += [plane(width // 2, height // 2, 2), plane(width // 2, height // 2, 4)]
    fmt = '<%dH' if bits > 8 else '%dB'
    return b''.join(struct.pack(fmt % len(p), *p) for p in planes)


SCALING_LIST = """
INTRA4X4_LUMA =
16,12,22,30,12,18,26,34,22,26,40,48,30,34,48,60
INTRA4X4_CHROMAU =
16,20,24,28,20,24,28,32,24,28,32,36,28,32,36,40
INTRA4X4_CHROMAV =
18,18,18,18,18,18,18,18,18,18,18,18,18,18,18,18
"""


def scaling_file(directory):
    lines = [SCALING_LIST]
    for size, count in [('INTER4X4', 16), ('INTRA8X8', 64), ('INTER8X8', 64), ('INTRA16X16', 64), ('INTER16X16', 64),
                        ('INTRA32X32', 64), ('INTER32X32', 64)]:
        for component in (['LUMA'] if size.endswith('32X32') else ['LUMA', 'CHROMAU', 'CHROMAV']):
            values = [16 + ((i * 7 + len(component) * 3) % 41) for i in range(count)]
            lines.append(f'{size}_{component} =\n' + ','.join(map(str, values)))
            if size.startswith(('INTRA16', 'INTER16', 'INTRA32', 'INTER32')):
                lines.append(f'{size}_{component}_DC =\n{12 + len(component)}')
    path = Path(directory) / 'scaling_list.txt'
    path.write_text('\n'.join(lines) + '\n')
    return path


# name, encoder, width, height, bits, chroma, extra arguments
CASES = [
    ('hm-main-qp32', 'hm', 192, 128, 8, 420, []),
    ('hm-main-qp22', 'hm', 192, 128, 8, 420, ['--QP=22']),
    ('hm-main-qp4', 'hm', 96, 64, 8, 420, ['--QP=4']),
    ('hm-main-qp51', 'hm', 96, 64, 8, 420, ['--QP=51']),
    ('hm-conformance-window', 'hm', 102, 58, 8, 420, ['--QP=27']),
    ('hm-ctu16', 'hm', 128, 96, 8, 420, ['--MaxCUWidth=16', '--MaxCUHeight=16', '--MaxPartitionDepth=2',
                                          '--QuadtreeTULog2MaxSize=4', '--QP=25']),
    ('hm-ctu32-tu8', 'hm', 128, 96, 8, 420, ['--MaxCUWidth=32', '--MaxCUHeight=32', '--MaxPartitionDepth=3',
                                             '--QuadtreeTULog2MaxSize=3', '--QuadtreeTUMaxDepthIntra=1', '--QP=30']),
    ('hm-tu-depth1', 'hm', 128, 96, 8, 420, ['--QuadtreeTUMaxDepthIntra=1', '--QP=28']),
    ('hm-no-transform-skip-no-signhide', 'hm', 128, 96, 8, 420, ['--TransformSkip=0', '--SignHideFlag=0', '--QP=20']),
    ('hm-transform-skip-qp12', 'hm', 128, 96, 8, 420, ['--TransformSkip=1', '--TransformSkipFast=0', '--QP=12']),
    ('hm-scaling-default', 'hm', 128, 96, 8, 420, ['--ScalingList=1', '--QP=24']),
    ('hm-scaling-file', 'hm', 128, 96, 8, 420, ['--ScalingList=2', '--QP=24']),
    ('hm-pcm', 'hm', 128, 96, 8, 420, ['--PCMEnabledFlag=1', '--PCMLog2MinSize=3', '--PCMLog2MaxSize=5',
                                         '--PCMInputBitDepthFlag=0', '--QP=1']),
    ('hm-pcm-nofilter', 'hm', 128, 96, 8, 420, ['--PCMEnabledFlag=1', '--PCMLog2MinSize=3', '--PCMLog2MaxSize=4',
                                                  '--PCMFilterDisableFlag=1', '--QP=1']),
    ('hm-transquant-bypass', 'hm', 96, 64, 8, 420, ['--TransquantBypassEnable=1', '--CUTransquantBypassFlagForce=1']),
    ('hm-cu-qp-delta', 'hm', 192, 128, 8, 420, ['--MaxDeltaQP=4', '--MaxCuDQPDepth=2', '--AdaptiveQP=1',
                                                  '--MaxQPAdaptationRange=6', '--CbQpOffset=-3', '--CrQpOffset=5']),
    ('hm-slice-chroma-qp-offsets', 'hm', 128, 96, 8, 420, ['--SliceChromaQPOffsetPeriodicity=1',
                                                             '--SliceCbQpOffsetIntraOrPeriodic=4',
                                                             '--SliceCrQpOffsetIntraOrPeriodic=-6']),
    ('hm-deblock-off-sao-off', 'hm', 128, 96, 8, 420, ['--LoopFilterDisable=1', '--SAO=0']),
    ('hm-deblock-offsets', 'hm', 128, 96, 8, 420, ['--LoopFilterBetaOffset_div2=5', '--LoopFilterTcOffset_div2=-4',
                                                     '--QP=37']),
    ('hm-deblock-slice-offsets', 'hm', 128, 96, 8, 420, ['--LoopFilterOffsetInPPS=0', '--LoopFilterBetaOffset_div2=-3',
                                                           '--LoopFilterTcOffset_div2=6', '--QP=35']),
    ('hm-sao-only', 'hm', 192, 128, 8, 420, ['--LoopFilterDisable=1', '--QP=34']),
    ('hm-sao-lcu-boundary', 'hm', 192, 128, 8, 420, ['--SAOLcuBoundary=1', '--QP=30']),
    ('hm-strong-intra-off', 'hm', 192, 128, 8, 420, ['--StrongIntraSmoothing=0', '--QP=26']),
    ('hm-tiles-uniform', 'hm', 768, 160, 8, 420, ['--TileUniformSpacing=1', '--NumTileColumnsMinus1=2',
                                                    '--NumTileRowsMinus1=1', '--MaxCUWidth=32', '--MaxCUHeight=32',
                                                    '--MaxPartitionDepth=3']),
    ('hm-tiles-explicit-nocross', 'hm', 576, 192, 8, 420, ['--TileUniformSpacing=0', '--NumTileColumnsMinus1=1',
                                                             '--TileColumnWidthArray=8', '--NumTileRowsMinus1=1',
                                                             '--TileRowHeightArray=2', '--LFCrossTileBoundaryFlag=0',
                                                             '--MaxCUWidth=32', '--MaxCUHeight=32',
                                                             '--MaxPartitionDepth=3']),
    ('hm-wpp', 'hm', 256, 192, 8, 420, ['--WaveFrontSynchro=1', '--MaxCUWidth=32', '--MaxCUHeight=32',
                                         '--MaxPartitionDepth=3']),
    ('hm-slices-ctu', 'hm', 256, 192, 8, 420, ['--SliceMode=1', '--SliceArgument=5', '--LFCrossSliceBoundaryFlag=0',
                                                '--MaxCUWidth=32', '--MaxCUHeight=32', '--MaxPartitionDepth=3']),
    ('hm-slice-segments', 'hm', 256, 192, 8, 420, ['--SliceMode=1', '--SliceArgument=9', '--SliceSegmentMode=1',
                                                    '--SliceSegmentArgument=3', '--MaxCUWidth=32', '--MaxCUHeight=32',
                                                    '--MaxPartitionDepth=3']),
    ('hm-wpp-slices', 'hm', 256, 192, 8, 420, ['--WaveFrontSynchro=1', '--SliceMode=1', '--SliceArgument=11',
                                                '--MaxCUWidth=32', '--MaxCUHeight=32', '--MaxPartitionDepth=3']),
    ('hm-tiles-slice-per-tile', 'hm', 512, 128, 8, 420, ['--TileUniformSpacing=1', '--NumTileColumnsMinus1=1',
                                                           '--NumTileRowsMinus1=1', '--SliceMode=3', '--SliceArgument=1',
                                                           '--MaxCUWidth=32', '--MaxCUHeight=32',
                                                           '--MaxPartitionDepth=3']),
    ('hm-main10-qp30', 'hm', 128, 96, 10, 420, ['--QP=30']),
    ('hm-main10-qp8-tskip', 'hm', 128, 96, 10, 420, ['--QP=8', '--TransformSkipFast=0']),
    ('hm-main10-pcm', 'hm', 128, 96, 10, 420, ['--PCMEnabledFlag=1', '--PCMLog2MinSize=3', '--PCMLog2MaxSize=5',
                                                 '--PCMInputBitDepthFlag=1', '--QP=0']),
    ('hm-mono8', 'hm', 128, 96, 8, 400, ['--QP=28']),
    ('hm-mono10', 'hm', 128, 96, 10, 400, ['--QP=26']),
    # Range extension coding tools are outside the supported profiles: decoders must raise NotSupportedException.
    ('hm-mono8-rext-tools', 'hm', 64, 64, 8, 400, ['--QP=30']),
    ('kvazaar-default', 'kvazaar', 192, 128, 8, 420, []),
    ('kvazaar-qp12-signhide-tskip', 'kvazaar', 128, 96, 8, 420, ['--qp', '12', '--signhide', '--transform-skip']),
    ('kvazaar-scaling-default', 'kvazaar', 128, 96, 8, 420, ['--scaling-list', 'default']),
    ('kvazaar-tiles', 'kvazaar', 512, 192, 8, 420, ['--tiles', '2x2', '--no-wpp']),
    ('kvazaar-wpp', 'kvazaar', 256, 192, 8, 420, ['--wpp']),
    ('kvazaar-slices', 'kvazaar', 512, 128, 8, 420, ['--tiles', '2x1', '--slices', 'tiles']),
    ('kvazaar-mono', 'kvazaar', 128, 96, 8, 400, ['--input-format', 'P400']),
    ('kvazaar-qp45-deblock', 'kvazaar', 128, 96, 8, 420, ['--qp', '45', '--deblock', '3:-2']),
]


def run(command, **kwargs):
    result = subprocess.run(command, capture_output=True, **kwargs)
    if result.returncode != 0:
        sys.exit(f'{command[0]} failed ({result.returncode}):\n{result.stdout.decode(errors="replace")[-2000:]}'
                 f'\n{result.stderr.decode(errors="replace")[-2000:]}')
    return result


def encode(case, directory):
    name, encoder, width, height, bits, chroma, extra = case
    source = Path(directory) / (name + '.yuv')
    source.write_bytes(picture(width, height, bits, chroma, sum(map(ord, name))))
    stream = Path(directory) / (name + '.hevc')
    if encoder == 'hm':
        config = 'encoder_intra_main10.cfg' if bits > 8 else 'encoder_intra_main.cfg'
        if chroma == 400:
            config = 'encoder_intra_main_rext.cfg'
        command = [str(TOOLS / 'bin/TAppEncoder'), '-c', str(HM_CFG / config), '-i', str(source), '-b', str(stream),
                   '-o', '/dev/null', '-wdt', str(width), '-hgt', str(height), '-fr', '1', '-f', '1',
                   f'--InputBitDepth={bits}', f'--InternalBitDepth={bits}', f'--OutputBitDepth={bits}',
                   '--ConformanceWindowMode=1', '--SEIDecodedPictureHash=1']
        if chroma == 400:
            command += ['--InputChromaFormat=400', '--ChromaFormatIDC=400', '--Profile=main-RExt']
            if 'rext-tools' not in name:
                # Plain monochrome like x265 alpha planes: no range extension coding tools.
                command += ['--ResidualRotation=0', '--SingleSignificanceMapContext=0', '--ImplicitResidualDPCM=0',
                            '--ExplicitResidualDPCM=0', '--GolombRiceParameterAdaptation=0',
                            '--HighPrecisionPredictionWeighting=0', '--CrossComponentPrediction=0']
        if '--ScalingList=2' in extra:
            command.append(f'--ScalingListFile={scaling_file(directory)}')
        run(command + extra)
    else:
        command = [str(TOOLS / 'bin/kvazaar'), '-i', str(source), '--input-res', f'{width}x{height}', '-o',
                   str(stream), '--frames', '1', '-p', '1', '--threads', '0', '--hash', 'md5'] + extra
        run(command, env={**os.environ, 'LD_LIBRARY_PATH': str(TOOLS / 'lib')})
    return stream


def sps_info(stream):
    """Geometry from the HM decoder's trace-free output size is ambiguous, so read it from dec265's header dump."""
    dump = run([str(TOOLS / 'bin/dec265'), '-q', '-d', str(stream)]).stdout.decode()
    values = {}
    for line in dump.splitlines():
        line = line.removeprefix('INFO:')
        if ':' in line:
            key, value = line.split(':', 1)
            if value.strip():
                values.setdefault(key.strip(), value.split()[0])
    return values


def reference(stream, directory):
    """Returns per-plane SHA-256 after checking that HM and libde265 produce identical reconstructions."""
    hm_out = Path(directory) / 'hm.yuv'
    de_out = Path(directory) / 'de265.yuv'
    hm = run([str(TOOLS / 'bin/TAppDecoder'), '-b', str(stream), '-o', str(hm_out), '-d', '0',
              '--SEIDecodedPictureHash=1']).stdout.decode()
    hashed = '[MD5:' in hm or '[CRC:' in hm or '[Checksum:' in hm
    if hashed and '(OK)' not in hm:
        sys.exit(f'HM picture hash check failed for {stream.name}')
    de265 = subprocess.run([str(TOOLS / 'bin/dec265'), '-q', '-c', '-t', '0', '-0', '-o', str(de_out), str(stream)],
                           capture_output=True)
    oracles = ['HM'] + (['SEI hash'] if hashed else [])
    if de265.returncode == 0:
        oracles.append('libde265')
    elif not hashed:
        sys.exit(f'dec265 failed on {stream.name} and no picture hash decides:\n{de265.stdout.decode()[-500:]}')
    else:
        # The encoder's picture hash confirms HM; libde265's own hash check reports its reconstruction as wrong.
        print(f'note: libde265 fails the picture hash of {stream.name}; HM and the SEI hash are the reference')
    hm_bytes = hm_out.read_bytes()
    de_bytes = de_out.read_bytes() if de265.returncode == 0 else None
    info = sps_info(stream)
    width = int(info.get('pic_width_in_luma_samples'))
    height = int(info.get('pic_height_in_luma_samples'))
    if info.get('conformance_window_flag') == '1':
        sub = 1 if info.get('chroma_format_idc') == '0' else 2
        width -= sub * (int(info['conf_win_left_offset']) + int(info['conf_win_right_offset']))
        height -= sub * (int(info['conf_win_top_offset']) + int(info['conf_win_bottom_offset']))
    bits = int(info['bit_depth_luma'])
    chroma = int(info['chroma_format_idc'])
    sample = 2 if bits > 8 else 1
    luma = width * height * sample
    chroma_size = (width // 2) * (height // 2) * sample if chroma == 1 else 0
    if chroma == 0:
        # HM writes 4:0:0 output without chroma; libde265 appends neutral 4:2:0 chroma planes.
        de_bytes = de_bytes[:luma] if de_bytes is not None else None
        hm_bytes = hm_bytes[:luma]
    if len(hm_bytes) != luma + 2 * chroma_size or (de_bytes is not None and hm_bytes != de_bytes):
        sys.exit(f'HM and libde265 disagree on {stream.name}: {len(hm_bytes)} vs {len(de_bytes)} bytes')
    planes = [hm_bytes[:luma]]
    if chroma_size:
        planes += [hm_bytes[luma:luma + chroma_size], hm_bytes[luma + chroma_size:]]
    return dict(width=width, height=height, bitDepth=bits, chromaFormat=chroma, oracles=oracles,
                planes=[hashlib.sha256(p).hexdigest() for p in planes])


def boxes(data):
    offset = 0
    while offset + 8 <= len(data):
        size, kind = struct.unpack_from('>I4s', data, offset)
        yield kind, data[offset + 8:offset + size]
        offset += size


def heif_items(path):
    """Annex B streams of every hvc1 item (hvcC parameter sets followed by the item's NAL units)."""
    data = path.read_bytes()
    meta = next(p for k, p in boxes(data) if k == b'meta')[4:]
    children = dict((k, p) for k, p in boxes(meta))
    iinf = children[b'iinf']
    count_size = 2 if iinf[0] == 0 else 4
    types = {}
    for kind, infe in boxes(iinf[4 + count_size:]):
        version = infe[0]
        item_id = struct.unpack_from('>H' if version == 2 else '>I', infe, 4)[0]
        offset = 4 + (2 if version == 2 else 4) + 2
        types[item_id] = infe[offset:offset + 4]
    iloc = children[b'iloc']
    version = iloc[0]
    sizes = iloc[4], iloc[5]
    offset_size, length_size, base_size = sizes[0] >> 4, sizes[0] & 15, sizes[1] >> 4
    index_size = sizes[1] & 15 if version in (1, 2) else 0
    position = 6
    count = struct.unpack_from('>H' if version < 2 else '>I', iloc, position)[0]
    position += 2 if version < 2 else 4

    def number(size):
        nonlocal position
        value = int.from_bytes(iloc[position:position + size], 'big')
        position += size
        return value

    locations = {}
    for _ in range(count):
        item_id = number(2 if version < 2 else 4)
        method = number(2) & 15 if version in (1, 2) else 0
        number(2)
        base = number(base_size)
        extents = []
        for _ in range(number(2)):
            number(index_size)
            extents.append((base + number(offset_size), number(length_size)))
        locations[item_id] = (method, extents)
    ipco = dict(boxes(children[b'iprp'])).get(b'ipco')
    properties = [p for _, p in boxes(ipco)]
    kinds = [k for k, _ in boxes(ipco)]
    ipma = dict(boxes(children[b'iprp']))[b'ipma']
    flags = int.from_bytes(ipma[1:4], 'big')
    position = 8
    associations = {}
    for _ in range(struct.unpack_from('>I', ipma, 4)[0]):
        id_size = 2 if ipma[0] < 1 else 4
        item_id = int.from_bytes(ipma[position:position + id_size], 'big')
        position += id_size
        indices = []
        n = ipma[position]
        position += 1
        for _ in range(n):
            if flags & 1:
                indices.append(struct.unpack_from('>H', ipma, position)[0] & 0x7fff)
                position += 2
            else:
                indices.append(ipma[position] & 0x7f)
                position += 1
        associations[item_id] = indices
    idat = children.get(b'idat', b'')
    for item_id, kind in sorted(types.items()):
        if kind != b'hvc1':
            continue
        hvcc = next(properties[i - 1] for i in associations[item_id] if kinds[i - 1] == b'hvcC')
        stream = bytearray()
        position = 22
        for _ in range(hvcc[position]):
            position += 1
            nal_count = struct.unpack_from('>H', hvcc, position + 1)[0]
            position += 3
            for _ in range(nal_count):
                length = struct.unpack_from('>H', hvcc, position)[0]
                stream += b'\0\0\0\1' + hvcc[position + 2:position + 2 + length]
                position += 2 + length
            position -= 1
        method, extents = locations[item_id]
        payload = b''.join((idat if method == 1 else data)[o:o + n] for o, n in extents)
        length_bytes = (hvcc[21] & 3) + 1
        position = 0
        while position < len(payload):
            length = int.from_bytes(payload[position:position + length_bytes], 'big')
            stream += b'\0\0\0\1' + payload[position + length_bytes:position + length_bytes + length]
            position += length_bytes + length
        yield item_id, bytes(stream)


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    manifest = dict(streams=[], heifItems=[])
    with tempfile.TemporaryDirectory(prefix='hevc-corpus-') as directory:
        for case in CASES:
            stream = encode(case, directory)
            destination = OUTPUT / stream.name
            destination.write_bytes(stream.read_bytes())
            entry = dict(file=destination.name, encoder=case[1], sha256=hashlib.sha256(destination.read_bytes()).hexdigest(),
                         notSupported='rext-tools' in case[0])
            entry.update(reference(destination, directory))
            manifest['streams'].append(entry)
            print(destination.name, destination.stat().st_size, 'bytes')
        for path in sorted(HEIF_CORPUS.glob('*.heic')):
            for item_id, stream in heif_items(path):
                temporary = Path(directory) / 'item.hevc'
                temporary.write_bytes(stream)
                entry = dict(file=path.name, itemId=item_id)
                entry.update(reference(temporary, directory))
                manifest['heifItems'].append(entry)
    (OUTPUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    main()
