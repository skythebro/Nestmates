"""Build the player zip from the Release build: python -I tools/package.py

Layout: BepInEx/plugins/Nestmates/{Nestmates.dll, Nestmates.Net.dll}, README.txt, LICENSE.
Writes dist/out/Nestmates-<version>.zip and prints its SHA-256. Fixed timestamps, so the same build gives
the same zip.
"""
import hashlib
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD = os.path.join(ROOT, 'src', 'IronNestCoop', 'bin', 'Release', 'net6.0')


def version():
    props = open(os.path.join(ROOT, 'Directory.Build.props'), encoding='utf-8').read()
    return re.search(r'<Version>([^<]+)</Version>', props).group(1)


def main():
    v = version()
    files = [(os.path.join(BUILD, n), f'BepInEx/plugins/Nestmates/{n}') for n in ('Nestmates.dll', 'Nestmates.Net.dll')]
    files += [(os.path.join(ROOT, 'dist', 'README.txt'), 'README.txt'), (os.path.join(ROOT, 'LICENSE'), 'LICENSE')]
    for src, _ in files:
        if not os.path.exists(src):
            raise SystemExit(f'missing {src} (build Release first)')
    out_dir = os.path.join(ROOT, 'dist', 'out')
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, f'Nestmates-{v}.zip')
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        for src, arc in files:
            info = zipfile.ZipInfo(arc, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, open(src, 'rb').read())
    digest = hashlib.sha256(open(out, 'rb').read()).hexdigest()
    print(f'{out}\nSHA-256 {digest}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
