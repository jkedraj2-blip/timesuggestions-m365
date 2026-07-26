import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './services/auth.service';
import { SummaryStore } from './services/summary-store';
import { ToastService } from './services/toast.service';
import { ThemeService } from './services/theme.service';
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
      <div class="header-controls">
        <!-- Segmented control zamiast selecta: wybór widoczny od razu, bez etykiety formularzowej. -->
        <div class="theme-switch" role="group" aria-label="Motyw kolorystyczny">
          <button
            class="theme-option"
            [class.active]="themeService.theme() === 'light'"
            (click)="themeService.setTheme('light')"
            title="Motyw jasny"
            aria-label="Motyw jasny"
          >☀️</button>
          <button
            class="theme-option"
            [class.active]="themeService.theme() === 'blue'"
            (click)="themeService.setTheme('blue')"
            title="Motyw niebieski"
            aria-label="Motyw niebieski"
          >🔵</button>
          <button
            class="theme-option"
            [class.active]="themeService.theme() === 'dark'"
            (click)="themeService.setTheme('dark')"
            title="Motyw ciemny"
            aria-label="Motyw ciemny"
          >🌙</button>
        </div>

        @if (user()) {
          <!-- Sesja jako jedna pigułka: kto jest zalogowany + wyjście to jedno pojęcie. -->
          <div class="session-pill">
            <span class="text-muted">{{ user() }}</span>
            <span class="pill-divider" aria-hidden="true"></span>
            <button class="btn btn-ghost logout-btn" (click)="auth.logout()">Wyloguj</button>
          </div>
        }
      </div>
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
        <a class="tab" routerLink="/suggestions" routerLinkActive="active">Sugestie</a>
        <a class="tab" routerLink="/time-entries" routerLinkActive="active">Wpisy czasu</a>
        <a class="tab" routerLink="/cases" routerLinkActive="active">Sprawy</a>
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

    <div class="toasts">
      @for (toast of toastService.toasts(); track toast.id) {
        <div class="toast" [class.toast-error]="toast.kind === 'error'">
          <span>{{ toast.message }}</span>
          @if (toast.undo) {
            <button class="btn btn-ghost" (click)="toastService.runUndo(toast)">Cofnij</button>
          }
          <button class="btn btn-ghost" (click)="toastService.dismiss(toast.id)" aria-label="Zamknij">✕</button>
        </div>
      }
    </div>
  `,
  styles: `
    :host { display: block; max-width: 960px; margin: 0 auto; padding: var(--space-5) var(--space-4); }
    /* Jedna wspólna oś: tytuł po lewej, obie grupy kontrolek po prawej, wszystko wycentrowane
       w pionie; cienka linia domyka pasek przed kafelkami. */
    .app-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-4);
      flex-wrap: wrap;
      padding-bottom: var(--space-4);
      margin-bottom: var(--space-4);
      border-bottom: 1px solid var(--border);
    }
    h1 { font-size: var(--font-size-xl); }
    .subtitle { margin: var(--space-1) 0 0; font-size: var(--font-size-sm); }
    .header-controls { display: flex; align-items: center; gap: var(--space-4); flex-wrap: wrap; }

    .theme-switch {
      display: inline-flex;
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      overflow: hidden;
      background: var(--surface);
    }
    .theme-option {
      border: none;
      background: transparent;
      padding: var(--space-1) var(--space-3);
      cursor: pointer;
      font-size: var(--font-size-base);
      line-height: 1.6;
    }
    .theme-option:hover { background: var(--surface-alt); }
    .theme-option.active { background: var(--accent-soft); box-shadow: inset 0 -2px 0 var(--accent); }

    .session-pill {
      display: inline-flex;
      align-items: center;
      gap: var(--space-3);
      border: 1px solid var(--border);
      border-radius: 999px;
      background: var(--surface);
      padding: var(--space-1) var(--space-2) var(--space-1) var(--space-4);
      font-size: var(--font-size-sm);
    }
    .pill-divider { width: 1px; height: 1.1em; background: var(--border); }
    .logout-btn { padding: var(--space-1) var(--space-3); font-size: var(--font-size-sm); }
    .login-card { max-width: 480px; margin: var(--space-6) auto; text-align: center; display: flex; flex-direction: column; gap: var(--space-3); align-items: center; }
  `,
})
export class App implements OnInit {
  protected auth = inject(AuthService);
  protected summaryStore = inject(SummaryStore);
  protected toastService = inject(ToastService);
  protected themeService = inject(ThemeService);

  protected user = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.auth.init();
    this.user.set(this.auth.account?.username ?? null);
    if (this.user()) {
      await this.summaryStore.refresh();
    }
  }

}
