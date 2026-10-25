import {test,expect,type Page} from '@playwright/test';
import {capturePlaybackEvidence} from './playback-evidence';

const health=new WeakMap<Page,string[]>();
test.beforeEach(async({page})=>{
  const errors:string[]=[];health.set(page,errors);page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{const url=new URL(route.request().url());return url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:'?route.continue():route.abort();});
  await page.setViewportSize({width:1280,height:800});
});
test.afterEach(async({page})=>{expect(health.get(page)).toEqual([]);await expect(page.locator('vite-error-overlay')).toHaveCount(0);});
async function open(page:Page){
  await page.goto('/qa.html');await page.getByRole('button',{name:'Watch Last-second save',exact:true}).click();
  const video=page.locator('.watch-player video');
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(2);
  await video.evaluate(element=>{const player=element as HTMLVideoElement;player.pause();player.currentTime=1.15;player.dataset.qaIdentity='original-player';});
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  return video;
}

test('desktop fullscreen fills the viewport and exits at the same paused position',async({page},testInfo)=>{
  const video=await open(page),source=await video.getAttribute('src'),requests=await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length);
  const capability=await video.evaluate(element=>typeof element.requestFullscreen==='function'&&document.fullscreenEnabled);
  await page.getByRole('button',{name:'Fullscreen video',exact:true}).click();
  if(capability){
    await expect.poll(()=>page.evaluate(()=>document.fullscreenElement?.tagName??null)).toBe('VIDEO');
    const bounds=await video.boundingBox();expect(bounds).not.toBeNull();
    expect(bounds!.height).toBeGreaterThanOrEqual(798);expect(bounds!.width).toBeGreaterThanOrEqual(1278);
    await capturePlaybackEvidence(page,testInfo,'desktop-fullscreen');
    await page.keyboard.press('Escape');await expect.poll(()=>page.evaluate(()=>document.fullscreenElement===null)).toBe(true);
    await video.focus();await page.keyboard.press('f');
    await expect.poll(()=>page.evaluate(()=>document.fullscreenElement?.tagName??null)).toBe('VIDEO');
    await page.keyboard.press('Escape');await expect.poll(()=>page.evaluate(()=>document.fullscreenElement===null)).toBe(true);
    await page.getByLabel('Add a comment',{exact:true}).fill('Typing f keeps this comment inline.');
    expect(await page.evaluate(()=>document.fullscreenElement===null)).toBe(true);
    await expect(video).toHaveAttribute('data-qa-identity','original-player');await expect(video).toHaveAttribute('src',source!);
    expect(await video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.15,1);
    expect(await video.evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
    expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(requests);
  }else{
    testInfo.annotations.push({type:'capability',description:'This emulated browser port does not expose desktop document fullscreen; verified its fallback notice. Physical Safari native fullscreen requires device testing.'});
    await expect(page.getByRole('status').filter({hasText:'Fullscreen is unavailable here.'})).toBeVisible();
  }
});

test('Safari native fullscreen fallback uses the same video without a fresh playback request',async({page})=>{
  await page.addInitScript(()=>{
    Object.defineProperty(document,'fullscreenEnabled',{configurable:true,value:false});
    Object.defineProperty(HTMLVideoElement.prototype,'webkitEnterFullscreen',{configurable:true,value:function(this:HTMLVideoElement){this.dataset.qaNativeFullscreen='entered';}});
  });
  const video=await open(page),source=await video.getAttribute('src');
  await page.getByRole('button',{name:'Fullscreen video',exact:true}).click();
  await expect(video).toHaveAttribute('data-qa-native-fullscreen','entered');await expect(video).toHaveAttribute('data-qa-identity','original-player');await expect(video).toHaveAttribute('src',source!);
  expect(await video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.15,1);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().playbackRequests.length)).toBe(1);
});
