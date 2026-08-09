# TimeSuggestions

Prototyp automatycznych sugestii wpisów czasu pracy dla kancelarii prawnych.
Na podstawie spotkań z kalendarza Outlook i edytowanych dokumentów Word/Excel w OneDrive
(Microsoft Graph) aplikacja proponuje wpisy czasu, które użytkownik zatwierdza jednym
kliknięciem. Po zatwierdzeniu powstaje rozliczalny wpis (`TimeEntry`) przypisany do sprawy (`Case`).

## Architektura i przepływ danych

```
MSAL login → frontend pobiera Graph (kalendarz + pliki z okna synchronizacji, wszystkie strony)
           → filtr prywatności w przeglądarce (tytuły prywatnych nie opuszczają przeglądarki)
           → POST /api/sync (surowe dane + tombstone'y + liczniki filtrów klienckich)
           → backend: filtrowanie → dopasowanie do spraw → zapis sugestii (dedup)
             + rekonsyliacja kalendarza (aktualizacje w miejscu / usuwanie nieaktualnych)
           → raport synchronizacji + lista sugestii → karty w UI
           → Zatwierdź / Edytuj / Odrzuć → TimeEntry w bazie → widok "Wpisy czasu"
```

- **Frontend (Angular 21, `timesuggestions-web/`)**: logowanie MSAL (klient publiczny,
  bez sekretu), pobieranie surowych danych z Microsoft Graph, trzy widoki (Sugestie,
  Wpisy czasu, Sprawy), kafelki podsumowania, powiadomienia z akcją "Cofnij".
- **Backend (.NET 10 Web API, `TimeSuggestions/`)**: cała logika biznesowa
  (normalizacja, filtrowanie z licznikami, dopasowanie, agregacja, ochrona przed
  duplikatami), baza SQLite przez EF Core, endpointy REST.

**Dlaczego token nie idzie do backendu:** aplikacja jest klientem publicznym bez sekretu,
więc token Graph żyje wyłącznie w przeglądarce. Backend dostaje tylko surowe dane domenowe
(tytuły, daty, nazwy plików), dzięki czemu powierzchnia ataku jest mniejsza, a model
bezpieczeństwa prostszy. Logika w .NET jest przy tym czysto testowalna (xUnit, bez sieci
i logowania). Token nie jest też wysyłany pod żaden adres spoza `https://graph.microsoft.com`:
adresy stronicowania (`@odata.nextLink`, `@odata.deltaLink`, wartości z localStorage)
są walidowane przed dołączeniem nagłówka `Authorization`.

## Ograniczenia prototypu

- **API backendu nie ma uwierzytelniania.** To świadoma decyzja prototypu uruchamianego
  lokalnie: każdy proces z dostępem do portu 5188 może czytać i zapisywać dane.
  Backend nasłuchuje wyłącznie na `localhost` (profil `http` w `launchSettings.json`),
  więc nie jest wystawiony poza maszynę. W wersji produkcyjnej należałoby dodać
  walidację tokenu Entra dla własnego API oraz rozdzielenie danych per użytkownik;
  prototyp celowo tego nie implementuje.
- **Sugestie dokumentowe to nie pełna historia pracy.** Delta OneDrive zwraca ostatni
  zaobserwowany stan pliku: sugestia odpowiada ostatniej modyfikacji, a edycje z dni
  pomiędzy synchronizacjami (sprzed ostatniej zmiany pliku) nie są odtwarzane.
  Produkcyjnie rozwiązałby to endpoint `driveItem/versions`.
- **Jedna strefa biznesowa.** Czasy obu źródeł są sprowadzane do skonfigurowanej strefy
  (`Suggestions:BusinessTimeZoneId`, domyślnie `Europe/Warsaw`), bez obsługi wielu
  stref per użytkownik.

## Widoki aplikacji

| Widok | Rola |
|---|---|
| **Sugestie** | Karty propozycji z akcjami Zatwierdź / Edytuj / Odrzuć, filtr źródła i statusu, przycisk "Zatwierdź wszystkie dopasowane", raport synchronizacji (co pobrano, co odfiltrowano i dlaczego, co zaktualizowano), wskaźnik postępu, przywracanie odrzuconych, regulowany domyślny czas dokumentu i zakres synchronizacji (7/14/30 dni; szerszy przydaje się np. po urlopie) |
| **Wpisy czasu** | Zapisane wpisy pogrupowane po dniach z sumami i pochodzeniem (z jakiego spotkania/pliku powstały); usunięcie wpisu przywraca sugestię |
| **Sprawy** | Zarządzanie sprawami: dodawanie, edycja (w tym słów kluczowych sterujących dopasowaniem), dezaktywacja (celowo bez twardego usuwania); wyjaśnienie zasady dopasowania |

Nad zakładkami znajdują się kafelki podsumowania: oczekujące sugestie, zapisane wpisy, łączny czas,
ostatnia synchronizacja. W nagłówku przełącznik trzech motywów (jasny / niebieski / ciemny),
realizowanych wyłącznie tokenami CSS i zapamiętywanych lokalnie.

## Zapisane decyzje projektowe

| Decyzja | Uzasadnienie |
|---|---|
| **Delta query zamiast `/me/drive/recent`** | Endpoint „recent" jest oznaczony przez Microsoft jako wycofywany. `GET /me/drive/root/delta` jest wspierany i zwraca elementy dysku ze zmianami, w tym tombstone'y usuniętych plików (facet `deleted`), którymi backend czyści oczekujące sugestie. Filtrowanie (okno synchronizacji, rozszerzenia Word/Excel, autor modyfikacji) odbywa się po stronie klienta, bo delta nie wspiera `$filter`; odrzucenia klienckie są przy tym raportowane licznikami, żeby raport syncu pokazywał prawdę. Rozważone alternatywy: wyszukiwanie z sortowaniem po dacie (niestabilne wsparcie `$orderby`), endpointy aktywności (niedostępne dla kont osobistych). Szczegóły: `graph-files.service.ts`. |
| **Cache `deltaLink` w localStorage** | Pierwszy przebieg delta przechodzi cały dysk (na dużym OneDrive to dziesiątki sekund). Zapamiętany `deltaLink` sprawia, że kolejne synchronizacje pobierają wyłącznie zmiany. Link zapisywany dopiero po udanym zapisie w backendzie (obejmującym też tombstone'y); wygaśnięcie (HTTP 410) czyści cache i wymusza pełny przebieg. Zapisany adres jest walidowany przed użyciem: podmieniony wskaźnik nie wyśle tokenu pod obcy host. |
| **Strefa czasowa: `Prefer` + strefa biznesowa** | Kalendarz przychodzi w czasie lokalnym (nagłówek `Prefer: outlook.timezone`), dokumenty w UTC. Backend sprowadza oba źródła do wspólnej strefy biznesowej (`Suggestions:BusinessTimeZoneId`, ID IANA, domyślnie `Europe/Warsaw`): okno dokumentów walidowane na oryginalnym UTC, a `StartedAt`/`EntryDate` i agregacja per dzień liczone lokalnie; okno kalendarza liczone bezpośrednio w strefie biznesowej; „dzisiaj" w podsumowaniu również. Czas trwania spotkań liczony z różnicy instantów UTC, nie lokalnych `DateTime`, bo w noc zmiany czasu różnica lokalna kłamie o godzinę. Konwencje nocy zmiany czasu są jawne (`BusinessTime`): czas niejednoznaczny = pierwsze wystąpienie, czas nieistniejący = jakby zegar już przeskoczył (mapowanie monotoniczne, bez ujemnych trwań). |
| **Odporność na błędy Graph** | Wspólny helper obu serwisów Graph: ponowienia dla 429/502/503/504 z odczytem `Retry-After`, token pobierany per stronę, limit czasu żądania. Kalendarz i delta podążają za `@odata.nextLink` przez wszystkie strony. |
| **Filtr prywatności w przeglądarce** | Tytuły wydarzeń `private`/`confidential`/`personal` oraz anulowanych w ogóle nie opuszczają przeglądarki. Backend i tak powtarza swoje filtry (klientowi nie ufa), a liczniki filtrów klienckich są doliczane do raportu. |
| **Domyślny czas dokumentu jako parametr** | Graph mówi tylko *kiedy* plik zmieniono, nie *jak długo* trwała praca. Domyślne 30 min to parametr `Suggestions:DefaultDocumentDurationMinutes` w `appsettings.json`; użytkownik może poprawić wartość przed zatwierdzeniem. |
| **Dedup po `(źródło, id z Graph, dzień)`** | Indeks unikalny w bazie + scalanie z istniejącymi przy synchronizacji (duplikaty w obrębie jednego żądania też są scalane). Powtórny sync nie tworzy duplikatów, a **odrzucona sugestia nie wraca** (status zmieniany, rekord nieusuwany). |
| **Odświeżanie oczekujących przy syncu** | Zmiana nazwy pliku/tytułu spotkania nie zmienia ID w Graph, więc sam dedup zostawiałby stary tytuł. Sugestie **oczekujące** są nadpisywane wartościami ze źródła (z ponownym dopasowaniem); zatwierdzonych i odrzuconych sync nie dotyka. |
| **Rekonsyliacja kalendarza** | Backend rekonsyliuje kalendarz per spotkanie: przeniesione spotkanie aktualizuje istniejącą oczekującą sugestię w miejscu (bez „ducha" pod starą datą), odrzucenie jest „lepkie" per spotkanie (zmiana terminu nie przywraca sugestii), a oczekujące sugestie spotkań usuniętych lub już nierozliczalnych (anulowane/prywatne/całodniowe) znikają, a raport pokazuje je w liczniku „usunięte". Część destrukcyjna działa wyłącznie, gdy frontend zadeklaruje kompletny snapshot (`calendarSnapshotComplete`, czyli wszystkie strony pobrane bez błędu) wraz z zakresem dni (`calendarSnapshotDaysBack`), i tylko w przecięciu tego zakresu z oknem backendu; częściowe pobranie ani rozjazd konfiguracji okien nie skasują prawidłowych sugestii. Dokumentów to nie dotyczy: delta jest przyrostowa i nieobecność pliku w feedzie niczego nie dowodzi, więc czyszczą je wyłącznie jawne tombstone'y. |
| **Współbieżność rozstrzygana w bazie** | Indeksy unikalne (`TimeEntries.SuggestionId`, `Cases.CaseNumber`) domykają wyścigi: równoległe zatwierdzenie/duplikat numeru sprawy kończy się jawnym 409, a synchronizacja po konflikcie ponawia scalanie na czystym stanie kontekstu. Na 409 mapowane jest wyłącznie naruszenie unikalności (SQLite 2067), nie ogólne błędy constraintów. |
| **Dezaktywacja zamiast usuwania spraw** | Wpisy czasu wskazują na sprawy kluczem obcym, więc twarde usunięcie niszczyłoby dane rozliczeniowe. `IsActive=false` wyłącza sprawę z dopasowania i list wyboru, zachowując historię. |
| **Edycja = zatwierdzenie z poprawionymi wartościami** | Jeden endpoint `approve` przyjmuje wartości finalne: mniej ścieżek, ta sama walidacja. |
| **Raport z synchronizacji** | Backend zwraca liczniki: ile pobrano, ile odfiltrowano per reguła, ile zagregowano, jak dopasowano. Bez tego odfiltrowanie spotkań wygląda dla użytkownika jak zgubione dane. |

## Endpointy API

| Metoda i ścieżka | Opis |
|---|---|
| `POST /api/sync` | Przyjmuje surowe dane z Graph (+ opcjonalnie: tombstone'y usuniętych plików, liczniki filtrów klienckich, domyślny czas dokumentu, nadpisanie okna synchronizacji `syncDaysBack`, maks. 90 dni), zwraca pełny raport z faktycznie użytym oknem (`windowDays`); 409 przy kolizji z równoległą synchronizacją |
| `GET /api/suggestions?status=&source=` | Lista sugestii (domyślnie oczekujące) |
| `POST /api/suggestions/{id}/approve` | Tworzy wpis czasu, zamyka sugestię |
| `POST /api/suggestions/{id}/reject` | Odrzuca (status, bez usuwania) |
| `POST /api/suggestions/{id}/restore` | Przywraca odrzuconą do oczekujących |
| `GET /api/cases?includeInactive=` | Sprawy ze słowami kluczowymi (domyślnie tylko aktywne) |
| `POST /api/cases`, `PUT /api/cases/{id}` | Dodawanie i edycja spraw (unikalny numer sprawy) |
| `POST /api/cases/{id}/activate` / `deactivate` | Przełączanie aktywności (zamiast usuwania) |
| `GET /api/time-entries` | Wpisy pogrupowane po dniach z sumami |
| `DELETE /api/time-entries/{id}` | Usuwa wpis i przywraca sugestię |
| `GET /api/summary` | Liczniki do kafelków podsumowania |

Przykładowe wywołania wszystkich endpointów: `TimeSuggestions/TimeSuggestions/TimeSuggestions.http`.

## Uruchomienie

Wymagania: .NET SDK 10, Node 20+, Angular CLI.

**Backend** (profil `http`: nasłuchuje wyłącznie na `http://localhost:5188`;
baza SQLite tworzy się sama przy starcie; brak przekierowania na HTTPS, bo profil
nie ma endpointu https, a redirect psułby preflighty CORS):

```bash
cd TimeSuggestions/TimeSuggestions
dotnet run --launch-profile http
```

Migracje bazy wykonują się automatycznie przy starcie. Jeśli są oczekujące migracje,
backend najpierw tworzy obok bazy kopię `timesuggestions.db.bak-<migracja>`. Gdyby
migracja została przerwana (SQLite nie wykonuje przebudowy tabel atomowo), zatrzymaj
backend, zastąp `timesuggestions.db` plikiem kopii (usuń też pliki `-wal`/`-shm`,
jeśli istnieją) i uruchom ponownie.

**Frontend** (port 4200):

```bash
cd timesuggestions-web
npm install
npx ng serve
```

**Logowanie:** działa od razu po sklonowaniu, bo rejestracja aplikacji w Microsoft
Entra ID jest gotowa (multi-tenant + konta osobiste) i można logować się dowolnym
kontem Microsoft. Konfiguracja (client ID, authority, redirect URI) leży
w `timesuggestions-web/src/environments/environment.ts`; client ID to identyfikator
publiczny, nie sekret. Aplikacja działa jako klient publiczny (authorization code
+ PKCE, bez client secret) i prosi o delegowane uprawnienia **Calendars.Read**
i **Files.Read**. Ścieżka awaryjna: jeśli Twój tenant wymusza zgodę administratora
i nie możesz jej uzyskać, utwórz własną rejestrację (platforma **SPA**, redirect URI
`http://localhost:4200`, te same uprawnienia) i podmień `entraClientId`
w `environment.ts`.

**Testy** (175 testów backendu xUnit + 83 testy frontendu Vitest; bez sieci i logowania):

```bash
cd TimeSuggestions
dotnet test
```

```bash
cd timesuggestions-web
npm test -- --watch=false
```

## Struktura katalogów

```
TimeSuggestions/
  TimeSuggestions/          API .NET
    Configuration/          opcje (progi czasowe, okno syncu)
    Contracts/              DTO wejścia/wyjścia + walidacja + raport syncu
    Controllers/            cienkie kontrolery REST
    Data/                   DbContext + seed spraw testowych
    Migrations/             migracje EF Core (SQLite)
    Models/                 encje: Case, Suggestion, TimeEntry, SyncRun
    Services/               logika czysta (normalizacja, filtr z licznikami,
                            dopasowanie, budowa sugestii) + serwisy aplikacyjne
                            (sync, approval, summary)
  TimeSuggestions.Tests/    xUnit + fixtures JSON (TestData/)
timesuggestions-web/
  src/app/
    components/             suggestion-card
    pages/                  suggestions-page, time-entries-page, cases-page
    models/                 typy 1:1 z DTO backendu i Graph
    pipes/                  duration (minuty → "1 godz. 30 min")
    services/               auth (MSAL), graph-http (walidacja URL, retry, timeout),
                            graph-calendar, graph-files (delta+cache+tombstone'y),
                            api, summary-store, toast, data-refresh, user-message
  src/styles.css            system wizualny: tokeny, komponenty, ciemny motyw
```

## Reguły biznesowe (skrót)

- Z kalendarza odpadają: wydarzenia prywatne/poufne/osobiste, anulowane, krótsze niż
  skonfigurowany próg (domyślnie 5 min; dokładnie próg przechodzi), całodniowe, poza
  oknem synchronizacji oraz z nieprawidłowymi datami (koniec przed początkiem);
  raport syncu pokazuje, ile i dlaczego (łącznie z filtrami wykonanymi w przeglądarce).
- Okno synchronizacji obejmuje domyślnie ostatnie 7 dni; użytkownik może je poszerzyć
  w UI do 14 lub 30 dni (np. po urlopie), a backend przyjmuje nadpisanie do 90 dni;
  wartości spoza zakresu odrzuca walidacja.
- Z dysku wchodzą tylko pliki Word/Excel zmodyfikowane przez zalogowanego użytkownika
  w oknie synchronizacji; kilka edycji tego samego pliku jednego dnia (w strefie
  biznesowej) = jedna sugestia. Sugestia dokumentowa odzwierciedla ostatnią zaobserwowaną modyfikację
  pliku, nie pełną historię dni pracy (patrz „Ograniczenia prototypu").
- Dopasowanie do sprawy po **pełnych tokenach** znormalizowanego tekstu: termin
  jednowyrazowy musi być identycznym słowem („Alfa" nie pasuje do „Alfabet"),
  a termin wielowyrazowy ciągiem kolejnych słów; separatorem jest każdy znak niebędący
  literą ani cyfrą, więc interpunkcja przy nazwie („Kowalski, przegląd") nie psuje
  dopasowania, a numery spraw działają bez zmian. Gdy trafienie jednej sprawy w całości
  zawiera się w dłuższym trafieniu innej, wygrywa dłuższe („Audyt Beta Logistics" → klient
  „Beta Logistics", nie keyword „Beta"); trafienia rozłączne lub tylko zahaczające
  o siebie wspólnym słowem to osobne dowody i pozostają niejednoznaczne (spotkanie
  międzysprawowe). Świadomy kompromis: odmiany fleksyjne („Kowalskiego") nie są
  rozpoznawane; można je dodać jako słowa kluczowe sprawy. Trzy stany: jedno trafienie
  (sprawa przypisana), brak (karta „sprawdź to" z wyjaśnieniem), wiele (niejednoznaczna,
  UI wymienia pasujące sprawy).
- Zatwierdzenie wymaga wybranej sprawy i czasu 1–1440 min; tworzy `TimeEntry` ze źródłem
  pochodzenia i referencją do sugestii (dokładnie jeden wpis na sugestię, co gwarantuje
  indeks unikalny). Każda decyzja jest odwracalna (Cofnij / Przywróć / Usuń), a „Cofnij" działa
  także po przejściu na inną zakładkę.

### Przykłady dopasowania

Sprawa jest dopasowywana wyłącznie po trzech terminach: **nazwie klienta**, **numerze
sprawy** i **słowach kluczowych**. Sama nazwa sprawy (np. „Fuzja Alfa/Beta") jest tylko
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
- ⚠️ kilka pasujących spraw: sugestia trafia na kartę „sprawdź to", a aplikacja
  wymienia kandydatów do wyboru;
- ❌ żadna sprawa nie pasuje: sugestia trafia na kartę „sprawdź to" i sprawę
  wybiera się ręcznie.

#### Podstawy: klient, numer, słowo kluczowe

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `spotkanie z KOWALSKI` | ✅ Kowalski | nazwa klienta w tytule; wielkość liter nie ma znaczenia |
| `Prezentacja Alfa` | ✅ Alfa Holding | tytuł zawiera „Alfa", słowo kluczowe sprawy klienta Alfa Holding |
| `Spotkanie Alfa Holding` | ✅ Alfa Holding | dwuwyrazowa nazwa klienta pasuje, gdy oba słowa stoją obok siebie w tej kolejności |
| `rozmowa grzegrzolka` | ✅ Grzegrzółka | tytuł bez polskich znaków pasuje do klienta z polskimi znakami, bo litery takie jak ó i ł są sprowadzane do o i l po obu stronach porównania |
| `Rozmowa Grzegrzółka, pilna` | ✅ Grzegrzółka | polskie znaki w tytule i przecinek za nazwą; ani jedno, ani drugie nie przeszkadza |

#### Numery spraw

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `Analiza NT-2026-113` | ✅ NovaTech | pełny numer sprawy w tytule |
| `Omówienie NT 2026 113` | ✅ NovaTech | numer zapisany spacjami zamiast myślników; po normalizacji obie formy wyglądają identycznie |
| `NT.2026.113 przegląd` | ✅ NovaTech | numer zapisany kropkami także pasuje |
| `omówienie k-2026-001` | ✅ Kowalski | wielkość liter nie ma znaczenia również w numerze sprawy |
| `Przygotowanie do NT-2026` | ❌ brak | niepełny numer nie wystarcza; dopasowanie wymaga wszystkich trzech części numeru (NT, 2026 i 113) |

#### Interpunkcja i znaki specjalne w tytułach

Znaki inne niż litery i cyfry działają jak odstępy, więc nazwa klienta jest
rozpoznawana nawet „przyklejona" do interpunkcji:

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

#### Nazwy plików

| Nazwa pliku | Wynik | Wyjaśnienie |
|---|---|---|
| `Umowa_NovaTech_v2.docx` | ✅ NovaTech | podkreślniki dzielą nazwę na słowa; rozszerzenie `.docx` i oznaczenie wersji `v2` są pomijane |
| `Umowa (2)_NovaTech.docx` | ✅ NovaTech | pomijany jest też numer kopii `(2)`, który OneDrive dokleja przy powielaniu pliku |
| `Umowa_NovaTech (3) v2.docx` | ✅ NovaTech | numer kopii i oznaczenie wersji naraz |
| `alfa-holding_raport.docx` | ✅ Alfa Holding | dwuwyrazowa nazwa klienta rozcięta myślnikiem i podkreślnikiem to nadal dwa sąsiednie słowa |
| `raport-Kowalski-final.docx` | ✅ Kowalski | słowo „final" zostaje w nazwie i niczego nie psuje |
| `KOPIA umowy grzegrzolka.xlsx` | ✅ Grzegrzółka | „kopia" to zwykłe słowo; nie jest wycinane i nie przeszkadza |
| `NOTATKI_GRZEGRZOLKA_V3.XLSX` | ✅ Grzegrzółka | wielkie litery w całej nazwie, łącznie z rozszerzeniem i oznaczeniem wersji |
| `pozew!Kowalski!.docx` | ✅ Kowalski | interpunkcja w nazwie pliku działa jak odstęp |
| `faktura_2026.pdf` | ❌ brak | nazwa nie zawiera żadnego terminu sprawy; niezależnie od tego pliki PDF w ogóle nie przechodzą filtra typów (tylko Word/Excel) |

#### Kiedy jedna sprawa wygrywa z drugą

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `Audyt Beta Logistics` | ✅ Beta Logistics | tytuł zawiera pełną nazwę klienta „Beta Logistics"; słowo „Beta" (kluczowe dla sprawy Alfa Holding) jest tu tylko fragmentem tej dłuższej nazwy, więc tamta sprawa odpada |
| `Beta Logistics, przegląd roczny` | ✅ Beta Logistics | jak wyżej: pełna nazwa klienta wygrywa z pojedynczym słowem kluczowym |
| `NT-2026-113 status wdrożenia NovaTech` | ✅ NovaTech | numer sprawy i nazwa klienta wskazują tę samą sprawę, więc wynik pozostaje jednoznaczny |

#### Kilka pasujących spraw: wybór należy do użytkownika

| Tytuł spotkania | Wynik | Wyjaśnienie |
|---|---|---|
| `Analiza Beta` | ⚠️ 2 sprawy | „Beta" jest słowem kluczowym dwóch spraw (Alfa Holding i Beta Logistics); żadne trafienie nie jest lepsze od drugiego |
| `Logistics Beta, przegląd` | ⚠️ 2 sprawy | kolejność słów ma znaczenie: „Logistics Beta" to nie nazwa klienta „Beta Logistics", zostaje więc samo słowo „Beta", wspólne dla dwóch spraw |
| `Alfa i Beta, harmonogram fuzji` | ⚠️ 2 sprawy | „Alfa" wskazuje sprawę Alfa Holding, ale „Beta" wskazuje obie sprawy, więc niejednoznaczność zostaje |
| `Omówienie NT-2026-113 z Kowalski` | ⚠️ 2 sprawy | numer jednej sprawy i klient drugiej stoją w różnych miejscach tytułu; to dwa niezależne ślady, więc aplikacja nie zgaduje, której sprawy dotyczył czas |
| `Spór Kowalski vs NovaTech` | ⚠️ 2 sprawy | nazwy dwóch klientów w jednym tytule |
| `Kowalski/NovaTech harmonogram` | ⚠️ 2 sprawy | ukośnik rozdziela dwie nazwy klientów; w tytule nadal są dwie sprawy |

#### Brak dopasowania: sprawę wskazuje się ręcznie

| Tytuł spotkania / nazwa pliku | Wynik | Wyjaśnienie |
|---|---|---|
| `Analiza Alfabet` | ❌ brak | porównywane są całe słowa: „Alfa" nie pasuje do fragmentu dłuższego wyrazu „Alfabet", co chroni przed przypisaniem czasu do złej sprawy |
| `Betamax test` | ❌ brak | jak wyżej: „Beta" to nie „Betamax" |
| `Rozmowa z Kowalskim` | ❌ brak | „Kowalskim" to odmieniona forma, czyli inne słowo niż „Kowalski"; aplikacja nie zna polskiej odmiany, a obejściem jest dodanie „Kowalskim" do słów kluczowych sprawy |
| `Notatka_Kowalskiego.docx` | ❌ brak | jak wyżej, tym razem w nazwie pliku |
| `Spotkanie Fuzja` | ❌ brak | słowo „Fuzja" występuje wyłącznie w nazwie sprawy („Fuzja Alfa/Beta"), a dopasowanie nie zagląda do nazwy sprawy; sprawdza tylko klienta, numer i słowa kluczowe |
| `Cotygodniowy standup` | ❌ brak | tytuł nie zawiera żadnego terminu żadnej sprawy |

## Co zrobiłbym inaczej, mając więcej czasu

Projekt jest świadomie przygotowany jako lokalna aplikacja portfolio (patrz
„Ograniczenia prototypu"). Przed udostępnieniem go jako publicznej usługi
rozbudowałbym go w następującej kolejności:

- **Uwierzytelnienie API i izolacja danych użytkowników**: frontend pobierałby osobny
  token dla backendu, niezależny od tokenu Microsoft Graph. Backend weryfikowałby podpis,
  `issuer`, `audience` i wymagany scope, a dane byłyby przypisywane i filtrowane według
  `TenantId` oraz `UserObjectId`. Token Graph nadal nigdy nie trafiałby do backendu.
- **Produkcyjna baza danych**: SQLite pozostałby do pracy lokalnej, natomiast wdrożenie
  korzystałoby z PostgreSQL lub SQL Server, z kontrolowanymi migracjami, kopiami
  zapasowymi, retencją i procedurą odtwarzania danych.
- **Bezpieczne wdrożenie**: osobne konfiguracje Development/Test/Production, HTTPS,
  ograniczony CORS, bezpieczne przechowywanie connection stringów i sekretów oraz
  poprawne adresy API i redirect URI. Publiczny `clientId` Entra nadal mógłby pozostać
  w repozytorium, ponieważ nie jest sekretem.
- **Paginacja i ochrona API**: paginacja sugestii oraz wpisów czasu, a także rate
  limiting skonfigurowany per użytkownik, szczególnie dla kosztownych operacji
  synchronizacji.
- **Monitoring i obsługa błędów**: centralne `ProblemDetails`, strukturalne logowanie,
  identyfikatory żądań, metryki, health checks i alerty. Logi nie mogłyby zawierać
  tokenów, nagłówków autoryzacji, tytułów spotkań ani nazw poufnych dokumentów.
- **Automatyczna weryfikacja zmian**: CI/CD uruchamiające build backendu i frontendu,
  testy jednostkowe i integracyjne, test migracji na tymczasowej bazie oraz audyt
  zależności.
- **Dokładniejsza historia dokumentów**: obecna synchronizacja delta pokazuje ostatni
  zaobserwowany stan pliku, a nie wszystkie jego wcześniejsze modyfikacje. Pełniejsze
  odtwarzanie dni pracy wymagałoby użycia historii wersji `DriveItem`, dodatkowej
  deduplikacji, obsługi stronicowania, limitów Graph i zapamiętywania przetworzonych
  wersji.
