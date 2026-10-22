import {useCallback,useEffect,useRef,useState} from 'react';

export type AppPage='media'|'upload'|'profile';
export interface AppRoute {page:AppPage;watchId:string|null}
export type LeaveRoute=(next:AppRoute)=>boolean|Promise<boolean>;
const home:AppRoute={page:'media',watchId:null};
const clipId=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Fragments contain navigation only: never interpret a token, URL or arbitrary path. */
export function parseRoute(hash:string):AppRoute {
  if(hash==='#upload')return{page:'upload',watchId:null};
  if(hash==='#profile')return{page:'profile',watchId:null};
  const id=hash.startsWith('#watch/')?hash.slice(7):'';
  return clipId.test(id)?{page:'media',watchId:id.toLowerCase()}:{...home};
}
export function routeHash(route:AppRoute):string {
  if(route.page==='upload'||route.page==='profile')return`#${route.page}`;
  return route.watchId&&clipId.test(route.watchId)?`#watch/${route.watchId.toLowerCase()}`:'#media';
}
interface NavigationHost {
  location:Pick<Location,'hash'>;
  history:Pick<History,'state'|'pushState'|'replaceState'|'go'>;
  addEventListener(type:string,listener:()=>void):void;
  removeEventListener(type:string,listener:()=>void):void;
}
interface NavigationOptions {blocked:()=>boolean;canLeave:()=>LeaveRoute|undefined}
interface NavigateOptions {replace?:boolean;bypassGuard?:boolean}
function entryIndex(state:unknown):number|null {
  if(!state||typeof state!=='object')return null;
  const value=(state as {klippNavigation?:{version?:unknown;index?:unknown}}).klippNavigation;
  return value?.version===1&&Number.isSafeInteger(value.index)&&Number(value.index)>=0?Number(value.index):null;
}

/** Own history entries allow a canceled Back gesture to return to its exact entry. */
export class AppNavigation {
  private route:AppRoute;
  private index:number;
  private entries=new Map<number,string>();
  private restoring:{resolve:()=>void;promise:Promise<void>}|null=null;
  private permittedIndex:number|null=null;
  private pending=false;
  private disposed=false;
  constructor(private host:NavigationHost,private changed:(route:AppRoute)=>void,private options:NavigationOptions){
    this.route=parseRoute(host.location.hash);this.index=entryIndex(host.history.state)??0;
    this.entries.set(this.index,routeHash(this.route));this.write(this.route,true);
    host.addEventListener('popstate',this.onHistory);host.addEventListener('hashchange',this.onHistory);
  }
  dispose(){this.disposed=true;this.restoring?.resolve();this.host.removeEventListener('popstate',this.onHistory);this.host.removeEventListener('hashchange',this.onHistory);}
  async navigate(next:AppRoute,options:NavigateOptions={}):Promise<boolean>{
    next=parseRoute(routeHash(next));
    if(this.pending||this.disposed)return false;
    if(routeHash(next)===routeHash(this.route))return true;
    if(!options.bypassGuard){
      if(this.options.blocked())return false;
      this.pending=true;
      try{if(!await(this.options.canLeave()?.(next)??true))return false;}
      finally{this.pending=false;}
      if(this.disposed||this.options.blocked())return false;
    }
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
  private restore(from:number|null):Promise<void>{
    if(this.restoring)return this.restoring.promise;
    if(from===null||from===this.index){this.write(this.route,true);return Promise.resolve();}
    let resolve=()=>{};const promise=new Promise<void>(done=>{resolve=done;});
    this.restoring={resolve,promise};this.host.history.go(this.index-from);return promise;
  }
  private onHistory=()=>{void this.readHistory();};
  private async readHistory(){
    if(this.disposed)return;
    const next=parseRoute(this.host.location.hash),nextIndex=entryIndex(this.host.history.state);
    if(this.restoring){
      if(nextIndex===this.index){const restoring=this.restoring;this.restoring=null;restoring.resolve();}
      return;
    }
    if(nextIndex===this.index&&routeHash(next)===routeHash(this.route))return;
    if(this.permittedIndex!==null&&this.permittedIndex===nextIndex){this.permittedIndex=null;this.accept(next,nextIndex);return;}
    if(this.pending||this.options.blocked()){void this.restore(nextIndex);return;}
    const decision=this.options.canLeave()?.(next)??true;
    if(typeof decision==='boolean'){
      if(decision)this.accept(next,nextIndex);else void this.restore(nextIndex);
      return;
    }
    this.pending=true;
    const restored=this.restore(nextIndex);
    try{
      const allow=await decision;await restored;
      if(!allow||this.disposed||this.options.blocked())return;
      if(nextIndex!==null&&nextIndex!==this.index){this.permittedIndex=nextIndex;this.host.history.go(nextIndex-this.index);}
      else{this.index++;this.write(next,false);this.route=next;this.changed(next);}
    }finally{this.pending=false;}
  }
  private accept(next:AppRoute,index:number|null){
    this.index=index??this.index+1;this.route=next;this.write(next,true);this.changed(next);
  }
}

export function useAppNavigation(blocked:boolean,canLeave?:LeaveRoute){
  const[route,setRoute]=useState(()=>parseRoute(window.location.hash));
  const latest=useRef({blocked,canLeave});latest.current={blocked,canLeave};
  const navigation=useRef<AppNavigation|null>(null);
  useEffect(()=>{
    const controller=new AppNavigation(window,setRoute,{blocked:()=>latest.current.blocked,canLeave:()=>latest.current.canLeave});
    navigation.current=controller;return()=>{controller.dispose();navigation.current=null;};
  },[]);
  const navigate=useCallback((next:AppRoute,options?:NavigateOptions)=>navigation.current?.navigate(next,options)??Promise.resolve(false),[]);
  const backToMedia=useCallback(()=>navigation.current?.backToMedia(),[]);
  return{route,navigate,backToMedia};
}
