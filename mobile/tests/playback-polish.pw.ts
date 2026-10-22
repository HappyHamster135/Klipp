import {test,expect,type Page} from '@playwright/test';

const health=new WeakMap<Page,string[]>();
test.beforeEach(async({page})=>{
  const errors:string[]=[];health.set(page,errors);
  page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    return url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:'?route.continue():route.abort();
  });
});
test.afterEach(async({page})=>{
  expect(health.get(page)).toEqual([]);
  await expect(page.locator('vite-error-overlay')).toHaveCount(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width+1);
});

async function openClip(page:Page){
  await page.goto('/qa.html');
  await page.getByRole('button',{name:'Watch Last-second save',exact:true}).click();
  const video=page.locator('.watch-player video');
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(2);
  return video;
}

test('metadata alone exposes blocked autoplay without waiting for canplay or reloading the source',async({page},testInfo)=>{
  await page.addInitScript(()=>{
    // Safari may only preload metadata until play is requested. Do not let canplay drive this test.
    document.addEventListener('canplay',event=>{if(event.target instanceof HTMLVideoElement)event.stopImmediatePropagation();},true);
    const nativePlay=HTMLMediaElement.prototype.play;let deny=true;
    HTMLMediaElement.prototype.play=function(){if(deny){deny=false;return Promise.reject(new DOMException('A user gesture is required.','NotAllowedError'));}return nativePlay.call(this);};
  });
  const video=await openClip(page),source=await video.getAttribute('src');
  await expect(video).toHaveAttribute('aria-label','Video: Last-second save');
  await expect(video).toHaveAttribute('preload','metadata');
  await expect(page.getByRole('button',{name:'Play video',exact:true})).toBeVisible();
  expect(await video.evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  await page.screenshot({path:testInfo.outputPath('tap-to-play.png'),fullPage:false,scale:'css'});
  await page.getByRole('button',{name:'Play video',exact:true}).click();
  await expect(page.getByRole('button',{name:'Play video',exact:true})).toHaveCount(0);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(0.2);
  await expect(video).toHaveAttribute('src',source!);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(1);
});

test('a blocked resume offers Play video at the restored position',async({page})=>{
  const video=await openClip(page);
  await video.evaluate(async element=>{const player=element as HTMLVideoElement;player.muted=true;await player.play();player.currentTime=1.1;});
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  const position=await video.evaluate(element=>{const player=element as HTMLVideoElement;const position=player.currentTime;player.dispatchEvent(new Event('error'));return position;});
  await page.evaluate(()=>{
    const nativePlay=HTMLMediaElement.prototype.play;let deny=true;
    HTMLMediaElement.prototype.play=function(){if(deny){deny=false;return Promise.reject(new DOMException('A user gesture is required.','NotAllowedError'));}return nativePlay.call(this);};
  });
  await page.getByRole('button',{name:'Retry playback',exact:true}).click();
  await expect(video).toHaveAttribute('preload','auto');
  await expect(page.getByRole('button',{name:'Play video',exact:true})).toBeVisible();
  expect(await video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(position,1);
  await page.getByRole('button',{name:'Play video',exact:true}).click();
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(position+0.1);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(2);
});

test('brief buffering stays quiet and a prolonged stall offers manual retry',async({page},testInfo)=>{
  const video=await openClip(page),source=await video.getAttribute('src');
  await video.evaluate(async element=>{const player=element as HTMLVideoElement;player.muted=true;await player.play();});
  await page.clock.install();
  await video.evaluate(element=>element.dispatchEvent(new Event('waiting')));
  await page.clock.runFor(300);
  await expect(page.locator('.playback-status')).toHaveCount(0);
  await video.evaluate(element=>element.dispatchEvent(new Event('playing')));
  await page.clock.runFor(700);
  await expect(page.locator('.playback-status')).toHaveCount(0);
  await video.evaluate(element=>element.dispatchEvent(new Event('waiting')));
  await page.clock.runFor(750);
  await expect(page.locator('.playback-status')).toHaveText('Buffering video...');
  await expect(page.getByRole('button',{name:'Retry playback',exact:true})).toHaveCount(0);
  await page.clock.runFor(7300);
  await expect(page.locator('.playback-status')).toContainText('The video is taking longer to load.');
  await expect(page.getByRole('button',{name:'Retry playback',exact:true})).toBeVisible();
  await page.screenshot({path:testInfo.outputPath('buffering-guidance.png'),fullPage:false,scale:'css'});
  await expect(video).toHaveAttribute('src',source!);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(1);
  await video.evaluate(element=>(element as HTMLVideoElement).pause());
  await expect(page.locator('.playback-status')).toHaveCount(0);
  await page.clock.runFor(9000);
  await expect(page.locator('.playback-status')).toHaveCount(0);
});

test('playing and seeking while paused clear loading feedback without replacing video',async({page})=>{
  const video=await openClip(page),source=await video.getAttribute('src');
  await video.evaluate(async element=>{const player=element as HTMLVideoElement;player.dataset.identity='same-player';player.muted=true;await player.play();});
  await page.clock.install();
  await video.evaluate(element=>element.dispatchEvent(new Event('waiting')));
  await page.clock.runFor(750);
  await expect(page.locator('.playback-status')).toBeVisible();
  await video.evaluate(element=>element.dispatchEvent(new Event('playing')));
  await expect(page.locator('.playback-status')).toHaveCount(0);
  await video.evaluate(element=>{const player=element as HTMLVideoElement;player.pause();player.currentTime=1.8;});
  await page.clock.runFor(8500);
  await expect(page.locator('.playback-status')).toHaveCount(0);
  await page.locator('.watch-main').getByRole('button',{name:/^Like/}).click();
  await expect(video).toHaveAttribute('data-identity','same-player');
  await expect(video).toHaveAttribute('src',source!);
  expect(await video.evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(1);
});

test('switching away during a fresh playback link does not resume unexpectedly',async({page})=>{
  const video=await openClip(page);
  await video.evaluate(async element=>{const player=element as HTMLVideoElement;player.muted=true;await player.play();player.currentTime=1.2;});
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.getPlaybackUrl.bind(api);
    api.getPlaybackUrl=async(id,signal)=>{await new Promise(resolve=>setTimeout(resolve,500));return original(id,signal);};
  });
  const position=await video.evaluate(element=>{const player=element as HTMLVideoElement;const position=player.currentTime;player.dispatchEvent(new Event('error'));return position;});
  await page.getByRole('button',{name:'Retry playback',exact:true}).click();
  await page.evaluate(()=>window.dispatchEvent(new Event('pagehide')));
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(3);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  expect(await video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(position,1);
  expect(await video.evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  await expect(page.getByRole('button',{name:'Play video',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Play video',exact:true}).click();
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(position+0.1);
});

test('leaving the player cancels delayed buffering notices',async({page})=>{
  const video=await openClip(page);
  await video.evaluate(async element=>{const player=element as HTMLVideoElement;player.muted=true;await player.play();});
  await page.clock.install();
  await video.evaluate(element=>element.dispatchEvent(new Event('waiting')));
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  await page.clock.runFor(9000);
  await expect(page.locator('.watch')).toHaveCount(0);
  await expect(page.locator('.playback-status')).toHaveCount(0);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(1);
});

test('opening a clip on a short desktop keeps native transport above navigation',async({page},testInfo)=>{
  await page.setViewportSize({width:1265,height:713});
  const video=await openClip(page);
  await expect.poll(async()=>{
    const bounds=await video.boundingBox(),navigation=await page.locator('.bottom-nav').boundingBox();
    return !!bounds&&!!navigation&&bounds.y>=0&&bounds.y+bounds.height<=navigation.y-10;
  }).toBe(true);
  await page.screenshot({path:testInfo.outputPath('short-desktop-transport.png'),fullPage:false,scale:'css'});
});
