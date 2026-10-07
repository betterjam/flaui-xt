import { Routes } from '@angular/router';
import { TestSuiteComponent } from './components/test-suite.component';

/**
 * One unified app: the master-detail test suite IS the whole UI. Live, Video, and Report all
 * live inside it as tabs - no separate pages or isolated HTML reports.
 */
export const routes: Routes = [
  { path: '', component: TestSuiteComponent },
  { path: '**', redirectTo: '' },
];
