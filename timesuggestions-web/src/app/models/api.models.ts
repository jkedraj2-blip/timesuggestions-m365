/** Kontrakty backendu TimeSuggestions — 1:1 z DTO w C# (enumy serializowane jako camelCase). */

export type SuggestionSource = 'calendar' | 'document';
export type SuggestionStatus = 'pending' | 'approved' | 'rejected';

export interface CalendarEventPayload {
  id: string;
  subject: string | null;
  startDateTime: string;
  endDateTime: string;
  isAllDay: boolean;
  sensitivity: string | null;
}

export interface DriveFilePayload {
  id: string;
  name: string;
  lastModifiedDateTime: string;
  lastModifiedByMe: boolean;
}

export interface SyncRequest {
  calendarEvents: CalendarEventPayload[];
  driveFiles: DriveFilePayload[];
}

export interface SyncResult {
  created: number;
  skippedExisting: number;
}

export interface Suggestion {
  id: number;
  source: SuggestionSource;
  title: string;
  startedAt: string;
  durationMinutes: number;
  caseId: number | null;
  caseName: string | null;
  isAmbiguous: boolean;
  proposedDescription: string;
  status: SuggestionStatus;
}

export interface CaseInfo {
  id: number;
  name: string;
  caseNumber: string;
  clientName: string;
}

export interface ApprovePayload {
  caseId: number;
  durationMinutes: number;
  description: string;
}

export interface TimeEntry {
  id: number;
  caseId: number;
  caseName: string | null;
  entryDate: string;
  durationMinutes: number;
  description: string;
  createdFromSuggestion: boolean;
  source: SuggestionSource;
  suggestionId: number;
}
