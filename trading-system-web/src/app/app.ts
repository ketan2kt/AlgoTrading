import { AsyncPipe, DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterOutlet } from '@angular/router';
import { catchError, of, startWith, Subscription } from 'rxjs';
import { AuthService, CurrentUser } from './auth.service';
import { NiftyChartComponent } from './nifty-chart.component';
import { GrowwTokenService, GrowwTokenStatus } from './groww-token.service';
import { SystemStatusService } from './system-status.service';
import { TradingWorkspaceSnapshot } from './trading-workspace';
import { TradingWorkspaceService } from './trading-workspace.service';

@Component({
  selector: 'app-root',
  imports: [AsyncPipe, DatePipe, DecimalPipe, FormsModule, NiftyChartComponent, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  private readonly statusService = inject(SystemStatusService);
  private readonly auth = inject(AuthService);
  private readonly workspaceService = inject(TradingWorkspaceService);
  private readonly growwTokenService = inject(GrowwTokenService);
  private readonly subscriptions = new Subscription();

  protected user: CurrentUser | null = null;
  protected checkingSession = true;
  protected loginBusy = false;
  protected loginError = '';
  protected username = '';
  protected password = '';
  protected workspace: TradingWorkspaceSnapshot | null = null;
  protected workspaceError = '';
  protected tokenStatus: GrowwTokenStatus | null = null;
  protected growwAccessToken = '';
  protected tokenBusy = false;
  protected tokenError = '';
  protected tokenSuccess = '';
  protected showTokenForm = false;

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
        },
        error: () => {
          this.checkingSession = false;
          this.loginError = 'Unable to verify the application session.';
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
        },
        error: () => {
          this.password = '';
          this.loginBusy = false;
          this.loginError = 'Sign-in failed. Check your credentials or account lockout status.';
        },
      }),
    );
  }

  protected refreshWorkspace(): void {
    this.loadWorkspace();
    this.loadTokenStatus();
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
        next: (status) => {
          this.growwAccessToken = '';
          this.tokenStatus = status;
          this.tokenBusy = false;
          this.showTokenForm = false;
          this.tokenSuccess =
            'Today’s Groww token is protected. The live feed will retry automatically.';
          this.loadWorkspace();
        },
        error: () => {
          this.growwAccessToken = '';
          this.tokenBusy = false;
          this.tokenError = 'The token could not be stored. Confirm it is complete and try again.';
        },
      }),
    );
  }

  private startWorkspace(): void {
    this.loadTokenStatus();
    this.loadWorkspace();
    this.subscriptions.add(
      this.workspaceService.updates$().subscribe((snapshot) => {
        this.workspace = snapshot;
        this.workspaceError = '';
      }),
    );
    void this.workspaceService.connect().catch(() => {
      this.workspaceError =
        'Real-time dashboard connection is unavailable; manual refresh remains available.';
    });
  }

  private loadTokenStatus(): void {
    this.subscriptions.add(
      this.growwTokenService.getStatus().subscribe({
        next: (status) => (this.tokenStatus = status),
        error: () => (this.tokenError = 'Unable to read Groww token status.'),
      }),
    );
  }

  private loadWorkspace(): void {
    this.subscriptions.add(
      this.workspaceService.getNifty().subscribe({
        next: (snapshot) => {
          this.workspace = snapshot;
          this.workspaceError = '';
        },
        error: () => (this.workspaceError = 'Unable to load the protected Nifty workspace.'),
      }),
    );
  }
}
