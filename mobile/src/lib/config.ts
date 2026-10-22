import { CommunityError, type CommunityConfig, type StorageLike } from './models';

/** Only public publisher configuration belongs in the browser bundle. */
export function createConfig(config: CommunityConfig): CommunityConfig;
export function createConfig(projectUrl: string, publishableKey: string): CommunityConfig;
export function createConfig(input: string | CommunityConfig, publicKey?: string): CommunityConfig {
  try {
    const projectUrl = typeof input === 'string' ? input : input.projectUrl;
    const publishableKey = typeof input === 'string' ? publicKey! : input.publishableKey;
    const url = new URL(projectUrl.trim());
    const key = publishableKey.trim();
    if (url.protocol !== 'https:' || url.pathname !== '/' || url.search || url.hash || url.username || url.password
      || url.hostname.endsWith('.invalid') || !isPublicKey(key)) throw new Error();
    return Object.freeze({ projectUrl: url.origin, publishableKey: key });
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
  let memoryOnly = false;
  return {
    getItem(key) {
      try { return (rememberDevice ? globalThis.localStorage : globalThis.sessionStorage).getItem(key); }
      catch { memoryOnly = true; return memory.get(key) ?? null; }
    },
    setItem(key, value) {
      try { (rememberDevice ? globalThis.localStorage : globalThis.sessionStorage).setItem(key, value); memoryOnly = false; }
      catch {
        if (rememberDevice) throw new CommunityError('session_storage', 'This browser could not remember your sign-in. Turn off Remember this device and try again.');
        memoryOnly = true; memory.set(key, value);
      }
    },
    removeItem(key) {
      memory.delete(key);
      try { (rememberDevice ? globalThis.localStorage : globalThis.sessionStorage).removeItem(key); }
      catch {
        if (!memoryOnly) throw new CommunityError('session_storage', 'This browser could not remove your saved sign-in. Please try signing out again.');
      }
    },
  };
}
