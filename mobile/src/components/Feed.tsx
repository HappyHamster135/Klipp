import {lazy,Suspense,useEffect,useRef,useState,type KeyboardEvent} from 'react';
import {ArrowLeft,Play,RefreshCw,Trash2,Flag,UserRoundX} from 'lucide-react';
import type {Api} from '../lib/service';
import {messageFor,timeLabel} from '../lib/service';
import {CommunityError,type CommunityClip,type CommunityCursor} from '../lib/models';
import {Dialog,Empty,Notice} from './Primitives';
import {ClipActions} from './ClipActions';

const Watch=lazy(()=>import('./Watch').then(module=>({default:module.Watch})));
const Reels=lazy(()=>import('./Reels').then(module=>({default:module.Reels})));
const phoneQuery='(max-width:700px), (pointer:coarse) and (max-height:600px)';

function Poster({api,clip,generation}:{api:Api;clip:CommunityClip;generation:string}) {
  const ref=useRef<HTMLDivElement>(null),failure=useRef<(url:string)=>void>(()=>{});
  const [preview,setPreview]=useState({url:'',failed:false});
  useEffect(()=>{
    const abort=new AbortController();let requested=false,visible=false,failed=false,pending=false,url='';
    setPreview({url:'',failed:false});
    if(clip.moderationHidden)return()=>abort.abort();
    const load=async()=>{
      if(pending||abort.signal.aborted)return;
      pending=true;requested=true;failed=false;
      try{
        const next=await api.getPosterUrl(clip.id,abort.signal);
        if(!abort.signal.aborted){url=next;setPreview({url:next,failed:false});}
      }catch{if(!abort.signal.aborted){failed=true;setPreview({url:'',failed:true});}}
      finally{pending=false;}
    };
    failure.current=source=>{if(!abort.signal.aborted&&source===url){failed=true;setPreview({url:'',failed:true});}};
    const observer=new IntersectionObserver(entries=>{
      visible=entries.some(entry=>entry.isIntersecting);
      if(visible&&!requested)void load();
    },{rootMargin:'80px'});
    const online=()=>{if(visible&&failed)void load();};
    if(ref.current)observer.observe(ref.current);
    window.addEventListener('online',online);
    return()=>{abort.abort();observer.disconnect();window.removeEventListener('online',online);};
  },[api,clip.id,clip.moderationHidden,generation]);
  return <div ref={ref} className="poster">
    {preview.url?<img src={preview.url} alt="" loading="lazy" onError={event=>failure.current(event.currentTarget.getAttribute('src')??'')}/>:null}
    {clip.moderationHidden?<span className="poster-fallback">Hidden from Media</span>:<span className="play-circle"><Play fill="currentColor" size={24}/></span>}
    {preview.failed?<span className="poster-fallback">Preview unavailable</span>:null}
    <span className="duration">{timeLabel(clip.durationSeconds)}</span>
  </div>;
}

type FeedState={api:Api;mine:boolean;cursor:CommunityCursor|null;items:CommunityClip[];next:CommunityCursor|null;loading:boolean;loaded:boolean;error:string};
type FeedLifetime={api:Api;actor:string|null;active:boolean};
export function Feed({api,version,watchId,onWatchChange,layout='auto'}:{api:Api;version:number;watchId:string|null;onWatchChange:(id:string|null,replace?:boolean)=>void;layout?:'auto'|'browse'}){
  const [phoneViewport,setPhoneViewport]=useState(()=>window.matchMedia(phoneQuery).matches);
  const phone=layout!=='browse'&&phoneViewport;
  const [reelCommentsId,setReelCommentsId]=useState<string|null>(null);
  const [mine,setMine]=useState(false),[history,setHistory]=useState<(CommunityCursor|null)[]>([null]),[page,setPage]=useState(0),[refresh,setRefresh]=useState(0);
  const [notice,setNotice]=useState(''),[selected,setSelected]=useState<CommunityClip|null>(null);
  const [action,setAction]=useState<{clip:CommunityClip;type:'menu'|'delete'|'report'|'block'}|null>(null),[reason,setReason]=useState(''),[busy,setBusy]=useState(false);
  const [actionError,setActionError]=useState('');
  const [watchLoad,setWatchLoad]=useState<{api:Api;id:string|null;error:string}>({api,id:null,error:''}),[watchRetry,setWatchRetry]=useState(0);
  const [feed,setFeed]=useState<FeedState>(()=>({api,mine:false,cursor:null,items:[],next:null,loading:true,loaded:false,error:''}));
  const communityTab=useRef<HTMLButtonElement>(null),ownTab=useRef<HTMLButtonElement>(null),actionLock=useRef(false),dialogRequest=useRef(false);
  const mutations=useRef({epoch:0,clips:new Map<string,{epoch:number;clip:CommunityClip|null}>(),blocked:new Map<string,number>()});
  const lifetime=useRef<FeedLifetime>({api,actor:api.user?.id??null,active:false});
  const cursor=history[page]??null;
  const sameScope=feed.api===api&&feed.mine===mine&&feed.cursor===cursor;
  const items=sameScope?feed.items:[],next=sameScope?feed.next:null,loading=!sameScope||feed.loading;
  const error=sameScope?feed.error:'',loaded=sameScope&&feed.loaded;
  const watching=selected?.id===watchId?selected:phone?items.find(clip=>clip.id===watchId)??null:null;
  const reelItems=watching&&!items.some(clip=>clip.id===watching.id)?[watching,...items]:items;
  const reelComments=reelItems.find(clip=>clip.id===reelCommentsId)??null;
  const browseScroll=useRef(0),wasWatching=useRef(false),browseOpener=useRef<HTMLButtonElement|null>(null),clickedControl=useRef<HTMLButtonElement|null>(null);
  const browse=useRef<HTMLElement>(null),feedPanel=useRef<HTMLDivElement>(null),pageVisit=useRef({api,mine,page}),pageFocus=useRef(false);
  const watchError=watchLoad.api===api&&watchLoad.id===watchId?watchLoad.error:'';
  useEffect(()=>{const media=window.matchMedia(phoneQuery),changed=()=>setPhoneViewport(media.matches);media.addEventListener('change',changed);return()=>media.removeEventListener('change',changed);},[]);
  useEffect(()=>{if(!phone)setReelCommentsId(null);},[phone]);
  useEffect(()=>{
    const scope:FeedLifetime={api,actor:api.user?.id??null,active:true};lifetime.current=scope;
    mutations.current={epoch:0,clips:new Map(),blocked:new Map()};
    return()=>{scope.active=false;};
  },[api]);
  const current=(scope:FeedLifetime)=>scope.active&&lifetime.current===scope&&(api.user?.id??null)===scope.actor;
  useEffect(()=>{
    if(!watchId){setSelected(null);return;}
    if(selected?.id===watchId)return;
    const existing=items.find(clip=>clip.id===watchId);
    if(phone&&existing){setSelected(existing);return;}
    const abort=new AbortController();
    setWatchLoad({api,id:watchId,error:''});
    void api.getClip(watchId,abort.signal).then(clip=>{if(clip.moderationHidden)throw new CommunityError('clip_unavailable','This clip is hidden from Media.');if(!abort.signal.aborted)setSelected(clip);})
      .catch(error=>{if(!abort.signal.aborted)setWatchLoad({api,id:watchId,error:messageFor(error)});});
    return()=>abort.abort();
  },[api,watchId,watchRetry,phone]);
  useEffect(()=>{
    if(phone)return;
    if(watchId){wasWatching.current=true;return;}
    if(!wasWatching.current)return;
    wasWatching.current=false;
    const frame=requestAnimationFrame(()=>{
      const opener=browseOpener.current;
      (opener?.isConnected&&!opener.disabled?opener:(mine?ownTab:communityTab).current)?.focus({preventScroll:true});
      window.scrollTo({top:browseScroll.current});
    });
    return()=>cancelAnimationFrame(frame);
  },[watchId,phone]);
  useEffect(()=>{
    const previous=pageVisit.current;
    if(previous.api!==api||previous.mine!==mine)pageFocus.current=false;
    else if(previous.page!==page)pageFocus.current=true;
    pageVisit.current={api,mine,page};
    if(phone){pageFocus.current=false;return;}
    if(!pageFocus.current||loading)return;
    pageFocus.current=false;
    const frame=requestAnimationFrame(()=>{
      feedPanel.current?.focus({preventScroll:true});
      browse.current?.scrollIntoView({block:'start'});
    });
    return()=>cancelAnimationFrame(frame);
  },[api,mine,page,loading,phone]);
  useEffect(()=>{
    const abort=new AbortController(),epoch=mutations.current.epoch;
    setFeed(previous=>previous.api===api&&previous.mine===mine&&previous.cursor===cursor
      ?{...previous,loading:true,error:''}
      :{api,mine,cursor,items:[],next:null,loading:true,loaded:false,error:''});
    void api.getFeed(cursor,mine,abort.signal).then(result=>{
      if(abort.signal.aborted)return;
      // An older refresh must not undo a like, comment count, deletion or block.
      const items=result.items.flatMap(clip=>{
        if((mutations.current.blocked.get(clip.ownerId)??0)>epoch)return [];
        const changed=mutations.current.clips.get(clip.id);
        return changed&&changed.epoch>epoch?(changed.clip?[changed.clip]:[]):[clip];
      });
      setFeed({api,mine,cursor,items,next:result.nextCursor,loading:false,loaded:true,error:''});
    }).catch(error=>{
      if(!abort.signal.aborted)setFeed(previous=>({...previous,loading:false,error:messageFor(error)}));
    });
    return()=>abort.abort();
  },[api,mine,cursor,refresh,version]);
  function patchClip(id:string,clip:CommunityClip|null){
    mutations.current.clips.set(id,{epoch:++mutations.current.epoch,clip});
    setFeed(previous=>({...previous,items:previous.items.flatMap(item=>item.id===id?(clip?[clip]:[]):[item])}));
  }
  function updateClip(clip:CommunityClip){patchClip(clip.id,clip);setSelected(current=>current?.id===clip.id?clip:current);}
  async function like(clip:CommunityClip){
    const scope=lifetime.current,epoch=mutations.current.epoch;
    if(actionLock.current||!current(scope))return;actionLock.current=true;setBusy(true);
    try{
      const changed=await api.setLiked(clip.id,!clip.likedByMe);
      if(!current(scope))return;
      const newer=mutations.current.clips.get(clip.id);
      if(newer&&newer.epoch>epoch){
        if(newer.clip)updateClip({...newer.clip,likedByMe:changed.likedByMe,likeCount:changed.likeCount});
      }else updateClip(changed);
    }catch(error){if(current(scope))setNotice(messageFor(error));}
    finally{if(current(scope)){actionLock.current=false;setBusy(false);}}
  }
  function select(clip:CommunityClip){if(clip.status!=='published'||clip.moderationHidden)return;if(!watchId){browseScroll.current=window.scrollY;browseOpener.current=clickedControl.current;}setSelected(clip);onWatchChange(clip.id);window.scrollTo({top:0});}
  function switchTab(value:boolean){if(mine===value)return;if(phone){setReelCommentsId(null);onWatchChange(null,true);}setMine(value);setHistory([null]);setPage(0);}
  function activeReel(id:string){if(!reelItems.some(item=>item.id===id)||id===watchId)return;onWatchChange(id,true);}
  function nextPage(){if(!next)return;setHistory(list=>[...list.slice(0,page+1),next]);setPage(page+1);if(phone)onWatchChange(null,true);}
  function openActions(clip:CommunityClip){setActionError('');setReason('');setAction({clip,type:'menu'});}
  function closeActions(){if(!dialogRequest.current){setAction(null);setReason('');setActionError('');}}
  function tabKey(event:KeyboardEvent<HTMLButtonElement>){
    if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
    event.preventDefault();const own=event.key==='Home'?false:event.key==='End'?true:!mine;
    switchTab(own);(own?ownTab:communityTab).current?.focus();
  }
  async function applyAction(){
    const scope=lifetime.current;
    if(!action||actionLock.current||!current(scope))return;actionLock.current=true;dialogRequest.current=true;setBusy(true);setActionError('');
    try{
      const {clip,type}=action;
      if(type==='delete'){
        await api.deleteClip(clip.id);if(!current(scope))return;patchClip(clip.id,null);
        if(selected?.id===clip.id){setSelected(null);onWatchChange(null);}
        setNotice('Shared clip removed. Your original is unchanged.');
      }else if(type==='report'){
        await api.reportClip(clip.id,reason);if(!current(scope))return;setNotice('Report sent for review.');
      }else if(type==='block'){
        await api.setBlocked(clip.ownerId,true);
        if(!current(scope))return;
        mutations.current.blocked.set(clip.ownerId,++mutations.current.epoch);
        setFeed(previous=>({...previous,items:previous.items.filter(item=>item.ownerId!==clip.ownerId)}));
        if(selected?.ownerId===clip.ownerId){setSelected(null);onWatchChange(null);}
        setNotice('User blocked. You can unblock them from your profile.');
      }
      setAction(null);setReason('');
    }catch(error){if(current(scope))setActionError(messageFor(error));}
    finally{if(current(scope)){actionLock.current=false;dialogRequest.current=false;setBusy(false);}}
  }
  return <main className={phone?'media-page reels-page':watching?'media-page viewing':'media-page'}>
    <div className="page-title"><div><h1>Media</h1><p className="caption">Upload from Klipp on PC. Watch and connect here.</p></div></div>
    {notice?<Notice onClose={()=>setNotice('')}>{notice}</Notice>:null}
    {!phone&&watching?<Suspense fallback={<div className="video-loading" role="status">Opening clip…</div>}><Watch key={watching.id} api={api} clip={watching} onBack={()=>onWatchChange(null)} onChanged={updateClip} onLike={()=>void like(watching)} onMore={()=>openActions(watching)} busy={busy}/></Suspense>:watchId&&!watching?
      <section className="watch"><button className="back" onClick={()=>onWatchChange(null)}><ArrowLeft size={18}/>Back to clips</button><div className="watch-player">{watchError?<div className="playback-error" role="alert"><h2>Clip could not open</h2><p>{watchError}</p><button onClick={()=>setWatchRetry(value=>value+1)}>Retry clip</button></div>:<div className="video-loading" role="status">Opening clip...</div>}</div></section>:null}
    <section ref={browse} className="browse" aria-label="Browse clips">
      <div className="feed-toolbar">
        <div className="tabs" role="tablist" aria-label="Clip collection">
          <button ref={communityTab} id="community-clips-tab" role="tab" aria-controls="clip-feed" aria-selected={!mine} tabIndex={mine?-1:0} onKeyDown={tabKey} onClick={()=>switchTab(false)}>Community</button>
          <button ref={ownTab} id="my-clips-tab" role="tab" aria-controls="clip-feed" aria-selected={mine} tabIndex={mine?0:-1} onKeyDown={tabKey} onClick={()=>switchTab(true)}>My uploads</button>
        </div>
        <button className="icon-button" aria-label="Refresh clips" disabled={loading} onClick={()=>setRefresh(value=>value+1)}><RefreshCw size={18}/></button>
      </div>
      <div ref={feedPanel} id="clip-feed" role="tabpanel" tabIndex={-1} aria-labelledby={mine?'my-clips-tab':'community-clips-tab'} aria-busy={loading}>
        {loading?<p className="caption" role="status">Loading clips...</p>:null}
        {error?<div className="feed-error" role="alert"><div><h2>{loaded?'Could not refresh clips':'Could not load clips'}</h2><p>{error}{loaded&&items.length?' Your current clips are still shown.':''}</p></div><button onClick={()=>setRefresh(value=>value+1)}>Retry clips</button></div>:null}
        {!loading&&loaded&&!error&&!items.length?<Empty title={mine?'No uploads yet':'No clips shared yet'}><p>Upload clips from Klipp on your PC.</p></Empty>:null}
        {phone?(reelItems.length&&loaded&&(!watchId||watching)?<Suspense fallback={<div className="loading" role="status">Opening video feed…</div>}><Reels key={`${mine}:${page}`} api={api} items={reelItems} initialId={watchId} suspended={!!action||!!reelCommentsId} busy={busy} page={page} onLike={clip=>void like(clip)} onComments={clip=>setReelCommentsId(clip.id)} onMore={openActions} onActiveChange={activeReel} onNext={next&&!loading?nextPage:undefined} onPrevious={page>0&&!loading?()=>{setPage(page-1);onWatchChange(null,true);}:undefined}/></Suspense>:null):<div className="feed-grid">{items.map(clip=><article className={watching?.id===clip.id?'clip-card selected':'clip-card'} key={clip.id} onClickCapture={event=>{const target=(event.target as Element).closest('button');if(target)clickedControl.current=target;}}>
          <button className="poster-button" aria-label={`Watch ${clip.title}`} disabled={clip.status!=='published'||clip.moderationHidden} onClick={()=>select(clip)}><Poster api={api} clip={clip} generation={`${version}:${refresh}`}/></button>
          <div className="clip-meta"><button className="clip-title" onClick={()=>select(clip)} disabled={clip.status!=='published'||clip.moderationHidden}>{clip.title}</button><p className="author">@{clip.username||clip.displayName}<time dateTime={clip.createdAt}>{new Date(clip.createdAt).toLocaleDateString(undefined,{month:'short',day:'numeric'})}</time></p>
            {clip.moderationHidden?<p className="caption">Hidden by moderation. You can still delete your shared clip.</p>:null}
            {clip.status!=='published'?<p className="caption">{clip.status==='deleting'?'Deletion incomplete. Use Delete again to finish cleanup.':'Upload incomplete. Delete it to free this slot.'}</p>:null}
            <ClipActions clip={clip} onLike={()=>void like(clip)} onComments={()=>select(clip)} onMore={()=>openActions(clip)} busy={busy}/>
          </div>
        </article>)}</div>}
        {!phone&&(items.length||page>0)?<div className="pagination"><button disabled={loading||page===0} onClick={()=>setPage(page-1)}>Newer clips</button><span className="caption">Page {page+1}</span><button disabled={loading||!next} onClick={nextPage}>Older clips</button></div>:null}
      </div>
    </section>
    {phone&&reelComments?<Dialog title="Comments" onClose={()=>setReelCommentsId(null)}><Suspense fallback={<p role="status">Loading comments…</p>}><Watch key={reelComments.id} presentation="comments" api={api} clip={reelComments} onBack={()=>setReelCommentsId(null)} onLike={()=>void like(reelComments)} onMore={()=>openActions(reelComments)} onChanged={updateClip} busy={busy}/></Suspense></Dialog>:null}
{action?<Dialog title={action.type==='menu'?'Clip actions':action.type==='delete'?'Delete shared clip?':action.type==='block'?'Block this user?':'Report clip'} onClose={closeActions}>{actionError?<Notice>{actionError}</Notice>:null}{action.type==='menu'?<div className="action-menu">{action.clip.ownerId===api.user?.id?<button onClick={()=>setAction({...action,type:'delete'})}><Trash2 size={18}/>Delete shared clip</button>:<><button onClick={()=>setAction({...action,type:'report'})}><Flag size={18}/>Report clip</button><button onClick={()=>setAction({...action,type:'block'})}><UserRoundX size={18}/>Block @{action.clip.username}</button></>}</div>:<><p>{action.type==='delete'?'This removes the shared video and its comments. Your original recording stays on your device.':action.type==='block'?'You won’t see each other’s content. You can unblock this user in your profile.':'Tell us why this clip should be reviewed.'}</p>{action.type==='report'?<label>Reason<textarea disabled={dialogRequest.current} value={reason} onChange={event=>setReason(event.target.value)} maxLength={500} rows={4}/></label>:null}<div className="dialog-actions"><button disabled={dialogRequest.current} onClick={closeActions}>Cancel</button><button className={action.type==='delete'?'danger':'primary'} disabled={busy||(action.type==='report'&&!reason.trim())} onClick={()=>void applyAction()}>{dialogRequest.current?'Please wait…':action.type==='delete'?'Delete shared clip':action.type==='block'?'Block user':'Send report'}</button></div></>}</Dialog>:null}</main>;
}
