import {test,expect,type Page,type TestInfo} from '@playwright/test';

const localPath=(url:URL)=>decodeURIComponent(url.pathname).replace(/^\/([a-z]:\/)/i,'$1');
const source=localPath(new URL('../.qa/test-video.mp4',import.meta.url));
const unreadable=localPath(new URL('../.qa/unreadable.mp4',import.meta.url));
const health=new WeakMap<Page,string[]>();
test.beforeEach(async({page})=>{
  const errors:string[]=[];health.set(page,errors);
  page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if((url.protocol==='http:'&&url.hostname==='127.0.0.1')||url.protocol==='blob:'||url.protocol==='data:')void route.continue();
    else {errors.push(`Unexpected external request: ${url.origin}`);void route.abort('blockedbyclient');}
  });
  await page.goto('/qa.html');await expect(page).toHaveTitle('Klipp · Local QA');
  await expect(page.getByText('Local test data · no online changes',{exact:true})).toBeVisible();
  await page.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Upload',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Upload clip',exact:true})).toBeVisible();
});
test.afterEach(async({page})=>{
  expect(await page.locator('vite-error-overlay').count()).toBe(0);
  expect(health.get(page)).toEqual([]);
});
async function choose(page:Page,testInfo:TestInfo){
  await page.getByLabel('Choose video from your phone',{exact:true}).setInputFiles(source);
  const supported=await page.evaluate(()=>typeof VideoDecoder==='function'&&typeof VideoEncoder==='function');
  if(!supported){
    await expect(page.getByRole('status').filter({hasText:/cannot read|cannot prepare|could not prepare|cannot create/i})).toBeVisible({timeout:60000});
    testInfo.skip(true,'This desktop WebKit port lacks required WebCodecs; physical phones remain unverified.');return null;
  }
  const start=page.getByLabel('Section start in seconds',{exact:true}),end=page.getByLabel('Section end in seconds',{exact:true});
  await expect(start).toBeVisible({timeout:60000});return{start,end};
}
test('editing an empty trim input keeps the form and preview usable',async({page},testInfo)=>{
  const range=await choose(page,testInfo);if(!range)return;
  await range.start.fill('');
  await expect(page.getByRole('heading',{name:'Upload clip',exact:true})).toBeVisible();
  await expect(range.start).toHaveValue('');
  await expect(range.start).toHaveAttribute('inputmode','decimal');
  await range.start.fill('0,2');await range.end.fill('1,4');
  await range.end.press('Enter');
  await expect(range.start).toHaveValue('0.2');await expect(range.end).toHaveValue('1.4');
  await range.start.fill('0.5');await range.start.press('Escape');await expect(range.start).toHaveValue('0.2');
  const inputSize=await range.start.evaluate(input=>({fontSize:getComputedStyle(input).fontSize,height:input.getBoundingClientRect().height}));
  expect(inputSize.fontSize).toBe('16px');expect(inputSize.height).toBeGreaterThanOrEqual(44);
  await expect(page.getByText('0:00 – 0:01 · 1.2 seconds selected',{exact:true})).toBeVisible();
  await expect(page.locator('form.upload-grid').getByRole('button',{name:'Upload clip',exact:true})).toBeEnabled();
  expect(await page.evaluate(()=>window.__klippQA.snapshot().uploads.length)).toBe(0);
  await expect.poll(()=>page.locator('.preview-frame video').evaluate(element=>(element as HTMLVideoElement).seeking)).toBe(false);
  await page.screenshot({path:testInfo.outputPath('trim-input-recovery.png'),fullPage:true});
});
test('fractional trim values can be submitted without native step mismatch',async({page},testInfo)=>{
  const range=await choose(page,testInfo);if(!range)return;
  await range.start.fill('0.2');await range.end.fill('1.23');
  await page.getByLabel('Title',{exact:true}).fill('Fractional trim');
  await page.locator('form.upload-grid').getByRole('button',{name:'Upload clip',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>window.__klippQA.snapshot().uploads.length),{timeout:8000}).toBe(1);
  const upload=await page.evaluate(()=>window.__klippQA.snapshot().uploads[0]);
  expect(upload.actualDuration).toBeCloseTo(1.03,1);
});
test('a rejected file can be selected again through the same native picker',async({page},testInfo)=>{
  let workers=0;page.on('worker',()=>workers++);
  await page.getByLabel('Choose video from your phone',{exact:true}).setInputFiles(unreadable);
  await expect(page.getByRole('status').filter({hasText:/format|read|video/i})).toBeVisible({timeout:60000});
  const attempts=workers;
  const chooser=page.waitForEvent('filechooser');
  await page.getByRole('button',{name:'Change',exact:true}).click();
  await(await chooser).setFiles(unreadable);
  await expect.poll(()=>workers,{timeout:5000}).toBeGreaterThan(attempts);
  await expect(page.getByRole('status').filter({hasText:'This video could not be read.'})).toBeVisible({timeout:60000});
  await expect(page.getByRole('button',{name:'Change',exact:true})).toBeEnabled();
  expect(await page.evaluate(()=>window.__klippQA.snapshot().uploads.length)).toBe(0);
  await page.screenshot({path:testInfo.outputPath('file-retry-guidance.png'),fullPage:true});
});

test('trim handles preserve the opposite edge instead of crossing or jumping',async({page},testInfo)=>{
  const range=await choose(page,testInfo);if(!range)return;
  await range.start.fill('0.2');await range.end.fill('1.4');await range.end.press('Enter');
  await page.getByRole('slider',{name:'Trim section start',exact:true}).press('End');
  await expect(range.start).toHaveValue('1.3');await expect(range.end).toHaveValue('1.4');
  await page.getByRole('slider',{name:'Trim section end',exact:true}).press('Home');
  await expect(range.start).toHaveValue('1.3');await expect(range.end).toHaveValue('1.4');
});

test('preview pauses on backgrounding, retains position, and respects the selected end',async({page},testInfo)=>{
  const range=await choose(page,testInfo);if(!range)return;
  await range.start.fill('0.2');await range.end.fill('1.4');await range.end.press('Enter');
  const video=page.locator('.preview-frame video');
  await video.evaluate(async element=>{const media=element as HTMLVideoElement;media.muted=true;await media.play();});
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(0.35);
  const position=await video.evaluate(element=>(element as HTMLVideoElement).currentTime);
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  const pausedPosition=await video.evaluate(element=>(element as HTMLVideoElement).currentTime);
  expect(pausedPosition).toBeGreaterThanOrEqual(position);
  await page.evaluate(()=>{delete(document as unknown as {hidden?:boolean}).hidden;document.dispatchEvent(new Event('visibilitychange'));});
  expect(await video.evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  expect(await video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(pausedPosition,2);
  await video.evaluate(element=>(element as HTMLVideoElement).play());
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).paused)).toBe(true);
  expect(await video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeCloseTo(1.4,2);
  await page.screenshot({path:testInfo.outputPath('preview-trimmed-end.png'),fullPage:true});
});
