"""Nestmates QA launcher.

Starts real game copies (never the Steam install), runs an in-game bot scenario, collects logs and
prints one line: RESULT: PASS / FAIL / TIMEOUT.

Safety guards:
  * refuses to run if a game process is already running (a player may be playing);
  * exports the player's PlayerPrefs registry key before launch and restores it afterwards, deleting
    any value that was added;
  * each copy gets its own sandbox save dir (-coop-sandbox) and its own Unity log (-logFile);
  * windowed 960x540, 30 fps cap (plugin side);
  * only processes this script started are stopped (by PID).

Usage (run with python -I):
  coopqa.py setup                      create the hardlinked second copy and deploy the plugin
  coopqa.py deploy                     copy freshly built plugin DLLs into every copy
  coopqa.py smoke                      launch one copy, wait for the probe line, stop
  coopqa.py run SCENARIO [--timeout S] host + guest over TCP loopback with the bot scenario
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.dirname(ROOT)                         # F:\IronNestCoopDev
COPIES = {'a': os.path.join(WORK, 'game'), 'b': os.path.join(WORK, 'game-b')}
EXE = 'Iron Nest Heavy Turret Simulator.exe'
PLUGIN_OUT = os.path.join(ROOT, 'src', 'IronNestCoop', 'bin', 'Release', 'net6.0')
PREFS_KEY = r'HKCU\Software\Iron Nest'
PREFS_VALUES_KEY = r'Software\Iron Nest\Iron Nest: Heavy Turret Simulator'
RUNS = os.path.join(WORK, 'qa-runs')
PORT = 27731


def log(m):
    print(m, flush=True)


# ---------------------------------------------------------------- protecting the real install

def game_running():
    out = subprocess.run(['tasklist', '/FO', 'CSV', '/NH'], capture_output=True, text=True).stdout
    return [l for l in out.splitlines() if EXE.lower() in l.lower()]


def read_prefs():
    import winreg
    vals = {}
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, PREFS_VALUES_KEY) as k:
            i = 0
            while True:
                try:
                    name, data, typ = winreg.EnumValue(k, i)
                except OSError:
                    break
                vals[name] = (data, typ)
                i += 1
    except FileNotFoundError:
        pass
    return vals


def restore_prefs(saved):
    import winreg
    now = read_prefs()
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, PREFS_VALUES_KEY) as k:
        for name in now:
            if name not in saved:
                winreg.DeleteValue(k, name)
        for name, (data, typ) in saved.items():
            if now.get(name) != (data, typ):
                winreg.SetValueEx(k, name, 0, typ, data)
    after = read_prefs()
    if after != saved:
        raise SystemExit('FATAL: owner PlayerPrefs could not be restored exactly; backup in local/')
    return sum(1 for n in set(now) | set(saved) if now.get(n) != saved.get(n))


# ---------------------------------------------------------------- copies

SKIP_LINK = ('.log',)


def setup():
    a, b = COPIES['a'], COPIES['b']
    if not os.path.isdir(a):
        raise SystemExit(f'missing test copy {a}')
    n = 0
    for root, dirs, files in os.walk(a):
        rel = os.path.relpath(root, a)
        dst = os.path.join(b, rel)
        os.makedirs(dst, exist_ok=True)
        for f in files:
            if f.endswith(SKIP_LINK):
                continue
            s, d = os.path.join(root, f), os.path.join(dst, f)
            if os.path.exists(d):
                continue
            if 'plugins' in rel.split(os.sep) or 'config' in rel.split(os.sep):
                shutil.copy2(s, d)        # files we rewrite must not be shared
            else:
                os.link(s, d)
            n += 1
    log(f'copy b ready at {b} ({n} new files, hardlinked)')
    deploy()


PLUGIN_DIR = 'Nestmates'                 # BepInEx/plugins/<this> in each test copy
DLLS = ('Nestmates.dll', 'Nestmates.Net.dll')
OLD_PLUGIN_DIR = 'IronNestCoop'           # the folder of the builds before the rename: removed from test copies


def deploy():
    for c in COPIES.values():
        old = os.path.join(c, 'BepInEx', 'plugins', OLD_PLUGIN_DIR)
        if os.path.isdir(c) and os.path.isdir(old):
            shutil.rmtree(old)            # it would load next to the new one (BepInEx refuses that pair)
            log(f'removed the old plugin folder {old}')
    for name in DLLS:
        src = os.path.join(PLUGIN_OUT, name)
        if not os.path.exists(src):
            raise SystemExit(f'build first: {src} missing')
        for c in COPIES.values():
            if not os.path.isdir(c):
                continue
            d = os.path.join(c, 'BepInEx', 'plugins', PLUGIN_DIR)
            os.makedirs(d, exist_ok=True)
            tmp = os.path.join(d, name)
            if os.path.exists(tmp):
                os.remove(tmp)            # break any hardlink before writing
            shutil.copy2(src, tmp)
    log('plugin deployed to test copies')


def deploy_if_stale():
    """Runs always test the current build: redeploy when a copy's plugin differs from the build output."""
    for c in COPIES.values():
        if os.path.isdir(os.path.join(c, 'BepInEx', 'plugins', OLD_PLUGIN_DIR)):
            deploy()
            return
    for name in DLLS:
        src = os.path.join(PLUGIN_OUT, name)
        for c in COPIES.values():
            d = os.path.join(c, 'BepInEx', 'plugins', PLUGIN_DIR, name)
            if os.path.isdir(c) and os.path.exists(src) and (not os.path.exists(d) or open(src, 'rb').read() != open(d, 'rb').read()):
                deploy()
                return


# ---------------------------------------------------------------- launching

class Copy:
    def __init__(self, slot, run_dir, extra):
        self.slot = slot
        self.dir = COPIES[slot]
        self.run_dir = os.path.join(run_dir, slot)
        os.makedirs(self.run_dir, exist_ok=True)
        self.coop_log = os.path.join(self.run_dir, 'coop.log')
        self.unity_log = os.path.join(self.run_dir, 'player.log')
        self.result = os.path.join(self.run_dir, 'result.json')
        self.extra = extra
        self.proc = None

    def start(self):
        for f in ('LogOutput.log', 'ErrorLog.log'):
            p = os.path.join(self.dir, 'BepInEx', f)
            if os.path.exists(p):
                os.remove(p)
        with open(os.path.join(self.dir, 'steam_appid.txt'), 'w') as fh:
            fh.write('2950790')   # stops Steam from restarting the real Steam install
        args = [os.path.join(self.dir, EXE),
                '-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '540',
                '-logFile', self.unity_log,
                f'-coop-sandbox={os.path.join(self.run_dir, "saves")}',
                f'-coop-log={self.coop_log}',
                f'-coop-result={self.result}',
                '-coop-verbose'] + self.extra
        self.proc = subprocess.Popen(args, cwd=self.dir)
        log(f'[{self.slot}] started pid {self.proc.pid}')

    def alive(self):
        return self.proc is not None and self.proc.poll() is None

    def stop(self):
        if not self.alive():
            return
        subprocess.run(['taskkill', '/PID', str(self.proc.pid), '/T', '/F'], capture_output=True)
        try:
            self.proc.wait(10)
        except subprocess.TimeoutExpired:
            pass
        log(f'[{self.slot}] stopped')

    def text(self):
        try:
            with open(self.coop_log, encoding='utf-8', errors='replace') as fh:
                return fh.read()
        except FileNotFoundError:
            return ''

    def read_result(self):
        try:
            with open(self.result, encoding='utf-8') as fh:
                return json.load(fh)
        except (FileNotFoundError, json.JSONDecodeError):
            return None


def guarded(fn):
    running = game_running()
    if running:
        raise SystemExit('refusing to start: a game process is already running:\n' + '\n'.join(running))
    saved = read_prefs()
    with open(os.path.join(WORK, 'local', 'prefs-before-last-qa.json'), 'w') as fh:
        json.dump({k: [repr(v[0]), v[1]] for k, v in saved.items()}, fh)
    copies = []
    try:
        return fn(copies)
    finally:
        for c in copies:
            c.stop()
        changed = restore_prefs(saved)
        log(f'owner PlayerPrefs restored ({changed} values put back)')


def new_run_dir(name):
    d = os.path.join(RUNS, time.strftime('%Y%m%d-%H%M%S') + '-' + name)
    os.makedirs(d, exist_ok=True)
    return d


EXTRA = []
HOST_EXTRA = []


def smoke(timeout):
    def body(copies):
        c = Copy('a', new_run_dir('smoke'), EXTRA)
        copies.append(c)
        c.start()
        t0 = time.time()
        while time.time() - t0 < timeout:
            if not c.alive():
                log('RESULT: FAIL (game exited)')
                return 1
            if c.text().count('probe frame=') >= 1 and ('-coop-uiprobe' not in EXTRA or 'uiprobe FAILED' in c.text()):
                log('RESULT: PASS (plugin loaded and ticking)')
                return 0
            time.sleep(1)
        log('RESULT: TIMEOUT')
        return 3
    return guarded(body)


def run(scenario, timeout):
    def body(copies):
        d = new_run_dir(scenario)
        host = Copy('a', d, [f'-coop-host={PORT}', '-coop-id=1', '-coop-name=HostBot', f'-coop-bot={scenario}:host'] + HOST_EXTRA)
        guest = Copy('b', d, [f'-coop-join=127.0.0.1:{PORT}', '-coop-id=2', '-coop-name=GuestBot', f'-coop-bot={scenario}:guest'] + EXTRA)
        copies += [host, guest]
        host.start()
        time.sleep(3)
        guest.start()
        t0 = time.time()
        while time.time() - t0 < timeout:
            for c in copies:
                if not c.alive() and c.read_result() is None:
                    log(f'RESULT: FAIL ({c.slot} exited without a result)')
                    return 1
            rs = [c.read_result() for c in copies]
            if all(r is not None for r in rs):
                ok = all(r.get('pass') for r in rs)
                for c, r in zip(copies, rs):
                    log(f'[{c.slot}] {"PASS" if r.get("pass") else "FAIL"}: {r.get("summary")}')
                    for f in r.get('failures', []):
                        log(f'    - {f}')
                log(f'logs: {d}')
                log(f'RESULT: {"PASS" if ok else "FAIL"} {scenario}')
                return 0 if ok else 1
            time.sleep(1)
        log(f'logs: {d}')
        log(f'RESULT: TIMEOUT {scenario} after {timeout}s')
        return 3
    return guarded(body)


def solo(timeout, scenario='solo'):
    def body(copies):
        c = Copy('a', new_run_dir(scenario), [f'-coop-bot={scenario}:host'] + EXTRA)
        copies.append(c)
        c.start()
        t0 = time.time()
        while time.time() - t0 < timeout:
            r = c.read_result()
            if r is not None:
                log(f'RESULT: {"PASS" if r.get("pass") else "FAIL"} {scenario} {r.get("failures")}')
                return 0 if r.get('pass') else 1
            if not c.alive():
                log('RESULT: FAIL (crashed)')
                return 1
            time.sleep(1)
        log('RESULT: TIMEOUT')
        return 3
    return guarded(body)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('cmd', choices=['setup', 'deploy', 'smoke', 'run', 'solo'])
    ap.add_argument('scenario', nargs='?')
    ap.add_argument('--timeout', type=int, default=240)
    ap.add_argument('--extra', default='', help='extra game args, space separated')
    ap.add_argument('--host-extra', default='', help='extra game args for the host copy in run, space separated')
    a = ap.parse_args()
    EXTRA.extend(a.extra.split())
    HOST_EXTRA.extend(a.host_extra.split())
    if a.cmd == 'setup':
        setup()
        return 0
    if a.cmd == 'deploy':
        deploy()
        return 0
    if game_running():
        raise SystemExit('refusing to start: a game process is already running')
    deploy_if_stale()
    if a.cmd == 'smoke':
        return smoke(a.timeout)
    if a.cmd == 'solo':
        return solo(a.timeout, a.scenario or 'solo')
    if not a.scenario:
        raise SystemExit('run needs a scenario name')
    return run(a.scenario, a.timeout)


if __name__ == '__main__':
    sys.exit(main())
