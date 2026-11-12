import { describe, expect, it } from 'vitest';
import { captureAlertText } from './capture-alert';

describe('compact replay alerts', () => {
  const legacy = 'Feed Interrupted · Recording: Transport interrupted or invalid frame';

  it('does not turn the ambiguous legacy warning into a confirmed gap', () => {
    expect(captureAlertText([legacy])).toBe('Feed warning');
    expect(captureAlertText([legacy, 'Quote recording gap detected'])).toBe('Quote gap detected');
  });

  it('names the affected markets using the same short codes as trade alerts', () => {
    expect(captureAlertText(['NIFTY option price/depth recording missing or stale',
      'SENSEX option price/depth recording missing or stale', 'BANKNIFTY option price/depth recording missing or stale']))
      .toBe('N/S/BN option data stale or missing');
  });
});
