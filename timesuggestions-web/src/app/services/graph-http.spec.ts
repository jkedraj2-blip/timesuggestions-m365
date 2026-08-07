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

  it('honoruje Retry-After w formacie daty HTTP', async () => {
    vi.useFakeTimers({ now: new Date('2026-08-07T12:00:00Z') });
    const retryAt = new Date('2026-08-07T12:00:05Z').toUTCString(); // za 5 s
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 429, headers: { 'Retry-After': retryAt } }))
      .mockResolvedValueOnce(jsonResponse({ value: [] }));
    vi.stubGlobal('fetch', fetchMock);

    const pending = fetchGraphPage('https://graph.microsoft.com/v1.0/me', () => Promise.resolve('token'));
    // Po 4 s ponowienia jeszcze nie ma — data HTTP nie może dawać NaN i retry po 1 s.
    await vi.advanceTimersByTimeAsync(4000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1000);
    const response = await pending;

    expect(response.ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('dla nieparsowalnego Retry-After stosuje rosnący backoff', async () => {
    vi.useFakeTimers();
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 429, headers: { 'Retry-After': 'wkrotce-moze' } }))
      .mockResolvedValueOnce(jsonResponse({ value: [] }));
    vi.stubGlobal('fetch', fetchMock);

    const pending = fetchGraphPage('https://graph.microsoft.com/v1.0/me', () => Promise.resolve('token'));
    await vi.advanceTimersByTimeAsync(1000); // fallback pierwszej próby: 1 s
    const response = await pending;

    expect(response.ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('przycina Retry-After z odległą datą do górnego limitu 60 s', async () => {
    vi.useFakeTimers({ now: new Date('2026-08-07T12:00:00Z') });
    const farFuture = new Date('2026-08-07T13:00:00Z').toUTCString(); // za godzinę
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('', { status: 429, headers: { 'Retry-After': farFuture } }))
      .mockResolvedValueOnce(jsonResponse({ value: [] }));
    vi.stubGlobal('fetch', fetchMock);

    const pending = fetchGraphPage('https://graph.microsoft.com/v1.0/me', () => Promise.resolve('token'));
    await vi.advanceTimersByTimeAsync(60_000);
    const response = await pending;

    expect(response.ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('przerywa żądanie, gdy body nigdy się nie kończy — timeout obejmuje odczyt i parsowanie', async () => {
    vi.useFakeTimers();
    // Nagłówki przyszły, ale strumień body nigdy się nie domyka — json() wisi
    // aż do abortu z naszego kontrolera.
    const fetchMock = vi.fn((_url: RequestInfo | URL, init?: RequestInit) =>
      Promise.resolve({
        ok: true,
        status: 200,
        headers: new Headers(),
        json: () =>
          new Promise((_resolve, reject) => {
            init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
          }),
      } as unknown as Response),
    );
    vi.stubGlobal('fetch', fetchMock);

    const pending = fetchGraphPage('https://graph.microsoft.com/v1.0/me', () => Promise.resolve('token'));
    const expectation = expect(pending).rejects.toThrow('limit czasu');
    await vi.advanceTimersByTimeAsync(30_000);
    await expectation;
  });
});
