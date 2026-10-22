# Optional accounts and Media

The Windows Media workspace supports optional accounts, profiles and sharing clips with other members. Recording, editing, exporting and the local Library remain available without an account, an internet connection or a configured backend.

The online client supports email verification codes, saved sessions, profiles, a paged community feed, private clip storage, likes, comments, reports and reversible user blocking. Media requires a verified permanent account; a username is required to share and interact. Uploading is always an explicit action. Mobile clients, follows and a moderator interface are not included yet.

## Deployment status — 22 October 2026

The existing Supabase **Free** project **Shilpp** (`hxbblnuyfijoppjoglce`) is being used for this deployment. The accounts/profiles, Media and follow-up RPC-only policy migrations have been applied. The local publisher configuration at `backend/community.json` contains its public project URL and publishable key. The private `community-videos` and `community-posters` buckets have the size and MIME restrictions described below. The follow-up's six explicit deny policies also cleared the advisor's informational findings about Media tables having RLS enabled without policies.

The sending domain `accounts.shlipp.com` is verified in Resend. Receiving is disabled; this domain is being used to send login emails, not to host an inbox. The Resend account remains on **Free**, with paid overages disabled.

The approved **Sending access** API key is restricted to `accounts.shlipp.com`. Custom SMTP is enabled in Supabase using sender **Klipp** / `login@accounts.shlipp.com`, host `smtp.resend.com`, port `465` and username `resend`. These settings were confirmed after reloading the provider form; its saved password remains hidden. The key value is not stored in this repository or the desktop client.

Both **Confirm Sign Up** and **Magic Link / OTP** templates use the code-only Klipp HTML. Live emails were delivered with the subjects **Your Klipp confirmation code** and **Your Klipp sign-in code**. Their code-only bodies were verified without exposing the codes in chat, screenshots or logs.

The live account check used the actual desktop account service and Windows DPAPI session store: the pending signup was confirmed with its real code, the member profile was loaded and saved with the owner's chosen username, sign-out removed the saved credential, a fresh sign-in returned the same account, and a new service instance restored the protected session. Codes were transferred through a same-user-only named pipe and were never command-line arguments or plaintext files. No recipient address, code or token is recorded here.

The Media-enabled local installer **1.0.11-local.2** was built and installed on this PC with signing disabled. Its owned binary versions, application/installer/update-package icons, asset checksums and public client configuration were verified; automatic updates remain enabled. It has not been published as a GitHub release. SMTP credentials belong only in provider settings, not in the desktop configuration or installer.

The live Media harness uploaded a generated test clip through the actual account service, found it in the feed, downloaded its private video and poster through short-lived URLs, checked anonymous feed denial, and verified like/unlike and comment creation/removal. The installed build restored the saved account and passed feed/playback checks. Local preparation checks covered selected audio, output limits, cancellation and keeping the generated source unchanged. The test upload, both physical files and its metadata/interactions were removed afterward; original recording preferences were restored byte-for-byte. These checks used generated media, not a personal recording. See [the Media harness](../tools/Klipp.CommunityMediaHarness/README.md).

## Free-tier plan

Use one Supabase **Free** project for Auth, profiles, Media metadata and private file storage. The published free allowance currently includes 50,000 monthly active users, a 500 MB database and 1 GB of file storage; inactive free projects can pause after a week. Storage requests, cached delivery and uncached egress also have finite allowances. These are service limits, not a promise of unlimited free operation. See [Supabase pricing](https://supabase.com/pricing).

Email delivery needs separate attention: Supabase's default SMTP service sends only to organization team addresses and currently allows two messages per hour. In addition, new Free projects using the default SMTP service cannot customize Auth email templates from 3 June 2026. Klipp's code-only sign-in needs a template containing `{{ .Token }}`, so configure custom SMTP **before** editing its templates or testing the code flow, including internal tests on a new Free project. Existing projects that retained older templates are an exception; do not assume that exception applies to a new deployment. See [the Free template change](https://supabase.com/changelog/46599-changes-to-email-template-customisation-on-free-tier) and [Supabase SMTP limitations](https://supabase.com/docs/guides/auth/auth-smtp).

For a beta with real users, keep the custom SMTP provider within its free allowance. Resend advertises a free plan with 3,000 messages per month and a 100-message daily limit and requires a domain you own with DNS verification; buying a domain is a separate cost if you do not have one. Do not enable paid overages for a free-only beta. The deployment above uses the already-owned sending domain listed in its status. See [Resend pricing](https://resend.com/pricing) and [verified sender domains](https://resend.com/docs/dashboard/domains/introduction).

If no domain is available, Brevo documents temporary sender-address rewriting for unauthenticated domains, including verified Gmail senders, to its shared `brevosend.com` domain. Its Free plan has a 300-email daily allowance. This may support a small no-domain beta, subject to sender verification, transactional-platform activation and an actual delivery test; it is not a permanent branded-domain guarantee. Excess transactional messages can queue, so keep usage below the quota rather than allowing old login codes to arrive late. See [Brevo sender rewriting](https://help.brevo.com/hc/en-us/articles/16045394674066-Troubleshooting-Domain-authentication) and [Free plan limits](https://help.brevo.com/hc/en-us/articles/208580669-FAQs-What-are-the-limits-of-the-Free-plan).

Accounts alone do not require video storage, SMS login or a paid email provider. Media uses the project's existing free Storage allowance; no paid video/transcoding service is enabled.

The device creates a compressed share copy, JPEG poster and selected audio mix. The beta permits at most **60 seconds / 16 MiB of video**, **256 KiB per poster**, **5 reserved clips per account** and **40 reserved clips globally**. Each reservation counts as a full 16.25 MiB slot, including failed uploads and clips awaiting deletion, so clients cannot evade capacity limits by claiming a smaller size. This bounds reserved media to 650 MiB. It does not cap total viewing traffic: repeated plays and thumbnail loads still consume egress. Monitor usage and keep the beta small; move to another storage design or change limits deliberately before increasing capacity.

Video encoding and probing happen locally, at up to 720p / 30 fps with H.264 and optional AAC audio. The server checks account access, reservation limits and the stored objects' owner, path, size and MIME metadata before publishing. It does not decode videos, verify their actual duration from the uploaded bytes, transcode them or create adaptive streaming renditions.

## Configure a Supabase project

1. Use the existing intended project if it is already on **Free**; do not create a duplicate project for the deployment above. For a fresh deployment, create a project on the **Free** plan, choose the intended hosting region and keep paid add-ons disabled. Record the project URL and the **publishable** API key from the project's API settings.
2. Inspect the existing schema and migration history. Apply `backend/supabase/migrations/202610220001_accounts_profiles.sql` once if it has not already been applied. The migration creates profiles for existing accounts too. For the deployment above this step is complete. Alternatively, use the Supabase CLI migration workflow against the intended project after reviewing the migration.
3. In Authentication, enable the Email provider, allow new sign-ups for this beta and keep email confirmation enabled. Keep anonymous sign-ins disabled: online Media requires a permanent account. Klipp uses one-time email codes; it does not ask users to create a password or handle an email magic-link callback.
4. Set up the free email provider, verify its sender/domain, then enable and save **custom SMTP** in Supabase using the settings below. On new Free projects this must be completed before Auth email templates can be customized. Stay on Free and leave overages disabled.
5. In Authentication email templates, configure **Magic link or OTP** to display `{{ .Token }}`. Configure **Confirm sign up** to display it as well so both new and returning users receive a code. A minimal body is:

   ```html
   <h2>Your Klipp sign-in code</h2>
   <p>Enter this code in Klipp:</p>
   <p><strong>{{ .Token }}</strong></p>
   <p>If you did not request this code, you can ignore this email.</p>
   ```

   The complete Klipp-styled body is saved at `backend/supabase/templates/email-code.html`. Paste that file into both templates. Use **Your Klipp sign-in code** for the Magic Link subject and **Your Klipp confirmation code** for the Confirm Sign Up subject. The template has no external images, tracking links or redirect links. Keep the `{{ .Token }}` placeholder unchanged.

6. Keep Auth's server-side request and verification rate limits enabled and below the email provider's free quota. If CAPTCHA is enabled at the provider, a compatible client flow must be added before enabling that setting for this desktop beta.
7. For a new deployment, copy `backend/community.example.json` to `backend/community.json` and replace both placeholders with the project's public values. The local configuration for the deployment above has already been set:

   ```json
   {
     "projectUrl": "https://YOUR_PROJECT_REF.supabase.co",
     "publishableKey": "sb_publishable_YOUR_PUBLIC_KEY"
   }
   ```

8. Build or publish the desktop app. When present, `backend/community.json` is copied to `community.json` beside the executable. This is publisher configuration; end users should receive it with the app and should never need their own Supabase account or API key. Existing installs will need a newly packaged build to receive configuration changes. The local configuration file is ignored by Git.
9. Before inviting users, complete the signup, profile, sign-out and sign-in checks below with an address outside the Supabase organization team. Check actual code delivery and session restoration; the built-in SMTP's team-only testing is not a substitute for this custom-SMTP test.

Only a root HTTPS project URL and a public publishable key belong in this file. Legacy `anon` JWT keys are also supported. **Never** include a secret key, `service_role` key, database password, SMTP credentials or JWT signing secret in the desktop app, repository or installer. All client authorization depends on the user's verified session and database policies, not on keeping the public key secret. An absent, placeholder or invalid file leaves online features unavailable without affecting local capture/editing.

Official references: [passwordless email and OTP templates](https://supabase.com/docs/guides/auth/auth-email-passwordless), [email template variables](https://supabase.com/docs/guides/auth/auth-email-templates), [Auth user/profile tables](https://supabase.com/docs/guides/auth/managing-user-data).

## Free login email settings

Use **Supabase Free** and the email provider's **Free** plan. Leave transactional overages, automatic top-ups, paid add-ons and paid upgrades disabled. If onboarding requires buying a plan, stop at that screen; a paid plan is outside this setup. The free email allowance limits login volume, even though the Auth allowance is much larger.

For a domainless beta, create or sign in to Brevo, stay on Free, and add a sender named **Klipp** using an email address you control. Complete the provider's sender verification yourself. In its SMTP settings, obtain the **SMTP Login** and an **SMTP key**. Do not use the account password or an API key as the SMTP password.

In Supabase **Authentication → Email → SMTP settings**, enable custom SMTP and fill these fields:

| Field | Brevo Free | Resend Free, with an owned verified domain |
| --- | --- | --- |
| Sender name | `Klipp` | `Klipp` |
| Sender email | The email address verified in Brevo | An address on the verified sending domain |
| Host | `smtp-relay.brevo.com` | `smtp.resend.com` |
| Port | `587` | `465` |
| Username | Copy **SMTP Login** from Brevo | `resend` |
| Password | The Brevo **SMTP key** | A Resend sending API key |

Enter SMTP secrets directly into Supabase's protected provider settings. Do not put them in `community.json`, Git, screenshots, chat or the desktop installer. If a key must be created or copied, the account owner should handle it privately.

Keep email confirmation enabled. Klipp accepts email codes, not a password or email-link redirect. Use a six-digit code, a 60-second resend interval and an expiry appropriate for delivery time; Supabase's default is one hour. Custom SMTP initially has a 30-email/hour Supabase limit. For a small beta, use conservative server limits and monitor the provider's daily quota. The hourly limit is not a guarantee that the daily free quota cannot be reached.

Verify the integration before inviting users: request a **Create account** code from Klipp to an address outside the Supabase organization team, check arrival time and spam placement, enter it, save a username/profile, sign out, then request a fresh **Sign in** code for that same address. Do not create an administrative test account or disable confirmation to make the test pass. Verify session restoration after restarting Klipp too. A provider test email alone does not prove account creation, OTP verification and profile access work.

Official settings: [Brevo SMTP and activation](https://help.brevo.com/hc/en-us/articles/115000188150-Troubleshooting-Issues-with-Brevo-SMTP), [Resend with Supabase](https://resend.com/docs/send-with-supabase-smtp), [Supabase SMTP limits](https://supabase.com/docs/guides/auth/auth-smtp).

## Database access contract

`public.profiles` contains only `id`, `username`, `display_name`, `bio`, `created_at` and `updated_at`. Email addresses and authentication information remain in Supabase Auth.

- A private signup trigger creates a profile with `display_name = 'Player'`, no username and an empty biography. It ignores client-supplied signup metadata. Existing Auth accounts are backfilled by the migration.
- A username is optional until the member sets up their profile. Handles must be unique, lowercase ASCII, 3–24 characters, start with a letter, and contain only letters, numbers and underscores. Display names are trimmed, nonempty and up to 40 characters; biographies are up to 160 characters.
- Anonymous API access has no profile privileges. Signed-in members can read profiles. Row-level security restricts updates to the member's own profile, and column grants restrict changes to `username`, `display_name` and `bio`.
- Desktop requests use an explicit profile field list and the current account's ID. Updates are `PATCH` requests, not inserts or upserts. Identity and timestamps are managed by the server.
- After owned Media files and clip reservations have been removed, administrative Auth account deletion removes the profile through its foreign key. There is no client account-deletion endpoint or in-app deletion UI yet. Before a public beta, publish a private operator contact for deletion requests. Keep administrative credentials off the client.

See [Supabase RLS](https://supabase.com/docs/guides/database/postgres/row-level-security). Trigger functions use a fixed empty search path and live in an unexposed schema; clients cannot call them as RPCs.

## Enable Media storage and access

For a fresh deployment, first apply the accounts/profiles migration above. Create these buckets through the Supabase Storage dashboard or Storage API, keeping **Public bucket off**:

| Bucket | Maximum object size | Allowed MIME types |
| --- | --- | --- |
| `community-videos` | `16777216` bytes (16 MiB) | `video/mp4` |
| `community-posters` | `262144` bytes (256 KiB) | `image/jpeg` |

Apply `backend/supabase/migrations/20261022114947_community_media.sql` once against the intended project. It requires both private buckets with exactly these limits before allowing upload reservations. Do not create or remove Storage objects by writing to `storage.objects`; use the Storage API so physical files and metadata remain consistent.

Then apply `backend/supabase/migrations/20261022120248_community_rpc_only_policies.sql`. Its six restrictive deny policies cover the five public Media tables and the private comment-rate event table. Direct anonymous/member access remains denied even if a later change accidentally adds a table grant and a permissive policy. Checked private RPC implementations and protected operator access retain their intended privileges.

- Direct anonymous or member reads/writes of the Media tables are denied. Public RPC wrappers call private implementations that check the current verified, non-anonymous, non-banned Auth account. A user's JWT metadata cannot grant ownership or moderation privileges.
- Server-side reservation locking enforces the five-account / forty-global limits. Uploads use fixed owner/clip paths, reject replacement and retain capacity until physical file cleanup succeeds.
- Publishing requires both the reserved video and poster. Public feeds hide unfinished/deleting clips, blocked members in either direction, and authors with incomplete profiles. Owners can still find their own unfinished clips in **My uploads**.
- Feed pages show twelve clips with **Newer clips / Older clips** navigation. Comments load thirty at a time with **Load older comments**. Both use timestamp/ID cursors rather than downloading all rows. There is no automatic video playback; posters load for visible cards and videos stream when selected.
- Likes set an explicit on/off state. Members can post comments and delete their own comments. The server caps comments at 100 per clip and limits comment frequency. Reports are stored for the operator; they do not automatically remove a clip. **Block this user** hides their content, and **Blocked users → Unblock** reverses a block.
- Playback and poster links are signed on demand using the current user's access and Storage policies. They expire after five minutes and two minutes respectively. Possession of an already-issued link permits access until its expiry, including after a block or local sign-out.
- **Delete shared clip** hides an owned clip first, removes both files through Storage, then releases its reservation and associated likes/comments/reports. If cleanup fails, its hidden reservation remains in **My uploads** so deletion can be retried. Deleting a shared clip does not delete its original local recording.

There is no in-app moderation console or account-deletion action yet. The operator must review reports and handle moderation/deletion requests with protected administrative tools. Remove an account's physical Media files and clip reservations before deleting its Auth account; the clip foreign key intentionally prevents account deletion from leaving untracked files behind. Publish the operator's private contact method and retention details before a public beta.

To share, use **Upload clip** in Media or **Upload to Media** on a Library clip. Review the title, description, section and audio selection before confirming. Game audio is selected by default when a separate game track is available; microphone, Discord and music tracks are included only when selected. A single existing mixed track may already contain multiple sources. The original is unchanged. A temporary share copy is prepared under `%TEMP%\Klipp\CommunityShare`; Klipp attempts to remove it on completion or cancellation. Uploads show progress and can be canceled. No capture or editor project is uploaded automatically.

## Local development and SQL checks

The CLI's local Supabase stack runs for free. On Windows it requires a compatible container runtime; it is not included in Klipp. See [local development setup](https://supabase.com/docs/guides/local-development).

For a new disposable local setup, from the repository root:

```powershell
supabase init --workdir backend
supabase start --workdir backend
supabase db reset --workdir backend
```

`db reset` resets that local development database and applies the migrations. Use the local database connection shown by `supabase status --workdir backend`, then run the checks with `psql`:

```powershell
psql "postgresql://postgres:postgres@127.0.0.1:54322/postgres" -v ON_ERROR_STOP=1 -f backend/supabase/tests/accounts_profiles.sql
psql "postgresql://postgres:postgres@127.0.0.1:54322/postgres" -v ON_ERROR_STOP=1 -f backend/supabase/tests/community_media.sql
```

The account SQL file creates fixtures inside a transaction and rolls them back. It checks signup defaults, anonymous access denial, member reads, own-profile updates, cross-account update denial, protected identity/timestamps, username/content constraints, duplicate handles, missing identity and profile deletion cascade. The Media SQL file checks quotas, ownership, private-function grants, blocked/incomplete profiles, tuple pagination, interaction limits and cleanup policies, including denial after deliberately adding broad grants/permissive policies to its disposable fixtures. Its Storage rows are synthetic local fixtures, not physical uploaded files. Use a disposable local database for these tests, never the public service.

The desktop configuration intentionally requires HTTPS. For a full local Auth/UI integration run, put the local API behind a trusted local HTTPS endpoint, or use a separate hosted Free test project. Do not weaken the published client's HTTPS requirement to make local development easier.

The 24 account/profile assertions and 61 Media assertions passed on PostgreSQL 18.6 through temporary PGlite harnesses with minimal Supabase Auth, role and Storage fixtures. These SQL checks did not use a hosted service or real user credentials. All three migrations were subsequently applied to the existing hosted Free project listed above. Live account and generated-media checks are described in the deployment status. See [the account harness](../tools/Klipp.CommunityHarness/README.md) and [the Media harness](../tools/Klipp.CommunityMediaHarness/README.md); ordinary harness runs remain local.

`backend/supabase/tests/community_media_deployment_check.sql` is a separate read-only production check. It reports schema/policy metadata and aggregate reserved capacity without returning accounts, emails, object paths or media. Expected values after all migrations are: five public tables with RLS, thirteen public invoker RPCs, zero anonymous RPC grants, zero direct client table grants, eight Storage policies, six restrictive RPC-only deny policies and two correctly configured private buckets. Reserved slots must stay at or below forty. This metadata check complements the local fixture tests and live generated-media check; it does not decode or inspect uploaded videos.

## Data and session handling

Online requests occur on Media/account actions, not during local recording startup. Saved sessions are protected for the current Windows user using DPAPI and stored under `%LOCALAPPDATA%\Klipp\Community`, separated by a hash of the configured project URL. They are not stored in ordinary app settings. A restored session is checked with the Auth service before it is used; refresh, profile and Media requests stay separate from capture/editor work. Media uploads stream from disk in bounded buffers; they are not loaded entirely into RAM. Navigation/sign-out stops online playback, and sign-out fences stale requests.

Signing out clears the local session even when the network is unavailable. It leaves recordings and editor projects alone. It does not delete the online account. Auth/profile failures must stay within Media and must never block recording, editing or export. See [the privacy policy](PRIVACY.md) for the user-facing data description.
