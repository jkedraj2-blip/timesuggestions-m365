import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { GraphFilesService } from './graph-files.service';
import { AuthService } from './auth.service';
import { GraphDriveItem } from '../models/graph.models';

const STORAGE_KEY = 'timesuggestions.deltaLink.account-1';

function createFile(id: string, name: string, modifiedAt = '2026-08-06T10:00:00Z'): GraphDriveItem {
  return {
    id,
    name,
    file: { mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' },
    lastModifiedDateTime: modifiedAt,
  };
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function deltaResponse(value: GraphDriveItem[]): Response {
  return jsonResponse({ value, '@odata.deltaLink': 'https://graph.microsoft.com/v1.0/me/drive/root/delta?token=abc' });
}

describe('GraphFilesService', () => {
  let service: GraphFilesService;

  beforeEach(() => {
    // Stabilna data "teraz" (tylko Date — settimeout zostaje prawdziwy).
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
    service = TestBed.inject(GraphFilesService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('rozdziela tombstone-y od plików i liczy odrzucenia klienckie', async () => {
    const items: GraphDriveItem[] = [
      createFile('file-ok', 'Umowa_NovaTech.docx'),
      { id: 'file-deleted', deleted: { state: 'deleted' } }, // tombstone bez nazwy i dat
      createFile('file-old', 'Stara_umowa.docx', '2026-06-01T10:00:00Z'),
      createFile('file-image', 'zdjecie.png'),
    ];
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(deltaResponse(items)));

    const result = await service.getRecentDocuments(7);

    expect(result.files.map((file) => file.id)).toEqual(['file-ok']);
    expect(result.deletedDriveFileIds).toEqual(['file-deleted']);
    // Stary plik po prostu nie jest kandydatem — poza oknem leży cały dysk, więc
    // meldowanie go jako "pominięty" opisywało zasięg delty, nie pracę użytkownika.
    expect(result.documentsNotOfficeDocument).toBe(1);
    // Nazwa trafia do raportu w przeglądarce — sam licznik nie mówi, co zostało pominięte.
    expect(result.notOfficeDocumentNames).toHaveLength(1);
  });

  it('dla powtórzonego id obowiązuje ostatnie wystąpienie w feedzie (semantyka delta)', async () => {
    const items: GraphDriveItem[] = [
      createFile('file-1', 'Umowa_NovaTech.docx'),
      { id: 'file-1', deleted: { state: 'deleted' } }, // plik zmieniony, potem usunięty
    ];
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(deltaResponse(items)));

    const result = await service.getRecentDocuments(7);

    expect(result.files).toEqual([]);
    expect(result.deletedDriveFileIds).toEqual(['file-1']);
  });

  it('odrzuca zapisany deltaLink spoza graph.microsoft.com bez wykonania żądania i czyści go', async () => {
    localStorage.setItem(STORAGE_KEY, 'https://evil.example.com/delta?token=abc');
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    await expect(service.getRecentDocuments(7)).rejects.toThrow('wskaźnik synchronizacji');

    expect(fetchMock).not.toHaveBeenCalled();
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('410 na kolejnej stronie przebiegu z cache odrzuca zebrane elementy i robi jeden pełny crawl', async () => {
    localStorage.setItem(STORAGE_KEY, 'https://graph.microsoft.com/v1.0/me/drive/root/delta?token=stale');
    const nextLink = 'https://graph.microsoft.com/v1.0/me/drive/root/delta?token=stale-page2';
    const freshDeltaLink = 'https://graph.microsoft.com/v1.0/me/drive/root/delta?token=fresh';
    const fetchMock = vi
      .fn()
      // Strona 1 z zapisanego linku: OK, ale sesja delta zaraz wygaśnie.
      .mockResolvedValueOnce(jsonResponse({ value: [createFile('file-stale', 'Stary_przebieg.docx')], '@odata.nextLink': nextLink }))
      // Strona 2: 410 Gone w ŚRODKU przebiegu.
      .mockResolvedValueOnce(new Response('gone', { status: 410 }))
      // Pełny crawl od zera.
      .mockResolvedValueOnce(jsonResponse({ value: [createFile('file-fresh', 'Nowy_przebieg.docx')], '@odata.deltaLink': freshDeltaLink }))
      // Historia wersji pliku z pełnego crawla.
      .mockResolvedValueOnce(jsonResponse({ value: [] }));
    vi.stubGlobal('fetch', fetchMock);
    const pages: number[] = [];

    const result = await service.getRecentDocuments(7, (page) => pages.push(page));

    // Wynik zawiera WYŁĄCZNIE elementy pełnego crawla — częściowe dane z
    // unieważnionej sesji delta zostały odrzucone.
    expect(result.files.map((file) => file.id)).toEqual(['file-fresh']);
    expect(fetchMock).toHaveBeenCalledTimes(4);
    expect(String(fetchMock.mock.calls[2][0])).toContain('/me/drive/root/delta?$select=');
    // Zapisany wskaźnik wyczyszczony, licznik stron zresetowany.
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(pages).toEqual([1, 2, 1]);
    // pendingDeltaLink też został zresetowany — commit zapisuje wskaźnik pełnego crawla.
    service.commitDeltaLink();
    expect(localStorage.getItem(STORAGE_KEY)).toBe(freshDeltaLink);
  });

  it('410 podczas pełnego crawla rzuca błąd zamiast pętli resetów', async () => {
    // Bez zapisanego wskaźnika — przebieg od razu jest pełnym crawlem.
    const fetchMock = vi.fn().mockResolvedValue(new Response('gone', { status: 410 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(service.getRecentDocuments(7)).rejects.toThrow('Graph 410');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('410 po restarcie przebiegu z cache także rzuca błąd — restart jest jednokrotny', async () => {
    localStorage.setItem(STORAGE_KEY, 'https://graph.microsoft.com/v1.0/me/drive/root/delta?token=stale');
    const fetchMock = vi.fn().mockResolvedValue(new Response('gone', { status: 410 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(service.getRecentDocuments(7)).rejects.toThrow('Graph 410');
    // Strona z cache (410) + jedna próba pełnego crawla (410) — i koniec.
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('410 na pierwszej stronie z cache robi pełny crawl jak dotychczas', async () => {
    localStorage.setItem(STORAGE_KEY, 'https://graph.microsoft.com/v1.0/me/drive/root/delta?token=stale');
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response('gone', { status: 410 }))
      .mockResolvedValueOnce(deltaResponse([createFile('file-fresh', 'Umowa_NovaTech.docx')]))
      // Historia wersji pliku z pełnego crawla.
      .mockResolvedValueOnce(jsonResponse({ value: [] }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await service.getRecentDocuments(7);

    expect(result.files.map((file) => file.id)).toEqual(['file-fresh']);
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('dociąga historię wersji dla plików z okna i mapuje ją do payloadu', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(deltaResponse([createFile('file-1', 'Umowa_NovaTech.docx')]))
      .mockResolvedValueOnce(
        jsonResponse({
          value: [
            { id: '2.0', lastModifiedDateTime: '2026-08-06T10:00:00Z', size: 800 },
            { id: '1.0', lastModifiedDateTime: '2026-08-06T09:00:00Z', size: 500 },
          ],
        }),
      );
    vi.stubGlobal('fetch', fetchMock);
    const progress: Array<{ done: number; total: number }> = [];

    const result = await service.getRecentDocuments(7, undefined, (p) => progress.push(p));

    expect(String(fetchMock.mock.calls[1][0])).toContain('/me/drive/items/file-1/versions');
    expect(result.files[0].versions).toEqual([
      { versionId: '2.0', lastModifiedDateTime: '2026-08-06T10:00:00Z', size: 800 },
      { versionId: '1.0', lastModifiedDateTime: '2026-08-06T09:00:00Z', size: 500 },
    ]);
    expect(result.versionFetchErrors).toBe(0);
    expect(progress).toEqual([{ done: 1, total: 1 }]);
  });

  it('błąd pobrania wersji jednego pliku nie wywala syncu — plik zostaje z versions=null', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        deltaResponse([createFile('file-1', 'Umowa_NovaTech.docx'), createFile('file-2', 'Raport_Kowalski.docx')]),
      )
      // Wersje file-1: błąd nieprzejściowy; wersje file-2: OK.
      .mockImplementation((url: string) => {
        if (String(url).includes('file-1')) {
          return Promise.resolve(new Response('forbidden', { status: 403 }));
        }
        return Promise.resolve(
          jsonResponse({ value: [{ id: '1.0', lastModifiedDateTime: '2026-08-06T10:00:00Z', size: 100 }] }),
        );
      });
    vi.stubGlobal('fetch', fetchMock);

    const result = await service.getRecentDocuments(7);

    const byId = new Map(result.files.map((file) => [file.id, file]));
    expect(byId.get('file-1')?.versions).toBeNull();
    expect(byId.get('file-2')?.versions).toEqual([
      { versionId: '1.0', lastModifiedDateTime: '2026-08-06T10:00:00Z', size: 100 },
    ]);
    expect(result.versionFetchErrors).toBe(1);
  });

  it('historia wersji podąża za @odata.nextLink przez wszystkie strony', async () => {
    const nextLink = 'https://graph.microsoft.com/v1.0/me/drive/items/file-1/versions?$skiptoken=abc';
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(deltaResponse([createFile('file-1', 'Umowa_NovaTech.docx')]))
      .mockResolvedValueOnce(
        jsonResponse({
          value: [{ id: '2.0', lastModifiedDateTime: '2026-08-06T10:00:00Z', size: 800 }],
          '@odata.nextLink': nextLink,
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse({ value: [{ id: '1.0', lastModifiedDateTime: '2026-08-06T09:00:00Z', size: 500 }] }),
      );
    vi.stubGlobal('fetch', fetchMock);

    const result = await service.getRecentDocuments(7);

    expect(result.files[0].versions?.map((version) => version.versionId)).toEqual(['2.0', '1.0']);
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('commitDeltaLink zapisuje wskaźnik dopiero po jawnym wywołaniu (po udanym POST /api/sync)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(deltaResponse([])));

    await service.getRecentDocuments(7);
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();

    service.commitDeltaLink();
    expect(localStorage.getItem(STORAGE_KEY)).toBe(
      'https://graph.microsoft.com/v1.0/me/drive/root/delta?token=abc',
    );
  });
});
