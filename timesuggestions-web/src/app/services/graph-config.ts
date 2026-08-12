/** Wspólne stałe wywołań Microsoft Graph. */

export const GRAPH_BASE_URL = 'https://graph.microsoft.com/v1.0';

/**
 * Strefa czasowa odpowiedzi kalendarza — Graph domyślnie zwraca czasy w UTC,
 * a błąd strefy przekłada się wprost na złe godziny wpisów czasu pracy.
 */
export const OUTLOOK_TIMEZONE = 'Central European Standard Time';

/**
 * DOMYŚLNE okno synchronizacji w dniach — użytkownik może je zmienić w UI
 * (SYNC_DAYS_OPTIONS w suggestions-page). Wybrana wartość jedzie w żądaniu
 * jako syncDaysBack (nadpisanie okna backendu) i calendarSnapshotDaysBack
 * (granica destrukcyjnej rekonsyliacji — kasowanie działa tylko w przecięciu
 * okien, więc rozjazd konfiguracji zawęża kasowanie zamiast niszczyć dane).
 */
export const SYNC_DAYS_BACK = 7;

/**
 * Limit równoczesnych żądań historii wersji (per plik). Wersje pobieramy osobnym
 * żądaniem na każdy plik z okna — bez limitu duży sync odpaliłby dziesiątki
 * równoległych wywołań i wpadł w throttling Graph (429).
 */
export const VERSIONS_FETCH_CONCURRENCY = 4;

/**
 * Zapas dobierany do okna przy pobieraniu z Graph. Backend tnie okno po początku
 * dnia LOKALNEGO w strefie biznesowej, a tu odejmujemy godziny w UTC — przy zmianie
 * czasu doby lokalne i 24-godzinne interwały rozjeżdżają się o godzinę. Pobieramy
 * szerzej; jedynym źródłem prawdy filtru okna pozostaje backend.
 *
 * Realny zapas jest przez to WIĘKSZY niż ta stała: skoro okno backendu zaczyna się
 * o północy, a my liczymy godziny od „teraz", przy kalendarzu pobieramy ponad dwie
 * doby ponad okno. Pozycje z tego zapasu backend odrzuca i celowo NIE raportuje ich
 * jako pominiętych ani pobranych — inaczej każda synchronizacja meldowała ten sam
 * stały nadmiar jako „Pominięto N pozycji" (patrz SyncFilteredOutCounts).
 */
export const FETCH_MARGIN_DAYS = 1;

/**
 * Odstęp automatycznej synchronizacji w tle (AutoSyncService).
 *
 * Wartość wynika z silnika sesji, a nie z „co jakiś czas": przerwa dłuższa niż
 * SessionContinuationGapMinutes (15 min w appsettings) rozbija ciągłą pracę na dwie
 * sesje. Skoro każda synchronizacja jest POMIAREM (dopisuje próbkę
 * driveItem.lastModifiedDateTime do dziennika), próbkowanie musi być gęstsze niż ten
 * próg. 10 minut daje zapas na opóźnienie ticka w karcie w tle (przeglądarki dławią
 * timery ukrytych kart do ~1/min) bez przekraczania progu.
 *
 * Czego to NIE gwarantuje: o tym, kiedy w ogóle powstaje ślad do zmierzenia, decyduje
 * Word, nie my. Gęstsze próbkowanie zwiększa szansę na wierne odtworzenie sesji i
 * ogranicza dziurę w danych do kwadransa — nie obiecuje, że długa praca zawsze rozbije
 * się na sesje.
 *
 * W drugą stronę: przebieg pobiera CAŁE okno kalendarza (calendarView nie jest
 * przyrostowe), za to pliki idą deltą, a historia wersji tylko dla plików, które delta
 * zwróciła jako zmienione — cichy tick to kilka żądań. Przy ~6 przebiegach na godzinę
 * jest to daleko poniżej limitów throttlingu Graph.
 */
export const AUTO_SYNC_INTERVAL_MINUTES = 10;

/**
 * Po tylu nieudanych przebiegach z rzędu automat sam się wyłącza. Typowa przyczyna to
 * wygasła sesja Microsoft wymagająca logowania — bez tego licznika aplikacja dobijałaby
 * się do Graph w kółko, a użytkownik nie miałby pojęcia dlaczego.
 */
export const AUTO_SYNC_MAX_FAILURES = 3;
