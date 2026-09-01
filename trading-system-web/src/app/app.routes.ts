import { Routes } from '@angular/router';

// The root component owns the market workspace and reads the active market from
// the URL. These componentless routes let Angular accept direct loads, refreshes
// and browser navigation without requiring a nested router outlet.
export const routes: Routes = [
  { path: '', pathMatch: 'full', children: [] },
  { path: 'nifty', children: [] },
  { path: 'sensex', children: [] },
  { path: 'natural-gas', redirectTo: 'nifty', pathMatch: 'full' },
  { path: '**', redirectTo: '' },
];
