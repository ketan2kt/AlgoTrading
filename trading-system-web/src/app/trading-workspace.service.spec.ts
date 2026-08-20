import { isActiveMarketUpdate } from './trading-workspace.service';

describe('TradingWorkspaceService market isolation', () => {
  it('rejects Nifty updates while Natural Gas is active', () => {
    expect(isActiveMarketUpdate('natural-gas', 'nifty')).toBe(false);
    expect(isActiveMarketUpdate('natural-gas', 'natural-gas')).toBe(true);
  });

  it('rejects stale Natural Gas updates after returning to Nifty', () => {
    expect(isActiveMarketUpdate('nifty', 'natural-gas')).toBe(false);
    expect(isActiveMarketUpdate('nifty', 'nifty')).toBe(true);
  });
});
