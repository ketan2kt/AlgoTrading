import { describe, expect, it } from 'vitest';
import { routes } from './app.routes';

describe('market workspace routes', () => {
  it('accepts every supported direct market URL', () => {
    expect(routes.map(route => route.path)).toEqual([
      '',
      'nifty',
      'sensex',
      'natural-gas',
      '**',
    ]);
  });
});
