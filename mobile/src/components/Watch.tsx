import {useEffect,useRef,useState,type FormEvent} from 'react';
import {ArrowLeft,Trash2,Maximize,Share2,Play} from 'lucide-react';
import type {Api} from '../lib/service';
import {messageFor} from '../lib/service';
import type {CommunityClip,CommunityComment,CommunityCursor} from '../lib/models';
import {Dialog,Notice} from './Primitives';
import {ClipActions} from './ClipActions';
import {enterVideoFullscreen,rememberPlayback,restorePlayback,type PlaybackPosition} from '../lib/playback';
import {mergeCommentPages,unchangedInteractions} from '../lib/comments';
import {shareClipLink} from '../lib/share';

interface Props{api:Api;clip:CommunityClip;onBack:()=>void;onLike:()=>void;onMore:()=>void;onChanged:(clip:CommunityClip)=>void;busy:boolean}
interface CommentLoadError{message:string;older:boolean}
export function Watch({api,clip,onBack,onLike,onMore,onChanged,busy}:Props){
  const video=useRef<HTMLVideoElement>(null),playerFrame=useRef<HTMLDivElement>(null),resume=useRef<PlaybackPosition|null>(null),commentInput=useRef<HTMLTextAreaElement>(null),shareButton=useRef<HTMLButtonElement>(null);
  const [source,setSource]=useState({url:'',request:0}),[playError,setPlayError]=useState(''),[fullscreenNotice,setFullscreenNotice]=useState(''),[retry,setRetry]=useState(0);
  const [needsPlay,setNeedsPlay]=useState(false),[buffering,setBuffering]=useState<''|'waiting'|'stalled'>('');
  const initialPlay=useRef(true),bufferTimers=useRef<ReturnType<typeof setTimeout>[]>([]);
  const [comments,setComments]=useState<CommunityComment[]>([]),[cursor,setCursor]=useState<CommunityCursor|null>(null),[loading,setLoading]=useState(true),[loaded,setLoaded]=useState(false),[commentError,setCommentError]=useState<CommentLoadError|null>(null);
  const [body,setBody]=useState(''),[actionError,setActionError]=useState(''),[posting,setPosting]=useState(false),[deleteComment,setDeleteComment]=useState<string|null>(null);
  const [syncing,setSyncing]=useState(false),[syncNotice,setSyncNotice]=useState(''),[shareNotice,setShareNotice]=useState(''),[manualLink,setManualLink]=useState(''),[sharing,setSharing]=useState(false);
  const active=useRef(false),generation=useRef(0),pending=useRef(new Set<AbortController>()),readLock=useRef(false),mutationLock=useRef(false),shareLock=useRef(false),cursorRef=useRef<CommunityCursor|null>(null),removed=useRef(new Set<string>());
  const detailRequest=useRef<AbortController|null>(null),syncContext=useRef('Clip details saved.'),latestClip=useRef(clip),resumeAttempts=useRef(0);
  latestClip.current=clip;
  const viewerId=api.user?.id??null,url=source.request===retry?source.url:'';
  const current=(epoch:number)=>active.current&&generation.current===epoch&&api.user?.id===viewerId;
  function request(){const abort=new AbortController();pending.current.add(abort);return abort;}

  useEffect(()=>{
    active.current=true;generation.current++;readLock.current=false;mutationLock.current=false;shareLock.current=false;removed.current.clear();cursorRef.current=null;
    setComments([]);setCursor(null);setLoaded(false);setBody('');setActionError('');setDeleteComment(null);setPosting(false);setSyncing(false);setSyncNotice('');setShareNotice('');setManualLink('');setSharing(false);
    void loadComments(false);
    return()=>{active.current=false;generation.current++;for(const abort of pending.current)abort.abort();pending.current.clear();};
  },[api,clip.id,viewerId]);
  async function loadComments(older:boolean){
    if(readLock.current||(!cursorRef.current&&older))return;
    const epoch=generation.current,abort=request(),before=older?cursorRef.current:null;
    readLock.current=true;setLoading(true);setCommentError(null);
    try{
      const page=await api.getComments(clip.id,before,abort.signal);
      if(!current(epoch)||abort.signal.aborted)return;
      setComments(list=>mergeCommentPages(list,page.items,removed.current));cursorRef.current=page.nextCursor;setCursor(page.nextCursor);setLoaded(true);
    }catch(error){if(current(epoch)&&!abort.signal.aborted)setCommentError({message:messageFor(error),older});}
    finally{pending.current.delete(abort);if(current(epoch)){readLock.current=false;setLoading(false);}}
  }
  function adjustCount(delta:number){
    const changed={...latestClip.current,commentCount:Math.max(0,latestClip.current.commentCount+delta)};
    latestClip.current=changed;onChanged(changed);
  }
  async function refreshDetails(context:string){
    detailRequest.current?.abort();const epoch=generation.current,abort=request(),snapshot=latestClip.current;detailRequest.current=abort;syncContext.current=context;
    setSyncing(true);setSyncNotice('');
    try{
      const fresh=await api.getClip(clip.id,abort.signal);
      if(!current(epoch)||abort.signal.aborted||detailRequest.current!==abort||!unchangedInteractions(snapshot,latestClip.current))return;
      latestClip.current=fresh;onChanged(fresh);
    }catch(error){
      if(current(epoch)&&!abort.signal.aborted&&detailRequest.current===abort)setSyncNotice(`${context} The clip details could not refresh. ${messageFor(error)}`);
    }finally{pending.current.delete(abort);if(current(epoch)&&detailRequest.current===abort)setSyncing(false);}
  }
  async function post(event:FormEvent){
    event.preventDefault();const text=body.trim();if(mutationLock.current||!text||text.length>500)return;
    const epoch=generation.current,abort=request();mutationLock.current=true;setPosting(true);setActionError('');
    try{
      const added=await api.addComment(clip.id,text,abort.signal);
      if(!current(epoch)||abort.signal.aborted)return;
      setComments(list=>mergeCommentPages(list,[added],removed.current));setBody('');adjustCount(1);void refreshDetails('Comment posted.');
    }catch(error){if(current(epoch)&&!abort.signal.aborted)setActionError(messageFor(error));}
    finally{pending.current.delete(abort);if(current(epoch)){mutationLock.current=false;setPosting(false);}}
  }
  async function removeComment(){
    if(mutationLock.current||!deleteComment)return;
    const id=deleteComment,epoch=generation.current,abort=request();mutationLock.current=true;setPosting(true);setActionError('');
    try{
      await api.deleteComment(id,abort.signal);
      if(!current(epoch)||abort.signal.aborted)return;
      removed.current.add(id);setComments(list=>list.filter(comment=>comment.id!==id));setDeleteComment(null);adjustCount(-1);void refreshDetails('Comment deleted.');
    }catch(error){if(current(epoch)&&!abort.signal.aborted)setActionError(messageFor(error));}
    finally{pending.current.delete(abort);if(current(epoch)){mutationLock.current=false;setPosting(false);}}
  }
  function closeDelete(){if(!mutationLock.current){setDeleteComment(null);setActionError('');}}
  function closeShare(){
    const epoch=generation.current;setManualLink('');
    // WebKit does not focus a button on pointer activation; restore this dialog's explicit opener.
    requestAnimationFrame(()=>{if(current(epoch)&&shareButton.current?.isConnected)shareButton.current.focus({preventScroll:true});});
  }
  async function share(){
    if(shareLock.current)return;const epoch=generation.current;shareLock.current=true;setSharing(true);setShareNotice('');
    try{
      // Invoke the native sheet directly from this button gesture; never share a signed media URL.
      const result=await shareClipLink(clip.id,clip.title,window.location.href,{
        share:typeof navigator.share==='function'?data=>navigator.share(data):undefined,
        copy:async text=>{if(!navigator.clipboard?.writeText)throw new Error('Clipboard unavailable');await navigator.clipboard.writeText(text);},
      });
      if(!current(epoch))return;
      if(result.status==='copied')setShareNotice('Link copied. Sign in to Klipp to watch.');
      else if(result.status==='manual')setManualLink(result.url);
    }catch{if(current(epoch))setShareNotice('The clip link could not be prepared. Please try again.');}
    finally{if(current(epoch)){shareLock.current=false;setSharing(false);}}
  }

  // Playback behavior stays independent of comment and sharing requests.
  useEffect(()=>{
    let width=window.innerWidth,frame=0;
    const reveal=()=>{
      cancelAnimationFrame(frame);frame=requestAnimationFrame(()=>{
        const player=playerFrame.current;if(!player)return;
        const bounds=player.getBoundingClientRect(),nav=document.querySelector('.bottom-nav')?.getBoundingClientRect();
        const bottom=(nav?.top??window.innerHeight)-12;
        const landscape=window.innerWidth>window.innerHeight&&window.innerHeight<=600;
        const offset=landscape?bounds.top-12:Math.max(0,Math.min(bounds.top-12,bounds.bottom-bottom));
        if(offset)window.scrollTo({top:Math.max(0,window.scrollY+offset)});
      });
    };
    const resize=()=>{if(window.innerWidth!==width){width=window.innerWidth;reveal();}};
    reveal();window.addEventListener('resize',resize);return()=>{cancelAnimationFrame(frame);window.removeEventListener('resize',resize);};
  },[]);
  useEffect(()=>{const abort=new AbortController();initialPlay.current=retry===0;setPlayError('');setNeedsPlay(false);stopBuffering();void api.getPlaybackUrl(clip.id,abort.signal).then(url=>{if(!abort.signal.aborted)setSource({url,request:retry});}).catch(error=>{if(!abort.signal.aborted)setPlayError(messageFor(error));});return()=>abort.abort();},[api,clip.id,retry]);
  useEffect(()=>{
    const element=video.current;
    const escape=(event:KeyboardEvent)=>{if(event.key==='Escape'&&document.fullscreenElement===element)void document.exitFullscreen().catch(()=>{});};
    const pauseInBackground=()=>{
      const wanted=initialPlay.current||resume.current?.playing||Boolean(element&&!element.paused&&!element.ended);
      initialPlay.current=false;if(resume.current)resume.current.playing=false;
      if(wanted)setNeedsPlay(true);stopBuffering();element?.pause();
    };
    const visibility=()=>{if(document.hidden)pauseInBackground();};
    document.addEventListener('keydown',escape);document.addEventListener('visibilitychange',visibility);window.addEventListener('pagehide',pauseInBackground);
    return()=>{document.removeEventListener('keydown',escape);document.removeEventListener('visibilitychange',visibility);window.removeEventListener('pagehide',pauseInBackground);clearBufferTimers();if(element){element.pause();element.removeAttribute('src');element.load();}};
  },[url]);
  function clearBufferTimers(){for(const timer of bufferTimers.current)clearTimeout(timer);bufferTimers.current=[];}
  function stopBuffering(){clearBufferTimers();setBuffering('');}
  function waitingPlayback(element:HTMLVideoElement){
    if(element.paused||element.ended||bufferTimers.current.length)return;
    const waiting=()=>video.current===element&&!element.paused&&!element.ended;
    // Keep brief seeks quiet and leave the native transport uncovered.
    bufferTimers.current=[setTimeout(()=>{if(waiting())setBuffering('waiting');},700),setTimeout(()=>{if(waiting())setBuffering('stalled');},8000)];
  }
  function playVideo(element:HTMLVideoElement){
    setNeedsPlay(false);
    void element.play().catch(error=>{
      if(video.current!==element||!element.isConnected||document.hidden||error?.name==='AbortError')return;
      stopBuffering();if(element.error)failedPlayback();else setNeedsPlay(true);
    });
  }
  function readyPlayback(element:HTMLVideoElement){
    stopBuffering();
    if(resume.current){resumePlayback(element);return;}
    if(initialPlay.current){initialPlay.current=false;if(document.hidden)setNeedsPlay(true);else playVideo(element);}
  }
  function failedPlayback(){stopBuffering();setNeedsPlay(false);if(video.current&&!resume.current)resume.current=rememberPlayback(video.current);setPlayError('This clip could not play. Retry to get a fresh playback link.');}
  function retryPlayback(){if(!resume.current)resume.current=video.current?rememberPlayback(video.current):{time:0,playing:!document.hidden,muted:false,volume:1,rate:1};resumeAttempts.current=0;setPlayError('');setRetry(value=>value+1);}
  function finishResume(element:HTMLVideoElement){
    const saved=resume.current;if(!saved||element.seeking)return;
    const target=Math.max(0,Math.min(saved.time,Math.max(0,element.duration-0.01)));
    if(Math.abs(element.currentTime-target)>0.08){if(resumeAttempts.current<3)resumePlayback(element);else setPlayError('The playback position could not be restored. Retry to reopen this section.');return;}
    resume.current=null;if(saved.playing&&!document.hidden)playVideo(element);
  }
  function resumePlayback(element:HTMLVideoElement){if(resume.current&&resumeAttempts.current<3&&restorePlayback(element,resume.current)){resumeAttempts.current++;if(!element.seeking)finishResume(element);}}
  async function fullscreen(){const target=video.current;if(!target)return;setFullscreenNotice('');try{await enterVideoFullscreen(target);}catch{setFullscreenNotice('Fullscreen is unavailable here. You can keep watching below or use the video’s own fullscreen control.');}}
  return <section className="watch">
    <button className="back" onClick={onBack}><ArrowLeft size={18}/>Back to clips</button>
    <div className="watch-grid"><div className="watch-main">
      <div className="watch-player" ref={playerFrame}>{url?<video key={`${retry}:${url}`} ref={video} src={url} aria-label={`Video: ${clip.title}`} controls playsInline preload={retry===0?"metadata":"auto"} onLoadedMetadata={event=>readyPlayback(event.currentTarget)} onCanPlay={event=>readyPlayback(event.currentTarget)} onSeeked={event=>finishResume(event.currentTarget)} onWaiting={event=>waitingPlayback(event.currentTarget)} onSeeking={event=>waitingPlayback(event.currentTarget)} onPlay={()=>setNeedsPlay(false)} onPlaying={()=>{setNeedsPlay(false);stopBuffering();}} onPause={stopBuffering} onEnded={stopBuffering} onError={failedPlayback} onDoubleClick={()=>void fullscreen()}/>:<div className="video-loading" role="status">Loading clip…</div>}{needsPlay&&url&&!playError?<div className="playback-start"><button className="primary" onClick={()=>{if(video.current)playVideo(video.current);}}><Play size={18}/>Play video</button></div>:null}{playError?<div className="playback-error"><p>{playError}</p><button onClick={retryPlayback}>Retry playback</button></div>:null}</div>
      {buffering&&!playError?<div className="playback-status" role="status"><span>{buffering==='stalled'?'The video is taking longer to load. Retry if it stays stuck.':'Buffering video...'}</span>{buffering==='stalled'?<button onClick={retryPlayback}>Retry playback</button>:null}</div>:null}
      {fullscreenNotice?<Notice onClose={()=>setFullscreenNotice('')}>{fullscreenNotice}</Notice>:null}
      <div className="watch-title-row"><div><h1>{clip.title}</h1><p className="author">@{clip.username||clip.displayName}</p></div><div className="watch-tools"><button ref={shareButton} className="icon-button" aria-label="Share clip" title="Share clip" disabled={sharing} onClick={()=>void share()}><Share2/></button><button className="icon-button" aria-label="Fullscreen video" onClick={()=>void fullscreen()}><Maximize/></button></div></div>
      <ClipActions clip={clip} onLike={onLike} onComments={()=>{commentInput.current?.scrollIntoView({block:'center'});commentInput.current?.focus();}} onMore={onMore} busy={busy||posting}/>
      {shareNotice?<Notice onClose={()=>setShareNotice('')}>{shareNotice}</Notice>:null}{clip.description?<p className="description">{clip.description}</p>:null}
    </div><section className="comments" aria-label="Comments">
      <h2>Comments <span>{clip.commentCount||''}</span></h2>
      {actionError&&!deleteComment?<Notice onClose={()=>setActionError('')}>{actionError}</Notice>:null}
      <form onSubmit={post}><label className="sr-only" htmlFor="comment-body">Add a comment</label><textarea ref={commentInput} id="comment-body" placeholder="Add a comment" maxLength={500} rows={3} value={body} disabled={posting} onChange={event=>setBody(event.target.value)}/><div className="comment-compose-actions"><span className="caption">{body.length}/500</span><button type="submit" disabled={posting||!body.trim()} className="primary">{posting?'Posting…':'Post comment'}</button></div></form>
      {syncing||syncNotice?<div className="comments-state" role="status"><p>{syncNotice||'Refreshing clip details…'}</p>{syncNotice?<button type="button" disabled={syncing} onClick={()=>void refreshDetails(syncContext.current)}>Retry count refresh</button>:null}</div>:null}
      {comments.map(comment=><article className="comment" key={comment.id}><div><strong>@{comment.username||comment.displayName}</strong><time dateTime={comment.createdAt}>{new Date(comment.createdAt).toLocaleDateString(undefined,{month:'short',day:'numeric'})}</time>{comment.ownerId===api.user?.id?<button className="icon-button" aria-label="Delete your comment" disabled={posting} onClick={()=>{setActionError('');setDeleteComment(comment.id);}}><Trash2 size={16}/></button>:null}</div><p>{comment.body}</p></article>)}
      {loading?<p className="caption" role="status">Loading comments…</p>:null}
      {commentError?<div className="comments-state" role="status"><p>{commentError.older?'Older comments could not load.':'Comments could not load.'}</p><p className="caption">{commentError.message}</p><button type="button" disabled={loading} onClick={()=>void loadComments(commentError.older)}>{commentError.older?'Retry older comments':'Retry comments'}</button></div>:null}
      {!loading&&!commentError&&loaded&&!comments.length?<p className="caption">No comments yet. Start the conversation.</p>:null}
      {cursor&&!commentError?<button className="wide" disabled={loading} onClick={()=>void loadComments(true)}>Older comments</button>:null}
    </section></div>
    {deleteComment?<Dialog title="Delete your comment?" onClose={closeDelete}><p>This removes your comment from the conversation.</p>{actionError?<Notice>{actionError}</Notice>:null}<div className="dialog-actions"><button onClick={closeDelete} disabled={posting}>Cancel</button><button className="danger" disabled={posting} onClick={()=>void removeComment()}>{posting?'Deleting…':'Delete comment'}</button></div></Dialog>:null}
    {manualLink?<Dialog title="Share clip" onClose={closeShare}><p>Sign in to Klipp to watch this clip. Select the link below to copy it.</p><label>Clip link<textarea aria-label="Clip link" readOnly autoFocus rows={3} value={manualLink} onFocus={event=>event.currentTarget.select()} onClick={event=>{const input=event.currentTarget;requestAnimationFrame(()=>{if(input.isConnected)input.setSelectionRange(0,input.value.length);});}}/></label><div className="dialog-actions"><button onClick={closeShare}>Done</button></div></Dialog>:null}
  </section>;
}
