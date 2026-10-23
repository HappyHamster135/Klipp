import {useEffect,useRef,useState,type FormEvent} from 'react';
import type {Api} from '../lib/service';
import {messageFor} from '../lib/service';
import {CommunityError,type CommunitySupportRequest,type SupportCategory} from '../lib/models';
import type {ProfileEditingState} from './Profile';
import {Dialog,Notice} from './Primitives';

export function Support({api,onBack,onDeleted,onEditingChange,canDelete=false}:{api:Api;onBack:()=>void;onDeleted:()=>void;canDelete?:boolean;onEditingChange?:(state:ProfileEditingState)=>void}){
  const [requests,setRequests]=useState<CommunitySupportRequest[]>([]),[body,setBody]=useState(''),[category,setCategory]=useState<SupportCategory>('help'),[error,setError]=useState(''),[retry,setRetry]=useState(0),[loading,setLoading]=useState(true),[busy,setBusy]=useState(false),[deleting,setDeleting]=useState(false),[confirmation,setConfirmation]=useState('');
  const scope=useRef<AbortController|null>(null),lock=useRef(false),deleted=useRef(false);
  const [pendingDeletion,setPendingDeletion]=useState(false);
  useEffect(()=>{const abort=new AbortController();scope.current=abort;return()=>{abort.abort();lock.current=false;};},[api]);
  useEffect(()=>{onEditingChange?.({dirty:body.trim().length>0,busy});},[body,busy,onEditingChange]);
  useEffect(()=>()=>onEditingChange?.({dirty:false,busy:false}),[onEditingChange]);
  useEffect(()=>{
    const abort=new AbortController();setLoading(true);setError('');
    void api.getSupportRequests(abort.signal).then(value=>{if(!abort.signal.aborted)setRequests(value);}).catch(error=>{if(!abort.signal.aborted)setError(messageFor(error));}).finally(()=>{if(!abort.signal.aborted)setLoading(false);});
    return()=>abort.abort();
  },[api,retry]);
  async function submit(event:FormEvent){
    event.preventDefault();const abort=scope.current;if(!abort||abort.signal.aborted||lock.current||body.trim().length<10)return;lock.current=true;setBusy(true);setError('');
    try{const saved=await api.submitSupport(category,body,abort.signal);if(!abort.signal.aborted){setRequests(list=>[saved,...list]);setBody('');}}
    catch(error){if(!abort.signal.aborted)setError(messageFor(error));}finally{if(!abort.signal.aborted){lock.current=false;setBusy(false);}}
  }
  async function remove(){
    const abort=scope.current;if(!abort||abort.signal.aborted||lock.current)return;lock.current=true;setBusy(true);setError('');
    try{await api.deleteAccount(confirmation,abort.signal);if(!abort.signal.aborted){deleted.current=true;onDeleted();}}
    catch(error){if(!abort.signal.aborted){setError(messageFor(error));if(error instanceof CommunityError&&error.code==='deletion_pending')setPendingDeletion(true);if(error instanceof CommunityError&&error.code==='confirmation_required')setConfirmation('');}}finally{if(!abort.signal.aborted&&!deleted.current){lock.current=false;setBusy(false);}}
  }
  const expected=api.profile?.username??'DELETE';
  return <main className="profile-page operations-page"><button className="back" disabled={busy} onClick={onBack}>Back to profile</button><h1>Help &amp; requests</h1><p>Messages here are private to you and Klipp’s moderators.</p>{error?<Notice>{error}<button disabled={busy} onClick={()=>setRetry(value=>value+1)}>Refresh requests</button></Notice>:null}
    <form onSubmit={submit}><label>Topic<select value={category} disabled={busy} onChange={event=>setCategory(event.target.value as SupportCategory)}><option value="help">Help</option><option value="privacy">Privacy</option><option value="deletion">Deletion request</option></select></label><label>Message<textarea rows={5} minLength={10} maxLength={2000} required disabled={busy} value={body} onChange={event=>setBody(event.target.value)}/></label><button className="primary" disabled={busy||body.trim().length<10}>{busy?'Please wait…':'Send private request'}</button></form>
    <section className="account-tool-section"><h2>Your requests</h2>{loading?<p role="status">Loading requests…</p>:null}{requests.map(request=><article className="account-tool-section" key={request.id}><strong>{request.category} · {request.status}</strong><p>{request.body}</p>{request.reply?<p>Reply: {request.reply}</p>:<p className="caption">Awaiting a reply</p>}</article>)}{!loading&&!error&&!requests.length?<p>No requests yet.</p>:null}</section>
    {canDelete?<section className="account-tool-section"><h2>{pendingDeletion?'Account cleanup in progress':'Delete account'}</h2><p>{pendingDeletion?'Cleanup has started. Your sign-in stays available so you can retry and finish removing the remaining shared files and account.':'Delete your account, shared clips and interactions. Original recordings and projects on your devices stay there.'}</p><button className="danger" disabled={busy} onClick={()=>{setConfirmation('');setError('');setDeleting(true);}}>{pendingDeletion?'Retry account deletion':'Delete my account'}</button></section>:null}
    {deleting?<Dialog title="Delete your Klipp account?" onClose={()=>{if(!busy)setDeleting(false);}}><p>This is permanent. Type <strong>{expected}</strong> to confirm.</p><label>Username<input value={confirmation} disabled={busy} autoCapitalize="none" autoComplete="off" onChange={event=>setConfirmation(event.target.value)}/></label>{error?<Notice>{error}</Notice>:null}<div className="dialog-actions"><button disabled={busy} onClick={()=>setDeleting(false)}>Cancel</button><button className="danger" disabled={busy||confirmation!==expected} onClick={()=>void remove()}>{busy?'Deleting…':'Delete account'}</button></div></Dialog>:null}
  </main>;
}
