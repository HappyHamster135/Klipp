import {test,expect,type Page} from '@playwright/test';

interface WatchAudit{
  postCalls:number;deleteCalls:number;commentCalls:number;metadataCalls:number;
  releasePost:()=>void;releaseComments:()=>void;releaseMetadata:()=>void;
  holdNextMetadata:boolean;failNextMetadata:boolean;shareCalls:ShareData[];copies:string[];shareMode:'success'|'cancel';copyFails:boolean;
}
declare global{interface Window{__watchQA:WatchAudit}}
const health=new WeakMap<Page,string[]>();
test.beforeEach(async({page})=>{
  const errors:string[]=[];health.set(page,errors);
  page.on('pageerror',error=>errors.push(error.message));
  page.on('console',message=>{if(message.type()==='error'&&!message.text().includes('favicon.ico'))errors.push(message.text());});
  await page.route('**/*',route=>{
    const url=new URL(route.request().url());
    if((url.protocol==='http:'&&url.hostname==='127.0.0.1')||url.protocol==='blob:'||url.protocol==='data:')void route.continue();
    else{errors.push(`Unexpected external request: ${url.origin}`);void route.abort('blockedbyclient');}
  });
  await page.goto('/qa.html');await expect(page).toHaveTitle('Klipp · Local QA');
  await expect(page.getByRole('button',{name:'Watch Last-second save',exact:true})).toBeVisible();
  await page.evaluate(()=>{window.__watchQA={postCalls:0,deleteCalls:0,commentCalls:0,metadataCalls:0,releasePost:()=>{},releaseComments:()=>{},releaseMetadata:()=>{},holdNextMetadata:false,failNextMetadata:false,shareCalls:[],copies:[],shareMode:'success',copyFails:false};});
});
test.afterEach(async({page})=>{expect(await page.locator('vite-error-overlay').count()).toBe(0);expect(health.get(page)).toEqual([]);});
async function open(page:Page){await page.getByRole('button',{name:'Watch Last-second save',exact:true}).click();await expect(page.locator('.watch').getByRole('heading',{name:'Last-second save',exact:true})).toBeVisible();}

test('a comments load failure offers retry without claiming the conversation is empty',async({page},testInfo)=>{
  await page.evaluate(()=>{
    const api=window.__klippQA.api,original=api.getComments.bind(api);let fail=true;
    api.getComments=async(...args)=>{if(fail){fail=false;throw new Error('Connection interrupted while loading comments.');}return original(...args);};
  });
  await open(page);
  await expect(page.getByText('Comments could not load.',{exact:true})).toBeVisible();
  await expect(page.getByText('No comments yet. Start the conversation.',{exact:true})).toHaveCount(0);
  await page.screenshot({path:testInfo.outputPath('comments-retry.png'),fullPage:true});
  await page.getByRole('button',{name:'Retry comments',exact:true}).click();
  await expect(page.locator('article.comment').filter({hasText:'That timing!'})).toBeVisible();
  await expect(page.getByRole('button',{name:'Retry comments',exact:true})).toHaveCount(0);
});

test('comment mutations keep drafts on failure and never invite reposting after a count-refresh failure',async({page})=>{
  await page.evaluate(()=>{
    const api=window.__klippQA.api,qa=window.__watchQA,add=api.addComment.bind(api),remove=api.deleteComment.bind(api),get=api.getClip.bind(api);
    api.addComment=async(...args)=>{qa.postCalls++;if(qa.postCalls===1)throw new Error('The comment could not be sent. Try again.');const result=await add(...args);qa.failNextMetadata=true;return result;};
    api.deleteComment=async(...args)=>{qa.deleteCalls++;await remove(...args);qa.failNextMetadata=true;};
    api.getClip=async(...args)=>{
      qa.metadataCalls++;
      if(qa.failNextMetadata){qa.failNextMetadata=false;throw new Error('The count connection was interrupted.');}
      const result=await get(...args);
      if(qa.holdNextMetadata){qa.holdNextMetadata=false;await new Promise<void>(resolve=>{qa.releaseMetadata=resolve;});}
      return result;
    };
  });
  await open(page);await expect(page.locator('article.comment')).toHaveCount(1);
  const draft=page.getByLabel('Add a comment',{exact:true});await draft.fill('Keep this comment until it is sent.');
  await page.getByRole('button',{name:'Post comment',exact:true}).click();
  await expect(page.getByText('The comment could not be sent. Try again.',{exact:true})).toBeVisible();
  await expect(draft).toHaveValue('Keep this comment until it is sent.');
  await page.getByRole('button',{name:'Post comment',exact:true}).click();
  await expect(page.locator('article.comment').filter({hasText:'Keep this comment until it is sent.'})).toHaveCount(1);await expect(draft).toHaveValue('');
  await expect(page.getByRole('status').filter({hasText:'Comment posted. The clip details could not refresh.'})).toBeVisible();
  expect(await page.evaluate(()=>window.__watchQA.postCalls)).toBe(2);
  await page.evaluate(()=>{window.__watchQA.holdNextMetadata=true;});
  await page.getByRole('button',{name:'Retry count refresh',exact:true}).click();
  await expect(page.getByText('Refreshing clip details…',{exact:true})).toBeVisible();
  await page.locator('.watch-main').getByRole('button',{name:/^Like(?: ·|$)/}).click();
  await expect(page.locator('.watch-main').getByRole('button',{name:/^Liked/})).toHaveAttribute('aria-pressed','true');
  await page.evaluate(()=>window.__watchQA.releaseMetadata());
  await expect(page.getByText('Refreshing clip details…',{exact:true})).toHaveCount(0);
  await expect(page.locator('.watch-main').getByRole('button',{name:/^Liked/})).toHaveAttribute('aria-pressed','true');
  expect(await page.evaluate(()=>window.__watchQA.postCalls)).toBe(2);
  await page.getByRole('button',{name:'Delete your comment',exact:true}).click();
  await page.getByRole('dialog',{name:'Delete your comment?',exact:true}).getByRole('button',{name:'Delete comment',exact:true}).click();
  await expect(page.getByRole('dialog',{name:'Delete your comment?',exact:true})).toHaveCount(0);
  await expect(page.locator('article.comment').filter({hasText:'Keep this comment until it is sent.'})).toHaveCount(0);
  await expect(page.getByRole('status').filter({hasText:'Comment deleted. The clip details could not refresh.'})).toBeVisible();
  await page.getByRole('button',{name:'Retry count refresh',exact:true}).click();
  await expect(page.getByRole('button',{name:'Retry count refresh',exact:true})).toHaveCount(0);
  expect(await page.evaluate(()=>window.__watchQA.deleteCalls)).toBe(1);
});

test('a delayed comments page preserves the new post and double submit sends only once',async({page})=>{
  await page.evaluate(()=>{
    const api=window.__klippQA.api,qa=window.__watchQA,read=api.getComments.bind(api),add=api.addComment.bind(api);
    api.getComments=async(...args)=>{const result=await read(...args);await new Promise<void>(resolve=>{qa.releaseComments=resolve;});return result;};
    api.addComment=async(...args)=>{qa.postCalls++;await new Promise<void>(resolve=>{qa.releasePost=resolve;});return add(...args);};
  });
  await open(page);await expect(page.getByText('Loading comments…',{exact:true})).toBeVisible();
  await page.getByLabel('Add a comment',{exact:true}).fill('A new comment during the page load.');
  await page.locator('.comments form').evaluate(form=>{(form as HTMLFormElement).requestSubmit();(form as HTMLFormElement).requestSubmit();});
  expect(await page.evaluate(()=>window.__watchQA.postCalls)).toBe(1);
  await expect(page.getByLabel('Add a comment',{exact:true})).toBeDisabled();
  await page.evaluate(()=>window.__watchQA.releasePost());
  await expect(page.locator('article.comment').filter({hasText:'A new comment during the page load.'})).toHaveCount(1);
  await page.evaluate(()=>window.__watchQA.releaseComments());
  await expect(page.getByText('Loading comments…',{exact:true})).toHaveCount(0);
  await expect(page.locator('article.comment')).toHaveCount(2);
  expect(await page.evaluate(()=>window.__watchQA.postCalls)).toBe(1);
});

test('a delayed metadata refresh cannot undo an acknowledged Like and Unlike round trip',async({page})=>{
  await open(page);await expect(page.locator('article.comment')).toHaveCount(1);
  await page.evaluate(()=>{
    const api=window.__klippQA.api,qa=window.__watchQA,get=api.getClip.bind(api);let first=true;
    api.getClip=async(...args)=>{
      const result=await get(...args);
      if(!first)return result;
      first=false;await new Promise<void>(resolve=>{qa.releaseMetadata=resolve;});
      return{...result,likedByMe:true,likeCount:result.likeCount+1};
    };
  });
  await page.getByLabel('Add a comment',{exact:true}).fill('Refresh the count while I change my mind.');
  await page.getByRole('button',{name:'Post comment',exact:true}).click();
  await expect(page.getByText('Refreshing clip details…',{exact:true})).toBeVisible();
  const actions=page.locator('.watch-main');
  await actions.getByRole('button',{name:'Like · 12',exact:true}).click();
  await expect(actions.getByRole('button',{name:'Liked · 13',exact:true})).toBeEnabled();
  await actions.getByRole('button',{name:'Liked · 13',exact:true}).click();
  await expect(actions.getByRole('button',{name:'Like · 12',exact:true})).toBeEnabled();
  await page.evaluate(()=>window.__watchQA.releaseMetadata());
  await expect(page.getByText('Refreshing clip details…',{exact:true})).toHaveCount(0);
  await expect(actions.getByRole('button',{name:'Like · 12',exact:true})).toHaveAttribute('aria-pressed','false');
});

test('older-comment retry retains existing rows and deduplicates an overlapping page',async({page})=>{
  await page.evaluate(()=>{
    const api=window.__klippQA.api,qa=window.__watchQA,read=api.getComments.bind(api);
    api.getComments=async(...args)=>{
      qa.commentCalls++;const result=await read(...args);
      if(qa.commentCalls===1)return{...result,nextCursor:{id:result.items[0].id,createdAt:result.items[0].createdAt}};
      if(qa.commentCalls===2)throw new Error('The older page connection was interrupted.');
      const first=window.__klippQA.snapshot().comments[0];
      return{items:[first,{...first,id:'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',body:'An older comment.',createdAt:'2026-10-22T11:00:00Z'}],nextCursor:null};
    };
  });
  await open(page);
  await page.getByRole('button',{name:'Older comments',exact:true}).evaluate(button=>{(button as HTMLButtonElement).click();(button as HTMLButtonElement).click();});
  await expect(page.getByText('Older comments could not load.',{exact:true})).toBeVisible();
  await expect(page.locator('article.comment')).toHaveCount(1);expect(await page.evaluate(()=>window.__watchQA.commentCalls)).toBe(2);
  await page.getByRole('button',{name:'Retry older comments',exact:true}).click();
  await expect(page.locator('article.comment')).toHaveCount(2);
  await expect(page.locator('article.comment').filter({hasText:'That timing!'})).toHaveCount(1);
  await expect(page.getByRole('button',{name:'Older comments',exact:true})).toHaveCount(0);
});

test('native sharing uses only the canonical member page and cancel does not copy',async({page})=>{
  await page.evaluate(()=>{
    const qa=window.__watchQA;
    Object.defineProperty(navigator,'share',{configurable:true,value:async(data:ShareData)=>{qa.shareCalls.push(data);if(qa.shareMode==='cancel')throw new DOMException('Canceled','AbortError');}});
    Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async(text:string)=>{qa.copies.push(text);}}});
  });
  await open(page);
  const source=await page.locator('.watch-player video').getAttribute('src');
  await page.getByRole('button',{name:'Share clip',exact:true}).evaluate(button=>{(button as HTMLButtonElement).click();(button as HTMLButtonElement).click();});
  await expect(page.getByRole('button',{name:'Share clip',exact:true})).toBeEnabled();
  const shared=await page.evaluate(()=>window.__watchQA.shareCalls);
  expect(shared).toEqual([{title:'Last-second save',url:'http://127.0.0.1:5173/#watch/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'}]);
  expect(await page.evaluate(()=>window.__watchQA.copies)).toEqual([]);
  await page.evaluate(()=>{window.__watchQA.shareMode='cancel';});
  await page.getByRole('button',{name:'Share clip',exact:true}).click();
  await expect(page.getByRole('button',{name:'Share clip',exact:true})).toBeEnabled();
  expect(await page.evaluate(()=>window.__watchQA.copies)).toEqual([]);
  await expect(page.getByRole('dialog',{name:'Share clip',exact:true})).toHaveCount(0);
  await expect(page.locator('.watch-player video')).toHaveAttribute('src',source!);
});

test('copy sharing falls back to a readable selectable link when the clipboard is unavailable',async({page},testInfo)=>{
  await page.evaluate(()=>{
    const qa=window.__watchQA;Object.defineProperty(navigator,'share',{configurable:true,value:undefined});
    Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async(text:string)=>{if(qa.copyFails)throw new DOMException('Denied','NotAllowedError');qa.copies.push(text);}}});
  });
  await open(page);await page.getByRole('button',{name:'Share clip',exact:true}).click();
  await expect(page.getByText('Link copied. Sign in to Klipp to watch.',{exact:true})).toBeVisible();
  expect(await page.evaluate(()=>window.__watchQA.copies)).toEqual(['http://127.0.0.1:5173/#watch/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa']);
  await page.evaluate(()=>{window.__watchQA.copyFails=true;});
  await page.getByRole('button',{name:'Share clip',exact:true}).click();
  const dialog=page.getByRole('dialog',{name:'Share clip',exact:true}),link=dialog.getByLabel('Clip link',{exact:true});
  await expect(dialog).toBeVisible();await expect(link).toHaveValue('http://127.0.0.1:5173/#watch/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
  await expect(link).toHaveAttribute('readonly','');await expect(dialog).toContainText('Sign in to Klipp to watch this clip.');
  await link.click();await expect.poll(()=>link.evaluate(input=>{const text=input as HTMLTextAreaElement;return text.selectionEnd-text.selectionStart;})).toBeGreaterThan(0);
  await page.screenshot({path:testInfo.outputPath('share-link-fallback.png'),fullPage:false});
  await dialog.getByRole('button',{name:'Done',exact:true}).click();await expect(dialog).toHaveCount(0);
  await expect(page.getByRole('button',{name:'Share clip',exact:true})).toBeFocused();
});
