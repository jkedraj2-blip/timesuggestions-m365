import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { ApiService } from '../services/api.service';
import { AuthService } from '../services/auth.service';
import { DataRefreshService } from '../services/data-refresh.service';
import { ToastService } from '../services/toast.service';
import { toUserMessage } from '../services/user-message';
import { formatCaseMeta } from '../services/case-label';
import { scrollToAndHighlight } from '../services/scroll-highlight';
import { TimelineDay, TimelineItem } from '../models/api.models';
import { DurationPipe } from '../pipes/duration.pipe';
import { DocumentHistory } from './document-history';

/** Miesiąc kalendarzowy: rok + miesiąc 1–12. */
export interface MonthRef {
  year: number;
  month: number;
}

export function currentMonth(today: Date): MonthRef {
  return { year: today.getFullYear(), month: today.getMonth() + 1 };
}

export function shiftMonth(ref: MonthRef, delta: number): MonthRef {
  const index = ref.year * 12 + (ref.month - 1) + delta;
  return { year: Math.floor(index / 12), month: (index % 12) + 1 };
}

export function daysInMonth(ref: MonthRef): number {
  return new Date(ref.year, ref.month, 0).getDate();
}

/** Wszystkie dni miesiąca jako ISO yyyy-MM-dd. */
export function monthDays(ref: MonthRef): string[] {
  const month = String(ref.month).padStart(2, '0');
  return Array.from({ length: daysInMonth(ref) }, (_, index) => {
    const day = String(index + 1).padStart(2, '0');
    return `${ref.year}-${month}-${day}`;
  });
}

/** Nagłówek paska miesiąca: „01.08–31.08.2026". */
export function monthRangeLabel(ref: MonthRef): string {
  const month = String(ref.month).padStart(2, '0');
  return `01.${month}–${String(daysInMonth(ref)).padStart(2, '0')}.${month}.${ref.year}`;
}

const WEEKDAY_FORMAT = new Intl.DateTimeFormat('pl-PL', { weekday: 'short' });

/** Skrót dnia tygodnia z Intl (nie zaszyty): „pon." → „Pon". */
export function weekdayShort(isoDate: string): string {
  const raw = WEEKDAY_FORMAT.format(new Date(`${isoDate}T12:00:00`)).replace(/\.$/, '');
  return raw.charAt(0).toUpperCase() + raw.slice(1);
}

/**
 * Dwuliterowy skrót do komórki paska: pełny miesiąc to 31 kolumn na szerokości
 * strony, więc „Niedz" po prostu się nie mieści i etykiety wchodziły na sąsiadów.
 * Dwie litery wystarczają, bo są różne dla wszystkich siedmiu dni (Po, Wt, Śr,
 * Cz, Pt, So, Ni) — jednoliterowy skrót Intl myli poniedziałek z piątkiem.
 * Pełna nazwa dnia zostaje w etykiecie dostępności komórki.
 */
export function weekdayInitials(isoDate: string): string {
  return weekdayShort(isoDate).slice(0, 2);
}

/** Sobota/niedziela — wyróżnienie weekendu w pasku. */
export function isWeekend(isoDate: string): boolean {
  const weekday = new Date(`${isoDate}T12:00:00`).getDay();
  return weekday === 0 || weekday === 6;
}

/** Etykieta statusu pozycji — status nigdy nie jest komunikowany samym kolorem. */
export function statusLabel(status: TimelineItem['status']): string {
  switch (status) {
    case 'pending':
      return 'sugestia';
    case 'active':
      return 'do rozliczenia';
    case 'archived':
      return 'rozliczone';
  }
}

/** Stan panelu zapamiętywany lokalnie (per konto, jak deltaLink). */
interface StoredTimelineState {
  collapsed?: boolean;
  month?: MonthRef;
}

/**
 * Globalna, zwijana oś czasu pod kafelkami podsumowania — poziom 1 (pasek miesiąca)
 * i poziom 2 (lista dnia). Klik w pozycję nierozliczoną nawiguje do właściwej
 * zakładki i przewija do elementu (wspólny util scroll-highlight); rozliczone
 * są wyszarzone i nieklikalne.
 */
@Component({
  selector: 'app-timeline-panel',
  imports: [DatePipe, DurationPipe, DocumentHistory],
  template: `
    <section class="card timeline" aria-label="Oś czasu">
      <header class="timeline-header">
        <button
          class="btn btn-ghost toggle"
          (click)="toggleCollapsed()"
          [attr.aria-expanded]="!collapsed()"
        >
          <span aria-hidden="true">{{ collapsed() ? '▸' : '▾' }}</span> Oś czasu
        </button>

        @if (!collapsed()) {
          <div class="month-nav" (keydown.arrowLeft)="shift(-1)" (keydown.arrowRight)="shift(1)">
            <button class="btn btn-ghost" (click)="shift(-1)" aria-label="Poprzedni miesiąc">←</button>
            <span class="month-label" aria-live="polite">{{ monthLabel() }}</span>
            <button class="btn btn-ghost" (click)="shift(1)" aria-label="Następny miesiąc">→</button>
          </div>
        }
      </header>

      @if (!collapsed()) {
        @if (error()) {
          <div class="error-box">
            {{ error() }}
            <button class="btn btn-ghost" (click)="loadMonth()">Spróbuj ponownie</button>
          </div>
        }

        <!-- minmax(0, 1fr) zamiast 1fr: samo 1fr ma minimum "min-content", więc przy 31
             kolumnach etykiety rozpychały komórki i wchodziły na sąsiednie dni.
             Zero jako minimum pozwala kolumnie zwęzić się poniżej swojej treści,
             a przycinanie ogarnia już overflow komórki. -->
        <div
          class="month-grid"
          role="group"
          aria-label="Dni miesiąca"
          [style.grid-template-columns]="'repeat(' + days().length + ', minmax(0, 1fr))'"
        >
          @for (day of days(); track day.date) {
            <button
              class="day-cell"
              [class.weekend]="day.weekend"
              [class.today]="day.today"
              [class.selected]="selectedDate() === day.date"
              [disabled]="day.total === 0"
              (click)="selectDay(day.date)"
              [attr.aria-label]="day.ariaLabel"
            >
              <span class="day-date">{{ day.label }}</span>
              <span class="day-name" aria-hidden="true">{{ day.weekday }}</span>
              @if (day.total > 0) {
                <span class="day-badge">{{ day.total }}</span>
              }
            </button>
          }
        </div>

        @if (selectedDate(); as date) {
          <div class="day-items">
            <h4 class="day-items-title">{{ date | date: 'EEEE, d MMMM y' }}</h4>
            @if (dayLoading()) {
              <p class="empty-state">Ładowanie pozycji…</p>
            } @else {
              @for (item of dayItems(); track item.type + '-' + item.id) {
                <div class="timeline-row">
                  <!-- Rozliczone: wyszarzone i nieklikalne; pozostałe nawigują do zakładki. -->
                  <button
                    class="timeline-item"
                    [class.archived]="item.status === 'archived'"
                    [disabled]="item.status === 'archived'"
                    (click)="openItem(item)"
                    [attr.aria-label]="itemAriaLabel(item)"
                  >
                    <span class="item-hours">
                      {{ item.startedAt | date: 'HH:mm' }}–{{ item.endedAt | date: 'HH:mm' }}
                    </span>
                    <span class="item-title">{{ item.title }}</span>
                    @if (item.caseName) {
                      <span class="item-case text-muted">
                        {{ item.caseName }}
                        @if (formatCaseMeta(item.caseNumber, item.clientName); as meta) {
                          · {{ meta }}
                        }
                      </span>
                    }
                    <span class="item-duration">{{ item.durationMinutes | duration }}</span>
                    <span class="item-status status-{{ item.status }}">{{ statusLabel(item.status) }}</span>
                  </button>
                  @if (item.externalId; as externalId) {
                    <!-- Poziom 3: chronologia modyfikacji + diff wersji, tylko dokumenty. -->
                    <button
                      class="btn btn-ghost history-toggle"
                      (click)="toggleHistory(item)"
                      [attr.aria-expanded]="historyKey() === historyKeyOf(item)"
                    >
                      {{ historyKey() === historyKeyOf(item) ? 'Ukryj historię' : 'Historia zmian' }}
                    </button>
                  }
                </div>
                @if (item.externalId; as externalId) {
                  @if (historyKey() === historyKeyOf(item)) {
                    <app-document-history [externalId]="externalId" [fileName]="item.title" />
                  }
                }
              } @empty {
                <p class="empty-state">Brak pozycji tego dnia.</p>
              }
            }
          </div>
        }
      }
    </section>
  `,
  styles: `
    .timeline { margin-bottom: var(--space-4); padding: var(--space-3) var(--space-4); }
    .timeline-header { display: flex; align-items: center; gap: var(--space-4); flex-wrap: wrap; }
    .toggle { font-weight: 600; }
    .month-nav { display: flex; align-items: center; gap: var(--space-2); margin-left: auto; }
    .month-label { font-variant-numeric: tabular-nums; min-width: 12ch; text-align: center; }

    /* Fallback mobilny: siatka może się przewijać w poziomie, ale strona nie. */
    .month-grid {
      display: grid;
      gap: 2px;
      margin-top: var(--space-3);
      overflow-x: auto;
    }
    .day-cell {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 1px;
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      background: var(--surface);
      padding: var(--space-1) 1px;
      cursor: pointer;
      font-size: var(--font-size-sm);
      font-family: inherit;
      color: var(--text);
      /* Komórka nigdy nie rozpycha kolumny: nadmiar treści (długa liczba pozycji)
         jest przycinany, zamiast wchodzić na sąsiedni dzień. */
      min-width: 0;
      overflow: hidden;
    }
    .day-cell:disabled { opacity: 0.45; cursor: default; }
    .day-cell:hover:not(:disabled) { background: var(--surface-alt); }
    /* Weekend i dziś wyróżnione tokenami — działa we wszystkich trzech motywach. */
    .day-cell.weekend { background: var(--surface-alt); }
    .day-cell.today { border-color: var(--accent); box-shadow: inset 0 2px 0 var(--accent); }
    .day-cell.selected { background: var(--accent-soft); border-color: var(--accent); }
    .day-date { font-variant-numeric: tabular-nums; white-space: nowrap; }
    .day-name { font-size: 0.7rem; line-height: 1; color: var(--text-muted); }
    .day-badge {
      background: var(--accent);
      color: var(--accent-contrast);
      border-radius: 999px;
      min-width: 1.1rem;
      padding: 0 3px;
      font-size: 0.7rem;
      line-height: 1.1rem;
      text-align: center;
    }

    .day-items { margin-top: var(--space-3); border-top: 1px solid var(--border); padding-top: var(--space-3); }
    .day-items-title { font-size: var(--font-size-base); text-transform: capitalize; margin-bottom: var(--space-2); }
    .timeline-row { display: flex; align-items: center; gap: var(--space-2); }
    /* Przycisk historii ma stałą szerokość i nie kurczy się do wielolinijkowego
       napisu; miejsce oddaje mu pozycja obok (flex: 1 + min-width: 0). */
    .history-toggle { flex: 0 0 auto; font-size: var(--font-size-sm); white-space: nowrap; }
    .timeline-item {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      /* width: 100% wypychało przycisk historii poza kartę — pozycja ma zająć
         RESZTĘ wiersza, a nie jego całą szerokość. */
      flex: 1;
      min-width: 0;
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      background: var(--surface);
      padding: var(--space-2) var(--space-3);
      margin: var(--space-1) 0;
      cursor: pointer;
      font-size: var(--font-size-sm);
      font-family: inherit;
      color: var(--text);
      text-align: left;
    }
    .timeline-item:hover:not(:disabled) { background: var(--surface-alt); }
    .timeline-item.archived { opacity: 0.55; cursor: default; }
    .item-hours { font-variant-numeric: tabular-nums; white-space: nowrap; }
    /* min-width: 0 dopuszcza skrócenie wielokropkiem — bez niego element flex nie
       zejdzie poniżej szerokości swojej treści i to on rozpycha wiersz. */
    .item-title { font-weight: 600; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .item-case { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .item-duration { margin-left: auto; white-space: nowrap; }
    /* Status kolorem + etykietą tekstową — nigdy samym kolorem. */
    .item-status { border-radius: 999px; padding: 0 var(--space-2); white-space: nowrap; }
    .status-pending { background: var(--accent-soft); color: var(--accent); }
    .status-active { background: var(--success-soft); color: var(--success); }
    .status-archived { background: var(--surface-alt); color: var(--text-muted); }
  `,
})
export class TimelinePanel implements OnInit {
  private api = inject(ApiService);
  private auth = inject(AuthService);
  private router = inject(Router);
  private toasts = inject(ToastService);
  private dataRefresh = inject(DataRefreshService);

  protected readonly formatCaseMeta = formatCaseMeta;
  protected readonly statusLabel = statusLabel;

  protected collapsed = signal(true);
  protected month = signal<MonthRef>(currentMonth(new Date()));
  protected monthData = signal<Map<string, TimelineDay>>(new Map());
  protected selectedDate = signal<string | null>(null);
  protected dayItems = signal<TimelineItem[]>([]);
  protected dayLoading = signal(false);
  protected error = signal<string | null>(null);

  protected monthLabel = computed(() => monthRangeLabel(this.month()));

  /** Komórki paska: data, skrót dnia, licznik pozycji, wyróżnienia. */
  protected days = computed(() => {
    const todayIso = toIso(new Date());
    const counts = this.monthData();
    return monthDays(this.month()).map((date) => {
      const day = counts.get(date);
      const total = day ? day.pendingCount + day.activeCount + day.archivedCount : 0;
      const [, , dayOfMonth] = date.split('-');
      return {
        date,
        // Sam dzień miesiąca: miesiąc i rok stoją w nagłówku paska, a powtarzanie
        // ich w każdej z 31 komórek było jedynym powodem, dla którego się nie mieściły.
        label: dayOfMonth,
        weekday: weekdayInitials(date),
        weekend: isWeekend(date),
        today: date === todayIso,
        total,
        // Etykieta dostępności niesie pełną nazwę dnia — czytnik ekranu nie musi
        // odgadywać skrótu z komórki.
        ariaLabel: `${dayOfMonth} ${weekdayShort(date)}, ${total} pozycji`,
      };
    });
  });

  constructor() {
    // Zmiana danych z dowolnej zakładki (sync, zatwierdzenie, rozliczenie)
    // odświeża pasek i rozwiniętą listę dnia.
    let lastSeen: number | null = null;
    effect(() => {
      const version = this.dataRefresh.changes();
      if (lastSeen !== null && version !== lastSeen && !untracked(this.collapsed)) {
        untracked(() => void this.reloadAll());
      }
      lastSeen = version;
    });
  }

  ngOnInit(): void {
    this.restoreState();
    if (!this.collapsed()) {
      void this.loadMonth();
    }
  }

  protected toggleCollapsed(): void {
    this.collapsed.update((value) => !value);
    this.persistState();
    if (!this.collapsed()) {
      void this.loadMonth();
    }
  }

  protected shift(delta: number): void {
    this.month.set(shiftMonth(this.month(), delta));
    // Pasek zostaje, ale lista dnia dotyczyła poprzedniego miesiąca.
    this.selectedDate.set(null);
    this.persistState();
    void this.loadMonth();
  }

  protected selectDay(date: string): void {
    // Ponowny klik w wybrany dzień zwija listę; inny dzień przełącza bez zwijania paska.
    if (this.selectedDate() === date) {
      this.selectedDate.set(null);
      return;
    }
    this.selectedDate.set(date);
    void this.loadDay(date);
  }

  protected async loadMonth(): Promise<void> {
    this.error.set(null);
    const days = monthDays(this.month());
    try {
      const result = await this.api.getTimeline(days[0], days[days.length - 1]);
      this.monthData.set(new Map(result.map((day) => [day.date, day])));
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać osi czasu.'));
    }
  }

  private async loadDay(date: string): Promise<void> {
    this.dayLoading.set(true);
    try {
      this.dayItems.set(await this.api.getTimelineDay(date));
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać pozycji dnia.'));
    } finally {
      this.dayLoading.set(false);
    }
  }

  private async reloadAll(): Promise<void> {
    await this.loadMonth();
    const date = this.selectedDate();
    if (date !== null) {
      await this.loadDay(date);
    }
  }

  protected itemAriaLabel(item: TimelineItem): string {
    return `${item.title}, ${statusLabel(item.status)}`;
  }

  /** Klucz rozwiniętej historii — jedna naraz, ponowny klik zwija. */
  protected historyKey = signal<string | null>(null);

  protected historyKeyOf(item: TimelineItem): string {
    return `${item.type}-${item.id}`;
  }

  protected toggleHistory(item: TimelineItem): void {
    const key = this.historyKeyOf(item);
    this.historyKey.update((current) => (current === key ? null : key));
  }

  /**
   * Nawigacja do pozycji: sugestia → zakładka Sugestie, wpis → Wpisy czasu,
   * z przewinięciem i chwilowym podświetleniem. Element może nie istnieć
   * (filtr, inne okno) — wtedy toast z wyjaśnieniem, nie cisza.
   */
  protected async openItem(item: TimelineItem): Promise<void> {
    if (item.status === 'archived') {
      return;
    }

    const route = item.type === 'suggestion' ? '/suggestions' : '/time-entries';
    const elementId = item.type === 'suggestion' ? `suggestion-${item.id}` : `time-entry-${item.id}`;
    await this.router.navigate([route]);

    const found = await scrollToAndHighlight(elementId);
    if (!found) {
      this.toasts.show(
        'Pozycja nie jest widoczna na liście. Może ją ukrywać filtr albo inny widok.',
        { kind: 'error' },
      );
    }
  }

  /** Klucz per konto — jak deltaLink: zmiana użytkownika nie czyta cudzego stanu. */
  private storageKey(): string {
    return `timesuggestions.timeline.${this.auth.account?.homeAccountId ?? 'unknown'}`;
  }

  private restoreState(): void {
    try {
      const stored = JSON.parse(localStorage.getItem(this.storageKey()) ?? '{}') as StoredTimelineState;
      if (typeof stored.collapsed === 'boolean') {
        this.collapsed.set(stored.collapsed);
      }
      if (stored.month
        && Number.isInteger(stored.month.year)
        && Number.isInteger(stored.month.month)
        && stored.month.month >= 1 && stored.month.month <= 12) {
        this.month.set(stored.month);
      }
    } catch {
      // Uszkodzony stan lokalny nie może wysypać aplikacji — start od domyślnych.
    }
  }

  private persistState(): void {
    const state: StoredTimelineState = { collapsed: this.collapsed(), month: this.month() };
    localStorage.setItem(this.storageKey(), JSON.stringify(state));
  }
}

function toIso(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  return `${date.getFullYear()}-${month}-${day}`;
}
