# Dopasowanie sugestii do spraw: przykłady

Sprawa jest dopasowywana wyłącznie po trzech terminach: **nazwie klienta**, **numerze
sprawy** i **słowach kluczowych**. Sama nazwa sprawy (np. "Fuzja Alfa/Beta") jest tylko
etykietą wyświetlaną na listach i nie bierze udziału w dopasowaniu. Terminy spraw
z seedu bazy:

| Klient | Numer sprawy | Słowa kluczowe |
|---|---|---|
| Kowalski | K-2026-001 | (brak) |
| NovaTech | NT-2026-113 | (brak) |
| Grzegrzółka | GZ-2026-007 | (brak) |
| Alfa Holding | AB-2026-021 | Alfa; Beta |
| Beta Logistics | BL-2026-030 | Beta |

Wyniki poniżej pochodzą z uruchomienia logiki dopasowania na powyższych sprawach.
Znaczenie symboli:

- ✅ dokładnie jedna pasująca sprawa: sugestia dostaje ją automatycznie;
- ⚠️ kilka pasujących spraw: sugestia trafia na kartę "sprawdź to", a aplikacja
  wymienia kandydatów do wyboru;
- ❌ żadna sprawa nie pasuje: sugestia trafia na kartę "sprawdź to" i sprawę
  wybiera się ręcznie.

## Podstawy: klient, numer, słowo kluczowe

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `spotkanie z KOWALSKI` | ✅ Kowalski | nazwa klienta w tytule; wielkość liter nie ma znaczenia |
| `Prezentacja Alfa` | ✅ Alfa Holding | tytuł zawiera "Alfa", słowo kluczowe sprawy klienta Alfa Holding |
| `Spotkanie Alfa Holding` | ✅ Alfa Holding | dwuwyrazowa nazwa klienta pasuje, gdy oba słowa stoją obok siebie w tej kolejności |
| `rozmowa grzegrzolka` | ✅ Grzegrzółka | tytuł bez polskich znaków pasuje do klienta z polskimi znakami, bo litery takie jak ó i ł są sprowadzane do o i l po obu stronach porównania |
| `Rozmowa Grzegrzółka, pilna` | ✅ Grzegrzółka | polskie znaki w tytule i przecinek za nazwą; ani jedno, ani drugie nie przeszkadza |

## Numery spraw

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `Analiza NT-2026-113` | ✅ NovaTech | pełny numer sprawy w tytule |
| `Omówienie NT 2026 113` | ✅ NovaTech | numer zapisany spacjami zamiast myślników; po normalizacji obie formy wyglądają identycznie |
| `NT.2026.113 przegląd` | ✅ NovaTech | numer zapisany kropkami także pasuje |
| `omówienie k-2026-001` | ✅ Kowalski | wielkość liter nie ma znaczenia również w numerze sprawy |
| `Przygotowanie do NT-2026` | ❌ brak | niepełny numer nie wystarcza; dopasowanie wymaga wszystkich trzech części numeru (NT, 2026 i 113) |

## Interpunkcja i znaki specjalne w tytułach

Znaki inne niż litery i cyfry działają jak odstępy, więc nazwa klienta jest
rozpoznawana nawet "przyklejona" do interpunkcji:

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `Kowalski, przegląd umowy` | ✅ Kowalski | przecinek tuż za nazwą nie zmienia jej w inne słowo |
| `Kowalski: omówienie pozwu` | ✅ Kowalski | to samo z dwukropkiem |
| `Pilne! Kowalski?` | ✅ Kowalski | to samo z wykrzyknikiem i pytajnikiem |
| `(Kowalski) negocjacje` | ✅ Kowalski | nawiasy wokół nazwy nie przeszkadzają |
| `[NovaTech] status wdrożenia` | ✅ NovaTech | nawiasy kwadratowe także |
| `Spotkanie „Kowalski"` | ✅ Kowalski | cudzysłowy (drukarskie i proste) nie przeszkadzają |
| `Kowalski—przegląd` | ✅ Kowalski | długi myślnik wklejony bez spacji również oddziela słowa |
| `Spotkanie 🚀 „Kowalski" i l'affaire` | ✅ Kowalski | emoji i apostrof też działają jak odstępy |

## Nazwy plików

| Nazwa pliku | Wynik | Wyjaśnienie |
|---|---|---|
| `Umowa_NovaTech_v2.docx` | ✅ NovaTech | podkreślniki dzielą nazwę na słowa; rozszerzenie `.docx` i oznaczenie wersji `v2` są pomijane |
| `Umowa (2)_NovaTech.docx` | ✅ NovaTech | pomijany jest też numer kopii `(2)`, który OneDrive dokleja przy powielaniu pliku |
| `Umowa_NovaTech (3) v2.docx` | ✅ NovaTech | numer kopii i oznaczenie wersji naraz |
| `alfa-holding_raport.docx` | ✅ Alfa Holding | dwuwyrazowa nazwa klienta rozcięta myślnikiem i podkreślnikiem to nadal dwa sąsiednie słowa |
| `raport-Kowalski-final.docx` | ✅ Kowalski | słowo "final" zostaje w nazwie i niczego nie psuje |
| `KOPIA umowy grzegrzolka.xlsx` | ✅ Grzegrzółka | "kopia" to zwykłe słowo; nie jest wycinane i nie przeszkadza |
| `NOTATKI_GRZEGRZOLKA_V3.XLSX` | ✅ Grzegrzółka | wielkie litery w całej nazwie, łącznie z rozszerzeniem i oznaczeniem wersji |
| `pozew!Kowalski!.docx` | ✅ Kowalski | interpunkcja w nazwie pliku działa jak odstęp |
| `faktura_2026.pdf` | ❌ brak | nazwa nie zawiera żadnego terminu sprawy; niezależnie od tego pliki PDF w ogóle nie przechodzą filtra typów (tylko Word/Excel) |

## Kiedy jedna sprawa wygrywa z drugą

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `Audyt Beta Logistics` | ✅ Beta Logistics | tytuł zawiera pełną nazwę klienta "Beta Logistics"; słowo "Beta" (kluczowe dla sprawy Alfa Holding) jest tu tylko fragmentem tej dłuższej nazwy, więc tamta sprawa odpada |
| `Beta Logistics, przegląd roczny` | ✅ Beta Logistics | jak wyżej: pełna nazwa klienta wygrywa z pojedynczym słowem kluczowym |
| `NT-2026-113 status wdrożenia NovaTech` | ✅ NovaTech | numer sprawy i nazwa klienta wskazują tę samą sprawę, więc wynik pozostaje jednoznaczny |

## Kilka pasujących spraw: wybór należy do użytkownika

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `Analiza Beta` | ⚠️ 2 sprawy | "Beta" jest słowem kluczowym dwóch spraw (Alfa Holding i Beta Logistics); żadne trafienie nie jest lepsze od drugiego |
| `Logistics Beta, przegląd` | ⚠️ 2 sprawy | kolejność słów ma znaczenie: "Logistics Beta" to nie nazwa klienta "Beta Logistics", zostaje więc samo słowo "Beta", wspólne dla dwóch spraw |
| `Alfa i Beta, harmonogram fuzji` | ⚠️ 2 sprawy | "Alfa" wskazuje sprawę Alfa Holding, ale "Beta" wskazuje obie sprawy, więc niejednoznaczność zostaje |
| `Omówienie NT-2026-113 z Kowalski` | ⚠️ 2 sprawy | numer jednej sprawy i klient drugiej stoją w różnych miejscach tytułu; to dwa niezależne ślady, więc aplikacja nie zgaduje, której sprawy dotyczył czas |
| `Spór Kowalski vs NovaTech` | ⚠️ 2 sprawy | nazwy dwóch klientów w jednym tytule |
| `Kowalski/NovaTech harmonogram` | ⚠️ 2 sprawy | ukośnik rozdziela dwie nazwy klientów; w tytule nadal są dwie sprawy |

## Brak dopasowania: sprawę wskazuje się ręcznie

| Tytuł spotkania / nazwa pliku | Wynik | Wyjaśnienie |
|---|---|---|
| `Analiza Alfabet` | ❌ brak | porównywane są całe słowa: "Alfa" nie pasuje do fragmentu dłuższego wyrazu "Alfabet", co chroni przed przypisaniem czasu do złej sprawy |
| `Betamax test` | ❌ brak | jak wyżej: "Beta" to nie "Betamax" |
| `Rozmowa z Kowalskim` | ❌ brak | "Kowalskim" to odmieniona forma, czyli inne słowo niż "Kowalski"; aplikacja nie zna polskiej odmiany, a obejściem jest dodanie "Kowalskim" do słów kluczowych sprawy |
| `Notatka_Kowalskiego.docx` | ❌ brak | jak wyżej, tym razem w nazwie pliku |
| `Spotkanie Fuzja` | ❌ brak | słowo "Fuzja" występuje wyłącznie w nazwie sprawy ("Fuzja Alfa/Beta"), a dopasowanie nie zagląda do nazwy sprawy; sprawdza tylko klienta, numer i słowa kluczowe |
| `Cotygodniowy standup` | ❌ brak | tytuł nie zawiera żadnego terminu żadnej sprawy |
