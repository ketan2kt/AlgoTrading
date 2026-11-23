import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, Input } from '@angular/core';

export interface DailyCapitalSummary {
  netRealisedPnl:number;
  peakCapitalUsed:number|null;
  peakAtUtc:string|null;
  returnPercent:number|null;
  status:string;
  note:string;
}

@Component({
  selector:'app-daily-capital-summary',
  imports:[DatePipe, DecimalPipe],
  template:`
    <section aria-label="Paper capital and return" class="capital-summary">
      <article><span>Peak capital used</span>
        <strong>{{summary?.peakCapitalUsed == null ? '—' : '₹'+(summary!.peakCapitalUsed|number:'1.2-2')}}</strong>
        <small>{{summary?.peakAtUtc ? (summary!.peakAtUtc|date:'HH:mm:ss':'+0530')+' IST' : summary?.status === 'NoTrades' ? 'No trades' : 'Fill evidence incomplete'}}</small>
      </article>
      <article><span>Return on peak capital</span>
        <strong [class.good]="(summary?.returnPercent ?? 0)>0" [class.bad]="(summary?.returnPercent ?? 0)<0">{{summary?.returnPercent == null ? '—' : (summary!.returnPercent|number:'1.2-2')+'%'}}</strong>
        <small>Net realised ÷ peak capital · includes Hero Zero</small>
      </article>
    </section>
    <p class="capital-note">{{summary?.note ?? 'Capital metrics require recorded entry and exit fills.'}}</p>
  `,
  styles:[`
    .capital-summary{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;margin-top:10px}
    article{border:1px solid var(--ui-edge);border-radius:8px;padding:12px;background:var(--ui-surface);display:grid;gap:5px}
    span{font-size:12px;color:#486253}strong{font-size:23px;font-variant-numeric:tabular-nums;color:#193d2b}
    small,.capital-note{font-size:12px;color:#52685b}.capital-note{margin:8px 0 16px}
    .good{color:#177244}.bad{color:#b22c35}
    @media(max-width:560px){.capital-summary{grid-template-columns:1fr}strong{font-size:20px}}
  `]
})
export class DailyCapitalSummaryComponent {
  @Input() summary:DailyCapitalSummary|null|undefined;
}
