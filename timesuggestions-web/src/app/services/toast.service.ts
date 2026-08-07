import { Injectable, signal } from '@angular/core';

export interface Toast {
  id: number;
  message: string;
  kind: 'success' | 'error';
  /** Opcjonalna akcja "Cofnij" — decyzja użytkownika nie może być nieodwracalna. */
  undo?: () => Promise<void> | void;
}

const TOAST_LIFETIME_MS = 8000;

/**
 * Powiadomienia po akcjach. Zamiast cichego zniknięcia karty użytkownik dostaje
 * potwierdzenie "co się właśnie stało" i szansę na wycofanie pomyłki.
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<Toast[]>([]);

  private nextId = 1;

  show(message: string, options?: { kind?: Toast['kind']; undo?: Toast['undo'] }): void {
    const toast: Toast = {
      id: this.nextId++,
      message,
      kind: options?.kind ?? 'success',
      undo: options?.undo,
    };
    this.toasts.update((current) => [...current, toast]);
    setTimeout(() => this.dismiss(toast.id), TOAST_LIFETIME_MS);
  }

  dismiss(id: number): void {
    this.toasts.update((current) => current.filter((toast) => toast.id !== id));
  }

  async runUndo(toast: Toast): Promise<void> {
    this.dismiss(toast.id);
    try {
      await toast.undo?.();
    } catch (error) {
      // Błąd cofnięcia nie może zniknąć bez śladu — pokazujemy go tam, gdzie była akcja.
      this.show(
        error instanceof Error ? error.message : 'Nie udało się cofnąć operacji.',
        { kind: 'error' },
      );
    }
  }
}
