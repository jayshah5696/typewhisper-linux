"""Exercise the retention editor and retain old audio across a real app restart."""
import json
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path.cwd()
sys.path.insert(0, str(ROOT / 'scripts/verification'))
from hidden_desktop import HiddenDesktop
from isolated_app import IsolatedApp, write_json

out = Path(__file__).resolve().parent / 'live'
editor = '''
import gi
gi.require_version('Atspi', '2.0')
from gi.repository import Atspi
def find(node):
    if node is None: return None
    if node.get_name() == 'Keep dictation recordings for recovery (days)':
        editable = node.get_editable_text_iface()
        if editable is not None: return editable
    for i in range(node.get_child_count()):
        hit = find(node.get_child_at_index(i))
        if hit is not None: return hit
    return None
hit = find(Atspi.get_desktop(0))
assert hit is not None, 'Retention editor missing'
assert hit.set_text_contents('0'), 'Retention editor rejected value'
'''
with HiddenDesktop(out / 'desktop') as desktop:
    app = IsolatedApp(out / 'app', configuration='Release', settings={
        'historyRetentionMode': 1, 'dictationRecoveryRetentionDays': 30,
    }, extra_env=desktop.app_env(), display='none', minimized=False)
    data = app.instance / 'data/TypeWhisper'
    audio = data / 'Audio/dictation-old.wav'
    try:
        app.start()
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if desktop.accessibility(click='Dictation')['clicked']:
                break
            time.sleep(.2)
        subprocess.run(['/usr/bin/python3', '-c', editor], env=desktop.env, check=True)
        deadline = time.monotonic() + 10
        while json.loads((data / 'settings.json').read_text())['dictationRecoveryRetentionDays'] != 0:
            if time.monotonic() > deadline: raise AssertionError('Editor did not persist zero')
            time.sleep(.1)
        write_json(out / 'editor.json', desktop.accessibility())
        # Seed an old unreferenced capture after the finite-policy startup has completed.
        audio.parent.mkdir(parents=True, exist_ok=True)
        audio.write_bytes(b'old capture fixture')
        old = time.time() - 730 * 86400
        os.utime(audio, (old, old))
        app.stop()
        assert audio.read_bytes() == b'old capture fixture', 'Shutdown deleted old audio'
        app.overrides['dictationRecoveryRetentionDays'] = 0
        app.start()
        assert audio.read_bytes() == b'old capture fixture', 'Startup deleted old audio'
        assert json.loads((data / 'settings.json').read_text())['dictationRecoveryRetentionDays'] == 0
        app.stop()
        assert audio.read_bytes() == b'old capture fixture', 'Second shutdown deleted old audio'
        write_json(out / 'result.json', {'editor_persisted_zero': True,
            'old_unreferenced_audio_survived_shutdown_startup_shutdown': True})
        print('Retention editor and restart checks passed.')
    finally:
        app.stop()
