#!/usr/bin/env python3
"""Verify real docking, finishing cancellation, and copy recovery in a private desktop."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import time

from flow_control_smoke import EDITOR, find_node, wait_value
from hidden_desktop import HiddenDesktop
from private_x11_desktop import PrivateX11Desktop
from isolated_app import FakeLlm, IsolatedApp, run_manifest, write_json
from recovery_smoke import checkpoint, private_x11_env


def run(out: Path, kind: str):
    if out.exists():
        raise ValueError("Choose a fresh evidence directory")
    report = {"checks": {}, "desktop": kind}
    checks = report["checks"]
    desktop_type = HiddenDesktop if kind == "mutter" else PrivateX11Desktop
    with desktop_type(out / "desktop") as desktop, FakeLlm(5) as llm:
        settings, provider = llm.plugin_overrides()
        settings.update(selectedModelId="plugin:com.typewhisper.needle-whistle:whistle", showFloatingControl=True,
                        cleanupLevel=2, cleanupTimeoutSeconds=0, liveTranscriptionEnabled=False,
                        liveCleanupEnabled=False, autoPaste=False)
        settings.setdefault("pluginEnabledState", {})["com.typewhisper.filler-words"] = False
        extra = desktop.app_env() if kind == "x11" else private_x11_env(desktop.app_env())
        extra.update(DBUS_SYSTEM_BUS_ADDRESS=desktop.bus_address, TYPEWHISPER_DISABLE_OVERLAYS="0")
        private_bin = out / "fault-bin"
        private_bin.mkdir()
        failure = private_bin / "fail-copy"
        real_xclip = shutil.which("xclip")
        if not real_xclip:
            raise FileNotFoundError("xclip is required")
        stub = private_bin / "xclip"
        stub.write_text(f"#!/bin/sh\nif [ -e {shlex.quote(str(failure))} ]; then exit 1; fi\nexec {shlex.quote(real_xclip)} \"$@\"\n")
        stub.chmod(0o700)
        extra["PATH"] = str(private_bin) + os.pathsep + os.environ["PATH"]
        with IsolatedApp(out / "app", configuration="Release", minimized=False, settings=settings,
                         plugin_settings={"com.typewhisper.openai-compatible": provider},
                         plugin_data=["com.typewhisper.needle-whistle"], virtual_mic=True,
                         display="none", extra_env=extra) as app:
            def xdo(*args):
                if kind == "x11": desktop.validate(app.env)
                else: private_x11_env(app.env)
                return subprocess.check_output(["xdotool", *map(str, args)], env=app.env, text=True).strip()

            def pointer(point, action="move"):
                if kind == "mutter":
                    desktop.input([{action: list(point), "wait": .12}], persistent=True)
                else:
                    xdo("mousemove", *point)
                    if action == "click":
                        xdo("mousedown", 1);time.sleep(.1);xdo("mouseup", 1)
                    elif action == "down": xdo("mousedown", 1)
                    elif action == "up": xdo("mouseup", 1)
                    time.sleep(.12)

            def geometry():
                ids = xdo("search", "--all", "--pid", app.process.pid, "--name", "^TypeWhisper Overlay$").splitlines()
                assert len(ids) == 1, ids
                values = dict(line.split("=", 1) for line in xdo("getwindowgeometry", "--shell", ids[0]).splitlines())
                return {key: int(values[key]) for key in ("WINDOW", "X", "Y", "WIDTH", "HEIGHT")}

            def node(name):
                frame = find_node(desktop.accessibility()["tree"], "TypeWhisper Overlay")
                return find_node(frame.get("children", []), name) if frame else None

            def click_control(name):
                control = wait_value(lambda: node(name), bool, name)
                x, y, w, h = control["extents"]
                pointer((x + w // 2, y + h // 2), "click")

            def idle():
                pointer((30, 300))
                wait_value(lambda: node("Show dictation controls"), bool, "idle control")
                return wait_value(geometry, lambda b: (b["WIDTH"], b["HEIGHT"]) in ((36, 18), (18, 36)), "quiet bar")

            def expand():
                b = idle()
                pointer((b["X"] + b["WIDTH"] // 2, b["Y"] + b["HEIGHT"] // 2))
                return wait_value(geometry, lambda b: b["WIDTH"] == 140, "hover controls")

            saved = app.instance / "data/TypeWhisper/settings.json"
            read_settings = lambda: json.loads(saved.read_text())
            def current_session():
                for number in range(1, 12):
                    if app.api(f"/v1/dictation/transcription?sessionId={number}").get("state") == "in_progress":
                        return number
                raise AssertionError("No in-progress private dictation session was reported")
            editor = None
            try:
                drops = [(0, (720, 18)), (2, (18, 450)), (3, (1422, 450)), (1, (720, 882))]
                report["drops"] = []
                for edge, target in drops:
                    expand()
                    primary = node("Start dictation")
                    assert primary
                    x, y, w, h = primary["extents"]
                    start = (x + w // 2, y + h // 2)
                    pointer(start, "down")
                    pointer((start[0] + 20, start[1] + 20))
                    pointer(target)
                    pointer(target, "up")
                    wait_value(read_settings, lambda s: s["overlayPosition"] == edge, "persisted dock edge")
                    b = idle()
                    if edge == 0: correct = b["Y"] == 12 and b["X"] + b["WIDTH"] // 2 == 720
                    elif edge == 1: correct = b["Y"] + b["HEIGHT"] == 888 and b["X"] + b["WIDTH"] // 2 == 720
                    elif edge == 2: correct = b["X"] == 12 and b["Y"] + b["HEIGHT"] // 2 == 450
                    else: correct = b["X"] + b["WIDTH"] == 1428 and b["Y"] + b["HEIGHT"] // 2 == 450
                    assert correct, (edge, b, read_settings())
                    assert not app.api("/v1/dictation/status")["is_recording"]
                    assert not app.capture_sources()
                    s = read_settings()
                    assert s.get("overlayScreenX") == 0 and s.get("overlayScreenY") == 0
                    report["drops"].append({"edge": edge, "geometry": b,
                                            "screen": s.get("overlayScreenName")})
                    checkpoint(desktop, out, "dock-" + str(edge))
                checks["four_real_drags_snap_and_persist"] = True
                checks["drag_release_never_records"] = True
                checks["selected_display_persisted"] = True

                # Reopen the same private settings without rewriting them through a fixture.
                before = {key: read_settings().get(key) for key in ("overlayPosition", "overlayScreenName", "overlayScreenX", "overlayScreenY")}
                app.process.terminate();app.process.wait(timeout=20)
                with (out / "app/restart.log").open("w") as log:
                    app.process = subprocess.Popen([os.environ["DOTNET"], str(Path.cwd() / "src/TypeWhisper.Linux/bin/Release/net10.0/typewhisper.dll")],
                        env=app.env, cwd=app.instance / "home", stdin=subprocess.DEVNULL,
                        stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                app.status = app._wait_ready()
                after = {key: read_settings().get(key) for key in before}
                checks["dock_survives_app_restart"] = before == after
                assert checks["dock_survives_app_restart"]
                idle()
                report["load"] = app.load_model("needle-whistle", "whistle")
                editor_text = out / "editor.txt"
                editor = subprocess.Popen(["/usr/bin/python3", "-c", EDITOR, str(editor_text)],
                    env={**app.env, "GDK_BACKEND": "x11"}, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
                wait_value(editor_text.exists, bool, "editor")
                editor_id = xdo("search", "--all", "--pid", editor.pid, "--name", "^Flow control verification editor$").splitlines()[0]
                xdo("windowactivate", "--sync", editor_id)
                wait_value(lambda: xdo("getactivewindow"), lambda v: v == editor_id, "editor focus")
                expand();click_control("Start dictation")
                wait_value(lambda: app.api("/v1/dictation/status"), lambda s: s["is_recording"], "capture")
                assert app.capture_sources() == [app.mic.source]
                first_session = current_session()
                app.mic.play(Path("tests/fixtures/librispeech-1462-170145-0012.wav").resolve())
                click_control("Stop dictation")
                wait_value(lambda: node("Finishing…"), bool, "compact finishing state")
                checks["finishing_turns_microphone_off"] = not app.capture_sources()
                checks["finishing_keeps_cancel_enabled"] = node("Cancel") is not None
                report["cleanup_requests_before_cancel"] = wait_value(lambda: len(llm.requests), lambda count: count > 0,
                                                                      "cleanup request before cancel")
                checkpoint(desktop, out, "finishing")
                click_control("Cancel")
                result = wait_value(lambda: app.api(f"/v1/dictation/transcription?sessionId={first_session}"),
                                    lambda r: r.get("state") == "canceled", "canceled finishing")
                report["canceled"] = result
                checks["cancel_finishing_does_not_insert"] = editor_text.read_text() == ""
                checks["cancel_finishing_preserves_focus"] = xdo("getactivewindow") == editor_id

                # Genuine clipboard-tool failure, followed by real clipboard recovery.
                llm.delay_seconds = .1
                failure.touch()
                expand();click_control("Start dictation")
                wait_value(lambda: app.api("/v1/dictation/status"), lambda s: s["is_recording"], "second capture")
                second_session = current_session()
                app.mic.play(Path("tests/fixtures/librispeech-1462-170145-0012.wav").resolve())
                click_control("Stop dictation")
                wait_value(lambda: node("Copy text"), bool, "copy recovery action", timeout=30)
                result = wait_value(lambda: app.api(f"/v1/dictation/transcription?sessionId={second_session}"),
                                    lambda r: r.get("performance") is not None, "failed delivery result")
                report["failed_delivery"] = result
                checks["failed_delivery_does_not_claim_success"] = result.get("performance", {}).get("delivery_outcome") not in ("Pasted", "Typed", "CopiedToClipboard")
                checkpoint(desktop, out, "copy-recovery")
                failure.unlink()
                click_control("Copy text")
                copied = wait_value(lambda: subprocess.check_output([real_xclip, "-selection", "clipboard", "-out"], env=app.env, text=True),
                                    lambda text: text.strip() == result["text"].strip(), "exact recovered text")
                report["copied_text"] = copied
                checks["recovery_copies_exact_current_text"] = bool(copied.strip())
                checks["copy_recovery_never_pastes"] = editor_text.read_text() == ""
                checks["copy_recovery_preserves_focus"] = xdo("getactivewindow") == editor_id
                report["acceleration"] = app.api("/v1/status").get("acceleration")
                checks["cpu_execution"] = report["acceleration"]["active_backend"] == "cpu"
                assert all(checks.values()), checks
            except BaseException:
                checkpoint(desktop, out, "failure")
                raise
            finally:
                write_json(out / "result.json", report)
                write_json(out / "run.json", run_manifest({"kind": "quiet-dock-and-recovery", "desktop": kind}))
                if editor:
                    editor.terminate();editor.communicate(timeout=10)
    print(json.dumps(checks, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--desktop", choices=("mutter", "x11"), default="mutter")
    args = parser.parse_args()
    run(args.out.resolve(), args.desktop)
