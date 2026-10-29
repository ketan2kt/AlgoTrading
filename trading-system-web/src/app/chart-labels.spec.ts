import { describe, expect, it } from 'vitest';
import { chartPriceLineTitles } from './chart-labels';
import { TradingWorkspaceSnapshot } from './trading-workspace';

function snapshot(exchange: string, marketCode?: string): TradingWorkspaceSnapshot {
  return { exchange, marketCode } as TradingWorkspaceSnapshot;
}

describe('chartPriceLineTitles', () => {
  it('uses market-specific index labels', () => {
    expect(chartPriceLineTitles(snapshot('NSE')).entry).toBe('NIFTY TRIGGER');
    expect(chartPriceLineTitles(snapshot('BSE')).entry).toBe('SENSEX TRIGGER');
    expect(chartPriceLineTitles(snapshot('NSE', 'bank-nifty'))).toEqual({
      entry: 'BANK NIFTY TRIGGER', stop: 'BANK NIFTY INVALIDATION', target: 'BANK NIFTY OBJECTIVE',
    });
    expect(chartPriceLineTitles(snapshot('BSE', 'bankex'))).toEqual({
      entry: 'BANKEX TRIGGER', stop: 'BANKEX INVALIDATION', target: 'BANKEX OBJECTIVE',
    });
  });

  it('never labels Natural Gas levels as Nifty', () => {
    expect(chartPriceLineTitles(snapshot('MCX'), 'Buy')).toEqual({
      entry: 'BUY ENTRY', stop: 'STRUCTURAL SL', target: 'OBJECTIVE',
    });
    expect(chartPriceLineTitles(snapshot('MCX'), 'Sell').entry).toBe('SELL ENTRY');
  });
});
