import {useEffect,useRef,useState,type FormEvent} from 'react';
import {ArrowLeft,Mail} from 'lucide-react';
import type {Api} from '../lib/service';
import {messageFor} from '../lib/service';
import {CommunityError} from '../lib/models';
import {Notice} from './Primitives';

type AuthScope={controller:AbortController;active:boolean;kind:'send'|'verify'|null};
export function Auth({api,remember,onRemember,initializing=false}:{api:Api;remember:boolean;onRemember:(value:boolean)=>void;initializing?:boolean}) {
  const [email,setEmail]=useState(''),[code,setCode]=useState(''),[sent,setSent]=useState(false),[create,setCreate]=useState(false),[pending,setPending]=useState(false),[error,setError]=useState('');
  const [retry,setRetry]=useState({email:'',until:0}),[clock,setClock]=useState(()=>Date.now());
  const lifetime=useRef<AuthScope|null>(null),requestBusy=useRef(false);
  const busy=pending||initializing;
  const cooldown=email.trim().toLowerCase()===retry.email?Math.max(0,Math.ceil((retry.until-clock)/1000)):0;
  useEffect(()=>{
    const scope:AuthScope={controller:new AbortController(),active:true,kind:null};
    lifetime.current=scope;requestBusy.current=false;setPending(false);
    return()=>{
      scope.active=false;
      // The verified identity is published just before the Promise settles.
      // A successful navigation must not roll that accepted sign-in back.
      if(!(scope.kind==='verify'&&api.user))scope.controller.abort();
    };
  },[api]);
  useEffect(()=>{
    if(!retry.until)return;
    let timer:ReturnType<typeof setTimeout>|undefined;
    const update=()=>{clearTimeout(timer);const now=Date.now();setClock(now);if(now<retry.until)timer=setTimeout(update,Math.min(1000,retry.until-now));};
    // Phone browsers pause timers while the user opens Mail. Recalculate the
    // actual remaining time as soon as this page becomes active again.
    update();
    window.addEventListener('focus',update);document.addEventListener('visibilitychange',update);
    return()=>{clearTimeout(timer);window.removeEventListener('focus',update);document.removeEventListener('visibilitychange',update);};
  },[retry.until]);
  function startCooldown(){const now=Date.now();setClock(now);setRetry({email:email.trim().toLowerCase(),until:now+60000});}
  async function run(kind:'send'|'verify'){
    const scope=lifetime.current;
    if(!scope?.active||requestBusy.current||initializing||(kind==='send'&&cooldown>0))return;
    requestBusy.current=true;scope.kind=kind;setPending(true);setError('');
    try{
      if(kind==='verify')await api.verifyEmailCode(email.trim(),code.trim(),scope.controller.signal);
      else{
        await api.sendEmailCode(email.trim(),create,scope.controller.signal);
        if(scope.active&&!scope.controller.signal.aborted){setEmail(email.trim());setCode('');setSent(true);startCooldown();}
      }
    }catch(error){if(scope.active&&!scope.controller.signal.aborted){setError(messageFor(error));if(kind==='send'&&error instanceof CommunityError&&error.code==='rate_limited')startCooldown();}}
    finally{if(lifetime.current===scope){requestBusy.current=false;scope.kind=null;if(scope.active)setPending(false);}}
  }
  function submit(event:FormEvent){event.preventDefault();void run(sent?'verify':'send');}
  return <main className="auth-page">
    <img src="./icons/klipp.svg" alt="" width="42" height="42"/>
    <h1>{sent?'Check your email':create?'Create your Klipp account':'Sign in to Klipp'}</h1>
    <p>{sent?`Enter the code sent to ${email}.`:'Your clips and your community, across devices.'}</p>
    {error?<Notice>{error}</Notice>:null}
    <form onSubmit={submit}>
      {sent?<label>Email code<input inputMode="numeric" autoComplete="one-time-code" autoFocus value={code} disabled={busy} onChange={event=>setCode(event.target.value.replace(/\D/g,'').slice(0,10))} required minLength={6} maxLength={10} pattern="[0-9]{6,10}"/></label>:<>
        <label>Email<input type="email" autoComplete="email" autoCapitalize="none" autoCorrect="off" spellCheck={false} value={email} disabled={busy} onChange={event=>setEmail(event.target.value)} placeholder="you@example.com" maxLength={254} required/></label>
        <label className="check"><input type="checkbox" checked={remember} disabled={busy} onChange={event=>onRemember(event.target.checked)}/>Remember me on this device</label>
      </>}
      <button className="primary wide" disabled={busy||(!sent&&cooldown>0)}>{busy?'Please wait…':sent?'Continue':cooldown?`Send code in ${cooldown}s`:<><Mail size={18}/>{create?'Send confirmation code':'Send sign-in code'}</>}</button>
    </form>
    {sent?<div className="auth-links">
      <button type="button" disabled={busy||cooldown>0} onClick={()=>void run('send')}>{cooldown?`Resend in ${cooldown}s`:'Resend code'}</button>
      <button type="button" disabled={busy} onClick={()=>{setSent(false);setCode('');setError('');}}><ArrowLeft size={16}/>Change email</button>
    </div>:<button type="button" disabled={busy} className="text-button" onClick={()=>{setCreate(!create);setError('');}}>{create?'Already have an account? Sign in':'New to Klipp? Create an account'}</button>}
    <p className="caption">An account is only needed for online Media. Recording and editing in Klipp on your PC always work without one.</p>
  </main>;
}
