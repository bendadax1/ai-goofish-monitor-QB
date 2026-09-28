"""Subprocess-isolated authentication integration; never read repository .env."""

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


class PortableAuthIntegrationTests(unittest.TestCase):
    def _run(self, source, portable=True):
        repository = Path(__file__).resolve().parents[1]
        parent = repository / ".tmp" / "tests" / "portable-auth"
        parent.mkdir(parents=True, exist_ok=True)
        try:
            with tempfile.TemporaryDirectory(dir=parent) as directory:
                root = Path(directory)
                env = {key: os.environ[key] for key in ("SystemRoot", "WINDIR", "PATH") if key in os.environ}
                env.update({"PYTHONPATH": str(repository), "PYTHONDONTWRITEBYTECODE": "1"})
                if portable:
                    env.update({
                        "GOOFISH_PORTABLE_MODE": "portable",
                        "GOOFISH_PORTABLE_PROGRAM_ROOT": str(root / "app"),
                        "GOOFISH_PORTABLE_DATA_ROOT": str(root / "data"),
                        "GOOFISH_PORTABLE_CACHE_ROOT": str(root / "cache"),
                        "GOOFISH_PORTABLE_DATABASE_URL": "postgresql://fixture:unused@127.0.0.1:1/unused",
                        "SECRET_KEY": "test-signing-key-" + "A" * 32,
                        "ENCRYPTION_MASTER_KEY": "test-encryption-key-" + "B" * 32,
                    })
                code = "import sys; sys.path.insert(0, " + repr(str(repository)) + ")\n" + """
import dotenv
from unittest.mock import Mock
# Explicit stubs prevent legacy discovery climbing into the real repository.
dotenv.load_dotenv = Mock(return_value=False)
dotenv.dotenv_values = Mock(return_value={})
""" + source
                result = subprocess.run([sys.executable, "-I", "-B", "-c", code], cwd=root,
                                        env=env, capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stderr)
        finally:
            parent.rmdir()

    def test_portable_uses_revocable_storage_and_current_roles(self):
        self._run("""
from datetime import datetime, timedelta, timezone
from fastapi import Response, Request
import src.web.auth as auth
from pathlib import Path
assert auth.RBAC_CONFIG_FILE == str(Path.cwd() / 'data' / 'state' / 'rbac_config.json')
dotenv.load_dotenv.assert_not_called()
storage = Mock()
user = {'id': 'user-id', 'username': 'fixture', 'role': 'operator', 'is_active': True}
storage.get_user_by_id.return_value = user
storage.get_user_by_username.return_value = {**user, 'password_hash': 'unused'}
auth.get_storage = lambda: storage
auth.verify_password = Mock(return_value=True)
token = auth.create_session_token({'user_id': 'user-id', 'role': 'super_admin'})
storage.get_session_by_token.return_value = {
    'id': 'session-id', 'user_id': 'user-id',
    'expires_at': (datetime.now(timezone.utc) + timedelta(hours=1)).isoformat(),
}
assert token.startswith('p1.')
assert auth.verify_session_token(token)['role'] == 'operator'
user['role'] = 'viewer'
assert auth.verify_session_token(token)['role'] == 'viewer'
request = Request({'type': 'http', 'headers': [(b'cookie', ('session_token=' + token).encode())]})
auth.clear_session_cookie(Response(), request)
storage.delete_session.assert_called_once_with('session-id')
storage.get_user_by_username.return_value['is_active'] = False
assert auth.verify_user('fixture', 'unused') is None
auth.verify_password.assert_not_called()
storage.get_session_by_token.return_value = None
assert auth.verify_session_token(token) is None
assert auth.verify_session_token('old.signed.cookie') is None
""")

    def test_portable_rejects_missing_session_key(self):
        self._run("""
import os
os.environ.pop('SECRET_KEY')
try:
    import src.web.auth
except RuntimeError as error:
    assert '会话密钥' in str(error)
else:
    raise AssertionError('default session key accepted')
""")

    def test_existing_short_passwords_still_authenticate_in_both_modes(self):
        for portable in (False, True):
            with self.subTest(portable=portable):
                self._run("""
import src.web.auth as auth
from src.storage.utils import hash_password
auth.is_multi_user_mode = lambda: True
storage = Mock()
storage.get_user_by_username.return_value = {
    'id': 'fixture-id', 'username': 'admin', 'role': 'super_admin',
    'is_active': True, 'password_hash': hash_password('old123'),
}
auth.get_storage = lambda: storage
assert auth.verify_user('admin', 'old123')['user_id'] == 'fixture-id'
assert auth.verify_user('admin', 'wrong') is None
""", portable=portable)

    def test_storage_rejects_invalid_new_credentials_before_database_access(self):
        self._run("""
from src.storage.postgres_adapter import PostgresAdapter
adapter = object.__new__(PostgresAdapter)
adapter.get_session = Mock(side_effect=AssertionError('invalid input reached database'))
for operation in (
    lambda: adapter.create_user({'username': 'admin', 'password': '1234567'}),
    lambda: adapter.create_user({'username': ' admin', 'password': '12345678'}),
    lambda: adapter.update_user('fixture-id', {'password': '1234567'}),
):
    try:
        operation()
    except ValueError:
        pass
    else:
        raise AssertionError('invalid credentials accepted')
adapter.get_session.assert_not_called()
""")

    def test_actual_web_import_mounts_and_unauthenticated_boundary(self):
        self._run("""
import os
import socket
from pathlib import Path
from unittest.mock import patch
from fastapi.testclient import TestClient
root = Path.cwd()
app_root = root / 'app'
for name in ('static', 'images', 'templates'):
    (app_root / name).mkdir(parents=True)
(app_root / 'static' / 'fixture.txt').write_text('program-static', encoding='utf-8')
(app_root / 'images' / 'fixture.txt').write_text('program-branding', encoding='utf-8')
# Windows asyncio uses a loopback socketpair internally. Allow that transport,
# while rejecting external networking and every PostgreSQL connection.
import psycopg2
original_connect = socket.socket.connect
def local_connect(sock, address):
    if address[0] not in ('127.0.0.1', '::1'):
        raise AssertionError('unexpected external network')
    return original_connect(sock, address)
with patch.object(socket.socket, 'connect', local_connect), patch.object(psycopg2, 'connect', side_effect=AssertionError('unexpected database')):
    import src.web.main as web
    import src.web.auth as auth
    auth.get_storage = Mock(side_effect=AssertionError('unauthenticated request reached database'))
    (root / 'data' / 'assets' / 'avatars' / 'fixture.txt').write_text('user-avatar', encoding='utf-8')
    (root / 'data' / 'results' / 'images' / 'private.txt').write_text('private-result', encoding='utf-8')
    # No context manager: deliberately do not enter the business lifespan here.
    client = TestClient(web.app)
    try:
        assert client.get('/static/fixture.txt').text == 'program-static'
        assert client.get('/images/fixture.txt').text == 'program-branding'
        assert client.get('/static/avatars/fixture.txt').text == 'user-avatar'
        assert client.get('/images/private.txt').status_code == 404
        assert client.get('/api/tasks').status_code == 401
        assert client.get('/', follow_redirects=False).status_code == 302
        assert not web.scheduler.running
        assert not web.fetcher_processes
    finally:
        client.close()
import logging
logging.shutdown()
assert not (app_root / 'logs').exists()
assert not (app_root / 'state').exists()
dotenv.load_dotenv.assert_not_called()
""")

    def test_legacy_signed_session_behavior_remains(self):
        self._run("""
import src.web.auth as auth
assert not auth.PORTABLE_MODE
assert auth.RBAC_CONFIG_FILE == 'state' + __import__('os').sep + 'rbac_config.json'
token = auth.create_session_token({'user_id': 'local_admin', 'username': 'fixture', 'role': 'super_admin'})
assert not token.startswith('p1.')
assert auth.verify_session_token(token)['role'] == 'super_admin'
""", portable=False)
