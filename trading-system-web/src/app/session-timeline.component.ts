import {ChangeDetectorRef,Component,inject,Input,OnChanges,OnDestroy,output} from '@angular/core';
import {Subscription} from 'rxjs';
import {DailyDiagnostics,PaperReportService} from './paper-report.service';
import {extremes,Point,prepare,Series,sessionPoints,SessionTimeline,valueAt} from './session-timeline';

const money=new Intl.NumberFormat('en-IN',{style:'currency',currency:'INR',maximumFractionDigits:0});
const clock=new Intl.DateTimeFormat('en-GB',{timeZone:'Asia/Kolkata',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false});

@Component({selector:'app-session-timeline',templateUrl:'./session-timeline.component.html',styleUrl:'./session-timeline.component.scss'})
export class SessionTimelineComponent implements OnChanges,OnDestroy{
  @Input({required:true}) report!:DailyDiagnostics;
  readonly replay=output<string>();
  private readonly api=inject(PaperReportService);
  private readonly cd=inject(ChangeDetectorRef);
  private request?:Subscription;
  private expanded=false;
  protected timeline:SessionTimeline|null=null;
  protected pending=false;
  protected problem='';
  protected gaps:{start:number;end:number;reason:string}[]=[];
  protected series:Series[]=[];
  protected points:Point[]=[];
  protected stat=extremes([]);
  protected paths={booked:'',total:''};
  protected markers:{x:number;at:number;label:string}[]=[];
  protected first=0;protected last=0;protected pickEnd=false;
  protected rows:{label:string;group:string;delta:number|null}[]=[];
  protected gap=false;
  protected selectedChange:number|null=null;
  protected lo=0;protected hi=0;
  protected names=new Map<string,string>();
  protected cash=(n:number|null|undefined)=>n==null?'Unavailable':money.format(n);
  protected time=(n:number)=>clock.format(n);
  ngOnChanges(){
    this.request?.unsubscribe();this.pending=false;this.problem='';this.timeline=null;
    if(this.expanded)this.load();
  }
  ngOnDestroy(){this.request?.unsubscribe();}
  protected toggle(open:boolean){this.expanded=open;if(open&&!this.timeline&&!this.pending)this.load();}
  protected load(){
    this.pending=true;this.problem='';
    this.request=this.api.getTimeline(this.report.tradingDate).subscribe({
      next:data=>{this.timeline=data;this.pending=false;this.render();this.cd.markForCheck();},
      error:e=>{this.pending=false;this.problem=e.status===409?'Available after 15:30 IST.':'Could not load recorded quotes. Reopen the timeline to retry.';this.cd.markForCheck();}
    });
  }
  private render(){
    this.series=(this.timeline?.trades??[]).map(prepare);
    this.names=new Map(this.report.trades.map(t=>[t.tradeId,t.market+' · '+t.symbol]));
    this.points=this.series.length?sessionPoints(this.report.tradingDate,this.series):[];
    this.stat=extremes(this.points);
    this.pickEnd=false;this.markers=[];this.gaps=[];
    if(!this.points.length)return;
    for(let i=0;i<this.points.length-1;i++){
      const p=this.points[i], reason=p.reasons.join(' + ');
      if(!reason)continue;
      const prev=this.gaps[this.gaps.length-1],end=this.points[i+1].time;
      if(prev?.reason===reason && prev.end===p.time)prev.end=end;else this.gaps.push({start:p.time,end,reason});
    }
    const values=this.points.flatMap(p=>[p.total,p.booked]).filter((n):n is number=>n!==null);
    this.lo=0;this.hi=1;
    for(const n of values){this.lo=Math.min(this.lo,n);this.hi=Math.max(this.hi,n);}
    this.paths={booked:this.path('booked'),total:this.path('total')};
    this.markers=this.series.flatMap(t=>t.events.map(e=>({x:this.x(e.time),at:e.time,label:this.names.get(t.id)+' · '+e.kind+' @ '+this.cash(e.price)}))).sort((a,b)=>a.at-b.at);
    this.select(this.stat.start<0?0:this.stat.start,this.stat.end<0?this.points.length-1:this.stat.end);
  }
  protected x(t:number){return 1000*(t-this.points[0].time)/(this.points[this.points.length-1].time-this.points[0].time);}
  protected y(n:number){return 145-(n-this.lo)*132/(this.hi-this.lo);}
  private path(key:'booked'|'total'){
    let next=true,d='';
    for(const p of this.points){const n=p[key];if(n===null){next=true;continue;}
      const x=this.x(p.time).toFixed(2),y=this.y(n).toFixed(2);
      d+=next?'M'+x+' '+y+'h0.1':'H'+x+'V'+y;next=false;
    }return d;
  }
  protected select(a:number,b:number){
    this.first=Math.min(a,b);this.last=Math.max(a,b);
    const left=this.points[this.first],right=this.points[this.last];
    this.gap=this.points.slice(this.first,this.last+1).some(p=>p.total===null);
    this.selectedChange=left.total===null||right.total===null?null:right.total-left.total;
    this.rows=this.series.filter(t=>t.to>=left.time&&t.from<=right.time).map(t=>{
      const a=valueAt(t,left.time).total,b=valueAt(t,right.time).total;
      return {label:this.names.get(t.id)??t.id,group:t.from>left.time?'New trade':'Already open',delta:a===null||b===null?null:b-a};
    }).sort((a,b)=>(a.delta??Infinity)-(b.delta??Infinity));
  }
  protected chartClick(event:MouseEvent){
    const box=(event.currentTarget as SVGSVGElement).getBoundingClientRect();
    const t=this.points[0].time+(event.clientX-box.left)/box.width*(this.points[this.points.length-1].time-this.points[0].time);
    let i=0;while(i+1<this.points.length&&Math.abs(this.points[i+1].time-t)<Math.abs(this.points[i].time-t))i++;
    if(this.pickEnd)this.select(this.first,i);else this.select(i,i);
    this.pickEnd=!this.pickEnd;
  }
  protected biggest(){this.pickEnd=false;this.select(this.stat.start,this.stat.end);}
}
