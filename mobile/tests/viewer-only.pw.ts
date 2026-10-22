import {test,expect,type Page} from '@playwright/test';
const pcMessage='Upload clips from Klipp on your PC.';
const health=new WeakMap<Page,string[]>();
test.beforeEach(async({page})=>{
  const errors:string[]=[];health.set(page,errors);
  page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if((url.protocol==='http:'&&url.hostname==='127.0.0.1')||url.protocol==='blob:'||url.protocol==='data:')void route.continue();
    else{errors.push('Unexpected external request');void route.abort('blockedbyclient');}
  });
});
test.afterEach(async({page})=>{
  expect(health.get(page)).toEqual([]);
  expect(await page.locator('vite-error-overlay').count()).toBe(0);
});
async function noUploadControls(page:Page){
  await expect(page.locator('input[type=file]')).toHaveCount(0);
  await expect(page.getByRole('button',{name:/^(Upload|Upload clip|Choose a video|Choose a clip)$/i})).toHaveCount(0);
}
test('Media and Profile offer viewing and interaction without a file picker or upload tab',async({page},testInfo)=>{
  let workers=0;page.on('worker',()=>workers++);
  await page.goto('/qa.html#media');
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
  await expect(page.getByText('Upload from Klipp on PC. Watch and connect here.',{exact:true})).toBeVisible();
  const navigation=page.getByRole('navigation',{name:'Main navigation'});
  await expect(navigation.getByRole('button')).toHaveCount(2);
  await noUploadControls(page);
  await navigation.getByRole('button',{name:'Profile',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();
  await noUploadControls(page);expect(workers).toBe(0);
  await navigation.getByRole('button',{name:'Media',exact:true}).click();
  await page.screenshot({path:testInfo.outputPath('viewer-media.png'),fullPage:false});
});
test('old upload bookmarks and hash navigation recover to Media with PC guidance',async({page})=>{
  const notice=page.getByRole('status').filter({hasText:pcMessage});
  await page.goto('/qa.html#upload');
  await expect(page).toHaveURL(/#media$/);
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
  await expect(notice).toBeVisible();
  await noUploadControls(page);
  await notice.getByRole('button',{name:'Dismiss message',exact:true}).click();
  await page.evaluate(()=>{location.hash='#upload';});
  await expect(page).toHaveURL(/#media$/);
  await expect(notice).toBeVisible();
  await noUploadControls(page);
});
test('empty Community and My uploads explain PC publishing without offering file selection',async({page},testInfo)=>{
  await page.goto('/qa.html?empty=1#media');
  await expect(page.getByRole('heading',{name:'No clips shared yet',exact:true})).toBeVisible();
  await expect(page.getByText(pcMessage,{exact:true})).toBeVisible();await noUploadControls(page);
  await page.getByRole('tab',{name:'My uploads',exact:true}).click();
  await expect(page.getByRole('heading',{name:'No uploads yet',exact:true})).toBeVisible();
  await expect(page.getByText(pcMessage,{exact:true})).toBeVisible();await noUploadControls(page);
  await page.screenshot({path:testInfo.outputPath('viewer-empty-pc-guidance.png'),fullPage:false});
});
