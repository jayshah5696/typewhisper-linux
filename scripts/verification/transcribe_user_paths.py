#!/usr/bin/env python3
"""Verify GGUF downloads and live dictation in a private desktop using public audio only.

Requires xclip, the HiddenDesktop dependencies, and an AMD/Intel/NVIDIA hardware Vulkan
adapter. Downloads the catalog-pinned 296 MB Moonshine streaming medium model into the
throwaway instance. The user's app, data, microphone, and clipboard are never used.
"""
import argparse
import hashlib
import json
import os
import signal
import subprocess
import sys
import time
import wave
import urllib.request
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts/verification'))
import hidden_desktop
from hidden_desktop import HiddenDesktop, all_names
from isolated_app import IsolatedApp, write_json, run_manifest
from recovery_smoke import X11Clipboard, private_x11_env
from live_smoke import summarize_timings
# Avalonia's empty search TextBox exposes a null AT-SPI text value. Keep the node
# in this check's tree instead of dropping the entire subtree on that TypeError.
hidden_desktop._ATSPI_SCRIPT = hidden_desktop._ATSPI_SCRIPT.replace(
    '            item["text"] = text.get_text(0, -1)',
    '            try: item["text"] = text.get_text(0, -1)\n            except TypeError: pass')
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--out', type=Path, required=True)
parser.add_argument('--app-dir', type=Path, required=True, help='Final published or installed scratch payload')
args = parser.parse_args()
OUT = args.out.resolve()
if OUT.exists():
    parser.error('Choose a new output directory to preserve previous evidence')
OUT.mkdir(parents=True)
PLUGIN = 'com.typewhisper.transcribe-cpp'
MODEL = 'handy-computer/moonshine-streaming-medium-gguf'
CATALOG = json.loads((ROOT / 'plugins/TypeWhisper.Plugin.TranscribeCpp/Models/catalog.json').read_text())
META = next(m for m in CATALOG['models'] if m['id'] == MODEL)
APP = args.app_dir.resolve()
JFK_URL = 'https://raw.githubusercontent.com/ggml-org/whisper.cpp/v1.7.6/samples/jfk.wav'
JFK_SHA256 = '59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e'
FIXTURE = ROOT / 'tests/fixtures/librispeech-1462-170145-0012.wav'
EDIT = r'''
import gi, sys
gi.require_version('Atspi', '2.0')
from gi.repository import Atspi
def find(n):
    if n is None: return None
    try:
        if n.get_name() == sys.argv[1] and 'EditableText' in n.get_interfaces(): return n
        for i in range(n.get_child_count()):
            hit = find(n.get_child_at_index(i))
            if hit: return hit
    except Exception: pass
n = find(Atspi.get_desktop(0))
assert n is not None
assert n.get_editable_text_iface().set_text_contents(sys.argv[2])
'''
def wait(test, timeout=120):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        value = test()
        if value: return value
        time.sleep(.2)
    raise AssertionError('Timed out waiting for ' + repr(test))
def click(desktop, name):
    result = desktop.accessibility(click=name)['clicked']
    assert result and result['ok'], (name, result)
    time.sleep(.3)
def names(desktop): return all_names(desktop.accessibility()['tree'])
def tree(desktop, label): write_json(OUT / (label + '.json'), desktop.accessibility()['tree'])
def workers(app):
    children = set()
    for task in Path(f'/proc/{app.process.pid}/task').glob('*/children'):
        try: children.update(task.read_text().split())
        except FileNotFoundError: pass
    found = []
    for pid in children:
        try:
            if '--transcription-worker' in Path(f'/proc/{pid}/cmdline').read_text(): found.append(int(pid))
        except FileNotFoundError: pass
    return sorted(found)
def acceleration(app): return app.api('/v1/status').get('acceleration') or {}
def set_backend(desktop, app, label, backend):
    click(desktop, 'Local model acceleration')
    click(desktop, label)
    click(desktop, 'Local model acceleration')
    wait(lambda: acceleration(app).get('active_backend') == backend, 120)
def dictate(desktop, app, audio, label, fault=False):
    watcher = desktop.watch_texts('TypeWhisper Overlay', seconds=180)
    try:
        time.sleep(.5)
        started = app.api('/v1/dictation/start', {})
        time.sleep(.4)
        sources = app.capture_sources()
        assert sources == [app.mic.source], sources
        start_at = time.time()
        player = subprocess.Popen(['paplay', '--device=' + app.mic.name, str(audio)])
        time.sleep(2)
        if fault:
            owned = workers(app)
            assert len(owned) == 1, owned
            os.kill(owned[0], signal.SIGKILL)
            write_json(OUT / (label + '-killed-owned-worker.json'), owned)
        else:
            tree(desktop, label + '-recording')
            desktop.screenshot(OUT / (label + '-recording.png'))
        player.wait(timeout=145)
        time.sleep(2)
        stop_at = time.time()
        stopped = app.api('/v1/dictation/stop', {}, timeout=240)
        result_path = '/v1/dictation/transcription?sessionId=' + str(started['session_id'])
        result = wait(lambda: (r if (r := app.api(result_path)).get('performance') else None), 120)
        clipboard = X11Clipboard(app.env).read().decode()
        assert result['state'] == 'ready', result
        assert clipboard.strip() == result['text'].strip() and clipboard.strip(), (clipboard, result)
        assert 'bent his face over her hair' in clipboard.lower(), clipboard
        assert result['performance']['delivery_outcome'] == 'CopiedToClipboard', result
        status = acceleration(app)
        assert status.get('active_backend') == ('cpu' if fault or label.startswith('cpu') else 'vulkan'), status
    finally:
        watcher.terminate()
        stdout, stderr = watcher.communicate(timeout=10)
        samples = [json.loads(line) for line in stdout.splitlines() if line.strip()]
        write_json(OUT / (label + '-overlay-samples.json'), samples)
    previews = sorted({text for s in samples if start_at <= s['t'] < stop_at for text in s['texts']
        if any(word in text.lower() for word in ['bent', 'hair', 'face', 'country'])})
    if not fault: assert previews, samples
    if audio.name == 'public-110s.wav':
        assert 'ask not what your country can do for you' in clipboard.lower(), clipboard
        assert 'ask what you can do for your country' in clipboard.lower(), clipboard
    record = {'started': started, 'stopped': stopped, 'result': result, 'capture_sources': sources,
        'clipboard_text': clipboard, 'clipboard_matches': True, 'visible_previews_before_stop': previews,
        'acceleration': status, 'fault_injected_own_worker': fault}
    write_json(OUT / (label + '.json'), record)
    print(label, json.dumps({'text': clipboard, 'visible_previews': previews, 'backend': status, 'performance': result['performance']}), flush=True)
    return record

def run():
    # Distinct public utterances exercise rollover. The existing host dictation policy
    # intentionally collapses adjacent identical phrases, so repeating the same fixture
    # cannot prove the later utterance survived final normalization.
    with wave.open(str(FIXTURE), 'rb') as source:
        pcm = source.readframes(source.getnframes()); params = source.getparams()
    assert params.framerate == 16000 and params.sampwidth == 2 and params.nchannels == 1
    with urllib.request.urlopen(JFK_URL, timeout=30) as response:
        second_bytes = response.read(1024 * 1024)
    assert hashlib.sha256(second_bytes).hexdigest() == JFK_SHA256
    (OUT / 'jfk-public.wav').write_bytes(second_bytes)
    write_json(OUT / 'public-audio.json', {'jfk_url': JFK_URL, 'jfk_sha256': JFK_SHA256,
        'jfk_reference': 'And so, my fellow Americans, ask not what your country can do for you. Ask what you can do for your country.',
        'librispeech_fixture': str(FIXTURE), 'attribution': str(ROOT / 'tests/fixtures/README.md')})
    with wave.open(str(OUT / 'jfk-public.wav'), 'rb') as second:
        assert second.getparams()[:3] == params[:3]
        second_pcm = second.readframes(second.getnframes())
    long_pcm = bytearray(110 * 16000 * 2)
    long_pcm[:len(pcm)] = pcm
    long_pcm[86*16000*2:86*16000*2+len(second_pcm)] = second_pcm
    long_audio = OUT / 'public-110s.wav'
    with wave.open(str(long_audio), 'wb') as target:
        target.setparams(params); target.writeframes(long_pcm)
    with HiddenDesktop(OUT / 'desktop') as desktop:
        extra = {**private_x11_env(desktop.app_env()), 'DBUS_SYSTEM_BUS_ADDRESS': desktop.bus_address}
        with IsolatedApp(OUT / ('app-' + str(time.time_ns())), configuration='Release', executable=APP / 'typewhisper',
            assembly=APP / 'typewhisper.dll', minimized=False, virtual_mic=True, display='none', extra_env=extra,
            settings={'pluginEnabledState': {PLUGIN: True, 'com.typewhisper.filler-words': False},
                'localModelAcceleration': 'cpu', 'liveTranscriptionEnabled': True,
                'liveTranscriptionStreamingEnabled': True, 'liveCleanupEnabled': False,
                'cleanupLevel': 0, 'saveToHistoryEnabled': True, 'diagnosticLogEnabled': True}) as app:
            wait(lambda: MODEL in json.dumps(app.api('/v1/models')), 60)
            click(desktop, 'Dictation')
            subprocess.run([desktop._gi_python(), '-c', EDIT, 'Search models or providers', MODEL], env=desktop.env, check=True)
            time.sleep(.5)
            base = app.instance / 'data/TypeWhisper'
            settings_before = (base / 'settings.json').read_bytes()
            model_dir = base / 'PluginData' / PLUGIN / 'models'
            partial = model_dir / (META['filename'] + '.partial')
            ready = model_dir / (META['filename'] + '.ready.json')
            click(desktop, 'Download')
            wait(lambda: partial.exists() and partial.stat().st_size > 1024*1024, 180)
            click(desktop, 'Cancel download')
            wait(lambda: 'Download' in names(desktop), 30)
            assert partial.exists() and 0 < partial.stat().st_size < META['size'], partial
            assert not ready.exists()
            canceled_size = partial.stat().st_size
            prefix = hashlib.sha256(partial.read_bytes()[:1024*1024]).hexdigest()
            tree(desktop, 'download-canceled')
            click(desktop, 'Download')
            wait(lambda: ready.exists(), 600)
            owned = model_dir / META['filename']
            assert owned.stat().st_size == META['size']
            assert hashlib.file_digest(owned.open('rb'), 'sha256').hexdigest() == META['sha256']
            assert hashlib.sha256(owned.read_bytes()[:1024*1024]).hexdigest() == prefix
            assert not partial.exists()
            assert not json.loads(ready.read_text())['isExternal']
            assert (base / 'settings.json').read_bytes() == settings_before
            wait(lambda: 'Use model' in names(desktop), 30)
            tree(desktop, 'download-complete')
            desktop.screenshot(OUT / 'download-complete.png')
            click(desktop, 'Use model')
            wait(lambda: acceleration(app).get('active_backend') == 'cpu', 120)
            selected = json.loads((base / 'settings.json').read_text())['selectedModelId']
            assert selected == 'plugin:' + PLUGIN + ':' + MODEL, selected
            records = [dictate(desktop, app, FIXTURE, 'cpu-stream')]
            set_backend(desktop, app, 'Vulkan', 'vulkan')
            records.append(dictate(desktop, app, FIXTURE, 'vulkan-stream'))
            records.append(dictate(desktop, app, long_audio, 'vulkan-long-stream'))
            set_backend(desktop, app, 'CPU', 'cpu')
            records.append(dictate(desktop, app, FIXTURE, 'cpu-worker-loss-fallback', fault=True))
            history = json.loads((base / 'Data/history.json').read_text())
            assert len(history) == 4, history
            assert 'libtranscribe' not in Path(f'/proc/{app.process.pid}/maps').read_text()
            diagnostics = base / 'Logs/diagnostics.jsonl'
            events = [json.loads(line) for line in diagnostics.read_text().splitlines() if line.strip()]
            finalizes = [event['data']['outcome'] for event in events if event['event'] == 'streaming.finalize.end']
            assert finalizes == ['ok', 'ok', 'ok', 'failed'], finalizes
            write_json(OUT / 'result.json', {'download_canceled_partial_bytes': canceled_size,
                'streaming_finalize_outcomes': finalizes,
                'download_retry_exact_sha256': META['sha256'], 'download_did_not_select_or_save': True,
                'actual_selection_saved': selected, 'dictations': records, 'history_count': len(history),
                'host_native_uninitialized': True, 'timings_ms': summarize_timings(records)})
    write_json(OUT / 'run.json', run_manifest({'kind': 'transcribe-final-distributed-user-paths', 'app': str(APP)}))
    print('PASS final-payload model download/cancel/retry/select, CPU/Vulkan virtual-mic streaming, 110s rollover, worker-loss full-recording fallback and private clipboard read-back', flush=True)
if __name__ == '__main__': run()
