# TimeSuggestions

TimeSuggestions jest prototypem aplikacji wspierającej ewidencję czasu pracy w kancelarii
prawnej. Na podstawie spotkań z kalendarza Outlook oraz aktywności w dokumentach Word i
Excel zapisanych w OneDrive aplikacja tworzy sugestie wpisów czasu. Użytkownik weryfikuje
sprawę, opis i czas, a następnie zatwierdza sugestię jako rozliczalny `TimeEntry`.

Projekt nie jest automatycznym systemem rozliczeniowym. Dane z Microsoft 365 stanowią
materiał pomocniczy, natomiast decyzja o przypisaniu czasu do klienta i sprawy zawsze
należy do użytkownika.

## Zakres funkcjonalny

Aplikacja udostępnia trzy główne widoki:

| Widok | Zakres |
|---|---|
| **Sugestie** | Synchronizacja danych z Microsoft 365, filtrowanie i zatwierdzanie propozycji, edycja czasu i opisu, odrzucanie, przywracanie, scalanie sesji dokumentowych oraz rozdzielanie wolnych przerw |
| **Wpisy czasu** | Ewidencja aktywnych wpisów według dni, korekty czasu, obsługa przerw, scalanie i rozdzielanie wpisów, zaokrąglanie do jednostki rozliczeniowej oraz archiwizacja rozliczonego czasu |
| **Sprawy** | Dodawanie i edycja spraw, konfiguracja numeru, klienta i słów kluczowych, aktywacja oraz dezaktywacja bez usuwania historii |

Nad widokami znajduje się podsumowanie liczby oczekujących sugestii, aktywnych wpisów,
nierozliczonego czasu i ostatniej synchronizacji. Zwijana oś czasu przedstawia sugestie,
aktywne wpisy i pozycje rozliczone w układzie dziennym. Dla dokumentów dostępna jest
również historia obserwowanych wersji; porównanie treści działa dla plików `.docx` i jest
wykonywane wyłącznie w przeglądarce.

## Architektura i przepływ danych

```text
Logowanie MSAL
  -> frontend pobiera kalendarz i metadane plików z Microsoft Graph
  -> dane prywatne i nieobsługiwane elementy są filtrowane w przeglądarce
  -> POST /api/sync przekazuje dane domenowe do lokalnego API
  -> backend filtruje dane, odtwarza sesje dokumentowe i dopasowuje sprawy
  -> zapisuje lub aktualizuje sugestie w SQLite
  -> użytkownik zatwierdza, edytuje albo odrzuca propozycję
  -> zatwierdzenie tworzy wpis czasu przypisany do sprawy
```

Frontend w katalogu `timesuggestions-web/` jest aplikacją Angular 21. Odpowiada za
logowanie MSAL, komunikację z Microsoft Graph, filtrowanie danych wrażliwych, obsługę
interfejsu i opcjonalną synchronizację w tle.

Backend w katalogu `TimeSuggestions/` jest aplikacją .NET 10 Web API. Zawiera logikę
biznesową, silnik sesji dokumentowych, dopasowanie spraw, ochronę przed duplikatami,
operacje na czasie oraz dostęp do lokalnej bazy SQLite przez EF Core.

Aplikacja jest klientem publicznym OAuth 2.0 z PKCE i nie posiada `client secret`.
Token Microsoft Graph pozostaje w przeglądarce i nie jest przekazywany do backendu.
Adres każdego żądania zawierającego nagłówek `Authorization`, w tym adresy stronicowania
`@odata.nextLink` i `@odata.deltaLink`, jest weryfikowany przed użyciem.

## Synchronizacja danych

Kalendarz jest pobierany jako kompletny widok wybranego okna czasu. Domyślne okno
obejmuje 7 dni, w interfejsie można wybrać 14 albo 30 dni, a API dopuszcza maksymalnie
90 dni. Frontend odrzuca wydarzenia prywatne, osobiste, poufne i anulowane, zanim ich
tytuły opuszczą przeglądarkę. Backend ponownie stosuje reguły walidacji i odrzuca także
wydarzenia całodniowe, z błędnymi datami, poza oknem oraz krótsze niż skonfigurowane
minimum, domyślnie 5 minut.

Dokumenty są pobierane przez `GET /me/drive/root/delta`. Pierwsza synchronizacja może
przejść przez cały OneDrive, natomiast kolejne używają zapisanego `deltaLink` i pobierają
tylko zmiany. Wskaźnik delty jest zapisywany dopiero po prawidłowym zakończeniu
`POST /api/sync`, dzięki czemu nieudany przebieg nie powoduje utraty zmian. Odpowiedzi
`410 Gone` powodują kontrolowane wyczyszczenie wskaźnika i wykonanie pełnej synchronizacji.
Jawne tombstone'y usuniętych plików usuwają wyłącznie oczekujące sugestie; brak pliku
w przyrostowej odpowiedzi nie jest traktowany jako dowód jego usunięcia.

Obsługiwane są pliki `.docx`, `.doc`, `.xlsx` i `.xls`, zmodyfikowane przez zalogowanego
użytkownika w wybranym oknie synchronizacji. Dla każdego kwalifikującego się pliku
frontend pobiera stronicowaną historię wersji z Microsoft Graph. Liczba równoległych
żądań jest ograniczona, a błąd historii pojedynczego pliku nie przerywa całej
synchronizacji.

## Naliczanie czasu pracy nad dokumentami

### Dane wejściowe

Microsoft Graph nie udostępnia informacji o tym, przez jaki czas użytkownik aktywnie
edytował dokument. Dla plików Word i Excel dostępne są przede wszystkim znaczniki
`lastModifiedDateTime` elementu oraz zapisanych wersji. Z tego powodu czas dokumentowy
jest estymowany na podstawie obserwowalnych punktów zapisu, a nie mierzony jak przez
lokalny rejestrator aktywności klawiatury lub procesu.

Backend prowadzi niemodyfikowalny dziennik `DocumentActivity`. Naturalnym kluczem
obserwacji jest trójka:

```text
(identyfikator pliku, identyfikator wersji lub próbki, moment modyfikacji UTC)
```

Do dziennika trafiają znaczniki wszystkich pobranych wersji. Jeżeli bieżący
`driveItem.lastModifiedDateTime` wskazuje moment, którego nie ma w historii wersji,
zapisywana jest dodatkowa próbka bieżącego stanu pliku. Jest to istotne zwłaszcza przy
ciągłej edycji: aplikacje Office mogą przez dłuższy czas aktualizować tę samą wersję albo
utworzyć nową wersję dopiero po zamknięciu dokumentu lub okresie bezczynności.

### Budowanie sesji

Dla każdego pliku aktywności są porządkowane chronologicznie i deduplikowane według
momentu. Następnie silnik dzieli je na sesje zgodnie z konfigurowalnymi progami:

- odstęp do 15 minut włącznie oznacza kontynuację tej samej sesji;
- odstęp powyżej 15 i do 30 minut włącznie pozostaje częścią sesji, ale jest oznaczany
  jako wykryta przerwa, którą użytkownik może wyłączyć z rozliczenia;
- odstęp dłuższy niż 30 minut rozpoczyna nową sesję i nową sugestię;
- zmiana lokalnego dnia zawsze rozpoczyna nową sesję, niezależnie od długości odstępu.

Początkiem sesji jest moment pierwszego zaobserwowanego zapisu, a końcem moment
ostatniego zapisu. Czas brutto stanowi zaokrąglona do pełnej minuty różnica między tymi
punktami. Obliczenie jest wykonywane na znacznikach UTC, aby zmiana czasu letniego lub
zimowego nie zafałszowała długości, natomiast data i godziny prezentowane użytkownikowi
są przeliczane na strefę biznesową, domyślnie `Europe/Warsaw`.

System nie dodaje czasu przed pierwszym zapisem ani po ostatnim zapisie. Nie stosuje
również dawnej, stałej wartości 30 minut dla każdego dokumentu. Jeżeli historia zawiera
tylko jeden punkt albo wszystkie obserwacje mieszczą się po zaokrągleniu w tej samej
minucie, rzeczywisty czas jest nieznany. Taka sugestia otrzymuje techniczne minimum
5 minut oraz znacznik `NeedsTimeReview`. Przed zatwierdzeniem użytkownik musi podać lub
potwierdzić czas; operacja hurtowa pomija takie pozycje.

Jeżeli pobranie historii wersji nie powiedzie się, plik trafia do trybu awaryjnego:
powstaje najwyżej jedna sugestia dla danego pliku i dnia, również oznaczona jako
wymagająca weryfikacji czasu. Błąd jest wykazywany w raporcie synchronizacji.

### Znaczenie synchronizacji co 10 minut

Opcjonalne automatyczne sprawdzanie wykonuje synchronizację co 10 minut, gdy użytkownik
jest zalogowany i karta aplikacji pozostaje otwarta. Funkcja jest domyślnie wyłączona
i wymaga świadomego włączenia. Po trzech kolejnych błędach wyłącza się automatycznie,
aby nie wykonywać bez końca nieskutecznych żądań.

Dziesięciominutowy interwał jest krótszy od 15-minutowego progu ciągłości sesji. Jeżeli
Office aktualizuje `lastModifiedDateTime`, częstsze odczyty tworzą gęstszy szereg próbek
i ograniczają ryzyko błędnego podziału ciągłej pracy. Mechanizm ten poprawia jakość
estymacji, szczególnie wtedy, gdy wiele obserwacji dotyczy tej samej wersji pliku.

Nie jest to jednak gwarancja kompletnego pomiaru. Synchronizacja działa tylko przy
otwartej aplikacji i aktywnej sesji Microsoft, a częstotliwość tworzenia wersji oraz
aktualizacji metadanych zależy od Worda, Excela, sposobu zapisu i klienta Office.
Jeżeli źródło nie opublikuje kolejnych znaczników, aplikacja nie ma danych pozwalających
odtworzyć czasu między nimi. Z tego powodu wynik należy traktować jako rekonstrukcję
sesji na podstawie dostępnej telemetrii, wymagającą kontroli użytkownika.

### Dalsza praca, przerwy i nakładanie

Po zatwierdzeniu lub odrzuceniu sugestii jej przedział aktywności jest uznawany za
rozstrzygnięty. Późniejsze zapisy tego samego pliku mogą zbudować kolejną sugestię,
zamiast zostać zablokowane przez wcześniejszy wpis. Numer sesji, na przykład „edycja 3”,
jest wyliczany na podstawie całej historii pliku.

Przerwy wykryte wewnątrz sesji są częścią czasu brutto, dopóki użytkownik ich nie
wyłączy. Wolną lukę między sąsiednimi pozycjami można przypisać do jednej lub obu stron,
jeżeli nie zawiera innego wpisu lub sugestii, nie przekracza lokalnej północy i spełnia
limit operacji. Sesje tego samego dokumentu można scalać. System rozróżnia zasięg
czasowy pozycji od liczby minut podlegających rozliczeniu i pilnuje, aby jedna minuta
doby nie należała do więcej niż jednej aktywnej pozycji. Przy zatwierdzaniu zasięg może
zostać przycięty do najbliższego sąsiada, a wynik operacji jest komunikowany wprost.

## Dopasowanie sugestii do spraw

Dopasowanie wykorzystuje nazwę pliku albo tytuł spotkania oraz aktywne dane sprawy:
klienta, numer sprawy i słowa kluczowe. Tekst jest normalizowany i dzielony na pełne
tokeny. Termin jednowyrazowy musi odpowiadać całemu słowu, dlatego `Alfa` nie pasuje do
`Alfabet`. Termin wielowyrazowy musi tworzyć ciąg kolejnych słów, a interpunkcja nie
przerywa dopasowania.

Jeżeli krótsze trafienie w całości zawiera się w dłuższym, wybierane jest trafienie
bardziej szczegółowe. Przykładowo nazwa `Audyt Beta Logistics.docx` może wskazać klienta
`Beta Logistics` zamiast ogólnego słowa kluczowego `Beta`. Rozłączne trafienia kilku
spraw pozostają niejednoznaczne i wymagają decyzji użytkownika. Brak dopasowania nie
blokuje sugestii; sprawę można wskazać ręcznie. Mechanizm nie wykonuje analizy odmiany
fleksyjnej, dlatego wymagane warianty nazw należy dodać jako słowa kluczowe.

## Reguły ewidencji i rozliczania

- Zatwierdzenie wymaga aktywnej sprawy oraz dodatniego czasu nieprzekraczającego
  obowiązującego limitu. Jedna lub kilka scalonych sugestii może wskazywać ten sam wpis.
- Każda ręczna korekta czasu jest rejestrowana jako `TimeEntryAdjustment`, co pozwala
  odtworzyć sposób uzyskania wartości końcowej.
- Zaokrąglenie jest wykonywane po stronie serwera do jednostki z konfiguracji,
  domyślnie 30 minut. Jednostka rozliczeniowa nie jest tym samym co estymacja czasu
  dokumentu i nie wpływa na wynik silnika sesji.
- Cofnięcie zatwierdzenia usuwa aktywny wpis i przywraca jego sugestie. Odrzuconą
  sugestię można przywrócić do czasu jej archiwizacji.
- Rozliczenie przenosi wpis do jednokierunkowego archiwum. Rozliczonego wpisu nie można
  edytować ani cofnąć; ewentualne storno pozostaje funkcją planowaną.
- Sprawy są dezaktywowane zamiast usuwane. Numer pozostaje zajęty, ponieważ identyfikuje
  sprawę w historycznych wpisach.
- Indeksy unikalne i token współbieżności w bazie domykają wyścigi przy synchronizacji,
  zatwierdzaniu oraz edycji danych. Konflikty są zwracane jako odpowiedzi `409`.

## API

Najważniejsze endpointy lokalnego backendu:

| Metoda i ścieżka | Znaczenie |
|---|---|
| `POST /api/sync` | Synchronizuje kalendarz, pliki, wersje i tombstone'y; zwraca raport filtrowania, sesji i zmian |
| `GET /api/suggestions` | Zwraca sugestie filtrowane według statusu i źródła |
| `POST /api/suggestions/merge` | Scala sugestie sesji tego samego dokumentu |
| `POST /api/suggestions/{id}/claim-gap` | Rozdziela wolną lukę między sąsiednie pozycje |
| `POST /api/suggestions/{id}/approve` | Zatwierdza sugestię i tworzy wpis czasu |
| `POST /api/suggestions/{id}/reject` | Odrzuca sugestię bez usuwania historii |
| `POST /api/suggestions/{id}/restore` | Przywraca odrzuconą sugestię |
| `POST /api/suggestions/{id}/archive` | Archiwizuje pojedynczą odrzuconą sugestię |
| `POST /api/suggestions/archive-rejected` | Archiwizuje wszystkie odrzucone sugestie |
| `GET`, `POST`, `PUT /api/cases` | Odczytuje, dodaje i aktualizuje sprawy |
| `POST /api/cases/{id}/activate` lub `deactivate` | Zmienia aktywność sprawy |
| `GET /api/time-entries` | Zwraca aktywne albo zarchiwizowane wpisy czasu |
| `POST /api/time-entries/merge` | Scala wpisy jednej sesji dokumentowej |
| `POST /api/time-entries/{id}/unmerge` | Odtwarza wpisy składowe przed archiwizacją |
| `POST /api/time-entries/{id}/add-gap` lub `subtract-gap` | Włącza albo wyłącza wykrytą przerwę |
| `POST /api/time-entries/{id}/adjust` | Wykonuje ręczną korektę czasu |
| `POST /api/time-entries/{id}/round` | Zaokrągla czas do jednostki rozliczeniowej |
| `POST /api/time-entries/archive` | Archiwizuje wpisy z zakresu dat |
| `POST /api/time-entries/{id}/archive` | Archiwizuje pojedynczy wpis |
| `DELETE /api/time-entries/{id}` | Cofa zatwierdzenie aktywnego wpisu |
| `GET /api/timeline` i `GET /api/timeline/{date}` | Zwraca miesięczne podsumowanie i pozycje wybranego dnia |
| `GET /api/timeline/document-activity` | Zwraca historię obserwacji i sesji dokumentu |
| `GET /api/summary` | Zwraca dane kafelków podsumowania |

Przykładowe żądania znajdują się w
`TimeSuggestions/TimeSuggestions/TimeSuggestions.http`.

## Bezpieczeństwo i ograniczenia prototypu

Treść dokumentów nie jest zapisywana w backendzie ani w SQLite. Porównanie wersji
`.docx` pobiera pliki bezpośrednio z Microsoft Graph, wykonuje analizę w pamięci
przeglądarki i nie zapisuje wyniku w `localStorage`. Backend przechowuje jednak wrażliwe
metadane: nazwy plików, tytuły nieprywatnych spotkań, klientów, numery spraw, czasy
aktywności i opisy wpisów. Baza stanowi zatem indeks pracy kancelarii i wymaga ochrony
na równi z innymi danymi zawodowymi.

Obecna wersja jest lokalnym prototypem dla jednego użytkownika:

- API nie ma własnego uwierzytelniania i nasłuchuje wyłącznie na `localhost:5188`;
- baza `timesuggestions.db` oraz kopie wykonywane przed migracjami nie są szyfrowane
  przez aplikację;
- komunikacja z lokalnym API odbywa się po HTTP, a CORS dopuszcza wyłącznie
  `http://localhost:4200`;
- dane nie są rozdzielane według użytkownika ani tenantów;
- dziennik `DocumentActivity` nie ma automatycznej polityki retencji;
- aplikacja używa jednej strefy biznesowej dla wszystkich danych;
- sugestie dokumentowe zależą od jakości i częstotliwości metadanych publikowanych
  przez Microsoft 365;
- listy sugestii oraz wpisów nie są stronicowane.

Przed wdrożeniem produkcyjnym wymagane są co najmniej: walidacja tokenu Entra dla API,
izolacja danych użytkowników, HTTPS, szyfrowanie danych spoczynkowych, kontrolowana
retencja i kopie zapasowe, audyt dostępu, ograniczanie liczby żądań, paginacja oraz
monitoring bez rejestrowania poufnych metadanych. Token Graph nadal powinien pozostać
wyłącznie po stronie klienta.

## Uruchomienie lokalne

Wymagane są .NET SDK 10, Node.js 20 lub nowszy oraz Angular CLI.

Backend:

```bash
cd TimeSuggestions/TimeSuggestions
dotnet run --launch-profile http
```

Backend działa pod adresem `http://localhost:5188`. Przy starcie automatycznie tworzy
bazę SQLite i wykonuje migracje. Przed każdą oczekującą migracją powstaje kopia
`timesuggestions.db.bak-<nazwa-migracji>`. Jeżeli migracja zostanie przerwana, należy
zatrzymać backend, przywrócić kopię bazy, usunąć odpowiadające jej pliki `-wal` i `-shm`,
jeżeli istnieją, a następnie uruchomić aplikację ponownie.

Frontend:

```bash
cd timesuggestions-web
npm install
npx ng serve
```

Frontend działa pod adresem `http://localhost:4200`. Repozytorium zawiera publiczną
konfigurację rejestracji Entra ID obsługującą konta organizacyjne i osobiste. Aplikacja
prosi o delegowane uprawnienia `Calendars.Read` i `Files.Read`. Jeżeli tenant wymaga
zgody administratora, można utworzyć własną rejestrację typu SPA z adresem przekierowania
`http://localhost:4200` i podmienić `entraClientId` w
`timesuggestions-web/src/environments/environment.ts`.

## Testy

Testy backendu:

```bash
cd TimeSuggestions
dotnet test
```

Testy frontendu:

```bash
cd timesuggestions-web
npm test -- --watch=false
```

Testy nie wymagają połączenia z Microsoft Graph ani interaktywnego logowania.

## Struktura projektu

```text
TimeSuggestions/
  TimeSuggestions/                 API .NET
    Configuration/                 konfiguracja reguł biznesowych
    Contracts/                     DTO, walidacja i raport synchronizacji
    Controllers/                   kontrolery REST
    Data/                          DbContext i obsługa migracji
    Migrations/                    migracje EF Core dla SQLite
    Models/                        encje i typy domenowe
    Services/                      silnik sesji i usługi aplikacyjne
  TimeSuggestions.Tests/           testy xUnit i dane testowe

timesuggestions-web/
  src/app/
    components/                    oś czasu, historia dokumentu i karty sugestii
    pages/                         Sugestie, Wpisy czasu i Sprawy
    models/                        modele API i Microsoft Graph
    pipes/                         formatowanie czasu i polska odmiana liczebników
    services/                      MSAL, Graph, API, synchronizacja i stan interfejsu
```
