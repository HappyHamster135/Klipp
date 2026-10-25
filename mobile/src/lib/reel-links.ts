import type {Api} from './service';
import {cancelled} from './models';

const capacity=3,ttl=30_000,expiryMargin=15_000;
type LinkApi=Pick<Api,'getPlaybackUrl'|'user'>;
interface Consumer {resolve:(url:string)=>void;reject:(error:unknown)=>void;signal?:AbortSignal;abort?:()=>void}
interface Entry {
  id:string;actor:string;controller:AbortController;consumers:Set<Consumer>;
  fresh:boolean;invalid:boolean;pending:boolean;url:string;expiresAt:number;
}

/** A small in-memory lease cache. It requests URLs, never video bytes. */
export class ReelLinks {
  private readonly entries=new Map<string,Entry>();
  private actor:string|null;
  private disposed=false;

  constructor(private readonly api:LinkApi){this.actor=api.user?.id??null;}

  get(id:string,signal?:AbortSignal,fresh=false):Promise<string> {
    if(!this.currentActor()||signal?.aborted)return Promise.reject(cancelled());
    this.purgeExpired();
    let entry=this.entries.get(id);
    // Concurrent retries can share one new request, but a retry must never use
    // the failed cached lease or an older speculative request.
    if(fresh&&entry&&(!entry.pending||!entry.fresh)){this.invalidate(entry);entry=undefined;}
    if(entry&&!entry.pending){
      this.touch(entry);const cached=entry;
      // A ready hit still crosses an async boundary. Recheck the consumer and
      // account before exposing a lease invalidated by logout or a fresh retry.
      return Promise.resolve().then(()=>{if(signal?.aborted||!this.usable(cached))throw cancelled();return cached.url;});
    }
    if(!entry){
      while(this.entries.size>=capacity){const oldest=this.entries.values().next().value;if(oldest)this.invalidate(oldest);}
      entry={id,actor:this.actor!,controller:new AbortController(),consumers:new Set(),fresh,invalid:false,pending:true,url:'',expiresAt:0};
      this.entries.set(id,entry);
      const started=entry;
      // Let the consumer attach before a synchronous fixture response or an
      // immediate cancellation. The actual API still owns authentication.
      void Promise.resolve().then(async()=>{
        if(!this.usable(started))return;
        try{this.complete(started,await this.api.getPlaybackUrl(started.id,started.controller.signal));}
        catch(error){this.fail(started,error);}
      });
    }else this.touch(entry);
    return this.consume(entry,signal);
  }

  retain(ids:readonly string[]):void {
    if(!this.currentActor())return;
    this.purgeExpired();
    const keep=new Set([...new Set(ids)].slice(0,capacity));
    for(const entry of this.entries.values())if(!keep.has(entry.id))this.invalidate(entry);
  }

  dispose():void {
    if(this.disposed)return;
    this.disposed=true;
    this.clear();
  }

  private currentActor():boolean {
    if(this.disposed)return false;
    const actor=this.api.user?.id??null;
    if(actor!==this.actor){this.actor=actor;this.clear();}
    return actor!==null;
  }

  private usable(entry:Entry):boolean {
    return this.currentActor()&&!entry.invalid&&entry.actor===this.actor&&this.entries.get(entry.id)===entry;
  }

  private consume(entry:Entry,signal?:AbortSignal):Promise<string> {
    return new Promise((resolve,reject)=>{
      if(signal?.aborted||!this.usable(entry)){reject(cancelled());return;}
      const consumer:Consumer={resolve,reject,signal};
      consumer.abort=()=>{
        if(!entry.consumers.has(consumer))return;
        this.detach(entry,consumer);reject(cancelled());
        if(entry.pending&&!entry.consumers.size)this.invalidate(entry);
      };
      entry.consumers.add(consumer);
      signal?.addEventListener('abort',consumer.abort,{once:true});
      if(signal?.aborted)consumer.abort();
    });
  }

  private complete(entry:Entry,url:string):void {
    if(!this.usable(entry))return;
    entry.pending=false;entry.url=url;entry.expiresAt=this.expiry(url);
    this.touch(entry);
    for(const consumer of [...entry.consumers]){
      this.detach(entry,consumer);
      if(consumer.signal?.aborted)consumer.reject(cancelled());else consumer.resolve(url);
    }
    // A nearly expired lease may serve its existing caller, but is never reused.
    if(entry.expiresAt<=Date.now())this.entries.delete(entry.id);
  }

  private fail(entry:Entry,error:unknown):void {
    if(!this.usable(entry))return;
    this.entries.delete(entry.id);entry.invalid=true;entry.pending=false;
    for(const consumer of [...entry.consumers]){this.detach(entry,consumer);consumer.reject(error);}
  }

  private detach(entry:Entry,consumer:Consumer):void {
    if(consumer.abort)consumer.signal?.removeEventListener('abort',consumer.abort);
    entry.consumers.delete(consumer);
  }

  private invalidate(entry:Entry):void {
    if(entry.invalid)return;
    entry.invalid=true;
    if(this.entries.get(entry.id)===entry)this.entries.delete(entry.id);
    entry.controller.abort();
    for(const consumer of [...entry.consumers]){this.detach(entry,consumer);consumer.reject(cancelled());}
    entry.url='';
  }

  private clear():void {for(const entry of [...this.entries.values()])this.invalidate(entry);}
  private purgeExpired():void {for(const entry of this.entries.values())if(!entry.pending&&entry.expiresAt<=Date.now())this.invalidate(entry);}
  private touch(entry:Entry):void {if(this.entries.get(entry.id)===entry){this.entries.delete(entry.id);this.entries.set(entry.id,entry);}}

  private expiry(source:string):number {
    const now=Date.now();
    try{
      const url=new URL(source),expires=url.searchParams.get('expires');
      if(expires!==null){
        if(!/^\d{1,12}$/.test(expires))return now;
        return Math.min(now+ttl,Number(expires)*1000-expiryMargin);
      }
      return now+ttl;
    }catch{return now;}
  }
}
