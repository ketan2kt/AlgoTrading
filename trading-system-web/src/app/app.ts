import { AsyncPipe, DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { ChangeDetectorRef, Component, inject, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterOutlet } from '@angular/router';
import { catchError, of, startWith, Subscription } from 'rxjs';
import { AuthService, CurrentUser } from './auth.service';
import { NiftyChartComponent } from './nifty-chart.component';
import { PublicHomeComponent } from './public-home.component';
import { PreSessionBriefComponent } from './pre-session-brief.component';
import { GrowwTokenService, GrowwTokenStatus } from './groww-token.service';
import { SystemStatusService } from './system-status.service';
import { mergeWorkspaceSnapshot, OperationalAutomationReport, TradingWorkspaceSnapshot, WorkspaceTradeOverlay } from './trading-workspace';
import { compactContractName } from './contract-name';
import { marketCodeForSnapshot, tradeAlertTransition, TradeAlertKind, TradingMarketCode } from './trade-alerts';
import { TradingWorkspaceService } from './trading-workspace.service';
import { PaperRiskService } from './paper-risk.service';
import { GrowwLivePosition, GrowwPositionsService } from './groww-positions.service';
import { DailyDiagnostics, PaperPnlSummary, PaperReportService, PaperTradingReport, PaperTradeHistoryItem } from './paper-report.service';
import { HeroZeroMonitor, HeroZeroService } from './hero-zero.service';
import { LiveExecutionService, LiveExecutionStatus } from './live-execution.service';
import { TradingLotConfiguration, TradingLotsService } from './trading-lots.service';
import {
  buildMarketNarratorEvents,
  MarketNarratorEvent,
  NarratorCategory,
  narratorCategories,
  NarratorMode,
  selectHighSignalNarratorEvents,
} from './market-narrator';

@Component({
  selector: 'app-root',
  imports: [AsyncPipe, DatePipe, DecimalPipe, PercentPipe, FormsModule, NiftyChartComponent, PublicHomeComponent, PreSessionBriefComponent, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  private readonly statusService = inject(SystemStatusService);
  private readonly auth = inject(AuthService);
  private readonly workspaceService = inject(TradingWorkspaceService);
  private readonly growwTokenService = inject(GrowwTokenService);
  private readonly changeDetector = inject(ChangeDetectorRef);
  private readonly paperRisk = inject(PaperRiskService);
  private readonly growwPositionsService = inject(GrowwPositionsService);
  private readonly paperReportService = inject(PaperReportService);
  private readonly heroZeroService = inject(HeroZeroService);
  private readonly liveExecutionService = inject(LiveExecutionService);
  private readonly tradingLotsService = inject(TradingLotsService);
  private readonly subscriptions = new Subscription();

  protected user: CurrentUser | null = null;
  protected checkingSession = true;
  protected loginBusy = false;
  protected loginError = '';
  protected username = '';
  protected password = '';
  protected workspace: TradingWorkspaceSnapshot | null = null;
  protected workspaceError = '';
  protected tokenStatus: GrowwTokenStatus = {
    isConfigured: false,
    isExpired: false,
    expiresAtUtc: null,
    updatedAtUtc: null,
    source: 'Loading',
  };
  protected tokenStatusLoading = true;
  protected growwAccessToken = '';
  protected tokenBusy = false;
  protected tokenError = '';
  protected tokenSuccess = '';
  protected instrumentSyncBusy = false;
  protected showTokenForm = false;
  protected killSwitchActive = false;
  protected killSwitchBusy = false;
  protected killSwitchMessage = '';
  protected dailyLossOverrideActive = false;
  protected dailyLossOverrideBusy = false;
  protected dailyLossOverrideMessage = '';
  protected logsOpen = false;
  protected growwPositionsOpen = false;
  protected growwPositionsLoading = false;
  protected growwPositionsError = '';
  protected growwPositions: GrowwLivePosition[] = [];
  protected growwPositionsObservedAt = '';
  protected reportOpen = false;
  protected reportLoading = false;
  protected reportError = '';
  protected report: PaperTradingReport | null = null;
  protected dailyAnalysisOpen = false;
  protected preSessionOpen = false;
  protected dailyAnalysisLoading = false;
  protected dailyAnalysisError = '';
  protected dailyAnalysis: DailyDiagnostics | null = null;
  protected dailyAnalysisDate = this.isoDateDaysAgo(0);
  protected dailyAnalysisPromptCopied = false;
  protected dailyAnalysisTradesCopied = false;
  protected dailyAnalysisTradesCopyError = false;
  protected pnlReportOpen = false;
  protected pnlReportLoading = false;
  protected pnlReportError = '';
  protected pnlSummary: PaperPnlSummary | null = null;
  protected pnlFrom = this.isoDateDaysAgo(30);
  protected pnlTo = this.isoDateDaysAgo(0);
  protected pnlMarket = 'all';
  protected tradeFilter = '';
  protected heroZeroOpen = false;
  protected heroZeroLoading = false;
  protected heroZeroError = '';
  protected heroZero: HeroZeroMonitor | null = null;
  private heroZeroPollTimer: ReturnType<typeof setInterval> | null = null;
  protected controlsOpen = false;
  protected liveExecution: LiveExecutionStatus | null = null;
  protected liveExecutionBusy = false;
  protected liveExecutionPassword = '';
  protected liveExecutionReason = 'Daily automatic Nifty and Sensex live trading activation.';
  protected liveExecutionMessage = '';
  protected tradingLots: TradingLotConfiguration = { niftyLots: 10, sensexLots: 10, updatedAtUtc: null };
  protected tradingLotsBusy = false;
  protected tradingLotsMessage = '';
  private tradeStates = new Map<string, string>();
  private initializedTradeMarkets = new Set<TradingMarketCode>();
  protected marketTabAlerts = new Map<TradingMarketCode, TradeAlertKind>();
  private marketTabAlertTimers = new Map<TradingMarketCode, ReturnType<typeof setTimeout>>();
  private readonly armTradeAlerts = (): void => this.ensureTradeAlertsReady();
  protected narratorOpen = false;
  protected narratorMode: NarratorMode = this.readNarratorMode();
  protected narratorVoiceEnabled = this.readNarratorVoiceEnabled();
  protected readonly narratorCategories = narratorCategories;
  protected enabledNarratorCategories = this.readNarratorCategories();
  protected narratorTranscript: MarketNarratorEvent[] = [];
  protected narratorToasts: MarketNarratorEvent[] = [];
  private narratorSpeechArmed = false;
  private narratorSnapshots = new Map<Exclude<TradingMarketCode, 'natural-gas'>, TradingWorkspaceSnapshot>();
  private narratedKeys = new Map<string, number>();
  private narratorToastTimers = new Map<string, ReturnType<typeof setTimeout>>();
  protected chartTimeframeMinutes = this.readChartTimeframe();
  protected readonly chartTimeframes = [1, 5, 15];
  protected selectedMarket: TradingMarketCode | null = this.marketFromPath();
  private readonly marketNavigation = (): void => {
    const market = this.marketFromPath();
    if (market === this.selectedMarket) return;
    this.selectedMarket = market;
    this.workspace = null;
    if (market !== null) {
      this.loadWorkspace();
      void this.workspaceService.connect(market).catch(() => {
        this.workspaceError = 'Real-time dashboard connection is unavailable; manual refresh remains available.';
        this.refreshView();
      });
    }
    this.refreshView();
  };

  protected readonly status$ = this.statusService.getCurrent().pipe(
    startWith({
      mode: 'Paper' as const,
      liveTradingAvailable: false,
      tradingEnabled: false,
      status: 'Loading',
      observedAtUtc: '',
    }),
    catchError(() =>
      of({
        mode: 'Paper' as const,
        liveTradingAvailable: false,
        tradingEnabled: false,
        status: 'Unavailable',
        observedAtUtc: '',
      }),
    ),
  );

  ngOnInit(): void {
    this.ensureTradeAlertsReady();
    document.addEventListener('pointerdown', this.armTradeAlerts, { once: true, capture: true });
    document.addEventListener('keydown', this.armTradeAlerts, { once: true, capture: true });
    window.addEventListener('popstate', this.marketNavigation);
    this.subscriptions.add(
      this.auth.currentUser().subscribe({
        next: (user) => {
          this.user = user;
          this.checkingSession = false;
          if (user && this.selectedMarket !== null) this.startWorkspace();
          this.refreshView();
        },
        error: () => {
          this.checkingSession = false;
          this.loginError = 'Unable to verify the application session.';
          this.refreshView();
        },
      }),
    );
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
    document.removeEventListener('pointerdown', this.armTradeAlerts, { capture: true });
    document.removeEventListener('keydown', this.armTradeAlerts, { capture: true });
    window.removeEventListener('popstate', this.marketNavigation);
    this.marketTabAlertTimers.forEach((timer) => clearTimeout(timer));
    this.narratorToastTimers.forEach((timer) => clearTimeout(timer));
    if (this.heroZeroPollTimer) clearInterval(this.heroZeroPollTimer);
    if (typeof speechSynthesis !== 'undefined') speechSynthesis.cancel();
    void this.workspaceService.disconnect();
  }

  protected login(): void {
    if (!this.username.trim() || !this.password) return;
    this.ensureTradeAlertsReady();
    this.loginBusy = true;
    this.loginError = '';
    this.subscriptions.add(
      this.auth.login(this.username.trim(), this.password).subscribe({
        next: (user) => {
          this.user = user;
          this.password = '';
          this.loginBusy = false;
          this.startWorkspace();
          this.refreshView();
        },
        error: () => {
          this.password = '';
          this.loginBusy = false;
          this.loginError = 'Sign-in failed. Check your credentials or account lockout status.';
          this.refreshView();
        },
      }),
    );
  }

  protected refreshWorkspace(): void {
    this.loadWorkspace();
    this.loadTokenStatus();
    this.loadKillSwitch();
    this.loadDailyLossOverride();
  }

  protected openMarket(market: TradingMarketCode): void {
    if (market === this.selectedMarket && this.workspace !== null) return;
    const path = market === 'natural-gas' ? '/natural-gas' : `/${market}`;
    window.history.pushState({}, '', path);
    this.selectedMarket = market;
    this.workspace = null;
    this.loadWorkspace();
    void this.workspaceService.connect(market);
    this.refreshView();
  }

  protected backToMarkets(): void {
    window.location.assign('/');
  }

  protected openControls(): void {
    this.controlsOpen = true;
    this.loadLiveExecution();
    this.loadTradingLots();
  }
  protected closeControls(): void { this.controlsOpen = false; }
  protected openNarrator(): void { this.narratorOpen = true; this.ensureTradeAlertsReady(); }
  protected closeNarrator(): void { this.narratorOpen = false; }
  protected setNarratorMode(mode: NarratorMode): void {
    this.narratorMode = mode;
    localStorage.setItem('sarthi.narratorMode', mode);
    this.refreshView();
  }
  protected setNarratorVoiceEnabled(enabled: boolean): void {
    this.narratorVoiceEnabled = enabled;
    localStorage.setItem('sarthi.narratorVoiceEnabled', String(enabled));
    if (!enabled && typeof speechSynthesis !== 'undefined') speechSynthesis.cancel();
    this.refreshView();
  }
  protected toggleNarratorMute(): void {
    this.setNarratorVoiceEnabled(!this.narratorVoiceEnabled);
  }
  protected narratorCategoryEnabled(category: NarratorCategory): boolean {
    return this.enabledNarratorCategories.has(category);
  }
  protected toggleNarratorCategory(category: NarratorCategory): void {
    const next = new Set(this.enabledNarratorCategories);
    if (next.has(category)) next.delete(category); else next.add(category);
    this.enabledNarratorCategories = next;
    localStorage.setItem('sarthi.narratorCategories', JSON.stringify([...next]));
    this.refreshView();
  }
  protected clearNarratorTranscript(): void { this.narratorTranscript = []; this.refreshView(); }
  protected closeNarratorToast(key: string): void {
    const timer = this.narratorToastTimers.get(key);
    if (timer) clearTimeout(timer);
    this.narratorToastTimers.delete(key);
    this.narratorToasts = this.narratorToasts.filter((event) => event.key !== key);
    this.refreshView();
  }
  protected testNarratorVoice(): void {
    this.ensureTradeAlertsReady();
    this.speakNarratorMessage('Sarthi market narrator is ready for Nifty and Sensex.');
  }

  protected loadLiveExecution(): void {
    this.subscriptions.add(this.liveExecutionService.getStatus().subscribe({
      next: status => { this.liveExecution = status; this.refreshView(); },
      error: () => { this.liveExecutionMessage = 'Live execution status is unavailable.'; this.refreshView(); },
    }));
  }

  protected loadTradingLots(): void {
    this.subscriptions.add(this.tradingLotsService.get().subscribe({
      next: value => { this.tradingLots = value; this.refreshView(); },
      error: () => { this.tradingLotsMessage = 'Unable to load saved lot sizes.'; this.refreshView(); },
    }));
  }

  protected saveTradingLots(): void {
    const nifty = Number(this.tradingLots.niftyLots);
    const sensex = Number(this.tradingLots.sensexLots);
    if (!Number.isInteger(nifty) || !Number.isInteger(sensex) ||
        nifty < 1 || nifty > 50 || sensex < 1 || sensex > 50) {
      this.tradingLotsMessage = 'Enter whole lot values between 1 and 50.';
      return;
    }
    this.tradingLotsBusy = true;
    this.tradingLotsMessage = '';
    this.subscriptions.add(this.tradingLotsService.save(nifty, sensex).subscribe({
      next: value => {
        this.tradingLots = value;
        this.tradingLotsBusy = false;
        this.tradingLotsMessage = 'Saved. New Nifty and Sensex entries will use these lot sizes.';
        this.refreshView();
      },
      error: () => {
        this.tradingLotsBusy = false;
        this.tradingLotsMessage = 'Unable to save lot sizes.';
        this.refreshView();
      },
    }));
  }

  protected setLiveExecution(armed: boolean): void {
    if (!this.liveExecutionPassword || this.liveExecutionReason.trim().length < 10) return;
    this.liveExecutionBusy = true;
    this.liveExecutionMessage = '';
    this.subscriptions.add(this.liveExecutionService.setArmed(armed,
      this.liveExecutionReason.trim(), this.liveExecutionPassword).subscribe({
      next: status => {
        this.liveExecution = status;
        this.liveExecutionPassword = '';
        this.liveExecutionBusy = false;
        this.liveExecutionMessage = armed ? 'Automatic live execution is armed for today.' :
          'Automatic live execution is disarmed; no new live entries will be submitted.';
        this.refreshView();
      },
      error: () => {
        this.liveExecutionPassword = '';
        this.liveExecutionBusy = false;
        this.liveExecutionMessage = 'Live execution change failed. Check the password and server readiness.';
        this.refreshView();
      },
    }));
  }

  protected preparedMarketName(): string {
    return this.selectedMarket === 'nifty' ? 'Nifty' :
      this.selectedMarket === 'sensex' ? 'Sensex' : 'Natural Gas Mini Futures';
  }

  protected preparedMarketVenue(): string {
    return this.selectedMarket === 'nifty' ? 'NSE index and options' :
      this.selectedMarket === 'sensex' ? 'BSE index and options' : 'MCX commodity futures';
  }

  private marketFromPath(): TradingMarketCode | null {
    const path = window.location.pathname.toLowerCase().replace(/\/$/, '');
    return path === '/nifty' ? 'nifty' : path === '/sensex' ? 'sensex' :
      path === '/natural-gas' ? 'natural-gas' : null;
  }

  protected setChartTimeframe(value: number): void {
    if (!this.chartTimeframes.includes(value)) return;
    this.chartTimeframeMinutes = value;
    localStorage.setItem('sarthi.chartTimeframeMinutes', String(value));
  }

  protected executedTrades(view: TradingWorkspaceSnapshot): WorkspaceTradeOverlay[] {
    return view.overlays.filter((overlay) => overlay.fillPrice !== null);
  }

  protected compactOptionName(overlay: WorkspaceTradeOverlay): string {
    return compactContractName(overlay);
  }

  protected openLogs(): void {
    this.controlsOpen = false;
    this.logsOpen = true;
  }

  protected closeLogs(): void {
    this.logsOpen = false;
  }

  protected openGrowwPositions(): void {
    this.controlsOpen = false;
    this.growwPositionsOpen = true;
    this.growwPositionsLoading = true;
    this.growwPositionsError = '';
    this.subscriptions.add(this.growwPositionsService.get().subscribe({
      next: (response) => {
        this.growwPositions = response.positions;
        this.growwPositionsObservedAt = response.observedAtUtc;
        this.growwPositionsLoading = false;
        this.refreshView();
      },
      error: () => {
        this.growwPositionsLoading = false;
        this.growwPositionsError = 'Unable to read Groww positions. Confirm today’s token is valid.';
        this.refreshView();
      },
    }));
  }

  protected closeGrowwPositions(): void { this.growwPositionsOpen = false; }

  protected openHeroZero(): void {
    if (this.selectedMarket !== 'nifty' && this.selectedMarket !== 'sensex') return;
    this.heroZeroOpen = true;
    this.loadHeroZero();
    this.heroZeroPollTimer ??= setInterval(() => this.loadHeroZero(), 5000);
  }
  private loadHeroZero(): void {
    if (!this.heroZeroOpen || (this.selectedMarket !== 'nifty' && this.selectedMarket !== 'sensex')) return;
    this.heroZeroLoading = true;
    this.heroZeroError = '';
    this.subscriptions.add(this.heroZeroService.get(this.selectedMarket).subscribe({
      next: value => {
        this.heroZero = value;
        this.heroZeroLoading = false;
        this.refreshView();
      },
      error: () => {
        this.heroZeroLoading = false;
        this.heroZeroError = 'Unable to read the expiry monitor.';
        this.refreshView();
      },
    }));
  }
  protected closeHeroZero(): void {
    this.heroZeroOpen = false;
    if (this.heroZeroPollTimer) clearInterval(this.heroZeroPollTimer);
    this.heroZeroPollTimer = null;
  }

  protected openReport(): void {
    this.controlsOpen = false;
    this.reportOpen=true; this.reportLoading=true; this.reportError='';
    this.subscriptions.add(this.paperReportService.get().subscribe({
      next:value=>{this.report=value;this.reportLoading=false;this.refreshView();},
      error:()=>{this.reportLoading=false;this.reportError='Unable to load paper-trading report.';this.refreshView();},
    }));
  }
  protected closeReport():void { this.reportOpen=false; }
  protected openDailyAnalysis(): void {
    this.dailyAnalysisOpen = true; this.dailyAnalysisLoading = true;
    this.dailyAnalysisError = ''; this.dailyAnalysis = null;
    this.subscriptions.add(this.paperReportService.getDaily(this.dailyAnalysisDate).subscribe({
      next: value => { this.dailyAnalysis = value; this.dailyAnalysisLoading = false; this.refreshView(); },
      error: error => { this.dailyAnalysisLoading = false;
        this.dailyAnalysisError = error.status === 409 ? error.error?.message ?? "Today's analysis is available after 3:45 PM IST." : 'Unable to load the session analysis. Try refreshing the report.';
        this.refreshView(); },
    }));
  }
  protected closeDailyAnalysis(): void { this.dailyAnalysisOpen = false; }
  protected dailyAnalysisPrompt(): string {
    if (!this.dailyAnalysis) return '';
    const p = this.dailyAnalysis;
    return `Review the ${p.tradingDate} IST session using the attached production-backed Daily Analysis. Validate observations against the trade IDs, stop/target outcomes and telemetry. Separate confirmed defects from normal losing trades or missing evidence. Propose narrowly scoped corrections and regression tests; do not edit strategy or deploy until I approve.\n\n`+
      `Closed legs: ${p.summary.completedTradeCount}; wins ${p.summary.wins}; losses ${p.summary.losses}; gross ₹${p.summary.grossPnl.toFixed(2)}; charges ₹${p.summary.costs.toFixed(2)}; net ₹${p.summary.netRealisedPnl.toFixed(2)}. Decisions ${p.summary.decisionCount} (${p.summary.acceptedDecisionCount} accepted, ${p.summary.rejectedDecisionCount} rejected).\n`+
      p.trades.map(t => `${t.market} ${t.symbol} ${t.openedAtUtc}→${t.closedAtUtc ?? 'open'}: ${t.exitReason ?? 'open'}, net ₹${t.netRealisedPnl.toFixed(2)}; peak ₹${t.maximumFavourableExcursion ?? 'unavailable'} at ${t.maximumFavourableExcursionAtUtc ?? 'time unavailable'}, adverse ₹${t.maximumAdverseExcursion ?? 'unavailable'} at ${t.maximumAdverseExcursionAtUtc ?? 'time unavailable'}; ${t.excursionCoverage ?? 'coverage unavailable'} (${t.excursionSampleCount ?? 0} observations) [${t.tradeId}]`).join('\n')+
      `\nDecision outcomes: ${JSON.stringify(p.summary.outcomes)}\n`+
      `\n\nTelemetry limitations: ${p.telemetryWarnings.join(' | ') || 'none reported'}`;
  }
  protected dailyAnalysisFlags(a: DailyDiagnostics): string[] {
    const flags: string[] = [];
    if (a.summary.completedTradeCount === 0) flags.push('No completed trades were persisted for this date; inspect the decision funnel and feed status before concluding the strategy missed a move.');
    if (a.summary.decisionCount === 0) flags.push('No persisted strategy decisions were found. This can indicate missing decision telemetry or an inactive evaluation loop.');
    if (a.summary.rejectedDecisionCount > 0) {
      const leading = Object.entries(a.summary.outcomes).sort((left, right) => right[1] - left[1]).slice(0, 3);
      flags.push(`Decision funnel: ${a.summary.acceptedDecisionCount} accepted / ${a.summary.rejectedDecisionCount} rejected. Most common recorded outcomes: ${leading.map(([name, count]) => `${name} (${count})`).join(', ') || 'not available'}.`);
    }
    if (a.summary.costs > 0) flags.push(`Recorded charges were ₹${a.summary.costs.toFixed(2)}; compare gross and net results because costs can erase a small gross edge.`);
    const withMfe = a.trades.filter(t => t.maximumFavourableExcursion !== null);
    const incompletePath = a.trades.filter(t => t.closedAtUtc &&
      (t.excursionCoverage?.includes('unavailable') || t.excursionCoverage?.includes('earlier path unavailable')));
    if (incompletePath.length > 0) flags.push(`Intratrade peak/adverse coverage is incomplete for ${incompletePath.length} trade(s); displayed values use only the available samples and cannot reconstruct unsampled history.`);
    const givebacks = withMfe.filter(t => t.maximumFavourableExcursion! > Math.max(0, t.grossPnl));
    if (givebacks.length) flags.push(`${givebacks.length} trade(s) exited below their recorded peak favourable excursion; review the trade rows and linked decision trail to distinguish ordinary pullback from thesis failure.`);
    if (a.trades.some(t => !t.signalId)) flags.push('At least one trade has no linked signal ID; research-candidate linkage is incomplete for those rows.');
    if (a.feeds.some(feed => !feed.isAvailable)) flags.push(`At least one recorded feed is unavailable (${a.feeds.filter(feed => !feed.isAvailable).map(feed => feed.provider).join(', ')}).`);
    if (a.summary.lastCandleAtUtc) flags.push(`Last recorded candle: ${new Date(a.summary.lastCandleAtUtc).toLocaleString('en-IN', { timeZone: 'Asia/Kolkata' })} IST; compare against the expected session close.`);
    return flags.length ? flags : ['No obvious data-quality or outcome flags detected in the fields returned. This is not a strategy-performance certification.'];
  }
  protected async copyDailyAnalysisPrompt(): Promise<void> {
    await navigator.clipboard.writeText(this.dailyAnalysisPrompt());
    this.dailyAnalysisPromptCopied = true;
    setTimeout(() => { this.dailyAnalysisPromptCopied = false; this.refreshView(); }, 2500);
  }
  protected async copyDailyAnalysisTrades(): Promise<void> {
    const analysis = this.dailyAnalysis;
    if (!analysis || analysis.trades.length === 0) return;
    try {
      await navigator.clipboard.writeText(buildDailyAnalysisTradesTsv(analysis.tradingDate, analysis.trades));
      this.dailyAnalysisTradesCopied = true;
      this.dailyAnalysisTradesCopyError = false;
    } catch {
      this.dailyAnalysisTradesCopied = false;
      this.dailyAnalysisTradesCopyError = true;
    }
    this.refreshView();
    setTimeout(() => {
      this.dailyAnalysisTradesCopied = false;
      this.dailyAnalysisTradesCopyError = false;
      this.refreshView();
    }, 2500);
  }
  protected openPnlReport():void { this.controlsOpen=false; this.pnlReportOpen=true; }
  protected closePnlReport():void { this.pnlReportOpen=false; }
  protected searchPnl():void {
    if (!this.pnlFrom || !this.pnlTo || this.pnlFrom > this.pnlTo) {
      this.pnlReportError='Select a valid From and To date range.'; return;
    }
    this.pnlReportLoading=true; this.pnlReportError=''; this.pnlSummary=null;
    this.subscriptions.add(this.paperReportService.getPnlSummary(this.pnlFrom,this.pnlTo,this.pnlMarket).subscribe({
      next:value=>{this.pnlSummary=value;this.pnlReportLoading=false;this.refreshView();},
      error:()=>{this.pnlReportLoading=false;this.pnlReportError='Unable to load the P&L report.';this.refreshView();},
    }));
  }
  protected marketReportName(market:string):string {
    return market==='nifty' ? 'Nifty' : market==='sensex' ? 'Sensex' : 'Natural Gas';
  }
  private isoDateDaysAgo(days:number):string {
    const value=new Date(Date.now()-days*86400000);
    return new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Kolkata',year:'numeric',month:'2-digit',day:'2-digit'}).format(value);
  }
  protected filteredTrades():PaperTradeHistoryItem[] {
    const value=this.tradeFilter.trim().toLowerCase();
    return !value ? this.report?.trades || [] : (this.report?.trades || []).filter(trade=>
      [trade.contract,trade.direction,trade.exitReason,trade.strategy].some(field=>field.toLowerCase().includes(value)));
  }
  protected exportTrades():void {
    const header='Contract,Direction,Quantity,Entry,Exit,NetPnL,TotalCharges,Brokerage,STT,GST,Reason,EntryTimeUTC,ExitTimeUTC';
    const lines=this.filteredTrades().map(t=>[t.contract,t.direction,t.quantity,t.entryPrice,t.exitPrice,t.realisedPnl,t.costs.total,t.costs.brokerage,t.costs.securitiesTransactionTax,t.costs.goodsAndServicesTax,t.exitReason,t.signalTimeUtc,t.exitTimeUtc].join(','));
    const url=URL.createObjectURL(new Blob([[header,...lines].join('\n')],{type:'text/csv'}));
    const anchor=document.createElement('a'); anchor.href=url; anchor.download='paper-trades.csv'; anchor.click(); URL.revokeObjectURL(url);
  }

  protected operationalAlerts(view:TradingWorkspaceSnapshot):string[] {
    const alerts:string[]=[];
    if (!this.tokenStatus.isConfigured || this.tokenStatus.isExpired) alerts.push('Groww token requires attention.');
    if (!view.isFresh) alerts.push(`${view.instrument} market data is stale or disconnected.`);
    if (view.paperAutomation.status==='PositionUnmonitored') alerts.push('Open paper position quote is unavailable.');
    if (view.paperAutomation.status==='ReconciliationRequired') alerts.push('Paper broker reconciliation requires attention.');
    if (this.killSwitchActive) alerts.push('Emergency kill switch is active.');
    return alerts;
  }

  protected latestOperationalReport(view:TradingWorkspaceSnapshot):OperationalAutomationReport|null {
    return [...(view.operationalReports ?? [])]
      .sort((left,right)=>new Date(right.generatedAtUtc).getTime()-new Date(left.generatedAtUtc).getTime())[0] ?? null;
  }

  protected setKillSwitch(active: boolean): void {
    this.killSwitchBusy = true;
    this.killSwitchMessage = '';
    const reason = active ? 'Administrator emergency stop from dashboard.' :
      'Administrator reviewed system state and cleared emergency stop.';
    this.subscriptions.add(this.paperRisk.setKillSwitch(active, reason).subscribe({
      next: (status) => {
        this.killSwitchActive = status.active;
        this.killSwitchBusy = false;
        this.killSwitchMessage = active ? 'Kill switch activated.' : 'Kill switch cleared.';
        this.loadWorkspace();
        this.refreshView();
      },
      error: () => {
        this.killSwitchBusy = false;
        this.killSwitchMessage = 'Unable to change the kill switch.';
        this.refreshView();
      },
    }));
  }

  protected setDailyLossOverride(active: boolean): void {
    this.dailyLossOverrideBusy = true;
    this.dailyLossOverrideMessage = '';
    const reason = active
      ? 'Administrator allowed paper entries beyond the daily loss limit for this session.'
      : 'Administrator restored the paper daily loss limit for this session.';
    this.subscriptions.add(this.paperRisk.setDailyLossOverride(active, reason).subscribe({
      next: (status) => {
        this.dailyLossOverrideActive = status.active;
        this.dailyLossOverrideBusy = false;
        this.dailyLossOverrideMessage = active
          ? 'Daily loss limit ignored for today.'
          : 'Daily loss limit restored.';
        this.loadWorkspace();
        this.refreshView();
      },
      error: () => {
        this.dailyLossOverrideBusy = false;
        this.dailyLossOverrideMessage = 'Unable to change the daily loss override.';
        this.refreshView();
      },
    }));
  }

  protected openTokenForm(): void {
    this.showTokenForm = true;
    this.tokenError = '';
    this.tokenSuccess = '';
  }

  protected saveGrowwToken(): void {
    const token = this.growwAccessToken.trim();
    if (token.length < 20) {
      this.tokenError = 'Enter the complete Groww access token.';
      return;
    }

    this.tokenBusy = true;
    this.tokenError = '';
    this.tokenSuccess = '';
    this.subscriptions.add(
      this.growwTokenService.store(token).subscribe({
        next: (response) => {
          this.growwAccessToken = '';
          this.tokenStatus = response.token;
          this.tokenBusy = false;
          this.showTokenForm = false;
          const sync = response.instrumentSynchronization;
          this.tokenSuccess = sync
            ? `Today’s Groww token is protected and instruments are synchronised (${sync.inserted} added, ${sync.updated} refreshed).`
            : 'Today’s Groww token is protected. Instrument synchronisation still needs attention.';
          this.tokenError = response.instrumentSynchronizationError ?? '';
          this.loadWorkspace();
          this.refreshView();
        },
        error: () => {
          this.growwAccessToken = '';
          this.tokenBusy = false;
          this.tokenError = 'The token could not be stored. Confirm it is complete and try again.';
          this.refreshView();
        },
      }),
    );
  }

  protected synchronizeInstruments(): void {
    this.instrumentSyncBusy = true;
    this.tokenError = '';
    this.tokenSuccess = '';
    this.subscriptions.add(this.growwTokenService.synchronizeInstruments().subscribe({
      next: (result) => {
        this.instrumentSyncBusy = false;
        this.tokenSuccess = `Groww instruments synchronised (${result.inserted} added, ${result.updated} refreshed). The live feed will reconnect automatically.`;
        this.loadWorkspace();
        this.refreshView();
      },
      error: () => {
        this.instrumentSyncBusy = false;
        this.tokenError = 'Instrument synchronisation failed. Confirm the Groww token is current and try again.';
        this.refreshView();
      },
    }));
  }

  private startWorkspace(): void {
    this.loadTokenStatus();
    this.loadKillSwitch();
    this.loadDailyLossOverride();
    if (this.selectedMarket !== null) this.loadWorkspace();
    this.subscriptions.add(
      this.workspaceService.updates$().subscribe((snapshot) => {
        const market = marketCodeForSnapshot(snapshot);
        this.processMarketNarrator(market, snapshot);
        this.detectTradeAlerts(market, snapshot);
        if (this.selectedMarket !== market) return;
        this.workspace = mergeWorkspaceSnapshot(this.workspace, snapshot);
        this.workspaceError = '';
        this.refreshView();
      }),
    );
    void this.workspaceService.connect(this.selectedMarket ?? 'nifty').catch(() => {
      this.workspaceError =
        'Real-time dashboard connection is unavailable; manual refresh remains available.';
      this.refreshView();
    });
  }

  private loadKillSwitch(): void {
    this.subscriptions.add(this.paperRisk.getKillSwitch().subscribe({
      next: (status) => {
        this.killSwitchActive = status.active;
        this.refreshView();
      },
      error: () => {
        this.killSwitchMessage = 'Kill-switch state is unavailable.';
        this.refreshView();
      },
    }));
  }

  private loadDailyLossOverride(): void {
    this.subscriptions.add(this.paperRisk.getDailyLossOverride().subscribe({
      next: (status) => {
        this.dailyLossOverrideActive = status.active;
        this.refreshView();
      },
      error: () => {
        this.dailyLossOverrideMessage = 'Daily loss override state is unavailable.';
        this.refreshView();
      },
    }));
  }

  private loadTokenStatus(): void {
    this.tokenStatusLoading = true;
    this.subscriptions.add(
      this.growwTokenService.getStatus().subscribe({
        next: (status) => {
          this.tokenStatus = status;
          this.tokenStatusLoading = false;
          this.tokenError = '';
          this.refreshView();
        },
        error: () => {
          this.tokenStatusLoading = false;
          this.tokenError = 'Unable to read Groww token status. You can still add today’s token.';
          this.refreshView();
        },
      }),
    );
  }

  private loadWorkspace(): void {
    if (this.selectedMarket === null) return;
    const requestedMarket = this.selectedMarket;
    this.subscriptions.add(
      this.workspaceService.getMarket(requestedMarket).subscribe({
        next: (snapshot) => {
          if (this.selectedMarket !== requestedMarket) return;
          this.processMarketNarrator(requestedMarket, snapshot);
          this.detectTradeAlerts(requestedMarket, snapshot);
          this.workspace = mergeWorkspaceSnapshot(this.workspace, snapshot);
          this.workspaceError = '';
          this.refreshView();
        },
        error: () => {
          if (this.selectedMarket !== requestedMarket) return;
          this.workspaceError = `Unable to load the protected ${this.preparedMarketName()} workspace.`;
          this.refreshView();
        },
      }),
    );
  }

  private refreshView(): void {
    this.changeDetector.markForCheck();
  }

  protected marketTabAlert(market: TradingMarketCode): TradeAlertKind | null {
    return this.marketTabAlerts.get(market) ?? null;
  }

  private detectTradeAlerts(market: TradingMarketCode, snapshot: TradingWorkspaceSnapshot): void {
    const trades = snapshot.overlays.filter((overlay) => overlay.fillPrice !== null);
    if (!this.initializedTradeMarkets.has(market)) {
      trades.forEach((trade) => this.tradeStates.set(`${market}:${trade.signalId}`, trade.lifecycleStatus));
      this.initializedTradeMarkets.add(market);
      return;
    }
    for (const trade of trades) {
      const key = `${market}:${trade.signalId}`;
      const alert = tradeAlertTransition(this.tradeStates.get(key), trade.lifecycleStatus);
      if (alert) {
        this.flashMarketTab(market, alert);
      }
      this.tradeStates.set(key, trade.lifecycleStatus);
    }
  }

  private flashMarketTab(market: TradingMarketCode, kind: TradeAlertKind): void {
    const existing = this.marketTabAlertTimers.get(market);
    if (existing) clearTimeout(existing);
    this.marketTabAlerts.set(market, kind);
    this.marketTabAlertTimers.set(market, setTimeout(() => {
      this.marketTabAlerts.delete(market);
      this.marketTabAlertTimers.delete(market);
      this.refreshView();
    }, 10000));
    this.refreshView();
  }

  private ensureTradeAlertsReady(): void {
    this.narratorSpeechArmed = true;
    if (typeof speechSynthesis !== 'undefined') speechSynthesis.resume();
  }

  private processMarketNarrator(market: TradingMarketCode, snapshot: TradingWorkspaceSnapshot): void {
    if (market === 'natural-gas') return;
    const previous = this.narratorSnapshots.get(market);
    this.narratorSnapshots.set(market, snapshot);
    if (!previous) return;
    const events = selectHighSignalNarratorEvents(buildMarketNarratorEvents(market, previous, snapshot))
      .filter((event) => this.enabledNarratorCategories.has(event.category))
      .filter((event) => !this.narratedKeys.has(event.key));
    if (!events.length) return;
    const timestamp = Date.now();
    for (const event of events) {
      this.narratedKeys.set(event.key, timestamp);
      this.narratorTranscript.unshift(event);
      this.showNarratorToast(event);
      if (this.shouldSpeakNarratorEvent(event)) this.speakNarratorMessage(event.message, event.priority === 'critical');
    }
    this.narratorTranscript = this.narratorTranscript.slice(0, 150);
    if (this.narratedKeys.size > 1000) {
      const cutoff = timestamp - 24 * 60 * 60 * 1000;
      for (const [key, observed] of this.narratedKeys) if (observed < cutoff) this.narratedKeys.delete(key);
    }
    this.refreshView();
  }

  private showNarratorToast(event: MarketNarratorEvent): void {
    this.narratorToasts = [event, ...this.narratorToasts.filter((item) => item.key !== event.key)].slice(0, 4);
    const visibleKeys = new Set(this.narratorToasts.map((item) => item.key));
    for (const [key, timer] of this.narratorToastTimers) {
      if (!visibleKeys.has(key)) { clearTimeout(timer); this.narratorToastTimers.delete(key); }
    }
    const existing = this.narratorToastTimers.get(event.key);
    if (existing) clearTimeout(existing);
    this.narratorToastTimers.set(event.key, setTimeout(() => this.closeNarratorToast(event.key), 15000));
  }

  private shouldSpeakNarratorEvent(event: MarketNarratorEvent): boolean {
    if (!this.narratorVoiceEnabled || !this.narratorSpeechArmed || this.narratorMode === 'quiet') return false;
    if (this.narratorMode === 'learning') return true;
    return event.category === 'trade' || (event.category === 'risk-system' && ['warning', 'critical'].includes(event.priority));
  }

  private speakNarratorMessage(message: string, interrupt = false): void {
    if (!this.narratorVoiceEnabled || typeof speechSynthesis === 'undefined' || typeof SpeechSynthesisUtterance === 'undefined') return;
    if (interrupt) speechSynthesis.cancel();
    const utterance = new SpeechSynthesisUtterance(message);
    utterance.rate = 0.96;
    utterance.pitch = 1;
    utterance.volume = 1;
    utterance.lang = 'en-IN';
    speechSynthesis.speak(utterance);
  }

  private readNarratorMode(): NarratorMode {
    const value = localStorage.getItem('sarthi.narratorMode');
    return value === 'quiet' || value === 'essential' || value === 'learning' ? value : 'learning';
  }

  private readNarratorVoiceEnabled(): boolean {
    return localStorage.getItem('sarthi.narratorVoiceEnabled') !== 'false';
  }

  private readNarratorCategories(): Set<NarratorCategory> {
    try {
      const stored = JSON.parse(localStorage.getItem('sarthi.narratorCategories') ?? '[]') as NarratorCategory[];
      const valid = new Set(narratorCategories.map((category) => category.id));
      const selected = stored.filter((category) => valid.has(category));
      if (selected.length) return new Set(selected);
    } catch { /* Fall back to every category. */ }
    return new Set(narratorCategories.map((category) => category.id));
  }

  private readChartTimeframe(): number {
    const value = Number(localStorage.getItem('sarthi.chartTimeframeMinutes'));
    return [1, 5, 15].includes(value) ? value : 5;
  }
}

export function buildDailyAnalysisTradesTsv(
  tradingDate: string,
  trades: DailyDiagnostics['trades'],
): string {
  const formatMoney = (value: number | null): string =>
    value === null ? '—' : `₹${new Intl.NumberFormat('en-IN', {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(value)}`;
  const formatTime = (value: string | null): string => {
    if (!value) return 'time unavailable';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return 'time unavailable';
    const time = new Intl.DateTimeFormat('en-GB', {
      timeZone: 'Asia/Kolkata',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hour12: false,
    }).format(date);
    return `${time} IST`;
  };
  const cell = (value: string): string => `"${value.replace(/"/g, '""').replace(/[\r\n]+/g, ' ')}"`;
  const headers = [
    'Market / contract', 'Entry → exit', 'Exit reason', 'Gross', 'Costs', 'Net',
    'Peak profit', 'Max adverse',
  ];
  const rows = trades.map((trade) => [
    `${trade.market} · ${trade.symbol} · ${trade.strategy} · ${trade.direction} · ${trade.quantity} qty`,
    `${formatTime(trade.openedAtUtc)} → ${trade.closedAtUtc ? formatTime(trade.closedAtUtc) : 'Open'}`,
    trade.exitReason ?? 'Open',
    formatMoney(trade.grossPnl),
    formatMoney(trade.costs),
    formatMoney(trade.netRealisedPnl),
    `${formatMoney(trade.maximumFavourableExcursion)} at ${formatTime(trade.maximumFavourableExcursionAtUtc)}`,
    `${formatMoney(trade.maximumAdverseExcursion)} at ${formatTime(trade.maximumAdverseExcursionAtUtc)} · ${trade.excursionCoverage || 'coverage unavailable'} · ${trade.excursionSampleCount ?? 0} observations`,
  ]);
  return [
    `Daily Analysis — ${tradingDate} IST`,
    headers.map(cell).join('\t'),
    ...rows.map((row) => row.map(cell).join('\t')),
  ].join('\r\n');
}
