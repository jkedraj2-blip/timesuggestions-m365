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

Nad widokami znajduje się podsumowanie liczby oczekujących i zatwierdzonych sugestii,
sumy minut w aktywnych wpisach oraz czasu ostatniej synchronizacji. Kafelek opisany jako
„zapisane wpisy” prezentuje wartość `approvedCount`, czyli liczbę zatwierdzonych sugestii;
po scaleniu kilka sugestii może należeć do jednego wpisu czasu. Zwijana oś czasu
przedstawia oczekujące sugestie, aktywne wpisy i pozycje rozliczone w układzie dziennym.
Dla dokumentów dostępna jest historia obserwowanych wersji. Porównanie treści działa
wyłącznie dla plików `.docx` i jest wykonywane w przeglądarce.

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

Logowanie i pobieranie tokenów obsługuje `@azure/msal-browser` skonfigurowany jako klient
publiczny bez `client secret`. Token Microsoft Graph pozostaje w przeglądarce i nie jest
przekazywany do backendu. Przed dołączeniem nagłówka `Authorization` frontend sprawdza,
czy początkowy adres żądania używa HTTPS i hosta `graph.microsoft.com`. Kontrola obejmuje
również adresy stronicowania `@odata.nextLink` i wskaźnik `@odata.deltaLink` zapisany
w `localStorage`.

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

Obsługiwane są pliki `.docx`, `.doc`, `.xlsx` i `.xls` z wybranego okna synchronizacji.
Frontend ustala pole `lastModifiedByMe` na podstawie danych `lastModifiedBy` i aktywnego
konta MSAL; brak danych autora na dysku osobistym jest traktowany jako modyfikacja
właściciela. Backend wyklucza z budowania sugestii pliki, dla których
`lastModifiedByMe` ma wartość `false`. Aktywności wersji są zapisywane dla wszystkich
plików przekazanych w payloadzie. Dla każdego pliku spełniającego filtr rozszerzenia
i czasu frontend pobiera stronicowaną historię wersji z Microsoft Graph. Jednocześnie
działają najwyżej cztery żądania historii. Błąd dotyczący pojedynczego pliku ustawia jego
pole `versions` na `null`, zwiększa licznik błędów w raporcie i nie przerywa całej
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

Do dziennika trafiają znaczniki pobranych wersji. Jeżeli historia wersji została pobrana,
a bieżący `driveItem.lastModifiedDateTime` wskazuje moment nieobecny na tej liście,
backend zapisuje dodatkową obserwację z identyfikatorem `item`. Dziennik może również
zawierać kilka momentów przypisanych do tego samego identyfikatora wersji zwróconego
przez Graph. Powtórne dane o identycznym pliku, identyfikatorze wersji i momencie są
pomijane przez kontrolę w serwisie oraz indeks unikalny w SQLite.

### Budowanie sesji

Dla każdego pliku aktywności są porządkowane chronologicznie i deduplikowane według
momentu. Następnie silnik dzieli je na sesje zgodnie z konfigurowalnymi progami:

- odstęp do 15 minut włącznie oznacza kontynuację tej samej sesji;
- odstęp powyżej 15 i do 30 minut włącznie pozostaje częścią sesji, ale jest oznaczany
  jako wykryta przerwa, którą użytkownik może wyłączyć z rozliczenia;
- odstęp dłuższy niż 30 minut rozpoczyna nową sesję i nową sugestię;
- zmiana lokalnego dnia zawsze rozpoczyna nową sesję, niezależnie od długości odstępu.

Początkiem sesji jest moment pierwszego zaobserwowanego zapisu, a końcem moment
ostatniego zapisu. Czas brutto jest zaokrągloną do pełnej minuty różnicą między tymi
punktami. Obliczenie jest wykonywane na znacznikach UTC, aby zmiana czasu letniego lub
zimowego nie zafałszowała długości, natomiast data i godziny prezentowane użytkownikowi
są przeliczane na strefę biznesową, domyślnie `Europe/Warsaw`.

Dla sesji z mierzalnym odstępem system nie dodaje czasu przed pierwszą ani po ostatniej
obserwacji. Jeżeli różnica między pierwszym i ostatnim punktem po zaokrągleniu wynosi zero
minut, zasięg kończy się po upływie `MinimumSessionMinutes`, domyślnie 5 minut, a sugestia
otrzymuje tę samą wartość czasu oraz znacznik `NeedsTimeReview`. Interfejs wymaga wtedy
dwukrotnego potwierdzenia wartości domyślnej albo wpisania własnej liczby minut.
Zatwierdzanie hurtowe pomija sugestie ze znacznikiem `NeedsTimeReview`.

Tor awaryjny jest używany, gdy dla pliku nie ma żadnej zapisanej aktywności. Dotyczy to
między innymi pliku z `versions: null`, dla którego baza nie zawiera wcześniejszych
obserwacji. W tym trybie powstaje najwyżej jedna sugestia dla danego pliku i lokalnego
dnia. Otrzymuje ona `MinimumSessionMinutes` i `NeedsTimeReview`. Jeżeli baza zawiera już
aktywności pliku, silnik może zbudować sesje z zapisanych obserwacji także wtedy, gdy
bieżące pobranie historii wersji zakończyło się błędem.

### Znaczenie synchronizacji co 10 minut

Opcjonalne automatyczne sprawdzanie uruchamia synchronizację co 10 minut, gdy użytkownik
jest zalogowany i aplikacja pozostaje otwarta. Funkcja jest domyślnie wyłączona. Jej
włączenie uruchamia synchronizację od razu, a przywrócenie zapisanej preferencji po
ponownym otwarciu aplikacji uruchamia pierwszy przebieg po upływie interwału. Po trzech
kolejnych błędach automat wyłącza się i pokazuje komunikat użytkownikowi.

Dziesięciominutowy interwał jest krótszy od 15-minutowego progu ciągłości sesji. Przebieg
automatyczny odczytuje pełne okno kalendarza i przyrostową deltę OneDrive. Jeżeli delta
zwróci plik i pobranie historii zakończy się powodzeniem, backend dodaje nieznane
dotychczas znaczniki wersji. Dodatkowa obserwacja `item` powstaje, gdy
`driveItem.lastModifiedDateTime` nie występuje wśród momentów zwróconych wersji. Kolejne
przebiegi mogą w ten sposób zagęścić dziennik aktywności.

Nie jest to jednak gwarancja kompletnego pomiaru. Synchronizacja działa tylko przy
otwartej aplikacji i aktywnej sesji Microsoft, a częstotliwość tworzenia wersji oraz
aktualizacji metadanych zależy od Worda, Excela, sposobu zapisu i klienta Office.
Jeżeli źródło nie opublikuje kolejnych znaczników, aplikacja nie ma danych pozwalających
odtworzyć czasu między nimi. Z tego powodu wynik należy traktować jako rekonstrukcję
sesji na podstawie dostępnej telemetrii, wymagającą kontroli użytkownika.

### Dalsza praca, przerwy i nakładanie

Aktywności objęte zatwierdzoną sugestią dokumentową albo oczekującą sugestią poprawioną
przez użytkownika są wyłączane z wejścia silnika sesji. Aktywności spoza tych zakresów
mogą utworzyć kolejne sugestie tego samego pliku. Odrzucone i zarchiwizowane sugestie
pozostają w bazie i uczestniczą w ochronie przed ponownym utworzeniem tej samej sesji.
Numer sesji, na przykład „edycja 3”, jest wyliczany na podstawie chronologii sugestii
danego pliku.

Przerwy od ponad 15 do 30 minut wewnątrz sesji są częścią czasu brutto, dopóki użytkownik
nie wyłączy ich w aktywnym wpisie. Wolną lukę między sąsiednimi pozycjami można doliczyć
do bieżącej sugestii, a także podzielić między dwie oczekujące sugestie. Podział nie może
przekroczyć długości luki, przejść przez lokalną północ ani przekroczyć limitu 480 minut.
Jeżeli po drugiej stronie znajduje się wpis czasu, operacja nie zmienia go z poziomu
sugestii.

Oczekujące sugestie tego samego dokumentu można scalać między sobą, jeżeli pochodzą
z jednego dnia. Ta sama reguła dotyczy scalania aktywnych wpisów między sobą. Serwisy
scalania i obsługi luk sprawdzają, czy nowo zajęty odcinek nie zawiera innej pozycji.
Podczas zatwierdzania koniec zasięgu jest przycinany do późniejszego sąsiada
rozpoczynającego się wewnątrz tego zasięgu. Pokrycie z pozycją, która rozpoczyna się
wcześniej albo dokładnie w tym samym momencie, nie blokuje zatwierdzenia; wpis powstaje
z komunikatem `notice` opisującym nakładanie. Pole `DurationMinutes` pozostaje liczbą
minut do rozliczenia, natomiast `StartedAt` i `EndedAt` określają położenie wpisu na osi
czasu.

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

- Zatwierdzenie wymaga aktywnej sprawy, opisu o długości do 500 znaków oraz czasu od
  1 do 1440 minut. Jeden wpis może być powiązany z kilkoma scalonymi sugestiami.
- Korekty aktywnego wpisu wykonywane przez zmianę minut, przełączanie przerw,
  zaokrąglanie i scalanie z doliczeniem luk są zapisywane jako `TimeEntryAdjustment`.
  Czas podany w formularzu zatwierdzenia jest zapisywany bez osobnego rekordu korekty.
- Zaokrąglenie jest wykonywane po stronie serwera do najbliższej wielokrotności
  `BillingIncrementMinutes`, domyślnie 30 minut. Dokładna połowa jest zaokrąglana w dół,
  a minimalnym wynikiem jest jedna jednostka. Jednostka rozliczeniowa nie wpływa na
  wynik silnika sesji dokumentowych.
- Korekta minut, zaokrąglenie albo przełączenie przerwy aktywnego wpisu nie może
  zwiększyć jego czasu ponad 480 minut. Zmniejszanie wpisu przekraczającego ten próg jest
  dozwolone. Zatwierdzenie przyjmuje do 1440 minut, a operacje scalania sumują czasy
  pozycji zgodnie z własnymi regułami.
- Cofnięcie zatwierdzenia usuwa aktywny wpis i przywraca jego sugestie. Odrzuconą
  sugestię można przywrócić do czasu jej archiwizacji.
- Rozliczenie ustawia `ArchivedAt`. Rozliczonego wpisu nie można korygować, scalać,
  rozdzielać ani usunąć przez operację cofnięcia zatwierdzenia.
- Sprawy są dezaktywowane zamiast usuwane. Numer pozostaje zajęty, ponieważ identyfikuje
  sprawę w historycznych wpisach.
- Unikalne indeksy obejmują kotwicę sugestii, aktywność dokumentu i numer sprawy. Status
  sugestii jest tokenem współbieżności. Konflikty domenowe i przegrane wyścigi są
  zwracane jako odpowiedzi `409` w obsługiwanych ścieżkach.

## API

Najważniejsze endpointy lokalnego backendu:

| Metoda i ścieżka | Znaczenie |
|---|---|
| `POST /api/sync` | Przetwarza dane kalendarza, pliki, wersje i tombstone'y przesłane przez frontend; zwraca raport filtrowania, sesji i zmian |
| `GET /api/suggestions` | Zwraca sugestie filtrowane według statusu i źródła |
| `POST /api/suggestions/merge` | Scala sugestie sesji tego samego dokumentu |
| `POST /api/suggestions/{id}/claim-gap` | Rozdziela wolną lukę między sąsiednie pozycje |
| `POST /api/suggestions/{id}/approve` | Zatwierdza sugestię i tworzy wpis czasu |
| `POST /api/suggestions/{id}/reject` | Odrzuca sugestię bez usuwania historii |
| `POST /api/suggestions/{id}/restore` | Przywraca odrzuconą sugestię |
| `POST /api/suggestions/{id}/archive` | Archiwizuje pojedynczą odrzuconą sugestię |
| `POST /api/suggestions/archive-rejected` | Archiwizuje wszystkie odrzucone sugestie |
| `GET /api/cases` | Zwraca aktywne sprawy albo, z `includeInactive=true`, wszystkie sprawy |
| `POST /api/cases` | Dodaje sprawę z unikalnym numerem |
| `PUT /api/cases/{id}` | Aktualizuje dane sprawy |
| `POST /api/cases/{id}/activate` | Aktywuje sprawę |
| `POST /api/cases/{id}/deactivate` | Dezaktywuje sprawę |
| `GET /api/time-entries` | Zwraca aktywne albo zarchiwizowane wpisy czasu |
| `POST /api/time-entries/merge` | Scala aktywne wpisy tego samego dokumentu i dnia |
| `POST /api/time-entries/{id}/unmerge` | Odtwarza wpisy składowe przed archiwizacją |
| `POST /api/time-entries/{id}/add-gap` lub `subtract-gap` | Włącza albo wyłącza wykrytą przerwę |
| `POST /api/time-entries/{id}/adjust` | Wykonuje ręczną korektę czasu |
| `POST /api/time-entries/{id}/round` | Zaokrągla czas do jednostki rozliczeniowej |
| `POST /api/time-entries/archive` | Archiwizuje wpisy z zakresu dat |
| `POST /api/time-entries/{id}/archive` | Archiwizuje pojedynczy wpis |
| `DELETE /api/time-entries/{id}` | Cofa zatwierdzenie aktywnego wpisu |
| `GET /api/timeline?from=&to=` | Zwraca dzienne liczniki z zakresu nieprzekraczającego 366 dni |
| `GET /api/timeline/{date}` | Zwraca oczekujące sugestie i wpisy wybranego dnia |
| `GET /api/timeline/document-activity?externalId=` | Zwraca historię obserwacji i sesji dokumentu |
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

Implementacja jest lokalnym prototypem dla jednego użytkownika:

- API nie ma własnego uwierzytelniania; profil `http` używany w instrukcji uruchomienia
  nasłuchuje na `http://localhost:5188`;
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

## Uruchomienie lokalne

Wymagane są .NET SDK 10 oraz Node.js w wersji obsługiwanej przez Angular 21:
`^20.19.0`, `^22.12.0` albo `>=24.0.0`. Angular CLI jest zależnością deweloperską
projektu i jest uruchamiany lokalnie przez `npx`.

Backend:

```bash
cd TimeSuggestions/TimeSuggestions
dotnet run --launch-profile http
```

Backend działa pod adresem `http://localhost:5188`. Przy starcie wykonuje migracje EF
Core. Jeżeli plik bazy już istnieje i co najmniej jedna migracja oczekuje na wykonanie,
przed całym przebiegiem powstaje jedna kopia
`timesuggestions.db.bak-<ostatnia-oczekująca-migracja>`. Istniejąca kopia o tej nazwie
nie jest nadpisywana. Przy pierwszym uruchomieniu, gdy plik bazy jeszcze nie istnieje,
kopia nie powstaje. Jeżeli migracja zostanie przerwana, należy zatrzymać backend,
przywrócić kopię bazy, usunąć odpowiadające jej pliki `-wal` i `-shm`, jeżeli istnieją,
a następnie uruchomić aplikację ponownie.

Frontend:

```bash
cd timesuggestions-web
npm install
npx ng serve
```

Frontend działa pod adresem `http://localhost:4200`. Konfiguracja w
`timesuggestions-web/src/environments/environment.ts` zawiera publiczny identyfikator
klienta, authority `https://login.microsoftonline.com/common` oraz adres przekierowania
`http://localhost:4200`. Logowanie żąda delegowanych uprawnień `Calendars.Read` i
`Files.Read`. Dostępność logowania zależy również od ustawień zewnętrznej rejestracji
Entra ID i zasad zgody obowiązujących w danym tenancie.

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
