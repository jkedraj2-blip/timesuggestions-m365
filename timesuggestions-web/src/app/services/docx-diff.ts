import { Injectable, inject } from '@angular/core';
import { unzipSync } from 'fflate';
import { diffArrays } from 'diff';
import { AuthService } from './auth.service';
import { GRAPH_BASE_URL } from './graph-config';
import { fetchGraphBinary } from './graph-http';

/**
 * Diff dwóch wersji .docx liczony W PRZEGLĄDARCE — treść dokumentów nie przechodzi
 * przez backend (decyzja prywatnościowa, patrz README). Frontend pobiera obie wersje
 * bezpośrednio z Graph, rozpakowuje ZIP (fflate), wyciąga tekst akapitów
 * z word/document.xml (DOMParser, namespace WordprocessingML) i diffuje po akapitach
 * (pakiet `diff`, LCS). Tylko .docx — .doc (binarny) i arkusze Excela dostają
 * chronologię bez diffu.
 */

const WORDPROCESSINGML_NS = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main';

/**
 * Zmiana jednej LINII; dla 'changed' previousText niesie brzmienie sprzed zmiany.
 *
 * Jednostką diffu jest linia, a nie akapit. Word robi z entera raz nowy akapit (w:p),
 * raz złamanie wiersza (w:br) — przy jednostce „akapit" wiele widocznych linii lądowało
 * w JEDNEJ pozycji listy zmian: jeden przycisk „Pokaż całość" na kilkanaście linii,
 * licznik znaków liczony dla całego bloku i wielokropek ucinający w przypadkowym
 * miejscu w środku. Linia to jednostka, którą użytkownik widzi w dokumencie.
 */
export interface LineChange {
  kind: 'added' | 'removed' | 'changed';
  text: string;
  previousText?: string;
  /**
   * Ile PUSTYCH linii pod rząd reprezentuje ta pozycja (pole obecne wyłącznie dla
   * takich pozycji). Seria enterów to seria pustych linii — każda jako własny wiersz
   * rozdymała listę zmian, nie wnosząc nic ponad „tu zrobiono odstęp".
   */
  emptyLines?: number;
}

/** Rzucany dla wersji wyciętej z retencji OneDrive — UI pokazuje komunikat i działa dalej. */
export class VersionUnavailableError extends Error {
  constructor() {
    super('Ta wersja nie jest już dostępna w OneDrive.');
  }
}

/** Etykieta, którą część dysków opisuje najnowszą wersję zamiast numerem. */
export const CURRENT_VERSION_LABEL = 'current';

/** Wersja w kształcie potrzebnym do pobrania treści (z chronologii modyfikacji). */
export interface VersionRef {
  versionId: string;
  /** Najnowszy znany zapis pliku — backend wskazuje go w chronologii. */
  isCurrent: boolean;
}

/**
 * Adres treści wersji. BIEŻĄCA wersja jest wyjątkiem: Graph nie wydaje jej spod
 * adresu /versions/{id}/content, tylko odpowiada 400 (to źródło błędu „Nie udało
 * się pobrać treści wersji" przy porównaniu najnowszej pary) — treścią bieżącej
 * wersji jest po prostu aktualna treść pliku, więc bierzemy ją z endpointu elementu.
 * Rozpoznajemy ją po znaczniku z backendu, a dodatkowo po etykiecie "current",
 * którą część dysków wstawia w miejsce numeru wersji. Jedno miejsce, w którym
 * identyfikator wersji trafia do adresu — dlatego reguła jest tutaj.
 */
export function versionContentUrl(itemId: string, version: VersionRef): string {
  const item = `${GRAPH_BASE_URL}/me/drive/items/${encodeURIComponent(itemId)}`;
  return version.isCurrent || version.versionId === CURRENT_VERSION_LABEL
    ? `${item}/content`
    : `${item}/versions/${encodeURIComponent(version.versionId)}/content`;
}

/** Czy plik w ogóle podlega diffowi tekstu (tylko .docx; .doc i Excel — nie). */
export function isDiffableDocument(fileName: string | null | undefined): boolean {
  return (fileName ?? '').toLowerCase().endsWith('.docx');
}

/**
 * Tekst jednego akapitu czytany W KOLEJNOŚCI DOKUMENTU, ze złamaniami wiersza jako '\n'
 * (rozbija je na linie dopiero extractLines). Samo złączenie w:t gubiło te złamania:
 * miękki enter (w:br, Shift+Enter, ale też wynik wklejenia i autokorekty Worda)
 * i tabulator (w:tab) nie są tekstem, więc dwie linie sklejały się w jeden ciąg bez
 * odstępu („…umowyStrony ustalają…"). Word raz robi z entera nowy akapit, raz w:br —
 * stąd „raz jest przerwa, raz jej nie ma".
 */
function readParagraphText(paragraph: Element): string {
  const parts: string[] = [];

  const visit = (node: Element): void => {
    for (const child of Array.from(node.children)) {
      if (child.namespaceURI !== WORDPROCESSINGML_NS) {
        visit(child);
        continue;
      }

      switch (child.localName) {
        case 't':
          parts.push(child.textContent ?? '');
          break;
        case 'br':
        case 'cr':
          parts.push('\n');
          break;
        case 'tab':
          parts.push('\t');
          break;
        default:
          // Zagnieżdżone akapity (tabele, pola tekstowe) mają własny w:p na liście —
          // tu schodzimy tylko po runach, żeby nie policzyć ich tekstu dwa razy.
          if (child.localName !== 'p') {
            visit(child);
          }
      }
    }
  };

  visit(paragraph);
  return parts.join('');
}

/**
 * Linie tekstu z bajtów .docx: jeden element listy = jedna linia widoczna w dokumencie.
 *
 * Akapit (w:p) rozbijamy dodatkowo po złamaniach wiersza, bo dla użytkownika enter
 * i Shift+Enter to ta sama rzecz — nowa linia. Dzięki temu każda linia jest osobną
 * pozycją diffu: ma własny wiersz, własny licznik znaków i własny przycisk rozwinięcia,
 * a dwie linie NIGDY nie zlewają się w jeden ciąg.
 *
 * Puste linie zostają — ich usunięcie przesuwałoby diff względem realnego układu
 * (sklejaniem serii pustych linii zajmuje się dopiero warstwa diffu).
 */
export function extractLines(docxBytes: Uint8Array): string[] {
  const entries = unzipSync(docxBytes);
  const documentXml = entries['word/document.xml'];
  if (!documentXml) {
    throw new Error('Plik nie zawiera word/document.xml, więc to nie jest dokument .docx.');
  }

  const parsed = new DOMParser().parseFromString(new TextDecoder().decode(documentXml), 'application/xml');
  return Array.from(parsed.getElementsByTagNameNS(WORDPROCESSINGML_NS, 'p'))
    .flatMap((paragraph) => readParagraphText(paragraph).split('\n'));
}

/**
 * Skleja serie pustych linii tego samego rodzaju w jedną pozycję z licznikiem.
 * Kilka enterów pod rząd to kilka pustych linii, więc lista zmian dostawała tyle samo
 * wierszy „(pusta linia)" — przy dłuższym odstępie historia zmian robiła się nie do
 * przejrzenia, a niosła jedną informację: zrobiono odstęp. Sklejamy tylko SĄSIEDNIE
 * i tylko tego samego rodzaju: dodane odstępy to co innego niż usunięte, a pusta linia
 * między liniami z tekstem nie zlewa się z niczym.
 */
function collapseEmptyLines(changes: LineChange[]): LineChange[] {
  const collapsed: LineChange[] = [];

  for (const change of changes) {
    // Pozycja 'changed' ma dwa brzmienia i mówi coś o TREŚCI — nie sklejamy jej,
    // nawet gdy jedna ze stron jest pusta.
    const isEmptyLine = change.kind !== 'changed' && change.text.length === 0;
    const previous = collapsed[collapsed.length - 1];

    if (isEmptyLine && previous?.emptyLines !== undefined && previous.kind === change.kind) {
      previous.emptyLines++;
      continue;
    }

    collapsed.push(isEmptyLine ? { ...change, emptyLines: 1 } : change);
  }

  return collapsed;
}

/**
 * Diff po liniach (LCS z pakietu `diff`). Sąsiadujące grupy usunięte+dodane parujemy
 * jako "zmienione" (stara → nowa linia); nadwyżka zostaje czystym dodaniem/usunięciem.
 * Serie pustych linii wychodzą sklejone (patrz collapseEmptyLines).
 * Pusta lista = brak różnic tekstowych (zmiany dotyczyły formatowania).
 */
export function diffLines(before: string[], after: string[]): LineChange[] {
  const groups = diffArrays(before, after);
  const changes: LineChange[] = [];

  for (let i = 0; i < groups.length; i++) {
    const group = groups[i];
    if (group.removed) {
      const next = groups[i + 1];
      if (next?.added) {
        // Para usunięte→dodane w tym samym miejscu = akapity zmienione.
        const pairCount = Math.min(group.value.length, next.value.length);
        for (let j = 0; j < pairCount; j++) {
          changes.push({ kind: 'changed', text: next.value[j], previousText: group.value[j] });
        }
        for (let j = pairCount; j < group.value.length; j++) {
          changes.push({ kind: 'removed', text: group.value[j] });
        }
        for (let j = pairCount; j < next.value.length; j++) {
          changes.push({ kind: 'added', text: next.value[j] });
        }
        i++; // grupa added skonsumowana
      } else {
        for (const text of group.value) {
          changes.push({ kind: 'removed', text });
        }
      }
    } else if (group.added) {
      for (const text of group.value) {
        changes.push({ kind: 'added', text });
      }
    }
  }

  return collapseEmptyLines(changes);
}

@Injectable({ providedIn: 'root' })
export class DocxDiffService {
  private auth = inject(AuthService);

  /**
   * Cache wyników W PAMIĘCI SESJI (mapa po itemId+parze wersji), świadomie nie
   * w localStorage: wynik diffu to pochodna treści dokumentu, która nie może
   * przeżyć karty przeglądarki.
   */
  private cache = new Map<string, LineChange[]>();

  /**
   * Porównuje dwie sąsiednie wersje pliku. Każde pierwsze wywołanie = 2 pobrania
   * pliku (potencjalnie MB) — dlatego ładowanie wyłącznie po kliknięciu konkretnej
   * pary, ze spinnerem i możliwością anulowania (AbortController).
   */
  async compareVersions(
    itemId: string,
    older: VersionRef,
    newer: VersionRef,
    signal: AbortSignal,
  ): Promise<LineChange[]> {
    // Para z wersją bieżącą jest CELOWO poza cache: bieżąca wersja to ruchoma
    // głowica pliku, więc po kolejnym zapisie ten sam klucz oznaczałby już inną
    // treść — a to właśnie najświeższe zmiany prawnik chce zobaczyć.
    const cacheable = !older.isCurrent && !newer.isCurrent;
    const cacheKey = `${itemId}|${older.versionId}|${newer.versionId}`;
    const cached = cacheable ? this.cache.get(cacheKey) : undefined;
    if (cached) {
      return cached;
    }

    const [olderBytes, newerBytes] = await Promise.all([
      this.fetchVersionContent(itemId, older, signal),
      this.fetchVersionContent(itemId, newer, signal),
    ]);

    const changes = diffLines(extractLines(olderBytes), extractLines(newerBytes));
    if (cacheable) {
      this.cache.set(cacheKey, changes);
    }
    return changes;
  }

  /**
   * Treść wersji przez wspólny helper Graph (walidacja adresu, ponowienia 429/5xx
   * z Retry-After, limit czasu, anulowanie) — pobranie kilkumegabajtowego pliku nie
   * może padać na pierwszym throttlingu ani wisieć bez granicy.
   *
   * UDOKUMENTOWANY WYJĄTEK od reguły "token tylko do graph.microsoft.com": Graph
   * odpowiada przekierowaniem 302 do domeny pobrań (adres pre-autoryzowany,
   * z jednorazowym tokenem w URL); fetch podąża za nim automatycznie, a przeglądarka
   * przy przekierowaniu cross-origin sama USUWA nagłówek Authorization — token Graph
   * nie trafia więc do domeny pobrań. Walidujemy wyłącznie adres początkowy.
   */
  private async fetchVersionContent(
    itemId: string,
    version: VersionRef,
    signal: AbortSignal,
  ): Promise<Uint8Array> {
    const result = await fetchGraphBinary(
      versionContentUrl(itemId, version),
      () => this.auth.getToken(),
      signal,
    );

    if (result.ok) {
      return result.bytes;
    }
    if (result.status === 404) {
      // Ograniczona retencja wersji OneDrive to stan normalny, nie błąd aplikacji.
      throw new VersionUnavailableError();
    }

    // Kod błędu z ciała odpowiedzi, nie sam status — „400 invalidRequest" mówi,
    // co Graph odrzucił, a „400" nie mówi nic. Komunikat NIE powtarza wstępu
    // wywołującego („Nie udało się porównać wersji."), żeby użytkownik nie czytał
    // dwa razy tego samego zdania.
    const detail = result.errorCode === null ? `${result.status}` : `${result.status} ${result.errorCode}`;
    throw new Error(`Graph odrzucił pobranie treści wersji (${detail}).`);
  }
}
