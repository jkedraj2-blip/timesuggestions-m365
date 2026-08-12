import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../services/api.service';
import { formatCaseMeta } from '../services/case-label';
import { toUserMessage } from '../services/user-message';
import {
  ApprovePayload,
  CaseInfo,
  Suggestion,
  SuggestionNeighbor,
  TimeEntry,
} from '../models/api.models';
import { DurationPipe, formatDuration } from '../pipes/duration.pipe';
import { polishPlural } from '../pipes/polish-plural';
import { DocumentHistory } from './document-history';

/** Zdarzenie dla rodzica: sugestia rozstrzygnięta — do zdjęcia z listy i pokazania toastu. */
export interface SuggestionResolved {
  suggestion: Suggestion;
  action: 'approved' | 'rejected' | 'restored' | 'archived';
  /** Wpis utworzony przy zatwierdzeniu — potrzebny do akcji "Cofnij". */
  createdEntry?: TimeEntry;
}

/**
 * „10:00–11:20" — odcinek, który sugestia zajmuje na osi dnia: od początku do
 * PÓŹNIEJSZEJ z granic, czyli końca liczonego czasu albo ostatniej znanej zmiany
 * (ta sama reguła co SuggestionSpan po stronie serwera). Po scaleniu te dwie granice
 * to co innego: czas jest sumą sesji, a odcinek sięga końca drugiej z nich, więc
 * godzina wyliczona z samych minut kończyłaby sugestię w środku pracy.
 */
export function formatSpan(suggestion: Suggestion): string {
  const start = new Date(suggestion.startedAt);
  const durationEnd = new Date(start.getTime() + suggestion.durationMinutes * 60_000);
  const lastActivity = new Date(suggestion.lastActivityAt);
  const end = lastActivity.getTime() > durationEnd.getTime() ? lastActivity : durationEnd;
  const time = (date: Date): string =>
    date.toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit' });
  return `${time(start)}–${time(end)}`;
}

/**
 * Komunikat po doliczeniu przerwy. Operacja jest cicha i natychmiastowa — bez zdania
 * „co się właśnie stało" prawnik widzi wyłącznie przeskakujące liczby i nie ma jak
 * sprawdzić, czy kliknął to, co chciał. Delty liczymy z ODPOWIEDZI serwera, a nie
 * z tego, co pokazywał ekran: rozmiar luki przelicza serwer i mógł się zmienić.
 */
export function gapClaimMessage(
  previous: Suggestion,
  changed: Suggestion[],
  neighborTitle: string,
  side: 'before' | 'after',
  neighborMinutes: number,
): string {
  const mine = changed.find((suggestion) => suggestion.id === previous.id);
  // Minuty własne z różnicy czasów w odpowiedzi (przy „Dolicz całość" nikt ich nie podawał);
  // minuty sąsiada wprost z żądania — serwer stosuje dokładnie tę liczbę albo odmawia.
  const mineMinutes = mine ? mine.durationMinutes - previous.durationMinutes : 0;
  const where = side === 'before' ? 'przed' : 'po';
  if (mine === undefined) {
    return `Doliczono ${neighborMinutes} min do „${neighborTitle}", ta sugestia bez zmian.`;
  }

  const nowLine = ` Ta sugestia to teraz ${formatSpan(mine)} (${formatDuration(mine.durationMinutes)}).`;
  return neighborMinutes > 0
    ? `Przerwa podzielona: ${mineMinutes} min tutaj, ${neighborMinutes} min do „${neighborTitle}".${nowLine}`
    : `Doliczono ${mineMinutes} min ${where} tą sesją.${nowLine}`;
}

/**
 * Komunikat po scaleniu: jaki odcinek obejmuje wynik i ile z niego liczymy.
 * Godziny to ZASIĘG (od pierwszego zapisu do ostatniego), a liczba w nawiasie
 * to czas do rozliczenia — po scaleniu bez przerw są to różne wartości i komunikat
 * musi pokazać obie, inaczej prawnik szuka „brakujących" minut.
 */
export function mergeMessage(survivor: Suggestion): string {
  return `Sesje scalone w jedną: „${survivor.title}" ${formatSpan(survivor)}`
    + ` (${formatDuration(survivor.durationMinutes)} do rozliczenia).`
    + ' Przerwa między sesjami nie została doliczona.';
}

/**
 * Zdanie o przerwach sugestii. Powstało z konkretnego braku: przy samym „Zatwierdź"
 * prawnik nie miał skąd wiedzieć, że w tym czasie siedzi wykryta przerwa i że da się ją
 * odjąć po zatwierdzeniu, ani że po scaleniu sesji przerwa MIĘDZY nimi w ogóle nie jest
 * liczona. Obie rzeczy zmieniają kwotę na rachunku, więc muszą być napisane, a nie
 * dostępne wyłącznie w historii wersji dla tego, kto sam wpadnie ją otworzyć.
 */
export function suggestionGapNote(suggestion: Suggestion): string | null {
  const sentences: string[] = [];

  const detected = suggestion.detectedGaps;
  if (detected.length > 0) {
    const total = detected.reduce((sum, gap) => sum + gap.minutes, 0);
    const noun = polishPlural(detected.length, 'przerwa', 'przerwy', 'przerw');
    const verb = detected.length === 1 ? 'jest' : 'są';
    const pronoun = detected.length === 1 ? 'ją' : 'je';
    sentences.push(
      `W tej sesji ${verb} ${detected.length} ${noun} (łącznie ${formatDuration(total)})`
        + ` wliczona w czas pracy. Po zatwierdzeniu możesz ${pronoun} odjąć jednym kliknięciem`
        + ' w zakładce „Wpisy czasu".',
    );
  }

  const spanMinutes = Math.round(
    (new Date(suggestion.lastActivityAt).getTime() - new Date(suggestion.startedAt).getTime()) / 60_000,
  );
  const uncounted = spanMinutes - suggestion.durationMinutes;
  if (uncounted > 0) {
    sentences.push(
      `Ta pozycja obejmuje ${formatDuration(spanMinutes)}, a liczy ${formatDuration(suggestion.durationMinutes)}:`
        + ` ${formatDuration(uncounted)} przerw między sesjami nie jest liczone.`
        + ' Po zatwierdzeniu możesz je doliczyć we wpisie czasu.',
    );
  }

  return sentences.length === 0 ? null : sentences.join(' ');
}

@Component({
  selector: 'app-suggestion-card',
  imports: [DatePipe, FormsModule, DurationPipe, DocumentHistory],
  template: `
    <div class="card" [class.card-review]="needsReview()">
      <div class="card-header">
        <span class="badge badge-neutral">{{ sourceIcon() }} {{ sourceLabel() }}</span>
        <strong class="title">{{ suggestion().title }}</strong>
        @if (suggestion().sessionLabel; as sessionLabel) {
          <!-- Jeden plik daje tyle pozycji, ile było sesji pracy, a wszystkie mają tę samą
               nazwę. Bez numeru lista wygląda jak zduplikowana i nie widać, o którą pracę
               chodzi. -->
          <span
            class="badge badge-neutral"
            title="Kolejna sesja pracy nad tym plikiem, licząc od pierwszej zapisanej w historii wersji. Numeracja biegnie przez wszystkie dni, bo praca nad dokumentem zwykle się na nich rozkłada."
          >{{ sessionLabel }}</span>
        }
        @if (needsReview()) {
          <span class="badge badge-warn">sprawdź to</span>
        }
      </div>

      <!-- Kolejność jest chronologiczna i podpisana: od kiedy liczymy czas, do kiedy
           trwała praca, ile z tego wyszło. Trzy gołe liczby obok siebie nie mówiły,
           co jest czym. -->
      <div class="card-details text-muted">
        <span class="detail">
          <span class="detail-label">początek</span>
          {{ suggestion().startedAt | date: 'dd.MM.yyyy HH:mm' }}
        </span>
        @if (lastActivityLabel(); as lastActivity) {
          <span class="detail" title="Ostatni znany zapis dokumentu. To po nim ustawiona jest kolejność listy sugestii.">
            <span class="detail-label">ostatnia zmiana</span>
            {{ lastActivity }}
          </span>
        }
        <span class="detail">
          <span class="detail-label">czas pracy</span>
          {{ suggestion().durationMinutes | duration }}
        </span>
        @if (suggestion().isUserAdjusted) {
          <span
            class="badge badge-neutral"
            title="Czas poprawiony ręcznie (scalenie sesji albo doliczona przerwa). Synchronizacja już go nie przelicza."
          >czas poprawiony ręcznie</span>
        }
        @if (suggestion().needsTimeReview) {
          <!-- Cała sesja mieści się w jednej minucie: o długości pracy nie wiemy nic,
               więc zamiast podsuwać zgadywaną wartość prosimy o wpisanie własnej. Amber,
               bo to nie informacja, tylko rzecz do zrobienia przed zatwierdzeniem.
               Sesja ZMIERZONA, choćby dwuminutowa, tej plakietki nie dostaje — jej czas
               jest znany i wpisany bez zaokrąglania w górę. -->
          <span
            class="badge badge-warn"
            title="Zapisy tej sesji mieszczą się w jednej minucie, więc z historii wersji nie wynika, jak długo trwała praca. Wpisz czas ręcznie (Edytuj); wartość obok to tylko minimum z konfiguracji."
          >czas do uzupełnienia</span>
        }
      </div>

      @if (suggestion().caseName) {
        <!-- Sprawa w osobnej, podpisanej linii — z tego widoku powstaje rozliczenie,
             a numer sprawy to identyfikator z faktury; plakietka w wierszu metadanych
             była niezauważalna. Zielona plakietka zostaje: sygnalizuje "dopasowano". -->
        <p class="case-line">
          <span class="text-muted">Sprawa:</span>
          <span class="badge badge-success">{{ suggestion().caseName }}</span>
          @if (caseMeta()) {
            <span class="text-muted">· {{ caseMeta() }}</span>
          }
        </p>
      }

      @if (reviewReason()) {
        <p class="review-reason text-warn">{{ reviewReason() }}</p>
      }

      @if (gapNote(); as note) {
        <!-- Przy samym „Zatwierdź" prawnik nie miał skąd wiedzieć, że wykryta przerwa
             siedzi w tym czasie i że da się ją odjąć później. Zdanie mówi to wprost,
             razem z miejscem, w którym się to robi. -->
        <p class="gap-note text-muted">{{ note }}</p>
      }

      @if (sourceConflict()) {
        <!-- Źródło zmieniło się w tle podczas edycji — nie nadpisujemy pracy użytkownika,
             ale nie pozwalamy też zatwierdzić nieświadomie nieaktualnych wartości. -->
        <div class="info-box conflict-box">
          <span>Sugestia została zmieniona podczas synchronizacji.</span>
          <div class="actions">
            <button class="btn" (click)="acceptSourceValues()">Odśwież wartości</button>
            <button class="btn" (click)="keepMyValues()">Zachowaj moje</button>
          </div>
        </div>
      }

      @if (isArchived()) {
        <!-- Archiwum jest tylko do odczytu: bez Przywróć i bez Archiwizuj (stan terminalny). -->
      } @else if (isRejected()) {
        <div class="actions">
          <button class="btn" (click)="restore()" [disabled]="busy()">Przywróć</button>
          <button class="btn" (click)="archive()" [disabled]="busy()">Archiwizuj</button>
        </div>
      } @else if (editing()) {
        <!-- Formularz w siatce: sprawa i opis to zdania (cała szerokość), czas to dwie
             cyfry (własna, wąska kolumna). Trzy pola jednej szerokości nie mówiły nic
             o tym, które z nich jest ważne, a pole na minuty ciągnące się przez ekran
             wyglądało jak miejsce na kwotę. -->
        <div class="edit-form">
          <label class="field field-wide">
            <span class="field-label">Sprawa</span>
            <select [(ngModel)]="selectedCaseId">
              <option [ngValue]="null">wybierz sprawę…</option>
              @for (caseInfo of cases(); track caseInfo.id) {
                <!-- Klient obok numeru: przy podobnych nazwach spraw to on rozstrzyga wybór. -->
                <option [ngValue]="caseInfo.id">{{ caseInfo.name }} ({{ caseInfo.caseNumber }}) · {{ caseInfo.clientName }}</option>
              }
            </select>
          </label>
          <label class="field field-minutes">
            <span class="field-label">Czas</span>
            <span class="minutes-input">
              <input type="number" min="1" [(ngModel)]="durationDraft" />
              <span class="text-muted">min</span>
            </span>
          </label>
          <label class="field field-wide">
            <span class="field-label">Opis czynności</span>
            <input type="text" [(ngModel)]="descriptionDraft" />
          </label>
          <p class="field-hint text-muted">
            Czas możesz poprawiać także po zatwierdzeniu, w zakładce „Wpisy czasu".
          </p>
          <div class="actions">
            <button class="btn btn-primary" (click)="approve()" [disabled]="busy() || sourceConflict()">Zapisz i zatwierdź</button>
            <button class="btn" (click)="editing.set(false)" [disabled]="busy()">Anuluj</button>
          </div>
        </div>
      } @else {
        <p class="description">{{ descriptionDraft() }}</p>
        <div class="actions">
          <button class="btn btn-primary" (click)="approve()" [disabled]="busy() || sourceConflict()">Zatwierdź</button>
          <button class="btn" (click)="editing.set(true)" [disabled]="busy()">Edytuj</button>
          <button class="btn btn-danger" (click)="reject()" [disabled]="busy()">Odrzuć</button>
        </div>
      }

      @if (sides().length > 0) {
        <!-- Sąsiedzi na osi dnia pojawiają się TYLKO wtedy, gdy jest co zrobić.
             Backend przysyła lukę wyłącznie wolną i mieszczącą się w limicie: jeśli
             prawnik w tym czasie pracował nad innym dokumentem, przerwy nie ma —
             ten czas jest już rozliczony w historii tamtego pliku. „Scal sesje"
             wystawiamy wyłącznie na canMerge, czyli gdy backend potwierdził, że
             scalenie przejdzie — przycisk kończący się odmową był gorszy od braku
             przycisku, bo obiecywał operację odrzucaną przez tę samą warstwę. -->
        <div class="neighbors">
          @for (side of sides(); track side.side) {
            <div class="neighbor-row">
              <span class="text-muted">{{ neighborLabel(side.neighbor, side.side) }}</span>
              <!-- Etykiety mówią WPROST, co zrobi kliknięcie i o ile minut chodzi.
                   „Dolicz całość" nie niosło ani liczby, ani kierunku: prawnik klikał
                   i widział tylko, że czas skądś podskoczył. -->
              @if (side.neighbor.gapMinutes > 0) {
                <button
                  class="btn btn-ghost"
                  (click)="claimWhole(side)"
                  [disabled]="busy()"
                  [title]="wholeHint(side)"
                >
                  Dolicz {{ side.neighbor.gapMinutes }} min do tej sesji
                </button>
                <button
                  class="btn btn-ghost"
                  (click)="startSplit(side)"
                  [disabled]="busy()"
                  [title]="splitHint(side)"
                >
                  {{ side.neighbor.suggestionId === null ? 'Dolicz część…' : 'Podziel przerwę…' }}
                </button>
              }
              @if (side.neighbor.canMerge) {
                <button
                  class="btn btn-ghost"
                  (click)="mergeWith(side.neighbor.suggestionId!)"
                  [disabled]="busy()"
                  [title]="mergeHint(side)"
                >
                  Scal w jedną sesję
                </button>
              }
            </div>

            @if (splitTarget()?.side === side.side) {
              <!-- Podział jest jawny, a nie „po połowie w ciemno": prawnik widzi obie
                   liczby przed zapisem i od razu je poprawia, bo to on wie, po której
                   stronie przerwy naprawdę pracował. Połowa jest tylko wartością
                   startową. Niedobrane minuty zostają wolne — nie dopisujemy ich
                   nikomu za użytkownika. -->
              <div class="split-form">
                <label class="split-field">
                  tutaj
                  <input type="number" min="0" [max]="side.neighbor.gapMinutes" [(ngModel)]="splitMinutes" />
                  min
                </label>
                @if (side.neighbor.suggestionId !== null) {
                  <label class="split-field">
                    „{{ side.neighbor.title }}"
                    <input type="number" min="0" [max]="side.neighbor.gapMinutes" [(ngModel)]="splitNeighborMinutes" />
                    min
                  </label>
                }
                <span class="split-rest" [class.text-warn]="splitFreeMinutes() < 0">
                  z {{ side.neighbor.gapMinutes }} min zostaje wolne: {{ splitFreeMinutes() }} min
                </span>
                <div class="actions">
                  <button class="btn btn-primary" (click)="saveSplit()" [disabled]="busy() || !splitValid()">
                    Zapisz podział
                  </button>
                  <button class="btn" (click)="splitTarget.set(null)" [disabled]="busy()">Anuluj</button>
                </div>
              </div>
            }
          }
        </div>
      }

      @if (suggestion().sourceExternalId; as externalId) {
        <!-- Przebieg edycji na żądanie: decyzja o przerwie albo o czasie wymaga
             zobaczenia, kiedy dokument faktycznie zapisywano. Ładowane dopiero
             po kliknięciu — chronologia to osobne żądanie na kartę. -->
        <div class="history">
          <button
            class="btn btn-ghost history-toggle"
            (click)="showHistory.set(!showHistory())"
            [attr.aria-expanded]="showHistory()"
          >
            {{ showHistory() ? 'Ukryj historię zmian' : 'Historia zmian' }}
          </button>
          @if (showHistory()) {
            <!-- Chronologia niesie stan WSZYSTKICH pozycji tego pliku, a ta sugestia jest
                 w niej wskazana po identyfikatorze. Wcześniej stan podawała karta, więc
                 z sugestii nie było widać, że sąsiedni fragment tej samej historii jest
                 już rozliczonym wpisem. -->
            <app-document-history
              [externalId]="externalId"
              [fileName]="suggestion().title"
              [currentSuggestionId]="suggestion().id"
              [sessionGaps]="suggestion().detectedGaps"
            />
          }
        </div>
      }

      @if (error()) {
        <p class="error-box">{{ error() }}</p>
      }
    </div>
  `,
  styles: `
    .card { margin: var(--space-2) 0; }
    .card-header { display: flex; align-items: center; gap: var(--space-2); flex-wrap: wrap; }
    .title { flex: 1; font-size: var(--font-size-lg); }
    .card-details { display: flex; align-items: baseline; gap: var(--space-4); margin: var(--space-2) 0; flex-wrap: wrap; }
    .detail { display: inline-flex; align-items: baseline; gap: var(--space-1); font-variant-numeric: tabular-nums; }
    /* Podpis mniejszy i cichszy od wartości — ma nazywać liczbę, nie konkurować z nią. */
    .detail-label { font-size: var(--font-size-sm); opacity: 0.75; }
    .case-line { display: flex; align-items: center; gap: var(--space-1); flex-wrap: wrap; margin: var(--space-1) 0; }
    .review-reason { margin: var(--space-1) 0; font-size: var(--font-size-sm); }
    .gap-note { margin: var(--space-1) 0; font-size: var(--font-size-sm); }
    .description { margin: var(--space-2) 0; }
    /* Siatka zamiast kolumny: pola dostają szerokość odpowiadającą temu, co się w nie
       wpisuje. „auto" dla minut zwęża je do zawartości, reszta zajmuje resztę wiersza. */
    .edit-form {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: var(--space-2) var(--space-3);
      align-items: end;
      margin: var(--space-3) 0;
    }
    .field { display: flex; flex-direction: column; gap: var(--space-1); min-width: 0; }
    .field-wide { grid-column: 1 / -1; }
    .field-label { font-size: var(--font-size-sm); color: var(--text-muted); }
    .minutes-input { display: inline-flex; align-items: center; gap: var(--space-1); }
    .minutes-input input { width: 5rem; }
    .field-hint { grid-column: 1 / -1; margin: 0; font-size: var(--font-size-sm); }
    .edit-form .actions { grid-column: 1 / -1; margin-top: 0; }
    .actions { display: flex; gap: var(--space-2); justify-content: flex-end; margin-top: var(--space-3); }
    .conflict-box { display: flex; align-items: center; gap: var(--space-3); flex-wrap: wrap; margin: var(--space-2) 0; }
    .conflict-box .actions { margin-top: 0; }
    .history { margin-top: var(--space-2); border-top: 1px solid var(--border); padding-top: var(--space-2); }
    .history-toggle { padding: 0 var(--space-2); font-size: var(--font-size-sm); }
    .neighbors { margin-top: var(--space-2); display: flex; flex-direction: column; gap: var(--space-1); }
    .neighbor-row {
      display: flex; align-items: center; gap: var(--space-2);
      flex-wrap: wrap; font-size: var(--font-size-sm);
    }
    .neighbor-row .text-muted { flex: 1; min-width: 0; }
    .neighbor-row .btn { padding: 0 var(--space-2); font-size: var(--font-size-sm); }
    .split-form {
      display: flex; align-items: center; gap: var(--space-3);
      flex-wrap: wrap; font-size: var(--font-size-sm);
      padding: var(--space-2); border: 1px solid var(--border); border-radius: var(--radius);
    }
    .split-field { display: inline-flex; align-items: center; gap: var(--space-1); }
    /* Wąskie pole: to są minuty przerwy, nie kwota — szersze sugerowałoby większe liczby. */
    .split-field input { width: 4.5rem; }
    .split-rest { flex: 1; min-width: 0; opacity: 0.85; font-variant-numeric: tabular-nums; }
    .split-form .actions { margin-top: 0; }
  `,
})
export class SuggestionCard {
  private api = inject(ApiService);

  suggestion = input.required<Suggestion>();
  cases = input.required<CaseInfo[]>();
  resolved = output<SuggestionResolved>();

  /**
   * Czas sugestii zmieniony (scalenie, doliczona luka) — rodzic przeładowuje listę,
   * bo zmiana dotyczy także sąsiada i całej osi dnia, nie tylko tej karty. Ładunkiem
   * jest gotowe zdanie do pokazania: karta po operacji znika z ekranu w obecnym
   * kształcie (lista ładuje się od nowa), więc potwierdzenie musi wyjść poza nią.
   */
  adjusted = output<string>();

  protected editing = signal(false);
  protected busy = signal(false);
  protected error = signal<string | null>(null);

  /** Chronologia modyfikacji rozwijana na żądanie — nie ładujemy jej dla każdej karty z listy. */
  protected showHistory = signal(false);

  // Robocze wartości formularza — resetowane automatycznie po zmianie źródła,
  // o ile użytkownik niczego nie edytował (patrz applySourceChange).
  protected selectedCaseId = signal<number | null>(null);
  protected durationDraft = signal(0);
  protected descriptionDraft = signal('');

  /** Źródło zmieniło się w tle podczas edycji — Zatwierdź zablokowane do decyzji użytkownika. */
  protected sourceConflict = signal(false);

  /** Wartości źródła, na których oparte są bieżące drafty. */
  private draftSource: Suggestion | null = null;

  constructor() {
    effect(() => {
      const current = this.suggestion();
      untracked(() => this.applySourceChange(current));
    });
  }

  protected needsReview = computed(
    () => this.suggestion().status === 'pending'
      && (this.suggestion().caseId === null || this.suggestion().isAmbiguous),
  );

  protected isRejected = computed(() => this.suggestion().status === 'rejected');

  protected isArchived = computed(() => this.suggestion().status === 'archived');

  protected caseMeta = computed(() =>
    formatCaseMeta(this.suggestion().caseNumber, this.suggestion().clientName),
  );

  /** Przerwy w tym czasie nazwane wprost — razem z tym, gdzie się je poprawia. */
  protected gapNote = computed(() => suggestionGapNote(this.suggestion()));

  /**
   * „Ostatnia zmiana" pokazywana TYLKO wtedy, gdy niesie nową informację. Sesja
   * zbudowana z jednego zapisu zaczyna się dokładnie w momencie tego zapisu, więc
   * początek i ostatnia modyfikacja to ten sam fakt — powtarzanie go dwa razy w jednym
   * wierszu wyglądało jak błąd danych. Datę dokładamy tylko przy zmianie doby;
   * w obrębie dnia sama godzina wystarcza i nie zaśmieca wiersza.
   */
  protected lastActivityLabel = computed(() => {
    const { startedAt, lastActivityAt } = this.suggestion();
    const start = new Date(startedAt);
    const last = new Date(lastActivityAt);
    if (Number.isNaN(last.getTime()) || last.getTime() <= start.getTime()) {
      return null;
    }

    const time = last.toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit' });
    return last.toDateString() === start.toDateString()
      ? time
      : `${last.toLocaleDateString('pl-PL')} ${time}`;
  });

  protected sourceIcon = computed(() => (this.suggestion().source === 'calendar' ? '📅' : '📄'));

  protected sourceLabel = computed(() =>
    this.suggestion().source === 'calendar' ? 'Spotkanie' : 'Dokument',
  );

  /** Konkret zamiast samego "sprawdź to" — user wie, czego brakuje albo co jest niejasne. */
  protected reviewReason = computed(() => {
    if (!this.needsReview()) {
      return null;
    }
    const suggestion = this.suggestion();
    if (suggestion.isAmbiguous) {
      return `Pasuje do ${suggestion.matchCandidates.length} spraw: ${suggestion.matchCandidates.join(', ')}. Wybierz właściwą przy edycji.`;
    }
    return 'Nie znaleziono nazwy klienta ani numeru sprawy w tytule. Wskaż sprawę przy edycji.';
  });

  /**
   * Zmiana wartości wejściowej sugestii (np. odświeżenie po synchronizacji):
   * bez edycji użytkownika drafty resetują się do nowych wartości; w trakcie
   * edycji nie nadpisujemy jego pracy — pokazujemy konflikt do rozstrzygnięcia.
   */
  private applySourceChange(current: Suggestion): void {
    const previous = this.draftSource;
    this.draftSource = current;

    if (previous === null) {
      this.resetDrafts(current);
      return;
    }

    if (previous.caseId === current.caseId
      && previous.durationMinutes === current.durationMinutes
      && previous.proposedDescription === current.proposedDescription) {
      return;
    }

    if (this.editing() || this.isDirty(previous)) {
      this.sourceConflict.set(true);
    } else {
      this.resetDrafts(current);
    }
  }

  private isDirty(source: Suggestion): boolean {
    return this.selectedCaseId() !== source.caseId
      || this.durationDraft() !== source.durationMinutes
      || this.descriptionDraft() !== source.proposedDescription;
  }

  private resetDrafts(source: Suggestion): void {
    this.selectedCaseId.set(source.caseId);
    this.durationDraft.set(source.durationMinutes);
    this.descriptionDraft.set(source.proposedDescription);
    this.sourceConflict.set(false);
  }

  /** Konflikt: użytkownik wybrał wartości z synchronizacji. */
  protected acceptSourceValues(): void {
    this.resetDrafts(this.suggestion());
  }

  /** Konflikt: użytkownik świadomie zostaje przy swoich wartościach. */
  protected keepMyValues(): void {
    this.sourceConflict.set(false);
  }

  protected async approve(): Promise<void> {
    if (this.sourceConflict()) {
      this.error.set('Sugestia zmieniła się podczas synchronizacji. Najpierw wybierz, które wartości zachować.');
      return;
    }
    if (this.selectedCaseId() === null) {
      // Przy braku lub niejednoznacznym dopasowaniu użytkownik musi wskazać sprawę.
      this.error.set('Wybierz sprawę przed zatwierdzeniem.');
      this.editing.set(true);
      return;
    }
    if (this.durationDraft() <= 0) {
      this.error.set('Czas trwania musi być większy od zera.');
      return;
    }

    const payload: ApprovePayload = {
      caseId: this.selectedCaseId()!,
      durationMinutes: this.durationDraft(),
      description: this.descriptionDraft().trim() || this.suggestion().title,
    };

    await this.run(async () => {
      const createdEntry = await this.api.approve(this.suggestion().id, payload);
      this.resolved.emit({ suggestion: this.suggestion(), action: 'approved', createdEntry });
    });
  }

  /**
   * Sąsiedzi pokazywani tylko dla sugestii oczekującej i tylko wtedy, gdy jest co
   * zrobić: wolna luka do rozdzielenia albo scalenie potwierdzone przez backend.
   * Jedna lista zamiast dwóch gałęzi szablonu — obie strony różnią się wyłącznie
   * słowem „przed"/„po", a duplikat rozjeżdżał się przy każdej zmianie przycisków.
   */
  protected sides = computed((): Array<{ side: 'before' | 'after'; neighbor: SuggestionNeighbor }> => {
    const suggestion = this.suggestion();
    if (suggestion.status !== 'pending' || !suggestion.gaps) {
      return [];
    }

    const isActionable = (neighbor: SuggestionNeighbor | null): neighbor is SuggestionNeighbor =>
      neighbor !== null && (neighbor.gapMinutes > 0 || neighbor.canMerge);
    const { before, after } = suggestion.gaps;
    const result: Array<{ side: 'before' | 'after'; neighbor: SuggestionNeighbor }> = [];
    if (isActionable(before)) {
      result.push({ side: 'before', neighbor: before });
    }
    if (isActionable(after)) {
      result.push({ side: 'after', neighbor: after });
    }
    return result;
  });

  /** Strona, której podział jest właśnie edytowany — null, gdy formularz jest zamknięty. */
  protected splitTarget = signal<{ side: 'before' | 'after'; neighbor: SuggestionNeighbor } | null>(null);

  protected splitMinutes = signal(0);
  protected splitNeighborMinutes = signal(0);

  /** Minuty luki nieprzypisane nikomu — ujemne znaczy, że podział nie mieści się w przerwie. */
  protected splitFreeMinutes = computed(() => {
    const target = this.splitTarget();
    return target === null
      ? 0
      : target.neighbor.gapMinutes - this.splitMinutes() - this.splitNeighborMinutes();
  });

  protected splitValid = computed(() =>
    this.splitMinutes() >= 0
    && this.splitNeighborMinutes() >= 0
    && this.splitFreeMinutes() >= 0
    && this.splitMinutes() + this.splitNeighborMinutes() > 0);

  /** Zdanie mówiące, ile czasu i przy kim jest wolne — bez tego przyciski są zagadką. */
  protected neighborLabel(neighbor: SuggestionNeighbor, side: 'before' | 'after'): string {
    if (neighbor.gapMinutes === 0) {
      return `Ta sama praca ${side === 'before' ? 'tuż przed' : 'tuż po'}: „${neighbor.title}".`;
    }
    const where = side === 'before' ? 'przed' : 'po';
    // „Nierozliczone" zamiast „wolne": luka jest wolna w sensie osi dnia, ale dla prawnika
    // znaczy to jedno — ten czas nie trafił jeszcze na żaden rachunek.
    return `Nierozliczone ${neighbor.gapMinutes} min ${where} tą sesją (dalej: „${neighbor.title}").`;
  }

  /**
   * Otwiera podział z połową w polach. Połowa to wartość startowa, nie decyzja —
   * przy nieparzystej liczbie minut nadwyżka ląduje tutaj, bo to na tej karcie
   * kliknięto; user i tak widzi obie liczby i może je zmienić przed zapisem.
   */
  protected startSplit(target: { side: 'before' | 'after'; neighbor: SuggestionNeighbor }): void {
    const mine = Math.ceil(target.neighbor.gapMinutes / 2);
    this.splitMinutes.set(mine);
    this.splitNeighborMinutes.set(
      target.neighbor.suggestionId === null ? 0 : target.neighbor.gapMinutes - mine);
    this.splitTarget.set(target);
  }

  /** Podpowiedzi na przyciskach — pełne zdanie o skutku nie mieści się w etykiecie. */
  protected wholeHint(side: { side: 'before' | 'after'; neighbor: SuggestionNeighbor }): string {
    const where = side.side === 'before' ? 'wcześniej' : 'później';
    return `Cała wolna przerwa (${side.neighbor.gapMinutes} min) doliczy się do tej sugestii:`
      + ` sesja zacznie się ${where} lub potrwa dłużej. Sąsiad zostaje bez zmian.`;
  }

  protected splitHint(side: { side: 'before' | 'after'; neighbor: SuggestionNeighbor }): string {
    return side.neighbor.suggestionId === null
      ? `Doliczysz wybraną część z ${side.neighbor.gapMinutes} min; reszta zostanie wolna.`
      : `Rozdzielisz ${side.neighbor.gapMinutes} min między tę sugestię a „${side.neighbor.title}";`
        + ' niedobrane minuty zostaną wolne.';
  }

  protected mergeHint(side: { side: 'before' | 'after'; neighbor: SuggestionNeighbor }): string {
    return `Ta sugestia i „${side.neighbor.title}" (ten sam plik, ten sam dzień) staną się jedną`
      + ' pozycją. Czas = suma zmierzonych sesji; przerwa między nimi NIE jest doliczana,'
      + ' od tego są przyciski obok.';
  }

  /** Skrót: cała wolna luka do tej sugestii, bez otwierania formularza. */
  protected async claimWhole(side: { side: 'before' | 'after'; neighbor: SuggestionNeighbor }): Promise<void> {
    const previous = this.suggestion();
    await this.run(async () => {
      const changed = await this.api.claimSuggestionGap(previous.id, side.side);
      this.splitTarget.set(null);
      this.adjusted.emit(gapClaimMessage(previous, changed, side.neighbor.title, side.side, 0));
    });
  }

  protected async saveSplit(): Promise<void> {
    const target = this.splitTarget();
    if (target === null) {
      return;
    }

    const previous = this.suggestion();
    const forNeighbor = this.splitNeighborMinutes();
    await this.run(async () => {
      const changed = await this.api.claimSuggestionGap(
        previous.id, target.side, this.splitMinutes(), forNeighbor);
      this.splitTarget.set(null);
      this.adjusted.emit(
        gapClaimMessage(previous, changed, target.neighbor.title, target.side, forNeighbor));
    });
  }

  /**
   * Scalanie CELOWO bez doliczania luki: łączymy to, co zmierzone, a wolny czas
   * między sesjami zostaje osobną decyzją („Dolicz tutaj"). Gdy zgadujemy, mylimy
   * się na niekorzyść kancelarii, nie klienta.
   */
  protected async mergeWith(otherSuggestionId: number): Promise<void> {
    await this.run(async () => {
      const merged = await this.api.mergeSuggestions([this.suggestion().id, otherSuggestionId], false);
      this.adjusted.emit(mergeMessage(merged[0] ?? this.suggestion()));
    });
  }

  protected async reject(): Promise<void> {
    await this.run(async () => {
      await this.api.reject(this.suggestion().id);
      this.resolved.emit({ suggestion: this.suggestion(), action: 'rejected' });
    });
  }

  protected async restore(): Promise<void> {
    await this.run(async () => {
      await this.api.restore(this.suggestion().id);
      this.resolved.emit({ suggestion: this.suggestion(), action: 'restored' });
    });
  }

  protected async archive(): Promise<void> {
    await this.run(async () => {
      await this.api.archiveSuggestion(this.suggestion().id);
      this.resolved.emit({ suggestion: this.suggestion(), action: 'archived' });
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      // Ten sam kształt komunikatu co w całej aplikacji (fallback + treść błędu).
      this.error.set(toUserMessage(error, 'Operacja nie powiodła się.'));
    } finally {
      this.busy.set(false);
    }
  }
}
