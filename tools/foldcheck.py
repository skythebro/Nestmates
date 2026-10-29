"""Find Harmony patch targets whose native code is shared with other methods (MSVC identical-code folding).

Patching a folded method detours every method that shares its body, so our patch runs inside unrelated
code (with a wrong __instance) and the game crashes natively. Found the hard way with
Teleprinter.SignalAlarm (2026-10-27).

Usage: python -I foldcheck.py <cpp2il-ilspy-cs-dir> <mod-src-dir>
Reads [Address(RVA = ...)] attributes from the decompiled Cpp2IL output and the
[HarmonyPatch(typeof(T), nameof(T.M) ...)] attributes from the mod source. Exit code 1 if a target is folded.
"""
import os
import re
import sys
from collections import defaultdict

ADDR = re.compile(r'\[Address\(RVA = "0x([0-9A-F]+)", Offset = "0x[0-9A-F]+", Length = "0x([0-9A-F]+)"\)\]')
METHOD = re.compile(r'^\s*(?:public|private|protected|internal)[^=;(]*?\s(\w+)\s*\(([^)]*)\)\s*$')
TYPE = re.compile(r'^\s*(?:public|internal|private)?\s*(?:static |sealed |abstract |partial )*(?:class|struct) (\w+)')
PATCH = re.compile(r'\[HarmonyPatch\(typeof\(([\w\.]+)\),\s*(?:nameof\([\w\.]+\.(\w+)\)|"(\w+)")([^\]]*)\]')


def index_methods(cs_dir):
    by_rva = defaultdict(list)
    by_name = defaultdict(list)
    for root, _, files in os.walk(cs_dir):
        for f in files:
            if not f.endswith('.cs'):
                continue
            pending = None
            stack = []
            with open(os.path.join(root, f), encoding='utf-8', errors='replace') as fh:
                for line in fh:
                    # One top-level type per file in ILSpy project output: key methods by file name.
                    stack = [os.path.splitext(f)[0]]
                    a = ADDR.search(line)
                    if a:
                        pending = (int(a.group(1), 16), int(a.group(2), 16))
                        continue
                    m = METHOD.match(line)
                    if m and pending:
                        cls = stack[-1] if stack else f[:-3]
                        name = f'{cls}.{m.group(1)}({m.group(2).strip()})'
                        by_rva[pending[0]].append((name, pending[1]))
                        params = m.group(2).strip()
                        nparams = 0 if not params else params.count(',') + 1
                        by_name[(cls, m.group(1))].append((pending[0], pending[1], nparams))
                        pending = None
    return by_rva, by_name


def patch_targets(src_dir):
    out = []
    for root, _, files in os.walk(src_dir):
        for f in files:
            if f.endswith('.cs'):
                text = open(os.path.join(root, f), encoding='utf-8').read()
                for m in PATCH.finditer(text):
                    rest = m.group(4)
                    n = rest.count('typeof(') if 'MethodType' not in rest else -1
                    out.append((m.group(1).split('.')[-1], m.group(2) or m.group(3), f, n if n > 0 else -1))
    return out


def main(cs_dir, src_dir):
    by_rva, by_name = index_methods(cs_dir)
    bad = 0
    for cls, meth, src, nargs in sorted(set(patch_targets(src_dir))):
        hits = [h for h in by_name.get((cls, meth), []) if nargs < 0 or h[2] == nargs]
        if not hits:
            print(f'?    {cls}.{meth} ({src}): no address found (getter/property or name mismatch)')
            continue
        for rva, length, _ in hits:
            sharers = [n for n, _ in by_rva[rva] if not n.startswith(f'{cls}.{meth}(')]
            if sharers:
                bad += 1
                print(f'FOLD {cls}.{meth} @0x{rva:X} len 0x{length:X} ({src}) shares code with {len(sharers)}: {", ".join(sharers[:6])}{" ..." if len(sharers) > 6 else ""}')
            elif length < 0x20:
                bad += 1
                print(f'SHORT {cls}.{meth} @0x{rva:X} len 0x{length:X} ({src}): too short to detour safely (a 14-byte jump overwrites it)')
            else:
                print(f'ok   {cls}.{meth} @0x{rva:X} len 0x{length:X}')
    print(f'RESULT: {"FAIL" if bad else "PASS"} ({bad} unsafe patch targets)')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1], sys.argv[2]))
