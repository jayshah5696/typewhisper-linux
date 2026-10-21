"""Exercise the microphone-test controls using public audio and a private virtual source."""
import json
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from hidden_desktop import HiddenDesktop
from isolated_app import IsolatedApp, write_json
from recovery_smoke import click, private_x11_env, wait_for, checkpoint

out = Path(__file__).resolve().parent / "microphone-ui"
with HiddenDesktop(out / "desktop") as desktop:
    with IsolatedApp(out / "app", configuration="Release", virtual_mic=True,
                     display="none", extra_env=private_x11_env(desktop.app_env()),
                     minimized=False, settings={"interfaceLanguage": "en"}) as app:
        wait_for(desktop, lambda names: "Dictation" in names, "main window")
        click(desktop, "Dictation")
        wait_for(desktop, lambda names: "Test microphone" in names, "microphone test button")
        click(desktop, "Test microphone")
        wait_for(desktop, lambda names: "Stop test" in names, "running microphone test")
        sources = app.capture_sources()
        assert sources == ["tw_verify_fixture.monitor"], sources
        player = threading.Thread(target=app.mic.play,
                                  args=(Path("/tmp/typewhisper-benchmark/1462-170145-0012.wav"),))
        player.start()
        wait_for(desktop, lambda names: "Audio signal received" in names, "public audio detected")
        checkpoint(desktop, out, "signal")
        click(desktop, "Stop test")
        player.join(timeout=10)
        wait_for(desktop, lambda names: "Test microphone" in names, "stopped microphone test")
        checkpoint(desktop, out, "stopped")
        audio = app.instance / "data/TypeWhisper/Audio"
        recordings = list(audio.rglob("*.wav")) if audio.exists() else []
        assert not recordings, recordings
        write_json(out / "result.json", {"source": sources, "signal_received": True,
                                         "stopped_via_button": True, "recordings_created": 0})
print("Microphone test detected public audio, stopped via its button, and saved no recording.")
