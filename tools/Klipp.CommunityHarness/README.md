# Community account Windows checks

Run `dotnet run --project tools/Klipp.CommunityHarness` on Windows.

This harness links the desktop deployment-configuration loader and Windows DPAPI session store into a console application. It verifies encrypted credential roundtrips, isolation between backend projects, clearing, corrupt and oversized files, cancellation, configuration failure states, and safe persistence errors. All HTTP replies come from an in-memory handler. It never contacts a hosted backend, changes application settings, or starts capture.

An explicit operator-only live mode is available separately:

```powershell
dotnet run --project tools/Klipp.CommunityHarness -- --live --config backend/community.json --email <owner-controlled-address> --pipe Klipp.CommunityHarness.<random-nonce> --keep-session
```

This opt-in mode uses the actual account service, configured hosted backend and current Windows user's project-scoped DPAPI store. It sends two account emails only after named-pipe commands, verifies signup/sign-in codes, loads and updates the profile, signs out and verifies a new service can restore the final session. A user-chosen username can be supplied with `--username <chosen-username>` or through the profile-update pipe stage. Existing display name and bio are preserved. It never invents a username; a skipped incomplete profile remains incomplete. A different existing saved account causes a safe failure. `--session-directory <folder>` can explicitly select the target Windows credential folder; otherwise it uses the same `%LOCALAPPDATA%\Klipp\Community` location as the app. Successful live checks retain the protected session for the app; failed checks remove sessions they created where possible.

Each `LIVE_READY` line supplies the pipe name, stage and accepted input. A same-Windows-user pipe client sends one newline-terminated value per connection: `send` for `signup-send`, the first code for `signup-code`, `username:<chosen-value>`, `keep` or `skip` for `profile-update` (unless `--username` was supplied), `continue` for `signout`, `send` for `signin-send`, and the fresh code for `signin-code`. `keep` tests a PATCH with existing values if the username is already complete; `skip` makes no profile change. The final status explicitly reports whether profile PATCH was verified. Each input has a ten-minute deadline. The pipe uses `CurrentUserOnly`; codes must never be passed in command-line arguments, printed, saved to plaintext files, or pasted into chat. Output contains stages, booleans and safe client error codes only. The live mode is not run by the ordinary offline harness or builds. `--live --help` displays the interface without contacting any backend.
