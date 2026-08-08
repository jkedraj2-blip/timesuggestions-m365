import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { ApiService, SyncStage } from '../services/api.service';
import { DataRefreshService } from '../services/data-refresh.service';
import { SummaryStore } from '../services/summary-store';
import { ToastService } from '../services/toast.service';
import { toUserMessage } from '../services/user-message';
import {
  CaseInfo,
  Suggestion,
  SuggestionSource,
  SuggestionStatus,
  SyncFetchedCounts,
  SyncFilteredOutCounts,
  SyncReport,
} from '../models/api.models';
import { FormsModule } from '@angular/forms';
import { SuggestionCard, SuggestionResolved } from '../components/suggestion-card';
import { formatDuration } from '../pipes/duration.pipe';
import { polishPlural } from '../pipes/polish-plural';
import { SYNC_DAYS_BACK } from '../services/graph-config';

/** Klucz localStorage z preferencją użytkownika dla czasu dokumentów. */
const DOCUMENT_MINUTES_STORAGE_KEY = 'timesuggestions.defaultDocumentMinutes';
const DOCUMENT_MINUTES_MIN = 1;
const DOCUMENT_MINUTES_MAX = 480;
/** Wartość startowa pola — odpowiednik Suggestions:DefaultDocumentDurationMinutes w backendzie. */
const DOCUMENT_MINUTES_DEFAULT = 30;

/** Wartość spoza zakresu traktujemy jak brak preferencji — backend użyje swojej konfiguracji. */
export function normalizedDocumentMinutes(raw: unknown): number | undefined {
  const value = Number(raw);
  const isValid = Number.isInteger(value)
    && value >= DOCUMENT_MINUTES_MIN
    && value <= DOCUMENT_MINUTES_MAX;
  return isValid ? value : undefined;
}

type SourceFilter = 'all' | SuggestionSource;
type StatusFilter = Extract<SuggestionStatus, 'pending' | 'rejected'>;

/**
 * Nagłówek raportu mówi o EFEKCIE syncu (nowe/zaktualizowane/usunięte),
 * nie o licznikach technicznych — "pominięto" i "pobrano" to szczegóły.
 */
export function syncReportHeadline(report: Pick<SyncReport, 'created' | 'updated' | 'removed'>): string {
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
  if (parts.length === 0) {
    return 'Synchronizacja zakończona — bez zmian. Wszystkie sugestie są aktualne.';
  }
  return `Synchronizacja zakończona: ${parts.join(', ')}.`;
}

/**
 * "Sprawdzono" zamiast "pobrano" — każdy sync celowo pobiera pełny snapshot
 * okna (wykrywanie usuniętych/przeniesionych spotkań), więc liczba powtarza
 * się co sync i ma brzmieć jak kontrola, nie jak nowe dane. Zera pomijamy.
 */
export function syncCheckedLine(fetched: SyncFetchedCounts): string {
  const meetings = fetched.calendarEvents;
  const files = fetched.driveFiles;
  if (meetings === 0 && files === 0) {
    return `Brak spotkań i plików do sprawdzenia w ostatnich ${SYNC_DAYS_BACK} dniach.`;
  }
  const meetingsText = `${meetings} ${polishPlural(meetings, 'spotkanie', 'spotkania', 'spotkań')}`;
  const filesText = `${files} ${polishPlural(files, 'plik', 'pliki', 'plików')}`;
  if (files === 0) {
    return `Sprawdzono ${meetingsText} z ostatnich ${SYNC_DAYS_BACK} dni.`;
  }
  if (meetings === 0) {
    return `Sprawdzono ${filesText} z ostatnich ${SYNC_DAYS_BACK} dni.`;
  }
  return `Sprawdzono ${meetingsText} z ostatnich ${SYNC_DAYS_BACK} dni i ${filesText}.`;
}

/** "Pominięto (już istniały)" po ludzku — powtórny sync niczego nie duplikuje. */
export function syncSkippedLine(count: number): string {
  const subject = polishPlural(count, 'pozycja była', 'pozycje były', 'pozycji było');
  return `${count} ${subject} już wcześniej na liście sugestii — nic nie duplikujemy.`;
}

/**
 * Rozbicie odrzuceń na niezerowe pozycje sklejone separatorem — join() zamiast
 * spanów z doklejonym "· " w szablonie, bo tam separator wisiał także po
 * ostatniej wyrenderowanej pozycji. Etykiety opisują powód z perspektywy
 * użytkownika, nie nazwy licznika.
 */
export function filteredOutBreakdown(filtered: SyncFilteredOutCounts): string {
  const parts: string[] = [];
  if (filtered.private > 0) {
    parts.push(`${filtered.private} prywatne lub poufne (ich tytuły nie opuszczają przeglądarki)`);
  }
  if (filtered.cancelled > 0) {
    parts.push(`${filtered.cancelled} odwołane`);
  }
  if (filtered.tooShort > 0) {
    parts.push(`${filtered.tooShort} krótsze niż 5 minut`);
  }
  if (filtered.allDay > 0) {
    parts.push(`${filtered.allDay} całodniowe`);
  }
  if (filtered.invalidDates > 0) {
    parts.push(`${filtered.invalidDates} z błędnymi datami`);
  }
  if (filtered.notOfficeDocument > 0) {
    parts.push(`${filtered.notOfficeDocument} ${polishPlural(
      filtered.notOfficeDocument, 'plik inny niż Word/Excel', 'pliki inne niż Word/Excel', 'plików innych niż Word/Excel')}`);
  }
  if (filtered.outsideWindow > 0) {
    parts.push(`${filtered.outsideWindow} poza zakresem ostatnich ${SYNC_DAYS_BACK} dni (np. spotkanie, które jeszcze się nie odbyło)`);
  }
  if (filtered.notModifiedByUser > 0) {
    parts.push(`${filtered.notModifiedByUser} zmodyfikowane przez kogoś innego`);
  }
  return parts.join(' · ');
}

/** Pełna linia odrzuceń: wstęp z odmianą + rozbicie na powody. */
export function filteredOutLine(filtered: SyncFilteredOutCounts): string {
  const subject = polishPlural(
    filtered.total,
    'pozycję, która nie jest czasem pracy',
    'pozycje, które nie są czasem pracy',
    'pozycji, które nie są czasem pracy',
  );
  return `Pominięto ${filtered.total} ${subject}: ${filteredOutBreakdown(filtered)}.`;
}

@Component({
  selector: 'app-suggestions-page',
  imports: [SuggestionCard, FormsModule],
  template: `
    <div class="toolbar">
      <button class="btn btn-primary" (click)="sync()" [disabled]="syncing() || loading()">
        {{ syncing() ? 'Synchronizuję…' : 'Synchronizuj' }}
      </button>

      <label class="field doc-minutes" title="Ile minut przyjąć dla sugestii z dokumentu — Graph nie mierzy czasu edycji. Możesz to potem poprawić na każdej karcie.">
        Domyślny czas dokumentu (min)
        <input type="number" min="1" max="480" [(ngModel)]="documentMinutesDraft" (change)="saveDocumentMinutes()" />
      </label>

      <div class="filter-group">
        <span class="text-muted">Źródło:</span>
        <button class="btn" [class.btn-ghost]="sourceFilter() !== 'all'" (click)="sourceFilter.set('all')">wszystkie</button>
        <button class="btn" [class.btn-ghost]="sourceFilter() !== 'calendar'" (click)="sourceFilter.set('calendar')">spotkania</button>
        <button class="btn" [class.btn-ghost]="sourceFilter() !== 'document'" (click)="sourceFilter.set('document')">dokumenty</button>
      </div>

      <div class="filter-group">
        <span class="text-muted">Status:</span>
        <button class="btn" [class.btn-ghost]="statusFilter() !== 'pending'" (click)="setStatusFilter('pending')">oczekujące</button>
        <button class="btn" [class.btn-ghost]="statusFilter() !== 'rejected'" (click)="setStatusFilter('rejected')">odrzucone</button>
      </div>

      @if (autoMatchedCount() > 0) {
        <button class="btn" (click)="approveAllMatched()" [disabled]="bulkApproving()">
          {{ bulkApproving() ? 'Zatwierdzam…' : 'Zatwierdź wszystkie dopasowane (' + autoMatchedCount() + ')' }}
        </button>
      }
    </div>

    @if (syncing()) {
      <div class="info-box sync-progress">
        <span class="spinner"></span>
        <span>{{ stageLabel() }}</span>
      </div>
    }

    @if (syncReport(); as report) {
      <details class="info-box report" open>
        <summary>
          <strong>{{ headline(report) }}</strong>
        </summary>
        <ul>
          <li>{{ checkedLine(report.fetched) }}</li>
          @if (report.skippedExisting > 0) {
            <li>{{ skippedLine(report.skippedExisting) }}</li>
          }
          @if (report.filteredOut.total > 0) {
            <li>{{ filteredLine(report.filteredOut) }}</li>
          }
          @if (report.aggregated > 0) {
            <li>Zwinięto {{ report.aggregated }} {{ plural(report.aggregated, 'dodatkową edycję', 'dodatkowe edycje', 'dodatkowych edycji') }} tego samego pliku w jedną sugestię dziennie.</li>
          }
          @if (report.deduplicated > 0) {
            <li>Scalono {{ report.deduplicated }} {{ plural(report.deduplicated, 'zduplikowaną pozycję', 'zduplikowane pozycje', 'zduplikowanych pozycji') }} z Graph (ten sam element pobrany wielokrotnie).</li>
          }
          @if (report.updated > 0) {
            <li>Zaktualizowano {{ report.updated }} {{ plural(report.updated, 'istniejącą sugestię', 'istniejące sugestie', 'istniejących sugestii') }} (np. po zmianie nazwy pliku, tytułu lub terminu spotkania).</li>
          }
          @if (report.removed > 0) {
            <li>Usunięto {{ report.removed }} {{ plural(report.removed, 'nieaktualną sugestię', 'nieaktualne sugestie', 'nieaktualnych sugestii') }} (spotkania usunięte lub nierozliczalne, skasowane pliki).</li>
          }
          @if (report.created > 0) {
            <li>
              Dopasowanie nowych: {{ report.matched.single }} automatycznie,
              {{ report.matched.ambiguous }} niejednoznacznie,
              {{ report.matched.none }} bez sprawy.
            </li>
          }
        </ul>
      </details>
    }

    @if (error()) {
      <div class="error-box">
        {{ error() }}
        <button class="btn btn-ghost" (click)="loadData()">Spróbuj ponownie</button>
      </div>
    }

    @if (loading()) {
      <p class="empty-state">Ładowanie sugestii…</p>
    } @else {
      @for (suggestion of visibleSuggestions(); track suggestion.id) {
        <app-suggestion-card
          [suggestion]="suggestion"
          [cases]="cases()"
          (resolved)="onResolved($event)"
        />
      } @empty {
        @if (statusFilter() === 'rejected') {
          <p class="empty-state">Brak odrzuconych sugestii.</p>
        } @else {
          <div class="empty-state">
            <p><strong>Brak oczekujących sugestii.</strong></p>
            <p>
              Kliknij „Synchronizuj", aby pobrać spotkania z kalendarza Outlook i dokumenty
              z OneDrive z ostatnich {{ syncDaysBack }} dni. Aplikacja zamieni je na propozycje wpisów czasu pracy.
            </p>
          </div>
        }
      }
    }
  `,
  styles: `
    .toolbar { display: flex; align-items: center; gap: var(--space-5); margin-bottom: var(--space-4); flex-wrap: wrap; }
    .filter-group { display: flex; align-items: center; gap: var(--space-1); }
    .sync-progress { display: flex; align-items: center; gap: var(--space-3); }
    .spinner {
      width: 14px; height: 14px; border-radius: 50%;
      border: 2px solid var(--accent-soft); border-top-color: var(--accent);
      animation: spin 0.8s linear infinite;
    }
    @keyframes spin { to { transform: rotate(360deg); } }
    .report summary { cursor: pointer; }
    .report ul { margin: var(--space-2) 0 0; padding-left: var(--space-5); }
  `,
})
export class SuggestionsPage implements OnInit {
  private api = inject(ApiService);
  private summaryStore = inject(SummaryStore);
  private toasts = inject(ToastService);
  private dataRefresh = inject(DataRefreshService);

  constructor() {
    // Przeładowanie po operacjach spoza tego widoku (np. "Cofnij" z toastu,
    // który mógł zostać kliknięty już po zmianie zakładki).
    let lastSeen: number | null = null;
    effect(() => {
      const version = this.dataRefresh.changes();
      if (lastSeen !== null && version !== lastSeen) {
        untracked(() => void this.loadData());
      }
      lastSeen = version;
    });
  }

  protected suggestions = signal<Suggestion[]>([]);
  protected cases = signal<CaseInfo[]>([]);
  protected sourceFilter = signal<SourceFilter>('all');
  protected statusFilter = signal<StatusFilter>('pending');
  protected loading = signal(false);
  protected syncing = signal(false);
  protected error = signal<string | null>(null);
  protected syncReport = signal<SyncReport | null>(null);
  protected syncStage = signal<SyncStage | null>(null);

  /** Etap synchronizacji po ludzku — user widzi, że 30-sekundowy sync faktycznie pracuje. */
  protected stageLabel = computed(() => {
    const stage = this.syncStage();
    switch (stage?.kind) {
      case 'calendar':
        return `Pobieram spotkania z kalendarza Outlook (strona ${stage.page})…`;
      case 'files':
        return `Przeglądam pliki na OneDrive (strona ${stage.page})…`;
      case 'processing':
        return 'Przetwarzam dane i tworzę sugestie…';
      default:
        return 'Rozpoczynam synchronizację…';
    }
  });

  // Teksty raportu budują czyste, testowalne funkcje modułu — tu tylko aliasy dla szablonu.
  protected readonly headline = syncReportHeadline;
  protected readonly checkedLine = syncCheckedLine;
  protected readonly skippedLine = syncSkippedLine;
  protected readonly filteredLine = filteredOutLine;
  protected readonly plural = polishPlural;
  protected readonly syncDaysBack = SYNC_DAYS_BACK;

  protected visibleSuggestions = computed(() => {
    const activeFilter = this.sourceFilter();
    const all = this.suggestions();
    return activeFilter === 'all' ? all : all.filter((s) => s.source === activeFilter);
  });

  protected bulkApproving = signal(false);

  /** Preferencja czasu dokumentów — trzymana lokalnie, wysyłana z każdą synchronizacją. */
  protected documentMinutesDraft = this.loadDocumentMinutes();

  /** Sugestie z jednoznacznie dopasowaną sprawą — te można zatwierdzić hurtem, bez zastanowienia. */
  protected autoMatchedCount = computed(() =>
    this.statusFilter() === 'pending'
      ? this.visibleSuggestions().filter((s) => s.caseId !== null && !s.isAmbiguous).length
      : 0,
  );

  async ngOnInit(): Promise<void> {
    await this.loadData();
  }

  protected setStatusFilter(status: StatusFilter): void {
    this.statusFilter.set(status);
    void this.loadData();
  }

  protected async sync(): Promise<void> {
    this.syncing.set(true);
    this.error.set(null);
    this.syncReport.set(null);
    this.syncStage.set(null);
    try {
      const report = await this.api.syncNow(
        (stage) => this.syncStage.set(stage),
        normalizedDocumentMinutes(this.documentMinutesDraft),
      );
      this.syncReport.set(report);
      await this.loadData();
      await this.summaryStore.refresh();
    } catch (error) {
      this.error.set(toUserMessage(error, 'Synchronizacja nie powiodła się.'));
    } finally {
      this.syncing.set(false);
      this.syncStage.set(null);
    }
  }

  protected onResolved(event: SuggestionResolved): void {
    // Rozstrzygnięta sugestia znika z bieżącej listy bez ponownego pobierania.
    this.suggestions.update((current) => current.filter((s) => s.id !== event.suggestion.id));
    void this.summaryStore.refresh();
    this.showResolvedToast(event);
  }

  /**
   * Potwierdzenie akcji z możliwością cofnięcia — karta nie znika "w nicość".
   * Callback undo celowo NIE woła loadData() tego komponentu (mógł już zostać
   * zniszczony po zmianie zakładki) — tylko API + powiadomienie o zmianie danych,
   * na które bieżący widok reaguje przeładowaniem.
   */
  private showResolvedToast(event: SuggestionResolved): void {
    switch (event.action) {
      case 'approved': {
        const entry = event.createdEntry;
        const details = entry
          ? `${formatDuration(entry.durationMinutes)} — ${entry.caseName}`
          : event.suggestion.title;
        this.toasts.show(`Zapisano wpis: ${details}. Zobacz zakładkę „Wpisy czasu".`, {
          undo: entry
            ? async () => {
                await this.api.deleteTimeEntry(entry.id);
                this.dataRefresh.notify();
                await this.summaryStore.refresh();
              }
            : undefined,
        });
        break;
      }
      case 'rejected':
        this.toasts.show(`Odrzucono sugestię „${event.suggestion.title}".`, {
          undo: async () => {
            await this.api.restore(event.suggestion.id);
            this.dataRefresh.notify();
            await this.summaryStore.refresh();
          },
        });
        break;
      case 'restored':
        this.toasts.show('Sugestia wróciła na listę oczekujących.');
        break;
    }
  }

  /** Hurtowe zatwierdzenie jednoznacznie dopasowanych — obietnica "jednego kliknięcia" w praktyce. */
  protected async approveAllMatched(): Promise<void> {
    const matched = this.visibleSuggestions().filter((s) => s.caseId !== null && !s.isAmbiguous);
    if (matched.length === 0) {
      return;
    }

    this.bulkApproving.set(true);
    this.error.set(null);
    try {
      const results = await Promise.allSettled(
        matched.map((suggestion) =>
          this.api.approve(suggestion.id, {
            caseId: suggestion.caseId!,
            durationMinutes: suggestion.durationMinutes,
            description: suggestion.proposedDescription || suggestion.title,
          }),
        ),
      );

      const approvedCount = results.filter((result) => result.status === 'fulfilled').length;
      const failedCount = results.length - approvedCount;
      this.toasts.show(
        failedCount === 0
          ? `Zapisano ${approvedCount} ${polishPlural(approvedCount, 'wpis', 'wpisy', 'wpisów')} czasu pracy.`
          : `Zapisano ${approvedCount}, nie udało się ${failedCount} — spróbuj pojedynczo.`,
        { kind: failedCount === 0 ? 'success' : 'error' },
      );

      await this.loadData();
      await this.summaryStore.refresh();
    } finally {
      this.bulkApproving.set(false);
    }
  }

  protected async loadData(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [suggestions, cases] = await Promise.all([
        this.api.getSuggestions({ status: this.statusFilter() }),
        this.api.getCases(),
      ]);
      this.suggestions.set(suggestions);
      this.cases.set(cases);
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać danych z backendu.'));
    } finally {
      this.loading.set(false);
    }
  }

  protected saveDocumentMinutes(): void {
    const value = normalizedDocumentMinutes(this.documentMinutesDraft);
    if (value !== undefined) {
      localStorage.setItem(DOCUMENT_MINUTES_STORAGE_KEY, String(value));
    }
  }

  private loadDocumentMinutes(): number {
    const stored = Number(localStorage.getItem(DOCUMENT_MINUTES_STORAGE_KEY));
    return Number.isInteger(stored) && stored >= DOCUMENT_MINUTES_MIN && stored <= DOCUMENT_MINUTES_MAX
      ? stored
      : DOCUMENT_MINUTES_DEFAULT;
  }
}
