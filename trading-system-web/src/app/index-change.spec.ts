import { describe, expect, it } from 'vitest';
import { indexChange } from './index-change';
import { TradingWorkspaceSnapshot } from './trading-workspace';

function view(price = 10474, previous = 10000): TradingWorkspaceSnapshot {
  return { observedAtUtc:'2026-11-05T04:01:02Z', isLive:true, isFresh:true,
    previousSessionClose:{price:previous,sessionDate:'2026-11-04'}, candles:[{
      openTimeUtc:'2026-11-05T04:01:00Z',intervalSeconds:60,open:price,high:price,low:price,close:price,volume:0,isClosed:false
    }] } as TradingWorkspaceSnapshot;
}

describe('index day change', () => {
  it.each(['nifty','sensex','bank-nifty'])('updates %s from its own snapshot', market => {
    const snapshot = {...view(),marketCode:market};
    expect(indexChange(snapshot)?.points).toBe(474);
    expect(indexChange(snapshot)?.percent).toBeCloseTo(4.74);
    snapshot.candles[0].close = 9900;
    expect(indexChange(snapshot)?.points).toBe(-100);
    expect(indexChange(snapshot)?.percent).toBe(-1);
  });
  it('handles flat, missing and stale prices honestly', () => {
    expect(indexChange(view(10000))?.points).toBe(0);
    expect(indexChange({...view(),previousSessionClose:null})?.percent).toBeNull();
    expect(indexChange(view(100,0))?.percent).toBeNull();
    expect(indexChange({...view(),isFresh:false})?.fresh).toBe(false);
    expect(indexChange({...view(),candles:[]})?.price).toBeNull();
  });
  it('does not treat a previous session or future candle as the current price', () => {
    const snapshot=view();
    snapshot.candles[0].openTimeUtc='2026-11-04T09:59:00Z';
    expect(indexChange(snapshot)?.price).toBeNull();
    snapshot.candles[0].openTimeUtc='2026-11-05T04:02:00Z';
    expect(indexChange(snapshot)?.price).toBeNull();
  });
});
