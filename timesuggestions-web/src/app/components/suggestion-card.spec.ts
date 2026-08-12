import { beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ComponentFixture } from '@angular/core/testing';
import {
  SuggestionCard,
  gapClaimMessage,
  mergeMessage,
  suggestionGapNote,
} from './suggestion-card';
import { ApiService } from '../services/api.service';
import { Suggestion, SuggestionNeighbor } from '../models/api.models';

function createSuggestion(overrides: Partial<Suggestion> = {}): Suggestion {
  return {
    id: 1,
    source: 'calendar',
    title: 'Spotkanie z Kowalski',
    startedAt: '2026-08-06T10:00:00',
    durationMinutes: 60,
    caseId: 1,
    caseName: 'Kowalski sp. z o.o.',
    caseNumber: 'K-2026-001',
    clientName: 'Kowalski',
    isAmbiguous: false,
    matchCandidates: [],
    proposedDescription: 'Spotkanie z Kowalski',
    status: 'pending',
    detectedGaps: [],
    needsTimeReview: false,
    sourceExternalId: null,
    // Bez sufiksu „Z": DTO podaje ostatnią zmianę na TEJ SAMEJ osi co startedAt
    // (czas strefy biznesowej), więc fixture nie może udawać wartości w UTC.
    lastActivityAt: '2026-08-06T11:00:00',
    isUserAdjusted: false,
    gaps: null,
    sessionLabel: null,
    ...overrides,
  };
}

/**
 * Operacja na przerwie jest natychmiastowa i cicha: karta znika, lista ładuje się od nowa
 * i bez zdania „co się właśnie stało" prawnik widzi tylko przeskakujące liczby. Te teksty
 * są całym potwierdzeniem operacji — stąd testy na nie same, a nie na przypadkowy fragment.
 */
describe('komunikaty po operacjach na czasie', () => {
  const base = (overrides: Partial<Suggestion> = {}): Suggestion =>
    createSuggestion({ startedAt: '2026-08-06T10:00:00', durationMinutes: 60, ...overrides });

  it('doliczenie całości podaje minuty, kierunek i nowy zakres godzin', () => {
    const previous = base();
    // Sesja rośnie „przed": start cofa się o 30 min, czas rośnie o tyle samo.
    const updated = base({ startedAt: '2026-08-06T09:30:00', durationMinutes: 90 });

    expect(gapClaimMessage(previous, [updated], 'Umowa_NovaTech.docx', 'before', 0)).toBe(
      'Doliczono 30 min przed tą sesją. Ta sugestia to teraz 09:30–11:00 (1 godz. 30 min).',
    );
  });

  it('podział mówi, ile poszło tutaj, a ile do sąsiada', () => {
    const previous = base();
    const updated = base({ durationMinutes: 75 });
    const other = base({ id: 7, durationMinutes: 40 });

    expect(gapClaimMessage(previous, [updated, other], 'Umowa_NovaTech.docx', 'after', 15)).toBe(
      'Przerwa podzielona: 15 min tutaj, 15 min do „Umowa_NovaTech.docx".'
        + ' Ta sugestia to teraz 10:00–11:15 (1 godz. 15 min).',
    );
  });

  it('gdy cała przerwa poszła do sąsiada, mówi to wprost zamiast milczeć', () => {
    const previous = base();
    const other = base({ id: 7, durationMinutes: 40 });

    expect(gapClaimMessage(previous, [other], 'Umowa_NovaTech.docx', 'before', 20)).toBe(
      'Doliczono 20 min do „Umowa_NovaTech.docx", ta sugestia bez zmian.',
    );
  });

  it('scalenie mówi, co powstało i że przerwa NIE została doliczona', () => {
    expect(mergeMessage(base({ title: 'Umowa_NovaTech.docx', durationMinutes: 135 }))).toBe(
      'Sesje scalone w jedną: „Umowa_NovaTech.docx" 10:00–12:15 (2 godz. 15 min do rozliczenia).'
        + ' Przerwa między sesjami nie została doliczona.',
    );
  });

  /**
   * Sedno scalenia bez przerw: godziny obejmują OBIE sesje, a czas liczy tylko je same.
   * Komunikat wyliczający koniec z minut kończył scaloną sugestię w środku pracy —
   * prawnik czytał godzinę, pod którą wciąż pisał w dokumencie.
   */
  /**
   * Przy samym „Zatwierdź" nie było ŻADNEJ informacji, że w tym czasie siedzi przerwa
   * i że da się ją poprawić po zatwierdzeniu. To zmienia kwotę na rachunku, więc musi
   * być napisane, a nie ukryte w historii wersji.
   */
  describe('informacja o przerwach', () => {
    it('nazywa wykrytą przerwę i mówi, gdzie się ją odejmuje', () => {
      const note = suggestionGapNote(base({
        detectedGaps: [
          { startAt: '2026-08-06T10:20:00', endAt: '2026-08-06T10:40:00', minutes: 20, counted: true },
        ],
      }));

      expect(note).toContain('W tej sesji jest 1 przerwa (łącznie 20 min) wliczona w czas pracy.');
      expect(note).toContain('Wpisy czasu');
    });

    it('po scaleniu mówi wprost, ile minut NIE jest liczone', () => {
      const note = suggestionGapNote(base({
        startedAt: '2026-08-06T10:00:00',
        durationMinutes: 50,
        lastActivityAt: '2026-08-06T11:20:00',
      }));

      expect(note).toContain('obejmuje 1 godz. 20 min, a liczy 50 min');
      expect(note).toContain('30 min przerw między sesjami nie jest liczone');
    });

    it('milczy, gdy nie ma o czym mówić', () => {
      expect(suggestionGapNote(base())).toBeNull();
    });
  });

  it('po scaleniu podaje zasięg obu sesji, nie odcinek długości czasu', () => {
    const survivor = base({
      title: 'Umowa_NovaTech.docx',
      startedAt: '2026-08-06T10:00:00',
      durationMinutes: 50,
      lastActivityAt: '2026-08-06T11:20:00',
    });

    expect(mergeMessage(survivor)).toBe(
      'Sesje scalone w jedną: „Umowa_NovaTech.docx" 10:00–11:20 (50 min do rozliczenia).'
        + ' Przerwa między sesjami nie została doliczona.',
    );
  });
});

describe('SuggestionCard', () => {
  let fixture: ComponentFixture<SuggestionCard>;

  const approveMock = vi.fn();
  const claimGapMock = vi.fn();
  const mergeMock = vi.fn();

  beforeEach(async () => {
    approveMock.mockReset();
    claimGapMock.mockReset();
    mergeMock.mockReset();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: ApiService,
          useValue: {
            approve: approveMock,
            reject: vi.fn(),
            restore: vi.fn(),
            claimSuggestionGap: claimGapMock,
            mergeSuggestions: mergeMock,
          },
        },
      ],
    });
    fixture = TestBed.createComponent(SuggestionCard);
    fixture.componentRef.setInput('suggestion', createSuggestion());
    fixture.componentRef.setInput('cases', []);
    await fixture.whenStable();
    fixture.detectChanges();
  });

  function card(): SuggestionCard & {
    durationDraft: { (): number; set(value: number): void };
    descriptionDraft: { (): string; set(value: string): void };
    sourceConflict: () => boolean;
    editing: { (): boolean; set(value: boolean): void };
    approve: () => Promise<void>;
  } {
    // Dostęp do pól protected na potrzeby testu zachowania draftów.
    return fixture.componentInstance as never;
  }

  async function setSuggestion(suggestion: Suggestion): Promise<void> {
    fixture.componentRef.setInput('suggestion', suggestion);
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('inicjalizuje drafty z wartości sugestii', () => {
    expect(card().durationDraft()).toBe(60);
    expect(card().descriptionDraft()).toBe('Spotkanie z Kowalski');
  });

  it('pokazuje dopasowaną sprawę w podpisanej linii z numerem i klientem', () => {
    const caseLine = (fixture.nativeElement as HTMLElement).querySelector('.case-line');

    expect(caseLine?.textContent).toContain('Sprawa:');
    // Zielona plakietka sygnalizuje dopasowanie — jak przed przeniesieniem do osobnej linii.
    expect(caseLine?.querySelector('.badge-success')?.textContent).toBe('Kowalski sp. z o.o.');
    expect(caseLine?.textContent).toContain('K-2026-001 · Kowalski');
  });

  it('ukrywa linię sprawy przy braku dopasowania', async () => {
    await setSuggestion(createSuggestion({ caseId: null, caseName: null, caseNumber: null, clientName: null }));

    expect((fixture.nativeElement as HTMLElement).querySelector('.case-line')).toBeNull();
  });

  it('resetuje drafty po zmianie inputa, gdy użytkownik nie edytuje', async () => {
    await setSuggestion(createSuggestion({ durationMinutes: 90, proposedDescription: 'Nowy opis' }));

    expect(card().durationDraft()).toBe(90);
    expect(card().descriptionDraft()).toBe('Nowy opis');
    expect(card().sourceConflict()).toBe(false);
  });

  it('nie nadpisuje pracy użytkownika i blokuje Zatwierdź przy konflikcie edycji', async () => {
    card().editing.set(true);
    card().durationDraft.set(120);
    fixture.detectChanges();

    // Synchronizacja w tle zmieniła źródło, podczas gdy użytkownik edytuje.
    await setSuggestion(createSuggestion({ durationMinutes: 90 }));

    expect(card().sourceConflict()).toBe(true);
    expect(card().durationDraft()).toBe(120); // praca użytkownika nietknięta

    const approveButton = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('.btn-primary');
    expect(approveButton?.disabled).toBe(true);

    // Zatwierdzenie zablokowane także programowo — API nie może zostać wywołane.
    await card().approve();
    expect(approveMock).not.toHaveBeenCalled();
  });

  it('"Odśwież wartości" przejmuje nowe wartości źródła i odblokowuje Zatwierdź', async () => {
    card().editing.set(true);
    card().durationDraft.set(120);
    await setSuggestion(createSuggestion({ durationMinutes: 90 }));
    expect(card().sourceConflict()).toBe(true);

    (fixture.nativeElement as HTMLElement)
      .querySelectorAll<HTMLButtonElement>('.conflict-box button')[0]
      .click();
    fixture.detectChanges();

    expect(card().sourceConflict()).toBe(false);
    expect(card().durationDraft()).toBe(90);
  });

  it('"Zachowaj moje" utrzymuje wartości użytkownika i odblokowuje Zatwierdź', async () => {
    card().editing.set(true);
    card().durationDraft.set(120);
    await setSuggestion(createSuggestion({ durationMinutes: 90 }));

    (fixture.nativeElement as HTMLElement)
      .querySelectorAll<HTMLButtonElement>('.conflict-box button')[1]
      .click();
    fixture.detectChanges();

    expect(card().sourceConflict()).toBe(false);
    expect(card().durationDraft()).toBe(120);
  });

  describe('wiersz z czasami', () => {
    function details(): string[] {
      return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.detail'))
        .map((detail) => (detail.textContent ?? '').replace(/\s+/g, ' ').trim());
    }

    it('podaje początek, ostatnią zmianę i czas pracy — w tej kolejności i z podpisami', async () => {
      await setSuggestion(createSuggestion({
        startedAt: '2026-08-11T22:58:00',
        lastActivityAt: '2026-08-11T23:14:00',
        durationMinutes: 16,
      }));

      expect(details()).toEqual([
        'początek 11.08.2026 22:58',
        'ostatnia zmiana 23:14',
        'czas pracy 16 min',
      ]);
    });

    it('pomija ostatnią zmianę, gdy to ten sam moment co początek (sesja z jednego zapisu)', async () => {
      await setSuggestion(createSuggestion({
        startedAt: '2026-08-11T22:58:36',
        lastActivityAt: '2026-08-11T22:58:36',
        durationMinutes: 5,
      }));

      expect(details()).toEqual(['początek 11.08.2026 22:58', 'czas pracy 5 min']);
    });

    it('dokłada datę do ostatniej zmiany dopiero po zmianie doby', async () => {
      await setSuggestion(createSuggestion({
        startedAt: '2026-08-11T23:50:00',
        lastActivityAt: '2026-08-12T00:10:00',
        durationMinutes: 20,
      }));

      expect(details()[1]).toBe('ostatnia zmiana 12.08.2026 00:10');
    });
  });

  describe('wolne luki i scalanie sesji', () => {
    const neighbor = (overrides: Partial<SuggestionNeighbor> = {}): SuggestionNeighbor => ({
      suggestionId: 7,
      title: 'Umowa_NovaTech.docx',
      gapMinutes: 30,
      canMerge: true,
      ...overrides,
    });

    function neighborRows(): HTMLElement[] {
      return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.neighbor-row'));
    }

    function buttonLabels(root: HTMLElement): (string | undefined)[] {
      return Array.from(root.querySelectorAll('button')).map((button) => button.textContent?.trim());
    }

    function splitForm(): HTMLElement | null {
      return (fixture.nativeElement as HTMLElement).querySelector('.split-form');
    }

    /** Klika przycisk po widocznej etykiecie — test opisuje to, co widzi użytkownik. */
    async function click(label: string): Promise<void> {
      const buttons = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'));
      buttons.find((button) => button.textContent?.trim() === label)!.click();
      await fixture.whenStable();
      fixture.detectChanges();
    }

    it('nie pokazuje niczego, gdy backend nie przysłał luk', () => {
      expect(neighborRows()).toHaveLength(0);
    });

    /**
     * Etykieta ma nieść liczbę i kierunek. „Dolicz całość" nie mówiło ani ile minut,
     * ani gdzie one pójdą — prawnik klikał i widział wyłącznie, że czas skądś podskoczył.
     */
    it('opisuje wolną lukę i daje wybór: całość, podział albo scalenie', async () => {
      await setSuggestion(createSuggestion({ gaps: { before: neighbor(), after: null } }));

      const row = neighborRows()[0];
      expect(row.textContent).toContain('Nierozliczone 30 min przed tą sesją');
      expect(row.textContent).toContain('Umowa_NovaTech.docx');
      expect(buttonLabels(row)).toEqual([
        'Dolicz 30 min do tej sesji',
        'Podziel przerwę…',
        'Scal w jedną sesję',
      ]);
    });

    it('każdy przycisk niesie pełne zdanie o skutku w podpowiedzi', async () => {
      await setSuggestion(createSuggestion({ gaps: { before: neighbor(), after: null } }));

      const titles = Array.from(neighborRows()[0].querySelectorAll('button'))
        .map((button) => button.getAttribute('title') ?? '');
      expect(titles[0]).toContain('Sąsiad zostaje bez zmian');
      expect(titles[1]).toContain('niedobrane minuty zostaną wolne');
      expect(titles[2]).toContain('przerwa między nimi NIE jest doliczana');
    });

    it('przy wpisie czasu po drugiej stronie oferuje doliczenie części, nie podział', async () => {
      await setSuggestion(createSuggestion({
        gaps: { before: neighbor({ suggestionId: null, canMerge: false, title: 'wpis „Rozprawa"' }), after: null },
      }));

      expect(buttonLabels(neighborRows()[0])).toEqual(['Dolicz 30 min do tej sesji', 'Dolicz część…']);
    });

    it('przylegająca sesja tego samego pliku daje samo scalenie — nie ma czego doliczać', async () => {
      await setSuggestion(createSuggestion({
        gaps: { before: null, after: neighbor({ gapMinutes: 0 }) },
      }));

      const row = neighborRows()[0];
      expect(row.textContent).toContain('Ta sama praca tuż po');
      expect(buttonLabels(row)).toEqual(['Scal w jedną sesję']);
    });

    /**
     * Sedno poprawki: sąsiad zza północy nie może obiecywać scalenia, bo backend
     * odmawia scalania pozycji z dwóch dni. Sama przerwa nadal jest do rozdzielenia.
     */
    it('nie proponuje scalenia, gdy backend go nie potwierdził', async () => {
      await setSuggestion(createSuggestion({
        gaps: { before: neighbor({ canMerge: false }), after: null },
      }));

      expect(buttonLabels(neighborRows()[0])).toEqual(['Dolicz 30 min do tej sesji', 'Podziel przerwę…']);
    });

    it('sugestia rozstrzygnięta nie dostaje żadnych akcji na luce', async () => {
      await setSuggestion(createSuggestion({ status: 'rejected', gaps: { before: neighbor(), after: null } }));

      expect(neighborRows()).toHaveLength(0);
    });

    it('„Dolicz całość" nie podaje minut — całą lukę wylicza serwer', async () => {
      await setSuggestion(createSuggestion({ gaps: { before: neighbor(), after: null } }));
      claimGapMock.mockResolvedValue([]);

      await click('Dolicz 30 min do tej sesji');

      expect(claimGapMock).toHaveBeenCalledWith(1, 'before');
    });

    it('po doliczeniu wypuszcza gotowe potwierdzenie dla użytkownika', async () => {
      await setSuggestion(createSuggestion({ gaps: { before: neighbor(), after: null } }));
      claimGapMock.mockResolvedValue([
        createSuggestion({ startedAt: '2026-08-06T09:30:00', durationMinutes: 90 }),
      ]);
      const messages: string[] = [];
      fixture.componentInstance.adjusted.subscribe((message) => messages.push(message));

      await click('Dolicz 30 min do tej sesji');

      expect(messages).toEqual([
        'Doliczono 30 min przed tą sesją. Ta sugestia to teraz 09:30–11:00 (1 godz. 30 min).',
      ]);
    });

    it('podział startuje od połowy i pokazuje obie liczby przed zapisem', async () => {
      await setSuggestion(createSuggestion({ gaps: { before: neighbor(), after: null } }));

      await click('Podziel przerwę…');

      const inputs = Array.from(splitForm()!.querySelectorAll('input')) as HTMLInputElement[];
      expect(inputs.map((input) => input.value)).toEqual(['15', '15']);
      expect(splitForm()!.textContent).toContain('z 30 min zostaje wolne: 0 min');
    });

    it('zapisuje podział poprawiony ręcznie i melduje resztę jako wolną', async () => {
      await setSuggestion(createSuggestion({ gaps: { before: neighbor(), after: null } }));
      claimGapMock.mockResolvedValue([]);
      await click('Podziel przerwę…');

      const [mine, theirs] = Array.from(splitForm()!.querySelectorAll('input')) as HTMLInputElement[];
      mine.value = '20';
      mine.dispatchEvent(new Event('input'));
      theirs.value = '5';
      theirs.dispatchEvent(new Event('input'));
      await fixture.whenStable();
      fixture.detectChanges();

      expect(splitForm()!.textContent).toContain('zostaje wolne: 5 min');
      await click('Zapisz podział');
      expect(claimGapMock).toHaveBeenCalledWith(1, 'before', 20, 5);
    });

    it('nie pozwala zapisać podziału większego niż przerwa', async () => {
      await setSuggestion(createSuggestion({ gaps: { before: neighbor(), after: null } }));
      await click('Podziel przerwę…');

      const [mine] = Array.from(splitForm()!.querySelectorAll('input')) as HTMLInputElement[];
      mine.value = '40';
      mine.dispatchEvent(new Event('input'));
      await fixture.whenStable();
      fixture.detectChanges();

      const save = Array.from(splitForm()!.querySelectorAll('button'))
        .find((button) => button.textContent?.trim() === 'Zapisz podział') as HTMLButtonElement;
      expect(save.disabled).toBe(true);
      expect(claimGapMock).not.toHaveBeenCalled();
    });
  });
});
