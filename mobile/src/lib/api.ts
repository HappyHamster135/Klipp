import { createClient, type SupabaseClient } from '@supabase/supabase-js';
import { browserSessionStorage, createConfig } from './config';
import {
  cancelled, CommunityError, mediaLimits, unavailable, type CommunityBlockedUser, type CommunityClip,
  type CommunityComment, type CommunityConfig, type CommunityCursor, type CommunityPage, type CommunityProfile,
  type CommunityUpload, type CommunityUploadProgress, type CommunityUser, type StorageLike,
} from './models';

type Context = { generation: number; userId: string | null; signal: AbortSignal; wroteSession?: boolean; dispose(): void };
type UploadTransport = (url: string, headers: Record<string, string>, blob: Blob, signal: AbortSignal,
  progress: (bytes: number) => void) => Promise<Response>;
export interface CommunityApiOptions {
  rememberDevice?: boolean;
  storage?: StorageLike;
  fetch?: typeof fetch;
  uploadTransport?: UploadTransport;
}

/** The SDK owns Auth protocols; all public state excludes sessions, tokens and signed URLs. */
export class CommunityApi {
  private readonly config: CommunityConfig;
  private readonly client: SupabaseClient;
  private readonly storage: StorageLike;
  private readonly storageKey: string;
  private readonly requestFetch: typeof fetch;
  private readonly uploadTransport: UploadTransport;
  private listeners = new Set<() => void>();
  private generation = 0;
  private epoch = new AbortController();
  private authQueue: Promise<unknown> = Promise.resolve();
  private authContext: Context | null = null;
  private accessToken: string | null = null;
  private currentUser: CommunityUser | null = null;
  private currentProfile: CommunityProfile | null = null;
  private disposed = false;
  private signOutPending = false;
  private unsubscribe: () => void;

  constructor(config: CommunityConfig, options: CommunityApiOptions = {}) {
    this.config = createConfig(config.projectUrl, config.publishableKey);
    this.storage = options.storage ?? browserSessionStorage(options.rememberDevice ?? false);
    this.storageKey = `klipp-community-${new URL(this.config.projectUrl).host}-auth`;
    this.requestFetch = options.fetch ?? globalThis.fetch.bind(globalThis);
    this.uploadTransport = options.uploadTransport ?? ((...args) => browserUpload(this.requestFetch, ...args));
    const guardedStorage: StorageLike = {
      getItem: key => this.storage.getItem(key),
      setItem: async (key, value) => {
        const context = this.authContext;
        if (this.disposed || (context ? context.generation !== this.generation
          || context.signal.aborted : !this.currentUser)) throw cancelled();
        if (key === this.storageKey && context) context.wroteSession = true;
        await this.storage.setItem(key, value);
        if (!this.disposed && context && context.generation !== this.generation) {
          // An asynchronous storage adapter must not resurrect a session after explicit sign-out.
          if (await this.storage.getItem(key) === value) await this.storage.removeItem(key);
          throw cancelled();
        }
      },
      removeItem: key => this.storage.removeItem(key),
    };
    this.client = createClient(this.config.projectUrl, this.config.publishableKey, {
      auth: {
        storage: guardedStorage, storageKey: this.storageKey, persistSession: true,
        autoRefreshToken: false, detectSessionInUrl: false,
      },
      global: { fetch: (input, init) => this.sdkFetch(input, init) },
    });
    const { data } = this.client.auth.onAuthStateChange((event, session) => {
      // Never await another SDK method from its Auth callback: it runs under the SDK's lock.
      if (event === 'SIGNED_OUT' && this.currentUser) this.fence();
      else if (session && this.currentUser && session.user.id !== this.currentUser.id) {
        this.fence();
        queueMicrotask(() => { void this.restoreSession().catch(() => undefined); });
      } else if (event === 'TOKEN_REFRESHED' && session && this.currentUser?.id === session.user.id)
        this.accessToken = session.access_token;
    });
    this.unsubscribe = () => data.subscription.unsubscribe();
  }

  get user(): CommunityUser | null { return this.currentUser; }
  get profile(): CommunityProfile | null { return this.currentProfile; }
  subscribe(listener: () => void): () => void { this.listeners.add(listener); return () => this.listeners.delete(listener); }

  async restoreSession(signal?: AbortSignal): Promise<CommunityUser | null> {
    return this.authOperation(async context => {
      const { data, error } = await this.client.auth.getSession();
      if (error) throw safeError(error);
      if (!data.session) { this.assert(context); this.currentUser = null; this.currentProfile = null; this.emit(); return null; }
      const verified = await this.client.auth.getUser();
      this.assert(context);
      if (verified.error || !verified.data.user) {
        if (verified.error && !isExpired(verified.error)) throw safeError(verified.error);
        await this.clearStoredSession();
        this.currentUser = null; this.currentProfile = null; this.emit(); return null;
      }
      const user = parseUser(verified.data.user);
      if (user.id !== data.session.user.id) throw unavailable();
      this.currentUser = user; this.accessToken = data.session.access_token; this.currentProfile = null; this.emit();
      return user;
    }, signal);
  }

  async sendEmailCode(email: string, createAccount: boolean, signal?: AbortSignal): Promise<void> {
    email = emailAddress(email);
    await this.authOperation(async context => {
      const { error } = await this.client.auth.signInWithOtp({ email, options: { shouldCreateUser: createAccount } });
      this.assert(context);
      if (error) throw safeError(error);
    }, signal);
  }

  async verifyEmailCode(email: string, code: string, signal?: AbortSignal): Promise<CommunityUser> {
    email = emailAddress(email); code = code.trim();
    if (!/^\d{6,10}$/.test(code)) throw new CommunityError('invalid_code', 'Enter the code from your email.');
    return this.authOperation(async context => {
      try {
        const { data, error } = await this.client.auth.verifyOtp({ email, token: code, type: 'email' });
        this.assert(context);
        if (error) throw safeError(error, 'verify');
        if (!data.session || !data.user) throw unavailable();
        const verified = await this.client.auth.getUser();
        this.assert(context);
        if (verified.error || !verified.data.user) throw safeError(verified.error);
        const user = parseUser(verified.data.user);
        if (user.id !== data.user.id) throw unavailable();
        this.currentUser = user; this.currentProfile = null; this.accessToken = data.session.access_token; this.emit();
        return user;
      } catch (error) {
        if (context.wroteSession && !this.disposed && context.generation === this.generation) {
          await this.clearStoredSession();
          this.currentUser = null; this.currentProfile = null; this.accessToken = null; this.emit();
        }
        throw error;
      }
    }, signal);
  }

  async signOut(signal?: AbortSignal): Promise<void> {
    const token = this.accessToken;
    this.fence();
    const clearing = this.clearStoredSession();
    const local = this.enqueue(async () => { await clearing; await this.client.auth.signOut({ scope: 'local' }); });
    // Local identity and saved state are cleared before remote revocation is attempted.
    await clearing;
    if (token && !signal?.aborted) {
      const revocation = linkedSignal(signal ? [signal] : [], 4000);
      try {
        await this.requestFetch(`${this.config.projectUrl}/auth/v1/logout?scope=local`, {
          method: 'POST', headers: this.headers(token), signal: revocation.signal, redirect: 'error', cache: 'no-store',
        });
      } catch { /* Offline sign-out still removes this device's credentials. */ }
      finally { revocation.dispose(); }
    }
    await local;
  }

  getProfile(signal?: AbortSignal): Promise<CommunityProfile> {
    return this.identity(async context => {
      const rows = await this.json(`rest/v1/profiles?select=id,username,display_name,bio&id=eq.${context.userId}&limit=1`, 'GET', undefined, context);
      const list = array(rows, 1);
      if (list.length !== 1) throw new CommunityError('profile_unavailable', 'Your profile is not ready yet. Please refresh.');
      const profile = parseProfile(list[0], context.userId!);
      this.assert(context); this.currentProfile = profile; this.emit(); return profile;
    }, signal);
  }

  saveProfile(profile: Pick<CommunityProfile, 'username' | 'displayName' | 'bio'>, signal?: AbortSignal): Promise<CommunityProfile> {
    const username = profile.username?.trim().toLowerCase() ?? '';
    if (!/^[a-z][a-z0-9_]{2,23}$/.test(username)) throw new CommunityError('invalid_username', 'Use 3–24 letters, numbers or underscores, starting with a letter.');
    const displayName = validateText(profile.displayName, 1, 40, 'invalid_display_name', 'Enter a display name of 1–40 characters.');
    const bio = validateText(profile.bio, 0, 160, 'invalid_bio', 'Keep your bio to 160 characters.', true);
    return this.identity(async context => {
      const rows = array(await this.json(`rest/v1/profiles?select=id,username,display_name,bio&id=eq.${context.userId}`,
        'PATCH', { username, display_name: displayName, bio }, context, { Prefer: 'return=representation' }), 1);
      if (rows.length !== 1) throw unavailable();
      const saved = parseProfile(rows[0], context.userId!);
      this.assert(context); this.currentProfile = saved; this.emit(); return saved;
    }, signal);
  }

  getFeed(cursor: CommunityCursor | null = null, mine = false, signal?: AbortSignal): Promise<CommunityPage<CommunityClip>> {
    validateCursor(cursor);
    return this.identity(async context => {
      const rows = array(await this.rpc('community_feed', { p_limit: 13, p_before: cursor?.createdAt ?? null,
        p_before_id: cursor?.id ?? null, p_mine: mine }, context), 13).map(row => parseClip(row, context.userId!));
      if (rows.some(row => mine ? row.ownerId !== context.userId : row.status !== 'published')) throw unavailable();
      return page(rows, mediaLimits.feedPageSize, cursor);
    }, signal);
  }

  getClip(id: string, signal?: AbortSignal): Promise<CommunityClip> {
    id = uuid(id);
    return this.identity(context => this.getClipCore(id, context), signal);
  }
  getComments(id: string, cursor: CommunityCursor | null = null, signal?: AbortSignal): Promise<CommunityPage<CommunityComment>> {
    id = uuid(id); validateCursor(cursor);
    return this.identity(async context => {
      const rows = array(await this.rpc('community_comments', { p_clip_id: id, p_limit: 31,
        p_before: cursor?.createdAt ?? null, p_before_id: cursor?.id ?? null }, context), 31).map(parseComment);
      if (rows.some(row => row.clipId !== id)) throw unavailable();
      return page(rows, mediaLimits.commentsPageSize, cursor);
    }, signal);
  }
  setLiked(id: string, liked: boolean, signal?: AbortSignal): Promise<CommunityClip> {
    id = uuid(id);
    return this.identity(async context => {
      const clip = parseClip(await this.rpc('community_set_like', { p_clip_id: id, p_liked: liked }, context), context.userId!);
      if (clip.id !== id || clip.likedByMe !== liked || clip.status !== 'published') throw unavailable();
      return clip;
    }, signal);
  }
  addComment(id: string, body: string, signal?: AbortSignal): Promise<CommunityComment> {
    id = uuid(id); body = validateText(body, 1, 500, 'invalid_comment', 'Write a comment of 1–500 characters.', true);
    return this.identity(async context => {
      const comment = parseComment(await this.rpc('community_add_comment', { p_clip_id: id, p_body: body }, context));
      if (comment.clipId !== id || comment.ownerId !== context.userId) throw unavailable();
      return comment;
    }, signal);
  }
  deleteComment(id: string, signal?: AbortSignal): Promise<void> {
    id = uuid(id);
    return this.identity(async context => { requireTrue(await this.rpc('community_delete_comment', { p_comment_id: id }, context), 'deleted'); }, signal);
  }
  reportClip(id: string, reason: string, signal?: AbortSignal): Promise<void> {
    id = uuid(id); reason = validateText(reason, 1, 500, 'invalid_report', 'Describe the issue in 500 characters or fewer.', true);
    return this.identity(async context => { requireTrue(await this.rpc('community_report_clip', { p_clip_id: id, p_reason: reason }, context), 'reported'); }, signal);
  }
  getBlockedUsers(signal?: AbortSignal): Promise<CommunityBlockedUser[]> {
    return this.identity(async context => {
      const users = array(await this.rpc('community_blocked_members', {}, context), 100).map(value => {
        const row = object(value);
        return { userId: uuid(row.user_id), username: row.username === null ? '' : text(row.username, 24, true), displayName: text(row.display_name, 40) };
      });
      if (users.some(user => user.userId === context.userId) || new Set(users.map(user => user.userId)).size !== users.length) throw unavailable();
      return users;
    }, signal);
  }
  setBlocked(userId: string, blocked: boolean, signal?: AbortSignal): Promise<void> {
    userId = uuid(userId);
    return this.identity(async context => {
      if (userId === context.userId) throw new CommunityError('invalid_user', 'You cannot block yourself.');
      const row = object(await this.rpc('community_block_user', { p_user_id: userId, p_blocked: blocked }, context));
      if (row.blocked !== blocked) throw unavailable();
    }, signal);
  }
  blockUser(userId: string, signal?: AbortSignal): Promise<void> { return this.setBlocked(userId, true, signal); }
  deleteClip(id: string, signal?: AbortSignal): Promise<void> {
    id = uuid(id); return this.identity(context => this.deleteCore(id, context), signal);
  }
  getPlaybackUrl(id: string, signal?: AbortSignal): Promise<string> { return this.assetUrl(id, false, signal); }
  getPosterUrl(id: string, signal?: AbortSignal): Promise<string> { return this.assetUrl(id, true, signal); }

  uploadClip(upload: CommunityUpload, progress?: (value: CommunityUploadProgress) => void, signal?: AbortSignal): Promise<CommunityClip> {
    const title = validateText(upload.title, 1, 120, 'invalid_title', 'Enter a title of 1–120 characters.');
    const description = validateText(upload.description, 0, 500, 'invalid_description', 'Keep the description to 500 characters.', true);
    if (!(upload.video instanceof Blob) || !(upload.poster instanceof Blob) || upload.video.size < 1 || upload.video.size > mediaLimits.videoBytes
      || upload.poster.size < 1 || upload.poster.size > mediaLimits.posterBytes || upload.video.type !== 'video/mp4' || upload.poster.type !== 'image/jpeg')
      throw new CommunityError('invalid_file', 'Choose an MP4 up to 16 MiB and a JPEG thumbnail up to 256 KiB.');
    if (!Number.isFinite(upload.durationSeconds) || upload.durationSeconds <= 0 || upload.durationSeconds > 60)
      throw new CommunityError('invalid_duration', 'Choose a clip up to 60 seconds long.');
    return this.identity(async context => {
      const deadline = linkedSignal([context.signal], 180000);
      const uploading = { ...context, signal: deadline.signal };
      let reservation: string | null = null;
      const total = upload.video.size + upload.poster.size;
      const report = (bytes: number, status: string) => { try { progress?.({ completedBytes: bytes, totalBytes: total, status }); } catch { /* Presentation cannot strand a reservation. */ } };
      try {
        report(0, 'Preparing upload');
        await this.token(uploading, 240);
        const pending = parseClip(await this.rpc('community_begin_upload', { p_title: title, p_description: description,
          p_duration_seconds: upload.durationSeconds, p_byte_size: upload.video.size }, uploading), context.userId!);
        if (pending.ownerId !== context.userId || pending.status !== 'uploading') throw unavailable();
        reservation = pending.id;
        for (const poster of [false, true]) {
          const blob = poster ? upload.poster : upload.video;
          const bucket = poster ? 'community-posters' : 'community-videos';
          const token = await this.token(uploading);
          const response = await this.uploadTransport(`${this.config.projectUrl}/storage/v1/object/${bucket}/${assetPath(context.userId!, pending.id, poster)}`,
            { ...this.headers(token), 'Content-Type': blob.type, 'x-upsert': 'false' }, blob, uploading.signal,
            bytes => report((poster ? upload.video.size : 0) + Math.min(blob.size, bytes), poster ? 'Uploading thumbnail' : 'Uploading video'));
          this.assert(uploading);
          await responseJson(response, 128 * 1024, uploading.signal, 'storage');
        }
        report(total, 'Publishing clip');
        const clip = parseClip(await this.rpc('community_publish_clip', { p_clip_id: pending.id }, uploading), context.userId!);
        if (clip.id !== pending.id || clip.ownerId !== context.userId || clip.status !== 'published') throw unavailable();
        this.assert(uploading); report(total, 'Uploaded'); return clip;
      } catch (error) {
        if (reservation && this.isCurrent(context)) {
          const cleanup = this.context(undefined, 20000);
          try { await this.deleteCore(reservation, cleanup); } catch { /* My uploads retains the hidden reservation for retry. */ }
          finally { cleanup.dispose(); }
        }
        if (deadline.signal.aborted && !context.signal.aborted) throw new CommunityError('timeout', 'The upload took too long. Try a steadier connection.');
        throw error;
      } finally { deadline.dispose(); }
    }, signal);
  }

  dispose(): void {
    this.disposed = true; this.fence(); this.unsubscribe(); this.listeners.clear();
    void this.client.auth.dispose();
  }

  private async assetUrl(id: string, poster: boolean, signal?: AbortSignal): Promise<string> {
    id = uuid(id);
    return this.identity(async context => {
      const clip = await this.getClipCore(id, context);
      if (clip.status !== 'published') throw new CommunityError('clip_unavailable', 'This clip is not available to play.');
      const bucket = poster ? 'community-posters' : 'community-videos';
      const path = assetPath(clip.ownerId, id, poster);
      const row = object(await this.json(`storage/v1/object/sign/${bucket}/${path}`, 'POST', { expiresIn: poster ? 120 : 300 }, context));
      let signed = text(row.signedURL, 8192);
      if (signed.startsWith('/object/sign/')) signed = `/storage/v1${signed}`;
      const url = new URL(signed, `${this.config.projectUrl}/`);
      if (url.origin !== this.config.projectUrl || url.protocol !== 'https:' || url.username || url.password || url.hash
        || url.pathname !== `/storage/v1/object/sign/${bucket}/${path}` || !url.search.startsWith('?token=')
        || url.search.length < 8 || url.search.length > 4096 || /[\u0000-\u0020\u007f]/.test(signed)) throw unavailable();
      this.assert(context); return url.href;
    }, signal);
  }
  private async getClipCore(id: string, context: Context): Promise<CommunityClip> {
    const clip = parseClip(await this.rpc('community_get_clip', { p_clip_id: id }, context), context.userId!);
    if (clip.id !== id) throw unavailable(); return clip;
  }
  private async deleteCore(id: string, context: Context): Promise<void> {
    const row = object(await this.rpc('community_remove_clip', { p_clip_id: id }, context));
    if (uuid(row.id) !== id) throw unavailable();
    if (row.status === 'deleted') return;
    if (row.status !== 'deleting' || uuid(row.owner_id) !== context.userId) throw unavailable();
    validatePaths(row, context.userId!, id);
    let failure: unknown;
    for (const poster of [false, true]) {
      try { await this.json(`storage/v1/object/${poster ? 'community-posters' : 'community-videos'}`, 'DELETE',
        { prefixes: [assetPath(context.userId!, id, poster)] }, context); }
      catch (error) { if (context.signal.aborted || !this.isCurrent(context)) throw error; failure ??= error; }
    }
    if (failure) throw failure;
    requireTrue(await this.rpc('community_finish_delete', { p_clip_id: id }, context), 'deleted');
  }
  private rpc(name: string, body: object, context: Context): Promise<unknown> { return this.json(`rest/v1/rpc/${name}`, 'POST', body, context); }
  private async json(path: string, method: string, body: object | undefined, context: Context, extraHeaders: Record<string, string> = {}): Promise<unknown> {
    this.assert(context);
    const token = await this.token(context);
    const deadline = linkedSignal([context.signal], 20000);
    try {
      const response = await this.requestFetch(`${this.config.projectUrl}/${path}`, { method, headers: { ...this.headers(token), ...extraHeaders },
        body: body === undefined ? undefined : JSON.stringify(body), signal: deadline.signal, redirect: 'error', cache: 'no-store' });
      const result = await responseJson(response, 512 * 1024, deadline.signal, path);
      this.assert(context); return result;
    } catch (error) {
      if (context.signal.aborted || !this.isCurrent(context)) throw cancelled();
      if (deadline.signal.aborted) throw new CommunityError('timeout', 'Media took too long to respond. Please try again.');
      if (error instanceof CommunityError && error.code === 'session_expired') { this.fence(); await this.clearStoredSession(); }
      if (error instanceof CommunityError) throw error;
      throw new CommunityError('offline', 'Could not connect to Media. Check your connection and try again.');
    } finally { deadline.dispose(); }
  }
  private headers(token: string): Record<string, string> {
    return { apikey: this.config.publishableKey, Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', Accept: 'application/json' };
  }
  private async token(context: Context, minimumSeconds = 60): Promise<string> {
    return this.enqueue(async () => {
      this.assert(context); this.authContext = context;
      try {
        let { data, error } = await this.client.auth.getSession();
        if (error) throw safeError(error);
        if (!data.session) throw new CommunityError('session_expired', 'Please sign in again to use Media.');
        if ((data.session.expires_at ?? 0) <= Date.now() / 1000 + minimumSeconds) {
          const refreshed = await this.client.auth.refreshSession();
          data = refreshed.data; error = refreshed.error;
          if (error) throw safeError(error);
        }
        this.assert(context);
        const session = data.session;
        if (!session || session.user.id !== context.userId || !safeToken(session.access_token)) throw unavailable();
        this.accessToken = session.access_token; return session.access_token;
      } finally { this.authContext = null; }
    });
  }
  private async sdkFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
    const context = this.authContext;
    const deadline = linkedSignal([this.epoch.signal, ...(context ? [context.signal] : []), ...(init?.signal ? [init.signal] : [])], 20000);
    try {
      const requested = new URL(input instanceof Request ? input.url : String(input));
      if (requested.origin !== this.config.projectUrl || !requested.pathname.startsWith('/auth/v1/')) throw unavailable();
      const response = await this.requestFetch(input, { ...init, signal: deadline.signal, redirect: 'error', cache: 'no-store' });
      const bytes = await boundedBytes(response, 128 * 1024, deadline.signal);
      if (context) this.assert(context);
      if (deadline.signal.aborted) throw cancelled();
      return new Response(bytes.length ? bytes : null, { status: response.status, statusText: response.statusText, headers: response.headers });
    } finally { deadline.dispose(); }
  }
  private async identity<T>(operation: (context: Context) => Promise<T>, signal?: AbortSignal): Promise<T> {
    if (!this.currentUser) throw new CommunityError('signed_out', 'Sign in to use Media.');
    const context = this.context(signal);
    try { const result = await operation(context); this.assert(context); return result; }
    catch (error) {
      if (error instanceof CommunityError && error.code === 'session_expired' && this.isCurrent(context)) {
        this.fence(); await this.clearStoredSession(); throw error;
      }
      if (context.signal.aborted || !this.isCurrent(context)) throw cancelled();
      if (error instanceof CommunityError) throw error;
      throw new CommunityError('offline', 'Could not connect to Media. Check your connection and try again.');
    } finally { context.dispose(); }
  }
  private async authOperation<T>(operation: (context: Context) => Promise<T>, signal?: AbortSignal): Promise<T> {
    if (this.signOutPending) throw new CommunityError('signout_pending', 'Finish signing out before signing in again. Your saved sign-in could not be removed.');
    const context = this.context(signal);
    try {
      return await this.enqueue(async () => {
        this.assert(context); this.authContext = context;
        try { const result = await operation(context); this.assert(context); return result; }
        finally { this.authContext = null; }
      });
    } catch (error) {
      if (context.signal.aborted || context.generation !== this.generation) throw cancelled();
      if (error instanceof CommunityError) throw error;
      throw safeError(error);
    } finally { context.dispose(); }
  }
  private enqueue<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.authQueue.then(operation, operation);
    this.authQueue = result.catch(() => undefined); return result;
  }
  private context(signal?: AbortSignal, timeout?: number): Context {
    if (this.disposed) throw cancelled();
    const linked = linkedSignal([this.epoch.signal, ...(signal ? [signal] : [])], timeout);
    return { generation: this.generation, userId: this.currentUser?.id ?? null, ...linked };
  }
  private assert(context: Context): void {
    if (!this.isCurrent(context) || context.signal.aborted) throw cancelled();
  }
  private isCurrent(context: Context): boolean {
    return !this.disposed && context.generation === this.generation
      && (context.userId === null || context.userId === this.currentUser?.id);
  }
  private fence(): void {
    this.generation++; this.epoch.abort(); this.epoch = new AbortController();
    this.currentUser = null; this.currentProfile = null; this.accessToken = null; this.emit();
  }
  private async clearStoredSession(): Promise<void> {
    try {
      await this.storage.removeItem(this.storageKey);
      await this.storage.removeItem(`${this.storageKey}-code-verifier`);
      await this.storage.removeItem(`${this.storageKey}-user`);
      this.signOutPending = false;
    } catch {
      this.signOutPending = true;
      throw new CommunityError('session_storage', 'This browser could not remove your saved sign-in. Please try signing out again.');
    }
  }
  private emit(): void { for (const listener of this.listeners) { try { listener(); } catch { /* A view cannot change Auth state. */ } } }
}

function linkedSignal(signals: AbortSignal[], timeout?: number): { signal: AbortSignal; dispose(): void } {
  const controller = new AbortController();
  const abort = () => controller.abort();
  for (const signal of signals) { if (signal.aborted) abort(); else signal.addEventListener('abort', abort, { once: true }); }
  const timer = timeout ? setTimeout(abort, timeout) : undefined;
  return { signal: controller.signal, dispose() { if (timer) clearTimeout(timer); for (const signal of signals) signal.removeEventListener('abort', abort); } };
}
async function boundedBytes(response: Response, maximum: number, signal: AbortSignal): Promise<Uint8Array<ArrayBuffer>> {
  if (Number(response.headers.get('content-length') ?? 0) > maximum) throw unavailable();
  if (!response.body) return new Uint8Array();
  const reader = response.body.getReader(); const chunks: Uint8Array[] = []; let size = 0;
  try {
    while (true) {
      if (signal.aborted) throw cancelled();
      const { value, done } = await reader.read(); if (done) break;
      size += value.byteLength; if (size > maximum) throw unavailable(); chunks.push(value);
    }
  } catch (error) { await reader.cancel().catch(() => undefined); throw error; }
  finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size); let offset = 0; for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; } return bytes;
}
async function responseJson(response: Response, maximum: number, signal: AbortSignal, path: string): Promise<unknown> {
  const bytes = await boundedBytes(response, maximum, signal); let body: unknown = {};
  try { if (bytes.length) body = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); }
  catch { if (response.ok) throw unavailable(); }
  if (!response.ok) throw safeError({ ...(typeof body === 'object' && body ? body : {}), status: response.status }, path);
  return body;
}
function object(value: unknown): Record<string, unknown> { if (!value || typeof value !== 'object' || Array.isArray(value)) throw unavailable(); return value as Record<string, unknown>; }
function array(value: unknown, max: number): unknown[] { if (!Array.isArray(value) || value.length > max) throw unavailable(); return value; }
function text(value: unknown, max: number, empty = false): string {
  if (typeof value !== 'string' || (!empty && !value.length) || [...value].length > max || /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(value)) throw unavailable(); return value;
}
function uuid(value: unknown): string {
  if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value)) throw unavailable(); return value.toLowerCase();
}
function integer(value: unknown, max = Number.MAX_SAFE_INTEGER): number { if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0 || value > max) throw unavailable(); return value; }
function time(value: unknown): string { const result = text(value, 64); if (!/^\d{4}-\d\d-\d\dT/.test(result) || !Number.isFinite(Date.parse(result)) || Date.parse(result) < 0) throw unavailable(); return result; }
function validateCursor(cursor: CommunityCursor | null): void { if (cursor) { uuid(cursor.id); time(cursor.createdAt); } }
function parseUser(value: unknown): CommunityUser { const row = object(value); return { id: uuid(row.id), email: emailAddress(text(row.email, 254)) }; }
function parseProfile(value: unknown, owner: string): CommunityProfile {
  const row = object(value); const id = uuid(row.id); if (id !== owner) throw unavailable();
  return { id, username: row.username === null ? null : text(row.username, 24), displayName: text(row.display_name, 40), bio: text(row.bio, 160, true) };
}
function parseClip(value: unknown, viewer: string): CommunityClip {
  const row = object(value); const id = uuid(row.id); const ownerId = uuid(row.owner_id);
  if (row.username === null && ownerId !== viewer) throw unavailable();
  if (typeof row.duration_seconds !== 'number' || !Number.isFinite(row.duration_seconds) || row.duration_seconds <= 0 || row.duration_seconds > 60
    || typeof row.liked_by_me !== 'boolean' || !['uploading', 'published', 'deleting'].includes(String(row.status))) throw unavailable();
  const byteSize = integer(row.byte_size, mediaLimits.videoBytes); if (byteSize < 1) throw unavailable(); validatePaths(row, ownerId, id);
  return { id, ownerId, username: row.username === null ? '' : text(row.username, 24), displayName: text(row.display_name, 40),
    title: text(row.title, 120), description: text(row.description, 500, true), durationSeconds: row.duration_seconds,
    byteSize, createdAt: time(row.created_at), likeCount: integer(row.like_count), commentCount: integer(row.comment_count),
    likedByMe: row.liked_by_me, status: row.status as CommunityClip['status'] };
}
function parseComment(value: unknown): CommunityComment {
  const row = object(value); return { id: uuid(row.id), clipId: uuid(row.clip_id), ownerId: uuid(row.owner_id), username: text(row.username, 24),
    displayName: text(row.display_name, 40), body: text(row.body, 500), createdAt: time(row.created_at) };
}
function validatePaths(row: Record<string, unknown>, owner: string, id: string): void {
  if (row.video_path != null && row.video_path !== assetPath(owner, id, false) || row.poster_path != null && row.poster_path !== assetPath(owner, id, true)) throw unavailable();
}
function assetPath(owner: string, id: string, poster: boolean): string { return `${owner}/${id}/${poster ? 'poster.jpg' : 'video.mp4'}`; }
function page<T extends { id: string; createdAt: string }>(rows: T[], size: number, before: CommunityCursor | null): CommunityPage<T> {
  let previous = before;
  for (const row of rows) {
    // Compare full ISO instants including PostgreSQL microseconds (Date.parse alone loses sub-millisecond precision).
    const key = timestampKey(row.createdAt); const previousKey = previous && timestampKey(previous.createdAt);
    if (previous && (key > previousKey! || key === previousKey && row.id >= previous.id.toLowerCase())) throw unavailable();
    previous = row;
  }
  const items = rows.slice(0, size); const last = items.at(-1);
  return { items, nextCursor: rows.length > size && last ? { createdAt: last.createdAt, id: last.id } : null };
}
function timestampKey(value: string): bigint {
  const millis = Date.parse(value); const fraction = /\.(\d+)(?:Z|[+-]\d\d:\d\d)$/.exec(value)?.[1] ?? '';
  return BigInt(Math.trunc(millis)) * 1000n + BigInt((fraction.padEnd(6, '0').slice(3, 6)) || '0');
}
function requireTrue(value: unknown, field: string): void { if (object(value)[field] !== true) throw unavailable(); }
function safeToken(value: string): boolean { return value.length > 0 && value.length <= 16384 && /^[\x21-\x7e]+$/.test(value); }
function emailAddress(value: string): string {
  const email = value.trim(); if (email.length > 254 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) throw new CommunityError('invalid_email', 'Enter a valid email address.'); return email;
}
function validateText(value: string, min: number, max: number, code: string, message: string, multiline = false): string {
  const result = value.trim(); if ([...result].length < min || [...result].length > max
    || (multiline ? /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/ : /[\u0000-\u001f\u007f]/).test(result)) throw new CommunityError(code, message); return result;
}
function isExpired(value: unknown): boolean { const row = value && typeof value === 'object' ? value as Record<string, unknown> : {}; return row.status === 401 || ['bad_jwt', 'session_not_found', 'refresh_token_not_found', 'refresh_token_already_used', 'user_not_found'].includes(String(row.code)); }
function safeError(value: unknown, path = ''): CommunityError {
  if (value instanceof CommunityError) return value;
  const row = value && typeof value === 'object' ? value as Record<string, unknown> : {};
  const code = String(row.code ?? row.error_code ?? '');
  if (row.status === 429) return new CommunityError('rate_limited', 'Too many attempts. Wait a minute and try again.');
  if (isExpired(value)) return new CommunityError('session_expired', 'Please sign in again to use Media.');
  if (path === 'verify' && Number(row.status) >= 400 && Number(row.status) < 500) return new CommunityError('invalid_code', 'That code is invalid or expired. Request a new code.');
  if (code === '23505') return new CommunityError('username_taken', 'That username is already taken.');
  const messages: Record<string, [string, string]> = {
    KM001: ['verified_account_required', 'Verify your email before using Media.'], KM002: ['profile_required', 'Choose a username in your profile first.'],
    KM003: ['invalid_media', 'Check the clip details and try again.'], KM004: ['upload_limit', 'Your five upload slots are in use. Remove a clip from My uploads.'],
    KM005: ['storage_full', 'The free beta storage is full. Please try again later.'], KM006: ['upload_not_ready', 'The clip files are not ready to publish. Try uploading again.'],
    KM007: ['clip_unavailable', 'This clip or profile is no longer available.'], KM008: ['rate_limited', 'Please wait a moment before trying again.'],
    KM009: [path.endsWith('community_block_user') ? 'block_limit' : path.endsWith('community_report_clip') ? 'report_limit' : 'comment_limit', 'This beta feature has reached its limit.'],
    KM010: ['cleanup_pending', 'The clip is hidden. Retry deleting it from My uploads to finish cleanup.'], KM011: ['access_denied', 'Your account cannot change this clip or comment.'],
    signup_disabled: ['signup_disabled', 'New accounts are not available yet.'], email_provider_disabled: ['email_disabled', 'Email sign-in is not available yet.'],
  };
  if (messages[code]) return new CommunityError(...messages[code]);
  if (row.status === 403) return new CommunityError('access_denied', 'Your account cannot access this Media feature.');
  if (row.name === 'AuthRetryableFetchError' || value instanceof TypeError) return new CommunityError('offline', 'Could not connect to Media. Check your connection and try again.');
  return unavailable();
}

async function browserUpload(fetcher: typeof fetch, url: string, headers: Record<string, string>, blob: Blob,
  signal: AbortSignal, progress: (bytes: number) => void): Promise<Response> {
  if (typeof XMLHttpRequest === 'undefined') {
    const response = await fetcher(url, { method: 'POST', headers, body: blob, signal, redirect: 'error', cache: 'no-store' });
    if (response.ok) progress(blob.size); return response;
  }
  return new Promise<Response>((resolve, reject) => {
    const xhr = new XMLHttpRequest(); let settled = false;
    const finish = (operation: () => void) => { if (!settled) { settled = true; signal.removeEventListener('abort', abort); operation(); } };
    const abort = () => { finish(() => reject(cancelled())); xhr.abort(); };
    if (signal.aborted) { abort(); return; }
    xhr.open('POST', url); xhr.withCredentials = false; xhr.timeout = 180000;
    for (const [key, value] of Object.entries(headers)) xhr.setRequestHeader(key, value);
    signal.addEventListener('abort', abort, { once: true });
    xhr.upload.onprogress = event => progress(Math.min(event.loaded, blob.size));
    xhr.onprogress = event => { if (event.loaded > 128 * 1024) { finish(() => reject(unavailable())); xhr.abort(); } };
    xhr.onload = () => finish(() => {
      if (xhr.status < 100 || xhr.status > 599 || xhr.responseURL && new URL(xhr.responseURL).origin !== new URL(url).origin) { reject(unavailable()); return; }
      if (xhr.responseText.length > 128 * 1024) { reject(unavailable()); return; }
      resolve(new Response(xhr.responseText || null, { status: xhr.status, headers: { 'content-type': 'application/json' } }));
    });
    xhr.onerror = () => finish(() => reject(new CommunityError('offline', 'Could not upload the clip. Check your connection.')));
    xhr.ontimeout = () => finish(() => reject(new CommunityError('timeout', 'The upload took too long. Try a steadier connection.')));
    xhr.onabort = () => finish(() => reject(cancelled()));
    xhr.send(blob);
  });
}
