export interface TimelineEvent {at:string;kind:string;units:number;price:number}
export interface TimelineTrade {id:string;from:string;to:string;sign:number;finalGross:number;verified:boolean;events:TimelineEvent[];marks:{at:string;price:number;sourceAt:string|null}[]}
export interface SessionTimeline {trades:TimelineTrade[];unavailableReason:string|null;coverageNote?:string|null}
export const freshnessMs=30_000;
export function prepare(t:TimelineTrade){
  const from=Date.parse(t.from), to=Date.parse(t.to);
  const events=t.events.map(e=>({...e,time:Date.parse(e.at)})).sort((a,b)=>a.time-b.time);
  const fills=events.filter(e=>e.kind!=='Stop');
  // A quote is usable only as of its recorded time. Conflicting quotes at one timestamp stay unknown.
  const quotes=new Map<number,{price:number;source:number;conflict:boolean}>();
  for(const p of t.marks){
    const at=Date.parse(p.at),source=p.sourceAt?Date.parse(p.sourceAt):NaN,old=quotes.get(at);
    if(!old)quotes.set(at,{price:p.price,source,conflict:false});
    else if(old.price!==p.price || !Object.is(old.source,source))old.conflict=true;
  }
  const marks=[...quotes].sort((a,b)=>a[0]-b[0]);
  return {...t,from,to,events,fills,marks};
}
export type Series=ReturnType<typeof prepare>;
type Gap='Missing receipt'|'Stale broker price'|'Unverified quote'|'Incomplete fills';
export function valueAt(t:Series,time:number):{booked:number|null;total:number|null;gap?:Gap}{
  if(time<t.from) return {booked:0,total:0};
  if(time>=t.to) return {booked:t.finalGross,total:t.finalGross};
  if(!t.verified) return {booked:null,total:null,gap:'Incomplete fills'};
  let units=0,cost=0,booked=0;
  for(const e of t.fills){
    if(e.time>time) break;
    if(e.kind==='Entry'||e.kind==='Add-on'){cost+=e.price*e.units;units+=e.units;}
    else{
      if(units<e.units)return {booked:null,total:null,gap:'Incomplete fills'};
      const basis=cost/units;
      booked+=t.sign*(e.price-basis)*e.units; cost-=basis*e.units;units-=e.units;
    }
  }
  let lo=0,hi=t.marks.length;
  while(lo<hi){const m=(lo+hi)>>>1;if(t.marks[m][0]<=time)lo=m+1;else hi=m;}
  const last=t.marks[lo-1];
  if(!units)return {booked,total:booked};
  const fill=t.fills.find(e=>e.time===time);
  if(fill)return {booked,total:booked+t.sign*(fill.price*units-cost)};
  let gap:Gap|undefined;
  if(!last || time-last[0]>freshnessMs)gap='Missing receipt';
  else if(last[1].conflict || !Number.isFinite(last[1].source) || last[1].source>last[0])gap='Unverified quote';
  else if(time-last[1].source>freshnessMs)gap='Stale broker price';
  return gap?{booked,total:null,gap}:{booked,total:booked+t.sign*(last[1].price*units-cost)};
}
export function sessionPoints(day:string,series:Series[]){
  const start=Date.parse(day+'T09:15:00+05:30'),end=Date.parse(day+'T15:30:00+05:30');
  const times=new Set<number>([start,end]);
  for(const t of series){
    times.add(t.from);times.add(t.to);
    for(const e of t.events){times.add(e.time);if(e.kind!=='Stop'&&e.time+1<t.to)times.add(e.time+1);}
    for(const [at,q] of t.marks){
      times.add(at);
      for(const expiry of [at+freshnessMs+1,q.source+freshnessMs+1])
        if(expiry>at&&expiry<t.to)times.add(expiry);
    }
  }
  return [...times].filter(t=>t>=start&&t<=end).sort((a,b)=>a-b).map(time=>{
    let booked:number|null=0,total:number|null=0;
    const reasons=new Set<Gap>();
    for(const t of series){
      const v=valueAt(t,time);
      if(v.gap)reasons.add(v.gap);
      booked=booked===null||v.booked===null?null:booked+v.booked;
      total=total===null||v.total===null?null:total+v.total;
    }
    return {time,booked,total,reasons:[...reasons]};
  });
}
export type Point=ReturnType<typeof sessionPoints>[number];
export function extremes(points:Pick<Point,'booked'|'total'|'time'>[]){
  let peak=-1,high=-1,start=-1,end=-1,drop=0;
  points.forEach((p,i)=>{
    if(p.total===null){high=-1;return;}
    if(peak<0||p.total>points[peak].total!)peak=i;
    if(high<0||p.total>points[high].total!)high=i;
    const d=points[high].total!-p.total;
    if(d>drop){drop=d;start=high;end=i;}
  });
  return {peak,start,end,drop};
}
