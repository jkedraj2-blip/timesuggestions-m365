import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ApiService } from '../services/api.service';
import { CaseInfo, Suggestion, SuggestionSource } from '../models/api.models';
import { SuggestionCard, SuggestionResolved } from './suggestion-card';

type SourceFilter = 'all' | SuggestionSource;

@Component({
  selector: 'app-suggestion-list',
  imports: [SuggestionCard],
  template: `
    <div class="toolbar">
      <button class="primary" (click)="sync()" [disabled]="syncing() || loading()">
        {{ syncing() ? 'Synchronizuję…' : 'Synchronizuj' }}
      </button>

      <div class="filter">
        <label>Źródło:</label>
        <button [class.active]="filter() === 'all'" (click)="filter.set('all')">wszystkie</button>
        <button [class.active]="filter() === 'calendar'" (click)="filter.set('calendar')">spotkania</button>
        <button [class.active]="filter() === 'document'" (click)="filter.set('document')">dokumenty</button>
      </div>
    </div>

    @if (syncSummary()) {
      <p class="info">{{ syncSummary() }}</p>
    }

    @if (error()) {
      <p class="error">{{ error() }}</p>
    }

    @if (loading()) {
      <p>Ładowanie sugestii…</p>
    } @else {
      @for (suggestion of visibleSuggestions(); track suggestion.id) {
        <app-suggestion-card
          [suggestion]="suggestion"
          [cases]="cases()"
          (resolved)="onResolved($event)"
        />
      } @empty {
        <p class="empty">Brak sugestii — kliknij „Synchronizuj", aby pobrać dane z Microsoft 365.</p>
      }
    }
  `,
  styles: `
    .toolbar { display: flex; align-items: center; gap: 24px; margin: 12px 0; flex-wrap: wrap; }
    .filter { display: flex; align-items: center; gap: 6px; }
    .filter label { color: #555; }
    button { padding: 6px 14px; border-radius: 6px; border: 1px solid #999; background: #f5f5f5; cursor: pointer; }
    button:hover:not(:disabled) { background: #e8e8e8; }
    button:disabled { opacity: 0.6; cursor: default; }
    button.primary { background: #1863c6; border-color: #1863c6; color: #fff; }
    button.primary:hover:not(:disabled) { background: #124e9e; }
    button.active { background: #dce9fa; border-color: #1863c6; }
    .info { color: #1a6b1a; }
    .error { color: #a32d2d; white-space: pre-wrap; }
    .empty { color: #666; margin-top: 24px; }
  `,
})
export class SuggestionList implements OnInit {
  private api = inject(ApiService);

  protected suggestions = signal<Suggestion[]>([]);
  protected cases = signal<CaseInfo[]>([]);
  protected filter = signal<SourceFilter>('all');
  protected loading = signal(false);
  protected syncing = signal(false);
  protected error = signal<string | null>(null);
  protected syncSummary = signal<string | null>(null);

  protected visibleSuggestions = computed(() => {
    const activeFilter = this.filter();
    const all = this.suggestions();
    return activeFilter === 'all' ? all : all.filter((s) => s.source === activeFilter);
  });

  async ngOnInit(): Promise<void> {
    await this.loadData();
  }

  protected async sync(): Promise<void> {
    this.syncing.set(true);
    this.error.set(null);
    this.syncSummary.set(null);
    try {
      const result = await this.api.syncNow();
      this.syncSummary.set(
        `Synchronizacja zakończona: ${result.created} nowych sugestii, ${result.skippedExisting} pominiętych (już istniały).`,
      );
      await this.loadData();
    } catch (error) {
      this.error.set(this.toUserMessage(error, 'Synchronizacja nie powiodła się.'));
    } finally {
      this.syncing.set(false);
    }
  }

  protected onResolved(event: SuggestionResolved): void {
    // Rozstrzygnięta sugestia znika z listy oczekujących bez ponownego pobierania.
    this.suggestions.update((current) => current.filter((s) => s.id !== event.suggestionId));
  }

  private async loadData(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [suggestions, cases] = await Promise.all([this.api.getSuggestions(), this.api.getCases()]);
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
