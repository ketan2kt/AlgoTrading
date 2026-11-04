import {describe,expect,it} from 'vitest';
import {inProtectionDeskHours} from './live-protection-monitor.component';

describe('protection badge regular IST hours',()=>{
  it.each([
    ['2026-11-05T01:06:32+05:30',false],
    ['2026-11-05T03:44:59Z',false],
    ['2026-11-05T03:45:00Z',true],
    ['2026-11-05T15:29:59+05:30',true],
    ['2026-11-05T15:30:00+05:30',false],
    ['2026-11-07T10:00:00+05:30',false],
  ] as const)('%s → %s',(instant,expected)=>{
    expect(inProtectionDeskHours(new Date(instant))).toBe(expected);
  });
});
