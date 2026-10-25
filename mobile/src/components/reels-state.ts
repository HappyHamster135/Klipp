/** Keep selection stable while intersection callbacks arrive in either order. */
export function mostVisibleReel(ratios:ReadonlyMap<string,number>,current:string|null):string|null {
  let result=current,largest=current?ratios.get(current)??0:0;
  for(const [id,ratio] of ratios)if(ratio>largest){result=id;largest=ratio;}
  return largest>=0.55?result:null;
}

export function reelResumeTime(time:number,duration:number):number {
  if(!Number.isFinite(time)||time<0)return 0;
  return Number.isFinite(duration)&&duration>0?Math.min(time,Math.max(0,duration-0.05)):time;
}
