import {useEffect,useRef,useState} from 'react';
import type {Api} from '../lib/service';
import {messageFor} from '../lib/service';
import type {CommunityCursor,CommunityReport,CommunitySupportRequest} from '../lib/models';
import {Notice} from './Primitives';

export function Operations({api,onBack,onOpenClip}:{api:Api;onBack:()=>void;onOpenClip:(id:string)=>void}){
  const [kind,setKind]=useState<'reports'|'support'>('reports'),[reports,setReports]=useState<CommunityReport[]>([]),[requests,setRequests]=useState<CommunitySupportRequest[]>([]),[cursor,setCursor]=useState<CommunityCursor|null>(null),[error,setError]=useState(''),[retry,setRetry]=useState(0),[loading,setLoading]=useState(true),[busy,setBusy]=useState(false),[draft,setDraft]=useState('');
  const abort=useRef<AbortController|null>(null),lock=useRef(false);
  useEffect(()=>{
    const controller=new AbortController();abort.current=controller;setLoading(true);setError('');setDraft('');setCursor(null);setReports([]);setRequests([]);
    const loading=kind==='reports'?api.getOperatorReports(null,controller.signal).then(page=>{if(!controller.signal.aborted){setReports(page.items);setCursor(page.nextCursor);}}):api.getOperatorSupport(null,controller.signal).then(page=>{if(!controller.signal.aborted){setRequests(page.items);setCursor(page.nextCursor);}});
    void loading.catch(error=>{if(!controller.signal.aborted)setError(messageFor(error));}).finally(()=>{if(!controller.signal.aborted)setLoading(false);});
    return()=>{controller.abort();lock.current=false;};
  },[api,kind,retry]);
  async function mutate(action:(signal:AbortSignal)=>Promise<unknown>){
    const scope=abort.current;if(!scope||scope.signal.aborted||lock.current)return;lock.current=true;setBusy(true);setError('');
    try{await action(scope.signal);if(!scope.signal.aborted){setDraft('');setRetry(value=>value+1);}}
    catch(error){if(!scope.signal.aborted)setError(messageFor(error));}finally{if(!scope.signal.aborted){lock.current=false;setBusy(false);}}
  }
  async function older(){
    const scope=abort.current;if(!scope||scope.signal.aborted||!cursor||lock.current)return;lock.current=true;setBusy(true);setError('');
    try{if(kind==='reports'){const page=await api.getOperatorReports(cursor,scope.signal);if(!scope.signal.aborted){setReports(list=>[...list,...page.items]);setCursor(page.nextCursor);}}else{const page=await api.getOperatorSupport(cursor,scope.signal);if(!scope.signal.aborted){setRequests(list=>[...list,...page.items]);setCursor(page.nextCursor);}}}
    catch(error){if(!scope.signal.aborted)setError(messageFor(error));}finally{if(!scope.signal.aborted){lock.current=false;setBusy(false);}}
  }
  return <main className="profile-page operations-page"><button className="back" onClick={onBack} disabled={busy}>Back to usage</button><h1>Reports &amp; requests</h1><div className="profile-actions"><button disabled={busy||kind==='reports'} onClick={()=>setKind('reports')}>Reports</button><button disabled={busy||kind==='support'} onClick={()=>setKind('support')}>Private requests</button></div>
    {error?<Notice>{error}<button disabled={busy} onClick={()=>setRetry(value=>value+1)}>Refresh</button></Notice>:null}{loading?<p role="status">Loading {kind}…</p>:null}
    {!loading&&kind==='reports'?<><label>Review note<textarea maxLength={500} rows={3} value={draft} disabled={busy} onChange={event=>setDraft(event.target.value)} placeholder="Explain the review decision"/></label>{reports.length?reports.map(report=><article className="account-tool-section" key={report.id}><h2>{report.title}</h2><p>@{report.ownerUsername} · {report.status}{report.hidden?' · hidden':''}</p><p>{report.reason}</p><p className="caption">Reported by @{report.reporterUsername} · {new Date(report.createdAt).toLocaleString()}</p><div className="profile-actions"><button disabled={busy} onClick={()=>onOpenClip(report.clipId)}>View clip</button><button disabled={busy||!draft.trim()} onClick={()=>void mutate(signal=>api.reviewClip(report.clipId,'keep',draft,signal))}>Keep clip</button><button className="danger" disabled={busy||!draft.trim()} onClick={()=>void mutate(signal=>api.reviewClip(report.clipId,'hide',draft,signal))}>Hide clip</button></div></article>):<p>No reports to review.</p>}</>:null}
    {!loading&&kind==='support'?<><label>Reply<textarea maxLength={2000} rows={4} value={draft} disabled={busy} onChange={event=>setDraft(event.target.value)} placeholder="Write a private reply"/></label>{requests.length?requests.map(request=><article className="account-tool-section" key={request.id}><h2>{request.category} request</h2><p>@{request.username||'member'} · {request.status}</p><p>{request.body}</p>{request.reply?<p>Reply: {request.reply}</p>:null}<button disabled={busy||!draft.trim()} onClick={()=>void mutate(signal=>api.replySupport(request.id,draft,'resolved',signal))}>Reply &amp; resolve</button></article>):<p>No private requests.</p>}</>:null}
    {cursor?<button className="wide" disabled={busy||loading} onClick={()=>void older()}>Older {kind}</button>:null}
  </main>;
}
