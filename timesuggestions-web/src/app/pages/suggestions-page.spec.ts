import { describe, expect, it } from 'vitest';
import {
  archivedSuggestionsToast,
  filteredOutLine,
  normalizedSyncDays,
  syncCheckedLine,
  syncReportHeadline,
  syncSkippedLine,
} from './suggestions-page';
import { SyncFilteredOutCounts } from '../models/api.models';

const NO_FILTERED: SyncFilteredOutCounts = {
  private: 0,
  tooShort: 0,
  allDay: 0,
  cancelled: 0,
  invalidDates: 0,
  notOfficeDocument: 0,
  notModifiedByUser: 0,
  total: 0,
};

describe('syncReportHeadline', () => {
  it('bez żadnych zmian mówi wprost "bez zmian" zamiast zerowych liczników', () => {
    expect(syncReportHeadline({ created: 0, updated: 0, removed: 0 })).toBe(
      'Synchronizacja zakończona bez zmian. Wszystkie sugestie są aktualne.',
    );
  });

  it('wymienia tylko niezerowe efekty z poprawną odmianą', () => {
    expect(syncReportHeadline({ created: 2, updated: 0, removed: 1 })).toBe(
      'Synchronizacja zakończona: 2 nowe sugestie, 1 usunięto.',
    );
    expect(syncReportHeadline({ created: 1, updated: 3, removed: 0 })).toBe(
      'Synchronizacja zakończona: 1 nowa sugestia, 3 zaktualizowano.',
    );
    expect(syncReportHeadline({ created: 5, updated: 0, removed: 0 })).toBe(
      'Synchronizacja zakończona: 5 nowych sugestii.',
    );
  });
});

describe('archivedSuggestionsToast', () => {
  it('odmienia sugestie przez polishPlural', () => {
    expect(archivedSuggestionsToast(1)).toBe('Zarchiwizowano 1 sugestię.');
    expect(archivedSuggestionsToast(3)).toBe('Zarchiwizowano 3 sugestie.');
    expect(archivedSuggestionsToast(5)).toBe('Zarchiwizowano 5 sugestii.');
  });
});

describe('syncCheckedLine', () => {
  it('odmienia "3 spotkania" i pomija zerowe pliki', () => {
    expect(syncCheckedLine({ calendarEvents: 3, driveFiles: 0 }, 7)).toBe(
      'Sprawdzono 3 spotkania z ostatnich 7 dni.',
    );
  });

  it('łączy spotkania i pliki, gdy oba niezerowe', () => {
    expect(syncCheckedLine({ calendarEvents: 1, driveFiles: 2 }, 7)).toBe(
      'Sprawdzono 1 spotkanie z ostatnich 7 dni i 2 pliki.',
    );
  });

  it('oba zera → komunikat o braku danych do sprawdzenia', () => {
    expect(syncCheckedLine({ calendarEvents: 0, driveFiles: 0 }, 7)).toBe(
      'Brak spotkań i plików do sprawdzenia w ostatnich 7 dniach.',
    );
  });

  it('pokazuje faktycznie użyte okno z raportu, nie stałą', () => {
    expect(syncCheckedLine({ calendarEvents: 3, driveFiles: 0 }, 30)).toBe(
      'Sprawdzono 3 spotkania z ostatnich 30 dni.',
    );
  });
});

describe('normalizedSyncDays', () => {
  it.each([
    [7, 7],
    [14, 14],
    [30, 30],
    ['14', 14],
  ])('przepuszcza dozwolony zakres %s', (raw, expected) => {
    expect(normalizedSyncDays(raw)).toBe(expected);
  });

  it.each([0, 10, 365, -7, 'abc', null, undefined])(
    'wartość spoza listy (%s) wraca do domyślnych 7 dni',
    (raw) => {
      expect(normalizedSyncDays(raw)).toBe(7);
    },
  );
});

describe('syncSkippedLine', () => {
  it('tłumaczy "pominięto (już istniały)" bez technicznego żargonu', () => {
    expect(syncSkippedLine(1)).toBe('1 pozycja była już wcześniej na liście sugestii, więc nic nie duplikujemy.');
    expect(syncSkippedLine(5)).toBe('5 pozycji było już wcześniej na liście sugestii, więc nic nie duplikujemy.');
  });
});

describe('filteredOutLine', () => {
  it('przy jednym powodzie nie dubluje liczby', () => {
    expect(filteredOutLine({ ...NO_FILTERED, tooShort: 1, total: 1 })).toBe(
      'Pominięto 1 pozycję: krótsza niż 5 minut.',
    );
    expect(filteredOutLine({ ...NO_FILTERED, notOfficeDocument: 6, total: 6 })).toBe(
      'Pominięto 6 pozycji: plików innych niż Word/Excel.',
    );
  });

  it('przy wielu powodach skleja liczbę i powód separatorem, bez separatora na końcu', () => {
    const text = filteredOutLine({ ...NO_FILTERED, cancelled: 2, allDay: 1, total: 3 });
    expect(text).toBe('Pominięto 3 pozycje: 2 odwołane · 1 całodniowa.');
    expect(text.trimEnd().endsWith('·')).toBe(false);

    expect(filteredOutLine({ ...NO_FILTERED, cancelled: 2, tooShort: 1, total: 3 })).toBe(
      'Pominięto 3 pozycje: 2 odwołane · 1 krótsza niż 5 minut.',
    );
  });

  it('opisuje powody językiem użytkownika', () => {
    expect(filteredOutLine({ ...NO_FILTERED, private: 1, total: 1 })).toBe(
      'Pominięto 1 pozycję: prywatna lub poufna (tytuły nie opuszczają przeglądarki).',
    );
    expect(filteredOutLine({ ...NO_FILTERED, notOfficeDocument: 3, total: 3 })).toBe(
      'Pominięto 3 pozycje: pliki inne niż Word/Excel.',
    );
  });

  it('odmienia etykiety po liczebniku', () => {
    expect(filteredOutLine({ ...NO_FILTERED, cancelled: 5, total: 5 })).toBe(
      'Pominięto 5 pozycji: odwołanych.',
    );
    expect(filteredOutLine({ ...NO_FILTERED, notModifiedByUser: 2, total: 2 })).toBe(
      'Pominięto 2 pozycje: zmodyfikowane przez kogoś innego.',
    );
  });
});
