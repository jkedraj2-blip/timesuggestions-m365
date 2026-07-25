import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ApiService, SyncStage } from '../services/api.service';
import { SummaryStore } from '../services/summary-store';
import { ToastService } from '../services/toast.service';
import { CaseInfo, Suggestion, SuggestionSource, SuggestionStatus, SyncReport } from '../models/api.models';
import { SuggestionCard, SuggestionResolved } from '../components/suggestion-card';
import { DurationPipe } from '../pipes/duration.pipe';

type SourceFilter = 'all' | SuggestionSource;
type StatusFilter = Extract<SuggestionStatus, 'pending' | 'rejected'>;

@Component({
  selector: 'app-suggestions-page',
  imports: [SuggestionCard],
  template: `
    <div class="toolbar">
      <button class="btn btn-primary" (click)="sync()" [disabled]="syncing() || loading()">
        {{ syncing() ? 'Synchronizuję…' : 'Synchronizuj' }}
      </button>

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
          <strong>Synchronizacja zakończona:</strong>
          utworzono {{ report.created }} {{ report.created === 1 ? 'nową sugestię' : 'nowych sugestii' }},
          pominięto {{ report.skippedExisting }} (już istniały).
        </summary>
        <ul>
          <li>Pobrano {{ report.fetched.calendarEvents }} spotkań i {{ report.fetched.driveFiles }} plików.</li>
          @if (report.filteredOut.total > 0) {
            <li>
              Odfiltrowano {{ report.filteredOut.total }}:
              @if (report.filteredOut.private > 0) { <span>{{ report.filteredOut.private }} prywatne/poufne · </span> }
              @if (report.filteredOut.tooShort > 0) { <span>{{ report.filteredOut.tooShort }} krótsze niż 5 min · </span> }
              @if (report.filteredOut.allDay > 0) { <span>{{ report.filteredOut.allDay }} całodniowe · </span> }
              @if (report.filteredOut.notOfficeDocument > 0) { <span>{{ report.filteredOut.notOfficeDocument }} pliki inne niż Word/Excel · </span> }
              @if (report.filteredOut.outsideWindow > 0) { <span>{{ report.filteredOut.outsideWindow }} poza oknem 7 dni · </span> }
              @if (report.filteredOut.notModifiedByUser > 0) { <span>{{ report.filteredOut.notModifiedByUser }} zmodyfikowane przez inną osobę</span> }
            </li>
          }
          @if (report.aggregated > 0) {
            <li>Zwinięto {{ report.aggregated }} {{ report.aggregated === 1 ? 'dodatkową edycję' : 'dodatkowych edycji' }} tego samego pliku w jedną sugestię dziennie.</li>
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
              z OneDrive z ostatnich 7 dni. Aplikacja zamieni je na propozycje wpisów czasu pracy.
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
  private durationPipe = new DurationPipe();

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
        return 'Pobieram spotkania z kalendarza Outlook…';
      case 'files':
        return `Przeglądam pliki na OneDrive (strona ${stage.page})…`;
      case 'processing':
        return 'Przetwarzam dane i tworzę sugestie…';
      default:
        return 'Rozpoczynam synchronizację…';
    }
  });

  protected visibleSuggestions = computed(() => {
    const activeFilter = this.sourceFilter();
    const all = this.suggestions();
    return activeFilter === 'all' ? all : all.filter((s) => s.source === activeFilter);
  });

  protected bulkApproving = signal(false);

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
      const report = await this.api.syncNow((stage) => this.syncStage.set(stage));
      this.syncReport.set(report);
      await this.loadData();
      await this.summaryStore.refresh();
    } catch (error) {
      this.error.set(this.toUserMessage(error, 'Synchronizacja nie powiodła się.'));
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

  /** Potwierdzenie akcji z możliwością cofnięcia — karta nie znika "w nicość". */
  private showResolvedToast(event: SuggestionResolved): void {
    switch (event.action) {
      case 'approved': {
        const entry = event.createdEntry;
        const details = entry
          ? `${this.durationPipe.transform(entry.durationMinutes)} — ${entry.caseName}`
          : event.suggestion.title;
        this.toasts.show(`Zapisano wpis: ${details}. Zobacz zakładkę „Wpisy czasu".`, {
          undo: entry
            ? async () => {
                await this.api.deleteTimeEntry(entry.id);
                await this.loadData();
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
            await this.loadData();
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
          ? `Zapisano ${approvedCount} ${approvedCount === 1 ? 'wpis' : 'wpisów'} czasu pracy.`
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
      this.error.set(this.toUserMessage(error, 'Nie udało się pobrać danych z backendu.'));
    } finally {
      this.loading.set(false);
    }
  }

  private toUserMessage(error: unknown, fallback: string): string {
    return error instanceof Error ? `${fallback} ${error.message}` : fallback;
  }
}
