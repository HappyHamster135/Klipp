import {test,expect,type Page} from '@playwright/test';
declare global{interface Window{__feedQA?:{release?:()=>void;returned?:boolean;posterAttempts?:Record<string,number>;allowPosters?:boolean}}}
const firstId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const health=new WeakMap<Page,string[]>();
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
test.afterEach(async({page})=>{expect(health.get(page)).toEqual([]);expect(await page.locator('vite-error-overlay').count()).toBe(0);});
const errorPanel=(page:Page)=>page.getByRole('alert').filter({has:page.getByRole('button',{name:'Retry clips',exact:true})});

test('first-load failure offers Retry without claiming the community is empty',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const original=api.getFeed.bind(api);let fail=true;
    api.getFeed=async(...args)=>{if(fail){fail=false;throw Error('Synthetic first-load failure');}return original(...args);};
  };});
  await page.goto('/qa.html');
  await expect(errorPanel(page).getByRole('heading',{name:'Could not load clips',exact:true})).toBeVisible();
  await expect(page.getByRole('heading',{name:'No clips shared yet',exact:true})).toHaveCount(0);
  await errorPanel(page).getByRole('button',{name:'Retry clips',exact:true}).click();
  await expect(page.getByRole('button',{name:'Watch Last-second save',exact:true})).toBeVisible();
  await expect(errorPanel(page)).toHaveCount(0);
});

test('a failed My uploads request never presents Community rows as the member’s clips',async({page})=>{
  await page.goto('/qa.html');await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.getFeed.bind(api);let fail=true;
    api.getFeed=async(cursor,mine,signal)=>{if(mine&&fail){fail=false;throw Error('Synthetic My uploads failure');}return original(cursor,mine,signal);};
  });
  await page.getByRole('tab',{name:'My uploads',exact:true}).click();
  await expect(errorPanel(page)).toBeVisible();
  await expect(page.getByRole('button',{name:'Watch Last-second save',exact:true})).toHaveCount(0);
  await expect(page.getByRole('heading',{name:'No uploads yet',exact:true})).toHaveCount(0);
  await errorPanel(page).getByRole('button',{name:'Retry clips',exact:true}).click();
  await expect(page.locator('article.clip-card')).toHaveCount(1);
  await expect(page.getByRole('button',{name:'Watch Clean final round',exact:true})).toBeVisible();
});

test('failed pagination hides the previous page and retries the intended older cursor',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const template=api.clips[0];
    api.clips=Array.from({length:14},(_,index)=>({...template,id:`${(index+1).toString(16).padStart(8,'0')}-aaaa-4aaa-8aaa-aaaaaaaaaaaa`,title:`QA page clip ${index+1}`,createdAt:new Date(Date.UTC(2026,9,22,12,0,30-index)).toISOString()}));
    const original=api.getFeed.bind(api);let fail=true;
    api.getFeed=async(cursor,mine,signal)=>{if(cursor&&fail){fail=false;throw Error('Synthetic older-page failure');}return original(cursor,mine,signal);};
  };});
  await page.goto('/qa.html');await expect(page.locator('article.clip-card')).toHaveCount(12);
  await page.getByRole('button',{name:'Older clips',exact:true}).click();
  await expect(errorPanel(page)).toBeVisible();
  await expect(page.getByRole('button',{name:'Watch QA page clip 1',exact:true})).toHaveCount(0);
  await errorPanel(page).getByRole('button',{name:'Retry clips',exact:true}).click();
  await expect(page.locator('article.clip-card')).toHaveCount(2);
  await expect(page.getByRole('button',{name:'Watch QA page clip 13',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Newer clips',exact:true}).click();
  await expect(page.locator('article.clip-card')).toHaveCount(12);
  await expect(page.getByRole('button',{name:'Watch QA page clip 1',exact:true})).toBeVisible();
});

test('a background refresh keeps readable rows and provides an explicit retry',async({page})=>{
  await page.goto('/qa.html');await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.getFeed.bind(api);let fail=true;
    api.getFeed=async(...args)=>{if(fail){fail=false;await new Promise<void>((_,reject)=>{window.__feedQA={release:()=>reject(Error('Synthetic refresh failure'))};});}return original(...args);};
  });
  await page.getByRole('button',{name:'Refresh clips',exact:true}).click();
  await expect(page.getByRole('button',{name:'Refresh clips',exact:true})).toBeDisabled();
  await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.evaluate(()=>window.__feedQA?.release?.());
  await expect(errorPanel(page).getByRole('heading',{name:'Could not refresh clips',exact:true})).toBeVisible();
  await expect(page.locator('article.clip-card')).toHaveCount(3);
  await errorPanel(page).getByRole('button',{name:'Retry clips',exact:true}).click();
  await expect(errorPanel(page)).toHaveCount(0);await expect(page.locator('article.clip-card')).toHaveCount(3);
});
test('a delayed refresh cannot undo an acknowledged Like',async({page})=>{
  await page.goto('/qa.html');await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.getFeed.bind(api);let delay=true;
    api.getFeed=async(...args)=>{
      const result=await original(...args);if(!delay)return result;delay=false;
      await new Promise<void>(resolve=>{window.__feedQA={release:resolve};});
      window.__feedQA!.returned=true;return result;
    };
  });
  await page.getByRole('button',{name:'Refresh clips',exact:true}).click();
  await expect(page.getByRole('button',{name:'Refresh clips',exact:true})).toBeDisabled();
  const card=page.locator('article.clip-card').filter({has:page.getByRole('button',{name:'Watch Last-second save',exact:true})});
  await card.getByRole('button',{name:/^Like(?: ·|$)/}).click();
  await expect(card.getByRole('button',{name:'Liked · 13',exact:true})).toHaveAttribute('aria-pressed','true');
  await page.evaluate(()=>window.__feedQA?.release?.());
  await expect.poll(()=>page.evaluate(()=>window.__feedQA?.returned)).toBe(true);
  await expect(page.getByRole('button',{name:'Refresh clips',exact:true})).toBeEnabled();
  await page.evaluate(()=>new Promise(requestAnimationFrame));
  await expect(card.getByRole('button',{name:'Liked · 13',exact:true})).toHaveAttribute('aria-pressed','true');
});

test('an obsolete delayed tab response cannot replace the current collection',async({page})=>{
  await page.goto('/qa.html');await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.getFeed.bind(api);
    api.getFeed=async(cursor,mine,signal)=>{
      if(!mine)return original(cursor,mine,signal);
      const result=await original(cursor,mine);
      await new Promise<void>(resolve=>{window.__feedQA={release:resolve};});
      window.__feedQA!.returned=true;return result;
    };
  });
  await page.getByRole('tab',{name:'My uploads',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>typeof window.__feedQA?.release)).toBe('function');
  await page.getByRole('tab',{name:'Community',exact:true}).click();
  await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.evaluate(()=>window.__feedQA?.release?.());
  await expect.poll(()=>page.evaluate(()=>window.__feedQA?.returned)).toBe(true);
  await page.evaluate(()=>new Promise(requestAnimationFrame));
  await expect(page.getByRole('tab',{name:'Community',exact:true})).toHaveAttribute('aria-selected','true');
  await expect(page.locator('article.clip-card')).toHaveCount(3);
});

async function failPosters(page:Page){
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const original=api.getPosterUrl.bind(api);window.__feedQA={posterAttempts:{},allowPosters:false};
    api.getPosterUrl=async(id,signal)=>{
      const state=window.__feedQA!;state.posterAttempts![id]=(state.posterAttempts![id]??0)+1;
      if(!state.allowPosters)throw Error('Synthetic poster failure');return original(id,signal);
    };
  };});
  await page.goto('/qa.html');
  const card=page.locator('article.clip-card').filter({has:page.getByRole('button',{name:'Watch Last-second save',exact:true})});
  await expect(card.getByText('Preview unavailable',{exact:true})).toBeVisible();return card;
}
test('a failed preview keeps Watch usable and refresh explicitly retries the thumbnail',async({page})=>{
  const card=await failPosters(page);
  await card.getByRole('button',{name:'Watch Last-second save',exact:true}).click();
  await expect.poll(()=>page.locator('.watch-player video').evaluate(element=>(element as HTMLVideoElement).readyState)).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(id=>window.__feedQA?.posterAttempts?.[id],firstId)).toBe(1);
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  await page.evaluate(()=>{window.__feedQA!.allowPosters=true;});
  await page.getByRole('button',{name:'Refresh clips',exact:true}).click();
  await expect.poll(()=>card.locator('.poster img').evaluateAll(images=>images.some(image=>(image as HTMLImageElement).naturalWidth>0))).toBe(true);
  await expect(card.getByText('Preview unavailable',{exact:true})).toHaveCount(0);
});
test('returning online retries visible failed previews without a hot loop',async({page})=>{
  const card=await failPosters(page);
  await page.evaluate(()=>{window.__feedQA!.allowPosters=true;window.dispatchEvent(new Event('online'));});
  await expect.poll(()=>card.locator('.poster img').evaluateAll(images=>images.some(image=>(image as HTMLImageElement).naturalWidth>0))).toBe(true);
  expect(await page.evaluate(id=>window.__feedQA?.posterAttempts?.[id],firstId)).toBe(2);
});
test('an unreadable thumbnail response also falls back and refreshes cleanly',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const original=api.getPosterUrl.bind(api);window.__feedQA={allowPosters:false};
    api.getPosterUrl=async(id,signal)=>id==='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'&&!window.__feedQA!.allowPosters?'data:image/jpeg;base64,bm90LWFuLWltYWdl':original(id,signal);
  };});
  await page.goto('/qa.html');
  const card=page.locator('article.clip-card').filter({has:page.getByRole('button',{name:'Watch Last-second save',exact:true})});
  await expect(card.getByText('Preview unavailable',{exact:true})).toBeVisible();
  await page.evaluate(()=>{window.__feedQA!.allowPosters=true;});
  await page.getByRole('button',{name:'Refresh clips',exact:true}).click();
  await expect.poll(()=>card.locator('.poster img').evaluateAll(images=>images.some(image=>(image as HTMLImageElement).naturalWidth>0))).toBe(true);
});

test('a deferred Watch link shows opening feedback and Back ignores its late reply',async({page})=>{
  await page.addInitScript(()=>{window.__klippQASetup=api=>{
    const original=api.getClip.bind(api);let release=()=>{};
    const pending=new Promise<void>(resolve=>{release=resolve;});window.__feedQA={release};
    api.getClip=async(id)=>{const clip=await original(id);await pending;window.__feedQA!.returned=true;return clip;};
  };});
  await page.goto(`/qa.html#watch/${firstId}`);
  await expect(page.getByRole('status').filter({hasText:'Opening clip...'})).toBeVisible();
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  await expect(page).toHaveURL(/#media$/);
  await page.evaluate(()=>window.__feedQA?.release?.());
  await expect.poll(()=>page.evaluate(()=>window.__feedQA?.returned)).toBe(true);
  await page.evaluate(()=>new Promise(requestAnimationFrame));await expect(page.locator('.watch')).toHaveCount(0);
});

test('collection tabs support arrows and Home/End with a labelled panel',async({page})=>{
  await page.goto('/qa.html');
  const community=page.getByRole('tab',{name:'Community',exact:true}),mine=page.getByRole('tab',{name:'My uploads',exact:true});
  await community.focus();await community.press('ArrowRight');await expect(mine).toBeFocused();
  await expect(mine).toHaveAttribute('aria-selected','true');await expect(page.getByRole('tabpanel',{name:'My uploads',exact:true})).toBeVisible();
  await mine.press('Home');await expect(community).toBeFocused();await expect(community).toHaveAttribute('aria-selected','true');
  await community.press('End');await expect(mine).toBeFocused();
  await mine.press('ArrowLeft');await expect(community).toBeFocused();
});
test('the More dialog stays dismissible during an unrelated pending Like',async({page})=>{
  await page.goto('/qa.html');await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.setLiked.bind(api);
    api.setLiked=async(...args)=>{await new Promise<void>(resolve=>{window.__feedQA={release:resolve};});return original(...args);};
  });
  const card=page.locator('article.clip-card').filter({has:page.getByRole('button',{name:'Watch Last-second save',exact:true})});
  await card.getByRole('button',{name:/^Like(?: ·|$)/}).click();
  await card.getByRole('button',{name:'Actions for Last-second save',exact:true}).click();
  const dialog=page.getByRole('dialog',{name:'Clip actions',exact:true});await expect(dialog).toBeVisible();
  await dialog.getByRole('button',{name:'Close dialog',exact:true}).click();await expect(dialog).toHaveCount(0);
  await page.evaluate(()=>window.__feedQA?.release?.());
  await expect(card.getByRole('button',{name:'Liked · 13',exact:true})).toHaveAttribute('aria-pressed','true');
});

test('failed delete, report and block actions explain the failure inside the dialog and can retry',async({page})=>{
  await page.goto('/qa.html');await expect(page.locator('article.clip-card')).toHaveCount(3);
  await page.evaluate(()=>{
    const api=window.__klippQA.api;
    const remove=api.deleteClip.bind(api),report=api.reportClip.bind(api),block=api.setBlocked.bind(api);
    let failDelete=true,failReport=true,failBlock=true;
    api.deleteClip=async(...args)=>{if(failDelete){failDelete=false;throw Error('Synthetic delete failure');}return remove(...args);};
    api.reportClip=async(...args)=>{if(failReport){failReport=false;throw Error('Synthetic report failure');}return report(...args);};
    api.setBlocked=async(...args)=>{if(failBlock){failBlock=false;throw Error('Synthetic block failure');}return block(...args);};
  });
  const operations=[
    {title:'Clean final round',menu:'Delete shared clip',dialog:'Delete shared clip?',confirm:'Delete shared clip',error:'Synthetic delete failure'},
    {title:'Last-second save',menu:'Report clip',dialog:'Report clip',confirm:'Send report',error:'Synthetic report failure'},
    {title:'Night city sprint',menu:'Block @mika',dialog:'Block this user?',confirm:'Block user',error:'Synthetic block failure'},
  ];
  for(const operation of operations){
    const card=page.locator('article.clip-card').filter({has:page.getByRole('button',{name:`Watch ${operation.title}`,exact:true})});
    await card.getByRole('button',{name:`Actions for ${operation.title}`,exact:true}).click();
    await page.getByRole('dialog',{name:'Clip actions',exact:true}).getByRole('button',{name:operation.menu,exact:true}).click();
    const dialog=page.getByRole('dialog',{name:operation.dialog,exact:true});
    if(operation.confirm==='Send report')await dialog.getByLabel('Reason',{exact:true}).fill('Synthetic report reason.');
    await dialog.getByRole('button',{name:operation.confirm,exact:true}).click();
    await expect(dialog.getByRole('status')).toContainText(operation.error);
    await expect(dialog.getByRole('button',{name:operation.confirm,exact:true})).toBeEnabled();
    await dialog.getByRole('button',{name:'Close dialog',exact:true}).click();await expect(dialog).toHaveCount(0);
    await card.getByRole('button',{name:`Actions for ${operation.title}`,exact:true}).click();
    await page.getByRole('dialog',{name:'Clip actions',exact:true}).getByRole('button',{name:operation.menu,exact:true}).click();
    await expect(dialog.getByText(operation.error,{exact:true})).toHaveCount(0);
    if(operation.confirm==='Send report')await dialog.getByLabel('Reason',{exact:true}).fill('Synthetic report reason.');
    await dialog.getByRole('button',{name:operation.confirm,exact:true}).click();await expect(dialog).toHaveCount(0);
  }
  const snapshot=await page.evaluate(()=>window.__klippQA.snapshot());
  expect(snapshot.clips.some(clip=>clip.title==='Clean final round')).toBe(false);
  expect(snapshot.reports).toEqual([{id:firstId,reason:'Synthetic report reason.'}]);
  expect(snapshot.blocked).toEqual(['33333333-3333-4333-8333-333333333333']);
});

test('a delayed Like result preserves a newer acknowledged comment count',async({page})=>{
  await page.goto('/qa.html');await page.getByRole('button',{name:'Watch Last-second save',exact:true}).click();
  await expect(page.locator('article.comment')).toHaveCount(1);
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.setLiked.bind(api);
    api.setLiked=async(...args)=>{const changed=await original(...args);await new Promise<void>(resolve=>{window.__feedQA={release:resolve};});window.__feedQA!.returned=true;return changed;};
  });
  const actions=page.locator('.watch-main');
  await actions.getByRole('button',{name:'Like · 12',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>typeof window.__feedQA?.release)).toBe('function');
  await page.getByLabel('Add a comment',{exact:true}).fill('A comment posted while the Like is still returning.');
  await page.getByRole('button',{name:'Post comment',exact:true}).click();
  await expect(page.locator('article.comment')).toHaveCount(2);
  await expect(actions.getByRole('button',{name:'Comments · 2',exact:true})).toBeVisible();
  await page.evaluate(()=>window.__feedQA?.release?.());
  await expect.poll(()=>page.evaluate(()=>window.__feedQA?.returned)).toBe(true);
  await expect(actions.getByRole('button',{name:'Liked · 13',exact:true})).toBeEnabled();
  await expect(actions.getByRole('button',{name:'Comments · 2',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Back to clips',exact:true}).click();
  const card=page.locator('article.clip-card').filter({has:page.getByRole('button',{name:'Watch Last-second save',exact:true})});
  await expect(card.getByRole('button',{name:'Comments · 2',exact:true})).toBeVisible();
});

test('a deletion finishing after leaving Media cannot navigate away from Profile',async({page})=>{
  await page.goto('/qa.html');await page.getByRole('button',{name:'Watch Clean final round',exact:true}).click();
  await expect(page.locator('.watch')).toBeVisible();
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.deleteClip.bind(api);
    api.deleteClip=async(...args)=>{await new Promise<void>(resolve=>{window.__feedQA={release:resolve};});await original(...args);window.__feedQA!.returned=true;};
  });
  await page.locator('.watch-main').getByRole('button',{name:'Actions for Clean final round',exact:true}).click();
  await page.getByRole('dialog',{name:'Clip actions',exact:true}).getByRole('button',{name:'Delete shared clip',exact:true}).click();
  await page.getByRole('dialog',{name:'Delete shared clip?',exact:true}).getByRole('button',{name:'Delete shared clip',exact:true}).click();
  await expect.poll(()=>page.evaluate(()=>typeof window.__feedQA?.release)).toBe('function');
  // A browser route change can leave a pending dialog; the request may still finish on the server.
  await page.evaluate(()=>{location.hash='#profile';});
  await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();
  await page.evaluate(()=>window.__feedQA?.release?.());
  await expect.poll(()=>page.evaluate(()=>window.__feedQA?.returned)).toBe(true);
  await page.evaluate(()=>new Promise(requestAnimationFrame));
  await expect(page).toHaveURL(/#profile$/);await expect(page.getByRole('heading',{name:'Your profile',exact:true})).toBeVisible();
  expect(await page.evaluate(()=>window.__klippQA.snapshot().clips.some(clip=>clip.title==='Clean final round'))).toBe(false);
});
