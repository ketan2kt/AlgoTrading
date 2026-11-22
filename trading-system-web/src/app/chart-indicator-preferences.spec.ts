import {chartIndicatorDefaults, readChartIndicators} from './chart-indicator-preferences';

describe('chart indicator preferences',()=>{
  it('starts the index terminal clean without removing core indicators',()=>{
    expect(chartIndicatorDefaults(true)).toEqual({day:true,previous:false,openingRange:false,vwap:true,ema:true,structure:true,structureHistory:false,adx:true,chandelier:false});
    expect(chartIndicatorDefaults(false).chandelier).toBe(true);
  });
  it('retains explicit selections and ignores invalid stored preferences',()=>{
    expect(readChartIndicators('{"chandelier":true,"adx":false,"day":"false"}',true)).toMatchObject({chandelier:true,adx:false,day:true});
    for(const raw of [null,'invalid','null','[]'])expect(readChartIndicators(raw,true)).toEqual(chartIndicatorDefaults(true));
  });
});
