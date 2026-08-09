import { Component, OnInit, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from '../services/api.service';
import { DataRefreshService } from '../services/data-refresh.service';
import { SummaryStore } from '../services/summary-store';
import { ToastService } from '../services/toast.service';
import { toUserMessage } from '../services/user-message';
import { TimeEntriesResponse, TimeEntry } from '../models/api.models';
import { DurationPipe } from '../pipes/duration.pipe';

/**
 * Widok zapisanych wpisów czasu — dowód działania aplikacji.
 * Zatwierdzona sugestia nie znika "w nicość", tylko ląduje tutaj.
 */
@Component({
  selector: 'app-time-entries-page',
  imports: [DatePipe, DurationPipe],
  template: `
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
        <div class="empty-state">
          <p><strong>Brak zapisanych wpisów czasu.</strong></p>
          <p>Zatwierdź sugestię w zakładce „Sugestie", a rozliczalny wpis pojawi się tutaj.</p>
        </div>
      } @else {
        <p class="total text-muted">
          Łącznie zarejestrowane: <strong class="text-success">{{ response.totalMinutes | duration }}</strong>
        </p>

        @for (day of response.days; track day.date) {
          <section class="day">
            <header class="day-header">
              <h3>{{ day.date | date: 'EEEE, d MMMM y' }}</h3>
              <span class="badge badge-accent">{{ day.totalMinutes | duration }}</span>
            </header>

            @for (entry of day.entries; track entry.id) {
              <div class="card entry">
                <div class="entry-main">
                  <div class="entry-header">
                    <span class="badge badge-neutral">{{ entry.source === 'calendar' ? '📅 Spotkanie' : '📄 Dokument' }}</span>
                    <strong>{{ entry.caseName }}</strong>
                    <span class="badge badge-success">{{ entry.durationMinutes | duration }}</span>
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
                <button class="btn btn-danger" (click)="deleteEntry(entry)" [disabled]="busyId() === entry.id">
                  Usuń
                </button>
              </div>
            }
          </section>
        }
      }
    }
  `,
  styles: `
    .total { margin: 0 0 var(--space-4); font-size: var(--font-size-lg); }
    .day { margin-bottom: var(--space-5); }
    .day-header { display: flex; align-items: center; gap: var(--space-3); margin-bottom: var(--space-2); }
    .day-header h3 { font-size: var(--font-size-base); text-transform: capitalize; }
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

  async ngOnInit(): Promise<void> {
    await this.loadData();
  }

  protected async loadData(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.data.set(await this.api.getTimeEntries());
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać wpisów.'));
    } finally {
      this.loading.set(false);
    }
  }

  /** Usunięcie wpisu przywraca sugestię źródłową na listę oczekujących (pomyłka jest odwracalna). */
  protected async deleteEntry(entry: TimeEntry): Promise<void> {
    this.busyId.set(entry.id);
    this.error.set(null);
    try {
      await this.api.deleteTimeEntry(entry.id);
      await this.loadData();
      await this.summaryStore.refresh();
      this.toasts.show('Usunięto wpis — sugestia wróciła na listę oczekujących.');
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się usunąć wpisu.'));
    } finally {
      this.busyId.set(null);
    }
  }
}
