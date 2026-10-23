# Community Media harness

`--local` checks preparation using a generated clip. `--live` explicitly uploads
and removes a generated fixture through the configured backend. `--cleanup`
removes only that recorded fixture.

Add `--http2` to a live run to require HTTP/2 for uploads, API calls and media
downloads without falling back to HTTP/1.1. The default explicitly tests HTTP/1.1.
Both modes read the complete generated video and poster with time/size bounds and
compare their SHA256 hashes to the uploaded originals, then check byte-range seeking.
Live runs also verify that a one-byte upload with browser headers is rejected by
the desktop-only server guard before sending the normal clip upload.
Existing account credentials and signed URLs are never printed.

`--operations-status --operations` restores the protected session and reads the
deployed account capabilities without uploading. It sends a deliberately invalid,
non-confirming body to the deletion function to check the deployed runtime guard;
it cannot confirm or delete any account. When capabilities are enabled it also
checks private operator usage and queues.

Add `--operations` to `--live` only after the operations deployment is activated.
It checks usage, reports/hides/restores **only its generated clip**, verifies an
already issued home link is revoked while hidden, and creates/resolves a clearly
marked private test request. The usual finally block removes the generated clip.
These checks do not delete the signed-in account or change existing user clips.

`--export-legacy --config <public-config.json> --export-directory <artifacts-subdirectory>`
performs a read-only, one-time export of the signed-in owner's published legacy
Supabase clips. It restores the existing Windows DPAPI session through the normal
account service. It does not request a login email, change clips or call upload,
like, comment or deletion APIs. Run it while other clients using that saved
session are closed to avoid competing token refreshes.

The output directory must be empty and inside the current workspace's `artifacts`
directory, without symbolic links or junctions. The export checks account
ownership, a maximum of 40 clips, exact signed-capability host/path, HTTP 200,
content type, complete length and strict byte limits. Redirects are disabled by
the harness HTTP handler. Downloaded files receive SHA256 entries in
`manifest.json`; credentials, signed URLs, titles and email are never saved there
or printed. Treat the clip bytes and identifier-only manifest as private data.

The expected manifest shape is:

```json
{
  "files": {
    "<clip-guid>/video.mp4": { "sha256": "<hash>", "size": 123 },
    "<clip-guid>/poster.jpg": { "sha256": "<hash>", "size": 123 }
  },
  "clips": ["<clip-guid>"]
}
```

Copy this temporary transfer into the server's root-only legacy backup source,
verify every hash after transfer and run the encrypted backup/restoration check
before removing the temporary laptop export. The laptop export is not a backup
destination. New home-hosted uploads use the regular server backup path.

`--export-legacy-self-test` runs local download-boundary, oversized/chunked-body,
redirect, path, hash and incomplete-response checks without account access.
