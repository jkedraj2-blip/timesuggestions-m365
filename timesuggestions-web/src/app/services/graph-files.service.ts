import { Injectable, inject } from '@angular/core';
import { AuthService } from './auth.service';
import { GraphDeltaResponse, GraphDriveItem } from '../models/graph.models';
import { DriveFilePayload } from '../models/api.models';
import { GRAPH_BASE_URL } from './graph-config';

const ALLOWED_EXTENSIONS = ['.docx', '.doc', '.xlsx', '.xls'];

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
 */
@Injectable({ providedIn: 'root' })
export class GraphFilesService {
  private auth = inject(AuthService);

  async getRecentDocuments(days: number): Promise<DriveFilePayload[]> {
    const since = new Date(Date.now() - days * 24 * 60 * 60 * 1000);
    const items = await this.fetchAllDriveItems();

    return items
      .filter((item) => this.isRecentDocument(item, since))
      .map((item) => this.toPayload(item));
  }

  /** Delta stronicuje wyniki — podążamy za @odata.nextLink aż do końca. */
  private async fetchAllDriveItems(): Promise<GraphDriveItem[]> {
    const token = await this.auth.getToken();
    const select = '$select=id,name,file,lastModifiedDateTime,lastModifiedBy';
    let url: string | undefined = `${GRAPH_BASE_URL}/me/drive/root/delta?${select}`;

    const items: GraphDriveItem[] = [];
    while (url) {
      const response = await fetch(url, {
        headers: { Authorization: `Bearer ${token}` },
      });
      if (!response.ok) {
        throw new Error(`Nie udało się pobrać plików z OneDrive (Graph ${response.status}).`);
      }

      const body: GraphDeltaResponse = await response.json();
      items.push(...body.value);
      url = body['@odata.nextLink'];
    }

    return items;
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
