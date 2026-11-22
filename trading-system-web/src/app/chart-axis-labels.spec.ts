import {describe,it,expect} from 'vitest';
import {arrangeChartLabels} from './chart-axis-labels';

describe('chart indicator caption layout',()=>{
  it('spaces crowded labels without moving any true price/anchor or mutating input',()=>{
    const labels=[100,101,102,103,104].map((y,i)=>({title:`Level ${i}`,price:25000+i,color:'#123',sourceY:y,y}));
    const result=arrangeChartLabels(labels,500);
    expect(result.map(l=>l.sourceY)).toEqual(labels.map(l=>l.sourceY));
    expect(result.map(l=>l.price)).toEqual(labels.map(l=>l.price));
    expect(result.slice(1).every((l,i)=>l.y-result[i].y>=20)).toBe(true);
    expect(labels[1].y).toBe(101);
  });
  it('keeps edge labels inside the price pane and omits offscreen anchors',()=>{
    const labels=[-20,0,1,299,300,310].map((y,i)=>({title:`L${i}`,price:i,color:'#123',sourceY:y,y}));
    const result=arrangeChartLabels(labels,300);
    expect(result).toHaveLength(4);
    expect(result.every(l=>l.y>=10 && l.y<=290)).toBe(true);
    expect(result.slice(1).every((l,i)=>l.y-result[i].y>=20)).toBe(true);
  });
  it('handles empty and short panes without inventing prices',()=>{
    expect(arrangeChartLabels([],300)).toEqual([]);
    expect(arrangeChartLabels([{title:'A',price:100,color:'#123',sourceY:2,y:2}],0)).toEqual([]);
  });
});
