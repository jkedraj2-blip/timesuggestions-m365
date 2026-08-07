/** Kontrakty backendu TimeSuggestions — 1:1 z DTO w C# (enumy serializowane jako camelCase). */

export type SuggestionSource = 'calendar' | 'document';
export type SuggestionStatus = 'pending' | 'approved' | 'rejected';

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
  driveFiles: DriveFilePayload[];
  /** Tombstone'y z delta OneDrive — backend usuwa oczekujące sugestie usuniętych plików. */
  deletedDriveFileIds?: string[];
  /** Liczniki filtrów klienckich (prywatność + wstępne filtrowanie dokumentów). */
  clientFilteredCounts?: ClientFilteredCounts;
  /** Opcjonalna preferencja użytkownika — bez wartości obowiązuje konfiguracja backendu. */
  defaultDocumentDurationMinutes?: number;
}

export interface Suggestion {
  id: number;
  source: SuggestionSource;
  title: string;
  startedAt: string;
  durationMinutes: number;
  caseId: number | null;
  caseName: string | null;
  isAmbiguous: boolean;
  /** Nazwy spraw pasujących przy niejednoznacznym dopasowaniu — UI mówi konkretnie "pasuje do X i Y". */
  matchCandidates: string[];
  proposedDescription: string;
  status: SuggestionStatus;
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
  entryDate: string;
  durationMinutes: number;
  description: string;
  createdFromSuggestion: boolean;
  source: SuggestionSource;
  suggestionId: number;
  /** Tytuł spotkania / nazwa pliku, z którego powstał wpis — kotwica w realnym zdarzeniu. */
  sourceTitle: string | null;
  sourceStartedAt: string | null;
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

/** Liczniki dla kafelków podsumowania. */
export interface Summary {
  pendingCount: number;
  approvedCount: number;
  rejectedCount: number;
  totalLoggedMinutes: number;
  todayLoggedMinutes: number;
  lastSyncAt: string | null;
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

/** Pełny raport synchronizacji — aplikacja pokazuje użytkownikowi swoją pracę. */
export interface SyncReport {
  fetched: SyncFetchedCounts;
  filteredOut: SyncFilteredOutCounts;
  aggregated: number;
  created: number;
  /** Istniejące sugestie oczekujące odświeżone po zmianie źródła (np. nowej nazwie pliku lub dacie spotkania). */
  updated: number;
  skippedExisting: number;
  /** Oczekujące usunięte przez rekonsyliację (spotkania zniknięte/nierozliczalne, tombstone'y plików). */
  removed: number;
  matched: SyncMatchedCounts;
}
