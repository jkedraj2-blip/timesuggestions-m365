import { Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';
import { GraphDeltaResponse, GraphDriveItem, GraphVersionsResponse } from '../models/graph.models';
import { DriveFilePayload, DriveFileVersionPayload } from '../models/api.models';
import { FETCH_MARGIN_DAYS, GRAPH_BASE_URL, VERSIONS_FETCH_CONCURRENCY } from './graph-config';
import { assertTrustedGraphUrl, fetchGraphPage, GraphPageResult } from './graph-http';

const ALLOWED_EXTENSIONS = ['.docx', '.doc', '.xlsx', '.xls'];

/** Graph zwraca 410 Gone, gdy zapamiętany deltaLink wygasł — trzeba zacząć pełny przebieg od nowa. */
const HTTP_GONE = 410;

/** Wynik przebiegu delta: pliki do payloadu, tombstone'y i liczniki filtrów klienckich. */
export interface DriveDeltaResult {
  files: DriveFilePayload[];
  /** Identyfikatory usuniętych plików (facet `deleted`) — backend czyści ich oczekujące sugestie. */
  deletedDriveFileIds: string[];
  /** Odrzucenia po stronie klienta — raport syncu ma pokazywać prawdę, nie zera. */
  documentsOutsideWindow: number;
  documentsNotOfficeDocument: number;
  /** Ile pobrań historii wersji padło (per plik) — plik zostaje w payloadzie z versions=null. */
  versionFetchErrors: number;
}

/** Postęp dociągania historii wersji — raportowany do UI jak strony delty. */
export type VersionsProgress = { done: number; total: number };

/**
 * Pobieranie ostatnio edytowanych dokumentów Word/Excel z OneDrive.
 *
 * Decyzja: endpoint /me/drive/recent jest oznaczony przez Microsoft jako wycofywany,
 * dlatego używamy wspieranego zapytania delta (/me/drive/root/delta), które zwraca
 * elementy dysku wraz ze zmianami (w tym tombstone'y usuniętych plików).
 * Filtrowanie (okno czasu, rozszerzenia, autor modyfikacji) wykonujemy po stronie
 * klienta, bo delta nie wspiera $filter. Alternatywy rozważone: wyszukiwanie
 * z sortowaniem po dacie modyfikacji (niestabilne wsparcie $orderby w search)
 * oraz endpointy aktywności (niedostępne dla kont osobistych).
 *
 * Wydajność: pierwszy przebieg delta przechodzi cały dysk (na dużym OneDrive
 * to dziesiątki sekund), dlatego zapamiętujemy deltaLink w localStorage —
 * kolejne synchronizacje pobierają wyłącznie zmiany i trwają ułamek sekundy.
 * Link zapisujemy dopiero po udanym zapisie w backendzie (commitDeltaLink) —
 * obejmującym także tombstone'y — żeby nieudany sync nie "zjadł" zmian.
 */
@Injectable({ providedIn: 'root' })
export class GraphFilesService {
  private auth = inject(AuthService);

  /** deltaLink z ostatniej odpowiedzi — czeka na zatwierdzenie po udanym syncu. */
  private pendingDeltaLink: string | null = null;

  async getRecentDocuments(
    days: number,
    onPage?: (page: number) => void,
    onVersionsProgress?: (progress: VersionsProgress) => void,
  ): Promise<DriveDeltaResult> {
    // Doba zapasu ponad okno backendu (FETCH_MARGIN_DAYS) — wstępny filtr kliencki
    // nie może odrzucić pliku, który backend uznałby za część okna lokalnego.
    const since = new Date(Date.now() - (days + FETCH_MARGIN_DAYS) * 24 * 60 * 60 * 1000);
    const items = await this.fetchChangedDriveItems(onPage);

    // Semantyka delta: ten sam driveItem.id może wystąpić w feedzie wielokrotnie,
    // a obowiązuje OSTATNIE wystąpienie (np. plik zmieniony, potem usunięty).
    const latestById = new Map<string, GraphDriveItem>();
    for (const item of items) {
      latestById.set(item.id, item);
    }

    const files: DriveFilePayload[] = [];
    const deletedDriveFileIds: string[] = [];
    let documentsOutsideWindow = 0;
    let documentsNotOfficeDocument = 0;

    for (const item of latestById.values()) {
      if (item.deleted) {
        // Tombstone bywa pozbawiony nazwy i dat — nie czytamy z niego nic poza id.
        deletedDriveFileIds.push(item.id);
        continue;
      }
      if (!item.file) {
        continue; // folder albo pakiet — nie dokument
      }
      const name = item.name ?? '';
      if (!ALLOWED_EXTENSIONS.some((extension) => name.toLowerCase().endsWith(extension))) {
        documentsNotOfficeDocument++;
        continue;
      }
      if (!item.lastModifiedDateTime || new Date(item.lastModifiedDateTime) < since) {
        documentsOutsideWindow++;
        continue;
      }
      files.push(this.toPayload(item, name, item.lastModifiedDateTime));
    }

    // Etap 0 silnika sesji: dla plików, które przeszły filtry delty (i tylko dla nich —
    // wydajność), dociągamy historię wersji. Błąd pojedynczego pliku nie wywala syncu:
    // plik zostaje w payloadzie z versions=null, a backend użyje fallbacku.
    const versionFetchErrors = await this.fetchVersionsForFiles(files, onVersionsProgress);

    return {
      files,
      deletedDriveFileIds,
      documentsOutsideWindow,
      documentsNotOfficeDocument,
      versionFetchErrors,
    };
  }

  /**
   * Dociąga historię wersji dla każdego pliku z ograniczoną równoległością
   * (VERSIONS_FETCH_CONCURRENCY) — wspólna kolejka i stała pula "workerów"
   * zamiast Promise.all na wszystkich plikach naraz, żeby nie wpaść w 429.
   * Zwraca liczbę plików, dla których pobranie się nie powiodło.
   */
  private async fetchVersionsForFiles(
    files: DriveFilePayload[],
    onProgress?: (progress: VersionsProgress) => void,
  ): Promise<number> {
    let errors = 0;
    let done = 0;
    const queue = [...files];

    const workers = Array.from(
      { length: Math.min(VERSIONS_FETCH_CONCURRENCY, queue.length) },
      async () => {
        for (let file = queue.shift(); file; file = queue.shift()) {
          try {
            file.versions = await this.fetchFileVersions(file.id);
          } catch {
            // null ≠ pusta lista: null mówi backendowi "wersje niepobrane, użyj fallbacku".
            file.versions = null;
            errors++;
          }
          done++;
          onProgress?.({ done, total: files.length });
        }
      },
    );
    await Promise.all(workers);

    return errors;
  }

  /** Historia wersji jednego pliku — stronicowana jak delta (@odata.nextLink). */
  private async fetchFileVersions(fileId: string): Promise<DriveFileVersionPayload[]> {
    const select = '$select=id,lastModifiedDateTime,size';
    let url: string | undefined =
      `${GRAPH_BASE_URL}/me/drive/items/${encodeURIComponent(fileId)}/versions?${select}`;

    const versions: DriveFileVersionPayload[] = [];
    while (url) {
      const response: GraphPageResult<GraphVersionsResponse> =
        await fetchGraphPage<GraphVersionsResponse>(url, () => this.auth.getToken());
      if (!response.ok) {
        throw new Error(`Nie udało się pobrać historii wersji pliku (Graph ${response.status}).`);
      }

      for (const version of response.body.value) {
        // Wersja bez daty jest bezużyteczna dla silnika sesji — pomijamy ją zamiast
        // zgadywać czas (Graph praktycznie zawsze zwraca lastModifiedDateTime).
        if (!version.lastModifiedDateTime) {
          continue;
        }
        versions.push({
          versionId: version.id,
          lastModifiedDateTime: version.lastModifiedDateTime,
          size: version.size ?? 0,
        });
      }
      url = response.body['@odata.nextLink'];
    }

    return versions;
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
    const select = '$select=id,name,file,deleted,lastModifiedDateTime,lastModifiedBy';
    const fullCrawlUrl = `${GRAPH_BASE_URL}/me/drive/root/delta?${select}`;

    // Zapisany deltaLink to dane z localStorage — mógł zostać podmieniony.
    // Walidacja PRZED użyciem: podejrzany adres czyścimy i przerywamy z czytelnym
    // błędem zamiast wysyłać token pod obcy host.
    const storedDeltaLink = localStorage.getItem(this.deltaLinkStorageKey());
    if (storedDeltaLink !== null) {
      try {
        assertTrustedGraphUrl(storedDeltaLink);
      } catch {
        localStorage.removeItem(this.deltaLinkStorageKey());
        throw new Error(
          'Zapisany wskaźnik synchronizacji OneDrive był nieprawidłowy i został wyczyszczony — spróbuj ponownie.',
        );
      }
    }
    let url: string | undefined = storedDeltaLink ?? fullCrawlUrl;

    // Czy bieżący przebieg zaczął się od zapisanego wskaźnika — tylko taki przebieg
    // wolno JEDNOKROTNIE zrestartować po 410; pełny crawl (bez cache) już nie,
    // inaczej groziłaby pętla resetów.
    let startedFromStoredLink = storedDeltaLink !== null;

    let items: GraphDriveItem[] = [];
    let page = 0;
    while (url) {
      page++;
      onPage?.(page);
      const response: GraphPageResult<GraphDeltaResponse> =
        await fetchGraphPage<GraphDeltaResponse>(url, () => this.auth.getToken());

      if (response.status === HTTP_GONE && startedFromStoredLink) {
        // Wygasły deltaLink — 410 może przyjść także na KOLEJNEJ stronie przebiegu
        // (nextLink należy do unieważnionej sesji delta). Częściowo zebrane elementy
        // odrzucamy: pochodzą z nieaktualnego przebiegu, a pełny crawl i tak zwróci
        // bieżący stan dysku. Czyścimy oba wskaźniki i zaczynamy od zera.
        localStorage.removeItem(this.deltaLinkStorageKey());
        this.pendingDeltaLink = null;
        items = [];
        url = fullCrawlUrl;
        page = 0;
        startedFromStoredLink = false;
        continue;
      }

      if (!response.ok) {
        throw new Error(`Nie udało się pobrać plików z OneDrive (Graph ${response.status}).`);
      }

      items.push(...response.body.value);

      if (response.body['@odata.deltaLink']) {
        this.pendingDeltaLink = response.body['@odata.deltaLink'];
      }
      url = response.body['@odata.nextLink'];
    }

    return items;
  }

  /** Klucz per konto — zmiana zalogowanego użytkownika nie może czytać cudzego wskaźnika. */
  private deltaLinkStorageKey(): string {
    return `timesuggestions.deltaLink.${this.auth.account?.homeAccountId ?? 'unknown'}`;
  }

  private toPayload(item: GraphDriveItem, name: string, lastModifiedDateTime: string): DriveFilePayload {
    return {
      id: item.id,
      name,
      lastModifiedDateTime,
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
