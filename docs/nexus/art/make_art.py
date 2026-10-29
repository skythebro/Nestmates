"""Builds the Nexus images for Nestmates: HTML/SVG -> PNG with headless Chrome, rendered at 2x and downscaled.

    python -I make_art.py            everything
    python -I make_art.py compose    only re-compose gallery slides from art/screenshots/

Outputs (../images):
    banner_1920x480.png, thumbnail_1280x720.png          generated art
    gallery_07_shared.png, gallery_08_howto.png         generated info slides
    overlays/overlay_NN_*.png                           transparent caption overlays for your own screenshots
    gallery_NN_*.png                                    overlay + your screenshot, made when
                                                        art/screenshots/NN_*.png (or .jpg) exists
Fonts in ./fonts: Big Shoulders Display / Stencil (OFL), Special Elite (Apache 2.0).
The turret is our own drawing in the style of the game's key art, not a copy of it.
"""
import os, sys, math, random, subprocess, html, glob
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "images"))
OVL = os.path.join(OUT, "overlays")
SHOTS = os.path.join(HERE, "screenshots")
TMP = os.path.join(os.environ.get("TEMP", HERE), "nexusart-html")
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
for d in (OUT, OVL, SHOTS, TMP): os.makedirs(d, exist_ok=True)
FONTS = "file:///" + os.path.join(HERE, "fonts").replace("\\", "/")
SCALE = 2   # supersampling factor

# Palette: the game's poster look (flat red tones, navy for the deepest shadow).
BG_TOP, BG_BOT = "#d3121b", "#e6221f"
SUN, SUN_RING = "#ef3b2b", "#e52c24"
HAZE = "#d71b20"
GROUND = "#4a0f14"
NEAR = dict(t0="#b02a26", t1="#8c201f", t2="#62161a", t3="#3f1018", navy="#1d2232")
FAR = dict(t0="#7e1d1d", t1="#681919", t2="#4e1418", t3="#36101a", navy="#1d2232")
INK, CREAM = "#1c2230", "#f6e7c8"
CREW = ["#f2b134", "#5ec1a4", "#6fa8e8", "#e86f9b", "#b58be0"]   # stand-ins for the players' colours

CSS = f"""
@font-face {{ font-family:'BSD'; src:url('{FONTS}/BigShouldersDisplay.ttf'); font-weight:100 900; }}
@font-face {{ font-family:'BSS'; src:url('{FONTS}/BigShouldersStencilDisplay.ttf'); font-weight:100 900; }}
@font-face {{ font-family:'SE';  src:url('{FONTS}/SpecialElite.ttf'); }}
*{{margin:0;padding:0;box-sizing:border-box}}
html,body{{overflow:hidden;background:transparent}}
.stage{{position:relative;overflow:hidden}}
.ink{{font-family:'BSD';font-weight:900;color:{INK};line-height:.84;text-transform:uppercase;letter-spacing:.005em}}
.stencil{{font-family:'BSS';font-weight:800;color:{CREAM};text-transform:uppercase;letter-spacing:.07em}}
.type{{font-family:'SE';color:{CREAM}}}
.tag{{display:inline-block;background:{INK};padding:7px 22px 1px;box-shadow:6px 6px 0 rgba(40,0,6,.35)}}
"""

# ---- drawing helpers -----------------------------------------------------------------------------
def poly(pts, fill, extra=""):
    return f'<path d="M{" L".join(f"{x:.1f},{y:.1f}" for x, y in pts)} Z" fill="{fill}" {extra}/>'

def rot(px, py, deg):
    a = math.radians(deg); return px * math.cos(a) - py * math.sin(a), px * math.sin(a) + py * math.cos(a)

def leg(x, hip_y, kd, p):
    """Walker leg. x = hip centre, hip_y < 0, ground at 0, kd = knee direction (+1 forward, -1 back)."""
    H = -hip_y
    ky, ay = hip_y + H * .47, hip_y + H * .86
    kx, ax = x + 30 * kd, x + 6 * kd
    s = poly([(x - 34, hip_y + 18), (x + 34, hip_y + 18), (kx + 28, ky), (kx - 28, ky)], p["t2"])             # thigh
    s += poly([(x - 34, hip_y + 18), (x - 14, hip_y + 18), (kx - 10, ky), (kx - 28, ky)], p["t3"])            # thigh shade
    s += (f'<line x1="{x - 40 * kd:.1f}" y1="{hip_y + 26:.1f}" x2="{ax - 34 * kd:.1f}" y2="{ay - 52:.1f}" '
          f'stroke="{p["navy"]}" stroke-width="10" stroke-linecap="round"/>'                                    # ram
          f'<line x1="{x - 40 * kd:.1f}" y1="{hip_y + 26:.1f}" x2="{(x + ax) / 2 - 37 * kd:.1f}" y2="{(hip_y + ay) / 2 - 10:.1f}" '
          f'stroke="{p["t1"]}" stroke-width="16" stroke-linecap="round"/>')                                     # ram cylinder
    s += poly([(kx - 32, ky), (kx + 32, ky), (ax + 28, ay), (ax - 28, ay)], p["t1"])                          # shin
    s += poly([(kx + 10, ky), (kx + 32, ky), (ax + 28, ay), (ax + 8, ay)], p["t2"])                           # shin shade
    s += poly([(kx - 36, ky + 22), (kx + 6, ky + 22), (ax + 2, ay - 70), (ax - 32, ay - 66)], p["t0"])        # shin plate
    s += poly([(ax - 62, 0), (ax + 74, 0), (ax + 56, -26), (ax + 10, -40), (ax - 44, -34)], p["t2"])          # foot
    s += poly([(ax + 10, -40), (ax + 56, -26), (ax + 74, 0), (ax + 40, 0), (ax + 30, -20)], p["t1"])          # toe
    s += poly([(ax - 30, ay - 4), (ax + 30, ay - 4), (ax + 24, -30), (ax - 24, -30)], p["t3"])                # ankle
    s += (f'<circle cx="{kx:.1f}" cy="{ky:.1f}" r="33" fill="{p["t1"]}"/><circle cx="{kx:.1f}" cy="{ky:.1f}" r="21" fill="{p["t2"]}"/>'
          f'<circle cx="{kx:.1f}" cy="{ky:.1f}" r="9" fill="{p["t3"]}"/>')                                      # knee
    s += (f'<rect x="{x - 72}" y="{hip_y - 56}" width="144" height="112" rx="50" fill="{p["t1"]}"/>'
          f'<path d="M{x - 72},{hip_y + 4} a72,52 0 0 0 144,0 v10 a72,44 0 0 1 -144,0 Z" fill="{p["t2"]}"/>'
          f'<circle cx="{x}" cy="{hip_y}" r="40" fill="{p["t2"]}"/><circle cx="{x}" cy="{hip_y}" r="24" fill="{p["t3"]}"/>'
          f'<circle cx="{x}" cy="{hip_y}" r="9" fill="{p["t1"]}"/>')                                           # hip drum
    for i in range(8):
        bx, by = x + 32 * math.cos(i * math.pi / 4), hip_y + 32 * math.sin(i * math.pi / 4)
        s += f'<circle cx="{bx:.1f}" cy="{by:.1f}" r="3" fill="{p["t3"]}"/>'
    return s

def turret(x, y, s, barrel=-24):
    """Walking heavy turret, side view facing right. (x, y) = ground under the hull centre, s = scale."""
    p = NEAR
    g = f'<g transform="translate({x},{y}) scale({s})">'
    HY = -250                                   # hip height
    g += leg(-262, HY + 6, -1, FAR) + leg(82, HY + 6, 1, FAR)            # far legs (behind the hull)
    # undercarriage
    g += poly([(-322, -302), (222, -302), (200, -262), (-300, -262)], p["navy"])
    g += f'<rect x="-120" y="-272" width="200" height="36" rx="8" fill="{p["t3"]}"/>'
    # hull: roof band, side face, lower skirt, glacis
    g += poly([(-322, -482), (150, -482), (188, -460), (-336, -460)], p["t0"])
    g += poly([(-336, -460), (188, -460), (252, -404), (252, -338), (228, -300), (-330, -300), (-342, -322)], p["t1"])
    g += poly([(-342, -344), (252, -344), (252, -338), (228, -300), (-330, -300), (-342, -322)], p["t2"])
    g += poly([(188, -460), (252, -404), (252, -344), (200, -344), (170, -440)], p["t2"])
    for sx in (-210, -70, 70):                                                                           # panel seams
        g += f'<line x1="{sx}" y1="-460" x2="{sx}" y2="-344" stroke="{p["t2"]}" stroke-width="4"/>'
    g += f'<line x1="-336" y1="-418" x2="168" y2="-418" stroke="{p["t2"]}" stroke-width="3"/>'
    for rx in range(-320, 230, 26):                                                                       # rivets
        g += f'<circle cx="{rx}" cy="-451" r="3" fill="{p["t2"]}"/><circle cx="{rx}" cy="-333" r="3" fill="{p["t3"]}"/>'
    g += f'<rect x="-170" y="-450" width="78" height="62" rx="4" fill="{p["t2"]}"/><rect x="-160" y="-442" width="26" height="11" fill="{p["navy"]}"/>'
    g += f'<rect x="-48" y="-402" width="96" height="16" fill="{p["navy"]}" opacity=".55"/>'
    # raised rear box with vision slot, rear lamp arm
    g += poly([(-320, -482), (-320, -526), (-190, -526), (-172, -482)], p["t1"])
    g += poly([(-320, -526), (-190, -526), (-184, -517), (-320, -517)], p["t0"])
    g += f'<rect x="-306" y="-510" width="84" height="14" fill="{p["navy"]}"/>'
    g += f'<rect x="-378" y="-448" width="42" height="10" fill="{p["t2"]}"/><rect x="-390" y="-458" width="15" height="28" rx="3" fill="{p["t1"]}"/>'
    # roof details: vent, hatch dome, periscope
    g += f'<rect x="-130" y="-494" width="116" height="12" fill="{p["t2"]}"/>'
    g += f'<path d="M14,-482 a44,28 0 0 1 88,0 Z" fill="{p["t1"]}"/><path d="M24,-487 a34,19 0 0 1 42,-17" stroke="{p["t0"]}" stroke-width="5" fill="none"/>'
    g += f'<rect x="118" y="-504" width="16" height="24" fill="{p["t2"]}"/><rect x="111" y="-511" width="30" height="9" fill="{p["t1"]}"/>'
    # gun: mantlet, sleeve, collar, barrel, muzzle brake (rotated about the trunnion)
    px, py = 204, -440
    gun = f'<g transform="translate({px},{py}) rotate({barrel})">'
    gun += f'<rect x="40" y="28" width="210" height="26" rx="11" fill="{p["t2"]}"/>'                         # recuperator
    gun += f'<rect x="-62" y="-58" width="150" height="116" rx="18" fill="{p["t1"]}"/>'
    gun += f'<rect x="-62" y="8" width="150" height="50" rx="14" fill="{p["t2"]}"/><rect x="-62" y="-58" width="150" height="13" rx="6" fill="{p["t0"]}"/>'
    gun += f'<rect x="88" y="-36" width="220" height="72" fill="{p["t1"]}"/><rect x="88" y="10" width="220" height="26" fill="{p["t2"]}"/>'
    gun += f'<rect x="88" y="-36" width="220" height="10" fill="{p["t0"]}"/>'
    gun += f'<rect x="308" y="-43" width="26" height="86" rx="4" fill="{p["t2"]}"/>'
    gun += f'<rect x="334" y="-26" width="300" height="52" fill="{p["t1"]}"/><rect x="334" y="6" width="300" height="20" fill="{p["t2"]}"/>'
    gun += f'<rect x="334" y="-26" width="300" height="8" fill="{p["t0"]}"/>'
    gun += f'<rect x="634" y="-36" width="92" height="72" rx="6" fill="{p["t1"]}"/><rect x="634" y="8" width="92" height="28" fill="{p["t2"]}"/>'
    for sx in (650, 680):
        gun += f'<rect x="{sx}" y="-11" width="18" height="22" fill="{p["navy"]}"/>'
    gun += f'<rect x="722" y="-32" width="10" height="64" fill="{p["t3"]}"/></g>'
    # elevation ram from the hull to the sleeve
    ex, ey = rot(170, 36, barrel)
    g += (f'<line x1="160" y1="-392" x2="{px + ex:.1f}" y2="{py + ey:.1f}" stroke="{p["navy"]}" stroke-width="13" stroke-linecap="round"/>'
          f'<line x1="160" y1="-392" x2="{160 + (px + ex - 160) * .5:.1f}" y2="{-392 + (py + ey + 392) * .5:.1f}" stroke="{p["t2"]}" stroke-width="22" stroke-linecap="round"/>')
    g += gun
    # hanging cables
    g += (f'<path d="M-40,-264 C-30,-200 30,-210 38,-264" stroke="{p["navy"]}" stroke-width="6" fill="none"/>'
          f'<path d="M120,-268 C150,-170 210,-190 196,-262" stroke="{p["navy"]}" stroke-width="5" fill="none"/>'
          f'<path d="M-250,-264 C-230,-214 -200,-226 -190,-264" stroke="{p["navy"]}" stroke-width="5" fill="none"/>')
    g += leg(-196, HY, -1, NEAR) + leg(150, HY, 1, NEAR)                                                     # near legs
    return g + '</g>'

# Crew figure colours, after the mod's own avatar (docs/avatar): slate greatcoat, leather, brass, warm goggle lenses.
COAT, COAT_D, LEATHER, BRASS, LENS, HOOD = "#2c313d", "#1d2230", "#4d2f1c", "#c8913f", "#ffe7a1", "#2a1d18"

def shade(hex_col, k):
    r, g, b = (int(hex_col[i:i + 2], 16) for i in (1, 3, 5))
    return "#%02x%02x%02x" % tuple(max(0, min(255, round(c * k))) for c in (r, g, b))

def figure(x, y, s, col, pose=0):
    """Crew member like the in-game avatar: Brodie helmet and scarf in the player's colour, gas-mask hood with
    two glowing brass goggles, slate greatcoat with belt and buttons, boots. Front view, feet at (x, y).
    pose 0 idle, 1 pointing (left), 2 waving."""
    tilt = sum(map(ord, col)) % 13 - 6                      # each helmet sits at its own slight tilt
    f = f'<g transform="translate({x},{y}) scale({s})">'
    f += f'<rect x="-14" y="-24" width="10" height="16" fill="{COAT_D}"/><rect x="4" y="-24" width="10" height="16" fill="{COAT_D}"/>'
    f += f'<rect x="-17" y="-11" width="15" height="11" rx="3" fill="{LEATHER}"/><rect x="2" y="-11" width="15" height="11" rx="3" fill="{LEATHER}"/>'
    # arms behind the coat edge
    if pose == 1:
        f += f'<path d="M-15,-90 L-21,-81 L-58,-99 L-53,-108 Z" fill="{COAT}"/><circle cx="-58" cy="-104" r="5" fill="{LEATHER}"/>'
    else:
        f += f'<path d="M-17,-90 L-27,-86 L-28,-52 L-19,-52 Z" fill="{COAT}"/><circle cx="-23.5" cy="-50" r="4.8" fill="{LEATHER}"/>'
    if pose == 2:
        f += f'<path d="M14,-90 L21,-83 L33,-125 L24,-129 Z" fill="{COAT_D}"/><circle cx="29" cy="-130" r="5.2" fill="{LEATHER}"/>'
    else:
        f += f'<path d="M17,-90 L27,-86 L28,-52 L19,-52 Z" fill="{COAT_D}"/><circle cx="23.5" cy="-50" r="4.8" fill="{LEATHER}"/>'
    # greatcoat, shaded half, seam, belt, buttons
    f += f'<path d="M-22,-20 L-18,-91 Q0,-98 18,-91 L22,-20 Q0,-15 -22,-20 Z" fill="{COAT}"/>'
    f += f'<path d="M1,-96 Q10,-95 18,-91 L22,-20 Q12,-17 1,-16 Z" fill="{COAT_D}"/>'
    f += f'<rect x="-21.5" y="-58" width="43" height="6.5" fill="{LEATHER}"/><rect x="-3.5" y="-58.8" width="7" height="8" fill="{BRASS}"/>'
    f += f'<circle cx="5" cy="-77" r="1.9" fill="{BRASS}"/><circle cx="5" cy="-68" r="1.9" fill="{BRASS}"/>'
    # head: gas-mask hood, goggles, filter snout
    f += f'<circle cx="0" cy="-114" r="16" fill="{HOOD}"/>'
    for gx in (-6.5, 6.5):
        f += (f'<circle cx="{gx}" cy="-117" r="8.5" fill="{LENS}" opacity=".22"/><circle cx="{gx}" cy="-117" r="5.3" fill="{BRASS}"/>'
              f'<circle cx="{gx}" cy="-117" r="3.7" fill="{LENS}"/>')
    f += f'<path d="M-4,-110 L4,-110 L5.5,-102 L-5.5,-102 Z" fill="{COAT_D}"/><rect x="-5.5" y="-103.5" width="11" height="2.5" fill="{shade(COAT_D, 1.6)}"/>'
    # scarf in the player's colour (with its loose end)
    f += f'<rect x="-16" y="-101" width="32" height="10" rx="4" fill="{col}"/><path d="M8,-95 L16,-94 L18,-73 L11,-74 Z" fill="{shade(col, .72)}"/>'
    # Brodie helmet in the player's colour
    f += f'<g transform="rotate({tilt} 0 -126)"><ellipse cx="0" cy="-126" rx="27" ry="5" fill="{shade(col, .68)}"/>'
    f += f'<path d="M-16,-126 Q-15,-145 0,-146 Q15,-145 16,-126 Z" fill="{col}"/>'
    f += '<path d="M-10,-131 Q-8,-141 1,-142" stroke="#fff" stroke-opacity=".28" stroke-width="2.5" fill="none" stroke-linecap="round"/></g>'
    return f + '</g>'

def birds(cx, cy, n, spread, s, seed):
    r = random.Random(seed); d = ""
    for _ in range(n):
        x, y, k = cx + r.uniform(-spread, spread), cy + r.uniform(-spread * .45, spread * .45), s * r.uniform(.6, 1.2)
        d += (f'<path d="M{x:.1f},{y:.1f} q{6 * k:.1f},{-6 * k:.1f} {12 * k:.1f},{1 * k:.1f} q{6 * k:.1f},{-7 * k:.1f} {12 * k:.1f},{-1 * k:.1f}" '
              f'stroke="{GROUND}" stroke-width="{2.2 * k:.1f}" fill="none" stroke-linecap="round"/>')
    return d

def pines(x0, x1, base, h, seed, col):
    r = random.Random(seed); d = ""; x = x0
    while x < x1:
        th = h * r.uniform(.5, 1.1); w = th * .34
        d += f'<path d="M{x:.1f},{base - th:.1f} L{x + w:.1f},{base:.1f} L{x - w:.1f},{base:.1f} Z" fill="{col}"/>'
        x += r.uniform(w * .5, w * 1.6)
    return d

def grass(w, y, seed=7):
    r = random.Random(seed)
    d = f'<rect x="0" y="{y}" width="{w}" height="3000" fill="{GROUND}"/>'
    x = -10
    while x < w + 10:
        h = r.choice((r.uniform(6, 22), r.uniform(6, 22), r.uniform(18, 46)))
        lean = r.uniform(-10, 10); bw = r.uniform(3, 7)
        d += f'<path d="M{x:.1f},{y + 2} Q{x + lean * .3:.1f},{y - h * .6:.1f} {x + lean:.1f},{y - h:.1f} Q{x + bw * .6:.1f},{y - h * .5:.1f} {x + bw:.1f},{y + 2} Z" fill="{GROUND}"/>'
        x += r.uniform(2, 9)
    for _ in range(max(3, w // 300)):            # thin posts in the field
        px = r.uniform(0, w); ph = r.uniform(12, 22)
        d += f'<rect x="{px:.1f}" y="{y - ph:.1f}" width="2.5" height="{ph:.1f}" fill="{GROUND}"/><rect x="{px - 4:.1f}" y="{y - ph + 4:.1f}" width="10" height="2.5" fill="{GROUND}"/>'
    return d

def backdrop(w, h, horizon, sun, sun_r):
    sx, sy = sun
    b = (f'<defs><linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{BG_TOP}"/><stop offset="1" stop-color="{BG_BOT}"/></linearGradient>'
         f'<radialGradient id="glow"><stop offset="0" stop-color="#ff6a45" stop-opacity=".35"/><stop offset="1" stop-color="#ff6a45" stop-opacity="0"/></radialGradient>'
         f'<radialGradient id="vig" cx=".5" cy=".45" r=".75"><stop offset=".55" stop-color="#2a0006" stop-opacity="0"/><stop offset="1" stop-color="#2a0006" stop-opacity=".45"/></radialGradient>'
         f'<filter id="grain" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency=".85" numOctaves="2" seed="4" stitchTiles="stitch"/>'
         f'<feColorMatrix type="matrix" values="0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  0 0 0 .55 0"/></filter></defs>'
         f'<rect width="{w}" height="{h}" fill="url(#sky)"/>'
         f'<circle cx="{sx}" cy="{sy}" r="{sun_r * 1.6}" fill="url(#glow)"/>'
         f'<circle cx="{sx}" cy="{sy}" r="{sun_r * 1.06}" fill="{SUN_RING}"/><circle cx="{sx}" cy="{sy}" r="{sun_r}" fill="{SUN}"/>'
         f'<ellipse cx="{w * .86}" cy="{horizon - 10}" rx="{w * .22}" ry="{h * .09}" fill="#ef3a2c" opacity=".55"/>'
         + pines(w * .80, w + 40, horizon + 4, h * .17, 11, HAZE))
    return b

def finish(w, h):
    return (f'<rect width="{w}" height="{h}" fill="url(#vig)"/>'
            f'<rect width="{w}" height="{h}" filter="url(#grain)" opacity=".16" style="mix-blend-mode:multiply"/>')

def svg(w, h, inner):
    return f'<svg width="{w}" height="{h}" viewBox="0 0 {w} {h}" style="position:absolute;inset:0">{inner}</svg>'

def page(w, h, body, bg="transparent"):
    return (f'<!doctype html><meta charset="utf-8"><style>{CSS}</style>'
            f'<body><div class="stage" style="width:{w}px;height:{h}px;background:{bg}">{body}</div>')

# ---- banner & thumbnail --------------------------------------------------------------------------
def banner():
    w, h, hz = 1920, 480, 392
    a = backdrop(w, h, hz, (470, 300), 225)
    a += birds(1200, 330, 16, 90, 1.0, 3) + birds(420, 120, 4, 40, 1.1, 5)
    a += turret(400, hz + 6, .5, barrel=-22)
    for i, (c, pose) in enumerate(zip(CREW, (1, 0, 2, 0, 0))):
        a += figure(1530 + i * 78, hz + 8 + (i % 2) * 3, .78, c, pose)
    a += grass(w, hz) + finish(w, h)
    body = svg(w, h, a)
    body += ('<div style="position:absolute;left:880px;top:34px">'
             '<div class="ink" style="font-size:212px">NESTMATES</div>'
             '<div class="stencil tag" style="font-size:46px;margin-top:14px">Online co-op for Iron Nest</div></div>'
             f'<div class="type" style="position:absolute;left:0;right:0;top:{hz + 30}px;text-align:center;font-size:30px;letter-spacing:.05em">'
             'One turret &middot; up to five crew &middot; one shared world</div>')
    return page(w, h, body)

def thumb():
    w, h, hz = 1280, 720, 588
    a = backdrop(w, h, hz, (360, 430), 250)
    a += birds(1020, 420, 14, 80, 1.1, 8)
    a += turret(320, hz + 6, .46, barrel=-18)
    for i, (c, pose) in enumerate(zip(CREW, (1, 0, 2, 0, 0))):
        a += figure(820 + i * 72, hz + 8 + (i % 2) * 3, .8, c, pose)
    a += grass(w, hz) + finish(w, h)
    body = svg(w, h, a)
    body += ('<div class="ink" style="position:absolute;left:48px;top:34px;font-size:244px">NESTMATES</div>'
             '<div class="stencil tag" style="position:absolute;right:52px;top:268px;font-size:50px">Online co-op</div>'
             f'<div class="type" style="position:absolute;left:0;right:0;top:{hz + 44}px;text-align:center;font-size:36px;letter-spacing:.04em">'
             'Up to 5 crew &middot; Steam &middot; join a mission in progress</div>')
    return page(w, h, body)

# ---- gallery: overlays for your own screenshots --------------------------------------------------
GALLERY = [  # (slot, title, subtitle) -> art/screenshots/<slot>.png
    ("01_crew",     "Crew one turret together", "Up to five players over Steam. Everyone sees everyone, with name tags and their own colour."),
    ("02_controls", "Every control is shared",  "A guest\u2019s lever, dial or button runs on the host. The whole crew sees the result."),
    ("03_gunnery",  "Same shot, same crater",   "Every shell, recon photo and trajectory line ends up on everyone’s map."),
    ("04_menu",     "Built into the game’s menus", "Settings → Multiplayer: your player, what you see on screen, privacy & status, and the host’s rules."),
    ("05_lobby",    "Host and invite over Steam", "Invite through the overlay or a friend list. Streamer mode hides the lobby code until you press Show."),
    ("06_cats",     "One cat per player",       "Bring your own cat, with its own name. Everyone can see whose cat is whose."),
]

def overlay(n, title, sub):
    w, h = 1920, 1080
    body = ('<div style="position:absolute;left:0;right:0;bottom:0;height:300px;'
            'background:linear-gradient(rgba(22,5,9,0),rgba(22,5,9,.78) 45%,rgba(22,5,9,.93))"></div>'
            f'<div style="position:absolute;left:72px;bottom:62px;width:10px;height:150px;background:{BG_BOT}"></div>'
            f'<div class="ink" style="position:absolute;left:104px;bottom:118px;font-size:100px;color:{CREAM}">{html.escape(title)}</div>'
            f'<div class="type" style="position:absolute;left:108px;bottom:60px;font-size:33px;opacity:.95">{html.escape(sub)}</div>'
            '<div style="position:absolute;right:56px;top:46px;text-align:right">'
            f'<div class="stencil tag" style="font-size:40px;border-bottom:6px solid {BG_BOT}">Nestmates</div>'
            f'<div class="type" style="font-size:22px;margin-top:10px;color:{CREAM};text-shadow:0 2px 6px #000">online co-op for Iron Nest</div></div>')
    return page(w, h, body)

def compose():
    made = 0
    for slot, _, _ in GALLERY:
        ov = os.path.join(OVL, f"overlay_{slot}.png")
        shots = [p for p in glob.glob(os.path.join(SHOTS, slot + ".*")) if p.lower().endswith((".png", ".jpg", ".jpeg"))]
        if not shots or not os.path.exists(ov): continue
        top = Image.open(ov).convert("RGBA")
        shot = Image.open(shots[0]).convert("RGBA")
        k = max(top.width / shot.width, top.height / shot.height)        # cover, centred
        shot = shot.resize((round(shot.width * k), round(shot.height * k)), Image.LANCZOS)
        l, t = (shot.width - top.width) // 2, (shot.height - top.height) // 2
        shot = shot.crop((l, t, l + top.width, t + top.height))
        out = os.path.join(OUT, f"gallery_{slot}.png")
        Image.alpha_composite(shot, top).convert("RGB").save(out, optimize=True)
        print("composed", out); made += 1
    if not made: print("no screenshots in", SHOTS, "- only overlays were made")

# ---- gallery: generated info slides --------------------------------------------------------------
SHARED = [("Guns & shells", "Aim, reload steps, powder, every shot\u2019s flight and impact"),
          ("Every control", "Dials, sliders, levers, buttons, hover effects"),
          ("Enemies & mission", "Movement, timers, the clock, scripted events"),
          ("Teleprinters", "Each player reads them in their own language"),
          ("Cards & map", "Fire-mission cards, tokens, drawings, requisition"),
          ("Engine & lights", "Diesel, valves, lamps, alert lights, espresso"),
          ("Record player", "Same song at the same moment for everyone"),
          ("Your crew", "Avatars, name tags, one cat per player")]

def slide_bg(w, h):
    a = backdrop(w, h, h - 120, (w - 330, 330), 260)
    a += f'<g opacity=".22">{turret(w - 560, h - 114, .68, barrel=-26)}</g>'
    return a + grass(w, h - 120, 21) + finish(w, h)

def shared_slide():
    w, h = 1920, 1080
    cells = "".join(
        f'<div style="background:rgba(28,34,48,.94);padding:24px 30px 22px;border-left:10px solid {CREW[i % 5]};box-shadow:6px 6px 0 rgba(40,0,6,.3)">'
        f'<div class="stencil" style="font-size:48px">{html.escape(t)}</div>'
        f'<div class="type" style="font-size:27px;margin-top:8px;line-height:1.3;opacity:.92">{html.escape(d)}</div></div>'
        for i, (t, d) in enumerate(SHARED))
    body = svg(w, h, slide_bg(w, h))
    body += ('<div class="ink" style="position:absolute;left:72px;top:54px;font-size:150px">One world. Everyone in it.</div>'
             f'<div class="type" style="position:absolute;left:78px;top:200px;font-size:34px;color:#2a0d10">The host runs the mission. Everyone else sees the host\u2019s world and plays in it.</div>'
             f'<div style="position:absolute;left:72px;right:72px;top:278px;display:grid;grid-template-columns:1fr 1fr;gap:20px">{cells}</div>'
             '<div class="stencil tag" style="position:absolute;right:56px;bottom:30px;font-size:40px">Nestmates</div>')
    return page(w, h, body)

STEPS = [("Install BepInEx 6", "Build be.785, Unity IL2CPP x64. Start the game once."),
         ("Drop in Nestmates", "Extract the zip into the game folder. Every player needs the same version."),
         ("Host or join", "Settings \u2192 Multiplayer \u2192 Nestmates menu (or F7). Host, invite a friend, pick a mission.")]

def howto_slide():
    w, h = 1920, 1080
    cols = "".join(
        f'<div style="background:rgba(28,34,48,.94);padding:34px 36px 40px;box-shadow:8px 8px 0 rgba(40,0,6,.3);border-top:10px solid {CREW[i]}">'
        f'<div class="ink" style="font-size:150px;color:{CREW[i % 5]}">{i + 1}</div>'
        f'<div class="stencil" style="font-size:54px;margin-top:18px">{html.escape(t)}</div>'
        f'<div class="type" style="font-size:30px;margin-top:14px;line-height:1.35;opacity:.92">{html.escape(d)}</div></div>'
        for i, (t, d) in enumerate(STEPS))
    body = svg(w, h, slide_bg(w, h))
    body += ('<div class="ink" style="position:absolute;left:72px;top:54px;font-size:150px">Crew up in three steps</div>'
             f'<div class="type" style="position:absolute;left:78px;top:200px;font-size:34px;color:#2a0d10">Steam version of the game. Two to five players.</div>'
             f'<div style="position:absolute;left:72px;right:72px;top:300px;display:grid;grid-template-columns:1fr 1fr 1fr;gap:30px">{cols}</div>'
             '<div class="stencil tag" style="position:absolute;right:56px;bottom:30px;font-size:40px">Nestmates</div>')
    return page(w, h, body)

DISCORD_SHOT = os.path.normpath(os.path.join(HERE, "..", "..", "discord", "discord_status_example.png"))

def _scaled(src, max_w, max_h, name):
    """Upscale/downscale a screenshot with Lanczos into TMP (Chrome's own scaling is blurrier)."""
    im = Image.open(src).convert("RGBA")
    k = min(max_w / im.width, max_h / im.height)
    im = im.resize((round(im.width * k * SCALE), round(im.height * k * SCALE)), Image.LANCZOS)
    p = os.path.join(TMP, name); im.save(p)
    return "file:///" + p.replace("\\", "/"), round(im.width / SCALE), round(im.height / SCALE)

def _crisp(src, factor, name):
    """Integer upscale with nearest-neighbour (pre-multiplied by the supersampling factor), for small UI
    screenshots: text stays sharp instead of going soft. Returns (url, css width, css height)."""
    im = Image.open(src).convert("RGBA")
    big = im.resize((im.width * factor * SCALE, im.height * factor * SCALE), Image.NEAREST)
    p = os.path.join(TMP, name); big.save(p)
    return "file:///" + p.replace("\\", "/"), im.width * factor, im.height * factor

def status_slide():
    w, h = 1920, 1080
    panel = 'background:rgba(28,34,48,.94);padding:30px 34px 34px;box-shadow:8px 8px 0 rgba(40,0,6,.3)'
    d_src, d_w, d_h = _crisp(DISCORD_SHOT, 2, "discord_shot.png")          # 421x142 -> 842x284, sharp
    left = (f'<div style="{panel};border-top:10px solid #8f9cf3">'
            '<div class="stencil" style="font-size:52px">Discord</div>'
            f'<img src="{d_src}" style="display:block;width:{d_w}px;height:{d_h}px;margin-top:22px;border-radius:10px;image-rendering:pixelated">'
            '<div class="type" style="font-size:27px;margin-top:22px;line-height:1.35;opacity:.92">'
            'Needs the Discord desktop app. Mission, crew size and a live clock. No setup.</div>'
            '<div class="type" style="font-size:27px;margin-top:18px;padding:10px 18px;background:#171a24;'
            'border-left:6px solid #3ba55d;color:#e3f5e8">While you host, friends can join from your status.</div></div>')
    steam_shots = [p for p in glob.glob(os.path.join(SHOTS, "steam_status.*")) if p.lower().endswith((".png", ".jpg", ".jpeg"))]
    lines = ["In the main menu", "Choosing a mission on the operations map", "Counter-Battery · 12 min · hosting co-op, 2 players"]
    def line_boxes(ls, size):
        return "".join(f'<div class="type" style="font-size:{size}px;margin-top:14px;padding:10px 18px;background:#171a24;'
                       f'border-left:6px solid #66c0f4;color:#cfe6f5">{html.escape(t)}</div>' for t in ls)
    if steam_shots:   # the real game-info box (name cropped out), plus the lines it shows at other moments
        s_src, s_w, s_h = _crisp(steam_shots[0], 1, "steam_shot.png")        # natural size, sharp
        steam_body = (f'<img src="{s_src}" style="display:block;width:{s_w}px;height:{s_h}px;margin-top:22px;border-radius:6px;image-rendering:pixelated">'
                      '<div class="type" style="font-size:24px;margin-top:20px;opacity:.85">At other moments:</div>'
                      + line_boxes(lines[1:], 24))
    else:   # no screenshot yet: the three kinds of line the mod sets, as text
        steam_body = line_boxes(lines, 26)
    right = (f'<div style="{panel};border-top:10px solid #66c0f4">'
             '<div class="stencil" style="font-size:52px">Steam</div>' + steam_body +
             '<div class="type" style="font-size:27px;margin-top:22px;line-height:1.35;opacity:.92">'
             'Shown in “View game info”. Players of one co-op game are grouped together.</div></div>')
    body = svg(w, h, slide_bg(w, h))
    body += ('<div class="ink" style="position:absolute;left:72px;top:54px;font-size:132px">Friends see what you’re up to</div>'
             f'<div class="type" style="position:absolute;left:78px;top:200px;font-size:34px;color:#2a0d10">'
             'Built in, all of it optional. “Where I am in the turret” is off by default.</div>'
             f'<div style="position:absolute;left:72px;right:72px;top:290px;display:grid;grid-template-columns:{d_w + 68}px 1fr;gap:30px;align-items:start">{left}{right}</div>'
             '<div class="stencil tag" style="position:absolute;right:56px;bottom:30px;font-size:40px">Nestmates</div>')
    return page(w, h, body)

AVATAR_IMG = os.path.normpath(os.path.join(HERE, "..", "..", "avatar", "images"))

def _crop(name, box, out):
    """Crop a render from docs/avatar/images (drops the preview page's buttons) into TMP."""
    p = os.path.join(TMP, out)
    Image.open(os.path.join(AVATAR_IMG, name)).convert("RGB").crop(box).save(p)
    return p

CREW_POINTS = ["Helmet & scarf in your colour",
               "Goggles that glow and blink",
               "8 emotes on keys 1-8",
               "Works the lever you hold",
               "Points at things on the map",
               "Name tag over every head"]
EMOTES = ["Wave", "Look here!", "Thumbs up", "Salute", "Shrug", "Cheer", "Nod", "Shake head"]

def crew_slide():
    w, h = 1920, 1080
    # the 8 emotes from the avatar contact sheet (cells 320x250; row 1 = emotes 1-5, row 2 = 6-8).
    # Each cell loses its small label strip (top 30 px) and gets a band below for a readable label.
    # Full contact-sheet cells (raised arms reach almost to the top), with only the sheet's small label painted
    # out, plus a strip of plain wall above and floor below, so every figure has a margin in its frame.
    # Cell interiors only: the sheet's 2 px separators are at rows 249-250 and columns 319-320 of every cell.
    CW, CH, BAND, PT, PB, GAP = 318, 248, 42, 16, 26, 8
    cell_h = PT + CH + PB
    sheet = Image.open(os.path.join(AVATAR_IMG, "contact-sheet.png")).convert("RGB")
    grid = Image.new("RGB", (4 * CW + 3 * GAP, 2 * (cell_h + BAND)), INK)
    for i in range(8):
        r, c = divmod(i, 5)
        cell = sheet.crop((c * 320 + 1, r * 250 + 1, c * 320 + 1 + CW, r * 250 + 1 + CH))
        px = cell.load()
        # the sheet's label ("1 Wave"...) is light grey text in the top-left corner; figures there are colour or dark
        lw = max([x for y in range(0, 36) for x in range(0, 170)
                  if sum(px[x, y]) > 330 and max(px[x, y]) - min(px[x, y]) < 40] or [0])
        for y in range(0, 36):
            bg = px[1, y]
            for x in range(0, min(CW, lw + 8)):
                px[x, y] = bg
        framed = Image.new("RGB", (CW, cell_h))
        framed.paste(cell.crop((0, 2, CW, 3)).resize((CW, PT)), (0, 0))                 # plain wall above
        framed.paste(cell, (0, PT))
        last, floor = cell.crop((0, CH - 1, CW, CH)), px[2, CH - 2]
        for j in range(PB):                                                             # floor below, fading any
            t = min(1.0, (j + 1) / 10)                                                   # shadow into plain floor
            framed.paste(Image.blend(last, Image.new("RGB", (CW, 1), floor), t), (0, PT + CH + j))
        grid.paste(framed, ((i % 4) * (CW + GAP), (i // 4) * (cell_h + BAND)))
    CH = cell_h                                   # the label maths below works on the framed cell
    gp = os.path.join(TMP, "crew_emotes.png"); grid.save(gp)
    front, fw, fh = _scaled(gp, 1060, 540, "crew_emotes_s.png")
    k = (fw - 16) / grid.width          # the 8 px border sits inside the img box (border-box)
    back, bw, bh = _scaled(_crop("lineup.png", (60, 140, 1000, 640), "crew_lineup.png"), 690, fh, "crew_lineup_s.png")
    img = 'border:8px solid #1c2230;box-shadow:8px 8px 0 rgba(40,0,6,.3);display:block'
    top = 266
    labels = "".join(
        f'<div class="stencil" style="position:absolute;left:{72 + 8 + (i % 4) * (CW + GAP) * k:.0f}px;width:{CW * k:.0f}px;'
        f'top:{top + 8 + ((i // 4) * (CH + BAND) + CH) * k:.0f}px;height:{BAND * k:.0f}px;line-height:{BAND * k + 4:.0f}px;'
        f'text-align:center;font-size:26px;box-sizing:border-box;border-bottom:4px solid {CREW[i % 5]}">{i + 1}&nbsp; {html.escape(n)}</div>'
        for i, n in enumerate(EMOTES))
    points = "".join(f'<div class="type" style="font-size:27px;line-height:1.3;white-space:nowrap">'
                     f'<span style="color:{CREW[i % 5]}">\u25a0</span>&nbsp; {html.escape(t)}</div>' for i, t in enumerate(CREW_POINTS))
    body = svg(w, h, slide_bg(w, h))
    body += ('<div class="ink" style="position:absolute;left:72px;top:54px;font-size:150px">Meet your crew</div>'
             '<div class="type" style="position:absolute;left:78px;top:200px;font-size:34px;color:#2a0d10">'
             'Every player is a turret crew member of their own, built into the mod. No extra downloads.</div>'
             f'<img src="{front}" style="position:absolute;left:72px;top:{top}px;width:{fw}px;height:{fh}px;{img}">' + labels +
             f'<img src="{back}" style="position:absolute;right:72px;top:{top}px;width:{bw}px;height:{bh}px;{img}">'
             f'<div style="position:absolute;left:72px;top:{top + fh + 22:.0f}px;width:{fw + 16}px;background:rgba(28,34,48,.94);'
             f'padding:16px 28px;box-shadow:8px 8px 0 rgba(40,0,6,.3);display:grid;grid-template-columns:1fr 1fr;gap:8px 24px">{points}</div>'
             '<div class="stencil tag" style="position:absolute;right:56px;bottom:30px;font-size:40px">Nestmates</div>')
    return page(w, h, body)

# ---- rendering -----------------------------------------------------------------------------------
def render(path, doc, w, h, transparent=False):
    hp = os.path.join(TMP, os.path.basename(path) + ".html")
    open(hp, "w", encoding="utf-8").write(doc)
    raw = os.path.join(TMP, os.path.basename(path) + ".raw.png")
    args = [CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars", f"--force-device-scale-factor={SCALE}",
            f"--window-size={w},{h}", "--allow-file-access-from-files", "--virtual-time-budget=5000", f"--screenshot={raw}"]
    if transparent: args.append("--default-background-color=00000000")
    subprocess.run(args + ["file:///" + hp.replace("\\", "/")], check=True, capture_output=True)
    im = Image.open(raw).convert("RGBA" if transparent else "RGB").resize((w, h), Image.LANCZOS)
    im.save(path, optimize=True)
    print("wrote", path)

def main():
    if sys.argv[1:] == ["compose"]:
        render(os.path.join(OUT, "gallery_09_status.png"), status_slide(), 1920, 1080)   # picks up screenshots/steam_status.*
        return compose()
    render(os.path.join(OUT, "banner_1920x480.png"), banner(), 1920, 480)
    render(os.path.join(OUT, "thumbnail_1280x720.png"), thumb(), 1280, 720)
    for slot, t, s in GALLERY:
        render(os.path.join(OVL, f"overlay_{slot}.png"), overlay(slot, t, s), 1920, 1080, transparent=True)
    render(os.path.join(OUT, "gallery_07_shared.png"), shared_slide(), 1920, 1080)
    render(os.path.join(OUT, "gallery_08_howto.png"), howto_slide(), 1920, 1080)
    render(os.path.join(OUT, "gallery_09_status.png"), status_slide(), 1920, 1080)
    render(os.path.join(OUT, "gallery_10_crew.png"), crew_slide(), 1920, 1080)
    compose()

if __name__ == "__main__":
    main()
