import {useCallback,useEffect,useRef,useState} from 'react';

export type AppPage='media'|'profile';
export interface AppRoute {page:AppPage;watchId:string|null}
const home:AppRoute={page:'media',watchId:null};
const clipId=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Fragments contain navigation only: never interpret a token, URL or arbitrary path. */
export function parseRoute(hash:string):AppRoute {
  if(hash==='#profile')return{page:'profile',watchId:null};
  const id=hash.startsWith('#watch/')?hash.slice(7):'';
  return clipId.test(id)?{page:'media',watchId:id.toLowerCase()}:{...home};
}
export function routeHash(route:AppRoute):string {
  if(route.page==='profile')return'#profile';
  return route.watchId&&clipId.test(route.watchId)?`#watch/${route.watchId.toLowerCase()}`:'#media';
}
interface NavigationHost {
  location:Pick<Location,'hash'>;
  history:Pick<History,'state'|'pushState'|'replaceState'|'go'>;
  addEventListener(type:string,listener:()=>void):void;
  removeEventListener(type:string,listener:()=>void):void;
}
interface NavigateOptions {replace?:boolean}
function entryIndex(state:unknown):number|null {
  if(!state||typeof state!=='object')return null;
  const value=(state as {klippNavigation?:{version?:unknown;index?:unknown}}).klippNavigation;
  return value?.version===1&&Number.isSafeInteger(value.index)&&Number(value.index)>=0?Number(value.index):null;
}

/** Keep Watch/Profile Back and Forward on their original browser history entries. */
export class AppNavigation {
  private route:AppRoute;
  private index:number;
  private entries=new Map<number,string>();
  private disposed=false;
  constructor(private host:NavigationHost,private changed:(route:AppRoute)=>void,private legacyUpload?:()=>void){
    if(host.location.hash==='#upload')legacyUpload?.();
    this.route=parseRoute(host.location.hash);this.index=entryIndex(host.history.state)??0;
    this.write(this.route,true);
    host.addEventListener('popstate',this.onHistory);host.addEventListener('hashchange',this.onHistory);
  }
  dispose(){this.disposed=true;this.host.removeEventListener('popstate',this.onHistory);this.host.removeEventListener('hashchange',this.onHistory);}
  async navigate(next:AppRoute,options:NavigateOptions={}):Promise<boolean>{
    if(this.disposed)return false;
    next=parseRoute(routeHash(next));
    if(routeHash(next)===routeHash(this.route))return true;
    if(!options.replace)this.index++;
    this.write(next,!!options.replace);this.route=next;this.changed(next);return true;
  }
  backToMedia(){
    if(this.route.watchId&&this.entries.get(this.index-1)==='#media')this.host.history.go(-1);
    else void this.navigate(home,{replace:true});
  }
  private write(route:AppRoute,replace:boolean){
    const state={klippNavigation:{version:1,index:this.index}};
    this.host.history[replace?'replaceState':'pushState'](state,'',routeHash(route));
    this.entries.set(this.index,routeHash(route));
  }
  private onHistory=()=>{
    if(this.disposed)return;
    if(this.host.location.hash==='#upload')this.legacyUpload?.();
    const next=parseRoute(this.host.location.hash),index=entryIndex(this.host.history.state);
    if(index===this.index&&routeHash(next)===routeHash(this.route)){
      if(this.host.location.hash!==routeHash(next))this.write(next,true);
      return;
    }
    this.index=index??this.index+1;this.route=next;this.write(next,true);this.changed(next);
  };
}

export function useAppNavigation(){
  const[route,setRoute]=useState(()=>parseRoute(window.location.hash));
  const[uploadNotice,setUploadNotice]=useState(()=>window.location.hash==='#upload');
  const navigation=useRef<AppNavigation|null>(null);
  useEffect(()=>{
    const controller=new AppNavigation(window,setRoute,()=>setUploadNotice(true));
    navigation.current=controller;return()=>{controller.dispose();navigation.current=null;};
  },[]);
  const navigate=useCallback(async(next:AppRoute,options?:NavigateOptions)=>{
    const moved=await(navigation.current?.navigate(next,options)??Promise.resolve(false));
    if(moved)setUploadNotice(false);return moved;
  },[]);
  const backToMedia=useCallback(()=>navigation.current?.backToMedia(),[]);
  const dismissUploadNotice=useCallback(()=>setUploadNotice(false),[]);
  return{route,navigate,backToMedia,uploadNotice,dismissUploadNotice};
}
