import {afterEach,describe,expect,it,vi} from 'vitest';
import {ReelLinks} from './reel-links';
import type {Api} from './service';

type LinkApi={user:Api['user'];getPlaybackUrl:Api['getPlaybackUrl']};
function deferred<T>(){let resolve!:(value:T)=>void,reject!:(error:unknown)=>void;const promise=new Promise<T>((yes,no)=>{resolve=yes;reject=no;});return{promise,resolve,reject};}
function fixture(){
  const calls:{id:string;signal?:AbortSignal;result:ReturnType<typeof deferred<string>>}[]=[];
  const api:LinkApi={user:{id:'owner',email:'owner@example.invalid'},getPlaybackUrl:vi.fn((id:string,signal?:AbortSignal)=>{const result=deferred<string>();calls.push({id,signal,result});return result.promise;})};
  return{api,calls,cache:new ReelLinks(api)};
}
const tick=()=>Promise.resolve();
const url=(id:string)=>`https://media.example.invalid/v1/assets/owner/${id}/video?token=fixture`;
afterEach(()=>{vi.useRealTimers();});

describe('bounded reel playback leases',()=>{
  it('deduplicates a warmup and active consumer without requesting media bytes',async()=>{
    const{cache,calls,api}=fixture(),warm=cache.get('one'),active=cache.get('one');
    await tick();expect(calls).toHaveLength(1);calls[0].result.resolve(url('one'));
    expect(await warm).toBe(url('one'));expect(await active).toBe(url('one'));expect(await cache.get('one')).toBe(url('one'));
    expect(api.getPlaybackUrl).toHaveBeenCalledTimes(1);cache.dispose();
  });
  it('cancels only the aborted consumer while another still needs the request',async()=>{
    const{cache,calls}=fixture(),a=new AbortController(),b=new AbortController();
    const first=cache.get('one',a.signal),firstCheck=expect(first).rejects.toMatchObject({name:'AbortError'}),second=cache.get('one',b.signal);
    await tick();a.abort();await firstCheck;expect(calls[0].signal?.aborted).toBe(false);
    calls[0].result.resolve(url('one'));expect(await second).toBe(url('one'));cache.dispose();
  });
  it('aborts the API when the last consumer leaves and ignores an abort-ignoring result',async()=>{
    const{cache,calls}=fixture(),abort=new AbortController(),first=cache.get('one',abort.signal),checked=expect(first).rejects.toMatchObject({name:'AbortError'});
    await tick();abort.abort();await checked;expect(calls[0].signal?.aborted).toBe(true);calls[0].result.resolve(url('old'));await tick();await tick();
    const next=cache.get('one');await tick();expect(calls).toHaveLength(2);calls[1].result.resolve(url('new'));expect(await next).toBe(url('new'));cache.dispose();
  });
  it('does not call the API for an already-aborted or immediately canceled consumer',async()=>{
    const{cache,api}=fixture(),abort=new AbortController();abort.abort();
    await expect(cache.get('one',abort.signal)).rejects.toMatchObject({name:'AbortError'});
    const later=new AbortController(),pending=cache.get('one',later.signal),checked=expect(pending).rejects.toMatchObject({name:'AbortError'});later.abort();await checked;await tick();
    expect(api.getPlaybackUrl).not.toHaveBeenCalled();cache.dispose();
  });
  it('expires cached leases after thirty seconds',async()=>{
    vi.useFakeTimers();vi.setSystemTime(1_000_000);const{cache,calls}=fixture();const one=cache.get('one');await tick();calls[0].result.resolve(url('one'));await one;
    vi.advanceTimersByTime(29_999);expect(await cache.get('one')).toBe(url('one'));expect(calls).toHaveLength(1);
    vi.advanceTimersByTime(1);const again=cache.get('one');await tick();expect(calls).toHaveLength(2);calls[1].result.resolve(url('two'));await again;cache.dispose();
  });
  it('caps home expiry fifteen seconds before the signed expiry',async()=>{
    vi.useFakeTimers();vi.setSystemTime(1_000_000);const{cache,calls}=fixture();const first=cache.get('one');await tick();
    calls[0].result.resolve('https://media.example.invalid/v1/assets/owner/one/video?expires=1020&signature=fixture');await first;
    vi.advanceTimersByTime(4_999);await cache.get('one');expect(calls).toHaveLength(1);vi.advanceTimersByTime(1);
    const second=cache.get('one');await tick();expect(calls).toHaveLength(2);calls[1].result.resolve(url('new'));await second;cache.dispose();
  });
  it('never caches an already-near-expiry or malformed expiry lease',async()=>{
    vi.useFakeTimers();vi.setSystemTime(1_000_000);const{cache,calls}=fixture();
    for(const expires of ['1010','invalid']){const first=cache.get('one');await tick();calls.at(-1)!.result.resolve(`https://media.example.invalid/v1/assets/owner/one/video?expires=${expires}`);await first;}
    const third=cache.get('one');await tick();expect(calls).toHaveLength(3);calls.at(-1)!.result.resolve(url('new'));await third;cache.dispose();
  });
  it('retains only the requested three ids and does not fetch on retain',async()=>{
    const{cache,calls}=fixture();for(const id of ['a','b','c']){const p=cache.get(id);await tick();calls.at(-1)!.result.resolve(url(id));await p;}
    cache.retain(['b','b','c','d','a']);expect(calls).toHaveLength(3);await cache.get('b');await cache.get('c');
    const removed=cache.get('a');await tick();expect(calls).toHaveLength(4);calls.at(-1)!.result.resolve(url('new-a'));await removed;cache.dispose();
  });
  it('bounds even concurrent pending requests to three and evicts least-recently-used leases',async()=>{
    const{cache,calls}=fixture(),first=cache.get('a'),checked=expect(first).rejects.toMatchObject({name:'AbortError'});
    const b=cache.get('b'),c=cache.get('c');await tick();const d=cache.get('d');await checked;await tick();expect(calls[0].signal?.aborted).toBe(true);
    for(const call of calls.slice(1))call.result.resolve(url(call.id));await Promise.all([b,c,d]);calls[0].result.resolve(url('stale'));await tick();await cache.get('b');
    const e=cache.get('e');await tick();calls.at(-1)!.result.resolve(url('e'));await e;
    const evicted=cache.get('c');await tick();expect(calls).toHaveLength(6);calls.at(-1)!.result.resolve(url('new-c'));await evicted;cache.dispose();
  });
  it('aborts a pending lease removed by retain',async()=>{
    const{cache,calls}=fixture(),first=cache.get('one'),checked=expect(first).rejects.toMatchObject({name:'AbortError'});await tick();cache.retain(['other']);await checked;
    expect(calls[0].signal?.aborted).toBe(true);calls[0].result.resolve(url('late'));cache.dispose();
  });
  it('fresh retry bypasses a cached lease and deduplicates concurrent fresh callers',async()=>{
    const{cache,calls}=fixture(),first=cache.get('one');await tick();calls[0].result.resolve(url('old'));await first;
    const retry=cache.get('one',undefined,true),another=cache.get('one',undefined,true);await tick();expect(calls).toHaveLength(2);
    calls[1].result.resolve(url('fresh'));expect(await retry).toBe(url('fresh'));expect(await another).toBe(url('fresh'));cache.dispose();
  });
  it('fresh retry invalidates an older speculative request',async()=>{
    const{cache,calls}=fixture(),warm=cache.get('one'),checked=expect(warm).rejects.toMatchObject({name:'AbortError'});await tick();
    const fresh=cache.get('one',undefined,true);await checked;await tick();expect(calls[0].signal?.aborted).toBe(true);
    calls[0].result.resolve(url('old'));calls[1].result.resolve(url('new'));expect(await fresh).toBe(url('new'));expect(await cache.get('one')).toBe(url('new'));cache.dispose();
  });
  it('does not cache API errors',async()=>{
    const{cache,calls}=fixture(),first=cache.get('one'),checked=expect(first).rejects.toThrow('offline');await tick();calls[0].result.reject(new Error('offline'));await checked;
    const second=cache.get('one');await tick();calls[1].result.resolve(url('new'));expect(await second).toBe(url('new'));cache.dispose();
  });
  it('clears and aborts pending work when the actor changes on another operation',async()=>{
    const{cache,calls,api}=fixture(),first=cache.get('one'),checked=expect(first).rejects.toMatchObject({name:'AbortError'});await tick();
    api.user={id:'other',email:'other@example.invalid'};cache.retain(['one']);await checked;expect(calls[0].signal?.aborted).toBe(true);
    const second=cache.get('one');await tick();calls[0].result.resolve(url('old'));calls[1].result.resolve(url('new'));expect(await second).toBe(url('new'));cache.dispose();
  });
  it('rejects an old actor result even when the API ignores cancellation',async()=>{
    const{cache,calls,api}=fixture(),first=cache.get('one'),checked=expect(first).rejects.toMatchObject({name:'AbortError'});await tick();
    api.user={id:'other',email:'other@example.invalid'};calls[0].result.resolve(url('old'));await checked;expect(calls[0].signal?.aborted).toBe(true);cache.dispose();
  });
  it('clears a ready lease on logout and will not request links while signed out',async()=>{
    const{cache,calls,api}=fixture(),first=cache.get('one');await tick();calls[0].result.resolve(url('old'));await first;
    api.user=null;await expect(cache.get('one')).rejects.toMatchObject({name:'AbortError'});expect(calls).toHaveLength(1);
    api.user={id:'owner',email:'owner@example.invalid'};const second=cache.get('one');await tick();calls[1].result.resolve(url('new'));expect(await second).toBe(url('new'));cache.dispose();
  });
  it('fences a cached hit when the actor logs out before its continuation',async()=>{
    const{cache,calls,api}=fixture(),first=cache.get('one');await tick();calls[0].result.resolve(url('old'));await first;
    const cached=cache.get('one'),checked=expect(cached).rejects.toMatchObject({name:'AbortError'});api.user=null;await checked;
    api.user={id:'owner',email:'owner@example.invalid'};const next=cache.get('one');await tick();expect(calls).toHaveLength(2);calls[1].result.resolve(url('new'));expect(await next).toBe(url('new'));cache.dispose();
  });
  it('cancels an unconsumed cached hit without discarding the ready lease',async()=>{
    const{cache,calls}=fixture(),first=cache.get('one');await tick();calls[0].result.resolve(url('one'));await first;
    const abort=new AbortController(),cached=cache.get('one',abort.signal),checked=expect(cached).rejects.toMatchObject({name:'AbortError'});abort.abort();await checked;
    expect(await cache.get('one')).toBe(url('one'));expect(calls).toHaveLength(1);cache.dispose();
  });
  it('fences a cached hit invalidated by a fresh retry before its continuation',async()=>{
    const{cache,calls}=fixture(),first=cache.get('one');await tick();calls[0].result.resolve(url('old'));await first;
    const cached=cache.get('one'),checked=expect(cached).rejects.toMatchObject({name:'AbortError'}),fresh=cache.get('one',undefined,true);await checked;await tick();
    expect(calls).toHaveLength(2);calls[1].result.resolve(url('new'));expect(await fresh).toBe(url('new'));expect(await cache.get('one')).toBe(url('new'));cache.dispose();
  });
  it('disposes idempotently and fences late responses and later requests',async()=>{
    const{cache,calls,api}=fixture(),first=cache.get('one'),checked=expect(first).rejects.toMatchObject({name:'AbortError'});await tick();cache.dispose();cache.dispose();await checked;
    expect(calls[0].signal?.aborted).toBe(true);calls[0].result.resolve(url('late'));await tick();cache.retain(['one']);await expect(cache.get('one')).rejects.toMatchObject({name:'AbortError'});expect(api.getPlaybackUrl).toHaveBeenCalledTimes(1);
  });
});
