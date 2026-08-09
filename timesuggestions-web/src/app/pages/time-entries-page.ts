import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from '../services/api.service';
import { DataRefreshService } from '../services/data-refresh.service';
import { SummaryStore } from '../services/summary-store';
import { ToastService } from '../services/toast.service';
import { TwoStepConfirm } from '../services/confirm-state';
import { toUserMessage } from '../services/user-message';
import { TimeEntriesResponse, TimeEntry } from '../models/api.models';
import { DurationPipe, formatDuration } from '../pipes/duration.pipe';
import { polishPlural } from '../pipes/polish-plural';

/** Widok listy: aktywne (do rozliczenia) albo archiwum (rozliczone, tylko odczyt). */
export type EntriesView = 'active' | 'archive';

/** Zakres dat rozliczenia w formacie ISO (yyyy-MM-dd) — kontrakt endpointu archive. */
export interface DateRange {
  from: string;
  to: string;
}

/**
 * Data lokalna przeglądarki jako ISO yyyy-MM-dd. Zakresy rozliczeń liczymy z dat
 * lokalnych: w prototypie strefa użytkownika = strefa biznesowa (Europe/Warsaw),
 * co jest świadomym uproszczeniem (jedna strefa, patrz README).
 */
export function toIsoDate(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}

/** Ostatni tydzień = 7 ostatnich dni z dzisiaj włącznie. */
export function lastWeekRange(today: Date): DateRange {
  const from = new Date(today);
  from.setDate(from.getDate() - 6);
  return { from: toIsoDate(from), to: toIsoDate(today) };
}

/** Ostatni miesiąc = bieżący miesiąc kalendarzowy: od 1. dnia do dziś. */
export function currentMonthRange(today: Date): DateRange {
  const from = new Date(today.getFullYear(), today.getMonth(), 1);
  return { from: toIsoDate(from), to: toIsoDate(today) };
}

/**
 * Rozliczenie wszystkiego: od najstarszego widocznego dnia do dziś.
 * Pusta lista → null (nie ma czego rozliczać, przycisk i tak jest ukryty).
 */
export function allVisibleRange(dayDates: string[], today: Date): DateRange | null {
  if (dayDates.length === 0) {
    return null;
  }
  // Daty dzienne ISO sortują się leksykograficznie — minimum to najstarszy dzień.
  const oldest = [...dayDates].sort()[0];
  return { from: oldest, to: toIsoDate(today) };
}

/** Toast po rozliczeniu: „Rozliczono 12 wpisów (9 godz. 30 min)." */
export function settledToastMessage(count: number, totalMinutes: number): string {
  const noun = polishPlural(count, 'wpis', 'wpisy', 'wpisów');
  return `Rozliczono ${count} ${noun} (${formatDuration(totalMinutes)}).`;
}

/** Etykieta uzbrojonego przycisku: „Na pewno? Rozliczysz 3 wpisy (2 godz.)". */
export function confirmSettleLabel(count: number, totalMinutes: number): string {
  const noun = polishPlural(count, 'wpis', 'wpisy', 'wpisów');
  return `Na pewno? Rozliczysz ${count} ${noun} (${formatDuration(totalMinutes)})`;
}

/**
 * Widok zapisanych wpisów czasu — dowód działania aplikacji.
 * Zatwierdzona sugestia nie znika "w nicość", tylko ląduje tutaj.
 * Rozliczenie (archiwizacja) przenosi wpisy do widoku Archiwum: jednokierunkowo,
 * bez edycji — dlatego każda akcja rozliczenia ma dwustopniowe potwierdzenie.
 */
@Component({
  selector: 'app-time-entries-page',
  imports: [DatePipe, DurationPipe],
  // Klik poza przyciskiem rozbraja potwierdzenie (kliki w przyciski robią stopPropagation).
  host: { '(document:click)': 'confirm.reset()' },
  template: `
    <div class="toolbar">
      <!-- Segmented control jak przełącznik motywów w app.ts — wybór widoczny od razu. -->
      <div class="view-switch" role="group" aria-label="Widok wpisów">
        <button class="view-option" [class.active]="view() === 'active'" (click)="setView('active')">
          Aktywne
        </button>
        <button class="view-option" [class.active]="view() === 'archive'" (click)="setView('archive')">
          Archiwum
        </button>
      </div>

      @if (view() === 'active' && data(); as response) {
        @if (response.days.length > 0) {
          <div class="settle-actions">
            <button class="btn" [class.btn-danger]="confirm.isArmed('week')"
              (click)="settleRange('week', $event)" [disabled]="settling()">
              {{ settleButtonLabel('week', 'Rozlicz ostatni tydzień') }}
            </button>
            <button class="btn" [class.btn-danger]="confirm.isArmed('month')"
              (click)="settleRange('month', $event)" [disabled]="settling()">
              {{ settleButtonLabel('month', 'Rozlicz ostatni miesiąc') }}
            </button>
            <button class="btn" [class.btn-danger]="confirm.isArmed('all')"
              (click)="settleRange('all', $event)" [disabled]="settling()">
              {{ settleButtonLabel('all', 'Rozlicz wszystko') }}
            </button>
          </div>
        }
      }
    </div>

    @if (error()) {
      <div class="error-box">
        {{ error() }}
        <button class="btn btn-ghost" (click)="loadData()">Spróbuj ponownie</button>
      </div>
    }

    @if (loading()) {
      <p class="empty-state">Ładowanie wpisów…</p>
    } @else if (data(); as response) {
      @if (response.days.length === 0) {
        @if (view() === 'archive') {
          <p class="empty-state">Archiwum jest puste — rozliczone wpisy pojawią się tutaj.</p>
        } @else {
          <div class="empty-state">
            <p><strong>Brak zapisanych wpisów czasu.</strong></p>
            <p>Zatwierdź sugestię w zakładce „Sugestie", a rozliczalny wpis pojawi się tutaj.</p>
          </div>
        }
      } @else {
        <!-- Świadoma zbieżność z kafelkiem „nierozliczony czas": w widoku Aktywne stopka
             pokazuje tę samą liczbę, bo oba sumują aktywne wpisy. Kafelek to trwałe KPI
             widoczne na każdej zakładce, stopka — podsumowanie aktualnie oglądanej listy
             (w widoku Archiwum pokazuje sumę archiwum, której kafelek nigdy nie pokazuje). -->
        <p class="total text-muted">
          Łącznie zarejestrowane: <strong class="text-success">{{ response.totalMinutes | duration }}</strong>
        </p>

        @for (day of response.days; track day.date) {
          <section class="day">
            <header class="day-header">
              <h3>{{ day.date | date: 'EEEE, d MMMM y' }}</h3>
              <span class="badge badge-accent">{{ day.totalMinutes | duration }}</span>
              @if (view() === 'active') {
                <button class="btn day-settle" [class.btn-danger]="confirm.isArmed('day:' + day.date)"
                  (click)="settleDay(day.date, $event)" [disabled]="settling()">
                  {{ settleButtonLabel('day:' + day.date, 'Rozlicz dzień') }}
                </button>
              }
            </header>

            @for (entry of day.entries; track entry.id) {
              <div class="card entry">
                <div class="entry-main">
                  <div class="entry-header">
                    <span class="badge badge-neutral">{{ entry.source === 'calendar' ? '📅 Spotkanie' : '📄 Dokument' }}</span>
                    <strong>{{ entry.caseName }}</strong>
                    <span class="badge badge-success">{{ entry.durationMinutes | duration }}</span>
                    @if (entry.archivedAt) {
                      <span class="badge badge-neutral">rozliczono {{ entry.archivedAt | date: 'dd.MM.yyyy' }}</span>
                    }
                  </div>
                  <p class="description text-muted">{{ entry.description }}</p>
                  @if (entry.sourceTitle) {
                    <!-- Kotwica w realnym zdarzeniu — opis powyżej mógł zostać nadpisany przy zatwierdzaniu. -->
                    <p class="origin text-muted">
                      @if (entry.source === 'calendar') {
                        ze spotkania: „{{ entry.sourceTitle }}"
                        @if (entry.sourceStartedAt) {
                          <span>, {{ entry.sourceStartedAt | date: 'dd.MM HH:mm' }}</span>
                        }
                      } @else {
                        z dokumentu: „{{ entry.sourceTitle }}"
                        @if (entry.sourceStartedAt) {
                          <span>, {{ entry.sourceStartedAt | date: 'dd.MM' }}</span>
                        }
                      }
                    </p>
                  }
                </div>
                @if (view() === 'active') {
                  <button class="btn btn-danger" (click)="undoApproval(entry)" [disabled]="busyId() === entry.id">
                    Cofnij zatwierdzenie
                  </button>
                }
              </div>
            }
          </section>
        }
      }
    }
  `,
  styles: `
    .toolbar { display: flex; align-items: center; gap: var(--space-4); margin-bottom: var(--space-4); flex-wrap: wrap; }
    .view-switch {
      display: inline-flex;
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      overflow: hidden;
      background: var(--surface);
    }
    .view-option {
      border: none;
      background: transparent;
      padding: var(--space-1) var(--space-3);
      cursor: pointer;
      font-size: var(--font-size-base);
      line-height: 1.6;
    }
    .view-option:hover { background: var(--surface-alt); }
    .view-option.active { background: var(--accent-soft); box-shadow: inset 0 -2px 0 var(--accent); }
    .settle-actions { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; }
    .total { margin: 0 0 var(--space-4); font-size: var(--font-size-lg); }
    .day { margin-bottom: var(--space-5); }
    .day-header { display: flex; align-items: center; gap: var(--space-3); margin-bottom: var(--space-2); }
    .day-header h3 { font-size: var(--font-size-base); text-transform: capitalize; }
    .day-settle { margin-left: auto; }
    .entry { display: flex; align-items: center; gap: var(--space-4); margin: var(--space-2) 0; }
    .entry-main { flex: 1; }
    .entry-header { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; }
    .description { margin: var(--space-1) 0 0; }
    .origin { margin: var(--space-1) 0 0; font-size: var(--font-size-sm); }
  `,
})
export class TimeEntriesPage implements OnInit {
  private api = inject(ApiService);
  private summaryStore = inject(SummaryStore);
  private toasts = inject(ToastService);
  private dataRefresh = inject(DataRefreshService);

  constructor() {
    // Przeładowanie po operacjach spoza tego widoku (np. "Cofnij" z toastu).
    let lastSeen: number | null = null;
    effect(() => {
      const version = this.dataRefresh.changes();
      if (lastSeen !== null && version !== lastSeen) {
        untracked(() => void this.loadData());
      }
      lastSeen = version;
    });
  }

  protected data = signal<TimeEntriesResponse | null>(null);
  protected loading = signal(false);
  protected error = signal<string | null>(null);
  protected busyId = signal<number | null>(null);
  protected view = signal<EntriesView>('active');
  protected settling = signal(false);

  /** Dwustopniowe potwierdzenie akcji rozliczenia — archiwizacja jest nieodwracalna. */
  protected confirm = new TwoStepConfirm();

  /** Zakresy przycisków hurtowych — z dat lokalnych przeglądarki i załadowanej listy. */
  private ranges = computed<Record<'week' | 'month' | 'all', DateRange | null>>(() => {
    const today = new Date();
    const dayDates = this.data()?.days.map((day) => day.date) ?? [];
    return {
      week: lastWeekRange(today),
      month: currentMonthRange(today),
      all: allVisibleRange(dayDates, today),
    };
  });

  async ngOnInit(): Promise<void> {
    await this.loadData();
  }

  protected setView(view: EntriesView): void {
    if (this.view() === view) {
      return;
    }
    this.view.set(view);
    this.confirm.reset();
    void this.loadData();
  }

  protected async loadData(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.data.set(await this.api.getTimeEntries(this.view() === 'archive'));
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać wpisów.'));
    } finally {
      this.loading.set(false);
    }
  }

  /** Etykieta przycisku rozliczenia: uzbrojony pyta „Na pewno?" z liczbami z załadowanej listy. */
  protected settleButtonLabel(key: string, idleLabel: string): string {
    if (!this.confirm.isArmed(key)) {
      return idleLabel;
    }
    const range = key.startsWith('day:')
      ? { from: key.slice(4), to: key.slice(4) }
      : this.ranges()[key as 'week' | 'month' | 'all'];
    const { count, minutes } = this.countInRange(range);
    return confirmSettleLabel(count, minutes);
  }

  protected settleDay(date: string, event: Event): void {
    void this.settle(`day:${date}`, { from: date, to: date }, event);
  }

  protected settleRange(key: 'week' | 'month' | 'all', event: Event): void {
    void this.settle(key, this.ranges()[key], event);
  }

  /** Liczby do potwierdzenia liczone z już załadowanej listy — bez dodatkowego żądania. */
  private countInRange(range: DateRange | null): { count: number; minutes: number } {
    const days = this.data()?.days ?? [];
    // Daty ISO porównują się poprawnie jako stringi.
    const inRange = range === null
      ? []
      : days.filter((day) => day.date >= range.from && day.date <= range.to);
    return {
      count: inRange.reduce((sum, day) => sum + day.entries.length, 0),
      minutes: inRange.reduce((sum, day) => sum + day.totalMinutes, 0),
    };
  }

  private async settle(key: string, range: DateRange | null, event: Event): Promise<void> {
    // Klik nie może dolecieć do document — rozbroiłby potwierdzenie, które właśnie uzbrajamy.
    event.stopPropagation();
    if (range === null || !this.confirm.confirm(key)) {
      return;
    }

    this.settling.set(true);
    this.error.set(null);
    try {
      const result = await this.api.archiveTimeEntries(range.from, range.to);
      // Bez akcji „Cofnij" — rozliczenie jest nieodwracalne.
      this.toasts.show(settledToastMessage(result.archivedCount, result.totalMinutes));
      await this.loadData();
      await this.summaryStore.refresh();
      this.dataRefresh.notify();
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się rozliczyć wpisów.'));
    } finally {
      this.settling.set(false);
    }
  }

  /** Cofnięcie zatwierdzenia usuwa wpis i przywraca sugestię źródłową (pomyłka jest odwracalna). */
  protected async undoApproval(entry: TimeEntry): Promise<void> {
    this.busyId.set(entry.id);
    this.error.set(null);
    try {
      await this.api.deleteTimeEntry(entry.id);
      await this.loadData();
      await this.summaryStore.refresh();
      this.toasts.show('Cofnięto zatwierdzenie — sugestia wróciła na listę oczekujących.');
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się cofnąć zatwierdzenia.'));
    } finally {
      this.busyId.set(null);
    }
  }
}
