import {test,expect,type Locator,type Page} from '@playwright/test';
import {capturePlaybackEvidence} from './playback-evidence';

const firstId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const ownId='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const health=new WeakMap<Page,string[]>();
declare global{interface Window{__reelsQA?:{release?:()=>void;returned?:boolean;shares?:ShareData[]}}}

test.beforeEach(async({page})=>{
  // This interaction/recovery suite uses the constrained-network policy so
  // optional link warming cannot affect its exact active-source assertions.
  // reels-performance.pw.ts separately covers normal-network warming budgets.
  await page.addInitScript(()=>Object.defineProperty(navigator,'connection',{configurable:true,value:{saveData:false,effectiveType:'2g',addEventListener(){},removeEventListener(){}}}));
  const errors:string[]=[];health.set(page,errors);
  page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if(url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:')void route.continue();
    else{errors.push('Unexpected external request');void route.abort();}
  });
});
test.afterEach(async({page})=>{
  expect(health.get(page)).toEqual([]);
  await expect(page.locator('vite-error-overlay')).toHaveCount(0);
  await expect(page.locator('input[type=file]')).toHaveCount(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width+1);
});
const feed=(page:Page)=>page.getByRole('region',{name:'Video feed',exact:true});
const item=(page:Page,id:string)=>page.locator(`.reel-item[data-clip-id="${id}"]`);
const video=(page:Page)=>page.locator('.reels-feed video');
async function ready(player:Locator){await expect.poll(()=>player.evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(2);}
async function open(page:Page,hash=''){
  await page.goto(`/qa.html?reels=1${hash}`);
  await expect(page).toHaveTitle('Klipp · Local QA');
  await expect(feed(page)).toBeVisible();
  await expect(video(page)).toHaveCount(1);
  await ready(video(page));
}
async function scrollTo(page:Page,index:number){
  await feed(page).evaluate((element,index)=>(element as HTMLElement).scrollTo({top:element.clientHeight*index,behavior:'auto'}),index);
}

test('phone feed presents one inline muted decoder and fetches only its active clip',async({page},testInfo)=>{
  await open(page);
  await expect(item(page,firstId).locator('video')).toHaveCount(1);
  expect(await video(page).evaluate(element=>({muted:(element as HTMLVideoElement).muted,inline:(element as HTMLVideoElement).playsInline}))).toEqual({muted:true,inline:true});
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(0.1);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests)).toEqual([firstId]);
  const dimensions=await feed(page).evaluate(element=>({height:element.clientHeight,snap:getComputedStyle(element).scrollSnapType,width:element.getBoundingClientRect().width}));
  expect(dimensions.height).toBeGreaterThan(450);expect(dimensions.width).toBeLessThanOrEqual(page.viewportSize()!.width);
  expect(dimensions.snap).toContain('y');
  await capturePlaybackEvidence(page,testInfo,'reels-first-view');
});

test('swiping changes the active clip without leaving offscreen video decoders',async({page})=>{
  await open(page);await scrollTo(page,1);
  await expect(item(page,ownId).locator('video')).toHaveCount(1);await expect(video(page)).toHaveCount(1);await ready(video(page));
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests)).toEqual([firstId,ownId]);
  await expect.poll(()=>feed(page).evaluate(element=>Math.abs(element.scrollTop-element.clientHeight))).toBeLessThan(3);
  await scrollTo(page,2);
  await expect(item(page,'cccccccc-cccc-4ccc-8ccc-cccccccccccc').locator('video')).toHaveCount(1);
  await expect(video(page)).toHaveCount(1);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(3);
});

test('swiping away and back restores a deliberately paused position',async({page})=>{
  await open(page);
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(false);
  await page.locator('.reel-transport').getByRole('button',{name:'Pause video',exact:true}).click();
  await video(page).evaluate(element=>(element as HTMLVideoElement).currentTime=1.2);
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await scrollTo(page,1);await expect(item(page,ownId).locator('video')).toHaveCount(1);await ready(video(page));
  await scrollTo(page,0);await expect(item(page,firstId).locator('video')).toHaveCount(1);await ready(video(page));
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.2,1);
  expect(await video(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  await expect(video(page)).toHaveCount(1);
});

test('comments and likes keep the same video and playback link',async({page},testInfo)=>{
  await open(page);
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(false);
  await page.locator('.reel-transport').getByRole('button',{name:'Pause video',exact:true}).click();
  await video(page).evaluate(element=>{const player=element as HTMLVideoElement;player.currentTime=1.1;player.dataset.qaIdentity='original-reel';});
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  const source=await video(page).getAttribute('src');
  await item(page,firstId).getByRole('button',{name:/^Like/}).click();
  await expect(item(page,firstId).getByRole('button',{name:'Unlike Last-second save',exact:true})).toHaveAttribute('aria-pressed','true');
  await expect(video(page)).toHaveAttribute('data-qa-identity','original-reel');
  await item(page,firstId).getByRole('button',{name:/^Comments/}).click();
  const comments=page.getByRole('dialog',{name:'Comments',exact:true});
  await expect(comments).toBeVisible();await expect(comments.getByText('That timing!',{exact:true})).toBeVisible();
  await capturePlaybackEvidence(page,testInfo,'reels-comments');
  expect(await video(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  await comments.getByLabel('Add a comment',{exact:true}).fill('Synthetic reel comment.');
  await comments.getByRole('button',{name:'Post comment',exact:true}).click();
  await expect(comments.getByText('Synthetic reel comment.',{exact:true})).toBeVisible();
  await comments.getByRole('button',{name:'Close dialog',exact:true}).click();
  await expect(comments).toHaveCount(0);
  await expect(video(page)).toHaveAttribute('data-qa-identity','original-reel');await expect(video(page)).toHaveAttribute('src',source!);
  expect(await video(page).evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.1,1);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests)).toEqual([firstId]);
});

test('the end card reaches older pages without mounting a page of video decoders',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const template=api.clips[0];
    api.clips=Array.from({length:14},(_,index)=>({...template,id:`${(index+1).toString(16).padStart(8,'0')}-aaaa-4aaa-8aaa-aaaaaaaaaaaa`,title:`QA page clip ${index+1}`,createdAt:new Date(Date.UTC(2026,9,22,12,0,30-index)).toISOString()}));
  };});
  await open(page);await expect(page.locator('.reel-item[data-clip-id]')).toHaveCount(12);
  await feed(page).evaluate(element=>{element.scrollTop=element.scrollHeight;});
  await feed(page).getByRole('button',{name:'Older clips',exact:true}).click();
  await expect(page.locator('.reel-item[data-clip-id]')).toHaveCount(2);
  await expect(page.locator('.reel-item[data-clip-id="0000000d-aaaa-4aaa-8aaa-aaaaaaaaaaaa"] video')).toHaveCount(1);
  await expect(video(page)).toHaveCount(1);
  await feed(page).evaluate(element=>{element.scrollTop=element.scrollHeight;});
  await feed(page).getByRole('button',{name:'Newer clips',exact:true}).click();
  await expect(page.locator('.reel-item[data-clip-id]')).toHaveCount(12);
  await expect(page.locator('.reel-item[data-clip-id="00000001-aaaa-4aaa-8aaa-aaaaaaaaaaaa"] video')).toHaveCount(1);
  await expect(video(page)).toHaveCount(1);
});

test('backgrounding releases decoding and leaving Media keeps the source detached',async({page})=>{
  await open(page);
  await video(page).evaluate(async element=>{const player=element as HTMLVideoElement;player.muted=true;await player.play();});
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
  await expect(video(page)).toHaveCount(0);
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:false});});
  await page.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Profile',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();await expect(page.locator('video')).toHaveCount(0);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests)).toEqual([firstId]);
});

test('a late obsolete playback response cannot replace the clip reached by a swipe',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const original=api.getPlaybackUrl.bind(api);let delay=true;
    api.getPlaybackUrl=async(id,signal)=>{
      if(id==='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'&&delay){delay=false;await new Promise<void>(resolve=>{window.__reelsQA={release:resolve};});window.__reelsQA!.returned=true;return `${await original(id)}?clip=${id}`;}
      return `${await original(id,signal)}?clip=${id}`;
    };
  };});
  await page.goto('/qa.html?reels=1');await expect(feed(page)).toBeVisible();
  await expect.poll(()=>page.evaluate(()=>!!window.__reelsQA?.release)).toBe(true);
  await scrollTo(page,1);await expect(item(page,ownId).locator('video')).toHaveCount(1);await ready(video(page));
  await page.evaluate(()=>window.__reelsQA?.release?.());
  await expect.poll(()=>page.evaluate(()=>window.__reelsQA?.returned)).toBe(true);
  await expect(video(page)).toHaveAttribute('src',new RegExp(`clip=${ownId}$`));
  await expect(item(page,ownId).locator('video')).toHaveCount(1);await expect(video(page)).toHaveCount(1);
});

test('shared clip links open the intended reel and keep phone upload controls absent',async({page})=>{
  await open(page,`#watch/${ownId}`);
  await expect(item(page,ownId).locator('video')).toHaveCount(1);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests)).toEqual([ownId]);
  await expect(page.getByRole('button',{name:/^Upload(?: clip)?$/i})).toHaveCount(0);
});

test('mobile landscape keeps the swipe player and actions inside the viewport',async({page})=>{
  await page.setViewportSize({width:844,height:390});await open(page);
  const bounds=await feed(page).boundingBox();expect(bounds).not.toBeNull();
  expect(bounds!.y).toBeGreaterThanOrEqual(0);expect(bounds!.y+bounds!.height).toBeLessThanOrEqual(391);
  const like=await item(page,firstId).getByRole('button',{name:/^Like/}).boundingBox();expect(like).not.toBeNull();
  expect(like!.y+like!.height).toBeLessThanOrEqual(391);
});

test('sharing exposes only the clip link and actions pause the same player',async({page})=>{
  await page.addInitScript(()=>{
    window.__reelsQA={shares:[]};
    Object.defineProperty(navigator,'share',{configurable:true,value:async(data:ShareData)=>{window.__reelsQA!.shares!.push(data);}});
  });
  await open(page);
  await item(page,firstId).getByRole('button',{name:'Share Last-second save',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>window.__reelsQA?.shares?.length)).toBe(1);
  expect(await page.evaluate(()=>window.__reelsQA!.shares![0])).toEqual({title:'Last-second save',url:`http://127.0.0.1:5173/#watch/${firstId}`});
  await video(page).evaluate(element=>(element as HTMLVideoElement).dataset.qaIdentity='action-player');
  await item(page,firstId).getByRole('button',{name:'Actions for Last-second save',exact:true}).click();
  const actions=page.getByRole('dialog',{name:'Clip actions',exact:true});await expect(actions).toBeVisible();
  await expect(actions.getByRole('button',{name:'Report clip',exact:true})).toBeVisible();
  expect(await video(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  await actions.getByRole('button',{name:'Close dialog',exact:true}).click();await expect(actions).toHaveCount(0);
  await expect(video(page)).toHaveAttribute('data-qa-identity','action-player');
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests)).toEqual([firstId]);
});

test('opening the phone keyboard for comments keeps the active clip and position',async({page})=>{
  await open(page);await scrollTo(page,1);await expect(item(page,ownId).locator('video')).toHaveCount(1);await ready(video(page));
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(false);
  await page.locator('.reel-transport').getByRole('button',{name:'Pause video',exact:true}).click();
  await video(page).evaluate(element=>{const player=element as HTMLVideoElement;player.currentTime=1.2;player.dataset.qaKeyboardIdentity='same-comment-player';});
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  const height=await feed(page).evaluate(element=>element.clientHeight),source=await video(page).getAttribute('src');
  await item(page,ownId).getByRole('button',{name:'Comments for Clean final round',exact:true}).click();
  const comments=page.getByRole('dialog',{name:'Comments',exact:true});await expect(comments).toBeVisible();
  await comments.getByLabel('Add a comment',{exact:true}).fill('Keep this draft while the phone keyboard opens.');
  await page.evaluate(()=>{if(window.visualViewport){Object.defineProperty(window.visualViewport,'height',{configurable:true,value:360});window.visualViewport.dispatchEvent(new Event('resize'));}window.dispatchEvent(new Event('resize'));});
  await expect.poll(()=>feed(page).evaluate(element=>element.clientHeight)).toBe(height);
  await expect(item(page,ownId)).toHaveAttribute('data-active','true');
  await expect(video(page)).toHaveAttribute('data-qa-keyboard-identity','same-comment-player');
  await expect(comments.getByLabel('Add a comment',{exact:true})).toHaveValue('Keep this draft while the phone keyboard opens.');
  await page.evaluate(()=>{if(window.visualViewport){Object.defineProperty(window.visualViewport,'height',{configurable:true,value:844});window.visualViewport.dispatchEvent(new Event('resize'));}window.dispatchEvent(new Event('resize'));});
  await comments.getByRole('button',{name:'Close dialog',exact:true}).click();await expect(comments).toHaveCount(0);
  await expect(item(page,ownId)).toHaveAttribute('data-active','true');await expect(video(page)).toHaveAttribute('src',source!);
  await expect.poll(()=>video(page).evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.2,1);
  expect(await video(page).evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests)).toEqual([firstId,ownId]);
});

test('arrow navigation changes reels while seek keys stay on the current clip',async({page})=>{
  await open(page);await feed(page).focus();await page.keyboard.press('ArrowDown');
  await expect(item(page,ownId).locator('video')).toHaveCount(1);await ready(video(page));
  await page.getByRole('slider',{name:'Seek Clean final round',exact:true}).focus();await page.keyboard.press('ArrowLeft');
  await expect(item(page,ownId)).toHaveAttribute('data-active','true');await expect(video(page)).toHaveCount(1);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests)).toEqual([firstId,ownId]);
});
