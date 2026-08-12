import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import { TradingWorkspaceSnapshot } from './trading-workspace';

@Injectable({ providedIn: 'root' })
export class TradingWorkspaceService {
  private readonly http = inject(HttpClient);
  private readonly updates = new Subject<TradingWorkspaceSnapshot>();
  private connection: HubConnection | null = null;

  getNifty(): Observable<TradingWorkspaceSnapshot> {
    return this.http.get<TradingWorkspaceSnapshot>('/api/trading-workspace/nifty?candleCount=1500');
  }

  updates$(): Observable<TradingWorkspaceSnapshot> {
    return this.updates.asObservable();
  }

  async connect(): Promise<void> {
    if (this.connection?.state === HubConnectionState.Connected) return;
    this.connection = new HubConnectionBuilder()
      .withUrl('/hubs/system-health')
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .build();
    this.connection.on('niftyWorkspaceUpdated', (snapshot) =>
      this.updates.next(snapshot as TradingWorkspaceSnapshot),
    );
    await this.connection.start();
    await this.connection.invoke('SubscribeNiftyWorkspace');
  }

  async disconnect(): Promise<void> {
    if (this.connection) await this.connection.stop();
    this.connection = null;
  }
}
