# Media reliability checks

`check_media.py` anonymously checks the real public HTTPS origin, certificate trust/name and at least 14 days of certificate lifetime, bounded live/ready JSON, private-response headers and denial of an unsigned synthetic video request. It never signs in, uploads, reads a real clip or uses a credential. Three attempts with short delays tolerate transient failures; a healthy run is silent.

```powershell
python tools/hosting-monitor/check_media.py --attempts 1
python -m unittest discover --start-directory tools/hosting-monitor --pattern "test_*.py"
```

The `Media HTTPS availability` workflow runs every 15 minutes, offset from the hour, and can also be run manually. It is restricted to the public `HappyHamster135/Klipp` repository and standard Ubuntu runners; it uses no secrets, uploads no artifacts and creates no public issue or outgoing message. Public standard-runner usage is [free](https://docs.github.com/en/billing/concepts/product-billing/github-actions). Making the repository private causes the monitor job to skip rather than consume private-repository minutes.

The owner must enable GitHub **Settings → Notifications → System → Actions → Email / Only notify for failed workflows** and save. Native scheduled-run notifications go to the associated workflow actor, normally the schedule creator or last cron editor; they are not sent to Klipp members. Each failed scheduled run can notify again. Workflow checks/logs are public, but contain only anonymous service results. This is separate from the existing private server email alerts, which cannot report a complete power/network failure.

GitHub scheduling is best effort: jobs can be delayed or dropped, run only from the default branch, and public schedules are disabled after 60 days without repository activity. Review the Actions page and re-enable inactive schedules. This is not an uptime guarantee. The monitor is operational only after publication to the default branch and a successful GitHub-hosted run. See [schedule limits](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule) and [notification settings](https://docs.github.com/en/subscriptions-and-notifications/how-tos/managing-github-actions-notifications).

## Planned reboot inspection

`inspect_server.py` is a read-only Linux operator tool. It verifies both mounted volumes, all backup/restore timestamps, active/enabled API/proxy/private administration and three timers, no-swap backup jobs, actual running proxy identity and CA-verified local HTTPS. It neither reboots nor changes services. A complete inspection needs root to read protected recovery receipts and `/proc` identities. Do not copy arbitrary scripts into a privileged command: use the reviewed deployment copy.

Before a separately approved reboot, save the returned `boot_id` privately:

```bash
sudo python3 -I /REVIEWED_SOURCE/tools/hosting-monitor/inspect_server.py
```

After reconnecting over Tailscale, pass that exact identifier; an unchanged ID fails:

```bash
sudo python3 -I /REVIEWED_SOURCE/tools/hosting-monitor/inspect_server.py --previous-boot-id PREVIOUS_BOOT_UUID
```

Then run the public monitor from outside the server/home network, or use the phone with Wi-Fi disabled. Local certificate checks deliberately do not claim WAN reachability. No actual reboot is performed by these tools or claimed without the before/after checks.

## Offline/off-site recovery copy

The separate internal 120 GB SSD is the automatic backup destination. It protects against the main SSD failing, but shares the server's power, location and administrator. There is currently no verified third-disk/off-site copy. An offline disk need only be connected for copying; it need not run continuously.

First identify a third disk and its existing Linux filesystem/mount/UUID. Do not guess a drive or format it. It must be independent of both installed SSDs, root-owned, mounted with `rw,nodev,nosuid,noexec`, and have enough free space for a complete encrypted repository plus reserve. This preflight reads only and does not create even a destination directory:

```bash
sudo python3 -I /REVIEWED_SOURCE/tools/hosting-monitor/inspect_server.py \
  --offline-mount /EXPLICIT_REVIEWED_MOUNT --expected-uuid EXPLICIT_REVIEWED_UUID
```

After reviewing that result, the already installed copy tool is a separate explicit action:

```bash
sudo /usr/local/libexec/klipp-offline-export \
  --mount /EXPLICIT_REVIEWED_MOUNT --expected-uuid EXPLICIT_REVIEWED_UUID
```

It copies encrypted snapshots and performs a complete repository read check. It never formats or copies the recovery password. Safely unmount, disconnect and store the disk elsewhere; retain the recovery password separately. Record the successful snapshot/check date. No offline recovery protection is claimed until a real destination has passed this copy and verification. Offline snapshots also retain deleted account/media history until old copies are replaced or destroyed; treat them as private encrypted backups.
