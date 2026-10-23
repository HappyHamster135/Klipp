#!/usr/bin/env python3
"""Anonymous, bounded HTTPS checks. No account, upload, cookie or API key is used."""
import argparse
import datetime as dt
import http.client
import json
import socket
import ssl
import sys
import time

HOST = 'media.shlipp.com'
MAX_BODY = 4096
REQUEST_TIMEOUT = 8
CERTIFICATE_RESERVE_DAYS = 14
UNSIGNED_ASSET = '/v1/assets/00000000-0000-0000-0000-000000000001/00000000-0000-0000-0000-000000000002/video'


class CheckFailed(RuntimeError):
    pass


def request(path, *, connection_factory=http.client.HTTPSConnection, now=None):
    """Use public CA/name validation; redirects are never followed."""
    connection = connection_factory(HOST, 443, timeout=REQUEST_TIMEOUT, context=ssl.create_default_context())
    try:
        connection.connect()
        certificate = connection.sock.getpeercert()
        expiry = ssl.cert_time_to_seconds(certificate['notAfter'])
        current = time.time() if now is None else now
        if expiry - current < CERTIFICATE_RESERVE_DAYS * 86400:
            raise CheckFailed('The public HTTPS certificate expires in fewer than 14 days.')
        connection.request('GET', path, headers={
            'User-Agent': 'Klipp-public-uptime/1.0', 'Accept': 'application/json', 'Connection': 'close'})
        response = connection.getresponse()
        body = response.read(MAX_BODY + 1)
        if len(body) > MAX_BODY:
            raise CheckFailed('A check returned an unexpectedly large response.')
        if response.getheader('Content-Encoding', 'identity') != 'identity':
            raise CheckFailed('A check returned an unexpected content encoding.')
        if response.getheader('Content-Type', '').split(';')[0].strip().lower() != 'application/json':
            raise CheckFailed('A check did not return JSON.')
        if response.getheader('X-Content-Type-Options', '').lower() != 'nosniff':
            raise CheckFailed('The public response is missing its content-type protection.')
        if 'no-store' not in response.getheader('Cache-Control', '').lower():
            raise CheckFailed('A private API response allowed caching.')
        try:
            value = json.loads(body)
        except (ValueError, UnicodeError):
            raise CheckFailed('A check returned invalid JSON.') from None
        if not isinstance(value, dict):
            raise CheckFailed('A check returned an unexpected JSON shape.')
        return response.status, value
    finally:
        connection.close()


def check_once(fetch=request):
    for path, expected in (('/health/live', 'ok'), ('/health/ready', 'ready')):
        status, value = fetch(path)
        if status != 200 or value.get('status') != expected:
            raise CheckFailed('Public Media ' + path.rsplit('/', 1)[1] + ' did not report ready (HTTP ' + str(status) + ').')
    status, value = fetch(UNSIGNED_ASSET)
    if status != 403 or value.get('code') != 'invalid_link':
        raise CheckFailed('The unsigned media access check was not denied as expected (HTTP ' + str(status) + ').')


def check_with_retries(attempts=3, *, check=check_once, pause=time.sleep):
    if attempts not in (1, 2, 3):
        raise ValueError('Use one to three attempts.')
    last = None
    for attempt in range(attempts):
        try:
            check()
            return
        except (CheckFailed, OSError, http.client.HTTPException, KeyError) as error:
            # Never echo arbitrary exception text, response bodies, IPs or TLS headers.
            if isinstance(error, CheckFailed):
                last = str(error)
            elif isinstance(error, ssl.SSLCertVerificationError):
                last = 'The public HTTPS certificate could not be verified.'
            elif isinstance(error, socket.gaierror):
                last = 'The public Media hostname could not be resolved.'
            else:
                last = 'The public Media HTTPS request could not be completed.'
            if attempt + 1 < attempts:
                pause((5, 10)[attempt])
    raise CheckFailed(last + ' Failed after ' + str(attempts) + ' attempts.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--attempts', type=int, choices=(1, 2, 3), default=3)
    args = parser.parse_args()
    try:
        check_with_retries(args.attempts)
    except CheckFailed as error:
        print('Klipp Media check failed: ' + str(error), file=sys.stderr)
        return 1
    # Scheduled healthy checks deliberately produce no notification or status artifact.
    return 0


if __name__ == '__main__':
    sys.exit(main())
