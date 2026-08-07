import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../services/api.service';
import { ToastService } from '../services/toast.service';
import { toUserMessage } from '../services/user-message';
import { CaseInfo, CaseWritePayload } from '../models/api.models';

/** Robocze pola formularza sprawy — słowa kluczowe jako tekst rozdzielany przecinkami. */
interface CaseFormDraft {
  name: string;
  caseNumber: string;
  clientName: string;
  keywordsText: string;
}

const EMPTY_DRAFT: CaseFormDraft = { name: '', caseNumber: '', clientName: '', keywordsText: '' };

/**
 * Widok spraw: tłumaczy zasadę dopasowania i pozwala zarządzać listą
 * (dodawanie, edycja, dezaktywacja — celowo bez twardego usuwania,
 * bo wpisy czasu wskazują na sprawy i historia musi przetrwać).
 */
@Component({
  selector: 'app-cases-page',
  imports: [FormsModule],
  template: `
    <p class="info-box">
      Aplikacja dopasowuje sugestie automatycznie: szuka w tytule spotkania lub nazwie pliku
      <strong>nazwy klienta</strong>, <strong>numeru sprawy</strong> albo
      <strong>słowa kluczowego</strong> z poniższej listy. Dopasowywane są pełne słowa
      („Alfa" nie pasuje do „Alfabet"); odmiany (np. „Kowalskiego") dodaj jako słowa
      kluczowe. Wielkość liter, polskie znaki i separatory (podkreślenia, myślniki)
      nie mają znaczenia.
    </p>

    @if (error()) {
      <div class="error-box">
        {{ error() }}
        <button class="btn btn-ghost" (click)="loadData()">Spróbuj ponownie</button>
      </div>
    }

    <div class="toolbar">
      @if (!formVisible()) {
        <button class="btn btn-primary" (click)="startCreate()">Dodaj sprawę</button>
      }
      <!-- Przełącznik w idiomie filtrów aplikacji (jak Źródło/Status na liście sugestii) —
           natywny checkbox wyłamywał się z systemu wizualnego. -->
      <button
        class="btn"
        [class.btn-ghost]="!includeInactive()"
        (click)="toggleInactive(!includeInactive())"
        [attr.aria-pressed]="includeInactive()"
      >
        {{ includeInactive() ? '✓ ' : '' }}pokaż nieaktywne
      </button>
    </div>

    @if (formVisible()) {
      <div class="card form-card">
        <h3>{{ editedCaseId() === null ? 'Nowa sprawa' : 'Edycja sprawy' }}</h3>
        <div class="form-grid">
          <label class="field">
            Nazwa sprawy
            <input type="text" [(ngModel)]="draft.name" />
          </label>
          <label class="field">
            Numer sprawy
            <input type="text" [(ngModel)]="draft.caseNumber" placeholder="np. K-2026-002" />
          </label>
          <label class="field">
            Klient
            <input type="text" [(ngModel)]="draft.clientName" />
          </label>
          <label class="field">
            Słowa kluczowe (po przecinku)
            <input type="text" [(ngModel)]="draft.keywordsText" placeholder="np. Kowalski, K-2026" />
          </label>
        </div>
        <div class="actions">
          <button class="btn btn-primary" (click)="save()" [disabled]="busy()">Zapisz</button>
          <button class="btn" (click)="cancelForm()" [disabled]="busy()">Anuluj</button>
        </div>
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
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (caseInfo of cases(); track caseInfo.id) {
            <tr [class.inactive]="!caseInfo.isActive">
              <td>
                {{ caseInfo.name }}
                @if (!caseInfo.isActive) {
                  <span class="badge badge-neutral">nieaktywna</span>
                }
              </td>
              <td><code>{{ caseInfo.caseNumber }}</code></td>
              <td>{{ caseInfo.clientName }}</td>
              <td>
                @for (keyword of caseInfo.keywords; track keyword) {
                  <span class="badge badge-accent">{{ keyword }}</span>
                } @empty {
                  <span class="text-muted">—</span>
                }
              </td>
              <td class="row-actions">
                <button class="btn btn-ghost" (click)="startEdit(caseInfo)" [disabled]="busy()">Edytuj</button>
                @if (caseInfo.isActive) {
                  <button class="btn btn-ghost" (click)="setActive(caseInfo, false)" [disabled]="busy()">Dezaktywuj</button>
                } @else {
                  <button class="btn btn-ghost" (click)="setActive(caseInfo, true)" [disabled]="busy()">Aktywuj</button>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    .toolbar { display: flex; align-items: center; gap: var(--space-3); margin-bottom: var(--space-4); flex-wrap: wrap; }
    .badge + .badge { margin-left: var(--space-1); }
    code { font-size: var(--font-size-sm); }
    .form-card { margin-bottom: var(--space-4); display: flex; flex-direction: column; gap: var(--space-3); }
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: var(--space-3); }
    .actions { display: flex; gap: var(--space-2); }
    .row-actions { white-space: nowrap; text-align: right; }
    tr.inactive td { opacity: 0.6; }
    td .badge { margin-left: var(--space-2); }
  `,
})
export class CasesPage implements OnInit {
  private api = inject(ApiService);
  private toasts = inject(ToastService);

  protected cases = signal<CaseInfo[]>([]);
  protected loading = signal(false);
  protected busy = signal(false);
  protected error = signal<string | null>(null);
  protected includeInactive = signal(false);
  protected formVisible = signal(false);
  protected editedCaseId = signal<number | null>(null);

  protected draft: CaseFormDraft = { ...EMPTY_DRAFT };

  async ngOnInit(): Promise<void> {
    await this.loadData();
  }

  protected async loadData(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.cases.set(await this.api.getCases(this.includeInactive()));
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać listy spraw.'));
    } finally {
      this.loading.set(false);
    }
  }

  protected toggleInactive(value: boolean): void {
    this.includeInactive.set(value);
    void this.loadData();
  }

  protected startCreate(): void {
    this.editedCaseId.set(null);
    this.draft = { ...EMPTY_DRAFT };
    this.formVisible.set(true);
  }

  protected startEdit(caseInfo: CaseInfo): void {
    this.editedCaseId.set(caseInfo.id);
    this.draft = {
      name: caseInfo.name,
      caseNumber: caseInfo.caseNumber,
      clientName: caseInfo.clientName,
      keywordsText: caseInfo.keywords.join(', '),
    };
    this.formVisible.set(true);
  }

  protected cancelForm(): void {
    this.formVisible.set(false);
    this.editedCaseId.set(null);
  }

  protected async save(): Promise<void> {
    const payload = this.toPayload();
    if (!payload.name || !payload.caseNumber || !payload.clientName) {
      this.error.set('Nazwa, numer sprawy i klient są wymagane.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      const editedId = this.editedCaseId();
      if (editedId === null) {
        const created = await this.api.createCase(payload);
        this.toasts.show(`Dodano sprawę „${created.name}".`);
      } else {
        const updated = await this.api.updateCase(editedId, payload);
        this.toasts.show(`Zapisano zmiany w sprawie „${updated.name}".`);
      }
      this.cancelForm();
      await this.loadData();
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się zapisać sprawy.'));
    } finally {
      this.busy.set(false);
    }
  }

  protected async setActive(caseInfo: CaseInfo, isActive: boolean): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.api.setCaseActive(caseInfo.id, isActive);
      this.toasts.show(
        isActive
          ? `Sprawa „${caseInfo.name}" jest znów aktywna.`
          : `Zdezaktywowano sprawę „${caseInfo.name}" — nie będzie brana pod uwagę przy dopasowaniu.`,
      );
      await this.loadData();
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się zmienić statusu sprawy.'));
    } finally {
      this.busy.set(false);
    }
  }

  private toPayload(): CaseWritePayload {
    return {
      name: this.draft.name.trim(),
      caseNumber: this.draft.caseNumber.trim(),
      clientName: this.draft.clientName.trim(),
      keywords: this.draft.keywordsText
        .split(',')
        .map((keyword) => keyword.trim())
        .filter((keyword) => keyword.length > 0),
    };
  }
}
