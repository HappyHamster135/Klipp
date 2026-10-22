import { afterEach, describe, expect, it, vi } from 'vitest';
import { CommunityApi } from './api';
import { browserSessionStorage, createConfig } from './config';
import { CommunityError, mediaLimits, type StorageLike, type CommunityUploadProgress } from './models';

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
interface Captured { path: string; method: string; headers: Headers; body: unknown; signal: AbortSignal | null | undefined }
function fixture() {
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
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body, signal: init?.signal };
    requests.push(request); return reply(request);
  }) as typeof fetch;
  const api = new CommunityApi(config, { storage, fetch: fetcher }); instances.push(api);
  return { api, requests, storage, set reply(value: typeof reply) { reply = value; }, signIn: () => api.verifyEmailCode('player@example.com', '123456') };
}
function generatedUpload() { return { video: new Blob([new Uint8Array(1024)], { type: 'video/mp4' }),
  poster: new Blob([new Uint8Array(128)], { type: 'image/jpeg' }), title: 'A clip', description: '', durationSeconds: 2 }; }
function cleanupReply(request: Captured): Response {
  if (request.path.endsWith('community_begin_upload')) return json(clip({ status: 'uploading' }));
  if (request.path.endsWith('community_remove_clip')) return json(clip({ status: 'deleting' }));
  if (request.path.endsWith('community_finish_delete')) return json({ id: clipId, deleted: true });
  return json([]);
}

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
  it('restores only after server verification and does not share a desktop credential', async () => {
    const f = fixture(); await f.signIn(); f.api.dispose();
    const api = new CommunityApi(config, { storage: f.storage, fetch: async input => {
      expect(String(input)).toBe(`${config.projectUrl}/auth/v1/user`); return json(user);
    } }); instances.push(api);
    expect(api.user).toBeNull(); expect((await api.restoreSession())?.id).toBe(owner);
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

describe('intentional browser uploads and cleanup', () => {
  it('passes Blob bodies without reading entire files and uses no-upsert fixed paths', async () => {
    const f = fixture(); await f.signIn(); f.reply = request => request.path.endsWith('community_begin_upload') ? json(clip({ status: 'uploading' }))
      : request.path.endsWith('community_publish_clip') ? json(clip()) : json({ Key: 'ok' });
    const progress: CommunityUploadProgress[] = []; const upload = generatedUpload();
    expect((await f.api.uploadClip(upload, value => progress.push(value))).status).toBe('published');
    const objects = f.requests.filter(request => request.path.startsWith('/storage/v1/object/'));
    expect(objects).toHaveLength(2); expect(objects[0].body).toBe(upload.video); expect(objects[1].body).toBe(upload.poster);
    expect(objects[0].headers.get('x-upsert')).toBe('false'); expect(objects[0].path).toBe(`/storage/v1/object/community-videos/${owner}/${clipId}/video.mp4`);
    expect(progress.at(-1)).toEqual({ completedBytes: 1152, totalBytes: 1152, status: 'Uploaded' });
  });
  it('rejects oversized or incompatible files before reserving cloud capacity', async () => {
    const f = fixture(); await f.signIn(); const count = f.requests.length; const upload = generatedUpload();
    expect(() => f.api.uploadClip({ ...upload, video: new Blob([new Uint8Array(mediaLimits.videoBytes + 1)], { type: 'video/mp4' }) })).toThrow(CommunityError);
    expect(() => f.api.uploadClip({ ...upload, durationSeconds: NaN })).toThrow(CommunityError);
    expect(() => f.api.uploadClip({ ...upload, poster: new Blob(['x'], { type: 'image/png' }) })).toThrow(CommunityError);
    expect(f.requests).toHaveLength(count);
  });
  it('cancellation hides and deletes partial objects before releasing a reservation', async () => {
    const f = fixture(); await f.signIn(); const cancellation = new AbortController();
    f.reply = request => {
      if (request.path.startsWith('/storage/v1/object/community-videos/') && request.method === 'POST') { cancellation.abort(); throw new DOMException('Canceled', 'AbortError'); }
      return cleanupReply(request);
    };
    await expect(f.api.uploadClip(generatedUpload(), undefined, cancellation.signal)).rejects.toMatchObject({ name: 'AbortError' });
    const cleanup = f.requests.slice(-4); expect(cleanup.map(request => request.path)).toEqual([
      '/rest/v1/rpc/community_remove_clip', '/storage/v1/object/community-videos', '/storage/v1/object/community-posters', '/rest/v1/rpc/community_finish_delete']);
    expect(cleanup.every(request => request.headers.get('Authorization') === 'Bearer access-secret')).toBe(true);
  });
  it('failed physical cleanup retains its reservation and attempts both objects', async () => {
    const f = fixture(); await f.signIn(); f.reply = request => {
      if (request.path.endsWith('community_remove_clip')) return json(clip({ status: 'deleting' }));
      if (request.path.endsWith('community_begin_upload')) return json(clip({ status: 'uploading' }));
      return json({ message: 'private upstream failure' }, 502);
    };
    await expect(f.api.uploadClip(generatedUpload())).rejects.toBeInstanceOf(CommunityError);
    expect(f.requests.filter(request => request.method === 'DELETE')).toHaveLength(2);
    expect(f.requests.some(request => request.path.endsWith('community_finish_delete'))).toBe(false);
  });
  it('cleans owned incomplete-profile reservations using identity/path only', async () => {
    const f = fixture(); await f.signIn(); f.reply = request => request.path.endsWith('community_remove_clip')
      ? json({ id: clipId, owner_id: owner, status: 'deleting', username: null }) : cleanupReply(request);
    await f.api.deleteClip(clipId); expect(f.requests.at(-1)?.path).toBe('/rest/v1/rpc/community_finish_delete');
  });
});
