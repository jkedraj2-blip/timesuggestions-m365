import { afterEach, describe, expect, it, vi } from 'vitest';
import { assertTrustedGraphUrl, fetchGraphPage } from './graph-http';

function jsonResponse(body: unknown, init?: ResponseInit): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });
}

describe('assertTrustedGraphUrl', () => {
  it('przepuszcza adres https na hoście graph.microsoft.com', () => {
    expect(() => assertTrustedGraphUrl('https://graph.microsoft.com/v1.0/me/calendarView')).not.toThrow();
  });

  it.each([
    'http://graph.microsoft.com/v1.0/me', // brak TLS
    'https://evil.example.com/v1.0/me', // obcy host
    'https://graph.microsoft.com.evil.com/v1.0/me', // host-przedrostek
    'https://graph.microsoft.com:8443/v1.0/me', // niestandardowy port
    'nie-adres',
  ])('odrzuca adres %s', (url) => {
    expect(() => assertTrustedGraphUrl(url)).toThrow();
  });
});

describe('fetchGraphPage', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('nie wykonuje żądania pod niezaufany adres', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    await expect(
      fetchGraphPage('https://evil.example.com/v1.0/me', () => Promise.resolve('token')),
    ).rejects.toThrow('graph.microsoft.com');

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('ponawia po 429 z odczytem Retry-After i pobiera token per próba', async () => {
    vi.useFakeTimers();
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 429, headers: { 'Retry-After': '2' } }))
      .mockResolvedValueOnce(jsonResponse({ value: [] }));
    vi.stubGlobal('fetch', fetchMock);
    const getToken = vi.fn().mockResolvedValue('token');

    const pending = fetchGraphPage('https://graph.microsoft.com/v1.0/me', getToken);
    await vi.advanceTimersByTimeAsync(2000);
    const response = await pending;

    expect(response.ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    // Token per strona/próba — MSAL cache'uje, a długi przebieg nie padnie na wygasłym tokenie.
    expect(getToken).toHaveBeenCalledTimes(2);
  });

  it('po wyczerpaniu prób zwraca ostatnią odpowiedź 429', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn().mockResolvedValue(new Response('', { status: 429 }));
    vi.stubGlobal('fetch', fetchMock);

    const pending = fetchGraphPage('https://graph.microsoft.com/v1.0/me', () => Promise.resolve('token'));
    await vi.advanceTimersByTimeAsync(10_000);
    const response = await pending;

    expect(response.status).toBe(429);
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('nie ponawia odpowiedzi nieprzejściowych (np. 410 Gone wraca do wywołującego)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('', { status: 410 }));
    vi.stubGlobal('fetch', fetchMock);

    const response = await fetchGraphPage('https://graph.microsoft.com/v1.0/delta', () => Promise.resolve('token'));

    expect(response.status).toBe(410);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
