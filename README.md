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
           → GET /api/suggestions → karty w UI
           → Zatwierdź / Edytuj / Odrzuć → TimeEntry w bazie
```

- **Frontend (Angular 21, `timesuggestions-web/`)** — logowanie MSAL (klient publiczny,
  bez sekretu), pobieranie surowych danych z Microsoft Graph, cały interfejs.
- **Backend (.NET 10 Web API, `TimeSuggestions/`)** — cała logika biznesowa
  (normalizacja, filtrowanie, dopasowanie, agregacja, ochrona przed duplikatami),
  baza SQLite przez EF Core, endpointy REST.

**Dlaczego token nie idzie do backendu:** aplikacja jest klientem publicznym bez sekretu,
więc token Graph żyje wyłącznie w przeglądarce. Backend dostaje tylko surowe dane domenowe
(tytuły, daty, nazwy plików) — mniejsza powierzchnia ataku i prostszy model bezpieczeństwa.
Logika w .NET jest przy tym czysto testowalna (xUnit, bez sieci i logowania).

## Zapisane decyzje projektowe

| Decyzja | Uzasadnienie |
|---|---|
| **Delta query zamiast `/me/drive/recent`** | Endpoint „recent" jest oznaczony przez Microsoft jako wycofywany. `GET /me/drive/root/delta` jest wspierany i zwraca elementy dysku ze zmianami; filtrowanie (okno 7 dni, rozszerzenia Word/Excel, autor modyfikacji) odbywa się po stronie klienta, bo delta nie wspiera `$filter`. Rozważone alternatywy: wyszukiwanie z sortowaniem po dacie (niestabilne wsparcie `$orderby`), endpointy aktywności (niedostępne dla kont osobistych). Szczegóły: `graph-files.service.ts`. |
| **Strefa czasowa przez nagłówek `Prefer`** | Graph domyślnie zwraca czasy w UTC; nagłówek `Prefer: outlook.timezone="Central European Standard Time"` przenosi konwersję na serwer Graph. Błąd strefy przekładałby się wprost na złe godziny wpisów. |
| **Domyślny czas dokumentu jako parametr** | Graph mówi tylko *kiedy* plik zmieniono, nie *jak długo* trwała praca. Domyślne 30 min to parametr `Suggestions:DefaultDocumentDurationMinutes` w `appsettings.json`, nie liczba w kodzie; użytkownik może poprawić wartość przed zatwierdzeniem. |
| **Dedup po `(źródło, id z Graph, dzień)`** | Indeks unikalny w bazie + pominięcie istniejących kluczy przy synchronizacji. Dzięki temu powtórny sync nie tworzy duplikatów, a **odrzucona sugestia nie wraca** (status zmieniany, rekord nieusuwany). |
| **Edycja = zatwierdzenie z poprawionymi wartościami** | Jeden endpoint `approve` przyjmuje wartości finalne — mniej ścieżek, ta sama walidacja. |

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

**Testy** (40 testów jednostkowych logiki — bez sieci i logowania):

```bash
cd TimeSuggestions
dotnet test
```

Endpointy można wywoływać ręcznie plikiem `TimeSuggestions/TimeSuggestions/TimeSuggestions.http`.

## Struktura katalogów

```
TimeSuggestions/
  TimeSuggestions/          API .NET
    Configuration/          opcje (progi czasowe, okno syncu)
    Contracts/              DTO wejścia/wyjścia + walidacja
    Controllers/            cienkie kontrolery REST
    Data/                   DbContext + seed spraw testowych
    Migrations/             migracje EF Core (SQLite)
    Models/                 encje: Case, Suggestion, TimeEntry
    Services/               logika czysta (normalizacja, filtr, dopasowanie,
                            budowa sugestii) + serwisy aplikacyjne (sync, approval)
  TimeSuggestions.Tests/    xUnit + fixtures JSON (TestData/)
timesuggestions-web/
  src/app/
    components/             suggestion-list, suggestion-card
    models/                 typy 1:1 z DTO backendu i Graph
    services/               auth (MSAL), graph-calendar, graph-files (delta), api
```

## Reguły biznesowe (skrót)

- Z kalendarza odpadają: wydarzenia prywatne/poufne, krótsze niż 5 min
  (dokładnie 5 min przechodzi), całodniowe.
- Z dysku wchodzą tylko pliki Word/Excel zmodyfikowane przez zalogowanego użytkownika
  w oknie 7 dni; kilka edycji tego samego pliku jednego dnia = jedna sugestia.
- Dopasowanie do sprawy: znormalizowany tekst zawiera nazwę klienta, numer sprawy lub
  słowo kluczowe. Trzy stany: jedno trafienie (sprawa przypisana), brak (karta „sprawdź to"),
  wiele (niejednoznaczna — wybór przy edycji).
- Zatwierdzenie wymaga wybranej sprawy i czasu > 0; tworzy `TimeEntry` ze źródłem
  pochodzenia i referencją do sugestii.
