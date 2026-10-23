import datetime as dt
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location('inspect_server', Path(__file__).with_name('inspect_server.py'))
inspector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inspector)


class RecoveryInspectionTests(unittest.TestCase):
    def test_reboot_proof_requires_changed_valid_boot_id(self):
        current = '00000000-0000-0000-0000-000000000001'
        inspector.check_boot(current, None)
        inspector.check_boot(current, '00000000-0000-0000-0000-000000000002')
        for previous in (current, 'not-a-boot-id'):
            with self.assertRaises((ValueError, RuntimeError)):
                inspector.check_boot(current, previous)

    def test_accounts_and_clip_backup_are_both_required(self):
        now = dt.datetime.now(dt.timezone.utc)
        state = {name: now.isoformat() for name in ('successful_at', 'database_successful_at',
            'verified_restore_at', 'database_verified_restore_at')}
        self.assertEqual(inspector.check_backup(state, now), state)
        for name in state:
            old = dict(state, **{name: (now - dt.timedelta(days=9)).isoformat()})
            with self.assertRaises(RuntimeError):
                inspector.check_backup(old, now)
        with self.assertRaises(RuntimeError):
            inspector.check_backup(dict(state, successful_at=(now + dt.timedelta(hours=1)).isoformat()), now)

    def test_lvm_ancestry_resolves_to_physical_disks(self):
        self.assertEqual(inspector.physical_disks([{'name': '/dev/mapper/root', 'type': 'lvm', 'children': [
            {'name': '/dev/nvme0n1p3', 'type': 'part', 'children': [{'name': '/dev/nvme0n1', 'type': 'disk'}]}]}]),
            {str(Path('/dev/nvme0n1').resolve())})

    def test_wrong_uuid_cannot_create_destination_repository(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory).resolve()
            with mock.patch.object(inspector, 'mounted', return_value={'uuid': 'other', 'fstype': 'ext4'}):
                with self.assertRaises(RuntimeError):
                    inspector.inspect_offline(target, 'selected-uuid')
            self.assertEqual(list(target.iterdir()), [])

    def test_existing_internal_disk_is_rejected_without_writes(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory).resolve()
            record = {'uuid': 'external', 'fstype': 'ext4', 'options': 'rw,nodev,nosuid,noexec', 'source': '/dev/sdc1'}
            internal = {'uuid': 'root', 'source': '/dev/mapper/root'}
            with mock.patch.object(inspector, 'mounted', side_effect=[record, internal, internal, internal]), mock.patch.object(
                    inspector, 'disk_ancestors', return_value={'/dev/nvme0n1'}):
                with self.assertRaisesRegex(RuntimeError, 'third physical disk'):
                    inspector.inspect_offline(target, 'external')
            self.assertEqual(list(target.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
