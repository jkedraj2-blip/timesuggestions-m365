import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import {
  AUTO_SYNC_NUDGE_STORAGE_KEY,
  AUTO_SYNC_STORAGE_KEY,
  AutoSyncService,
  autoSyncToast,
} from './auto-sync.service';
import { ApiService } from './api.service';
import { SummaryStore } from './summary-store';
import { DataRefreshService } from './data-refresh.service';
import { ToastService } from './toast.service';
import { AUTO_SYNC_INTERVAL_MINUTES, AUTO_SYNC_MAX_FAILURES } from './graph-config';
import { SyncReport } from '../models/api.models';

function report(overrides: Partial<SyncReport> = {}): SyncReport {
  return {
    fetched: { calendarEvents: 0, driveFiles: 0 },
    created: 0,
    updated: 0,
    removed: 0,
    skippedExisting: 0,
    aggregated: 0,
    deduplicated: 0,
    windowDays: 7,
    matched: { single: 0, ambiguous: 0, none: 0 },
    versions: { filesWithHistory: 0, filesWithoutHistory: 0, fetchErrors: 0, newActivities: 0 },
    filteredOut: {
      private: 0,
      tooShort: 0,
      allDay: 0,
      cancelled: 0,
      invalidDates: 0,
      notOfficeDocument: 0,
      notModifiedByUser: 0,
      total: 0,
    },
    ...overrides,
  };
}

describe('autoSyncToast', () => {
  /** Automat ma być cichy: bez zmian nie zawraca głowy co dziesięć minut. */
  it('bez zmian nie ma o czym powiadamiać', () => {
    expect(autoSyncToast({ created: 0, updated: 0, removed: 0 })).toBeNull();
  });

  it('wymienia tylko niezerowe efekty i mówi, że to przebieg w tle', () => {
    expect(autoSyncToast({ created: 2, updated: 0, removed: 1 })).toBe(
      'Sprawdzenie w tle: 2 nowe sugestie, 1 usunięto.',
    );
  });
});

describe('AutoSyncService', () => {
  let service: AutoSyncService;
  const syncNowMock = vi.fn();
  const showToastMock = vi.fn();
  const notifyMock = vi.fn();

  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.removeItem(AUTO_SYNC_STORAGE_KEY);
    localStorage.removeItem(AUTO_SYNC_NUDGE_STORAGE_KEY);
    syncNowMock.mockReset();
    showToastMock.mockReset();
    notifyMock.mockReset();

    TestBed.configureTestingModule({
      providers: [
        { provide: ApiService, useValue: { syncNow: syncNowMock } },
        { provide: SummaryStore, useValue: { refresh: vi.fn().mockResolvedValue(undefined) } },
        { provide: DataRefreshService, useValue: { notify: notifyMock } },
        { provide: ToastService, useValue: { show: showToastMock } },
      ],
    });
    service = TestBed.inject(AutoSyncService);
  });

  afterEach(() => {
    service.setEnabled(false);
    vi.useRealTimers();
  });

  /**
   * Wskaźnik delty OneDrive przesuwa się dopiero po udanym zapisie, więc dwa równoległe
   * przebiegi biłyby się o ten sam stan. Blokada jest wspólna dla automatu i przycisku.
   */
  it('drugi równoległy przebieg nie rusza — zwraca null zamiast pobierać po raz drugi', async () => {
    let release: (value: SyncReport) => void = () => {};
    syncNowMock.mockReturnValue(new Promise<SyncReport>((resolve) => (release = resolve)));

    const first = service.sync();
    const second = await service.sync();
    expect(second).toBeNull();
    expect(syncNowMock).toHaveBeenCalledTimes(1);

    release(report());
    await first;
    expect(service.busy()).toBe(false);
  });

  it('włączenie mierzy od razu, a potem w takt interwału', async () => {
    syncNowMock.mockResolvedValue(report());

    service.setEnabled(true);
    await vi.advanceTimersByTimeAsync(0);
    expect(syncNowMock).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(AUTO_SYNC_INTERVAL_MINUTES * 60_000);
    expect(syncNowMock).toHaveBeenCalledTimes(2);
    // Powiadomienie BEZ inicjatora — odświeżyć ma się każdy otwarty widok.
    expect(notifyMock).toHaveBeenCalledWith();
  });

  it('przebieg bez zmian nie pokazuje żadnego powiadomienia', async () => {
    syncNowMock.mockResolvedValue(report());

    service.setEnabled(true);
    await vi.advanceTimersByTimeAsync(0);

    expect(showToastMock).not.toHaveBeenCalled();
  });

  /**
   * Typowa przyczyna to wygasła sesja Microsoft. Automat dobijający się w kółko do Graph
   * byłby gorszy od wyłączonego: nic się nie mierzy, a użytkownik nie wie dlaczego.
   */
  it('po serii nieudanych prób wyłącza się i mówi o tym wprost', async () => {
    syncNowMock.mockRejectedValue(new Error('Brak sesji.'));

    service.setEnabled(true);
    await vi.advanceTimersByTimeAsync(0);
    for (let attempt = 1; attempt < AUTO_SYNC_MAX_FAILURES; attempt++) {
      await vi.advanceTimersByTimeAsync(AUTO_SYNC_INTERVAL_MINUTES * 60_000);
    }

    expect(service.enabled()).toBe(false);
    expect(localStorage.getItem(AUTO_SYNC_STORAGE_KEY)).toBe('false');
    expect(showToastMock).toHaveBeenCalledWith(
      expect.stringContaining('Automatyczne sprawdzanie wyłączone'),
      { kind: 'error' },
    );
    // Powód zostaje w statusie także po wyłączeniu — to jedyny ślad, co się stało.
    expect(service.lastError()).toContain('Brak sesji.');

    // Zegar naprawdę stanął: kolejny interwał nie generuje już żądań.
    const callsAfterShutdown = syncNowMock.mock.calls.length;
    await vi.advanceTimersByTimeAsync(AUTO_SYNC_INTERVAL_MINUTES * 60_000);
    expect(syncNowMock).toHaveBeenCalledTimes(callsAfterShutdown);
  });

  /**
   * Sprawdzanie NIE jest włączone fabrycznie: sięganie do Microsoftu co kilka minut przy
   * pozornie bezczynnej karcie to decyzja użytkownika. Żeby jednak nie zależała od tego,
   * czy sam wypatrzy pole wyboru w pasku, aplikacja pyta go raz.
   */
  it('domyślnie wyłączone, ale z jednorazową zachętą do włączenia', () => {
    expect(service.enabled()).toBe(false);
    expect(service.suggestsEnabling()).toBe(true);
  });

  it.each([
    ['włączenie', (target: AutoSyncService) => target.setEnabled(true)],
    ['świadoma odmowa', (target: AutoSyncService) => target.dismissSuggestion()],
    // Wyłączenie też jest odpowiedzią na pytanie — zachęta nie może wracać po każdym wejściu.
    ['wyłączenie', (target: AutoSyncService) => target.setEnabled(false)],
  ])('zachęta znika po decyzji użytkownika (%s) i nie wraca', (_label, decide) => {
    syncNowMock.mockResolvedValue(report());

    decide(service);

    expect(service.suggestsEnabling()).toBe(false);
    expect(localStorage.getItem(AUTO_SYNC_NUDGE_STORAGE_KEY)).toBe('true');
  });

  /** Zapisana preferencja nie może uruchamiać pobierania przed odtworzeniem sesji. */
  it('sam start aplikacji nie uruchamia zegara — dopiero resume() po zalogowaniu', async () => {
    localStorage.setItem(AUTO_SYNC_STORAGE_KEY, 'true');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiService, useValue: { syncNow: syncNowMock } },
        { provide: SummaryStore, useValue: { refresh: vi.fn().mockResolvedValue(undefined) } },
        { provide: DataRefreshService, useValue: { notify: notifyMock } },
        { provide: ToastService, useValue: { show: showToastMock } },
      ],
    });
    syncNowMock.mockResolvedValue(report());
    service = TestBed.inject(AutoSyncService);

    await vi.advanceTimersByTimeAsync(AUTO_SYNC_INTERVAL_MINUTES * 60_000);
    expect(syncNowMock).not.toHaveBeenCalled();

    service.resume();
    // resume() też nie mierzy natychmiast — pierwszy pomiar wypada dopiero w takt zegara.
    expect(syncNowMock).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(AUTO_SYNC_INTERVAL_MINUTES * 60_000);
    expect(syncNowMock).toHaveBeenCalledTimes(1);
  });
});
