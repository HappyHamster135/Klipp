#!/usr/bin/env python3
"""Read-only Linux recovery/pre-reboot checks. Never reboots, mounts or formats a disk."""
import argparse
import datetime as dt
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import socket
import ssl
import stat
import subprocess
import uuid

UNITS = ('klipp-media.service', 'caddy.service', 'tailscaled.service',
         'klipp-backup.timer', 'klipp-backup-verify.timer', 'klipp-health.timer')


def run(*arguments):
    return subprocess.run(arguments, check=True, capture_output=True, text=True, timeout=20).stdout.strip()


def protected_json(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd, 'rb') as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022 or info.st_size > 1024**2:
            raise RuntimeError('A protected recovery checkpoint has unexpected ownership, mode or type.')
        return json.load(stream)


def check_backup(state, now=None):
    now = now or dt.datetime.now(dt.timezone.utc)
    for field, limit in (('successful_at', 48 * 3600), ('database_successful_at', 48 * 3600),
                         ('verified_restore_at', 8 * 86400), ('database_verified_restore_at', 8 * 86400)):
        stamp = dt.datetime.fromisoformat(state[field].replace('Z', '+00:00'))
        if stamp.tzinfo is None or not -120 <= (now - stamp).total_seconds() <= limit:
            raise RuntimeError('A required clip/accounts backup or isolated restore check is stale.')
    return {field: state[field] for field in ('successful_at', 'database_successful_at',
                                            'verified_restore_at', 'database_verified_restore_at')}


def mounted(path):
    records = json.loads(run('/usr/bin/findmnt', '--json', '--mountpoint', str(path), '--output', 'UUID,SOURCE,FSTYPE,OPTIONS'))['filesystems']
    if len(records) != 1:
        raise RuntimeError('An exact, single mounted filesystem is required.')
    return records[0]


def physical_disks(items):
    result = set()
    for item in items:
        if item['type'] == 'disk':
            result.add(str(Path(item['name']).resolve()))
        result |= physical_disks(item.get('children', []))
    return result


def disk_ancestors(source):
    return physical_disks(json.loads(run('/usr/bin/lsblk', '--json', '--inverse', '--paths', '--output', 'NAME,TYPE', source))['blockdevices'])


def inspect_offline(target, expected_uuid):
    if not target.is_absolute() or target.is_symlink() or target.resolve(strict=True) != target or target in (
            Path('/'), Path('/srv/klipp/media'), Path('/srv/klipp/backups')):
        raise RuntimeError('Choose the existing mountpoint of a separately reviewed third disk.')
    record = mounted(target)
    if record['uuid'] != expected_uuid or record['fstype'] not in ('ext4', 'xfs', 'btrfs'):
        raise RuntimeError('The offline filesystem differs from the selected UUID or supported Linux type.')
    if not {'rw', 'nodev', 'nosuid', 'noexec'} <= set(record['options'].split(',')):
        raise RuntimeError('The offline mount requires rw,nodev,nosuid,noexec.')
    disks = disk_ancestors(record['source'])
    protected = set()
    for path in ('/', '/srv/klipp/media', '/srv/klipp/backups'):
        existing = mounted(path)
        if record['uuid'] == existing['uuid']:
            raise RuntimeError('An internal server/backup filesystem cannot be the offline destination.')
        protected |= disk_ancestors(existing['source'])
    if len(disks) != 1 or disks & protected:
        raise RuntimeError('Offline recovery needs a third physical disk, independent of both installed SSDs.')
    info = target.stat()
    if info.st_uid != 0 or info.st_mode & 0o022:
        raise RuntimeError('The reviewed mountpoint must be root-owned and not writable by other users.')
    repository = target / 'klipp-encrypted-backup'
    if repository.exists() or repository.is_symlink():
        info = repository.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o077:
            raise RuntimeError('The existing destination repository must be a private root-owned directory.')
    source = Path('/srv/klipp/backups/repository')
    source_bytes = 0
    for directory, subdirs, files in os.walk(source, followlinks=False):
        for name in subdirs + files:
            info = (Path(directory) / name).lstat()
            if info.st_uid != 0 or stat.S_ISLNK(info.st_mode):
                raise RuntimeError('An encrypted source repository entry needs administrator review.')
            if name in files:
                if not stat.S_ISREG(info.st_mode):
                    raise RuntimeError('The encrypted source repository contains a non-file entry.')
                source_bytes += info.st_size
    reserve = max(1024**3, source_bytes // 10)
    free = shutil.disk_usage(target).free
    if not source_bytes or free < source_bytes + reserve:
        raise RuntimeError('The third disk lacks capacity for the encrypted repository plus its safety reserve.')
    return {'mount': str(target), 'filesystem_uuid': expected_uuid, 'separate_physical_disk': True,
            'encrypted_source_bytes': source_bytes, 'destination_free_bytes': free,
            'safety_reserve_bytes': reserve, 'repository_already_exists': repository.exists(),
            'read_only_preflight': True, 'copy_performed': False}


def check_boot(current, previous):
    if str(uuid.UUID(current)) != current:
        raise RuntimeError('The current Linux boot identifier is malformed.')
    if previous is not None and (str(uuid.UUID(previous)) != previous or previous == current):
        raise RuntimeError('A different, valid pre-reboot boot identifier is required to verify a real reboot.')


def trusted_ready():
    with socket.create_connection(('127.0.0.1', 443), timeout=5) as connection:
        with ssl.create_default_context().wrap_socket(connection, server_hostname='media.shlipp.com') as secured:
            secured.sendall(b'GET /health/ready HTTP/1.1\r\nHost: media.shlipp.com\r\nConnection: close\r\n\r\n')
            response = http.client.HTTPResponse(secured)
            response.begin()
            value = response.read(4097)
            if response.status != 200 or len(value) > 4096 or json.loads(value).get('status') != 'ready':
                raise RuntimeError('CA-verified local HTTPS did not return ready JSON.')


def running_proxy():
    manifest = protected_json('/etc/klipp/caddy-build.json')
    expected = manifest['sha256']
    if not re.fullmatch('[0-9a-f]{64}', expected):
        raise RuntimeError('The protected proxy identity is invalid.')
    pid = run('/usr/bin/systemctl', 'show', '-p', 'MainPID', '--value', 'caddy.service')
    if not pid.isdecimal() or int(pid) < 2:
        raise RuntimeError('Caddy has no running process.')
    # /proc/PID/exe is an intentional kernel link, unlike a user-owned input path.
    with open('/proc/' + pid + '/exe', 'rb') as stream:
        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
            raise RuntimeError('Caddy process identity is not an executable file.')
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if digest != expected or pid != run('/usr/bin/systemctl', 'show', '-p', 'MainPID', '--value', 'caddy.service'):
        raise RuntimeError('The running proxy differs from its root-reviewed build identity.')
    for field in ('ExecStart', 'ExecReload'):
        selection = run('/usr/bin/systemctl', 'show', '-p', field, '--value', 'caddy.service')
        if '/usr/local/libexec/klipp-caddy' not in selection or '/usr/bin/caddy' in selection:
            raise RuntimeError('Caddy no longer selects the reviewed executable at boot/reload.')
    return {'pid': int(pid), 'sha256': digest, 'version': manifest['version_output']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--previous-boot-id', help='Optional recorded pre-reboot ID; must differ from the current boot.')
    parser.add_argument('--offline-mount', type=Path)
    parser.add_argument('--expected-uuid')
    args = parser.parse_args()
    if bool(args.offline_mount) != bool(args.expected_uuid):
        parser.error('Offline preflight requires both --offline-mount and --expected-uuid.')
    if os.geteuid() != 0:
        raise RuntimeError('A full read-only inspection needs root to read protected recovery and process identities.')
    boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    check_boot(boot, args.previous_boot_id)
    run('/usr/local/libexec/klipp-verify-media-mount')
    configuration = protected_json('/etc/klipp/backup/config.json')
    backup = mounted('/srv/klipp/backups')
    if backup['uuid'] != configuration['backup_uuid'] or backup['fstype'] != 'ext4' or not {
            'rw', 'nodev', 'nosuid', 'noexec'} <= set(backup['options'].split(',')):
        raise RuntimeError('The separate online backup SSD is not mounted with its reviewed identity/options.')
    state = check_backup(protected_json('/var/lib/klipp-backup/status.json'))
    for unit in UNITS:
        if run('/usr/bin/systemctl', 'is-active', unit) != 'active' or run('/usr/bin/systemctl', 'is-enabled', unit) != 'enabled':
            raise RuntimeError('A required service or backup/health timer is not active and enabled.')
    for unit in ('klipp-backup.service', 'klipp-backup-verify.service'):
        if run('/usr/bin/systemctl', 'show', '-p', 'MemorySwapMax', '--value', unit) != '0':
            raise RuntimeError('The backup/restore no-swap protection is not effective.')
    trusted_ready()
    result = {'boot_id': boot, 'uptime_seconds': int(float(Path('/proc/uptime').read_text().split()[0])),
              'different_boot_verified': args.previous_boot_id is not None, 'media_mount_verified': True,
              'backup_mount_verified': True, 'services_and_timers_verified': list(UNITS),
              'backup_staging_swap_disabled': True, 'local_https_ready': True,
              'proxy': running_proxy(), 'backup': state, 'read_only': True,
              'external_wan_verified': False, 'reboot_performed': False, 'offline_copy_performed': False}
    if args.offline_mount:
        result['offline'] = inspect_offline(args.offline_mount, args.expected_uuid)
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Read-only inspection stopped: ' + str(error))
        raise SystemExit(1)
