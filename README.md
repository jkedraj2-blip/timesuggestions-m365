# TimeSuggestions

Prototyp automatycznych sugestii wpisów czasu pracy dla kancelarii prawnych.
Na podstawie spotkań z kalendarza Outlook i edytowanych dokumentów Word/Excel w OneDrive
(Microsoft Graph) aplikacja proponuje wpisy czasu, które użytkownik zatwierdza jednym
kliknięciem — powstaje wtedy rozliczalny wpis (`TimeEntry`) przypisany do sprawy (`Case`).

## Architektura i przepływ danych

```
MSAL login → frontend pobiera Graph (7 dni: kalendarz + pliki, wszystkie strony)
           → filtr prywatności w przeglądarce (tytuły prywatnych nie opuszczają przeglądarki)
           → POST /api/sync (surowe dane + tombstone'y + liczniki filtrów klienckich)
           → backend: filtrowanie → dopasowanie do spraw → zapis sugestii (dedup)
             + rekonsyliacja kalendarza (aktualizacje w miejscu / usuwanie nieaktualnych)
           → raport synchronizacji + lista sugestii → karty w UI
           → Zatwierdź / Edytuj / Odrzuć → TimeEntry w bazie → widok "Wpisy czasu"
```

- **Frontend (Angular 21, `timesuggestions-web/`)** — logowanie MSAL (klient publiczny,
  bez sekretu), pobieranie surowych danych z Microsoft Graph, trzy widoki (Sugestie,
  Wpisy czasu, Sprawy), kafelki podsumowania, powiadomienia z akcją "Cofnij".
- **Backend (.NET 10 Web API, `TimeSuggestions/`)** — cała logika biznesowa
  (normalizacja, filtrowanie z licznikami, dopasowanie, agregacja, ochrona przed
  duplikatami), baza SQLite przez EF Core, endpointy REST.

**Dlaczego token nie idzie do backendu:** aplikacja jest klientem publicznym bez sekretu,
więc token Graph żyje wyłącznie w przeglądarce. Backend dostaje tylko surowe dane domenowe
(tytuły, daty, nazwy plików) — mniejsza powierzchnia ataku i prostszy model bezpieczeństwa.
Logika w .NET jest przy tym czysto testowalna (xUnit, bez sieci i logowania).
Token nie jest też wysyłany pod żaden adres spoza `https://graph.microsoft.com` —
adresy stronicowania (`@odata.nextLink`, `@odata.deltaLink`, wartości z localStorage)
są walidowane przed dołączeniem nagłówka `Authorization`.

## Ograniczenia prototypu

- **API backendu nie ma uwierzytelniania.** To świadoma decyzja prototypu uruchamianego
  lokalnie: każdy proces z dostępem do portu 5188 może czytać i zapisywać dane.
  Backend nasłuchuje wyłącznie na `localhost` (profil `http` w `launchSettings.json`),
  więc nie jest wystawiony poza maszynę. W wersji produkcyjnej należałoby dodać
  walidację tokenu Entra dla własnego API oraz rozdzielenie danych per użytkownik —
  prototyp celowo tego nie implementuje.
- **Sugestie dokumentowe to nie pełna historia pracy.** Delta OneDrive zwraca ostatni
  zaobserwowany stan pliku — sugestia odpowiada ostatniej modyfikacji, a edycje z dni
  pomiędzy synchronizacjami (sprzed ostatniej zmiany pliku) nie są odtwarzane.
  Produkcyjnie rozwiązałby to endpoint `driveItem/versions`.
- **Jedna strefa biznesowa.** Czasy obu źródeł są sprowadzane do skonfigurowanej strefy
  (`Suggestions:BusinessTimeZoneId`, domyślnie `Europe/Warsaw`) — bez obsługi wielu
  stref per użytkownik.

## Widoki aplikacji

| Widok | Rola |
|---|---|
| **Sugestie** | Karty propozycji z akcjami Zatwierdź / Edytuj / Odrzuć, filtr źródła i statusu, przycisk "Zatwierdź wszystkie dopasowane", raport synchronizacji (co pobrano, co odfiltrowano i dlaczego, co zaktualizowano), wskaźnik postępu, przywracanie odrzuconych, regulowany domyślny czas dokumentu |
| **Wpisy czasu** | Zapisane wpisy pogrupowane po dniach z sumami i pochodzeniem (z jakiego spotkania/pliku powstały); usunięcie wpisu przywraca sugestię |
| **Sprawy** | Zarządzanie sprawami: dodawanie, edycja (w tym słów kluczowych sterujących dopasowaniem), dezaktywacja — celowo bez twardego usuwania; wyjaśnienie zasady dopasowania |

Nad zakładkami kafelki podsumowania: oczekujące sugestie, zapisane wpisy, łączny czas,
ostatnia synchronizacja. W nagłówku przełącznik trzech motywów (jasny / niebieski / ciemny),
realizowanych wyłącznie tokenami CSS i zapamiętywanych lokalnie.

## Zapisane decyzje projektowe

| Decyzja | Uzasadnienie |
|---|---|
| **Delta query zamiast `/me/drive/recent`** | Endpoint „recent" jest oznaczony przez Microsoft jako wycofywany. `GET /me/drive/root/delta` jest wspierany i zwraca elementy dysku ze zmianami — w tym tombstone'y usuniętych plików (facet `deleted`), którymi backend czyści oczekujące sugestie. Filtrowanie (okno 7 dni, rozszerzenia Word/Excel, autor modyfikacji) odbywa się po stronie klienta, bo delta nie wspiera `$filter` — a odrzucenia klienckie są raportowane licznikami, żeby raport syncu pokazywał prawdę. Rozważone alternatywy: wyszukiwanie z sortowaniem po dacie (niestabilne wsparcie `$orderby`), endpointy aktywności (niedostępne dla kont osobistych). Szczegóły: `graph-files.service.ts`. |
| **Cache `deltaLink` w localStorage** | Pierwszy przebieg delta przechodzi cały dysk (na dużym OneDrive to dziesiątki sekund). Zapamiętany `deltaLink` sprawia, że kolejne synchronizacje pobierają wyłącznie zmiany. Link zapisywany dopiero po udanym zapisie w backendzie (obejmującym też tombstone'y); wygaśnięcie (HTTP 410) czyści cache i wymusza pełny przebieg. Zapisany adres jest walidowany przed użyciem — podmieniony wskaźnik nie wyśle tokenu pod obcy host. |
| **Strefa czasowa: `Prefer` + strefa biznesowa** | Kalendarz przychodzi w czasie lokalnym (nagłówek `Prefer: outlook.timezone`), dokumenty w UTC. Backend sprowadza oba źródła do wspólnej strefy biznesowej (`Suggestions:BusinessTimeZoneId`, ID IANA, domyślnie `Europe/Warsaw`): okno dokumentów walidowane na oryginalnym UTC, a `StartedAt`/`EntryDate` i agregacja per dzień liczone lokalnie; okno kalendarza liczone bezpośrednio w strefie biznesowej; „dzisiaj" w podsumowaniu również. |
| **Odporność na błędy Graph** | Wspólny helper obu serwisów Graph: ponowienia dla 429/502/503/504 z odczytem `Retry-After`, token pobierany per stronę, limit czasu żądania. Kalendarz i delta podążają za `@odata.nextLink` przez wszystkie strony. |
| **Filtr prywatności w przeglądarce** | Tytuły wydarzeń `private`/`confidential`/`personal` oraz anulowanych w ogóle nie opuszczają przeglądarki. Backend i tak powtarza swoje filtry (klientowi nie ufa), a liczniki filtrów klienckich są doliczane do raportu. |
| **Domyślny czas dokumentu jako parametr** | Graph mówi tylko *kiedy* plik zmieniono, nie *jak długo* trwała praca. Domyślne 30 min to parametr `Suggestions:DefaultDocumentDurationMinutes` w `appsettings.json`; użytkownik może poprawić wartość przed zatwierdzeniem. |
| **Dedup po `(źródło, id z Graph, dzień)`** | Indeks unikalny w bazie + scalanie z istniejącymi przy synchronizacji (duplikaty w obrębie jednego żądania też są scalane). Powtórny sync nie tworzy duplikatów, a **odrzucona sugestia nie wraca** (status zmieniany, rekord nieusuwany). |
| **Odświeżanie oczekujących przy syncu** | Zmiana nazwy pliku/tytułu spotkania nie zmienia ID w Graph, więc sam dedup zostawiałby stary tytuł. Sugestie **oczekujące** są nadpisywane wartościami ze źródła (z ponownym dopasowaniem); zatwierdzonych i odrzuconych sync nie dotyka. |
| **Rekonsyliacja kalendarza** | Kalendarz to pełny snapshot okna, więc backend rekonsyliuje go per spotkanie: przeniesione spotkanie aktualizuje istniejącą oczekującą sugestię w miejscu (bez „ducha" pod starą datą), odrzucenie jest „lepkie" per spotkanie (zmiana terminu nie przywraca sugestii), a oczekujące sugestie spotkań usuniętych lub już nierozliczalnych (anulowane/prywatne/całodniowe) znikają — raport pokazuje je w liczniku „usunięte". Dokumentów to nie dotyczy: delta jest przyrostowa i nieobecność pliku w feedzie niczego nie dowodzi — czyszczą je wyłącznie jawne tombstone'y. |
| **Współbieżność rozstrzygana w bazie** | Indeksy unikalne (`TimeEntries.SuggestionId`, `Cases.CaseNumber`) domykają wyścigi: równoległe zatwierdzenie/duplikat numeru sprawy kończy się jawnym 409, a synchronizacja po konflikcie ponawia scalanie na czystym stanie kontekstu. Na 409 mapowane jest wyłącznie naruszenie unikalności (SQLite 2067), nie ogólne błędy constraintów. |
| **Dezaktywacja zamiast usuwania spraw** | Wpisy czasu wskazują na sprawy kluczem obcym — twarde usunięcie niszczyłoby dane rozliczeniowe. `IsActive=false` wyłącza sprawę z dopasowania i list wyboru, zachowując historię. |
| **Edycja = zatwierdzenie z poprawionymi wartościami** | Jeden endpoint `approve` przyjmuje wartości finalne — mniej ścieżek, ta sama walidacja. |
| **Raport z synchronizacji** | Backend zwraca liczniki: ile pobrano, ile odfiltrowano per reguła, ile zagregowano, jak dopasowano. Bez tego odfiltrowanie spotkań wygląda dla użytkownika jak zgubione dane. |

## Endpointy API

| Metoda i ścieżka | Opis |
|---|---|
| `POST /api/sync` | Przyjmuje surowe dane z Graph (+ opcjonalnie: tombstone'y usuniętych plików, liczniki filtrów klienckich, domyślny czas dokumentu), zwraca pełny raport; 409 przy kolizji z równoległą synchronizacją |
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

Wszystkie wywołania przykładowe: `TimeSuggestions/TimeSuggestions/TimeSuggestions.http`.

## Uruchomienie

Wymagania: .NET SDK 10, Node 20+, Angular CLI.

**Backend** (profil `http`: nasłuchuje wyłącznie na `http://localhost:5188`;
baza SQLite tworzy się sama przy starcie; brak przekierowania na HTTPS — profil
nie ma endpointu https, a redirect psułby preflighty CORS):

```bash
cd TimeSuggestions/TimeSuggestions
dotnet run --launch-profile http
```

**Frontend** (port 4200):

```bash
cd timesuggestions-web
npm install
npx ng serve
```

**Logowanie:** działa od razu po sklonowaniu — rejestracja aplikacji w Microsoft
Entra ID jest gotowa (multi-tenant + konta osobiste) i można logować się dowolnym
kontem Microsoft. Konfiguracja (client ID, authority, redirect URI) leży
w `timesuggestions-web/src/environments/environment.ts`; client ID to identyfikator
publiczny, nie sekret. Aplikacja działa jako klient publiczny (authorization code
+ PKCE, bez client secret) i prosi o delegowane uprawnienia **Calendars.Read**
i **Files.Read**. Ścieżka awaryjna: jeśli Twój tenant wymusza zgodę administratora
i nie możesz jej uzyskać, utwórz własną rejestrację (platforma **SPA**, redirect URI
`http://localhost:4200`, te same uprawnienia) i podmień `entraClientId`
w `environment.ts`.

**Testy** (107 testów backendu xUnit + 38 testów frontendu Vitest — bez sieci i logowania):

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
  oknem synchronizacji oraz z nieprawidłowymi datami (koniec przed początkiem) —
  a raport syncu pokazuje, ile i dlaczego (łącznie z filtrami wykonanymi w przeglądarce).
- Z dysku wchodzą tylko pliki Word/Excel zmodyfikowane przez zalogowanego użytkownika
  w oknie 7 dni; kilka edycji tego samego pliku jednego dnia (w strefie biznesowej) =
  jedna sugestia. Sugestia dokumentowa odzwierciedla ostatnią zaobserwowaną modyfikację
  pliku, nie pełną historię dni pracy (patrz „Ograniczenia prototypu").
- Dopasowanie do sprawy po **pełnych tokenach** znormalizowanego tekstu: termin
  jednowyrazowy musi być identycznym słowem („Alfa" nie pasuje do „Alfabet"), termin
  wielowyrazowy — ciągiem kolejnych słów; numery spraw działają dzięki zamianie
  separatorów na spacje. Świadomy kompromis: odmiany fleksyjne („Kowalskiego") nie są
  rozpoznawane — można je dodać jako słowa kluczowe sprawy. Trzy stany: jedno trafienie
  (sprawa przypisana), brak (karta „sprawdź to" z wyjaśnieniem), wiele (niejednoznaczna —
  UI wymienia pasujące sprawy).
- Zatwierdzenie wymaga wybranej sprawy i czasu 1–1440 min; tworzy `TimeEntry` ze źródłem
  pochodzenia i referencją do sugestii (dokładnie jeden wpis na sugestię — gwarantowane
  indeksem). Każda decyzja jest odwracalna (Cofnij / Przywróć / Usuń), a „Cofnij" działa
  także po przejściu na inną zakładkę.
