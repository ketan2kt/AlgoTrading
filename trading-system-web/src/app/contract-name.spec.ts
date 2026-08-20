import { describe, expect, it } from 'vitest';
import { compactContractName } from './contract-name';
import { WorkspaceTradeOverlay } from './trading-workspace';

function overlay(symbol: string, type = 'Future', strike: number | null = null): WorkspaceTradeOverlay {
  return {
    signalId: '1', strategy: 'test', direction: 'Buy', signalTimeUtc: '', entry: 0,
    stopLoss: 0, target: 0, status: 'Filled', quantity: 1000, fillPrice: 260,
    executionInstrument: symbol, executionInstrumentType: type, executionExpiry: null,
    executionStrike: strike, executionLotSize: 250, executionMaximumLots: 4,
    executionProposedEntry: 260, executionOneLotRisk: null, executionStopLoss: 250,
    executionTarget: 270, executionRiskAmount: null, executionCapitalExposure: null,
    rejectionReasons: [], lifecycleStatus: 'Active', currentOptionPrice: 262,
    exitPrice: null, realisedPnl: null, unrealisedPnl: 2000, entryTimeUtc: null, exitTimeUtc: null,
  };
}

describe('compactContractName', () => {
  it('shows only the Natural Gas Mini futures contract suffix', () => {
    expect(compactContractName(overlay('NATGASMINI26AUG26FUT'))).toBe('26AUG26FUT');
    expect(compactContractName(overlay('NATURALGAS26AUG26FUT'))).toBe('26AUG26FUT');
  });

  it('keeps the compact strike and option type for index options', () => {
    expect(compactContractName(overlay('NIFTY26AUG24300PE', 'PutOption', 24300))).toBe('24300PE');
  });
});
