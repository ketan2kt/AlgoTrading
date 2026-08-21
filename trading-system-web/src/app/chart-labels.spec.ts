import { describe, expect, it } from 'vitest';
import { chartPriceLineTitles } from './chart-labels';
import { TradingWorkspaceSnapshot } from './trading-workspace';

function snapshot(exchange: string): TradingWorkspaceSnapshot {
  return { exchange } as TradingWorkspaceSnapshot;
}

describe('chartPriceLineTitles', () => {
  it('uses market-specific index labels', () => {
    expect(chartPriceLineTitles(snapshot('NSE')).entry).toBe('NIFTY TRIGGER');
    expect(chartPriceLineTitles(snapshot('BSE')).entry).toBe('SENSEX TRIGGER');
  });

  it('never labels Natural Gas levels as Nifty', () => {
    expect(chartPriceLineTitles(snapshot('MCX'), 'Buy')).toEqual({
      entry: 'BUY ENTRY', stop: 'STRUCTURAL SL', target: 'OBJECTIVE',
    });
    expect(chartPriceLineTitles(snapshot('MCX'), 'Sell').entry).toBe('SELL ENTRY');
  });
});
