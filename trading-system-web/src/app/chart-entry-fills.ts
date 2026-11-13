import { WorkspaceCandle, WorkspaceChartEntry, WorkspaceTradeOverlay } from './trading-workspace';

interface ChartEntryFill extends WorkspaceChartEntry {
  at: number;
  time: number;
  evidence?: string[] | null;
}

export function chartEntryFills(
  trades: readonly WorkspaceTradeOverlay[],
  bars: readonly WorkspaceCandle[],
  history: readonly WorkspaceChartEntry[] = [],
): ChartEntryFill[] {
  if (!bars.length) return [];
  const entries = new Map<string, WorkspaceChartEntry & Pick<ChartEntryFill, 'evidence'>>(
    history.map(entry => [entry.tradeId, entry]),
  );
  for (const trade of trades) {
    const contract = trade.executionInstrument?.trim();
    const suffix = contract?.toUpperCase().slice(-2);
    const side = suffix === 'CE' || suffix === 'PE' ? suffix
      : trade.executionInstrumentType === 'CallOption' ? 'CE'
      : trade.executionInstrumentType === 'PutOption' ? 'PE' : null;
    if (trade.fillPrice == null || trade.fillPrice <= 0 || !trade.entryTimeUtc ||
        !Number.isFinite(Date.parse(trade.entryTimeUtc)) || !contract || !side) continue;
    entries.set(trade.signalId, {
      tradeId: trade.signalId, entryTimeUtc: trade.entryTimeUtc, entryPrice: trade.fillPrice,
      contract, side, evidence: trade.entryEvidence,
    });
  }

  const ordered = [...entries.values()].map(entry => ({ ...entry, at: Date.parse(entry.entryTimeUtc) }))
    .filter(entry => Number.isFinite(entry.at) && entry.entryPrice > 0)
    .sort((a, b) => a.at - b.at || a.tradeId.localeCompare(b.tradeId));
  if (!ordered.length) return [];
  const starts = bars.map(bar => Date.parse(bar.openTimeUtc));
  const fills: ChartEntryFill[] = [];
  let barIndex = 0;
  for (const entry of ordered) {
    while (barIndex + 1 < bars.length && starts[barIndex + 1] <= entry.at) barIndex++;
    const start = starts[barIndex];
    if (start <= entry.at && entry.at < start + bars[barIndex].intervalSeconds * 1000) {
      fills.push({ ...entry, time: start / 1000 });
    }
  }
  return fills;
}
