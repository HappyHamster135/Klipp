import {test,expect,type Page} from '@playwright/test';

declare global{interface Window{__sessionRecovery:{restores:number;emails:number;release?:()=>void}}}
const health=new WeakMap<Page,string[]>();
test.beforeEach(async({page})=>{
  const errors:string[]=[];health.set(page,errors);
  page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    return url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:'?route.continue():route.abort();
  });
});
test.afterEach(async({page})=>{
  expect(health.get(page)).toEqual([]);
  await expect(page.locator('vite-error-overlay')).toHaveCount(0);
});

test('saved sign-in retries without an email and preserves a direct clip route',async({page},testInfo)=>{
  await page.addInitScript(()=>{
    window.__klippQASetup=api=>{
      const user=api.user,profile=api.profile;
      api.user=null;api.profile=null;
      const state=window.__sessionRecovery={restores:0,emails:0};
      api.sendEmailCode=async()=>{state.emails++;};
      api.restoreSession=async signal=>{
        signal?.throwIfAborted();state.restores++;
        if(state.restores===1)throw new Error('Could not connect to Klipp. Check your connection and try again.');
        api.user=user;api.profile=profile;return user;
      };
    };
  });
  await page.goto('/qa.html#watch/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
  await expect(page.getByRole('heading',{name:'Saved sign-in could not be checked',exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'Retry saved sign-in',exact:true})).toBeEnabled();
  await page.screenshot({path:testInfo.outputPath('saved-sign-in-retry.png'),fullPage:false,scale:'css'});
  await page.getByRole('button',{name:'Retry saved sign-in',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Last-second save',exact:true})).toBeVisible();
  await expect(page).toHaveURL(/#watch\/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa$/);
  expect(await page.evaluate(()=>({restores:window.__sessionRecovery.restores,emails:window.__sessionRecovery.emails}))).toEqual({restores:2,emails:0});
  await expect(page.getByRole('heading',{name:'Saved sign-in could not be checked',exact:true})).toHaveCount(0);
});

test('one pending restore preserves an entered email and a null result returns to normal sign-in',async({page})=>{
  await page.addInitScript(()=>{
    window.__klippQASetup=api=>{
      api.user=null;api.profile=null;
      const state:Window['__sessionRecovery']=window.__sessionRecovery={restores:0,emails:0};
      api.sendEmailCode=async()=>{state.emails++;};
      api.restoreSession=async signal=>{
        state.restores++;
        if(state.restores===1)throw new Error('Saved sign-in could not connect.');
        await new Promise<void>(resolve=>{state.release=resolve;});signal?.throwIfAborted();return null;
      };
    };
  });
  await page.goto('/qa.html');
  await page.getByLabel('Email',{exact:true}).fill('returning@example.test');
  await page.getByRole('button',{name:'Retry saved sign-in',exact:true}).evaluate(button=>{
    (button as HTMLButtonElement).click();(button as HTMLButtonElement).click();
  });
  await expect(page.getByRole('button',{name:'Checking saved sign-in...',exact:true})).toBeDisabled();
  await expect(page.getByLabel('Email',{exact:true})).toHaveValue('returning@example.test');
  await expect(page.getByLabel('Email',{exact:true})).toBeDisabled();
  expect(await page.evaluate(()=>window.__sessionRecovery.restores)).toBe(2);
  await page.evaluate(()=>window.__sessionRecovery.release?.());
  await expect(page.getByRole('heading',{name:'Saved sign-in could not be checked',exact:true})).toHaveCount(0);
  await expect(page.getByLabel('Email',{exact:true})).toBeEnabled();
  await expect(page.getByLabel('Email',{exact:true})).toHaveValue('returning@example.test');
  expect(await page.evaluate(()=>window.__sessionRecovery.emails)).toBe(0);
});

test('failed retry retains the email and cannot race an email-code request',async({page})=>{
  await page.addInitScript(()=>{
    window.__klippQASetup=api=>{
      api.user=null;api.profile=null;
      const state:Window['__sessionRecovery']=window.__sessionRecovery={restores:0,emails:0};
      api.restoreSession=async()=>{state.restores++;throw new Error('Could not connect. Please try again.');};
      api.sendEmailCode=async()=>{state.emails++;await new Promise<void>(resolve=>{state.release=resolve;});};
    };
  });
  await page.goto('/qa.html');
  await page.getByLabel('Email',{exact:true}).fill('returning@example.test');
  await page.getByRole('button',{name:'Retry saved sign-in',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>window.__sessionRecovery.restores)).toBe(2);
  await expect(page.getByRole('button',{name:'Retry saved sign-in',exact:true})).toBeEnabled();
  await expect(page.getByLabel('Email',{exact:true})).toHaveValue('returning@example.test');
  await page.getByRole('button',{name:'Send sign-in code',exact:true}).click();
  await expect(page.getByRole('button',{name:'Retry saved sign-in',exact:true})).toBeDisabled();
  expect(await page.evaluate(()=>window.__sessionRecovery.restores)).toBe(2);
  await page.evaluate(()=>window.__sessionRecovery.release?.());
  await expect(page.getByRole('heading',{name:'Check your email',exact:true})).toBeVisible();
  expect(await page.evaluate(()=>window.__sessionRecovery.emails)).toBe(1);
});

test('a fresh email sign-in and intentional sign-out clear an earlier restore failure',async({page})=>{
  await page.addInitScript(()=>{
    window.__klippQASetup=api=>{
      api.user=null;api.profile=null;
      const state=window.__sessionRecovery={restores:0,emails:0};
      api.restoreSession=async()=>{state.restores++;throw new Error('Saved sign-in could not connect.');};
      api.sendEmailCode=async()=>{state.emails++;};
    };
  });
  await page.goto('/qa.html');
  await expect(page.getByRole('heading',{name:'Saved sign-in could not be checked',exact:true})).toBeVisible();
  await page.getByLabel('Email',{exact:true}).fill('returning@example.test');
  await page.getByRole('button',{name:'Send sign-in code',exact:true}).click();
  await page.getByLabel('Email code',{exact:true}).fill('123456');
  await page.getByRole('button',{name:'Continue',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
  await page.getByRole('navigation',{name:'Main navigation',exact:true}).getByRole('button',{name:'Profile',exact:true}).click();
  await page.getByRole('button',{name:'Sign out',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Sign in to Klipp',exact:true})).toBeVisible();
  await expect(page.getByRole('heading',{name:'Saved sign-in could not be checked',exact:true})).toHaveCount(0);
  expect(await page.evaluate(()=>window.__sessionRecovery.restores)).toBe(1);
});
