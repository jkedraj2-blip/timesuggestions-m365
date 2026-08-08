const PLURAL_RULES = new Intl.PluralRules('pl');

/**
 * Polska odmiana rzeczownika po liczebniku: 1 spotkanie / 3 spotkania / 5 spotkań.
 * Kategorie Intl: one (1), few (2–4, 22–24, …), many (0, 5–21, 25–31, …);
 * ułamki ("other") traktujemy jak many.
 */
export function polishPlural(count: number, one: string, few: string, many: string): string {
  switch (PLURAL_RULES.select(count)) {
    case 'one':
      return one;
    case 'few':
      return few;
    default:
      return many;
  }
}
