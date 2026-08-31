import { AsyncPipe, DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { ChangeDetectorRef, Component, inject, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterOutlet } from '@angular/router';
import { catchError, of, startWith, Subscription } from 'rxjs';
import { AuthService, CurrentUser } from './auth.service';
import { NiftyChartComponent } from './nifty-chart.component';
import { GrowwTokenService, GrowwTokenStatus } from './groww-token.service';
import { SystemStatusService } from './system-status.service';
import { mergeWorkspaceSnapshot, TradingWorkspaceSnapshot, WorkspaceTradeOverlay } from './trading-workspace';
import { compactContractName } from './contract-name';
import { marketCodeForSnapshot, tradeAlertTransition, TradeAlertKind, TradingMarketCode } from './trade-alerts';
import { TradingWorkspaceService } from './trading-workspace.service';
import { PaperRiskService } from './paper-risk.service';
import { GrowwLivePosition, GrowwPositionsService } from './groww-positions.service';
import { PaperPnlSummary, PaperReportService, PaperTradingReport, PaperTradeHistoryItem } from './paper-report.service';
import { HeroZeroMonitor, HeroZeroService } from './hero-zero.service';

@Component({
  selector: 'app-root',
  imports: [AsyncPipe, DatePipe, DecimalPipe, PercentPipe, FormsModule, NiftyChartComponent, RouterOutlet],
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
  private audioContext: AudioContext | null = null;
  private tradeStates = new Map<string, string>();
  private initializedTradeMarkets = new Set<TradingMarketCode>();
  protected marketTabAlerts = new Map<TradingMarketCode, TradeAlertKind>();
  private marketTabAlertTimers = new Map<TradingMarketCode, ReturnType<typeof setTimeout>>();
  private readonly armTradeAlerts = (): void => this.ensureTradeAlertsReady();
  protected chartTimeframeMinutes = this.readChartTimeframe();
  protected readonly chartTimeframes = [1, 5, 15];
  protected selectedMarket: 'nifty' | 'sensex' | 'natural-gas' | null = this.marketFromPath();
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
          if (user) this.startWorkspace();
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
    if (this.heroZeroPollTimer) clearInterval(this.heroZeroPollTimer);
    void this.audioContext?.close();
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

  protected openMarket(market: 'nifty' | 'sensex' | 'natural-gas'): void {
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
    window.history.pushState({}, '', '/');
    this.selectedMarket = null;
    this.refreshView();
  }

  protected openControls(): void { this.controlsOpen = true; }
  protected closeControls(): void { this.controlsOpen = false; }

  protected preparedMarketName(): string {
    return this.selectedMarket === 'nifty' ? 'Nifty' :
      this.selectedMarket === 'sensex' ? 'Sensex' : 'Natural Gas Mini Futures';
  }

  protected preparedMarketVenue(): string {
    return this.selectedMarket === 'nifty' ? 'NSE index and options' :
      this.selectedMarket === 'sensex' ? 'BSE index and options' : 'MCX commodity futures';
  }

  private marketFromPath(): 'nifty' | 'sensex' | 'natural-gas' | null {
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
        this.playTradeAlert(alert);
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
    if (typeof AudioContext === 'undefined') return;
    this.audioContext ??= new AudioContext();
    if (this.audioContext.state === 'suspended') void this.audioContext.resume();
  }

  private playTradeAlert(kind: 'entry' | 'exit'): void {
    this.ensureTradeAlertsReady();
    if (!this.audioContext) return;
    if (this.audioContext.state === 'suspended') {
      void this.audioContext.resume().then(() => this.emitTradeAlert(kind)).catch(() => undefined);
      return;
    }
    this.emitTradeAlert(kind);
  }

  private emitTradeAlert(kind: 'entry' | 'exit'): void {
    if (!this.audioContext) return;
    const frequencies = kind === 'entry' ? [880, 1175, 880] : [440, 294, 220];
    const start = this.audioContext.currentTime;
    frequencies.forEach((frequency, index) => {
      const oscillator = this.audioContext!.createOscillator();
      const gain = this.audioContext!.createGain();
      oscillator.type = 'square';
      oscillator.frequency.value = frequency;
      gain.gain.setValueAtTime(0.001, start + index * 0.22);
      gain.gain.exponentialRampToValueAtTime(0.55, start + index * 0.22 + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.001, start + index * 0.22 + 0.18);
      oscillator.connect(gain).connect(this.audioContext!.destination);
      oscillator.start(start + index * 0.22);
      oscillator.stop(start + index * 0.22 + 0.2);
    });
  }

  private readChartTimeframe(): number {
    const value = Number(localStorage.getItem('sarthi.chartTimeframeMinutes'));
    return [1, 5, 15].includes(value) ? value : 5;
  }
}
