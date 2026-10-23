import {test,expect,type Page} from '@playwright/test';
declare global{interface Window{__opsCalls:{id:string;action:string;note:string}[];__opsRelease?:()=>void;__capsRelease?:()=>void}}
const errors=new WeakMap<Page,string[]>();
test.beforeEach(async({page})=>{
  const seen:string[]=[];errors.set(page,seen);page.on('pageerror',error=>seen.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))seen.push(message.text());});
  await page.route('**/*',route=>{const url=new URL(route.request().url());if(url.hostname==='127.0.0.1'||url.protocol==='blob:'||url.protocol==='data:')void route.continue();else{seen.push('Unexpected external request');void route.abort();}});
});
test.afterEach(async({page})=>{expect(errors.get(page)).toEqual([]);await expect(page.locator('vite-error-overlay')).toHaveCount(0);await expect(page.locator('input[type=file]')).toHaveCount(0);});
async function operator(page:Page,mode:'normal'|'delay'|'fail-first'='normal'){
  await page.addInitScript(({mode})=>{
    window.__opsCalls=[];window.__klippQASetup=api=>{
      api.capabilities.operator=true;
      const usage=api.getUsage.bind(api);
      api.getUsage=async signal=>{const result=await usage(signal);const unknown={used:null,limit:100,periodStart:null,periodEnd:null,observedAt:null,source:null};return{...result,operator:true,operations:{registeredUsers:4,databaseBytes:100000,databaseLimitBytes:500000000,openReports:2,openSupport:0,providerUsage:{cachedEgress:unknown,uncachedEgress:unknown,monthlyActiveUsers:unknown,emailDaily:{...unknown,used:95,periodStart:new Date(Date.now()-3600000).toISOString(),periodEnd:new Date(Date.now()+3600000).toISOString(),observedAt:new Date().toISOString(),source:'Verified provider dashboard'},emailMonthly:unknown}}};};
      const reports=[{id:'aaaaaaaa-0000-4000-8000-000000000001',clipId:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',title:'First report',hidden:false},{id:'aaaaaaaa-0000-4000-8000-000000000002',clipId:'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',title:'Second report',hidden:true}].map((item,index)=>({...item,reporterId:'33333333-3333-4333-8333-333333333333',reason:'Please review this clip',status:'open' as const,createdAt:new Date(Date.now()-index*1000).toISOString(),ownerUsername:'alex',reporterUsername:'mika',reviewNote:'',reviewedAt:null}));
      api.getOperatorReports=async()=>({items:structuredClone(reports),nextCursor:null});
      let release:()=>void=()=>{};const delayed=new Promise<void>(resolve=>{release=resolve;});window.__opsRelease=release;
      api.reviewClip=async(id,action,note,signal)=>{signal?.throwIfAborted();window.__opsCalls.push({id,action,note});if(mode==='delay')await delayed;if(mode==='fail-first'&&window.__opsCalls.length===1)throw new Error('Decision could not be saved. Retry.');};
    };
  },{mode});
}

test('server-disabled tools remain unavailable even with the build switch on',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{api.capabilities.enabled=false;api.getUsage=async()=>{throw new Error('Unexpected usage call');};};});
  await page.goto('/qa.html?operations=1#usage');await expect(page).toHaveURL(/#profile$/);
  await expect(page.getByRole('button',{name:/Storage & usage|Help & requests/})).toHaveCount(0);
});

test('a member cannot enter the moderator screen through its URL',async({page})=>{
  await page.goto('/qa.html?operations=1#manage');await expect(page).toHaveURL(/#profile$/);
  await expect(page.getByRole('heading',{name:'Reports & requests',exact:true})).toHaveCount(0);
});

test('late privileged capabilities cannot reappear after sign-out',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{api.getOperationsCapabilities=()=>new Promise(resolve=>{window.__capsRelease=()=>resolve({version:1,enabled:true,operator:true,accountDeletion:true});});};});
  await page.goto('/qa.html?operations=1#profile');await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Sign out',exact:true}).click();await page.evaluate(()=>window.__capsRelease?.());
  await expect(page.getByRole('heading',{name:'Sign in to Klipp',exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:/Storage & usage|Help & requests/})).toHaveCount(0);
});

test('incomplete verified profiles can request help and retry deletion without losing sign-in',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{api.profile!.username=null;api.pendingDeletionOnce=true;};});
  await page.goto('/qa.html?operations=1#help');await expect(page.getByRole('heading',{name:'Help & requests',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Delete my account',exact:true}).click();const dialog=page.getByRole('dialog',{name:'Delete your Klipp account?',exact:true});
  await dialog.getByRole('textbox',{name:'Username',exact:true}).fill('DELETE');await dialog.getByRole('button',{name:'Delete account',exact:true}).click();
  await expect(dialog.getByRole('status')).toContainText('cleanup is in progress');expect(await page.evaluate(()=>window.__klippQA.api.user!==null)).toBe(true);
  await dialog.getByRole('button',{name:'Cancel',exact:true}).click();await expect(page.getByRole('heading',{name:'Account cleanup in progress',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Retry account deletion',exact:true}).click();await dialog.getByRole('textbox',{name:'Username',exact:true}).fill('DELETE');await dialog.getByRole('button',{name:'Delete account',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Sign in to Klipp',exact:true})).toBeVisible();expect(await page.evaluate(()=>window.__klippQA.api.accountDeletionCalls)).toBe(2);
});

test('incomplete verified profiles can inspect usage without choosing a username',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{api.profile!.username=null;};});
  await page.goto('/qa.html?operations=1#usage');await expect(page.getByRole('heading',{name:'Storage & usage',exact:true})).toBeVisible();await expect(page).toHaveURL(/#usage$/);
  await page.getByRole('button',{name:'Back to profile',exact:true}).click();await expect(page.getByRole('button',{name:/Help & requests/})).toBeVisible();
});

test('per-report drafts survive a failed decision and hidden clips cannot be previewed',async({page})=>{
  await operator(page,'fail-first');await page.goto('/qa.html?operations=1#manage');
  const first=page.getByRole('article',{name:'Report: First report',exact:true}),second=page.getByRole('article',{name:'Report: Second report',exact:true});
  await first.getByRole('textbox',{name:'Review note',exact:true}).fill('This is the first decision.');await second.getByRole('textbox',{name:'Review note',exact:true}).fill('Keep this second draft.');
  await expect(second.getByRole('button',{name:'View clip',exact:true})).toBeDisabled();await expect(second.getByText('Hidden clips cannot be played here.',{exact:true})).toBeVisible();
  await first.getByRole('button',{name:'Keep clip',exact:true}).click();await expect(page.getByRole('status')).toContainText('Decision could not be saved');
  await expect(first.getByRole('textbox',{name:'Review note',exact:true})).toHaveValue('This is the first decision.');
  await first.getByRole('button',{name:'Keep clip',exact:true}).click();await expect(first.getByRole('textbox',{name:'Review note',exact:true})).toHaveValue('');
  await expect(second.getByRole('textbox',{name:'Review note',exact:true})).toHaveValue('Keep this second draft.');
  expect(await page.evaluate(()=>window.__opsCalls)).toEqual([{id:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',action:'keep',note:'This is the first decision.'},{id:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',action:'keep',note:'This is the first decision.'}]);
  await page.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Profile',exact:true}).click();const discard=page.getByRole('dialog',{name:'Discard your message?',exact:true});await expect(discard).toBeVisible();await discard.getByRole('button',{name:'Keep editing',exact:true}).click();await expect(second.getByRole('textbox',{name:'Review note',exact:true})).toHaveValue('Keep this second draft.');
});

test('hiding requires confirmation and blocks navigation while the decision saves',async({page})=>{
  await operator(page,'delay');await page.goto('/qa.html?operations=1#manage');const first=page.getByRole('article',{name:'Report: First report',exact:true});
  await first.getByRole('textbox',{name:'Review note',exact:true}).fill('Reason for hiding this clip.');await first.getByRole('button',{name:'Hide clip',exact:true}).click();
  const confirmation=page.getByRole('dialog',{name:'Hide this clip?',exact:true});expect(await page.evaluate(()=>window.__opsCalls.length)).toBe(0);
  await confirmation.getByRole('button',{name:'Cancel',exact:true}).click();await first.getByRole('button',{name:'Hide clip',exact:true}).click();await confirmation.getByRole('button',{name:'Hide clip',exact:true}).click();
  await expect(page.getByRole('navigation',{name:'Main navigation'}).getByRole('button',{name:'Profile',exact:true})).toBeDisabled();await expect(confirmation.getByRole('button',{name:'Cancel',exact:true})).toBeDisabled();
  expect(await page.evaluate(()=>window.__opsCalls.length)).toBe(1);await page.evaluate(()=>window.__opsRelease?.());await expect(confirmation).toHaveCount(0);
});

test('usage separates the logical budget and measured provider warnings from unknown usage',async({page},testInfo)=>{
  await operator(page);await page.goto('/qa.html?operations=1#usage');await expect(page.getByRole('heading',{name:'Administration',exact:true})).toBeVisible();
  await expect(page.getByText(/not a measurement of free space/)).toBeVisible();await expect(page.getByText('Not measured recently',{exact:true})).toHaveCount(4);
  await expect(page.getByRole('status').filter({hasText:'Daily emails is nearing its free allowance.'})).toBeVisible();await expect(page.getByRole('progressbar',{name:'Daily emails',exact:true})).toHaveAttribute('value','95');
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width+1);await page.screenshot({path:testInfo.outputPath('verified-usage.png'),fullPage:true});
});
