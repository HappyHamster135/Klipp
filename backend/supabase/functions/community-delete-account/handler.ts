import { deadline, httpsOrigin, object, readJson, requestJson, SafeFailure, uuid } from "./http.ts";
import type { Fetcher } from "./http.ts";

const BROWSER_ORIGIN = "https://happyhamster135.github.io";
const HOME_ORIGIN = "https://media.shlipp.com";
const MAX_CLIPS = 5;

export type Configuration = { projectUrl: string; publishableKey: string; serviceRoleKey: string };
type Clip = { id: string; provider: "home" | "supabase"; video: string; poster: string };

function failure(status: number, code: string): never { throw new SafeFailure(status, code); }
function tokenOf(request: Request): string {
  const header = request.headers.get("authorization") ?? "";
  if (header.length > 8192 || !/^Bearer [A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(header)) {
    return failure(401, "session_expired");
  }
  return header.slice(7);
}

function clipsOf(value: unknown, owner: string): Clip[] {
  const row = object(value);
  if (row.pending !== true || row.owner_id !== owner || !Array.isArray(row.clips) || row.clips.length > MAX_CLIPS) {
    return failure(503, "unavailable");
  }
  const seen = new Set<string>();
  return row.clips.map((value) => {
    const clip = object(value);
    const id = uuid(clip.id);
    const video = `${owner}/${id}/video.mp4`;
    const poster = `${owner}/${id}/poster.jpg`;
    if (seen.has(id) || clip.owner_id !== owner || !["home", "supabase"].includes(String(clip.storage_provider)) ||
      clip.video_path !== video || clip.poster_path !== poster) return failure(503, "unavailable");
    seen.add(id);
    return { id, provider: clip.storage_provider as "home" | "supabase", video, poster };
  });
}

function upstreamError(status: number, body: unknown): never {
  const row = body !== null && typeof body === "object" && !Array.isArray(body) ? body as Record<string, unknown> : {};
  // Only stable allowlisted codes escape. No upstream details, user records,
  // object paths, URLs, tokens, database hints, or exception messages are exposed.
  if (row.code === "KO002") return failure(400, "KO002");
  if (row.code === "KO003" || status === 429) return failure(429, "KO003");
  if (row.code === "KO004") return failure(409, "KO004");
  if (status === 401 || row.code === "KM001") return failure(401, "session_expired");
  if (status === 403) return failure(403, "access_denied");
  return failure(503, "unavailable");
}

function response(status: number, value: object, origin: string | null): Response {
  const headers: Record<string, string> = {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store, private",
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "no-referrer",
    "Vary": "Origin",
  };
  if (origin === BROWSER_ORIGIN) headers["Access-Control-Allow-Origin"] = origin;
  if (status === 429) headers["Retry-After"] = "60";
  return new Response(JSON.stringify(value), { status, headers });
}

export function createHandler(configuration: Configuration, fetcher: Fetcher = fetch): (request: Request) => Promise<Response> {
  const project = httpsOrigin(configuration.projectUrl);
  if (!configuration.publishableKey || !configuration.serviceRoleKey || configuration.publishableKey === configuration.serviceRoleKey ||
    /[\r\n]/.test(configuration.publishableKey + configuration.serviceRoleKey)) failure(503, "unavailable");
  const userHeaders = (token: string) => ({
    apikey: configuration.publishableKey, Authorization: `Bearer ${token}`,
    "Content-Type": "application/json", Accept: "application/json",
  });
  const adminHeaders = {
    apikey: configuration.serviceRoleKey, Authorization: `Bearer ${configuration.serviceRoleKey}`,
    "Content-Type": "application/json", Accept: "application/json",
  };

  return async (request) => {
    const origin = request.headers.get("origin");
    if (origin !== null && origin !== BROWSER_ORIGIN) return response(403, { code: "access_denied" }, null);
    if (request.method === "OPTIONS") {
      if (origin !== BROWSER_ORIGIN || request.headers.get("access-control-request-method") !== "POST") {
        return response(403, { code: "access_denied" }, origin);
      }
      const requested = (request.headers.get("access-control-request-headers") ?? "").split(",").map((header) => header.trim().toLowerCase()).filter(Boolean);
      const allowed = ["authorization", "apikey", "content-type", "x-client-info"];
      if (requested.some((header) => !allowed.includes(header))) return response(403, { code: "access_denied" }, origin);
      const result = response(200, { ok: true }, origin);
      result.headers.set("Access-Control-Allow-Methods", "POST, OPTIONS");
      result.headers.set("Access-Control-Allow-Headers", allowed.join(", "));
      result.headers.set("Access-Control-Max-Age", "300");
      return result;
    }
    if (request.method !== "POST") {
      const result = response(405, { code: "method_not_allowed" }, origin);
      result.headers.set("Allow", "POST, OPTIONS");
      return result;
    }
    const budget = deadline(request.signal, 18000);
    let cleanupStarted = false;
    try {
      const token = tokenOf(request);
      if (!/^application\/json(?:\s*;|$)/i.test(request.headers.get("content-type") ?? "")) failure(400, "KO002");
      const bodyDeadline = deadline(budget.signal, 3000);
      let body: Record<string, unknown>;
      try { body = object(await readJson(new Response(request.body, { headers: request.headers }), 512, bodyDeadline.signal)); }
      catch { return response(400, { code: "KO002" }, origin); }
      finally { bodyDeadline.dispose(); }
      if (Object.keys(body).length !== 1 || typeof body.confirmation !== "string" ||
        !/^(?:[a-z][a-z0-9_]{2,23}|DELETE)$/.test(body.confirmation)) failure(400, "KO002");

      const userResult = await requestJson(fetcher, `${project}/auth/v1/user`, { method: "GET", headers: userHeaders(token) }, budget.signal);
      if (!userResult.response.ok) upstreamError(userResult.response.status, userResult.body);
      const user = object(userResult.body);
      const owner = uuid(user.id);
      if (user.is_anonymous === true || typeof user.email_confirmed_at !== "string" || !user.email_confirmed_at) failure(403, "access_denied");

      const rpc = async (name: string, args: object, privileged = false): Promise<unknown> => {
        const result = await requestJson(fetcher, `${project}/rest/v1/rpc/${name}`, {
          method: "POST", headers: privileged ? adminHeaders : userHeaders(token), body: JSON.stringify(args),
        }, budget.signal);
        if (!result.response.ok) upstreamError(result.response.status, result.body);
        return result.body;
      };

      // A lost response may still have committed the deletion intent. Retrying
      // must remain safe and must never report success before cleanup finishes.
      cleanupStarted = true;
      const clips = clipsOf(await rpc("community_prepare_account_delete", { p_confirmation: body.confirmation }), owner);
      for (const clip of clips.filter((clip) => clip.provider === "home")) {
        const result = await requestJson(fetcher, `${HOME_ORIGIN}/v1/clips/${clip.id}`, {
          method: "DELETE", headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
        }, budget.signal);
        if (!result.response.ok) failure(409, "KO004");
        const deleted = object(result.body);
        if (deleted.id !== clip.id || deleted.deleted !== true) failure(409, "KO004");
      }
      const legacy = clips.filter((clip) => clip.provider === "supabase");
      if (legacy.length > 0) {
        for (const poster of [false, true]) {
          const bucket = poster ? "community-posters" : "community-videos";
          const result = await requestJson(fetcher, `${project}/storage/v1/object/${bucket}`, {
            method: "DELETE", headers: userHeaders(token),
            body: JSON.stringify({ prefixes: legacy.map((clip) => poster ? clip.poster : clip.video) }),
          }, budget.signal);
          if (!result.response.ok || !Array.isArray(result.body)) failure(409, "KO004");
        }
        // SQL checks Storage absence before releasing any clip reservation.
        for (const clip of legacy) {
          const deleted = object(await rpc("community_finish_delete", { p_clip_id: clip.id }));
          if (deleted.id !== clip.id || deleted.deleted !== true) failure(409, "KO004");
        }
      }

      // This is the first privileged request. SQL requires durable user intent,
      // zero remaining clips and zero owned Storage objects before approving it.
      const ready = object(await rpc("community_account_delete_ready", { p_owner_id: owner }, true));
      if (ready.ready !== true || ready.owner_id !== owner) failure(409, "KO004");
      const deletion = await requestJson(fetcher, `${project}/auth/v1/admin/users/${owner}`, {
        method: "DELETE", headers: adminHeaders, body: JSON.stringify({ should_soft_delete: false }),
      }, budget.signal);
      if (!deletion.response.ok && deletion.response.status !== 404) failure(409, "KO004");
      // Hard deletion cascades Auth sessions and all application account rows.
      // No sign-out beforehand: a transient failure must remain retryable.
      return response(200, { deleted: true }, origin);
    } catch (error) {
      let safe = error instanceof SafeFailure ? error : new SafeFailure(503, "unavailable");
      if (cleanupStarted && safe.code === "unavailable") safe = new SafeFailure(409, "KO004");
      return response(safe.status, { code: safe.code }, origin);
    } finally { budget.dispose(); }
  };
}
