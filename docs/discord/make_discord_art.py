"""Discord Rich Presence pictures for Nestmates, in the Nexus art's style (its drawing code is reused, not changed).

    python -I make_discord_art.py

Outputs (next to this script):
    nestmates_1024.png     the Rich Presence asset, key "nestmates" (shown as the large image of the status)
    cover_1024x576.png     the Rich Presence invite (cover) image
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.normpath(os.path.join(HERE, "..", "nexus", "art")))
import make_art as A   # noqa: E402  (the Nexus page's art: palette, turret, crew, fonts, renderer)


def square():
    """The status picture: Discord shows it small, so the turret against the sun, the crew, and the name."""
    w = h = 1024
    hz = 800
    a = A.backdrop(w, h, hz, (470, 610), 330)
    a += A.birds(820, 470, 9, 70, 1.0, 8)
    a += A.turret(300, hz + 6, .52, barrel=-20)
    for i, (c, pose) in enumerate(zip(A.CREW, (1, 0, 2, 0, 0))):
        a += A.figure(640 + i * 72, hz + 8 + (i % 2) * 3, .82, c, pose)
    a += A.grass(w, hz) + A.finish(w, h)
    body = A.svg(w, h, a)
    body += ('<div class="ink" style="position:absolute;left:0;right:0;top:46px;text-align:center;font-size:206px">NESTMATES</div>'
             '<div style="position:absolute;left:0;right:0;top:236px;text-align:center">'
             '<div class="stencil tag" style="font-size:52px">Online co-op</div></div>')
    return A.page(w, h, body)


def main():
    A.render(os.path.join(HERE, "nestmates_1024.png"), square(), 1024, 1024)
    A.render(os.path.join(HERE, "cover_1024x576.png"), A.thumb(), 1280, 720)
    from PIL import Image
    p = os.path.join(HERE, "cover_1024x576.png")
    Image.open(p).resize((1024, 576), Image.LANCZOS).save(p, optimize=True)
    print("resized", p)


if __name__ == "__main__":
    main()
