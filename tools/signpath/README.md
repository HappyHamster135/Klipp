# SignPath build integration

Status: prepared for Foundation review. No signing account, certificate or signing approval is configured by these files.

The manual **Windows release candidate** workflow defaults to an **unsigned review** build. Signing is requested only when its `sign` input is enabled, the repository variable `SIGNPATH_ENABLED` is exactly `true`, and all approved settings are present. It only accepts `HappyHamster135/Klipp`, `main`, manual dispatch and GitHub-hosted runners. It does not publish releases or change an existing release.

After Foundation approval, configure these GitHub repository variables using the values assigned by SignPath:

- `SIGNPATH_ORGANIZATION_ID`
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`
- `SIGNPATH_EXPECTED_PUBLISHER`: the exact full certificate subject, not an assumed display name
- `SIGNPATH_ENABLED`: set to `true` only after approval, the signing policy and artifact configuration have been reviewed

Store the submitter token as the GitHub Actions secret `SIGNPATH_API_TOKEN`. Do not put it in source files, workflow inputs or command arguments. Keep manual approval enabled for the `release-signing` policy.

The local Node action keeps GitHub's artifact runtime context available while the packaging process runs. Each callback uploads an allowlisted binary batch through the pinned official artifact action, then submits that artifact ID using the pinned official SignPath action and its GitHub connector. The service verifies workflow origin and waits for maintainer approval. Returned files must all have valid, timestamped code-signing signatures from the configured publisher before any original is replaced. The complete release and copied downloadable artifact are then verified again by `Test-KlippReleaseSignatures.ps1`.

The allowlist includes Klipp's binaries and the Velopack launcher/updater/Setup helpers needed for installation. Foundation must explicitly approve this helper scope and metadata policy before enabling the workflow. Other dependency DLLs are distributed with their existing signatures and are never submitted under Klipp's signing policy. The Velopack signing exclusion in `Pack-KlippInstaller.ps1` must match this allowlist.

No third-party action source is vendored. Official action repositories are fetched at reviewed, fixed commit hashes in the workflow. No npm install is required. Review the official action changes before updating those hashes.

Local offline checks:

```powershell
node --test tools/signpath/signpath.test.cjs
```

These checks use temporary files and mocked action processes. They do not upload artifacts, submit signing requests, access certificates or sign anything. A real signed CI run still requires the approved SignPath account and policy.

Primary references:

- [SignPath's GitHub origin verification and action inputs](https://docs.signpath.io/trusted-build-systems/github)
- [Official SignPath submit action](https://github.com/SignPath/github-action-submit-signing-request)
- [GitHub artifact action](https://github.com/actions/upload-artifact)
