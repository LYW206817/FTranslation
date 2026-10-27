# Generates the installer wizard art in the app palette: cream white with a blue tint.
from PIL import Image, ImageDraw, ImageFilter
import os

OUT = r'D:\FTranslation\assets\installer'
ICON = r'D:\FTranslation\assets\icon\icon-256.png'
os.makedirs(OUT, exist_ok=True)

TOP = (253, 254, 255)
BOT = (226, 237, 252)
GLOW = (37, 98, 255)
HAIR = (214, 221, 232)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def gradient(w, h):
    img = Image.new('RGB', (w, h), TOP)
    d = ImageDraw.Draw(img)
    for y in range(h):
        d.line([(0, y), (w, y)], fill=lerp(TOP, BOT, (y / (h - 1)) ** 1.05))
    return img


def wizard_strip(w, h):
    img = gradient(w, h)
    glow = Image.new('L', (w, h), 0)
    gd = ImageDraw.Draw(glow)
    r = int(w * 1.15)
    cx, cy = int(w * 0.5), int(h * 0.92)
    gd.ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
    glow = glow.filter(ImageFilter.GaussianBlur(w * 0.45))
    glow = glow.point(lambda v: int(v * 0.34))
    img = Image.composite(Image.new('RGB', (w, h), GLOW), img, glow)

    # the app mark (a droplet) as a faint watermark low in the strip
    mark = Image.open(ICON).convert('RGBA')
    side = int(w * 0.86)
    mark = mark.resize((side, side), Image.LANCZOS)
    alpha = mark.getchannel('A').point(lambda v: int(v * 0.16))
    mark.putalpha(alpha)
    layer = Image.new('RGBA', (w, h), (255, 255, 255, 0))
    layer.alpha_composite(mark, (int((w - side) / 2), int(h * 0.60)))
    img = Image.alpha_composite(img.convert('RGBA'), layer).convert('RGB')
    return img


def badge(size):
    # The app icon itself, plus a hairline outline so its cream plate keeps an edge on a #F7F9FC page.
    s = size * 4
    art = Image.open(ICON).convert('RGBA').resize((s, s), Image.LANCZOS)
    d = ImageDraw.Draw(art)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), outline=HAIR + (255,), width=max(1, s // 116))
    return art.resize((size, size), Image.LANCZOS)


for w, h in [(240, 459), (336, 643), (403, 772)]:
    wizard_strip(w, h).save(os.path.join(OUT, 'wizard-%dx%d.png' % (w, h)))
    print('wizard-%dx%d.png' % (w, h))

for size in (116, 147, 159):
    badge(size).save(os.path.join(OUT, 'small-%d.png' % size))
    print('small-%d.png' % size)
