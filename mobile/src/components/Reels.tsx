import {useCallback,useEffect,useLayoutEffect,useMemo,useRef,useState,type KeyboardEvent} from 'react';
import {ArrowDown,ArrowUp,Heart,Leaf,MessageCircle,MoreHorizontal,Pause,Play,RotateCcw,Share2,Volume2,VolumeX} from 'lucide-react';
import type {Api} from '../lib/service';
import {messageFor,timeLabel} from '../lib/service';
import type {CommunityClip} from '../lib/models';
import {Dialog,Notice} from './Primitives';
import {shareClipLink} from '../lib/share';
import {restorePlayback} from '../lib/playback';
import {ReelLinks} from '../lib/reel-links';
import {reelResumeTime} from './reels-state';
import './reels.css';

interface Props {
  api:Api;items:CommunityClip[];initialId:string|null;suspended:boolean;busy:boolean;page:number;
  onLike:(clip:CommunityClip)=>void;onComments:(clip:CommunityClip)=>void;onMore:(clip:CommunityClip)=>void;
  onNext?:()=>void;onPrevious?:()=>void;onActiveChange:(id:string)=>void;
}
interface SavedPosition {time:number;paused:boolean}
type Connection=EventTarget&{saveData?:boolean;effectiveType?:string};
const connection=()=>(navigator as Navigator&{connection?:Connection}).connection;
const dataPreference='klipp.media-save-data.v1';
function initialDataSaver(){try{const saved=localStorage.getItem(dataPreference);if(saved!==null)return saved==='yes';}catch{}return connection()?.saveData===true;}

/** Only the active clip mounts a media element; prepared links contain no video bytes. */
function ReelVideo({links,clip,muted,onMuted,suspended,dataSaver,onPlayingChange,positions}:{links:ReelLinks;clip:CommunityClip;muted:boolean;onMuted:(muted:boolean)=>void;suspended:boolean;dataSaver:boolean;onPlayingChange:(playing:boolean)=>void;positions:Map<string,SavedPosition>}) {
  const video=useRef<HTMLVideoElement>(null),progress=useRef<HTMLInputElement>(null),elapsed=useRef<HTMLOutputElement>(null);
  const saved=useRef(positions.get(clip.id)),paused=useRef(saved.current?.paused??false),mounted=useRef(true),resumeAttempts=useRef(0);
  const [awake,setAwake]=useState(()=>!document.hidden),[requested,setRequested]=useState(()=>!dataSaver&&!document.hidden);
  const [url,setUrl]=useState(''),[error,setError]=useState(''),[retry,setRetry]=useState(0),[playing,setPlaying]=useState(false),[needsPlay,setNeedsPlay]=useState(false),[loading,setLoading]=useState(!dataSaver);
  const scrubbing=useRef(false),previousSaver=useRef(dataSaver);
  const suspendedRef=useRef(suspended);suspendedRef.current=suspended;
  const play=useCallback((element:HTMLVideoElement)=>{
    if(suspendedRef.current||document.hidden||paused.current||saved.current||scrubbing.current)return;
    setNeedsPlay(false);
    void element.play().catch(problem=>{
      if(!mounted.current||video.current!==element||problem?.name==='AbortError')return;
      setNeedsPlay(true);setPlaying(false);
    });
  },[]);
  useEffect(()=>{
    mounted.current=true;
    if(!awake||!requested){setUrl('');setLoading(false);return()=>{mounted.current=false;};}
    const abort=new AbortController();setError('');setLoading(true);setUrl('');
    void links.get(clip.id,abort.signal,retry>0).then(source=>{if(!abort.signal.aborted)setUrl(source);})
      .catch(problem=>{if(!abort.signal.aborted&&problem?.name!=='AbortError'){setLoading(false);setError(messageFor(problem));}});
    return()=>{mounted.current=false;abort.abort();};
  },[links,clip.id,retry,awake,requested]);
  useEffect(()=>{
    const element=video.current;if(!element)return;
    return()=>{
      if(element.hasAttribute('src'))positions.set(clip.id,{time:Number.isFinite(element.currentTime)?element.currentTime:0,paused:paused.current});
      onPlayingChange(false);element.pause();element.removeAttribute('src');element.load();
    };
  },[url,clip.id,positions,onPlayingChange]);
  useEffect(()=>{
    const element=video.current;if(!element)return;
    if(suspended)element.pause();else play(element);
  },[suspended,url,play]);
  useEffect(()=>{
    const release=()=>{
      const element=video.current;
      // visibilitychange and pagehide can arrive together. A second release
      // must not replace the saved position with the detached player's zero.
      if(element?.hasAttribute('src')){saved.current={time:element.currentTime,paused:true};positions.set(clip.id,saved.current);element.pause();element.removeAttribute('src');element.load();}
      paused.current=true;resumeAttempts.current=0;setRequested(false);setAwake(false);setPlaying(false);onPlayingChange(false);setNeedsPlay(false);setError('');setLoading(false);
    };
    const background=()=>{if(document.hidden)release();else setAwake(true);};
    const pagehide=()=>release(),pageshow=()=>setAwake(!document.hidden);
    document.addEventListener('visibilitychange',background);window.addEventListener('pagehide',pagehide);
    window.addEventListener('pageshow',pageshow);
    return()=>{document.removeEventListener('visibilitychange',background);window.removeEventListener('pagehide',pagehide);window.removeEventListener('pageshow',pageshow);};
  },[clip.id,positions,onPlayingChange]);
  useEffect(()=>{
    if(dataSaver&&!previousSaver.current){
      const element=video.current;if(element){saved.current={time:element.currentTime,paused:true};positions.set(clip.id,saved.current);element.pause();}
      paused.current=true;resumeAttempts.current=0;setRequested(false);setPlaying(false);onPlayingChange(false);setNeedsPlay(false);setError('');setLoading(false);
    }
    previousSaver.current=dataSaver;
  },[dataSaver,clip.id,positions,onPlayingChange]);
  useEffect(()=>{
    if(!url||!loading||suspended||!awake)return;
    const timer=setTimeout(()=>failPlayback('Video is taking too long to load. Check your connection or retry playback.'),12000);
    return()=>clearTimeout(timer);
  },[url,loading,suspended,awake]);
  function ready(element:HTMLVideoElement){
    if(saved.current){resumePlayback(element);return;}
    setLoading(false);play(element);
  }
  function finishResume(element:HTMLVideoElement){
    const position=saved.current;if(!position||element.seeking)return;
    const target=reelResumeTime(position.time,element.duration);
    if(Math.abs(element.currentTime-target)>.08){
      if(resumeAttempts.current<3)resumePlayback(element);
      else{setLoading(false);setError('The saved playback position could not be restored. Retry playback.');}
      return;
    }
    saved.current=undefined;setLoading(false);updateProgress(element);play(element);
  }
  function resumePlayback(element:HTMLVideoElement){
    const position=saved.current;if(!position||resumeAttempts.current>=3)return;
    if(restorePlayback(element,{time:position.time,playing:!position.paused,muted:element.muted,volume:element.volume,rate:element.playbackRate})){
      resumeAttempts.current++;if(!element.seeking)finishResume(element);
    }
  }
  function toggle(){const element=video.current;if(!element||suspended)return;
    if(element.paused){paused.current=false;play(element);}else{paused.current=true;element.pause();}
  }
  function start(){
    if(suspended||!awake)return;
    if(!requested){paused.current=false;setRequested(true);setNeedsPlay(false);return;}
    toggle();
  }
  function updateProgress(element:HTMLVideoElement){
    if(scrubbing.current)return;
    // Playback time never rerenders the feed or the interaction controls.
    if(progress.current){progress.current.max=String(Number.isFinite(element.duration)?element.duration:clip.durationSeconds);progress.current.value=String(element.currentTime);progress.current.setAttribute('aria-valuetext',`${timeLabel(element.currentTime)} of ${timeLabel(element.duration||clip.durationSeconds)}`);}
    if(elapsed.current)elapsed.current.value=timeLabel(element.currentTime);
  }
  function recover(){
    const element=video.current;
    if(element&&!saved.current)saved.current={time:element.currentTime,paused:paused.current};
    resumeAttempts.current=0;
    setRequested(true);
    setRetry(value=>value+1);
  }
  function failPlayback(message:string){
    const element=video.current;
    if(element){saved.current={time:element.currentTime,paused:paused.current};positions.set(clip.id,saved.current);element.pause();element.removeAttribute('src');element.load();}
    setUrl('');setLoading(false);setPlaying(false);onPlayingChange(false);setError(message);
  }
  function seek(value:string){
    const element=video.current;if(!element)return;
    if(elapsed.current)elapsed.current.value=timeLabel(Number(value));
    if(!scrubbing.current)element.currentTime=Number(value);
  }
  function finishScrub(value:string){
    if(!scrubbing.current)return;scrubbing.current=false;
    if(video.current){video.current.currentTime=Number(value);if(!video.current.seeking)play(video.current);}
  }
  return <div className="reel-player">
    {url?<video className="reel-video" ref={video} src={url} aria-label={`Video: ${clip.title}`} playsInline muted={muted} loop preload="auto"
      onLoadedMetadata={event=>ready(event.currentTarget)} onCanPlay={event=>ready(event.currentTarget)} onSeeked={event=>{setLoading(false);if(saved.current)finishResume(event.currentTarget);else if(!scrubbing.current)play(event.currentTarget);}}
      onPlaying={()=>{setPlaying(true);onPlayingChange(true);setNeedsPlay(false);setLoading(false);}} onPause={()=>{setPlaying(false);onPlayingChange(false);}}
      onTimeUpdate={event=>updateProgress(event.currentTarget)} onWaiting={()=>{setLoading(true);onPlayingChange(false);}}
      onError={event=>{if(event.currentTarget.hasAttribute('src')&&!document.hidden)failPlayback('This clip could not play. Retry for a fresh playback link.');}}/>:null}
    <button className="reel-surface" aria-label={playing?'Pause clip':'Play clip'} onClick={start} disabled={requested&&!url||!!error||suspended}>
      {!playing&&!loading&&!error?<span className="reel-play-symbol"><Play fill="currentColor" size={32}/></span>:null}
    </button>
    {loading&&!error?<span className="reel-loading" role="status">Loading video…</span>:null}
    {!requested?<span className="reel-load-hint">{dataSaver?'Data saver · Tap to play':'Tap to continue'}</span>:null}
    {needsPlay&&!error?<button className="reel-play-fallback" onClick={start}><Play size={18}/>Start video</button>:null}
    {error?<div className="reel-error" role="alert"><p>{error}</p><button onClick={recover}><RotateCcw size={18}/>Retry playback</button></div>:null}
    <div className="reel-transport">
      <button className="reel-small-button" aria-label={playing?'Pause video':'Play video'} onClick={start} disabled={requested&&!url||!!error||suspended}>{playing?<Pause size={18}/>:<Play size={18}/>}</button>
      <output ref={elapsed} className="reel-time" aria-hidden="true">{timeLabel(saved.current?.time??0)}</output>
      <input ref={progress} type="range" className="reel-seek" aria-label={`Seek ${clip.title}`} min="0" max={clip.durationSeconds} step="0.01" defaultValue={saved.current?.time??0} disabled={!url||!!error} onPointerDown={()=>{scrubbing.current=true;video.current?.pause();}} onPointerUp={event=>finishScrub(event.currentTarget.value)} onPointerCancel={event=>finishScrub(event.currentTarget.value)} onBlur={event=>finishScrub(event.currentTarget.value)} onChange={event=>seek(event.currentTarget.value)}/>
      <button className="reel-small-button" aria-label={muted?'Unmute video':'Mute video'} onClick={()=>onMuted(!muted)}>{muted?<VolumeX size={20}/>:<Volume2 size={20}/>}</button>
    </div>
  </div>;
}

export function Reels({api,items,initialId,suspended,busy,page,onLike,onComments,onMore,onNext,onPrevious,onActiveChange}:Props){
  const scroller=useRef<HTMLDivElement>(null),positions=useRef(new Map<string,SavedPosition>()),activeRef=useRef<string|null>(null),activeLifetime=useRef(true);
  const [active,setActive]=useState<string|null>(()=>items.some(clip=>clip.id===initialId)?initialId:items[0]?.id??null),[visible,setVisible]=useState(true),[muted,setMuted]=useState(true),[manualLink,setManualLink]=useState(''),[shareNotice,setShareNotice]=useState('');
  const [moving,setMoving]=useState(false),[playing,setPlaying]=useState(false),[dataSaver,setDataSaver]=useState(initialDataSaver),[networkVersion,setNetworkVersion]=useState(0),[foreground,setForeground]=useState(()=>!document.hidden);
  const links=useMemo(()=>new ReelLinks(api),[api]),playingChanged=useCallback((value:boolean)=>setPlaying(value),[]);
  const movingRef=useRef(false),measureRef=useRef<()=>void>(()=>{});
  const sharing=useRef(false),requestId=useRef(0),requested=useRef<string|null>(null);
  activeRef.current=active;
  const routeChange=useRef(onActiveChange),visibleRef=useRef(visible);routeChange.current=onActiveChange;visibleRef.current=visible;
  const blocked=suspended||!!manualLink;
  const frozen=useRef(blocked),wasBlocked=useRef(blocked),measuredHeight=useRef<number|null>(null);frozen.current=blocked;
  const ids=items.map(clip=>clip.id).join(':');
  useEffect(()=>()=>links.dispose(),[links]);
  useEffect(()=>{
    const network=connection(),changed=()=>setNetworkVersion(value=>value+1);
    const visibility=()=>{setForeground(!document.hidden);if(document.hidden)links.retain([]);};
    const hide=()=>{setForeground(false);links.retain([]);},show=()=>setForeground(!document.hidden);
    network?.addEventListener('change',changed);window.addEventListener('online',changed);window.addEventListener('offline',changed);
    document.addEventListener('visibilitychange',visibility);window.addEventListener('pagehide',hide);window.addEventListener('pageshow',show);
    return()=>{network?.removeEventListener('change',changed);window.removeEventListener('online',changed);window.removeEventListener('offline',changed);document.removeEventListener('visibilitychange',visibility);window.removeEventListener('pagehide',hide);window.removeEventListener('pageshow',show);};
  },[links]);
  useEffect(()=>{
    const index=items.findIndex(clip=>clip.id===active),next=items[index+1];
    // Links are small authorization responses only. No offscreen video, poster,
    // range request or decoder is created. Keep at most the adjacent three links.
    links.retain(foreground?items.slice(Math.max(0,index-1),index+2).map(clip=>clip.id):[]);
    const network=connection();
    if(!foreground||blocked||moving||!visible||!playing||dataSaver||!navigator.onLine||network?.saveData||['slow-2g','2g','3g'].includes(network?.effectiveType??'')||!next||next.status!=='published'||next.moderationHidden)return;
    const abort=new AbortController();
    const timer=setTimeout(()=>{void links.get(next.id,abort.signal).catch(()=>{});},450);
    return()=>{clearTimeout(timer);abort.abort();};
  },[links,active,ids,foreground,blocked,moving,visible,playing,dataSaver,networkVersion]);
  useEffect(()=>{activeLifetime.current=true;return()=>{activeLifetime.current=false;requestId.current++;};},[api]);
  useEffect(()=>{
    if(blocked){movingRef.current=false;setMoving(false);}
    else if(wasBlocked.current){
      const element=scroller.current,index=items.findIndex(clip=>clip.id===activeRef.current);
      if(element&&index>=0)element.scrollTop=index*element.clientHeight;
    }
    wasBlocked.current=blocked;
  },[blocked]);
  useEffect(()=>{
    if(active&&!items.some(clip=>clip.id===active)){setActive(items[0]?.id??null);setVisible(true);if(scroller.current)scroller.current.scrollTop=0;}
    else if(!active&&items.length)setActive(items[0].id);
  },[ids,active]);
  useLayoutEffect(()=>{
    const element=scroller.current;if(!element)return;
    let frame=0;
    const measure=()=>{cancelAnimationFrame(frame);frame=requestAnimationFrame(()=>{
      // The keyboard or a modal must not resize/snap the paused feed behind it.
      if(frozen.current||movingRef.current)return;
      const bottom=document.querySelector('.bottom-nav')?.getBoundingClientRect().height??76;
      const height=window.visualViewport?.height??window.innerHeight;
      const nextHeight=Math.max(180,height-element.getBoundingClientRect().top-bottom);
      if(measuredHeight.current===nextHeight)return;
      const cards=Array.from(element.querySelectorAll<HTMLElement>('.reel-item'));
      const anchor=visibleRef.current?cards.findIndex(card=>card.dataset.clipId===activeRef.current):cards.length-1;
      measuredHeight.current=nextHeight;
      element.style.setProperty('--reels-height',`${nextHeight}px`);
      // Each snap card is one viewport tall. Anchor the same clip after rotation,
      // keyboard dismissal or a notice changing the available space above it.
      if(anchor>=0)element.scrollTop=anchor*nextHeight;
    });};
    measureRef.current=measure;
    const observer=new ResizeObserver(measure);
    for(const target of [document.querySelector('.app-header'),document.querySelector('.bottom-nav'),document.querySelector('.app-shell')])if(target)observer.observe(target);
    measure();window.addEventListener('resize',measure);window.visualViewport?.addEventListener('resize',measure);
    return()=>{measureRef.current=()=>{};cancelAnimationFrame(frame);observer.disconnect();window.removeEventListener('resize',measure);window.visualViewport?.removeEventListener('resize',measure);};
  },[blocked]);
  useEffect(()=>{
    const element=scroller.current;if(!element)return;
    let timer:ReturnType<typeof setTimeout>|undefined;
    const touches=new Set<number>();
    const settle=()=>{
      clearTimeout(timer);if(frozen.current||touches.size)return;
      const cards=Array.from(element.querySelectorAll<HTMLElement>('.reel-item'));
      const index=Math.max(0,Math.min(cards.length-1,Math.round(element.scrollTop/element.clientHeight)));
      const next=cards[index]?.dataset.clipId;
      if(next){setActive(next);activeRef.current=next;setVisible(true);visibleRef.current=true;routeChange.current(next);}
      else{setVisible(false);visibleRef.current=false;}
      movingRef.current=false;setMoving(false);measureRef.current();
    };
    const scroll=()=>{
      if(frozen.current)return;
      // One update at gesture start/end, never React state on every scroll frame.
      if(!movingRef.current){movingRef.current=true;setMoving(true);element.querySelector('video')?.pause();}
      clearTimeout(timer);timer=setTimeout(settle,120);
    };
    const down=(event:PointerEvent)=>{if(event.pointerType==='touch')touches.add(event.pointerId);};
    const up=(event:PointerEvent)=>{touches.delete(event.pointerId);if(movingRef.current){clearTimeout(timer);timer=setTimeout(settle,120);}};
    const ended=()=>{clearTimeout(timer);timer=setTimeout(settle,40);};
    element.addEventListener('scroll',scroll,{passive:true});element.addEventListener('scrollend',ended);
    element.addEventListener('pointerdown',down,{passive:true});window.addEventListener('pointerup',up,{passive:true});window.addEventListener('pointercancel',up,{passive:true});
    return()=>{clearTimeout(timer);element.removeEventListener('scroll',scroll);element.removeEventListener('scrollend',ended);element.removeEventListener('pointerdown',down);window.removeEventListener('pointerup',up);window.removeEventListener('pointercancel',up);};
  },[ids]);
  useEffect(()=>{
    if(!initialId||requested.current===initialId)return;
    const element=scroller.current,item=Array.from(element?.querySelectorAll<HTMLElement>('[data-clip-id]')??[]).find(item=>item.dataset.clipId===initialId);
    if(!element||!item)return;
    const firstRequest=requested.current===null;requested.current=initialId;if(!firstRequest&&initialId===activeRef.current)return;setActive(initialId);setVisible(true);element.scrollTop=item.offsetTop-element.offsetTop;
  },[initialId,ids]);
  function move(direction:number){
    const element=scroller.current;if(!element)return;
    const index=visibleRef.current?Math.max(0,items.findIndex(clip=>clip.id===activeRef.current)):items.length;
    const target=element.querySelectorAll<HTMLElement>('.reel-item')[Math.max(0,Math.min(items.length,index+direction))];
    if(target)element.scrollTo({top:target.offsetTop-element.offsetTop,behavior:'auto'});
  }
  function key(event:KeyboardEvent<HTMLDivElement>){
    if(event.target!==event.currentTarget)return;
    if(event.key==='ArrowDown'||event.key==='PageDown'){event.preventDefault();move(1);}
    else if(event.key==='ArrowUp'||event.key==='PageUp'){event.preventDefault();move(-1);}
  }
  function toggleDataSaver(){
    const value=!dataSaver;setDataSaver(value);
    try{localStorage.setItem(dataPreference,value?'yes':'no');}catch{}
    setShareNotice(value?'Data saver on. Tap each clip to play; next clips won’t load in the background.':'Data saver off. Clips play automatically; only the next playback link is prepared.');
  }
  async function share(clip:CommunityClip){
    if(sharing.current)return;sharing.current=true;const request=++requestId.current;setShareNotice('');
    try{
      const result=await shareClipLink(clip.id,clip.title,window.location.href,{
        share:typeof navigator.share==='function'?data=>navigator.share(data):undefined,
        copy:async text=>{if(!navigator.clipboard?.writeText)throw new Error('Clipboard unavailable');await navigator.clipboard.writeText(text);},
      });
      if(!activeLifetime.current||requestId.current!==request)return;
      if(result.status==='manual')setManualLink(result.url);else if(result.status==='copied')setShareNotice('Link copied. Sign in to Klipp to watch.');
    }catch{if(activeLifetime.current&&requestId.current===request)setShareNotice('The clip link could not be shared. Please try again.');}
    finally{if(requestId.current===request)sharing.current=false;}
  }
  return <>
    <div className="reels-shell">
    <button className="reel-data-mode" aria-label="Save data" aria-pressed={dataSaver} onClick={toggleDataSaver} title="Tap to play each clip and skip background preparation"><Leaf size={15}/>{dataSaver?'Data saver on':'Save data'}</button>
    <div ref={scroller} className="reels-feed" role="region" aria-label="Video feed" tabIndex={0} onKeyDown={key} aria-keyshortcuts="ArrowDown ArrowUp PageDown PageUp" data-scrolling={moving?'true':'false'}>
      {items.map((clip,index)=>{const selected=active===clip.id&&visible,available=clip.status==='published'&&!clip.moderationHidden;return <article className="reel-item" key={clip.id} data-clip-id={clip.id} data-active={selected?'true':'false'} aria-label={`${clip.title}, clip ${index+1} of ${items.length}`}>
        {selected&&available?<ReelVideo key={clip.id} links={links} clip={clip} muted={muted} onMuted={setMuted} suspended={blocked||moving} dataSaver={dataSaver} onPlayingChange={playingChanged} positions={positions.current}/>:<div className="reel-idle" aria-hidden="true"><Play size={34}/></div>}
        <div className="reel-caption"><p className="reel-author">@{clip.username||clip.displayName}</p><h2>{clip.title}</h2>{clip.description?<p className="reel-description">{clip.description}</p>:null}{!available?<p className="reel-unavailable">{clip.moderationHidden?'Hidden from Media. You can manage this upload from its actions.':'This upload is incomplete. Use its actions to finish removing it.'}</p>:null}</div>
        <div className="reel-actions">
          <button aria-label={`${clip.likedByMe?'Unlike':'Like'} ${clip.title}`} aria-pressed={clip.likedByMe} className={clip.likedByMe?'liked':''} disabled={busy||!available} onClick={()=>onLike(clip)}><Heart fill={clip.likedByMe?'currentColor':'none'}/><span>{clip.likeCount||'Like'}</span></button>
          <button aria-label={`Comments for ${clip.title}`} disabled={!available} onClick={()=>onComments(clip)}><MessageCircle/><span>{clip.commentCount||'Comments'}</span></button>
          <button aria-label={`Share ${clip.title}`} disabled={!available} onClick={()=>void share(clip)}><Share2/><span>Share</span></button>
          <button aria-label={`Actions for ${clip.title}`} onClick={()=>onMore(clip)}><MoreHorizontal/><span>More</span></button>
        </div>
        <span className="reel-position" aria-hidden="true">{index+1} / {items.length}</span>
      </article>;})}
      <div className="reel-item reel-end" data-reel-end><div><ArrowDown size={26}/><h2>{onNext?'More clips are ready':'You’re all caught up'}</h2><p>{onNext?'Continue to the next clips.':'New clips will appear here when they’re shared.'}</p>{onNext?<button onClick={onNext}>Older clips<ArrowDown size={18}/></button>:null}{onPrevious?<button onClick={onPrevious}><ArrowUp size={18}/>Newer clips</button>:null}<p className="caption">Page {page+1} · Upload from Klipp on PC</p></div></div>
    </div>
    </div>
    {shareNotice?<div className="reel-share-notice"><Notice onClose={()=>setShareNotice('')}>{shareNotice}</Notice></div>:null}
    {manualLink?<Dialog title="Share clip" onClose={()=>setManualLink('')}><p>Sign in to Klipp to watch this clip.</p><label>Clip link<textarea aria-label="Clip link" readOnly autoFocus rows={3} value={manualLink} onFocus={event=>event.currentTarget.select()}/></label><div className="dialog-actions"><button onClick={()=>setManualLink('')}>Done</button></div></Dialog>:null}
  </>;
}
