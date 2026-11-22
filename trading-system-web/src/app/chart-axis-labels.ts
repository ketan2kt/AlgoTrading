export interface ChartAxisLabel { title:string; price:number; color:string; sourceY:number; y:number; }

/** Move only the captions, never the underlying levels. Connectors retain their true coordinates. */
export function arrangeChartLabels(labels:ChartAxisLabel[], height:number):ChartAxisLabel[] {
  const rows=labels.filter(l=>Number.isFinite(l.sourceY) && l.sourceY>=0 && l.sourceY<=height)
    .map(l=>({...l})).sort((a,b)=>a.sourceY-b.sourceY || a.title.localeCompare(b.title));
  if (!rows.length || height<20) return [];
  const gap=Math.min(20,(height-20)/Math.max(1,rows.length-1));
  rows.forEach((row,i)=>row.y=Math.max(10,row.sourceY,i ? rows[i-1].y+gap : 10));
  rows[rows.length-1].y=Math.min(rows[rows.length-1].y,height-10);
  for(let i=rows.length-2;i>=0;i--) rows[i].y=Math.min(rows[i].y,rows[i+1].y-gap);
  return rows;
}
