import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DocumentHistory, formatGapSpan } from './document-history';
import { ApiService } from '../services/api.service';
import { DocxDiffService, LineChange } from '../services/docx-diff';
import {
  DetectedGap,
  DocumentActivityItem,
  DocumentSession,
  DocumentSessionKind,
} from '../models/api.models';

/** Pozycja pliku — w testach opisujemy ją zakresem godzin i stanem. */
function session(
  kind: DocumentSessionKind,
  startAt: string,
  endAt: string,
  overrides: Partial<DocumentSession> = {},
): DocumentSession {
  return {
    kind,
    startAt,
    endAt,
    suggestionId: kind === 'unsettled' || kind === 'settled' ? null : 1,
    timeEntryId: kind === 'unsettled' || kind === 'settled' ? 1 : null,
    label: null,
    gaps: [],
    ...overrides,
  };
}

function version(overrides: Partial<DocumentActivityItem> = {}): DocumentActivityItem {
  return {
    versionId: '1.0',
    occurredAt: '2026-08-11T23:34:48',
    size: 9414,
    gapMinutesSincePrevious: null,
    isSessionBreak: false,
    splitsSession: false,
    isCurrent: false,
    isSameVersionAsPrevious: false,
    ...overrides,
  };
}

/**
 * Chronologia pliku, w której najnowsza wersja jest jeszcze otwarta w Wordzie online:
 * numer 5.0 stoi, przesuwa się tylko jego znacznik czasu, więc dziennik ma trzy próbki
 * tej samej wersji. Dokładnie ten układ wywoływał błąd 400 invalidRequest.
 */
const OPEN_VERSION_HISTORY: DocumentActivityItem[] = [
  version({ versionId: '4.0', occurredAt: '2026-08-11T23:45:57' }),
  version({ versionId: '5.0', occurredAt: '2026-08-12T00:02:51', isCurrent: true }),
  version({ versionId: '5.0', occurredAt: '2026-08-12T00:07:10', isCurrent: true, isSameVersionAsPrevious: true }),
  version({ versionId: '5.0', occurredAt: '2026-08-12T00:11:09', isCurrent: true, isSameVersionAsPrevious: true }),
];

describe('DocumentHistory', () => {
  let fixture: ComponentFixture<DocumentHistory>;
  const getActivityMock = vi.fn();
  const compareMock = vi.fn();

  async function render(
    activity: DocumentActivityItem[],
    fileName = 'Umowa_NovaTech.docx',
    current?: { sessions: DocumentSession[]; suggestionId?: number; entryId?: number; gaps?: DetectedGap[] },
  ): Promise<void> {
    getActivityMock.mockResolvedValue({ versions: activity, sessions: current?.sessions ?? [] });
    fixture = TestBed.createComponent(DocumentHistory);
    fixture.componentRef.setInput('externalId', 'file-1');
    fixture.componentRef.setInput('fileName', fileName);
    if (current) {
      fixture.componentRef.setInput('currentSuggestionId', current.suggestionId ?? null);
      fixture.componentRef.setInput('currentEntryId', current.entryId ?? null);
      fixture.componentRef.setInput('sessionGaps', current.gaps ?? []);
    }
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function compareButtons(): HTMLButtonElement[] {
    return Array.from(element().querySelectorAll('.compare-btn'));
  }

  /** Opisy stanu przerw w kolejności chronologicznej — puste, gdy nic nie wiadomo. */
  function gapBadges(): string[] {
    return Array.from(element().querySelectorAll('.gap-row .badge'))
      .map((badge) => badge.textContent?.trim() ?? '');
  }

  beforeEach(() => {
    getActivityMock.mockReset();
    compareMock.mockReset();
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiService, useValue: { getDocumentHistory: getActivityMock } },
        { provide: DocxDiffService, useValue: { compareVersions: compareMock } },
      ],
    });
  });

  it('pokazuje każdą próbkę osobnym wierszem, także gdy numer wersji się powtarza', async () => {
    await render(OPEN_VERSION_HISTORY);

    // Powtórzony numer wersji NIE może scalić wierszy ani wysypać listy — klucz track
    // musi być unikalny (numer wersji sam w sobie nie jest).
    expect(element().querySelectorAll('.version-row')).toHaveLength(4);
  });

  /**
   * Sedno błędu: dwa wiersze tej samej wersji nie mają czego porównać, bo OneDrive
   * trzyma jeden snapshot na numer wersji. Zamiast przycisku kończącego się błędem
   * Graph 400 — zdanie tłumaczące, dlaczego nie ma co klikać.
   */
  it('nie daje przycisku porównania między dwiema próbkami tej samej wersji', async () => {
    await render(OPEN_VERSION_HISTORY);

    expect(compareButtons()).toHaveLength(1);
    expect(element().querySelectorAll('.same-version')).toHaveLength(2);
    expect(element().textContent).toContain('brak osobnego zapisu tej wersji');
  });

  /**
   * Samo „brak stanu pośredniego" zostawiało prawnika z wrażeniem, że praca z tych minut
   * przepadła. Efekt tych zapisów jest w porównaniu przy PIERWSZYM wierszu tej wersji —
   * i wiadomość ma na niego wskazać godziną z widocznego wiersza.
   */
  it('wskazuje zapis, przy którym te zmiany będą widoczne', async () => {
    await render(OPEN_VERSION_HISTORY);

    const hints = Array.from(element().querySelectorAll('.same-version'))
      .map((hint) => hint.textContent?.trim());
    // 5.0 pojawia się pierwszy raz o 00:02:51 — i tam stoi „Porównaj z poprzednią".
    expect(hints).toEqual([
      'brak osobnego zapisu tej wersji; zmiany zobaczysz w porównaniu przy 00:02:51',
      'brak osobnego zapisu tej wersji; zmiany zobaczysz w porównaniu przy 00:02:51',
    ]);
    // Pełne wyjaśnienie zostaje w dymku: w wierszu powtarzało się przy każdej próbce
    // i zajmowało więcej miejsca niż sama chronologia.
    expect(element().querySelector('.same-version')?.getAttribute('title'))
      .toContain('OneDrive przechowuje jeden zapis treści na numer wersji');
  });

  /** Wersja otwierająca dziennik nie ma poprzedniczki — nie ma dokąd odesłać. */
  it('przy wersji bez poprzedniczki nie obiecuje przycisku, którego nie ma', async () => {
    await render([
      version({ versionId: '1.0', occurredAt: '2026-08-11T09:00:00' }),
      version({ versionId: '1.0', occurredAt: '2026-08-11T09:20:00', isSameVersionAsPrevious: true }),
    ]);

    expect(compareButtons()).toHaveLength(0);
    expect(element().querySelector('.same-version')?.textContent?.trim()).toBe(
      'brak osobnego zapisu tej wersji; zmiany zobaczysz przy następnej',
    );
  });

  it('porównanie 4.0 → bieżąca 5.0 idzie z oznaczeniem bieżącej wersji', async () => {
    await render(OPEN_VERSION_HISTORY);
    compareMock.mockResolvedValue([]);

    compareButtons()[0].click();
    await fixture.whenStable();

    expect(compareMock).toHaveBeenCalledWith(
      'file-1',
      { versionId: '4.0', isCurrent: false },
      // Znacznik bieżącej wersji jest tu kluczowy: bez niego frontend pobrałby treść
      // spod /versions/5.0/content, czego Graph nie wydaje (400 invalidRequest).
      { versionId: '5.0', isCurrent: true },
      expect.anything(),
    );
  });

  it('plakietkę „bieżąca" pokazuje raz, przy najnowszym zapisie', async () => {
    await render(OPEN_VERSION_HISTORY);

    const badges = Array.from(element().querySelectorAll('.version-row .badge'));
    expect(badges).toHaveLength(1);
    expect(badges[0].textContent?.trim()).toBe('bieżąca');
  });

  it('dla pliku bez diffu nie ma ani przycisków, ani wyjaśnień o wersjach', async () => {
    await render(OPEN_VERSION_HISTORY, 'Zestawienie.xlsx');

    expect(compareButtons()).toHaveLength(0);
    expect(element().querySelectorAll('.same-version')).toHaveLength(0);
    expect(element().textContent).toContain('tylko dla plików .docx');
  });

  /**
   * Jeden plik ma zwykle kilka sesji, a więc i kilka sugestii. Bez zaznaczenia prawnik
   * dostawał całą historię dokumentu i nie miał jak poznać, które zapisy złożyły się na
   * czas, który właśnie zatwierdza.
   */
  describe('zaznaczenie zakresu oglądanej pozycji', () => {
    /** Trzy sesje w jednym pliku; zatwierdzana jest środkowa. */
    const THREE_SESSIONS: DocumentActivityItem[] = [
      version({ versionId: '1.0', occurredAt: '2026-08-11T09:00:00' }),
      version({ versionId: '2.0', occurredAt: '2026-08-11T09:08:00', gapMinutesSincePrevious: 8 }),
      version({
        versionId: '3.0', occurredAt: '2026-08-11T11:00:00',
        gapMinutesSincePrevious: 112, isSessionBreak: true, splitsSession: true,
      }),
      version({ versionId: '4.0', occurredAt: '2026-08-11T11:05:00', gapMinutesSincePrevious: 5 }),
      version({
        versionId: '5.0', occurredAt: '2026-08-11T15:00:00', gapMinutesSincePrevious: 235,
        isSessionBreak: true, splitsSession: true, isCurrent: true,
      }),
    ];

    function rowClasses(): string[][] {
      return Array.from(element().querySelectorAll('.version-row'))
        .map((row) => Array.from(row.classList));
    }

    /** Trzy pozycje pliku w trzech różnych stanach; oglądana jest środkowa. */
    const SESSIONS: DocumentSession[] = [
      session('settled', '2026-08-11T09:00:00', '2026-08-11T09:08:00', { timeEntryId: 7, suggestionId: null }),
      session('pending', '2026-08-11T11:00:00', '2026-08-11T11:05:00', { suggestionId: 3, timeEntryId: null }),
      session('rejected', '2026-08-11T15:00:00', '2026-08-11T15:00:00', { suggestionId: 4, timeEntryId: null }),
    ];

    const CURRENT = { sessions: SESSIONS, suggestionId: 3 };

    it('klamra obejmuje tylko zapisy z zakresu, z początkiem i końcem', async () => {
      await render(THREE_SESSIONS, 'Umowa_NovaTech.docx', CURRENT);

      const classes = rowClasses();
      // Każdy zapis należy do swojej pozycji; oglądana jest odróżniona osobną klasą.
      expect(classes.map((row) => row.includes('current-session')))
        .toEqual([false, false, true, true, false]);
      // Zaokrąglenie tylko na krańcach: z osobnych wierszy ma powstać jeden blok.
      expect(classes[2]).toContain('opens-session');
      expect(classes[3]).toContain('closes-session');
      expect(classes[2]).not.toContain('closes-session');
    });

    /**
     * Sedno poprawki: jeden plik daje kilka pozycji w RÓŻNYCH stanach naraz. Wcześniej
     * stan podawał ten, kto historię otwierał, więc z karty sugestii nie było widać,
     * że wcześniejsza praca nad tym plikiem jest już rozliczona.
     */
    it('każda pozycja pliku niesie swój własny stan, nie stan oglądanej', async () => {
      await render(THREE_SESSIONS, 'Umowa_NovaTech.docx', CURRENT);

      const labels = Array.from(element().querySelectorAll('.version-row .session-badge'))
        .map((badge) => badge.textContent?.trim());
      expect(labels).toEqual(['wpis rozliczony', 'sugestia oczekująca', 'sugestia odrzucona']);
      expect(element().textContent).toContain('Podświetlone obszary to zapisy');
    });

    it('oglądaną pozycję widać po osobnej plakietce, nie po kolorze', async () => {
      await render(THREE_SESSIONS, 'Umowa_NovaTech.docx', CURRENT);

      const marks = Array.from(element().querySelectorAll('.version-row .badge-accent'))
        .map((badge) => badge.textContent?.trim());
      expect(marks).toEqual(['ta pozycja']);
    });

    it('numer edycji pada obok stanu, tak jak na karcie pozycji', async () => {
      await render(THREE_SESSIONS, 'Umowa_NovaTech.docx', {
        sessions: [session('settled', '2026-08-11T09:00:00', '2026-08-11T09:08:00', { label: 'edycja 1' })],
      });

      expect(element().querySelector('.version-row .session-badge')?.textContent?.trim())
        .toBe('edycja 1, wpis rozliczony');
    });

    it.each([
      ['pending', 'sugestia oczekująca'],
      ['rejected', 'sugestia odrzucona'],
      ['archived', 'sugestia w archiwum'],
      ['unsettled', 'wpis do rozliczenia'],
      ['settled', 'wpis rozliczony'],
    ] as Array<[DocumentSessionKind, string]>)(
      'stan %s ma własny kolor obszaru i własną plakietkę',
      async (kind, label) => {
        await render(THREE_SESSIONS, 'Umowa_NovaTech.docx', {
          sessions: [session(kind, '2026-08-11T11:00:00', '2026-08-11T11:05:00')],
        });

        // Kolor idzie z klasy stanu na wierszu, więc obszar i plakietka nie mogą
        // pokazywać dwóch różnych rzeczy.
        const row = element().querySelectorAll('.version-row')[2];
        expect(row.classList).toContain(`kind-${kind}`);
        expect(row.querySelector('.session-badge')?.textContent?.trim()).toBe(label);
      },
    );

    /** Przerwa MIĘDZY zapisami pozycji należy do niej; przerwa prowadząca do niej z zewnątrz nie. */
    it('przerwa wchodzi do klamry tylko wtedy, gdy oba jej końce są w zakresie', async () => {
      await render(THREE_SESSIONS, 'Umowa_NovaTech.docx', CURRENT);

      const gaps = Array.from(element().querySelectorAll('.gap-row'))
        .map((row) => row.classList.contains('in-session'));
      // Przerwy: 09:00→09:08, 09:08→11:00 (wejście do sesji), 11:00→11:05, 11:05→15:00.
      expect(gaps).toEqual([true, false, true, false]);
    });

    it('plik bez pozycji to sama chronologia zapisów', async () => {
      await render(THREE_SESSIONS);

      expect(element().querySelectorAll('.in-session')).toHaveLength(0);
      expect(element().querySelectorAll('.session-badge')).toHaveLength(0);
    });

    /**
     * Przerwa poza wszystkimi pozycjami nie zmienia niczyjego czasu. Napisanie przy niej
     * „nie wliczona w czas" byłoby zaproszeniem do doliczenia minut, których pozycja nie
     * obejmuje — a prawnik mógł w tym czasie robić coś, o czym ten plik nie wie.
     */
    it('przerwa między pozycjami nie udaje, że mówi o czyimś czasie', async () => {
      await render(THREE_SESSIONS, 'Umowa_NovaTech.docx', CURRENT);

      expect(gapBadges()).toEqual(['poza pozycjami', 'poza pozycjami']);
    });
  });

  /**
   * Sedno: w historii wersji ma być widać nie tylko, że był przestój, ale czy jego
   * minuty ktoś rozlicza. Wcześniej opisana była wyłącznie przerwa z przedziału
   * 15–30 min, więc dwugodzinna wyglądała jak zwykły odstęp między zapisami.
   */
  describe('stan przerw w oglądanej pozycji', () => {
    /** Wpis po scaleniu: sesja 09:00–09:20, przerwa 40 min, sesja 10:00–10:30. */
    const MERGED_ENTRY: DocumentActivityItem[] = [
      version({ versionId: '1.0', occurredAt: '2026-08-11T09:00:00' }),
      version({ versionId: '2.0', occurredAt: '2026-08-11T09:05:00', gapMinutesSincePrevious: 5 }),
      version({
        versionId: '3.0', occurredAt: '2026-08-11T09:20:00',
        gapMinutesSincePrevious: 15, isSessionBreak: true,
      }),
      version({
        versionId: '4.0', occurredAt: '2026-08-11T10:00:00',
        gapMinutesSincePrevious: 40, isSessionBreak: true, splitsSession: true,
      }),
      version({ versionId: '5.0', occurredAt: '2026-08-11T10:30:00', gapMinutesSincePrevious: 30, isSessionBreak: true }),
    ];

    /** Cały odcinek należy do jednego, nierozliczonego wpisu — i to on jest oglądany. */
    const SPAN = {
      sessions: [session('unsettled', '2026-08-11T09:00:00', '2026-08-11T10:30:00', { timeEntryId: 1 })],
      entryId: 1,
    };

    it('przerwa dzieląca sesje jest opisana jako nie wliczona, krótsza jako wliczona', async () => {
      await render(MERGED_ENTRY, 'Umowa_NovaTech.docx', SPAN);

      expect(gapBadges()).toEqual(['wliczona w czas', 'nie wliczona w czas', 'wliczona w czas']);
    });

    /** Odstęp poniżej progu ciągłości to pauza w pisaniu — zostaje szary i bez opisu. */
    it('krótki odstęp między zapisami zostaje bez opisu', async () => {
      await render(MERGED_ENTRY, 'Umowa_NovaTech.docx', SPAN);

      const rows = Array.from(element().querySelectorAll('.gap-row'));
      expect(rows).toHaveLength(4);
      expect(rows[0].classList).not.toContain('break');
      expect(rows[0].querySelector('.badge')).toBeNull();
    });

    /**
     * Decyzja prawnika bije próg: doliczoną przerwę chronologia ma pokazywać jako
     * wliczoną, inaczej ekran i rozliczenie mówiłyby dwie różne rzeczy.
     */
    it('lista przerw pozycji jest ważniejsza od progu', async () => {
      const claimed: DetectedGap[] = [{
        startAt: '2026-08-11T09:20:00',
        endAt: '2026-08-11T10:00:00',
        minutes: 40,
        counted: true,
      }];
      await render(MERGED_ENTRY, 'Umowa_NovaTech.docx', { ...SPAN, gaps: claimed });

      expect(gapBadges()).toEqual(['wliczona w czas', 'wliczona w czas', 'wliczona w czas']);
    });

    /** Bez kontekstu pozycji (chronologia z osi czasu) o rozliczeniu nie wiemy nic. */
    it('bez zakresu pozycji przerwa jest wyróżniona, ale bez stanu', async () => {
      await render(MERGED_ENTRY);

      expect(gapBadges()).toEqual([]);
      const breaks = Array.from(element().querySelectorAll('.gap-row'))
        .filter((row) => row.classList.contains('break'));
      expect(breaks).toHaveLength(3);
    });
  });

  /**
   * Sesja pracy nie przechodzi przez północ, więc odstęp między dniami nigdy nie jest
   * przerwą w pracy. Nazywany „przerwą", kolorowany jak przestój do rozliczenia i podany
   * w surowych minutach mówił o dwóch dobach bez dotknięcia pliku to samo, co o kwadransie
   * pauzy w pisaniu — a liczbę 3797 trzeba było przeliczać w głowie.
   */
  describe('granica dnia w chronologii', () => {
    /** Dwie edycje tego samego pliku: 10.08 w nocy i 12.08 po południu. */
    const TWO_DAYS: DocumentActivityItem[] = [
      version({ versionId: '1.0', occurredAt: '2026-08-10T01:33:55' }),
      version({ versionId: '2.0', occurredAt: '2026-08-10T01:34:46', gapMinutesSincePrevious: 1 }),
      version({
        versionId: '3.0', occurredAt: '2026-08-12T16:51:38',
        gapMinutesSincePrevious: 3797, isSessionBreak: true, splitsSession: true,
      }),
      version({
        versionId: '4.0', occurredAt: '2026-08-12T16:53:33',
        gapMinutesSincePrevious: 2, isCurrent: true,
      }),
    ];

    const TWO_DAYS_SESSIONS: DocumentSession[] = [
      session('pending', '2026-08-10T01:33:55', '2026-08-10T01:34:46', { suggestionId: 1 }),
      session('pending', '2026-08-12T16:51:38', '2026-08-12T16:53:33', { suggestionId: 2 }),
    ];

    it('odstęp między dniami nie jest przerwą: ani słowem, ani plakietką stanu', async () => {
      await render(TWO_DAYS, 'Umowa_NovaTech.docx', { sessions: TWO_DAYS_SESSIONS, suggestionId: 2 });

      expect(element().querySelector('.day-row')?.textContent)
        .toContain('nowy dzień · 2 dni 15 godz. bez zapisu');
      expect(element().textContent).not.toContain('3797');
      expect(element().querySelectorAll('.day-row .badge')).toHaveLength(0);
      // Odstępy wewnątrz dnia zostają zwykłymi wierszami przerw.
      expect(element().querySelectorAll('.gap-row')).toHaveLength(2);
    });

    /** Przestój w obrębie jednego dnia to nadal decyzja do podjęcia — z kolorem i opisem. */
    it('długi odstęp w jednym dniu zostaje przerwą, tylko liczoną po ludzku', async () => {
      await render([
        version({ versionId: '1.0', occurredAt: '2026-08-12T09:00:00' }),
        version({
          versionId: '2.0', occurredAt: '2026-08-12T10:52:00',
          gapMinutesSincePrevious: 112, isSessionBreak: true, splitsSession: true,
        }),
      ], 'Umowa_NovaTech.docx', {
        sessions: [session('unsettled', '2026-08-12T09:00:00', '2026-08-12T10:52:00', { timeEntryId: 1 })],
        entryId: 1,
      });

      expect(element().querySelectorAll('.day-row')).toHaveLength(0);
      const gap = element().querySelector('.gap-row')!;
      expect(gap.textContent).toContain('przerwa 1 godz. 52 min');
      expect(gap.classList).toContain('break');
    });
  });

  describe('lista zmian', () => {
    async function showDiff(changes: LineChange[]): Promise<void> {
      await render(OPEN_VERSION_HISTORY);
      compareMock.mockResolvedValue(changes);
      compareButtons()[0].click();
      await fixture.whenStable();
      fixture.detectChanges();
    }

    function changeRows(): HTMLElement[] {
      return Array.from(element().querySelectorAll('.change'));
    }

    function expandButton(): HTMLButtonElement {
      return element().querySelector('.expand-btn')!;
    }

    it('seria enterów zajmuje jeden wiersz z liczbą, nie tyle wierszy, ile enterów', async () => {
      await showDiff([
        { kind: 'added', text: '', emptyLines: 4 },
        { kind: 'added', text: 'Nowa linia' },
      ]);

      const rows = changeRows();
      expect(rows).toHaveLength(2);
      expect(rows[0].textContent).toContain('(4 puste linie)');
    });

    it('pojedyncza pusta linia zostaje przy krótkiej etykiecie', async () => {
      await showDiff([{ kind: 'removed', text: '', emptyLines: 1 }]);

      expect(changeRows()[0].textContent).toContain('(pusta linia)');
    });

    /**
     * Każda linia dostaje własny przycisk. Wcześniej wielolinijkowy blok był jedną
     * pozycją: jeden przycisk gdzieś na górze, licznik znaków dla całego bloku
     * i wielokropek ucinający w przypadkowym miejscu w środku.
     */
    it('każda długa linia ma własny przycisk z własnym licznikiem znaków', async () => {
      await showDiff([
        { kind: 'added', text: 'a'.repeat(310) },
        { kind: 'added', text: 'Krótka linia' },
        { kind: 'added', text: 'b'.repeat(450) },
      ]);

      const labels = Array.from(element().querySelectorAll('.expand-btn'))
        .map((button) => button.textContent?.trim());
      expect(labels).toEqual(['Pokaż całość (310 znaków)', 'Pokaż całość (450 znaków)']);
    });

    /**
     * Wielokropek na końcu akapitu wygląda jak uszkodzone dane, dopóki nie powie,
     * ile znaków chowa. Przycisk działa w obie strony — długi akapit da się zwinąć.
     */
    it('skrócony akapit mówi, ile znaków chowa, i rozwija się w obie strony', async () => {
      const long = '1'.repeat(420);
      await showDiff([{ kind: 'added', text: long }]);

      expect(changeRows()[0].textContent).toContain('…');
      expect(expandButton().textContent?.trim()).toBe('Pokaż całość (420 znaków)');

      expandButton().click();
      fixture.detectChanges();
      expect(changeRows()[0].textContent).toContain(long);
      expect(expandButton().textContent?.trim()).toBe('Zwiń');

      expandButton().click();
      fixture.detectChanges();
      expect(changeRows()[0].textContent).not.toContain(long);
      expect(expandButton().textContent?.trim()).toBe('Pokaż całość (420 znaków)');
    });

    it('krótki akapit nie dostaje ani wielokropka, ani przycisku', async () => {
      await showDiff([{ kind: 'added', text: 'Krótki akapit' }]);

      expect(element().querySelectorAll('.expand-btn')).toHaveLength(0);
      expect(changeRows()[0].textContent).not.toContain('…');
    });
  });
});

describe('formatGapSpan', () => {
  it('poniżej doby liczy tak samo jak czas pracy', () => {
    expect(formatGapSpan(45)).toBe('45 min');
    expect(formatGapSpan(112)).toBe('1 godz. 52 min');
  });

  /** Przy takiej skali minuty są szumem: chodzi o to, że plik leżał kilka dni. */
  it('od doby w górę liczy w dniach i pomija minuty', () => {
    expect(formatGapSpan(1440)).toBe('1 dzień');
    expect(formatGapSpan(3797)).toBe('2 dni 15 godz.');
    expect(formatGapSpan(7200)).toBe('5 dni');
  });
});
