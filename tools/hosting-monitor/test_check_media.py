"""No network access: exercise failure boundaries and bounded retries."""
import http.client
import importlib.util
import json
from pathlib import Path
import ssl
import unittest
from unittest.mock import Mock

spec = importlib.util.spec_from_file_location('monitor', Path(__file__).with_name('check_media.py'))
monitor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(monitor)


class PublicCheckTests(unittest.TestCase):
    def connection(self, body=None, status=200, expiry='Jan  1 00:00:00 2030 GMT', headers=None):
        response = Mock()
        response.status = status
        response.read.return_value = b'{"status":"ready"}' if body is None else body
        values = {'Content-Type': 'application/json; charset=utf-8',
                  'X-Content-Type-Options': 'nosniff', 'Cache-Control': 'private, no-store'}
        values.update(headers or {})
        response.getheader.side_effect = lambda name, default=None: values.get(name, default)
        connection = Mock()
        connection.sock.getpeercert.return_value = {'notAfter': expiry}
        connection.getresponse.return_value = response
        factory = Mock(return_value=connection)
        return connection, factory

    def test_real_context_requires_public_ca_and_hostname_and_sends_no_auth(self):
        connection, factory = self.connection()
        self.assertEqual(monitor.request('/health/ready', connection_factory=factory, now=0), (200, {'status': 'ready'}))
        context = factory.call_args.kwargs['context']
        self.assertEqual(context.verify_mode, ssl.CERT_REQUIRED)
        self.assertTrue(context.check_hostname)
        self.assertEqual(factory.call_args.args, ('media.shlipp.com', 443))
        self.assertNotIn('Authorization', connection.request.call_args.kwargs['headers'])
        connection.close.assert_called_once()

    def test_expiring_certificate_stops_before_http_request(self):
        connection, factory = self.connection(expiry='Jan  1 00:00:00 1970 GMT')
        with self.assertRaises(monitor.CheckFailed):
            monitor.request('/health/ready', connection_factory=factory, now=0)
        connection.request.assert_not_called()
        connection.close.assert_called_once()

    def test_body_encoding_and_security_headers_are_bounded(self):
        for body, headers in ((b'a' * 4097, {}), (b'not-json', {}), (b'[]', {}),
                              (b'{}', {'Content-Type': 'text/html'}),
                              (b'{}', {'Content-Encoding': 'gzip'}),
                              (b'{}', {'Cache-Control': 'public'}),
                              (b'{}', {'X-Content-Type-Options': ''})):
            with self.subTest(headers=headers, length=len(body)):
                connection, factory = self.connection(body, headers=headers)
                with self.assertRaises(monitor.CheckFailed):
                    monitor.request('/health/ready', connection_factory=factory, now=0)
                connection.getresponse.return_value.read.assert_called_once_with(4097)

    def test_a_200_error_or_redirect_is_not_readiness(self):
        for result in ((200, {'status': 'error'}), (302, {'status': 'ready'}), (503, {'status': 'ready'})):
            with self.assertRaises(monitor.CheckFailed):
                monitor.check_once(lambda path: result)

    def test_anonymous_asset_must_be_denied_without_a_real_clip(self):
        calls = []
        def fetch(path):
            calls.append(path)
            if path == '/health/live':
                return 200, {'status': 'ok'}
            if path == '/health/ready':
                return 200, {'status': 'ready'}
            return 403, {'code': 'invalid_link'}
        monitor.check_once(fetch)
        self.assertEqual(calls[-1], monitor.UNSIGNED_ASSET)
        for bad in ((200, {}), (403, {'code': 'unavailable'})):
            with self.assertRaises(monitor.CheckFailed):
                monitor.check_once(lambda path: bad if path == monitor.UNSIGNED_ASSET else fetch(path))

    def test_transient_failure_recovers_silently_with_bounded_retry(self):
        check = Mock(side_effect=[OSError('private details'), None])
        pause = Mock()
        monitor.check_with_retries(3, check=check, pause=pause)
        self.assertEqual(check.call_count, 2)
        pause.assert_called_once_with(5)

    def test_failure_never_echoes_untrusted_exception_content(self):
        check = Mock(side_effect=http.client.HTTPException('secret.example private-credential'))
        pause = Mock()
        with self.assertRaisesRegex(monitor.CheckFailed, 'Failed after 3 attempts') as error:
            monitor.check_with_retries(3, check=check, pause=pause)
        self.assertNotIn('private-credential', str(error.exception))
        self.assertEqual([call.args[0] for call in pause.call_args_list], [5, 10])
        self.assertEqual(check.call_count, 3)


if __name__ == '__main__':
    unittest.main()
