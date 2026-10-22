import {test,expect,type Page} from '@playwright/test';

const firstId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',ownId='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const health=new WeakMap<Page,string[]>();
declare global{interface Window{__feedNavigationQA?:{release?:()=>void;returned?:boolean;attempts?:number;failed?:boolean}}}

test.beforeEach(async({page})=>{
  const errors:string[]=[];health.set(page,errors);
  page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if(url.protocol==='http:'&&url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:')void route.continue();
    else{errors.push('Unexpected external request');void route.abort('blockedbyclient');}
  });
});
test.afterEach(async({page})=>{
  expect(health.get(page)).toEqual([]);
  await expect(page.locator('vite-error-overlay')).toHaveCount(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width+1);
});

test('Back restores the tapped poster and feed position, including browser Forward',async({page})=>{
  await page.goto('/qa.html#media');
  const opener=page.getByRole('button',{name:'Watch Night city sprint',exact:true});
  await opener.scrollIntoViewIfNeeded();
  const before=await page.evaluate(()=>window.scrollY);
  expect(before).toBeGreaterThan(100);
  await opener.click();await expect(page.locator('.watch h1')).toHaveText('Night city sprint');
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  await expect(opener).toBeFocused();
  await expect.poll(()=>page.evaluate(()=>window.scrollY)).toBeCloseTo(before,0);
  await page.goForward();await expect(page.locator('.watch h1')).toHaveText('Night city sprint');
  await page.goBack();await expect(opener).toBeFocused();
  await expect.poll(()=>page.evaluate(()=>window.scrollY)).toBeCloseTo(before,0);
});

test('keyboard-opened title and Comments return to their own controls',async({page})=>{
  await page.goto('/qa.html#media');
  const card=page.locator('article.clip-card').filter({has:page.getByRole('button',{name:'Watch Clean final round',exact:true})});
  const title=card.getByRole('button',{name:'Clean final round',exact:true});
  await title.focus();await title.press('Enter');await expect(page.locator('.watch h1')).toHaveText('Clean final round');
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();await expect(title).toBeFocused();
  const comments=card.getByRole('button',{name:'Comments',exact:true});
  await comments.click();await expect(page.locator('.watch h1')).toHaveText('Clean final round');
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();await expect(comments).toBeFocused();
});

async function longFeed(page:Page,failOlder=false){
  await page.addInitScript(fail=>{window.__klippQASetup=api=>{
    const template=api.clips[0];
    api.clips=Array.from({length:14},(_,index)=>({...template,id:`${(index+1).toString(16).padStart(8,'0')}-aaaa-4aaa-8aaa-aaaaaaaaaaaa`,title:`QA page clip ${index+1}`,createdAt:new Date(Date.UTC(2026,9,22,12,0,30-index)).toISOString()}));
    const original=api.getFeed.bind(api);let failOnce=fail;
    api.getFeed=async(cursor,mine,signal)=>{if(cursor&&failOnce){failOnce=false;throw Error('Synthetic older-page connection failure');}return original(cursor,mine,signal);};
  };},failOlder);
  await page.goto('/qa.html#media');await expect(page.locator('article.clip-card')).toHaveCount(12);
}
async function assertPageStart(page:Page){
  await expect(page.getByRole('tabpanel')).toBeFocused();
  // A short error page may fit without scrolling; its controls must still be above the fold.
  await expect.poll(async()=>{const box=await page.locator('.feed-toolbar').boundingBox();return !!box&&box.y>=-1&&box.y+box.height<page.viewportSize()!.height/3;}).toBe(true);
}

test('Older and Newer clips show the beginning of the requested page',async({page},testInfo)=>{
  await longFeed(page);
  const older=page.getByRole('button',{name:'Older clips',exact:true});
  await older.scrollIntoViewIfNeeded();expect(await page.evaluate(()=>window.scrollY)).toBeGreaterThan(1000);
  await older.click();await expect(page.locator('article.clip-card')).toHaveCount(2);
  await assertPageStart(page);await expect(page.getByRole('button',{name:'Watch QA page clip 13',exact:true})).toBeInViewport();
  await page.screenshot({path:testInfo.outputPath('older-clips-page-start.png'),fullPage:false,scale:'css'});
  await page.getByRole('button',{name:'Newer clips',exact:true}).click();
  await expect(page.locator('article.clip-card')).toHaveCount(12);await assertPageStart(page);
  await expect(page.getByRole('button',{name:'Watch QA page clip 1',exact:true})).toBeInViewport();
});

test('an older-page failure is brought into view and Retry keeps that page',async({page})=>{
  await longFeed(page,true);await page.getByRole('button',{name:'Older clips',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Could not load clips',exact:true})).toBeVisible();await assertPageStart(page);
  await expect(page.getByRole('button',{name:'Retry clips',exact:true})).toBeInViewport();
  await page.getByRole('button',{name:'Retry clips',exact:true}).click();
  await expect(page.locator('article.clip-card')).toHaveCount(2);
  await expect(page.getByRole('button',{name:'Watch QA page clip 13',exact:true})).toBeInViewport();
});

test('a failed direct clip link stays open and can retry without losing its route',async({page},testInfo)=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const original=api.getClip.bind(api);window.__feedNavigationQA={attempts:0};
    api.getClip=async(id,signal)=>{window.__feedNavigationQA!.attempts!++;if(window.__feedNavigationQA!.attempts===1)throw Error('Synthetic clip connection failure');return original(id,signal);};
  };});
  await page.goto(`/qa.html#watch/${firstId}`);
  await expect(page.getByRole('heading',{name:'Clip could not open',exact:true})).toBeVisible();
  await expect(page).toHaveURL(new RegExp(`#watch/${firstId}$`));
  await expect(page.getByRole('button',{name:'Back to clips',exact:true})).toBeVisible();
  await page.screenshot({path:testInfo.outputPath('direct-clip-retry.png'),fullPage:false,scale:'css'});
  await page.getByRole('button',{name:'Retry clip',exact:true}).click();
  await expect(page.locator('.watch h1')).toHaveText('Last-second save');
  await expect(page.getByRole('heading',{name:'Clip could not open',exact:true})).toHaveCount(0);
  await expect(page).toHaveURL(new RegExp(`#watch/${firstId}$`));
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  await expect(page).toHaveURL(/#media$/);await expect(page.getByRole('tab',{name:'Community',exact:true})).toBeFocused();
});

test('a late direct-link failure cannot close or replace a newer selected clip',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const original=api.getClip.bind(api);let delay=true;
    api.getClip=async(id,signal)=>{
      if(id==='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'&&delay){delay=false;await new Promise<void>(resolve=>{window.__feedNavigationQA={release:resolve};});window.__feedNavigationQA!.returned=true;throw Error('Synthetic obsolete clip failure');}
      return original(id,signal);
    };
  };});
  await page.goto(`/qa.html#watch/${firstId}`);
  await expect(page.getByRole('status').filter({hasText:'Opening clip...'})).toBeVisible();
  await page.getByRole('button',{name:'Watch Clean final round',exact:true}).click();
  await expect(page.locator('.watch h1')).toHaveText('Clean final round');
  await page.evaluate(()=>window.__feedNavigationQA!.release?.());
  await expect.poll(()=>page.evaluate(()=>window.__feedNavigationQA?.returned)).toBe(true);
  await expect(page).toHaveURL(new RegExp(`#watch/${ownId}$`));
  await expect(page.locator('.watch h1')).toHaveText('Clean final round');
  await expect(page.getByRole('heading',{name:'Clip could not open',exact:true})).toHaveCount(0);
});
