import {test,expect,type Page} from '@playwright/test';
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
  await expect(page.getByText('Upload clips from Klipp on your PC.',{exact:true})).toBeVisible();
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
