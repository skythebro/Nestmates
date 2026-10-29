"""Renders the 8 emote tiles for gallery_10_crew from the avatar preview (docs/avatar/preview.html), without labels.

    python -I render_emotes.py                 the final tiles -> art/emotes/emote_<n>.png (used by make_art.py)
    python -I render_emotes.py candidates      several moments per emote -> %TEMP%/nexusart-emotes/ for picking

docs/avatar belongs to the avatar work and is only READ: preview.html and geometry.js are copied to a temp folder,
and the copy gets three small changes: the controls are hidden, time stops after the warm-up (so the frame is
exactly `at` seconds into the emote), and #hue picks the helmet/scarf colour.
"""
import os, sys, shutil, subprocess
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
AVATAR = os.path.normpath(os.path.join(HERE, "..", "..", "avatar"))
OUT = os.path.join(HERE, "emotes")
TMP = os.path.join(os.environ.get("TEMP", HERE), "nexusart-emotes")
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
W, H = 640, 500                     # tile aspect of the slide's emote cells
SS = 2                              # preview supersampling; the screenshot is taken at device scale 2 as well

# n, name, camera yaw, moment in seconds (picked from the candidates), helmet hue index (0-4)
EMOTES = [
    (1, "Wave",       25, 0.90, 0),
    (2, "Look here!", 75, 0.70, 1),
    (3, "Thumbs up",  35, 0.70, 2),
    (4, "Salute",     40, 0.70, 3),
    (5, "Shrug",      15, 0.70, 4),
    (6, "Cheer",      25, 0.70, 0),
    (7, "Nod",        75, 0.50, 1),
    (8, "Shake head", 10, 0.70, 2),
]

def prepare():
    os.makedirs(TMP, exist_ok=True)
    shutil.copy(os.path.join(AVATAR, "geometry.js"), TMP)
    src = open(os.path.join(AVATAR, "preview.html"), encoding="utf-8").read()
    patches = [
        ("</style>", "  #ui, #info { display: none !important; }\n</style>"),
        ("else simulate(dt);", 'else if (!hash.has("freeze")) simulate(dt);'),
        ("[Figure(0, HUES[0], 0)]", '[Figure(0, HUES[(+(hash.get("hue") || 0)) % 5], 0)]'),
    ]
    for a, b in patches:
        if src.count(a) != 1:
            sys.exit(f"preview.html changed: can't find {a!r} exactly once; update render_emotes.py")
        src = src.replace(a, b)
    page = os.path.join(TMP, "preview.html")
    open(page, "w", encoding="utf-8").write(src)
    return page

def shot(page, out, n, yaw, at, hue):
    # like the contact sheet's camera (pitch 10), pulled back a little and aimed lower so raised arms keep a margin
    h = f"single&freeze&emote={n}&t=4&at={at}&yaw={yaw}&pitch=10&dist=3.15&tx=0&ty=0.92&tz=0.15&hue={hue}&ss={SS}"
    url = "file:///" + page.replace("\\", "/") + "#" + h
    subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars", f"--force-device-scale-factor={SCALE_DSF}",
                    f"--window-size={W},{H}", "--allow-file-access-from-files", "--virtual-time-budget=1500",
                    f"--screenshot={out}", url], check=True, capture_output=True)

SCALE_DSF = 1                       # canvas is drawn at SS x the window size and shown at 100vw: DSF 1 keeps it 1:1 per css px

def main():
    page = prepare()
    if sys.argv[1:] == ["candidates"]:
        os.makedirs(TMP, exist_ok=True)
        for n, name, yaw, at, hue in EMOTES:
            for k, t in enumerate((0.35, 0.5, 0.7, 0.9, 1.1)):
                shot(page, os.path.join(TMP, f"cand_{n}_{k}.png"), n, yaw, t, hue)
        print("candidates in", TMP)
        return
    os.makedirs(OUT, exist_ok=True)
    for n, name, yaw, at, hue in EMOTES:
        out = os.path.join(OUT, f"emote_{n}.png")
        shot(page, out, n, yaw, at, hue)
        Image.open(out).convert("RGB").save(out, optimize=True)
        print("wrote", out)

if __name__ == "__main__":
    main()
