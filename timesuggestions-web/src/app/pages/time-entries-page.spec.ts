import { describe, expect, it } from 'vitest';
import {
  allVisibleRange,
  confirmSettleLabel,
  currentMonthRange,
  formatRangeLabel,
  lastWeekRange,
  mergeGroupKey,
  mergePreview,
  settledToastMessage,
  toIsoDate,
  uncountedMinutes,
  signedMinutes,
  gapButtonLabel,
} from './time-entries-page';
import { DetectedGap, TimeEntry } from '../models/api.models';

function createEntry(overrides: Partial<TimeEntry> = {}): TimeEntry {
  return {
    id: 1,
    caseId: 1,
    caseName: 'Kowalski',
    caseNumber: 'K-2026-001',
    clientName: 'Kowalski',
    entryDate: '2026-08-06',
    startedAt: '2026-08-06T09:00:00',
    endedAt: '2026-08-06T10:00:00',
    durationMinutes: 60,
    description: 'Praca',
    createdFromSuggestion: true,
    source: 'document',
    suggestionIds: [1],
    sourceTitle: 'Umowa.docx',
    sourceStartedAt: '2026-08-06T09:00:00',
    sourceExternalId: 'file-1',
    archivedAt: null,
    detectedGaps: [],
    sessionLabel: null,
    roundedDurationMinutes: 60,
    roundingMinutes: 0,
    notice: null,
    ...overrides,
  };
}

describe('toIsoDate', () => {
  it('formatuje datę lokalną jako yyyy-MM-dd z zerami wiodącymi', () => {
    expect(toIsoDate(new Date(2026, 7, 9))).toBe('2026-08-09');
    expect(toIsoDate(new Date(2026, 0, 1))).toBe('2026-01-01');
  });
});

describe('lastWeekRange', () => {
  it('obejmuje 7 ostatnich dni z dzisiaj włącznie', () => {
    expect(lastWeekRange(new Date(2026, 7, 9))).toEqual({ from: '2026-08-03', to: '2026-08-09' });
  });

  it('przechodzi przez granicę miesiąca i roku', () => {
    expect(lastWeekRange(new Date(2026, 7, 2))).toEqual({ from: '2026-07-27', to: '2026-08-02' });
    expect(lastWeekRange(new Date(2026, 0, 3))).toEqual({ from: '2025-12-28', to: '2026-01-03' });
  });
});

describe('currentMonthRange', () => {
  it('zaczyna od pierwszego dnia bieżącego miesiąca', () => {
    expect(currentMonthRange(new Date(2026, 7, 9))).toEqual({ from: '2026-08-01', to: '2026-08-09' });
  });

  it('pierwszego dnia miesiąca zakres to jeden dzień', () => {
    expect(currentMonthRange(new Date(2026, 7, 1))).toEqual({ from: '2026-08-01', to: '2026-08-01' });
  });
});

describe('allVisibleRange', () => {
  it('rozciąga się od najstarszego widocznego dnia do dziś', () => {
    const range = allVisibleRange(['2026-08-05', '2026-07-20', '2026-08-01'], new Date(2026, 7, 9));

    expect(range).toEqual({ from: '2026-07-20', to: '2026-08-09' });
  });

  it('pusta lista → null (nie ma czego rozliczać)', () => {
    expect(allVisibleRange([], new Date(2026, 7, 9))).toBeNull();
  });
});

describe('settledToastMessage', () => {
  it('odmienia wpisy przez polishPlural i formatuje czas', () => {
    expect(settledToastMessage(1, 45)).toBe('Rozliczono 1 wpis (45 min).');
    expect(settledToastMessage(3, 120)).toBe('Rozliczono 3 wpisy (2 godz.).');
    expect(settledToastMessage(5, 570)).toBe('Rozliczono 5 wpisów (9 godz. 30 min).');
    expect(settledToastMessage(12, 570)).toBe('Rozliczono 12 wpisów (9 godz. 30 min).');
  });

  it('zero wpisów to normalny komunikat, nie błąd', () => {
    expect(settledToastMessage(0, 0)).toBe('Rozliczono 0 wpisów (0 min).');
  });
});

describe('formatRangeLabel', () => {
  it('w obrębie jednego roku pomija rok', () => {
    expect(formatRangeLabel({ from: '2026-08-03', to: '2026-08-09' })).toBe('03.08–09.08');
    expect(formatRangeLabel({ from: '2026-01-01', to: '2026-08-09' })).toBe('01.01–09.08');
  });

  it('przy przełomie roku dopisuje lata, żeby zakres nie wyglądał na kilka tygodni', () => {
    expect(formatRangeLabel({ from: '2025-01-01', to: '2026-08-09' }))
      .toBe('01.01.2025–09.08.2026');
  });
});

describe('confirmSettleLabel', () => {
  it('pyta o potwierdzenie z liczbą wpisów i sumą czasu', () => {
    expect(confirmSettleLabel(3, 130)).toBe('Na pewno? Rozliczysz 3 wpisy (2 godz. 10 min)');
    expect(confirmSettleLabel(1, 60)).toBe('Na pewno? Rozliczysz 1 wpis (1 godz.)');
  });

  it('dla akcji hurtowej dopisuje zakres dat', () => {
    expect(confirmSettleLabel(3, 130, { from: '2026-08-03', to: '2026-08-09' }))
      .toBe('Na pewno? Rozliczysz 3 wpisy (2 godz. 10 min) z 03.08–09.08');
  });

  it('wieloletnie „wszystko" ujawnia skalę zakresu', () => {
    expect(confirmSettleLabel(143, 12600, { from: '2025-01-01', to: '2026-08-09' }))
      .toBe('Na pewno? Rozliczysz 143 wpisy (210 godz.) z 01.01.2025–09.08.2026');
  });

  it('przy „Rozlicz dzień" zakres jest pomijany, bo wynika z nagłówka dnia', () => {
    expect(confirmSettleLabel(2, 90, null)).toBe('Na pewno? Rozliczysz 2 wpisy (1 godz. 30 min)');
  });
});

describe('mergeGroupKey', () => {
  it('grupuje wpisy dokumentowe po dniu i pliku', () => {
    expect(mergeGroupKey(createEntry())).toBe('2026-08-06|file-1');
  });

  it('wpis kalendarzowy i wpis bez id pliku nie należą do żadnej grupy', () => {
    expect(mergeGroupKey(createEntry({ source: 'calendar', sourceExternalId: null }))).toBeNull();
    expect(mergeGroupKey(createEntry({ sourceExternalId: null }))).toBeNull();
  });

  it('ten sam plik w różne dni to różne grupy (scalanie działa w obrębie dnia)', () => {
    const monday = mergeGroupKey(createEntry({ entryDate: '2026-08-03' }));
    const tuesday = mergeGroupKey(createEntry({ entryDate: '2026-08-04' }));
    expect(monday).not.toBe(tuesday);
  });
});

describe('mergePreview', () => {
  it('liczy sumę sesji oraz sumę z lukami między przedziałami', () => {
    const preview = mergePreview([
      createEntry({ startedAt: '2026-08-06T09:00:00', endedAt: '2026-08-06T09:30:00', durationMinutes: 30 }),
      createEntry({ id: 2, startedAt: '2026-08-06T10:00:00', endedAt: '2026-08-06T10:30:00', durationMinutes: 30 }),
    ]);

    expect(preview.sessionsMinutes).toBe(60);
    expect(preview.withGapsMinutes).toBe(90); // + luka 09:30–10:00
  });

  it('kolejność wejścia nie zmienia wyniku (sortowanie po starcie)', () => {
    const preview = mergePreview([
      createEntry({ id: 2, startedAt: '2026-08-06T10:00:00', endedAt: '2026-08-06T10:30:00', durationMinutes: 30 }),
      createEntry({ startedAt: '2026-08-06T09:00:00', endedAt: '2026-08-06T09:30:00', durationMinutes: 30 }),
    ]);

    expect(preview.withGapsMinutes).toBe(90);
  });

  it('przylegające przedziały nie generują luki', () => {
    const preview = mergePreview([
      createEntry({ startedAt: '2026-08-06T09:00:00', endedAt: '2026-08-06T09:30:00', durationMinutes: 30 }),
      createEntry({ id: 2, startedAt: '2026-08-06T09:30:00', endedAt: '2026-08-06T10:00:00', durationMinutes: 30 }),
    ]);

    expect(preview.withGapsMinutes).toBe(60);
  });
});

/**
 * Wpis po scaleniu sesji obejmuje cały odcinek pracy, a rozlicza tylko sesje.
 * Bez nazwania tej różnicy godziny i czas obok siebie wyglądają jak błąd rachunku —
 * dokładnie tak wyglądał wpis, w którym „brakowało" krótszej ze scalonych sesji.
 */
describe('uncountedMinutes', () => {
  const gap = (minutes: number, counted: boolean): DetectedGap => ({
    startAt: '2026-08-06T09:30:00',
    endAt: '2026-08-06T10:20:00',
    minutes,
    counted,
  });

  it('sumuje minuty przerw, których nikt nie rozlicza', () => {
    const merged = createEntry({
      startedAt: '2026-08-06T09:00:00',
      endedAt: '2026-08-06T10:20:00',
      durationMinutes: 50,
      detectedGaps: [gap(30, false)],
    });

    expect(uncountedMinutes(merged)).toBe(30);
  });

  it('przerwa wliczona w czas nie jest minutą nieliczoną', () => {
    expect(uncountedMinutes(createEntry({ detectedGaps: [gap(20, true)] }))).toBe(0);
  });

  it('zwykły wpis nie ma nieliczonych minut', () => {
    expect(uncountedMinutes(createEntry())).toBe(0);
  });

  /**
   * Sedno poprawki: zaokrąglenie w dół powiększa różnicę „godziny minus czas", ale nie
   * jest przerwą. Liczone razem z przerwami meldowało minuty, których w historii wersji
   * nie było i których nie dało się kliknąć.
   */
  it('zaokrąglenie w dół nie udaje przerwy', () => {
    const rounded = createEntry({
      startedAt: '2026-08-06T09:00:00',
      endedAt: '2026-08-06T10:00:00',
      durationMinutes: 30,
      roundingMinutes: -30,
    });

    expect(uncountedMinutes(rounded)).toBe(0);
  });
});

describe('signedMinutes', () => {
  it('pokazuje kierunek korekty bez liczenia w głowie', () => {
    expect(signedMinutes(10)).toBe('+10 min');
    expect(signedMinutes(-20)).toBe('−20 min');
  });
});

/**
 * Kierunek akcji na przerwie wynika z jej STANU, nie z pochodzenia: przerwa wewnątrz
 * sesji jest wliczona (da się odjąć), przerwa między scalonymi sesjami nie jest (da się
 * doliczyć). Wcześniej istniało tylko „Odejmij", więc minut po scaleniu nie dało się
 * odzyskać jednym kliknięciem.
 */
describe('gapButtonLabel', () => {
  const gap = (overrides: Partial<DetectedGap> = {}): DetectedGap => ({
    startAt: '2026-08-06T09:30:00',
    endAt: '2026-08-06T10:20:00',
    minutes: 50,
    counted: true,
    ...overrides,
  });

  it('wliczoną przerwę proponuje odjąć', () => {
    expect(gapButtonLabel(gap())).toBe('Odejmij przerwę 09:30 (50 min)');
  });

  it('nieliczoną przerwę proponuje doliczyć', () => {
    expect(gapButtonLabel(gap({ counted: false }))).toBe('Dolicz przerwę 09:30 (50 min)');
  });
});
