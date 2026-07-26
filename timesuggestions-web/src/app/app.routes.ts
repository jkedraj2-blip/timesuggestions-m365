import { Routes } from '@angular/router';
import { SuggestionsPage } from './pages/suggestions-page';
import { TimeEntriesPage } from './pages/time-entries-page';
import { CasesPage } from './pages/cases-page';

// Ścieżki po angielsku — spójnie z identyfikatorami w kodzie i endpointami API
// (np. /suggestions ↔ /api/suggestions). Teksty widoczne w UI pozostają polskie.
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'suggestions' },
  { path: 'suggestions', component: SuggestionsPage },
  { path: 'time-entries', component: TimeEntriesPage },
  { path: 'cases', component: CasesPage },
];
