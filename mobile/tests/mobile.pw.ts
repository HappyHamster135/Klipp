import {test,expect,type Page,type TestInfo} from '@playwright/test';
const sourceUrl=new URL('../.qa/test-video.mp4',import.meta.url);
const source=decodeURIComponent(sourceUrl.pathname).replace(/^\/([a-z]:\/)/i,'$1');
const opusSource=decodeURIComponent(new URL('../.qa/test-video-opus.webm',import.meta.url).pathname).replace(/^\/([a-z]:\/)/i,'$1');
const health=new WeakMap<Page,{errors:string[];external:string[]}>();
const snapshot=(page:Page)=>page.evaluate(()=>window.__klippQA.snapshot());
const mediaNav=(page:Page)=>page.getByRole('navigation',{name:'Main navigation'});

test.beforeEach(async({page})=>{
  const state={errors:[] as string[],external:[] as string[]};health.set(page,state);
  page.on('pageerror',error=>state.errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))state.errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if(url.protocol==='http:'&&url.hostname==='127.0.0.1'){void route.continue();return;}
    if(url.protocol==='blob:'||url.protocol==='data:'){void route.continue();return;}
    state.external.push(url.origin);void route.abort('blockedbyclient');
  });
  await page.goto('/qa.html');
  await expect(page).toHaveTitle('Klipp · Local QA');
  await expect(page.getByText('Local test data · no online changes',{exact:true})).toBeVisible();
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'Watch Last-second save',exact:true})).toBeVisible();
});

test.afterEach(async({page})=>{
  expect(await page.locator('vite-error-overlay').count()).toBe(0);
  expect(health.get(page)?.external).toEqual([]);
  expect(health.get(page)?.errors).toEqual([]);
});

async function assertNoOverflow(page:Page){
  const width=page.viewportSize()!.width;
  expect(await page.evaluate(()=>document.documentElement.scrollWidth),`Layout must fit the configured ${width}px mobile viewport`).toBeLessThanOrEqual(width+1);
}
async function startPlaying(page:Page){
  const video=page.locator('.watch-player video');
  await expect(video).toBeVisible();
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(2);
  await video.evaluate(async element=>{const video=element as HTMLVideoElement;video.muted=true;await video.play();});
  await expect.poll(()=>video.evaluate(element=>(element as HTMLVideoElement).currentTime)).toBeGreaterThan(0.35);
  expect(await video.evaluate(element=>(element as HTMLVideoElement).videoWidth)).toBeGreaterThan(0);
  return video;
}
async function chooseSource(page:Page,testInfo:TestInfo,path=source){
  await mediaNav(page).getByRole('button',{name:'Upload',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Upload clip',exact:true})).toBeVisible();
  await page.getByLabel('Choose video from your phone',{exact:true}).setInputFiles(path);
  const start=page.getByLabel('Section start in seconds',{exact:true});
  const supported=await page.evaluate(()=>typeof VideoDecoder==='function'&&typeof VideoEncoder==='function');
  if(!supported){
    await expect(page.getByRole('status').filter({hasText:/cannot read|cannot prepare|could not prepare|cannot create/i})).toBeVisible({timeout:60000});
    testInfo.annotations.push({type:'capability',description:'This desktop WebKit/Chromium port lacks required WebCodecs; physical Safari/Android remains unverified.'});
    await page.screenshot({path:testInfo.outputPath('unsupported-codec.png'),fullPage:true});
    testInfo.skip(true,'Encoding requires WebCodecs unavailable in this desktop browser port; the guidance path is tested separately.');
    return null;
  }
  await expect(start).toBeVisible({timeout:60000});
  return{start,end:page.getByLabel('Section end in seconds',{exact:true})};
}
async function disableNativeAac(page:Page){
  // Redirect only this fixture page's preparation workers; production has no test flag.
  await page.evaluate(()=>{
    const NativeWorker=window.Worker;
    window.Worker=class extends NativeWorker{
      constructor(url:string|URL,options?:WorkerOptions){
        super(String(url).includes('/src/media/prepare.worker.ts')
          ? new URL('/tests/native-aac-disabled.worker.ts?worker_file&type=module',location.href)
          : url,options);
      }
    };
  });
}

test('first viewport is readable and local feed controls work',async({page},testInfo)=>{
  await assertNoOverflow(page);
  await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.screenshot({path:testInfo.outputPath('media-first-viewport.png'),fullPage:false});
  await page.getByRole('tab',{name:'My uploads',exact:true}).click();
  await expect(page.locator('article.clip-card')).toHaveCount(1);
  await expect(page.getByRole('button',{name:'Watch Clean final round',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Actions for Clean final round',exact:true}).click();
  await page.getByRole('dialog',{name:'Clip actions',exact:true}).getByRole('button',{name:'Delete shared clip',exact:true}).click();
  await page.getByRole('dialog',{name:'Delete shared clip?',exact:true}).getByRole('button',{name:'Delete shared clip',exact:true}).click();
  await expect(page.getByRole('heading',{name:'No uploads yet',exact:true})).toBeVisible();
  expect((await snapshot(page)).clips.some(clip=>clip.title==='Clean final round')).toBe(false);
});

test('decoded playback advances, fullscreen returns to same source, and comments work',async({page},testInfo)=>{
  await page.getByRole('button',{name:'Watch Last-second save',exact:true}).click();
  const video=await startPlaying(page);
  await assertNoOverflow(page);
  await page.screenshot({path:testInfo.outputPath('watch-and-comments.png'),fullPage:true});
  const sourceBefore=await video.getAttribute('src');
  const requestsBefore=(await snapshot(page)).playbackRequests.length;
  const fullscreen=await video.evaluate(element=>({request:typeof(element as HTMLVideoElement).requestFullscreen==='function',enabled:document.fullscreenEnabled}));
  if(fullscreen.request&&fullscreen.enabled){
    await page.getByRole('button',{name:'Fullscreen video',exact:true}).click();
    await expect.poll(()=>page.evaluate(()=>document.fullscreenElement?.tagName??null)).toBe('VIDEO');
    await page.keyboard.press('Escape');
    await expect.poll(()=>page.evaluate(()=>document.fullscreenElement?.tagName??null)).toBe(null);
    await expect(video).toHaveAttribute('src',sourceBefore!);
    expect((await snapshot(page)).playbackRequests).toHaveLength(requestsBefore);
  }else testInfo.annotations.push({type:'capability',description:'Native video fullscreen cannot be asserted by this emulated browser port; real-device fullscreen is unverified.'});
  const watch=page.locator('.watch-main');
  await watch.getByRole('button',{name:/^Like(?: ·|$)/}).click();
  await expect(watch.getByRole('button',{name:/^Liked/})).toHaveAttribute('aria-pressed','true');
  await page.getByLabel('Add a comment',{exact:true}).fill('Great timing from my phone.');
  await page.getByRole('button',{name:'Post comment',exact:true}).click();
  await expect(page.locator('article.comment').filter({hasText:'Great timing from my phone.'})).toBeVisible();
  await page.getByRole('button',{name:'Delete your comment',exact:true}).click();
  await page.getByRole('dialog',{name:'Delete your comment?',exact:true}).getByRole('button',{name:'Delete comment',exact:true}).click();
  await expect(page.locator('article.comment').filter({hasText:'Great timing from my phone.'})).toHaveCount(0);
});

test('report, block, profile save, and unblock are reversible local actions',async({page})=>{
  await page.getByRole('button',{name:'Actions for Last-second save',exact:true}).click();
  await page.getByRole('button',{name:'Report clip',exact:true}).click();
  await page.getByLabel('Reason',{exact:true}).fill('Synthetic QA report, local fixture only.');
  await page.getByRole('button',{name:'Send report',exact:true}).click();
  expect((await snapshot(page)).reports).toHaveLength(1);
  await page.getByRole('button',{name:'Actions for Last-second save',exact:true}).click();
  await page.getByRole('button',{name:'Block @alex',exact:true}).click();
  await page.getByRole('button',{name:'Block user',exact:true}).click();
  await expect(page.getByRole('button',{name:'Watch Last-second save',exact:true})).toHaveCount(0);
  await mediaNav(page).getByRole('button',{name:'Profile',exact:true}).click();
  await page.getByLabel('Display name',{exact:true}).fill('Sam QA');
  await page.getByRole('button',{name:'Save profile',exact:true}).click();
  await expect(page.getByText('Profile saved.',{exact:true})).toBeVisible();
  expect((await snapshot(page)).profile?.displayName).toBe('Sam QA');
  await page.getByRole('button',{name:'Blocked users',exact:true}).click();
  await expect(page.locator('.blocked')).toContainText('@alex');
  await page.getByRole('button',{name:'Unblock',exact:true}).click();
  await expect(page.locator('.blocked')).toContainText('No blocked users.');
  await mediaNav(page).getByRole('button',{name:'Media',exact:true}).click();
  await expect(page.getByRole('button',{name:'Watch Last-second save',exact:true})).toBeVisible();
});

test('fixture email-code sign-in survives synchronous account publication',async({page})=>{
  await mediaNav(page).getByRole('button',{name:'Profile',exact:true}).click();
  await page.getByRole('button',{name:'Sign out',exact:true}).click();
  await page.getByLabel('Email',{exact:true}).fill('returning@example.test');
  await page.getByRole('button',{name:'Send sign-in code',exact:true}).click();
  await page.getByLabel('Email code',{exact:true}).fill('123456');
  await page.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'Watch Last-second save',exact:true})).toBeVisible();
  await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toHaveCount(0);
  await expect(page.getByText(/abort|canceled|could not connect/i)).toHaveCount(0);
});

test('upload touch targets stay exposed above navigation',async({page},testInfo)=>{
  const range=await chooseSource(page,testInfo);if(!range)return;
  await page.getByLabel('Description',{exact:false}).fill('Touch target geometry, local fixture only.');
  const audio=page.getByRole('switch',{name:'Include audio',exact:true});
  const capture=()=>audio.evaluate(input=>{
    const rect=(element:Element|null)=>{if(!element)return null;const r=element.getBoundingClientRect();return{x:r.x,y:r.y,width:r.width,height:r.height,right:r.right,bottom:r.bottom};};
    const bounds=input.getBoundingClientRect(),x=bounds.x+bounds.width/2,y=bounds.y+bounds.height/2;
    const hit=document.elementFromPoint(x,y);
    return{switch:rect(input),textarea:rect(document.querySelector('.upload-fields textarea')),navigation:rect(document.querySelector('.bottom-nav')),pointer:{x,y},hit:{tag:hit?.tagName,className:hit?.className},exposed:hit===input||input.contains(hit),scrollY,viewport:{width:innerWidth,height:innerHeight,documentWidth:document.documentElement.scrollWidth,visualWidth:visualViewport?.width,visualHeight:visualViewport?.height,visualScale:visualViewport?.scale,visualOffsetTop:visualViewport?.offsetTop},overflow:[...document.querySelectorAll('main *')].map(element=>({tag:element.tagName,className:element.className,...rect(element)!})).filter(element=>element.right>391||element.x< -1).slice(0,20)};
  });
  const beforeScroll=await capture();
  await audio.evaluate(element=>element.scrollIntoView({block:'center',inline:'nearest'}));
  const afterScroll=await capture();
  await page.screenshot({path:testInfo.outputPath('upload-touch-targets.png'),fullPage:true});
  const afterScreenshot=await capture();
  const geometry={beforeScroll,afterScroll,afterScreenshot};
  await testInfo.attach('audio-touch-target',{body:JSON.stringify(geometry,null,2),contentType:'application/json'});
  expect(afterScreenshot.exposed,JSON.stringify(geometry)).toBe(true);
  await assertNoOverflow(page);
  await audio.setChecked(false);await expect(audio).not.toBeChecked();
  const submit=page.locator('form.upload-grid').getByRole('button',{name:'Upload clip',exact:true});
  await submit.evaluate(element=>element.scrollIntoView({block:'center',inline:'nearest'}));
  expect(await submit.evaluate(button=>{const bounds=button.getBoundingClientRect(),hit=document.elementFromPoint(bounds.x+bounds.width/2,bounds.y+bounds.height/2);return hit===button||button.contains(hit);})).toBe(true);
});

for(const includeAudio of [true,false])test(`real worker trims and uploads a playable ${includeAudio?'audio':'silent'} share`,async({page},testInfo)=>{
  const range=await chooseSource(page,testInfo);if(!range)return;
  await range.start.fill('0.2');await range.end.fill('1.4');
  await page.getByLabel('Title',{exact:true}).fill(includeAudio?'Trimmed with audio':'Trimmed without audio');
  await page.getByLabel('Description',{exact:false}).fill('Actual worker output; local fixture upload.');
  const audio=page.getByRole('switch',{name:'Include audio',exact:true});
  await expect(audio).toBeVisible();await audio.evaluate(element=>element.scrollIntoView({block:'center',inline:'nearest'}));await audio.setChecked(includeAudio);
  await expect(page.getByText('0:00 – 0:01 · 1.2 seconds selected',{exact:true})).toBeVisible();
  await assertNoOverflow(page);
  await page.screenshot({path:testInfo.outputPath(`trim-${includeAudio?'audio':'silent'}.png`),fullPage:true});
  await page.locator('form.upload-grid').getByRole('button',{name:'Upload clip',exact:true}).click();
  await expect.poll(async()=>(await snapshot(page)).uploads.length,{timeout:60000}).toBe(1);
  const upload=(await snapshot(page)).uploads[0];
  await testInfo.attach('prepared-upload-metadata',{body:JSON.stringify(upload,null,2),contentType:'application/json'});
  expect(upload.declaredDuration).toBeCloseTo(1.2,1);expect(upload.actualDuration).toBeCloseTo(1.2,1);
  expect(upload.width).toBeLessThanOrEqual(1280);expect(upload.height).toBeLessThanOrEqual(720);
  expect(upload.videoCodec).toBe('avc');expect(upload.audioTracks).toBe(includeAudio?1:0);
  expect(upload.videoBytes).toBeGreaterThan(100);expect(upload.videoBytes).toBeLessThanOrEqual(16*1024*1024);
  expect(upload.posterBytes).toBeGreaterThan(100);expect(upload.posterBytes).toBeLessThanOrEqual(256*1024);
  expect(upload.videoType).toBe('video/mp4');expect(upload.posterType).toBe('image/jpeg');
  await page.getByRole('button',{name:`Watch ${upload.title}`,exact:true}).click();
  const video=await startPlaying(page);await expect(video).toHaveAttribute('src',upload.videoUrl);
});

test('without native AAC encoding, real worker copies only primary AAC packets inside the trim',async({page},testInfo)=>{
  await disableNativeAac(page);
  const range=await chooseSource(page,testInfo);if(!range)return;
  const start=0.2,end=1.4;
  await range.start.fill(String(start));await range.end.fill(String(end));
  await page.getByLabel('Title',{exact:true}).fill('AAC packet-copy section');
  await page.locator('form.upload-grid').getByRole('button',{name:'Upload clip',exact:true}).click();
  await expect.poll(async()=>(await snapshot(page)).uploads.length,{timeout:60000}).toBe(1);
  const upload=(await snapshot(page)).uploads[0];
  const audit=await page.evaluate(async url=>({source:await window.__klippQA.inspectAudio('/.qa/test-video.mp4'),output:await window.__klippQA.inspectAudio(url)}),upload.videoUrl);
  expect(audit.source).toHaveLength(2);expect(audit.source[0].codec).toBe('aac');
  expect(audit.output).toHaveLength(1);expect(audit.output[0].codec).toBe('aac');
  const eligible=audit.source[0].packets.filter(packet=>packet.timestamp>=start-1e-6&&packet.timestamp+packet.duration<=end+1e-6);
  expect(eligible.length).toBeGreaterThan(20);
  expect(audit.output[0].packets.map(packet=>packet.hash),'Every encoded AAC packet must exactly match the primary source packets fully inside the selected range').toEqual(eligible.map(packet=>packet.hash));
  expect(audit.output[0].packets[0].timestamp).toBeGreaterThanOrEqual(-1e-6);
  const last=audit.output[0].packets.at(-1)!;
  expect(last.timestamp+last.duration).toBeLessThanOrEqual(end-start+1e-6);
  expect(upload.audioTracks).toBe(1);expect(upload.videoCodec).toBe('avc');expect(upload.actualDuration).toBeCloseTo(end-start,1);
  await testInfo.attach('aac-copy-boundaries',{body:JSON.stringify({upload,selectedRange:{start,end},eligibleSourcePackets:eligible,outputPackets:audit.output[0].packets},null,2),contentType:'application/json'});
  await page.getByRole('button',{name:`Watch ${upload.title}`,exact:true}).click();
  const video=await startPlaying(page);await expect(video).toHaveAttribute('src',upload.videoUrl);
  await page.screenshot({path:testInfo.outputPath('aac-copy-playback.png'),fullPage:true});
});

test('without native AAC encoding, non-AAC audio gives a useful error and can be excluded',async({page},testInfo)=>{
  await disableNativeAac(page);
  const range=await chooseSource(page,testInfo,opusSource);if(!range)return;
  await range.start.fill('0.2');await range.end.fill('1.4');
  await page.getByLabel('Title',{exact:true}).fill('Opus audio guidance');
  await page.locator('form.upload-grid').getByRole('button',{name:'Upload clip',exact:true}).click();
  await expect(page.getByRole('status').filter({hasText:'This browser cannot convert this audio to AAC.'})).toBeVisible({timeout:60000});
  expect((await snapshot(page)).uploads).toHaveLength(0);
  await page.screenshot({path:testInfo.outputPath('aac-unavailable-guidance.png'),fullPage:true});
  const audio=page.getByRole('switch',{name:'Include audio',exact:true});
  await audio.evaluate(element=>element.scrollIntoView({block:'center',inline:'nearest'}));await audio.setChecked(false);
  await page.locator('form.upload-grid').getByRole('button',{name:'Upload clip',exact:true}).click();
  await expect.poll(async()=>(await snapshot(page)).uploads.length,{timeout:60000}).toBe(1);
  const upload=(await snapshot(page)).uploads[0];expect(upload.audioTracks).toBe(0);expect(upload.videoCodec).toBe('avc');
  await page.getByRole('button',{name:`Watch ${upload.title}`,exact:true}).click();await startPlaying(page);
});

test('canceling preparation keeps the original and publishes no fixture upload',async({page},testInfo)=>{
  const range=await chooseSource(page,testInfo);if(!range)return;
  await page.locator('form.upload-grid').getByRole('button',{name:'Upload clip',exact:true}).click();
  const cancel=page.getByRole('button',{name:'Cancel upload',exact:true});
  await cancel.evaluate(element=>element.scrollIntoView({block:'center',inline:'nearest'}));
  await cancel.click();
  await expect(page.getByRole('status').filter({hasText:/Upload canceled/})).toBeVisible({timeout:60000});
  expect((await snapshot(page)).uploads).toHaveLength(0);
  await expect(page.getByLabel('Title',{exact:true})).toBeEnabled();
});

test('unsupported browser ports show useful preparation guidance',async({page},testInfo)=>{
  const supported=await page.evaluate(()=>typeof VideoDecoder==='function'&&typeof VideoEncoder==='function');
  testInfo.skip(supported,'This browser supports the worker codecs; actual output is covered by the encoding tests.');
  await mediaNav(page).getByRole('button',{name:'Upload',exact:true}).click();
  await page.getByLabel('Choose video from your phone',{exact:true}).setInputFiles(source);
  await expect(page.getByRole('status').filter({hasText:/cannot read|cannot prepare|could not prepare|cannot create/i})).toBeVisible({timeout:60000});
  expect((await snapshot(page)).uploads).toHaveLength(0);
  await page.screenshot({path:testInfo.outputPath('unsupported-browser-guidance.png'),fullPage:true});
});
