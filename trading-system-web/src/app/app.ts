import { AsyncPipe, DatePipe, DecimalPipe, PercentPipe } from '@angular/common';
import { ChangeDetectorRef, Component, inject, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterOutlet } from '@angular/router';
import { catchError, of, startWith, Subscription } from 'rxjs';
import { AuthService, CurrentUser } from './auth.service';
import { NiftyChartComponent } from './nifty-chart.component';
import { GrowwTokenService, GrowwTokenStatus } from './groww-token.service';
import { SystemStatusService } from './system-status.service';
import { TradingWorkspaceSnapshot, WorkspaceTradeOverlay } from './trading-workspace';
import { TradingWorkspaceService } from './trading-workspace.service';
import { PaperRiskService } from './paper-risk.service';

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
  protected logsOpen = false;
  protected chartTimeframeMinutes = this.readChartTimeframe();
  protected readonly chartTimeframes = [1, 5, 15];

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
    void this.workspaceService.disconnect();
  }

  protected login(): void {
    if (!this.username.trim() || !this.password) return;
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
  }

  protected setChartTimeframe(value: number): void {
    if (!this.chartTimeframes.includes(value)) return;
    this.chartTimeframeMinutes = value;
    localStorage.setItem('sarthi.chartTimeframeMinutes', String(value));
  }

  protected executedTrades(view: TradingWorkspaceSnapshot): WorkspaceTradeOverlay[] {
    return view.overlays.filter((overlay) => overlay.fillPrice !== null);
  }

  protected openLogs(): void {
    this.logsOpen = true;
  }

  protected closeLogs(): void {
    this.logsOpen = false;
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
    this.loadWorkspace();
    this.subscriptions.add(
      this.workspaceService.updates$().subscribe((snapshot) => {
        this.workspace = snapshot;
        this.workspaceError = '';
        this.refreshView();
      }),
    );
    void this.workspaceService.connect().catch(() => {
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
    this.subscriptions.add(
      this.workspaceService.getNifty().subscribe({
        next: (snapshot) => {
          this.workspace = snapshot;
          this.workspaceError = '';
          this.refreshView();
        },
        error: () => {
          this.workspaceError = 'Unable to load the protected Nifty workspace.';
          this.refreshView();
        },
      }),
    );
  }

  private refreshView(): void {
    this.changeDetector.markForCheck();
  }

  private readChartTimeframe(): number {
    const value = Number(localStorage.getItem('sarthi.chartTimeframeMinutes'));
    return [1, 5, 15].includes(value) ? value : 5;
  }
}
