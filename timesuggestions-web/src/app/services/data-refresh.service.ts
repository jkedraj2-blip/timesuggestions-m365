import { Injectable, signal } from '@angular/core';

/**
 * Powiadomienie "dane się zmieniły" dla operacji wykonywanych poza bieżącym
 * widokiem (np. "Cofnij" z toastu po przejściu na inną zakładkę). Strony
 * nasłuchują i przeładowują własne dane — callback undo nie musi (i nie może)
 * trzymać referencji do potencjalnie zniszczonego komponentu.
 */
@Injectable({ providedIn: 'root' })
export class DataRefreshService {
  private version = signal(0);

  /** Rosnący licznik zmian — strony obserwują go w effect(). */
  readonly changes = this.version.asReadonly();

  notify(): void {
    this.version.update((value) => value + 1);
  }
}
