import {describe,expect,it} from 'vitest';
import {AppNavigation,parseRoute,routeHash,type AppRoute,type LeaveRoute} from './navigation';

const id='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const settle=()=>new Promise(resolve=>setTimeout(resolve,0));
function setup(hash='#media'){
  const listeners=new Map<string,Set<()=>void>>();
  const entries=[{state:null as unknown,hash}];let position=0;
  const location={hash};
  const host={location,history:{
    get state(){return entries[position].state;},
    pushState(state:unknown,_title:string,url?:string|URL|null){entries.splice(position+1);entries.push({state,hash:String(url)});position++;location.hash=String(url);},
    replaceState(state:unknown,_title:string,url?:string|URL|null){entries[position]={state,hash:String(url)};location.hash=String(url);},
    go(delta:number){const target=position+delta;if(target<0||target>=entries.length)return;queueMicrotask(()=>{position=target;location.hash=entries[position].hash;for(const type of ['popstate','hashchange'])listeners.get(type)?.forEach(callback=>callback());});},
  },addEventListener(type:string,listener:()=>void){if(!listeners.has(type))listeners.set(type,new Set());listeners.get(type)!.add(listener);},removeEventListener(type:string,listener:()=>void){listeners.get(type)?.delete(listener);}};
  let blocked=false,leave:LeaveRoute|undefined;
  const routes:AppRoute[]=[];
  const navigation=new AppNavigation(host,route=>routes.push(route),{blocked:()=>blocked,canLeave:()=>leave});
  return{host,navigation,routes,entries,set blocked(value:boolean){blocked=value;},set leave(value:LeaveRoute|undefined){leave=value;}};
}

describe('mobile navigation',()=>{
  it('accepts only app routes and a complete clip UUID, never a private URL or token',()=>{
    expect(parseRoute(`#watch/${id.toUpperCase()}`)).toEqual({page:'media',watchId:id});
    for(const hash of ['','#unknown','#watch/not-an-id',`#watch/${id}/extra`,'#access_token=private','#https://example.test/private?token=secret'])
      expect(parseRoute(hash)).toEqual({page:'media',watchId:null});
    expect(routeHash({page:'upload',watchId:id})).toBe('#upload');
    expect(routeHash({page:'media',watchId:'https://example.test/secret'})).toBe('#media');
  });
  it('returns from Watch with real Back/Forward entries instead of pushing another Media entry',async()=>{
    const test=setup();await test.navigation.navigate({page:'media',watchId:id});
    test.navigation.backToMedia();await settle();
    expect(test.host.location.hash).toBe('#media');expect(test.routes.at(-1)?.watchId).toBeNull();
    expect(test.entries).toHaveLength(2);
    test.host.history.go(1);await settle();expect(test.routes.at(-1)?.watchId).toBe(id);
  });
  it('replaces a deep-linked Watch entry when Back to clips has no known Media entry',async()=>{
    const test=setup(`#watch/${id}`);test.navigation.backToMedia();await settle();
    expect(test.host.location.hash).toBe('#media');expect(test.entries).toHaveLength(1);
  });
  it('keeps a busy upload on its exact entry and allows Back once work is finished',async()=>{
    const test=setup();await test.navigation.navigate({page:'upload',watchId:null});test.blocked=true;
    test.host.history.go(-1);await settle();
    expect(test.host.location.hash).toBe('#upload');expect(test.routes.at(-1)?.page).toBe('upload');expect(test.entries).toHaveLength(2);
    expect(await test.navigation.navigate({page:'profile',watchId:null})).toBe(false);
    test.blocked=false;test.host.history.go(-1);await settle();expect(test.routes.at(-1)?.page).toBe('media');
  });
  it('restores the upload URL while an unsaved Back confirmation is open, then honors cancel or discard',async()=>{
    const test=setup();await test.navigation.navigate({page:'upload',watchId:null});
    let decide:(allow:boolean)=>void=()=>{};
    test.leave=()=>new Promise(resolve=>{decide=resolve;});
    test.host.history.go(-1);await settle();expect(test.host.location.hash).toBe('#upload');
    decide(false);await settle();expect(test.routes.at(-1)?.page).toBe('upload');
    test.host.history.go(-1);await settle();decide(true);await settle();
    expect(test.host.location.hash).toBe('#media');expect(test.routes.at(-1)?.page).toBe('media');expect(test.entries).toHaveLength(2);
  });
  it('does not add history for canceled navigation and stops listening after disposal',async()=>{
    const test=setup();test.leave=()=>false;
    expect(await test.navigation.navigate({page:'profile',watchId:null})).toBe(false);expect(test.entries).toHaveLength(1);
    test.leave=undefined;await test.navigation.navigate({page:'upload',watchId:null});test.navigation.dispose();
    test.host.history.go(-1);await settle();expect(test.routes.at(-1)?.page).toBe('upload');
  });
  it('finishes a successful upload without a stale busy or discard guard and replaces its entry',async()=>{
    const test=setup();await test.navigation.navigate({page:'upload',watchId:null});
    test.blocked=true;let asked=false;test.leave=()=>{asked=true;return false;};
    expect(await test.navigation.navigate({page:'media',watchId:null},{replace:true,bypassGuard:true})).toBe(true);
    expect(asked).toBe(false);expect(test.host.location.hash).toBe('#media');expect(test.entries).toHaveLength(2);
  });
  it('normalizes an unknown initial fragment before it can enter application navigation',()=>{
    const test=setup('#access_token=not-a-route');expect(test.host.location.hash).toBe('#media');
    expect(test.entries).toHaveLength(1);
  });
});
