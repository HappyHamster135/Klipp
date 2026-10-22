# SignPath Foundation application draft

This draft is ready for the maintainer to review and copy into the [Foundation application](https://signpath.org/apply.html). It has not been submitted and does not claim approval.

| Field | Project information |
| --- | --- |
| Project name | Klipp |
| Repository and project home | https://github.com/HappyHamster135/Klipp |
| License | MIT; license and dependency notices are included in release packages |
| Existing Windows release | https://github.com/HappyHamster135/Klipp/releases/tag/v1.0.6 |
| Maintainer account | https://github.com/HappyHamster135 |
| Publisher type/location | Individual open-source developer in Sweden |
| Code signing policy | https://github.com/HappyHamster135/Klipp/blob/main/docs/SIGNING.md |
| Privacy policy | https://github.com/HappyHamster135/Klipp/blob/main/docs/PRIVACY.md |

## Project description

Klipp is an open-source Windows game-recording, instant-clipping and video-editing application. It captures a selected display or window and chosen audio sources, stores recordings locally and provides a multitrack editor. It uses .NET 10, WinUI 3, Windows Graphics Capture, Media Foundation, Direct3D 11 and WASAPI. FFmpeg and optional local caption tools download when needed; recordings are not uploaded to those providers.

## Signing request

We request free open-source code signing for the Windows application and installer. The proposed build is a manual GitHub Actions workflow on GitHub-hosted Windows runners. It builds from the public repository and uploads signing inputs using pinned official GitHub and SignPath actions for build-origin verification. Production signing requires manual maintainer approval and is disabled until the Foundation account and policy are approved.

The intended artifacts are Klipp's own managed and native binaries, the approved Velopack execution stub/updater, and the final bundled Setup executable. The same signed payload is included in the portable ZIP and full update package; hashes are generated afterward. Please confirm the allowed signing scope and metadata rules for the upstream Velopack helpers. Upstream runtime/dependency libraries keep their original signatures and are not submitted as Klipp-owned binaries.

The optional DirectX capture hook is source available in `src/Klipp.GameHook`; its purpose is game-frame capture and non-interrupting notifications. Please include this component and the included Microsoft system runtimes in the project review.

## Details the account owner must provide

- Contact name and email for the application; do not put identity documents or account passwords in this repository.
- Confirmation of source ownership, the proposed author/reviewer/approver roles and MFA on both accounts.
- Acceptance of the Foundation's conditions and any requested review or project-reputation information.

After approval, configure the provider identifiers, approved publisher subject and Actions secret. The repository never creates a signing identity or accepts the provider's terms on the maintainer's behalf.
