import type {CommunityClip,CommunityComment} from './models';

/** Late pages must preserve new posts and must not resurrect a deleted comment. */
export function mergeCommentPages(current:CommunityComment[],incoming:CommunityComment[],removed:ReadonlySet<string>):CommunityComment[]{
  const rows=new Map<string,CommunityComment>();
  for(const comment of [...current,...incoming])if(!removed.has(comment.id))rows.set(comment.id,comment);
  return [...rows.values()].sort((a,b)=>{
    const first=timestampKey(a.createdAt),second=timestampKey(b.createdAt);
    return first<second?1:first>second?-1:a.id<b.id?1:a.id>b.id?-1:0;
  });
}
/** A background details read cannot undo interactions acknowledged since it began. */
export function unchangedInteractions(before:CommunityClip,current:CommunityClip):boolean{
  // Clip updates are immutable: identity also catches a Like/Unlike round trip.
  return before===current;
}
function timestampKey(value:string):bigint{
  const fraction=/\.(\d+)(?:Z|[+-]\d\d:\d\d)$/.exec(value)?.[1]??'';
  return BigInt(Math.trunc(Date.parse(value)))*1000n+BigInt(fraction.padEnd(6,'0').slice(3,6)||'0');
}
