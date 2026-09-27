"""Shutdown admission and detached-background regression tests."""

import asyncio
import unittest
from unittest.mock import AsyncMock, Mock

from src.portable.web_runtime import (
    _ShutdownCoordinator, _DrainRequestTrackingMiddleware,
    register_portable_background_task, portable_background_tasks_active,
)


class PortableDrainRaceTests(unittest.IsolatedAsyncioTestCase):
    def coordinator(self, **overrides):
        values = dict(timeout_seconds=1, processes_active=lambda: False,
            pause_scheduler=AsyncMock(return_value=True), resume_scheduler=AsyncMock(),
            dispose_storage=Mock(), request_server_exit=Mock())
        values.update(overrides)
        return _ShutdownCoordinator(**values)

    async def test_unfinished_detached_monitor_holds_drain_after_process_exit(self):
        release = asyncio.Event()
        monitor = register_portable_background_task(asyncio.create_task(release.wait()))
        callback = Mock()
        coordinator = self.coordinator(processes_active=portable_background_tasks_active, request_server_exit=callback)
        await coordinator.begin()
        await asyncio.sleep(.07)
        callback.assert_not_called()
        self.assertEqual(coordinator.phase, "Draining")
        release.set()
        await monitor
        await asyncio.sleep(.07)
        callback.assert_called_once()
        self.assertEqual(coordinator.phase, "Stopped")
        self.assertIsNone(coordinator.admit_and_track("/api/tasks"))

    async def test_cancel_then_simultaneous_restart_waits_for_restoration(self):
        restore_started, restore_release = asyncio.Event(), asyncio.Event()
        async def restore(_):
            restore_started.set()
            await restore_release.wait()
        coordinator = self.coordinator(processes_active=lambda: True, resume_scheduler=restore)
        await coordinator.begin()
        cancellation = asyncio.create_task(coordinator.cancel())
        await restore_started.wait()
        self.assertEqual(coordinator.phase, "Cancelling")
        self.assertIsNone(coordinator.admit_and_track("/setup"))
        restart = asyncio.create_task(coordinator.begin())
        restore_release.set()
        await cancellation
        await restart
        self.assertEqual(coordinator.phase, "Draining")
        await coordinator.cancel()

    async def test_restoration_error_is_reported_not_hidden_as_idle(self):
        coordinator = self.coordinator(processes_active=lambda: True,
            resume_scheduler=AsyncMock(side_effect=RuntimeError("fixture")))
        await coordinator.begin()
        with self.assertLogs("src.portable.web_runtime", level="ERROR"):
            snapshot = await coordinator.cancel()
        self.assertEqual(snapshot["state"], "TimedOut")
        self.assertEqual(snapshot["detail"], "shutdown_resume_failed")

    async def test_asgi_admission_tracks_entire_accepted_response(self):
        entered, release = asyncio.Event(), asyncio.Event()
        async def application(scope, receive, send):
            entered.set()
            await release.wait()
        callback = Mock()
        coordinator = self.coordinator(request_server_exit=callback)
        tracked = _DrainRequestTrackingMiddleware(application, coordinator=coordinator)
        task = asyncio.create_task(tracked({"type":"http", "path":"/api/tasks"}, AsyncMock(), AsyncMock()))
        await entered.wait()
        await coordinator.begin()
        await asyncio.sleep(.06)
        callback.assert_not_called()
        sent = AsyncMock()
        await tracked({"type":"http", "path":"/api/tasks"}, AsyncMock(), sent)
        self.assertEqual(sent.call_args_list[0].args[0]["status"], 503)
        release.set()
        await task
        await asyncio.sleep(.06)
        callback.assert_called_once()
