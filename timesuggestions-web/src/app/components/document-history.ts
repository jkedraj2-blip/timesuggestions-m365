import { Component, OnInit, inject, input, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { ApiService } from '../services/api.service';
import { toUserMessage } from '../services/user-message';
import {
  DocxDiffService,
  ParagraphChange,
  VersionUnavailableError,
  isDiffableDocument,
} from '../services/docx-diff';
import { DocumentActivityItem } from '../models/api.models';

/** Powyżej tylu znaków akapit jest przycinany z przyciskiem rozwinięcia. */
const PARAGRAPH_PREVIEW_CHARS = 300;

/**
 * Poziom 3 osi czasu: chronologia modyfikacji dokumentu (z dziennika DocumentActivity)
 * i diff dwóch sąsiednich wersji liczony w przeglądarce. Diff tylko dla .docx;
 * .doc (binarny) i arkusze Excela dostają chronologię z komunikatem zamiast parsowania.
 */
@Component({
  selector: 'app-document-history',
  imports: [DatePipe, DecimalPipe],
  template: `
    <div class="history">
      @if (error()) {
        <div class="error-box">{{ error() }}</div>
      }

      @if (loading()) {
        <p class="empty-state">Ładowanie historii wersji…</p>
      } @else if (activity().length === 0) {
        <!-- Brak wersji to stan normalny (retencja OneDrive), nie błąd. -->
        <p class="empty-state">Brak zapisanej historii wersji tego pliku.</p>
      } @else {
        @if (!diffable()) {
          <p class="text-muted note">Podgląd zmian dostępny tylko dla plików .docx.</p>
        }

        <ol class="versions">
          @for (version of activity(); track version.versionId; let index = $index) {
            @if (version.gapMinutesSincePrevious; as gap) {
              <li class="gap-row" [class.detected]="version.isDetectedGapRange">
                <span aria-hidden="true">⋮</span>
                przerwa {{ gap }} min
                @if (version.isDetectedGapRange) {
                  <span class="badge badge-accent">wykryta przerwa</span>
                }
              </li>
            }
            <li class="version-row">
              <span class="version-time">{{ version.occurredAt | date: 'HH:mm:ss' }}</span>
              <span class="version-date text-muted">{{ version.occurredAt | date: 'dd.MM' }}</span>
              <span class="version-size text-muted">{{ version.size / 1024 | number: '1.0-1' }} KB</span>
              @if (diffable() && index > 0) {
                <button
                  class="btn btn-ghost compare-btn"
                  (click)="compare(index)"
                  [disabled]="comparingIndex() !== null"
                >
                  Porównaj z poprzednią
                </button>
              }
            </li>
            @if (comparingIndex() === index) {
              <li class="diff-panel">
                <span>Porównuję wersje…</span>
                <button class="btn btn-ghost" (click)="cancelCompare()">Anuluj</button>
              </li>
            }
            @if (diffIndex() === index) {
              <li class="diff-panel">
                @if (diffResult(); as changes) {
                  @if (changes.length === 0) {
                    <p class="text-muted">Brak różnic tekstowych — zmiany dotyczyły formatowania.</p>
                  } @else {
                    <ul class="changes">
                      @for (change of changes; track $index) {
                        <!-- Prefiks +/−/~ obok koloru — zmiana nigdy nie jest komunikowana samym kolorem. -->
                        <li class="change change-{{ change.kind }}">
                          <span class="change-prefix" aria-hidden="true">{{ prefix(change) }}</span>
                          <span class="change-text">
                            @if (change.kind === 'changed' && change.previousText) {
                              <s class="text-muted">{{ display(change.previousText, $index * 2 + 1) }}</s>
                              →
                            }
                            {{ display(change.text, $index * 2) }}
                          </span>
                          @if (isTruncated(change, $index)) {
                            <button class="btn btn-ghost expand-btn" (click)="expand($index)">Pokaż całość</button>
                          }
                        </li>
                      }
                    </ul>
                  }
                }
              </li>
            }
          }
        </ol>
      }
    </div>
  `,
  styles: `
    .history { padding: var(--space-2) 0 var(--space-2) var(--space-4); }
    .note { margin: 0 0 var(--space-2); font-size: var(--font-size-sm); }
    .versions { list-style: none; margin: 0; padding: 0; font-size: var(--font-size-sm); }
    .version-row { display: flex; align-items: center; gap: var(--space-3); padding: var(--space-1) 0; }
    .version-time { font-variant-numeric: tabular-nums; font-weight: 600; }
    .version-date, .version-size { font-variant-numeric: tabular-nums; }
    .compare-btn, .expand-btn { padding: 0 var(--space-2); font-size: var(--font-size-sm); }
    .gap-row { display: flex; align-items: center; gap: var(--space-2); color: var(--text-muted); padding-left: var(--space-2); }
    .gap-row.detected { color: var(--accent); }
    .diff-panel {
      display: flex; align-items: center; gap: var(--space-3); flex-wrap: wrap;
      background: var(--surface-alt); border-radius: var(--radius-sm);
      padding: var(--space-2) var(--space-3); margin: var(--space-1) 0;
    }
    .changes { list-style: none; margin: 0; padding: 0; width: 100%; }
    .change { display: flex; gap: var(--space-2); padding: var(--space-1) 0; align-items: baseline; }
    .change-prefix { font-family: monospace; font-weight: 700; }
    /* Tokeny motywów zamiast gołych kolorów — czytelne również w ciemnym motywie. */
    .change-added { color: var(--success); }
    .change-removed { color: var(--warn-strong); }
    .change-changed { color: var(--accent); }
    .change-text { white-space: pre-wrap; overflow-wrap: anywhere; }
  `,
})
export class DocumentHistory implements OnInit {
  private api = inject(ApiService);
  private docxDiff = inject(DocxDiffService);

  /** Id pliku z Graph — po nim pobierana jest chronologia i treść wersji. */
  externalId = input.required<string>();

  /** Nazwa pliku — decyduje, czy diff jest dostępny (tylko .docx). */
  fileName = input<string | null>(null);

  protected activity = signal<DocumentActivityItem[]>([]);
  protected loading = signal(false);
  protected error = signal<string | null>(null);

  /** Indeks wersji porównywanej w tej chwili (spinner + Anuluj); null = nic nie trwa. */
  protected comparingIndex = signal<number | null>(null);
  protected diffIndex = signal<number | null>(null);
  protected diffResult = signal<ParagraphChange[] | null>(null);
  private expandedChanges = signal<ReadonlySet<number>>(new Set());
  private abortController: AbortController | null = null;

  protected diffable = signal(false);

  async ngOnInit(): Promise<void> {
    this.diffable.set(isDiffableDocument(this.fileName()));
    this.loading.set(true);
    try {
      this.activity.set(await this.api.getDocumentActivity(this.externalId()));
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać historii wersji.'));
    } finally {
      this.loading.set(false);
    }
  }

  /** Porównanie wersji index-1 → index. Każde kliknięcie = 2 pobrania pliku. */
  protected async compare(index: number): Promise<void> {
    const versions = this.activity();
    const older = versions[index - 1];
    const newer = versions[index];
    if (!older || !newer) {
      return;
    }

    this.error.set(null);
    this.diffIndex.set(null);
    this.diffResult.set(null);
    this.expandedChanges.set(new Set());
    this.comparingIndex.set(index);
    this.abortController = new AbortController();

    try {
      const changes = await this.docxDiff.compareVersions(
        this.externalId(),
        older.versionId,
        newer.versionId,
        this.abortController.signal,
      );
      this.diffResult.set(changes);
      this.diffIndex.set(index);
    } catch (error) {
      if (this.abortController?.signal.aborted) {
        return; // anulowane świadomie — bez komunikatu błędu
      }
      const message = error instanceof VersionUnavailableError
        ? error.message
        : toUserMessage(error, 'Nie udało się porównać wersji.');
      this.error.set(message);
    } finally {
      this.comparingIndex.set(null);
      this.abortController = null;
    }
  }

  protected cancelCompare(): void {
    this.abortController?.abort();
  }

  protected prefix(change: ParagraphChange): string {
    switch (change.kind) {
      case 'added':
        return '+';
      case 'removed':
        return '−';
      case 'changed':
        return '~';
    }
  }

  /** Długie akapity przycięte z rozwinięciem — klucz łączy indeks zmiany i wariant tekstu. */
  protected display(text: string, key: number): string {
    if (text.length <= PARAGRAPH_PREVIEW_CHARS || this.expandedChanges().has(key)) {
      return text;
    }
    return `${text.slice(0, PARAGRAPH_PREVIEW_CHARS)}…`;
  }

  protected isTruncated(change: ParagraphChange, index: number): boolean {
    const expanded = this.expandedChanges();
    return (change.text.length > PARAGRAPH_PREVIEW_CHARS && !expanded.has(index * 2))
      || ((change.previousText?.length ?? 0) > PARAGRAPH_PREVIEW_CHARS && !expanded.has(index * 2 + 1));
  }

  protected expand(index: number): void {
    this.expandedChanges.update((current) => new Set([...current, index * 2, index * 2 + 1]));
  }
}
