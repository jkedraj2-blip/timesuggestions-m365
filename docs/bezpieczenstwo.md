# Bezpieczeństwo: co leży w bazie i czym to grozi

Pytanie zadane wprost (czy trzymanie danych z dokumentów w bazie to duże ryzyko wycieku)
zasługuje na uczciwą odpowiedź, a nie na zapewnienie, że "jest bezpiecznie".

## Czego w bazie nie ma

Treść dokumentów nigdy nie trafia do backendu. Diff wersji `.docx` liczy się
w przeglądarce: to ona pobiera obie wersje prosto z Graph, rozpakowuje je i porównuje,
a wynik żyje w pamięci karty (świadomie nie w `localStorage`). Backend nie ma ani
biblioteki do parsowania dokumentów, ani żadnej kolumny na ich treść. Nie ma też tokenu
Microsoft: token żyje wyłącznie w przeglądarce, więc z samej bazy nie da się sięgnąć po
nic więcej z M365. Tytuły spotkań oznaczonych jako prywatne, osobiste lub poufne
odfiltrowuje przeglądarka i nie opuszczają jej w ogóle.

## Co w bazie jest

Metadane, które same w sobie bywają tajemnicą zawodową:

| Dane | Skąd | Wrażliwość |
| --- | --- | --- |
| Nazwa pliku (`Suggestion.Title`) | OneDrive | wysoka: nazwy dokumentów kancelaryjnych zwykle zawierają nazwisko klienta i sygnaturę |
| Tytuł spotkania | Outlook | wysoka, z wyjątkiem prywatnych/poufnych (odfiltrowanych) |
| Sprawy: nazwa, numer, klient (`Case`) | wpisane ręcznie | wysoka: to wprost rejestr klientów |
| Identyfikator pliku, numery wersji, momenty i rozmiary zapisów (`DocumentActivity`) | Graph | średnia: nie treść, ale pełny dziennik "kto nad czym i kiedy pracował" |
| Wpisy czasu i opisy czynności (`TimeEntry`) | zatwierdzenia użytkownika | wysoka: to materiał rozliczeniowy |

Innymi słowy: baza nie zawiera dokumentów, ale zawiera **indeks pracy kancelarii**,
czyli listę klientów, spraw i godzin. Dla kogoś z zewnątrz bywa to cenniejsze niż
pojedyncza umowa.

## Realne ryzyka, od najważniejszego

1. **Plik bazy jest niezaszyfrowany.** `timesuggestions.db` to zwykły plik SQLite obok
   binarki; skopiowanie go (skradziony laptop, backup do chmury, dowolny proces
   użytkownika) daje pełny rejestr klientów bez żadnego hasła. Do tego przed każdą
   migracją powstaje kopia `.bak`, czyli kolejne kopie tych samych danych. Kopie są
   wyłączone z gita, ale nikt ich nie kasuje.
2. **API nie ma uwierzytelniania.** Każdy proces na tej maszynie może czytać i pisać
   przez port 5188. Ogranicznikiem jest wyłącznie to, że Kestrel nasłuchuje na
   `localhost`, a `AllowedHosts` przyjmuje tylko nagłówki lokalne (to drugie chroni
   przed DNS rebinding, czyli przed stroną w przeglądarce próbującą dobić się do
   lokalnego API).
3. **CORS wpuszcza tylko `http://localhost:4200`**, celowo nie `AllowAnyOrigin`.
   To ochrona przed cudzą stroną w tej samej przeglądarce, nie przed procesem na
   maszynie.
4. **Cały ruch idzie po HTTP.** Na localhoście nie opuszcza pętli zwrotnej; w sieci
   byłoby nie do przyjęcia.

## Co trzeba zrobić przed produkcją

Świadomie poza zakresem prototypu:

- walidacja tokenu Entra dla własnego API i rozdzielenie danych per użytkownik;
- przegląd komunikatów odmowy pod kątem rozdziału danych: odmowy niosą dziś opis cudzej
  pozycji ("Scalenie blokuje pozycja: wpis ..."), co w prototypie jednego użytkownika
  jest jego własną informacją, a przy wielu użytkownikach byłoby wyciekiem między nimi;
- szyfrowanie spoczynkowe (BitLocker/FileVault na dysku albo SQLCipher na samej bazie);
- HTTPS;
- retencja i kasowanie dziennika `DocumentActivity` oraz kopii migracyjnych `.bak`
  (dziennik rośnie bez retencji i bez kaskady, kopii migracyjnych nikt nie kasuje);
- przy pracy zespołowej rozdział ról i audyt dostępu.

Dopóki aplikacja stoi na jednym laptopie prawnika, ryzyko jest porównywalne z ryzykiem
samych plików w OneDrive na tym samym dysku; w momencie postawienia jej na serwerze
zmienia się jakościowo i punkty 1-2 przestają być akceptowalne.

## Co zrobiłbym inaczej, mając więcej czasu

Projekt jest świadomie przygotowany jako lokalna aplikacja portfolio. Przed
udostępnieniem go jako publicznej usługi rozbudowałbym go w następującej kolejności:

- **Uwierzytelnienie API i izolacja danych użytkowników**: frontend pobierałby osobny
  token dla backendu, niezależny od tokenu Microsoft Graph. Backend weryfikowałby
  podpis, `issuer`, `audience` i wymagany scope, a dane byłyby przypisywane i filtrowane
  według `TenantId` oraz `UserObjectId`. Token Graph nadal nigdy nie trafiałby do
  backendu. Razem z izolacją danych trzeba przejrzeć komunikaty odmowy, które dziś
  opisują cudzą pozycję (patrz lista wyżej).
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
