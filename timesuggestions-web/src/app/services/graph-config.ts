/** Wspólne stałe wywołań Microsoft Graph. */

export const GRAPH_BASE_URL = 'https://graph.microsoft.com/v1.0';

/**
 * Strefa czasowa odpowiedzi kalendarza — Graph domyślnie zwraca czasy w UTC,
 * a błąd strefy przekłada się wprost na złe godziny wpisów czasu pracy.
 */
export const OUTLOOK_TIMEZONE = 'Central European Standard Time';

/** Okno synchronizacji w dniach — odpowiednik Suggestions:SyncDaysBack w backendzie. */
export const SYNC_DAYS_BACK = 7;

/**
 * Zapas dobierany do okna przy pobieraniu z Graph. Backend tnie okno po początku
 * dnia LOKALNEGO w strefie biznesowej, a tu odejmujemy godziny w UTC — przy zmianie
 * czasu doby lokalne i 24-godzinne interwały rozjeżdżają się o godzinę. Pobieramy
 * szerzej; jedynym źródłem prawdy filtru okna pozostaje backend.
 */
export const FETCH_MARGIN_DAYS = 1;
