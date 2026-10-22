import {test,expect} from '@playwright/test';

test.beforeEach(async({page})=>{
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    return url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:'?route.continue():route.abort();
  });
  await page.goto('/qa.html');
  await page.getByRole('button',{name:'Watch Last-second save',exact:true}).click();
  await expect.poll(()=>page.locator('.watch-player video').evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(2);
});

test('a fresh playback link preserves a paused position and audio choices',async({page})=>{
  const video=page.locator('.watch-player video');
  await video.evaluate(element=>{
    const player=element as HTMLVideoElement;
    player.pause();player.muted=true;player.volume=0.4;player.playbackRate=1.5;player.currentTime=1.1;
  });
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.1,1);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.1,1);
  const requests=await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length);
  await video.evaluate(element=>element.dispatchEvent(new Event('error')));
  await page.getByRole('button',{name:'Retry playback',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(requests+1);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(2);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.1,1);
  expect(await video.evaluate(element=>{const p=element as HTMLVideoElement;return{paused:p.paused,muted:p.muted,rate:p.playbackRate};})).toEqual({paused:true,muted:true,rate:1.5});
  await expect(page.locator('.playback-error')).toHaveCount(0);
  await page.locator('.watch-main').getByRole('button',{name:/^Like/,exact:false}).click();
  expect(await video.evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
});

test('a denied fullscreen request leaves inline playback usable',async({page},testInfo)=>{
  const video=page.locator('.watch-player video');
  await video.evaluate(async element=>{
    const player=element as HTMLVideoElement&{webkitEnterFullscreen?:()=>void};
    player.muted=true;await player.play();
    player.requestFullscreen=()=>Promise.reject(new Error('Fullscreen unavailable in this embedding.'));
    player.webkitEnterFullscreen=()=>{throw new Error('Native fullscreen unavailable.');};
  });
  const source=await video.getAttribute('src');
  const requests=await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length);
  await page.getByRole('button',{name:'Fullscreen video',exact:true}).click();
  await expect(page.getByRole('status').filter({hasText:'Fullscreen is unavailable here.'})).toBeVisible();
  await expect(page.locator('.playback-error')).toHaveCount(0);
  await expect(video).toHaveAttribute('src',source!);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(0.2);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(requests);
  await page.screenshot({path:testInfo.outputPath('inline-fullscreen-guidance.png'),fullPage:false});
});

test('retrying an interrupted playing clip resumes after its saved position',async({page})=>{
  const video=page.locator('.watch-player video');
  await video.evaluate(async element=>{const player=element as HTMLVideoElement;player.muted=true;await player.play();});
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(0.6);
  const requests=await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length);
  const position=await video.evaluate(element=>{const player=element as HTMLVideoElement;const position=player.currentTime;player.dispatchEvent(new Event('error'));return position;});
  await page.getByRole('button',{name:'Retry playback',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(requests+1);
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(position+0.1);
  expect(await video.evaluate(element=>{const p=element as HTMLVideoElement;return{paused:p.paused,muted:p.muted};})).toEqual({paused:false,muted:true});
});

test('native Safari fullscreen is used when document fullscreen is unavailable',async({page})=>{
  await page.evaluate(()=>Object.defineProperty(document,'fullscreenEnabled',{configurable:true,value:false}));
  const video=page.locator('.watch-player video');
  await video.evaluate(element=>{
    const player=element as HTMLVideoElement&{webkitEnterFullscreen?:()=>void};
    player.webkitEnterFullscreen=()=>player.dataset.nativeFullscreen='entered';
    player.requestFullscreen=()=>Promise.reject(new Error('Should use the native video method.'));
  });
  await page.getByRole('button',{name:'Fullscreen video',exact:true}).click();
  await expect(video).toHaveAttribute('data-native-fullscreen','entered');
  await expect(page.locator('.playback-error')).toHaveCount(0);
});

test('landscape player and transport occupy the same visible rectangle',async({page},testInfo)=>{
  await page.setViewportSize({width:844,height:390});
  const player=page.locator('.watch-player'),video=player.locator('video');
  await expect.poll(async()=>{const bounds=await player.boundingBox(),nav=await page.locator('.bottom-nav').boundingBox();return !!bounds&&!!nav&&bounds.y>=0&&bounds.y+bounds.height<nav.y;}).toBe(true);
  const bounds=await player.boundingBox(),videoBounds=await video.boundingBox();
  expect(bounds).not.toBeNull();expect(videoBounds).not.toBeNull();
  expect(bounds!.height).toBeLessThanOrEqual(390*0.65+1);
  expect(Math.abs(bounds!.height-videoBounds!.height)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(845);
  await page.screenshot({path:testInfo.outputPath('landscape-player.png'),fullPage:false});
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  await page.getByRole('button',{name:'Watch Last-second save',exact:true}).click();
  await expect.poll(async()=>{const bounds=await player.boundingBox(),nav=await page.locator('.bottom-nav').boundingBox();return !!bounds&&!!nav&&bounds.y>=0&&bounds.y+bounds.height<nav.y;}).toBe(true);
});
