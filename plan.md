# PictureSharp.Heif: plan rozwoju

## Stan i cel

**Bieżąca wersja: 0.6.1, wydana 2026-10-18; minimalny PictureSharp 0.7.6.** Wtyczka formatu w C#, bez bibliotek
natywnych, z własnym dekoderem i enkoderem HEVC intra.

- Odczyt: kontener HEIF (`heic`/`heix`/`mif1`), HEVC Main/Main 10/Main Still Picture 4:2:0 i 4:0:0, siatki, `iden`,
  `irot`/`imir`/`clap`, alfa (`auxl`, `prem`), nclx/ICC, Exif/XMP, gain mapy Apple/ISO (`ToHdr`), `ReadMetadata` bez
  dekodu HEVC, wybór elementu przez `HeifDecoderOptions.ItemId`, skompresowane XMP (`deflate`). Slice'y P/B, RExt, SCC, PQ/HLG, sekwencje →
  `NotSupportedException`.
- Zapis: HEVC Main 8-bit 4:2:0, alfa 4:0:0, siatka kafli, nclx/ICC, Exif/XMP (`SaveAsHeic`, `HeifEncoderOptions`).
- SIMD x64/ARM64 bitowo zgodne ze skalarem, równoległe kafle, wynik niezależny od liczby wątków.

**Cel 0.7.0 (zakres uproszczony 2026-10-18):** katalog zawartości pliku (zdjęcia, miniatury, obrazy pomocnicze),
odczyt i generowanie miniatur, surowe próbki depth oraz rozpoznawanie par Live Photo. Realizacja w wydaniach
0.6.1–0.6.3, następnie integracyjne 0.7.0. Zapis kolekcji, animacje HEIF, interpretacja depth i eksport Live Photo
przeniesione poza 0.7.0.

Decyzje użytkownika (2026-10-16): własny port dekodera i enkodera HEVC intra w C# (nie wrapper libheif); osobne
repozytorium i paczka z własnym SemVer od 0.1.0; zależność wyłącznie od paczki NuGet `PictureSharp`. Zmiany potrzebne
w samej bibliotece planuje rdzeń (`../picturesharp/plan.md`).

Reguły pracy: [AGENTS.md](AGENTS.md). Lokalna historia pracy: `progres.md` (bez commitowania). Historia zamkniętych
wersji 0.1.0–0.6.0 (DoD, wyniki, pomiary, kryteria enkodera): [docs/history.md](docs/history.md).

## Wersje

| Wersja | Zakres | Minimalny PictureSharp | Status |
| --- | --- | --- | --- |
| 0.1.0 | Kontener HEIF, detekcja, `Identify`, limity, fuzzing parsera | 0.6.9 | Zamknięta lokalnie |
| 0.2.0 | Dekoder HEVC intra, siatka, kolor, alfa, transformacje, ICC/EXIF/XMP | 0.6.9 | Zamknięta lokalnie |
| 0.3.0 | Enkoder HEVC intra Main 8-bit 4:2:0, alfa, siatka, metadane | 0.6.9 | Zamknięta lokalnie |
| 0.4.0 | Gain map Apple/ISO → `ToHdr`, `ReadMetadata` bez dekodu | 0.7.1 | Zamknięta lokalnie |
| 0.5.0 | SIMD i równoległość, pomiary względem libheif, ARM64 | 0.7.1 | Wydana 2026-10-17 (pierwsze publiczne wydanie) |
| 0.6.0 | `HeifDecoderOptions.ItemId` przez `DecoderOptions.FormatOptions` | 0.7.6 | Wydana 2026-10-18 |
| 0.6.1 | Skompresowane XMP: zlib/deflate i limity | 0.7.6 | Wydana 2026-10-18 |
| 0.6.2 | Katalog `HeifDocument`, powiązania, auxiliary, odczyt i generowanie miniatur | 0.7.6 (założenie) | Planowana |
| 0.6.3 | Surowe próbki depth, rozpoznawanie par Live Photo | 0.7.6 (założenie) | Planowana |
| 0.7.0 | Integracja, regresje, dokumentacja i paczka całego zakresu | 0.7.6 (założenie) | Planowana |
| dalej | Patrz „Poza 0.7.0” | Do ustalenia | Poza 0.7.0 |

Numery określają kolejność, nie terminy. Każda wersja wymaga własnego DoD (niżej) oraz wspólnej bramki jakości;
testy odporności (uszkodzone/obcięte dane, limity, anulowanie) powstają w etapie, który dodaje daną ścieżkę.
Publikacja każdej wersji wyłącznie na decyzję użytkownika.

## Plan 0.6.1–0.7.0

### 0.6.1 — skompresowane XMP

- Element `mime` XMP z `content_encoding` = `deflate` rozpakowany przez `ZLibStream` (z zapasowym `DeflateStream`,
  gdy brak nagłówka zlib) i dostępny w `Metadata.Xmp` przy `Decode` i `ReadMetadata`.
- Rozmiar po rozpakowaniu liczony przez `ReserveMetadata` i ograniczony: bomba dekompresji → `ImageFormatException`,
  bez alokacji ponad limit.
- Nieznane kodowanie lub uszkodzony strumień XMP nie blokuje obrazu: blok jest pomijany (dziś cały plik kończy się
  `NotSupportedException`). Zapis bez zmian (XMP nieskompresowane); brak nowego publicznego API.
- **DoD:** zawartość = XMP źródłowe, uszkodzone dane, bomba dekompresji, `IgnoreMetadata`, odczyt przez `Load`
  i `ReadMetadata`; syntetyczne `infe` i pliki z `heif-enc`, jeśli wyrocznia generuje skompresowane metadane;
  fuzzing skompresowanego bloku; snapshot API bez zmian.
- **Stan (2026-10-18):** DoD spełnione poza plikami z `heif-enc` — libheif 1.20.2 nie ma opcji kompresji metadanych,
  więc testy używają syntetycznych `infe` (pliki z enkodera wtyczki z podmienionym blokiem XMP). Obcięty strumień
  traktowany jak uszkodzony (`DeflateStream` zwraca wtedy dane częściowe bez błędu). Przy `IgnoreMetadata` XMP nie
  jest rozpakowywane, chyba że potrzebuje go gain mapa Apple (wtedy limit `MaxMetadataBytes` nadal obowiązuje).
  Pełne testy Release 405/406 (1 pominięty), 0 ostrzeżeń.

### 0.6.2 — katalog, powiązania i miniatury

- `HeifDocument` i niemutowalne opisy elementów w `PictureSharp.Formats.Heif`. Otwieranie ze ścieżki, `Stream`
  i pamięci; bufor wejścia ograniczony przez `MaxInputBytes`, bez dekodowania pikseli.
- `PrimaryItemId`, katalog elementów i `TopLevelImages`: ID, typ, nazwa i flaga `hidden` z `infe` (dziś pomijane),
  dostępne wymiary i głębia bitowa. Klasyfikacja zdjęć uwzględnia relacje i ukrycie; nie każdy `hvc1` jest
  osobnym zdjęciem. `ContainerItemCount` i `Image.Frames` bez zmian.
- Powiązania konkretnego zdjęcia: miniatury (`thmb`), auxiliary (`auxl`) z typem i surowym `aux_subtype`
  (także nieznanych producentów), zależności `dimg`, metadane `cdsc`. Rozróżnienie alpha, depth/disparity, gain mapy
  i pozostałych auxiliary, zawsze z oryginalnym identyfikatorem typu. Dekod dowolnego elementu nadal przez `ItemId`.
- Katalogowanie oddzielone od dekodu: nieobsługiwany dodatkowy element pozostaje w katalogu, dopiero próba jego
  dekodu zgłasza `NotSupportedException`.
- Odczyt podglądu: ID zdjęcia i rozmiar docelowy → najmniejsza obsługiwana miniatura pokrywająca rozmiar, inaczej
  dekod i skalowanie zdjęcia; własne transformacje, kolor i alfa miniatury. `Image.Load(TargetSize)` bez zmian
  (osadzona miniatura jest jawnym wyborem).
- Generowanie miniatury w `SaveAsHeic` według maksymalnego wymiaru (opcja w `HeifEncoderOptions`, domyślnie
  wyłączona): zachowane proporcje, bez powiększania, `thmb`, kolor i alfa, bez kopiowania EXIF/XMP. Bez miniatur
  przekazywanych przez użytkownika.
- **DoD:** poprawny katalog dla zdjęć, kafli, miniatur, alfy, gain map, nieznanych auxiliary i metadanych bez
  uruchamiania dekodera HEVC; wiele zdjęć ze współdzielonymi zależnościami bez mieszania metadanych; podgląd przy
  wielu rozmiarach i braku miniatury, orientacje bez podwójnego obrotu; libheif rozpoznaje generowaną miniaturę,
  a API podglądu ją odczytuje.

### 0.6.3 — surowe próbki depth i pary Live Photo

- `HeifDepthImage`: `ReadOnlyMemory<ushort>`, głębia bitowa, wymiary, ID zdjęcia nadrzędnego, surowe `aux_subtype`
  i surowe bloki metadanych elementu (`cdsc`, XMP). Próbki pobierane przed konwersją koloru i redukcją do 8 bitów;
  siatki, kadrowanie, obrót i odbicie bez zmiany wartości, bez gamma i skalowania do rozmiaru zdjęcia. Bez
  interpretacji jednostek, kalibracji i konwersji do Z/disparity. Budżety przed alokacją, anulowanie.
- `LivePhotoPair`: jawnie podane zdjęcie HEIC/JPEG i film MOV (bez przeszukiwania katalogów). Odczyt
  ContentIdentifier zdjęcia (Apple MakerNote; parser jak dla gain mapy w `HeifGainMap`) i filmu (`com.apple.quicktime.content.identifier`
  w `meta`/`keys`) oraz znacznika czasu zdjęcia w filmie, jeśli jest. Wynik: dopasowane, brak metadanych lub
  sprzeczne ID; nazwy plików nie potwierdzają pary. Parser MOV czyta tylko potrzebne boksy, z limitami.
- **DoD:** próbki depth bitowo zgodne z HM/libde265 (także 10-bit, siatki, transformacje), limity/anulowanie;
  Live Photo: HEIC+MOV i JPEG+MOV, brak i sprzeczność ID, uszkodzone/obcięte MOV, fuzzing parsera MOV.

### 0.7.0 — integracja i zamknięcie zakresu

- Bez nowych funkcji: regresja katalogu, miniatur, depth, HDR i Live Photo; korpus z kombinacjami miniatur, alfy,
  depth, gain map i auxiliary producentów; pomiar pamięci/czasu otwierania katalogu dużych plików.
- Zgodność API 0.6.0, architektura, SIMD/skalar i deterministyczność liczby wątków.
- Walidacja paczki NuGet i przykładów; dokumentacja możliwości i ograniczeń.
- **DoD:** kryteria 0.6.1–0.6.3 spełnione, dokumentacja zgodna z paczką, brak regresji. Dopiero potem aktualizacja
  VERSION i statusu w planie; publikacja na osobną decyzję użytkownika.

### Wspólne założenia i granice

- Nowe API należy do `PictureSharp.Formats.Heif`. Zwrócone obrazy są własnością użytkownika; operacje nie zamykają
  strumieni użytkownika.
- Katalog nie alokuje pikseli. Odczyt zdjęcia rozlicza jego zależności. API surowych próbek zapewnia równoważne
  limity przed alokacją, mimo braku kontekstu rdzenia.
- Buforowane wejście przez `Stream` nie oznacza streamingu dużych plików.

### Poza 0.7.0

- Zapis kolekcji wielu zdjęć (`HeifCollection`, wybór primary, `ipma` > 127) — po zgłoszeniu konkretnej potrzeby.
- Sekwencje HEIF: odczyt/zapis ścieżek — razem z dekoderem HEVC P/B (same klatki all-intra rzadko występują).
- Interpretacja depth/disparity (metadane Apple, jednostki, kalibracja, konwersje).
- Live Photo: eksport/zapis par, dekod i edycja filmu.
- Zapis gain map HDR (po SPI rdzenia 0.8.0), depth i dowolnych auxiliary; miniatury od użytkownika; edycja bez
  rekompresji; overlay, grupy i regiony; bezstratny tryb, 4:4:4 i 10-bit w enkoderze.
- Poza harmonogramem: AVIF, nowe profile HEVC, CICP PQ/HLG, pliki ponad dostępne bufory.

### Źródła zakresu (przegląd 2026-10-18)

- Kolekcje, miniatury, auxiliary i obrazy pochodne: [HEIF Technical Information](https://nokiatech.github.io/heif/technical.html).
- Porównanie funkcjonalne (nie zależność ani źródło portowania): [libheif-sharp](https://github.com/0xC0000054/libheif-sharp).
- Depth: [Apple — Creating auxiliary depth data manually](https://developer.apple.com/documentation/avfoundation/creating-auxiliary-depth-data-manually).
- Live Photo: [Apple — Capturing and saving Live Photos](https://developer.apple.com/documentation/avfoundation/capturing-and-saving-live-photos).

## Zasady

Ograniczenia techniczne, licencje, patenty, porty i wyrocznie opisuje [AGENTS.md](AGENTS.md). Uzupełnienia:

- Narzędzia wyroczni z `scripts/build-heif-reference.sh` (HM 16.20, kvazaar 2.3.1, x265 3.4, libde265 1.0.15,
  libheif 1.20.2); źródła w `reference/` read-only, pobieranie za zgodą użytkownika.
- Zgodność odbiorców (Windows HEIF Image Extensions, Apple Photos) ręcznie, gdy dostępne; brak testu nie jest
  zaliczeniem i nie uprawnia do deklaracji zgodności.
- Repozytorium `art-ix/PictureSharp.Heif`; GitHub Actions wyłączone na życzenie użytkownika, workflow
  `.github/workflows/ci.yml` pozostaje do późniejszego włączenia.

## Zależności od rdzenia PictureSharp

Używane: 0.7.1 (`AttachGainMap` z `HdrInfo.Detected`), 0.7.6 (`FormatDecoderOptions`). Planowane w rdzeniu 0.8.0:
gain map w enkoderach wtyczek i serializacja ISO, ewentualnie `ReportStoredSize` dla miniatur — tylko po wykazaniu
potrzeby. Nowe potrzeby SPI zgłaszamy do planu rdzenia; nie obchodzimy ich kopiowaniem kodu wewnętrznego.

## Wspólna bramka jakości każdej wersji

- Build Release bez ostrzeżeń, pełne testy Release i Oracle przed zamknięciem wersji. Pominięta wyrocznia nie
  potwierdza zgodności.
- Korpus syntetyczny, testy uszkodzonych/obciętych danych i limitów dla nowych ścieżek.
- Nowe SIMD: identyczność ze skalarem na x64 i rzeczywistym ARM64, sprawdzenie fallbacków; równoległość:
  deterministyczność i zwalnianie zasobów po błędzie.
- Aktualne XML docs, migawka publicznego API, przykłady, notices i paczka z `LICENSE` oraz `THIRD-PARTY-NOTICES.txt`.
- Po znaczącym kroku wpis w `progres.md`. Plan aktualizujemy po rzeczywistym spełnieniu DoD.

## Changelog

- [0.6.1](docs/releases/release-0.6.1.md): skompresowane XMP (`deflate`) w limicie `MaxMetadataBytes`.
- [0.6.0](docs/releases/release-0.6.0.md): wybór elementu przez `HeifDecoderOptions.ItemId`, minimum PictureSharp 0.7.6.
- [0.5.0](docs/releases/release-0.5.0.md): pierwsze publiczne wydanie, SIMD i równoległość.
- 0.5.1 nie została zrealizowana; plan skompresowanego XMP przeniesiono do 0.6.1.
- Wcześniejsze wersje (0.1.0–0.4.0, niewydane publicznie): [docs/history.md](docs/history.md).
