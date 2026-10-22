# Code signing policy

**Status: signing disabled.** Klipp 1.0.8 and earlier public releases are unsigned. SignPath setup is deferred. The repository retains signing and verification support, but no production certificate or SignPath account has been configured. Do not describe those releases as signed or install a development certificate on users' computers.

## Proposed provider and responsibilities

The maintainer has chosen to apply for the free [SignPath Foundation open-source program](https://signpath.org/). Acceptance is subject to their review and [conditions](https://signpath.org/terms.html). The planned attribution, to be activated only after approval, is: “Free code signing provided by SignPath.io, certificate by SignPath Foundation.”

Proposed author, reviewer and signing approver: [HappyHamster135](https://github.com/HappyHamster135). The maintainer must confirm these roles and enable multi-factor authentication on GitHub and SignPath before activation. External contributions require maintainer review. Signing requests require manual approval; an unsigned review build does not authorize signing.

See the [privacy policy](PRIVACY.md) for local data handling and network downloads. The signing service receives CI build artifacts and source/build provenance, not user recordings or settings.

## Application

Use the prepared [application details](SIGNPATH-APPLICATION.md) with the [Foundation application](https://signpath.org/apply.html). The account owner supplies their own contact information, confirms MFA and accepts the provider's terms. No application has been submitted by these build scripts.

Ask Foundation to review the WinUI/.NET/VC++ system-runtime dependencies, the optional DirectX game-capture hook, and signing of Velopack's generated launcher, updater and bundled installer. Approval of this helper scope is required before enabling it. Do not submit arbitrary third-party libraries under Klipp's signing identity.

## Release pipeline

The manual [Windows release candidate workflow](../.github/workflows/windows-release.yml) can build an unsigned candidate for provider review. Production signing remains disabled until the approved account, GitHub integration, signing policy, secret token and exact publisher subject are configured. It uses GitHub-hosted builders and pinned official upload/signing actions so the provider can verify the build origin. The [CI setup instructions](../tools/signpath/README.md) list the required variables and secret.

Velopack submits the Klipp payload and approved packaging helpers during packaging, then submits the completed Setup executable. These can require separate manual approvals. Its built-in portable ZIP and update package contain the signed payload. Do not pre-authorize unreviewed releases or alter files after signing.

The build stops on failed signing or verification. It checks the installer and archive contents for trusted, timestamped signatures by the expected publisher, and checks asset/feed hashes. A successful signed build records `signing.verified=true`. The workflow produces a review artifact; it does not automatically publish a GitHub release.

Never store signing tokens, private keys or passwords in this repository or command arguments. Put the SignPath token in the repository's Actions secret named `SIGNPATH_API_TOKEN`, after approval. Non-secret provider identifiers and the exact approved publisher subject belong in Actions variables.

## Windows warnings

A publicly trusted signature provides a verified publisher and integrity checking. It does not guarantee that every Windows security or antivirus prompt disappears immediately. SmartScreen also evaluates reputation. [Microsoft's current guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation) applies; a self-signed development certificate does not solve public distribution trust.
