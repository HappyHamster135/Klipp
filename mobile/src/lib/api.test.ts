import { afterEach, describe, expect, it, vi } from 'vitest';
import { CommunityApi } from './api';
import { browserSessionStorage, createConfig } from './config';
import { CommunityError, type StorageLike } from './models';

const owner = '161cd682-c82e-4eac-8776-4ecdf5ef13a0';
const clipId = '261cd682-c82e-4eac-8776-4ecdf5ef13a0';
const config = createConfig('https://community.example.com', 'sb_publishable_testpublickey123456789');
const instances: CommunityApi[] = [];
afterEach(() => { instances.forEach(api => api.dispose()); instances.length = 0; vi.unstubAllGlobals(); });
const user = { id: owner, email: 'player@example.com', aud: 'authenticated', created_at: '2026-10-22T10:00:00Z' };
function session() { return { access_token: 'access-secret', refresh_token: 'refresh-secret', token_type: 'bearer', expires_in: 3600, expires_at: Math.floor(Date.now() / 1000) + 3600, user }; }
function json(value: unknown, status = 200) { return new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } }); }
function clip(overrides: Record<string, unknown> = {}) {
  return { id: clipId, owner_id: owner, username: 'player', display_name: 'Player', title: 'A clip', description: '',
    duration_seconds: 2, byte_size: 1024, created_at: '2026-10-22T10:00:00Z', like_count: 1, comment_count: 0,
    liked_by_me: false, status: 'published', video_path: `${owner}/${clipId}/video.mp4`, poster_path: `${owner}/${clipId}/poster.jpg`, ...overrides };
}
function comment(id = clipId, created = '2026-10-22T10:00:00Z') { return { id, clip_id: clipId, owner_id: owner, username: 'player', display_name: 'Player', body: 'Nice clip', created_at: created }; }
function uuid(index: number) { return `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`; }
function memoryStorage(): StorageLike & { values: Map<string, string> } {
  const values = new Map<string, string>();
  return { values, getItem: key => values.get(key) ?? null, setItem: (key, value) => { values.set(key, value); }, removeItem: key => { values.delete(key); } };
}
interface Captured { path: string; method: string; headers: Headers; body: unknown; signal: AbortSignal | null | undefined; origin: string; redirect: RequestRedirect | undefined }
function fixture(storageAdapter?: StorageLike, selectedConfig = config) {
  const requests: Captured[] = [];
  const storage = memoryStorage();
  let reply: (request: Captured) => Response | Promise<Response> = request => {
    if (request.path === '/auth/v1/verify') return json(session());
    if (request.path === '/auth/v1/user') return json(user);
    if (request.path.startsWith('/auth/v1/logout')) return json({});
    throw new Error('No fake response for this request');
  };
  const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(input instanceof Request ? input.url : String(input));
    const request: Captured = { path: url.pathname + url.search, method: init?.method ?? 'GET', headers: new Headers(init?.headers),
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body, signal: init?.signal, origin: url.origin, redirect: init?.redirect };
    requests.push(request); return reply(request);
  }) as typeof fetch;
  const api = new CommunityApi(selectedConfig, { storage: storageAdapter ?? storage, fetch: fetcher }); instances.push(api);
  return { api, requests, storage, set reply(value: typeof reply) { reply = value; }, signIn: () => api.verifyEmailCode('player@example.com', '123456') };
}
function cleanupReply(request: Captured): Response {
  if (request.path.endsWith('community_remove_clip')) return json(clip({ status: 'deleting' }));
  if (request.path.endsWith('community_finish_delete')) return json({ id: clipId, deleted: true });
  return json([]);
}

describe('verified account tools and destructive confirmations',()=>{
  const profile=(username:string|null='player',id=owner)=>[{id,username,display_name:'Player',bio:''}];
  async function account(){const f=fixture();await f.signIn();f.reply=()=>json(profile());await f.api.getProfile();return f;}
  it('fetches live capabilities using only the current public key and user bearer',async()=>{
    const f=await account();f.reply=()=>json({version:1,enabled:true,operator:true,account_deletion:true});
    expect(await f.api.getOperationsCapabilities()).toEqual({version:1,enabled:true,operator:true,accountDeletion:true});
    const request=f.requests.at(-1)!;expect(request.path).toBe('/rest/v1/rpc/community_operations_capabilities');
    expect(request.headers.get('Authorization')).toBe('Bearer access-secret');expect(request.headers.get('apikey')).toBe(config.publishableKey);
  });
  it('does not enable unsupported capability versions or use user claims as fallback',async()=>{
    const f=await account();f.reply=()=>json({version:2,enabled:true,operator:true,account_deletion:true});
    expect(await f.api.getOperationsCapabilities()).toMatchObject({enabled:false,operator:false,accountDeletion:false});
    f.reply=()=>json({version:1,enabled:true,operator:'true',account_deletion:true});
    await expect(f.api.getOperationsCapabilities()).rejects.toMatchObject({code:'unavailable'});
  });
  it('rejects a late capability response after sign-out',async()=>{
    const f=await account();let resolve!:(value:Response)=>void;let started!:()=>void;const beginning=new Promise<void>(done=>{started=done;});
    f.reply=request=>request.path.endsWith('community_operations_capabilities')?new Promise<Response>(done=>{resolve=done;started();}):json({});
    const pending=f.api.getOperationsCapabilities();await beginning;const signout=f.api.signOut();resolve(json({version:1,enabled:true,operator:true,account_deletion:true}));
    await expect(pending).rejects.toMatchObject({name:'AbortError'});await signout;expect(f.api.user).toBeNull();
  });
  it('rechecks the current self profile and stops deletion if the username changed',async()=>{
    const f=await account();f.reply=request=>{expect(request.path).toContain('/rest/v1/profiles?');return json(profile('new_name'));};
    await expect(f.api.deleteAccount('player')).rejects.toMatchObject({code:'confirmation_required'});
    expect(f.api.profile?.username).toBe('new_name');expect(f.api.user?.id).toBe(owner);expect(f.requests.some(request=>request.path.includes('community-delete-account'))).toBe(false);
  });
  it('keeps pending cleanup signed in and retryable',async()=>{
    const f=await account();f.reply=request=>request.path.startsWith('/rest/v1/profiles')?json(profile()):json({code:'KO004'},409);
    await expect(f.api.deleteAccount('player')).rejects.toMatchObject({code:'deletion_pending'});
    expect(f.api.user?.id).toBe(owner);expect(f.storage.values.size).toBeGreaterThan(0);
  });
  it('never clears credentials on an unacknowledged deletion',async()=>{
    const f=await account();f.reply=request=>request.path.startsWith('/rest/v1/profiles')?json(profile()):json({deleted:false});
    await expect(f.api.deleteAccount('player')).rejects.toMatchObject({code:'unavailable'});expect(f.api.user?.id).toBe(owner);
  });
  it('only clears the identity and saved session after the server acknowledges deletion',async()=>{
    const f=await account();f.reply=request=>request.path.startsWith('/rest/v1/profiles')?json(profile()):json({deleted:true});
    await f.api.deleteAccount('player');expect(f.api.user).toBeNull();expect(f.storage.values.size).toBe(0);
  });
  it('permits verified incomplete profiles to confirm account deletion using DELETE',async()=>{
    const f=await account();f.reply=()=>json(profile(null));await f.api.getProfile();f.reply=request=>request.path.startsWith('/rest/v1/profiles')?json(profile(null)):json({deleted:true});
    await f.api.deleteAccount('DELETE');expect(f.api.user).toBeNull();
  });
  it.each([false,true])('does not request a capability for hidden video or poster (%s)',async poster=>{
    const f=await account();f.reply=request=>{expect(request.path).toContain('community_get_clip');return json(clip({moderation_hidden:true}));};
    await expect(poster?f.api.getPosterUrl(clipId):f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({code:'clip_unavailable'});
  });
  it('requires a meaningful review note and preserves 1000-character server notes',async()=>{
    const f=await account();expect(()=>f.api.reviewClip(clipId,'hide','')).toThrow('Write a review note');
    f.reply=()=>json({reviewed:true,hidden:true});await f.api.reviewClip(clipId,'hide','a'.repeat(1000));
  });
});

describe('public configuration and browser sessions', () => {
  it('rejects privileged keys and non-root/insecure URLs', () => {
    for (const key of ['sb_secret_private', 'eyJpdiI.eyJyb2xlIjoic2VydmljZV9yb2xlIn0.signature', 'YOUR_KEY'])
      expect(() => createConfig(config.projectUrl, key)).toThrow(CommunityError);
    for (const url of ['http://community.example.com', 'https://user@community.example.com', 'https://community.example.com/api', 'https://community.example.com?x=1'])
      expect(() => createConfig(url, config.publishableKey)).toThrow(CommunityError);
  });
  it('uses session storage by default and local storage only after opt-in', () => {
    const tab = memoryStorage(); const remembered = memoryStorage();
    vi.stubGlobal('sessionStorage', tab); vi.stubGlobal('localStorage', remembered);
    browserSessionStorage().setItem('session', 'tab-only');
    expect(tab.values.get('session')).toBe('tab-only'); expect(remembered.values.size).toBe(0);
    browserSessionStorage(true).setItem('session', 'remembered'); expect(remembered.values.get('session')).toBe('remembered');
  });
  it('retains an ephemeral session when browser reads work but quota rejects writes', () => {
    const tab = memoryStorage();
    vi.stubGlobal('sessionStorage', { ...tab, setItem() { throw new DOMException('Quota exceeded', 'QuotaExceededError'); } });
    const storage = browserSessionStorage();
    storage.setItem('session', 'memory-only');
    expect(storage.getItem('session')).toBe('memory-only');
    expect(tab.values.size).toBe(0);
    storage.removeItem('session');
    expect(storage.getItem('session')).toBeNull();
  });
  it('keeps memory fallback authoritative if blocked browser storage becomes readable again', () => {
    const tab = memoryStorage(); let blocked = true;
    vi.stubGlobal('sessionStorage', { ...tab,
      getItem(key: string) { if (blocked) throw new DOMException('Blocked', 'SecurityError'); return tab.getItem(key); },
      setItem(key: string, value: string) { if (blocked) throw new DOMException('Blocked', 'SecurityError'); return tab.setItem(key, value); },
    });
    const storage = browserSessionStorage();
    expect(storage.getItem('session')).toBeNull();
    storage.setItem('session', 'memory-only'); blocked = false;
    expect(storage.getItem('session')).toBe('memory-only');
  });
  it('does not silently report sign-out complete if a known saved session cannot be removed', () => {
    const tab = memoryStorage(); tab.setItem('session', 'old-saved-session');
    vi.stubGlobal('sessionStorage', { ...tab,
      setItem() { throw new DOMException('Quota exceeded', 'QuotaExceededError'); },
      removeItem() { throw new DOMException('Blocked', 'SecurityError'); },
    });
    const storage = browserSessionStorage();
    expect(storage.getItem('session')).toBe('old-saved-session');
    storage.setItem('session', 'refreshed-memory-session');
    expect(() => storage.removeItem('session')).toThrow('could not remove your saved sign-in');
  });
  it('can clear a memory-only sign-in when all browser storage is blocked', () => {
    vi.stubGlobal('sessionStorage', {
      getItem() { throw new DOMException('Blocked', 'SecurityError'); },
      setItem() { throw new DOMException('Blocked', 'SecurityError'); },
      removeItem() { throw new DOMException('Blocked', 'SecurityError'); },
    });
    const storage = browserSessionStorage();
    expect(storage.getItem('session')).toBeNull(); storage.setItem('session', 'memory-only');
    expect(() => storage.removeItem('session')).not.toThrow(); expect(storage.getItem('session')).toBeNull();
  });
  it('explains inaccessible persistent storage instead of silently forgetting the Remember choice', () => {
    vi.stubGlobal('localStorage', {
      getItem() { throw new DOMException('Blocked', 'SecurityError'); },
      setItem() { throw new DOMException('Blocked', 'SecurityError'); },
      removeItem() { throw new DOMException('Blocked', 'SecurityError'); },
    });
    const storage = browserSessionStorage(true);
    expect(() => storage.getItem('session')).toThrow('Turn off Remember');
    expect(() => storage.setItem('session', 'not-saved')).toThrow('Turn off Remember');
    expect(() => storage.removeItem('session')).toThrow('could not remove your saved sign-in');
  });
  it('surfaces blocked Remember storage through the SDK without sending an auth request', async () => {
    vi.stubGlobal('localStorage', {
      getItem() { throw new DOMException('Blocked', 'SecurityError'); },
      setItem() { throw new DOMException('Blocked', 'SecurityError'); },
      removeItem() { throw new DOMException('Blocked', 'SecurityError'); },
    });
    const f = fixture(browserSessionStorage(true));
    await expect(f.api.restoreSession()).rejects.toMatchObject({ code: 'session_storage', message: expect.stringContaining('Turn off Remember') });
    expect(f.requests).toHaveLength(0); expect(f.api.user).toBeNull();
  });
  it('does not start a hosted request on construction and exposes no session tokens', async () => {
    const f = fixture(); await f.api.restoreSession();
    expect(f.requests).toHaveLength(0); expect(f.api.user).toBeNull(); expect('session' in f.api).toBe(false);
  });
  it.each([false, true])('preserves explicit create-account choice: %s', async create => {
    const f = fixture(); f.reply = () => json({}); await f.api.sendEmailCode(' player@example.com ', create);
    expect(f.requests.at(-1)?.path).toBe('/auth/v1/otp');
    expect(f.requests.at(-1)?.body).toMatchObject({ email: 'player@example.com', create_user: create });
  });
  it('verifies an email OTP and checks the actual user before exposing identity', async () => {
    const f = fixture(); await f.signIn();
    expect(f.requests[0].body).toMatchObject({ token: '123456', type: 'email' });
    expect(f.requests[1].path).toBe('/auth/v1/user'); expect(f.api.user).toEqual({ id: owner, email: 'player@example.com' });
    expect(JSON.stringify(f.api.user)).not.toContain('secret'); expect(f.storage.values.size).toBeGreaterThan(0);
  });
  it('can verify, use and sign out of a memory-only phone session when browser writes fail', async () => {
    const tab = memoryStorage();
    vi.stubGlobal('sessionStorage', { ...tab, setItem() { throw new DOMException('Quota exceeded', 'QuotaExceededError'); } });
    const f = fixture(browserSessionStorage()); await f.signIn();
    expect(f.api.user?.id).toBe(owner); expect(tab.values.size).toBe(0);
    f.reply = () => json([]); expect((await f.api.getFeed()).items).toEqual([]);
    expect(f.requests.at(-1)?.headers.get('Authorization')).toBe('Bearer access-secret');
    f.reply = () => json({}); await f.api.signOut();
    expect(f.api.user).toBeNull(); expect(tab.values.size).toBe(0);
  });
  it('restores only after server verification and does not share a desktop credential', async () => {
    const f = fixture(); await f.signIn(); f.api.dispose();
    const api = new CommunityApi(config, { storage: f.storage, fetch: async input => {
      expect(String(input)).toBe(`${config.projectUrl}/auth/v1/user`); return json(user);
    } }); instances.push(api);
    expect(api.user).toBeNull(); expect((await api.restoreSession())?.id).toBe(owner);
  });
  it('refreshes an expired saved phone session before verifying and exposing its user', async () => {
    const f = fixture(); await f.signIn(); f.api.dispose();
    const key = [...f.storage.values.keys()].find(key => key.endsWith('-auth'))!;
    const saved = JSON.parse(f.storage.values.get(key)!); saved.expires_at = Math.floor(Date.now() / 1000) - 60;
    f.storage.setItem(key, JSON.stringify(saved));
    const paths: string[] = [];
    const api = new CommunityApi(config, { storage: f.storage, fetch: async (input, init) => {
      const url = new URL(String(input)); paths.push(url.pathname);
      if (url.pathname === '/auth/v1/token') {
        expect(url.searchParams.get('grant_type')).toBe('refresh_token');
        expect(JSON.parse(String(init?.body))).toEqual({ refresh_token: 'refresh-secret' });
        return json(session());
      }
      if (url.pathname === '/auth/v1/user') return json(user);
      throw new Error('Unexpected fake auth request');
    } }); instances.push(api);
    expect(api.user).toBeNull(); expect((await api.restoreSession())?.id).toBe(owner);
    expect(paths).toEqual(['/auth/v1/token', '/auth/v1/user']);
  });
  it('preserves a saved session through a temporary offline restore and can retry without another email', async () => {
    const f = fixture(); await f.signIn(); f.api.dispose(); let offline = true;
    const api = new CommunityApi(config, { storage: f.storage, fetch: async () => {
      if (offline) throw new TypeError('Offline, including sensitive URL details'); return json(user);
    } }); instances.push(api);
    await expect(api.restoreSession()).rejects.toMatchObject({ code: 'offline' });
    expect(api.user).toBeNull(); expect(f.storage.values.size).toBeGreaterThan(0);
    offline = false; expect((await api.restoreSession())?.id).toBe(owner);
  });
  it('recognizes a server-expired session by its Auth error code even with HTTP 400', async () => {
    const f = fixture(); await f.signIn(); f.api.dispose();
    const api = new CommunityApi(config, { storage: f.storage, fetch: async () => {
      const response = json({ code: 'session_expired', message: 'Private details' }, 400);
      response.headers.set('x-supabase-api-version', '2024-01-01'); return response;
    } }); instances.push(api);
    expect(await api.restoreSession()).toBeNull();
    expect(api.user).toBeNull(); expect(f.storage.values.size).toBe(0);
  });
  it('clears local identity and storage even when remote sign-out is offline', async () => {
    const f = fixture(); await f.signIn(); f.reply = () => { throw new TypeError('private secret host'); };
    await f.api.signOut(); expect(f.api.user).toBeNull(); expect(f.storage.values.size).toBe(0);
    await expect(f.api.getFeed()).rejects.toMatchObject({ code: 'signed_out' });
  });
  it('canceling verification after the SDK saves the session does not leave a hidden sign-in', async () => {
    const f = fixture(); const cancellation = new AbortController();
    let ready!: () => void; const beginning = new Promise<void>(resolve => { ready = resolve; });
    let release!: (response: Response) => void;
    f.reply = request => request.path === '/auth/v1/verify' ? json(session()) : new Promise<Response>(resolve => { release = resolve; ready(); });
    const verification = f.api.verifyEmailCode('player@example.com', '123456', cancellation.signal);
    await beginning; expect(f.storage.values.size).toBeGreaterThan(0);
    cancellation.abort(); release(json(user));
    await expect(verification).rejects.toMatchObject({ name: 'AbortError' });
    expect(f.api.user).toBeNull(); expect(f.storage.values.size).toBe(0);
  });
  it('sign-out fences a late verification response before it can save credentials', async () => {
    const f = fixture(); let ready!: () => void; const beginning = new Promise<void>(resolve => { ready = resolve; });
    let release!: (response: Response) => void;
    f.reply = () => new Promise<Response>(resolve => { release = resolve; ready(); });
    const verification = f.api.verifyEmailCode('player@example.com', '123456');
    await beginning; const signingOut = f.api.signOut(); release(json(session()));
    await expect(verification).rejects.toMatchObject({ name: 'AbortError' }); await signingOut;
    expect(f.api.user).toBeNull(); expect(f.storage.values.size).toBe(0);
  });
  it('disposal never signs out or deletes a shared browser session', async () => {
    const f = fixture(); await f.signIn(); const size = f.storage.values.size; const requests = f.requests.length;
    f.api.dispose(); expect(f.storage.values.size).toBe(size); expect(f.requests).toHaveLength(requests);
  });
});

describe('authenticated Media contract', () => {
  it('requires sign-in and uses current user JWTs rather than a client owner argument', async () => {
    const f = fixture(); await expect(f.api.getFeed()).rejects.toMatchObject({ code: 'signed_out' });
    await f.signIn(); f.reply = () => json([]); await f.api.getFeed();
    const request = f.requests.at(-1)!;
    expect(request.headers.get('Authorization')).toBe('Bearer access-secret');
    expect(request.headers.get('apikey')).toBe(config.publishableKey);
    expect(JSON.stringify(request.body)).not.toContain(owner); expect(JSON.stringify(request.body)).not.toContain('secret');
  });
  it('reads/updates only explicit profile fields belonging to the current user', async () => {
    const f = fixture(); await f.signIn();
    f.reply = () => json([{ id: owner, username: 'player', display_name: 'Player', bio: 'Hello' }]);
    await f.api.getProfile(); await f.api.saveProfile({ username: ' PLAYER ', displayName: ' Player ', bio: ' Hello ' });
    expect(f.api.profile?.username).toBe('player');
    expect(f.requests.at(-1)?.body).toEqual({ username: 'player', display_name: 'Player', bio: 'Hello' });
    expect(f.requests.at(-1)?.headers.get('Prefer')).toBe('return=representation');
    expect(f.requests.at(-1)?.path).toBe(`/rest/v1/profiles?select=id,username,display_name,bio&id=eq.${owner}`);
  });
  it('uses bounded keyset pages and keeps PostgreSQL microsecond precision', async () => {
    const f = fixture(); await f.signIn();
    f.reply = () => json(Array.from({ length: 13 }, (_, i) => clip({ id: uuid(i + 1), video_path: null, poster_path: null,
      created_at: `2026-10-22T10:00:00.${String(999999 - i).padStart(6, '0')}Z` })));
    const result = await f.api.getFeed(); expect(result.items).toHaveLength(12);
    expect(result.nextCursor).toEqual({ id: uuid(12), createdAt: '2026-10-22T10:00:00.999988Z' });
    f.reply = () => json([]); await f.api.getFeed(result.nextCursor, true);
    expect(f.requests.at(-1)?.body).toEqual({ p_limit: 13, p_before: result.nextCursor!.createdAt, p_before_id: uuid(12), p_mine: true });
  });
  it('rejects oversized duplicate or foreign-own feed responses', async () => {
    const f = fixture(); await f.signIn();
    for (const rows of [[clip(), clip()], Array.from({ length: 14 }, () => clip()), [clip({ owner_id: uuid(9), video_path: null, poster_path: null })]]) {
      f.reply = () => json(rows); await expect(f.api.getFeed(null, true)).rejects.toMatchObject({ code: 'unavailable' });
    }
  });
  it('sets likes deterministically and returns server counts', async () => {
    const f = fixture(); await f.signIn(); f.reply = request => {
      expect(request.body).toEqual({ p_clip_id: clipId, p_liked: true }); return json(clip({ liked_by_me: true, like_count: 9 }));
    };
    expect((await f.api.setLiked(clipId, true)).likeCount).toBe(9); expect((await f.api.setLiked(clipId, true)).likedByMe).toBe(true);
  });
  it('paginates comments and submits only validated text', async () => {
    const f = fixture(); await f.signIn();
    f.reply = () => json(Array.from({ length: 31 }, (_, i) => comment(uuid(i + 1), `2026-10-22T10:00:${String(59 - i).padStart(2, '0')}Z`)));
    const page = await f.api.getComments(clipId); expect(page.items).toHaveLength(30); expect(page.nextCursor?.id).toBe(uuid(30));
    f.reply = request => { expect(request.body).toEqual({ p_clip_id: clipId, p_body: 'Nice clip' }); return json(comment()); };
    expect((await f.api.addComment(clipId, ' Nice clip ')).ownerId).toBe(owner);
    expect(() => f.api.addComment(clipId, 'x'.repeat(501))).toThrow(CommunityError);
  });
  it('provides reversible outgoing blocks without revealing reverse blockers', async () => {
    const f = fixture(); await f.signIn();
    f.reply = () => json([{ user_id: uuid(9), username: null, display_name: 'Player' }]);
    expect(await f.api.getBlockedUsers()).toEqual([{ userId: uuid(9), username: '', displayName: 'Player' }]);
    f.reply = request => { expect(request.body).toEqual({ p_user_id: uuid(9), p_blocked: false }); return json({ blocked: false }); };
    await f.api.setBlocked(uuid(9), false);
  });
  it('bounds JSON and maps errors without exposing server secrets', async () => {
    const f = fixture(); await f.signIn();
    f.reply = () => json({ code: 'KM005', message: 'secret private database https://private/?token=hidden' }, 400);
    await expect(f.api.getFeed()).rejects.toMatchObject({ code: 'storage_full', message: 'The free beta storage is full. Please try again later.' });
    f.reply = () => new Response('x', { headers: { 'content-length': String(512 * 1024 + 1) } });
    await expect(f.api.getFeed()).rejects.toMatchObject({ code: 'unavailable' });
  });
  it('accepts valid emoji character limits from PostgreSQL', async () => {
    const f = fixture(); await f.signIn(); f.reply = () => json([clip({ display_name: '🎮'.repeat(40), title: '🎮'.repeat(120) })]);
    expect((await f.api.getFeed()).items[0].displayName).toBe('🎮'.repeat(40));
  });
  it.each(['https://evil.example/object/sign/x?token=secret', 'http://community.example.com/object/sign/x?token=secret',
    '//evil.example/object/sign/x?token=secret', '/object/sign/community-videos/wrong/video.mp4?token=secret'])('rejects an untrusted signed URL: %s', async signedURL => {
    const f = fixture(); await f.signIn(); f.reply = request => request.path.endsWith('community_get_clip') ? json(clip()) : json({ signedURL });
    await expect(f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({ code: 'unavailable' });
  });
  it('signs only the exact current clip path for a short lifetime', async () => {
    const f = fixture(); await f.signIn(); f.reply = request => request.path.endsWith('community_get_clip') ? json(clip())
      : json({ signedURL: `/object/sign/community-videos/${owner}/${clipId}/video.mp4?token=short-lived` });
    expect(await f.api.getPlaybackUrl(clipId)).toBe(`${config.projectUrl}/storage/v1/object/sign/community-videos/${owner}/${clipId}/video.mp4?token=short-lived`);
    expect(f.requests.at(-1)?.body).toEqual({ expiresIn: 300 });
  });
  it('never returns a late response after sign-out', async () => {
    const f = fixture(); await f.signIn(); let resolve!: (value: Response) => void;
    let started!: () => void; const beginning = new Promise<void>(r => { started = r; });
    f.reply = request => request.path.endsWith('community_feed') ? new Promise<Response>(r => { resolve = r; started(); }) : json({});
    const pending = f.api.getFeed(); await beginning; const signOut = f.api.signOut(); resolve(json([]));
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' }); await signOut; expect(f.api.user).toBeNull();
  });
});

describe('viewer-only Media and owned clip cleanup', () => {
  it('does not expose browser upload or upload-transport capabilities', () => {
    const f = fixture();
    expect('uploadClip' in f.api).toBe(false);
    expect('uploadTransport' in f.api).toBe(false);
    expect(f.requests).toHaveLength(0);
  });
  it('hides and deletes owned assets before releasing an existing clip slot', async () => {
    const f = fixture(); await f.signIn(); f.reply = cleanupReply;
    await f.api.deleteClip(clipId);
    const cleanup = f.requests.slice(-4);
    expect(cleanup.map(request => request.path)).toEqual([
      '/rest/v1/rpc/community_remove_clip', '/storage/v1/object/community-videos',
      '/storage/v1/object/community-posters', '/rest/v1/rpc/community_finish_delete']);
    expect(cleanup.map(request => request.method)).toEqual(['POST', 'DELETE', 'DELETE', 'POST']);
    expect(cleanup[0].body).toEqual({ p_clip_id: clipId });
    expect(cleanup[1].body).toEqual({ prefixes: [owner + '/' + clipId + '/video.mp4'] });
    expect(cleanup[2].body).toEqual({ prefixes: [owner + '/' + clipId + '/poster.jpg'] });
    expect(cleanup.every(request => request.headers.get('Authorization') === 'Bearer access-secret')).toBe(true);
  });
  it('retains a deletion reservation and tries both assets when physical cleanup fails', async () => {
    const f = fixture(); await f.signIn(); f.reply = request => {
      if (request.path.endsWith('community_remove_clip')) return json(clip({ status: 'deleting' }));
      return request.path.endsWith('community-videos') ? json({ message: 'private upstream failure' }, 502) : json([]);
    };
    await expect(f.api.deleteClip(clipId)).rejects.toBeInstanceOf(CommunityError);
    expect(f.requests.filter(request => request.method === 'DELETE')).toHaveLength(2);
    expect(f.requests.some(request => request.path.endsWith('community_finish_delete'))).toBe(false);
  });
  it('accepts an already deleted own clip without redundant Storage changes', async () => {
    const f = fixture(); await f.signIn(); const previous = f.requests.length;
    f.reply = () => json({ id: clipId, status: 'deleted' });
    await f.api.deleteClip(clipId);
    expect(f.requests.slice(previous).map(request => request.path)).toEqual(['/rest/v1/rpc/community_remove_clip']);
  });
  it('cleans owned incomplete-profile reservations using identity/path only', async () => {
    const f = fixture(); await f.signIn(); f.reply = request => request.path.endsWith('community_remove_clip')
      ? json({ id: clipId, owner_id: owner, status: 'deleting', username: null }) : cleanupReply(request);
    await f.api.deleteClip(clipId); expect(f.requests.at(-1)?.path).toBe('/rest/v1/rpc/community_finish_delete');
  });
});

describe('home-hosted media alongside existing clips', () => {
  const homeConfig = createConfig({...config, mediaApiUrl: 'https://media.example.com'});
  function homeLinks(overrides:Record<string,unknown>={}) {
    const now=Math.floor(Date.now()/1000);
    return { video_url:`https://media.example.com/v1/assets/${owner}/${clipId}/video?expires=${now+300}&signature=${'a'.repeat(64)}`,
      poster_url:`https://media.example.com/v1/assets/${owner}/${clipId}/poster?expires=${now+120}&signature=${'b'.repeat(64)}`,
      expires_at:new Date((now+120)*1000).toISOString(),...overrides };
  }
  it('validates an optional home origin without changing default operations availability',()=>{
    expect(homeConfig.mediaApiUrl).toBe('https://media.example.com');
    expect(homeConfig.operationsEnabled).toBeUndefined();
    for(const mediaApiUrl of ['http://media.example.com','https://player@media.example.com','https://media.example.com/api','https://media.example.com/?key=x'])
      expect(()=>createConfig({...config,mediaApiUrl})).toThrow(CommunityError);
  });
  it('signs only the configured home asset with bearer auth and forbids redirects',async()=>{
    const f=fixture(undefined,homeConfig);await f.signIn();
    f.reply=request=>request.path.endsWith('community_get_clip')?json(clip({storage_provider:'home'})):json(homeLinks());
    const url=await f.api.getPlaybackUrl(clipId);
    expect(new URL(url).pathname).toBe(`/v1/assets/${owner}/${clipId}/video`);
    const request=f.requests.at(-1)!;
    expect(request.path).toBe(`/v1/clips/${clipId}/playback`);expect(request.origin).toBe(homeConfig.mediaApiUrl);
    expect(request.headers.get('authorization')).toBe('Bearer access-secret');expect(request.headers.get('apikey')).toBeNull();
    expect(request.redirect).toBe('error');expect(request.body).toEqual({});
    expect(new URL(await f.api.getPosterUrl(clipId)).pathname).toBe(`/v1/assets/${owner}/${clipId}/poster`);
  });
  it('retains legacy signing when a home origin is configured',async()=>{
    const f=fixture(undefined,homeConfig);await f.signIn();
    f.reply=request=>request.path.endsWith('community_get_clip')?json(clip()):json({signedURL:`/object/sign/community-videos/${owner}/${clipId}/video.mp4?token=legacy`});
    expect(await f.api.getPlaybackUrl(clipId)).toContain(`${config.projectUrl}/storage/v1/object/sign/`);
    expect(f.requests.at(-1)?.origin).toBe(config.projectUrl);
  });
  it.each(['https://evil.example.com','http://media.example.com','https://player@media.example.com'])('rejects a home asset from an untrusted origin %s',async origin=>{
    const f=fixture(undefined,homeConfig);await f.signIn();const links=homeLinks();
    f.reply=request=>request.path.endsWith('community_get_clip')?json(clip({storage_provider:'home'})):json({...links,video_url:String(links.video_url).replace('https://media.example.com',origin)});
    await expect(f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({code:'unavailable'});
  });
  it.each(['&extra=1','&signature=duplicate'])('rejects unexpected or duplicate signing fields %s',async suffix=>{
    const f=fixture(undefined,homeConfig);await f.signIn();const links=homeLinks();
    f.reply=request=>request.path.endsWith('community_get_clip')?json(clip({storage_provider:'home'})):json({...links,video_url:links.video_url+suffix});
    await expect(f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({code:'unavailable'});
  });
  it('rejects expired or unexpectedly long-lived home links',async()=>{
    const f=fixture(undefined,homeConfig);await f.signIn();
    for(const offset of [-1,10000]){
      const links=homeLinks(),expiry=Math.floor(Date.now()/1000)+offset;
      f.reply=request=>request.path.endsWith('community_get_clip')?json(clip({storage_provider:'home'})):json({...links,video_url:links.video_url.replace(/expires=\d+/,`expires=${expiry}`)});
      await expect(f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({code:'unavailable'});
    }
  });
  it('fails closed for unknown providers or a home clip without a configured endpoint',async()=>{
    const f=fixture();await f.signIn();f.reply=()=>json(clip({storage_provider:'home'}));
    await expect(f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({code:'media_not_configured'});
    expect(f.requests.at(-1)?.path).toBe('/rest/v1/rpc/community_get_clip');
    f.reply=()=>json([clip({storage_provider:'arbitrary'})]);await expect(f.api.getFeed()).rejects.toMatchObject({code:'unavailable'});
  });
  it('deletes home assets through the authenticated host and checks its acknowledgement',async()=>{
    const f=fixture(undefined,homeConfig);await f.signIn();
    f.reply=request=>request.path.endsWith('community_remove_clip')?json(clip({status:'deleting',storage_provider:'home'})):json({id:clipId,deleted:true});
    await f.api.deleteClip(clipId);
    expect(f.requests.slice(-2).map(request=>request.path)).toEqual(['/rest/v1/rpc/community_remove_clip',`/v1/clips/${clipId}`]);
    expect(f.requests.at(-1)?.method).toBe('DELETE');expect(f.requests.at(-1)?.headers.get('apikey')).toBeNull();
    f.reply=request=>request.path.endsWith('community_remove_clip')?json(clip({status:'deleting',storage_provider:'home'})):json({id:owner,deleted:true});
    await expect(f.api.deleteClip(clipId)).rejects.toMatchObject({code:'unavailable'});
  });
  it('does not switch to legacy storage when home hosting is offline',async()=>{
    const f=fixture(undefined,homeConfig);await f.signIn();f.reply=request=>request.path.endsWith('community_get_clip')?json(clip({storage_provider:'home'})):json({},503);
    await expect(f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({code:'offline'});
    expect(f.requests.some(request=>request.path.startsWith('/storage/'))).toBe(false);
  });
  it.each([
    ['uploads_paused','Uploads are temporarily paused while clip storage is being prepared. Please try again later.'],
    ['backup_required','Uploads are temporarily paused until the server backup and recovery checks pass. Please try again later.'],
  ])('explains paused hosting without displaying private server details: %s',async(code,message)=>{
    const f=fixture(undefined,homeConfig);await f.signIn();f.reply=request=>request.path.endsWith('community_get_clip')?json(clip({storage_provider:'home'})):json({code,message:'private provider detail'},503);
    await expect(f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({code:'hosting_unavailable',message});
  });
  it('rejects a malformed signed link as an unavailable clip',async()=>{
    const f=fixture(undefined,homeConfig);await f.signIn();f.reply=request=>request.path.endsWith('community_get_clip')?json(clip({storage_provider:'home'})):json(homeLinks({video_url:'not a URL'}));
    await expect(f.api.getPlaybackUrl(clipId)).rejects.toMatchObject({code:'unavailable'});
  });
  it('never returns a late home playback link after sign-out',async()=>{
    const f=fixture(undefined,homeConfig);await f.signIn();let resolve!:(value:Response)=>void,started!:()=>void;const beginning=new Promise<void>(done=>{started=done;});
    f.reply=request=>request.path.endsWith('community_get_clip')?json(clip({storage_provider:'home'})):request.path.endsWith('/playback')?new Promise<Response>(done=>{resolve=done;started();}):json({});
    const pending=f.api.getPlaybackUrl(clipId);await beginning;const signout=f.api.signOut();resolve(json(homeLinks()));
    await expect(pending).rejects.toMatchObject({name:'AbortError'});await signout;expect(f.api.user).toBeNull();
  });
  it('keeps browser upload capabilities absent when home hosting is configured',()=>{
    const f=fixture(undefined,homeConfig);expect('uploadClip' in f.api).toBe(false);expect('uploadTransport' in f.api).toBe(false);
  });
});
