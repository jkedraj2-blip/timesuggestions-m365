import { Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';
import { GraphDeltaResponse, GraphDriveItem } from '../models/graph.models';
import { DriveFilePayload } from '../models/api.models';
import { GRAPH_BASE_URL } from './graph-config';

const ALLOWED_EXTENSIONS = ['.docx', '.doc', '.xlsx', '.xls'];

/** Graph zwraca 410 Gone, gdy zapamiętany deltaLink wygasł — trzeba zacząć pełny przebieg od nowa. */
const HTTP_GONE = 410;

/**
 * Pobieranie ostatnio edytowanych dokumentów Word/Excel z OneDrive.
 *
 * Decyzja: endpoint /me/drive/recent jest oznaczony przez Microsoft jako wycofywany,
 * dlatego używamy wspieranego zapytania delta (/me/drive/root/delta), które zwraca
 * elementy dysku wraz ze zmianami. Filtrowanie (okno czasu, rozszerzenia, autor
 * modyfikacji) wykonujemy po stronie klienta, bo delta nie wspiera $filter.
 * Alternatywy rozważone: wyszukiwanie z sortowaniem po dacie modyfikacji
 * (niestabilne wsparcie $orderby w search) oraz endpointy aktywności (niedostępne
 * dla kont osobistych).
 *
 * Wydajność: pierwszy przebieg delta przechodzi cały dysk (na dużym OneDrive
 * to dziesiątki sekund), dlatego zapamiętujemy deltaLink w localStorage —
 * kolejne synchronizacje pobierają wyłącznie zmiany i trwają ułamek sekundy.
 * Link zapisujemy dopiero po udanym zapisie w backendzie (commitDeltaLink),
 * żeby nieudany sync nie "zjadł" zmian.
 */
@Injectable({ providedIn: 'root' })
export class GraphFilesService {
  private auth = inject(AuthService);

  /** deltaLink z ostatniej odpowiedzi — czeka na zatwierdzenie po udanym syncu. */
  private pendingDeltaLink: string | null = null;

  async getRecentDocuments(
    days: number,
    onPage?: (page: number) => void,
  ): Promise<DriveFilePayload[]> {
    const since = new Date(Date.now() - days * 24 * 60 * 60 * 1000);
    const items = await this.fetchChangedDriveItems(onPage);

    return items
      .filter((item) => this.isRecentDocument(item, since))
      .map((item) => this.toPayload(item));
  }

  /** Wywoływane przez ApiService po udanym POST /api/sync — dopiero wtedy przesuwamy "wskaźnik" delta. */
  commitDeltaLink(): void {
    if (this.pendingDeltaLink) {
      localStorage.setItem(this.deltaLinkStorageKey(), this.pendingDeltaLink);
      this.pendingDeltaLink = null;
    }
  }

  /** Delta stronicuje wyniki — podążamy za @odata.nextLink aż do końca, raportując postęp do UI. */
  private async fetchChangedDriveItems(onPage?: (page: number) => void): Promise<GraphDriveItem[]> {
    const token = await this.auth.getToken();
    const select = '$select=id,name,file,lastModifiedDateTime,lastModifiedBy';
    const fullCrawlUrl = `${GRAPH_BASE_URL}/me/drive/root/delta?${select}`;

    const storedDeltaLink = localStorage.getItem(this.deltaLinkStorageKey());
    let url: string | undefined = storedDeltaLink ?? fullCrawlUrl;

    const items: GraphDriveItem[] = [];
    let page = 0;
    while (url) {
      page++;
      onPage?.(page);
      const response = await fetch(url, {
        headers: { Authorization: `Bearer ${token}` },
      });

      if (response.status === HTTP_GONE && storedDeltaLink && items.length === 0) {
        // Wygasły deltaLink — czyścimy i robimy pełny przebieg od zera.
        localStorage.removeItem(this.deltaLinkStorageKey());
        url = fullCrawlUrl;
        page = 0;
        continue;
      }

      if (!response.ok) {
        throw new Error(`Nie udało się pobrać plików z OneDrive (Graph ${response.status}).`);
      }

      const body: GraphDeltaResponse = await response.json();
      items.push(...body.value);

      if (body['@odata.deltaLink']) {
        this.pendingDeltaLink = body['@odata.deltaLink'];
      }
      url = body['@odata.nextLink'];
    }

    return items;
  }

  /** Klucz per konto — zmiana zalogowanego użytkownika nie może czytać cudzego wskaźnika. */
  private deltaLinkStorageKey(): string {
    return `timesuggestions.deltaLink.${this.auth.account?.homeAccountId ?? 'unknown'}`;
  }

  private isRecentDocument(item: GraphDriveItem, since: Date): boolean {
    if (!item.file) {
      return false; // folder albo pakiet — nie dokument
    }
    if (!ALLOWED_EXTENSIONS.some((extension) => item.name.toLowerCase().endsWith(extension))) {
      return false;
    }
    return new Date(item.lastModifiedDateTime) >= since;
  }

  private toPayload(item: GraphDriveItem): DriveFilePayload {
    return {
      id: item.id,
      name: item.name,
      lastModifiedDateTime: item.lastModifiedDateTime,
      lastModifiedByMe: this.isModifiedByCurrentUser(item),
    };
  }

  /**
   * Ustalenie, czy modyfikacji dokonał zalogowany użytkownik. Na prywatnym OneDrive
   * Graph nie zawsze zwraca e-mail modyfikującego — brak danych traktujemy jako
   * modyfikację właściciela dysku (to jego prywatny dysk).
   */
  private isModifiedByCurrentUser(item: GraphDriveItem): boolean {
    const modifier = item.lastModifiedBy?.user;
    if (!modifier || (!modifier.email && !modifier.displayName)) {
      return true;
    }

    const account = this.auth.account;
    if (!account) {
      return false;
    }

    const email = modifier.email?.toLowerCase();
    const username = account.username.toLowerCase();
    return email === username || modifier.displayName === account.name;
  }
}
