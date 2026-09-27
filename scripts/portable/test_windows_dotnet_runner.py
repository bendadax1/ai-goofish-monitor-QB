"""Pure-Python safety tests for the Windows-only dotnet acceptance runner."""

import importlib.util
import io
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "windows_dotnet_runner", Path(__file__).with_name("windows_dotnet_runner.py")
)
runner = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = runner
SPEC.loader.exec_module(runner)


class Api:
    def __init__(self, callback=lambda *args: 1):
        self.callback = callback
        self.argtypes = None
        self.restype = None

    def __call__(self, *args):
        return self.callback(*args)


class FakeKernel32:
    def __init__(self):
        self.closed = []
        self.terminated = []
        self.process = None
        self.CreateJobObjectW = Api(lambda *_: 44)
        self.SetInformationJobObject = Api(lambda *_: 1)
        self.AssignProcessToJobObject = Api(lambda *_: 1)
        self.TerminateJobObject = Api(self._terminate)
        self.WaitForSingleObject = Api(lambda *_: 0)
        self.CloseHandle = Api(self.closed.append)

    def _terminate(self, handle, code):
        self.terminated.append((handle, code))
        if self.process is not None:
            self.process.timeout = False
        return 1


class FakeProcess:
    pid = 43210
    _handle = 55

    def __init__(self, exit_code=0, timeout=False, stdout=b"", stderr=b""):
        self.exit_code = exit_code
        self.timeout = timeout
        self.finished = False
        self.wait_calls = 0
        self.killed = False
        self.stdout = io.BytesIO(stdout)
        self.stderr = io.BytesIO(stderr)

    def wait(self, timeout=None):
        self.wait_calls += 1
        if self.timeout and timeout is not None:
            raise subprocess.TimeoutExpired("dotnet", timeout)
        self.finished = True
        return self.exit_code

    def poll(self):
        return self.exit_code if self.finished else None

    def kill(self):
        self.killed = True
        self.finished = True


class WindowsDotnetRunnerTests(unittest.TestCase):
    def setUp(self):
        parent = ROOT / ".tmp" / "tests" / "windows-dotnet-runner"
        parent.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=parent)
        self.root = Path(self.temporary.name)
        (self.root / ".tmp").mkdir()
        (self.root / "dotnet.exe").write_bytes(b"fixture")
        (self.root / "acceptance.dll").write_bytes(b"fixture")

    def tearDown(self):
        self.temporary.cleanup()
        parent = self.root.parent
        if parent.exists() and not any(parent.iterdir()):
            parent.rmdir()

    def invoke(self, log_name="run", timeout_seconds=20, expected_marker=None):
        return runner.run_dotnet(
            self.root / "dotnet.exe", self.root / "acceptance.dll", ["--contracts"],
            timeout_seconds, self.root / ".tmp" / log_name, self.root, expected_marker,
        )

    def test_exact_marker_is_matched_as_a_whole_line(self):
        self.assertTrue(runner._contains_exact_marker(
            b"diagnostic noise\nBUNDLE_ACCEPTANCE=PASS\r\n", "BUNDLE_ACCEPTANCE=PASS"
        ))
        self.assertFalse(runner._contains_exact_marker(
            b"prefix BUNDLE_ACCEPTANCE=PASS\n", "BUNDLE_ACCEPTANCE=PASS"
        ))
        self.assertFalse(runner._contains_exact_marker(
            b"BUNDLE_ACCEPTANCE=PASS extra\n", "BUNDLE_ACCEPTANCE=PASS"
        ))

    def test_expected_marker_is_required_and_capture_is_removed(self):
        child = FakeProcess(stdout=b"UI_SMOKE_PASS\r\nraw path=secret\n", stderr=b"secret stderr")
        kernel = FakeKernel32()
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", return_value=0
        ), patch.object(runner, "restore_windows_error_mode"), patch.object(
            runner, "_create_job_object", return_value=(kernel, 44)
        ), patch.object(runner, "_resume_suspended_process"), patch.object(
            runner.subprocess, "Popen", return_value=child
        ):
            self.assertEqual(self.invoke(expected_marker="UI_SMOKE_PASS"), 0)
        self.assertFalse((self.root / ".tmp" / "run").exists())
        self.assertFalse((self.root / ".tmp" / "run" / "dotnet.stderr.log").exists())

        child = FakeProcess(stdout=b"prefix UI_SMOKE_PASS\n")
        kernel = FakeKernel32()
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", return_value=0
        ), patch.object(runner, "restore_windows_error_mode"), patch.object(
            runner, "_create_job_object", return_value=(kernel, 44)
        ), patch.object(runner, "_resume_suspended_process"), patch.object(
            runner.subprocess, "Popen", return_value=child
        ):
            with self.assertRaises(runner.MissingExpectedMarker):
                self.invoke(log_name="missing", expected_marker="UI_SMOKE_PASS")
        self.assertFalse((self.root / ".tmp" / "missing").exists())

    def test_test_id_and_marker_are_restricted_to_fixed_ascii_tokens(self):
        self.assertTrue(runner.TEST_ID_PATTERN.fullmatch("bundle-host-pg-e2e"))
        self.assertFalse(runner.TEST_ID_PATTERN.fullmatch("bundle host"))
        self.assertTrue(runner.MARKER_PATTERN.fullmatch("BUNDLE_ACCEPTANCE=PASS"))
        self.assertFalse(runner.MARKER_PATTERN.fullmatch("C:\\secret"))

    def test_main_emits_only_expected_or_runner_derived_pass_status(self):
        base_arguments = [
            "--dotnet-exe", "dotnet.exe", "--dll", "acceptance.dll",
            "--timeout-seconds", "10", "--log-dir", ".tmp/tests/run",
            "--test-id", "ui-smoke",
        ]
        stdout = io.StringIO()
        with patch.object(runner, "run_dotnet", return_value=0), redirect_stdout(stdout):
            result = runner.main(base_arguments + ["--expected-marker", "UI_SMOKE_PASS"])
        self.assertEqual(result, 0)
        self.assertEqual(stdout.getvalue(), "UI_SMOKE_PASS\n")

        stdout = io.StringIO()
        with patch.object(runner, "run_dotnet", return_value=0), redirect_stdout(stdout):
            result = runner.main(base_arguments)
        self.assertEqual(result, 0)
        self.assertEqual(stdout.getvalue(), "SAFE_TEST_PASS:ui-smoke\n")

        stdout = io.StringIO()
        stderr = io.StringIO()
        with patch.object(runner, "run_dotnet", return_value=7), redirect_stdout(stdout), redirect_stderr(stderr):
            result = runner.main(base_arguments)
        self.assertEqual(result, 7)
        self.assertEqual(stdout.getvalue(), "")
        self.assertEqual(stderr.getvalue(), "SAFE_TEST_FAIL:ui-smoke:EXIT:7:TYPE:DotnetExit\n")

        stderr = io.StringIO()
        with patch.object(runner, "run_dotnet", side_effect=runner.MissingExpectedMarker()), redirect_stderr(stderr):
            result = runner.main(base_arguments + ["--expected-marker", "UI_SMOKE_PASS"])
        self.assertEqual(result, 1)
        self.assertEqual(stderr.getvalue(), "SAFE_TEST_FAIL:ui-smoke:EXIT:1:TYPE:MissingExpectedMarker\n")

    def test_non_windows_rejects_before_starting_child(self):
        with patch.object(runner.os, "name", "posix"), patch.object(
            runner.subprocess, "Popen"
        ) as popen:
            with self.assertRaisesRegex(runner.RunnerError, "仅支持 Windows"):
                self.invoke()
        popen.assert_not_called()

    def test_error_mode_setup_failure_fails_closed_before_child(self):
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", side_effect=runner.RunnerError("mode failed")
        ), patch.object(runner.subprocess, "Popen") as popen:
            with self.assertRaisesRegex(runner.RunnerError, "mode failed"):
                self.invoke()
        popen.assert_not_called()
        self.assertFalse((self.root / ".tmp" / "run").exists())

    def test_exit_code_and_exact_invocation_are_preserved(self):
        child = FakeProcess(exit_code=7)
        kernel = FakeKernel32()
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", return_value=0
        ), patch.object(runner, "restore_windows_error_mode"), patch.object(
            runner, "_create_job_object", return_value=(kernel, 44)
        ), patch.object(runner, "_resume_suspended_process"), patch.object(
            runner.subprocess, "Popen", return_value=child
        ) as popen:
            result = self.invoke()

        self.assertEqual(result, 7)
        self.assertEqual(popen.call_args.args[0], [
            str((self.root / "dotnet.exe").resolve()),
            str((self.root / "acceptance.dll").resolve()),
            "--contracts",
        ])
        self.assertTrue(popen.call_args.kwargs["creationflags"] & runner.CREATE_SUSPENDED)

    def test_failed_cas_emits_only_whitelisted_sanitized_stderr_diagnostic(self):
        child = FakeProcess(
            exit_code=1,
            stderr=(b"secret path=C:\\private\\fixture\n"
                    b"CONFIG_PG_CAS_FAILURE stage=probe category=Io\r\n"
                    b"CONFIG_PG_CAS_FAILURE stage=probe category=secret\n"),
        )
        kernel = FakeKernel32()
        stderr = io.StringIO()
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", return_value=0
        ), patch.object(runner, "restore_windows_error_mode"), patch.object(
            runner, "_create_job_object", return_value=(kernel, 44)
        ), patch.object(runner, "_resume_suspended_process"), patch.object(
            runner.subprocess, "Popen", return_value=child
        ), redirect_stderr(stderr):
            self.assertEqual(self.invoke(), 1)

        self.assertEqual(
            stderr.getvalue(),
            "SAFE_TEST_DIAGNOSTIC:CONFIG_PG_CAS_FAILURE stage=probe category=Io\n",
        )
        self.assertFalse((self.root / ".tmp" / "run").exists())

    def test_cas_failure_marker_accepts_fixture_creation_stage_only_from_allowlist(self):
        self.assertTrue(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"CONFIG_PG_CAS_FAILURE stage=fixture-create category=Security"
        ))
        self.assertFalse(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"CONFIG_PG_CAS_FAILURE stage=fixture-create secret=C:\\private"
        ))
        self.assertTrue(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"CONFIG_PG_CAS_FAILURE stage=web-port-apply category=Io"
        ))
        self.assertTrue(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"CONFIG_PG_CAS_FAILURE stage=web-port-seed category=Io"
        ))

    def test_web_port_host_failure_marker_is_separate_and_redacted(self):
        self.assertTrue(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"WEB_PORT_HOST_FAILURE stage=rollback-ready category=Io"
        ))
        self.assertFalse(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"WEB_PORT_HOST_FAILURE stage=rollback-ready port=58001"
        ))
        self.assertFalse(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"WEB_PORT_HOST_FAILURE stage=secret category=Io"
        ))
        self.assertTrue(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"WEB_PORT_HOST_FIXTURE_RETAINED=YES"
        ))
        self.assertFalse(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(
            b"WEB_PORT_HOST_FIXTURE_RETAINED=C:\\private"
        ))

    def test_probe_checkpoint_is_forwarded_only_when_step_and_exception_are_allowlisted(self):
        child = FakeProcess(
            exit_code=2,
            stderr=(b"dsn=postgresql://user:password@127.0.0.1/db\n"
                    b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity exception=OperationalError\r\n"
                    b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-address exception=RuntimeError\n"
                    b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-port exception=RuntimeError\n"
                    b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity exception=PrivateError\n"
                    b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity sql=SELECT secret\n"
                    b"traceback with key=synthetic\n"),
        )
        kernel = FakeKernel32()
        stderr = io.StringIO()
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", return_value=0
        ), patch.object(runner, "restore_windows_error_mode"), patch.object(
            runner, "_create_job_object", return_value=(kernel, 44)
        ), patch.object(runner, "_resume_suspended_process"), patch.object(
            runner.subprocess, "Popen", return_value=child
        ), redirect_stderr(stderr):
            self.assertEqual(self.invoke(), 2)

        self.assertEqual(
            stderr.getvalue(),
            "SAFE_TEST_DIAGNOSTIC:CONFIG_PG_CAS_PROBE_FAILURE step=db-identity exception=OperationalError\n"
            "SAFE_TEST_DIAGNOSTIC:CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-address exception=RuntimeError\n"
            "SAFE_TEST_DIAGNOSTIC:CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-port exception=RuntimeError\n",
        )
        self.assertFalse((self.root / ".tmp" / "run").exists())

    def test_database_identity_probe_markers_are_fixed_and_reject_extra_fields(self):
        accepted = (
            b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity exception=RuntimeError",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-address exception=RuntimeError",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-port exception=RuntimeError",
        )
        rejected = (
            b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-address exception=RuntimeError address=127.0.0.1",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-port exception=RuntimeError port=5432",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=db-identity-port exception=ValueError raw secret traceback",
        )
        for marker in accepted:
            with self.subTest(marker=marker):
                self.assertIsNotNone(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(marker))
        for marker in rejected:
            with self.subTest(marker=marker):
                self.assertIsNone(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(marker))

    def test_import_probe_markers_accept_only_fixed_steps_and_exception_classes(self):
        accepted = (
            b"CONFIG_PG_CAS_PROBE_FAILURE step=import-fastapi exception=ImportError",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=import-sqlalchemy exception=AttributeError",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=import-storage-adapter exception=SyntaxError",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=import-settings-manager exception=TypeError",
        )
        rejected = (
            b"CONFIG_PG_CAS_PROBE_FAILURE step=import-fastapi exception=PrivateError",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=import-other-module exception=ImportError",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=import-fastapi exception=ImportError detail=C:\\secret",
            b"CONFIG_PG_CAS_PROBE_FAILURE step=import-fastapi exception=ImportError\ntraceback",
        )
        for marker in accepted:
            with self.subTest(marker=marker):
                self.assertIsNotNone(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(marker))
        for marker in rejected:
            with self.subTest(marker=marker):
                self.assertIsNone(runner.CAS_FAILURE_DIAGNOSTIC_PATTERN.fullmatch(marker))

    def test_import_probe_marker_is_forwarded_without_untrusted_output(self):
        child = FakeProcess(
            exit_code=2,
            stderr=(b"CONFIG_PG_CAS_PROBE_FAILURE step=import-storage-adapter exception=ImportError\r\n"
                    b"module=private.module path=C:\\secret\n"),
        )
        kernel = FakeKernel32()
        stderr = io.StringIO()
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", return_value=0
        ), patch.object(runner, "restore_windows_error_mode"), patch.object(
            runner, "_create_job_object", return_value=(kernel, 44)
        ), patch.object(runner, "_resume_suspended_process"), patch.object(
            runner.subprocess, "Popen", return_value=child
        ), redirect_stderr(stderr):
            self.assertEqual(self.invoke(), 2)

        self.assertEqual(
            stderr.getvalue(),
            "SAFE_TEST_DIAGNOSTIC:CONFIG_PG_CAS_PROBE_FAILURE step=import-storage-adapter exception=ImportError\n",
        )

    def test_port_selftest_checkpoint_is_forwarded_only_when_allowlisted(self):
        child = FakeProcess(
            exit_code=1,
            stderr=(b"raw error path=C:\\private\\fixture\n"
                    b"CONFIG_PG_CAS_PORT_SELFTEST_FAILURE step=transaction-commit category=Io\r\n"
                    b"CONFIG_PG_CAS_PORT_SELFTEST_FAILURE step=transaction-commit category=PrivateError\n"),
        )
        kernel = FakeKernel32()
        stderr = io.StringIO()
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", return_value=0
        ), patch.object(runner, "restore_windows_error_mode"), patch.object(
            runner, "_create_job_object", return_value=(kernel, 44)
        ), patch.object(runner, "_resume_suspended_process"), patch.object(
            runner.subprocess, "Popen", return_value=child
        ), redirect_stderr(stderr):
            self.assertEqual(self.invoke(), 1)

        self.assertEqual(
            stderr.getvalue(),
            "SAFE_TEST_DIAGNOSTIC:CONFIG_PG_CAS_PORT_SELFTEST_FAILURE step=transaction-commit category=Io\n",
        )
        self.assertFalse((self.root / ".tmp" / "run").exists())

    def test_timeout_terminates_only_the_owned_job_and_fails(self):
        child = FakeProcess(exit_code=124, timeout=True)
        kernel = FakeKernel32()
        kernel.process = child
        with patch.object(runner.os, "name", "nt"), patch.object(
            runner, "configure_windows_error_mode", return_value=0
        ), patch.object(runner, "restore_windows_error_mode"), patch.object(
            runner, "_create_job_object", return_value=(kernel, 44)
        ), patch.object(runner, "_resume_suspended_process"), patch.object(
            runner.subprocess, "Popen", return_value=child
        ):
            with self.assertRaisesRegex(runner.RunnerError, "专属 Job Object"):
                self.invoke(timeout_seconds=1)
        self.assertEqual(kernel.terminated, [(44, 124)])
        self.assertFalse(child.killed)

    def test_output_limit_terminates_job_and_removes_unredacted_capture(self):
        child = FakeProcess(stdout=b"secret-data")
        kernel = FakeKernel32()
        kernel.process = child
        with patch.object(runner, "MAX_CAPTURE_BYTES_PER_STREAM", 4), patch.object(
            runner.os, "name", "nt"
        ), patch.object(runner, "configure_windows_error_mode", return_value=0), patch.object(
            runner, "restore_windows_error_mode"
        ), patch.object(runner, "_create_job_object", return_value=(kernel, 44)), patch.object(
            runner, "_resume_suspended_process"
        ), patch.object(runner.subprocess, "Popen", return_value=child):
            with self.assertRaisesRegex(runner.RunnerError, "16 MiB 上限"):
                self.invoke()
        self.assertEqual(kernel.terminated, [(44, 125)])
        self.assertFalse((self.root / ".tmp" / "run").exists())

    def test_log_path_must_be_strictly_inside_tmp(self):
        with self.assertRaisesRegex(runner.RunnerError, "必须位于仓库 .tmp"):
            runner.resolve_log_directory(self.root, self.root / "outside")
        with self.assertRaisesRegex(runner.RunnerError, "独立的 .tmp"):
            runner.resolve_log_directory(self.root, self.root / ".tmp")
        self.assertEqual(
            runner.resolve_log_directory(self.root, Path(".tmp") / "bounded"),
            self.root / ".tmp" / "bounded",
        )

    def test_log_path_rejects_link_even_when_target_is_inside_tmp(self):
        target = self.root / ".tmp" / "actual"
        target.mkdir()
        link = self.root / ".tmp" / "alias"
        try:
            link.symlink_to(target, target_is_directory=True)
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"symlink creation unavailable in this environment: {exc}")
        with self.assertRaisesRegex(runner.RunnerError, "重解析点"):
            runner.resolve_log_directory(self.root, link / "run")


if __name__ == "__main__":
    unittest.main()
