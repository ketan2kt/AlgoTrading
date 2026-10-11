import { marketCodeForSnapshot } from './trade-alerts';

describe('TradingWorkspaceService market routing', () => {
  it('identifies every supported market update', () => {
    expect(marketCodeForSnapshot({ exchange: 'NSE' })).toBe('nifty');
    expect(marketCodeForSnapshot({ exchange: 'BSE' })).toBe('sensex');
    expect(marketCodeForSnapshot({ exchange: 'MCX' })).toBe('natural-gas');
    expect(marketCodeForSnapshot({ exchange: 'NSE', marketCode: 'nifty-scalp' })).toBe('nifty-scalp');
    expect(marketCodeForSnapshot({ exchange: 'BSE', marketCode: 'sensex-scalp' })).toBe('sensex-scalp');
  });
});
