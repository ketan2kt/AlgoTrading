import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import { TradingWorkspaceSnapshot } from './trading-workspace';

export function isActiveMarketUpdate(activeMarket: string, updatedMarket: string): boolean {
  return activeMarket === updatedMarket;
}

@Injectable({ providedIn: 'root' })
export class TradingWorkspaceService {
  private readonly http = inject(HttpClient);
  private readonly updates = new Subject<TradingWorkspaceSnapshot>();
  private connection: HubConnection | null = null;
  private connectionStart: Promise<void> | null = null;
  private activeMarket = 'nifty';

  getMarket(market: string): Observable<TradingWorkspaceSnapshot> {
    return this.http.get<TradingWorkspaceSnapshot>(`/api/trading-workspace/${market}?candleCount=1500`);
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
        if (isActiveMarketUpdate(this.activeMarket, updatedMarket)) {
          this.updates.next(snapshot as TradingWorkspaceSnapshot);
        }
      });
      this.connection.onreconnected(() =>
        this.connection?.invoke('SubscribeMarketWorkspace', this.activeMarket));
      this.connectionStart = this.connection.start();
    }
    await this.connectionStart;
    await this.connection.invoke('SubscribeMarketWorkspace', this.activeMarket);
  }

  async disconnect(): Promise<void> {
    if (this.connection) await this.connection.stop();
    this.connection = null;
    this.connectionStart = null;
  }
}
