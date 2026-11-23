import {Component, Input} from '@angular/core';
import {DecimalPipe} from '@angular/common';
import {WorkspaceTradeOverlay} from './trading-workspace';

@Component({
  selector:'app-trade-exit-price', standalone:true, imports:[DecimalPipe],
  template:`
    @if (compact) {
      @if (trade.exitTimeUtc && trade.exitPrice != null) {
        <details class="compact-fills">
          <summary [attr.aria-label]="trade.exitBreakdown?.complete ? 'Average exit premium; expand exit fills' : 'Final exit premium; full average unavailable'">{{(trade.exitBreakdown?.complete ? trade.exitBreakdown!.averagePrice : trade.exitPrice) | number:'1.2-2'}}{{trade.exitBreakdown?.complete ? '' : '*'}} ▾</summary>
          <div class="fill-breakdown">
            @for (fill of trade.exitBreakdown?.fills ?? []; track $index) { <p>{{fill.kind}} · {{fill.quantity}} units × ₹{{fill.price | number:'1.2-4'}}</p> }
            <p>{{trade.exitBreakdown?.complete ? 'Quantity-weighted average before costs. P&L uses unrounded fills.' : 'Full breakdown unavailable. Showing final exit, not average.'}}</p>
          </div>
        </details>
      } @else { <strong>{{trade.currentOptionPrice != null ? (trade.currentOptionPrice | number:'1.2-2') : '—'}}</strong> }
    } @else {
    @if (trade.exitTimeUtc && trade.exitPrice != null) {
      @if (trade.exitBreakdown?.complete && trade.exitBreakdown?.averagePrice != null) {
        <span>Avg exit premium</span>
        <strong>{{trade.exitBreakdown!.averagePrice | number:'1.2-4'}}</strong>
      } @else {
        <span>Final exit premium</span>
        <strong>{{trade.exitPrice | number:'1.2-2'}}</strong>
      }
      <details>
        <summary>Exit fills</summary>
        @for (fill of trade.exitBreakdown?.fills ?? []; track $index) {
          <p>{{fill.kind}} · {{fill.quantity}} units × ₹{{fill.price | number:'1.2-4'}}</p>
        }
        @if (!trade.exitBreakdown?.complete) {
          <p>Full fill breakdown unavailable. Showing final exit, not average.</p>
        } @else {
          <p>Quantity-weighted average before costs. P&amp;L uses unrounded fill prices.</p>
        }
      </details>
    } @else {
      <span>Current premium</span>
      <strong>{{trade.currentOptionPrice != null ? (trade.currentOptionPrice | number:'1.2-2') : 'Awaiting quote'}}</strong>
    }
    }
  `,
  styles:[`:host{display:grid;gap:5px;min-width:0}span{font-size:.68rem;text-transform:uppercase;letter-spacing:.05em;color:var(--ui-muted)}strong{font-size:1rem}details{font-size:.75rem}summary{cursor:pointer;width:fit-content;border:1px solid var(--ui-edge);border-radius:4px;padding:2px 6px;color:var(--ui-ink)}summary:focus-visible{outline:2px solid var(--ui-accent)}p{margin:5px 0;overflow-wrap:anywhere}:host(.compact){display:block;font-size:12px}:host(.compact) strong{font:inherit}.compact-fills summary{margin-left:auto;list-style:none;border:0;padding:0;text-decoration:underline dotted;text-underline-offset:4px}.fill-breakdown{width:220px;max-width:50vw;white-space:normal;text-align:left;background:var(--ui-raised);padding:8px;border:1px solid var(--ui-edge)}`],
  host:{'[class.compact]':'compact'},
})
export class TradeExitPriceComponent {
  @Input({required:true}) trade!: WorkspaceTradeOverlay;
  @Input() compact=false;
}
