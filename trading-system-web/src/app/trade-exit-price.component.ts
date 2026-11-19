import {Component, Input} from '@angular/core';
import {DecimalPipe} from '@angular/common';
import {WorkspaceTradeOverlay} from './trading-workspace';

@Component({
  selector:'app-trade-exit-price', standalone:true, imports:[DecimalPipe],
  template:`
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
  `,
  styles:[`:host{display:grid;gap:5px;min-width:0}span{font-size:.68rem;text-transform:uppercase;letter-spacing:.05em;color:#557460}strong{font-size:1rem}details{font-size:.75rem}summary{cursor:pointer;width:fit-content;border:1px solid #a2bea8;border-radius:4px;padding:2px 6px;color:#205b39}summary:focus-visible{outline:2px solid #176c3c}p{margin:5px 0;overflow-wrap:anywhere}`]
})
export class TradeExitPriceComponent {
  @Input({required:true}) trade!: WorkspaceTradeOverlay;
}
