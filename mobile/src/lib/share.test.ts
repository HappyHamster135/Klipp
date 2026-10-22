import {describe,expect,it,vi} from 'vitest';
import {clipViewerLink,shareClipLink} from './share';

const id='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
describe('member clip links',()=>{
  it('shares only the app directory and clip fragment, excluding query data and previous fragments',()=>{
    expect(clipViewerLink(id.toUpperCase(),'https://example.test/Klipp/index.html?token=private#access_token=private'))
      .toBe(`https://example.test/Klipp/#watch/${id}`);
    expect(clipViewerLink(id,'https://example.test/Klipp/#profile')).toBe(`https://example.test/Klipp/#watch/${id}`);
  });
  it('rejects non-clip paths and non-HTTP pages',()=>{
    expect(()=>clipViewerLink('https://private.test?token=secret','https://example.test/')).toThrow();
    expect(()=>clipViewerLink(id,'javascript:alert(1)')).toThrow();
  });
  it('prefers the native share sheet and sends no media file or signed playback URL',async()=>{
    const share=vi.fn(async()=>{}),copy=vi.fn(async()=>{});
    expect((await shareClipLink(id,'A highlight','https://example.test/Klipp/',{share,copy})).status).toBe('shared');
    expect(share).toHaveBeenCalledWith({title:'A highlight',url:`https://example.test/Klipp/#watch/${id}`});
    expect(copy).not.toHaveBeenCalled();
  });
  it('treats closing the native sheet as cancellation without copying anything',async()=>{
    const share=vi.fn(async()=>{throw new DOMException('Canceled','AbortError');}),copy=vi.fn(async()=>{});
    expect((await shareClipLink(id,'Clip','https://example.test/',{share,copy})).status).toBe('canceled');
    expect(copy).not.toHaveBeenCalled();
    expect((await shareClipLink(id,'Clip','https://example.test/',{share:async()=>{throw{name:'AbortError'};},copy})).status).toBe('canceled');
    expect(copy).not.toHaveBeenCalled();
  });
  it('copies a page link when native sharing is unavailable or denied',async()=>{
    const copy=vi.fn(async()=>{});
    expect((await shareClipLink(id,'Clip','https://example.test/',{copy})).status).toBe('copied');
    expect((await shareClipLink(id,'Clip','https://example.test/',{share:async()=>{throw new Error('Unavailable');},copy})).status).toBe('copied');
    expect(copy).toHaveBeenLastCalledWith(`https://example.test/#watch/${id}`);
  });
  it('offers manual selection when clipboard access is absent or rejected',async()=>{
    expect((await shareClipLink(id,'Clip','https://example.test/',{})).status).toBe('manual');
    expect((await shareClipLink(id,'Clip','https://example.test/',{copy:async()=>{throw new Error('Denied');}})).status).toBe('manual');
  });
});
