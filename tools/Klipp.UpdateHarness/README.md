# Update checks

Run the isolated update state-machine checks:

```powershell
dotnet run --project tools/Klipp.UpdateHarness/Klipp.UpdateHarness.csproj
```

The checks use fake update clients and isolated temporary state files. They cover offline/rate-limit handling, poll and download backoff across launches, concurrent requests, canceled downloads, verified-package readiness, graceful restart preparation, disposal and portable builds. They do not install, update or close Klipp.

For a real native update test, first install a local earlier-version fixture using its Setup executable. Keep the production application ID, but use QA settings with capture disabled and a test clip library. Close Klipp before applying:

```powershell
dotnet run --project tools/Klipp.UpdateHarness/Klipp.UpdateHarness.csproj -- `
  --installed-root '<actual Velopack install root>' `
  --feed '<directory containing releases.win.json and the newer .nupkg>' `
  --expected-version 1.0.6 --apply
```

This parses the actual installed `current/sq.version`, checks and downloads through the SDK and `AppUpdateService`, verifies the prepared version, then launches the real native updater. The updater waits for the harness to exit, applies the package and restarts Klipp. Omitting `--apply` checks/downloads without installing. A feed containing only older versions should result in `UpToDate` and no downgrade.

The test refuses another application ID, a missing installation manifest or a running Klipp process at apply time. It never forces process exit and does not use UI automation.
