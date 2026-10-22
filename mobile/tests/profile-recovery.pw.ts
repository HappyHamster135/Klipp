import {test,expect,type Page} from '@playwright/test';

declare global{interface Window{__profileQA?:{calls:number;release:()=>void}}}
const health=new WeakMap<Page,{errors:string[];external:string[]}>();
const navigation=(page:Page)=>page.getByRole('navigation',{name:'Main navigation',exact:true});
const discard=(page:Page)=>page.getByRole('dialog',{name:'Discard profile changes?',exact:true});
test.beforeEach(async({page})=>{
  const state={errors:[] as string[],external:[] as string[]};health.set(page,state);
  page.on('pageerror',error=>state.errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))state.errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if((url.protocol==='http:'&&url.hostname==='127.0.0.1')||url.protocol==='blob:'||url.protocol==='data:')return route.continue();
    state.external.push(url.origin);return route.abort('blockedbyclient');
  });
});
test.afterEach(async({page})=>{
  expect(health.get(page)?.external).toEqual([]);expect(health.get(page)?.errors).toEqual([]);
  expect(await page.locator('vite-error-overlay').count()).toBe(0);
  expect(await page.locator('input[type=file]').count()).toBe(0);
});
async function openProfile(page:Page){
  await page.goto('/qa.html#media');await expect(page).toHaveTitle('Klipp · Local QA');
  await expect(page.getByText('Local test data · no online changes',{exact:true})).toBeVisible();
  await navigation(page).getByRole('button',{name:'Profile',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();await expect(page).toHaveURL(/#profile$/);
}
async function controlledSave(page:Page,mode:'delay'|'fail-first'){
  await page.addInitScript(({mode})=>{
    window.__klippQASetup=api=>{
      let release:()=>void=()=>{};
      const pending=new Promise<void>(resolve=>{release=resolve;});
      window.__profileQA={calls:0,release};const save=api.saveProfile.bind(api);
      api.saveProfile=async(...args)=>{
        window.__profileQA!.calls++;
        if(mode==='delay')await pending;
        else if(window.__profileQA!.calls===1)throw new Error('Could not save your profile. Try again.');
        return save(...args);
      };
    };
  },{mode});
}

test('internal navigation protects a profile draft and discarding restores saved values',async({page},testInfo)=>{
  await openProfile(page);const name=page.getByLabel('Display name',{exact:true});
  await expect(page.getByRole('button',{name:'Save profile',exact:true})).toBeDisabled();
  await name.fill('Unsaved name');const media=navigation(page).getByRole('button',{name:'Media',exact:true});await media.focus();await page.keyboard.press('Enter');
  await expect(discard(page)).toBeVisible();await expect(page).toHaveURL(/#profile$/);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().profile?.displayName)).toBe('Sam');
  await page.screenshot({path:testInfo.outputPath('unsaved-profile-dialog.png'),fullPage:true});
  await discard(page).getByRole('button',{name:'Keep editing',exact:true}).click();await expect(name).toHaveValue('Unsaved name');await expect(media).toBeFocused();
  await navigation(page).getByRole('button',{name:'Media',exact:true}).click();
  await discard(page).getByRole('button',{name:'Discard changes',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
  await navigation(page).getByRole('button',{name:'Profile',exact:true}).click();await expect(name).toHaveValue('Sam');
});

test('phone Back restores the dirty Profile entry until Keep editing or Discard is chosen',async({page})=>{
  await openProfile(page);await page.getByRole('textbox',{name:'Bio',exact:true}).fill('Draft kept when Back is canceled.');
  const entries=await page.evaluate(()=>history.length);
  await page.evaluate(()=>history.back());await expect(discard(page)).toBeVisible();await expect(page).toHaveURL(/#profile$/);
  await discard(page).getByRole('button',{name:'Keep editing',exact:true}).click();
  await expect(page.getByRole('textbox',{name:'Bio',exact:true})).toHaveValue('Draft kept when Back is canceled.');
  await page.evaluate(()=>history.back());await expect(discard(page)).toBeVisible();
  await discard(page).getByRole('button',{name:'Discard changes',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();await expect(page).toHaveURL(/#media$/);
  expect(await page.evaluate(()=>history.length)).toBe(entries);
  await page.goForward();await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();
  await expect(page.getByRole('textbox',{name:'Bio',exact:true})).toHaveValue('Game highlights and good company.');
});

test('sign-out asks before discarding a draft and cancel keeps the account signed in',async({page})=>{
  await openProfile(page);await page.getByLabel('Display name',{exact:true}).fill('Not saved');
  await page.getByRole('button',{name:'Sign out',exact:true}).click();await expect(discard(page)).toBeVisible();
  await discard(page).getByRole('button',{name:'Keep editing',exact:true}).click();
  expect(await page.evaluate(()=>window.__klippQA.api.user?.id)).toBeTruthy();
  await expect(page.getByLabel('Display name',{exact:true})).toHaveValue('Not saved');
  await page.getByRole('button',{name:'Sign out',exact:true}).click();
  await discard(page).getByRole('button',{name:'Discard changes',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Sign in to Klipp',exact:true})).toBeVisible();
  expect(await page.evaluate(()=>window.__klippQA.api.user)).toBeNull();
});

test('profile guidance counts real characters and a successful save clears dirty state',async({page},testInfo)=>{
  await openProfile(page);const username=page.getByLabel('Username',{exact:true}),name=page.getByLabel('Display name',{exact:true}),bio=page.getByRole('textbox',{name:'Bio',exact:true});
  await expect(username).toHaveAttribute('autocorrect','off');await expect(username).toHaveAttribute('aria-describedby','profile-username-help');
  await expect(page.locator('#profile-username-help')).toContainText('Start with a letter.');
  await name.fill('🎮'.repeat(40));await expect(name).toHaveValue('🎮'.repeat(40));await expect(page.locator('#profile-name-help')).toContainText('40/40 characters');
  await bio.fill('🎮'.repeat(160));await expect(bio).toHaveValue('🎮'.repeat(160));await expect(page.locator('#profile-bio-help')).toHaveText('160/160 characters');
  await name.fill('  Sam QA  ');await bio.fill('  New profile details.  ');
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await expect(page.getByText('Profile saved.',{exact:true})).toBeVisible();
  await expect(name).toHaveValue('Sam QA');await expect(bio).toHaveValue('New profile details.');
  await expect(page.getByRole('button',{name:'Save profile',exact:true})).toBeDisabled();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width+1);
  await page.screenshot({path:testInfo.outputPath('saved-profile-guidance.png'),fullPage:true});
  await navigation(page).getByRole('button',{name:'Media',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();await expect(discard(page)).toHaveCount(0);
});

test('a delayed save blocks double submit, field edits and Back until the accepted response',async({page})=>{
  await controlledSave(page,'delay');await openProfile(page);await page.getByLabel('Display name',{exact:true}).fill('Saved once');
  await page.locator('main.profile-page form').evaluate(element=>{const form=element as HTMLFormElement;form.requestSubmit();form.requestSubmit();});
  await expect.poll(()=>page.evaluate(()=>window.__profileQA?.calls)).toBe(1);
  await expect(page.getByLabel('Display name',{exact:true})).toBeDisabled();await expect(page.getByRole('textbox',{name:'Bio',exact:true})).toBeDisabled();
  await expect(navigation(page).getByRole('button',{name:'Media',exact:true})).toBeDisabled();
  await page.evaluate(()=>history.back());await expect(page).toHaveURL(/#profile$/);await expect(discard(page)).toHaveCount(0);
  await page.evaluate(()=>window.__profileQA!.release());await expect(page.getByText('Profile saved.',{exact:true})).toBeVisible();
  await expect(page.getByLabel('Display name',{exact:true})).toBeEnabled();await expect(page.getByRole('button',{name:'Save profile',exact:true})).toBeDisabled();
  await navigation(page).getByRole('button',{name:'Media',exact:true}).click();await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
  await expect(discard(page)).toHaveCount(0);expect(await page.evaluate(()=>window.__profileQA?.calls)).toBe(1);
});

test('failed save keeps the draft protected and retry clears the guard',async({page})=>{
  await controlledSave(page,'fail-first');await openProfile(page);await page.getByLabel('Display name',{exact:true}).fill('Retry this name');
  await page.getByRole('button',{name:'Save profile',exact:true}).click();
  await expect(page.getByText('Could not save your profile. Try again.',{exact:true})).toBeVisible();
  await expect(page.getByLabel('Display name',{exact:true})).toHaveValue('Retry this name');await expect(page.getByRole('button',{name:'Save profile',exact:true})).toBeEnabled();
  await navigation(page).getByRole('button',{name:'Media',exact:true}).click();await expect(discard(page)).toBeVisible();
  await discard(page).getByRole('button',{name:'Keep editing',exact:true}).click();await page.getByRole('button',{name:'Save profile',exact:true}).click();
  await expect(page.getByText('Profile saved.',{exact:true})).toBeVisible();expect(await page.evaluate(()=>window.__profileQA?.calls)).toBe(2);
  await navigation(page).getByRole('button',{name:'Media',exact:true}).click();await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
});

test('choosing a first username stays on Profile through the synchronous saved-account update',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{api.profile!.username=null;};});
  await page.goto('/qa.html#media');await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();await expect(page).toHaveURL(/#profile$/);
  await page.getByLabel('Username',{exact:true}).fill('new_player');await page.getByRole('button',{name:'Save profile',exact:true}).click();
  await expect(page.getByText('Profile saved.',{exact:true})).toBeVisible();await expect(page).toHaveURL(/#profile$/);
  await navigation(page).getByRole('button',{name:'Media',exact:true}).click();await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();
});

test('a delayed profile request cannot update the view after the fixture identity signs out',async({page})=>{
  await controlledSave(page,'delay');await openProfile(page);await page.getByLabel('Display name',{exact:true}).fill('Late response');
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await expect.poll(()=>page.evaluate(()=>window.__profileQA?.calls)).toBe(1);
  await page.evaluate(()=>window.__klippQA.api.signOut());await expect(page.getByRole('heading',{name:'Sign in to Klipp',exact:true})).toBeVisible();
  await page.evaluate(()=>window.__profileQA!.release());
  await expect(page.getByText('Profile saved.',{exact:true})).toHaveCount(0);await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toHaveCount(0);
  expect(await page.evaluate(()=>window.__klippQA.snapshot().profile)).toBeNull();
});

test('a slow blocked-users read allows editing and leaving while still protecting dirty drafts',async({page})=>{
  await page.addInitScript(()=>{
    window.__klippQASetup=api=>{
      let release:()=>void=()=>{};const pending=new Promise<void>(resolve=>{release=resolve;});
      window.__profileQA={calls:0,release};const read=api.getBlockedUsers.bind(api);
      api.getBlockedUsers=async(signal)=>{window.__profileQA!.calls++;await pending;return read(signal);};
    };
  });
  await openProfile(page);await page.getByRole('button',{name:'Blocked users',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>window.__profileQA?.calls)).toBe(1);
  const name=page.getByLabel('Display name',{exact:true});await expect(name).toBeEnabled();
  await expect(navigation(page).getByRole('button',{name:'Media',exact:true})).toBeEnabled();
  await name.fill('Dirty during a read');await navigation(page).getByRole('button',{name:'Media',exact:true}).click();
  await expect(discard(page)).toBeVisible();await discard(page).getByRole('button',{name:'Keep editing',exact:true}).click();
  await name.fill('Sam');await navigation(page).getByRole('button',{name:'Media',exact:true}).click();
  await expect(page.getByRole('heading',{name:'Media',exact:true})).toBeVisible();await expect(discard(page)).toHaveCount(0);
  await page.evaluate(()=>window.__profileQA!.release());
  await expect(page.getByRole('heading',{name:'Blocked users',exact:true})).toHaveCount(0);
});
