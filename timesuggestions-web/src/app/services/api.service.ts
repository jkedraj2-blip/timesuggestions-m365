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
  SyncRequest,
  SyncResult,
  TimeEntry,
} from '../models/api.models';
import { GraphEvent } from '../models/graph.models';

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
  async syncNow(): Promise<SyncResult> {
    const [events, files] = await Promise.all([
      this.graphCalendar.getEventsLastDays(SYNC_DAYS_BACK),
      this.graphFiles.getRecentDocuments(SYNC_DAYS_BACK),
    ]);

    const request: SyncRequest = {
      calendarEvents: events.map((event) => this.toCalendarPayload(event)),
      driveFiles: files,
    };

    return this.requestJson<SyncResult>('POST', '/api/sync', request);
  }

  getSuggestions(source?: SuggestionSource): Promise<Suggestion[]> {
    const query = source ? `?source=${source}` : '';
    return this.requestJson<Suggestion[]>('GET', `/api/suggestions${query}`);
  }

  approve(suggestionId: number, payload: ApprovePayload): Promise<TimeEntry> {
    return this.requestJson<TimeEntry>('POST', `/api/suggestions/${suggestionId}/approve`, payload);
  }

  async reject(suggestionId: number): Promise<void> {
    await this.request('POST', `/api/suggestions/${suggestionId}/reject`);
  }

  getCases(): Promise<CaseInfo[]> {
    return this.requestJson<CaseInfo[]>('GET', '/api/cases');
  }

  getTimeEntries(): Promise<TimeEntry[]> {
    return this.requestJson<TimeEntry[]>('GET', '/api/time-entries');
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
