// Deliberately separate from main.tsx: no Supabase client, keys, persisted sessions, or online writes.
import {createRoot} from 'react-dom/client';
import {App} from '../src/App';
import type {Api} from '../src/lib/service';
import {CommunityError} from '../src/lib/models';
import type {CommunityBlockedUser,CommunityClip,CommunityComment,CommunityCursor,CommunityProfile,CommunityUser,CommunityUsage,CommunitySupportRequest,CommunityReport,SupportCategory,CommunityPage,CommunityOperationsCapabilities} from '../src/lib/models';
import '../src/styles.css';

const me='11111111-1111-4111-8111-111111111111';
const friend='22222222-2222-4222-8222-222222222222';
const teammate='33333333-3333-4333-8333-333333333333';
const videoUrl='/.qa/test-video.mp4';
const initialClips:CommunityClip[]=[
  {id:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',ownerId:friend,username:'alex',displayName:'Alex',title:'Last-second save',description:'One last round with the team.',durationSeconds:6,byteSize:691836,createdAt:'2026-10-22T12:05:00Z',likeCount:12,commentCount:1,likedByMe:false,status:'published'},
  {id:'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',ownerId:me,username:'sam',displayName:'Sam',title:'Clean final round',description:'My own highlight.',durationSeconds:6,byteSize:691836,createdAt:'2026-10-22T12:04:00Z',likeCount:3,commentCount:0,likedByMe:false,status:'published'},
  {id:'cccccccc-cccc-4ccc-8ccc-cccccccccccc',ownerId:teammate,username:'mika',displayName:'Mika',title:'Night city sprint',description:'Testing another clip card.',durationSeconds:6,byteSize:691836,createdAt:'2026-10-22T12:03:00Z',likeCount:4,commentCount:0,likedByMe:false,status:'published'},
];
class FixtureApi implements Api {
  user:CommunityUser|null={id:me,email:'sam@example.test'};
  profile:CommunityProfile|null={id:me,username:'sam',displayName:'Sam',bio:'Game highlights and good company.'};
  clips=structuredClone(initialClips);
  comments:CommunityComment[]=[{id:'dddddddd-dddd-4ddd-8ddd-dddddddddddd',clipId:initialClips[0].id,ownerId:teammate,username:'mika',displayName:'Mika',body:'That timing!',createdAt:'2026-10-22T12:06:00Z'}];
  blocked=new Set<string>();
  reports:{id:string;reason:string}[]=[];
  playbackRequests:string[]=[];
  capabilities:CommunityOperationsCapabilities={version:1,enabled:true,operator:false,accountDeletion:true};
  pendingDeletionOnce=false;
  accountDeletionCalls=0;
  private listeners=new Set<()=>void>();
  constructor(private defaultPoster:string,empty=false){if(empty)this.clips=[];}
  private check(signal?:AbortSignal){signal?.throwIfAborted();if(!this.user)throw new Error('Sign in to use Media.');}
  private emit(){this.listeners.forEach(listener=>listener());}
  subscribe(listener:()=>void){this.listeners.add(listener);return()=>this.listeners.delete(listener);}
  async restoreSession(signal?:AbortSignal){signal?.throwIfAborted();return this.user;}
  async sendEmailCode(_email:string,_create:boolean,signal?:AbortSignal){signal?.throwIfAborted();}
  async verifyEmailCode(email:string,_code:string,signal?:AbortSignal){signal?.throwIfAborted();this.user={id:me,email};this.profile??={id:me,username:'sam',displayName:'Sam',bio:''};this.emit();return {...this.user};}
  async signOut(signal?:AbortSignal){signal?.throwIfAborted();this.user=null;this.profile=null;this.emit();}
  async getProfile(signal?:AbortSignal){this.check(signal);return {...this.profile!};}
  async saveProfile(profile:Pick<CommunityProfile,'username'|'displayName'|'bio'>,signal?:AbortSignal){this.check(signal);this.profile={...profile,id:me};this.clips=this.clips.map(clip=>clip.ownerId===me?{...clip,username:profile.username??'',displayName:profile.displayName}:clip);this.emit();return {...this.profile};}
  async getFeed(cursor:CommunityCursor|null=null,mine=false,signal?:AbortSignal){this.check(signal);let clips=this.clips.filter(clip=>!this.blocked.has(clip.ownerId)&&(!mine||clip.ownerId===me));if(cursor)clips=clips.filter(clip=>clip.createdAt<cursor.createdAt||(clip.createdAt===cursor.createdAt&&clip.id<cursor.id));const items=clips.slice(0,12);const last=items.at(-1);return{items:structuredClone(items),nextCursor:clips.length>12&&last?{id:last.id,createdAt:last.createdAt}:null};}
  async getClip(id:string,signal?:AbortSignal){this.check(signal);const clip=this.clips.find(item=>item.id===id);if(!clip)throw new Error('This clip is no longer available.');return {...clip};}
  async getComments(id:string,cursor:CommunityCursor|null=null,signal?:AbortSignal){this.check(signal);let comments=this.comments.filter(comment=>comment.clipId===id&&!this.blocked.has(comment.ownerId));if(cursor)comments=comments.filter(comment=>comment.createdAt<cursor.createdAt||(comment.createdAt===cursor.createdAt&&comment.id<cursor.id));const items=comments.slice(0,30);const last=items.at(-1);return{items:structuredClone(items),nextCursor:comments.length>30&&last?{id:last.id,createdAt:last.createdAt}:null};}
  async setLiked(id:string,liked:boolean,signal?:AbortSignal){const clip=await this.getClip(id,signal);const updated={...clip,likedByMe:liked,likeCount:clip.likeCount+(liked===clip.likedByMe?0:liked?1:-1)};this.clips=this.clips.map(item=>item.id===id?updated:item);return {...updated};}
  async addComment(id:string,body:string,signal?:AbortSignal){this.check(signal);const comment={id:crypto.randomUUID(),clipId:id,ownerId:me,username:this.profile!.username??'',displayName:this.profile!.displayName,body,createdAt:new Date().toISOString()};this.comments.unshift(comment);this.clips=this.clips.map(clip=>clip.id===id?{...clip,commentCount:clip.commentCount+1}:clip);return {...comment};}
  async deleteComment(id:string,signal?:AbortSignal){this.check(signal);const comment=this.comments.find(item=>item.id===id);if(comment?.ownerId!==me)throw new Error('Only your own comment can be removed.');this.comments=this.comments.filter(item=>item.id!==id);this.clips=this.clips.map(clip=>clip.id===comment.clipId?{...clip,commentCount:Math.max(0,clip.commentCount-1)}:clip);}
  async reportClip(id:string,reason:string,signal?:AbortSignal){this.check(signal);this.reports.push({id,reason});}
  async getBlockedUsers(signal?:AbortSignal):Promise<CommunityBlockedUser[]>{this.check(signal);return [...this.blocked].map(userId=>{const clip=initialClips.find(item=>item.ownerId===userId)!;return{userId,username:clip.username,displayName:clip.displayName};});}
  async setBlocked(id:string,blocked:boolean,signal?:AbortSignal){this.check(signal);if(blocked)this.blocked.add(id);else this.blocked.delete(id);}
  async deleteClip(id:string,signal?:AbortSignal){const clip=await this.getClip(id,signal);if(clip.ownerId!==me)throw new Error('Only your own clip can be removed.');this.clips=this.clips.filter(item=>item.id!==id);this.comments=this.comments.filter(item=>item.clipId!==id);}
  async getPlaybackUrl(id:string,signal?:AbortSignal){await this.getClip(id,signal);this.playbackRequests.push(id);return videoUrl;}
  async getPosterUrl(id:string,signal?:AbortSignal){await this.getClip(id,signal);return this.defaultPoster;}
  async getOperationsCapabilities(signal?:AbortSignal):Promise<CommunityOperationsCapabilities>{this.check(signal);return structuredClone(this.capabilities);}
  async getUsage(signal?:AbortSignal):Promise<CommunityUsage>{this.check(signal);const mine=this.clips.filter(clip=>clip.ownerId===me);return{me:{slotsUsed:mine.length,slotsLimit:5,published:mine.length,uploading:0,deleting:0,storedBytes:mine.reduce((sum,clip)=>sum+clip.byteSize,0)},beta:{slotsUsed:this.clips.length,slotsLimit:40,reservedBytes:0,storageBytes:this.clips.reduce((sum,clip)=>sum+clip.byteSize,0),storageLimitBytes:1024**3,unknownStorageObjects:0},limits:{maxVideoBytes:16*1024**2,maxDurationSeconds:60,warningPercent:80},operator:false,checkedAt:new Date().toISOString(),operations:null};}
  private support:CommunitySupportRequest[]=[];
  async getSupportRequests(signal?:AbortSignal){this.check(signal);return structuredClone(this.support);}
  async submitSupport(category:SupportCategory,body:string,signal?:AbortSignal){this.check(signal);const request:CommunitySupportRequest={id:crypto.randomUUID(),category,body,status:'open',createdAt:new Date().toISOString(),updatedAt:new Date().toISOString(),reply:''};this.support.unshift(request);return {...request};}
  async getOperatorReports(_cursor:CommunityCursor|null=null,signal?:AbortSignal):Promise<CommunityPage<CommunityReport>>{this.check(signal);throw new Error('Only Klipp moderators can open this section.');}
  async reviewClip(_id:string,_action:'hide'|'keep',_note:string,signal?:AbortSignal):Promise<void>{this.check(signal);throw new Error('Only Klipp moderators can open this section.');}
  async getOperatorSupport(_cursor:CommunityCursor|null=null,signal?:AbortSignal):Promise<CommunityPage<CommunitySupportRequest>>{this.check(signal);throw new Error('Only Klipp moderators can open this section.');}
  async replySupport(_id:string,_reply:string,_status:'open'|'resolved',signal?:AbortSignal):Promise<CommunitySupportRequest>{this.check(signal);throw new Error('Only Klipp moderators can open this section.');}
  async deleteAccount(confirmation:string,signal?:AbortSignal){this.check(signal);if(confirmation!==(this.profile?.username??'DELETE'))throw new Error('Type your username to confirm.');this.accountDeletionCalls++;if(this.pendingDeletionOnce){this.pendingDeletionOnce=false;throw new CommunityError('deletion_pending','Account cleanup is in progress. Please retry account deletion to finish.');}this.clips=this.clips.filter(clip=>clip.ownerId!==me);this.comments=this.comments.filter(comment=>comment.ownerId!==me);await this.signOut(signal);}
  snapshot(){return structuredClone({clips:this.clips,comments:this.comments,blocked:[...this.blocked],reports:this.reports,profile:this.profile,playbackRequests:this.playbackRequests});}
}

const canvas=document.createElement('canvas');canvas.width=480;canvas.height=270;
const context=canvas.getContext('2d')!;context.fillStyle='#171d28';context.fillRect(0,0,480,270);context.fillStyle='#7198c7';context.fillRect(36,40,260,12);context.fillStyle='#e6e8eb';context.font='bold 30px sans-serif';context.fillText('Klipp local test',36,130);context.fillStyle='#a7adb5';context.font='18px sans-serif';context.fillText('Synthetic video · no personal media',36,170);
const poster=await new Promise<Blob>(resolve=>canvas.toBlob(blob=>resolve(blob!),'image/jpeg',0.8));
const api=new FixtureApi(URL.createObjectURL(poster),new URLSearchParams(location.search).get('empty')==='1');
declare global{interface Window{__klippQA:{api:FixtureApi;snapshot:()=>ReturnType<FixtureApi['snapshot']>};__klippQASetup?:(api:FixtureApi)=>void}}
window.__klippQA={api,snapshot:()=>api.snapshot()};
window.__klippQASetup?.(api);
createRoot(document.getElementById('root')!).render(<App api={api} fixture operationsEnabled={new URLSearchParams(location.search).get('operations')==='1'}/>);
