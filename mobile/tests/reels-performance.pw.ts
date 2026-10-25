import {test,expect,type Locator,type Page} from '@playwright/test';
import {capturePlaybackEvidence} from './playback-evidence';
import {createMediaAudit,type MediaAudit} from './reels-media-audit';

const first='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',second='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',third='cccccccc-cccc-4ccc-8ccc-cccccccccccc';
interface LinkCall{id:string;actor:string|null;started:number;finished:number|null;aborted:boolean;issued:number}
interface PerfAudit{calls:LinkCall[];starts:number[];maxDecoders:number;connection:EventTarget&{saveData:boolean;effectiveType:string};swapActor:()=>void}
declare global{interface Window{__reelPerf:PerfAudit}}
const health=new WeakMap<Page,string[]>(),runs=new WeakMap<Page,string>();
let mediaAudit:MediaAudit;
test.beforeAll(async()=>{mediaAudit=await createMediaAudit();});
test.afterAll(async()=>{await mediaAudit?.close();});
const mediaIds=(page:Page)=>mediaAudit.requests.filter(request=>request.run===runs.get(page)).map(request=>request.id);
test.beforeEach(async({page})=>{
  const errors:string[]=[];health.set(page,errors);
  page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{const url=new URL(route.request().url());if(url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:')void route.continue();else{errors.push('Unexpected external request');void route.abort();}});
});
test.afterEach(async({page})=>{expect(health.get(page)).toEqual([]);await expect(page.locator('vite-error-overlay')).toHaveCount(0);await expect(page.locator('input[type=file]')).toHaveCount(0);});

async function configure(page:Page,options:{saveData?:boolean;network?:string;delays?:Record<string,number>;failFirst?:string[]}={}){
  const run=crypto.randomUUID();runs.set(page,run);
  await page.addInitScript(({saveData,network,delays,failFirst,mediaOrigin,run})=>{
    const connection=Object.assign(new EventTarget(),{saveData,effectiveType:network});
    Object.defineProperty(navigator,'connection',{configurable:true,value:connection});
    window.__reelPerf={calls:[],starts:[],maxDecoders:0,connection,swapActor:()=>{}};
    new MutationObserver(()=>{window.__reelPerf.maxDecoders=Math.max(window.__reelPerf.maxDecoders,document.querySelectorAll('.reels-feed video').length);}).observe(document,{subtree:true,childList:true});
    document.addEventListener('playing',event=>{const element=event.target;if(element instanceof HTMLVideoElement&&element.matches('.reel-video'))window.__reelPerf.starts.push(element.currentTime);},true);
    window.__klippQASetup=api=>{
      const original=api.getPlaybackUrl.bind(api),attempts=new Map<string,number>(),listeners=new Set<()=>void>(),subscribe=api.subscribe.bind(api);
      api.subscribe=callback=>{listeners.add(callback);const stop=subscribe(callback);return()=>{listeners.delete(callback);return stop();};};
      window.__reelPerf.swapActor=()=>{const id='44444444-4444-4444-8444-444444444444';api.user={id,email:'second@example.test'};api.profile={id,username:'second_viewer',displayName:'Second viewer',bio:''};listeners.forEach(listener=>listener());};
      api.getPlaybackUrl=async(id,signal)=>{
        const issued=(attempts.get(id)??0)+1;attempts.set(id,issued);
        const call:LinkCall={id,actor:api.user?.id??null,started:performance.now(),finished:null,aborted:false,issued};window.__reelPerf.calls.push(call);
        const abort=()=>{call.aborted=true;};signal?.addEventListener('abort',abort,{once:true});
        try{
          const delay=delays[id]??0;
          if(delay)await new Promise<void>((resolve,reject)=>{
            const timer=setTimeout(resolve,delay);
            signal?.addEventListener('abort',()=>{clearTimeout(timer);reject(new DOMException('Synthetic cancelled link','AbortError'));},{once:true});
          });
          signal?.throwIfAborted();
          if(issued===1&&failFirst.includes(id))throw Error('Synthetic warmup unavailable');
          await original(id,signal);const url=new URL('/video.mp4',mediaOrigin);
          url.searchParams.set('run',run);url.searchParams.set('clip',id);url.searchParams.set('issued',String(issued));url.searchParams.set('actor',call.actor??'');
          return url.href;
        }finally{call.finished=performance.now();signal?.removeEventListener('abort',abort);}
      };
    };
  },{saveData:options.saveData??false,network:options.network??'4g',delays:options.delays??{},failFirst:options.failFirst??[],mediaOrigin:mediaAudit.origin,run});
}
const feed=(page:Page)=>page.getByRole('region',{name:'Video feed',exact:true});
const player=(page:Page)=>page.locator('.reels-feed video');
const item=(page:Page,id:string)=>page.locator(`.reel-item[data-clip-id="${id}"]`);
async function ready(video:Locator){await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(2);}
async function open(page:Page){await page.goto('/qa.html?reels=1');await expect(feed(page)).toBeVisible();}
async function playing(page:Page){await expect(player(page)).toHaveCount(1);await ready(player(page));await expect.poll(()=>player(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(false);}
async function scroll(page:Page,index:number){await feed(page).evaluate((element,index)=>{element.scrollTop=element.clientHeight*index;},index);}
async function calls(page:Page,id?:string){return page.evaluate(id=>window.__reelPerf.calls.filter(call=>!id||call.id===id),id);}
async function idle(page:Page){await page.waitForTimeout(800);}

test('normal-network warming prepares one link without downloading the next video',async({page},info)=>{
  await configure(page,{delays:{[second]:400}});await open(page);await playing(page);
  await expect.poll(async()=>(await calls(page,second)).filter(call=>call.finished!==null&&!call.aborted).length).toBe(1);
  expect((await calls(page)).map(call=>call.id)).toEqual([first,second]);const preparedMedia=[...new Set(mediaIds(page))];expect(preparedMedia).toEqual([first]);await expect(player(page)).toHaveCount(1);
  const start=await page.evaluate(()=>performance.now());await scroll(page,1);await expect(item(page,second).locator('video')).toHaveCount(1);await ready(player(page));
  const latency=await page.evaluate(start=>performance.now()-start,start);
  expect((await calls(page,second))).toHaveLength(1);await expect(player(page)).toHaveCount(1);
  const maxDecoders=await page.evaluate(()=>window.__reelPerf.maxDecoders);expect(maxDecoders).toBe(1);
  await info.attach('controlled-warmed-handoff',{body:JSON.stringify({simulated_link_latency_ms:400,measured_handoff_to_ready_frame_ms:latency,additional_link_requests:(await calls(page,second)).length-1,max_live_decoders:maxDecoders,next_video_requests_before_swipe:preparedMedia.filter(id=>id!==first).length}),contentType:'application/json'});
});

test('an unfinished warmup cancels cleanly and the destination still opens',async({page})=>{
  await configure(page,{delays:{[second]:800}});await open(page);await playing(page);
  await expect.poll(async()=>(await calls(page,second)).length).toBe(1);
  expect((await calls(page,second))[0].finished).toBeNull();await scroll(page,1);
  await expect(item(page,second).locator('video')).toHaveCount(1);await ready(player(page));
  expect((await calls(page,second)).length).toBeLessThanOrEqual(2);await expect(player(page)).toHaveCount(1);
  expect(mediaIds(page).filter(id=>id===second).length).toBeGreaterThan(0);
});

test('rapid flicks do not open a skipped clip or hand off while a finger is held',async({page})=>{
  await configure(page,{network:'2g'});await open(page);await playing(page);
  await feed(page).evaluate(element=>{
    element.dispatchEvent(new PointerEvent('pointerdown',{pointerType:'touch',pointerId:7,bubbles:true}));
    element.scrollTop=element.clientHeight;
    requestAnimationFrame(()=>{element.scrollTop=element.clientHeight*2;});
  });
  await page.waitForTimeout(180);await expect(feed(page)).toHaveAttribute('data-scrolling','true');
  expect((await calls(page)).map(call=>call.id)).toEqual([first]);expect(new Set(mediaIds(page))).toEqual(new Set([first]));
  await page.evaluate(()=>window.dispatchEvent(new PointerEvent('pointerup',{pointerType:'touch',pointerId:7,bubbles:true})));
  await expect(item(page,third).locator('video')).toHaveCount(1);await ready(player(page));
  expect((await calls(page)).map(call=>call.id)).toEqual([first,third]);expect(new Set(mediaIds(page))).toEqual(new Set([first,third]));
});

for(const network of ['2g','slow-2g'])test(`${network} never speculates while active playback remains usable`,async({page})=>{
  await configure(page,{network});await open(page);await playing(page);await idle(page);
  expect((await calls(page)).map(call=>call.id)).toEqual([first]);expect(new Set(mediaIds(page))).toEqual(new Set([first]));await expect(player(page)).toHaveCount(1);
});

test('browser Save Data starts idle and only the tapped clip obtains a link or bytes',async({page},info)=>{
  await configure(page,{saveData:true});await open(page);await expect(page.getByRole('button',{name:'Save data',exact:true})).toHaveAttribute('aria-pressed','true');
  await expect(page.getByText('Data saver · Tap to play',{exact:true})).toBeVisible();await idle(page);
  expect(await calls(page)).toEqual([]);expect(mediaIds(page)).toEqual([]);await expect(player(page)).toHaveCount(0);
  await capturePlaybackEvidence(page,info,'data-saver-idle');
  await item(page,first).getByRole('button',{name:'Play clip',exact:true}).click();await playing(page);await idle(page);
  expect((await calls(page)).map(call=>call.id)).toEqual([first]);expect(new Set(mediaIds(page))).toEqual(new Set([first]));
  await scroll(page,1);await expect(item(page,second)).toHaveAttribute('data-active','true');await expect(player(page)).toHaveCount(0);await idle(page);
  expect((await calls(page)).map(call=>call.id)).toEqual([first]);
});

test('turning Save data on releases decoding and manual play restores the saved position',async({page})=>{
  await configure(page,{network:'2g'});await open(page);await playing(page);
  await page.locator('.reel-transport').getByRole('button',{name:'Pause video',exact:true}).click();await player(page).evaluate(element=>(element as HTMLVideoElement).currentTime=1.2);
  await expect.poll(()=>player(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await page.getByRole('button',{name:'Save data',exact:true}).click();await expect(player(page)).toHaveCount(0);await idle(page);
  expect((await calls(page)).map(call=>call.id)).toEqual([first]);
  const starts=await page.evaluate(()=>window.__reelPerf.starts.length);
  await item(page,first).getByRole('button',{name:'Play clip',exact:true}).click();await playing(page);
  await expect.poll(()=>page.evaluate(()=>window.__reelPerf.starts.length)).toBeGreaterThan(starts);
  expect(await page.evaluate(index=>window.__reelPerf.starts[index],starts)).toBeCloseTo(1.2,1);
  await expect(page.getByRole('button',{name:'Save data',exact:true})).toHaveAttribute('aria-pressed','true');
  expect((await calls(page,first))).toHaveLength(1);
});

test('a pending link cannot attach a media source after the page is hidden',async({page})=>{
  await configure(page,{network:'2g',delays:{[first]:500}});await open(page);await expect.poll(async()=>(await calls(page,first)).length).toBe(1);
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));window.dispatchEvent(new Event('pagehide'));});
  await page.waitForTimeout(650);await expect(player(page)).toHaveCount(0);expect(mediaIds(page)).toEqual([]);
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:false});document.dispatchEvent(new Event('visibilitychange'));window.dispatchEvent(new Event('pageshow'));});
  await expect(page.getByText('Tap to continue',{exact:true})).toBeVisible();await expect(player(page)).toHaveCount(0);
  await item(page,first).getByRole('button',{name:'Play clip',exact:true}).click();await playing(page);
  expect((await calls(page,first)).length).toBeLessThanOrEqual(2);expect(new Set(mediaIds(page))).toEqual(new Set([first]));
});

test('comments pause playback and cancel new background preparation',async({page})=>{
  await configure(page);await open(page);await playing(page);
  await item(page,first).getByRole('button',{name:'Comments for Last-second save',exact:true}).click();
  await expect(page.getByRole('dialog',{name:'Comments',exact:true})).toBeVisible();const count=(await calls(page)).length;await idle(page);
  expect((await calls(page))).toHaveLength(count);expect(new Set(mediaIds(page))).toEqual(new Set([first]));
  expect(await player(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
});

test('a rejected speculative link never errors the current clip and retries when selected',async({page})=>{
  await configure(page,{failFirst:[second]});await open(page);await playing(page);
  await expect.poll(async()=>(await calls(page,second)).filter(call=>call.finished!==null).length).toBe(1);
  await expect(page.locator('.reel-error')).toHaveCount(0);expect(new Set(mediaIds(page))).toEqual(new Set([first]));
  await scroll(page,1);await expect(item(page,second).locator('video')).toHaveCount(1);await ready(player(page));
  expect(await calls(page,second)).toHaveLength(2);await expect(player(page)).toHaveCount(1);
});

test('identity replacement discards warmed playback capabilities',async({page})=>{
  await configure(page);await open(page);await playing(page);
  await expect.poll(async()=>(await calls(page,second)).filter(call=>call.finished!==null).length).toBe(1);
  await page.evaluate(()=>window.__reelPerf.swapActor());
  await expect.poll(async()=>(await calls(page,first)).some(call=>call.actor==='44444444-4444-4444-8444-444444444444')).toBe(true);
  await playing(page);await expect(player(page)).toHaveAttribute('src',/actor=44444444-4444-4444-8444-444444444444/);
  await expect(player(page)).toHaveCount(1);
});

test('dragging the seek handle commits one seek on release and preserves pause intent',async({page})=>{
  await configure(page,{network:'2g'});await open(page);await playing(page);
  await page.locator('.reel-transport').getByRole('button',{name:'Pause video',exact:true}).click();
  await player(page).evaluate(element=>{element.setAttribute('data-seek-count','0');element.addEventListener('seeking',()=>element.setAttribute('data-seek-count',String(Number(element.getAttribute('data-seek-count'))+1)));});
  const seek=page.getByRole('slider',{name:'Seek Last-second save',exact:true});
  await seek.dispatchEvent('pointerdown',{pointerType:'touch',pointerId:8});
  for(const value of ['.5','1','1.5'])await seek.evaluate((element,value)=>{const input=element as HTMLInputElement;input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));},value);
  await expect(player(page)).toHaveAttribute('data-seek-count','0');await seek.dispatchEvent('pointerup',{pointerType:'touch',pointerId:8});
  await expect.poll(()=>player(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await expect(player(page)).toHaveAttribute('data-seek-count','1');expect(await player(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  expect(await player(page).evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.5,1);
});

test('returning from the background waits for a tap and resumes the preserved position',async({page},info)=>{
  await configure(page,{network:'2g'});await open(page);await playing(page);
  await page.locator('.reel-transport').getByRole('button',{name:'Pause video',exact:true}).click();await player(page).evaluate(element=>(element as HTMLVideoElement).currentTime=1.2);
  await expect.poll(()=>player(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));window.dispatchEvent(new Event('pagehide'));});
  await expect(player(page)).toHaveCount(0);const requestCount=mediaIds(page).length;
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:false});document.dispatchEvent(new Event('visibilitychange'));window.dispatchEvent(new Event('pageshow'));});
  await expect(page.getByText('Tap to continue',{exact:true})).toBeVisible();await idle(page);await expect(player(page)).toHaveCount(0);expect(mediaIds(page)).toHaveLength(requestCount);
  const starts=await page.evaluate(()=>window.__reelPerf.starts.length);
  await item(page,first).getByRole('button',{name:'Play clip',exact:true}).click();await playing(page);
  await expect.poll(()=>page.evaluate(()=>window.__reelPerf.starts.length)).toBeGreaterThan(starts);
  const resumedAt=await page.evaluate(index=>window.__reelPerf.starts[index],starts);expect(resumedAt).toBeCloseTo(1.2,1);await expect(player(page)).toHaveCount(1);
  await info.attach('background-resume-first-frame',{body:JSON.stringify({saved_time_seconds:1.2,first_playing_time_seconds:resumedAt,background_media_requests:mediaIds(page).length-requestCount}),contentType:'application/json'});
});

test('resizing during a held swipe defers anchoring until the destination settles',async({page})=>{
  await configure(page,{network:'2g'});await open(page);await playing(page);const originalHeight=await feed(page).evaluate(element=>element.clientHeight);
  await feed(page).evaluate(element=>{element.dispatchEvent(new PointerEvent('pointerdown',{pointerType:'touch',pointerId:10,bubbles:true}));element.scrollTop=element.clientHeight*2;});
  await expect(feed(page)).toHaveAttribute('data-scrolling','true');
  await page.evaluate(()=>{if(window.visualViewport){Object.defineProperty(window.visualViewport,'height',{configurable:true,value:700});window.visualViewport.dispatchEvent(new Event('resize'));}window.dispatchEvent(new Event('resize'));});
  await page.waitForTimeout(80);expect(await feed(page).evaluate(element=>element.clientHeight)).toBe(originalHeight);
  await page.evaluate(()=>window.dispatchEvent(new PointerEvent('pointerup',{pointerType:'touch',pointerId:10,bubbles:true})));
  await expect(feed(page)).toHaveAttribute('data-scrolling','false');await expect(item(page,third)).toHaveAttribute('data-active','true');await expect(item(page,third).locator('video')).toHaveCount(1);await ready(player(page));
  await expect.poll(()=>feed(page).evaluate(element=>Math.abs(element.scrollTop-element.clientHeight*2))).toBeLessThan(3);
  expect((await calls(page)).map(call=>call.id)).toEqual([first,third]);
});

test('retry invalidates the cached link and restores the paused section',async({page})=>{
  await configure(page,{network:'2g'});await open(page);await playing(page);
  await page.locator('.reel-transport').getByRole('button',{name:'Pause video',exact:true}).click();await player(page).evaluate(element=>(element as HTMLVideoElement).currentTime=1.2);
  await expect.poll(()=>player(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);const original=await player(page).getAttribute('src');
  await player(page).evaluate(element=>element.dispatchEvent(new Event('error')));await expect(player(page)).toHaveCount(0);
  await page.getByRole('button',{name:'Retry playback',exact:true}).click();await expect(player(page)).toHaveCount(1);await ready(player(page));
  await expect(player(page)).not.toHaveAttribute('src',original!);expect(await calls(page,first)).toHaveLength(2);
  await expect.poll(()=>player(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await expect.poll(()=>player(page).evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.2,1);
  expect(await player(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
});

test('a prolonged buffering stall releases the decoder and offers a deliberate retry',async({page})=>{
  await configure(page,{network:'2g'});await open(page);await playing(page);
  await page.locator('.reel-transport').getByRole('button',{name:'Pause video',exact:true}).click();
  await expect.poll(()=>player(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);await page.waitForTimeout(250);
  await page.clock.install();await player(page).evaluate(element=>element.dispatchEvent(new Event('waiting')));
  await expect(page.locator('.reel-loading')).toBeVisible();
  await page.clock.runFor(11900);await expect(page.locator('.reel-error')).toHaveCount(0);
  await page.clock.runFor(200);await expect(page.locator('.reel-error')).toContainText('Video is taking too long to load.');
  await expect(player(page)).toHaveCount(0);await expect(page.getByRole('button',{name:'Retry playback',exact:true})).toBeVisible();
  expect((await calls(page)).map(call=>call.id)).toEqual([first]);
});
