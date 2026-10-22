import {useEffect,useRef,useState,type FormEvent} from 'react';
import {ArrowLeft,Mail} from 'lucide-react';
import type {Api} from '../lib/service';
import {messageFor} from '../lib/service';
import {Notice} from './Primitives';

type AuthScope={controller:AbortController;active:boolean;kind:'send'|'verify'|null};
export function Auth({api,remember,onRemember,initializing=false}:{api:Api;remember:boolean;onRemember:(value:boolean)=>void;initializing?:boolean}) {
  const [email,setEmail]=useState(''),[code,setCode]=useState(''),[sent,setSent]=useState(false),[create,setCreate]=useState(false),[pending,setPending]=useState(false),[error,setError]=useState(''),[cooldown,setCooldown]=useState(0);
  const lifetime=useRef<AuthScope|null>(null),requestBusy=useRef(false);
  const busy=pending||initializing;
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
  useEffect(()=>{if(!cooldown)return;const timer=setTimeout(()=>setCooldown(value=>value-1),1000);return()=>clearTimeout(timer);},[cooldown]);
  async function run(kind:'send'|'verify'){
    const scope=lifetime.current;
    if(!scope?.active||requestBusy.current||initializing)return;
    requestBusy.current=true;scope.kind=kind;setPending(true);setError('');
    try{
      if(kind==='verify')await api.verifyEmailCode(email.trim(),code.trim(),scope.controller.signal);
      else{
        await api.sendEmailCode(email.trim(),create,scope.controller.signal);
        if(scope.active&&!scope.controller.signal.aborted){setSent(true);setCooldown(60);}
      }
    }catch(error){if(scope.active&&!scope.controller.signal.aborted)setError(messageFor(error));}
    finally{if(lifetime.current===scope){requestBusy.current=false;scope.kind=null;if(scope.active)setPending(false);}}
  }
  function submit(event:FormEvent){event.preventDefault();void run(sent?'verify':'send');}
  return <main className="auth-page"><img src="./icons/klipp.svg" alt="" width="42" height="42"/><h1>{sent?'Check your email':create?'Create your Klipp account':'Sign in to Klipp'}</h1><p>{sent?`Enter the code sent to ${email}.`:'Your clips and your community, across devices.'}</p>{error?<Notice>{error}</Notice>:null}<form onSubmit={submit}>{sent?<label>Email code<input inputMode="numeric" autoComplete="one-time-code" autoFocus value={code} disabled={busy} onChange={event=>setCode(event.target.value.replace(/\D/g,'').slice(0,10))} required minLength={6} maxLength={10}/></label>:<><label>Email<input type="email" autoComplete="email" value={email} disabled={busy} onChange={event=>setEmail(event.target.value)} placeholder="you@example.com" maxLength={254} required/></label><label className="check"><input type="checkbox" checked={remember} disabled={busy} onChange={event=>onRemember(event.target.checked)}/>Remember me on this device</label></>}<button className="primary wide" disabled={busy}>{busy?'Please wait…':sent?'Continue':<><Mail size={18}/>{create?'Send confirmation code':'Send sign-in code'}</>}</button></form>{sent?<div className="auth-links"><button disabled={busy||cooldown>0} onClick={()=>void run('send')}>{cooldown?`Resend in ${cooldown}s`:'Resend code'}</button><button disabled={busy} onClick={()=>{setSent(false);setCode('');setError('');}}><ArrowLeft size={16}/>Change email</button></div>:<button disabled={busy} className="text-button" onClick={()=>{setCreate(!create);setError('');}}>{create?'Already have an account? Sign in':'New to Klipp? Create an account'}</button>}<p className="caption">An account is only needed for online Media. Recording and editing in Klipp on your PC always work without one.</p></main>;
}
