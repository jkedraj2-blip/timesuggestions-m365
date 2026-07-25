import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../services/api.service';
import { ApprovePayload, CaseInfo, Suggestion } from '../models/api.models';

/** Zdarzenie dla rodzica: sugestia rozstrzygnięta (zatwierdzona lub odrzucona) — do zdjęcia z listy. */
export interface SuggestionResolved {
  suggestionId: number;
  action: 'approved' | 'rejected';
}

@Component({
  selector: 'app-suggestion-card',
  imports: [DatePipe, FormsModule],
  template: `
    <div class="card" [class.needs-review]="needsReview()">
      <div class="card-header">
        <span class="source-icon" [title]="sourceLabel()">{{ sourceIcon() }}</span>
        <strong class="title">{{ suggestion().title }}</strong>
        @if (needsReview()) {
          <span class="badge">sprawdź to</span>
        }
      </div>

      <div class="card-details">
        <span>{{ suggestion().startedAt | date: 'dd.MM.yyyy HH:mm' }}</span>
        <span>{{ durationMinutes() }} min</span>
        @if (suggestion().caseName) {
          <span class="case-name">{{ suggestion().caseName }}</span>
        } @else if (suggestion().isAmbiguous) {
          <span class="case-missing">kilka pasujących spraw — wybierz przy edycji</span>
        } @else {
          <span class="case-missing">brak dopasowanej sprawy</span>
        }
      </div>

      @if (editing()) {
        <div class="edit-form">
          <label>
            Sprawa
            <select [(ngModel)]="selectedCaseId">
              <option [ngValue]="null">— wybierz sprawę —</option>
              @for (caseInfo of cases(); track caseInfo.id) {
                <option [ngValue]="caseInfo.id">{{ caseInfo.name }} ({{ caseInfo.caseNumber }})</option>
              }
            </select>
          </label>
          <label>
            Czas trwania (min)
            <input type="number" min="1" [(ngModel)]="durationDraft" />
          </label>
          <label>
            Opis czynności
            <input type="text" [(ngModel)]="descriptionDraft" />
          </label>
          <div class="actions">
            <button class="primary" (click)="approve()" [disabled]="busy()">Zapisz i zatwierdź</button>
            <button (click)="editing.set(false)" [disabled]="busy()">Anuluj</button>
          </div>
        </div>
      } @else {
        <div class="description">{{ descriptionDraft }}</div>
        <div class="actions">
          <button class="primary" (click)="approve()" [disabled]="busy()">Zatwierdź</button>
          <button (click)="editing.set(true)" [disabled]="busy()">Edytuj</button>
          <button class="danger" (click)="reject()" [disabled]="busy()">Odrzuć</button>
        </div>
      }

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }
    </div>
  `,
  styles: `
    .card {
      border: 1px solid #ccc;
      border-radius: 8px;
      padding: 12px 16px;
      margin: 8px 0;
      max-width: 640px;
      background: #fff;
    }
    /* Żółte wyróżnienie: pozycje wymagające decyzji użytkownika (brak/niejednoznaczne dopasowanie). */
    .card.needs-review { border: 2px solid #e0a800; background: #fffdf2; }
    .card-header { display: flex; align-items: center; gap: 8px; }
    .source-icon { font-size: 1.2em; }
    .title { flex: 1; }
    .badge {
      background: #e0a800;
      color: #fff;
      border-radius: 4px;
      padding: 2px 8px;
      font-size: 0.8em;
      white-space: nowrap;
    }
    .card-details { display: flex; gap: 16px; margin: 6px 0; color: #444; flex-wrap: wrap; }
    .case-name { font-weight: 600; color: #1a6b1a; }
    .case-missing { color: #a06000; font-style: italic; }
    .description { margin: 6px 0; color: #333; }
    .edit-form { display: flex; flex-direction: column; gap: 8px; margin: 8px 0; }
    .edit-form label { display: flex; flex-direction: column; gap: 2px; font-size: 0.9em; color: #555; }
    .edit-form select, .edit-form input { padding: 6px; font-size: 1em; }
    .actions { display: flex; gap: 8px; margin-top: 8px; }
    button { padding: 6px 14px; border-radius: 6px; border: 1px solid #999; background: #f5f5f5; cursor: pointer; }
    button:hover:not(:disabled) { background: #e8e8e8; }
    button:disabled { opacity: 0.6; cursor: default; }
    button.primary { background: #1863c6; border-color: #1863c6; color: #fff; }
    button.primary:hover:not(:disabled) { background: #124e9e; }
    button.danger { border-color: #a32d2d; color: #a32d2d; }
    .error { color: #a32d2d; margin: 6px 0 0; }
  `,
})
export class SuggestionCard implements OnInit {
  private api = inject(ApiService);

  suggestion = input.required<Suggestion>();
  cases = input.required<CaseInfo[]>();
  resolved = output<SuggestionResolved>();

  protected editing = signal(false);
  protected busy = signal(false);
  protected error = signal<string | null>(null);

  // Robocze wartości formularza — inicjalizowane z sugestii przy pierwszym renderze.
  protected selectedCaseId: number | null = null;
  protected durationDraft = 0;
  protected descriptionDraft = '';

  private initialized = false;

  protected needsReview = computed(
    () => this.suggestion().caseId === null || this.suggestion().isAmbiguous,
  );

  protected sourceIcon = computed(() => (this.suggestion().source === 'calendar' ? '📅' : '📄'));

  protected sourceLabel = computed(() =>
    this.suggestion().source === 'calendar' ? 'spotkanie z kalendarza' : 'dokument z OneDrive',
  );

  protected durationMinutes = computed(() => this.suggestion().durationMinutes);

  ngOnInit(): void {
    if (this.initialized) return;
    const suggestion = this.suggestion();
    this.selectedCaseId = suggestion.caseId;
    this.durationDraft = suggestion.durationMinutes;
    this.descriptionDraft = suggestion.proposedDescription;
    this.initialized = true;
  }

  protected async approve(): Promise<void> {
    if (this.selectedCaseId === null) {
      // Przy braku lub niejednoznacznym dopasowaniu użytkownik musi wskazać sprawę.
      this.error.set('Wybierz sprawę przed zatwierdzeniem (przycisk Edytuj).');
      this.editing.set(true);
      return;
    }
    if (this.durationDraft <= 0) {
      this.error.set('Czas trwania musi być większy od zera.');
      return;
    }

    const payload: ApprovePayload = {
      caseId: this.selectedCaseId,
      durationMinutes: this.durationDraft,
      description: this.descriptionDraft.trim() || this.suggestion().title,
    };

    await this.run(async () => {
      await this.api.approve(this.suggestion().id, payload);
      this.resolved.emit({ suggestionId: this.suggestion().id, action: 'approved' });
    });
  }

  protected async reject(): Promise<void> {
    await this.run(async () => {
      await this.api.reject(this.suggestion().id);
      this.resolved.emit({ suggestionId: this.suggestion().id, action: 'rejected' });
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.error.set(error instanceof Error ? error.message : 'Nieznany błąd.');
    } finally {
      this.busy.set(false);
    }
  }
}
