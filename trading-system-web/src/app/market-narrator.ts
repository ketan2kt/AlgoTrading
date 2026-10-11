import { TradingWorkspaceSnapshot, WorkspaceCandle, WorkspaceStrategyEvaluation } from './trading-workspace';
import { TradingMarketCode } from './trade-alerts';

export type NarratorMode = 'quiet' | 'essential' | 'learning';
export type NarratorPriority = 'info' | 'opportunity' | 'warning' | 'critical';
export type NarratorCategory =
  | 'trade'
  | 'ema-vwap'
  | 'structure'
  | 'levels'
  | 'trend'
  | 'gap'
  | 'range-volatility'
  | 'volume-momentum'
  | 'options-oi'
  | 'session'
  | 'risk-system';

export const narratorCategories: { id: NarratorCategory; label: string }[] = [
  { id: 'trade', label: 'Trades and profit protection' },
  { id: 'ema-vwap', label: 'EMA and VWAP' },
  { id: 'structure', label: 'Price action and structure' },
  { id: 'levels', label: 'Highs, lows and key levels' },
  { id: 'trend', label: 'Trend and regime' },
  { id: 'gap', label: 'Opening gaps' },
  { id: 'range-volatility', label: 'Range and volatility' },
  { id: 'volume-momentum', label: 'Volume and momentum' },
  { id: 'options-oi', label: 'Options, OI and IV' },
  { id: 'session', label: 'Session timing' },
  { id: 'risk-system', label: 'Risk, feed and system' },
];

export interface MarketNarratorEvent {
  key: string;
  market: Exclude<TradingMarketCode, 'natural-gas'>;
  category: NarratorCategory;
  priority: NarratorPriority;
  message: string;
  evidence: string;
  occurredAtUtc: string;
}

interface MarketLevels {
  dayHigh: number;
  dayLow: number;
  previousHigh: number | null;
  previousLow: number | null;
  previousClose: number | null;
  openingRangeHigh: number | null;
  openingRangeLow: number | null;
  sessionOpen: number;
}

export function buildMarketNarratorEvents(
  market: Exclude<TradingMarketCode, 'natural-gas'>,
  previous: TradingWorkspaceSnapshot,
  current: TradingWorkspaceSnapshot,
): MarketNarratorEvent[] {
  const name = market === 'nifty' ? 'Nifty' : market === 'sensex' ? 'Sensex' :
    market === 'nifty-scalp' ? 'Nifty Scalp' : 'Sensex Scalp';
  const events: MarketNarratorEvent[] = [];
  const now = current.observedAtUtc;
  const add = (category: NarratorCategory, priority: NarratorPriority, code: string,
    message: string, evidence: string, time = now): void => {
    events.push({ key: `${market}:${category}:${code}`, market, category, priority, message, evidence, occurredAtUtc: time });
  };

  detectTradeEvents(name, previous, current, add);
  detectSystemEvents(name, previous, current, add);
  detectResearchEvents(name, previous, current, add);

  const priorBar = lastClosed(previous.candles);
  const bar = lastClosed(current.candles);
  if (!bar || bar.openTimeUtc === priorBar?.openTimeUtc) return events;

  const closed = current.candles.filter((item) => item.isClosed && Date.parse(item.openTimeUtc) <= Date.parse(bar.openTimeUtc));
  if (closed.length < 3) return events;
  const before = closed.at(-2)!;
  const evaluation = latestEvaluation(current.evaluations, bar.openTimeUtc);
  const previousEvaluation = latestEvaluation(previous.evaluations, priorBar?.openTimeUtc);
  const atr = averageTrueRange(closed.slice(-15));
  const levels = sessionLevels(closed, bar.openTimeUtc);
  const at = bar.openTimeUtc;

  detectAverageEvents(name, closed, before, bar, previousEvaluation, evaluation, atr, add, at);
  detectStructureEvents(name, closed, levels, atr, add, at);
  detectLevelEvents(name, before, bar, levels, atr, add, at);
  detectRegimeEvents(name, previousEvaluation, evaluation, add, at);
  detectGapEvents(name, closed, levels, before, bar, atr, add, at);
  detectRangeAndVolatilityEvents(name, closed, atr, evaluation, add, at);
  detectVolumeAndMomentumEvents(name, closed, current, evaluation, atr, add, at);
  detectSessionEvents(name, priorBar, bar, add, at);
  return events;
}

type AddEvent = (category: NarratorCategory, priority: NarratorPriority, code: string,
  message: string, evidence: string, time?: string) => void;

function detectTradeEvents(name: string, previous: TradingWorkspaceSnapshot,
  current: TradingWorkspaceSnapshot, add: AddEvent): void {
  const old = new Map(previous.overlays.map((trade) => [trade.signalId, trade]));
  for (const trade of current.overlays) {
    const before = old.get(trade.signalId);
    if (trade.fillPrice !== null && (!before || before.fillPrice === null)) {
      add('trade', 'critical', `entry:${trade.signalId}:${trade.entryTimeUtc ?? trade.signalTimeUtc}`,
        `${name} ${spokenDirection(trade.direction)} entry. ${spokenContract(trade.executionInstrument)}. Entry ${price(trade.fillPrice)}. Stop loss ${price(trade.executionStopLoss ?? trade.stopLoss)}.`,
        `${trade.strategy}; quantity ${trade.quantity ?? 'unavailable'}`, trade.entryTimeUtc ?? trade.signalTimeUtc);
    }
    if (before && before.lifecycleStatus !== trade.lifecycleStatus && isClosedStatus(trade.lifecycleStatus)) {
      const result = (trade.realisedPnl ?? 0) >= 0 ? 'profit' : 'loss';
      add('trade', 'critical', `exit:${trade.signalId}:${trade.lifecycleStatus}:${trade.exitTimeUtc}`,
        `${name} exit. ${spokenExit(trade.lifecycleStatus)}. ${result} ${money(Math.abs(trade.realisedPnl ?? 0))}.`,
        `Exit ${price(trade.exitPrice)}; ${trade.strategy}`, trade.exitTimeUtc ?? current.observedAtUtc);
    }
    if (before && trade.fillPrice !== null && before.stopLoss !== trade.stopLoss) {
      const protectedProfit = trade.direction.toLowerCase().includes('sell')
        ? trade.stopLoss < (trade.fillPrice ?? trade.entry)
        : trade.stopLoss > (trade.fillPrice ?? trade.entry);
      add('trade', protectedProfit ? 'opportunity' : 'info', `stop:${trade.signalId}:${trade.stopLoss}`,
        `${name} stop loss moved to ${price(trade.stopLoss)}${protectedProfit ? ', protecting profit' : ''}.`,
        `Previous stop ${price(before.stopLoss)}; current unrealised ${money(trade.unrealisedPnl)}`);
    }
  }
  const oldPnl = previous.paperAutomation.unrealisedPnl;
  const pnl = current.paperAutomation.unrealisedPnl;
  const risk = current.paperAutomation.portfolioRisk?.openRiskAtStops ?? 0;
  for (const threshold of [500, 1000, 2000, 5000, 10000]) {
    if (oldPnl < threshold && pnl >= threshold) {
      add('trade', 'opportunity', `open-pnl:${current.paperAutomation.activeSignalId}:${threshold}`,
        `${name} open profit crossed ${money(threshold)}. Review profit protection.`,
        `Unrealised ${money(pnl)}; risk at stops ${money(risk)}`);
    }
  }
  if (oldPnl >= 500 && pnl < oldPnl * 0.5) {
    add('trade', pnl <= 0 ? 'critical' : 'warning',
      `profit-giveback:${current.paperAutomation.activeSignalId}:${roundBucket(pnl, 250)}`,
      pnl <= 0 ? `${name} open position gave back its profit and is no longer green.` : `${name} open profit has fallen by more than half.`,
      `Previous mark ${money(oldPnl)}; current mark ${money(pnl)}; risk at stops ${money(risk)}`);
  }
}

function detectSystemEvents(name: string, previous: TradingWorkspaceSnapshot,
  current: TradingWorkspaceSnapshot, add: AddEvent): void {
  if (previous.isFresh !== current.isFresh || previous.isLive !== current.isLive || previous.feedStatus !== current.feedStatus) {
    add('risk-system', current.isFresh && current.isLive ? 'info' : 'critical',
      `feed:${current.isLive}:${current.isFresh}:${current.feedStatus}`,
      current.isFresh && current.isLive ? `${name} live feed restored.` : `${name} live feed is stale or disconnected.`,
      current.feedStatus);
  }
  if (previous.paperAutomation.status !== current.paperAutomation.status) {
    add('risk-system', current.paperAutomation.tradingPermitted ? 'info' : 'warning',
      `automation:${current.paperAutomation.status}`,
      `${name} automation status: ${current.paperAutomation.status}.`, current.paperAutomation.message);
  }
  const oldRisk = previous.paperAutomation.portfolioRisk;
  const risk = current.paperAutomation.portfolioRisk;
  if (risk && risk.maximumDailyLoss > 0) {
    const oldRatio = oldRisk ? oldRisk.dailyLossConsumed / oldRisk.maximumDailyLoss : 0;
    const ratio = risk.dailyLossConsumed / risk.maximumDailyLoss;
    for (const threshold of [0.5, 0.75, 0.9]) {
      if (oldRatio < threshold && ratio >= threshold) {
        add('risk-system', threshold >= 0.9 ? 'critical' : 'warning', `daily-risk:${threshold}`,
          `${name} has consumed ${Math.round(threshold * 100)} percent of the daily loss allowance.`,
          `${money(risk.dailyLossConsumed)} of ${money(risk.maximumDailyLoss)}`);
      }
    }
    if (oldRisk?.reconciliationHealthy !== risk.reconciliationHealthy && !risk.reconciliationHealthy) {
      add('risk-system', 'critical', 'reconciliation', `${name} position reconciliation needs attention.`,
        `${risk.openPositions} open positions; ${risk.quoteUnavailablePositions} quotes unavailable`);
    }
  }
  const oldChecks = new Map((previous.paperAutomation.readinessChecks ?? []).map((check) => [check.code, check]));
  for (const check of current.paperAutomation.readinessChecks ?? []) {
    if (!check.ready && oldChecks.get(check.code)?.ready !== false) {
      add('risk-system', 'warning', `readiness:${check.code}:${check.detail}`, `${name} readiness blocked: ${check.label}.`, check.detail);
    }
  }
}

function detectResearchEvents(name: string, previous: TradingWorkspaceSnapshot,
  current: TradingWorkspaceSnapshot, add: AddEvent): void {
  const oldAdvanced = previous.research?.intelligence?.advanced;
  const advanced = current.research?.intelligence?.advanced;
  if (!advanced) return;
  if (oldAdvanced?.options.available && advanced.options.available) {
    const oldPcr = oldAdvanced.options.putCallOiRatio;
    const pcr = advanced.options.putCallOiRatio;
    if (Math.abs(pcr - oldPcr) >= 0.1) {
      add('options-oi', 'opportunity', `pcr:${roundBucket(pcr, 0.1)}`,
        `${name} put call open interest ratio moved to ${pcr.toFixed(2)}${pcr > oldPcr ? ', put support is increasing' : ', call pressure is increasing'}.`,
        advanced.options.evidence);
    }
    if (Math.abs(advanced.options.ivSkew - oldAdvanced.options.ivSkew) >= 1) {
      add('options-oi', 'info', `iv-skew:${roundBucket(advanced.options.ivSkew, 1)}`,
        `${name} option volatility skew changed to ${advanced.options.ivSkew.toFixed(1)}.`, advanced.options.evidence);
    }
  } else if (!oldAdvanced?.options.available && advanced.options.available) {
    add('options-oi', 'info', `options-ready:${advanced.options.contracts}`,
      `${name} options data available. Put call O I ratio ${advanced.options.putCallOiRatio.toFixed(2)}.`, advanced.options.evidence);
  }
  if (oldAdvanced?.volatility.state !== advanced.volatility.state) {
    add('range-volatility', advanced.volatility.state.toLowerCase().includes('high') ? 'warning' : 'info',
      `volatility-state:${advanced.volatility.state}`,
      `${name} volatility regime changed to ${humanize(advanced.volatility.state)}.`,
      `ATR percentile ${advanced.volatility.percentile.toFixed(0)}; expansion ${advanced.volatility.expansionRatio.toFixed(2)}`);
  }
  if (oldAdvanced?.setupLifecycle.phase !== advanced.setupLifecycle.phase) {
    add('trend', 'opportunity', `setup:${advanced.setupLifecycle.phase}:${advanced.setupLifecycle.direction}`,
      `${name} setup phase is now ${humanize(advanced.setupLifecycle.phase)}${advanced.setupLifecycle.direction ? `, ${spokenDirection(advanced.setupLifecycle.direction)}` : ''}.`,
      advanced.setupLifecycle.evidence);
  }
  const oldPremium = latestEvaluation(previous.evaluations)?.optionPremium;
  const premium = latestEvaluation(current.evaluations)?.optionPremium;
  if (oldPremium && premium && oldPremium > 0) {
    const change = (premium - oldPremium) / oldPremium;
    if (Math.abs(change) >= 0.2) {
      add('options-oi', change > 0 ? 'opportunity' : 'warning',
        `premium-jump:${latestEvaluation(current.evaluations)?.optionSymbol}:${roundBucket(change, 0.1)}`,
        `${name} selected option premium ${change > 0 ? 'jumped' : 'fell'} ${Math.round(Math.abs(change) * 100)} percent to ${price(premium)}.`,
        `Previous premium ${price(oldPremium)}`);
    }
  }
  for (const level of advanced.levels) {
    const wasKnown = oldAdvanced?.levels.some((item) => item.kind === level.kind && Math.abs(item.price - level.price) < 0.01);
    if (!wasKnown && level.strength >= 0.7) {
      add('levels', 'info', `research-level:${level.kind}:${level.price}`,
        `${name} strong ${humanize(level.kind)} level identified near ${price(level.price)}.`, level.evidence);
    }
  }
}

function detectAverageEvents(name: string, candles: WorkspaceCandle[], before: WorkspaceCandle, bar: WorkspaceCandle,
  previousEvaluation: WorkspaceStrategyEvaluation | null, evaluation: WorkspaceStrategyEvaluation | null,
  atr: number, add: AddEvent, at: string): void {
  if (!evaluation) return;
  const previousVwap = previousEvaluation?.vwap ?? evaluation.vwap;
  crossed(name, 'VWAP', before.close, bar.close, previousVwap, evaluation.vwap, 'ema-vwap', add, at);
  const previousFast = previousEvaluation?.fastEma ?? evaluation.fastEma;
  const previousSlow = previousEvaluation?.slowEma ?? evaluation.slowEma;
  crossed(name, 'EMA 9', before.close, bar.close, previousFast, evaluation.fastEma, 'ema-vwap', add, at);
  crossed(name, 'EMA 21', before.close, bar.close, previousSlow, evaluation.slowEma, 'ema-vwap', add, at);
  if (previousFast <= previousSlow && evaluation.fastEma > evaluation.slowEma) {
    add('ema-vwap', 'opportunity', `ema-bull:${at}`, `${name} bullish EMA 9 and EMA 21 crossover confirmed.`,
      `EMA 9 ${price(evaluation.fastEma)}; EMA 21 ${price(evaluation.slowEma)}`, at);
  } else if (previousFast >= previousSlow && evaluation.fastEma < evaluation.slowEma) {
    add('ema-vwap', 'opportunity', `ema-bear:${at}`, `${name} bearish EMA 9 and EMA 21 crossover confirmed.`,
      `EMA 9 ${price(evaluation.fastEma)}; EMA 21 ${price(evaluation.slowEma)}`, at);
  }
  if (atr > 0 && Math.abs(bar.close - evaluation.vwap) >= atr * 1.5 && Math.abs(before.close - previousVwap) < atr * 1.5) {
    add('ema-vwap', 'warning', `vwap-extension:${side(bar.close - evaluation.vwap)}:${at}`,
      `${name} is extended ${side(bar.close - evaluation.vwap)} VWAP by more than one and a half A T R.`,
      `Price ${price(bar.close)}; VWAP ${price(evaluation.vwap)}; ATR ${price(atr)}`, at);
  }
  const ema50 = exponentialMovingAverage(candles, 50);
  const ema200 = exponentialMovingAverage(candles, 200);
  if (ema50) crossed(name, 'EMA 50', before.close, bar.close, ema50.previous, ema50.current, 'ema-vwap', add, at);
  if (ema200) crossed(name, 'EMA 200', before.close, bar.close, ema200.previous, ema200.current, 'ema-vwap', add, at);
}

function detectStructureEvents(name: string, candles: WorkspaceCandle[], levels: MarketLevels,
  atr: number, add: AddEvent, at: string): void {
  const [a, b, c] = candles.slice(-3);
  if (a.high < b.high && b.high < c.high && a.low < b.low && b.low < c.low) {
    add('structure', 'opportunity', `higher-structure:${at}`, `${name} confirmed higher highs and higher lows.`,
      `Last three closes ${price(a.close)}, ${price(b.close)}, ${price(c.close)}`, at);
  }
  if (a.high > b.high && b.high > c.high && a.low > b.low && b.low > c.low) {
    add('structure', 'opportunity', `lower-structure:${at}`, `${name} confirmed lower highs and lower lows.`,
      `Last three closes ${price(a.close)}, ${price(b.close)}, ${price(c.close)}`, at);
  }
  const body = Math.abs(c.close - c.open);
  const upperWick = c.high - Math.max(c.open, c.close);
  const lowerWick = Math.min(c.open, c.close) - c.low;
  if (atr > 0 && upperWick > Math.max(body * 1.8, atr * 0.35)) {
    add('structure', 'warning', `upper-rejection:${at}`, `${name} upper-price rejection candle confirmed.`,
      `High ${price(c.high)}; close ${price(c.close)}`, at);
  }
  if (atr > 0 && lowerWick > Math.max(body * 1.8, atr * 0.35)) {
    add('structure', 'opportunity', `lower-rejection:${at}`, `${name} lower-price rejection candle confirmed.`,
      `Low ${price(c.low)}; close ${price(c.close)}`, at);
  }
  const lastSix = candles.slice(-6);
  const green = lastSix.filter((item) => item.close > item.open).length;
  if (green >= 5) add('structure', 'opportunity', `impulse-up:${at}`, `${name} bullish impulse: five of the last six candles are green.`, `Close ${price(c.close)}`, at);
  if (green <= 1) add('structure', 'opportunity', `impulse-down:${at}`, `${name} bearish impulse: five of the last six candles are red.`, `Close ${price(c.close)}`, at);
  if (levels.openingRangeHigh && b.close > levels.openingRangeHigh && c.low <= levels.openingRangeHigh && c.close > levels.openingRangeHigh) {
    add('structure', 'opportunity', `or-retest-up:${at}`, `${name} opening-range breakout retest held on the upside.`, `Opening range high ${price(levels.openingRangeHigh)}`, at);
  }
  if (levels.openingRangeLow && b.close < levels.openingRangeLow && c.high >= levels.openingRangeLow && c.close < levels.openingRangeLow) {
    add('structure', 'opportunity', `or-retest-down:${at}`, `${name} opening-range breakdown retest held on the downside.`, `Opening range low ${price(levels.openingRangeLow)}`, at);
  }
  const prior = candles.slice(-11, -1);
  if (prior.length >= 5) {
    const priorHigh = Math.max(...prior.map((item) => item.high));
    const priorLow = Math.min(...prior.map((item) => item.low));
    if (c.close > priorHigh) {
      add('structure', 'opportunity', `local-breakout:${at}`, `${name} closed above its recent swing range. Bullish change of character.`, `Prior high ${price(priorHigh)}`, at);
    } else if (c.close < priorLow) {
      add('structure', 'opportunity', `local-breakdown:${at}`, `${name} closed below its recent swing range. Bearish change of character.`, `Prior low ${price(priorLow)}`, at);
    }
    if (b.high > priorHigh && c.close < priorHigh) {
      add('structure', 'warning', `failed-breakout:${at}`, `${name} upside breakout failed and price closed back inside the range.`, `Range high ${price(priorHigh)}`, at);
    }
    if (b.low < priorLow && c.close > priorLow) {
      add('structure', 'warning', `failed-breakdown:${at}`, `${name} downside breakdown failed and price closed back inside the range.`, `Range low ${price(priorLow)}`, at);
    }
  }
}

function detectLevelEvents(name: string, before: WorkspaceCandle, bar: WorkspaceCandle,
  levels: MarketLevels, atr: number, add: AddEvent, at: string): void {
  const candidates: [string, number | null][] = [
    ['previous high', levels.previousHigh], ['previous low', levels.previousLow],
    ['previous close', levels.previousClose], ['opening range high', levels.openingRangeHigh],
    ['opening range low', levels.openingRangeLow],
  ];
  for (const [label, level] of candidates) {
    if (level === null) continue;
    if (before.close <= level && bar.close > level) {
      add('levels', 'opportunity', `break-above:${label}:${at}`, `${name} closed above ${label} at ${price(level)}.`, `Close ${price(bar.close)}`, at);
    } else if (before.close >= level && bar.close < level) {
      add('levels', 'opportunity', `break-below:${label}:${at}`, `${name} closed below ${label} at ${price(level)}.`, `Close ${price(bar.close)}`, at);
    } else if (atr > 0 && bar.low <= level && bar.close > level && before.close > level) {
      add('levels', 'info', `support-reaction:${label}:${at}`, `${name} reacted from ${label}; support held near ${price(level)}.`, `Low ${price(bar.low)}; close ${price(bar.close)}`, at);
    } else if (atr > 0 && bar.high >= level && bar.close < level && before.close < level) {
      add('levels', 'info', `resistance-reaction:${label}:${at}`, `${name} reacted from ${label}; resistance held near ${price(level)}.`, `High ${price(bar.high)}; close ${price(bar.close)}`, at);
    }
  }
  if (bar.high >= levels.dayHigh && before.high < levels.dayHigh) {
    add('levels', 'opportunity', `day-high:${levels.dayHigh}:${at}`, `${name} made a new session high at ${price(levels.dayHigh)}.`, `Close ${price(bar.close)}`, at);
  }
  if (bar.low <= levels.dayLow && before.low > levels.dayLow) {
    add('levels', 'opportunity', `day-low:${levels.dayLow}:${at}`, `${name} made a new session low at ${price(levels.dayLow)}.`, `Close ${price(bar.close)}`, at);
  }
  const roundStep = name.startsWith('Nifty') ? 50 : 100;
  const round = Math.round(bar.close / roundStep) * roundStep;
  if (Math.abs(bar.close - round) <= Math.max(atr * 0.15, roundStep * 0.04)) {
    add('levels', 'info', `round:${round}:${sessionKey(at)}`, `${name} is reacting near the round-number level ${round}.`, `Close ${price(bar.close)}`, at);
  }
}

function detectRegimeEvents(name: string, previous: WorkspaceStrategyEvaluation | null,
  current: WorkspaceStrategyEvaluation | null, add: AddEvent, at: string): void {
  if (!current) return;
  if (!previous || previous.regime !== current.regime || previous.regimeBias !== current.regimeBias) {
    add('trend', current.regime.toLowerCase().includes('trend') ? 'opportunity' : 'info',
      `regime:${current.regime}:${current.regimeBias}`,
      `${name} market regime is ${humanize(current.regime)}${current.regimeBias ? ` with ${humanize(current.regimeBias)} bias` : ''}.`,
      `Confidence ${Math.round(current.regimeConfidence * 100)} percent`, at);
  }
}

function detectGapEvents(name: string, candles: WorkspaceCandle[], levels: MarketLevels,
  before: WorkspaceCandle, bar: WorkspaceCandle, atr: number, add: AddEvent, at: string): void {
  if (levels.previousClose === null || atr <= 0) return;
  const gap = levels.sessionOpen - levels.previousClose;
  if (Math.abs(gap) >= atr * 0.5 && isFirstSessionBar(candles, bar)) {
    add('gap', 'info', `gap-open:${sessionKey(at)}`, `${name} opened with a ${gap > 0 ? 'gap up' : 'gap down'} of ${price(Math.abs(gap))} points.`,
      `Open ${price(levels.sessionOpen)}; previous close ${price(levels.previousClose)}`, at);
  }
  const wasUnfilled = gap > 0 ? before.low > levels.previousClose : before.high < levels.previousClose;
  const isFilled = gap > 0 ? bar.low <= levels.previousClose : bar.high >= levels.previousClose;
  if (wasUnfilled && isFilled) {
    add('gap', 'opportunity', `gap-fill:${sessionKey(at)}`, `${name} opening gap has filled at the previous close.`,
      `Previous close ${price(levels.previousClose)}`, at);
  }
}

function detectRangeAndVolatilityEvents(name: string, candles: WorkspaceCandle[], atr: number,
  evaluation: WorkspaceStrategyEvaluation | null, add: AddEvent, at: string): void {
  if (atr <= 0) return;
  const bar = candles.at(-1)!;
  const recent = candles.slice(-8);
  const range = Math.max(...recent.map((item) => item.high)) - Math.min(...recent.map((item) => item.low));
  if (range <= atr * 1.6) {
    add('range-volatility', 'warning', `tight-range:${at}`, `${name} is compressed in a tight eight-candle range.`,
      `Range ${price(range)}; ATR ${price(atr)}`, at);
  }
  const candleRange = bar.high - bar.low;
  if (candleRange >= atr * 1.8) {
    add('range-volatility', 'warning', `range-expansion:${at}`, `${name} range expansion candle is ${candleRange >= atr * 2.5 ? 'very large' : 'large'}.`,
      `${(candleRange / atr).toFixed(1)} ATR`, at);
  }
  if (evaluation) {
    const reference = evaluation.vwap || evaluation.slowEma;
    const extension = Math.abs(bar.close - reference) / atr;
    if (extension >= 2) {
      add('range-volatility', 'warning', `mature-move:${side(bar.close - reference)}:${at}`,
        `${name} move is mature and extended ${extension.toFixed(1)} A T R ${side(bar.close - reference)} its reference. Avoid chasing.`,
        `Price ${price(bar.close)}; reference ${price(reference)}`, at);
    }
  }
}

function detectVolumeAndMomentumEvents(name: string, candles: WorkspaceCandle[], current: TradingWorkspaceSnapshot,
  evaluation: WorkspaceStrategyEvaluation | null, atr: number, add: AddEvent, at: string): void {
  const bar = candles.at(-1)!;
  const comparable = candles.slice(-21, -1).map((item) => item.volume).filter((value) => value > 0);
  const averageVolume = comparable.length ? comparable.reduce((sum, value) => sum + value, 0) / comparable.length : 0;
  const futuresVolume = current.futuresVolume?.find((item) => item.openTimeUtc === bar.openTimeUtc)?.volume ?? 0;
  const observedVolume = futuresVolume || bar.volume;
  if (averageVolume > 0 && observedVolume >= averageVolume * 1.8) {
    add('volume-momentum', 'opportunity', `volume-spike:${at}`, `${name} volume spike confirmed at ${(observedVolume / averageVolume).toFixed(1)} times average.`,
      `Volume ${Math.round(observedVolume)}; average ${Math.round(averageVolume)}`, at);
  } else if (evaluation && evaluation.relativeFuturesVolume >= 1.5) {
    add('volume-momentum', 'opportunity', `relative-volume:${at}`, `${name} futures volume is ${evaluation.relativeFuturesVolume.toFixed(1)} times normal.`,
      `Price candle ${bar.close > bar.open ? 'green' : 'red'}`, at);
  }
  const body = Math.abs(bar.close - bar.open);
  if (atr > 0 && body >= atr * 1.1) {
    add('volume-momentum', 'opportunity', `momentum:${side(bar.close - bar.open)}:${at}`,
      `${name} strong ${bar.close > bar.open ? 'bullish' : 'bearish'} momentum candle confirmed.`,
      `Body ${(body / atr).toFixed(1)} ATR; relative volume ${evaluation?.relativeFuturesVolume?.toFixed(2) ?? 'unavailable'}`, at);
  }
  const lastFour = candles.slice(-4);
  const sameDirection = lastFour.every((item) => item.close > item.open) || lastFour.every((item) => item.close < item.open);
  if (sameDirection) {
    add('volume-momentum', 'info', `four-candle-momentum:${side(bar.close - bar.open)}:${at}`,
      `${name} has four consecutive ${bar.close > bar.open ? 'green' : 'red'} candles.`, `Close ${price(bar.close)}`, at);
  }
}

function detectSessionEvents(name: string, previous: WorkspaceCandle | null, current: WorkspaceCandle,
  add: AddEvent, at: string): void {
  const minute = istMinute(current.openTimeUtc);
  const priorMinute = previous ? istMinute(previous.openTimeUtc) : -1;
  const milestones: [number, string, string][] = [
    [570, 'opening-range-complete', 'the first 15-minute opening range is complete'],
    [720, 'lunch-window', 'the lunch-time range window has started'],
    [870, 'power-window', 'the closing power-move window has started'],
    [900, 'late-entry-cutoff', 'the late-session entry caution window has started'],
    [915, 'new-entry-stop', 'the no-new-entry time has arrived'],
  ];
  for (const [target, code, message] of milestones) {
    if (priorMinute < target && minute >= target) add('session', target >= 900 ? 'warning' : 'info', `${code}:${sessionKey(at)}`, `${name}: ${message}.`, `IST minute ${minute}`, at);
  }
}

function crossed(name: string, label: string, beforePrice: number, priceNow: number,
  beforeLine: number, line: number, category: NarratorCategory, add: AddEvent, at: string): void {
  if (beforePrice <= beforeLine && priceNow > line) {
    add(category, 'opportunity', `${label}:above:${at}`, `${name} closed above ${label} at ${price(line)}.`, `Close ${price(priceNow)}`, at);
  } else if (beforePrice >= beforeLine && priceNow < line) {
    add(category, 'opportunity', `${label}:below:${at}`, `${name} closed below ${label} at ${price(line)}.`, `Close ${price(priceNow)}`, at);
  }
}

function lastClosed(candles: WorkspaceCandle[]): WorkspaceCandle | null {
  return [...candles].reverse().find((item) => item.isClosed) ?? null;
}

function latestEvaluation(evaluations: WorkspaceStrategyEvaluation[], time?: string): WorkspaceStrategyEvaluation | null {
  const limit = time ? Date.parse(time) : Number.POSITIVE_INFINITY;
  return [...evaluations].reverse().find((item) => Date.parse(item.candleTimeUtc) <= limit) ?? null;
}

function averageTrueRange(candles: WorkspaceCandle[]): number {
  if (candles.length < 2) return 0;
  let sum = 0;
  for (let index = 1; index < candles.length; index++) {
    const current = candles[index];
    const previous = candles[index - 1];
    sum += Math.max(current.high - current.low, Math.abs(current.high - previous.close), Math.abs(current.low - previous.close));
  }
  return sum / (candles.length - 1);
}

function sessionLevels(candles: WorkspaceCandle[], time: string): MarketLevels {
  const session = sessionKey(time);
  const sessions = new Map<string, WorkspaceCandle[]>();
  for (const candle of candles) {
    const key = sessionKey(candle.openTimeUtc);
    const items = sessions.get(key) ?? [];
    items.push(candle);
    sessions.set(key, items);
  }
  const current = sessions.get(session) ?? [candles.at(-1)!];
  const priorKey = [...sessions.keys()].filter((key) => key < session).sort().at(-1);
  const prior = priorKey ? sessions.get(priorKey) ?? [] : [];
  const opening = current.filter((item) => istMinute(item.openTimeUtc) < 570);
  return {
    dayHigh: Math.max(...current.map((item) => item.high)), dayLow: Math.min(...current.map((item) => item.low)),
    previousHigh: prior.length ? Math.max(...prior.map((item) => item.high)) : null,
    previousLow: prior.length ? Math.min(...prior.map((item) => item.low)) : null,
    previousClose: prior.at(-1)?.close ?? null,
    openingRangeHigh: opening.length ? Math.max(...opening.map((item) => item.high)) : null,
    openingRangeLow: opening.length ? Math.min(...opening.map((item) => item.low)) : null,
    sessionOpen: current[0].open,
  };
}

function isFirstSessionBar(candles: WorkspaceCandle[], bar: WorkspaceCandle): boolean {
  const key = sessionKey(bar.openTimeUtc);
  return candles.find((item) => sessionKey(item.openTimeUtc) === key)?.openTimeUtc === bar.openTimeUtc;
}

function sessionKey(time: string): string {
  const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(new Date(time));
  const value = Object.fromEntries(parts.map((item) => [item.type, item.value]));
  return `${value['year']}-${value['month']}-${value['day']}`;
}

function exponentialMovingAverage(candles: WorkspaceCandle[], period: number): { previous: number; current: number } | null {
  // The caller can pass the full series in future; do not announce a long EMA until enough evidence exists.
  if (candles.length < period + 1) return null;
  const multiplier = 2 / (period + 1);
  let ema = candles.slice(0, period).reduce((sum, item) => sum + item.close, 0) / period;
  let previous = ema;
  for (let index = period; index < candles.length; index++) {
    previous = ema;
    ema = (candles[index].close - ema) * multiplier + ema;
  }
  return { previous, current: ema };
}

function istMinute(time: string): number {
  const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', hour12: false }).formatToParts(new Date(time));
  const value = Object.fromEntries(parts.map((item) => [item.type, item.value]));
  return Number(value['hour']) * 60 + Number(value['minute']);
}

function isClosedStatus(status: string): boolean {
  return ['SL hit', 'Target hit', 'Time exit', 'Emergency exit', 'Trend reversal exit', 'Closed',
    'StopLossHit', 'TargetHit', 'FastFailureExit', 'StaleExit', 'TimeExit'].includes(status);
}

function spokenExit(status: string): string {
  if (status === 'SL hit') return 'stop loss hit';
  if (status === 'Target hit') return 'target or profit objective reached';
  if (status === 'StopLossHit') return 'stop loss hit';
  if (status === 'TargetHit') return 'target or profit objective reached';
  if (status === 'FastFailureExit') return 'fast failure exit';
  return humanize(status);
}

function spokenDirection(direction: string): string {
  const value = direction.toLowerCase();
  if (value.includes('sell') || value.includes('bear') || value.includes('put')) return 'bearish or put-side';
  return 'bullish or call-side';
}

function spokenContract(contract: string | null): string {
  return contract ? `Contract ${contract.replace(/([A-Z])/g, '$1 ')}` : 'Selected option contract';
}

function side(value: number): string { return value >= 0 ? 'above' : 'below'; }
function price(value: number | null | undefined): string { return value === null || value === undefined ? 'unavailable' : value.toFixed(2); }
function money(value: number | null | undefined): string { return value === null || value === undefined ? 'unavailable' : `${Math.round(value)} rupees`; }
function humanize(value: string): string { return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[-_]/g, ' ').toLowerCase(); }
function roundBucket(value: number, step: number): number { return Math.round(value / step) * step; }
