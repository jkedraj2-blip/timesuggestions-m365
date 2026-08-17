import { Injectable, inject } from '@angular/core';
import { environment } from '../../environments/environment';
import { GraphCalendarService } from './graph-calendar.service';
import { GraphFilesService } from './graph-files.service';
import { SYNC_DAYS_BACK } from './graph-config';
import {
  ApprovePayload,
  ArchiveSuggestionsResult,
  ArchiveTimeEntriesResult,
  CalendarEventPayload,
  CaseInfo,
  CaseWritePayload,
  Suggestion,
  SuggestionSource,
  SuggestionStatus,
  Summary,
  SyncReport,
  SyncRequest,
  TimeEntriesResponse,
  TimeEntry,
  DocumentHistory,
  TimelineDay,
  TimelineItem,
} from '../models/api.models';
import { GraphEvent } from '../models/graph.models';

/** Etapy synchronizacji raportowane do UI — user widzi, że coś się dzieje. */
export type SyncStage =
  | { kind: 'calendar'; page: number }
  | { kind: 'files'; page: number }
  | { kind: 'versions'; done: number; total: number }
  | { kind: 'processing' };

/** Wydarzenia o tych poufnościach nie opuszczają przeglądarki (spójnie z filtrem backendu). */
const EXCLUDED_SENSITIVITIES = ['personal', 'private', 'confidential'];

/**
 * REST do backendu TimeSuggestions. Celowo bez nagłówka Authorization —
 * token Graph nigdy nie opuszcza przeglądarki, backend dostaje tylko surowe dane.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private graphCalendar = inject(GraphCalendarService);
  private graphFiles = inject(GraphFilesService);
  private baseUrl = environment.apiBaseUrl;

  /**
   * Pobiera dane z obu źródeł Graph i przekazuje backendowi do filtrowania i dopasowania.
   * Kompletność snapshotu kalendarza jest deklarowana jawnie (calendarSnapshotComplete):
   * częściowo pobrany kalendarz trafia do backendu bez tej deklaracji, więc
   * destrukcyjna rekonsyliacja się nie uruchamia.
   */
  async syncNow(
    onStage?: (stage: SyncStage) => void,
    syncDaysBack?: number,
  ): Promise<SyncReport> {
    // Zakres wybrany w UI; brak wartości = domyślne okno (SYNC_DAYS_BACK).
    const days = syncDaysBack ?? SYNC_DAYS_BACK;
    const calendarSnapshot = await this.graphCalendar.getEventsLastDays(days, (page) =>
      onStage?.({ kind: 'calendar', page }),
    );
    const rawEvents = calendarSnapshot.events;

    // Filtr prywatności w przeglądarce: TYTUŁY wydarzeń prywatnych/poufnych/osobistych
    // i anulowanych nie mogą w ogóle trafić do API. Backend powtarza swoje filtry
    // (klientowi nie ufa) — a odrzucenia stąd raportujemy licznikami, żeby raport
    // syncu pokazywał prawdę.
    let privateCount = 0;
    let cancelledCount = 0;
    const events = rawEvents.filter((event) => {
      if (event.isCancelled) {
        cancelledCount++;
        return false;
      }
      if (event.sensitivity && EXCLUDED_SENSITIVITIES.includes(event.sensitivity.toLowerCase())) {
        privateCount++;
        return false;
      }
      return true;
    });

    const driveResult = await this.graphFiles.getRecentDocuments(
      days,
      (page) => onStage?.({ kind: 'files', page }),
      ({ done, total }) => onStage?.({ kind: 'versions', done, total }),
    );

    onStage?.({ kind: 'processing' });
    const request: SyncRequest = {
      calendarEvents: events.map((event) => this.toCalendarPayload(event)),
      // Destrukcyjna rekonsyliacja kalendarza w backendzie tylko przy KOMPLETNYM
      // snapshocie okna — częściowe pobranie nie może kasować prawidłowych sugestii.
      calendarSnapshotComplete: calendarSnapshot.snapshotComplete,
      // Deklaracja zakresu snapshotu: backend kasuje oczekujące sugestie wyłącznie
      // w przecięciu tego zakresu ze swoim oknem, więc rozjazd okien frontend/backend
      // zawęża kasowanie zamiast niszczyć dane.
      calendarSnapshotDaysBack: days,
      // Nadpisanie okna backendu tą samą wartością — jedno źródło prawdy dla
      // pobierania, filtrowania i tekstów raportu (raport odsyła windowDays).
      syncDaysBack: days,
      driveFiles: driveResult.files,
      deletedDriveFileIds: driveResult.deletedDriveFileIds,
      clientFilteredCounts: {
        private: privateCount,
        cancelled: cancelledCount,
        documentsNotOfficeDocument: driveResult.documentsNotOfficeDocument,
      },
      driveFileVersionFetchErrors: driveResult.versionFetchErrors,
    };

    const report = await this.requestJson<SyncReport>('POST', '/api/sync', request);
    // Nazwy pominiętych plików dopinamy lokalnie — nie były i nie będą wysyłane.
    report.skippedNotOfficeNames = driveResult.notOfficeDocumentNames;

    // Wskaźnik delta przesuwamy dopiero po udanym zapisie (obejmującym też
    // tombstone'y) — nieudany sync nie gubi zmian.
    this.graphFiles.commitDeltaLink();

    return report;
  }

  getSuggestions(filter?: {
    status?: SuggestionStatus;
    source?: SuggestionSource;
  }): Promise<Suggestion[]> {
    const params = new URLSearchParams();
    if (filter?.status) params.set('status', filter.status);
    if (filter?.source) params.set('source', filter.source);
    const query = params.size > 0 ? `?${params}` : '';
    return this.requestJson<Suggestion[]>('GET', `/api/suggestions${query}`);
  }

  /** Scalenie sesji tego samego dokumentu w jedną sugestię (opcjonalnie z wolnymi lukami). */
  mergeSuggestions(suggestionIds: number[], includeGaps: boolean): Promise<Suggestion[]> {
    return this.requestJson<Suggestion[]>('POST', '/api/suggestions/merge', { suggestionIds, includeGaps });
  }

  /**
   * Rozdziela wolną lukę: minutes trafia do tej sugestii, neighborMinutes do sąsiedniej,
   * reszta zostaje wolna. Bez obu wartości — cała luka do tej sugestii. Rozmiar luki
   * i tak przelicza serwer; stąd idzie wyłącznie decyzja użytkownika o podziale.
   */
  claimSuggestionGap(
    suggestionId: number,
    direction: 'before' | 'after',
    minutes?: number,
    neighborMinutes?: number,
  ): Promise<Suggestion[]> {
    return this.requestJson<Suggestion[]>(
      'POST', `/api/suggestions/${suggestionId}/claim-gap`, { direction, minutes, neighborMinutes });
  }

  approve(suggestionId: number, payload: ApprovePayload): Promise<TimeEntry> {
    return this.requestJson<TimeEntry>('POST', `/api/suggestions/${suggestionId}/approve`, payload);
  }

  async reject(suggestionId: number): Promise<void> {
    await this.request('POST', `/api/suggestions/${suggestionId}/reject`);
  }

  async restore(suggestionId: number): Promise<void> {
    await this.request('POST', `/api/suggestions/${suggestionId}/restore`);
  }

  /** Archiwizuje pojedynczą odrzuconą sugestię — operacja jednokierunkowa (bez unarchive). */
  async archiveSuggestion(suggestionId: number): Promise<void> {
    await this.request('POST', `/api/suggestions/${suggestionId}/archive`);
  }

  /** Hurtowa archiwizacja wszystkich odrzuconych — zwraca licznik do komunikatu. */
  archiveRejectedSuggestions(): Promise<ArchiveSuggestionsResult> {
    return this.requestJson<ArchiveSuggestionsResult>('POST', '/api/suggestions/archive-rejected');
  }

  getCases(includeInactive = false): Promise<CaseInfo[]> {
    const query = includeInactive ? '?includeInactive=true' : '';
    return this.requestJson<CaseInfo[]>('GET', `/api/cases${query}`);
  }

  createCase(payload: CaseWritePayload): Promise<CaseInfo> {
    return this.requestJson<CaseInfo>('POST', '/api/cases', payload);
  }

  updateCase(caseId: number, payload: CaseWritePayload): Promise<CaseInfo> {
    return this.requestJson<CaseInfo>('PUT', `/api/cases/${caseId}`, payload);
  }

  async setCaseActive(caseId: number, isActive: boolean): Promise<void> {
    await this.request('POST', `/api/cases/${caseId}/${isActive ? 'activate' : 'deactivate'}`);
  }

  getTimeEntries(archived = false): Promise<TimeEntriesResponse> {
    const query = archived ? '?archived=true' : '';
    return this.requestJson<TimeEntriesResponse>('GET', `/api/time-entries${query}`);
  }

  /** Rozliczenie hurtowe: domknięty zakres dat dziennych w formacie ISO (yyyy-MM-dd). */
  archiveTimeEntries(from: string, to: string): Promise<ArchiveTimeEntriesResult> {
    return this.requestJson<ArchiveTimeEntriesResult>('POST', '/api/time-entries/archive', { from, to });
  }

  /** Rozliczenie POJEDYNCZEGO wpisu — gotowość do faktury jest cechą wpisu, nie dnia. */
  archiveTimeEntry(timeEntryId: number): Promise<TimeEntry> {
    return this.requestJson<TimeEntry>('POST', `/api/time-entries/${timeEntryId}/archive`);
  }

  async deleteTimeEntry(timeEntryId: number): Promise<void> {
    await this.request('DELETE', `/api/time-entries/${timeEntryId}`);
  }

  /** Scala wpisy jednej sesji dokumentu; includeGaps dolicza wolne luki między sesjami. */
  mergeTimeEntries(timeEntryIds: number[], includeGaps: boolean): Promise<TimeEntry> {
    return this.requestJson<TimeEntry>('POST', '/api/time-entries/merge', { timeEntryIds, includeGaps });
  }

  /** Odwraca scalenie — przywraca wpisy składowe z ich sesji. */
  unmergeTimeEntry(timeEntryId: number): Promise<TimeEntry[]> {
    return this.requestJson<TimeEntry[]>('POST', `/api/time-entries/${timeEntryId}/unmerge`);
  }

  /**
   * Włącza albo wyłącza przerwę z rozliczanego czasu wpisu. Zakres musi pochodzić
   * z listy detectedGaps wpisu (backend liczy ją z historii wersji), a kierunek musi
   * zgadzać się z jej obecnym stanem — inaczej odpowiedź to 409.
   */
  setGapCounted(
    timeEntryId: number,
    gapStartAt: string,
    gapEndAt: string,
    counted: boolean,
  ): Promise<TimeEntry> {
    const action = counted ? 'add-gap' : 'subtract-gap';
    return this.requestJson<TimeEntry>('POST', `/api/time-entries/${timeEntryId}/${action}`, {
      gapStartAt,
      gapEndAt,
    });
  }

  /** Zaokrągla czas wpisu do jednostki rozliczeniowej z konfiguracji backendu. */
  roundTimeEntry(timeEntryId: number): Promise<TimeEntry> {
    return this.requestJson<TimeEntry>('POST', `/api/time-entries/${timeEntryId}/round`);
  }

  /** Szybka korekta czasu wpisu o ±N minut. */
  adjustTimeEntry(timeEntryId: number, minutes: number): Promise<TimeEntry> {
    return this.requestJson<TimeEntry>('POST', `/api/time-entries/${timeEntryId}/adjust`, { minutes });
  }

  getSummary(): Promise<Summary> {
    return this.requestJson<Summary>('GET', '/api/summary');
  }

  /** Agregacja osi czasu per dzień — jedno żądanie na cały miesiąc, nie 31. */
  getTimeline(from: string, to: string): Promise<TimelineDay[]> {
    return this.requestJson<TimelineDay[]>('GET', `/api/timeline?from=${from}&to=${to}`);
  }

  /** Pozycje jednego dnia osi czasu, posortowane po godzinie startu. */
  getTimelineDay(date: string): Promise<TimelineItem[]> {
    return this.requestJson<TimelineItem[]>('GET', `/api/timeline/${date}`);
  }

  /**
   * Chronologia modyfikacji dokumentu razem z pozycjami, które z niej powstały.
   * Jedno żądanie na obie rzeczy: stan pozycji jest częścią tej samej odpowiedzi,
   * więc nie może rozjechać się z wersjami.
   */
  getDocumentHistory(externalId: string): Promise<DocumentHistory> {
    return this.requestJson<DocumentHistory>(
      'GET',
      `/api/timeline/document-activity?externalId=${encodeURIComponent(externalId)}`,
    );
  }

  private toCalendarPayload(event: GraphEvent): CalendarEventPayload {
    return {
      id: event.id,
      subject: event.subject ?? null,
      startDateTime: event.start.dateTime,
      endDateTime: event.end.dateTime,
      isAllDay: event.isAllDay,
      sensitivity: event.sensitivity ?? null,
      isCancelled: event.isCancelled ?? false,
    };
  }

  private async requestJson<T>(method: string, path: string, body?: unknown): Promise<T> {
    const response = await this.request(method, path, body);
    return (await response.json()) as T;
  }

  private async request(method: string, path: string, body?: unknown): Promise<Response> {
    const response = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });

    if (!response.ok) {
      throw new Error(await this.readErrorMessage(response));
    }

    return response;
  }

  /** Backend zwraca { message } dla błędów domenowych — pokazujemy go zamiast surowego statusu. */
  private async readErrorMessage(response: Response): Promise<string> {
    try {
      const body = await response.json();
      if (typeof body?.message === 'string') {
        return body.message;
      }
      if (typeof body?.title === 'string') {
        return body.title; // ProblemDetails z walidacji ASP.NET Core
      }
    } catch {
      // Odpowiedź bez JSON — poniżej komunikat ogólny.
    }
    return `Błąd serwera (${response.status}).`;
  }
}
