"""Native maintenance shutdown control never authorizes business operations."""

import unittest
from unittest.mock import Mock

from fastapi.testclient import TestClient

from src.portable.maintenance import create_app
from tests.test_portable_maintenance import _settings, _AUTHORIZATION


class PortableShutdownTests(unittest.TestCase):
    def test_control_requires_bearer_instance_and_non_browser_caller(self):
        callback = Mock()
        headers = {**_AUTHORIZATION, "X-Goofish-Instance-Id": "portable-test-instance"}
        with TestClient(create_app(_settings(), request_shutdown=callback)) as client:
            self.assertEqual(client.post("/internal/shutdown").status_code, 401)
            self.assertEqual(client.post("/internal/shutdown", headers=_AUTHORIZATION).status_code, 403)
            self.assertEqual(client.post("/internal/shutdown", headers={
                **headers, "Origin": "https://untrusted.example",
            }).status_code, 403)
            callback.assert_not_called()
            self.assertEqual(client.get("/internal/shutdown", headers=headers).status_code, 405)
            first = client.post("/internal/shutdown", headers=headers)
            second = client.post("/internal/shutdown", headers=headers)
            self.assertEqual(first.status_code, 202)
            self.assertEqual(second.status_code, 202)
            self.assertEqual(first.json(), {"status": "shutdown_requested"})
            callback.assert_called_once_with()

    def test_unavailable_or_failed_control_is_not_success_and_is_sanitized(self):
        headers = {**_AUTHORIZATION, "X-Goofish-Instance-Id": "portable-test-instance"}
        with TestClient(create_app(_settings())) as client:
            self.assertEqual(client.post("/internal/shutdown", headers=headers).status_code, 503)
        callback = Mock(side_effect=OSError("secret-that-must-not-appear"))
        with self.assertLogs("src.portable.maintenance", level="ERROR") as logs:
            with TestClient(create_app(_settings(), request_shutdown=callback)) as client:
                response = client.post("/internal/shutdown", headers=headers)
        self.assertEqual(response.status_code, 503)
        self.assertNotIn("secret-that-must-not-appear", response.text + str(logs.output))
