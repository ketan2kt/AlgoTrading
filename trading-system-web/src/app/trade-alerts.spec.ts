import { describe, expect, it } from 'vitest';
import { tradeAlertTransition } from './trade-alerts';

describe('tradeAlertTransition', () => {
  it('detects entries and exits without repeating unchanged alerts', () => {
    expect(tradeAlertTransition(undefined, 'Active')).toBe('entry');
    expect(tradeAlertTransition('Active', 'Target hit')).toBe('exit');
    expect(tradeAlertTransition('Active', 'SL hit')).toBe('exit');
    expect(tradeAlertTransition('Active', 'Active')).toBeNull();
  });
});
