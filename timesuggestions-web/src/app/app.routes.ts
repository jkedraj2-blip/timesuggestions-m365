import { Routes } from '@angular/router';
import { SuggestionsPage } from './pages/suggestions-page';
import { TimeEntriesPage } from './pages/time-entries-page';
import { CasesPage } from './pages/cases-page';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'sugestie' },
  { path: 'sugestie', component: SuggestionsPage },
  { path: 'wpisy', component: TimeEntriesPage },
  { path: 'sprawy', component: CasesPage },
];
