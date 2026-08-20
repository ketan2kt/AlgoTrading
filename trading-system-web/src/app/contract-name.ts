import { WorkspaceTradeOverlay } from './trading-workspace';

export function compactContractName(overlay: WorkspaceTradeOverlay): string {
  const strike = overlay.executionStrike == null ? '' : Math.trunc(overlay.executionStrike).toString();
  const type = overlay.executionInstrumentType === 'PutOption' ? 'PE' :
    overlay.executionInstrumentType === 'CallOption' ? 'CE' : '';
  if (strike && type) return `${strike}${type}`;

  const symbol = overlay.executionInstrument || '';
  const naturalGasContract = symbol.replace(/^(?:NATURALGASMINI|NATURALGAS|NATGASMINI)/i, '');
  return naturalGasContract || symbol || 'Contract';
}
