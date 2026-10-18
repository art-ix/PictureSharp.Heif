# PictureSharp.Heif: plan rozwoju

## Stan i cel

**Bieżąca wersja: brak wydania; 0.1.0–0.5.0 zamknięte lokalnie (PictureSharp 0.7.1 z NuGet): `Identify`, `Decode` (YCbCr bit-exact z HM, RGB = heif-dec), `SaveAsHeic` (BD-rate PSNR −4,6%/SSIM +0,9% względem x265 medium na Kodaku po poprawach enkodera), gain mapy Apple/ISO → `ToHdr` i `ReadMetadata` bez dekodu, 2026-10-17. 0.5.0: SIMD i równoległość, bitowo identyczne na x64 i ARM64.** PictureSharp.Heif to wtyczka formatu dla
PictureSharp: moduł `IImageFormatModule` (rejestracja przez `ImageConfigurationBuilder.AddFormat`), który odczytuje i
zapisuje HEIC (HEVC w kontenerze HEIF) bez kodu natywnego. Zdjęcia HEIC zawierają tylko obrazy intra, więc port
obejmuje HEVC intra, bez kompensacji ruchu.

Decyzje użytkownika (2026-10-16): własny port dekodera i enkodera HEVC intra w C# (nie wrapper libheif); osobne
repozytorium i paczka z własnym SemVer od 0.1.0; zależność wyłącznie od paczki NuGet `PictureSharp`. Zmiany potrzebne
w samej bibliotece planuje rdzeń (`../picturesharp/plan.md`, sekcje 0.7.0 „SPI wtyczek” i 0.8.0).

Reguły pracy: [AGENTS.md](AGENTS.md). Lokalna historia pracy: `progres.md` (bez commitowania).

## Wersje

| Wersja | Zakres | Minimalny PictureSharp | Status |
| --- | --- | --- | --- |
| 0.1.0 | Szkielet repo, CI, kontener HEIF (ISOBMFF), detekcja, `Identify`, limity, fuzzing parsera | 0.6.9 | Zamknięta lokalnie (DoD spełnione; CI na GitHub odłożone decyzją użytkownika) |
| 0.2.0 | Dekoder HEVC intra (skalar), siatka, kolor, alfa, `irot`/`imir`/`clap`, ICC/EXIF/XMP | 0.6.9 | Zamknięta lokalnie (DoD spełnione, w tym strumienie konformacyjne JCT-VC) |
| 0.3.0 | Enkoder HEVC intra Main 8-bit 4:2:0, alfa, siatka, metadane | 0.6.9 | Zamknięta lokalnie (DoD spełnione) |
| 0.4.0 | Gain map Apple/ISO → `ToHdr`, `ReadMetadata` bez dekodu | 0.7.1 | Zamknięta lokalnie (DoD spełnione, 2026-10-17) |
| 0.5.0 | SIMD i równoległość, pomiary względem libheif, ARM64 | 0.7.1 | Zamknięta 2026-10-17 (x64 i ARM64), pierwsze publiczne wydanie (GitHub Release, NuGet przez Trusted Publishing) |
| 0.5.1 | Skompresowane XMP (`content_encoding` w `infe`) przez `System.IO.Compression` | 0.7.1 | Planowana (decyzja użytkownika 2026-10-17) |
| 0.6.0 | Opcje dekodera: `HeifDecoderOptions.ItemId` przez `DecoderOptions.FormatOptions` | 0.7.6 | Wydana 2026-10-18 (GitHub Release v0.6.0, NuGet) |
| dalej | Zapis gain map w HEIC, ewentualnie szybka miniatura `thmb` | 0.8.0 | Do decyzji |

Numery określają kolejność, nie terminy. Każda wersja wymaga własnego DoD (niżej) oraz wspólnej bramki jakości.
Publikacja każdej wersji wyłącznie na decyzję użytkownika.

## DoD wersji

- **0.1.0:** projekt `src/PictureSharp.Heif` i testy TUnit na paczce PictureSharp 0.6.9, test architektury (tylko
  `PictureSharp` + BCL, tylko publiczne API), CI. Parser boksów z limitami i bez rekurencji ponad ustaloną głębokość;
  `IsSupported` dla marek `heic`/`heix`/`mif1`; `Identify` (wymiary po transformacjach, alfa, głębia, liczba
  elementów w `ImageInfo.ContainerItemCount`); `Decode` → `NotSupportedException` do czasu 0.2.0. Fuzzing parsera (`Category=Slow`), korpus
  syntetyczny z `heif-enc`, skrypt `scripts/build-heif-reference.sh`.
- **0.2.0:** YCbCr bit-exact z HM i libde265; RGB zgodne z `heif-dec`; siatki, alfa, transformacje, 10-bit, monochrom,
  nclx/ICC, EXIF/XMP; uszkodzone i obcięte strumienie bez niekontrolowanych wyjątków; `TargetSize`,
  `ApplyExifOrientation` i `ColorConversion` przez rdzeń bez podwójnego obrotu.
- **0.3.0:** pliki odczytywane przez libheif i HM, nasz dekoder identyczny z HM na naszych plikach, kryterium
  jakości/rozmiaru ustalone przed implementacją, wynik niezależny od liczby wątków, `SaveAsHeic`/`ToHeic`, opcje.
- **0.4.0:** `ToHdr` dla HEIC z mapą Apple i ISO w budżetach referencji rdzenia; `Image.ReadMetadata` bez dekodu HEVC.
- **0.5.0:** SIMD x64/ARM64 bitowo identyczne ze skalarem, równoległe kafle, pomiary 12/48 MP względem libheif.
- **0.5.1:** element `mime` XMP z `content_encoding` = `deflate` rozpakowany przez `ZLibStream` (z zapasowym
  `DeflateStream`, gdy brak nagłówka zlib) i dostępny w `Metadata.Xmp` przy `Decode` i `ReadMetadata`; rozmiar
  po rozpakowaniu liczony przez `ReserveMetadata` i ograniczony (bomba dekompresji → `ImageFormatException`, bez
  alokacji ponad limit). Nieznane kodowanie lub uszkodzony strumień XMP nie blokuje obrazu: blok jest pomijany
  (dziś cały plik kończy się `NotSupportedException`). Zapis bez zmian (XMP nieskompresowane). Testy: pliki z
  `heif-enc` z kompresją metadanych (jeśli dostępna) i syntetyczne `infe`; zawartość = XMP źródłowe; fuzzing
  skompresowanego bloku; snapshot API bez zmian.

- **0.6.0:** `HeifDecoderOptions : FormatDecoderOptions` z `ItemId` (`uint?`, null = `pitm`, 0 →
  `ArgumentOutOfRangeException`); wybrany element obrazu (top-level, miniatura, kafel, obraz pomocniczy) dekodowany
  jak element główny: transformacje, kolor, alfa `auxl`, Exif/XMP `cdsc`, gain mapa — w `Identify`, `Decode` i
  `ReadMetadata`. Brak elementu lub element Exif/XMP → `ImageFormatException`; `ContainerItemCount` bez zmian.
  Minimalny PictureSharp 0.7.6. Testy: `Identify` syntetyczny (alfa, miniatura, Exif, brak ID), dekod kafla
  `grid-2x1` = lewe 64 kolumny siatki bit w bit; snapshot API; README („Choosing an image item”) i `docs/decoding.md`.

## Stan implementacji 0.1.0 (2026-10-16)

- Gotowe: szkielet/paczka, detekcja `ftyp` (także rozmiary 64-bit), ograniczony parser boksów, `Identify` dla `hvc1`/`grid`/`iden`,
  transformacje w kolejności `ipma`, alfa, głębia, limity wejścia/metadanych/pikseli, cykle i głębokość referencji.
- Gotowe: testy architektury i migawka publicznego API; 49/49 testów Release lokalnie, w tym 12 000 mutacji, Oracle i Local.
  Korpus syntetyczny: 7 plików z SHA-256 (x265/kvazaar, 8/10-bit, alfa, kadrowanie, siatka, obrót/odbicie).
  `heif-info` potwierdza wymiary/głębię/alfę korpusu, a `heif-dec` odczytuje wszystkie jego pliki.
- Gotowe: zatwierdzone pobranie i budowa przypiętych narzędzi (HM 16.20, kvazaar 2.3.1, x265 3.4,
  libde265 1.0.15, libheif 1.20.2), skrypty regeneracji korpusu, dokumentacja i workflow CI. Workflow wymaga jeszcze
  wykonania na GitHub; lokalny build ma 0 ostrzeżeń/błędów, `dotnet pack` poprawne nupkg/snupkg.
- Local: wszystkie 3 prywatne zdjęcia z `samples/` zidentyfikowane zgodnie z libheif 1.20.2. Pliki pozostają ignorowane
  (również rozszerzenia wielkimi literami); nie są częścią korpusu ani paczki.
- Gotowe: zależność od paczki PictureSharp 0.6.9 z nuget.org (`PictureSharpVersion`, nupkg zależy od 0.6.9);
  `Identify` ustawia `ImageInfo.ContainerItemCount` na liczbę wpisów `iinf` (kafle, alfa, miniatury i elementy
  Exif/XMP; `FrameCount` pozostaje 1). Manifest korpusu zawiera `itemCount` liczony niezależnie przez generator
  i zgodny z `heif-info`; testy syntetyczne (1/2/3 elementy), korpus i Local. Publiczne API wtyczki bez zmian.
- Repozytorium prywatne `art-ix/PictureSharp.Heif` (2026-10-16), GitHub Actions wyłączone na życzenie użytkownika ("na razie bez pipeline'ów"); workflow `.github/workflows/ci.yml` pozostaje w repo do późniejszego włączenia.
- Ograniczenia identyfikacji: jawne niezerowe długości extentów, całkowitopikselowy `clap`; funkcje nieobsługiwane
  zgłaszają `NotSupportedException`. `Identify` nie potwierdza poprawności HEVC ani możliwości dekodu profilu.
  `Decode` weryfikuje kontener i nadal zgłasza `NotSupportedException` — piksele są zakresem 0.2.0.

## Stan implementacji 0.2.0 (2026-10-16)

- Gotowe: referencje bit-exact — `tests/assets/generate-hevc-corpus.py` (43 strumienie HM/kvazaar pokrywające narzędzia
  kodowania + 10 elementów korpusu HEIF), SHA-256 płaszczyzn YCbCr z HM; libde265 musi się zgadzać, chyba że jego własny
  hash SEI zawodzi (2 strumienie z `slice_loop_filter_across_slices_enabled_flag=0`). Narzędzia: dec265 i HM `TAppEncoder`
  dodane do `scripts/build-heif-reference.sh`.
- Gotowe: skalarny dekoder HEVC intra w `src/PictureSharp.Heif/Hevc/` (port z HM 16.20, BSD-3, nota w THIRD-PARTY-NOTICES):
  NAL/RBSP, SPS (z VUI/HRD/RPS do pominięcia), PPS, nagłówek slice'a, CABAC, SAO, drzewo kodowania, PCM, transquant bypass,
  cu_qp_delta, listy skalowania, transform skip, DST/DCT 4–32, predykcja intra (35 trybów, wygładzanie, strong smoothing),
  deblocking, SAO, kafle, WPP, slice'y i segmenty zależne, Main/Main10/Main Still Picture i 4:0:0 bez narzędzi RExt.
  Slice'y P/B, 4:2:2/4:4:4, głębie > 10 bitów i narzędzia RExt/SCC → `NotSupportedException`.
- Wyniki: 43/43 strumienie i 10/10 elementów HEIF bitowo zgodne z HM; Local: 423 elementy HEVC (kafle, miniatury, obrazy
  pomocnicze) z 5 prywatnych zdjęć zgodne z HM i libde265; fuzzing 6000 mutacji bez niekontrolowanych wyjątków.
- Testy YCbCr linkują źródła `Hevc/` do projektu testów (bez `InternalsVisibleTo`); warstwa HEVC nie zależy od kontenera.
- Gotowe: `Decode` — siatki składane w YCbCr, `iden`, `clap`/`irot`/`imir` w kolejności `ipma` (irot przeciwnie do ruchu
  wskazówek; imir tryb 0 góra–dół, 1 lewo–prawo, jak libheif), alfa z `auxC` (+`prem` → alfa prosta jak `ToStraight` rdzenia),
  ICC/EXIF/XMP (EXIF orientation → TopLeft), nclx/VUI → `Chromaticities`/`Gamma` bez ICC, PQ/HLG z `ToSrgb` →
  `NotSupportedException`. Konwersja kolorów ustalona empirycznie względem heif-dec 1.20.2 (bez czytania kodu libheif):
  8-bit pełny zakres stałoprzecinkowo `round(k·256)`, pozostałe `float32` (limited: 1,1689/1,1429), chroma NN, a dla
  `clap` z nieparzystym początkiem dwuliniowa (9,3,3,1) na całym obrazie; 10→8 bit `round(v·255/1023)`. Opis: docs/decoding.md.
- Wyniki: 22 pliki korpusu HEIC (BT.601/709/2020, full/limited, 8/10 bit, VUI/brak colr, monochrom, alfa prosta/premult/10-bit,
  siatki, transformacje, clap parzysty/nieparzysty, ICC/EXIF/XMP) — RGBA identyczne z heif-dec; Local: 5 zdjęć iPhone
  (12/24 MP) identyczne z heif-dec, skalarnie ~1 s/12 MP; 148/148 testów, Oracle 1/1.
- Gotowe (zgoda użytkownika 2026-10-16): 147 strumieni konformacyjnych JCT-VC HEVC v1 (ITU-T, 129 MB archiwów, SHA-256
  przypięte w `scripts/jctvc-conformance-v1.sha256`, pliki w ignorowanym `tests/assets/external/jctvc`). Pierwszy obraz
  każdego strumienia: referencje HM dla 145 (RPS_C i VPSSPSPPS_A bez rozstrzygnięcia przez libde265/hash) w
  `tests/assets/jctvc-reference.json`; wszystkie 145 bit-exact (`Category=Conformance`).

## Stan implementacji 0.4.0 (2026-10-17)

- Zależność od PictureSharp 0.7.1 z nuget.org; testy zapisu przez `EncoderOptions.Configuration` (bez podmiany
  `ImageConfiguration.Default`, poza jednym testem domyślnej konfiguracji).
- Renderowanie wydzielone z dekodera: `HeifImageNode` (opis elementu: NAL, siatka, `iden`, transformacje, nclx; wersja
  odłączona kopiuje bajty) i `HeifRenderer` bez kontekstu dekodu; wyniki dekodu bez zmian.
- Gain mapy (`HeifGainMap`, `HeifGainMapImage`): ISO 21496-1 z elementu `tmap` (`dimg` = [primary, mapa], bajt wersji 0,
  ICC alternatywne z `colr`), Apple z obrazu pomocniczego `urn:com:apple:photo:2020:aux:hdrgainmap` (MakerNote z EXIF
  bazy, XMP mapy). ISO wygrywa, błędne ISO nie spada do Apple; błędy przez `DeclareGainMap`. Mapa renderowana z własnymi
  transformacjami, dekodowana leniwie z kopii danych; kolor bazy zachowany przy `IgnoreMetadata`. `Identify` z
  `LoadGainMap` zgłasza `ImageInfo.Hdr`.
- `HeifFormat.MetadataCodec` (`HeifMetadataCodec`): `ReadMetadata` bez dekodu HEVC, te same bloki co `Load`
  (wspólny `HeifMetadataReader`, kolor z nclx/VUI przez odczyt SPS); `EditMetadata` → `NotSupportedException`.
- Korpus: 6 plików z gain mapą (Apple z MakerNote, ISO szara i RGB, oba opisy z `irot`, wersja 1, obcięte metadane) z
  mapą referencyjną z heif-dec (`*.map.png`) i metadanymi w manifeście. `ToHdr` bitowo równe wtyczce referencyjnej
  (baza z dekodu, mapa z heif-dec, metadane generatora). Local: 5 zdjęć iPhone (3 ISO, 2 Apple) bitowo równe referencji
  z mapą `heif-dec --with-aux`. Fuzzing obejmuje `Identify`/`ReadMetadata`/`ToHdr` z `LoadGainMap`.
- Wyniki: 383 testy (382 zaliczone, 1 pominięty: przegląd AQ wymagający `PICTURESHARP_HEIF_SWEEP=1`), Oracle 4/4,
  build 0 ostrzeżeń, `dotnet pack` z zależnością PictureSharp 0.7.1.
- Luka SPI zamknięta w rdzeniu 0.7.1: wtyczka przekazuje wszystkie wykryte modele przez
  `AttachGainMap(metadata, map, detected)` i `DeclareGainMap`, więc HEIC z iOS 18 zgłasza `Iso21496, Apple` jak JPEG z
  tego zdjęcia. Manifest korpusu ma pole `detected`; test Local sprawdza zbiór modeli zdjęć iPhone.

## Stan implementacji 0.5.0 (2026-10-17, zamknięta lokalnie)

- Równoległość: kafle siatki dekodowane równolegle (pierwszy kafel ustala format, reszta wkleja się w rozłączne
  obszary; przy błędzie `Break` i wyjątek pierwszego wadliwego kafla w kolejności, jak sekwencyjnie), konwersja kolorów,
  obrót i odbicie pasami wierszy (`HeifParallel.ForRows`); `DecoderOptions.MaxDegreeOfParallelism` przekazywany do
  renderera i dekodu gain mapy.
- SIMD (`Vector128`, `Vector256` od 8x8, w plikach `*.Simd.cs`): odwrotna DCT i transformata w przód jako iloczyny
  macierzy (te same sumy całkowite co motylki HM, z pominięciem zerowych wierszy/kolumn), dekwantyzacja, rekonstrukcja
  próbek (`HevcSamples`), różnica i błąd kwadratowy w enkoderze, SATD 4x4, YCbCr→RGB 8-bit full range.
- Bez SIMD: zerowanie tylko zapisanych współczynników, bezpośrednie wyszukanie ostatniej pozycji skanu, RDOQ bez
  czyszczenia tablic (zerowanie pozycji przy odwiedzeniu), obrót czytający wiersze źródła.
- Weryfikacja x64: dekod korpusu i 5 zdjęć (z `ToHdr`) oraz 11 kodowań bajtowo identyczne z 0.4.0, także z
  `DOTNET_EnableHWIntrinsic=0`; pełne testy 389/390 (1 pominięty sweep AQ) na ścieżkach SIMD i skalarnych;
  `HevcSimdTests` (5 porównań wektor/skalar) i 2 testy niezależności dekodu od liczby wątków (także uszkodzone kafle).
- Pomiary x64 (`docs/measurements/speed-x64.tsv`): dekod 12 MP 646 ms / 109 ms (1 / 16 wątków; libheif 613 / 237),
  24 MP 1195 / 193 (981 / 394), 48 MP siatka 1009 / 165 (973 / 416); zapis 12 MP 31 s / 4,0 s (było 36,6 / 4,7;
  heif-enc x265 2,0 s, plik +8%), 48 MP 13,7 s (heif-enc 5,3 s, +6%).
- ARM64 (Orange Pi 5, RK3588, za zgodą użytkownika; kopia `~/heif-0.5.0` ze zdjęciami): build 0 ostrzeżeń, testy
  379/390 na NEON i z `DOTNET_EnableHWIntrinsic=0` (11 pominiętych: wyrocznie bez narzędzi na płytce i sweep AQ);
  77 skrótów dekodu i kodowania identycznych z x64 w obu trybach. `Vector256` nie jest tam przyspieszany, więc działają
  ścieżki 128-bitowe.
- Pomiary ARM64 (`docs/measurements/speed-arm64.tsv`, libheif 1.17.6 z pakietów Ubuntu): dekod 12 MP 1593 / 347 ms
  (1 / 8 wątków; libheif 2222 / 820, skalarnie 1710 / 407), 24 MP 2733 / 651 (4471 / 2701), 48 MP siatka 2618 / 718
  (4335 / 2392); zapis 12 MP 62,6 s / 14,0 s, 48 MP 41,3 s, bajty jak na x64. Brak heif-enc z x265 na płytce.
- DoD spełnione: SIMD = skalar bitowo na x64 i ARM64, równoległe kafle, pomiary 12/48 MP względem libheif.
- Dalsze kandydaty (poza DoD): parsowanie CABAC reszt (~40% dekodu), RDOQ i liczenie bitów (~40% zapisu), WPP w obrębie
  jednego obrazu, scalenie obrotu z konwersją kolorów.

## Porządki modelu kontenera (2026-10-17, niewydane)

Refaktoryzacja bez zmiany wyników (dekod i bajty enkodera identyczne z commitem 1bfaef8 na całym korpusie, w 8
orientacjach, z siatką i bez); warstwa `Hevc/` (porty) bez zmian.
- Język domeny: `HeifFourCc` nazywa kody pudełek, elementów, właściwości, referencji i brandów zamiast liczb `0x…`.
- Wartości: transformacje `HeifRotation`/`HeifMirror`/`HeifCleanAperture` (odczyt raz w kontenerze, te same typy w
  dekoderze i enkoderze, `Payload()` przy zapisie), `HeifNclx`, `HeifGridLayout`.
- Węzły renderowania jako typy (`HeifCodedNode`, `HeifGridNode`, `HeifIdentityNode`) zamiast enuma z opcjonalnymi
  polami; renderer i `HeifCanvas.Apply` dopasowują wzorce.
- `HeifItem` bez stanu walidacji (stan DFS i pamięć opisów w kontenerze), `Targets`/`RefersTo` zamiast powtarzanych
  `TryGetValue`; `HeifMetadataReader.ApplyColour`/`ApplyDescriptive` zamiast flagi; alfa w `HeifCanvas.SetAlpha`.

## Poprawy enkodera po 0.3.0 (2026-10-16, niewydane)

- AQ wariancyjne (kvazaar VAQ, `cu_qp_delta`, `HeifEncoderOptions.AdaptiveQuantizationStrength` = 1), RDOQ i sign data
  hiding (HM), SAO z decyzją na obrazie po deblockingu (dwa przebiegi zapisu wycinka), podział TU (RQT, głębokość 2), przyspieszenie (transformaty motylkowe, wspólne próbki
  referencyjne, zgrubny SATD, 4/2 kandydatów RD, wczesne zakończenie podziału CU).
- BD-rate względem x265 medium (PSNR RGB / SSIM luma): Kodak −4,63% / +0,85% (było −3,84% / +9,25%), prywatne wycinki
  −2,84% / +0,77% (było −1,18% / +9,16%). 12 MP w ~5,6 s (po RQT było 9,2 s).
- Dalsze kandydaty: psy-rd, SIMD (SATD, transformacje, RDOQ), równoległość w obrębie kafla (WPP).

## Kryterium i plan 0.3.0 (ustalone 2026-10-16, przed implementacją)

- **Kryterium (decyzja użytkownika):** przy równym PSNR (RGB względem źródła, dekodowane przez heif-dec) i równym SSIM
  rozmiar pliku HEIC ≤ 1,5× pliku z x265 `medium` (heif-enc), mierzone jako BD-rate po 4 punktach jakości na obrazach
  testowych (prywatne zdjęcia `Category=Local` i ewentualnie Kodak po zgodzie). Wynik niezależny od liczby wątków.
- **Poprawność:** każdy plik czytany przez libheif (heif-dec) i HM; rekonstrukcja enkodera = wynik naszego dekodera = HM.
- **Kolejność:** zapis bitstreamu i CABAC → enkoder intra (decyzje CU/trybów przez SATD+RD, kwantyzacja, potem RDOQ, SAO,
  podział TU) → writer HEIF (siatka 512×512, alfa 4:0:0, ICC/nclx, EXIF/XMP) → `SaveAsHeic`/`HeifEncoderOptions`.

## Stan implementacji 0.3.0 (2026-10-16)

- Gotowe: enkoder HEVC intra w `src/PictureSharp.Heif/Hevc/Encoder/` (port z HM: CABAC i estymacja bitów, zapis składni,
  transformaty w przód, kwantyzacja, decyzje trybów/CU): CTB 32, CU 32/16/8 + NxN, TU = CU, SATD (35 trybów) → RD z
  estymacją CABAC, 5 trybów chromy, podział CU przez RD, deblocking; SAO/RDOQ/sign hiding/RQT wyłączone (miejsce na
  poprawy). Predykcja intra współdzielona z dekoderem (`HevcIntraPredictor`).
- Gotowe: writer HEIF (`HeifWriter`), `HeifImageEncoder` (BT.601 full 4:2:0, chroma 2×2, siatka 512, alfa 4:0:0 RExt
  Monochrome w tym samym podziale, nieparzyste wymiary przez `clap`, orientacja EXIF → `irot`/`imir` z EXIF TopLeft,
  ICC → `colr prof`, inaczej nclx 1/13/6 full, Exif/XMP z `cdsc`), publiczne `HeifEncoderOptions` (Quality, AlphaQuality,
  TileSize, MaxDegreeOfParallelism) i `SaveAsHeic`/`SaveAsHeicAsync`/`ToHeic`; docs/encoding.md.
- Wyniki: HM dekoduje strumienie identycznie z naszym dekoderem, bez deblockingu dekoder = rekonstrukcja enkodera;
  heif-dec czyta pliki (pojedyncze, nieparzyste, siatki, alfa, alfa w siatce, orientacje, alfa+obrót) identycznie z
  nami; wynik niezależny od liczby wątków. Pomiar (Local, 5 wycinków 768×512, Q 30/45/60/75): BD-rate −1,18% (PSNR RGB)
  i +9,16% (SSIM luma) względem x265 medium (`docs/measurements/encoder-bdrate-x265-medium.tsv`); 12 MP w ~5,6 s na 16 wątkach.
- Luka API rdzenia (zgłosić do planu PictureSharp): zapis (`Save`/`Encode`/`SaveAsX`) używa tylko
  `ImageConfiguration.Default`; wtyczka nie może zapisać przez konfigurację przekazaną jak w `DecoderOptions.Configuration`.

## Zasady

- Te same reguły co rdzeń: `net10.0`, C# 14, BCL-only, bez `unsafe` i P/Invoke (`System.IO.Compression` dozwolone do dekompresji, decyzja użytkownika z
  2026-10-17); zależność
  wyłącznie od `PictureSharp` przez publiczne API (test architektury jak dla adapterów). Wyjątki, limity, anulowanie,
  pule buforów, `Rgba32` z prostą alfą.
- Licencje: port tylko z kodu zgodnego z MIT. Dekoder HEVC według ITU-T H.265 (bezpłatna specyfikacja) i
  oprogramowania referencyjnego HM (BSD-3); enkoder według HM i kvazaar (BSD-3). Kontener według ISO/IEC 23008-12 i
  ISO/IEC 14496-12. libde265, libheif (LGPL) i x265 (GPL) wyłącznie jako wyrocznie, bez portowania kodu.
  `THIRD-PARTY-NOTICES.txt` paczki uzupełniony o porty.
- Patenty HEVC: biblioteka ich nie rozstrzyga. README paczki opisuje sytuację; decyzja o publikacji paczki należy do
  użytkownika i zapada przed wydaniem.
- Źródła referencyjne przypięte w `reference/` tego repozytorium (HM, kvazaar, libheif + libde265 do narzędzi), read-only; pobranie
  wymaga zgody użytkownika. Narzędzia wyroczni (`heif-dec`, `heif-enc`, `heif-info`, dekoder HM) budowane skryptem
  `scripts/build-heif-reference.sh`, z nadpisaniem katalogu przez `PICTURESHARP_HEIF_TOOLS`.
- Port funkcja po funkcji z komentarzem źródłowym; najpierw skalar, SIMD dopiero po pomiarach i z testami identyczności.

## Zakres dekodowania

- Kontener: `ftyp` (`heic`, `heix`, `mif1`), `meta` z `hdlr pict`, `pitm`, `iinf`/`infe`, `iloc` (wersje 0–2, metody
  0 i 1), `iref` (`dimg`, `auxl`, `thmb`, `cdsc`, `prem`), `iprp`/`ipco`/`ipma` (`hvcC`, `ispe`, `colr` nclx/prof,
  `pixi`, `irot`, `imir`, `clap`, `auxC`), `idat`. Elementy `grid` (kafle iPhone’a) i `iden`; transformacje w kolejności
  z `ipma`. Ustalić relację z `ApplyExifOrientation` (HEIF stosuje `irot`/`imir`, EXIF orientation jest informacyjne).
- HEVC: profile Main, Main Still Picture i Main 10; chroma 4:2:0 i 4:0:0. VPS/SPS/PPS, segmenty slice, CABAC, drzewo
  kodowania, predykcja intra (35 trybów), transform skip, listy skalowania, transformaty 4–32 i DST 4×4, dekwantyzacja,
  deblocking, SAO, kafle i WPP, PCM. Slice inter, 4:2:2/4:4:4 (RExt) i SCC → `NotSupportedException`.
- Kolor: YCbCr → RGB według nclx (macierze BT.601/709/2020, zakres pełny/ograniczony) z dokładnie opisanym
  zaokrągleniem; ICC do metadanych; 10-bit → 8-bit jak PNG 16-bit. Alfa z obrazu pomocniczego (auxC alpha) jako prosta
  alfa, z odwróceniem premultiplikacji przy `prem`.
- Metadane: elementy Exif (z prefiksem offsetu), XMP i ICC w `ImageMetadata`; `Identify` bez dekodowania HEVC; miniatura
  `thmb` jako możliwa szybka ścieżka `TargetSize` (wymaga SPI z rdzenia 0.8.0).
- HDR: gain map Apple (`urn:com:apple:photo:2020:aux:hdrgainmap`) i ISO (`tmap`) dołączane przez SPI z rdzenia 0.7.0, więc
  `ToHdr`/`ToneMap` działają dla HEIC. Mapy głębi i matowe portretów, Live Photos, sekwencje HEIF i AVIF poza zakresem
  (AVIF może później współdzielić parser kontenera).
- Limity: `MaxInputBytes`, `MaxPixels` (suma kafli, alfy i mapy), `MaxMetadataBytes`, liczba elementów, głębokość i
  cykle `iref`, wymiary siatki i kafli, rozmiary NAL. Kafle siatki dekodowane równolegle, wynik niezależny od liczby wątków.

## Zakres kodowania

- Pojedynczy obraz (polityka dla animacji jak w pozostałych enkoderach jednoobrazowych), HEVC Main 8-bit 4:2:0, alfa
  jako obraz pomocniczy 4:0:0, siatka kafli dla dużych obrazów, nclx/ICC, Exif i XMP. Domyślne parametry kolorów
  ustalone według `heif-enc`.
- Enkoder intra: `Quality` 0–100 → QP, CTU 32/64, wybór trybów przez SATD i uproszczone RDO, CABAC, deblocking i SAO;
  deterministyczny i niezależny od liczby wątków (kafle równolegle).
- `HeifEncoderOptions` (robocze): `Quality`, `TileSize`, `Effort`, `MaxDegreeOfParallelism`. Bezstratny tryb, 4:4:4 i
  10-bit poza pierwszymi wersjami. Zapis gain map w HEIC po SPI z rdzenia 0.8.0 (decyzja po etapie dekodera).

## Referencje i tolerancje

Ustalane i zapisywane przed implementacją danej wersji.

- Dekoder: YCbCr bit-exact (tolerancja 0) z HM i libde265 na strumieniach konformacyjnych intra JCT-VC (pobranie za
  zgodą) i syntetycznym korpusie z `heif-enc` (x265 i kvazaar) oraz ffmpeg. RGB z `heif-dec` przy tej samej konwersji
  kolorów: 0, inaczej ustalona tolerancja.
- Korpus syntetyczny: siatki, alfa, rotacje/lustra/clap, 10-bit, monochrom, nclx/ICC, Exif/XMP, gain map Apple i ISO;
  uszkodzone, obcięte, cykliczne referencje i fuzzing. Prywatne zdjęcia HEIC z iPhone’a tylko lokalnie (`Category=Local`).
- Enkoder: pliki odczytywane przez libheif i dekoder HM; nasz dekoder identyczny z HM na naszych plikach. Kryterium
  jakości/rozmiaru względem x265 i kvazaar przy równym PSNR/SSIM (np. rozmiar ≤ 1,5× x265 `medium`) zapisane przed
  implementacją enkodera.
- Zgodność odbiorców: ręcznie Windows (HEIF Image Extensions) i Apple Photos, gdy dostępne; brak nie jest zaliczeniem.

## Kolejność prac

Według tabeli wersji; każdy etap z testami i wpisem w `progres.md`. SIMD i równoległość (IDCT/DST, predykcja intra, deblocking,
SAO, YCbCr→RGB) dopiero po wersji skalarnej.

## DoD całości (do 0.5.0)

- Wyrocznie zielone w ustalonych tolerancjach; skalar i SIMD identyczne na x64 i ARM64; wynik niezależny od liczby wątków.
- Testy limitów, uszkodzonych danych i anulowania; żadne wejście nie daje niekontrolowanego wyjątku (fuzzing `Category=Slow`).
- Pomiary dekodowania 12/48 MP i kodowania względem libheif (czas, alokacje, szczytowa pamięć).
- Paczka `PictureSharp.Heif` z README (w tym nota o patentach), `LICENSE` i notices; test architektury; konsument
  trim/Native AOT; dokumentacja z przykładami sprawdzanymi testami; przykład CLI z rejestracją wtyczki.


## Zależności od rdzenia PictureSharp

Audyt SPI rdzenia (2026-10-16, stan 0.6.8): dekod i zapis HEIC SDR są możliwe na obecnym publicznym API. Luki
zamykane w rdzeniu:
- 0.6.9: `ImageInfo.ContainerItemCount` dla `Identify` (wymagane przez 0.1.0).
- 0.7.0: dołączanie gain map przez wtyczkę (`GainMapMetadata`, `GainMapImage`, `ImageDecodeContext.AttachGainMap`),
  kolor bazy HDR z metadanych (ICC lub `Chromaticities`/`Gamma`), `IImageFormatMetadataCodec` dla `ReadMetadata`,
  opisany kontrakt orientacji.
- 0.7.1: `AttachGainMap` z pełnym `HdrInfo.Detected` (wydane 2026-10-17, używane od 0.4.0).
- 0.7.6: opcje dekodera per wtyczka (`FormatDecoderOptions`, `DecoderOptions.FormatOptions`), używane od 0.6.0.
- 0.8.0: gain map w enkoderach wtyczek i serializacja ISO, ewentualnie `ReportStoredSize` dla miniatur `thmb` — tylko
  jeśli wtyczka wykaże potrzebę.
- Poza planem: CICP PQ/HLG (HDR10 HEIC z Androida).

Nowe potrzeby SPI zgłaszamy do planu rdzenia; nie obchodzimy ich kopiowaniem kodu wewnętrznego.

## Wspólna bramka jakości każdej wersji

- Build Release bez ostrzeżeń, pełne testy Release i Oracle przed zamknięciem wersji. Pominięta wyrocznia nie
  potwierdza zgodności.
- Korpus syntetyczny, testy uszkodzonych/obciętych danych i limitów dla nowych ścieżek.
- Nowe SIMD: identyczność ze skalarem na x64 i rzeczywistym ARM64, sprawdzenie fallbacków; równoległość:
  deterministyczność i zwalnianie zasobów po błędzie.
- Aktualne XML docs, migawka publicznego API, przykłady, notices i paczka z `LICENSE` oraz `THIRD-PARTY-NOTICES.txt`.
- Po znaczącym kroku wpis w `progres.md`. Plan aktualizujemy po rzeczywistym spełnieniu DoD.

## Changelog

Brak wydanych wersji.
