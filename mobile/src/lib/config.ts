import { CommunityError, type CommunityConfig, type StorageLike } from './models';

/** Only public publisher configuration belongs in the browser bundle. */
export function createConfig(config: CommunityConfig): CommunityConfig;
export function createConfig(projectUrl: string, publishableKey: string, mediaApiUrl?: string): CommunityConfig;
export function createConfig(input: string | CommunityConfig, publicKey?: string, mediaUrl?: string): CommunityConfig {
  try {
    const projectUrl = typeof input === 'string' ? input : input.projectUrl;
    const publishableKey = typeof input === 'string' ? publicKey! : input.publishableKey;
    const url = new URL(projectUrl.trim());
    const key = publishableKey.trim();
    if (url.protocol !== 'https:' || url.pathname !== '/' || url.search || url.hash || url.username || url.password
      || url.hostname.endsWith('.invalid') || !isPublicKey(key)) throw new Error();
    const home = typeof input === 'string' ? mediaUrl : input.mediaApiUrl;
    let mediaApiUrl: string | undefined;
    if (home?.trim()) {
      const media = new URL(home.trim());
      if (media.protocol !== 'https:' || media.pathname !== '/' || media.search || media.hash || media.username || media.password
        || media.hostname.endsWith('.invalid')) throw new Error();
      mediaApiUrl = media.origin;
    }
    return Object.freeze({ projectUrl: url.origin, publishableKey: key, ...(mediaApiUrl ? { mediaApiUrl } : {}),
      ...(typeof input !== 'string' && input.operationsEnabled === true ? { operationsEnabled: true } : {}) });
  } catch {
    throw new CommunityError('not_configured', 'Online Media has not been connected for this build.');
  }
}

function isPublicKey(key: string): boolean {
  if (key.length > 16384 || /\s/.test(key)) return false;
  if (/^sb_publishable_[A-Za-z0-9_-]{11,}$/.test(key)) return true;
  try {
    const pieces = key.split('.');
    if (pieces.length !== 3) return false;
    const payload = pieces[1].replace(/-/g, '+').replace(/_/g, '/');
    return JSON.parse(atob(payload.padEnd(Math.ceil(payload.length / 4) * 4, '='))).role === 'anon';
  } catch { return false; }
}

/** A tab session is the default. Durable browser storage is selected only by explicit opt-in. */
export function browserSessionStorage(rememberDevice = false): StorageLike {
  const memory = new Map<string, string>();
  const knownSaved = new Set<string>();
  let memoryOnly = false;
  const browserStorage = () => rememberDevice ? globalThis.localStorage : globalThis.sessionStorage;
  return {
    getItem(key) {
      if (memoryOnly) return memory.get(key) ?? null;
      try {
        const value = browserStorage().getItem(key);
        if (value !== null) knownSaved.add(key);
        return value;
      } catch {
        if (rememberDevice) throw new CommunityError('session_storage', 'This browser could not read your saved sign-in. Turn off Remember this device and try again.');
        memoryOnly = true; return memory.get(key) ?? null;
      }
    },
    setItem(key, value) {
      // Some phone browsers allow reads while quota or privacy policies reject
      // writes. Once that happens, keep this tab's memory session authoritative.
      if (memoryOnly) { memory.set(key, value); return; }
      try { browserStorage().setItem(key, value); knownSaved.add(key); }
      catch {
        if (rememberDevice) throw new CommunityError('session_storage', 'This browser could not remember your sign-in. Turn off Remember this device and try again.');
        memoryOnly = true; memory.set(key, value);
      }
    },
    removeItem(key) {
      memory.delete(key);
      try { browserStorage().removeItem(key); knownSaved.delete(key); }
      catch {
        if (rememberDevice || !memoryOnly || knownSaved.has(key))
          throw new CommunityError('session_storage', 'This browser could not remove your saved sign-in. Please try signing out again.');
      }
    },
  };
}
