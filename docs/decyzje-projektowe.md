# Zapisane decyzje projektowe

Każda sekcja opisuje jedną decyzję: co postanowiliśmy, dlaczego i co było wcześniej.
Skrócona tabela z odnośnikami do tych sekcji jest w [README](../README.md).

## Delta query zamiast `/me/drive/recent`

Endpoint "recent" jest oznaczony przez Microsoft jako wycofywany. `GET /me/drive/root/delta`
jest wspierany i zwraca elementy dysku ze zmianami, w tym tombstone'y usuniętych plików
(facet `deleted`), którymi backend czyści oczekujące sugestie.

Filtrowanie (okno synchronizacji, rozszerzenia Word/Excel, autor modyfikacji) odbywa się
po stronie klienta, bo delta nie wspiera `$filter`. Odrzucenia klienckie są przy tym
raportowane licznikami, żeby raport syncu pokazywał prawdę.

Rozważone alternatywy: wyszukiwanie z sortowaniem po dacie (niestabilne wsparcie
`$orderby`) oraz endpointy aktywności (niedostępne dla kont osobistych).
Szczegóły: `graph-files.service.ts`.

## Cache `deltaLink` w localStorage

Pierwszy przebieg delta przechodzi cały dysk (na dużym OneDrive to dziesiątki sekund).
Zapamiętany `deltaLink` sprawia, że kolejne synchronizacje pobierają wyłącznie zmiany.

Link jest zapisywany dopiero po udanym zapisie w backendzie (obejmującym też tombstone'y).
Wygaśnięcie (HTTP 410) czyści cache i wymusza pełny przebieg. Zapisany adres jest
walidowany przed użyciem: podmieniony wskaźnik nie wyśle tokenu pod obcy host.

## Strefa czasowa: `Prefer` + strefa biznesowa

Kalendarz przychodzi w czasie lokalnym (nagłówek `Prefer: outlook.timezone`), dokumenty
w UTC. Backend sprowadza oba źródła do wspólnej strefy biznesowej
(`Suggestions:BusinessTimeZoneId`, ID IANA, domyślnie `Europe/Warsaw`):

- okno dokumentów walidowane na oryginalnym UTC, a `StartedAt`/`EntryDate` i agregacja
  per dzień liczone lokalnie;
- okno kalendarza liczone bezpośrednio w strefie biznesowej; "dzisiaj" w podsumowaniu
  również.

Czas trwania spotkań liczony jest z różnicy instantów UTC, nie lokalnych `DateTime`,
bo w noc zmiany czasu różnica lokalna kłamie o godzinę. Konwencje nocy zmiany czasu są
jawne (`BusinessTime`): czas niejednoznaczny to pierwsze wystąpienie, czas nieistniejący
traktujemy, jakby zegar już przeskoczył (mapowanie monotoniczne, bez ujemnych trwań).

## Odporność na błędy Graph

Wspólny helper obu serwisów Graph: ponowienia dla 429/502/503/504 z odczytem
`Retry-After`, token pobierany per stronę, limit czasu żądania. Kalendarz i delta
podążają za `@odata.nextLink` przez wszystkie strony.

## Filtr prywatności w przeglądarce

Tytuły wydarzeń `private`/`confidential`/`personal` oraz anulowanych w ogóle nie
opuszczają przeglądarki. Backend i tak powtarza swoje filtry (klientowi nie ufa),
a liczniki filtrów klienckich są doliczane do raportu.

## Append-only dziennik aktywności (`DocumentActivity`)

Historia modyfikacji to fakty, nie stan: rekord `(plik, wersja, moment, rozmiar)` nigdy
nie jest modyfikowany ani kasowany przez operacje użytkownika. Scalanie wpisów czasu
i korekty nie zmieniają historii, na której liczone są sesje.

Klucz naturalny obejmuje moment, nie samą wersję (indeks unikalny
`(ExternalId, VersionId, OccurredAt)`), i to jest sedno: Word online przez cały czas
ciągłej pracy dopisuje do tej samej wersji, przesuwając wyłącznie jej
`lastModifiedDateTime`. Przy kluczu `(plik, wersja)` każdy kolejny odczyt tej samej
wersji był odrzucany jako duplikat, więc godziny nieprzerwanej pracy zostawiały jeden
punkt i prośbę o ręczne wpisanie czasu.

Z tego samego powodu poza wersjami zapisujemy próbkę z samego pliku
(`driveItem.lastModifiedDateTime` pod pseudo-wersją `item`): jedyny sygnał o pracy
trwającej wewnątrz niezapieczętowanej wersji. Każda synchronizacja jest przez to
pomiarem: im częściej prawnik synchronizuje w trakcie pracy, tym gęstsza historia.
Próbkę bierzemy tylko, gdy klient faktycznie odpytał o wersje i gdy wnosi moment,
którego nie ma żadna wersja.

Etykieta `current` (część dysków wstawia ją zamiast numeru najnowszej wersji) opisuje
pozycję, nie tożsamość. Razem z momentem jest jednak normalnym faktem i ten sam zapis
widziany raz pod etykietą, raz pod numerem zostaje jednym punktem sesji.

Fakty zapisywane są także dla plików odfiltrowanych z sugestii (dziennik jest źródłem
prawdy, nie pochodną reguł). Błąd pobrania wersji jednego pliku nie wywala syncu:
plik jedzie z `versions=null` i dostaje sugestię fallbackową, a raport liczy błąd.

## Silnik sesji zamiast sztywnych 30 minut

Dla plików z historią wersji czas sugestii liczy się z sesji pracy: przerwa do 15 min
(`SessionContinuationGapMinutes`) to ciągła sesja, 15-30 min (`SessionFlaggedGapMinutes`,
oba progi domknięte "w górę") to jedna sesja z wykrytą przerwą (do przycisku "Odejmij
przerwę"), powyżej 30 min zaczyna się nowa sesja, czyli nowa sugestia.

Rozbieg został usunięty: sesja to dokładnie odcinek od pierwszego do ostatniego zapisu,
bez minut doklejanych z góry. Wcześniej każda sesja o dwóch lub więcej wersjach dostawała
10 minut z założenia "skoro Word zapisał o 22:58, to pisałeś wcześniej". Uzasadniałem to
Wordem desktopowym (pierwszy zapis jako ręczny Ctrl+S po kilkunastu minutach), ale to
uzasadnienie nie broni się faktami: AutoSave jest domyślnie włączony dla plików
w OneDrive/SharePoint także w desktopowym Wordzie z Microsoft 365, a do aplikacji plik
trafia wyłącznie z OneDrive. Scenariusz, dla którego rozbieg miał sens, praktycznie nie
występuje. Przede wszystkim zaś rozbieg mylił się w złą stronę: powiększał rachunek
klienta, łamiąc regułę "gdy zgadujemy, mylimy się na niekorzyść kancelarii, nie klienta",
tę samą, przez którą wyleciał "domyślny czas dokumentu". Czas sprzed pierwszego zapisu
prawnik dopisuje dziś świadomie (Edytuj albo doliczenie wolnej luki).

`MinimumSessionMinutes` (5 min) zastępuje brak pomiaru, nie poprawia pomiaru krótkiego.
Sesja, której zasięg nie wypełnia pełnej minuty (jeden zapis albo kilka w tej samej
minucie), nie mówi nic o długości pracy, więc dostaje minimum i flagę `NeedsTimeReview`:
na karcie "czas do uzupełnienia", czyli prośbę o wpisanie czasu zamiast zatwierdzania
wartości zgadniętej. Sesja zmierzona zostaje przy swojej długości, choćby to były dwie
minuty. Wcześniej minimum dostawała każda sesja krótsza od progu i dwie zmierzone minuty
szły do rozliczenia jako pięć: trzy minuty dopisane klientowi, w dodatku niewidocznie,
bo karta pokazywała obok siebie "początek 16:51", "ostatnia zmiana 16:53" i "czas pracy
5 min", czyli liczby nie do pogodzenia. Ten sam błąd co rozbieg i domyślny czas
dokumentu. Flaga liczy się z zasięgu, nie z liczby wersji: dwa zapisy sekundę po sobie
mówią o czasie pracy dokładnie tyle samo, co jeden.

Sesje nie przechodzą granicy dnia w strefie biznesowej: wersja po północy otwiera nową
sesję niezależnie od progu, bo `EntryDate` jest osią grupowania i archiwizacji. Z tej
samej reguły wynika odmowa doliczania luk przez północ (osobna sekcja niżej). Przerwy
i czas brutto liczone z instantów UTC (noc zmiany czasu), granice dnia lokalnie.

Sesje liczą się z unii dziennika `DocumentActivity` i payloadu: dziennik pamięta wersje
wycięte już z historii OneDrive (retencja). Zaległa wersja starsza niż kotwica scala
sesję "w dół" (sugestia aktualizowana w miejscu, kotwica przesuwana), a wersja mostkująca
dwie sesje scala je w jedną (nadmiarowa oczekująca znika). Wykryte przerwy trzymane są
w kolumnie JSON przy sugestii/wpisie, nie w tabeli: są niemutowalnym atrybutem wyliczonej
sesji, czytanym zawsze razem z pozycją, nigdy nie filtrowanym relacyjnie.

## Koniec z "domyślnym czasem dokumentu"

Parametr `Suggestions:DefaultDocumentDurationMinutes` (30 min) i pole "Domyślny czas
dokumentu" w pasku sugestii zostały usunięte. Była to liczba wzięta znikąd, która
trafiała prosto do rozliczenia klienta: Graph mówi tylko KIEDY plik zmieniono, nie JAK
DŁUGO trwała praca, więc każda wartość w tym polu była zgadywaniem udającym pomiar,
a plakietka "czas domyślny" na karcie zachęcała do zatwierdzania go bez zastanowienia.

Plik bez pobranej historii wersji dostaje dziś `MinimumSessionMinutes` i flagę
`NeedsTimeReview` ("czas do uzupełnienia"), tak samo jak sesja zbudowana z jednego
zapisu. Decyzję o czasie podejmuje człowiek, nie ustawienie.

## Dedup po `(źródło, id z Graph, kotwica sesji)`

Indeks unikalny w bazie plus scalanie z istniejącymi przy synchronizacji (duplikaty
w obrębie jednego żądania też są scalane). Powtórny sync nie tworzy duplikatów,
a odrzucona sugestia nie wraca (status zmieniany, rekord nieusuwany).

Kotwica sesji (`SessionAnchor`) zastąpiła dawny człon "dzień": jeden plik może mieć
wiele sesji (sugestii) tego samego dnia. Kotwica jest stabilna między syncami. Dla
dokumentu z historią wersji to czas pierwszej wersji sesji (sesja rosnąca "od tyłu" nie
zmienia kotwicy), dla dokumentu bez historii początek dnia biznesowego (zachowanie
identyczne z dawnym kluczem), dla kalendarza lokalny początek spotkania (dedup i tak
działa per spotkanie).

## Kolejność sugestii po ostatniej modyfikacji

Lista szła po `StartedAt`, czyli po momencie, w którym prawnik ZACZĄŁ pracę nad plikiem.
Dokument zapisany przed chwilą tkwił więc w środku listy i wyglądał na nieaktualny.
Sugestia niesie dziś `LastActivityAt` (koniec sesji dla dokumentu, koniec spotkania dla
kalendarza, oba na osi UTC) i po nim idzie sortowanie malejące.

Wiersz na karcie jest chronologiczny i podpisany: "początek", potem "ostatnia zmiana",
potem "czas pracy"; trzy gołe liczby obok siebie nie mówiły, co jest czym. Encja trzyma
`LastActivityAt` w UTC (jak kotwica sesji), ale DTO sprowadza ją na oś strefy biznesowej.
Bez tego karta pokazywała "początek 22:58, ostatnia zmiana 20:58", czyli ten sam moment
na dwóch osiach, wyglądający jak koniec przed początkiem. "Ostatnia zmiana" znika, gdy
nie wnosi nic nowego: sesja zbudowana z jednego zapisu zaczyna się dokładnie w momencie
tego zapisu, więc początek i ostatnia modyfikacja to ten sam fakt. Datę dokładamy dopiero
przy zmianie doby.

## Praca po zatwierdzeniu daje nową sugestię

Zatwierdzona sugestia zajmowała kotwicę sesji, więc dalsza praca nad tym samym plikiem
odtwarzała tę samą sesję, trafiała na rozstrzygnięty klucz i przepadała: dokument
edytowany po zatwierdzeniu wpisu po prostu przestawał się pojawiać.

Dziś silnik sesji dostaje wyłącznie aktywność niepokrytą przez przedział
`[SessionAnchor, LastActivityAt]` sugestii zatwierdzonych (i tych poprawionych ręcznie),
więc późniejsze zapisy tworzą osobną sesję i osobną sugestię; prawnik może je potem
połączyć we wpisach czasu. Odrzuconych i zarchiwizowanych to nie dotyczy: one nie mówią
"już rozliczone", tylko "tego nie rozliczam", więc ich lepkość zostaje bez zmian.

## Scalanie sugestii i doliczanie wolnych przerw

Poprawianie czasu zeszło o krok wcześniej, na sugestie, przed zatwierdzeniem. Sesje tego
samego dokumentu z tego samego dnia można scalić w jedną sugestię, a wolną lukę między
sąsiadami doliczyć w całości albo rozdzielić jawnie: prawnik widzi obie liczby minut
(ile tutaj, ile sąsiadowi), zmienia je przed zapisem, a niedobrany czas zostaje wolny.
Połowa jest tylko wartością startową pól, nie decyzją podjętą za użytkownika, bo to on
wie, po której stronie przerwy naprawdę pracował.

Kluczowa reguła: luka jest oferowana tylko wtedy, gdy naprawdę jest wolna na globalnej
osi dnia. Jeśli prawnik w tym czasie pracował nad innym dokumentem, ta "przerwa" jest już
rozliczona w historii tamtego pliku i przycisku nie ma; czas nie może zostać policzony
dwa razy. Luki dłuższe niż `MaxClaimableGapMinutes` (120 min) też nie są oferowane:
kilkugodzinna dziura to nie przerwa w pracy, tylko inna część dnia. Scalanie celowo nie
dolicza luki (to osobna, świadoma decyzja).

Przycisk "Scal w jedną sesję" pojawia się wyłącznie przy fladze `canMerge` liczonej przez
backend (ta sama sugestia oczekująca, ten sam plik, ten sam dzień): przycisk kończący się
odmową "tylko z tego samego dnia" był obietnicą odrzucaną przez tę samą warstwę, która ją
złożyła. Każda taka poprawka ustawia `IsUserAdjusted`: od tej chwili sync nie przelicza
sugestii ani nie odtwarza sesji z jej zasięgu, bo inaczej najbliższa synchronizacja
cofnęłaby decyzję człowieka.

## Luka przez lokalną północ nie jest oferowana ani przyjmowana

Sąsiad na osi dnia bywa znaleziony zza północy, bo luki szukamy o dobę w obie strony
(spotkanie kalendarzowe może przechodzić przez dzień). Doliczenie takiej luki cofałoby
`StartedAt` na inną dobę niż `EntryDate`, a `EntryDate` jest osią grupowania, sum
dziennych, filtra osi czasu i archiwizacji zakresem dat. Dokładnie to wydarzyło się na
realnych danych: sugestia z kotwicą tuż po północy dostała ofertę luki zza północy,
doliczenie przesunęło jej początek na poprzedni dzień, a pozycja pokazywała się w dniu
następnym z godzinami z poprzedniego.

Decyzja jest spójna z silnikiem sesji ("sesje nie przechodzą granicy dnia w strefie
biznesowej"): luka przechodząca przez lokalną północ nie jest oferowana (sąsiad zza
północy nie wystawia luki) ani przyjmowana (próba doliczenia dostaje jawną odmowę po
polsku). Alternatywa (przycięcie luki do północy) wymagałaby przeliczania `EntryDate`
przy każdej zmianie `StartedAt` w obu serwisach operacji; odmowa jest prostsza i nie
zostawia stanów pośrednich. Luka kończąca się równo o północy leży jeszcze w całości
w dobie poprzedniej i pozostaje legalna. Zastane rozjazdy naprawiła jednorazowa migracja
(`EntryDate = date(StartedAt)`).

## Sąsiedztwo sprawdzane na przerwach, nie na całym przedziale scalenia

Scalenie zajmuje dodatkowo tylko przerwy MIĘDZY składowymi: poza nimi wynik nie sięga
dalej niż one same. Wcześniej badaliśmy cały przedział od pierwszego początku do
ostatniego końca i operacja bywała odrzucana z powodu nakładania, które istniało już
wcześniej między składową a obcą pozycją: zaobserwowane 41 sekund zachodzenia dwóch
sesji tego samego pliku blokowało scalenie na zawsze, choć scalenie niczego nie
pogarszało. Do tego `CanMerge` patrzy właśnie na przerwę między sąsiadami, więc UI
proponowało przycisk, po którym przychodziła odmowa "Scalenie blokuje pozycja: ..."
wskazująca ten sam plik. Teraz obie warstwy pytają dokładnie o to samo.

Reguła nie została rozluźniona: obca pozycja stojąca w przerwie nadal blokuje,
a niezmiennik "każda minuta należy do najwyżej jednej pozycji" obowiązuje dalej.
Konsekwencja po stronie rozdzielania: `unmerge` odtwarza składowe z sesji w ich
pierwotnej postaci, więc nakładanie, które istniało przed scaleniem, wraca razem z nimi.
To odtworzenie stanu, nie naprawa danych; operacje na osi dnia bronią się przed
zachodzeniem same (wolna luka przy zachodzeniu nie istnieje).

## Przerwa liczona podłogą, nie zaokrągleniem

Minuta zaokrąglona w górę to minuta, której w przerwie nie ma. Przerwa 7 min 54 s
raportowana jako 8 min i doliczona w całości wysuwała sesję 6 sekund w sąsiada, a takie
nakładanie zostaje w danych na stałe i blokuje kolejne operacje na tej osi, przy czym
przyczyny nie widać w interfejsie, bo różnica gubi się w wyświetlanych minutach.

Zaokrąglenie miało też drugi skutek: zachodzenie krótsze niż pół minuty wychodziło jako
"przerwa zerowa", czyli sprzeczne dane meldowane jako przylegające sesje. Teraz
`FreeGapMinutes` obcina w dół i zwraca `null` przy zachodzeniu; ta sama funkcja po
stronie sugestii i wpisów czasu.

## Operacja na czasie mówi, co zrobiła

Przyciski przy luce niosą liczbę i kierunek ("Dolicz 30 min do tej sesji", "Podziel
przerwę..."), a nie samo "Dolicz całość": z etykiety ma wynikać, co się stanie, zanim się
kliknie. Pełne zdanie o skutku (łącznie z tym, że sąsiad zostaje bez zmian, a scalanie
NIE dolicza przerwy) siedzi w podpowiedzi.

Po operacji leci potwierdzenie: ile minut, w którą stronę, ile poszło sąsiadowi i jaki
jest teraz zakres godzin oraz czas sugestii. Bez tego akcja była cicha i natychmiastowa:
karta znikała przy przeładowaniu listy, a jedynym śladem były przeskakujące liczby, po
których nie dało się poznać, czy kliknęło się to, co się chciało. Minuty własne liczymy
z odpowiedzi serwera (przy "dolicz całość" nikt ich nie podawał, a rozmiar luki i tak
przelicza backend), minuty sąsiada z żądania, bo serwer stosuje dokładnie tę liczbę albo
odmawia. Sama luka nazwana jest "nierozliczoną", nie "wolną": dla prawnika liczy się to,
że ten czas nie trafił jeszcze na żaden rachunek.

## Relacja wiele-sugestii-do-jednego-wpisu

Scalanie sesji dokumentowych łączy wiele sugestii w jeden wpis czasu, więc klucz obcy
przeszedł na stronę sugestii (`Suggestion.TimeEntryId`), a dawny unikalny
`TimeEntries.SuggestionId` zniknął. Ochronę przed podwójnym zatwierdzeniem przejął token
współbieżności na statusie sugestii: przegrany wyścig dostaje jawne 409
(`AlreadyApproved`), a wycofana transakcja nie zostawia drugiego wpisu.

"Cofnij zatwierdzenie" przywraca wszystkie sugestie składowe wpisu. `TimeEntry.Source`
niesie źródło pierwszej sugestii (scalanie międzyźródłowe jest zabronione).

## Godziny na wpisie (`StartedAt`/`EndedAt`)

Wpis zna swoje położenie na osi dnia: to fundament niezmiennika "każda minuta doby
należy do najwyżej jednego wpisu" i osi czasu. Obie wartości są w czasie strefy
biznesowej (świadome odstępstwo od UTC): to ta sama oś co `Suggestion.StartedAt`, doba
z niezmiennika to doba lokalna prawnika, a `EntryDate` to wprost data ze `StartedAt`.
Backfill migracją: początek z najwcześniejszej sugestii wpisu, koniec = początek + czas
trwania.

Przy scalaniu wpisów koniec wyniku to najpóźniejszy koniec składowych, nie koniec
ostatniej po starcie: wpis z podniesionym przy zatwierdzaniu czasem potrafi zawierać
w sobie następny, a wynik kończący się wcześniej niż składowa wypuszczałby jej minuty
z zasięgu.

## Zasięg pozycji to nie to samo co jej czas (`SuggestionSpan`)

Godziny mówią, jaki odcinek doby pozycja zajmuje; `DurationMinutes` mówi, ile z niego
trafia na rachunek. Wpisy miały to rozróżnienie od zawsze (odjęcie przerwy i korekta
±15 min zmieniają czas, godzin nie ruszają), ale sugestie liczyły koniec jako
`StartedAt + DurationMinutes` i po scaleniu to przestawało być prawdą: wynik ma czas
będący sumą sesji, a rozciąga się od pierwszej kotwicy do ostatniej modyfikacji.

Skutek był widoczny w danych użytkownika: zatwierdzony wpis kończył się w godzinie,
w której praca wciąż trwała, więc krótszej ze scalonych sesji po prostu nie było. Nie
było jej w podświetleniu historii wersji, a jej minuty uchodziły za wolne, choć wpis już
je rozliczał. Koniec zasięgu liczy dziś jedna funkcja: późniejsza z granic "koniec czasu"
i "ostatnia modyfikacja" (`LastActivityAt` sprowadzona z UTC na oś biznesową). Używają
jej wszystkie miejsca, które pytają o teren pozycji: zatwierdzenie, rozdzielenie wpisu,
niezmiennik nakładania po obu stronach (sugestie i wpisy), sąsiedztwo przy doliczaniu
przerw i oś czasu.

Przy scalaniu "z przerwami" ma to drugi skutek: luka liczona od końca CZASU poprzedniczki
wciągałaby minuty, które ten czas już zawiera; ten sam kwadrans wchodziłby do rozliczenia
dwa razy. Sesja zwykła niczego nie zmienia, bo trwa dokładnie od pierwszego do ostatniego
zapisu i obie granice są tym samym momentem. Skrócenie czasu przy zatwierdzaniu nie
zwalnia minut sesji, dokładnie jak korekta -15 na wpisie. Różnicę widać w interfejsie:
przy wpisie, którego godziny są dłuższe niż czas, stoi plakietka "N przerw nieliczonych"
z pełnym zdaniem w dymku, bo bez tego godziny i czas obok siebie wyglądają jak błąd
rachunku.

## Nakładanie przycina zasięg, nie odrzuca zatwierdzenia

Zatwierdzenie nie kończy się już odmową "Ten czas nachodzi na istniejący wpis". Powód
zgłoszony z użycia: sugestia 23:53-00:02 dostała odmowę wskazującą wpis (00:07-03:31),
czyli pracę o pięć minut późniejszą. Przyczyną był koniec zasięgu liczony jako
`początek + minuty`, wartość pochodna (czas mógł zostać poprawiony ręcznie albo
zsumowany przy scaleniu), która wychodziła kilkanaście sekund za początek sąsiada,
podczas gdy komunikat pokazywał obie godziny jako 00:07. Decyzja zapadała na sekundach,
a interfejs operuje minutami, więc odmowy nie dało się ani zrozumieć, ani naprawić.

Dziś zasięg jest przycinany do początku najbliższej pozycji, a rozliczany czas zostaje
nietknięty (zasięg to teren, czas to rachunek, patrz `SuggestionSpan`); prawnik dostaje
zdanie o tym, co się stało. W zbiorze sąsiadów uczestniczą wpisy każdego źródła oraz
oczekujące sugestie (rezerwacje minut): ten sam zbiór, którym operują scalanie
i doliczanie luk. Pozycja zaczynająca się PRZED nową i sięgająca za jej początek to
jedyne pokrycie, którego przycięciem nie da się usunąć (dokument zapisany w trakcie
rozliczonego spotkania to realny przypadek): wpis i tak powstaje, a komunikat mówi
wprost, żeby sprawdzić, czy te minuty nie idą na rachunek dwa razy.

Reguła nadrzędna: w rozliczaniu czasu odmowa jest najgorszym wyjściem, bo zostawia
pracę, której nie da się rozliczyć, i nie podpowiada, co zrobić. Analiza reszty blokad
z tej samej rodziny: sąsiedztwo przy scalaniu i doliczaniu przerw liczy dziś
`TimeAxis.Overlaps` z progiem jednej minuty (nakładanie krótsze nie jest nakładaniem,
bo minuta jest jednostką rozliczenia i mniejszej wartości nie da się na ekranie
zobaczyć), a wszystkie komunikaty formatuje `BusinessMoment`, który dokłada sekundy,
gdy są niezerowe. Pozostałe odmowy (archiwum, obcy dokument, inny dzień, przerwa poza
listą) opisują fakty widoczne dla użytkownika i zostają.

## Limit czasu wpisu blokuje tylko wzrost

Zatwierdzenie dopuszcza 1-1440 min, a operacje na wpisie mają limit 480 min
(`MaxDocumentDurationMinutes`). Wpis powyżej 480 min istnieje więc legalnie, a operacja,
która czas ZMNIEJSZA (korekta -15, odjęcie przerwy, zaokrąglenie w dół), nie może być
odrzucana komunikatem "przekroczyłby limit". Sprawdzenie jest dwustronne: limit działa
wyłącznie, gdy nowy czas jest większy od obecnego. Dwie różne granice na tej samej
wielkości pozostają świadomym kompromisem: 1440 przy zatwierdzaniu pozwala rozliczyć
nietypowy dzień, 480 przy operacjach chroni przed rozdmuchaniem wpisu klikaniem.

## Przerwy wpisu liczone z dziennika, z jawnym stanem

Przerwa leżąca w godzinach wpisu ma jeden przełącznik: liczona to "Odejmij", nieliczona
to "Dolicz". Wcześniej istniało wyłącznie odejmowanie przerw zapisanych przy sugestii,
więc po scaleniu sesji przerwa MIĘDZY nimi nie istniała dla aplikacji: prawnik widział ją
w historii wersji, ale nie miał czym jej rozliczyć ani skąd wiedzieć, że nie jest
liczona.

Lista przerw pochodzi dziś z append-only dziennika `DocumentActivity` ograniczonego do
godzin wpisu (`EntryGapService`), a nie z kolumny JSON; dzięki temu działa też dla wpisów
scalonych, zanim ta funkcja powstała (fakty leżały w bazie od początku). Stan domyślny
bierze się z tego samego progu, którym tnie sesje silnik: przerwa do
`SessionFlaggedGapMinutes` leży wewnątrz sesji i jest w czasie brutto (do odjęcia),
dłuższa rozdziela sesje i nie jest rozliczana (do doliczenia). Ostatnia korekta na danym
zakresie jest nadrzędna wobec progu, bo to jawna decyzja człowieka; przełączanie jest
przez to idempotentne bez osobnej reguły ("druga próba" nie ma przycisku, bo widoczny
jest przeciwny).

Doliczane minuty leżą w zasięgu wpisu, więc nie mogą zabrać czasu innej pozycji.
Przerwa, na której wykonano już korektę, zostaje na liście nawet gdy dziennik przestanie
ją odtwarzać (zaległa wersja zmieniająca kształt sesji): inaczej zniknęłaby razem ze
swoim stanem i te same minuty dałoby się odjąć drugi raz. Wpisy kalendarzowe i dane
sprzed dziennika wracają do listy zapisanej przy wpisie.

## Rozliczenie pojedynczego wpisu

Rozliczanie zakresu dat zakłada, że dzień jest zamknięty, a to nieprawda w połowie
sytuacji: część pracy jest gotowa do faktury, część czeka na rozstrzygnięcie przerw albo
na wskazanie sprawy. Prawnik miał więc do wyboru rozliczyć razem z gotowym wpisem coś,
czego jeszcze nie sprawdził, albo nie rozliczyć nic.

Gotowość do faktury jest cechą wpisu, nie dnia, więc przycisk stoi przy wpisie
(`POST /api/time-entries/{id}/archive`), a rozliczanie dnia i zakresów zostaje bez
zmian. Dwustopniowe potwierdzenie jak przy operacjach hurtowych, bo archiwum jest
jednokierunkowe; druga próba na tym samym wpisie to 409, żeby data rozliczenia (wartość
audytowa) nie przesunęła się przy powtórzonym kliknięciu.

## Zaokrąglanie do jednostki rozliczeniowej

Jedno kliknięcie sprowadza czas wpisu do wielokrotności `BillingIncrementMinutes`
(30 min, wartość w konfiguracji, bo jednostka to ustalenie z klientem, nie stała
programu). Do najbliższej wielokrotności, a przy dokładnej połowie w dół: zaokrąglanie
w górę "z automatu" powiększa rachunek klienta, czyli łamie tę samą regułę, przez którą
wyleciał domyślny czas dokumentu. Minimum to jedna jednostka.

Wynik liczy serwer i podaje go w `roundedDurationMinutes`, więc etykieta przycisku
("Zaokrąglij do 1 godz.") nie może obiecać innej liczby, niż zapisze operacja: frontend
nie zna jednostki i nie ma jak się rozjechać. Korekta trafia do dziennika jako osobny
rodzaj (`Rounding`), bo to inna decyzja niż "o tyle a tyle minut", i jest odwracalna
przyciskami ±15. Suma tych korekt wraca w `roundingMinutes` i ma własną plakietkę
("zaokrąglenie -20 min"): wcześniej "nieliczone przerwy" liczyły się z różnicy godziny
minus czas, więc zaokrąglenie w dół wchodziło do tej samej liczby i wpis meldował
przerwy, których w historii wersji nie było i których nie dało się kliknąć. Minuty
nieliczone liczą się dziś z samych przerw (`detectedGaps` ze stanem `counted`), a każda
inna korekta ma swoją nazwę.

## Numer sesji ("edycja 3")

Jeden dokument daje tyle sugestii, ile było sesji pracy, a wszystkie noszą tę samą nazwę
pliku: lista wyglądała jak zduplikowana i nie dało się poznać, o którą pracę chodzi.
Numer jest liczony przy odczycie (`SessionLabelService`), nie zapisywany: w tytule
rozjeżdżałby się przy każdej synchronizacji (tytuł jest nadpisywany ze źródła) i przy
każdym scaleniu (składowe znikają). Porządek daje kotwica sesji, czyli czas pierwszej
wersji.

Numeracja biegnie przez całą historię pliku, nie w obrębie doby: umowa jest pisana
tygodniami, więc "która to część dnia" nie opisuje niczego, co prawnik ma w głowie,
a numerowanie dzienne łamało się dokładnie tam, gdzie kolejność jest najmniej oczywista.
Zaobserwowany przypadek: dwie sesje tego samego wieczoru (23:24 i 23:53) trafiły do
dwóch różnych pul, bo późniejsza ma `EntryDate` z następnego dnia; jedna wyszła
"pierwszą z dwóch", druga została bez numeru.

Numer dostaje każda sesja, licząc od pierwszej: pomijanie plików z jedną sesją znaczyło,
że brak plakietki raz mówi "ten plik ma jedną edycję", a raz "ta pozycja wypadła
z numeracji", czyli dwa różne fakty nie do odróżnienia na ekranie. Liczymy po sugestiach
we wszystkich stanach, nie tylko oczekujących; inaczej zatwierdzenie "edycji 2"
zmieniałoby "edycję 3" w "edycję 2", czyli ten sam numer wskazywałby raz jedną, raz drugą
pracę. Po scaleniu wynik zachowuje numer wcześniejszej ze scalanych sesji (zostaje jej
kotwica), a sesje po niej przesuwają się o jeden, bo realnie jest ich o jedną mniej.

## Korekty jako dziennik (`TimeEntryAdjustment`)

`DurationMinutes` wpisu = suma sesji + suma korekt, przeliczana i zapisywana przy każdej
zmianie: korekty są dziennikiem audytowym, nie stanem liczonym w locie ("skąd wzięła się
ta liczba"). Korekta prawnika jest nadrzędna: sync nigdy nie modyfikuje wpisu z korektami
ani zarchiwizowanego (sync wolno dotykać wyłącznie sugestii `Pending`, jak dotychczas).
Korekty przerw niosą zakres od-do, co czyni odjęcie tej samej przerwy idempotentnym
(druga próba to 409).

Rozdzielenie scalonego wpisu kasuje jego dziennik korekt świadomie: korekty dotyczyły
bytu, który przestaje istnieć, a sesje wracają w pierwotnej postaci. Przycisk "Rozdziel"
ma przez to dwustopniowe potwierdzenie z liczbą korekt do przepadnięcia.

## Odświeżanie oczekujących przy syncu

Zmiana nazwy pliku/tytułu spotkania nie zmienia ID w Graph, więc sam dedup zostawiałby
stary tytuł. Sugestie oczekujące są nadpisywane wartościami ze źródła (z ponownym
dopasowaniem); zatwierdzonych i odrzuconych sync nie dotyka.

## Rekonsyliacja kalendarza

Backend rekonsyliuje kalendarz per spotkanie: przeniesione spotkanie aktualizuje
istniejącą oczekującą sugestię w miejscu (bez "ducha" pod starą datą), odrzucenie jest
"lepkie" per spotkanie (zmiana terminu nie przywraca sugestii), a oczekujące sugestie
spotkań usuniętych lub już nierozliczalnych (anulowane/prywatne/całodniowe) znikają,
a raport pokazuje je w liczniku "usunięte".

Część destrukcyjna działa wyłącznie, gdy frontend zadeklaruje kompletny snapshot
(`calendarSnapshotComplete`, czyli wszystkie strony pobrane bez błędu) wraz z zakresem
dni (`calendarSnapshotDaysBack`), i tylko w przecięciu tego zakresu z oknem backendu;
częściowe pobranie ani rozjazd konfiguracji okien nie skasują prawidłowych sugestii.
Dokumentów to nie dotyczy: delta jest przyrostowa i nieobecność pliku w feedzie niczego
nie dowodzi, więc czyszczą je wyłącznie jawne tombstone'y.

## Współbieżność rozstrzygana w bazie

Wyścigi domykają mechanizmy bazy: token współbieżności na statusie sugestii (równoległe
zatwierdzenie dostaje jawne 409 `AlreadyApproved`) i indeks unikalny `Cases.CaseNumber`
(duplikat numeru sprawy to 409). Dawny indeks `TimeEntries.SuggestionId` zniknął razem
z przeniesieniem klucza obcego na stronę sugestii (patrz relacja
wiele-sugestii-do-jednego-wpisu). Synchronizacja po konflikcie ponawia scalanie na
czystym stanie kontekstu. Na 409 mapowane jest wyłącznie naruszenie unikalności
(SQLite 2067), nie ogólne błędy constraintów.

## Dezaktywacja zamiast usuwania spraw

Wpisy czasu wskazują na sprawy kluczem obcym, więc twarde usunięcie niszczyłoby dane
rozliczeniowe. `IsActive=false` wyłącza sprawę z dopasowania i list wyboru, zachowując
historię. Numer sprawy pozostaje przy niej zajęty także po dezaktywacji, bo identyfikuje
ją w historii rozliczeń; recykling numeru uczyniłby stare wpisy dwuznacznymi. Konflikt
numeru nazywa więc kolidującą sprawę i jej stan, zamiast wskazywać na rekord domyślnie
ukryty przed użytkownikiem.

## Archiwum zamiast usuwania

Rozliczone wpisy (`TimeEntry.ArchivedAt`, znacznik czasu zamiast flagi: darmowy ślad
audytowy "kiedy rozliczono") i schowane odrzucone sugestie (status `Archived`) trafiają
do jednokierunkowego archiwum. Archiwum blokuje edycję: DELETE rozliczonego wpisu
i restore zarchiwizowanej sugestii zwracają 409; rozliczony czas jest niezmienny,
a korekta (storno) to świadomie odłożona przyszła funkcja. Zarchiwizowana sugestia
zostaje w bazie i przy synchronizacji dalej blokuje ponowne utworzenie tej samej pozycji
(anty-nawrót jak przy odrzuceniu). Kafelek w nagłówku liczy wyłącznie nierozliczone
wpisy; archiwizacja jest jedynym "resetem" tej liczby.

## Edycja = zatwierdzenie z poprawionymi wartościami

Jeden endpoint `approve` przyjmuje wartości finalne: mniej ścieżek, ta sama walidacja.

## Diff wersji .docx liczony w przeglądarce

Decyzja prywatnościowa: treść dokumentów nigdy nie przechodzi przez backend. Frontend
pobiera obie wersje bezpośrednio z Graph
(`GET /me/drive/items/{id}/versions/{vId}/content`, mieści się w `Files.Read`),
rozpakowuje ZIP (`fflate`), wyciąga tekst z `word/document.xml` (DOMParser, namespace
WordprocessingML; akapit `w:p` czytany w kolejności dokumentu: `w:t` to tekst,
`w:br`/`w:cr` to złamanie wiersza, `w:tab` to tabulator; samo sklejanie `w:t` gubiło
miękkie entery i zlepiało wyrazy z dwóch linii, przez co "raz była przerwa, raz nie")
i diffuje po liniach (pakiet `diff`, LCS).

Jednostką jest linia, nie akapit: dla użytkownika enter i Shift+Enter to ta sama rzecz,
a Word robi z nich raz nowy `w:p`, raz `w:br`. Przy jednostce "akapit" kilkanaście
widocznych linii lądowało w jednej pozycji listy zmian: jeden przycisk "Pokaż całość" na
cały blok, licznik znaków liczony dla wszystkich linii razem i wielokropek ucinający
w przypadkowym miejscu w środku. Po rozbiciu każda linia ma własny wiersz, własny licznik
i własny przycisk, a dwie linie nigdy nie zlewają się w jeden ciąg. Tylko `.docx`;
`.doc` (binarny) i arkusze Excela dostają chronologię bez diffu, z komunikatem.

Udokumentowany wyjątek od reguły "token tylko do graph.microsoft.com": Graph odpowiada na
`/content` przekierowaniem 302 do domeny pobrań (adres pre-autoryzowany), a przeglądarka
przy przekierowaniu cross-origin sama usuwa nagłówek `Authorization`; walidowany jest
adres początkowy.

Bieżąca wersja jest wyjątkiem adresowym: Graph nie wydaje jej treści spod adresu wersji,
tylko odpowiada 400, bo treścią najnowszej wersji jest po prostu aktualna treść pliku,
więc bierzemy ją z `GET /me/drive/items/{id}/content`. Bez tego porównanie najnowszej
pary (jedynej, na której zwykle zależy) kończyło się komunikatem "Graph 400", podczas gdy
starsze pary działały. Którą pozycję traktować jako bieżącą, mówi backend, i mówi to
o każdym wierszu należącym do najnowszej wersji, nie tylko o ostatnim. Przy ciągłej pracy
Word online nie pieczętuje wersji, tylko przesuwa jej znacznik czasu, więc ten sam numer
trafia do dziennika wiele razy; oznaczanie samego ostatniego wiersza sprawiało, że
wcześniejsze próbki tej wersji dostawały adres wersji i porównanie kończyło się błędem
`400 invalidRequest`, dokładnie na najnowszym fragmencie pracy, czyli tam, gdzie
najbardziej zależy. Dodatkowo rozpoznajemy etykietę `current` w miejscu numeru wersji.

Dwie próbki tej samej wersji nie dostają przycisku "Porównaj"
(`isSameVersionAsPrevious`): OneDrive trzyma jeden snapshot na numer wersji, więc stanu
pośredniego nie ma i nie ma czego z czym zestawić. Zamiast przycisku jest zdanie, które
to tłumaczy i wskazuje godzinę zapisu, przy którym te zmiany widać ("te zmiany są
w »Porównaj z poprzednią« przy zapisie 00:02:51"). Porównanie stoi przy pierwszym wierszu
danej wersji, a jego wynik obejmuje całą pracę w tej wersji, także z późniejszych próbek;
samo "brak zapisanego stanu pośredniego" zostawiało prawnika z wrażeniem, że praca z tych
minut przepadła. Gdy powtórzona wersja otwiera dziennik, mówimy wprost, że porównanie
pojawi się dopiero przy następnej wersji: obiecywanie przycisku, którego nigdzie nie ma,
byłoby odesłaniem donikąd. Z tego samego powodu klucz `track` listy to numer wersji razem
z momentem, bo sam numer nie jest unikalny. Para z wersją bieżącą jest celowo poza cache:
to ruchoma głowica pliku, więc po kolejnym zapisie ten sam klucz oznaczałby inną treść.

Ładowanie wyłącznie po kliknięciu konkretnej pary wersji (każde = 2 pobrania pliku),
ze spinnerem i anulowaniem (`AbortController`); pobieranie idzie przez ten sam helper co
reszta Graph (ponowienia 429/5xx z `Retry-After`, limit czasu, anulowanie), a komunikat
błędu niesie kod z ciała odpowiedzi (`error.code`), bo sam status "400" nie mówi nic.
Wynik cache'owany w pamięci sesji, świadomie nie w localStorage. Brak wersji (404) to
stan normalny (retencja OneDrive jest ograniczona); UI mówi "ta wersja nie jest już
dostępna" i działa dalej.

Prezentacja: +/-/~ obok koloru (nigdy sam kolor), brak różnic tekstowych = "zmiany
dotyczyły formatowania". Serie pustych linii są sklejane w jedną pozycję z liczbą
("(4 puste linie)"): kilka enterów pod rząd to kilka pustych linii, więc lista zmian
dostawała tyle samo wierszy, niosąc jedną informację (zrobiono odstęp). Sklejamy tylko
sąsiednie i tylko tego samego rodzaju (dodany odstęp to co innego niż usunięty),
a pozycji "zmienione" nie ruszamy, bo mówi o treści. Długie linie są przycięte do 300
znaków, ale wielokropek nie zostaje bez wyjaśnienia: przycisk podaje liczbę ukrytych
znaków ("Pokaż całość (420 znaków)"), stoi przy swojej linii i działa w obie strony;
samo "..." wyglądało jak uszkodzone dane i nie dawało się cofnąć. Nowe zależności
frontendu ograniczone do `fflate` i `diff` (małe, audytowalne); backend bez żadnych
zależności do parsowania dokumentów.

## Historia zmian tam, gdzie zapada decyzja

Chronologia modyfikacji (i diff) jest dostępna nie tylko w osi czasu, ale też pod
przyciskiem "Historia zmian" na karcie sugestii i przy wpisie czasu. Decyzja o czasie
albo o odjęciu przerwy wymaga zobaczenia, kiedy dokument faktycznie zapisywano;
odsyłanie po to do innego widoku sprawiało, że prawnik zatwierdzał w ciemno. Ładowane
dopiero po kliknięciu (osobne żądanie na pozycję), jedna historia naraz.

## Historia zmian pokazuje stan wszystkich pozycji pliku

Chronologia pokazuje cały plik, a jeden plik ma zwykle kilka sesji, czyli kilka pozycji,
i to w różnych stanach naraz. Wcześniej stan podawał ten, kto historię otwierał (zakres
i rodzaj przekazywane z karty), więc ta sama historia opowiadała dwie różne rzeczy
zależnie od miejsca otwarcia: w archiwum fragment był oznaczony jako rozliczony,
a z karty sugestii ta sama praca wyglądała, jakby nikt jej nie rozliczył.

Odpowiedź `document-activity` niesie dziś obok wersji listę pozycji (`sessions`: zakres,
stan, numer edycji, przerwy) i każdy wiersz bierze stan od swojej własnej pozycji.
Sugestia zatwierdzona nie jest osobną pozycją, reprezentuje ją wpis, bo inaczej te same
zapisy należałyby na ekranie do dwóch pozycji naraz. Oglądaną pozycję rozpoznajemy po
identyfikatorze (`currentSuggestionId` / `currentEntryId`), nie po zakresie godzin:
zakres bywa przycięty do sąsiada albo zmieniony korektą, a wtedy porównywanie godzin
wskazywałoby raz tę pozycję, raz żadnej. Wyróżnia ją grubszy pasek i plakietka
"ta pozycja", nigdy sam kolor.

Kolor niesie stan: `pending` niebieski, `rejected` czerwony, `archived` szary,
`unsettled` (wpis do rozliczenia) bursztynowy, `settled` (rozliczony) zielony, zgodnie
z konwencją całej aplikacji, gdzie zieleń znaczy "zamknięte", a bursztyn "masz tu coś do
zrobienia". Plakietka pada raz, przy pierwszym wierszu obszaru, i niesie numer edycji
razem ze stanem ("edycja 3, wpis rozliczony"). Sąsiednie elementy listy stykają się, więc
z osobnych wierszy powstaje jeden blok; zaokrąglane są tylko krańce (`opens-session` /
`closes-session`).

Przerwa należy do pozycji wyłącznie wtedy, gdy oba jej końce są w tym samym obszarze;
leżąca między pozycjami dostaje "poza pozycjami", bo nie zmienia niczyjego czasu,
a prawnik mógł w tym czasie robić coś, o czym historia tego pliku nie wie. Przerwy
oglądanej pozycji rodzic podaje osobno (`sessionGaps`): po doliczeniu przerwy lista
wpisów ładuje się od nowa, a otwarta chronologia nie jest pobierana drugi raz, więc
świeższy stan ma rodzic.

## Granica dnia zamiast przerwy na 3797 minut

Odstęp między zapisami z różnych dni nie jest przerwą w pracy: sesja nigdy nie przechodzi
przez północ, więc taki odstęp z definicji nie leży w żadnej pozycji i nie ma przy nim
żadnej decyzji do podjęcia. Dostawał jednak to samo słowo ("przerwa"), ten sam kolor
przestoju i tę samą plakietkę stanu co kwadrans pauzy w pisaniu, w dodatku w surowych
minutach: dwie i pół doby bez dotknięcia pliku czytało się jako "przerwa 3797 min",
czyli liczbę do przeliczenia w głowie.

Dziś w tym miejscu stoi cicha kreska "nowy dzień · 2 dni 15 godz. bez zapisu", bez koloru
i bez plakietki. Odstępy w obrębie dnia zostają przerwami ze stanem, ale liczy je
`formatGapSpan`: poniżej doby ta sama forma co czas pracy ("1 godz. 52 min"), od doby
w górę dni z pominięciem minut, bo przy tej skali minuty są szumem, nie precyzją.

## Przerwa w historii zmian mówi, czy ktoś ją rozlicza

Chronologia pokazywała sam fakt przestoju, a plakietkę dostawała wyłącznie przerwa
z przedziału 15-30 min. Skutek zgłoszony z użycia: przerwa 110-minutowa, czyli ta,
o którą naprawdę chodzi w rozliczeniu, wyglądała jak zwykły odstęp między zapisami,
bo o niej ekran nie mówił nic.

Dziś każdy przestój dłuższy niż próg ciągłości jest wyróżniony kolorem, a wewnątrz
pozycji dostaje stan wprost: "wliczona w czas" albo "nie wliczona w czas", z pełnym
zdaniem w dymku (razem z tym, że we wpisie czasu przełącza się to jednym kliknięciem).
Odstępy krótsze od progu zostają szare i bez opisu: to pauza na myślenie w trakcie
pisania, nie pozycja do rozliczenia. Stan bierze się z listy przerw należącej do pozycji
(`detectedGaps`), bo tylko ona zna decyzje prawnika; próg jest regułą awaryjną, gdy
pozycja swojej listy nie ma. Chronologia i przyciski przy wpisie czytają więc ten sam
stan i nie mogą pokazać dwóch różnych rzeczy. Przerwa leżąca między pozycjami dostaje
"poza pozycjami": nie zmienia niczyjego czasu, a napisanie przy niej "nie wliczona"
byłoby zaproszeniem do doliczenia minut, których żadna pozycja nie obejmuje. Plik bez
pozycji (nic jeszcze nie zatwierdzono, nic nie czeka) wyróżnia przestoje kolorem, ale
o rozliczeniu nie twierdzi nic.

## Układ karty wpisu: akcje pod treścią, nie obok

Operacje wpisu (czas, przerwy, rozdzielenie) stały w kolumnie z prawej strony karty.
Po rozwinięciu historii zmian ta kolumna wisiała w połowie kilkudziesięciowierszowej
listy wersji i zabierała jej szerokość, a to właśnie ta lista jest dowodem, z którego
bierze się decyzja o czasie. Karta układa się dziś w wiersze: fakty, pasek akcji
w podpisanych grupach, historia na pełnej szerokości u dołu. Sama chronologia dostała
stałe kolumny (godzina, data, rozmiar), więc czyta się w pionie, a wyjaśnienie przy
powtórzonej wersji zeszło pod wiersz w skróconej formie, z pełnym zdaniem w dymku:
powtórzone przy każdej próbce otwartej wersji zajmowało więcej miejsca niż cała
chronologia.

## Dlaczego ciągła edycja bywa niewidoczna (i co z tym zrobiono)

Diagnoza: w Wordzie online nowa wersja pliku powstaje dopiero przy zamknięciu karty albo
po kilkunastu minutach bezczynności; autozapis w trakcie pracy dopisuje do wersji już
otwartej. Prawnik piszący dwie godziny bez przerwy nie produkuje więc dwóch godzin
historii, tylko jeden wpis "ostatnia zmiana". Wcześniej dokładał się do tego nasz własny
błąd: dziennik kluczowany po `(plik, wersja)` odrzucał każdy kolejny odczyt tej samej
wersji jako duplikat, więc przesuwający się znacznik czasu przepadał bez śladu.

Naprawa jest po stronie modelu: fakt = `(plik, wersja, moment)` plus próbka
z `driveItem.lastModifiedDateTime` przy każdym syncu (patrz sekcja o dzienniku). Każda
synchronizacja staje się pomiarem, a sesja rośnie razem z pracą zamiast czekać na
zapieczętowanie wersji. Tryb na żywo usunięty: cykliczne odpytywanie nie działało
dokładnie dlatego, że dziennik i tak odrzucał powtórzone obserwacje; bez naprawy modelu
było to odpytywanie o nic. Po naprawie granicą pozostaje to, jak często dane w ogóle się
zmieniają i jak często prawnik synchronizuje.

Odrzucona alternatywa: `getActivitiesByInterval`/`itemActivityStat`. Endpoint jest
w `v1.0` i mieści się w `Files.Read` (nowych zakresów nie trzeba), ale nie działa dla
kont osobistych Microsoft, a nasza rejestracja stoi na `/common` i prywatny OneDrive jest
realnym scenariuszem. Przede wszystkim jednak zwraca liczniki, nie momenty: "drugiego
stycznia były 3 edycje", bez znaczników czasu. Silnik sesji potrzebuje chwil, żeby
wyliczyć początek, koniec i przerwy, więc z agregatów nie odtworzy osi czasu. Do tego
limit 90 dni na zapytanie i zastrzeżenie, że agregaty nie są dostępne dla wszystkich
typów akcji.

## Automatyczne sprawdzanie co 10 minut (przy otwartej karcie)

Każda synchronizacja jest pomiarem: dopisuje do dziennika `DocumentActivity` próbkę
"plik miał tę datę modyfikacji o tej godzinie". Word online nie pieczętuje wersji
w trakcie pisania, więc materiał do liczenia sesji bywa bardzo rzadki, a ręczne klikanie
"Synchronizuj" raz dziennie daje najgorszy możliwy materiał pomiarowy (realnie: nikt tego
nie klika). Stąd `AutoSyncService` z odstępem `AUTO_SYNC_INTERVAL_MINUTES`. Wartość
(10 min) wynika z silnika sesji, nie z "co jakiś czas": musi być gęstsza niż
`SessionContinuationGapMinutes` (15 min), z zapasem na dławienie timerów w karcie w tle
(przeglądarki tną je do około jednego na minutę).

Czego to nie obiecuje: że długa praca zawsze rozbije się na sesje. O tym, kiedy powstaje
ślad, decyduje Word, nie aplikacja. Gęstsze próbkowanie ogranicza dziurę w danych do
kwadransa zamiast całego dnia; to zwiększona szansa na wierny pomiar, nie gwarancja,
i dokładnie tak jest to nazwane w interfejsie. W drugą stronę częściej niż co kilka minut
nie ma sensu, bo przebieg pobiera całe okno kalendarza (`calendarView` nie jest
przyrostowe); pliki idą deltą, a historia wersji tylko dla plików zwróconych przez deltę
jako zmienione, więc cichy przebieg to kilka żądań.

Nie działa przy zamkniętej przeglądarce i nie może: token Graph nigdy nie opuszcza
przeglądarki, więc backend nie ma czym pobrać danych sam z siebie; praca "gdy aplikacja
jest wyłączona" wymagałaby trzymania po stronie serwera tokenu odświeżającego, czyli
dokładnie tego, czego ta architektura unika. Blokada `busy` jest wspólna z przyciskiem
"Synchronizuj": dwa równoległe przebiegi biłyby się o wskaźnik delty OneDrive, który
przesuwa się dopiero po udanym zapisie. Automat jest cichy (powiadamia tylko wtedy, gdy
naprawdę coś przybyło, ubyło albo się zmieniło) i sam się wyłącza po
`AUTO_SYNC_MAX_FAILURES` nieudanych próbach (typowo: wygasła sesja Microsoft), bo cicho
dobijający się do Graph automat jest gorszy od wyłączonego. Startuje dopiero po
zalogowaniu i zatrzymuje się przy wylogowaniu; żyje w korzeniu aplikacji, więc mierzy
także wtedy, gdy otwarta jest zakładka "Wpisy czasu".

Domyślnie wyłączone, ale z jednorazową zachętą. Włączanie fabrycznie odrzucone:
aplikacja sięgająca do Microsoftu co kilka minut przy pozornie bezczynnej karcie to
w kancelarii decyzja użytkownika, a nie ustawienie producenta; każdy przebieg dodatkowo
pisze do bazy i potrafi dorzucić sugestie na ekran, na który ktoś właśnie patrzy.
Zostawienie tego samemu polu wyboru w pasku też jest złe (funkcja, o której nikt się nie
dowie, nie istnieje, a prawnik i tak nie klika "Synchronizuj"), więc po zalogowaniu
pokazuje się jeden panel z rekomendacją i dwoma przyciskami. Odpowiedzią jest każda
z trzech decyzji (włączenie, "Nie teraz" i świadome wyłączenie) i żadna nie wraca
(`AUTO_SYNC_NUDGE_STORAGE_KEY`). Obok pola wyboru stoi nazwany odnośnik "Co to daje?",
nie znak zapytania w kółku: samo "?" nie mówi, czego dotyczy wyjaśnienie, a wyjaśnienie
to cztery akapity, których nie da się zmieścić w podpowiedzi pod kursorem (na dotyku
nieistniejącej).

## Jedno rozgłoszenie zmiany danych

Po synchronizacji i po operacjach na wpisach widoki odświeżają się same
(`DataRefreshService`), bez przeładowania przeglądarki; wcześniej oś czasu i liczniki dni
zostawały na starych danych do F5. Powiadomienie niesie inicjatora: widok, który operację
wykonał i odświeżył się już sam, pomija własny sygnał, więc lista nie ładuje się dwa
razy; sprawdzenie w tle nie ma inicjatora i odświeża wszystkich.

## Raport z synchronizacji

Backend zwraca liczniki: ile pobrano, ile odfiltrowano per reguła, ile zagregowano, jak
dopasowano. Bez tego odfiltrowanie spotkań wygląda dla użytkownika jak zgubione dane.
Przy pominiętych plikach raport podaje też nazwy (kilka pierwszych): delta OneDrive
melduje każdą zmianę na dysku, także w plikach, których użytkownik nigdy nie otwierał
w tej aplikacji, więc samo "pominięto 1 pozycję: plik inny niż Word/Excel" wygląda jak
wzięte z powietrza i nie da się sprawdzić, czy to zdjęcie z kopii zapasowej, czy zgubiony
dokument. Nazwy zbierane są w przeglądarce i nie są wysyłane na backend, bo nie ma po co.

Pozycje spoza okna rozliczenia nie są raportowane w ogóle, ani spotkania, ani dokumenty.
Okno jest zakresem rozliczenia, a nie regułą odrzucającą pracę: poza nim leży cała reszta
kalendarza i cały dysk. Frontend pobiera z Graph z zapasem (przy dokumentach doba ponad
okno; przy kalendarzu ponad dwie doby, bo okno backendu zaczyna się od początku doby
lokalnej, a pobieranie liczy się w godzinach od "teraz"), więc licznik meldował przy
każdej synchronizacji ten sam stały zapas jako "Pominięto N pozycji", u dokumentów
dodatkowo skacząc do kilkunastu po każdym pełnym przebiegu delty. Pozycje spoza okna nie
liczą się też jako pobrane, dzięki czemu nadal zachodzi niezmiennik: pobrano = utworzone
+ zaktualizowane + pominięte (już istniały) + odfiltrowane + zagregowane + zdeduplikowane.
