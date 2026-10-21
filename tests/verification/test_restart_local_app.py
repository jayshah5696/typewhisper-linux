"""Restart failures must not stop an active dictation or accept another app's status."""
import importlib.util
import json
import os
from pathlib import Path
import socket
import tempfile
import threading
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("restart_local_app", Path(__file__).parents[2] / "scripts/restart_local_app.py")
restart = importlib.util.module_from_spec(spec)
spec.loader.exec_module(restart)


class RestartTests(unittest.TestCase):
    def test_long_fallback_does_not_break_a_short_runtime_socket(self):
        with patch.dict(os.environ, {"XDG_DATA_HOME": "/data/" + "x" * 120, "XDG_RUNTIME_DIR": "/tmp/tw-short"}):
            self.assertEqual([Path("/tmp/tw-short/typewhisper/control.sock")], restart.control_paths())

    def test_no_usable_socket_path_reports_the_runtime_directory_requirement(self):
        with patch.dict(os.environ, {"XDG_DATA_HOME": "/data/" + "x" * 120}, clear=True):
            with self.assertRaisesRegex(RuntimeError, "XDG_RUNTIME_DIR"):
                restart.control_paths()

    def test_busy_app_is_not_signalled(self):
        for state in ("recording", "starting", "transcribing", "injecting", None):
            with self.subTest(state=state), patch.object(restart.os, "pidfd_open") as opened:
                with self.assertRaisesRegex(RuntimeError, "idle"):
                    restart.stop_app(os.getpid(), {"ok": True, "state": state})
                opened.assert_not_called()

    def test_status_pid_must_match_the_socket_peer(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "control.sock"
            with socket.socket(socket.AF_UNIX) as server:
                server.bind(str(path))
                server.listen()

                def reply():
                    client, _ = server.accept()
                    with client:
                        client.recv(4096)
                        client.sendall(json.dumps({"ok": True, "state": "idle", "process_id": os.getpid() + 1}).encode() + b"\n")

                worker = threading.Thread(target=reply)
                worker.start()
                try:
                    with self.assertRaisesRegex(RuntimeError, "peer"):
                        restart.read_status(path)
                finally:
                    worker.join(timeout=5)

    def test_old_status_without_pid_still_uses_kernel_peer_identity(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "control.sock"
            with socket.socket(socket.AF_UNIX) as server:
                server.bind(str(path))
                server.listen()

                def reply():
                    client, _ = server.accept()
                    with client:
                        client.recv(4096)
                        client.sendall(b'{"ok":true,"state":"idle"}\n')

                worker = threading.Thread(target=reply)
                worker.start()
                try:
                    pid, status = restart.read_status(path)
                    self.assertEqual(os.getpid(), pid)
                    self.assertEqual("idle", status["state"])
                finally:
                    worker.join(timeout=5)

    def test_new_process_must_report_expected_version_path_and_plugin_directory(self):
        expected = {"process_id": 123, "app_version": "0.18.0", "app_path": "/install/typewhisper.dll",
                    "plugins_path": "/data/Plugins"}
        restart.validate_started(expected, 123, Path("/install/typewhisper.dll"), "0.18.0", Path("/data/Plugins"))
        for field, wrong in (("process_id", 999), ("app_version", "0.17.0"),
                             ("app_path", "/old/typewhisper.dll"), ("plugins_path", "/dev/Plugins")):
            with self.subTest(field=field), self.assertRaises(RuntimeError):
                restart.validate_started({**expected, field: wrong}, 123, Path("/install/typewhisper.dll"),
                                         "0.18.0", Path("/data/Plugins"))
