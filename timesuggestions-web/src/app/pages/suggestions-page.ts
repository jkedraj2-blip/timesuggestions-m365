import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ApiService } from '../services/api.service';
import { CaseInfo, Suggestion, SuggestionSource, SuggestionStatus } from '../models/api.models';
import { SuggestionCard, SuggestionResolved } from '../components/suggestion-card';

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
    </div>

    @if (syncMessage()) {
      <p class="info-box">{{ syncMessage() }}</p>
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
  `,
})
export class SuggestionsPage implements OnInit {
  private api = inject(ApiService);

  protected suggestions = signal<Suggestion[]>([]);
  protected cases = signal<CaseInfo[]>([]);
  protected sourceFilter = signal<SourceFilter>('all');
  protected statusFilter = signal<StatusFilter>('pending');
  protected loading = signal(false);
  protected syncing = signal(false);
  protected error = signal<string | null>(null);
  protected syncMessage = signal<string | null>(null);

  protected visibleSuggestions = computed(() => {
    const activeFilter = this.sourceFilter();
    const all = this.suggestions();
    return activeFilter === 'all' ? all : all.filter((s) => s.source === activeFilter);
  });

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
    this.syncMessage.set(null);
    try {
      const report = await this.api.syncNow();
      this.syncMessage.set(
        `Synchronizacja zakończona: ${report.created} nowych sugestii, ${report.skippedExisting} pominiętych (już istniały).`,
      );
      await this.loadData();
    } catch (error) {
      this.error.set(this.toUserMessage(error, 'Synchronizacja nie powiodła się.'));
    } finally {
      this.syncing.set(false);
    }
  }

  protected onResolved(event: SuggestionResolved): void {
    // Rozstrzygnięta sugestia znika z bieżącej listy bez ponownego pobierania.
    this.suggestions.update((current) => current.filter((s) => s.id !== event.suggestion.id));
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
