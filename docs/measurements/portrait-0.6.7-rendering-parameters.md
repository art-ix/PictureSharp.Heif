# Apple portrait rendering parameters (0.6.7)

Observations of the private `RenderingParameters` blob in Apple's depth XMP (`AppleDepth.RenderingParameters`), moved
from the decoding guide. They describe the supplied iPhone files; this is not an Apple specification.

The inventory is [`portrait-0.6.7-rendering-parameters.tsv`](portrait-0.6.7-rendering-parameters.tsv).
The plugin still exposes the raw blob and does not assign meanings to its numeric keys.

| Observation | Evidence | Confidence |
| --- | --- | --- |
| The observed layout is little-endian, with a 16-byte `REND` header and 8-byte entries (u16 key, u16 type, 4-byte payload). | All 15 originals have version 7, size field equal to 1352 bytes, final header field 1, and 167 entries. | High for this dataset. |
| Type 1 behaves as float32 and type 2 as int32. | 149 type-1 and 18 type-2 entries per file; decoded values are finite/plausible; raw hexadecimal payloads are retained. | High for these files, not a guarantee for other versions/types. |
| All observed keys are present in every original, without duplicates. | Same set of 167 keys in all 15 files. | High. |
| Exports do not encode the edited aperture/refocus in this blob. | Every one of the 66 exports contains the same blob as its original. | High for the supplied exports. |
| The blob is not only a lens profile. | Within 1×/2×/4× there are 2/5/4 distinct blobs across 2/6/6 scenes. The front camera has one scene. | High; the cause of the changing fields is unknown. |
| Keys 400–453 are not evidence of a face-only parameter block. | They occur in every original and vary also on object-only scenes. | High for presence/variation; semantic meaning unknown. |

Across the dataset, 88 keys are constant and 79 change. Keys 400–403 and 450–453 vary within a lens; key 603 also
varies between the 4× leaf image and other 4× scenes. Other observed differences largely follow camera configuration.
Several payloads have exact-looking numerical relationships (for example 402 ≈ 48×401); this does not identify
what they control. No value has been shown to predict a rendering parameter on independent scenes, so none is
used by the renderer and no parsed public view is added. Earlier four-file hypotheses of 168 entries, no variation
within a lens, or a face-only 400–453 range are superseded by this larger inventory.


The renderer's calibration is described in [portrait-0.6.7-calibration.md](portrait-0.6.7-calibration.md).
