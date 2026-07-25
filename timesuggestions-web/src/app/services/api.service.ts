import { Injectable, inject } from '@angular/core';
import { environment } from '../../environments/environment';
import { GraphCalendarService } from './graph-calendar.service';
import { GraphFilesService } from './graph-files.service';
import { SYNC_DAYS_BACK } from './graph-config';
import {
  ApprovePayload,
  CalendarEventPayload,
  CaseInfo,
  Suggestion,
  SuggestionSource,
  SuggestionStatus,
  Summary,
  SyncReport,
  SyncRequest,
  TimeEntriesResponse,
  TimeEntry,
} from '../models/api.models';
import { GraphEvent } from '../models/graph.models';

/** Etapy synchronizacji raportowane do UI — user widzi, że coś się dzieje. */
export type SyncStage =
  | { kind: 'calendar' }
  | { kind: 'files'; page: number }
  | { kind: 'processing' };

/**
 * REST do backendu TimeSuggestions. Celowo bez nagłówka Authorization —
 * token Graph nigdy nie opuszcza przeglądarki, backend dostaje tylko surowe dane.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private graphCalendar = inject(GraphCalendarService);
  private graphFiles = inject(GraphFilesService);
  private baseUrl = environment.apiBaseUrl;

  /** Pobiera dane z obu źródeł Graph i przekazuje backendowi do filtrowania i dopasowania. */
  async syncNow(
    onStage?: (stage: SyncStage) => void,
    defaultDocumentDurationMinutes?: number,
  ): Promise<SyncReport> {
    onStage?.({ kind: 'calendar' });
    const events = await this.graphCalendar.getEventsLastDays(SYNC_DAYS_BACK);

    const files = await this.graphFiles.getRecentDocuments(SYNC_DAYS_BACK, (page) =>
      onStage?.({ kind: 'files', page }),
    );

    onStage?.({ kind: 'processing' });
    const request: SyncRequest = {
      calendarEvents: events.map((event) => this.toCalendarPayload(event)),
      driveFiles: files,
      defaultDocumentDurationMinutes,
    };

    const report = await this.requestJson<SyncReport>('POST', '/api/sync', request);

    // Wskaźnik delta przesuwamy dopiero po udanym zapisie — nieudany sync nie gubi zmian.
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

  approve(suggestionId: number, payload: ApprovePayload): Promise<TimeEntry> {
    return this.requestJson<TimeEntry>('POST', `/api/suggestions/${suggestionId}/approve`, payload);
  }

  async reject(suggestionId: number): Promise<void> {
    await this.request('POST', `/api/suggestions/${suggestionId}/reject`);
  }

  async restore(suggestionId: number): Promise<void> {
    await this.request('POST', `/api/suggestions/${suggestionId}/restore`);
  }

  getCases(): Promise<CaseInfo[]> {
    return this.requestJson<CaseInfo[]>('GET', '/api/cases');
  }

  getTimeEntries(): Promise<TimeEntriesResponse> {
    return this.requestJson<TimeEntriesResponse>('GET', '/api/time-entries');
  }

  async deleteTimeEntry(timeEntryId: number): Promise<void> {
    await this.request('DELETE', `/api/time-entries/${timeEntryId}`);
  }

  getSummary(): Promise<Summary> {
    return this.requestJson<Summary>('GET', '/api/summary');
  }

  private toCalendarPayload(event: GraphEvent): CalendarEventPayload {
    return {
      id: event.id,
      subject: event.subject ?? null,
      startDateTime: event.start.dateTime,
      endDateTime: event.end.dateTime,
      isAllDay: event.isAllDay,
      sensitivity: event.sensitivity ?? null,
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
