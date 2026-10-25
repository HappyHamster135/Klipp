import {describe,expect,it} from 'vitest';
import {reelResumeTime} from './reels-state';

describe('reel saved position',()=>{
  it('clamps a restored seek before the end and rejects corrupt positions',()=>{
    expect(reelResumeTime(20,8)).toBe(7.95);
    expect(reelResumeTime(-1,8)).toBe(0);
    expect(reelResumeTime(Number.NaN,8)).toBe(0);
    expect(reelResumeTime(3,Number.NaN)).toBe(3);
  });
});
