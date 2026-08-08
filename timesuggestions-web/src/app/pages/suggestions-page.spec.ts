import { describe, expect, it } from 'vitest';
import {
  filteredOutBreakdown,
  filteredOutLine,
  normalizedDocumentMinutes,
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
  outsideWindow: 0,
  notModifiedByUser: 0,
  total: 0,
};

describe('syncReportHeadline', () => {
  it('bez żadnych zmian mówi wprost "bez zmian" zamiast zerowych liczników', () => {
    expect(syncReportHeadline({ created: 0, updated: 0, removed: 0 })).toBe(
      'Synchronizacja zakończona — bez zmian. Wszystkie sugestie są aktualne.',
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

describe('syncCheckedLine', () => {
  it('odmienia "3 spotkania" i pomija zerowe pliki', () => {
    expect(syncCheckedLine({ calendarEvents: 3, driveFiles: 0 })).toBe(
      'Sprawdzono 3 spotkania z ostatnich 7 dni.',
    );
  });

  it('łączy spotkania i pliki, gdy oba niezerowe', () => {
    expect(syncCheckedLine({ calendarEvents: 1, driveFiles: 2 })).toBe(
      'Sprawdzono 1 spotkanie z ostatnich 7 dni i 2 pliki.',
    );
  });

  it('oba zera → komunikat o braku danych do sprawdzenia', () => {
    expect(syncCheckedLine({ calendarEvents: 0, driveFiles: 0 })).toBe(
      'Brak spotkań i plików do sprawdzenia w ostatnich 7 dniach.',
    );
  });
});

describe('syncSkippedLine', () => {
  it('tłumaczy "pominięto (już istniały)" bez technicznego żargonu', () => {
    expect(syncSkippedLine(1)).toBe('1 pozycja była już wcześniej na liście sugestii — nic nie duplikujemy.');
    expect(syncSkippedLine(5)).toBe('5 pozycji było już wcześniej na liście sugestii — nic nie duplikujemy.');
  });
});

describe('filteredOutBreakdown', () => {
  it('skleja tylko niezerowe powody bez separatora na końcu', () => {
    const text = filteredOutBreakdown({ ...NO_FILTERED, cancelled: 2, outsideWindow: 1, total: 3 });
    expect(text).toBe(
      '2 odwołane · 1 poza zakresem ostatnich 7 dni (np. spotkanie, które jeszcze się nie odbyło)',
    );
    expect(text.endsWith('·')).toBe(false);
    expect(text.trimEnd().endsWith('·')).toBe(false);
  });

  it('opisuje powody językiem użytkownika', () => {
    expect(filteredOutBreakdown({ ...NO_FILTERED, private: 1, total: 1 })).toBe(
      '1 prywatne lub poufne (ich tytuły nie opuszczają przeglądarki)',
    );
    expect(filteredOutBreakdown({ ...NO_FILTERED, notOfficeDocument: 3, total: 3 })).toBe(
      '3 pliki inne niż Word/Excel',
    );
  });
});

describe('filteredOutLine', () => {
  it('buduje pełne zdanie z odmianą wstępu', () => {
    expect(filteredOutLine({ ...NO_FILTERED, tooShort: 1, total: 1 })).toBe(
      'Pominięto 1 pozycję, która nie jest czasem pracy: 1 krótsze niż 5 minut.',
    );
  });
});

describe('normalizedDocumentMinutes', () => {
  it.each([
    [1, 1],
    [30, 30],
    [480, 480],
  ])('przepuszcza wartość %i', (raw, expected) => {
    expect(normalizedDocumentMinutes(raw)).toBe(expected);
  });

  it.each([0, -5, 481, 30.5, Number.NaN, 'abc', '', null, undefined])(
    'wartość spoza zakresu (%s) traktuje jak brak preferencji',
    (raw) => {
      expect(normalizedDocumentMinutes(raw)).toBeUndefined();
    },
  );
});
