import { Component, OnInit, inject, signal } from '@angular/core';
import { ApiService } from '../services/api.service';
import { CaseInfo } from '../models/api.models';

/**
 * Widok spraw — tłumaczy użytkownikowi, jak działa dopasowanie.
 * Bez tej listy komunikat "brak dopasowanej sprawy" jest niezrozumiały.
 */
@Component({
  selector: 'app-cases-page',
  imports: [],
  template: `
    <p class="info-box">
      Aplikacja dopasowuje sugestie automatycznie: szuka w tytule spotkania lub nazwie pliku
      <strong>nazwy klienta</strong>, <strong>numeru sprawy</strong> albo
      <strong>słowa kluczowego</strong> z poniższej listy. Wielkość liter, polskie znaki
      i separatory (podkreślenia, myślniki) nie mają znaczenia.
    </p>

    @if (error()) {
      <div class="error-box">
        {{ error() }}
        <button class="btn btn-ghost" (click)="loadData()">Spróbuj ponownie</button>
      </div>
    }

    @if (loading()) {
      <p class="empty-state">Ładowanie spraw…</p>
    } @else if (cases().length > 0) {
      <table class="table">
        <thead>
          <tr>
            <th>Sprawa</th>
            <th>Numer sprawy</th>
            <th>Klient</th>
            <th>Słowa kluczowe</th>
          </tr>
        </thead>
        <tbody>
          @for (caseInfo of cases(); track caseInfo.id) {
            <tr>
              <td>{{ caseInfo.name }}</td>
              <td><code>{{ caseInfo.caseNumber }}</code></td>
              <td>{{ caseInfo.clientName }}</td>
              <td>
                @for (keyword of caseInfo.keywords; track keyword) {
                  <span class="badge badge-accent">{{ keyword }}</span>
                } @empty {
                  <span class="text-muted">—</span>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    .badge + .badge { margin-left: var(--space-1); }
    code { font-size: var(--font-size-sm); }
  `,
})
export class CasesPage implements OnInit {
  private api = inject(ApiService);

  protected cases = signal<CaseInfo[]>([]);
  protected loading = signal(false);
  protected error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    await this.loadData();
  }

  protected async loadData(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.cases.set(await this.api.getCases());
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'Nie udało się pobrać listy spraw.');
    } finally {
      this.loading.set(false);
    }
  }
}
