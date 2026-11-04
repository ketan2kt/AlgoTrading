import { WorkspaceCandle, WorkspaceVolumeBar } from './trading-workspace';

export interface ChartLevelSnapshot {
  sessionKey:string; dayHigh:number; dayLow:number; dayRange:number; dayRangePercent:number;
  previousHigh:number|null; previousLow:number|null; previousClose:number|null;
  openingRangeHigh:number|null; openingRangeLow:number|null;
}
export interface IndicatorPoint { openTimeUtc:string; value:number; }

const sessionDate = new Intl.DateTimeFormat('en-US',{timeZone:'Asia/Kolkata',year:'numeric',month:'2-digit',day:'2-digit'});
export function sessionKey(openTimeUtc:string):string {
  const parts=sessionDate.formatToParts(new Date(openTimeUtc));
  const part=(type:string)=>parts.find(value=>value.type===type)?.value || '';
  return `${part('year')}-${part('month')}-${part('day')}`;
}

export function chartLevels(candles:WorkspaceCandle[],openingRangeMinutes=15):ChartLevelSnapshot|null {
  if (!candles.length) return null;
  const ordered=[...candles].sort((a,b)=>Date.parse(a.openTimeUtc)-Date.parse(b.openTimeUtc));
  const keys=[...new Set(ordered.map(value=>sessionKey(value.openTimeUtc)))];
  const currentKey=keys.at(-1)!;
  const current=ordered.filter(value=>sessionKey(value.openTimeUtc)===currentKey);
  const previousKey=keys.length>1 ? keys.at(-2)! : null;
  const previous=previousKey ? ordered.filter(value=>sessionKey(value.openTimeUtc)===previousKey) : [];
  const start=Date.parse(currentKey+'T09:15:00+05:30');
  const end=start+openingRangeMinutes*60000;
  const opening=current.filter(c=>Date.parse(c.openTimeUtc)>=start && Date.parse(c.openTimeUtc)<end);
  let cursor=start;
  const complete=opening.every(c=>{
    if(!c.isClosed || c.intervalSeconds<=0 || Date.parse(c.openTimeUtc)!==cursor)return false;
    cursor+=c.intervalSeconds*1000;
    return cursor<=end;
  }) && cursor===end;
  const dayHigh=Math.max(...current.map(value=>value.high));
  const dayLow=Math.min(...current.map(value=>value.low));
  return {sessionKey:currentKey,dayHigh,dayLow,dayRange:dayHigh-dayLow,
    dayRangePercent:current[0].open>0 ? (dayHigh-dayLow)/current[0].open*100 : 0,
    previousHigh:previous.length ? Math.max(...previous.map(value=>value.high)) : null,
    previousLow:previous.length ? Math.min(...previous.map(value=>value.low)) : null,
    previousClose:previous.length ? previous.at(-1)!.close : null,
    openingRangeHigh:complete?Math.max(...opening.map(value=>value.high)):null,openingRangeLow:complete?Math.min(...opening.map(value=>value.low)):null};
}

export function ema(candles:WorkspaceCandle[],period:number):IndicatorPoint[] {
  if (!candles.length) return [];
  const multiplier=2/(period+1); let current=candles[0].close;
  return candles.map((candle,index)=>{current=index===0?candle.close:candle.close*multiplier+current*(1-multiplier);return {openTimeUtc:candle.openTimeUtc,value:current};});
}

export function sessionVwap(candles:WorkspaceCandle[],volumeBars:WorkspaceVolumeBar[]=[]):IndicatorPoint[] {
  const volumes=new Map(volumeBars.map(value=>[Date.parse(value.openTimeUtc),value.volume]));
  let key=''; let priceVolume=0; let totalVolume=0;
  return candles.map(candle=>{const next=sessionKey(candle.openTimeUtc);if(next!==key){key=next;priceVolume=0;totalVolume=0;}
    const volume=volumes.get(Date.parse(candle.openTimeUtc))??candle.volume;if(volume>0){priceVolume+=((candle.high+candle.low+candle.close)/3)*volume;totalVolume+=volume;}
    return {openTimeUtc:candle.openTimeUtc,value:totalVolume>0?priceVolume/totalVolume:Number.NaN};}).filter(value=>Number.isFinite(value.value));
}
