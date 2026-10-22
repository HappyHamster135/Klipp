import {useEffect,useRef,useState,type FormEvent,type KeyboardEvent} from 'react';
import {ArrowLeft,Film,FolderOpen,Volume2,VolumeX} from 'lucide-react';
import type {Api} from '../lib/service';
import {messageFor,sizeLabel,timeLabel} from '../lib/service';
import {inspectClip,prepareClip,validRange,type ClipInfo} from '../media/prepare';
import {parseSeconds,secondsInput} from '../media/range';
import {Notice} from './Primitives';

interface Props{
  api:Api;onCancel:()=>void;onBusy:(value:boolean)=>void;onUploaded:()=>void;onDirty?:(value:boolean)=>void;
}
export default function UploadClip({api,onCancel,onBusy,onUploaded,onDirty}:Props){
  const input=useRef<HTMLInputElement>(null),video=useRef<HTMLVideoElement>(null),lifetime=useRef<AbortController|null>(null),mounted=useRef(true),discardTrim=useRef(false);
  const [file,setFile]=useState<File|null>(null),[url,setUrl]=useState(''),[info,setInfo]=useState<ClipInfo|null>(null),[posters,setPosters]=useState<string[]>([]);
  const [start,setStart]=useState(0),[end,setEnd]=useState(0),[startText,setStartText]=useState('0'),[endText,setEndText]=useState('0');
  const [title,setTitle]=useState(''),[description,setDescription]=useState(''),[audio,setAudio]=useState(true),[reading,setReading]=useState(false),[busy,setBusy]=useState(false);
  const [progress,setProgress]=useState(0),[status,setStatus]=useState(''),[error,setError]=useState('');
  const draftStart=parseSeconds(startText),draftEnd=parseSeconds(endText);
  const rangeValid=!!info&&draftStart!==null&&draftEnd!==null&&validRange(draftStart,draftEnd,info.duration);
  const current=(abort:AbortController)=>mounted.current&&lifetime.current===abort;

  useEffect(()=>{
    if(!file){setUrl('');return;}
    const local=URL.createObjectURL(file);setUrl(local);return()=>URL.revokeObjectURL(local);
  },[file]);
  useEffect(()=>{
    const element=video.current;
    return()=>{if(element){element.pause();element.removeAttribute('src');element.load();}};
  },[url]);
  useEffect(()=>{
    const urls=info?.posters.map(blob=>URL.createObjectURL(blob))??[];
    setPosters(urls);return()=>urls.forEach(value=>URL.revokeObjectURL(value));
  },[info]);
  useEffect(()=>{
    mounted.current=true;
    return()=>{mounted.current=false;lifetime.current?.abort();onBusy(false);onDirty?.(false);};
  },[onBusy,onDirty]);
  useEffect(()=>{onDirty?.(!!file);},[file,onDirty]);
  useEffect(()=>{
    const pause=()=>{if(document.hidden)video.current?.pause();};
    document.addEventListener('visibilitychange',pause);
    return()=>document.removeEventListener('visibilitychange',pause);
  },[]);
  useEffect(()=>{
    const element=video.current;
    if(element){element.pause();if(element.readyState>=1&&Number.isFinite(start))element.currentTime=start;}
  },[start,end,url]);

  function applyRange(nextStart:number,nextEnd:number){
    setStart(nextStart);setEnd(nextEnd);setStartText(secondsInput(nextStart));setEndText(secondsInput(nextEnd));
  }
  async function choose(picked:File|undefined){
    if(!picked||busy)return;
    lifetime.current?.abort();const abort=new AbortController();lifetime.current=abort;
    setFile(picked);setInfo(null);setReading(true);setError('');setStatus('');setProgress(0);
    setTitle(picked.name.replace(/\.[^.]+$/,'').replace(/[_-]/g,' ').slice(0,120));setDescription('');applyRange(0,0);
    try{
      const metadata=await inspectClip(picked,abort.signal);
      if(!current(abort)||abort.signal.aborted)return;
      setInfo(metadata);applyRange(0,Math.min(60,metadata.duration));setAudio(metadata.hasAudio);
    }catch(error){if(current(abort)&&!abort.signal.aborted)setError(messageFor(error));}
    finally{if(current(abort))setReading(false);}
  }
  function commitDraft(){if(discardTrim.current){discardTrim.current=false;return;}if(rangeValid)applyRange(draftStart!,draftEnd!);}
  function trimKey(event:KeyboardEvent<HTMLInputElement>){
    if(event.key==='Enter'){event.preventDefault();event.currentTarget.blur();}
    else if(event.key==='Escape'){event.preventDefault();discardTrim.current=true;setStartText(secondsInput(start));setEndText(secondsInput(end));event.currentTarget.blur();}
  }
  function moveEdge(edge:'start'|'end',value:number){
    if(!info||!Number.isFinite(value))return;
    const minimum=Math.min(0.1,info.duration);
    if(edge==='start')applyRange(Math.max(0,end-60,Math.min(end-minimum,value)),end);
    else applyRange(start,Math.min(info.duration,start+60,Math.max(start+minimum,value)));
  }
  async function submit(event:FormEvent){
    event.preventDefault();
    if(!file||!rangeValid||!title.trim()||busy||reading)return;
    video.current?.pause();const abort=new AbortController();lifetime.current?.abort();lifetime.current=abort;
    setBusy(true);onBusy(true);setError('');setProgress(0);setStatus('Preparing on this device…');
    let uploadStarted=false;
    try{
      const share=await prepareClip(file,{start:draftStart!,end:draftEnd!,includeAudio:audio},abort.signal,value=>{
        if(current(abort)&&!abort.signal.aborted)setProgress(Math.max(0,Math.min(65,Math.round(value*65))));
      });
      if(!current(abort)||abort.signal.aborted)return;
      setProgress(65);setStatus('Uploading your share copy…');uploadStarted=true;
      await api.uploadClip({...share,title:title.trim(),description:description.trim()},value=>{
        if(current(abort)&&!abort.signal.aborted){setStatus(value.status);setProgress(65+Math.min(35,Math.round(35*value.completedBytes/Math.max(1,value.totalBytes))));}
      },abort.signal);
      if(current(abort)&&!abort.signal.aborted){onDirty?.(false);onUploaded();}
    }catch(error){
      if(!current(abort))return;
      if(abort.signal.aborted){
        setStatus('Upload canceled. Your original is unchanged.');
        setError(uploadStarted?'Upload canceled. If cleanup could not finish, remove the incomplete entry from My uploads.':'Upload canceled. Your original is unchanged.');
      }else setError(messageFor(error));
    }finally{if(current(abort)){setBusy(false);onBusy(false);}}
  }
  function cancel(){if(busy){lifetime.current?.abort();setStatus('Canceling and cleaning up…');}else onCancel();}

  return <main className="upload-page">
    <div className="upload-heading"><button className="icon-button" aria-label="Back to Media" disabled={busy} onClick={onCancel}><ArrowLeft/></button><h1>Upload clip</h1></div>
    <input className="sr-only" ref={input} type="file" disabled={busy} accept="video/mp4,video/quicktime,video/webm,.mp4,.mov,.webm" aria-label="Choose video from your phone" onChange={event=>{const picked=event.currentTarget.files?.[0];event.currentTarget.value='';void choose(picked);}}/>
    {error?<Notice onClose={()=>setError('')}>{error}</Notice>:null}
    {!file?<section className="file-picker"><Film size={42}/><h2>Choose your highlight</h2><p>Select a video from your photo library or files.</p><button className="primary" onClick={()=>input.current?.click()}><FolderOpen size={18}/>Choose a video</button><p className="caption">Up to 60 seconds. Five shared clips per account.<br/>Your original stays on your phone.</p></section>:
      <form onSubmit={submit} className="upload-grid">
        <section className="upload-preview">
          <div className="preview-frame"><video key={url} ref={video} src={url||undefined} controls playsInline preload="metadata" muted={!audio}
            onLoadedMetadata={()=>{if(video.current&&Number.isFinite(start))video.current.currentTime=start;}}
            onPlay={()=>{if(!rangeValid||busy||reading){video.current?.pause();return;}if(video.current&&(video.current.currentTime<start||video.current.currentTime>=end))video.current.currentTime=start;}}
            onTimeUpdate={()=>{if(video.current&&end>0&&video.current.currentTime>=end){video.current.pause();if(video.current.currentTime>end+0.001)video.current.currentTime=end;}}}
            onSeeking={()=>{if(video.current&&rangeValid&&(video.current.currentTime<start||video.current.currentTime>end))video.current.currentTime=Math.max(start,Math.min(end,video.current.currentTime));}}/>
          </div>
          <div className="source-summary"><div><strong>{file.name}</strong><p>{info?`${timeLabel(info.duration)} · ${info.width} × ${info.height} · ${sizeLabel(file.size)}`:sizeLabel(file.size)}</p></div><button type="button" disabled={busy} onClick={()=>input.current?.click()}>Change</button></div>
          {reading?<p role="status" className="caption">Reading video and creating previews…</p>:null}
          {info?<div className="range-editor">
            <div className="range-labels">
              <label>Start <input aria-label="Section start in seconds" aria-describedby="trim-section-summary" aria-invalid={!rangeValid} type="text" inputMode="decimal" autoComplete="off" enterKeyHint="done" disabled={busy} value={startText} onChange={event=>setStartText(event.target.value)} onBlur={commitDraft} onKeyDown={trimKey}/></label>
              <label>End <input aria-label="Section end in seconds" aria-describedby="trim-section-summary" aria-invalid={!rangeValid} type="text" inputMode="decimal" autoComplete="off" enterKeyHint="done" disabled={busy} value={endText} onChange={event=>setEndText(event.target.value)} onBlur={commitDraft} onKeyDown={trimKey}/></label>
            </div>
            <div className="range-strip">{posters.map(poster=><img key={poster} src={poster} alt=""/>)}<div className="range-selected" style={{left:`${100*start/info.duration}%`,right:`${100-100*end/info.duration}%`}}/>
              <input type="range" min="0" max={info.duration} step="0.1" aria-label="Trim section start" aria-valuetext={`${secondsInput(start)} seconds`} value={start} disabled={busy} onChange={event=>moveEdge('start',Number(event.target.value))}/>
              <input type="range" min="0" max={info.duration} step="0.1" aria-label="Trim section end" aria-valuetext={`${secondsInput(end)} seconds`} value={end} disabled={busy} onChange={event=>moveEdge('end',Number(event.target.value))}/>
            </div>
            <p id="trim-section-summary" className={rangeValid?'caption':'range-error'}>{rangeValid?`${timeLabel(draftStart!)} – ${timeLabel(draftEnd!)} · ${(draftEnd!-draftStart!).toFixed(1)} seconds selected`:'Enter start and end in seconds, inside the video and up to 60 seconds apart.'}</p>
          </div>:null}
        </section>
        <section className="upload-fields">
          <label>Title<input maxLength={120} value={title} onChange={event=>setTitle(event.target.value)} required disabled={busy}/></label>
          <label><span className="field-label">Description <span>(optional)</span></span><textarea maxLength={500} value={description} onChange={event=>setDescription(event.target.value)} rows={3} disabled={busy}/></label>
          {info?.hasAudio?<label className="audio-toggle"><span>{audio?<Volume2 size={20}/>:<VolumeX size={20}/>}Include audio</span><input type="checkbox" role="switch" checked={audio} onChange={event=>setAudio(event.target.checked)} disabled={busy}/></label>:info?<p className="caption">This clip has no audio.</p>:null}
          <p className="upload-explainer">Only this section is shared with signed-in Klipp members. A smaller MP4 is prepared on your device. Your original stays on your phone.</p>
          {busy?<div role="status" className="upload-progress"><p>{status}</p><progress max="100" value={progress}/><span>{progress}%</span><p className="caption">Keep Klipp open until this finishes. Switching apps may pause preparation.</p></div>:null}
          <div className="upload-submit"><button type="submit" className="primary wide" disabled={busy||reading||!rangeValid||!title.trim()}>{busy?'Uploading…':'Upload clip'}</button><button type="button" className="text-button wide" onClick={cancel}>{busy?'Cancel upload':'Cancel'}</button></div>
        </section>
      </form>}
  </main>;
}
