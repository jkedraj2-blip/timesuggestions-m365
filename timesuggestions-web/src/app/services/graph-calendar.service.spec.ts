import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { GraphCalendarService } from './graph-calendar.service';
import { AuthService } from './auth.service';
import { GraphEvent } from '../models/graph.models';

function createEvent(id: string): GraphEvent {
  return {
    id,
    subject: `Spotkanie ${id}`,
    start: { dateTime: '2026-08-06T10:00:00', timeZone: 'Central European Standard Time' },
    end: { dateTime: '2026-08-06T11:00:00', timeZone: 'Central European Standard Time' },
    isAllDay: false,
    sensitivity: 'normal',
    isCancelled: false,
  };
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('GraphCalendarService', () => {
  let service: GraphCalendarService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: { getToken: () => Promise.resolve('token') } }],
    });
    service = TestBed.inject(GraphCalendarService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('pobiera z dobą zapasu ponad okno — granica okna backendu zawsze objęta zapytaniem', async () => {
    // 27.10.2026 — tydzień po jesiennej zmianie czasu. Okno backendu zaczyna się
    // 21.10 00:00 czasu warszawskiego (22:00Z 20.10); zapytanie z zapasem sięga
    // 19.10 12:00Z, więc wydarzenia na styku okna zawsze docierają do backendu,
    // który jest jedynym źródłem prawdy filtru.
    vi.useFakeTimers({ now: new Date('2026-10-27T12:00:00Z'), toFake: ['Date'] });
    const fetchMock = vi.fn().mockResolvedValueOnce(jsonResponse({ value: [] }));
    vi.stubGlobal('fetch', fetchMock);

    await service.getEventsLastDays(7);

    const requestedUrl = new URL(String(fetchMock.mock.calls[0][0]));
    expect(requestedUrl.searchParams.get('startDateTime')).toBe('2026-10-19T12:00:00.000Z');
    expect(requestedUrl.searchParams.get('endDateTime')).toBe('2026-10-27T12:00:00.000Z');
  });

  it('skleja wszystkie strony wyników przez @odata.nextLink', async () => {
    const nextLink = 'https://graph.microsoft.com/v1.0/me/calendarView?$skip=50';
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({ value: [createEvent('event-1')], '@odata.nextLink': nextLink }))
      .mockResolvedValueOnce(jsonResponse({ value: [createEvent('event-2')] }));
    vi.stubGlobal('fetch', fetchMock);
    const pages: number[] = [];

    const snapshot = await service.getEventsLastDays(7, (page) => pages.push(page));

    expect(snapshot.events.map((event) => event.id)).toEqual(['event-1', 'event-2']);
    // Wszystkie strony pobrane bez błędu → snapshot zadeklarowany jako kompletny.
    expect(snapshot.snapshotComplete).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1][0]).toBe(nextLink);
    expect(pages).toEqual([1, 2]);
  });

  it('zwraca częściowy snapshot bez deklaracji kompletności, gdy kolejna strona padnie', async () => {
    const nextLink = 'https://graph.microsoft.com/v1.0/me/calendarView?$skip=50';
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({ value: [createEvent('event-1')], '@odata.nextLink': nextLink }))
      .mockResolvedValueOnce(new Response('server error', { status: 500 }));
    vi.stubGlobal('fetch', fetchMock);

    const snapshot = await service.getEventsLastDays(7);

    // Backend nie może uznać spotkań z niepobranych stron za usunięte.
    expect(snapshot.snapshotComplete).toBe(false);
    expect(snapshot.events.map((event) => event.id)).toEqual(['event-1']);
  });

  it('rzuca błąd, gdy padnie pierwsza strona — awaria systemowa musi być widoczna', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(new Response('unauthorized', { status: 401 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(service.getEventsLastDays(7)).rejects.toThrow('Graph 401');
  });

  it('odrzuca nextLink prowadzący poza graph.microsoft.com bez wykonania żądania', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        jsonResponse({ value: [createEvent('event-1')], '@odata.nextLink': 'https://evil.example.com/next' }),
      );
    vi.stubGlobal('fetch', fetchMock);

    await expect(service.getEventsLastDays(7)).rejects.toThrow('graph.microsoft.com');
    // Tylko pierwsza (prawidłowa) strona — token nie poszedł pod obcy adres.
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
