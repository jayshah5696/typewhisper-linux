"""Check Needle file transcription and virtual-mic dictation in a private X11 session.

Run from the repository root. Pass --audio with the prepared public LibriSpeech
1462-170145-0012 WAV. Requires the project hidden-desktop dependencies and xclip.
"""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop
from isolated_app import IsolatedApp, run_manifest, write_json
from live_smoke import dictate_once, session_checks
from recovery_smoke import X11Clipboard, private_x11_env

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--audio", type=Path, required=True)
parser.add_argument("--out", type=Path, required=True)
parser.add_argument("--app-dir", type=Path, help="Verify this installed or published app payload")
parser.add_argument("--desktop-file", type=Path, help="Launch the executable named by this installer-owned desktop entry")
args = parser.parse_args()
out = args.out.resolve()
executable = args.app_dir.resolve() / "typewhisper" if args.app_dir else None
if args.desktop_file:
    launch = next(line[5:] for line in args.desktop_file.read_text().splitlines() if line.startswith("Exec="))
    if not executable or launch != str(executable):
        raise RuntimeError(f"Desktop entry does not launch the expected installed payload: {launch}")
    executable = Path(launch)
plugin = "com.typewhisper.needle-whistle"
settings = {
    "selectedModelId": f"plugin:{plugin}:whistle",
    "cleanupLevel": 0,
    "liveTranscriptionEnabled": False,
    "liveCleanupEnabled": False,
    "pluginEnabledState": {"com.typewhisper.filler-words": False},
}
with HiddenDesktop(out / "desktop") as desktop:
    with IsolatedApp(out / "app", configuration="Release", settings=settings,
                     plugin_data=[plugin], virtual_mic=True, display="none",
                     extra_env=private_x11_env(desktop.app_env()),
                     assembly=args.app_dir / "typewhisper.dll" if args.app_dir else None,
                     executable=executable) as app:
        load = app.load_model("needle-whistle", "whistle")
        file_result = app.api("/v1/transcribe/local-file", {
            "path": str(args.audio.resolve()), "engine": "needle-whistle", "model": "whistle",
            "language": "en", "applyCorrections": False,
        }, timeout=180)
        session = dictate_once(app, None, X11Clipboard(app.env), args.audio.resolve(), tail=.3)
        acceleration = app.api("/v1/status").get("acceleration") or {}
        checks = session_checks(session, live_cleanup=False)
        checks["file_engine_is_needle"] = file_result.get("engine") == "needle-whistle" and file_result.get("model") == "whistle"
        checks["dictation_engine_is_needle"] = session["result"].get("engine") == "needle-whistle" and session["result"].get("model") == "whistle"
        checks["file_transcription_has_expected_speech"] = "bent his face over her hair" in file_result.get("text", "").lower()
        checks["dictation_has_expected_speech"] = "bent his face over her hair" in session["result"].get("text", "").lower()
        checks["active_cpu_backend"] = acceleration.get("active_backend") == "cpu"
        result = {"checks": checks, "load": load, "file": file_result,
                  "dictation": session, "acceleration": acceleration}
        write_json(out / "result.json", result)
        write_json(out / "run.json", run_manifest({"kind": "needle-regression", "engine": "needle-whistle",
                   "model": "whistle", "audio": str(args.audio.resolve()), "backend": acceleration.get("active_backend")}))
        assert all(checks.values()), checks
print(json.dumps(result, indent=2))
