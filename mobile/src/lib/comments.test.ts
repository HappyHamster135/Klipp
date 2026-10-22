import {describe,expect,it} from 'vitest';
import type {CommunityClip,CommunityComment} from './models';
import {mergeCommentPages,unchangedInteractions} from './comments';

const comment=(id:string,createdAt:string):CommunityComment=>({id,createdAt,clipId:'clip',ownerId:'member',username:'member',displayName:'Member',body:id});
describe('comment page reconciliation',()=>{
  it('keeps a newly posted comment when an earlier page finishes late',()=>{
    const fresh=comment('fresh','2026-10-22T12:01:00Z'),old=comment('old','2026-10-22T12:00:00Z');
    expect(mergeCommentPages([fresh],[old],new Set()).map(row=>row.id)).toEqual(['fresh','old']);
  });
  it('deduplicates overlapping pages and filters deleted comments from late responses',()=>{
    const newer=comment('new','2026-10-22T12:01:00Z'),old=comment('old','2026-10-22T12:00:00Z');
    expect(mergeCommentPages([newer],[newer,old],new Set(['old']))).toEqual([newer]);
  });
  it('orders equal timestamps by the same descending ID cursor convention',()=>{
    const a=comment('a','2026-10-22T12:00:00Z'),b=comment('b','2026-10-22T12:00:00Z');
    expect(mergeCommentPages([a],[b],new Set()).map(row=>row.id)).toEqual(['b','a']);
  });
  it('preserves PostgreSQL microseconds before comparing comment IDs',()=>{
    const earlier=comment('z','2026-10-22T12:00:00.000001Z'),later=comment('a','2026-10-22T13:00:00.000002+01:00');
    expect(mergeCommentPages([earlier],[later],new Set()).map(row=>row.id)).toEqual(['a','z']);
  });
  it('rejects a delayed metadata response after a newer Like or comment acknowledgement',()=>{
    const before={id:'clip',likeCount:1,likedByMe:false,commentCount:2} as CommunityClip;
    expect(unchangedInteractions(before,{...before,likeCount:2,likedByMe:true})).toBe(false);
    expect(unchangedInteractions(before,{...before,commentCount:3})).toBe(false);
    expect(unchangedInteractions(before,before)).toBe(true);
    expect(unchangedInteractions(before,{...before})).toBe(false);
  });
});
