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

  /**
   * Widok, który zgłosił ostatnią zmianę. Celowo zwykłe pole, nie sygnał:
   * służy wyłącznie do odsiania własnego powiadomienia i nie ma powodu,
   * żeby samo w sobie wyzwalało przeliczenia.
   */
  private lastOrigin: object | null = null;

  /** Rosnący licznik zmian — strony obserwują go w effect(). */
  readonly changes = this.version.asReadonly();

  /**
   * Rozgłasza "dane się zmieniły". <paramref name="origin"/> to widok, który
   * operację wykonał i odświeżył się już sam — dzięki temu nie ładuje swojej
   * listy drugi raz, a pozostałe widoki (oś czasu, kafelki) i tak dostają sygnał.
   * Bez origin (np. „Cofnij" z toastu) przeładowują się wszyscy.
   */
  notify(origin: object | null = null): void {
    this.lastOrigin = origin;
    this.version.update((value) => value + 1);
  }

  /** Czy ostatnie powiadomienie pochodzi od tego właśnie widoku. */
  isOwn(listener: object): boolean {
    return this.lastOrigin === listener;
  }
}
