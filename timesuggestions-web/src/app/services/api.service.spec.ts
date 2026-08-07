import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import { SyncRequest } from '../models/api.models';
import { GraphEvent } from '../models/graph.models';

function createEvent(id: string, overrides: Partial<GraphEvent> = {}): GraphEvent {
  return {
    id,
    subject: `Spotkanie ${id}`,
    start: { dateTime: '2026-08-06T10:00:00', timeZone: 'Central European Standard Time' },
    end: { dateTime: '2026-08-06T11:00:00', timeZone: 'Central European Standard Time' },
    isAllDay: false,
    sensitivity: 'normal',
    isCancelled: false,
    ...overrides,
  };
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

const EMPTY_REPORT = {
  fetched: { calendarEvents: 1, driveFiles: 0 },
  filteredOut: {
    private: 2, tooShort: 0, allDay: 0, cancelled: 1, invalidDates: 0,
    notOfficeDocument: 0, outsideWindow: 0, notModifiedByUser: 0, total: 3,
  },
  aggregated: 0,
  created: 1,
  updated: 0,
  skippedExisting: 0,
  removed: 0,
  matched: { single: 1, none: 0, ambiguous: 0 },
};

describe('ApiService.syncNow', () => {
  let service: ApiService;

  beforeEach(() => {
    // Stabilna data "teraz" (tylko Date — setTimeout zostaje prawdziwy).
    vi.useFakeTimers({ now: new Date('2026-08-07T12:00:00Z'), toFake: ['Date'] });
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: AuthService,
          useValue: {
            getToken: () => Promise.resolve('token'),
            account: { homeAccountId: 'account-1', username: 'user@example.com', name: 'User' },
          },
        },
      ],
    });
    service = TestBed.inject(ApiService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('filtruje wydarzenia prywatne/osobiste/anulowane przed wysyłką i raportuje liczniki klienckie', async () => {
    let capturedRequest: SyncRequest | null = null;
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/me/calendarView')) {
        return jsonResponse({
          value: [
            createEvent('event-normal'),
            createEvent('event-private', { sensitivity: 'private', subject: 'Wizyta u lekarza' }),
            createEvent('event-personal', { sensitivity: 'Personal', subject: 'Trening' }),
            createEvent('event-cancelled', { isCancelled: true, subject: 'Odwołane' }),
          ],
        });
      }
      if (url.includes('/me/drive/root/delta')) {
        return jsonResponse({
          value: [{ id: 'file-deleted', deleted: { state: 'deleted' } }],
          '@odata.deltaLink': 'https://graph.microsoft.com/v1.0/me/drive/root/delta?token=abc',
        });
      }
      if (url.includes('/api/sync')) {
        capturedRequest = JSON.parse(String(init?.body)) as SyncRequest;
        return jsonResponse(EMPTY_REPORT);
      }
      throw new Error(`Nieoczekiwany adres w teście: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    const report = await service.syncNow();

    expect(capturedRequest).not.toBeNull();
    const request = capturedRequest!;
    // Tytuły wydarzeń prywatnych/osobistych/anulowanych nie opuszczają przeglądarki.
    expect(request.calendarEvents.map((event) => event.id)).toEqual(['event-normal']);
    const serialized = JSON.stringify(request);
    expect(serialized).not.toContain('Wizyta u lekarza');
    expect(serialized).not.toContain('Trening');
    expect(serialized).not.toContain('Odwołane');
    // Liczniki filtrów klienckich — raport ma pokazywać prawdę.
    expect(request.clientFilteredCounts).toEqual({
      private: 2,
      cancelled: 1,
      documentsOutsideWindow: 0,
      documentsNotOfficeDocument: 0,
    });
    expect(request.deletedDriveFileIds).toEqual(['file-deleted']);
    // Kalendarz pobrany w całości → jawna deklaracja kompletności snapshotu.
    expect(request.calendarSnapshotComplete).toBe(true);
    expect(report.created).toBe(1);
  });

  it('nie deklaruje kompletności snapshotu, gdy pobieranie stron kalendarza przerwał błąd', async () => {
    let capturedRequest: SyncRequest | null = null;
    const nextLink = 'https://graph.microsoft.com/v1.0/me/calendarView?$skip=50';
    let calendarCall = 0;
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/me/calendarView')) {
        calendarCall++;
        return calendarCall === 1
          ? jsonResponse({ value: [createEvent('event-1')], '@odata.nextLink': nextLink })
          : new Response('server error', { status: 500 });
      }
      if (url.includes('/me/drive/root/delta')) {
        return jsonResponse({ value: [] });
      }
      if (url.includes('/api/sync')) {
        capturedRequest = JSON.parse(String(init?.body)) as SyncRequest;
        return jsonResponse(EMPTY_REPORT);
      }
      throw new Error(`Nieoczekiwany adres w teście: ${url}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    await service.syncNow();

    expect(capturedRequest).not.toBeNull();
    const request = capturedRequest!;
    // Częściowy snapshot: pobrane spotkania płyną dalej, ale bez deklaracji
    // kompletności backend nie usunie sugestii nieobecnych spotkań.
    expect(request.calendarEvents.map((event) => event.id)).toEqual(['event-1']);
    expect(request.calendarSnapshotComplete).toBe(false);
  });
});
