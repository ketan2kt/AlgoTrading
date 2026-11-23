import {Component, EventEmitter, Input, Output} from '@angular/core';
import {DatePipe, DecimalPipe} from '@angular/common';
import {WorkspaceTradeOverlay} from './trading-workspace';
import {compactContractName} from './contract-name';
import {TradeExitPriceComponent} from './trade-exit-price.component';

@Component({
  selector:'app-compact-trade-ledger', standalone:true,
  imports:[DatePipe, DecimalPipe, TradeExitPriceComponent],
  template:`
    @for (group of groups; track group.date) {
      <div class="session-heading">{{group.date}} · Prices and P&amp;L in ₹ · Times in IST</div>
      <div class="ledger-scroll" role="region" aria-label="Trade ledger, scroll for more columns" tabindex="0">
        <table>
          <thead><tr><th>Status</th><th class="number">Net P&amp;L</th><th>Contract</th><th class="number" title="Entry premium">E</th><th class="number" title="Average exit when closed; current premium while open">Avg X / Now</th><th class="number">SL</th><th class="number">Tgt</th><th class="number">Qty</th><th>Exp</th><th class="number">Gross</th><th class="number">Costs</th><th class="number" title="Maximum observed gross profit">Peak</th><th class="number" title="Maximum observed gross adverse loss">MAE</th><th>In</th><th>Out</th>@if (hasActive) { <th>Action</th> }</tr></thead>
          <tbody>
            @for (t of group.trades; track t.signalId) {
              <tr [id]="'position-'+t.signalId" tabindex="-1" [class.open-position]="!t.exitTimeUtc">
                <td><button type="button" class="status" [attr.aria-expanded]="expandedStatus===t.signalId" [attr.aria-label]="t.lifecycleStatus" (click)="expandedStatus=expandedStatus===t.signalId ? null : t.signalId">{{shortStatus(t.lifecycleStatus)}}</button></td>
                <td class="number net" [class.good]="(net(t) ?? 0)>0" [class.bad]="(net(t) ?? 0)<0" [title]="t.exitTimeUtc ? 'Final realised net P&L' : 'Estimated net at latest mark, including booked partials and estimated closing costs'"><strong>{{signed(net(t))}}</strong></td>
                <td><strong>{{contract(t)}}</strong></td>
                <td class="number">{{t.fillPrice | number:'1.2-2'}}</td>
                <td class="number"><app-trade-exit-price [trade]="t" [compact]="true" /></td>
                <td class="number">{{t.executionStopLoss | number:'1.2-2'}}</td>
                <td class="number">{{t.executionTarget | number:'1.2-2'}}</td>
                <td class="number">{{t.quantity}}</td><td>{{t.executionExpiry | date:'dd MMM':'+0530'}}</td>
                <td class="number" [class.good]="(gross(t) ?? 0)>0" [class.bad]="(gross(t) ?? 0)<0">{{signed(gross(t),0)}}</td>
                <td class="number">{{costs(t)==null ? '—' : (costs(t) | number:'1.0-0')}}</td>
                <td class="number good">{{t.maximumFavourablePnl==null ? '—' : (t.maximumFavourablePnl | number:'1.0-0')}}</td>
                <td class="number bad">{{t.maximumAdverseLoss==null ? '—' : (t.maximumAdverseLoss | number:'1.0-0')}}</td>
                <td>{{t.entryTimeUtc | date:'HH:mm:ss':'+0530'}}</td><td>{{t.exitTimeUtc ? (t.exitTimeUtc | date:'HH:mm:ss':'+0530') : 'Open'}}</td>
                @if (hasActive) { <td>@if (t.lifecycleStatus.startsWith('Active')) { <button type="button" (click)="exitTrade.emit(t)" [disabled]="cached || requested.has(market+':'+t.signalId)">{{requested.has(market+':'+t.signalId) ? 'Requested' : 'Exit trade'}}</button> }</td> }
              </tr>
              @if (expandedStatus===t.signalId) { <tr class="status-detail"><td [attr.colspan]="hasActive?16:15">{{contract(t)}} · {{t.lifecycleStatus}}</td></tr> }
            }
          </tbody>
        </table>
      </div>
    } @empty { <p>No trades taken for this market this session.</p> }
  `,
  styleUrl:'./compact-trade-ledger.component.scss',
})
export class CompactTradeLedgerComponent {
  @Input({required:true}) trades:WorkspaceTradeOverlay[]=[];
  @Input() market='nifty';
  @Input() cached=false;
  @Input() requested:ReadonlySet<string>=new Set();
  @Output() exitTrade=new EventEmitter<WorkspaceTradeOverlay>();
  expandedStatus:string|null=null;
  readonly contract=compactContractName;
  get hasActive(){return this.trades.some(t=>t.lifecycleStatus.startsWith('Active'));}
  get groups(){
    const groups=new Map<string,WorkspaceTradeOverlay[]>();
    for(const t of this.trades){
      const raw=t.entryTimeUtc ?? t.signalTimeUtc;
      const date=raw ? new Date(raw).toLocaleDateString('en-GB',{timeZone:'Asia/Kolkata',day:'2-digit',month:'short',year:'numeric'}) : 'Session';
      if(!groups.has(date))groups.set(date,[]);
      groups.get(date)!.push(t);
    }
    return [...groups].map(([date,trades])=>({date,trades}));
  }
  net(t:WorkspaceTradeOverlay){return t.exitTimeUtc ? t.realisedPnl : t.openPnlEstimate?.net ?? null;}
  gross(t:WorkspaceTradeOverlay){return t.exitTimeUtc ? t.grossRealisedPnl : t.openPnlEstimate?.gross;}
  costs(t:WorkspaceTradeOverlay){return t.exitTimeUtc ? t.realisedCosts : t.openPnlEstimate?.costs;}
  signed(n:number|null|undefined,digits=2){return n==null || !Number.isFinite(n) ? '—' : (n>0?'+':n<0?'−':'')+Math.abs(n).toLocaleString('en-IN',{minimumFractionDigits:digits,maximumFractionDigits:digits});}
  shortStatus(status:string){
    const key=status.replace(/[\s·_-]/g,'').toLowerCase();
    if(key.startsWith('active'))return key.includes('unavailable')?'Open · no quote':'Open';
    const labels:Record<string,string>={bankniftyconfirmedtrail:'Trail',stoplosshit:'SL',slhit:'SL',
      underlyingtrendinvalidated:'Trend break',trendreversalexit:'Trend break',failedsetupexit:'Setup exit',targethit:'Target',
      manualexit:'Manual',manualclose:'Manual',sessionclose:'Session end',sessionend:'Session end',
      profitprotection:'Profit lock',profitprotectionexit:'Profit lock',trailingstophit:'Trail'};
    return labels[key] ?? status.replace(/([a-z])([A-Z])/g,'$1 $2');
  }
}
