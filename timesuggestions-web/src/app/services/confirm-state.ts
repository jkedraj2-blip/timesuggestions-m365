import { signal } from '@angular/core';

/** Czas, po którym uzbrojone potwierdzenie samo wraca do stanu wyjściowego. */
export const CONFIRM_TIMEOUT_MS = 5000;

/**
 * Dwustopniowe potwierdzenie akcji nieodwracalnej bez window.confirm:
 * pierwsze kliknięcie uzbraja przycisk (jego etykieta zmienia się w pytanie),
 * drugie wykonuje. Kliknięcie gdziekolwiek indziej lub ~5 s bezczynności
 * rozbraja — przypadkowy podwójny klik za tydzień nie rozliczy niczego.
 */
export class TwoStepConfirm {
  private armedKey = signal<string | null>(null);
  private timeoutId: ReturnType<typeof setTimeout> | null = null;

  /** Czy przycisk o tym kluczu czeka na drugie kliknięcie (sygnał — szablon reaguje). */
  isArmed(key: string): boolean {
    return this.armedKey() === key;
  }

  /** true = potwierdzone (wykonaj akcję); false = dopiero uzbrojono przycisk. */
  confirm(key: string): boolean {
    if (this.armedKey() === key) {
      this.reset();
      return true;
    }

    // Uzbrojenie innego przycisku rozbraja poprzedni — naraz pytamy o jedną akcję.
    this.reset();
    this.armedKey.set(key);
    this.timeoutId = setTimeout(() => this.reset(), CONFIRM_TIMEOUT_MS);
    return false;
  }

  reset(): void {
    if (this.timeoutId !== null) {
      clearTimeout(this.timeoutId);
      this.timeoutId = null;
    }
    this.armedKey.set(null);
  }
}
