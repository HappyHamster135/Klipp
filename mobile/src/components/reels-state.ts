export function reelResumeTime(time:number,duration:number):number {
  if(!Number.isFinite(time)||time<0)return 0;
  return Number.isFinite(duration)&&duration>0?Math.min(time,Math.max(0,duration-0.05)):time;
}
