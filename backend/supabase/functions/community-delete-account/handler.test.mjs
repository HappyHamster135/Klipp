import test from 'node:test';
import assert from 'node:assert/strict';
import { createHandler } from './handler.ts';
import { deadline, readJson, requestJson } from './http.ts';

const project = 'https://example.supabase.co';
const owner = '11223344-1234-1234-1234-123456789abc';
const other = '99887766-1234-1234-1234-123456789abc';
const clipId = 'aabbccdd-1234-1234-1234-123456789abc';
const secondId = 'aabbccdd-1234-1234-1234-123456789abd';
const token = 'eyJfakeHeader.eyJfakeBody.signatureForTests';
const publicKey = 'test-public-key';
const adminKey = 'test-admin-key-not-a-real-secret';
const configuration = { projectUrl: project, publishableKey: publicKey, serviceRoleKey: adminKey };
const json = (body, status = 200, headers = {}) => new Response(JSON.stringify(body), {
  status, headers: { 'Content-Type': 'application/json', ...headers },
});
const clip = (provider = 'home', id = clipId) => ({ id, owner_id: owner, storage_provider: provider,
  video_path: `${owner}/${id}/video.mp4`, poster_path: `${owner}/${id}/poster.jpg` });
const request = (overrides = {}) => new Request(`${project}/functions/v1/community-delete-account`, {
  method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...overrides.headers },
  body: JSON.stringify(overrides.body ?? { confirmation: 'jonte' }),
});

function fixture({ clips = [], override = () => undefined } = {}) {
  const calls = [];
  const fetcher = async (input, init = {}) => {
    const url = String(input);
    const headers = new Headers(init.headers);
    const body = init.body === undefined ? undefined : JSON.parse(String(init.body));
    const call = { url, init, headers, body }; calls.push(call);
    assert.equal(init.redirect, 'error');
    assert.equal(init.cache, 'no-store');
    assert.ok(init.signal instanceof AbortSignal);
    const custom = await override(call);
    if (custom !== undefined) return custom;
    if (url === `${project}/auth/v1/user`) {
      assert.equal(init.method, 'GET');
      assert.equal(headers.get('apikey'), publicKey);
      assert.equal(headers.get('authorization'), `Bearer ${token}`);
      return json({ id: owner, email_confirmed_at: '2026-10-23T00:00:00Z', is_anonymous: false });
    }
    if (url === `${project}/rest/v1/rpc/community_prepare_account_delete`) {
      assert.equal(headers.get('apikey'), publicKey);
      assert.deepEqual(body, { p_confirmation: 'jonte' });
      return json({ pending: true, owner_id: owner, clips });
    }
    if (url.startsWith('https://media.shlipp.com/v1/clips/')) {
      assert.equal(init.method, 'DELETE');
      assert.equal(headers.get('authorization'), `Bearer ${token}`);
      assert.equal(headers.get('apikey'), null);
      assert.equal(headers.get('origin'), null);
      return json({ id: url.split('/').at(-1), deleted: true });
    }
    if (url === `${project}/storage/v1/object/community-videos` || url === `${project}/storage/v1/object/community-posters`) {
      assert.equal(init.method, 'DELETE');
      assert.equal(headers.get('apikey'), publicKey);
      assert.equal(headers.get('authorization'), `Bearer ${token}`);
      return json(body.prefixes.map(name => ({ name })));
    }
    if (url === `${project}/rest/v1/rpc/community_finish_delete`) {
      assert.equal(headers.get('apikey'), publicKey);
      return json({ id: body.p_clip_id, deleted: true });
    }
    if (url === `${project}/rest/v1/rpc/community_account_delete_ready`) {
      assert.equal(headers.get('apikey'), adminKey);
      assert.equal(headers.get('authorization'), `Bearer ${adminKey}`);
      assert.deepEqual(body, { p_owner_id: owner });
      return json({ ready: true, owner_id: owner });
    }
    if (url === `${project}/auth/v1/admin/users/${owner}`) {
      assert.equal(init.method, 'DELETE');
      assert.equal(headers.get('apikey'), adminKey);
      assert.equal(headers.get('authorization'), `Bearer ${adminKey}`);
      assert.deepEqual(body, { should_soft_delete: false });
      return json({ id: owner });
    }
    assert.fail(`Unexpected test route: ${url}`);
  };
  return { calls, handle: createHandler(configuration, fetcher) };
}
const wasDeleted = calls => calls.some(call => call.url.includes('/auth/v1/admin/users/'));
const usedAdmin = calls => calls.some(call => call.headers.get('apikey') === adminKey);

test('account without clips: live user -> SQL intent -> privileged ready -> hard Auth deletion', async () => {
  const f = fixture(); const result = await f.handle(request());
  assert.equal(result.status, 200); assert.deepEqual(await result.json(), { deleted: true });
  assert.deepEqual(f.calls.map(call => new URL(call.url).pathname), [
    '/auth/v1/user', '/rest/v1/rpc/community_prepare_account_delete',
    '/rest/v1/rpc/community_account_delete_ready', `/auth/v1/admin/users/${owner}`,
  ]);
  assert.match(result.headers.get('cache-control'), /no-store/);
  assert.equal(result.headers.get('access-control-allow-origin'), null);
});

test('home and legacy cleanup uses owner credentials before any administrator request', async () => {
  const f = fixture({ clips: [clip(), clip('supabase', secondId)] });
  const result = await f.handle(request({ headers: { Origin: 'https://happyhamster135.github.io' } }));
  assert.equal(result.status, 200);
  const physical = f.calls.filter(call => call.url.includes('/storage/v1/object/'));
  assert.deepEqual(physical.map(call => call.body), [
    { prefixes: [`${owner}/${secondId}/video.mp4`] }, { prefixes: [`${owner}/${secondId}/poster.jpg`] },
  ]);
  const readyIndex = f.calls.findIndex(call => call.url.endsWith('community_account_delete_ready'));
  assert.ok(f.calls.findIndex(call => call.url.endsWith('community_finish_delete')) < readyIndex);
  assert.equal(result.headers.get('access-control-allow-origin'), 'https://happyhamster135.github.io');
  assert.equal(f.calls.some(call => call.init.method === 'PUT'), false);
});

test('legacy missing objects are idempotent: empty remove results still require SQL absence acknowledgement', async () => {
  const f = fixture({ clips: [clip('supabase')], override: call => call.url.includes('/storage/v1/object/') ? json([]) : undefined });
  assert.equal((await f.handle(request())).status, 200);
  assert.ok(f.calls.some(call => call.url.endsWith('community_finish_delete')));
});

test('account without a username supports the explicit DELETE fallback confirmation', async () => {
  const f = fixture({ override: call => {
    if (!call.url.endsWith('community_prepare_account_delete')) return undefined;
    assert.deepEqual(call.body, { p_confirmation: 'DELETE' });
    return json({ pending: true, owner_id: owner, clips: [] });
  } });
  assert.equal((await f.handle(request({ body: { confirmation: 'DELETE' } }))).status, 200);
});

test('retry after a committed partial cleanup succeeds without repeating or abandoning reservations', async () => {
  let removedHome = false; let failLegacy = true;
  const f = fixture({ override: call => {
    if (call.url.endsWith('community_prepare_account_delete')) {
      return json({ pending: true, owner_id: owner, clips: [...(removedHome ? [] : [clip()]), clip('supabase', secondId)] });
    }
    if (call.url.startsWith('https://media.shlipp.com/')) removedHome = true;
    if (call.url.endsWith('/storage/v1/object/community-videos') && failLegacy) return json({}, 503);
    return undefined;
  } });
  const first = await f.handle(request());
  assert.equal(first.status, 409); assert.equal(usedAdmin(f.calls), false);
  assert.equal(removedHome, true); failLegacy = false;
  const second = await f.handle(request()); assert.equal(second.status, 200);
  assert.deepEqual(await second.json(), { deleted: true });
  assert.equal(f.calls.filter(call => call.url.startsWith('https://media.shlipp.com/')).length, 1);
});

test('same user concurrent deletion returning Auth 404 is acknowledged only after SQL ready', async () => {
  const f = fixture({ override: call => call.url.includes('/auth/v1/admin/users/') ? json({ code: 'user_not_found' }, 404) : undefined });
  assert.equal((await f.handle(request())).status, 200);
  assert.ok(usedAdmin(f.calls));
});

for (const [name, changes, expected] of [
  ['missing bearer', { headers: { Authorization: '' } }, 401],
  ['combined bearer', { headers: { Authorization: `Bearer ${token}, Bearer ${token}` } }, 401],
  ['API key as bearer', { headers: { Authorization: 'Bearer sb_publishable_not_a_jwt' } }, 401],
  ['foreign origin', { headers: { Origin: 'https://attacker.example' } }, 403],
  ['null origin', { headers: { Origin: 'null' } }, 403],
  ['extra target account', { body: { confirmation: 'jonte', owner_id: other } }, 400],
  ['injected media origin', { body: { confirmation: 'jonte', mediaUrl: 'http://127.0.0.1' } }, 400],
  ['wrong body type', { body: ['jonte'] }, 400],
  ['too much request data', { body: { confirmation: 'x'.repeat(2000) } }, 400],
  ['bad content type', { headers: { 'Content-Type': 'text/plain' } }, 400],
]) test(`${name} cannot reach any upstream service`, async () => {
  const f = fixture(); const result = await f.handle(request(changes));
  assert.equal(result.status, expected); assert.equal(f.calls.length, 0);
  assert.equal(result.headers.get('access-control-allow-origin'), null);
});

test('wrong username remains a safe SQL validation error; no privileged request', async () => {
  const f = fixture({ override: call => call.url.endsWith('community_prepare_account_delete') ? json({ code: 'KO002', details: token }, 400) : undefined });
  const result = await f.handle(request());
  assert.equal(result.status, 400); assert.deepEqual(await result.json(), { code: 'KO002' });
  assert.equal(usedAdmin(f.calls), false);
});

test('database request limit returns a bounded retry hint', async () => {
  const f = fixture({ override: call => call.url.endsWith('community_prepare_account_delete') ? json({ code: 'KO003' }, 400) : undefined });
  const result = await f.handle(request());
  assert.equal(result.status, 429); assert.equal(result.headers.get('retry-after'), '60');
  assert.equal(usedAdmin(f.calls), false);
});

for (const bad of [
  { pending: false, owner_id: owner, clips: [] },
  { pending: true, owner_id: other, clips: [] },
  { pending: true, owner_id: owner, clips: [clip(), clip()] },
  { pending: true, owner_id: owner, clips: Array.from({length: 6}, (_, index) => clip('home', `aabbccdd-1234-1234-1234-123456789ab${index}`)) },
  { pending: true, owner_id: owner, clips: [{ ...clip(), owner_id: other }] },
  { pending: true, owner_id: owner, clips: [{ ...clip(), storage_provider: 'https://attacker.example' }] },
  { pending: true, owner_id: owner, clips: [{ ...clip(), id: '../admin' }] },
  { pending: true, owner_id: owner, clips: [{ ...clip(), video_path: `${other}/${clipId}/video.mp4` }] },
  { pending: true, owner_id: owner, clips: [{ ...clip(), poster_path: `${owner}/../poster.jpg` }] },
]) test(`untrusted deletion plan cannot cause physical or Auth deletion: ${JSON.stringify(bad).slice(0, 90)}`, async () => {
  const f = fixture({ override: call => call.url.endsWith('community_prepare_account_delete') ? json(bad) : undefined });
  const result = await f.handle(request());
  assert.equal(result.status, 409); assert.deepEqual(await result.json(), { code: 'KO004' });
  assert.equal(f.calls.length, 2); assert.equal(usedAdmin(f.calls), false);
});

for (const status of [401, 403, 429, 500, 503]) test(`home cleanup ${status} preserves account and remains retryable`, async () => {
  const f = fixture({ clips: [clip()], override: call => call.url.startsWith('https://media.shlipp.com/') ? json({ detail: adminKey }, status) : undefined });
  const result = await f.handle(request());
  assert.equal(result.status, 409); assert.deepEqual(await result.json(), { code: 'KO004' });
  assert.equal(usedAdmin(f.calls), false);
});

for (const value of [{ id: other, deleted: true }, { id: clipId, deleted: false }]) test('home deletion acknowledgement must match exact clip', async () => {
  const f = fixture({ clips: [clip()], override: call => call.url.startsWith('https://media.shlipp.com/') ? json(value) : undefined });
  assert.equal((await f.handle(request())).status, 409); assert.equal(usedAdmin(f.calls), false);
});

for (const suffix of ['storage/v1/object/community-videos', 'storage/v1/object/community-posters', 'rest/v1/rpc/community_finish_delete', 'rest/v1/rpc/community_account_delete_ready']) {
  test(`${suffix} failure never deletes Auth account`, async () => {
    const f = fixture({ clips: [clip('supabase')], override: call => call.url.endsWith(suffix) ? json({ code: 'KO004' }, 409) : undefined });
    const result = await f.handle(request());
    assert.equal(result.status, 409); assert.equal(wasDeleted(f.calls), false);
  });
}

test('ready acknowledgement must match live authenticated owner', async () => {
  const f = fixture({ override: call => call.url.endsWith('community_account_delete_ready') ? json({ ready: true, owner_id: other }) : undefined });
  assert.equal((await f.handle(request())).status, 409); assert.equal(wasDeleted(f.calls), false);
});

test('transient final Auth failure retains retryable intent and never falsely clears client session', async () => {
  const f = fixture({ override: call => call.url.includes('/auth/v1/admin/users/') ? json({ message: adminKey }, 503) : undefined });
  const result = await f.handle(request());
  assert.equal(result.status, 409); assert.deepEqual(await result.json(), { code: 'KO004' });
  assert.equal(f.calls.some(call => call.url.includes('/logout')), false);
});

for (const data of [
  { id: owner, is_anonymous: true, email_confirmed_at: '2026-10-23T00:00:00Z' },
  { id: owner, is_anonymous: false, email_confirmed_at: null },
]) test('anonymous or unverified account cannot prepare deletion', async () => {
  const f = fixture({ override: call => call.url.endsWith('/auth/v1/user') ? json(data) : undefined });
  assert.equal((await f.handle(request())).status, 403); assert.equal(f.calls.length, 1);
});

test('live Auth refusal never trusts JWT claims, user metadata, or a stale cached user', async () => {
  const f = fixture({ override: call => call.url.endsWith('/auth/v1/user') ? json({ user_metadata: { role: 'admin' }, message: token }, 401) : undefined });
  const result = await f.handle(request()); assert.equal(result.status, 401);
  assert.deepEqual(await result.json(), { code: 'session_expired' }); assert.equal(f.calls.length, 1);
});

test('upstream network exceptions are sanitized; cleanup phase gets retryable pending', async () => {
  const f = fixture({ override: call => { if (call.url.endsWith('community_prepare_account_delete')) throw new Error(`${adminKey} ${token}`); } });
  const result = await f.handle(request());
  assert.equal(result.status, 409); assert.deepEqual(await result.json(), { code: 'KO004' });
});

test('redirected home response cannot leak caller credentials or trigger administrator deletion', async () => {
  const f = fixture({ clips: [clip()], override: call => call.url.startsWith('https://media.shlipp.com/') ? new Response(null, { status: 307, headers: { Location: 'https://attacker.example' } }) : undefined });
  assert.equal((await f.handle(request())).status, 409); assert.equal(usedAdmin(f.calls), false);
  assert.equal(f.calls.some(call => call.url.startsWith('https://attacker.example')), false);
});

test('exact browser origin supports preflight without auth or upstream requests', async () => {
  const f = fixture();
  const result = await f.handle(new Request(`${project}/functions/v1/community-delete-account`, { method: 'OPTIONS', headers: {
    Origin: 'https://happyhamster135.github.io', 'Access-Control-Request-Method': 'POST',
    'Access-Control-Request-Headers': 'authorization,apikey,content-type',
  } }));
  assert.equal(result.status, 200); assert.equal(result.headers.get('access-control-allow-methods'), 'POST, OPTIONS');
  assert.equal(result.headers.get('access-control-allow-credentials'), null); assert.equal(f.calls.length, 0);
});

test('preflight cannot enable upload methods or arbitrary headers', async () => {
  const f = fixture();
  for (const extra of [
    { 'Access-Control-Request-Method': 'PUT' },
    { 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'x-admin-target' },
  ]) {
    const result = await f.handle(new Request(project, { method: 'OPTIONS', headers: { Origin: 'https://happyhamster135.github.io', ...extra } }));
    assert.equal(result.status, 403);
  }
  assert.equal(f.calls.length, 0);
});

test('GET never performs account mutation', async () => {
  const f = fixture(); const result = await f.handle(new Request(project));
  assert.equal(result.status, 405); assert.equal(result.headers.get('allow'), 'POST, OPTIONS'); assert.equal(f.calls.length, 0);
});

test('invalid JSON is rejected before authentication or other upstream work', async () => {
  const f = fixture();
  const result = await f.handle(new Request(project, { method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' }, body: '{bad json' }));
  assert.equal(result.status, 400); assert.equal(f.calls.length, 0);
});

test('oversized upstream Auth response cannot proceed to intent or expose its private payload', async () => {
  const f = fixture({ override: call => call.url.endsWith('/auth/v1/user') ? json({ secret: adminKey, metadata: 'x'.repeat(65536) }) : undefined });
  const result = await f.handle(request());
  assert.equal(result.status, 503); assert.deepEqual(await result.json(), { code: 'unavailable' });
  assert.equal(f.calls.length, 1);
});

test('stream length is bounded even without Content-Length', async () => {
  let cancelled = false;
  const stream = new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(65537)); }, cancel() { cancelled = true; } });
  await assert.rejects(readJson(new Response(stream), 65536, new AbortController().signal));
  assert.equal(cancelled, true);
});

test('stalled response body is covered by transport deadline', async () => {
  let cancelled = false;
  const fetcher = async () => new Response(new ReadableStream({ cancel() { cancelled = true; } }));
  await assert.rejects(requestJson(fetcher, project, {}, new AbortController().signal, 10));
  assert.equal(cancelled, true);
});

test('parent cancellation cancels blocked reads', async () => {
  const parent = new AbortController(); const timer = deadline(parent.signal, 1000);
  const reading = readJson(new Response(new ReadableStream()), 512, timer.signal);
  parent.abort(); await assert.rejects(reading); timer.dispose();
});

test('untrusted project URL and identical client/admin credentials fail closed at startup', () => {
  for (const url of ['http://example.supabase.co', 'https://user:pass@example.supabase.co', 'https://example.supabase.co/path', 'https://example.supabase.co?token=bad']) {
    assert.throws(() => createHandler({ ...configuration, projectUrl: url }));
  }
  assert.throws(() => createHandler({ ...configuration, publishableKey: adminKey }));
});
