import {useEffect,useRef,useState,type FormEvent} from 'react';
import type {Api} from '../lib/service';
import {messageFor} from '../lib/service';
import type {CommunityBlockedUser} from '../lib/models';
import {Notice} from './Primitives';

export interface ProfileEditingState {dirty:boolean;busy:boolean}
type Draft={username:string;displayName:string;bio:string};
type Scope={active:boolean;userId:string;controller:AbortController};
const normalized=(draft:Draft):Draft=>({username:draft.username.trim().toLowerCase(),displayName:draft.displayName.trim(),bio:draft.bio.trim()});
const matches=(left:Draft,right:Draft)=>left.username===right.username&&left.displayName===right.displayName&&left.bio===right.bio;
const limited=(value:string,length:number)=>[...value].slice(0,length).join('');

export function Profile({api,onSaved,onSignOut,onEditingChange,onUsage,onHelp}:{api:Api;onSaved:()=>void;onSignOut:()=>void;onEditingChange?:(state:ProfileEditingState)=>void;onUsage?:()=>void;onHelp?:()=>void}) {
  const[draft,setDraft]=useState<Draft>(()=>({username:api.profile?.username??'',displayName:api.profile?.displayName??'',bio:api.profile?.bio??''}));
  const[busy,setBusy]=useState<'save'|'blocked'|'unblock'|null>(null),[notice,setNotice]=useState(''),[blocked,setBlocked]=useState<CommunityBlockedUser[]|null>(null);
  const draftRef=useRef(draft),baseline=useRef(normalized(draft)),requestBusy=useRef(false),operation=useRef<'save'|'blocked'|'unblock'|null>(null),lifetime=useRef<Scope|null>(null);
  const dirty=!matches(normalized(draft),baseline.current);
  function report(){onEditingChange?.({dirty:!matches(normalized(draftRef.current),baseline.current),busy:operation.current==='save'});}
  useEffect(()=>{
    const scope:Scope={active:true,userId:api.user?.id??'',controller:new AbortController()};
    lifetime.current=scope;report();
    return()=>{scope.active=false;scope.controller.abort();requestBusy.current=false;operation.current=null;onEditingChange?.({dirty:false,busy:false});};
  },[api,onEditingChange]);
  function current(scope:Scope){return lifetime.current===scope&&scope.active&&!scope.controller.signal.aborted&&api.user?.id===scope.userId;}
  function begin(action:'save'|'blocked'|'unblock'){
    const scope=lifetime.current;if(!scope||!current(scope)||requestBusy.current)return null;
    requestBusy.current=true;operation.current=action;setBusy(action);setNotice('');report();return scope;
  }
  function finish(scope:Scope){if(current(scope)){requestBusy.current=false;operation.current=null;setBusy(null);report();}}
  function failed(scope:Scope,error:unknown){if(current(scope)&&!(error instanceof Error&&error.name==='AbortError'))setNotice(messageFor(error));}
  function edit(field:keyof Draft,value:string){
    if(operation.current==='save')return;
    const next={...draftRef.current,[field]:value};draftRef.current=next;setDraft(next);setNotice('');report();
  }
  async function save(event:FormEvent){
    event.preventDefault();if(!dirty)return;
    const scope=begin('save');if(!scope)return;
    try{
      const saved=await api.saveProfile(normalized(draftRef.current),scope.controller.signal);
      if(!current(scope))return;
      const accepted={username:saved.username??'',displayName:saved.displayName,bio:saved.bio};
      baseline.current=normalized(accepted);draftRef.current=accepted;setDraft(accepted);
      setNotice('Profile saved.');report();onSaved();
    }catch(error){failed(scope,error);}finally{finish(scope);}
  }
  async function manageBlocked(){
    const scope=begin('blocked');if(!scope)return;
    try{const users=await api.getBlockedUsers(scope.controller.signal);if(current(scope))setBlocked(users);}
    catch(error){failed(scope,error);}finally{finish(scope);}
  }
  async function unblock(id:string){
    const scope=begin('unblock');if(!scope)return;
    try{await api.setBlocked(id,false,scope.controller.signal);if(current(scope))setBlocked(list=>list?.filter(member=>member.userId!==id)??[]);}
    catch(error){failed(scope,error);}finally{finish(scope);}
  }
  return <main className="profile-page">
    <h1>Your profile</h1>
    {!api.profile?.username?<p>Choose a username to join the conversation.</p>:null}
    {notice?<Notice onClose={()=>setNotice('')}>{notice}</Notice>:null}
    <form onSubmit={save}>
      <div><label>Username<input value={draft.username} disabled={busy==='save'} onChange={event=>edit('username',event.target.value.toLowerCase())} pattern="[a-z][a-z0-9_]{2,23}" minLength={3} maxLength={24} required autoCapitalize="none" autoCorrect="off" spellCheck={false} autoComplete="username" aria-describedby="profile-username-help" title="Start with a letter. Use 3–24 lowercase letters, numbers or underscores."/></label><small id="profile-username-help">Start with a letter. Use 3–24 lowercase letters, numbers or underscores.</small></div>
      <div><label>Display name<input value={draft.displayName} disabled={busy==='save'} onChange={event=>edit('displayName',limited(event.target.value,40))} maxLength={80} required autoComplete="nickname" aria-describedby="profile-name-help"/></label><small id="profile-name-help">Shown beside your clips and comments. {[...draft.displayName].length}/40 characters</small></div>
      <div><label>Bio<textarea value={draft.bio} disabled={busy==='save'} onChange={event=>edit('bio',limited(event.target.value,160))} maxLength={320} rows={3} aria-describedby="profile-bio-help"/></label><small id="profile-bio-help">{[...draft.bio].length}/160 characters</small></div>
      <button className="primary" disabled={!!busy||!dirty}>{busy==='save'?'Saving…':'Save profile'}</button>
    </form>
    <div className="profile-actions"><button disabled={!!busy} onClick={()=>void manageBlocked()}>{busy==='blocked'?'Loading…':'Blocked users'}</button><button disabled={busy==='save'} onClick={onSignOut}>Sign out</button></div>
    {onUsage||onHelp?<section className="profile-links" aria-label="Account tools">{onUsage?<button disabled={busy==='save'} onClick={onUsage}><span>Storage &amp; usage</span><span className="caption">Your clips and beta capacity</span></button>:null}{onHelp?<button disabled={busy==='save'} onClick={onHelp}><span>Help &amp; requests</span><span className="caption">Private support and account deletion</span></button>:null}</section>:null}
    {blocked?<section className="blocked"><h2>Blocked users</h2>{blocked.length?blocked.map(user=><div key={user.userId}><span>@{user.username||user.displayName}</span><button disabled={!!busy} onClick={()=>void unblock(user.userId)}>Unblock</button></div>):<p>No blocked users.</p>}</section>:null}
    <section className="install-help"><h2>Keep Klipp on your home screen</h2><p>iPhone: open in Safari, tap Share, then Add to Home Screen. Android: open the browser menu and choose Install app or Add to home screen.</p></section>
  </main>;
}
