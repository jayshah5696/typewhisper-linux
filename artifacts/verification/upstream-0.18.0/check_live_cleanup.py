"""Verify live cleanup and clipboard delivery on the private X11 desktop."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop
from isolated_app import IsolatedApp, FakeLlm, write_json
from live_smoke import dictate_once, session_checks
from recovery_smoke import X11Clipboard, private_x11_env

out = Path(__file__).resolve().parent / "live-cleanup-x11"
with HiddenDesktop(out / "desktop") as desktop, FakeLlm(.25) as llm:
    settings, provider = llm.plugin_overrides()
    settings.update(selectedModelId="plugin:com.typewhisper.sherpa-onnx:parakeet-tdt-0.6b",
                    cleanupLevel=2, liveTranscriptionEnabled=True, liveCleanupEnabled=True)
    settings["pluginEnabledState"]["com.typewhisper.filler-words"] = False
    with IsolatedApp(out / "app", configuration="Release", settings=settings,
                     plugin_data=["com.typewhisper.sherpa-onnx"],
                     plugin_settings={"com.typewhisper.openai-compatible": provider},
                     virtual_mic=True, display="none", extra_env=private_x11_env(desktop.app_env())) as app:
        app.load_model("sherpa-onnx", "parakeet-tdt-0.6b")
        session = dictate_once(app, llm, X11Clipboard(app.env),
                               Path("/tmp/typewhisper-benchmark/1462-170145-0012.wav"), tail=4)
        checks = session_checks(session, live_cleanup=True)
        write_json(out / "result.json", {"checks": checks, "session": session})
        assert all(checks.values()), checks
print(json.dumps(checks, indent=2))
