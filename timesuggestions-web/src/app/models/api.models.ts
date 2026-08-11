/** Kontrakty backendu TimeSuggestions — 1:1 z DTO w C# (enumy serializowane jako camelCase). */

export type SuggestionSource = 'calendar' | 'document';
export type SuggestionStatus = 'pending' | 'approved' | 'rejected' | 'archived';

export interface CalendarEventPayload {
  id: string;
  subject: string | null;
  startDateTime: string;
  endDateTime: string;
  isAllDay: boolean;
  sensitivity: string | null;
  isCancelled: boolean;
}

export interface DriveFilePayload {
  id: string;
  name: string;
  lastModifiedDateTime: string;
  lastModifiedByMe: boolean;
  /**
   * Historia wersji pliku; null = pobranie wersji nie powiodło się dla tego pliku
   * (backend zachowuje wtedy fallback z domyślnym czasem dokumentu).
   */
  versions?: DriveFileVersionPayload[] | null;
}

/** Jedna wersja pliku z historii Graph — surowy fakt do dziennika DocumentActivity w backendzie. */
export interface DriveFileVersionPayload {
  versionId: string;
  lastModifiedDateTime: string;
  size: number;
}

/** Liczniki pozycji odfiltrowanych w przeglądarce — backend dolicza je do raportu. */
export interface ClientFilteredCounts {
  private: number;
  cancelled: number;
  documentsOutsideWindow: number;
  documentsNotOfficeDocument: number;
}

export interface SyncRequest {
  calendarEvents: CalendarEventPayload[];
  /** Czy calendarEvents to kompletny snapshot okna (wszystkie strony pobrane bez błędu) — warunek destrukcyjnej rekonsyliacji. */
  calendarSnapshotComplete: boolean;
  /** Ile ostatnich pełnych dni lokalnych pokrywa snapshot — backend kasuje tylko w przecięciu tego zakresu ze swoim oknem. */
  calendarSnapshotDaysBack?: number;
  /** Nadpisanie okna synchronizacji (preferencja z UI); brak wartości = konfiguracja backendu. */
  syncDaysBack?: number;
  driveFiles: DriveFilePayload[];
  /** Tombstone'y z delta OneDrive — backend usuwa oczekujące sugestie usuniętych plików. */
  deletedDriveFileIds?: string[];
  /** Liczniki filtrów klienckich (prywatność + wstępne filtrowanie dokumentów). */
  clientFilteredCounts?: ClientFilteredCounts;
  /** Opcjonalna preferencja użytkownika — bez wartości obowiązuje konfiguracja backendu. */
  defaultDocumentDurationMinutes?: number;
  /** Ile pobrań historii wersji padło po stronie klienta — raport wersji ma pokazywać prawdę. */
  driveFileVersionFetchErrors?: number;
}

/** Wykryta przerwa sesji (15–30 min między wersjami) — czasy strefy biznesowej. */
export interface DetectedGap {
  startAt: string;
  endAt: string;
  minutes: number;
}

export interface Suggestion {
  id: number;
  source: SuggestionSource;
  title: string;
  startedAt: string;
  durationMinutes: number;
  caseId: number | null;
  caseName: string | null;
  /** Numer i klient czytane na żywo z dopasowanej sprawy (bez snapshotu) — null, gdy brak dopasowania. */
  caseNumber: string | null;
  clientName: string | null;
  isAmbiguous: boolean;
  /** Sprawy pasujące przy niejednoznacznym dopasowaniu, w formacie „Nazwa (Numer)" — UI mówi konkretnie "pasuje do X i Y". */
  matchCandidates: string[];
  proposedDescription: string;
  status: SuggestionStatus;
  /** Wykryte przerwy sesji dokumentowej — przycisk "Odejmij przerwę" bierze dane stąd, nie z heurystyki UI. */
  detectedGaps: DetectedGap[];
}

export interface CaseInfo {
  id: number;
  name: string;
  caseNumber: string;
  clientName: string;
  keywords: string[];
  isActive: boolean;
}

/** Dane sprawy przy tworzeniu i edycji. */
export interface CaseWritePayload {
  name: string;
  caseNumber: string;
  clientName: string;
  keywords: string[];
}

export interface ApprovePayload {
  caseId: number;
  durationMinutes: number;
  description: string;
}

export interface TimeEntry {
  id: number;
  caseId: number;
  caseName: string | null;
  /** Numer i klient czytane na żywo z powiązanej sprawy (bez snapshotu) — null, gdy nawigacja niezaładowana. */
  caseNumber: string | null;
  clientName: string | null;
  entryDate: string;
  /** Początek i koniec wpisu w strefie biznesowej — oś niezmiennika nakładania i osi czasu. */
  startedAt: string;
  endedAt: string;
  durationMinutes: number;
  description: string;
  createdFromSuggestion: boolean;
  source: SuggestionSource;
  /** Sugestie składowe wpisu — po scaleniu sesji jest ich więcej niż jedna. */
  suggestionIds: number[];
  /** Tytuł spotkania / nazwa pliku, z którego powstał wpis — kotwica w realnym zdarzeniu. */
  sourceTitle: string | null;
  sourceStartedAt: string | null;
  /** Id pliku z Graph (wpisy dokumentowe) — po nim UI rozpoznaje wpisy tego samego dokumentu do scalenia. */
  sourceExternalId: string | null;
  /** Moment rozliczenia (UTC); null = wpis aktywny. Zarchiwizowany wpis jest tylko do odczytu. */
  archivedAt: string | null;
  /** Wykryte przerwy przeniesione z sugestii przy zatwierdzeniu. */
  detectedGaps: DetectedGap[];
}

/** Wpisy pogrupowane po dniach z gotowymi sumami z backendu. */
export interface TimeEntriesResponse {
  totalMinutes: number;
  days: TimeEntryDay[];
}

export interface TimeEntryDay {
  date: string;
  totalMinutes: number;
  entries: TimeEntry[];
}

/** Liczniki jednego dnia osi czasu — zatwierdzona sugestia liczona raz, jako wpis. */
export interface TimelineDay {
  date: string;
  pendingCount: number;
  activeCount: number;
  archivedCount: number;
}

/** Pozycja listy dnia na osi czasu. */
export interface TimelineItem {
  /** Typ mówi, do której zakładki nawigować po kliknięciu. */
  type: 'suggestion' | 'timeEntry';
  id: number;
  source: SuggestionSource;
  startedAt: string;
  endedAt: string;
  durationMinutes: number;
  title: string;
  caseName: string | null;
  caseNumber: string | null;
  clientName: string | null;
  /** Status koloruje i etykietuje pozycję (nigdy samym kolorem); archived jest nieklikalne. */
  status: 'pending' | 'active' | 'archived';
  /** Id pliku z Graph dla pozycji dokumentowych — zasila chronologię modyfikacji i diff wersji. */
  externalId: string | null;
}

/** Jedna wersja z chronologii modyfikacji dokumentu (poziom 3 osi czasu). */
export interface DocumentActivityItem {
  versionId: string;
  occurredAt: string;
  size: number;
  /** Przerwa od poprzedniej wersji w minutach; null dla pierwszej. */
  gapMinutesSincePrevious: number | null;
  /** true = przerwa w przedziale wykrywanym (15–30 min). */
  isDetectedGapRange: boolean;
}

/** Liczniki dla kafelków podsumowania. */
export interface Summary {
  pendingCount: number;
  approvedCount: number;
  rejectedCount: number;
  /** Suma minut wpisów AKTYWNYCH — archiwizacja jest jedynym „resetem" tej liczby. */
  unsettledMinutes: number;
  todayLoggedMinutes: number;
  lastSyncAt: string | null;
}

/** Wynik rozliczenia hurtowego wpisów — liczby do komunikatu w UI. */
export interface ArchiveTimeEntriesResult {
  archivedCount: number;
  totalMinutes: number;
}

/** Wynik hurtowej archiwizacji odrzuconych sugestii. */
export interface ArchiveSuggestionsResult {
  archivedCount: number;
}

export interface SyncFetchedCounts {
  calendarEvents: number;
  driveFiles: number;
}

export interface SyncFilteredOutCounts {
  private: number;
  tooShort: number;
  allDay: number;
  cancelled: number;
  invalidDates: number;
  notOfficeDocument: number;
  outsideWindow: number;
  notModifiedByUser: number;
  total: number;
}

export interface SyncMatchedCounts {
  single: number;
  none: number;
  ambiguous: number;
}

/** Liczniki historii wersji z jednego syncu — pomiar gęstości wersji (etap 0 silnika sesji). */
export interface SyncVersionCounts {
  filesWithHistory: number;
  filesWithoutHistory: number;
  fetchErrors: number;
  newActivities: number;
}

/** Pełny raport synchronizacji — aplikacja pokazuje użytkownikowi swoją pracę. */
export interface SyncReport {
  fetched: SyncFetchedCounts;
  filteredOut: SyncFilteredOutCounts;
  aggregated: number;
  /** Duplikaty tego samego klucza scalone w obrębie jednego żądania (np. wydarzenie zduplikowane między stronami calendarView). */
  deduplicated: number;
  created: number;
  /** Istniejące sugestie oczekujące odświeżone po zmianie źródła (np. nowej nazwie pliku lub dacie spotkania). */
  updated: number;
  skippedExisting: number;
  /** Oczekujące usunięte przez rekonsyliację (spotkania zniknięte/nierozliczalne, tombstone'y plików). */
  removed: number;
  matched: SyncMatchedCounts;
  /** Faktycznie użyte okno w dniach — teksty raportu pokazują je zamiast zgadywać z własnej stałej. */
  windowDays: number;
  /** Liczniki historii wersji plików — ile z historią, ile bez, ile błędów pobierania. */
  versions: SyncVersionCounts;
}
