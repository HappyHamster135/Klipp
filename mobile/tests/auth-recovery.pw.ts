import {test,expect,type Page} from '@playwright/test';

const health=new WeakMap<Page,{errors:string[];external:string[]}>();
test.beforeEach(async({page})=>{
  const state={errors:[] as string[],external:[] as string[]};health.set(page,state);
  page.on('pageerror',error=>state.errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))state.errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if((url.protocol==='http:'&&url.hostname==='127.0.0.1')||url.protocol==='blob:'||url.protocol==='data:')return route.continue();
    state.external.push(url.origin);return route.abort('blockedbyclient');
  });
  await page.goto('/qa.html');
  await expect(page).toHaveTitle('Klipp · Local QA');
  await expect(page).toHaveURL(/\/qa\.html(?:#.*)?$/);
  await expect(page.getByText('Local test data · no online changes',{exact:true})).toBeVisible();
  await page.getByRole('navigation',{name:'Main navigation',exact:true}).getByRole('button',{name:'Profile',exact:true}).click();
  await page.getByRole('button',{name:'Sign out',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Sign in to Klipp',exact:true})).toBeVisible();
});
test.afterEach(async({page})=>{
  expect(health.get(page)?.external).toEqual([]);
  expect(health.get(page)?.errors).toEqual([]);
  expect(await page.locator('vite-error-overlay').count()).toBe(0);
});

async function requestCode(page:Page){
  await page.getByLabel('Email',{exact:true}).fill('returning@example.test');
  await page.getByRole('button',{name:'Send sign-in code',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Check your email',exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:/^Resend in \d+s$/})).toBeDisabled();
}

test('returning from Mail expires the resend wait by elapsed time and clears the old code',async({page})=>{
  const time=Date.parse('2026-10-22T10:00:00Z');
  await page.clock.install({time:new Date(time)});
  await requestCode(page);
  await page.getByLabel('Email code',{exact:true}).fill('123456');
  // Change the clock without running the backgrounded tab's timer callbacks.
  await page.clock.setSystemTime(new Date(time+61000));
  await page.evaluate(()=>document.dispatchEvent(new Event('visibilitychange')));
  await expect(page.getByRole('button',{name:'Resend code',exact:true})).toBeEnabled();
  await page.getByRole('button',{name:'Resend code',exact:true}).click();
  await expect(page.getByLabel('Email code',{exact:true})).toHaveValue('');
  await expect(page.getByRole('button',{name:/^Resend in \d+s$/})).toBeDisabled();
});

test('changing email cannot accidentally resend to the same address during its wait',async({page})=>{
  await requestCode(page);
  await page.getByRole('button',{name:'Change email',exact:true}).click();
  await expect(page.getByLabel('Email',{exact:true})).toHaveValue('returning@example.test');
  await expect(page.getByRole('button',{name:/^Send code in \d+s$/})).toBeDisabled();
  await page.getByLabel('Email',{exact:true}).fill('RETURNING@example.test');
  await expect(page.getByRole('button',{name:/^Send code in \d+s$/})).toBeDisabled();
  await page.getByLabel('Email',{exact:true}).fill('new-player@example.test');
  await expect(page.getByRole('button',{name:'Send sign-in code',exact:true})).toBeEnabled();
  await page.getByRole('button',{name:'New to Klipp? Create an account',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Create your Klipp account',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Send confirmation code',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Check your email',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Change email',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Create your Klipp account',exact:true})).toBeVisible();
});

test('phone sign-in fields offer code autofill and return to Media after verification',async({page},testInfo)=>{
  const email=page.getByLabel('Email',{exact:true});
  await expect(email).toHaveAttribute('autocomplete','email');
  await expect(email).toHaveAttribute('autocapitalize','none');
  await expect(page.getByLabel('Remember me on this device',{exact:true})).not.toBeChecked();
  await requestCode(page);
  const code=page.getByLabel('Email code',{exact:true});
  await expect(code).toHaveAttribute('autocomplete','one-time-code');
  await expect(code).toHaveAttribute('inputmode','numeric');
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width+1);
  await page.screenshot({path:testInfo.outputPath('email-code-screen.png'),fullPage:true});
  await code.fill('123 456');
  await expect(code).toHaveValue('123456');
  await page.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'Watch Last-second save',exact:true})).toBeVisible();
  const width=page.viewportSize()!.width;
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(width+1);
});
