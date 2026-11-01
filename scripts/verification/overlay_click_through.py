"""Probe actual clicks beside an overlay payload in a private desktop, without dictation."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time

sys.path.insert(0, str(Path.cwd() / "scripts/verification"))
from flow_control_smoke import SHAPES, find_node, wait_value
from hidden_desktop import HiddenDesktop
from private_x11_desktop import PrivateX11Desktop
from isolated_app import IsolatedApp, run_manifest, write_json
from recovery_smoke import click, private_x11_env

SENSOR = r'''
import gi,json,sys
from pathlib import Path
gi.require_version("Gtk","3.0")
from gi.repository import Gtk,Gdk
window=Gtk.Window(title="Overlay click-through target")
window.set_decorated(False);window.set_default_size(1000,200);window.move(220,700)
area=Gtk.DrawingArea();area.add_events(Gdk.EventMask.BUTTON_PRESS_MASK)
events=[]
def pressed(widget,event):
 events.append({'x':event.x_root,'y':event.y_root,'button':event.button})
 Path(sys.argv[1]).write_text(json.dumps(events))
 return True
area.connect("button-press-event",pressed);window.add(area)
window.connect("destroy",Gtk.main_quit);window.show_all()
Path(sys.argv[1]).write_text('[]');Gtk.main()
'''


def run(out, kind, payload):
    if out.exists():
        raise ValueError("Use a fresh output directory")
    report = {"desktop": kind, "payload": str(payload), "cases": [],
              "assembly_sha256": hashlib.sha256((payload / "typewhisper.dll").read_bytes()).hexdigest()}
    desktop_type = HiddenDesktop if kind == "mutter" else PrivateX11Desktop
    with desktop_type(out / "desktop") as desktop:
        extra = desktop.app_env() if kind == "x11" else private_x11_env(desktop.app_env())
        extra.update(DBUS_SYSTEM_BUS_ADDRESS=desktop.bus_address, TYPEWHISPER_DISABLE_OVERLAYS="0")
        with IsolatedApp(out / "app", configuration="Release", minimized=False,
                         settings={"showFloatingControl": True}, virtual_mic=True,
                         display="none", extra_env=extra,
                         assembly=payload / "typewhisper.dll", executable=payload / "typewhisper") as app:
            def xdo(*args):
                if kind == "x11": desktop.validate(app.env)
                else: private_x11_env(app.env)
                return subprocess.check_output(["xdotool", *map(str, args)], env=app.env, text=True).strip()

            def pointer(point, press=False):
                if kind == "mutter":
                    desktop.input([{"click" if press else "move": list(point)}], persistent=True)
                else:
                    xdo("mousemove", *point)
                    if press:
                        time.sleep(.1);xdo("mousedown",1);time.sleep(.1);xdo("mouseup",1)

            def geometry():
                ids=xdo("search","--all","--pid",app.process.pid,"--name","^TypeWhisper Overlay$").splitlines()
                assert len(ids)==1,ids
                values=dict(line.split('=',1) for line in xdo("getwindowgeometry","--shell",ids[0]).splitlines())
                return {k:int(values[k]) for k in ("WINDOW","X","Y","WIDTH","HEIGHT")}

            def shapes():
                return json.loads(subprocess.check_output(["python3","-c",SHAPES,str(geometry()["WINDOW"])],env=app.env,text=True))

            def collapse():
                pointer((20,300))
                box=wait_value(geometry,lambda b:b['WIDTH']==36,"collapsed control")
                time.sleep(.3)
                return geometry()

            def expand():
                b=collapse();pointer((b['X']+b['WIDTH']//2,b['Y']+b['HEIGHT']//2))
                try:
                    box=wait_value(geometry,lambda b:b['WIDTH']==140,"expanded control")
                except AssertionError:
                    report['hover_failure_pointer']=xdo('getmouselocation','--shell')
                    report['hover_failure_properties']=subprocess.check_output(['xprop','-id',str(geometry()['WINDOW']),'WM_STATE','_NET_WM_STATE','WM_HINTS'],env=app.env,text=True)
                    desktop.screenshot(out/'hover-failure.png')
                    raise
                time.sleep(.8)
                assert geometry()['WIDTH']==140
                return geometry()

            log=out/'clicks.json'
            receiver=subprocess.Popen(['/usr/bin/python3','-c',SENSOR,str(log)],
                env={**app.env,'GDK_BACKEND':'x11'},stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
            try:
                wait_value(log.exists,bool,"click target")
                target=xdo('search','--all','--pid',receiver.pid,'--name','^Overlay click-through target$').splitlines()[0]
                def raise_target():
                    xdo('windowactivate','--sync',target)
                    wait_value(lambda:xdo('getactivewindow'),lambda v:v==target,"target focus")
                raise_target()
                report['target_properties']=subprocess.check_output(['xprop','-id',target,'_NET_WM_STATE','_NET_WM_WINDOW_TYPE'],env=app.env,text=True)
                def events():
                    try:return json.loads(log.read_text())
                    except json.JSONDecodeError:return []
                def probe(label, point, before):
                    previous=len(events());pointer(point,press=True)
                    try:
                        rows=wait_value(events,lambda r:len(r)>previous,label,timeout=2)
                        hit=rows[-1]
                        passed=abs(hit['x']-point[0])<1 and abs(hit['y']-point[1])<1
                    except AssertionError:
                        passed=False;hit=None
                    case={'name':label,'point':point,'underlying_target_received_click':passed,
                          'received_event':hit,'overlay_before':before}
                    report['cases'].append(case)
                    assert not app.api('/v1/dictation/status')['is_recording'], 'Unexpected recording'
                    print(label, 'PASS' if passed else 'BLOCKED',flush=True)
                def ring(b):
                    x,y,w,h=b['X'],b['Y'],b['WIDTH'],b['HEIGHT']
                    return [('left',(x-1,y+h//2)),('right',(x+w,y+h//2)),
                            ('above',(x+w//2,y-1)),('below',(x+w//2,y+h)),
                            ('top-left',(x-1,y-1)),('top-right',(x+w,y-1)),
                            ('bottom-left',(x-1,y+h)),('bottom-right',(x+w,y+h))]
                idle=collapse();report['idle']=idle;report['idle_input_shapes']=shapes()
                for name,point in ring(idle):
                    probe('idle-'+name,point,collapse())
                expanded=expand();report['expanded']=expanded;report['expanded_input_shapes']=shapes()
                for name,point in ring(expanded):
                    probe('expanded-'+name,point,expand())
                x,y,w,h=expanded['X'],expanded['Y'],expanded['WIDTH'],expanded['HEIGHT']
                old_points=[(x+2,y+h//2),(x+22,y+h//2),(x+w-3,y+h//2),(x+w-23,y+h//2),
                            (x+w//2,y+2),(x+5,y+5),(x+w-6,y+5),(x+w//2,y+4)]
                for number,point in enumerate(old_points):
                    expand();probe('collapsed-old-toolbar-'+str(number),point,collapse())
                # Probe farther above the toolbar as well, where a hover tooltip may appear.
                for gap in (12,24,40):
                    b=expand();probe('expanded-above-'+str(gap),(b['X']+b['WIDTH']//2,b['Y']-gap),b)
                # Disable through the real setting and test the former visible area.
                expand()
                frame=find_node(desktop.accessibility()['tree'],'TypeWhisper Overlay')
                button=find_node(frame.get('children',[]),'Settings…')
                bx,by,bw,bh=button['extents'];pointer((bx+bw//2,by+bh//2),press=True)
                wait_value(lambda:find_node(desktop.accessibility()['tree'],'Show floating control'),bool,'appearance setting')
                click(desktop,'Show floating control')
                wait_value(shapes,lambda rows:not rows[0]['input'],'disabled input region')
                report['disabled_input_shapes']=shapes()
                raise_target();pointer((20,300));time.sleep(.8)
                for number,point in enumerate([(idle['X']+idle['WIDTH']//2,idle['Y']+idle['HEIGHT']//2),*old_points]):
                    probe('disabled-former-widget-'+str(number),point,geometry())
                report['no_recording_started']=not app.api('/v1/dictation/status')['is_recording']
                report['passed']=all(c['underlying_target_received_click'] for c in report['cases'])
                desktop.screenshot(out/'final.png')
            finally:
                write_json(out/'result.json',report)
                write_json(out/'run.json',run_manifest({'kind':'overlay-adjacent-clicks','desktop':kind,'payload':str(payload)}))
                receiver.terminate();receiver.communicate(timeout=10)
    assert report.get('passed'),[c['name'] for c in report['cases'] if not c['underlying_target_received_click']]


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out',type=Path,required=True)
    parser.add_argument('--desktop',choices=('mutter','x11'),default='mutter')
    parser.add_argument('--app-dir',type=Path,default=Path('src/TypeWhisper.Linux/bin/Release/net10.0'))
    args=parser.parse_args();run(args.out.resolve(),args.desktop,args.app_dir.resolve())
