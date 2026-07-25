# TimeSuggestions

Prototyp automatycznych sugestii wpisów czasu pracy dla kancelarii prawnych.
Na podstawie spotkań z kalendarza Outlook i edytowanych dokumentów Word/Excel w OneDrive
(Microsoft Graph) aplikacja proponuje wpisy czasu, które użytkownik zatwierdza jednym
kliknięciem — powstaje wtedy rozliczalny wpis (`TimeEntry`) przypisany do sprawy (`Case`).

## Architektura i przepływ danych

```
MSAL login → frontend pobiera Graph (7 dni: kalendarz + pliki)
           → POST /api/sync (surowe dane)
           → backend: filtrowanie → dopasowanie do spraw → zapis sugestii (dedup)
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
| **Delta query zamiast `/me/drive/recent`** | Endpoint „recent" jest oznaczony przez Microsoft jako wycofywany. `GET /me/drive/root/delta` jest wspierany i zwraca elementy dysku ze zmianami; filtrowanie (okno 7 dni, rozszerzenia Word/Excel, autor modyfikacji) odbywa się po stronie klienta, bo delta nie wspiera `$filter`. Rozważone alternatywy: wyszukiwanie z sortowaniem po dacie (niestabilne wsparcie `$orderby`), endpointy aktywności (niedostępne dla kont osobistych). Szczegóły: `graph-files.service.ts`. |
| **Cache `deltaLink` w localStorage** | Pierwszy przebieg delta przechodzi cały dysk (na dużym OneDrive to dziesiątki sekund). Zapamiętany `deltaLink` sprawia, że kolejne synchronizacje pobierają wyłącznie zmiany. Link zapisywany dopiero po udanym zapisie w backendzie; wygaśnięcie (HTTP 410) czyści cache i wymusza pełny przebieg. |
| **Strefa czasowa przez nagłówek `Prefer`** | Graph domyślnie zwraca czasy w UTC; nagłówek `Prefer: outlook.timezone` przenosi konwersję na serwer Graph. Błąd strefy przekładałby się wprost na złe godziny wpisów. |
| **Domyślny czas dokumentu jako parametr** | Graph mówi tylko *kiedy* plik zmieniono, nie *jak długo* trwała praca. Domyślne 30 min to parametr `Suggestions:DefaultDocumentDurationMinutes` w `appsettings.json`; użytkownik może poprawić wartość przed zatwierdzeniem. |
| **Dedup po `(źródło, id z Graph, dzień)`** | Indeks unikalny w bazie + scalanie z istniejącymi przy synchronizacji. Powtórny sync nie tworzy duplikatów, a **odrzucona sugestia nie wraca** (status zmieniany, rekord nieusuwany). |
| **Odświeżanie oczekujących przy syncu** | Zmiana nazwy pliku/tytułu spotkania nie zmienia ID w Graph, więc sam dedup zostawiałby stary tytuł. Sugestie **oczekujące** są nadpisywane wartościami ze źródła (z ponownym dopasowaniem); zatwierdzonych i odrzuconych sync nie dotyka. |
| **Dezaktywacja zamiast usuwania spraw** | Wpisy czasu wskazują na sprawy kluczem obcym — twarde usunięcie niszczyłoby dane rozliczeniowe. `IsActive=false` wyłącza sprawę z dopasowania i list wyboru, zachowując historię. |
| **Edycja = zatwierdzenie z poprawionymi wartościami** | Jeden endpoint `approve` przyjmuje wartości finalne — mniej ścieżek, ta sama walidacja. |
| **Raport z synchronizacji** | Backend zwraca liczniki: ile pobrano, ile odfiltrowano per reguła, ile zagregowano, jak dopasowano. Bez tego odfiltrowanie spotkań wygląda dla użytkownika jak zgubione dane. |

## Endpointy API

| Metoda i ścieżka | Opis |
|---|---|
| `POST /api/sync` | Przyjmuje surowe dane z Graph (+ opcjonalny domyślny czas dokumentu), zwraca pełny raport |
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

**Backend** (port 5188, baza SQLite tworzy się sama przy starcie):

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

**Konfiguracja logowania:** w `timesuggestions-web/src/app/services/auth.service.ts`
wklej identyfikator aplikacji (klienta) z rejestracji w Microsoft Entra ID w miejsce
placeholdera. Client ID jest identyfikatorem publicznym (nie sekretem). Rejestracja musi
mieć platformę **SPA** z redirect URI `http://localhost:4200`. Aplikacja nie używa
client secret — działa jako klient publiczny (authorization code + PKCE).

**Testy** (60 testów jednostkowych logiki — bez sieci i logowania):

```bash
cd TimeSuggestions
dotnet test
```

Scenariusz manualny do demo: [DEMO.md](DEMO.md).

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
    services/               auth (MSAL), graph-calendar, graph-files (delta+cache),
                            api, summary-store, toast
  src/styles.css            system wizualny: tokeny, komponenty, ciemny motyw
```

## Reguły biznesowe (skrót)

- Z kalendarza odpadają: wydarzenia prywatne/poufne, krótsze niż 5 min
  (dokładnie 5 min przechodzi), całodniowe — a raport syncu pokazuje, ile i dlaczego.
- Z dysku wchodzą tylko pliki Word/Excel zmodyfikowane przez zalogowanego użytkownika
  w oknie 7 dni; kilka edycji tego samego pliku jednego dnia = jedna sugestia.
- Dopasowanie do sprawy: znormalizowany tekst zawiera nazwę klienta, numer sprawy lub
  słowo kluczowe. Trzy stany: jedno trafienie (sprawa przypisana), brak (karta „sprawdź to"
  z wyjaśnieniem), wiele (niejednoznaczna — UI wymienia pasujące sprawy).
- Zatwierdzenie wymaga wybranej sprawy i czasu > 0; tworzy `TimeEntry` ze źródłem
  pochodzenia i referencją do sugestii. Każda decyzja jest odwracalna (Cofnij / Przywróć / Usuń).
