import {lazy,Suspense,useCallback,useEffect,useRef,useState} from 'react';
import {Clapperboard,UserRound} from 'lucide-react';
import type {Api} from './lib/service';
import {messageFor} from './lib/service';
import {Auth} from './components/Auth';
import {Profile,type ProfileEditingState} from './components/Profile';
import {Feed} from './components/Feed';
import {Dialog,Notice} from './components/Primitives';
import {useAppNavigation,type AppPage} from './lib/navigation';
import type {CommunityOperationsCapabilities} from './lib/models';

const UsageDashboard=lazy(()=>import('./components/Usage').then(module=>({default:module.UsageDashboard})));
const Operations=lazy(()=>import('./components/Operations').then(module=>({default:module.Operations})));
const Support=lazy(()=>import('./components/Support').then(module=>({default:module.Support})));

export function App({api,remember=false,onRemember=()=>{},fixture=false,operationsEnabled=false}:{api:Api;remember?:boolean;onRemember?:(value:boolean)=>void;fixture?:boolean;operationsEnabled?:boolean}) {
  const [account,setAccount]=useState(()=>({api,user:api.user,profile:api.profile}));
  const [restoration,setRestoration]=useState(()=>({api,done:false,pending:true,error:'',attempt:0})),[restoreRetry,setRestoreRetry]=useState(0);
  const restoreLock=useRef(false);
  const [profileLoad,setProfileLoad]=useState<{api:Api;userId:string|null;ready:boolean;error:string}>(()=>({api,userId:null,ready:false,error:''}));
  const [profileRetry,setProfileRetry]=useState(0);
  const [capabilityRetry,setCapabilityRetry]=useState(0);
  const [capability,setCapability]=useState<{api:Api;userId:string|null;value:CommunityOperationsCapabilities|null;error:string;pending:boolean}>(()=>({api,userId:null,value:null,error:'',pending:false}));
  const [notice,setNotice]=useState(''),[feedVersion,setFeedVersion]=useState(0),[online,setOnline]=useState(navigator.onLine);
  const [profileEditing,setProfileEditing]=useState<ProfileEditingState>({dirty:false,busy:false}),[leaveProfile,setLeaveProfile]=useState(false);
  const [leaveKind,setLeaveKind]=useState<'profile'|'message'>('profile');
  const editingPage=useRef<'profile'|'message'>('profile');
  const editing=useRef(profileEditing),decision=useRef<{promise:Promise<boolean>;resolve:(allow:boolean)=>void}|null>(null),signingOut=useRef(false);
  const updateEditing=useCallback((state:ProfileEditingState)=>{editing.current=state;setProfileEditing(previous=>previous.dirty===state.dirty&&previous.busy===state.busy?previous:state);},[]);
  const finishLeave=useCallback((allow:boolean)=>{const pending=decision.current;decision.current=null;if(pending){setLeaveProfile(false);pending.resolve(allow);}},[]);
  const confirmLeave=useCallback(()=>{
    if(editing.current.busy)return false;
    if(!editing.current.dirty)return true;
    if(decision.current)return decision.current.promise;
    setLeaveKind(editingPage.current);let resolve:(allow:boolean)=>void=()=>{};
    const promise=new Promise<boolean>(done=>{resolve=done;});decision.current={promise,resolve};setLeaveProfile(true);return promise;
  },[]);
  const navigation=useAppNavigation(confirmLeave);
  const {page,watchId}=navigation.route;
  editingPage.current=page==='help'||page==='manage'?'message':'profile';
  // A replacement client never inherits the previous client's visible identity.
  const current=account.api===api?account:{api,user:api.user,profile:api.profile};
  const restoring=restoration.api!==api||restoration.pending||restoration.attempt!==restoreRetry;
  const restoreError=restoration.api===api?restoration.error:'';
  const userId=current.user?.id??null;
  const identity=useRef({api,userId});identity.current={api,userId};
  const profileReady=!!userId&&profileLoad.api===api&&profileLoad.userId===userId&&profileLoad.ready&&current.profile?.id===userId;
  const profileError=profileLoad.api===api&&profileLoad.userId===userId?profileLoad.error:'';
  const capabilityCurrent=capability.api===api&&capability.userId===userId;
  const toolsEnabled=operationsEnabled&&capabilityCurrent&&capability.value?.enabled===true;
  const operator=toolsEnabled&&capability.value?.operator===true;
  const canDelete=toolsEnabled&&capability.value?.accountDeletion===true;
  useEffect(()=>{
    if(!operationsEnabled||restoring||!userId)return;
    const abort=new AbortController();setCapability({api,userId,value:null,error:'',pending:true});
    void api.getOperationsCapabilities(abort.signal).then(value=>{
      if(!abort.signal.aborted&&api.user?.id===userId)setCapability({api,userId,value,error:'',pending:false});
    }).catch(error=>{if(!abort.signal.aborted)setCapability({api,userId,value:null,error:messageFor(error),pending:false});});
    return()=>abort.abort();
  },[api,userId,restoring,operationsEnabled,capabilityRetry]);
  useEffect(()=>{
    updateEditing({dirty:false,busy:false});finishLeave(false);
    if(userId)setRestoration(previous=>previous.api===api&&previous.error?{...previous,error:''}:previous);
  },[api,userId,updateEditing,finishLeave]);
  useEffect(()=>()=>{decision.current?.resolve(false);decision.current=null;},[]);

  useEffect(()=>{
    let active=true;
    const abort=new AbortController();
    const snapshot=()=>{if(active)setAccount({api,user:api.user,profile:api.profile});};
    const unsubscribe=api.subscribe(snapshot);
    snapshot();setNotice('');
    restoreLock.current=true;
    setRestoration(previous=>({api,done:previous.done,pending:true,error:previous.api===api?previous.error:'',attempt:restoreRetry}));
    void api.restoreSession(abort.signal).then(()=>{
      if(active&&!abort.signal.aborted){snapshot();setRestoration({api,done:true,pending:false,error:'',attempt:restoreRetry});}
    }).catch(error=>{
      if(active&&!abort.signal.aborted){snapshot();setRestoration({api,done:true,pending:false,error:messageFor(error),attempt:restoreRetry});}
    }).finally(()=>{if(active)restoreLock.current=false;});
    return()=>{active=false;abort.abort();unsubscribe();};
  },[api,restoreRetry]);

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

  useEffect(()=>{
    if(profileReady&&!current.profile?.username&&page!=='profile'&&page!=='help'&&page!=='usage')void navigation.navigate({page:'profile',watchId:null},{replace:true,bypassGuard:true});
  },[profileReady,current.profile?.username,page,navigation.navigate]);
  useEffect(()=>{
    if((!operationsEnabled||capabilityCurrent&&!capability.pending&&!toolsEnabled||page==='manage'&&toolsEnabled&&!operator)&&page!=='media'&&page!=='profile')void navigation.navigate({page:'profile',watchId:null},{replace:true,bypassGuard:true});
  },[operationsEnabled,capabilityCurrent,capability.pending,toolsEnabled,operator,page,navigation.navigate]);

  useEffect(()=>{const update=()=>setOnline(navigator.onLine);window.addEventListener('online',update);window.addEventListener('offline',update);return()=>{window.removeEventListener('online',update);window.removeEventListener('offline',update);};},[]);
  useEffect(()=>{
    if(!profileEditing.dirty&&!profileEditing.busy)return;
    const warn=(event:BeforeUnloadEvent)=>{event.preventDefault();event.returnValue='';};
    window.addEventListener('beforeunload',warn);return()=>window.removeEventListener('beforeunload',warn);
  },[profileEditing.dirty,profileEditing.busy]);
  function navigate(next:AppPage){if(!!current.user&&!profileReady)return;void navigation.navigate({page:next,watchId:null}).then(moved=>{if(moved){setNotice('');window.scrollTo({top:0});}});}
  function watch(id:string|null){if(id===null)navigation.backToMedia();else void navigation.navigate({page:'media',watchId:id});}
  function retryRestore(){if(restoreLock.current||api.user)return;restoreLock.current=true;setRestoreRetry(value=>value+1);}
  async function signOut(){
    if(signingOut.current)return;
    signingOut.current=true;const owner=userId;
    try{
      if(!await confirmLeave()||identity.current.api!==api||identity.current.userId!==owner)return;
      await api.signOut();void navigation.navigate({page:'media',watchId:null},{replace:true,bypassGuard:true});setAccount({api,user:null,profile:null});
      setRestoration(previous=>previous.api===api?{...previous,error:''}:previous);
    }catch(error){if(identity.current.api===api)setNotice(messageFor(error));}finally{signingOut.current=false;}
  }
  const complete=!!current.profile?.username;
  let content;
  if(restoring&&!restoration.done)content=<main className="loading" role="status">Opening Klipp…</main>;
  // Preserve an entered email while the Remember choice creates a new client.
  else if(!current.user)content=<Auth api={api} remember={remember} onRemember={onRemember} initializing={restoring} restoreError={restoreError} onRetryRestore={retryRestore}/>;
  else if(restoring||!profileReady)content=profileError?
    <main className="empty"><h1>Your profile could not load</h1><p>{profileError}</p><div className="dialog-actions"><button className="primary" onClick={()=>setProfileRetry(value=>value+1)}>Retry</button><button onClick={()=>void signOut()}>Sign out</button></div></main>:
    <main className="loading" role="status">Loading your profile…</main>;
  else if(toolsEnabled&&page==='help')content=<Support key={userId} api={api} canDelete={canDelete} onBack={()=>navigate('profile')} onDeleted={()=>{setAccount({api,user:api.user,profile:api.profile});setNotice('Your Klipp account and shared content have been deleted. Your original recordings stay on your devices.');void navigation.navigate({page:'media',watchId:null},{replace:true,bypassGuard:true});}} onEditingChange={updateEditing}/>;
  else if(page==='profile'||!complete&&page!=='usage'||!toolsEnabled&&page!=='media')content=<Profile key={userId} api={api} onSaved={()=>{setAccount({api,user:api.user,profile:api.profile});setFeedVersion(value=>value+1);}} onSignOut={()=>void signOut()} onEditingChange={updateEditing} onUsage={toolsEnabled?()=>navigate('usage'):undefined} onHelp={toolsEnabled?()=>navigate('help'):undefined} toolsError={operationsEnabled&&capabilityCurrent?capability.error:''} onRetryTools={()=>setCapabilityRetry(value=>value+1)}/>;
  else if(toolsEnabled&&page==='usage')content=<UsageDashboard key={userId} api={api} onBack={()=>navigate('profile')} onManage={()=>navigate('manage')} onManageClips={()=>navigate('media')}/>;
  else if(operator&&page==='manage')content=<Operations key={userId} api={api} onBack={()=>navigate('usage')} onOpenClip={id=>watch(id)} onEditingChange={updateEditing}/>;
  else content=<Feed api={api} version={feedVersion} watchId={watchId} onWatchChange={watch}/>;
  return <div className="app-shell"><header className="app-header"><a className="brand" href="#media" onClick={event=>{event.preventDefault();navigate('media');}}><img src="./icons/klipp.svg" width="29" height="29" alt=""/>Klipp</a>{current.user?<button className="avatar" disabled={!profileReady||profileEditing.busy} onClick={()=>navigate('profile')} aria-label="Your profile">{(current.profile?.displayName||'K')[0].toUpperCase()}</button>:null}</header>{fixture?<div className="fixture-banner">Local test data · no online changes</div>:null}{!online?<Notice>You’re offline. Connect to the internet to use Media.</Notice>:null}{navigation.uploadNotice?<Notice onClose={navigation.dismissUploadNotice}>Upload clips from Klipp on your PC.</Notice>:null}{notice?<Notice onClose={()=>setNotice('')}>{notice}</Notice>:null}<Suspense fallback={<main className="loading" role="status">Opening account tools…</main>}>{content}</Suspense>{current.user?<nav className="bottom-nav" aria-label="Main navigation"><button aria-current={page==='media'?'page':undefined} disabled={!profileReady||profileEditing.busy} onClick={()=>navigate('media')}><Clapperboard/>Media</button><button aria-current={page!=='media'?'page':undefined} disabled={!profileReady||profileEditing.busy} onClick={()=>navigate('profile')}><UserRound/>Profile</button></nav>:null}<footer className="app-footer">Klipp · <a href="https://github.com/HappyHamster135/Klipp/blob/main/docs/PRIVACY.md" target="_blank" rel="noopener noreferrer">Privacy</a></footer>{leaveProfile?<Dialog title={leaveKind==='profile'?'Discard profile changes?':'Discard your message?'} onClose={()=>finishLeave(false)}><p>Your changes haven’t been saved. Discard them to continue.</p><div className="dialog-actions"><button onClick={()=>finishLeave(false)}>Keep editing</button><button className="danger" onClick={()=>finishLeave(true)}>Discard changes</button></div></Dialog>:null}</div>;
}
