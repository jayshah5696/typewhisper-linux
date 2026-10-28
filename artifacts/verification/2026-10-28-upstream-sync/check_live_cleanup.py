"""Exercise speculative cleanup and clipboard delivery in a private X11 desktop."""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop
from isolated_app import FakeLlm, IsolatedApp, run_manifest, write_json
from live_smoke import dictate_once, session_checks
from recovery_smoke import X11Clipboard, private_x11_env

parser = argparse.ArgumentParser()
parser.add_argument("--out", type=Path, required=True)
out = parser.parse_args().out.resolve()
with HiddenDesktop(out / "desktop") as desktop, FakeLlm(.25) as llm:
    settings, provider = llm.plugin_overrides()
    settings.update(selectedModelId="plugin:com.typewhisper.sherpa-onnx:parakeet-tdt-0.6b",
                    cleanupLevel=2, liveTranscriptionEnabled=True, liveCleanupEnabled=True)
    settings["pluginEnabledState"]["com.typewhisper.filler-words"] = False
    with IsolatedApp(out / "app", configuration="Release", settings=settings,
                     plugin_settings={"com.typewhisper.openai-compatible": provider},
                     plugin_data=["com.typewhisper.sherpa-onnx"], virtual_mic=True,
                     display="none", extra_env=private_x11_env(desktop.app_env())) as app:
        app.load_model("sherpa-onnx", "parakeet-tdt-0.6b")
        session = dictate_once(app, llm, X11Clipboard(app.env),
                               Path("tests/fixtures/librispeech-1462-170145-0012.wav").resolve(), tail=4)
        checks = session_checks(session, live_cleanup=True)
        acceleration = app.api("/v1/status")["acceleration"]
        checks["active_cpu_backend"] = acceleration["active_backend"] == "cpu"
        checks["only_virtual_microphone"] = session["capture_sources"] == [app.mic.source]
        result = {"checks": checks, "session": session, "acceleration": acceleration}
        write_json(out / "result.json", result)
        assert all(checks.values()), checks
write_json(out / "run.json", run_manifest({"kind": "live-cleanup-private-x11", "provider": "local echo fixture"}))
print(json.dumps(checks, indent=2))
