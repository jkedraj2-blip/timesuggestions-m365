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
