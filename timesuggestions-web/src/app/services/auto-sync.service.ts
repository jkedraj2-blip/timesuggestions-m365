import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService, SyncStage } from './api.service';
import { DataRefreshService } from './data-refresh.service';
import { SummaryStore } from './summary-store';
import { ToastService } from './toast.service';
import { toUserMessage } from './user-message';
import { SyncReport } from '../models/api.models';
import { loadSyncDays, normalizedSyncDays } from './sync-preferences';
import { AUTO_SYNC_INTERVAL_MINUTES, AUTO_SYNC_MAX_FAILURES } from './graph-config';
import { polishPlural } from '../pipes/polish-plural';

/** Klucz localStorage z przełącznikiem automatu — preferencja per przeglądarka. */
export const AUTO_SYNC_STORAGE_KEY = 'timesuggestions.autoSync';

/** Klucz localStorage z zamkniętą podpowiedzią „warto to włączyć" — ma pojawić się RAZ. */
export const AUTO_SYNC_NUDGE_STORAGE_KEY = 'timesuggestions.autoSyncNudgeDismissed';

/**
 * Krótkie zdanie o efekcie przebiegu w tle. Automat ma być cichy: bez zmian nie mówi nic
 * (zwraca null), a odzywa się wyłącznie wtedy, gdy na liście naprawdę coś przybyło,
 * ubyło albo się zmieniło.
 */
export function autoSyncToast(report: Pick<SyncReport, 'created' | 'updated' | 'removed'>): string | null {
  const parts: string[] = [];
  if (report.created > 0) {
    parts.push(`${report.created} ${polishPlural(report.created, 'nowa sugestia', 'nowe sugestie', 'nowych sugestii')}`);
  }
  if (report.updated > 0) {
    parts.push(`${report.updated} zaktualizowano`);
  }
  if (report.removed > 0) {
    parts.push(`${report.removed} usunięto`);
  }
  return parts.length === 0 ? null : `Sprawdzenie w tle: ${parts.join(', ')}.`;
}

/**
 * Automatyczna synchronizacja w tle.
 *
 * PO CO: każdy przebieg synchronizacji jest pomiarem — dopisuje do dziennika
 * DocumentActivity próbkę „plik miał tę datę modyfikacji o tej godzinie". Word online
 * nie pieczętuje wersji w trakcie pisania (nowa wersja powstaje przy zamknięciu karty
 * albo po kilkunastu minutach bezczynności), więc materiał do liczenia sesji bywa
 * bardzo rzadki. Ręczne klikanie „Synchronizuj" raz dziennie daje najgorszy możliwy
 * materiał pomiarowy — a realnie nikt tego nie klika.
 *
 * CZEGO NIE OBIECUJE: tego, że długa praca zawsze rozbije się na sesje. Kiedy powstanie
 * ślad, decyduje Word. Gęstsze próbkowanie ogranicza dziurę w danych do kwadransa
 * zamiast całego dnia — to zwiększona szansa na wierny pomiar, nie gwarancja.
 *
 * CZEGO NIE ROBI: nie działa przy zamkniętej przeglądarce. Token Graph nigdy nie
 * opuszcza przeglądarki (to reguła całej aplikacji, nie ograniczenie techniczne), więc
 * backend nie ma czym pobrać danych sam z siebie. Praca „gdy aplikacja jest wyłączona"
 * wymagałaby trzymania po stronie serwera tokenu odświeżającego użytkownika — czyli
 * dokładnie tego, czego ta architektura unika. Karta w tle wystarcza i o to chodzi:
 * prawnik pisze w Wordzie, karta z aplikacją zostaje otwarta i próbkuje.
 *
 * Poprzednie podejście („tryb na żywo") usunięto, bo dziennik kluczowany po
 * (plik, wersja) odrzucał powtórzone obserwacje — cykliczne odpytywanie było wtedy
 * odpytywaniem o nic. Po naprawie modelu na (plik, wersja, moment) każdy przebieg
 * wnosi informację.
 */
@Injectable({ providedIn: 'root' })
export class AutoSyncService {
  private api = inject(ApiService);
  private summaryStore = inject(SummaryStore);
  private dataRefresh = inject(DataRefreshService);
  private toasts = inject(ToastService);

  /**
   * Domyślnie WYŁĄCZONE. Aplikacja sięgająca do Microsoftu co kilka minut, gdy karta
   * wygląda na bezczynną, to w kancelarii decyzja użytkownika, a nie ustawienie fabryczne —
   * każdy przebieg dodatkowo pisze do bazy i potrafi dorzucić sugestie na ekran, na który
   * ktoś właśnie patrzy. Zamiast domyślnego „tak" jest jednorazowa podpowiedź (patrz
   * <see cref="suggestsEnabling"/>), bo funkcja, o której nikt się nie dowie, nie istnieje.
   */
  readonly enabled = signal(localStorage.getItem(AUTO_SYNC_STORAGE_KEY) === 'true');

  private nudgeDismissed = signal(localStorage.getItem(AUTO_SYNC_NUDGE_STORAGE_KEY) === 'true');

  /** Czy pokazać zachętę do włączenia — dopóki użytkownik ani nie włączył, ani nie odmówił. */
  readonly suggestsEnabling = computed(() => !this.enabled() && !this.nudgeDismissed());

  /** „Nie teraz" — pytamy raz, nie przy każdym wejściu na listę. */
  dismissSuggestion(): void {
    this.nudgeDismissed.set(true);
    localStorage.setItem(AUTO_SYNC_NUDGE_STORAGE_KEY, 'true');
  }

  /**
   * Trwa synchronizacja — WSPÓLNA dla automatu i przycisku „Synchronizuj". Dwa równoległe
   * przebiegi pobierałyby te same dane i biły się o wskaźnik delty OneDrive, który
   * przesuwa się dopiero po udanym zapisie.
   */
  readonly busy = signal(false);

  readonly lastSyncAt = signal<Date | null>(null);
  readonly lastError = signal<string | null>(null);

  readonly intervalMinutes = AUTO_SYNC_INTERVAL_MINUTES;

  private timer: ReturnType<typeof setInterval> | null = null;
  private consecutiveFailures = 0;

  /**
   * Wznowienie po zalogowaniu. Preferencja przeżywa przeładowanie strony, ale zegar
   * NIE wstaje razem z aplikacją: dopóki sesja Microsoft nie jest odtworzona, przebieg
   * skończyłby się serią błędów i sam wyłączyłby automat. Bez natychmiastowego pomiaru —
   * pierwszy i tak wypada w ciągu kwadransa.
   */
  resume(): void {
    if (this.enabled() && this.timer === null) {
      this.startTimer();
    }
  }

  /** Zatrzymanie bez kasowania preferencji (wylogowanie) — po ponownym logowaniu wraca. */
  pause(): void {
    this.stopTimer();
  }

  /** Przełącznik z UI — świadoma decyzja użytkownika, więc mierzymy od razu. */
  setEnabled(enabled: boolean): void {
    this.enabled.set(enabled);
    localStorage.setItem(AUTO_SYNC_STORAGE_KEY, String(enabled));
    // Świadome wyłączenie też jest odpowiedzią na pytanie — zachęta nie ma wracać.
    this.dismissSuggestion();
    this.consecutiveFailures = 0;
    this.lastError.set(null);
    this.stopTimer();

    if (enabled) {
      this.startTimer();
      void this.tick();
    }
  }

  private startTimer(): void {
    this.timer = setInterval(() => void this.tick(), AUTO_SYNC_INTERVAL_MINUTES * 60_000);
  }

  private stopTimer(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  /**
   * Jedno wejście do synchronizacji dla całej aplikacji. Zwraca null, gdy przebieg już
   * trwa — wołający ma wtedy powiedzieć użytkownikowi, że nie ma co klikać drugi raz,
   * zamiast uruchamiać drugie pobieranie.
   */
  async sync(onStage?: (stage: SyncStage) => void): Promise<SyncReport | null> {
    if (this.busy()) {
      return null;
    }

    this.busy.set(true);
    try {
      const report = await this.api.syncNow(onStage, normalizedSyncDays(loadSyncDays()));
      this.lastSyncAt.set(new Date());
      this.lastError.set(null);
      this.consecutiveFailures = 0;
      await this.summaryStore.refresh();
      return report;
    } finally {
      this.busy.set(false);
    }
  }

  /**
   * Przebieg automatu. Celowo bez etapów i bez panelu raportu — to sprawdzenie w tle,
   * a nie akcja użytkownika. Powiadomienie o zmianie danych idzie BEZ inicjatora, więc
   * odświeżają się wszystkie otwarte widoki, łącznie z tym, na który patrzy użytkownik.
   */
  private async tick(): Promise<void> {
    try {
      const report = await this.sync();
      if (report === null) {
        return; // ręczna synchronizacja właśnie trwa — ten tick jest zbędny
      }

      this.dataRefresh.notify();
      const message = autoSyncToast(report);
      if (message !== null) {
        this.toasts.show(message);
      }
    } catch (error) {
      this.consecutiveFailures++;
      const message = toUserMessage(error, 'Sprawdzenie w tle nie powiodło się.');
      if (this.consecutiveFailures >= AUTO_SYNC_MAX_FAILURES) {
        // Najczęstsza przyczyna to wygasła sesja Microsoft. Cichy automat dobijający się
        // w kółko do Graph byłby gorszy od wyłączonego: użytkownik nie wiedziałby ani że
        // nic się nie mierzy, ani dlaczego.
        this.setEnabled(false);
        this.toasts.show(
          `${message} Automatyczne sprawdzanie wyłączone. Zsynchronizuj ręcznie i włącz je z powrotem.`,
          { kind: 'error' },
        );
      }
      // Komunikat zostaje w statusie także po wyłączeniu automatu (setEnabled go czyści),
      // bo to jedyny ślad, dlaczego pomiar przestał działać.
      this.lastError.set(message);
    }
  }
}
