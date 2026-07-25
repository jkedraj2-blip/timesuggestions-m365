import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { AuthService } from './auth.service';
import { GraphService, GraphEvent } from './graph.service';

@Component({
  selector: 'app-root',
  imports: [DatePipe],
  template: `
    <h1>Sugestie wpisów czasu pracy</h1>

    @if (user()) {
      <p>Zalogowano jako {{ user() }}</p>
      <button (click)="load()" [disabled]="loading()">
        {{ loading() ? 'Pobieram…' : 'Pobierz spotkania z 7 dni' }}
      </button>
      <button (click)="auth.logout()">Wyloguj</button>
    } @else {
      <button (click)="auth.login()">Zaloguj przez Microsoft</button>
    }

    @if (error()) {
      <pre class="error">{{ error() }}</pre>
    }

    @for (event of events(); track event.id) {
      <div class="card">
        <strong>{{ event.subject || '(bez tytułu)' }}</strong>
        <div>{{ event.start.dateTime | date: 'dd.MM.yyyy HH:mm' }}</div>
        <div>{{ durationMinutes(event) }} min</div>
        <div>całodniowe: {{ event.isAllDay }} · widoczność: {{ event.sensitivity }}</div>
      </div>
    } @empty {
      @if (loaded()) {
        <p>Brak wydarzeń w tym okresie.</p>
      }
    }
  `,
  styles: `
    .card { border: 1px solid #ccc; border-radius: 8px; padding: 12px; margin: 8px 0; max-width: 520px; }
    .error { color: #a32d2d; white-space: pre-wrap; }
  `,
})
export class App implements OnInit {
  protected auth = inject(AuthService);
  private graph = inject(GraphService);

  protected user = signal<string | null>(null);
  protected events = signal<GraphEvent[]>([]);
  protected error = signal<string | null>(null);
  protected loading = signal(false);
  protected loaded = signal(false);

  async ngOnInit(): Promise<void> {
    await this.auth.init();
    this.user.set(this.auth.account?.username ?? null);
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.events.set(await this.graph.getEventsLastDays(7));
      this.loaded.set(true);
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : String(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected durationMinutes(event: GraphEvent): number {
    const start = new Date(event.start.dateTime).getTime();
    const end = new Date(event.end.dateTime).getTime();
    return Math.round((end - start) / 60000);
  }
}
