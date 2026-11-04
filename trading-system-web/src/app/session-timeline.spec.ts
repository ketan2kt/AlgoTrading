import {describe,expect,it} from 'vitest';
import {extremes,prepare,sessionPoints,TimelineTrade,valueAt} from './session-timeline';

const date='2026-11-05';
const at=(s:number)=>new Date(Date.parse(date+'T10:00:00+05:30')+s*1000).toISOString();
const tick=(s:number,price:number,source=s)=>({at:at(s),price,sourceAt:at(source)});
function trade():TimelineTrade{return {id:'a',from:at(0),to:at(180),sign:1,verified:true,finalGross:60,
  events:[{at:at(0),kind:'Entry',units:10,price:100},{at:at(20),kind:'Partial',units:5,price:110},
    {at:at(180),kind:'Exit',units:5,price:102}],marks:[tick(10,110),tick(60,104)]};}
describe('session timeline',()=>{
  it('books a partial once, leaves stale open marks unknown and settles to recorded gross',()=>{
    const t=prepare(trade());
    expect(valueAt(t,Date.parse(at(20)))).toEqual({booked:50,total:100});
    expect(valueAt(t,Date.parse(at(55)))).toEqual({booked:50,total:null,gap:'Missing receipt'});
    expect(valueAt(t,Date.parse(at(60)))).toEqual({booked:50,total:70});
    expect(valueAt(t,Date.parse(at(180)))).toEqual({booked:60,total:60});
  });
  it('does not add peaks reached at different times or carry a drawdown through a gap',()=>{
    const b=trade();b.id='b';b.marks=[tick(10,90),tick(60,120)];
    const pts=sessionPoints(date,[prepare(trade()),prepare(b)]);
    expect(pts.find(p=>p.time===Date.parse(at(10)))?.total).toBe(0);
    expect(pts.some(p=>p.total===null)).toBe(true);
    const separated=extremes([{time:1,booked:0,total:100},{time:2,booked:0,total:null},{time:3,booked:0,total:20}]);
    expect(separated.drop).toBe(0);
  });
  it('does not substitute zero for incomplete fill history',()=>{
    const t=prepare({...trade(),verified:false});
    expect(valueAt(t,Date.parse(at(10))).total).toBeNull();
    expect(valueAt(t,Date.parse(at(180))).booked).toBe(60);
  });
  it('does not refresh an old price on repeated receipt and distinguishes missing from stale',()=>{
    const t=prepare({...trade(),marks:[tick(10,110),tick(40,110,10),tick(80,108)]});
    expect(valueAt(t,Date.parse(at(40))).total).toBe(100);
    expect(valueAt(t,Date.parse(at(40))+1).gap).toBe('Stale broker price');
    expect(valueAt(t,Date.parse(at(71))).gap).toBe('Missing receipt');
    expect(valueAt(t,Date.parse(at(80))).total).toBe(90);
    const pts=sessionPoints(date,[t]);
    expect(pts.find(p=>p.time===Date.parse(at(40))+1)?.total).toBeNull();
  });
  it('rejects missing or future price timestamps',()=>{
    const t=prepare({...trade(),marks:[{...tick(10,110),sourceAt:null},tick(60,104,65)]});
    expect(valueAt(t,Date.parse(at(10))).gap).toBe('Unverified quote');
    expect(valueAt(t,Date.parse(at(60))).gap).toBe('Unverified quote');
  });
});
