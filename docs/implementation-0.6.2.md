# Plan implementacji PictureSharp.Heif 0.6.2

Stan: implementacja ukończona lokalnie 2026-10-18, z uwzględnieniem uzgodnionej korekty kontraktu API.
Poniższy kontrakt wdrożono w kodzie, testach, migawce API i przykładach. Wersja 0.6.2 wydana 2026-10-18.
Zakres i nadrzędne DoD: [plan.md](../plan.md#062--katalog-powiązania-i-miniatury).

## Ustalenia

- Realizacja w istniejącym repozytorium, na publicznym API paczki PictureSharp 0.7.6. Bez nowych zależności.
- Katalog nie uruchamia dekodera HEVC i nie alokuje pikseli. Walidacja struktury kontenera pozostaje obowiązkowa.
- Nieobsługiwany dodatkowy obraz, auxiliary lub właściwość nie uniemożliwia katalogowania. Ograniczenia dekodera
  sprawdzamy dla wybranego obrazu i jego zależności przy odczycie pikseli.
- `Image.Load`, `Image.Frames`, `ContainerItemCount` i wybór przez `HeifDecoderOptions.ItemId` zachowują semantykę.
- Miniatury są wybierane jawnie przez API dokumentu. Zapis miniatur domyślnie wyłączony.
- Bez zmian wersji paczki, commita, pusha i publikacji w ramach samej implementacji; przygotowanie wydania osobno.
- Depth jako próbki i Live Photo należą do 0.6.3; zapis kolekcji i miniatur dostarczonych przez użytkownika poza zakresem.

## 1. Kontrakt API i własność danych

Docelowy kontrakt do wdrożenia i sprawdzenia testem konsumenta:

- `HeifDocument.Open(path / Stream / ReadOnlyMemory<byte>, HeifDocumentOptions? options = null,
  CancellationToken cancellationToken = default)`.
- `HeifDocument.OpenAsync(path / Stream, HeifDocumentOptions? options = null,
  CancellationToken cancellationToken = default)` zwracające `Task<HeifDocument>`. Bez wariantu async dla pamięci.
- Niemutowalne `HeifDocumentOptions` opisują wyłącznie otwieranie kontenera. Na tym etapie zawierają
  `MaxInputBytes` (wartość dodatnia, domyślnie `Array.MaxLength`, zgodnie z rdzeniem).
  Nie zawierają `ItemId`, `TargetSize`, konfiguracji dekodera, konwersji koloru ani `LoadGainMap`.
- Dokument: `PrimaryItemId`, `PrimaryImage` (`HeifItemInfo`, opis wskazany przez primary ID, bez dekodowania),
  `Items`, `TopLevelImages` i `GetItem(uint itemId)`. `PrimaryImage` nie zależy od filtrowania `TopLevelImages`.
- `LoadImage(uint itemId, DecoderOptions? options = null, CancellationToken cancellationToken = default)`:
  odczyt wskazanego obrazu z bufora dokumentu, bez ponownego dostarczania ścieżki lub strumienia.
- `LoadPreview(uint itemId, Size targetSize, DecoderOptions? options = null,
  CancellationToken cancellationToken = default)` oraz skrót dla primary:
  `LoadPreview(Size targetSize, DecoderOptions? options = null, CancellationToken cancellationToken = default)`.
- Niemutowalny `HeifItemInfo`: `Id`, `Type` (oryginalny FourCC), `Name`, `IsHidden`, opcjonalne wymiary
  i głębia bitowa, relacje oraz opcjonalny opis auxiliary. Nie deklarować pewnej dekodowalności na podstawie samego katalogu.
- Relacja zachowuje oryginalny FourCC i kolejność docelowych ID. Wygodne listy dla zdjęcia obejmują miniatury,
  auxiliary, źródła `dimg` oraz metadane `cdsc`. Kierunek jest jawny: `thmb`, `auxl`, `cdsc` prowadzą do zdjęcia,
  a `dimg` prowadzi od obrazu pochodnego do źródeł.
- Opis auxiliary: oryginalny `aux_type`, surowe `aux_subtype` i rozpoznana kategoria
  (alpha, depth, disparity, gain map, pozostałe). Nieznany typ zachowuje identyfikator i bajty.
- `HeifEncoderOptions.ThumbnailMaxDimension`: 0 wyłącza miniaturę, wartość dodatnia ogranicza dłuższy bok;
  wartości ujemne są odrzucane.

Dokument przechowuje własną kopię wejścia, także przy otwarciu z pamięci, aby modyfikacja bufora użytkownika nie
unieważniała walidacji. Strumień czytany od bieżącej pozycji do końca, bez wymagania `Seek` i bez zamykania go.
Dokument nie trzyma uchwytu pliku; nie wymaga `IDisposable`. Obrazy zwracane przez `LoadImage` i `LoadPreview`
należą do użytkownika. `OpenAsync` korzysta z asynchronicznego I/O z tokenem, a następnie parsuje kontener
z kontrolą anulowania; nie jest opakowaniem synchronicznego `Open` w `Task.Run`.
Kolekcje nie mogą udostępniać mutowalnych tablic; surowe bajty auxiliary udostępnić jako kopię lub kolekcję tylko
do odczytu. Dokument nie zapamiętuje opcji dekodowania: każde wywołanie `LoadImage`/`LoadPreview` otrzymuje
własne `DecoderOptions` (null = domyślne opcje rdzenia). Jawne ID zastępuje `HeifDecoderOptions.ItemId`;
w podglądzie jawny rozmiar zastępuje dodatkowo `TargetSize`. `LoadImage` respektuje przekazane `TargetSize`
tak jak `Image.Load`; domyślnie zwraca pełny rozmiar. Pozostałe opcje zachowujemy bez mutowania obiektu użytkownika.
Rejestracja `HeifFormat` w konfiguracji przekazanej do odczytu lub w konfiguracji domyślnej jest wymagana
do dekodowania, jak przy `Image.Load`; samo katalogowanie jej nie wymaga.

Limity mają jawny zakres: `HeifDocumentOptions.MaxInputBytes` ogranicza kopiowanie/buforowanie wejścia podczas
otwierania, a `DecoderOptions.MaxPixels` i `MaxMetadataBytes` ograniczają pojedynczy odczyt obrazu i jego
wymaganych zależności. Jeśli opcje odczytu określają niższe `MaxInputBytes` niż rozmiar bufora dokumentu,
odczyt odrzuca dane zgodnie z kontraktem `Image.Load`. Większy limit późniejszego odczytu nie zmienia limitu
zastosowanego podczas otwierania. Niezależne odczyty nie zużywają wspólnego budżetu dokumentu.

Przykład docelowego użycia (do objęcia testem konsumenta):

```csharp
var configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new HeifFormat()).Build();
var document = await HeifDocument.OpenAsync(path,
    new HeifDocumentOptions { MaxInputBytes = 64L << 20 }, cancellationToken);
var decodeOptions = new DecoderOptions { Configuration = configuration, LoadGainMap = true };
using Image photo = document.LoadImage(document.PrimaryImage.Id, decodeOptions, cancellationToken);
using Image preview = document.LoadPreview(new Size(320, 320),
    new DecoderOptions { Configuration = configuration }, cancellationToken);
```

**Wynik:** XML docs opisują własność, limity, brak dekodu przy otwieraniu i wyjątki. Publiczne typy pozostają
w `PictureSharp.Formats.Heif`; szczegóły kontenera są wewnętrzne.

## 2. Oddzielenie parsera kontenera od przygotowania dekodu

Obecnie `HeifContainer.Parse` po walidacji wywołuje `Describe`, wybiera alfę i sprawdza budżet pikseli.
Te operacje należy przenieść do przygotowania wybranego obrazu; dotychczasowe ścieżki dekodera i metadanych
nadal je wywołują. Katalog korzysta wyłącznie ze strukturalnego wyniku parsowania.

Prace w `Container/HeifContainer*.cs` i `HeifItem.cs`:

- Zachować nazwę i flagę `hidden` z `infe`.
- Oddzielić uszkodzenie struktury od poprawnej, ale nieobsługiwanej cechy elementu. Przejrzeć obecne globalne
  `NotSupportedException` (m.in. ochrona, lokalizacja danych, właściwości) i odroczyć ograniczenia lokalne do
  wybranego elementu. Ograniczeń uniemożliwiających odczyt struktury całego kontenera nie maskować.
- Zachować kontrole długości, indeksów, niezdefiniowanych referencji, cykli, głębokości, liczby boksów i wpisów.
- Nie czytać próbek HEVC ani nie rozpakowywać XMP podczas katalogowania. Wymiary/głębię pozyskiwać z deklaracji
  kontenera; transformacje uwzględnić, gdy ich wynik da się określić. Brak informacji oznacza wartość opcjonalną.
- Sprawdzić rozliczanie metadanych: dziś `ValidateItems` i `ValidateProperty` liczą bloki globalnie.
  Odczyt zdjęcia powinien uwzględniać jego metadane i wymagane zależności, bez bloków innych zdjęć;
  zachować limity rozpakowanego XMP oraz specjalną ścieżkę gain map.
- Dodać anulowanie parsera również bez `ImageDecodeContext`.

**Wynik:** dotychczasowe Identify/Load/ReadMetadata przechodzą regresję; katalogowanie działa również dla
syntetycznego pliku z niedekodowalnym payloadem HEVC i dla dodatkowego nieobsługiwanego obrazu.

## 3. Katalog, klasyfikacja i bezpieczne otwieranie

- Zbudować indeks ID i niemutowalne opisy oraz indeks referencji odwrotnych. Unikać wielokrotnego skanowania
  wszystkich elementów przy budowaniu powiązań każdego zdjęcia.
- `TopLevelImages`: uwzględniać typ obrazu, `hidden`, rolę kafla/źródła `dimg`, miniatury i auxiliary.
  Nie klasyfikować metadanych lub dowolnego nieznanego FourCC jako zdjęcia. ISO `tmap` i powiązana gain mapa
  wymagają osobnej klasyfikacji, aby mapa nie stała się dodatkowym zdjęciem, a baza nie zniknęła z listy.
- Zachować współdzielone zależności, kolejność `dimg` i wiele relacji do różnych zdjęć.
- Zaimplementować wejścia path/stream/memory z kontrolą `MaxInputBytes` przed kopiowaniem i przy każdym
  dopełnianiu bufora. Uwzględnić strumień bez długości, deklarujący błędną długość i zwracający krótkie odczyty.
- Dodać `OpenAsync` dla path/stream z takim samym katalogiem, własnością bufora i limitami jak w `Open`.
  Sprawdzić anulowanie przed i podczas I/O oraz podczas parsowania; strumień użytkownika pozostaje otwarty
  również po błędzie i anulowaniu. Test strumienia obsługującego wyłącznie asynchroniczny odczyt powinien
  potwierdzać użycie `ReadAsync` zamiast synchronicznego odczytu w tle.
- Przechowywanie katalogu ograniczyć istniejącymi limitami liczby wpisów oraz rozmiarem wejścia; `MaxPixels`
  egzekwować dopiero dla dekodowanego obrazu i zależności.

**Wynik:** testy katalogu zdjęć, kafli, miniatur, alfy, Apple/ISO gain map, depth/disparity, nieznanych auxiliary
i metadanych. Test dwóch zdjęć ze wspólnym źródłem i różnymi blokami `cdsc`; brak mieszania metadanych.

## 4. Odczyt obrazu i podglądu

- Dodać `LoadImage` wykorzystujące własny bufor dokumentu oraz wspólną z podglądem ścieżkę wyboru elementu
  i przekazywania opcji do dekodera. Zweryfikować zgodność pikseli/metadanych z `Image.Load` dla tego samego ID,
  działanie po zamknięciu strumienia/usunięciu pliku oraz niezależność opcji kolejnych odczytów.
- `LoadPreview(Size, ...)` deleguje do wariantu z `PrimaryItemId`; wynik, limity i błędy obu wariantów są identyczne.
- Zweryfikować ID i dodatni rozmiar docelowy. API podglądu przyjmuje oba dodatnie boki; wynik mieści się
  w tym prostokącie, zachowuje proporcje i nie jest powiększany.
- Obliczyć potrzebny rozmiar po dopasowaniu proporcji zdjęcia. Odrzucić miniatury o innych proporcjach
  po zastosowaniu ich własnych transformacji. Tolerować zaokrąglenie krótszego boku do całych pikseli
  (różnica najwyżej 1 piksela względem proporcjonalnego pomniejszenia przy dłuższym boku miniatury);
  nie używać stałej tolerancji procentowej. Jeśli proporcji zdjęcia lub miniatury nie da się ustalić,
  nie wybierać tej miniatury automatycznie. Wśród pasujących miniatur wybrać najmniejszą
  powierzchnią, której wymiary po transformacjach pokrywają potrzebny rozmiar; przy remisie mniejsze ID.
- Dekodować przez istniejące publiczne `Image.Load` z `ItemId` i `TargetSize`, z zachowaniem limitów,
  konfiguracji, koloru, własnej alfy miniatury i anulowania; opcje pochodzą z danego wywołania odczytu.
- Nieobsługiwana miniatura (`NotSupportedException`) pozwala sprawdzić następną kandydatkę. Nie tłumić
  uszkodzeń, przekroczeń limitów ani anulowania. Bez obsługiwanej miniatury dekodować i skalować zdjęcie.
- Nie kopiować do wyniku metadanych ani orientacji zdjęcia nadrzędnego. Sprawdzić brak podwójnego obrotu
  z `ApplyExifOrientation` i zwalnianie obrazów na ścieżkach błędów.
- Opisać w XML docs i przewodniku, że osadzony podgląd może różnić się kadrem, kompresją i kolorem od
  pomniejszonego oryginału. Kontrola proporcji nie dowodzi identyczności kadru. Własny ICC i transformacje
  miniatury pozostają obsługiwane; nie wymagamy identycznych deklaracji z obrazem nadrzędnym.
  Użytkownik wymagający pomniejszenia właściwego zdjęcia wybiera `LoadImage` z `DecoderOptions.TargetSize`.

**Wynik:** testy pełnego odczytu, skrótu primary, niezależnych opcji dekodowania, kilku rozmiarów miniatur,
remisu, za małej i nieobsługiwanej miniatury, braku miniatur, skrajnych i niezgodnych proporcji,
zaokrągleń przy nieparzystych wymiarach, własnego ICC/alfa/transformacji oraz limitów.
Test wykazujący, że wybranie miniatury
nie dekoduje dużego zdjęcia (np. prawidłowa miniatura i niedekodowalny payload zdjęcia).

## 5. Generowanie miniatury i zapis `thmb`

- Wydzielić z `HeifImageEncoder.Encode` przygotowanie `HeifWriter.Content`, aby wykorzystać ten sam encoder
  dla zdjęcia i miniatury bez rekurencyjnego zapisywania kolejnych plików.
- Utworzyć pomniejszoną kopię obrazu przez publiczne API przetwarzania; nie modyfikować oryginału.
  Zachować proporcje, nie powiększać, obsłużyć nieparzyste wymiary i anulowanie.
- Miniatura używa jakości i ustawień alfy z opcji zapisu; zachowuje opis koloru zgodnie z polityką `Metadata`.
  EXIF/XMP zdjęcia nie trafiają do miniatury. Orientację utrwalić konsekwentnie w transformacjach miniatury
  lub w jej pikselach, nigdy w obu miejscach.
- Rozszerzyć `HeifWriter.Content` oraz writer o miniaturę, jej ewentualne kafle i alfę. Nadać niezależne ID,
  dodać `thmb` od miniatury do zdjęcia i `auxl` od jej alfy do miniatury. Zachować primary i offsety.
- Zweryfikować limity identyfikatorów, właściwości i obliczeń rozmiaru zapisu. Przy wyłączonej opcji dotychczasowe
  wyniki enkodera pozostają identyczne.

**Wynik:** round-trip miniatury przez katalog i podgląd; przypadki z alfą, ICC, orientacjami, siatką,
nieparzystymi wymiarami, zdjęciem mniejszym od limitu i `Metadata=None`; wynik niezależny od liczby wątków.

## 6. Odporność, wyrocznie i dokumentacja

- Rozbudować syntetyczne fixture'y; dodać obcięte i uszkodzone wejścia, mutacje katalogu/referencji,
  granice `MaxInputBytes`, `MaxPixels`, `MaxMetadataBytes`, anulowanie wejścia synchronicznego i asynchronicznego,
  parsera, odczytu obrazu, podglądu i zapisu. Sprawdzić rozdzielenie limitów otwarcia i poszczególnych odczytów.
- Sprawdzić odroczenie nieobsługiwanych funkcji bez połykania błędów struktury i bez niekontrolowanych wyjątków.
- Test z lokalnym libheif: wygenerowana miniatura jest rozpoznana jako miniatura właściwego zdjęcia;
  porównać jej odczyt, alfa i orientację. Narzędzia uruchamiać z `LD_LIBRARY_PATH=reference/tools/lib`
  ze względu na zapisaną starą ścieżkę w ich RUNPATH.
- Wyrocznie HEVC/HM obejmują również nowe kodowane elementy. Korzystać z istniejących narzędzi i korpusów;
  pobieranie dodatkowych źródeł lub zewnętrznych korpusów wymaga osobnej zgody zgodnie z `AGENTS.md`.
- Uzupełnić README, `docs/container.md`, `docs/decoding.md`, `docs/encoding.md`, XML docs i migawkę publicznego API.
  Przykłady: katalog zdjęć i relacji, `OpenAsync`, `PrimaryImage`, `LoadImage`, oba warianty podglądu,
  zapis miniatury. Sprawdzić je testem konsumenta paczki. Zastąpić przykłady wcześniejszego kontraktu
  `Open(..., DecoderOptions)` i testy zakładające zapamiętywanie opcji dekodowania przy otwieraniu.
- Zbudować Release bez ostrzeżeń, uruchomić pełne testy i Oracle, sprawdzić paczkę z LICENSE/notices.
  Pominięta wyrocznia pozostaje jawną luką w weryfikacji. Nie ma planowanych nowych SIMD ani wymogu dostępu
  do ARM64 dla tego zakresu; jeśli zakres się zmieni, zastosować odpowiednią bramkę z `AGENTS.md`.
- Uzupełniać `progres.md` po etapach; status w `plan.md` zmienić na ukończony lokalnie dopiero po spełnieniu DoD.

## Kolejność i bramki

| Etap | Zależy od | Warunek przejścia |
| --- | --- | --- |
| 1. Kontrakt | — | Spójna własność danych, relacje i zasady podglądu |
| 2. Parser | 1 | Regresja obecnego API, katalog bez dekodu HEVC |
| 3. Katalog | 2 | Klasyfikacja, relacje, Open/OpenAsync i limity pokryte testami |
| 4. Odczyt obrazu i podglądu | 3 | LoadImage, skrót primary, niezależne opcje, proporcje, fallback i orientacja przechodzą testy |
| 5. Zapis | 2–4 | Generowana miniatura odczytywana przez API dokumentu |
| 6. Integracja | 1–5 | Pełne testy, Oracle, dokumentacja, migawka i paczka |

Ryzyka do rozstrzygnięcia podczas wdrażania: globalna walidacja nieobsługiwanych elementów, naliczanie metadanych
wielu zdjęć, klasyfikacja bazy ISO `tmap` oraz wybór miniatur przy różnych proporcjach i orientacjach.
Każde ma przypisane testy powyżej; nie wymaga wcześniejszej zmiany zakresu. Jeśli publiczne API PictureSharp 0.7.6
okaże się niewystarczające, opisać konkretną lukę w planie rdzenia zamiast kopiować jego implementację.

## Wynik implementacji i weryfikacji (2026-10-18)

- Zrealizowano etapy 1–6. Katalog nie dekoduje HEVC ani nie rozpakowuje XMP; wejście jest własnością dokumentu.
- API zgodne z korektą: osobne opcje otwarcia, rzeczywiste I/O async, PrimaryImage, LoadImage i dwa podglądy,
  niezależne opcje/budżety odczytów, kontrola proporcji z tolerancją jednego piksela.
- Miniatura generowana po AutoOrient na pikselach, z własną alfą i kolorem, bez EXIF/XMP; obsługiwane siatki
  i nieparzyste wymiary. Nie zależy od orientacji zdjęcia nadrzędnego.
- Release: 0 ostrzeżeń, 456/457 testów zaliczonych (1 opcjonalny sweep Kodak pominięty); katalog/I/O 35/35.
  Oracle 6/6: libheif rozpoznaje relację i daje identyczne RGBA, HM potwierdza dane wszystkich kodowanych elementów.
- Paczka testowa 0.6.2-dev.3 zawiera LICENSE/notices/README/XML i zależy wyłącznie od PictureSharp 0.7.6.
  Przykłady przechodzą w zewnętrznym konsumencie oraz Native AOT x64. Linki dokumentacji i migawka API sprawdzone.
- Cztery syntetyczne porównania z kodem 0.6.1: zapis bez miniatury identyczny bajtowo.
  `VERSION` 0.6.2; GitHub Release i publikacja NuGet zakończone (workflow Release 39149646458).
