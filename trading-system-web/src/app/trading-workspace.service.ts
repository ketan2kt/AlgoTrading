import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { Observable, Subject, switchMap } from 'rxjs';
import { TradingWorkspaceSnapshot } from './trading-workspace';

@Injectable({ providedIn: 'root' })
export class TradingWorkspaceService {
  private static readonly markets = ['nifty', 'sensex', 'bank-nifty', 'bankex', 'natural-gas'];
  private readonly http = inject(HttpClient);
  private readonly updates = new Subject<TradingWorkspaceSnapshot>();
  private connection: HubConnection | null = null;
  private connectionStart: Promise<void> | null = null;
  private activeMarket = 'nifty';

  getMarket(market: string): Observable<TradingWorkspaceSnapshot> {
    return this.http.get<TradingWorkspaceSnapshot>(`/api/trading-workspace/${market}?candleCount=7000`);
  }

  requestTradeExit(market: string, tradeId: string): Observable<{ status: string }> {
    return this.http.get<{ token: string }>('/api/security/antiforgery-token').pipe(
      switchMap(({ token }) => this.http.post<{ status: string }>(
        `/api/paper/trades/${encodeURIComponent(market)}/${encodeURIComponent(tradeId)}/exit`, {},
        { headers: new HttpHeaders({ 'X-CSRF-TOKEN': token }) })),
    );
  }

  updates$(): Observable<TradingWorkspaceSnapshot> {
    return this.updates.asObservable();
  }

  async connect(market: string): Promise<void> {
    this.activeMarket = market;
    if (this.connection?.state === HubConnectionState.Connected) {
      await this.connection.invoke('SubscribeMarketWorkspace', market);
      return;
    }
    if (!this.connection) {
      this.connection = new HubConnectionBuilder()
        .withUrl('/hubs/system-health')
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .build();
      this.connection.on('marketWorkspaceUpdated', (updatedMarket, snapshot) => {
        this.updates.next({ ...(snapshot as TradingWorkspaceSnapshot), marketCode: updatedMarket });
      });
      this.connection.onreconnected(() => this.subscribeAllMarkets());
      this.connectionStart = this.connection.start();
    }
    await this.connectionStart;
    await this.subscribeAllMarkets();
  }

  private async subscribeAllMarkets(): Promise<void> {
    if (!this.connection || this.connection.state !== HubConnectionState.Connected) return;
    await Promise.all(TradingWorkspaceService.markets.map((market) =>
      this.connection!.invoke('SubscribeMarketWorkspace', market)));
  }

  async disconnect(): Promise<void> {
    if (this.connection) await this.connection.stop();
    this.connection = null;
    this.connectionStart = null;
  }
}
