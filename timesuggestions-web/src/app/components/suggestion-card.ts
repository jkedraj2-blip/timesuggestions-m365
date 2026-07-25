import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../services/api.service';
import { ApprovePayload, CaseInfo, Suggestion, TimeEntry } from '../models/api.models';
import { DurationPipe } from '../pipes/duration.pipe';

/** Zdarzenie dla rodzica: sugestia rozstrzygnięta — do zdjęcia z listy i pokazania toastu. */
export interface SuggestionResolved {
  suggestion: Suggestion;
  action: 'approved' | 'rejected' | 'restored';
  /** Wpis utworzony przy zatwierdzeniu — potrzebny do akcji "Cofnij". */
  createdEntry?: TimeEntry;
}

@Component({
  selector: 'app-suggestion-card',
  imports: [DatePipe, FormsModule, DurationPipe],
  template: `
    <div class="card" [class.card-review]="needsReview()">
      <div class="card-header">
        <span class="badge badge-neutral">{{ sourceIcon() }} {{ sourceLabel() }}</span>
        <strong class="title">{{ suggestion().title }}</strong>
        @if (needsReview()) {
          <span class="badge badge-warn">sprawdź to</span>
        }
      </div>

      <div class="card-details text-muted">
        <span>{{ suggestion().startedAt | date: 'dd.MM.yyyy HH:mm' }}</span>
        <span>{{ suggestion().durationMinutes | duration }}</span>
        @if (suggestion().source === 'document') {
          <!-- Tylko dokumenty: czas spotkań jest zmierzony, czas dokumentów — założony.
               Neutralna plakietka (nie amber) — to informacja, nie ostrzeżenie. -->
          <span
            class="badge badge-neutral"
            title="Microsoft Graph nie podaje, jak długo trwała edycja — to wartość domyślna z konfiguracji. Popraw ją, jeśli pracowałeś dłużej."
          >czas domyślny</span>
        }
        @if (suggestion().caseName) {
          <span class="badge badge-success">{{ suggestion().caseName }}</span>
        }
      </div>

      @if (reviewReason()) {
        <p class="review-reason text-warn">{{ reviewReason() }}</p>
      }

      @if (isRejected()) {
        <div class="actions">
          <button class="btn" (click)="restore()" [disabled]="busy()">Przywróć</button>
        </div>
      } @else if (editing()) {
        <div class="edit-form">
          <label class="field">
            Sprawa
            <select [(ngModel)]="selectedCaseId">
              <option [ngValue]="null">— wybierz sprawę —</option>
              @for (caseInfo of cases(); track caseInfo.id) {
                <option [ngValue]="caseInfo.id">{{ caseInfo.name }} ({{ caseInfo.caseNumber }})</option>
              }
            </select>
          </label>
          <label class="field">
            Czas trwania (min)
            <input type="number" min="1" [(ngModel)]="durationDraft" />
          </label>
          <label class="field">
            Opis czynności
            <input type="text" [(ngModel)]="descriptionDraft" />
          </label>
          <div class="actions">
            <button class="btn btn-primary" (click)="approve()" [disabled]="busy()">Zapisz i zatwierdź</button>
            <button class="btn" (click)="editing.set(false)" [disabled]="busy()">Anuluj</button>
          </div>
        </div>
      } @else {
        <p class="description">{{ descriptionDraft }}</p>
        <div class="actions">
          <button class="btn btn-primary" (click)="approve()" [disabled]="busy()">Zatwierdź</button>
          <button class="btn" (click)="editing.set(true)" [disabled]="busy()">Edytuj</button>
          <button class="btn btn-danger" (click)="reject()" [disabled]="busy()">Odrzuć</button>
        </div>
      }

      @if (error()) {
        <p class="error-box">{{ error() }}</p>
      }
    </div>
  `,
  styles: `
    .card { margin: var(--space-2) 0; }
    .card-header { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; }
    .title { flex: 1; font-size: var(--font-size-lg); }
    .card-details { display: flex; align-items: center; gap: var(--space-4); margin: var(--space-2) 0; flex-wrap: wrap; }
    .review-reason { margin: var(--space-1) 0; font-size: var(--font-size-sm); }
    .description { margin: var(--space-2) 0; }
    .edit-form { display: flex; flex-direction: column; gap: var(--space-2); margin: var(--space-3) 0; }
    .actions { display: flex; gap: var(--space-2); justify-content: flex-end; margin-top: var(--space-3); }
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
    () => this.suggestion().status === 'pending'
      && (this.suggestion().caseId === null || this.suggestion().isAmbiguous),
  );

  protected isRejected = computed(() => this.suggestion().status === 'rejected');

  protected sourceIcon = computed(() => (this.suggestion().source === 'calendar' ? '📅' : '📄'));

  protected sourceLabel = computed(() =>
    this.suggestion().source === 'calendar' ? 'Spotkanie' : 'Dokument',
  );

  /** Konkret zamiast samego "sprawdź to" — user wie, czego brakuje albo co jest niejasne. */
  protected reviewReason = computed(() => {
    if (!this.needsReview()) {
      return null;
    }
    const suggestion = this.suggestion();
    if (suggestion.isAmbiguous) {
      return `Pasuje do ${suggestion.matchCandidates.length} spraw: ${suggestion.matchCandidates.join(', ')} — wybierz właściwą przy edycji.`;
    }
    return 'Nie znaleziono nazwy klienta ani numeru sprawy w tytule — wskaż sprawę przy edycji.';
  });

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
      this.error.set('Wybierz sprawę przed zatwierdzeniem.');
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
      const createdEntry = await this.api.approve(this.suggestion().id, payload);
      this.resolved.emit({ suggestion: this.suggestion(), action: 'approved', createdEntry });
    });
  }

  protected async reject(): Promise<void> {
    await this.run(async () => {
      await this.api.reject(this.suggestion().id);
      this.resolved.emit({ suggestion: this.suggestion(), action: 'rejected' });
    });
  }

  protected async restore(): Promise<void> {
    await this.run(async () => {
      await this.api.restore(this.suggestion().id);
      this.resolved.emit({ suggestion: this.suggestion(), action: 'restored' });
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
