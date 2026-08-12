# Endpointy API

Przykładowe wywołania wszystkich endpointów:
[`TimeSuggestions/TimeSuggestions/TimeSuggestions.http`](../TimeSuggestions/TimeSuggestions/TimeSuggestions.http).

| Metoda i ścieżka | Opis |
|---|---|
| `POST /api/sync` | Przyjmuje surowe dane z Graph (+ opcjonalnie: tombstone'y usuniętych plików, historia wersji per plik `versions`, liczniki filtrów klienckich, nadpisanie okna synchronizacji `syncDaysBack`, maks. 90 dni), zwraca pełny raport z faktycznie użytym oknem (`windowDays`) i licznikami wersji (`versions`); 409 przy kolizji z równoległą synchronizacją. Długości list w żądaniu są ograniczone walidacją |
| `GET /api/suggestions?status=&source=` | Lista sugestii (domyślnie oczekujące), posortowana malejąco po ostatniej modyfikacji; niesie też `lastActivityAt`, `isUserAdjusted` i wolne luki `gaps` wokół pozycji |
| `POST /api/suggestions/merge` | Scala sesje tego samego dokumentu z tego samego dnia w jedną sugestię (`{suggestionIds, includeGaps}`); odpowiedź niesie pełne DTO (z lukami i numerem sesji) |
| `POST /api/suggestions/{id}/claim-gap` | Rozdziela wolną lukę (`{direction: before\|after, minutes?, neighborMinutes?}`): `minutes` bierze ta sugestia, `neighborMinutes` sąsiednia, reszta zostaje wolna; bez obu wartości cała luka trafia tutaj. Rozmiar luki liczy serwer i tylko jeśli jest wolna, mieści się w limicie i nie przechodzi przez lokalną północ |
| `POST /api/suggestions/{id}/approve` | Tworzy wpis czasu, zamyka sugestię; zasięg wpisu jest przycinany do najbliższej pozycji (wpisu albo oczekującej sugestii), a pozostałe pokrycie opisuje `notice` |
| `POST /api/suggestions/{id}/reject` | Odrzuca (status, bez usuwania) |
| `POST /api/suggestions/{id}/restore` | Przywraca odrzuconą do oczekujących (409 dla zarchiwizowanej: archiwum jest terminalne) |
| `POST /api/suggestions/{id}/archive` | Archiwizuje pojedynczą odrzuconą sugestię (409, gdy status inny niż odrzucona) |
| `POST /api/suggestions/archive-rejected` | Hurtowo archiwizuje wszystkie odrzucone sugestie, zwraca licznik |
| `GET /api/cases?includeInactive=` | Sprawy ze słowami kluczowymi (domyślnie tylko aktywne) |
| `POST /api/cases`, `PUT /api/cases/{id}` | Dodawanie i edycja spraw (unikalny numer sprawy) |
| `POST /api/cases/{id}/activate` / `deactivate` | Przełączanie aktywności (zamiast usuwania) |
| `GET /api/time-entries?archived=` | Wpisy pogrupowane po dniach z sumami (domyślnie aktywne; `archived=true` zwraca archiwum, suma dotyczy zwróconego widoku) |
| `POST /api/time-entries/merge` | Scala wpisy jednej sesji dokumentu (`{timeEntryIds, includeGaps}`): co najmniej 2 wpisy, ten sam dokument i dzień, żaden nie zarchiwizowany; sąsiedztwo sprawdzane na przerwach między składowymi (obca pozycja w przerwie to 409 z jej tytułem); `includeGaps` dolicza wolne luki z zapisem `GapAddition` |
| `POST /api/time-entries/{id}/unmerge` | Odwraca scalenie: przywraca wpisy składowe z ich sesji (możliwe do momentu archiwizacji); korekty scalonego wpisu przepadają, o czym uprzedza dwustopniowe potwierdzenie w UI |
| `POST /api/time-entries/{id}/subtract-gap` | Wyłącza przerwę z rozliczanego czasu (`{gapStartAt, gapEndAt}` z listy przerw wpisu); przerwa już nieliczona to 409 |
| `POST /api/time-entries/{id}/add-gap` | Dolicza przerwę leżącą w godzinach wpisu (ten sam kształt żądania); przerwa już liczona to 409 |
| `POST /api/time-entries/{id}/round` | Zaokrągla czas wpisu do jednostki rozliczeniowej z konfiguracji; czas już będący wielokrotnością to 400 |
| `POST /api/time-entries/{id}/adjust` | Szybka korekta `{minutes: ±N}`; wynik musi być dodatni, a limit 480 min blokuje wyłącznie zwiększanie |
| `POST /api/time-entries/archive` | Rozlicza (archiwizuje) aktywne wpisy z domkniętego zakresu dat (maks. 366 dni); idempotentne, zwraca liczbę wpisów i sumę minut |
| `POST /api/time-entries/{id}/archive` | Rozlicza pojedynczy wpis i zwraca go; wpis już rozliczony to 409 (data rozliczenia jest wartością audytową i nie przesuwa się przy drugiej próbie) |
| `DELETE /api/time-entries/{id}` | Cofa zatwierdzenie: usuwa aktywny wpis i przywraca sugestię; 409 dla wpisu rozliczonego |
| `GET /api/timeline?from=&to=` | Agregacja osi czasu per dzień: `[{date, pendingCount, activeCount, archivedCount}]`; jedno żądanie na cały miesiąc (maks. 366 dni), zatwierdzona sugestia liczona raz (jako wpis) |
| `GET /api/timeline/{date}` | Pozycje jednego dnia (oczekujące sugestie + wpisy) posortowane po godzinie startu, ze statusem `pending`/`active`/`archived` |
| `GET /api/timeline/document-activity?externalId=` | Chronologia modyfikacji dokumentu z dziennika `DocumentActivity` (`versions`) razem z pozycjami, które z niej powstały (`sessions`: zakres, stan, numer edycji, przerwy): godzina i rozmiar każdej wersji, przerwy między nimi z dwoma znacznikami (`isSessionBreak`: dłuższa niż próg ciągłości, `splitsSession`: rozcinająca pracę na dwie sesje); `isCurrent` na każdym wierszu należącym do najnowszej wersji (jej treść pobiera się z endpointu pliku, nie wersji), `isSameVersionAsPrevious` na kolejnych próbkach tej samej wersji |
| `GET /api/summary` | Liczniki do kafelków podsumowania |
