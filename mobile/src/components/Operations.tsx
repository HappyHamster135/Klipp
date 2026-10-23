import {useEffect,useRef,useState} from 'react';
import type {Api} from '../lib/service';
import {messageFor} from '../lib/service';
import type {CommunityCursor,CommunityReport,CommunitySupportRequest} from '../lib/models';
import type {ProfileEditingState} from './Profile';
import {Dialog,Notice} from './Primitives';

export function Operations({api,onBack,onOpenClip,onEditingChange}:{api:Api;onBack:()=>void;onOpenClip:(id:string)=>void;onEditingChange?:(state:ProfileEditingState)=>void}){
  const [kind,setKind]=useState<'reports'|'support'>('reports'),[reports,setReports]=useState<CommunityReport[]>([]),[requests,setRequests]=useState<CommunitySupportRequest[]>([]);
  const [cursor,setCursor]=useState<CommunityCursor|null>(null),[error,setError]=useState(''),[retry,setRetry]=useState(0),[loading,setLoading]=useState(true),[busy,setBusy]=useState(false);
  const [notes,setNotes]=useState<Record<string,string>>({}),[replies,setReplies]=useState<Record<string,string>>({}),[hiding,setHiding]=useState<CommunityReport|null>(null);
  const abort=useRef<AbortController|null>(null),lock=useRef(false);
  const dirty=Object.values(notes).some(value=>value.trim())||Object.values(replies).some(value=>value.trim());
  useEffect(()=>{onEditingChange?.({dirty,busy});},[dirty,busy,onEditingChange]);
  useEffect(()=>()=>onEditingChange?.({dirty:false,busy:false}),[onEditingChange]);
  useEffect(()=>{
    const controller=new AbortController();abort.current=controller;setLoading(true);setError('');setCursor(null);setReports([]);setRequests([]);
    const loading=kind==='reports'?api.getOperatorReports(null,controller.signal).then(page=>{if(!controller.signal.aborted){setReports(page.items);setCursor(page.nextCursor);}}):api.getOperatorSupport(null,controller.signal).then(page=>{if(!controller.signal.aborted){setRequests(page.items);setCursor(page.nextCursor);}});
    void loading.catch(error=>{if(!controller.signal.aborted)setError(messageFor(error));}).finally(()=>{if(!controller.signal.aborted)setLoading(false);});
    return()=>{controller.abort();lock.current=false;};
  },[api,kind,retry]);
  async function mutate(action:(signal:AbortSignal)=>Promise<unknown>,accepted:()=>void){
    const scope=abort.current;if(!scope||scope.signal.aborted||lock.current)return;lock.current=true;setBusy(true);setError('');
    try{await action(scope.signal);if(!scope.signal.aborted){accepted();setRetry(value=>value+1);}}
    catch(error){if(!scope.signal.aborted)setError(messageFor(error));}finally{if(!scope.signal.aborted){lock.current=false;setBusy(false);}}
  }
  async function review(report:CommunityReport,hide:boolean){
    const note=notes[report.id]?.trim()??'';if(!note)return;
    await mutate(signal=>api.reviewClip(report.clipId,hide?'hide':'keep',note,signal),()=>{
      setNotes(previous=>{const next={...previous};delete next[report.id];return next;});setHiding(null);
    });
  }
  async function older(){
    const scope=abort.current;if(!scope||scope.signal.aborted||!cursor||lock.current)return;lock.current=true;setBusy(true);setError('');
    try{if(kind==='reports'){const page=await api.getOperatorReports(cursor,scope.signal);if(!scope.signal.aborted){setReports(list=>[...list,...page.items.filter(item=>list.every(old=>old.id!==item.id))]);setCursor(page.nextCursor);}}else{const page=await api.getOperatorSupport(cursor,scope.signal);if(!scope.signal.aborted){setRequests(list=>[...list,...page.items.filter(item=>list.every(old=>old.id!==item.id))]);setCursor(page.nextCursor);}}}
    catch(error){if(!scope.signal.aborted)setError(messageFor(error));}finally{if(!scope.signal.aborted){lock.current=false;setBusy(false);}}
  }
  return <main className="profile-page operations-page"><button className="back" onClick={onBack} disabled={busy}>Back to usage</button><h1>Reports &amp; requests</h1><p>Each note and private reply belongs to its own item. Decisions are checked and recorded by the server.</p><div className="profile-actions"><button disabled={busy||kind==='reports'} onClick={()=>setKind('reports')}>Reports</button><button disabled={busy||kind==='support'} onClick={()=>setKind('support')}>Private requests</button></div>
    {error?<Notice>{error}<button disabled={busy} onClick={()=>setRetry(value=>value+1)}>Refresh</button></Notice>:null}{loading?<p role="status">Loading {kind}…</p>:null}
    {!loading&&kind==='reports'?(reports.length?reports.map(report=><article className="account-tool-section moderation-item" key={report.id} aria-label={`Report: ${report.title}`}><h2>{report.title}</h2><p>@{report.ownerUsername||'member'} · {report.status}{report.hidden?' · hidden':''}</p><p>{report.reason}</p><p className="caption">Reported by @{report.reporterUsername||'member'} · {new Date(report.createdAt).toLocaleString()}</p>{report.reviewNote?<p className="caption">Previous decision: {report.reviewNote}</p>:null}<button disabled={busy||report.hidden} onClick={()=>onOpenClip(report.clipId)}>View clip</button>{report.hidden?<p className="caption">Hidden clips cannot be played here.</p>:null}<label>Review note<textarea maxLength={1000} rows={3} value={notes[report.id]??''} disabled={busy} onChange={event=>setNotes(previous=>({...previous,[report.id]:event.target.value}))} placeholder="Explain the review decision"/></label><div className="dialog-actions"><button disabled={busy||!notes[report.id]?.trim()} onClick={()=>void review(report,false)}>Keep clip</button><button className="danger" disabled={busy||!notes[report.id]?.trim()} onClick={()=>setHiding(report)}>Hide clip</button></div></article>):<p>No reports to review.</p>):null}
    {!loading&&kind==='support'?(requests.length?requests.map(request=><article className="account-tool-section moderation-item" key={request.id} aria-label={`Request from ${request.username||'member'}`}><h2>{request.category} request</h2><p>@{request.username||'member'} · {request.status}</p><p>{request.body}</p>{request.reply?<p>Reply: {request.reply}</p>:null}<label>Private reply<textarea maxLength={2000} rows={4} value={replies[request.id]??''} disabled={busy} onChange={event=>setReplies(previous=>({...previous,[request.id]:event.target.value}))} placeholder="Only this member can read your reply"/></label><button disabled={busy||!replies[request.id]?.trim()} onClick={()=>void mutate(signal=>api.replySupport(request.id,replies[request.id],'resolved',signal),()=>setReplies(previous=>{const next={...previous};delete next[request.id];return next;}))}>Reply &amp; resolve</button></article>):<p>No private requests.</p>):null}
    {cursor?<button className="wide" disabled={busy||loading} onClick={()=>void older()}>Older {kind}</button>:null}
    {hiding?<Dialog title="Hide this clip?" onClose={()=>{if(!busy)setHiding(null);}}><p><strong>{hiding.title}</strong> will be hidden from Media. The server records your review note.</p><p className="caption">{notes[hiding.id]}</p>{error?<Notice>{error}</Notice>:null}<div className="dialog-actions"><button disabled={busy} onClick={()=>setHiding(null)}>Cancel</button><button className="danger" disabled={busy} onClick={()=>void review(hiding,true)}>{busy?'Saving decision…':'Hide clip'}</button></div></Dialog>:null}
  </main>;
}
