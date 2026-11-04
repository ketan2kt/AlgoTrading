import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { Observable, Subject, switchMap } from 'rxjs';
import { TradingWorkspaceSnapshot } from './trading-workspace';

@Injectable({ providedIn: 'root' })
export class TradingWorkspaceService {
  private static readonly markets = ['nifty', 'sensex', 'bank-nifty'];
  private readonly http = inject(HttpClient);
  private readonly updates = new Subject<TradingWorkspaceSnapshot>();
  private connection: HubConnection | null = null;
  private connectionStart: Promise<void> | null = null;
  private groupsReady = false;

  getMarket(market: string, extendedHistory = false): Observable<TradingWorkspaceSnapshot> {
    return this.http.get<TradingWorkspaceSnapshot>(`/api/trading-workspace/${market}?candleCount=${extendedHistory ? 7000 : 750}`);
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

  async connect(_market: string): Promise<void> {
    if (!this.connection) {
      this.connection = new HubConnectionBuilder()
        .withUrl('/hubs/system-health')
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .build();
      this.connection.on('marketWorkspaceUpdated', (updatedMarket, snapshot) => {
        this.updates.next({ ...(snapshot as TradingWorkspaceSnapshot), marketCode: updatedMarket });
      });
      this.connection.onreconnecting(() => { this.groupsReady = false; });
      this.connection.onclose(() => { this.groupsReady = false; });
      this.connection.onreconnected(() => this.connect(_market));
    }
    const hub = this.connection;
    if (hub.state === HubConnectionState.Reconnecting || (hub.state === HubConnectionState.Connected && this.groupsReady)) return;
    if (this.connectionStart) return this.connectionStart;
    const join = async () => {
      if (hub.state === HubConnectionState.Disconnected) await hub.start();
      await Promise.all(TradingWorkspaceService.markets.map(code => hub.invoke('SubscribeMarketWorkspace', code)));
      if (this.connection === hub && hub.state === HubConnectionState.Connected) this.groupsReady = true;
    };
    this.connectionStart = join().finally(() => { if (this.connection === hub) this.connectionStart = null; });
    return this.connectionStart;
  }

  async disconnect(): Promise<void> {
    if (this.connection) await this.connection.stop();
    this.connection = null;
    this.connectionStart = null;
    this.groupsReady = false;
  }
}
