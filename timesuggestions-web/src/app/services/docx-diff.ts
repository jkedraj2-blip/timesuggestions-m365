import { Injectable, inject } from '@angular/core';
import { unzipSync } from 'fflate';
import { diffArrays } from 'diff';
import { AuthService } from './auth.service';
import { GRAPH_BASE_URL } from './graph-config';
import { assertTrustedGraphUrl } from './graph-http';

/**
 * Diff dwóch wersji .docx liczony W PRZEGLĄDARCE — treść dokumentów nie przechodzi
 * przez backend (decyzja prywatnościowa, patrz README). Frontend pobiera obie wersje
 * bezpośrednio z Graph, rozpakowuje ZIP (fflate), wyciąga tekst akapitów
 * z word/document.xml (DOMParser, namespace WordprocessingML) i diffuje po akapitach
 * (pakiet `diff`, LCS). Tylko .docx — .doc (binarny) i arkusze Excela dostają
 * chronologię bez diffu.
 */

const WORDPROCESSINGML_NS = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main';

/** Zmiana jednego akapitu; dla 'changed' previousText niesie brzmienie sprzed zmiany. */
export interface ParagraphChange {
  kind: 'added' | 'removed' | 'changed';
  text: string;
  previousText?: string;
}

/** Rzucany dla wersji wyciętej z retencji OneDrive — UI pokazuje komunikat i działa dalej. */
export class VersionUnavailableError extends Error {
  constructor() {
    super('Ta wersja nie jest już dostępna w OneDrive.');
  }
}

/** Czy plik w ogóle podlega diffowi tekstu (tylko .docx; .doc i Excel — nie). */
export function isDiffableDocument(fileName: string | null | undefined): boolean {
  return (fileName ?? '').toLowerCase().endsWith('.docx');
}

/**
 * Tekst akapitów z bajtów .docx: tekst = złączone w:t per w:p.
 * Puste akapity zostają — ich usunięcie przesuwałoby diff względem realnego układu.
 */
export function extractParagraphs(docxBytes: Uint8Array): string[] {
  const entries = unzipSync(docxBytes);
  const documentXml = entries['word/document.xml'];
  if (!documentXml) {
    throw new Error('Plik nie zawiera word/document.xml — to nie jest dokument .docx.');
  }

  const parsed = new DOMParser().parseFromString(new TextDecoder().decode(documentXml), 'application/xml');
  return Array.from(parsed.getElementsByTagNameNS(WORDPROCESSINGML_NS, 'p')).map((paragraph) =>
    Array.from(paragraph.getElementsByTagNameNS(WORDPROCESSINGML_NS, 't'))
      .map((text) => text.textContent ?? '')
      .join(''),
  );
}

/**
 * Diff po akapitach (LCS z pakietu `diff`). Sąsiadujące grupy usunięte+dodane parujemy
 * jako "zmienione" (stary → nowy akapit); nadwyżka zostaje czystym dodaniem/usunięciem.
 * Pusta lista = brak różnic tekstowych (zmiany dotyczyły formatowania).
 */
export function diffParagraphs(before: string[], after: string[]): ParagraphChange[] {
  const groups = diffArrays(before, after);
  const changes: ParagraphChange[] = [];

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

  return changes;
}

@Injectable({ providedIn: 'root' })
export class DocxDiffService {
  private auth = inject(AuthService);

  /**
   * Cache wyników W PAMIĘCI SESJI (mapa po itemId+parze wersji), świadomie nie
   * w localStorage: wynik diffu to pochodna treści dokumentu, która nie może
   * przeżyć karty przeglądarki.
   */
  private cache = new Map<string, ParagraphChange[]>();

  /**
   * Porównuje dwie sąsiednie wersje pliku. Każde pierwsze wywołanie = 2 pobrania
   * pliku (potencjalnie MB) — dlatego ładowanie wyłącznie po kliknięciu konkretnej
   * pary, ze spinnerem i możliwością anulowania (AbortController).
   */
  async compareVersions(
    itemId: string,
    olderVersionId: string,
    newerVersionId: string,
    signal: AbortSignal,
  ): Promise<ParagraphChange[]> {
    const cacheKey = `${itemId}|${olderVersionId}|${newerVersionId}`;
    const cached = this.cache.get(cacheKey);
    if (cached) {
      return cached;
    }

    const [olderBytes, newerBytes] = await Promise.all([
      this.fetchVersionContent(itemId, olderVersionId, signal),
      this.fetchVersionContent(itemId, newerVersionId, signal),
    ]);

    const changes = diffParagraphs(extractParagraphs(olderBytes), extractParagraphs(newerBytes));
    this.cache.set(cacheKey, changes);
    return changes;
  }

  /**
   * Treść wersji: GET /me/drive/items/{id}/versions/{vId}/content (mieści się
   * w Files.Read). UDOKUMENTOWANY WYJĄTEK od reguły "token tylko do graph.microsoft.com":
   * Graph odpowiada przekierowaniem 302 do domeny pobrań (adres pre-autoryzowany,
   * z jednorazowym tokenem w URL); fetch podąża za nim automatycznie, a przeglądarka
   * przy przekierowaniu cross-origin sama USUWA nagłówek Authorization — token Graph
   * nie trafia więc do domeny pobrań. Walidujemy wyłącznie adres początkowy.
   */
  private async fetchVersionContent(
    itemId: string,
    versionId: string,
    signal: AbortSignal,
  ): Promise<Uint8Array> {
    const url = `${GRAPH_BASE_URL}/me/drive/items/${encodeURIComponent(itemId)}/versions/${encodeURIComponent(versionId)}/content`;
    assertTrustedGraphUrl(url);

    const token = await this.auth.getToken();
    const response = await fetch(url, {
      headers: { Authorization: `Bearer ${token}` },
      signal,
    });

    if (response.status === 404) {
      // Ograniczona retencja wersji OneDrive to stan normalny, nie błąd aplikacji.
      throw new VersionUnavailableError();
    }
    if (!response.ok) {
      throw new Error(`Nie udało się pobrać treści wersji (Graph ${response.status}).`);
    }

    return new Uint8Array(await response.arrayBuffer());
  }
}
