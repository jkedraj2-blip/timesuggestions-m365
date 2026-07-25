/** Wspólne stałe wywołań Microsoft Graph. */

export const GRAPH_BASE_URL = 'https://graph.microsoft.com/v1.0';

/**
 * Strefa czasowa odpowiedzi kalendarza — Graph domyślnie zwraca czasy w UTC,
 * a błąd strefy przekłada się wprost na złe godziny wpisów czasu pracy.
 */
export const OUTLOOK_TIMEZONE = 'Central European Standard Time';

/** Okno synchronizacji w dniach — odpowiednik Suggestions:SyncDaysBack w backendzie. */
export const SYNC_DAYS_BACK = 7;
