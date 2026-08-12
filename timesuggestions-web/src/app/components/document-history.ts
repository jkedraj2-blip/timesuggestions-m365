import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { ApiService } from '../services/api.service';
import { toUserMessage } from '../services/user-message';
import {
  DocxDiffService,
  LineChange,
  VersionUnavailableError,
  isDiffableDocument,
} from '../services/docx-diff';
import {
  DetectedGap,
  DocumentActivityItem,
  DocumentSession,
  DocumentSessionKind,
} from '../models/api.models';
import { polishPlural } from '../pipes/polish-plural';
import { formatDuration } from '../pipes/duration.pipe';

/** Powyżej tylu znaków linia jest przycinana z przyciskiem rozwinięcia. */
const LINE_PREVIEW_CHARS = 300;

/** Zastępnik dla linii bez tekstu — patrz display(). */
const EMPTY_LINE_LABEL = '(pusta linia)';

/**
 * Nazwa stanu w plakietce. Zielony to rzecz zamknięta (rozliczona), bursztyn to rzecz
 * do zrobienia (wpis czeka na rozliczenie) — spójnie z resztą aplikacji, gdzie plakietka
 * ostrzegawcza znaczy „masz tu coś do zrobienia". Bez „ten"/„ta": historia pokazuje
 * WSZYSTKIE pozycje pliku naraz, więc zaimek wskazujący pasowałby tylko do jednej,
 * a oglądaną poznaje się po osobnej plakietce.
 */
const SESSION_LABELS: Record<DocumentSessionKind, string> = {
  pending: 'sugestia oczekująca',
  rejected: 'sugestia odrzucona',
  archived: 'sugestia w archiwum',
  unsettled: 'wpis do rozliczenia',
  settled: 'wpis rozliczony',
};

export function sessionKindLabel(kind: DocumentSessionKind): string {
  return SESSION_LABELS[kind];
}

/** Moment z osi biznesowej na liczbę; null przy braku wartości albo nieparsowalnej dacie. */
function toTimestamp(value: string | null): number | null {
  if (value === null) {
    return null;
  }
  const time = new Date(value).getTime();
  return Number.isNaN(time) ? null : time;
}

/**
 * Co przerwa znaczy dla rozliczenia. „outside" to przerwa poza wszystkimi pozycjami
 * tego pliku: nie zmienia niczyjego czasu i nie wolno o niej twierdzić nic więcej,
 * bo prawnik mógł w tym czasie robić coś, o czym historia tego pliku nie wie.
 */
export type GapState = 'counted' | 'uncounted' | 'outside';

const GAP_STATE_LABELS: Record<GapState, string> = {
  counted: 'wliczona w czas',
  uncounted: 'nie wliczona w czas',
  outside: 'poza pozycjami',
};

const GAP_STATE_HINTS: Record<GapState, string> = {
  counted: 'Te minuty siedzą w czasie tej pozycji, choć nikt wtedy nie zapisał pliku.'
    + ' We wpisie czasu możesz je odjąć jednym kliknięciem.',
  uncounted: 'Te minuty leżą w godzinach tej pozycji, ale nie są rozliczane (dzielą dwie'
    + ' sesje pracy). We wpisie czasu możesz je doliczyć jednym kliknięciem.',
  outside: 'Ta przerwa nie należy do żadnej pozycji z tego pliku, więc nie zmienia'
    + ' niczyjego czasu. Mogła być pracą nad czymś innym.',
};

export function gapStateLabel(state: GapState): string {
  return GAP_STATE_LABELS[state];
}

const MINUTES_PER_DAY = 24 * 60;

/**
 * Odstęp między zapisami po ludzku. Chronologia jednego pliku potrafi mieć dziurę
 * wielodniową (plik leżał tydzień), a surowe minuty przestają wtedy cokolwiek znaczyć:
 * „przerwa 3797 min" trzeba przeliczać w głowie, żeby zobaczyć, że to dwa i pół dnia.
 * Od doby w górę minuty są POMIJANE — przy takiej skali są szumem, a nie precyzją.
 */
export function formatGapSpan(minutes: number): string {
  if (minutes < MINUTES_PER_DAY) {
    return formatDuration(minutes);
  }

  const days = Math.floor(minutes / MINUTES_PER_DAY);
  const hours = Math.floor((minutes % MINUTES_PER_DAY) / 60);
  const dayLabel = `${days} ${polishPlural(days, 'dzień', 'dni', 'dni')}`;
  return hours === 0 ? dayLabel : `${dayLabel} ${hours} godz.`;
}

/**
 * Czy między dwoma zapisami zmienił się dzień. Sesja pracy nigdy nie przechodzi przez
 * północ (granica dnia tnie sesje w silniku), więc taki odstęp NIGDY nie jest przerwą
 * w żadnej pozycji — nazywanie go „przerwą", kolorowanie jak przestój do rozliczenia
 * i przypinanie plakietki stanu mówiło o dwóch dobach bez dotknięcia pliku to samo,
 * co o kwadransie przerwy w pisaniu.
 */
function startsNewDay(previous: DocumentActivityItem, item: DocumentActivityItem): boolean {
  const from = new Date(previous.occurredAt);
  const to = new Date(item.occurredAt);
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return false;
  }
  return from.toDateString() !== to.toDateString();
}

/**
 * Wiersz chronologii razem z pozycją, do której należy. Jeden plik daje zwykle kilka
 * pozycji w RÓŻNYCH stanach naraz (rozliczony wpis, wpis do rozliczenia, sugestia
 * czekająca na decyzję), więc każdy wiersz niesie stan swojej własnej pozycji, a nie
 * stan tej, z której akurat otwarto historię.
 */
interface HistoryRow extends DocumentActivityItem {
  session: DocumentSession | null;
  /** Oglądana pozycja — ta, z której karty otwarto chronologię. */
  isCurrentSession: boolean;
  inSession: boolean;
  opensSession: boolean;
  closesSession: boolean;
  /** Przerwa nad tym wierszem leży w całości wewnątrz jednej pozycji. */
  gapInSession: boolean;
  /** Przerwa nad wierszem jest przestojem, a nie pauzą w pisaniu — wtedy ją opisujemy. */
  isBreak: boolean;
  /** Nad wierszem zaczyna się nowy dzień — odstęp nie jest przerwą w pracy, tylko granicą dnia. */
  startsNewDay: boolean;
  /** Stan przerwy nad wierszem; null, gdy odstęp jest za krótki, żeby o nim mówić. */
  gapState: GapState | null;
}

/**
 * Stan przerwy nad wierszem. Pierwszeństwo ma lista przerw pozycji, do której przerwa
 * należy: to ona zna decyzje prawnika (doliczoną przerwę, odjętą przerwę), a próg jest
 * tylko regułą domyślną. Gdy pozycja swojej listy nie ma (np. sugestia sprzed scalenia),
 * rozstrzyga ten sam próg, którym silnik tnie sesje — przerwa dzieląca sesje nie jest
 * rozliczana, krótsza siedzi w czasie brutto i jest.
 */
function gapStateOf(
  item: DocumentActivityItem,
  previous: DocumentActivityItem,
  session: DocumentSession | null,
  gaps: readonly DetectedGap[],
): GapState | null {
  if (!item.isSessionBreak) {
    return null;
  }
  if (session === null) {
    return 'outside';
  }

  const startAt = toTimestamp(previous.occurredAt);
  const endAt = toTimestamp(item.occurredAt);
  const match = gaps.find(
    (gap) => toTimestamp(gap.startAt) === startAt && toTimestamp(gap.endAt) === endAt,
  );
  if (match) {
    return match.counted ? 'counted' : 'uncounted';
  }
  return item.splitsSession ? 'uncounted' : 'counted';
}

/**
 * Poziom 3 osi czasu: chronologia modyfikacji dokumentu (z dziennika DocumentActivity)
 * i diff dwóch sąsiednich wersji liczony w przeglądarce. Diff tylko dla .docx;
 * .doc (binarny) i arkusze Excela dostają chronologię z komunikatem zamiast parsowania.
 */
@Component({
  selector: 'app-document-history',
  imports: [DatePipe, DecimalPipe],
  template: `
    <div class="history">
      @if (error()) {
        <div class="error-box">{{ error() }}</div>
      }

      @if (loading()) {
        <p class="empty-state">Ładowanie historii wersji…</p>
      } @else if (activity().length === 0) {
        <!-- Brak wersji to stan normalny (retencja OneDrive), nie błąd. -->
        <p class="empty-state">Brak zapisanej historii wersji tego pliku.</p>
      } @else {
        @if (!diffable()) {
          <p class="text-muted note">Podgląd zmian dostępny tylko dla plików .docx.</p>
        }

        @if (hasSession()) {
          <!-- Jeden plik daje zwykle kilka pozycji w RÓŻNYCH stanach naraz. Wcześniej
               stan podawał ten, kto historię otwierał, więc ta sama chronologia mówiła
               co innego w archiwum, a co innego w sugestiach. Teraz każdy obszar niesie
               swój własny stan, a oglądaną pozycję widać po osobnej plakietce. -->
          <p class="text-muted note">
            Podświetlone obszary to zapisy, z których policzono pozycje tego pliku;
            plakietka i kolor mówią, w jakim stanie jest każda z nich. Pozycja, z której
            tu wszedłeś, ma plakietkę <span class="badge badge-accent">ta pozycja</span>.
            Przy każdej przerwie widać, czy jej minuty są wliczone w czas.
          </p>
        }

        <ol class="versions">
          <!-- Klucz z numeru wersji I momentu: numer sam w sobie NIE jest unikalny —
               wersja jeszcze niezapieczętowana pojawia się w dzienniku wielokrotnie,
               a zdublowane klucze track sprawiają, że Angular nie umie odróżnić wierszy. -->
          @for (version of rows(); track version.versionId + '@' + version.occurredAt; let index = $index) {
            @if (version.gapMinutesSincePrevious; as gap) {
              @if (version.startsNewDay) {
                <!-- Granica dnia zamiast przerwy: sesja nigdy nie przechodzi przez północ,
                     więc taki odstęp nie należy do żadnej pozycji i nie ma o czym przy nim
                     decydować. Wcześniej dostawał to samo słowo, kolor i plakietkę co
                     kwadrans przerwy w pisaniu, w dodatku w surowych minutach — dwie doby
                     bez dotknięcia pliku czytało się jako „przerwa 3797 min". -->
                <li class="day-row" [title]="dayGapHint">
                  <span aria-hidden="true">⌄</span>
                  <span>{{ dayGapLabel(gap) }}</span>
                </li>
              } @else {
                <!-- Przestój dostaje kolor i opis NIEZALEŻNIE od długości. Wcześniej opisana
                     była wyłącznie przerwa z przedziału 15–30 min, więc dwugodzinna wyglądała
                     na ekranie jak zwykły odstęp między zapisami — czyli najważniejsza
                     pozycja do rozliczenia była tą jedyną bez wyjaśnienia. -->
                <li
                  class="gap-row"
                  [class]="version.session ? 'kind-' + version.session.kind : ''"
                  [class.break]="version.isBreak"
                  [class.in-session]="version.gapInSession"
                  [class.current-session]="version.gapInSession && version.isCurrentSession"
                >
                  <span aria-hidden="true">⋮</span>
                  <span>przerwa {{ gapSpan(gap) }}</span>
                  @if (version.gapState; as state) {
                    <span
                      class="badge"
                      [class.badge-accent]="state === 'uncounted'"
                      [class.badge-neutral]="state !== 'uncounted'"
                      [title]="gapStateHint(state)"
                    >{{ gapStateLabel(state) }}</span>
                  }
                </li>
              }
            }
            <li
              class="version-row"
              [class]="version.session ? 'kind-' + version.session.kind : ''"
              [class.in-session]="version.inSession"
              [class.current-session]="version.isCurrentSession"
              [class.opens-session]="version.opensSession"
              [class.closes-session]="version.closesSession"
            >
              <!-- Wiersz w siatce: godzina, data i rozmiar stoją w stałych kolumnach,
                   więc chronologia czyta się w pionie. Wyjaśnienia i przyciski mają
                   własne miejsce zamiast wpychać się między liczby. -->
              <div class="version-line">
                <span class="version-time">{{ version.occurredAt | date: 'HH:mm:ss' }}</span>
                <span class="version-date text-muted">{{ version.occurredAt | date: 'dd.MM' }}</span>
                <span class="version-size text-muted">{{ version.size / 1024 | number: '1.0-1' }} KB</span>
                <span class="version-marks">
                  @if (version.opensSession && version.session; as session) {
                    <!-- Etykieta tylko przy pierwszym wierszu obszaru: powtórzona przy każdym
                         byłaby szumem, a tło i tak pokazuje, dokąd zaznaczenie sięga.
                         Numer edycji obok stanu, bo to ten sam numer, który pozycja nosi
                         na swojej karcie — inaczej trzeba by je kojarzyć po godzinach. -->
                    <span class="session-badge">{{ sessionBadge(session) }}</span>
                    @if (version.isCurrentSession) {
                      <span class="badge badge-accent" title="Pozycja, z której otwarto tę historię.">ta pozycja</span>
                    }
                  }
                  @if (version.isCurrent && $last) {
                    <!-- Plakietka tylko przy najnowszym zapisie, choć znacznik isCurrent
                         niesie każda próbka tej wersji — to informacja dla użytkownika,
                         nie powtarzany trzy razy stan techniczny. Treścią bieżącej wersji
                         jest aktualna treść pliku, pobierana z innego adresu niż wersje
                         historyczne, więc porównanie z nią działa. -->
                    <span class="badge badge-neutral" title="Najnowszy znany zapis, czyli aktualna treść pliku.">bieżąca</span>
                  }
                </span>
                @if (diffable() && index > 0 && !version.isSameVersionAsPrevious) {
                  <button
                    class="btn btn-ghost compare-btn"
                    (click)="compare(index)"
                    [disabled]="comparingIndex() !== null"
                  >
                    Porównaj z poprzednią
                  </button>
                }
              </div>
              @if (diffable() && index > 0 && version.isSameVersionAsPrevious) {
                <!-- Nie ma czego porównać: OneDrive trzyma jeden snapshot na numer
                     wersji, a te dwa wiersze to dwie próbki TEJ SAMEJ wersji. Zamiast
                     przycisku kończącego się pustym wynikiem albo błędem — zdanie,
                     które to tłumaczy, ORAZ wskazanie, gdzie te zmiany widać:
                     samo „brak stanu pośredniego" zostawiało prawnika z wrażeniem,
                     że praca z tych minut przepadła. Wyjaśnienie idzie pod wiersz:
                     wciśnięte w kolumnę obok liczb łamało się na kilka linijek
                     i przykrywało samą chronologię. -->
                <p class="same-version text-muted" [title]="sameVersionHint(index)">
                  {{ sameVersionNote(index) }}
                </p>
              }
            </li>
            @if (comparingIndex() === index) {
              <li class="diff-panel">
                <span>Porównuję wersje…</span>
                <button class="btn btn-ghost" (click)="cancelCompare()">Anuluj</button>
              </li>
            }
            @if (diffIndex() === index) {
              <li class="diff-panel">
                @if (diffResult(); as changes) {
                  @if (changes.length === 0) {
                    <p class="text-muted">Brak różnic tekstowych: zmiany dotyczyły formatowania.</p>
                  } @else {
                    <ul class="changes">
                      @for (change of changes; track $index) {
                        <!-- Prefiks +/−/~ obok koloru — zmiana nigdy nie jest komunikowana samym kolorem. -->
                        <li class="change change-{{ change.kind }}">
                          <span class="change-prefix" aria-hidden="true">{{ prefix(change) }}</span>
                          <span class="change-text">
                            @if (change.emptyLines) {
                              <!-- Seria enterów jako jedna pozycja z liczbą — kilkanaście
                                   wierszy "(pusta linia)" pod rząd nie niosło nic ponad to. -->
                              <em class="text-muted">{{ emptyLinesLabel(change.emptyLines) }}</em>
                            } @else {
                              @if (change.kind === 'changed' && change.previousText) {
                                <s class="text-muted">{{ display(change.previousText, $index * 2 + 1) }}</s>
                                →
                              }
                              {{ display(change.text, $index * 2) }}
                            }
                          </span>
                          @if (isLong(change)) {
                            <!-- Etykieta mówi WPROST, ile znaków chowa wielokropek: samo "…"
                                 wyglądało jak uszkodzone dane. Przycisk działa w obie strony,
                                 żeby rozwinięty akapit dało się z powrotem schować. -->
                            <button class="btn btn-ghost expand-btn" (click)="toggleExpanded($index)">
                              {{ expandLabel(change, $index) }}
                            </button>
                          }
                        </li>
                      }
                    </ul>
                  }
                }
              </li>
            }
          }
        </ol>
      }
    </div>
  `,
  styles: `
    .history { padding: var(--space-2) 0 var(--space-2) var(--space-4); }
    .note { margin: 0 0 var(--space-2); font-size: var(--font-size-sm); }
    .versions { list-style: none; margin: 0; padding: 0; font-size: var(--font-size-sm); }
    .version-row { padding: var(--space-1) 0; }
    /* Stałe kolumny na liczby: godzina, data i rozmiar mają tę samą szerokość w każdym
       wierszu, więc listę czyta się w pionie. Reszta wiersza (plakietki) rozpycha się
       w kolumnie elastycznej, a przycisk porównania stoi zawsze na tym samym końcu. */
    .version-line {
      display: grid;
      grid-template-columns: 5.5rem 3rem 5rem minmax(0, 1fr) auto;
      align-items: center;
      gap: var(--space-2);
    }
    .version-time { font-variant-numeric: tabular-nums; font-weight: 600; }
    .version-date, .version-size { font-variant-numeric: tabular-nums; }
    .version-marks { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; min-width: 0; }
    .compare-btn, .expand-btn { padding: 0 var(--space-2); font-size: var(--font-size-sm); }
    /* Etykiety przycisków muszą zostać w jednej linii — łamane w wąskiej kolumnie
       wyglądały jak druga, rozsypana kolumna tekstu. */
    .compare-btn, .expand-btn { white-space: nowrap; flex-shrink: 0; }
    /* Wyjaśnienie pod wierszem, wcięte do kolumny z godziną: należy do tego zapisu,
       ale nie konkuruje z liczbami o miejsce w wierszu. */
    .same-version {
      margin: 0 0 0 5.5rem;
      font-size: var(--font-size-sm);
      opacity: 0.8;
    }
    .gap-row {
      display: flex; align-items: center; gap: var(--space-2);
      color: var(--text-muted); padding: 2px 0 2px var(--space-2);
    }
    /* Przestój wyróżniony kolorem niezależnie od długości: to pozycja, o której trzeba
       podjąć decyzję, a nie odstęp między zapisami. */
    .gap-row.break { color: var(--accent); }
    /* Granica dnia jako cicha kreska: ma rozdzielać chronologię, a nie konkurować
       o uwagę z przerwami, przy których jest co zrobić. */
    .day-row {
      display: flex; align-items: center; gap: var(--space-2);
      color: var(--text-muted); padding: var(--space-2) 0 var(--space-1) var(--space-2);
      margin-top: var(--space-2); border-top: 1px dashed var(--border);
    }
    /* Kolor stanu ustawiany na WIERSZU, nie na kontenerze: jeden plik ma zwykle kilka
       pozycji w różnych stanach naraz, a wcześniej cała chronologia brała barwę od tej
       jednej, z której ją otwarto. Zieleń to rzecz zamknięta (rozliczona), bursztyn to
       rzecz do zrobienia, czerwień to odrzucenie, szarość to archiwum. */
    .kind-pending { --session-color: var(--accent); --session-fill: var(--accent-soft); }
    .kind-rejected { --session-color: var(--danger); --session-fill: var(--danger-soft); }
    .kind-archived { --session-color: var(--border); --session-fill: var(--surface-alt); }
    .kind-unsettled { --session-color: var(--warn); --session-fill: var(--warn-soft); }
    .kind-settled { --session-color: var(--success); --session-fill: var(--success-soft); }

    /* Ciągły pasek i tło przez wszystkie wiersze jednej sesji. Sąsiednie elementy listy
       stykają się, więc z osobnych wierszy powstaje jeden blok — zaokrąglamy tylko jego
       początek i koniec. Zaznaczenie kolorem TŁA, nie samym tekstem: chodzi o zasięg,
       a nie o wyróżnienie pojedynczej wartości. */
    .in-session {
      border-left: 3px solid var(--session-color);
      background: var(--session-fill);
      padding-left: var(--space-3);
    }
    /* Oglądana pozycja grubszym paskiem — obok plakietki „ta pozycja", nigdy zamiast niej. */
    .current-session.in-session { border-left-width: 6px; padding-left: calc(var(--space-3) - 3px); }
    .opens-session { border-top-left-radius: var(--radius-sm); border-top-right-radius: var(--radius-sm); }
    .closes-session { border-bottom-left-radius: var(--radius-sm); border-bottom-right-radius: var(--radius-sm); }
    .session-badge {
      color: var(--session-color); border: 1px solid var(--session-color);
      border-radius: 999px; padding: 0 var(--space-2);
      font-size: var(--font-size-sm); white-space: nowrap;
    }
    .diff-panel {
      display: flex; align-items: center; gap: var(--space-3); flex-wrap: wrap;
      background: var(--surface-alt); border-radius: var(--radius-sm);
      padding: var(--space-2) var(--space-3); margin: var(--space-1) 0;
    }
    .changes { list-style: none; margin: 0; padding: 0; width: 100%; }
    .change { display: flex; gap: var(--space-2); padding: var(--space-1) 0; align-items: baseline; }
    .change-prefix { font-family: monospace; font-weight: 700; }
    /* Tokeny motywów zamiast gołych kolorów — czytelne również w ciemnym motywie. */
    .change-added { color: var(--success); }
    .change-removed { color: var(--warn-strong); }
    .change-changed { color: var(--accent); }
    .change-text { white-space: pre-wrap; overflow-wrap: anywhere; }
  `,
})
export class DocumentHistory implements OnInit {
  private api = inject(ApiService);
  private docxDiff = inject(DocxDiffService);

  /** Id pliku z Graph — po nim pobierana jest chronologia i treść wersji. */
  externalId = input.required<string>();

  /** Nazwa pliku — decyduje, czy diff jest dostępny (tylko .docx). */
  fileName = input<string | null>(null);

  /**
   * Pozycja, z której otwarto chronologię — rozpoznawana po IDENTYFIKATORZE, nie po
   * zakresie godzin. Zakres bywa przycięty do sąsiada albo zmieniony korektą, a wtedy
   * porównywanie godzin wskazywałoby raz tę pozycję, raz żadnej. Null z obu stron
   * (widok osi czasu) znaczy tyle, że żadna pozycja nie jest „ta".
   */
  currentSuggestionId = input<number | null>(null);
  currentEntryId = input<number | null>(null);

  /**
   * Przerwy oglądanej pozycji razem z ich stanem — nadpisują listę z serwera dla TEJ
   * jednej pozycji. Rodzic ma je świeższe: po doliczeniu przerwy lista wpisów ładuje
   * się od nowa, a otwarta chronologia nie jest pobierana drugi raz.
   */
  sessionGaps = input<readonly DetectedGap[]>([]);

  protected activity = signal<DocumentActivityItem[]>([]);
  protected sessions = signal<DocumentSession[]>([]);
  protected loading = signal(false);
  protected error = signal<string | null>(null);

  /** Indeks wersji porównywanej w tej chwili (spinner + Anuluj); null = nic nie trwa. */
  protected comparingIndex = signal<number | null>(null);
  protected diffIndex = signal<number | null>(null);
  protected diffResult = signal<LineChange[] | null>(null);
  private expandedChanges = signal<ReadonlySet<number>>(new Set());
  private abortController: AbortController | null = null;

  protected diffable = signal(false);

  /**
   * Chronologia wzbogacona o pozycję, do której należy każdy zapis. Przypisanie liczone
   * tu, a nie w szablonie, bo wiersz musi wiedzieć nie tylko „do której pozycji należy",
   * ale też czy ją otwiera i zamyka — z tego robi się widoczna klamra zamiast paska
   * przy każdym wierszu z osobna.
   */
  protected rows = computed<HistoryRow[]>(() => {
    const items = this.activity();
    const sessions = this.sessions();
    const current = this.currentSession();
    const currentGaps = this.sessionGaps();

    // Pozycje nie nachodzą na siebie (niezmiennik rozliczania), więc pierwsza pasująca
    // jest jedyną pasującą; przy uszkodzonych danych wygrywa wcześniejsza z listy.
    const owners = items.map((item) => {
      const at = toTimestamp(item.occurredAt);
      if (at === null) {
        return null;
      }
      return sessions.find((session) => {
        const from = toTimestamp(session.startAt);
        const to = toTimestamp(session.endAt);
        return from !== null && to !== null && at >= from && at <= to;
      }) ?? null;
    });

    return items.map((item, index) => {
      const session = owners[index];
      const gapInSession = index > 0 && session !== null && owners[index - 1] === session;
      const gapOwnerGaps = session === current ? currentGaps : session?.gaps ?? [];
      const newDay = index > 0 && startsNewDay(items[index - 1], item);
      return {
        ...item,
        session,
        isCurrentSession: session !== null && session === current,
        inSession: session !== null,
        gapInSession,
        opensSession: session !== null && owners[index - 1] !== session,
        closesSession: session !== null && owners[index + 1] !== session,
        startsNewDay: newDay,
        isBreak: index > 0 && item.isSessionBreak && !newDay,
        // Plik bez żadnej pozycji (nic jeszcze nie zatwierdzono, nic nie czeka) nie ma
        // czego przypisać przerwie — wtedy zostaje sam kolor przestoju, bez twierdzeń.
        // Granica dnia też nie dostaje stanu: nie leży w żadnej pozycji z definicji.
        gapState: index === 0 || sessions.length === 0 || newDay
          ? null
          : gapStateOf(item, items[index - 1], gapInSession ? session : null, gapOwnerGaps),
      };
    });
  });

  /** Pozycja, z której otwarto chronologię; null w widoku osi czasu. */
  private currentSession = computed<DocumentSession | null>(() => {
    const suggestionId = this.currentSuggestionId();
    const entryId = this.currentEntryId();
    return this.sessions().find((session) =>
      (suggestionId !== null && session.suggestionId === suggestionId)
      || (entryId !== null && session.timeEntryId === entryId)) ?? null;
  });

  /** Czy w ogóle jest co zaznaczać — plik bez pozycji to sama chronologia zapisów. */
  protected hasSession = computed(() => this.rows().some((row) => row.inSession));

  async ngOnInit(): Promise<void> {
    this.diffable.set(isDiffableDocument(this.fileName()));
    this.loading.set(true);
    try {
      const history = await this.api.getDocumentHistory(this.externalId());
      this.activity.set(history.versions);
      this.sessions.set(history.sessions);
    } catch (error) {
      this.error.set(toUserMessage(error, 'Nie udało się pobrać historii wersji.'));
    } finally {
      this.loading.set(false);
    }
  }

  protected readonly kindLabel = sessionKindLabel;

  /** „edycja 3, wpis rozliczony" — numer razem ze stanem, jak na karcie pozycji. */
  protected sessionBadge(session: DocumentSession): string {
    const kind = sessionKindLabel(session.kind);
    return session.label === null ? kind : `${session.label}, ${kind}`;
  }

  /** Porównanie wersji index-1 → index. Każde kliknięcie = 2 pobrania pliku. */
  protected async compare(index: number): Promise<void> {
    const versions = this.activity();
    const older = versions[index - 1];
    const newer = versions[index];
    if (!older || !newer) {
      return;
    }

    this.error.set(null);
    this.diffIndex.set(null);
    this.diffResult.set(null);
    this.expandedChanges.set(new Set());
    this.comparingIndex.set(index);
    this.abortController = new AbortController();

    try {
      const changes = await this.docxDiff.compareVersions(
        this.externalId(),
        { versionId: older.versionId, isCurrent: older.isCurrent },
        { versionId: newer.versionId, isCurrent: newer.isCurrent },
        this.abortController.signal,
      );
      this.diffResult.set(changes);
      this.diffIndex.set(index);
    } catch (error) {
      if (this.abortController?.signal.aborted) {
        return; // anulowane świadomie — bez komunikatu błędu
      }
      const message = error instanceof VersionUnavailableError
        ? error.message
        : toUserMessage(error, 'Nie udało się porównać wersji.');
      this.error.set(message);
    } finally {
      this.comparingIndex.set(null);
      this.abortController = null;
    }
  }

  protected cancelCompare(): void {
    this.abortController?.abort();
  }

  /**
   * Wiersz będący kolejną próbką tej samej wersji: mówimy nie tylko, czego NIE ma,
   * ale też gdzie efekt tych zapisów zobaczyć. Porównanie stoi przy PIERWSZYM wierszu
   * tej wersji, a jego wynik obejmuje całą pracę wykonaną w tej wersji — także tę
   * z późniejszych próbek, bo treścią jest jeden snapshot na numer wersji.
   */
  protected sameVersionHint(index: number): string {
    const base = 'OneDrive przechowuje jeden zapis treści na numer wersji, a ten wiersz to'
      + ' kolejna próbka wersji wciąż otwartej w edytorze — stanu pośredniego nie ma z czym'
      + ' porównać. Praca z tych minut nie przepadła:';
    const target = this.sameVersionTarget(index);
    return target === null
      ? `${base} zobaczysz ją w porównaniu przy następnej wersji.`
      : `${base} zobaczysz ją w porównaniu przy zapisie ${target}.`;
  }

  /**
   * Skrót do wiersza; pełne wyjaśnienie zostaje w dymku. Trzy linijki powtórzone przy
   * każdej próbce otwartej wersji zajmowały więcej miejsca niż sama chronologia.
   */
  protected sameVersionNote(index: number): string {
    const target = this.sameVersionTarget(index);
    return target === null
      ? 'brak osobnego zapisu tej wersji; zmiany zobaczysz przy następnej'
      : `brak osobnego zapisu tej wersji; zmiany zobaczysz w porównaniu przy ${target}`;
  }

  /**
   * Godzina wiersza, przy którym stoi porównanie obejmujące te zmiany: pierwszej próbki
   * tej samej wersji. Null, gdy wersja otwiera dziennik — nie ma wtedy poprzedniczki,
   * więc przycisku nie ma nigdzie i obiecywanie go byłoby odesłaniem donikąd.
   */
  private sameVersionTarget(index: number): string | null {
    const versions = this.activity();
    let first = index;
    while (first > 0 && versions[first - 1].versionId === versions[index].versionId) {
      first--;
    }
    if (first === 0) {
      return null;
    }
    return new Date(versions[first].occurredAt)
      .toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  protected readonly gapStateLabel = gapStateLabel;

  protected readonly gapSpan = formatGapSpan;

  /** „nowy dzień · 2 dni 15 godz. bez zapisu" — fakt, przy którym nie ma decyzji. */
  protected dayGapLabel(minutes: number): string {
    return `nowy dzień · ${formatGapSpan(minutes)} bez zapisu`;
  }

  protected readonly dayGapHint = 'Sesja pracy nie przechodzi przez północ, więc odstęp'
    + ' między dniami nie jest przerwą w żadnej pozycji i nie zmienia niczyjego czasu.';

  protected gapStateHint(state: GapState): string {
    return GAP_STATE_HINTS[state];
  }

  protected prefix(change: LineChange): string {
    switch (change.kind) {
      case 'added':
        return '+';
      case 'removed':
        return '−';
      case 'changed':
        return '~';
    }
  }

  /** Długie linie przycięte z rozwinięciem — klucz łączy indeks zmiany i wariant tekstu. */
  protected display(text: string, key: number): string {
    if (text.length === 0) {
      // Pusta linia po jednej stronie zmiany treści inaczej byłaby niewidzialna —
      // zostawałby goły prefiks bez tekstu, co wygląda jak błąd renderowania.
      // Serie samych enterów idą osobną ścieżką (emptyLines).
      return EMPTY_LINE_LABEL;
    }
    if (text.length <= LINE_PREVIEW_CHARS || this.expandedChanges().has(key)) {
      return text;
    }
    return `${text.slice(0, LINE_PREVIEW_CHARS)}…`;
  }

  /** „(pusta linia)" albo „(3 puste linie)" — jedna pozycja zamiast serii wierszy. */
  protected emptyLinesLabel(count: number): string {
    return count === 1
      ? EMPTY_LINE_LABEL
      : `(${count} ${polishPlural(count, 'pusta linia', 'puste linie', 'pustych linii')})`;
  }

  /**
   * Czy pozycja ma tekst dłuższy niż podgląd. Niezależne od stanu rozwinięcia —
   * przycisk ma zostać na miejscu także po rozwinięciu, żeby dało się zwinąć z powrotem.
   */
  protected isLong(change: LineChange): boolean {
    return change.text.length > LINE_PREVIEW_CHARS
      || (change.previousText?.length ?? 0) > LINE_PREVIEW_CHARS;
  }

  /**
   * Etykieta nazywa liczbę ukrytych znaków. Sam wielokropek na końcu akapitu nie mówi,
   * czy to skrót aplikacji, czy treść dokumentu — a to pierwsze pytanie użytkownika.
   */
  protected expandLabel(change: LineChange, index: number): string {
    if (this.expandedChanges().has(index * 2)) {
      return 'Zwiń';
    }
    const longest = Math.max(change.text.length, change.previousText?.length ?? 0);
    return `Pokaż całość (${longest} ${polishPlural(longest, 'znak', 'znaki', 'znaków')})`;
  }

  protected toggleExpanded(index: number): void {
    this.expandedChanges.update((current) => {
      const next = new Set(current);
      // Oba warianty tekstu (przed i po zmianie) rozwijają się razem — inaczej
      // porównanie pokazywałoby jedną stronę w całości, a drugą uciętą.
      if (next.delete(index * 2)) {
        next.delete(index * 2 + 1);
        return next;
      }
      return next.add(index * 2).add(index * 2 + 1);
    });
  }
}
