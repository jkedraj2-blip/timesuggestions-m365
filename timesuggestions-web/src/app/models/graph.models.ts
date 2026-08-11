/** Kształty odpowiedzi Microsoft Graph — tylko pola, których używamy. */

export interface GraphDateTime {
  dateTime: string;
  timeZone: string;
}

export interface GraphEvent {
  id: string;
  subject: string;
  start: GraphDateTime;
  end: GraphDateTime;
  isAllDay: boolean;
  sensitivity: string;
  isCancelled: boolean;
}

/** Stronicowana odpowiedź widoku kalendarza. */
export interface GraphCalendarResponse {
  value: GraphEvent[];
  '@odata.nextLink'?: string;
}

/**
 * Element dysku z zapytania delta — pliki, foldery i tombstone'y (facet `deleted`
 * dla elementów usuniętych; taki wpis może nie mieć nazwy ani daty modyfikacji).
 */
export interface GraphDriveItem {
  id: string;
  name?: string;
  file?: { mimeType: string };
  deleted?: { state?: string };
  lastModifiedDateTime?: string;
  lastModifiedBy?: {
    user?: { displayName?: string; email?: string; id?: string };
  };
}

export interface GraphDeltaResponse {
  value: GraphDriveItem[];
  '@odata.nextLink'?: string;
  '@odata.deltaLink'?: string;
}

/** Jedna wersja pliku z historii driveItem (GET /me/drive/items/{id}/versions). */
export interface GraphDriveItemVersion {
  id: string;
  lastModifiedDateTime?: string;
  size?: number;
}

/** Stronicowana odpowiedź historii wersji pliku. */
export interface GraphVersionsResponse {
  value: GraphDriveItemVersion[];
  '@odata.nextLink'?: string;
}
