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
  /** Rozmiar pliku w bajtach — trafia do dziennika razem z próbką aktywności. */
  size: number;
  lastModifiedByMe: boolean;
  /**
   * Historia wersji pliku; null = pobranie wersji nie powiodło się dla tego pliku
   * (backend buduje wtedy sugestię z minimum czasu i prośbą o uzupełnienie).
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
  /** Ile pobrań historii wersji padło po stronie klienta — raport wersji ma pokazywać prawdę. */
  driveFileVersionFetchErrors?: number;
}

/**
 * Przerwa w zasięgu pozycji (czasy strefy biznesowej). `counted` mówi, czy jej minuty
 * wchodzą do rozliczanego czasu: przerwa wewnątrz sesji jest liczona (można ją odjąć),
 * przerwa między scalonymi sesjami nie jest (można ją doliczyć).
 */
export interface DetectedGap {
  startAt: string;
  endAt: string;
  minutes: number;
  counted: boolean;
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
  /** Czas wyliczony z jednego zapisu — karta prosi o wpisanie go zamiast podsuwać zgadywaną wartość. */
  needsTimeReview: boolean;
  /** Id pliku z Graph (tylko dokumenty) — po nim karta pobiera chronologię modyfikacji. */
  sourceExternalId: string | null;
  /** Ostatnia znana modyfikacja (UTC) — po niej idzie kolejność listy sugestii. */
  lastActivityAt: string;
  /** Czas poprawiony ręcznie (scalenie, doliczona luka) — synchronizacja go nie przelicza. */
  isUserAdjusted: boolean;
  /** Wolne luki wokół sugestii; null = nie ma czego doliczać po żadnej ze stron. */
  gaps: SuggestionGaps | null;
  /** „edycja 3" — numer sesji w całej historii pliku; null dla pozycji kalendarzowych. */
  sessionLabel: string | null;
}

/**
 * Sąsiad sugestii na osi dnia razem z wolną luką dzielącą ich od siebie. Backend
 * przysyła to WYŁĄCZNIE dla luk faktycznie wolnych i mieszczących się w limicie —
 * jeśli w tym czasie trwała praca nad innym dokumentem, luki tu nie ma, bo ten czas
 * jest już rozliczony gdzie indziej.
 */
export interface SuggestionNeighbor {
  /** Id sąsiada, gdy jest sugestią oczekującą; null dla wpisu czasu (podziału z nim nie ma). */
  suggestionId: number | null;
  title: string;
  gapMinutes: number;
  /**
   * Scalenie z tym sąsiadem na pewno przejdzie (ta sama sugestia oczekująca, ten sam
   * plik, ten sam dzień). Backend liczy to sam — karta nie zgaduje po nazwie pliku
   * i nie wystawia przycisku, po którym przychodzi odmowa.
   */
  canMerge: boolean;
}

export interface SuggestionGaps {
  before: SuggestionNeighbor | null;
  after: SuggestionNeighbor | null;
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
  /** Przerwy leżące w godzinach wpisu, liczone z historii wersji razem z ich stanem. */
  detectedGaps: DetectedGap[];
  /** „edycja 3" — numer sesji w całej historii pliku; null dla wpisów kalendarzowych. */
  sessionLabel: string | null;
  /**
   * Czas po zaokrągleniu do jednostki rozliczeniowej — liczy go backend, żeby etykieta
   * przycisku nie mogła obiecać innej wartości niż zapisze operacja. Równy
   * durationMinutes = nie ma czego zaokrąglać.
   */
  roundedDurationMinutes: number;
  /**
   * Suma korekt z zaokrąglania (dodatnia = dołożono, ujemna = zdjęto); 0 = nie
   * zaokrąglano. Osobno od przerw, bo to inna decyzja: doliczona do różnicy między
   * godzinami a czasem zamieniała się w komunikat o „nieliczonych przerwach",
   * których w historii wersji nikt nigdy nie widział.
   */
  roundingMinutes: number;
  /**
   * Zdanie o tym, co stało się z godzinami wpisu przy zatwierdzaniu (przycięcie do
   * sąsiada, pozostałe pokrycie). Wypełniane tylko w odpowiedzi na zatwierdzenie.
   */
  notice: string | null;
  /**
   * Liczba korekt w dzienniku wpisu. Rozdzielenie scalonego wpisu kasuje jego korekty
   * (dotyczyły bytu, który przestaje istnieć) — potwierdzenie „Rozdziel" mówi,
   * ile ich przepadnie.
   */
  adjustmentCount: number;
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

/** Stan pozycji powstałej z historii pliku — niesie kolor obszaru i treść plakietki. */
export type DocumentSessionKind = 'pending' | 'rejected' | 'archived' | 'unsettled' | 'settled';

/**
 * Jedna pozycja powstała z tego pliku: sugestia albo wpis czasu. Sugestia zatwierdzona
 * nie jest osobną pozycją — reprezentuje ją wpis, który z niej powstał.
 */
export interface DocumentSession {
  kind: DocumentSessionKind;
  startAt: string;
  endAt: string;
  suggestionId: number | null;
  timeEntryId: number | null;
  /** „edycja 3" — ten sam numer, który pozycja nosi na swojej karcie. */
  label: string | null;
  gaps: DetectedGap[];
}

/**
 * Chronologia pliku razem ze wszystkimi pozycjami, które z niej powstały. Stan pozycji
 * przychodzi z bazy, a nie od tego, kto historię otworzył — inaczej ta sama historia
 * pokazywała pracę raz jako rozliczoną, raz jako nierozliczoną.
 */
export interface DocumentHistory {
  versions: DocumentActivityItem[];
  sessions: DocumentSession[];
}

/** Jedna wersja z chronologii modyfikacji dokumentu (poziom 3 osi czasu). */
export interface DocumentActivityItem {
  versionId: string;
  occurredAt: string;
  size: number;
  /** Przerwa od poprzedniej wersji w minutach; null dla pierwszej. */
  gapMinutesSincePrevious: number | null;
  /** Przerwa dłuższa niż próg ciągłości pracy, czyli przestój, a nie pauza w pisaniu. */
  isSessionBreak: boolean;
  /** Przerwa rozcinająca pracę na dwie sesje — domyślnie nie jest wliczona w czas. */
  splitsSession: boolean;
  /**
   * Wiersz należy do bieżącej wersji pliku — jej treść to aktualna treść dokumentu.
   * Dotyczy KAŻDEJ próbki tej wersji, nie tylko ostatniej: wersja jeszcze
   * niezapieczętowana trafia do dziennika wiele razy.
   */
  isCurrent: boolean;
  /** Kolejna próbka tej samej wersji co wiersz wyżej — Graph nie ma stanu pośredniego do porównania. */
  isSameVersionAsPrevious: boolean;
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
  /**
   * Nazwy plików pominiętych jako „inne niż Word/Excel", ustawiane PO STRONIE
   * PRZEGLĄDARKI (backend ich nie widzi — nie ma powodu ich wysyłać). Bez nich raport
   * mówi tylko „pominięto 1 pozycję" i wygląda jak magia, bo delta OneDrive melduje
   * każdą zmianę na dysku, także w plikach spoza tej aplikacji.
   */
  skippedNotOfficeNames?: string[];
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
