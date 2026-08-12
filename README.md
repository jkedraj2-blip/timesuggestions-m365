# TimeSuggestions

Prototyp automatycznych sugestii wpisów czasu pracy dla kancelarii prawnych.
Na podstawie spotkań z kalendarza Outlook i edytowanych dokumentów Word/Excel w OneDrive
(Microsoft Graph) aplikacja proponuje wpisy czasu, które użytkownik zatwierdza jednym
kliknięciem. Po zatwierdzeniu powstaje rozliczalny wpis (`TimeEntry`) przypisany
do sprawy (`Case`).

## Architektura i przepływ danych

```
MSAL login → frontend pobiera Graph (kalendarz + pliki + historia wersji, wszystkie strony)
           → filtr prywatności w przeglądarce (tytuły prywatnych nie opuszczają przeglądarki)
           → POST /api/sync (surowe dane + tombstone'y + liczniki filtrów klienckich)
           → backend: filtrowanie → silnik sesji → dopasowanie do spraw → zapis sugestii (dedup)
           → raport synchronizacji + lista sugestii → karty w UI
           → Zatwierdź / Edytuj / Odrzuć → TimeEntry w bazie → widok "Wpisy czasu"
```

- **Frontend (Angular 21, `timesuggestions-web/`)**: logowanie MSAL (klient publiczny,
  bez sekretu), pobieranie surowych danych z Graph, trzy widoki, oś czasu, diff wersji
  `.docx` w przeglądarce, automatyczne sprawdzanie w tle.
- **Backend (.NET 10 Web API, `TimeSuggestions/`)**: cała logika biznesowa (filtrowanie,
  silnik sesji, dopasowanie, dedup, operacje na czasie), SQLite przez EF Core, REST.

Token Graph żyje wyłącznie w przeglądarce (klient publiczny bez sekretu); backend
dostaje tylko surowe dane domenowe, a adresy przed dołączeniem nagłówka `Authorization`
są walidowane. Analiza ryzyk: [docs/bezpieczenstwo.md](docs/bezpieczenstwo.md).

## Widoki aplikacji

| Widok | Rola |
|---|---|
| **Sugestie** | Karty propozycji z akcjami Zatwierdź / Edytuj / Odrzuć, scalanie sesji i doliczanie wolnych przerw, raport synchronizacji, filtry statusu i źródła, automatyczne sprawdzanie co 10 minut |
| **Wpisy czasu** | Zapisane wpisy pogrupowane po dniach, korekty czasu i przerw, scalanie/rozdzielanie, rozliczanie pojedynczego wpisu i zakresów, widoki Aktywne / Archiwum |
| **Sprawy** | Zarządzanie sprawami i słowami kluczowymi sterującymi dopasowaniem; dezaktywacja zamiast usuwania |

Pod kafelkami podsumowania jest globalna, zwijana **oś czasu**: pasek miesiąca
z licznikami dni i lista pozycji wybranego dnia; klik w pozycję przenosi do właściwej
zakładki. Z kart sugestii i wpisów dostępna jest **historia zmian** dokumentu
(chronologia wersji, stany pozycji, diff `.docx` liczony w przeglądarce).

To zdjęcie pokazuje listę sugestii: kartę z dopasowaną sprawą, kartę z plakietką
„czas do uzupełnienia" i przyciski doliczania wolnej przerwy przy sąsiedniej sesji.

<!-- ZRÓB ZRZUT: zakładka Sugestie, widoczne min. 2 karty - jedna z zieloną plakietką
     sprawy, jedna z bursztynową „czas do uzupełnienia". Rozwiń wiersz sąsiadów,
     żeby było widać przyciski „Dolicz N min" i „Scal w jedną sesję". Zapisz jako
     docs/screenshots/sugestie.png -->
![Widok Sugestie](docs/screenshots/sugestie.png)

To zdjęcie pokazuje wpisy czasu pogrupowane po dniach: pasek akcji w grupach
(czas, przerwy, wpis) oraz plakietki przerw nieliczonych i zaokrąglenia.

<!-- ZRÓB ZRZUT: zakładka Wpisy czasu, widok Aktywne, min. 2 dni z sumami. Jeden wpis
     z plakietką „przerw nieliczonych" i rozwiniętym paskiem akcji (grupy Czas /
     Przerwy / Wpis). Zapisz jako docs/screenshots/wpisy-czasu.png -->
![Widok Wpisy czasu](docs/screenshots/wpisy-czasu.png)

To zdjęcie pokazuje listę spraw i formularz edycji ze słowami kluczowymi,
które sterują automatycznym dopasowaniem.

<!-- ZRÓB ZRZUT: zakładka Sprawy, lista spraw z numerami i klientami, otwarty formularz
     edycji z wypełnionymi słowami kluczowymi. Zapisz jako docs/screenshots/sprawy.png -->
![Widok Sprawy](docs/screenshots/sprawy.png)

To zdjęcie pokazuje rozwiniętą oś czasu: pasek miesiąca z badge'ami liczby pozycji
i listę pozycji dnia ze statusami (sugestia / do rozliczenia / rozliczone).

<!-- ZRÓB ZRZUT: rozwinięta oś czasu pod kafelkami, wybrany dzień z kilkoma pozycjami
     w różnych statusach (kolor + etykieta). Zapisz jako docs/screenshots/os-czasu.png -->
![Oś czasu](docs/screenshots/os-czasu.png)

To zdjęcie pokazuje historię zmian dokumentu: chronologię wersji z podświetlonymi
obszarami sesji (kolor = stan pozycji) i wynik porównania dwóch wersji.

<!-- ZRÓB ZRZUT: rozwinięta „Historia zmian" przy wpisie albo sugestii, widoczne
     obszary sesji z plakietkami stanu i otwarty wynik „Porównaj z poprzednią"
     z pozycjami +/−/~. Zapisz jako docs/screenshots/historia-zmian.png -->
![Historia zmian z diffem](docs/screenshots/historia-zmian.png)

To zdjęcie pokazuje raport synchronizacji: liczniki „pobrano / odfiltrowano /
utworzono" z rozbiciem powodów odfiltrowania.

<!-- ZRÓB ZRZUT: zakładka Sugestie tuż po synchronizacji, rozwinięty raport z licznikami
     i powodami odfiltrowania. Zapisz jako docs/screenshots/raport-sync.png -->
![Raport synchronizacji](docs/screenshots/raport-sync.png)

## Zapisane decyzje projektowe

Pełne uzasadnienia: [docs/decyzje-projektowe.md](docs/decyzje-projektowe.md).

| Decyzja | Jedno zdanie |
|---|---|
| [Delta query zamiast `/me/drive/recent`](docs/decyzje-projektowe.md#delta-query-zamiast-medriverecent) | Wspierany endpoint z tombstone'ami usuniętych plików; filtry po stronie klienta są raportowane licznikami |
| [Cache `deltaLink`](docs/decyzje-projektowe.md#cache-deltalink-w-localstorage) | Kolejne synchronizacje pobierają tylko zmiany; link walidowany przed użyciem |
| [Strefa biznesowa](docs/decyzje-projektowe.md#strefa-czasowa-prefer--strefa-biznesowa) | Oba źródła sprowadzane do jednej strefy; czas trwania liczony z instantów UTC |
| [Odporność na błędy Graph](docs/decyzje-projektowe.md#odporność-na-błędy-graph) | Ponowienia 429/5xx z `Retry-After`, stronicowanie do końca |
| [Filtr prywatności w przeglądarce](docs/decyzje-projektowe.md#filtr-prywatności-w-przeglądarce) | Tytuły prywatnych spotkań nie opuszczają przeglądarki |
| [Append-only dziennik `DocumentActivity`](docs/decyzje-projektowe.md#append-only-dziennik-aktywności-documentactivity) | Fakt = (plik, wersja, moment); każda synchronizacja jest pomiarem |
| [Silnik sesji](docs/decyzje-projektowe.md#silnik-sesji-zamiast-sztywnych-30-minut) | Czas z sesji pracy między zapisami; bez rozbiegu, minimum tylko przy braku pomiaru |
| [Koniec z „domyślnym czasem dokumentu"](docs/decyzje-projektowe.md#koniec-z-domyślnym-czasem-dokumentu) | Zgadywana wartość wyleciała; decyzję o czasie podejmuje człowiek |
| [Dedup po kotwicy sesji](docs/decyzje-projektowe.md#dedup-po-źródło-id-z-graph-kotwica-sesji) | Jeden plik może mieć wiele sesji jednego dnia; odrzucona sugestia nie wraca |
| [Kolejność po ostatniej modyfikacji](docs/decyzje-projektowe.md#kolejność-sugestii-po-ostatniej-modyfikacji) | Świeżo zapisany dokument jest na górze listy |
| [Praca po zatwierdzeniu daje nową sugestię](docs/decyzje-projektowe.md#praca-po-zatwierdzeniu-daje-nową-sugestię) | Silnik dostaje tylko aktywność niepokrytą przez rozstrzygnięte zakresy |
| [Scalanie sugestii i wolne przerwy](docs/decyzje-projektowe.md#scalanie-sugestii-i-doliczanie-wolnych-przerw) | Luka oferowana tylko, gdy naprawdę wolna; podział jawny, nie po połowie |
| [Limit przerwy tylko dla doliczania](docs/decyzje-projektowe.md#limit-przerwy-dotyczy-doliczania-nie-scalania) | 8 godzin na doliczenie jednym ruchem; scalanie sesji tego samego pliku bez limitu |
| [Zdanie o przerwie z godzinami](docs/decyzje-projektowe.md#zdanie-o-wolnej-przerwie-podaje-godziny) | „Od 18:00 do 18:30 nic nie jest rozliczone", zamiast samych minut i skrótu „dalej:" |
| [Luka przez północ odmawiana](docs/decyzje-projektowe.md#luka-przez-lokalną-północ-nie-jest-oferowana-ani-przyjmowana) | Sesje nie przekraczają granicy dnia; `EntryDate` zostaje spójne z godzinami |
| [Sąsiedztwo na przerwach](docs/decyzje-projektowe.md#sąsiedztwo-sprawdzane-na-przerwach-nie-na-całym-przedziale-scalenia) | Scalenie blokuje tylko obca pozycja w przerwie między składowymi |
| [Przerwa liczona podłogą](docs/decyzje-projektowe.md#przerwa-liczona-podłogą-nie-zaokrągleniem) | Zaokrąglenie w górę tworzyło trwałe nakładania sekundowe |
| [Operacja mówi, co zrobiła](docs/decyzje-projektowe.md#operacja-na-czasie-mówi-co-zrobiła) | Etykiety z liczbą i kierunkiem, potwierdzenie po operacji |
| [Wiele sugestii do jednego wpisu](docs/decyzje-projektowe.md#relacja-wiele-sugestii-do-jednego-wpisu) | Klucz obcy po stronie sugestii; wyścig zatwierdzeń łapie token współbieżności |
| [Godziny na wpisie](docs/decyzje-projektowe.md#godziny-na-wpisie-startedatendedat) | Wpis zna swoje położenie na osi dnia; koniec scalenia to najpóźniejszy koniec składowych |
| [Zasięg ≠ czas (`SuggestionSpan`)](docs/decyzje-projektowe.md#zasięg-pozycji-to-nie-to-samo-co-jej-czas-suggestionspan) | Godziny to teren, `DurationMinutes` to rachunek; jedna funkcja liczy koniec zasięgu |
| [Nakładanie przycina, nie odrzuca](docs/decyzje-projektowe.md#nakładanie-przycina-zasięg-nie-odrzuca-zatwierdzenia) | Zatwierdzenie przycina zasięg do sąsiada (także oczekującej sugestii) i mówi o tym |
| [Limit czasu blokuje tylko wzrost](docs/decyzje-projektowe.md#limit-czasu-wpisu-blokuje-tylko-wzrost) | Wpis powyżej 480 min można zawsze zmniejszać |
| [Przerwy wpisu z dziennika](docs/decyzje-projektowe.md#przerwy-wpisu-liczone-z-dziennika-z-jawnym-stanem) | Jeden przełącznik liczona/nieliczona; stan z decyzji człowieka, próg jako reguła awaryjna |
| [Rozliczenie pojedynczego wpisu](docs/decyzje-projektowe.md#rozliczenie-pojedynczego-wpisu) | Gotowość do faktury jest cechą wpisu, nie dnia |
| [Zaokrąglanie do jednostki](docs/decyzje-projektowe.md#zaokrąglanie-do-jednostki-rozliczeniowej) | Do najbliższej wielokrotności, połowa w dół; wynik liczy serwer |
| [Numer sesji („edycja 3")](docs/decyzje-projektowe.md#numer-sesji-edycja-3) | Liczony przy odczycie, przez całą historię pliku, dla każdej sesji |
| [Korekty jako dziennik](docs/decyzje-projektowe.md#korekty-jako-dziennik-timeentryadjustment) | Każda zmiana czasu ma ślad audytowy; rozdzielenie kasuje dziennik z ostrzeżeniem |
| [Odświeżanie oczekujących przy syncu](docs/decyzje-projektowe.md#odświeżanie-oczekujących-przy-syncu) | Zmiana tytułu w źródle nadpisuje oczekującą sugestię |
| [Rekonsyliacja kalendarza](docs/decyzje-projektowe.md#rekonsyliacja-kalendarza) | Przeniesione spotkanie aktualizuje sugestię; kasowanie tylko przy kompletnym snapshocie |
| [Współbieżność w bazie](docs/decyzje-projektowe.md#współbieżność-rozstrzygana-w-bazie) | Token współbieżności na statusie i unikalny numer sprawy dają jawne 409 |
| [Dezaktywacja zamiast usuwania spraw](docs/decyzje-projektowe.md#dezaktywacja-zamiast-usuwania-spraw) | Historia rozliczeń zostaje; numer sprawy nie jest recyklingowany |
| [Archiwum zamiast usuwania](docs/decyzje-projektowe.md#archiwum-zamiast-usuwania) | Rozliczone wpisy są niezmienne; storno to świadomie odłożona funkcja |
| [Edycja = zatwierdzenie z wartościami](docs/decyzje-projektowe.md#edycja--zatwierdzenie-z-poprawionymi-wartościami) | Jeden endpoint, ta sama walidacja |
| [Diff `.docx` w przeglądarce](docs/decyzje-projektowe.md#diff-wersji-docx-liczony-w-przeglądarce) | Treść dokumentów nigdy nie przechodzi przez backend; diff po liniach |
| [Historia zmian przy decyzji](docs/decyzje-projektowe.md#historia-zmian-tam-gdzie-zapada-decyzja) | Chronologia dostępna z karty sugestii i wpisu, nie tylko z osi czasu |
| [Historia zmian ze stanem pozycji](docs/decyzje-projektowe.md#historia-zmian-pokazuje-stan-wszystkich-pozycji-pliku) | Stan każdej sesji przychodzi z bazy i jest jeden, niezależnie skąd się patrzy |
| [Granica dnia zamiast przerwy 3797 min](docs/decyzje-projektowe.md#granica-dnia-zamiast-przerwy-na-3797-minut) | Odstęp między dniami to cicha kreska, nie „przerwa" do rozliczenia |
| [Przerwa mówi, czy ktoś ją rozlicza](docs/decyzje-projektowe.md#przerwa-w-historii-zmian-mówi-czy-ktoś-ją-rozlicza) | Każdy przestój powyżej progu ma jawny stan wliczenia |
| [Akcje pod treścią karty](docs/decyzje-projektowe.md#układ-karty-wpisu-akcje-pod-treścią-nie-obok) | Historia wersji dostaje pełną szerokość, akcje są w podpisanych grupach |
| [Czemu ciągła edycja bywa niewidoczna](docs/decyzje-projektowe.md#dlaczego-ciągła-edycja-bywa-niewidoczna-i-co-z-tym-zrobiono) | Word pieczętuje wersje rzadko; próbka per sync łata dziurę w danych |
| [Automatyczne sprawdzanie co 10 min](docs/decyzje-projektowe.md#automatyczne-sprawdzanie-co-10-minut-przy-otwartej-karcie) | Gęstsze próbkowanie historii; domyślnie wyłączone, z jednorazową zachętą |
| [Jedno rozgłoszenie zmiany danych](docs/decyzje-projektowe.md#jedno-rozgłoszenie-zmiany-danych) | Widoki odświeżają się same, bez podwójnych przeładowań |
| [Raport z synchronizacji](docs/decyzje-projektowe.md#raport-z-synchronizacji) | Aplikacja pokazuje swoją pracę; pozycje spoza okna nie są raportowane wcale |

## Reguły biznesowe (skrót)

- Z kalendarza odpadają: prywatne/poufne, anulowane, krótsze niż próg (domyślnie 5 min),
  całodniowe, poza oknem synchronizacji (domyślnie 7 dni, w UI do 30, w API do 90).
- Z dysku wchodzą pliki Word/Excel zmodyfikowane przez użytkownika w oknie. Plik
  z historią wersji daje tyle sugestii, ile było sesji pracy (także kilku jednego dnia);
  dopiero plik bez historii dostaje jedną sugestię fallbackową na dzień.
- Dopasowanie do sprawy po pełnych tokenach (klient, numer, słowa kluczowe);
  przykłady w [docs/dopasowanie.md](docs/dopasowanie.md).
- **Niezmiennik nakładania: każda minuta doby należy do najwyżej jednej pozycji**
  (wpisu dowolnego źródła albo oczekującej sugestii). Zatwierdzenie nie odrzuca przy
  nakładaniu: przycina zasięg wpisu do najbliższej pozycji i mówi o tym w `notice`.
- Operacje prawnika działają tylko na pozycjach aktywnych; archiwum blokuje wszystko.
  Każda korekta ląduje w dzienniku `TimeEntryAdjustment`; sync nie modyfikuje wpisów
  ani sugestii poprawionych ręcznie. Rozliczenie (archiwizacja) jest jednokierunkowe;
  pozostałe decyzje są odwracalne.

## Ograniczenia prototypu

- **API bez uwierzytelniania** (świadomie, lokalny prototyp): szczegóły i plan
  produkcyjny w [docs/bezpieczenstwo.md](docs/bezpieczenstwo.md).
- **Gęstość historii wersji zależy od klienta Worda**: Word Online wersjonuje często,
  Word desktop potrafi oddać 1-2 wersje na godzinę; przy rzadkiej historii sesja
  degraduje się do jednego ciągłego odcinka, a automatyczne sprawdzanie dogęszcza
  pomiar próbkami.
- **Jedna strefa biznesowa** (`Suggestions:BusinessTimeZoneId`, domyślnie
  `Europe/Warsaw`), bez obsługi wielu stref per użytkownik.

## Uruchomienie

Wymagania: .NET SDK 10, Node 20+, Angular CLI.

```bash
cd TimeSuggestions/TimeSuggestions && dotnet run --launch-profile http
```

```bash
cd timesuggestions-web && npm install && npx ng serve
```

Backend nasłuchuje na `http://localhost:5188`; baza SQLite i migracje wykonują się same
przy starcie (z kopią `.bak` przed migracją). Logowanie działa od razu po sklonowaniu
(rejestracja Entra ID: multi-tenant + konta osobiste, klient publiczny z PKCE,
uprawnienia **Calendars.Read** i **Files.Read**); konfiguracja w
`timesuggestions-web/src/environments/environment.ts`, a jeśli tenant wymusza zgodę
administratora, utwórz własną rejestrację (SPA, redirect `http://localhost:4200`)
i podmień `entraClientId`.

**Testy** (344 backend xUnit + 230 frontend Vitest; bez sieci i logowania):

```bash
cd TimeSuggestions && dotnet test
```

```bash
cd timesuggestions-web && npm test -- --watch=false
```

## Struktura katalogów

```
TimeSuggestions/TimeSuggestions/   API .NET
  Configuration/   opcje (progi czasowe, okno syncu, jednostka rozliczeniowa)
  Contracts/       DTO wejścia/wyjścia + walidacja + raport syncu
  Controllers/     cienkie kontrolery REST
  Data/            DbContext + seed spraw, migrator z kopią bazy
  Migrations/      migracje EF Core (SQLite)
  Models/          encje (Case, Suggestion, TimeEntry, TimeEntryAdjustment,
                   DocumentActivity, SyncRun) + enumy statusów i rodzajów korekt
  Services/        logika czysta (normalizacja, filtry, dopasowanie, silnik sesji,
                   TimeAxis, SuggestionSpan, BusinessMoment) + serwisy aplikacyjne
                   (sync, approval, archiwum, operacje na sugestiach i wpisach,
                   przerwy wpisów, numery sesji, oś czasu, widoki wpisów, summary)
TimeSuggestions/TimeSuggestions.Tests/   xUnit + fixtures JSON (TestData/)
timesuggestions-web/src/app/
  components/      suggestion-card, document-history, timeline-panel
  pages/           suggestions-page, time-entries-page, cases-page
  models/          typy 1:1 z DTO backendu i Graph
  pipes/           duration, polish-plural
  services/        auth (MSAL), graph-http, graph-calendar, graph-files, graph-config,
                   api, summary-store, auto-sync, sync-preferences, docx-diff,
                   confirm-state, scroll-highlight, case-label, theme, toast,
                   data-refresh, user-message
```

## Dokumentacja

[Decyzje projektowe](docs/decyzje-projektowe.md) ·
[Endpointy API](docs/endpointy.md) ·
[Przykłady dopasowania](docs/dopasowanie.md) ·
[Bezpieczeństwo i plan produkcyjny](docs/bezpieczenstwo.md)
