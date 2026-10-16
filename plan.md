# PictureSharp.Heif: plan rozwoju

## Stan i cel

**Bieżąca wersja: brak wydania; 0.1.0 i 0.2.0 zamknięte lokalnie (PictureSharp 0.6.9 z NuGet): `Identify` i `Decode` HEIC, YCbCr bit-exact z HM, RGB identyczne z heif-dec (2026-10-16). Następna 0.3.0 (enkoder).** PictureSharp.Heif to wtyczka formatu dla
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
| 0.2.0 | Dekoder HEVC intra (skalar), siatka, kolor, alfa, `irot`/`imir`/`clap`, ICC/EXIF/XMP | 0.6.9 | Zamknięta lokalnie (DoD spełnione; opcjonalnie strumienie JCT-VC po zgodzie na pobranie) |
| 0.3.0 | Enkoder HEVC intra Main 8-bit 4:2:0, alfa, siatka, metadane | 0.6.9 | Planowana |
| 0.4.0 | Gain map Apple/ISO → `ToHdr`, `ReadMetadata` bez dekodu | 0.7.0 | Planowana |
| 0.5.0 | SIMD i równoległość, pomiary względem libheif, ARM64 | 0.7.0 | Planowana |
| dalej | Zapis gain map w HEIC, ewentualnie szybka miniatura `thmb` i opcje dekodera | 0.8.0 | Do decyzji |

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
- Opcjonalnie (decyzja użytkownika): strumienie konformacyjne JCT-VC intra (pobranie wymaga zgody).

## Zasady

- Te same reguły co rdzeń: `net10.0`, C# 14, BCL-only, bez `unsafe`, P/Invoke i `System.IO.Compression`; zależność
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
- 0.8.0: gain map w enkoderach wtyczek i serializacja ISO, ewentualnie `ReportStoredSize` dla miniatur `thmb` i opcje
  dekodera per wtyczka — tylko jeśli wtyczka wykaże potrzebę.
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
