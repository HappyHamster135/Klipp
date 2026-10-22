export function validRange(start:number,end:number,duration:number){return [start,end,duration].every(Number.isFinite)&&start>=0&&end>start&&end<=duration+0.02&&end-start<=60.001;}
export function outputDimensions(width:number,height:number){const scale=Math.min(1,1280/width,720/height);return{width:Math.max(2,Math.floor(width*scale/2)*2),height:Math.max(2,Math.floor(height*scale/2)*2)};}
