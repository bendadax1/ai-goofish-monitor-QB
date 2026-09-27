"""Session boundaries with no real credentials or database access."""

from datetime import datetime, timedelta, timezone
import unittest
from unittest.mock import Mock

from src.portable.sessions import issue_session, read_session, revoke_session


class PortableSessionTests(unittest.TestCase):
    def setUp(self):
        self.user = {"id": "test-user", "username": "tester", "role": "operator", "is_active": True}
        self.storage = Mock()
        self.storage.get_user_by_id.return_value = self.user
        self.storage.get_session_by_token.return_value = {
            "id": "session-id", "user_id": "test-user",
            "expires_at": (datetime.now(timezone.utc) + timedelta(hours=1)).isoformat(),
        }

    def test_opaque_random_tokens_store_hash_only_and_read_current_role(self):
        first = issue_session(self.storage, "test-user", 3600)
        second = issue_session(self.storage, "test-user", 3600)
        self.assertNotEqual(first, second)
        self.assertNotIn(first, str(self.storage.create_session.call_args_list))
        self.assertEqual(len(self.storage.create_session.call_args.args[0]["token_hash"]), 64)
        self.assertEqual(read_session(self.storage, first)["role"], "operator")
        self.user["role"] = "viewer"
        self.assertEqual(read_session(self.storage, first)["role"], "viewer")
        self.user["is_active"] = False
        self.assertIsNone(read_session(self.storage, first))

    def test_missing_revoked_expired_and_malformed_tokens_fail_closed(self):
        token = issue_session(self.storage, "test-user", 3600)
        for malformed in (None, "", "x" * 10000, "p1.bad", "old.signed.cookie"):
            self.assertIsNone(read_session(self.storage, malformed))
        self.storage.get_session_by_token.assert_not_called()
        self.storage.get_session_by_token.return_value = None
        self.assertIsNone(read_session(self.storage, token))
        for expiry in ("invalid", datetime.now(), datetime.now(timezone.utc) - timedelta(seconds=1)):
            self.storage.get_session_by_token.return_value = {
                "id": "session-id", "user_id": "test-user", "expires_at": expiry,
            }
            self.assertIsNone(read_session(self.storage, token))

    def test_logout_deletes_only_presented_session(self):
        token = issue_session(self.storage, "test-user", 3600)
        revoke_session(self.storage, token)
        self.storage.delete_session.assert_called_once_with("session-id")
        self.storage.delete_user_sessions.assert_not_called()
        self.storage.get_session_by_token.return_value = None
        self.assertIsNone(read_session(self.storage, token))

    def test_failure_is_sanitized_and_never_issues_local_fallback(self):
        self.storage.create_session.side_effect = OSError("sensitive-dsn")
        with self.assertLogs("src.portable.sessions", level="ERROR") as logs:
            with self.assertRaises(RuntimeError) as error:
                issue_session(self.storage, "test-user", 3600)
        self.assertNotIn("sensitive-dsn", str(logs.output) + str(error.exception))
        self.storage.get_session_by_token.side_effect = OSError("sensitive-dsn")
        with self.assertLogs("src.portable.sessions", level="WARNING") as logs:
            self.assertIsNone(read_session(self.storage, "p1." + "A" * 43))
        self.assertNotIn("sensitive-dsn", str(logs.output))
        with self.assertLogs("src.portable.sessions", level="ERROR"):
            with self.assertRaises(RuntimeError):
                revoke_session(self.storage, "p1." + "A" * 43)
