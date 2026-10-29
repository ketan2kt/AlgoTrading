import { describe, expect, it } from 'vitest';
import { marketCodeForSnapshot, tradeAlertTransition } from './trade-alerts';

describe('tradeAlertTransition', () => {
  it('detects entries and exits without repeating unchanged alerts', () => {
    expect(tradeAlertTransition(undefined, 'Active')).toBe('entry');
    expect(tradeAlertTransition('Active', 'Target hit')).toBe('exit');
    expect(tradeAlertTransition('Active', 'SL hit')).toBe('exit');
    expect(tradeAlertTransition('Active', 'Active')).toBeNull();
    expect(tradeAlertTransition('Active', 'FastFailureExit')).toBe('exit');
  });
});

describe('marketCodeForSnapshot', () => {
  it('keeps banking-index alerts separate from Nifty and Sensex', () => {
    expect(marketCodeForSnapshot({ exchange: 'NSE', marketCode: 'bank-nifty' })).toBe('bank-nifty');
    expect(marketCodeForSnapshot({ exchange: 'BSE', marketCode: 'bankex' })).toBe('bankex');
  });
});
