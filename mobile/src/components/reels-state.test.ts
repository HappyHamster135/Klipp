import {describe,expect,it} from 'vitest';
import {mostVisibleReel,reelResumeTime} from './reels-state';

describe('reel visibility handoff',()=>{
  it('does not switch the decoder while neither card owns most of the viewport',()=>{
    expect(mostVisibleReel(new Map([['a',.5],['b',.5]]),'a')).toBeNull();
    expect(mostVisibleReel(new Map([['a',.4],['b',.6]]),'a')).toBe('b');
  });
  it('keeps the current item on equal visibility and ignores vanished cards',()=>{
    expect(mostVisibleReel(new Map([['a',.6],['b',.6]]),'b')).toBe('b');
    expect(mostVisibleReel(new Map([['b',1]]),'a')).toBe('b');
  });
  it('clamps a restored seek before the end and rejects corrupt positions',()=>{
    expect(reelResumeTime(20,8)).toBe(7.95);
    expect(reelResumeTime(-1,8)).toBe(0);
    expect(reelResumeTime(Number.NaN,8)).toBe(0);
    expect(reelResumeTime(3,Number.NaN)).toBe(3);
  });
});
