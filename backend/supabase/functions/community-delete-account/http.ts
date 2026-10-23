// All transport stays inside the reviewed project and fixed media origin. There
// are no SDK dependencies, redirect following, raw errors, or credential logs.
export type Fetcher = (input: string | URL | Request, init?: RequestInit) => Promise<Response>;

export class SafeFailure extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code: string) {
    super(code);
    this.status = status;
    this.code = code;
  }
}

export function object(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new SafeFailure(503, "unavailable");
  }
  return value as Record<string, unknown>;
}

export function uuid(value: unknown): string {
  if (typeof value !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value)) {
    throw new SafeFailure(503, "unavailable");
  }
  return value;
}

export function httpsOrigin(value: string): string {
  const parsed = new URL(value);
  if (parsed.protocol !== "https:" || parsed.username || parsed.password ||
    parsed.pathname !== "/" || parsed.search || parsed.hash || parsed.port) {
    throw new SafeFailure(503, "unavailable");
  }
  return parsed.origin;
}

// Bounds bytes received even when Content-Length is absent or dishonest. The
// caller's deadline also covers stalled response bodies, not just their headers.
export async function readJson(response: Response, maximum: number, signal: AbortSignal): Promise<unknown> {
  const advertised = response.headers.get("content-length");
  if (advertised !== null && (!/^\d+$/.test(advertised) || Number(advertised) > maximum)) {
    await response.body?.cancel();
    throw new SafeFailure(503, "unavailable");
  }
  const reader = response.body?.getReader();
  if (!reader) throw new SafeFailure(503, "unavailable");
  const cancel = () => { void reader.cancel().catch(() => undefined); };
  signal.addEventListener("abort", cancel, { once: true });
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      if (signal.aborted) throw new SafeFailure(503, "unavailable");
      const part = await reader.read();
      if (part.done) break;
      length += part.value.length;
      if (length > maximum) throw new SafeFailure(503, "unavailable");
      chunks.push(part.value);
    }
    if (signal.aborted) throw new SafeFailure(503, "unavailable");
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    try { return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)); }
    catch { throw new SafeFailure(503, "unavailable"); }
  } finally {
    signal.removeEventListener("abort", cancel);
    try { await reader.cancel(); } catch { /* Nothing sensitive is logged. */ }
    reader.releaseLock();
  }
}

export function deadline(parent: AbortSignal, milliseconds: number): { signal: AbortSignal; dispose(): void } {
  const controller = new AbortController();
  const abort = () => controller.abort();
  if (parent.aborted) controller.abort();
  else parent.addEventListener("abort", abort, { once: true });
  const timer = setTimeout(abort, milliseconds);
  return {
    signal: controller.signal,
    dispose() { clearTimeout(timer); parent.removeEventListener("abort", abort); },
  };
}

export async function requestJson(
  fetcher: Fetcher, url: string, init: RequestInit, parent: AbortSignal,
  milliseconds = 8000,
): Promise<{ response: Response; body: unknown }> {
  const timeout = deadline(parent, milliseconds);
  try {
    const response = await fetcher(url, { ...init, redirect: "error", cache: "no-store", signal: timeout.signal });
    // Also reject unexpected redirects from injected/nonconforming transports.
    if (response.redirected || (response.status >= 300 && response.status < 400)) {
      await response.body?.cancel();
      throw new SafeFailure(503, "unavailable");
    }
    if (response.status === 204) return { response, body: null };
    const body = await readJson(response, 65536, timeout.signal);
    return { response, body };
  } catch (error) {
    if (error instanceof SafeFailure) throw error;
    throw new SafeFailure(503, "unavailable");
  } finally { timeout.dispose(); }
}
