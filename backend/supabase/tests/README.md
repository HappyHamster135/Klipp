# Supabase checks

For the current home-hosted upload deployment, run `community_upload_hardening.sql`
and `community_home_cleanup_queue.sql` after applying both matching migrations.
These create only reserved QA application and
Auth rows, then roll the transaction back. They never write Storage-managed
metadata. Use the Supabase project-owner SQL connection, never a client role.
Each script has a two-second lock timeout and a twenty-second statement timeout.

`community_upload_hardening_deployment_check.sql` and
`community_home_deployment_check.sql` are read-only receipts for the current
deployment. They return aggregate checks without email, account IDs or secrets.

The earlier `community_media.sql` is a **local-only historical test** of the
original cloud-upload stage. It writes fake Storage metadata and must never run
against hosted Storage. `community_home_media.sql` tests the earlier mixed-provider
stage, before new cloud uploads were retired. Apply only the migrations for those
stages when running their historical tests; their old-upload expectations are
intentionally superseded by `community_upload_hardening.sql`.

Reservation cleanup is a two-phase operation. The gateway first claims expired
home reservations or already owner-authorized home deletion tombstones, deleting their files under the same clip and backup locks used
by uploads. It confirms cleanup through the scoped maintenance RPC only after
successful physical deletion. A crash or failed cleanup retains the metadata and
quota and is retried. Never release capacity by deleting pending metadata alone.
Both uploading and deleting home rows count toward the pending-work bounds;
calling the remove RPC cannot reset those bounds before file cleanup completes.
