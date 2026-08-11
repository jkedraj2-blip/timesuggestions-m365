import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from '../services/api.service';
import { DataRefreshService } from '../services/data-refresh.service';
import { SummaryStore } from '../services/summary-store';
import { ToastService } from '../services/toast.service';
import { TwoStepConfirm } from '../services/confirm-state';
import { toUserMessage } from '../services/user-message';
import { formatCaseMeta } from '../services/case-label';
import { DetectedGap, TimeEntriesResponse, TimeEntry } from '../models/api.models';
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

/**
 * Zakres dat do etykiety potwierdzenia: „01.01–09.08", a przy przełomie roku
 * „01.01.2025–09.08.2026". Rok pada wyłącznie wtedy, gdy granice leżą w różnych
 * latach: to jedyny przypadek, w którym sam dzień z miesiącem sugerowałyby kilka
 * tygodni zamiast kilkunastu miesięcy.
 */
export function formatRangeLabel(range: DateRange): string {
  const [fromYear, fromMonth, fromDay] = range.from.split('-');
  const [toYear, toMonth, toDay] = range.to.split('-');
  return fromYear === toYear
    ? `${fromDay}.${fromMonth}–${toDay}.${toMonth}`
    : `${fromDay}.${fromMonth}.${fromYear}–${toDay}.${toMonth}.${toYear}`;
}

/**
 * Etykieta uzbrojonego przycisku: „Na pewno? Rozliczysz 3 wpisy (2 godz.) z 03.08–09.08".
 * Zakres jest opcjonalny, bo przy „Rozlicz dzień" wynika z nagłówka dnia. Przy akcjach
 * hurtowych jest kluczowy: sama liczba wpisów nie mówi, czy „wszystko" to bieżący
 * miesiąc, czy osiem miesięcy wstecz.
 */
export function confirmSettleLabel(
  count: number,
  totalMinutes: number,
  range?: DateRange | null,
): string {
  const noun = polishPlural(count, 'wpis', 'wpisy', 'wpisów');
  const scope = range ? ` z ${formatRangeLabel(range)}` : '';
  return `Na pewno? Rozliczysz ${count} ${noun} (${formatDuration(totalMinutes)})${scope}`;
}

/**
 * Klucz grupy scalania: wpisy dokumentowe tego samego pliku z tego samego dnia.
 * null = wpis niescalalny (kalendarzowy albo bez id pliku). Backend i tak waliduje —
 * klucz służy tylko temu, żeby checkbox pojawiał się wyłącznie tam, gdzie operacja
 * ma szansę być legalna.
 */
export function mergeGroupKey(entry: TimeEntry): string | null {
  return entry.source === 'document' && entry.sourceExternalId
    ? `${entry.entryDate}|${entry.sourceExternalId}`
    : null;
}

/**
 * Podgląd minut do dialogu scalania: suma samych sesji oraz suma z lukami między
 * posortowanymi przedziałami. Liczby są podglądem — wiążącą wartość liczy backend.
 */
export function mergePreview(entries: TimeEntry[]): { sessionsMinutes: number; withGapsMinutes: number } {
  const sorted = [...entries].sort((a, b) => a.startedAt.localeCompare(b.startedAt));
  const sessionsMinutes = sorted.reduce((sum, entry) => sum + entry.durationMinutes, 0);
  let gapMinutes = 0;
  for (let i = 1; i < sorted.length; i++) {
    const gapMs = new Date(sorted[i].startedAt).getTime() - new Date(sorted[i - 1].endedAt).getTime();
    gapMinutes += Math.max(0, Math.round(gapMs / 60_000));
  }
  return { sessionsMinutes, withGapsMinutes: sessionsMinutes + gapMinutes };
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
          <!-- Etykieta grupy jak przy filtrach na stronie sugestii — przyciski nie muszą
               powtarzać słowa „Rozlicz" i pasek robi się czytelniejszy. -->
          <div class="settle-actions">
            <span class="text-muted">Rozlicz:</span>
            <button class="btn" [class.btn-danger]="confirm.isArmed('week')"
              (click)="settleRange('week', $event)" [disabled]="settling()">
              {{ settleButtonLabel('week', 'ostatni tydzień') }}
            </button>
            <button class="btn" [class.btn-danger]="confirm.isArmed('month')"
              (click)="settleRange('month', $event)" [disabled]="settling()">
              {{ settleButtonLabel('month', 'bieżący miesiąc') }}
            </button>
            <button class="btn" [class.btn-danger]="confirm.isArmed('all')"
              (click)="settleRange('all', $event)" [disabled]="settling()">
              {{ settleButtonLabel('all', 'wszystko') }}
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

    @if (selectedCount() >= 2) {
      <!-- Dialog scalania: wybór „tylko sesje / z przerwami" z podglądem minut.
           Pasek zamiast okna modalnego — spójnie z dwustopniowymi potwierdzeniami. -->
      <div class="card merge-bar">
        @if (mergeSelectionValid()) {
          <span>Scal {{ mergeCountLabel() }} dokumentu:</span>
          <button class="btn btn-primary" (click)="merge(false)" [disabled]="merging()">
            Tylko sesje ({{ mergePreviewMinutes().sessionsMinutes | duration }})
          </button>
          <button class="btn" (click)="merge(true)" [disabled]="merging()">
            Z przerwami ({{ mergePreviewMinutes().withGapsMinutes | duration }})
          </button>
        } @else {
          <span class="text-muted">Scalać można tylko wpisy tego samego dokumentu z jednego dnia.</span>
        }
        <button class="btn btn-ghost" (click)="clearSelection()">Wyczyść wybór</button>
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
                <!-- Wypełniony akcent (btn-primary): to główna akcja tego widoku i musi być
                     widoczna w ciemnym motywie; uzbrojone potwierdzenie przełącza na danger. -->
                <button class="btn day-settle"
                  [class.btn-primary]="!confirm.isArmed('day:' + day.date)"
                  [class.btn-danger]="confirm.isArmed('day:' + day.date)"
                  (click)="settleDay(day.date, $event)" [disabled]="settling()">
                  {{ settleButtonLabel('day:' + day.date, 'Rozlicz dzień') }}
                </button>
              }
            </header>

            @for (entry of day.entries; track entry.id) {
              <div class="card entry">
                <div class="entry-main">
                  <div class="entry-header">
                    @if (view() === 'active' && isMergeSelectable(entry)) {
                      <!-- Checkbox tylko tam, gdzie scalenie ma szansę być legalne
                           (≥2 wpisy tego samego dokumentu i dnia) — backend i tak waliduje. -->
                      <input type="checkbox" class="merge-check"
                        [checked]="selection().has(entry.id)"
                        (change)="toggleSelect(entry)"
                        [attr.aria-label]="'Zaznacz do scalenia: ' + entry.description" />
                    }
                    <span class="badge badge-neutral">{{ entry.source === 'calendar' ? '📅 Spotkanie' : '📄 Dokument' }}</span>
                    <span class="entry-hours text-muted">{{ entry.startedAt | date: 'HH:mm' }}–{{ entry.endedAt | date: 'HH:mm' }}</span>
                    <strong>{{ entry.caseName }}</strong>
                    @if (formatCaseMeta(entry.caseNumber, entry.clientName); as caseMeta) {
                      <!-- Numer sprawy to identyfikator z faktury — widoczny tam, gdzie
                           powstaje rozliczenie, zamiast szukania sprawy na innej zakładce. -->
                      <span class="text-muted">{{ caseMeta }}</span>
                    }
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
                  <div class="entry-actions">
                    <!-- Szybkie korekty ±15 min — dziennik korekt po stronie backendu. -->
                    <button class="btn btn-compact" (click)="adjust(entry, -15)"
                      [disabled]="busyId() === entry.id" aria-label="Odejmij 15 minut">−15</button>
                    <button class="btn btn-compact" (click)="adjust(entry, 15)"
                      [disabled]="busyId() === entry.id" aria-label="Dodaj 15 minut">+15</button>
                    @for (gap of entry.detectedGaps; track gap.startAt) {
                      <!-- Przycisk istnieje tylko, gdy wpis MA wykrytą przerwę (dane
                           sesji, nie heurystyka UI); znika po odjęciu, bo backend
                           zwraca wyłącznie przerwy jeszcze nieodjęte. -->
                      <button class="btn btn-compact" (click)="subtractGap(entry, gap)"
                        [disabled]="busyId() === entry.id">
                        Odejmij przerwę ({{ gap.minutes }} min)
                      </button>
                    }
                    @if (entry.suggestionIds.length > 1) {
                      <button class="btn btn-compact" (click)="unmerge(entry)"
                        [disabled]="busyId() === entry.id">
                        Rozdziel
                      </button>
                    }
                    <button class="btn btn-danger btn-compact" (click)="undoApproval(entry)"
                      [disabled]="busyId() === entry.id">
                      Cofnij zatwierdzenie
                    </button>
                  </div>
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
    /* Ta sama wysokość i typografia co .btn (padding space-2/space-4) — przełącznik
       nie może wyglądać jak mniejszy, obcy element obok przycisków akcji. */
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
      color: var(--text-muted);
      padding: var(--space-2) var(--space-4);
      cursor: pointer;
      font-size: var(--font-size-base);
      font-family: inherit;
      transition: background 0.15s, color 0.15s;
    }
    .view-option + .view-option { border-left: 1px solid var(--border); }
    .view-option:hover:not(.active) { background: var(--surface-alt); }
    /* Pełny akcent zamiast bladego podkreślenia — aktywny widok widać od razu,
       także w ciemnym motywie. */
    .view-option.active {
      background: var(--accent);
      color: var(--accent-contrast);
      font-weight: 600;
      cursor: default;
    }
    /* Akcje odsunięte na prawy brzeg: filtr widoku i operacje rozliczenia to
       osobne grupy, nie jeden ciąg przycisków. */
    .settle-actions { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; margin-left: auto; }
    .total { margin: 0 0 var(--space-4); font-size: var(--font-size-lg); }
    .day { margin-bottom: var(--space-5); }
    .day-header { display: flex; align-items: center; gap: var(--space-3); margin-bottom: var(--space-2); }
    .day-header h3 { font-size: var(--font-size-base); text-transform: capitalize; }
    .day-settle { margin-left: auto; }
    .entry { display: flex; align-items: center; gap: var(--space-4); margin: var(--space-2) 0; }
    .entry-main { flex: 1; }
    .entry-header { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; }
    .entry-hours { font-variant-numeric: tabular-nums; }
    .description { margin: var(--space-1) 0 0; }
    .origin { margin: var(--space-1) 0 0; font-size: var(--font-size-sm); }
    /* Kolumna akcji: operacje per wpis układają się pionowo, żeby karta nie puchła w szerz. */
    .entry-actions { display: flex; flex-direction: column; gap: var(--space-1); align-items: stretch; }
    .btn-compact { padding: var(--space-1) var(--space-3); font-size: var(--font-size-sm); }
    .merge-check { width: 1.1rem; height: 1.1rem; accent-color: var(--accent); }
    .merge-bar { display: flex; align-items: center; gap: var(--space-3); flex-wrap: wrap; margin-bottom: var(--space-4); padding: var(--space-3) var(--space-4); }
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
  protected merging = signal(false);

  /** Zaznaczenie do scalenia — czyszczone przy każdym przeładowaniu listy. */
  protected selection = signal<ReadonlySet<number>>(new Set());

  protected selectedCount = computed(() => this.selection().size);

  /** Zaznaczone wpisy z aktualnie załadowanej listy. */
  private selectedEntries = computed<TimeEntry[]>(() => {
    const ids = this.selection();
    return (this.data()?.days ?? [])
      .flatMap((day) => day.entries)
      .filter((entry) => ids.has(entry.id));
  });

  /** Scalenie legalne tylko dla wpisów jednej grupy (dokument + dzień) — backend i tak waliduje. */
  protected mergeSelectionValid = computed(() => {
    const entries = this.selectedEntries();
    if (entries.length < 2) {
      return false;
    }
    const keys = new Set(entries.map((entry) => mergeGroupKey(entry)));
    return keys.size === 1 && !keys.has(null);
  });

  protected mergePreviewMinutes = computed(() => mergePreview(this.selectedEntries()));

  /** Dwustopniowe potwierdzenie akcji rozliczenia — archiwizacja jest nieodwracalna. */
  protected confirm = new TwoStepConfirm();

  protected readonly formatCaseMeta = formatCaseMeta;

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
      // Świeża lista unieważnia zaznaczenie — id mogły zniknąć (scalenie, rozliczenie).
      this.selection.set(new Set());
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać wpisów.'));
    } finally {
      this.loading.set(false);
    }
  }

  /** Checkbox scalania tylko tam, gdzie w dniu są ≥2 wpisy tego samego dokumentu. */
  protected isMergeSelectable(entry: TimeEntry): boolean {
    const key = mergeGroupKey(entry);
    if (key === null || entry.archivedAt !== null) {
      return false;
    }
    const groupSize = (this.data()?.days ?? [])
      .flatMap((day) => day.entries)
      .filter((candidate) => mergeGroupKey(candidate) === key && candidate.archivedAt === null)
      .length;
    return groupSize >= 2;
  }

  protected toggleSelect(entry: TimeEntry): void {
    this.selection.update((current) => {
      const next = new Set(current);
      if (next.has(entry.id)) {
        next.delete(entry.id);
      } else {
        next.add(entry.id);
      }
      return next;
    });
  }

  protected clearSelection(): void {
    this.selection.set(new Set());
  }

  protected mergeCountLabel(): string {
    const count = this.selectedCount();
    return `${count} ${polishPlural(count, 'wpis', 'wpisy', 'wpisów')}`;
  }

  protected async merge(includeGaps: boolean): Promise<void> {
    if (!this.mergeSelectionValid()) {
      return;
    }
    this.merging.set(true);
    this.error.set(null);
    try {
      const merged = await this.api.mergeTimeEntries([...this.selection()], includeGaps);
      this.toasts.show(`Scalono w jeden wpis (${formatDuration(merged.durationMinutes)}).`);
      await this.loadData();
      await this.summaryStore.refresh();
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się scalić wpisów.'));
    } finally {
      this.merging.set(false);
    }
  }

  protected async unmerge(entry: TimeEntry): Promise<void> {
    await this.runEntryOperation(entry, async () => {
      const restored = await this.api.unmergeTimeEntry(entry.id);
      this.toasts.show(`Rozdzielono na ${restored.length} wpisy z sesji.`);
    }, 'Nie udało się rozdzielić wpisu.');
  }

  protected async adjust(entry: TimeEntry, minutes: number): Promise<void> {
    await this.runEntryOperation(entry, async () => {
      const updated = await this.api.adjustTimeEntry(entry.id, minutes);
      this.toasts.show(`Skorygowano czas wpisu do ${formatDuration(updated.durationMinutes)}.`);
    }, 'Nie udało się skorygować wpisu.');
  }

  protected async subtractGap(entry: TimeEntry, gap: DetectedGap): Promise<void> {
    await this.runEntryOperation(entry, async () => {
      const updated = await this.api.subtractGap(entry.id, gap.startAt, gap.endAt);
      this.toasts.show(`Odjęto przerwę ${gap.minutes} min — wpis ma teraz ${formatDuration(updated.durationMinutes)}.`);
    }, 'Nie udało się odjąć przerwy.');
  }

  /** Wspólny szkielet operacji na wpisie: blokada przycisków, obsługa błędu, odświeżenie. */
  private async runEntryOperation(
    entry: TimeEntry,
    operation: () => Promise<void>,
    fallbackMessage: string,
  ): Promise<void> {
    this.busyId.set(entry.id);
    this.error.set(null);
    try {
      await operation();
      await this.loadData();
      await this.summaryStore.refresh();
    } catch (error) {
      this.error.set(toUserMessage(error, fallbackMessage));
    } finally {
      this.busyId.set(null);
    }
  }

  /** Etykieta przycisku rozliczenia: uzbrojony pyta „Na pewno?" z liczbami z załadowanej listy. */
  protected settleButtonLabel(key: string, idleLabel: string): string {
    if (!this.confirm.isArmed(key)) {
      return idleLabel;
    }
    const isDay = key.startsWith('day:');
    const range = isDay
      ? { from: key.slice(4), to: key.slice(4) }
      : this.ranges()[key as 'week' | 'month' | 'all'];
    const { count, minutes } = this.countInRange(range);
    return confirmSettleLabel(count, minutes, isDay ? null : range);
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
