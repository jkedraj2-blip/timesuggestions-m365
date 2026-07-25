import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './services/auth.service';
import { SummaryStore } from './services/summary-store';
import { DurationPipe } from './pipes/duration.pipe';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, DatePipe, DurationPipe],
  template: `
    <header class="app-header">
      <div>
        <h1>TimeSuggestions</h1>
        <p class="text-muted subtitle">Automatyczne sugestie wpisów czasu pracy z Microsoft 365</p>
      </div>
      @if (user()) {
        <div class="session">
          <span class="text-muted">{{ user() }}</span>
          <button class="btn" (click)="auth.logout()">Wyloguj</button>
        </div>
      }
    </header>

    @if (user()) {
      @if (summaryStore.summary(); as summary) {
        <div class="tiles">
          <div class="tile">
            <div class="tile-value">{{ summary.pendingCount }}</div>
            <div class="tile-label">oczekujące sugestie</div>
          </div>
          <div class="tile">
            <div class="tile-value">{{ summary.approvedCount }}</div>
            <div class="tile-label">zapisane wpisy</div>
          </div>
          <div class="tile">
            <div class="tile-value">{{ summary.totalLoggedMinutes | duration }}</div>
            <div class="tile-label">łączny zalogowany czas</div>
          </div>
          <div class="tile">
            <div class="tile-value">
              @if (summary.lastSyncAt) {
                {{ summary.lastSyncAt | date: 'dd.MM HH:mm' }}
              } @else {
                —
              }
            </div>
            <div class="tile-label">ostatnia synchronizacja</div>
          </div>
        </div>
      }

      <nav class="tabs">
        <a class="tab" routerLink="/sugestie" routerLinkActive="active">Sugestie</a>
        <a class="tab" routerLink="/wpisy" routerLinkActive="active">Wpisy czasu</a>
        <a class="tab" routerLink="/sprawy" routerLinkActive="active">Sprawy</a>
      </nav>

      <router-outlet />
    } @else {
      <div class="card login-card">
        <h2>Zaloguj się, aby zacząć</h2>
        <p class="text-muted">
          Aplikacja pobierze Twoje spotkania z kalendarza Outlook i dokumenty Word/Excel
          z OneDrive z ostatnich 7 dni, a następnie zaproponuje gotowe wpisy czasu pracy.
          Wystarczy jedno kliknięcie „Zatwierdź".
        </p>
        <button class="btn btn-primary" (click)="auth.login()">Zaloguj przez Microsoft</button>
      </div>
    }
  `,
  styles: `
    :host { display: block; max-width: 960px; margin: 0 auto; padding: var(--space-5) var(--space-4); }
    .app-header { display: flex; align-items: flex-start; justify-content: space-between; gap: var(--space-4); margin-bottom: var(--space-4); flex-wrap: wrap; }
    h1 { font-size: var(--font-size-xl); }
    .subtitle { margin: var(--space-1) 0 0; font-size: var(--font-size-sm); }
    .session { display: flex; align-items: center; gap: var(--space-3); }
    .login-card { max-width: 480px; margin: var(--space-6) auto; text-align: center; display: flex; flex-direction: column; gap: var(--space-3); align-items: center; }
  `,
})
export class App implements OnInit {
  protected auth = inject(AuthService);
  protected summaryStore = inject(SummaryStore);

  protected user = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.auth.init();
    this.user.set(this.auth.account?.username ?? null);
    if (this.user()) {
      await this.summaryStore.refresh();
    }
  }
}
