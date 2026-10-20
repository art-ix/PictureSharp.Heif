# E5 metrics (fixed 2026-10-19 before model changes). Clean pairs with a tone LUT, background ROI, normalized.
#  highlight PSNR: RGB MSE over background pixels whose reference luma >= the 99th percentile of reference background luma
#  tail ratio: (P99.5 - P50) of background luma, render / reference -> mean |log2| (1 = same highlight contrast)
#  grain ratio: TSV render_norm_grain / reference_grain -> mean |log2|
import sys, os, glob, csv, math
import numpy as np
from PIL import Image
from collections import defaultdict
# Usage: analyze-portrait-bokeh.py <dump with references> <name>=<render dump>,<measurement TSV> ...
leaf={'liscie_1x','liscie_2x','liscie_4x'}; edited={'wlosy','portret_4x'}
def run(folder, tsv, refdir):
    rows={ (r['scene'],r['variant'],float(r['aperture'])):r for r in csv.DictReader(open(tsv),delimiter='\t')}
    out={}
    for f in sorted(glob.glob(os.path.join(folder,'*_render.png'))):
        stem=os.path.basename(f)[:-11]
        parts=stem.rsplit('_f',1); ap=float(parts[1]); sv=parts[0]
        key=next(k for k in rows if f"{k[0]}_{k[1]}"==sv and abs(k[2]-ap)<1e-3)
        if key[0] in leaf or key[0] in edited: continue
        base=os.path.join(refdir,stem)
        if not os.path.exists(base+'_lut.bin'): continue
        lut=np.frombuffer(open(base+'_lut.bin','rb').read(),np.uint8).reshape(3,256)
        reg=np.frombuffer(open(base+'_regions.bin','rb').read(),np.uint8)
        R=np.asarray(Image.open(f).convert('RGB')); A=np.asarray(Image.open(base+'_reference.png').convert('RGB'))
        R=np.stack([lut[c][R[...,c]] for c in range(3)],-1).astype(np.float64).reshape(-1,3); A=A.astype(np.float64).reshape(-1,3)
        bg=(reg&1)!=0
        luma=lambda X: X@np.array([0.299,0.587,0.114])
        lr,la=luma(R[bg]),luma(A[bg])
        t=np.percentile(la,99); H=la>=t
        hmse=((R[bg][H]-A[bg][H])**2).mean()
        tail=lambda l: np.percentile(l,99.5)-np.percentile(l,50)
        tr=tail(lr)/max(tail(la),1e-6)
        r=rows[key]; g=float(r['render_norm_grain'])/float(r['reference_grain'])
        out[key]=(hmse,tr,g)
    return out
def pooled(out,keys=None):
    sc=defaultdict(list)
    for k,(h,t,g) in out.items():
        if keys is None or keys(k): sc[k[0]].append((h,abs(math.log2(t)),abs(math.log2(g))))
    hs=np.mean([np.mean([x[0] for x in v]) for v in sc.values()])
    return 10*math.log10(65025/hs), np.mean([np.mean([x[1] for x in v]) for v in sc.values()]), np.mean([np.mean([x[2] for x in v]) for v in sc.values()]), sum(len(v) for v in sc.values())
if __name__=='__main__':
    refdir=sys.argv[1]; res={}
    for a in sys.argv[2:]:
        n,folder,tsv=a.split('=')[0],*a.split('=')[1].split(',')
        res[n]=run(folder,tsv,refdir)
    for label,sel in [('all clean',None),('f<=2.8',lambda k:k[2]<=2.8),('f4.5',lambda k:abs(k[2]-4.5)<.01),('f>=8',lambda k:k[2]>=8)]:
        print(label)
        for n,o in res.items():
            h,t,g,c=pooled(o,sel); print(f'  {n:8} pairs {c:2}  highlight PSNR {h:7.3f}  |log2 tail| {t:.3f}  |log2 grain| {g:.3f}')
    n0=list(res)[0]
    print('per pair (first variant): highlight dB, tail ratio, grain ratio')
    for k in sorted(res[n0],key=lambda k:(k[0],k[1],k[2])):
        h,t,g=res[n0][k]; print(f'  {k[0]:11} {k[1][:12]:12} f{k[2]:<4g} {10*math.log10(65025/h):6.2f} {t:5.2f} {g:5.2f}')
