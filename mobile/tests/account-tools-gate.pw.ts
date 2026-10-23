import {test,expect,type Page} from '@playwright/test';

declare global{interface Window{__operationCalls:number;__supportSends:number}}
const errors=new WeakMap<Page,string[]>();
test.beforeEach(async({page})=>{
  const seen:string[]=[];errors.set(page,seen);
  page.on('pageerror',error=>seen.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))seen.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if(url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:')void route.continue();
    else{seen.push('Unexpected external request');void route.abort('blockedbyclient');}
  });
});
test.afterEach(async({page})=>{expect(errors.get(page)).toEqual([]);await expect(page.locator('vite-error-overlay')).toHaveCount(0);});

test('unfinished account tools stay hidden and never load their API when disabled',async({page})=>{
  await page.addInitScript(()=>{
    window.__operationCalls=0;window.__klippQASetup=api=>{
      const usage=api.getUsage.bind(api);api.getUsage=async(signal)=>{window.__operationCalls++;return usage(signal);};
      const support=api.getSupportRequests.bind(api);api.getSupportRequests=async(signal)=>{window.__operationCalls++;return support(signal);};
      const reports=api.getOperatorReports.bind(api);api.getOperatorReports=async(cursor,signal)=>{window.__operationCalls++;return reports(cursor,signal);};
    };
  });
  for(const route of ['usage','help','manage']){
    await page.goto(`/qa.html#${route}`);
    await expect(page).toHaveURL(/#profile$/);
    await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();
    await expect(page.getByRole('button',{name:/Storage & usage|Help & requests/})).toHaveCount(0);
    expect(await page.evaluate(()=>window.__operationCalls)).toBe(0);
    await expect(page.locator('input[type=file]')).toHaveCount(0);
  }
});

test('prepared usage page renders measured capacity only when explicitly enabled',async({page},testInfo)=>{
  await page.goto('/qa.html?operations=1#profile');
  await page.getByRole('button',{name:/Storage & usage/}).click();
  await expect(page.getByRole('heading',{name:'Storage & usage',exact:true})).toBeVisible();
  await expect(page.getByText('1 of 5',{exact:true})).toBeVisible();
  await expect(page.getByRole('progressbar',{name:'Your upload slots'})).toHaveAttribute('value','1');
  await expect(page.getByRole('button',{name:'Review reports & requests',exact:true})).toHaveCount(0);
  await expect(page.locator('input[type=file]')).toHaveCount(0);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width+1);
  await page.screenshot({path:testInfo.outputPath('prepared-usage.png'),fullPage:true});
  await page.getByRole('button',{name:'Back to profile',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();
});

test('prepared private support preserves an unsent message and retries a failed request',async({page})=>{
  await page.addInitScript(()=>{
    window.__supportSends=0;window.__klippQASetup=api=>{
      const send=api.submitSupport.bind(api);
      api.submitSupport=async(category,body,signal)=>{window.__supportSends++;if(window.__supportSends===1)throw new Error('The request could not be sent. Try again.');return send(category,body,signal);};
    };
  });
  await page.goto('/qa.html?operations=1#help');
  await expect(page.getByRole('heading',{name:'Help & requests',exact:true})).toBeVisible();
  const message=page.getByRole('textbox',{name:'Message',exact:true});await message.fill('Please help me with my account.');
  await page.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Media',exact:true}).click();
  const discard=page.getByRole('dialog',{name:'Discard your message?',exact:true});await expect(discard).toBeVisible();
  await discard.getByRole('button',{name:'Keep editing',exact:true}).click();
  await expect(message).toHaveValue('Please help me with my account.');
  await page.getByRole('button',{name:'Send private request',exact:true}).click();
  await expect(page.getByRole('status').filter({hasText:'The request could not be sent. Try again.'})).toBeVisible();
  await expect(message).toHaveValue('Please help me with my account.');
  await page.getByRole('button',{name:'Send private request',exact:true}).click();
  await expect(message).toHaveValue('');await expect(page.getByText('Awaiting a reply',{exact:true})).toBeVisible();
  expect(await page.evaluate(()=>window.__supportSends)).toBe(2);
  await page.getByRole('button',{name:'Back to profile',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
