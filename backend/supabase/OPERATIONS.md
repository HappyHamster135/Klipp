# Online operations deployment

An earlier local operations placeholder was empty and was never applied.
Apply the new `20261023160537_community_operations.sql` only after review. It starts
with operations and account deletion disabled. No account UUID is embedded.

Deploy in this order:

1. Apply the reviewed operations migration. Run `tests/community_operations.sql`
   inside its rollback transaction and run the read-only deployment receipt.
2. Install the home API build that checks `community_home_asset_allowed` on every
   new asset GET/HEAD and rejects hidden playback metadata. This proof-gated RPC
   uses the existing gateway capability, never an administrative credential.
3. Deploy `functions/community-delete-account/index.ts` with its two sibling
   modules and platform JWT verification enabled. Use its mocked tests and strict
   TypeScript check first. No real account deletion is part of deployment testing.
4. Select the already verified owner account through the trusted administrator
   interface, confirm its identity, and insert that UUID into the private operator
   allowlist. Never infer privileges from email text or user metadata.
5. Only after API and Edge checks pass, enable the two private flags:

```sql
update klipp_private.community_operations_config
set operations_enabled=true,account_deletion_enabled=true where singleton;
```

`community_operations_capabilities()` returns `enabled`, `operations_enabled`
(aliases), `version:1`, `account_deletion`, and the current caller's `operator`
boolean. Clients must also retain their production-build feature gate until this
deployment is verified. Incomplete but verified permanent accounts can use Help,
Usage and self-deletion; the fallback deletion confirmation is exactly `DELETE`.

Usage reports reserved slots and known logical media bytes. `storage_limit_bytes`
is the 40-slot application budget, not physical SSD capacity. `home_bytes` uses
published media evidence and `legacy_bytes` uses Storage metadata. Partial home
uploads and unknown files are not represented as measured SSD usage. Billing,
egress, active-user and email counters remain unknown until an owner supplies a
verified observation in the private metrics table. Their documented free-plan
limits are not a claim of current measured consumption.

Operator moderation requires both the private allowlist and a live Auth session.
Hide/keep decisions and support replies are audited; the audit stores a support
action summary rather than duplicating the private reply. Audit retention is
90 days, with a bounded 5,000-entry capacity. New viewer access is revoked when a
clip is hidden. Already downloaded bytes and open streams cannot be recalled;
previously issued legacy Supabase playback links may remain valid until their
short expiry. Hidden previews are disabled rather than bypassing the viewer gate.

Account deletion uses live-session confirmation, durable pending intent, physical
cleanup through the home/Storage APIs, a service-only final readiness proof and
then Auth hard deletion. New reservations recheck pending intent after acquiring
the same advisory lock as confirmation/readiness, closing the waiting-request
race. Cleanup RPCs and the caller's own profile read remain available for retries.
No administrative credential is placed on the home API or either app. Owned
orphan Storage objects block final deletion for operator review. Retained encrypted
backups may contain prior data until retention expires; restore procedures must
reconcile deletion requests before exposing restored data publicly.

Provider reference documents checked during implementation:

- [Supabase billing quotas](https://supabase.com/docs/guides/platform/billing-on-supabase)
- [Supabase Storage bandwidth](https://supabase.com/docs/guides/storage/serving/bandwidth)
- [Auth sessions and revocation](https://supabase.com/docs/guides/auth/sessions)
- [Resend free email limits](https://resend.com/pricing)
