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
}

/** Element dysku z zapytania delta — pliki i foldery; foldery odfiltrowujemy po polu `file`. */
export interface GraphDriveItem {
  id: string;
  name: string;
  file?: { mimeType: string };
  lastModifiedDateTime: string;
  lastModifiedBy?: {
    user?: { displayName?: string; email?: string; id?: string };
  };
}

export interface GraphDeltaResponse {
  value: GraphDriveItem[];
  '@odata.nextLink'?: string;
  '@odata.deltaLink'?: string;
}
