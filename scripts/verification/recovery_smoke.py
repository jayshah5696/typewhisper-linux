#!/usr/bin/env python3
"""Verify dictation recovery from public audio through the real UI and a private X11 clipboard."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hidden_desktop
import isolated_app
import live_smoke


def private_x11_env(desktop_env: dict[str, str]) -> dict[str, str]:
    """Require the hidden compositor's authority file before choosing an X11 clipboard."""
    authority = Path(desktop_env.get("XAUTHORITY", ""))
    display = desktop_env.get("DISPLAY", "")
    if (not re.fullmatch(r":\d+", display) or authority.parent.parent != Path("/tmp")
            or not authority.parent.name.startswith("tw-verify-")
            or not authority.name.startswith(".mutter-Xwaylandauth")):
        raise ValueError("A private HiddenDesktop display and authority file are required")
    return {**desktop_env, "WAYLAND_DISPLAY": "", "XDG_SESSION_TYPE": "x11"}


class X11Clipboard:
    def __init__(self, env: dict[str, str]):
        self.env = env

    def read(self) -> bytes:
        result = subprocess.run(["xclip", "-selection", "clipboard", "-out"], env=self.env,
                                capture_output=True, check=True, timeout=5)
        return result.stdout

    def write(self, text: str) -> None:
        subprocess.run(["xclip", "-selection", "clipboard", "-in"], input=text.encode(), env=self.env,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True, timeout=5)


def wait_for(desktop: hidden_desktop.HiddenDesktop, predicate, label: str, timeout: float = 15) -> list[dict]:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        tree = desktop.accessibility()["tree"]
        if predicate(hidden_desktop.all_names(tree)):
            return tree
        time.sleep(.1)
    checkpoint(desktop, desktop.workdir.parent, "failure")
    raise AssertionError(f"UI did not reach {label}")


def click(desktop: hidden_desktop.HiddenDesktop, name: str) -> None:
    result = desktop.accessibility(click=name)["clicked"]
    if not result or not result.get("ok"):
        raise AssertionError(f"Could not activate {name!r}")


def checkpoint(desktop: hidden_desktop.HiddenDesktop, out: Path, name: str) -> None:
    isolated_app.write_json(out / f"{name}.json", desktop.accessibility()["tree"])
    desktop.screenshot(out / f"{name}.png")


def wait_request(llm: isolated_app.FakeLlm, count: int) -> None:
    deadline = time.monotonic() + 5
    while len(llm.requests) < count:
        if time.monotonic() > deadline:
            raise AssertionError("Cleanup request did not reach the fake provider")
        time.sleep(.05)


def run(args: argparse.Namespace) -> dict:
    if not args.audio.is_file():
        raise FileNotFoundError(f"Public audio is missing: {args.audio}. Run just prepare-corpus first.")
    if not shutil.which("xclip") or not shutil.which("xdotool"):
        raise RuntimeError("xclip and xdotool are required on PATH for the private X11 desktop")
    subprocess.run(["xclip", "-version"], check=True, capture_output=True, timeout=5)
    subprocess.run([str(args.atspi_python), "-c", "import gi; gi.require_version('Atspi', '2.0')"],
                   check=True, capture_output=True, timeout=5)
    # The existing desktop helper invokes python3 for system GI bindings.
    os.environ["PATH"] = str(args.atspi_python.parent) + os.pathsep + os.environ["PATH"]
    report: dict = {"checks": {}, "clipboard": "private X11", "audio": str(args.audio), "provider": "fake echo"}
    checks = report["checks"]
    with hidden_desktop.HiddenDesktop(args.out / "desktop") as desktop, isolated_app.FakeLlm(.1) as llm:
        extra = private_x11_env(desktop.app_env())
        settings, provider = llm.plugin_overrides()
        settings.update({"selectedModelId": f"plugin:{args.plugin}:{args.model}", "cleanupLevel": 2,
                         "cleanupTimeoutSeconds": 1, "saveToHistoryEnabled": True,
                         "liveTranscriptionEnabled": False, "liveCleanupEnabled": False,
                         "pluginEnabledState": {**settings["pluginEnabledState"], "com.typewhisper.filler-words": False}})
        with isolated_app.IsolatedApp(args.out / "app", configuration="Release", settings=settings,
                                     plugin_data=[args.plugin], plugin_settings={"com.typewhisper.openai-compatible": provider},
                                     virtual_mic=True, display="none", extra_env=extra, minimized=False) as app:
            clipboard = X11Clipboard(app.env)
            report["model_load"] = app.load_model(args.engine, args.model)
            report["dictation"] = live_smoke.dictate_once(app, llm, clipboard, args.audio, tail=.3)
            checks.update(live_smoke.session_checks(report["dictation"], live_cleanup=False))
            if not all(checks.values()):
                raise AssertionError(f"Audio-to-clipboard dictation failed: {checks}")
            raw = report["dictation"]["result"]["raw_text"]
            final = report["dictation"]["result"]["text"]
            backend = app.api("/v1/status").get("acceleration") or {}
            report["acceleration"] = backend
            checks["active_cpu_backend"] = backend.get("active_backend") == "cpu"
            history = app.instance / "data/TypeWhisper/Data/history.json"
            deadline = time.monotonic() + 5
            while not history.is_file():
                if time.monotonic() > deadline:
                    raise AssertionError("The actual dictation was not saved to History")
                time.sleep(.1)
            history_before = history.read_bytes()
            records = json.loads(history_before)
            checks["actual_history_saved"] = any(r["RawText"] == raw and r["FinalText"] == final for r in records)
            click(desktop, "History")
            click(desktop, "Recent transcriptions")
            wait_for(desktop, lambda names: "Copy original" in names, "recovery palette")
            checkpoint(desktop, args.out, "palette-original")
            click(desktop, "Copy original")
            wait_for(desktop, lambda names: "Copied recent transcription to clipboard." in names, "original copied")
            checks["original_clipboard_matches"] = clipboard.read().decode() == raw
            sentinel = "TypeWhisper private recovery verification clipboard"
            clipboard.write(sentinel)
            before_request = len(llm.requests)
            click(desktop, "Clean up again")
            wait_for(desktop, lambda names: "New result ready. Review it, then copy it." in names, "new cleanup preview")
            checks["one_cleanup_request"] = len(llm.requests) == before_request + 1
            checks["preview_did_not_copy"] = clipboard.read().decode() == sentinel
            prompt = llm.requests[-1]["prompt"]
            expected = json.loads(prompt[prompt.index("{"):])["dictated_text"]
            checkpoint(desktop, args.out, "palette-cleaned")
            click(desktop, "Copy new result")
            wait_for(desktop, lambda names: "Copied recent transcription to clipboard." in names, "new result copied")
            checks["new_clipboard_matches"] = clipboard.read().decode() == expected
            report["recovered_text"] = expected
            # Closing while the provider works must never copy its eventual response.
            clipboard.write(sentinel)
            llm.delay_seconds = 2
            before_request = len(llm.requests)
            click(desktop, "Clean up again")
            wait_request(llm, before_request + 1)
            # Use the window manager's normal close request; Escape is also covered
            # by the rendered-window input test, without depending on nested keyboard focus.
            subprocess.run(["xdotool", "search", "--name", "^Recent transcriptions$", "windowclose"],
                           env=app.env, check=True, capture_output=True, timeout=5)
            wait_for(desktop, lambda names: "Copy original" not in names, "closed recovery palette")
            time.sleep(2.1)
            checks["cancel_kept_clipboard"] = clipboard.read().decode() == sentinel
            click(desktop, "Recent transcriptions")
            wait_for(desktop, lambda names: "Copy original" in names, "reopened recovery palette")
            before_request = len(llm.requests)
            click(desktop, "Clean up again")
            wait_request(llm, before_request + 1)
            reason = "AI cleanup timed out. Used basic cleanup."
            wait_for(desktop, lambda names: reason in names and "Cleaning up..." not in names, "cleanup timeout")
            checks["timeout_visible"] = True
            checks["timeout_did_not_copy"] = clipboard.read().decode() == sentinel
            checkpoint(desktop, args.out, "palette-timeout")
            checks["history_unchanged"] = history.read_bytes() == history_before
            result = app.api("/v1/text/process", {"text": "uh preserve the previous result"})
            report["timeout_api"] = result
            checks["api_actual_level"] = result["cleanup_level"] == "Light" and result["cleanup_outcome"] == {
                "requested_level": "Medium", "applied_level": "Light", "fallback_reason": "TimedOut"}
            checks["no_rejected_llm_routes"] = not llm.rejected_paths
            report["timings_ms"] = live_smoke.summarize_timings([report["dictation"]])
    if not all(checks.values()):
        raise AssertionError(f"Failed checks: {[name for name, passed in checks.items() if not passed]}")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--audio", type=Path, default=Path("/tmp/typewhisper-benchmark/1462-170145-0012.wav"))
    parser.add_argument("--plugin", default="com.typewhisper.sherpa-onnx")
    parser.add_argument("--engine", default="sherpa-onnx")
    parser.add_argument("--model", default="parakeet-tdt-0.6b")
    parser.add_argument("--atspi-python", type=Path, default=Path("/usr/bin/python3"))
    args = parser.parse_args()
    args.out = args.out.resolve()
    if args.out.exists():
        parser.error("Choose a new output directory to preserve previous evidence")
    args.out.mkdir(parents=True)
    try:
        report = run(args)
        isolated_app.write_json(args.out / "recovery-smoke.json", report)
        print(json.dumps({"checks": report["checks"], "timings_ms": report["timings_ms"], "evidence": str(args.out)}, indent=2))
        return 0
    except Exception as error:
        isolated_app.write_json(args.out / "failure.json", {"error": str(error), "traceback": traceback.format_exc()})
        traceback.print_exc()
        return 1
    finally:
        isolated_app.write_json(args.out / "run.json", isolated_app.run_manifest({
            "kind": "dictation-recovery-e2e", "configuration": "Release", "clipboard": "private X11",
            "audio": str(args.audio), "plugin": args.plugin, "model": args.model, "provider": "fake echo",
            "xclip": shutil.which("xclip"), "xdotool": shutil.which("xdotool"), "atspi_python": str(args.atspi_python)}))


if __name__ == "__main__":
    sys.exit(main())
