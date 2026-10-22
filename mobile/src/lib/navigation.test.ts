import {describe,expect,it} from 'vitest';
import {AppNavigation,parseRoute,routeHash,type AppRoute,type LeaveRoute} from './navigation';

const id='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const settle=()=>new Promise(resolve=>setTimeout(resolve,0));
function setup(hash='#media'){
  const listeners=new Map<string,Set<()=>void>>();
  const entries=[{state:null as unknown,hash}];let position=0,legacyNotices=0,leave:LeaveRoute|undefined;
  const location={hash};
  const emit=()=>{for(const type of ['popstate','hashchange'])listeners.get(type)?.forEach(callback=>callback());};
  const host={location,history:{
    get state(){return entries[position].state;},
    pushState(state:unknown,_title:string,url?:string|URL|null){entries.splice(position+1);entries.push({state,hash:String(url)});position++;location.hash=String(url);},
    replaceState(state:unknown,_title:string,url?:string|URL|null){entries[position]={state,hash:String(url)};location.hash=String(url);},
    go(delta:number){const target=position+delta;if(target<0||target>=entries.length)return;queueMicrotask(()=>{position=target;location.hash=entries[position].hash;emit();});},
  },addEventListener(type:string,listener:()=>void){if(!listeners.has(type))listeners.set(type,new Set());listeners.get(type)!.add(listener);},removeEventListener(type:string,listener:()=>void){listeners.get(type)?.delete(listener);}};
  const routes:AppRoute[]=[];
  const navigation=new AppNavigation(host,route=>routes.push(route),()=>{legacyNotices++;},next=>leave?.(next)??true);
  return{host,navigation,routes,entries,get legacyNotices(){return legacyNotices;},set leave(value:LeaveRoute|undefined){leave=value;},visitHash(hash:string){host.history.pushState(null,'',hash);emit();}};
}

describe('viewer-only mobile navigation',()=>{
  it('accepts app routes and a complete clip UUID, never a private URL or token',()=>{
    expect(parseRoute(`#watch/${id.toUpperCase()}`)).toEqual({page:'media',watchId:id});
    for(const hash of ['','#upload','#unknown','#watch/not-an-id',`#watch/${id}/extra`,'#access_token=private','#https://example.test/private?token=secret'])
      expect(parseRoute(hash)).toEqual({page:'media',watchId:null});
    expect(routeHash({page:'profile',watchId:id})).toBe('#profile');
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
  it('retains Profile Back/Forward entries without upload guards',async()=>{
    const test=setup();await test.navigation.navigate({page:'profile',watchId:null});
    test.host.history.go(-1);await settle();expect(test.routes.at(-1)?.page).toBe('media');
    test.host.history.go(1);await settle();expect(test.routes.at(-1)?.page).toBe('profile');
    expect(test.entries).toHaveLength(2);
  });
  it('recovers an initial legacy upload link with one explanatory notice and no extra history entry',()=>{
    const test=setup('#upload');
    expect(test.host.location.hash).toBe('#media');expect(test.legacyNotices).toBe(1);expect(test.entries).toHaveLength(1);
  });
  it('recovers legacy upload hashes entered while watching and normalizes the URL',async()=>{
    const test=setup();await test.navigation.navigate({page:'media',watchId:id});test.visitHash('#upload');
    expect(test.host.location.hash).toBe('#media');expect(test.routes.at(-1)).toEqual({page:'media',watchId:null});
    expect(test.legacyNotices).toBe(1);expect(test.entries).toHaveLength(3);
  });
  it('does not add history for repeated navigation and stops listening after disposal',async()=>{
    const test=setup();await test.navigation.navigate({page:'profile',watchId:null});
    await test.navigation.navigate({page:'profile',watchId:null});expect(test.entries).toHaveLength(2);
    test.navigation.dispose();test.host.history.go(-1);await settle();
    expect(test.routes.at(-1)?.page).toBe('profile');expect(await test.navigation.navigate({page:'media',watchId:null})).toBe(false);
  });
  it('normalizes an unknown initial fragment without showing upload guidance',()=>{
    const test=setup('#access_token=not-a-route');expect(test.host.location.hash).toBe('#media');
    expect(test.entries).toHaveLength(1);expect(test.legacyNotices).toBe(0);
  });
  it('keeps a saving profile at its exact history entry until the operation finishes',async()=>{
    const test=setup();await test.navigation.navigate({page:'profile',watchId:null});test.leave=()=>false;
    test.host.history.go(-1);await settle();
    expect(test.host.location.hash).toBe('#profile');expect(test.entries).toHaveLength(2);
    test.leave=undefined;test.host.history.go(-1);await settle();expect(test.routes.at(-1)?.page).toBe('media');
  });
  it('restores the Profile URL while a dirty Back decision is pending and supports keeping or discarding',async()=>{
    const test=setup();await test.navigation.navigate({page:'profile',watchId:null});
    let decide:(allow:boolean)=>void=()=>{};test.leave=()=>new Promise(resolve=>{decide=resolve;});
    test.host.history.go(-1);await settle();expect(test.host.location.hash).toBe('#profile');
    decide(false);await settle();expect(test.routes.at(-1)?.page).toBe('profile');expect(test.entries).toHaveLength(2);
    test.host.history.go(-1);await settle();decide(true);await settle();
    expect(test.host.location.hash).toBe('#media');expect(test.entries).toHaveLength(2);
    test.leave=undefined;test.host.history.go(1);await settle();expect(test.routes.at(-1)?.page).toBe('profile');
  });
  it('coalesces navigation while the unsaved decision is pending and never creates a canceled entry',async()=>{
    const test=setup();await test.navigation.navigate({page:'profile',watchId:null});
    let decide:(allow:boolean)=>void=()=>{},asked=0;test.leave=()=>{asked++;return new Promise(resolve=>{decide=resolve;});};
    const pending=test.navigation.navigate({page:'media',watchId:null});
    expect(await test.navigation.navigate({page:'media',watchId:id})).toBe(false);expect(asked).toBe(1);
    decide(false);expect(await pending).toBe(false);expect(test.entries).toHaveLength(2);expect(test.host.location.hash).toBe('#profile');
    test.leave=undefined;expect(await test.navigation.navigate({page:'media',watchId:null})).toBe(true);
  });
  it('does not commit an awaited navigation after its controller has been disposed',async()=>{
    const test=setup();let decide:(allow:boolean)=>void=()=>{};test.leave=()=>new Promise(resolve=>{decide=resolve;});
    const pending=test.navigation.navigate({page:'profile',watchId:null});test.navigation.dispose();decide(true);
    expect(await pending).toBe(false);expect(test.entries).toHaveLength(1);expect(test.host.location.hash).toBe('#media');
  });
});
