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
    expect(result.documentsOutsideWindow).toBe(1);
    expect(result.documentsNotOfficeDocument).toBe(1);
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
