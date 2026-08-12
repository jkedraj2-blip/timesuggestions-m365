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

## Bezpieczeństwo: co leży w bazie i czym to grozi

Pytanie zadane wprost — czy trzymanie danych z dokumentów w bazie to duże ryzyko wycieku —
zasługuje na uczciwą odpowiedź, a nie na zapewnienie, że „jest bezpiecznie".

**Czego w bazie NIE MA.** Treść dokumentów nigdy nie trafia do backendu. Diff wersji `.docx`
liczy się w przeglądarce: to ona pobiera obie wersje prosto z Graph, rozpakowuje je i porównuje,
a wynik żyje w pamięci karty (świadomie nie w `localStorage`). Backend nie ma ani biblioteki do
parsowania dokumentów, ani żadnej kolumny na ich treść. Nie ma też tokenu Microsoft: token żyje
wyłącznie w przeglądarce, więc z samej bazy nie da się sięgnąć po nic więcej z M365. Tytuły
spotkań oznaczonych jako prywatne, osobiste lub poufne odfiltrowuje przeglądarka — nie
opuszczają jej w ogóle.

**Co w bazie JEST.** Metadane, które same w sobie bywają tajemnicą zawodową:

| Dane | Skąd | Wrażliwość |
| --- | --- | --- |
| Nazwa pliku (`Suggestion.Title`) | OneDrive | wysoka — nazwy dokumentów kancelaryjnych zwykle zawierają nazwisko klienta i sygnaturę |
| Tytuł spotkania | Outlook | wysoka, z wyjątkiem prywatnych/poufnych (odfiltrowanych) |
| Sprawy: nazwa, numer, klient (`Case`) | wpisane ręcznie | wysoka — to wprost rejestr klientów |
| Identyfikator pliku, numery wersji, momenty i rozmiary zapisów (`DocumentActivity`) | Graph | średnia — nie treść, ale pełny dziennik „kto nad czym i kiedy pracował" |
| Wpisy czasu i opisy czynności (`TimeEntry`) | zatwierdzenia użytkownika | wysoka — to materiał rozliczeniowy |

Innymi słowy: baza nie zawiera dokumentów, ale zawiera **indeks pracy kancelarii** — listę
klientów, spraw i godzin. Dla kogoś z zewnątrz bywa to cenniejsze niż pojedyncza umowa.

**Realne ryzyka, od najważniejszego.**

1. **Plik bazy jest niezaszyfrowany.** `timesuggestions.db` to zwykły plik SQLite obok
   binarki; skopiowanie go (skradziony laptop, backup do chmury, dowolny proces
   użytkownika) daje pełny rejestr klientów bez żadnego hasła. Do tego przed każdą
   migracją powstaje kopia `.bak` — czyli kolejne kopie tych samych danych. Kopie są
   wyłączone z gita, ale nikt ich nie kasuje.
2. **API nie ma uwierzytelniania.** Każdy proces na tej maszynie może czytać i pisać przez
   port 5188. Ogranicznikiem jest wyłącznie to, że Kestrel nasłuchuje na `localhost`,
   a `AllowedHosts` przyjmuje tylko nagłówki lokalne (to drugie chroni przed DNS rebinding,
   czyli przed stroną w przeglądarce próbującą dobić się do lokalnego API).
3. **CORS wpuszcza tylko `http://localhost:4200`** — celowo nie `AllowAnyOrigin`. To ochrona
   przed cudzą stroną w tej samej przeglądarce, nie przed procesem na maszynie.
4. **Cały ruch idzie po HTTP.** Na localhoście nie opuszcza pętli zwrotnej; w sieci byłoby
   nie do przyjęcia.

**Co trzeba zrobić przed produkcją** (świadomie poza zakresem prototypu): walidacja tokenu
Entra dla własnego API i rozdzielenie danych per użytkownik, szyfrowanie spoczynkowe
(BitLocker/FileVault na dysku albo SQLCipher na samej bazie), HTTPS, retencja i kasowanie
dziennika `DocumentActivity` oraz kopii migracyjnych, a przy pracy zespołowej — rozdział ról
i audyt dostępu. Dopóki aplikacja stoi na jednym laptopie prawnika, ryzyko jest
porównywalne z ryzykiem samych plików w OneDrive na tym samym dysku; w momencie postawienia
jej na serwerze zmienia się jakościowo i punkty 1–2 przestają być akceptowalne.

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
- **Gęstość historii wersji zależy od klienta Worda (pomiar etapu 0 silnika sesji).**
  Synchronizacja dociąga `GET /me/drive/items/{id}/versions` dla plików z okna
  i zapisuje surowe fakty w append-only dzienniku `DocumentActivity`; raport syncu
  pokazuje, ile plików wróciło z historią, ile bez i ile pobrań padło. Word Online
  tworzy wersje często (autozapis co chwilę), ale **Word desktop potrafi oddać
  1–2 wersje na godzinę pracy** — przy takiej gęstości progi przerw (15/30 min)
  i przycisk „Odejmij przerwę" nie mają paliwa. Procedura pomiaru: godzina realnej
  pracy w Wordzie desktop i osobno w Word Online, sync, odczyt liczników `versions`
  z raportu. Wynik pomiaru należy dopisać tutaj przed oparciem UI o progi przerw;
  do tego czasu funkcje sesji traktują rzadką historię jako jedną ciągłą sesję
  (mechanika degraduje się łagodnie, nie błędnie).
- **Jedna strefa biznesowa.** Czasy obu źródeł są sprowadzane do skonfigurowanej strefy
  (`Suggestions:BusinessTimeZoneId`, domyślnie `Europe/Warsaw`), bez obsługi wielu
  stref per użytkownik.

## Widoki aplikacji

| Widok | Rola |
|---|---|
| **Sugestie** | Karty propozycji z akcjami Zatwierdź / Edytuj / Odrzuć, dopasowana sprawa w podpisanej linii z numerem i klientem (kandydaci przy niejednoznacznym dopasowaniu z numerami w nawiasach), filtr źródła i statusu (oczekujące / odrzucone / zarchiwizowane), przycisk "Zatwierdź wszystkie dopasowane", raport synchronizacji (co pobrano, co odfiltrowano i dlaczego, co zaktualizowano), wskaźnik postępu, przywracanie odrzuconych, archiwizacja odrzuconych (hurtowo i pojedynczo), scalanie sesji tego samego dokumentu i doliczanie wolnych przerw (całość albo jawny podział z sąsiadem — obie liczby minut widoczne i edytowalne przed zapisem), historia zmian pod przyciskiem na karcie, zakres synchronizacji (7/14/30 dni; szerszy przydaje się np. po urlopie), przełącznik automatycznego sprawdzania co 10 minut razem ze statusem („sprawdzam…" / „ostatnio 14:20" / powód niepowodzenia) i rozwijanym wyjaśnieniem pod znakiem zapytania (skąd biorą się liczby, czym jest „jeden wpis", czego mechanizm NIE gwarantuje i że działa tylko przy otwartej karcie) — cztery akapity nie mieszczą się w podpowiedzi pod kursorem, której na dotyku i tak nie ma. Lista idzie od **ostatniej modyfikacji**, nie od początku sesji |
| **Wpisy czasu** | Zapisane wpisy pogrupowane po dniach z sumami i pochodzeniem (z jakiego spotkania/pliku powstały); przy każdym wpisie nazwa sprawy z numerem i klientem (numer to identyfikator używany na fakturze); przełącznik widoków Aktywne / Archiwum; "Cofnij zatwierdzenie" usuwa aktywny wpis i przywraca sugestię; rozliczanie **pojedynczego wpisu** oraz okresowe (dzień, ostatni tydzień, bieżący miesiąc, wszystko), zawsze z dwustopniowym potwierdzeniem, przenosi wpisy do archiwum |
| **Sprawy** | Zarządzanie sprawami: dodawanie, edycja (w tym słów kluczowych sterujących dopasowaniem), dezaktywacja (celowo bez twardego usuwania); wyjaśnienie zasady dopasowania |

Pod kafelkami podsumowania znajduje się **globalna, zwijana oś czasu** (widoczna z każdej
zakładki, domyślnie zwinięta; stan zwinięcia i wybrany miesiąc w localStorage per konto,
jak `deltaLink`): pasek miesiąca `← 01.08–31.08.2026 →` z komórkami dni (dzień miesiąca, dwuliterowy
skrót dnia tygodnia z Intl, badge z liczbą pozycji; miesiąc i rok stoją w nagłówku,
a kolumny są `minmax(0, 1fr)` — pełne 31 kolumn mieści się bez nachodzenia etykiet
na siebie, pełna nazwa dnia zostaje w etykiecie dostępności; dni bez pozycji wygaszone i nieklikalne, weekend i dziś
wyróżnione tokenami CSS we wszystkich trzech motywach), a po kliknięciu w dzień —
pionowa lista pozycji (godziny od–do, tytuł, sprawa z numerem i klientem, czas, status
kolorem **i** etykietą: `sugestia` / `do rozliczenia` / `rozliczone`; rozliczone
wyszarzone i nieklikalne). Klik w pozycję nierozliczoną przenosi do właściwej zakładki
i przewija do elementu z chwilowym podświetleniem (wspólny util `scroll-highlight`,
wyciągnięty z formularza edycji sprawy); jeśli element nie jest widoczny — toast
z wyjaśnieniem, nie cisza. Pasek i lista są obsługiwane klawiaturą (`aria-label`
z pełną datą i liczbą pozycji).

Nad zakładkami znajdują się kafelki podsumowania: oczekujące sugestie, zapisane wpisy,
nierozliczony czas (tylko aktywne wpisy; archiwizacja zdejmuje godziny z kafelka),
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
| **Append-only dziennik aktywności (`DocumentActivity`)** | Historia modyfikacji to fakty, nie stan: rekord `(plik, wersja, moment, rozmiar)` nigdy nie jest modyfikowany ani kasowany przez operacje użytkownika — scalanie wpisów czasu i korekty nie zmieniają historii, na której liczone są sesje. Klucz naturalny obejmuje **moment**, nie samą wersję (indeks unikalny `(ExternalId, VersionId, OccurredAt)`), i to jest sedno: Word online przez cały czas ciągłej pracy dopisuje do TEJ SAMEJ wersji, przesuwając wyłącznie jej `lastModifiedDateTime`. Przy kluczu `(plik, wersja)` każdy kolejny odczyt tej samej wersji był odrzucany jako duplikat, więc godziny nieprzerwanej pracy zostawiały **jeden punkt** i prośbę o ręczne wpisanie czasu. Z tego samego powodu poza wersjami zapisujemy **próbkę z samego pliku** (`driveItem.lastModifiedDateTime` pod pseudo-wersją `item`) — jedyny sygnał o pracy trwającej wewnątrz niezapieczętowanej wersji. Każda synchronizacja jest przez to **pomiarem**: im częściej prawnik synchronizuje w trakcie pracy, tym gęstsza historia. Próbkę bierzemy tylko, gdy klient faktycznie odpytał o wersje i gdy wnosi moment, którego nie ma żadna wersja. Etykieta `current` (część dysków wstawia ją zamiast numeru najnowszej wersji) opisuje POZYCJĘ, nie tożsamość — razem z momentem jest jednak normalnym faktem i ten sam zapis widziany raz pod etykietą, raz pod numerem zostaje jednym punktem sesji. Fakty zapisywane są także dla plików odfiltrowanych z sugestii (dziennik jest źródłem prawdy, nie pochodną reguł). Błąd pobrania wersji jednego pliku nie wywala syncu: plik jedzie z `versions=null` i dostaje sugestię fallbackową, a raport liczy błąd. |
| **Silnik sesji zamiast sztywnych 30 minut** | Dla plików z historią wersji czas sugestii liczy się z sesji pracy: przerwa ≤ 15 min (`SessionContinuationGapMinutes`) to ciągła sesja, 15–30 min (`SessionFlaggedGapMinutes`, oba progi domknięte "w górę") to jedna sesja z **wykrytą przerwą** (do przycisku „Odejmij przerwę"), powyżej 30 min nowa sesja → nowa sugestia. **Rozbieg został USUNIĘTY** — sesja to dokładnie odcinek od pierwszego do ostatniego zapisu, bez minut doklejanych z góry. Wcześniej każda sesja o dwóch lub więcej wersjach dostawała 10 minut z założenia „skoro Word zapisał o 22:58, to pisałeś wcześniej". Uzasadniałem to Wordem desktopowym (pierwszy zapis jako ręczny Ctrl+S po kilkunastu minutach), ale to uzasadnienie nie broni się faktami: **AutoSave jest domyślnie włączony dla plików w OneDrive/SharePoint także w desktopowym Wordzie z Microsoft 365**, a do aplikacji plik trafia wyłącznie z OneDrive. Scenariusz, dla którego rozbieg miał sens, praktycznie nie występuje. Przede wszystkim zaś rozbieg mylił się w ZŁĄ stronę — powiększał rachunek klienta, łamiąc regułę „gdy zgadujemy, mylimy się na niekorzyść kancelarii, nie klienta", tę samą, przez którą wyleciał „domyślny czas dokumentu". Czas sprzed pierwszego zapisu prawnik dopisuje dziś świadomie (Edytuj albo doliczenie wolnej luki). **`MinimumSessionMinutes` (5 min) zastępuje BRAK pomiaru, nie poprawia pomiaru krótkiego.** Sesja, której zasięg nie wypełnia pełnej minuty (jeden zapis albo kilka w tej samej minucie), nie mówi nic o długości pracy, więc dostaje minimum i flagę `NeedsTimeReview` — na karcie **„czas do uzupełnienia"**, czyli prośbę o wpisanie czasu zamiast zatwierdzania wartości zgadniętej. Sesja ZMIERZONA zostaje przy swojej długości, choćby to były dwie minuty. Wcześniej minimum dostawała każda sesja krótsza od progu i dwie zmierzone minuty szły do rozliczenia jako pięć: trzy minuty dopisane klientowi, w dodatku niewidocznie, bo karta pokazywała obok siebie „początek 16:51", „ostatnia zmiana 16:53" i „czas pracy 5 min" — liczby nie do pogodzenia. Ten sam błąd co rozbieg i domyślny czas dokumentu. Flaga liczy się z zasięgu, nie z liczby wersji: dwa zapisy sekundę po sobie mówią o czasie pracy dokładnie tyle samo, co jeden. Zasada: gdy zgadujemy, mylimy się na niekorzyść kancelarii, nie klienta. **Sesje nie przechodzą granicy dnia w strefie biznesowej** — wersja po północy otwiera nową sesję niezależnie od progu, bo `EntryDate` jest osią grupowania i archiwizacji. Przerwy i czas brutto liczone z instantów UTC (noc zmiany czasu), granice dnia lokalnie. Sesje liczą się z **unii dziennika `DocumentActivity` i payloadu** — dziennik pamięta wersje wycięte już z historii OneDrive (retencja). Zaległa wersja starsza niż kotwica scala sesję „w dół" (sugestia aktualizowana w miejscu, kotwica przesuwana), a wersja mostkująca dwie sesje scala je w jedną (nadmiarowa oczekująca znika). Wykryte przerwy trzymane w kolumnie JSON przy sugestii/wpisie, nie w tabeli — są niemutowalnym atrybutem wyliczonej sesji, czytanym zawsze razem z pozycją, nigdy nie filtrowanym relacyjnie. |
| **Koniec z „domyślnym czasem dokumentu"** | Parametr `Suggestions:DefaultDocumentDurationMinutes` (30 min) i pole „Domyślny czas dokumentu" w pasku sugestii **usunięte**. Była to liczba wzięta znikąd, która trafiała prosto do rozliczenia klienta: Graph mówi tylko *kiedy* plik zmieniono, nie *jak długo* trwała praca, więc każda wartość w tym polu była zgadywaniem udającym pomiar — a plakietka „czas domyślny" na karcie zachęcała do zatwierdzania go bez zastanowienia. Plik bez pobranej historii wersji dostaje dziś `MinimumSessionMinutes` i flagę `NeedsTimeReview` („czas do uzupełnienia"), tak samo jak sesja zbudowana z jednego zapisu. Decyzję o czasie podejmuje człowiek, nie ustawienie. |
| **Dedup po `(źródło, id z Graph, kotwica sesji)`** | Indeks unikalny w bazie + scalanie z istniejącymi przy synchronizacji (duplikaty w obrębie jednego żądania też są scalane). Powtórny sync nie tworzy duplikatów, a **odrzucona sugestia nie wraca** (status zmieniany, rekord nieusuwany). Kotwica sesji (`SessionAnchor`) zastąpiła dawny człon „dzień": jeden plik może mieć **wiele sesji (sugestii) tego samego dnia**. Kotwica jest stabilna między syncami — dla dokumentu z historią wersji to czas pierwszej wersji sesji (sesja rosnąca „od tyłu" nie zmienia kotwicy), dla dokumentu bez historii początek dnia biznesowego (zachowanie identyczne z dawnym kluczem), dla kalendarza lokalny początek spotkania (dedup i tak działa per spotkanie). |
| **Kolejność sugestii po ostatniej modyfikacji** | Lista szła po `StartedAt`, czyli po momencie, w którym prawnik ZACZĄŁ pracę nad plikiem — dokument zapisany przed chwilą tkwił więc w środku listy i wyglądał na nieaktualny. Sugestia niesie dziś `LastActivityAt` (koniec sesji dla dokumentu, koniec spotkania dla kalendarza, oba na osi UTC) i po nim idzie sortowanie malejące. Wiersz na karcie jest chronologiczny i **podpisany**: „początek" → „ostatnia zmiana" → „czas pracy"; trzy gołe liczby obok siebie nie mówiły, co jest czym. Encja trzyma `LastActivityAt` w UTC (jak kotwica sesji), ale **DTO sprowadza ją na oś strefy biznesowej** — bez tego karta pokazywała „początek 22:58, ostatnia zmiana 20:58", czyli ten sam moment na dwóch osiach, wyglądający jak koniec przed początkiem. „Ostatnia zmiana" znika, gdy nie wnosi nic nowego: sesja zbudowana z jednego zapisu zaczyna się dokładnie w momencie tego zapisu, więc początek i ostatnia modyfikacja to ten sam fakt. Datę dokładamy dopiero przy zmianie doby. |
| **Praca po zatwierdzeniu daje NOWĄ sugestię** | Zatwierdzona sugestia zajmowała kotwicę sesji, więc dalsza praca nad tym samym plikiem odtwarzała tę samą sesję, trafiała na rozstrzygnięty klucz i **przepadała** — dokument edytowany po zatwierdzeniu wpisu po prostu przestawał się pojawiać. Dziś silnik sesji dostaje wyłącznie aktywność NIEPOKRYTĄ przez przedział `[SessionAnchor, LastActivityAt]` sugestii zatwierdzonych (i tych poprawionych ręcznie), więc późniejsze zapisy tworzą osobną sesję i osobną sugestię; prawnik może je potem połączyć we wpisach czasu. Odrzuconych i zarchiwizowanych to nie dotyczy: one nie mówią „już rozliczone", tylko „tego nie rozliczam", więc ich lepkość zostaje bez zmian. |
| **Scalanie sugestii i doliczanie WOLNYCH przerw** | Poprawianie czasu zeszło o krok wcześniej — na sugestie, przed zatwierdzeniem. Sesje tego samego dokumentu z tego samego dnia można scalić w jedną sugestię, a wolną lukę między sąsiadami doliczyć w całości albo **rozdzielić jawnie**: prawnik widzi obie liczby minut (ile tutaj, ile sąsiadowi), zmienia je przed zapisem, a niedobrany czas zostaje wolny. Połowa jest tylko wartością startową pól — nie decyzją podjętą za użytkownika, bo to on wie, po której stronie przerwy naprawdę pracował. Kluczowa reguła: luka jest oferowana **tylko wtedy, gdy naprawdę jest wolna** na globalnej osi dnia. Jeśli prawnik w tym czasie pracował nad innym dokumentem, ta „przerwa" jest już rozliczona w historii tamtego pliku i przycisku nie ma — czas nie może zostać policzony dwa razy. Luki dłuższe niż `MaxClaimableGapMinutes` (120 min) też nie są oferowane: kilkugodzinna dziura to nie przerwa w pracy, tylko inna część dnia. Scalanie celowo **nie dolicza** luki (to osobna, świadoma decyzja). Przycisk „Scal w jedną sesję" pojawia się wyłącznie przy fladze `canMerge` liczonej przez backend (ta sama sugestia oczekująca, ten sam plik, **ten sam dzień**): sąsiad bywa znaleziony zza północy, bo luki szukamy o dobę w obie strony, a przycisk kończący się odmową „tylko z tego samego dnia" był obietnicą odrzucaną przez tę samą warstwę, która ją złożyła. Każda taka poprawka ustawia `IsUserAdjusted`: od tej chwili sync nie przelicza sugestii ani nie odtwarza sesji z jej zasięgu, bo inaczej najbliższa synchronizacja cofnęłaby decyzję człowieka. |
| **Sąsiedztwo sprawdzane na PRZERWACH, nie na całym przedziale scalenia** | Scalenie zajmuje dodatkowo tylko przerwy **między** składowymi — poza nimi wynik nie sięga dalej niż one same. Wcześniej badaliśmy cały przedział od pierwszego początku do ostatniego końca i operacja bywała odrzucana z powodu nakładania, które **istniało już wcześniej** między składową a obcą pozycją: zaobserwowane 41 sekund zachodzenia dwóch sesji tego samego pliku blokowało scalenie **na zawsze**, choć scalenie niczego nie pogarszało. Do tego `CanMerge` patrzy właśnie na przerwę między sąsiadami, więc UI proponowało przycisk, po którym przychodziła odmowa „Scalenie blokuje pozycja: …" wskazująca **ten sam plik**. Teraz obie warstwy pytają dokładnie o to samo. Reguła nie została rozluźniona: obca pozycja stojąca w przerwie nadal blokuje, a niezmiennik „każda minuta należy do najwyżej jednej pozycji" obowiązuje dalej. |
| **Przerwa liczona PODŁOGĄ, nie zaokrągleniem** | Minuta zaokrąglona w górę to minuta, której w przerwie nie ma. Przerwa 7 min 54 s raportowana jako 8 min i doliczona w całości wysuwała sesję **6 sekund w sąsiada** — a takie nakładanie zostaje w danych na stałe i blokuje kolejne operacje na tej osi, przy czym przyczyny nie widać w interfejsie, bo różnica gubi się w wyświetlanych minutach. Zaokrąglenie miało też drugi skutek: zachodzenie krótsze niż pół minuty wychodziło jako „przerwa zerowa", czyli sprzeczne dane meldowane jako przylegające sesje. Teraz `FreeGapMinutes` obcina w dół i zwraca `null` przy zachodzeniu — ta sama funkcja po stronie sugestii i wpisów czasu. |
| **Operacja na czasie mówi, co zrobiła** | Przyciski przy luce niosą liczbę i kierunek („Dolicz 30 min do tej sesji", „Podziel przerwę…"), a nie samo „Dolicz całość" — z etykiety ma wynikać, co się stanie, zanim się kliknie; pełne zdanie o skutku (łącznie z tym, że sąsiad zostaje bez zmian, a scalanie **nie** dolicza przerwy) siedzi w podpowiedzi. Po operacji leci potwierdzenie: ile minut, w którą stronę, ile poszło sąsiadowi i **jaki jest teraz zakres godzin oraz czas sugestii**. Bez tego akcja była cicha i natychmiastowa — karta znikała przy przeładowaniu listy, a jedynym śladem były przeskakujące liczby, po których nie dało się poznać, czy kliknęło się to, co się chciało. Minuty własne liczymy z **odpowiedzi serwera** (przy „dolicz całość" nikt ich nie podawał, a rozmiar luki i tak przelicza backend), minuty sąsiada — z żądania, bo serwer stosuje dokładnie tę liczbę albo odmawia. Sama luka nazwana jest „nierozliczoną", nie „wolną": dla prawnika liczy się to, że ten czas nie trafił jeszcze na żaden rachunek. |
| **Relacja wiele-sugestii-do-jednego-wpisu** | Scalanie sesji dokumentowych łączy wiele sugestii w jeden wpis czasu, więc klucz obcy przeszedł na stronę sugestii (`Suggestion.TimeEntryId`), a dawny unikalny `TimeEntries.SuggestionId` zniknął. Ochronę przed podwójnym zatwierdzeniem przejął **token współbieżności na statusie sugestii**: przegrany wyścig dostaje jawne 409 (`AlreadyApproved`), a wycofana transakcja nie zostawia drugiego wpisu. „Cofnij zatwierdzenie" przywraca **wszystkie** sugestie składowe wpisu. `TimeEntry.Source` niesie źródło pierwszej sugestii (scalanie międzyźródłowe jest zabronione). |
| **Godziny na wpisie (`StartedAt`/`EndedAt`)** | Wpis zna swoje położenie na osi dnia — fundament niezmiennika „każda minuta doby należy do najwyżej jednego wpisu" i osi czasu. Obie wartości w **czasie strefy biznesowej** (świadome odstępstwo od UTC): to ta sama oś co `Suggestion.StartedAt`, doba z niezmiennika to doba lokalna prawnika, a `EntryDate` to wprost data ze `StartedAt`. Backfill migracją: początek z najwcześniejszej sugestii wpisu, koniec = początek + czas trwania. |
| **Zasięg pozycji to nie to samo co jej czas (`SuggestionSpan`)** | Godziny mówią, JAKI ODCINEK doby pozycja zajmuje; `DurationMinutes` mówi, ILE z niego trafia na rachunek. Wpisy miały to rozróżnienie od zawsze (odjęcie przerwy i korekta ±15 min zmieniają czas, godzin nie ruszają), ale sugestie liczyły koniec jako `StartedAt + DurationMinutes` — i po **scaleniu** to przestawało być prawdą: wynik ma czas będący SUMĄ sesji, a rozciąga się od pierwszej kotwicy do ostatniej modyfikacji. Skutek był widoczny w danych użytkownika: zatwierdzony wpis kończył się w godzinie, w której praca wciąż trwała, więc **krótszej ze scalonych sesji po prostu nie było** — nie było jej w podświetleniu historii wersji, a jej minuty uchodziły za wolne, choć wpis już je rozliczał. Koniec zasięgu liczy dziś jedna funkcja: późniejsza z granic „koniec czasu" i „ostatnia modyfikacja" (`LastActivityAt` sprowadzona z UTC na oś biznesową). Używają jej wszystkie miejsca, które pytają o teren pozycji: zatwierdzenie, rozdzielenie wpisu, niezmiennik nakładania po obu stronach (sugestie i wpisy), sąsiedztwo przy doliczaniu przerw i oś czasu. Przy scalaniu „z przerwami" ma to drugi skutek: luka liczona od końca **czasu** poprzedniczki wciągałaby minuty, które ten czas już zawiera — ten sam kwadrans wchodziłby do rozliczenia dwa razy. Sesja zwykła niczego nie zmienia, bo trwa dokładnie od pierwszego do ostatniego zapisu i obie granice są tym samym momentem. Skrócenie czasu przy zatwierdzaniu **nie zwalnia** minut sesji — dokładnie jak korekta −15 na wpisie. Różnicę widać w interfejsie: przy wpisie, którego godziny są dłuższe niż czas, stoi plakietka „N przerw nieliczonych" z pełnym zdaniem w dymku, bo bez tego godziny i czas obok siebie wyglądają jak błąd rachunku. |
| **Nakładanie PRZYCINA zasięg, nie odrzuca zatwierdzenia** | Zatwierdzenie nie kończy się już odmową „Ten czas nachodzi na istniejący wpis". Powód zgłoszony z użycia: sugestia 23:53–00:02 dostała odmowę wskazującą wpis (00:07–03:31), czyli pracę o pięć minut późniejszą. Przyczyną był koniec zasięgu liczony jako `początek + minuty` — wartość POCHODNA (czas mógł zostać poprawiony ręcznie albo zsumowany przy scaleniu), która wychodziła kilkanaście SEKUND za początek sąsiada, podczas gdy komunikat pokazywał obie godziny jako 00:07. Decyzja zapadała na sekundach, a interfejs operuje minutami, więc odmowy nie dało się ani zrozumieć, ani naprawić. Dziś zasięg jest przycinany do początku najbliższej pozycji, a **rozliczany czas zostaje nietknięty** (zasięg to teren, czas to rachunek — patrz `SuggestionSpan`); prawnik dostaje zdanie o tym, co się stało. Pozycja zaczynająca się PRZED nową i sięgająca za jej początek to jedyne pokrycie, którego przycięciem nie da się usunąć (dokument zapisany w trakcie rozliczonego spotkania to realny przypadek): wpis i tak powstaje, a komunikat mówi wprost, żeby sprawdzić, czy te minuty nie idą na rachunek dwa razy. Reguła nadrzędna: **w rozliczaniu czasu odmowa jest najgorszym wyjściem**, bo zostawia pracę, której nie da się rozliczyć, i nie podpowiada, co zrobić. Analiza reszty blokad z tej samej rodziny: sąsiedztwo przy scalaniu i doliczaniu przerw liczy dziś `TimeAxis.Overlaps` z progiem JEDNEJ MINUTY (nakładanie krótsze nie jest nakładaniem, bo minuta jest jednostką rozliczenia i mniejszej wartości nie da się na ekranie zobaczyć), a wszystkie komunikaty formatuje `BusinessMoment`, który dokłada sekundy, gdy są niezerowe. Pozostałe odmowy (archiwum, obcy dokument, inny dzień, przerwa poza listą) opisują fakty widoczne dla użytkownika i zostają. |
| **Przerwy wpisu liczone z dziennika, z jawnym stanem** | Przerwa leżąca w godzinach wpisu ma jeden przełącznik: liczona → „Odejmij", nieliczona → „Dolicz". Wcześniej istniało wyłącznie odejmowanie przerw zapisanych przy sugestii, więc po scaleniu sesji przerwa MIĘDZY nimi nie istniała dla aplikacji: prawnik widział ją w historii wersji, ale nie miał czym jej rozliczyć ani skąd wiedzieć, że nie jest liczona. Lista przerw pochodzi dziś z append-only dziennika `DocumentActivity` ograniczonego do godzin wpisu (`EntryGapService`), a nie z kolumny JSON — dzięki temu działa też dla wpisów scalonych, ZANIM ta funkcja powstała (fakty leżały w bazie od początku). Stan domyślny bierze się z tego samego progu, którym tnie sesje silnik: przerwa do `SessionFlaggedGapMinutes` leży wewnątrz sesji i jest w czasie brutto (do odjęcia), dłuższa rozdziela sesje i nie jest rozliczana (do doliczenia). Ostatnia korekta na danym zakresie jest nadrzędna wobec progu, bo to jawna decyzja człowieka; przełączanie jest przez to idempotentne bez osobnej reguły („druga próba" nie ma przycisku, bo widoczny jest przeciwny). Doliczane minuty leżą w zasięgu wpisu, więc nie mogą zabrać czasu innej pozycji. Przerwa, na której wykonano już korektę, zostaje na liście nawet gdy dziennik przestanie ją odtwarzać (zaległa wersja zmieniająca kształt sesji) — inaczej zniknęłaby razem ze swoim stanem i te same minuty dałoby się odjąć drugi raz. Wpisy kalendarzowe i dane sprzed dziennika wracają do listy zapisanej przy wpisie. |
| **Rozliczenie pojedynczego wpisu** | Rozliczanie zakresu dat zakłada, że dzień jest zamknięty, a to nieprawda w połowie sytuacji: część pracy jest gotowa do faktury, część czeka na rozstrzygnięcie przerw albo na wskazanie sprawy. Prawnik miał więc do wyboru rozliczyć razem z gotowym wpisem coś, czego jeszcze nie sprawdził, albo nie rozliczyć nic. Gotowość do faktury jest cechą WPISU, nie dnia, więc przycisk stoi przy wpisie (`POST /api/time-entries/{id}/archive`), a rozliczanie dnia i zakresów zostaje bez zmian. Dwustopniowe potwierdzenie jak przy operacjach hurtowych, bo archiwum jest jednokierunkowe; druga próba na tym samym wpisie to 409, żeby data rozliczenia (wartość audytowa) nie przesunęła się przy powtórzonym kliknięciu. |
| **Zaokrąglanie do jednostki rozliczeniowej** | Jedno kliknięcie sprowadza czas wpisu do wielokrotności `BillingIncrementMinutes` (30 min, wartość w konfiguracji, bo jednostka to ustalenie z klientem, nie stała programu). Do NAJBLIŻSZEJ wielokrotności, a przy dokładnej połowie w DÓŁ: zaokrąglanie w górę „z automatu" powiększa rachunek klienta, czyli łamie tę samą regułę, przez którą wyleciał domyślny czas dokumentu. Minimum to jedna jednostka. Wynik liczy SERWER i podaje go w `roundedDurationMinutes`, więc etykieta przycisku („Zaokrąglij do 1 godz.") nie może obiecać innej liczby, niż zapisze operacja — frontend nie zna jednostki i nie ma jak się rozjechać. Korekta trafia do dziennika jako osobny rodzaj (`Rounding`), bo to inna decyzja niż „o tyle a tyle minut", i jest odwracalna przyciskami ±15. Suma tych korekt wraca w `roundingMinutes` i ma **własną plakietkę** („zaokrąglenie −20 min"): wcześniej „nieliczone przerwy" liczyły się z różnicy godziny minus czas, więc zaokrąglenie w dół wchodziło do tej samej liczby i wpis meldował przerwy, których w historii wersji nie było i których nie dało się kliknąć. Minuty nieliczone liczą się dziś z SAMYCH przerw (`detectedGaps` ze stanem `counted`), a każda inna korekta ma swoją nazwę. |
| **Numer sesji („edycja 3")** | Jeden dokument daje tyle sugestii, ile było sesji pracy, a wszystkie noszą tę samą nazwę pliku — lista wyglądała jak zduplikowana i nie dało się poznać, o którą pracę chodzi. Numer jest LICZONY przy odczycie (`SessionLabelService`), nie zapisywany: w tytule rozjeżdżałby się przy każdej synchronizacji (tytuł jest nadpisywany ze źródła) i przy każdym scaleniu (składowe znikają). Porządek daje kotwica sesji, czyli czas pierwszej wersji. Numeracja biegnie przez **całą historię pliku**, nie w obrębie doby: umowa jest pisana tygodniami, więc „która to część dnia" nie opisuje niczego, co prawnik ma w głowie, a numerowanie dzienne łamało się dokładnie tam, gdzie kolejność jest najmniej oczywista. Zaobserwowany przypadek: dwie sesje tego samego wieczoru (23:24 i 23:53) trafiły do dwóch różnych pul, bo późniejsza ma `EntryDate` z następnego dnia — jedna wyszła „pierwszą z dwóch", druga została bez numeru. Numer dostaje KAŻDA sesja, licząc od pierwszej: pomijanie plików z jedną sesją znaczyło, że brak plakietki raz mówi „ten plik ma jedną edycję", a raz „ta pozycja wypadła z numeracji" — dwa różne fakty nie do odróżnienia na ekranie. Liczymy po sugestiach we WSZYSTKICH stanach, nie tylko oczekujących — inaczej zatwierdzenie „edycji 2" zmieniałoby „edycję 3" w „edycję 2", czyli ten sam numer wskazywałby raz jedną, raz drugą pracę. Po scaleniu wynik zachowuje numer wcześniejszej ze scalanych sesji (zostaje jej kotwica), a sesje po niej przesuwają się o jeden, bo realnie jest ich o jedną mniej. |
| **Korekty jako dziennik (`TimeEntryAdjustment`)** | `DurationMinutes` wpisu = suma sesji + suma korekt, przeliczana i zapisywana przy każdej zmianie — korekty są **dziennikiem audytowym**, nie stanem liczonym w locie („skąd wzięła się ta liczba"). Korekta prawnika jest nadrzędna: sync nigdy nie modyfikuje wpisu z korektami ani zarchiwizowanego (sync wolno dotykać wyłącznie sugestii `Pending`, jak dotychczas). Korekty przerw niosą zakres od–do, co czyni odjęcie tej samej przerwy idempotentnym (druga próba → 409). |
| **Odświeżanie oczekujących przy syncu** | Zmiana nazwy pliku/tytułu spotkania nie zmienia ID w Graph, więc sam dedup zostawiałby stary tytuł. Sugestie **oczekujące** są nadpisywane wartościami ze źródła (z ponownym dopasowaniem); zatwierdzonych i odrzuconych sync nie dotyka. |
| **Rekonsyliacja kalendarza** | Backend rekonsyliuje kalendarz per spotkanie: przeniesione spotkanie aktualizuje istniejącą oczekującą sugestię w miejscu (bez „ducha" pod starą datą), odrzucenie jest „lepkie" per spotkanie (zmiana terminu nie przywraca sugestii), a oczekujące sugestie spotkań usuniętych lub już nierozliczalnych (anulowane/prywatne/całodniowe) znikają, a raport pokazuje je w liczniku „usunięte". Część destrukcyjna działa wyłącznie, gdy frontend zadeklaruje kompletny snapshot (`calendarSnapshotComplete`, czyli wszystkie strony pobrane bez błędu) wraz z zakresem dni (`calendarSnapshotDaysBack`), i tylko w przecięciu tego zakresu z oknem backendu; częściowe pobranie ani rozjazd konfiguracji okien nie skasują prawidłowych sugestii. Dokumentów to nie dotyczy: delta jest przyrostowa i nieobecność pliku w feedzie niczego nie dowodzi, więc czyszczą je wyłącznie jawne tombstone'y. |
| **Współbieżność rozstrzygana w bazie** | Indeksy unikalne (`TimeEntries.SuggestionId`, `Cases.CaseNumber`) domykają wyścigi: równoległe zatwierdzenie/duplikat numeru sprawy kończy się jawnym 409, a synchronizacja po konflikcie ponawia scalanie na czystym stanie kontekstu. Na 409 mapowane jest wyłącznie naruszenie unikalności (SQLite 2067), nie ogólne błędy constraintów. |
| **Dezaktywacja zamiast usuwania spraw** | Wpisy czasu wskazują na sprawy kluczem obcym, więc twarde usunięcie niszczyłoby dane rozliczeniowe. `IsActive=false` wyłącza sprawę z dopasowania i list wyboru, zachowując historię. Numer sprawy pozostaje przy niej zajęty także po dezaktywacji, bo identyfikuje ją w historii rozliczeń; recykling numeru uczyniłby stare wpisy dwuznacznymi. Konflikt numeru nazywa więc kolidującą sprawę i jej stan, zamiast wskazywać na rekord domyślnie ukryty przed użytkownikiem. |
| **Archiwum zamiast usuwania** | Rozliczone wpisy (`TimeEntry.ArchivedAt`, znacznik czasu zamiast flagi: darmowy ślad audytowy "kiedy rozliczono") i schowane odrzucone sugestie (status `Archived`) trafiają do jednokierunkowego archiwum. Archiwum blokuje edycję: DELETE rozliczonego wpisu i restore zarchiwizowanej sugestii zwracają 409; rozliczony czas jest niezmienny, a korekta (storno) to świadomie odłożona przyszła funkcja. Zarchiwizowana sugestia zostaje w bazie i przy synchronizacji dalej blokuje ponowne utworzenie tej samej pozycji (anty-nawrót jak przy odrzuceniu). Kafelek w nagłówku liczy wyłącznie nierozliczone wpisy; archiwizacja jest jedynym "resetem" tej liczby. |
| **Edycja = zatwierdzenie z poprawionymi wartościami** | Jeden endpoint `approve` przyjmuje wartości finalne: mniej ścieżek, ta sama walidacja. |
| **Diff wersji .docx liczony w przeglądarce** | Decyzja prywatnościowa: **treść dokumentów nigdy nie przechodzi przez backend** — frontend pobiera obie wersje bezpośrednio z Graph (`GET /me/drive/items/{id}/versions/{vId}/content`, mieści się w `Files.Read`), rozpakowuje ZIP (`fflate`), wyciąga tekst z `word/document.xml` (DOMParser, namespace WordprocessingML; akapit `w:p` czytany w kolejności dokumentu: `w:t` to tekst, `w:br`/`w:cr` to złamanie wiersza, `w:tab` to tabulator — samo sklejanie `w:t` gubiło miękkie entery i zlepiało wyrazy z dwóch linii, przez co „raz była przerwa, raz nie") i diffuje **po LINIACH** (pakiet `diff`, LCS). Jednostką jest linia, nie akapit: dla użytkownika enter i Shift+Enter to ta sama rzecz, a Word robi z nich raz nowy `w:p`, raz `w:br`. Przy jednostce „akapit" kilkanaście widocznych linii lądowało w JEDNEJ pozycji listy zmian — jeden przycisk „Pokaż całość" na cały blok, licznik znaków liczony dla wszystkich linii razem i wielokropek ucinający w przypadkowym miejscu w środku. Po rozbiciu każda linia ma własny wiersz, własny licznik i własny przycisk, a dwie linie nigdy nie zlewają się w jeden ciąg. Tylko `.docx` — `.doc` (binarny) i arkusze Excela dostają chronologię bez diffu, z komunikatem. Udokumentowany wyjątek od reguły „token tylko do graph.microsoft.com": Graph odpowiada na `/content` przekierowaniem 302 do domeny pobrań (adres pre-autoryzowany), a przeglądarka przy przekierowaniu cross-origin sama usuwa nagłówek `Authorization` — walidowany jest adres początkowy. **Bieżąca wersja jest wyjątkiem adresowym**: Graph nie wydaje jej treści spod adresu wersji, tylko odpowiada 400 — treścią najnowszej wersji jest po prostu aktualna treść pliku, więc bierzemy ją z `GET /me/drive/items/{id}/content`. Bez tego porównanie **najnowszej pary** (jedynej, na której zwykle zależy) kończyło się komunikatem „Graph 400", podczas gdy starsze pary działały. Którą pozycję traktować jako bieżącą, mówi backend — i mówi to o **każdym wierszu należącym do najnowszej wersji**, nie tylko o ostatnim. Przy ciągłej pracy Word online nie pieczętuje wersji, tylko przesuwa jej znacznik czasu, więc ten sam numer trafia do dziennika wiele razy; oznaczanie samego ostatniego wiersza sprawiało, że wcześniejsze próbki tej wersji dostawały adres wersji i porównanie kończyło się błędem `400 invalidRequest` — dokładnie na najnowszym fragmencie pracy, czyli tam, gdzie najbardziej zależy. Dodatkowo rozpoznajemy etykietę `current` w miejscu numeru wersji. Dwie próbki **tej samej** wersji nie dostają przycisku „Porównaj" (`isSameVersionAsPrevious`): OneDrive trzyma jeden snapshot na numer wersji, więc stanu pośredniego nie ma i nie ma czego z czym zestawić — zamiast przycisku jest zdanie, które to tłumaczy, **i wskazuje godzinę zapisu, przy którym te zmiany widać** („te zmiany są w »Porównaj z poprzednią« przy zapisie 00:02:51"). Porównanie stoi przy PIERWSZYM wierszu danej wersji, a jego wynik obejmuje całą pracę w tej wersji, także z późniejszych próbek; samo „brak zapisanego stanu pośredniego" zostawiało prawnika z wrażeniem, że praca z tych minut przepadła. Gdy powtórzona wersja otwiera dziennik, mówimy wprost, że porównanie pojawi się dopiero przy następnej wersji — obiecywanie przycisku, którego nigdzie nie ma, byłoby odesłaniem donikąd. Z tego samego powodu klucz `track` listy to numer wersji **razem z momentem**: sam numer nie jest unikalny. Para z wersją bieżącą jest celowo **poza cache** — to ruchoma głowica pliku, więc po kolejnym zapisie ten sam klucz oznaczałby inną treść. Ładowanie wyłącznie po kliknięciu konkretnej pary wersji (każde = 2 pobrania pliku), ze spinnerem i anulowaniem (`AbortController`); pobieranie idzie przez ten sam helper co reszta Graph (ponowienia 429/5xx z `Retry-After`, limit czasu, anulowanie), a komunikat błędu niesie **kod z ciała odpowiedzi** (`error.code`), bo sam status „400" nie mówi nic. Wynik cache'owany w pamięci sesji, świadomie nie w localStorage. Brak wersji (404) to stan normalny — retencja OneDrive jest ograniczona; UI mówi „ta wersja nie jest już dostępna" i działa dalej. Prezentacja: +/−/~ obok koloru (nigdy sam kolor), brak różnic tekstowych = „zmiany dotyczyły formatowania". **Serie pustych linii są sklejane w jedną pozycję z liczbą** („(4 puste linie)"): kilka enterów pod rząd to kilka pustych linii, więc lista zmian dostawała tyle samo wierszy, niosąc jedną informację — zrobiono odstęp. Sklejamy tylko sąsiednie i tylko tego samego rodzaju (dodany odstęp to co innego niż usunięty), a pozycji „zmienione" nie ruszamy, bo mówi o treści. Długie linie są przycięte do 300 znaków, ale wielokropek **nie zostaje bez wyjaśnienia**: przycisk podaje liczbę ukrytych znaków („Pokaż całość (420 znaków)"), stoi przy swojej linii i działa w obie strony — samo „…" wyglądało jak uszkodzone dane i nie dawało się cofnąć. Nowe zależności frontendu ograniczone do `fflate` i `diff` (małe, audytowalne); backend bez żadnych zależności do parsowania dokumentów. |
| **Historia zmian tam, gdzie zapada decyzja** | Chronologia modyfikacji (i diff) jest dostępna nie tylko w osi czasu, ale też pod przyciskiem „Historia zmian" na karcie sugestii i przy wpisie czasu. Decyzja o czasie albo o odjęciu przerwy wymaga zobaczenia, kiedy dokument faktycznie zapisywano — odsyłanie po to do innego widoku sprawiało, że prawnik zatwierdzał w ciemno. Ładowane dopiero po kliknięciu (osobne żądanie na pozycję), jedna historia naraz. |
| **Historia zmian pokazuje stan WSZYSTKICH pozycji pliku** | Chronologia pokazuje CAŁY plik, a jeden plik ma zwykle kilka sesji, czyli kilka pozycji — i to w różnych stanach naraz. Wcześniej stan podawał ten, kto historię otwierał (zakres + rodzaj przekazywane z karty), więc ta sama historia opowiadała dwie różne rzeczy zależnie od miejsca otwarcia: w archiwum fragment był oznaczony jako rozliczony, a z karty sugestii ta sama praca wyglądała, jakby nikt jej nie rozliczył. Odpowiedź `document-activity` niesie dziś obok wersji listę POZYCJI (`sessions`: zakres, stan, numer edycji, przerwy) i każdy wiersz bierze stan od swojej własnej pozycji. Sugestia ZATWIERDZONA nie jest osobną pozycją — reprezentuje ją wpis, bo inaczej te same zapisy należałyby na ekranie do dwóch pozycji naraz. Oglądaną pozycję rozpoznajemy po IDENTYFIKATORZE (`currentSuggestionId` / `currentEntryId`), nie po zakresie godzin: zakres bywa przycięty do sąsiada albo zmieniony korektą, a wtedy porównywanie godzin wskazywałoby raz tę pozycję, raz żadnej. Wyróżnia ją grubszy pasek i plakietka „ta pozycja" — nigdy sam kolor. **Kolor niesie stan**: `pending` niebieski, `rejected` czerwony, `archived` szary, `unsettled` (wpis do rozliczenia) bursztynowy, `settled` (rozliczony) zielony, zgodnie z konwencją całej aplikacji, gdzie zieleń znaczy „zamknięte", a bursztyn „masz tu coś do zrobienia". Plakietka pada RAZ, przy pierwszym wierszu obszaru, i niesie numer edycji razem ze stanem („edycja 3, wpis rozliczony"). Sąsiednie elementy listy stykają się, więc z osobnych wierszy powstaje jeden blok; zaokrąglane są tylko krańce (`opens-session` / `closes-session`). Przerwa należy do pozycji wyłącznie wtedy, gdy OBA jej końce są w tym samym obszarze; leżąca między pozycjami dostaje „poza pozycjami", bo nie zmienia niczyjego czasu, a prawnik mógł w tym czasie robić coś, o czym historia tego pliku nie wie. Przerwy oglądanej pozycji rodzic podaje osobno (`sessionGaps`) — po doliczeniu przerwy lista wpisów ładuje się od nowa, a otwarta chronologia nie jest pobierana drugi raz, więc świeższy stan ma rodzic. |
| **Granica dnia zamiast przerwy na 3797 minut** | Odstęp między zapisami z RÓŻNYCH dni nie jest przerwą w pracy: sesja nigdy nie przechodzi przez północ, więc taki odstęp z definicji nie leży w żadnej pozycji i nie ma przy nim żadnej decyzji do podjęcia. Dostawał jednak to samo słowo („przerwa"), ten sam kolor przestoju i tę samą plakietkę stanu co kwadrans pauzy w pisaniu, w dodatku w surowych minutach — dwie i pół doby bez dotknięcia pliku czytało się jako „przerwa 3797 min", czyli liczbę do przeliczenia w głowie. Dziś w tym miejscu stoi cicha kreska „nowy dzień · 2 dni 15 godz. bez zapisu", bez koloru i bez plakietki. Odstępy w obrębie dnia zostają przerwami ze stanem, ale liczy je `formatGapSpan`: poniżej doby ta sama forma co czas pracy („1 godz. 52 min"), od doby w górę dni z pominięciem minut — przy tej skali minuty są szumem, nie precyzją. |
| **Przerwa w historii zmian mówi, czy ktoś ją rozlicza** | Chronologia pokazywała sam fakt przestoju, a plakietkę dostawała wyłącznie przerwa z przedziału 15–30 min. Skutek zgłoszony z użycia: przerwa 110-minutowa — czyli ta, o którą naprawdę chodzi w rozliczeniu — wyglądała jak zwykły odstęp między zapisami, bo o niej ekran nie mówił nic. Dziś **każdy** przestój dłuższy niż próg ciągłości jest wyróżniony kolorem, a wewnątrz pozycji dostaje stan wprost: „wliczona w czas" albo „nie wliczona w czas", z pełnym zdaniem w dymku (razem z tym, że we wpisie czasu przełącza się to jednym kliknięciem). Odstępy krótsze od progu zostają szare i bez opisu — to pauza na myślenie w trakcie pisania, nie pozycja do rozliczenia. Stan bierze się z listy przerw NALEŻĄCEJ do pozycji (`detectedGaps`), bo tylko ona zna decyzje prawnika; próg jest regułą awaryjną, gdy pozycja swojej listy nie ma. Chronologia i przyciski przy wpisie czytają więc ten sam stan i nie mogą pokazać dwóch różnych rzeczy. Przerwa leżąca między pozycjami dostaje „poza pozycjami": nie zmienia niczyjego czasu, a napisanie przy niej „nie wliczona" byłoby zaproszeniem do doliczenia minut, których żadna pozycja nie obejmuje. Plik bez pozycji (nic jeszcze nie zatwierdzono, nic nie czeka) wyróżnia przestoje kolorem, ale o rozliczeniu nie twierdzi nic. |
| **Układ karty wpisu: akcje pod treścią, nie obok** | Operacje wpisu (czas, przerwy, rozdzielenie) stały w kolumnie z prawej strony karty. Po rozwinięciu historii zmian ta kolumna wisiała w połowie kilkudziesięciowierszowej listy wersji i zabierała jej szerokość — a to właśnie ta lista jest dowodem, z którego bierze się decyzja o czasie. Karta układa się dziś w wiersze: fakty, pasek akcji w podpisanych grupach, historia na pełnej szerokości u dołu. Sama chronologia dostała stałe kolumny (godzina, data, rozmiar), więc czyta się w pionie, a wyjaśnienie przy powtórzonej wersji zeszło pod wiersz w skróconej formie, z pełnym zdaniem w dymku: powtórzone przy każdej próbce otwartej wersji zajmowało więcej miejsca niż cała chronologia. |
| **Dlaczego ciągła edycja bywa niewidoczna (i co z tym zrobiono)** | Diagnoza: w Wordzie online nowa **wersja** pliku powstaje dopiero przy zamknięciu karty albo po kilkunastu minutach bezczynności — autozapis w trakcie pracy dopisuje do wersji już otwartej. Prawnik piszący dwie godziny bez przerwy nie produkuje więc dwóch godzin historii, tylko jeden wpis „ostatnia zmiana". Wcześniej dokładał się do tego nasz własny błąd: dziennik kluczowany po `(plik, wersja)` odrzucał każdy kolejny odczyt tej samej wersji jako duplikat, więc przesuwający się znacznik czasu przepadał bez śladu. Naprawa jest po stronie modelu: fakt = `(plik, wersja, moment)` plus próbka z `driveItem.lastModifiedDateTime` przy każdym syncu (patrz wiersz o dzienniku). Każda synchronizacja staje się pomiarem, a sesja rośnie razem z pracą zamiast czekać na zapieczętowanie wersji. **Tryb na żywo usunięty**: cykliczne odpytywanie nie działało dokładnie dlatego, że dziennik i tak odrzucał powtórzone obserwacje — bez naprawy modelu było to odpytywanie o nic. Po naprawie granicą pozostaje to, jak często dane w ogóle się zmieniają i jak często prawnik synchronizuje. Odrzucona alternatywa: `getActivitiesByInterval`/`itemActivityStat`. Endpoint jest w `v1.0` i mieści się w `Files.Read` (nowych zakresów nie trzeba), ale **nie działa dla kont osobistych Microsoft** — a nasza rejestracja stoi na `/common` i prywatny OneDrive jest realnym scenariuszem. Przede wszystkim jednak zwraca **liczniki, nie momenty**: „drugiego stycznia były 3 edycje", bez znaczników czasu. Silnik sesji potrzebuje chwil, żeby wyliczyć początek, koniec i przerwy, więc z agregatów nie odtworzy osi czasu. Do tego limit 90 dni na zapytanie i zastrzeżenie, że agregaty nie są dostępne dla wszystkich typów akcji. |
| **Automatyczne sprawdzanie co 10 minut (przy otwartej karcie)** | Każda synchronizacja jest **pomiarem**: dopisuje do dziennika `DocumentActivity` próbkę „plik miał tę datę modyfikacji o tej godzinie". Word online nie pieczętuje wersji w trakcie pisania, więc materiał do liczenia sesji bywa bardzo rzadki — a ręczne klikanie „Synchronizuj" raz dziennie daje najgorszy możliwy materiał pomiarowy (realnie: nikt tego nie klika). Stąd `AutoSyncService` z odstępem `AUTO_SYNC_INTERVAL_MINUTES`. Wartość (10 min) wynika z silnika sesji, nie z „co jakiś czas": musi być **gęstsza niż `SessionContinuationGapMinutes`** (15 min), z zapasem na dławienie timerów w karcie w tle (przeglądarki tną je do ~1/min). **Czego to nie obiecuje:** że długa praca zawsze rozbije się na sesje — o tym, kiedy powstaje ślad, decyduje Word, nie aplikacja. Gęstsze próbkowanie ogranicza dziurę w danych do kwadransa zamiast całego dnia; to zwiększona szansa na wierny pomiar, nie gwarancja, i dokładnie tak jest to nazwane w interfejsie. W drugą stronę częściej niż co kilka minut nie ma sensu, bo przebieg pobiera **całe** okno kalendarza (`calendarView` nie jest przyrostowe); pliki idą deltą, a historia wersji tylko dla plików zwróconych przez deltę jako zmienione, więc cichy przebieg to kilka żądań. **Nie działa przy zamkniętej przeglądarce** — i nie może: token Graph nigdy nie opuszcza przeglądarki, więc backend nie ma czym pobrać danych sam z siebie; praca „gdy aplikacja jest wyłączona" wymagałaby trzymania po stronie serwera tokenu odświeżającego, czyli dokładnie tego, czego ta architektura unika. Blokada `busy` jest **wspólna** z przyciskiem „Synchronizuj": dwa równoległe przebiegi biłyby się o wskaźnik delty OneDrive, który przesuwa się dopiero po udanym zapisie. Automat jest cichy — powiadamia tylko wtedy, gdy naprawdę coś przybyło, ubyło albo się zmieniło — i **sam się wyłącza po `AUTO_SYNC_MAX_FAILURES` nieudanych próbach** (typowo: wygasła sesja Microsoft), bo cicho dobijający się do Graph automat jest gorszy od wyłączonego. Startuje dopiero po zalogowaniu i zatrzymuje się przy wylogowaniu; żyje w korzeniu aplikacji, więc mierzy także wtedy, gdy otwarta jest zakładka „Wpisy czasu". **Domyślnie wyłączone, ale z jednorazową zachętą.** Włączanie fabrycznie odrzucone: aplikacja sięgająca do Microsoftu co kilka minut przy pozornie bezczynnej karcie to w kancelarii decyzja użytkownika, a nie ustawienie producenta — każdy przebieg dodatkowo pisze do bazy i potrafi dorzucić sugestie na ekran, na który ktoś właśnie patrzy. Zostawienie tego samemu polu wyboru w pasku też jest złe (funkcja, o której nikt się nie dowie, nie istnieje — a prawnik i tak nie klika „Synchronizuj"), więc po zalogowaniu pokazuje się **jeden** panel z rekomendacją i dwoma przyciskami. Odpowiedzią jest każda z trzech decyzji — włączenie, „Nie teraz" i świadome wyłączenie — i żadna nie wraca (`AUTO_SYNC_NUDGE_STORAGE_KEY`). Obok pola wyboru stoi **nazwany odnośnik „Co to daje?"**, nie znak zapytania w kółku: samo „?" nie mówi, czego dotyczy wyjaśnienie, a wyjaśnienie to cztery akapity, których nie da się zmieścić w podpowiedzi pod kursorem (na dotyku nieistniejącej). Poprzedni „tryb na żywo" usunięto, bo dziennik kluczowany po `(plik, wersja)` odrzucał powtórzone obserwacje — po naprawie modelu na `(plik, wersja, moment)` każdy przebieg wnosi informację. |
| **Jedno rozgłoszenie zmiany danych** | Po synchronizacji i po operacjach na wpisach widoki odświeżają się same (`DataRefreshService`), bez przeładowania przeglądarki — wcześniej oś czasu i liczniki dni zostawały na starych danych do F5. Powiadomienie niesie **inicjatora**: widok, który operację wykonał i odświeżył się już sam, pomija własny sygnał, więc lista nie ładuje się dwa razy; sprawdzenie w tle nie ma inicjatora i odświeża wszystkich. |
| **Raport z synchronizacji** | Backend zwraca liczniki: ile pobrano, ile odfiltrowano per reguła, ile zagregowano, jak dopasowano. Bez tego odfiltrowanie spotkań wygląda dla użytkownika jak zgubione dane. Przy pominiętych plikach raport podaje też **nazwy** (kilka pierwszych): delta OneDrive melduje każdą zmianę na dysku — także w plikach, których użytkownik nigdy nie otwierał w tej aplikacji — więc samo „pominięto 1 pozycję: plik inny niż Word/Excel" wygląda jak wzięte z powietrza i nie da się sprawdzić, czy to zdjęcie z kopii zapasowej, czy zgubiony dokument. Nazwy zbierane są w przeglądarce i **nie są wysyłane na backend** — nie ma po co. Pozycje **spoza okna rozliczenia nie są raportowane w ogóle** — ani spotkania, ani dokumenty. Okno jest zakresem rozliczenia, a nie regułą odrzucającą pracę: poza nim leży cała reszta kalendarza i cały dysk. Frontend pobiera z Graph z zapasem (przy dokumentach doba ponad okno; przy kalendarzu ponad **dwie doby**, bo okno backendu zaczyna się od początku doby lokalnej, a pobieranie liczy się w godzinach od „teraz"), więc licznik meldował przy każdej synchronizacji ten sam stały zapas jako „Pominięto N pozycji" — u dokumentów dodatkowo skacząc do kilkunastu po każdym pełnym przebiegu delty. Pozycje spoza okna nie liczą się też jako **pobrane**, dzięki czemu nadal zachodzi niezmiennik: pobrano = utworzone + zaktualizowane + pominięte (już istniały) + odfiltrowane + zagregowane + zdeduplikowane. |

## Endpointy API

| Metoda i ścieżka | Opis |
|---|---|
| `POST /api/sync` | Przyjmuje surowe dane z Graph (+ opcjonalnie: tombstone'y usuniętych plików, historia wersji per plik `versions`, liczniki filtrów klienckich, nadpisanie okna synchronizacji `syncDaysBack`, maks. 90 dni), zwraca pełny raport z faktycznie użytym oknem (`windowDays`) i licznikami wersji (`versions`); 409 przy kolizji z równoległą synchronizacją |
| `GET /api/suggestions?status=&source=` | Lista sugestii (domyślnie oczekujące), **posortowana malejąco po ostatniej modyfikacji**; niesie też `lastActivityAt`, `isUserAdjusted` i wolne luki `gaps` wokół pozycji |
| `POST /api/suggestions/merge` | Scala sesje tego samego dokumentu z tego samego dnia w jedną sugestię (`{suggestionIds, includeGaps}`) |
| `POST /api/suggestions/{id}/claim-gap` | Rozdziela wolną lukę (`{direction: before\|after, minutes?, neighborMinutes?}`): `minutes` bierze ta sugestia, `neighborMinutes` sąsiednia, reszta zostaje wolna; bez obu wartości cała luka trafia tutaj. Rozmiar luki liczy serwer i tylko jeśli jest wolna oraz mieści się w limicie |
| `POST /api/suggestions/{id}/approve` | Tworzy wpis czasu, zamyka sugestię |
| `POST /api/suggestions/{id}/reject` | Odrzuca (status, bez usuwania) |
| `POST /api/suggestions/{id}/restore` | Przywraca odrzuconą do oczekujących (409 dla zarchiwizowanej: archiwum jest terminalne) |
| `POST /api/suggestions/{id}/archive` | Archiwizuje pojedynczą odrzuconą sugestię (409, gdy status inny niż odrzucona) |
| `POST /api/suggestions/archive-rejected` | Hurtowo archiwizuje wszystkie odrzucone sugestie, zwraca licznik |
| `GET /api/cases?includeInactive=` | Sprawy ze słowami kluczowymi (domyślnie tylko aktywne) |
| `POST /api/cases`, `PUT /api/cases/{id}` | Dodawanie i edycja spraw (unikalny numer sprawy) |
| `POST /api/cases/{id}/activate` / `deactivate` | Przełączanie aktywności (zamiast usuwania) |
| `GET /api/time-entries?archived=` | Wpisy pogrupowane po dniach z sumami (domyślnie aktywne; `archived=true` zwraca archiwum, suma dotyczy zwróconego widoku) |
| `POST /api/time-entries/merge` | Scala wpisy jednej sesji dokumentu (`{timeEntryIds, includeGaps}`): ≥2 wpisy, ten sam dokument i dzień, żaden nie zarchiwizowany, sąsiedztwo globalne (między pierwszym a ostatnim żadnej innej pozycji — inaczej 409 z tytułem blokującej); `includeGaps` dolicza wolne luki z zapisem `GapAddition` |
| `POST /api/time-entries/{id}/unmerge` | Odwraca scalenie: przywraca wpisy składowe z ich sesji (możliwe do momentu archiwizacji) |
| `POST /api/time-entries/{id}/subtract-gap` | Wyłącza przerwę z rozliczanego czasu (`{gapStartAt, gapEndAt}` z listy przerw wpisu); przerwa już nieliczona → 409 |
| `POST /api/time-entries/{id}/add-gap` | Dolicza przerwę leżącą w godzinach wpisu (ten sam kształt żądania); przerwa już liczona → 409 |
| `POST /api/time-entries/{id}/round` | Zaokrągla czas wpisu do jednostki rozliczeniowej z konfiguracji; czas już będący wielokrotnością → 400 |
| `POST /api/time-entries/{id}/adjust` | Szybka korekta `{minutes: ±N}`; wynik w przedziale (0, 480] min |
| `POST /api/time-entries/archive` | Rozlicza (archiwizuje) aktywne wpisy z domkniętego zakresu dat (maks. 366 dni); idempotentne, zwraca liczbę wpisów i sumę minut |
| `POST /api/time-entries/{id}/archive` | Rozlicza pojedynczy wpis i zwraca go; wpis już rozliczony → 409 (data rozliczenia jest wartością audytową i nie przesuwa się przy drugiej próbie) |
| `DELETE /api/time-entries/{id}` | Cofa zatwierdzenie: usuwa aktywny wpis i przywraca sugestię; 409 dla wpisu rozliczonego |
| `GET /api/timeline?from=&to=` | Agregacja osi czasu per dzień: `[{date, pendingCount, activeCount, archivedCount}]` — jedno żądanie na cały miesiąc (maks. 366 dni), zatwierdzona sugestia liczona raz (jako wpis) |
| `GET /api/timeline/{date}` | Pozycje jednego dnia (oczekujące sugestie + wpisy) posortowane po godzinie startu, ze statusem `pending`/`active`/`archived` |
| `GET /api/timeline/document-activity?externalId=` | Chronologia modyfikacji dokumentu z dziennika `DocumentActivity` (`versions`) RAZEM z pozycjami, które z niej powstały (`sessions`: zakres, stan, numer edycji, przerwy): godzina i rozmiar każdej wersji, przerwy między nimi z dwoma znacznikami (`isSessionBreak` — dłuższa niż próg ciągłości, `splitsSession` — rozcinająca pracę na dwie sesje); `isCurrent` na **każdym** wierszu należącym do najnowszej wersji (jej treść pobiera się z endpointu pliku, nie wersji), `isSameVersionAsPrevious` na kolejnych próbkach tej samej wersji |
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

**Testy** (331 testów backendu xUnit + 227 testów frontendu Vitest; bez sieci i logowania):

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
    Data/                   DbContext + seed spraw, migrator z kopią bazy,
                            mapowanie błędów unikalności SQLite (2067 → 409)
    Migrations/             migracje EF Core (SQLite)
    Models/                 encje (Case, Suggestion, TimeEntry, SyncRun)
                            + enumy źródła i statusu sugestii
    Services/               logika czysta (normalizacja, filtr z licznikami,
                            dopasowanie, budowa sugestii, strefy i zmiana czasu)
                            + serwisy aplikacyjne (sync, approval, summary)
  TimeSuggestions.Tests/    xUnit + fixtures JSON (TestData/)
timesuggestions-web/
  src/app/
    components/             suggestion-card, document-history, timeline-panel
    pages/                  suggestions-page, time-entries-page, cases-page
    models/                 typy 1:1 z DTO backendu i Graph
    pipes/                  duration (minuty → "1 godz. 30 min"),
                            polish-plural (odmiana liczebników)
    services/               auth (MSAL), graph-http (walidacja URL, retry, timeout),
                            graph-calendar, graph-files (delta+cache+tombstone'y),
                            graph-config (stałe Graph), api, summary-store,
                            auto-sync (pomiar w tle co 10 min przy otwartej karcie),
                            docx-diff (rozpakowanie i diff wersji w przeglądarce),
                            theme (motywy), toast, data-refresh, user-message
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
- **Niezmiennik nakładania: każda minuta doby należy do najwyżej jednego wpisu czasu.**
  Wszystkie reguły anty-podwójne z niego wynikają: przerwa doliczona do wpisu A nie jest
  dostępna dla wpisu B, scalenie „z przerwami" jest legalne tylko dla przerw wolnych,
  a zatwierdzenie sugestii waliduje nakładanie z istniejącymi wpisami (409 z nazwą
  blokującego wpisu zamiast cichego nakładu). Walidacja odbywa się na backendzie
  (klientowi nie ufamy); w niezmienniku uczestniczą wpisy każdego źródła oraz oczekujące
  sugestie (jako rezerwacje minut). Przedziały są półotwarte: koniec 10:00 i start 10:00
  to nie nakład.
- Zatwierdzenie wymaga wybranej sprawy i czasu 1–1440 min; tworzy `TimeEntry` ze źródłem
  pochodzenia, godzinami z sugestii i powiązaniem sugestii przez `TimeEntryId`.
  Decyzje są odwracalne (Cofnij / Przywróć / Cofnij zatwierdzenie — to ostatnie
  przywraca wszystkie sugestie składowe wpisu), a „Cofnij" działa także po przejściu
  na inną zakładkę; jedynym świadomym wyjątkiem jest rozliczenie (archiwizacja),
  które jest nieodwracalne.
- Operacje prawnika na wpisach (scalanie, rozdzielanie, korekty ±N, odjęcie wykrytej
  przerwy, doliczenie wolnej luki) działają wyłącznie na wpisach aktywnych — archiwum
  blokuje wszystkie. Scalenie jest odwracalne (`unmerge` przywraca wpisy składowe
  z ich sesji; korekty scalonego wpisu przepadają świadomie). Każda korekta ląduje
  w dzienniku `TimeEntryAdjustment`; sync nigdy nie modyfikuje wpisów — dotyka
  wyłącznie sugestii `Pending`.
- Cykl życia wpisu czasu: aktywny, potem rozliczony (zarchiwizowany). Rozliczenie jest
  hurtowe (dzień albo zakres dat, maks. 366 dni), jednokierunkowe i blokuje edycję;
  „Cofnij zatwierdzenie" działa wyłącznie dla wpisów aktywnych. Odrzuconą sugestię
  można zarchiwizować (Rejected → Archived, stan terminalny bez unarchive);
  zarchiwizowana nadal chroni przed ponownym utworzeniem tej samej pozycji
  przy synchronizacji.

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
- **Korekty rozliczonych wpisów (storno)**: archiwum celowo blokuje edycję i cofanie
  rozliczonego czasu, więc pomyłka wykryta po rozliczeniu wymaga dziś poprawki poza
  aplikacją. Docelowo dodałbym jawną operację korygującą (wpis storno z referencją
  do oryginału) zamiast cichej edycji historii, a do archiwum retencję i eksport.
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
