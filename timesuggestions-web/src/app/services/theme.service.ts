import { Injectable, signal } from '@angular/core';

export type ThemeName = 'light' | 'blue' | 'dark';

const THEME_STORAGE_KEY = 'timesuggestions.theme';
const AVAILABLE_THEMES: ThemeName[] = ['light', 'blue', 'dark'];

/**
 * Jawny wybór motywu przez użytkownika. Wybór zapamiętywany lokalnie;
 * przy pierwszej wizycie (brak zapisu) szanujemy preferencję systemową.
 * Motyw działa wyłącznie przez atrybut data-theme na <html> — tokeny CSS
 * w styles.css robią całą resztę, komponenty o niczym nie wiedzą.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly theme = signal<ThemeName>(this.resolveInitialTheme());

  constructor() {
    // Atrybut ustawiamy od razu przy starcie, żeby uniknąć mignięcia złym motywem.
    this.applyToDocument(this.theme());
  }

  setTheme(theme: ThemeName): void {
    this.theme.set(theme);
    this.applyToDocument(theme);
    localStorage.setItem(THEME_STORAGE_KEY, theme);
  }

  private applyToDocument(theme: ThemeName): void {
    document.documentElement.setAttribute('data-theme', theme);
  }

  private resolveInitialTheme(): ThemeName {
    const stored = localStorage.getItem(THEME_STORAGE_KEY) as ThemeName | null;
    if (stored && AVAILABLE_THEMES.includes(stored)) {
      return stored;
    }
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
}
