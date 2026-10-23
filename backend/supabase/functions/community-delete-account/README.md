# Self-service account deletion

This Edge Function implements the desktop/mobile contract:

```http
POST /functions/v1/community-delete-account
Authorization: Bearer <current user JWT>
apikey: <project publishable key>
Content-Type: application/json

{"confirmation":"exact_current_username"}
```

The confirmation is `DELETE` only when the account has no username. Successful
completion returns exactly `{"deleted":true}`. `KO004` means physical cleanup is
still pending; the app retains its session and can retry. No caller-supplied user
ID, object path, home-server URL, or administrative key is accepted.

## Prerequisites and deployment

Deploy only after the account-operations migration has installed these reviewed
RPCs and their restricted grants:

1. Caller JWT: `community_prepare_account_delete(p_confirmation text)` validates
   the live Auth session and confirmation, freezes new account writes, marks all
   owned clips deleting and returns at most five owned clip records.
2. Caller JWT: `community_finish_delete(p_clip_id uuid)` releases a legacy clip
   reservation only after its Storage files have been removed.
3. Service role only: `community_account_delete_ready(p_owner_id uuid)` requires
   durable deletion intent, no clip reservations and no owned Storage objects.

Keep the platform JWT check enabled (the default). If the project uses a checked-in
`supabase/config.toml`, merge this setting without replacing its other entries:

```toml
[functions.community-delete-account]
verify_jwt = true
```

The handler also calls Auth's live `getUser` endpoint. It does not trust a locally
decoded JWT, `getSession`, user metadata, or a target ID from the request body.

Supabase supplies `SUPABASE_URL`, `SUPABASE_ANON_KEY` and
`SUPABASE_SERVICE_ROLE_KEY` inside Edge Functions. An optional
`KLIPP_PUBLISHABLE_KEY` overrides the built-in anonymous API key for ordinary
caller requests. Do not copy the service-role credential to the PC/mobile apps,
home media server, command lines, repository, screenshots, or logs.

No npm/URL dependencies are imported, so there is no remotely resolved dependency
or floating version. `index.ts` is the deployment entry point; `handler.ts` and
`http.ts` must be included alongside it. Use the installed Supabase CLI's help to
discover deployment flags, or deploy these three source files through the
project's authenticated function-deployment interface. Do not disable JWT checks
to work around a client authentication error.

## Cleanup and security properties

- All clip records are validated before the first physical deletion: exact owner,
  UUID, one of the two supported providers, generated video/poster paths, no
  duplicate IDs and a maximum of five records.
- Home cleanup calls only the fixed `https://media.shlipp.com/v1/clips/<uuid>`
  endpoint with the caller JWT. Redirects are errors. It never forwards the
  administrative credential to the home server and never sends an upload request.
- Legacy files are removed through the supported Storage API with caller
  authorization, then SQL independently checks physical absence. There are no
  direct writes to Storage-managed SQL tables.
- Only after all physical cleanup does Edge use its service role to check final
  readiness and call Auth's hard-delete endpoint. Auth removal cascades sessions,
  profiles, comments and other application rows defined with deletion cascades.
  Protected SQL operations also check live Auth/session records because already
  issued JWTs cannot be retroactively made cryptographically invalid.
- There is no global sign-out before deletion. A transient cleanup or Auth error
  must leave the durable intent retryable rather than removing the only usable
  session halfway through cleanup. Concurrent successful hard deletion returning
  Auth 404 is accepted only after the same caller has passed readiness checks.
- The whole request has an 18-second budget (existing clients time out at 20
  seconds), individual upstream calls eight seconds, request JSON 512 bytes and
  upstream JSON 64 KiB. Deadlines also cover stalled bodies. A later retry safely
  continues if some files were already removed.
- Browser CORS is restricted to the published Klipp origin. Other origins,
  including `null`, are rejected. Native desktop calls have no Origin. Only POST
  is enabled; this does not weaken PC-only upload restrictions.
- Responses contain only stable safe codes and no private account fields,
  upstream error details, credentials, signed playback URLs or file paths. No
  application credential/request logging is added.

Owned Storage objects without matching clip reservations deliberately prevent final
account deletion. An operator must review those historical orphaned objects; this
function never performs an unbounded owner-prefix sweep. Retained encrypted
backups can contain earlier account data until their documented retention expires;
a disaster recovery procedure must reconcile deletion intent before republishing.

## Verification

These checks use mocked fetch and synthetic fixtures only, with no live accounts,
remote deletions, deployment, credentials or network access:

```powershell
node --test backend/supabase/functions/community-delete-account/handler.test.mjs
node mobile/node_modules/typescript/lib/tsc.js --noEmit --strict --target ES2022 --module esnext --moduleResolution bundler --allowImportingTsExtensions --lib ES2022,DOM backend/supabase/functions/community-delete-account/http.ts backend/supabase/functions/community-delete-account/handler.ts backend/supabase/functions/community-delete-account/index.ts
```

The suite covers ownership/path substitution, fixed origins, credential separation,
legacy and home cleanup, idempotency, failed acknowledgements, partial failures,
rate limits, CORS, body limits, cancellation and stalled-body timeouts. Before
enabling the UI, deployment verification must additionally exercise the Edge
runtime using a deliberately disposable test account with no real user content.
Never use an existing user's account for a deletion test.

## Reviewed official documentation (23 October 2026)

- [Supabase changelog](https://supabase.com/changelog): reviewed relevant Auth,
  Storage and Edge breaking changes. The announced CBC cipher retirement does
  not require a downgrade or TLS bypass for the modern Edge runtime.
- [Edge authorization headers](https://supabase.com/docs/guides/functions/auth-headers):
  platform `verify_jwt` and explicit caller authorization.
- [Auth deleteUser](https://supabase.com/docs/reference/javascript/auth-admin-deleteuser):
  server-only administrative deletion and explicit hard-delete behavior.
- [User management](https://supabase.com/docs/guides/auth/managing-user-data):
  remove owned Storage objects first; already issued JWTs need live-session
  authorization checks for sensitive operations.
- [Storage remove](https://supabase.com/docs/reference/javascript/storage-from-remove):
  supported physical file removal.
- [Edge runtime limits](https://supabase.com/docs/guides/functions/limits): Free
  wall-clock limit 150 seconds, memory 256 MB and CPU two seconds per request.
- [Edge pricing](https://supabase.com/docs/guides/functions/pricing): Free includes
  500,000 monthly invocations; this adds no paid service. Account-deletion traffic
  still counts toward the project's existing free quota.
