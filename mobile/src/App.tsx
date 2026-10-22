import {lazy,Suspense,useEffect,useRef,useState} from 'react';
import {CirclePlus,Clapperboard,UserRound} from 'lucide-react';
import type {Api} from './lib/service';
import {messageFor} from './lib/service';
import {Auth} from './components/Auth';
import {Profile} from './components/Profile';
import {Feed} from './components/Feed';
import {Dialog,Notice} from './components/Primitives';
import {useAppNavigation,type AppPage,type AppRoute} from './lib/navigation';
const UploadClip=lazy(()=>import('./components/UploadClip'));

export function App({api,remember=false,onRemember=()=>{},fixture=false}:{api:Api;remember?:boolean;onRemember?:(value:boolean)=>void;fixture?:boolean}) {
  const [account,setAccount]=useState(()=>({api,user:api.user,profile:api.profile}));
  const [restoration,setRestoration]=useState(()=>({api,done:false}));
  const [profileLoad,setProfileLoad]=useState<{api:Api;userId:string|null;ready:boolean;error:string}>(()=>({api,userId:null,ready:false,error:''}));
  const [profileRetry,setProfileRetry]=useState(0);
  const [notice,setNotice]=useState(''),[feedVersion,setFeedVersion]=useState(0),[online,setOnline]=useState(navigator.onLine),[uploadBusy,setUploadBusy]=useState(false),[uploadDirty,setUploadDirty]=useState(false),[leaveUpload,setLeaveUpload]=useState(false);
  const leaveDecision=useRef<((allow:boolean)=>void)|null>(null);
  function confirmNavigation(_next:AppRoute):boolean|Promise<boolean>{
    if(page!=='upload'||!uploadDirty)return true;
    return new Promise(resolve=>{leaveDecision.current=resolve;setLeaveUpload(true);});
  }
  const navigation=useAppNavigation(uploadBusy,confirmNavigation);
  const {page,watchId}=navigation.route;
  function finishLeave(allow:boolean){setLeaveUpload(false);const resolve=leaveDecision.current;leaveDecision.current=null;resolve?.(allow);}
  useEffect(()=>()=>{leaveDecision.current?.(false);leaveDecision.current=null;},[]);
  // A replacement client never inherits the previous client's visible identity.
  const current=account.api===api?account:{api,user:api.user,profile:api.profile};
  const restoring=restoration.api!==api||!restoration.done;
  const userId=current.user?.id??null;
  const profileReady=!!userId&&profileLoad.api===api&&profileLoad.userId===userId&&profileLoad.ready&&current.profile?.id===userId;
  const profileError=profileLoad.api===api&&profileLoad.userId===userId?profileLoad.error:'';

  useEffect(()=>{
    let active=true;
    const abort=new AbortController();
    const snapshot=()=>{if(active)setAccount({api,user:api.user,profile:api.profile});};
    const unsubscribe=api.subscribe(snapshot);
    snapshot();setNotice('');
    void api.restoreSession(abort.signal).catch(error=>{if(active&&!abort.signal.aborted)setNotice(messageFor(error));})
      .finally(()=>{if(active&&!abort.signal.aborted){snapshot();setRestoration({api,done:true});}});
    return()=>{active=false;abort.abort();unsubscribe();};
  },[api]);

  // Profile loading follows the verified account, including a new OTP sign-in.
  // Auth may unmount as soon as verification succeeds; it never owns this request.
  useEffect(()=>{
    if(restoring||!userId)return;
    const abort=new AbortController();let active=true;
    setProfileLoad({api,userId,ready:false,error:''});
    void api.getProfile(abort.signal).then(()=>{
      if(active&&!abort.signal.aborted&&api.user?.id===userId){
        setAccount({api,user:api.user,profile:api.profile});
        setProfileLoad({api,userId,ready:true,error:''});
      }
    }).catch(error=>{if(active&&!abort.signal.aborted)setProfileLoad({api,userId,ready:false,error:messageFor(error)});});
    return()=>{active=false;abort.abort();};
  },[api,userId,restoring,profileRetry]);

  useEffect(()=>{const update=()=>setOnline(navigator.onLine);window.addEventListener('online',update);window.addEventListener('offline',update);return()=>{window.removeEventListener('online',update);window.removeEventListener('offline',update);};},[]);
  useEffect(()=>{const warn=(event:BeforeUnloadEvent)=>{event.preventDefault();};if(uploadBusy||uploadDirty)window.addEventListener('beforeunload',warn);return()=>window.removeEventListener('beforeunload',warn);},[uploadBusy,uploadDirty]);
  function navigate(next:AppPage){if(uploadBusy||(!!current.user&&!profileReady))return;void navigation.navigate({page:next,watchId:null}).then(moved=>{if(moved){setNotice('');window.scrollTo({top:0});}});}
  function watch(id:string|null){if(id===null)navigation.backToMedia();else void navigation.navigate({page:'media',watchId:id});}
  async function signOut(){if(uploadBusy)return;try{await api.signOut();void navigation.navigate({page:'media',watchId:null},{replace:true,bypassGuard:true});setAccount({api,user:null,profile:null});}catch(error){setNotice(messageFor(error));}}
  const complete=!!current.profile?.username;
  let content;
  if(restoring&&!restoration.done)content=<main className="loading" role="status">Opening Klipp…</main>;
  // Preserve an entered email while the Remember choice creates a new client.
  else if(!current.user)content=<Auth api={api} remember={remember} onRemember={onRemember} initializing={restoring}/>;
  else if(restoring||!profileReady)content=profileError?
    <main className="empty"><h1>Your profile could not load</h1><p>{profileError}</p><div className="dialog-actions"><button className="primary" onClick={()=>setProfileRetry(value=>value+1)}>Retry</button><button onClick={()=>void signOut()}>Sign out</button></div></main>:
    <main className="loading" role="status">Loading your profile…</main>;
  else if(page==='profile'||!complete)content=<Profile api={api} onSaved={()=>{setAccount({api,user:api.user,profile:api.profile});setFeedVersion(value=>value+1);}} onSignOut={()=>void signOut()}/>;
  else if(page==='upload')content=<Suspense fallback={<main className="loading">Opening upload…</main>}><UploadClip api={api} onCancel={()=>navigate('media')} onBusy={setUploadBusy} onDirty={setUploadDirty} onUploaded={()=>{setUploadBusy(false);setUploadDirty(false);setFeedVersion(value=>value+1);void navigation.navigate({page:'media',watchId:null},{replace:true,bypassGuard:true});setNotice('Clip uploaded. Your community can watch it now.');}}/></Suspense>;
  else content=<Feed api={api} version={feedVersion} onUpload={()=>navigate('upload')} watchId={watchId} onWatchChange={watch}/>;
  return <div className={page==='upload'?'app-shell is-upload':'app-shell'}><header className="app-header"><a className="brand" href="#media" onClick={event=>{event.preventDefault();navigate('media');}}><img src="./icons/klipp.svg" width="29" height="29" alt=""/>Klipp</a>{current.user?<button className="avatar" disabled={uploadBusy||!profileReady} onClick={()=>navigate('profile')} aria-label="Your profile">{(current.profile?.displayName||'K')[0].toUpperCase()}</button>:null}</header>{fixture?<div className="fixture-banner">Local test data · no online changes</div>:null}{!online?<Notice>You’re offline. Connect to the internet to use Media.</Notice>:null}{notice?<Notice onClose={()=>setNotice('')}>{notice}</Notice>:null}{content}{current.user?<nav className="bottom-nav" aria-label="Main navigation"><button aria-current={page==='media'?'page':undefined} disabled={uploadBusy||!profileReady} onClick={()=>navigate('media')}><Clapperboard/>Media</button><button aria-current={page==='upload'?'page':undefined} disabled={uploadBusy||!profileReady} onClick={()=>navigate('upload')}><CirclePlus/>Upload</button><button aria-current={page==='profile'?'page':undefined} disabled={uploadBusy||!profileReady} onClick={()=>navigate('profile')}><UserRound/>Profile</button></nav>:null}<footer className="app-footer">Klipp · <a href="https://github.com/HappyHamster135/Klipp/blob/main/docs/PRIVACY.md" target="_blank" rel="noopener noreferrer">Privacy</a></footer>{leaveUpload?<Dialog title="Discard this upload?" onClose={()=>finishLeave(false)}><p>Your selected section and clip details will be discarded. Your original video stays on this device.</p><div className="dialog-actions"><button onClick={()=>finishLeave(false)}>Keep editing</button><button className="danger" onClick={()=>finishLeave(true)}>Discard upload</button></div></Dialog>:null}</div>;
}
