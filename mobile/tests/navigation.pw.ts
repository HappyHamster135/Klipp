import {test,expect} from '@playwright/test';
const ownId='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const source=decodeURIComponent(new URL('../.qa/test-video.mp4',import.meta.url).pathname).replace(/^\/([a-z]:\/)/i,'$1');

test.beforeEach(async({page})=>{
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if(url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:')void route.continue();
    else void route.abort('blockedbyclient');
  });
});

test('phone Back and Forward close and reopen a clip while preserving My uploads',async({page})=>{
  await page.goto('/qa.html#media');
  await page.getByRole('tab',{name:'My uploads',exact:true}).click();
  await expect(page.locator('article.clip-card')).toHaveCount(1);
  await page.getByRole('button',{name:'Watch Clean final round',exact:true}).click();
  await expect(page).toHaveURL(new RegExp(`#watch/${ownId}$`));
  await expect(page.locator('.watch')).toBeVisible();
  const entries=await page.evaluate(()=>history.length);
  await page.goBack();
  await expect(page.locator('.watch')).toHaveCount(0);
  await expect(page.getByRole('tab',{name:'My uploads',exact:true})).toHaveAttribute('aria-selected','true');
  await expect(page.locator('article.clip-card')).toHaveCount(1);
  await page.goForward();await expect(page.locator('.watch')).toBeVisible();
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  await expect(page).toHaveURL(/#media$/);await expect(page.locator('.watch')).toHaveCount(0);
  expect(await page.evaluate(()=>history.length)).toBe(entries);
  await page.goForward();await expect(page.locator('.watch')).toBeVisible();
});

test('a direct Watch link loads the clip and Back to clips replaces that entry',async({page})=>{
  await page.goto(`/qa.html#watch/${ownId}`);
  await expect(page.locator('.watch')).toBeVisible();
  await expect(page.locator('.watch h1')).toHaveText('Clean final round');
  const entries=await page.evaluate(()=>history.length);
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  await expect(page).toHaveURL(/#media$/);await expect(page.locator('.watch')).toHaveCount(0);
  expect(await page.evaluate(()=>history.length)).toBe(entries);
});

test('Back protects an unsaved selected section and discard returns to Media',async({page})=>{
  await page.goto('/qa.html#media');
  await page.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Upload',exact:true}).click();
  await expect(page).toHaveURL(/#upload$/);
  await page.getByLabel('Choose video from your phone',{exact:true}).setInputFiles(source);
  await expect(page.locator('.source-summary strong')).toBeVisible();
  await page.goBack();
  const dialog=page.getByRole('dialog',{name:'Discard this upload?',exact:true});
  await expect(dialog).toBeVisible();await expect(page).toHaveURL(/#upload$/);
  await dialog.getByRole('button',{name:'Keep editing',exact:true}).click();
  await expect(dialog).toHaveCount(0);await expect(page.locator('.source-summary strong')).toBeVisible();
  await page.goBack();await expect(dialog).toBeVisible();
  await dialog.getByRole('button',{name:'Discard upload',exact:true}).click();
  await expect(page).toHaveURL(/#media$/);await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
});
