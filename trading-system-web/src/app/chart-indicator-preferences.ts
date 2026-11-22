export interface ChartOverlayVisibility {
  day:boolean; previous:boolean; openingRange:boolean; vwap:boolean; ema:boolean;
  structure:boolean; structureHistory:boolean; adx:boolean; chandelier:boolean;
}
export const terminalIndicatorKey='sarthi.chartTerminalIndicators.v2';
export function chartIndicatorDefaults(terminal:boolean):ChartOverlayVisibility {
  return {day:true,previous:!terminal,openingRange:!terminal,vwap:true,ema:true,
    structure:true,structureHistory:!terminal,adx:true,chandelier:!terminal};
}
export function readChartIndicators(raw:string|null, terminal:boolean):ChartOverlayVisibility {
  const defaults=chartIndicatorDefaults(terminal);
  try {
    const saved:unknown=JSON.parse(raw || '{}');
    if (!saved || typeof saved!=='object' || Array.isArray(saved)) return defaults;
    for(const key of Object.keys(defaults) as (keyof ChartOverlayVisibility)[]) {
      const value=(saved as Record<string,unknown>)[key];
      if(typeof value==='boolean')defaults[key]=value;
    }
  } catch { /* Invalid local preferences cannot prevent chart rendering. */ }
  return defaults;
}
