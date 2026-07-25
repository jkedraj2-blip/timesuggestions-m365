import { Component, OnInit, inject, signal } from '@angular/core';
import { AuthService } from './services/auth.service';
import { SuggestionList } from './components/suggestion-list';

@Component({
  selector: 'app-root',
  imports: [SuggestionList],
  template: `
    <header>
      <h1>Sugestie wpisów czasu pracy</h1>
      @if (user()) {
        <div class="session">
          <span>Zalogowano jako {{ user() }}</span>
          <button (click)="auth.logout()">Wyloguj</button>
        </div>
      }
    </header>

    @if (user()) {
      <app-suggestion-list />
    } @else {
      <p>Zaloguj się kontem Microsoft, aby pobrać spotkania i dokumenty z ostatnich 7 dni.</p>
      <button class="primary" (click)="auth.login()">Zaloguj przez Microsoft</button>
    }
  `,
  styles: `
    :host { display: block; max-width: 720px; margin: 0 auto; padding: 16px; font-family: system-ui, sans-serif; }
    header { display: flex; align-items: baseline; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
    h1 { font-size: 1.5em; }
    .session { display: flex; align-items: center; gap: 12px; color: #555; }
    button { padding: 6px 14px; border-radius: 6px; border: 1px solid #999; background: #f5f5f5; cursor: pointer; }
    button:hover { background: #e8e8e8; }
    button.primary { background: #1863c6; border-color: #1863c6; color: #fff; }
    button.primary:hover { background: #124e9e; }
  `,
})
export class App implements OnInit {
  protected auth = inject(AuthService);

  protected user = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.auth.init();
    this.user.set(this.auth.account?.username ?? null);
  }
}
