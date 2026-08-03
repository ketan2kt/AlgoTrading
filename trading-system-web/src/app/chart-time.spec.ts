import { describe, expect, it } from 'vitest';
import { Time } from 'lightweight-charts';
import { formatChartTimeIst, formatCrosshairTimeIst } from './chart-time';

describe('IST chart formatting', () => {
  it('formats UTC chart timestamps explicitly in Asia/Kolkata', () => {
    const epochSeconds = (Date.parse('2026-08-03T03:45:00Z') / 1000) as Time;

    expect(formatChartTimeIst(epochSeconds)).toBe('09:15');
    expect(formatCrosshairTimeIst(epochSeconds)).toContain('09:15 IST');
  });
});
