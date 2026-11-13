#!/usr/bin/env python3
"""
Концепт «крупный пиксель» (НЕ подключён к приложению): та же Вера и Вера-дрон,
но в стиле «стикер»: сетка 32×36 вместо 64×72, толстый тёмный контур, светлая
обводка-стикер, ограниченная палитра, светящиеся акценты.

Результат: docs/concepts/chunky_*.png / *.gif
Запуск: python3 tools/concepts/gen_chunky.py
"""
import os
import random
import sys
from collections import Counter

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(ROOT, "tools"))
sys.path.insert(0, HERE)
import gen_sprite as g  # noqa: E402
import gen_concepts as c  # noqa: E402

LW, LH = 32, 36            # низкое разрешение
OUT_DIR = os.path.join(ROOT, "docs", "concepts")

INK = (14, 11, 20)          # толстый контур
STICKER = (236, 234, 244)   # обводка-стикер
NEON, NEON_B, NEON_D = c.NEON, c.NEON_B, c.NEON_D

# палитры: всё приводится к ближайшему цвету
PAL_VERA = [
    INK, (40, 36, 52), (64, 60, 82),                         # куртка
    (250, 248, 255), (206, 204, 228), (158, 150, 196),       # волосы
    (206, 146, 100), (168, 108, 72),                         # кожа
    (232, 56, 92), (250, 250, 255), (78, 70, 104),           # бант, рубашка, юбка
    (255, 112, 168),                                         # заколка
]
PAL_DRONE = [
    INK, (40, 38, 52), (70, 66, 90),
    (246, 246, 255), (196, 202, 232), (140, 148, 200),       # волосы: бело-голубой градиент
    (236, 128, 186),                                         # розовые кончики
    (176, 180, 198), (118, 122, 146),                        # металл
    NEON, NEON_B, (22, 18, 34),                              # неон, визор
]


def nearest(col, pal):
    return min(pal, key=lambda p: (p[0] - col[0]) ** 2 * 2 + (p[1] - col[1]) ** 2 * 3 + (p[2] - col[2]) ** 2)


def downscale(img, pal, keep=()):
    """64×72 → 32×36: в каждом блоке 2×2 — самый частый цвет (детали из keep в приоритете)."""
    src = img.load()
    out = Image.new("RGBA", (LW, LH), (0, 0, 0, 0))
    dst = out.load()
    for y in range(LH):
        for x in range(LW):
            px = [src[x * 2 + dx, y * 2 + dy] for dy in (0, 1) for dx in (0, 1)]
            solid = [p[:3] for p in px if p[3] > 40]
            if len(solid) < 2:
                continue
            special = [p for p in solid if p in keep]
            col = Counter(special or solid).most_common(1)[0][0]
            dst[x, y] = nearest(col, pal) + (255,)
    return out


def thicken(img):
    """Контур: тёмная кайма по краю силуэта + светлая обводка-стикер снаружи."""
    w, h = img.size
    base = img.copy()
    p = base.load()
    def solid(x, y):
        return 0 <= x < w and 0 <= y < h and p[x, y][3] > 0
    ink = Image.new("RGBA", (w + 4, h + 4), (0, 0, 0, 0))
    q = ink.load()
    # тёмная кайма внутри силуэта
    for y in range(h):
        for x in range(w):
            if not solid(x, y):
                continue
            edge = any(not solid(x + dx, y + dy) for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))
            q[x + 2, y + 2] = INK + (255,) if edge else p[x, y]
    # стикер снаружи (8-связность — мягкие углы)
    res = ink.copy()
    r = res.load()
    for y in range(h + 4):
        for x in range(w + 4):
            if q[x, y][3]:
                continue
            if any(0 <= x + dx < w + 4 and 0 <= y + dy < h + 4 and q[x + dx, y + dy][3]
                   for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
                r[x, y] = STICKER + (255,)
    return res


def stamp(img, x0, y0, rows, cmap, mirror_x=None):
    p = img.load()
    for dy, row in enumerate(rows):
        for dx, ch in enumerate(row):
            if ch in cmap:
                p[x0 + dx, y0 + dy] = cmap[ch] + (255,)
                if mirror_x is not None:
                    p[mirror_x - dx, y0 + dy] = cmap[ch] + (255,)


# ------------------------------------------------------------------ лица в крупном пикселе (координаты 32×36 + 2 на кайму)
VERA_EYES = {
    "normal": ["OOO", "WP.", "PD."],
    "smug": ["...", "OOO", "PD."],
    "happy": ["...", ".O.", "O.O"],
    "closed": ["...", "...", "OO."],
    "surprised": ["OO.", "WP.", "OO."],
}
VERA_MOUTH = {"smirk": ["OO", "W."], "open": ["OO", "MM"], "cat": ["O.O", ".O."], "flat": ["OO"]}
VERA_MAP = {"W": (255, 255, 255), "P": (214, 72, 160), "D": (110, 30, 96), "O": INK, "M": (150, 40, 70)}

DRONE_EYES = {
    "normal": ["BB", "PP", "PP"],
    "smug": ["..", "BB", "PP"],
    "happy": ["...", ".P.", "P.P"],
    "heart": ["P.P", "PBP", ".P."],
    "closed": ["..", "..", "PP"],
    "angry": ["P..", ".PP", "P.."],
}
DRONE_MOUTH = {"smirk": ["P..P", ".PP."], "cat": ["P.P.P"], "flat": ["PPP"]}
DRONE_MAP = {"P": NEON, "B": NEON_B}

O = 2  # смещение из-за каймы


def vera_face(img, eyes, mouth, blush):
    stamp(img, O + 10, O + 13, VERA_EYES[eyes], VERA_MAP, mirror_x=O + 21)
    # рваные кончики чёлки
    stamp(img, O + 9, O + 12, ["O..O...O"], {"O": INK}, mirror_x=O + 22)
    m = VERA_MOUTH[mouth]
    stamp(img, O + 16 - len(m[0]) // 2, O + 18, m, VERA_MAP)
    if blush:
        stamp(img, O + 10, O + 17, ["B"], {"B": (236, 104, 112)}, mirror_x=O + 21)


def drone_face(img, eyes, mouth):
    rows = DRONE_EYES[eyes]
    stamp(img, O + 11, O + 14, rows, DRONE_MAP, mirror_x=O + 20)
    m = DRONE_MOUTH[mouth]
    stamp(img, O + 16 - len(m[0]) // 2, O + 18, m, DRONE_MAP)


# ------------------------------------------------------------------ сборка без лица (лицо рисуем крупно поверх)
def vera_body_stand(pose):
    base = g.build_base(pose)
    full = base.copy()
    full.paste(g.build_front(base))
    full.paste(g.hand_layer(pose if pose in ("giggle", "think") else "idle", full))
    return full


def vera_body_floor(pose):
    return c.compose_floor(pose, face_fn=lambda e, m, u: g.Layer())


CHUNKY_DRONE = dict(c.DRONE_PALETTE, RIBBON=(70, 66, 86), RIBBON_D=(52, 48, 66))


def drone_body_stand(pose):
    with c.palette(CHUNKY_DRONE):
        base = g.build_base(pose)
        full = g.Layer()
        full.paste(c.tail_layer(sitting=False, r=2.2))
        full.paste(base)
        blank = g.Layer()
        blank.poly([(20, 24), (44, 24), (46, 27), (46, 34), (43, 38), (21, 38), (18, 34), (18, 27)], c.VISOR, "visor")
        full.paste(blank.outlined(full, merge=()))
        full.paste(g.build_front(base))
        full.paste(g.hand_layer(pose if pose in ("giggle", "think") else "idle", full))
        full.paste(c.drone_front(full))
    return full


def drone_body_floor(pose):
    def visor(e, m, under):
        blank = g.Layer()
        blank.poly([(20, 24), (44, 24), (46, 27), (46, 34), (43, 38), (21, 38), (18, 34), (18, 27)], c.VISOR, "visor")
        return blank.outlined(under, merge=())
    with c.palette(CHUNKY_DRONE):
        return c.compose_floor(pose, face_fn=visor,
                               extras={"behind": lambda: c.tail_layer(sitting=True, r=2.2), "front": c.drone_front})


KEEP_VERA = {(255, 112, 168), (204, 58, 124), (226, 52, 86)}
KEEP_DRONE = {NEON, NEON_B, NEON_D}


def chunky_vera(pose, eyes, mouth, blush, floor=False):
    layer = vera_body_floor(pose) if floor else vera_body_stand(pose)
    img = thicken(downscale(layer.image(1), PAL_VERA, KEEP_VERA))
    vera_face(img, eyes, mouth, blush)
    return img


def chunky_drone(pose, eyes, mouth, floor=False):
    layer = drone_body_floor(pose) if floor else drone_body_stand(pose)
    img = thicken(downscale(layer.image(1), PAL_DRONE, KEEP_DRONE))
    drone_face(img, eyes, mouth)
    # антенны над ушками-модулями: крупные, с неоновым кончиком
    stamp(img, O + 3, O + 7, ["P.", "BO", ".O", ".O"], {"P": NEON, "B": NEON_B, "O": INK}, mirror_x=O + 28)
    return img


# ------------------------------------------------------------------ превью
BG = c.BG


def sheet(items, scale, titles, seed=1):
    pad = 18
    cw, ch = (LW + 4) * scale, (LH + 4) * scale
    img = Image.new("RGBA", (len(items) * (cw + pad) + pad, ch + pad * 2 + 24), BG)
    d = ImageDraw.Draw(img)
    rnd = random.Random(seed)
    for i, it in enumerate(items):
        x0 = pad + i * (cw + pad)
        img.alpha_composite(it.resize((cw, ch), Image.NEAREST), (x0, pad))
        tw = d.textlength(titles[i], font=c.FONT)
        d.text((x0 + (cw - tw) / 2, pad + ch + 2), titles[i], fill=(220, 214, 236), font=c.FONT)
        if rnd.random() < 0.7:
            c.sparkle(d, x0 + rnd.choice([12, cw - 14]), pad + rnd.randint(ch // 3, ch - 40), scale // 2 + 1, c.SPARK + (255,))
    return img


def gif(make_frames, path, scale=10):
    frames = []
    for f in range(24):
        sprite = make_frames(f)
        img = Image.new("RGBA", ((LW + 4) * scale + 40, (LH + 4) * scale + 30), BG)
        bob = scale if (f // 6) % 2 else 0
        img.alpha_composite(sprite.resize(((LW + 4) * scale, (LH + 4) * scale), Image.NEAREST), (20, 15 + bob // 2))
        frames.append(img.convert("RGB"))
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=140, loop=0, optimize=True)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    s = 10
    vera = [
        (chunky_vera("idle", "normal", "smirk", 0), "спокойно"),
        (chunky_vera("giggle", "smug", "smirk", 1), "дразнится"),
        (chunky_vera("wave", "happy", "open", 1), "машет"),
        (chunky_vera("lap", "normal", "smirk", 0, floor=True), "на полу"),
        (chunky_vera("giggle", "smug", "smirk", 1, floor=True), "на полу, хи-хи"),
    ]
    sheet([v for v, _ in vera], s, [t for _, t in vera], seed=2).save(os.path.join(OUT_DIR, "chunky_vera.png"))

    drone = [
        (chunky_drone("idle", "normal", "smirk"), "спокойно"),
        (chunky_drone("giggle", "smug", "smirk"), "дразнится"),
        (chunky_drone("wave", "happy", "cat"), "радуется"),
        (chunky_drone("idle", "heart", "cat"), "смущена"),
        (chunky_drone("idle", "angry", "flat"), ">_<"),
        (chunky_drone("lap", "normal", "smirk", floor=True), "на полу"),
    ]
    sheet([v for v, _ in drone], s, [t for _, t in drone], seed=4).save(os.path.join(OUT_DIR, "chunky_drone.png"))

    # сравнение: обычный стиль → крупный пиксель
    pad = 20
    big = 5
    cbig = 9  # крупный пиксель в том же росте
    comp = Image.new("RGBA", (4 * ((LW + 4) * cbig + pad) + pad, (LH + 4) * cbig + pad * 2 + 24), BG)
    d = ImageDraw.Draw(comp)
    items = [
        (g.compose("idle", "normal", "smirk", 0).image(big), "сейчас"),
        (chunky_vera("idle", "normal", "smirk", 0).resize(((LW + 4) * cbig, (LH + 4) * cbig), Image.NEAREST), "крупный пиксель"),
        (c.drone_stand("normal", "smirk").image(big), "дрон сейчас"),
        (chunky_drone("idle", "normal", "smirk").resize(((LW + 4) * cbig, (LH + 4) * cbig), Image.NEAREST), "дрон крупный"),
    ]
    x = pad
    for im, t in items:
        comp.alpha_composite(im, (x, pad))
        tw = d.textlength(t, font=c.FONT)
        d.text((x + (im.width - tw) / 2, pad + (LH + 4) * cbig + 2), t, fill=(220, 214, 236), font=c.FONT)
        x += (LW + 4) * cbig + pad
    comp.save(os.path.join(OUT_DIR, "chunky_compare.png"))

    def vera_frames(f):
        blink = f in (7, 8, 19)
        eyes = "closed" if blink else ("normal" if f < 12 else "smug")
        return chunky_vera("lap" if f < 12 else "giggle", eyes, "smirk", 0 if f < 12 else 1, floor=True)
    gif(vera_frames, os.path.join(OUT_DIR, "chunky_vera_floor.gif"))

    def drone_frames(f):
        seq = ["normal", "heart", "smug", "happy"]
        eyes = "closed" if f in (5, 17) else seq[(f // 6) % 4]
        return chunky_drone("idle", eyes, "cat" if eyes in ("heart", "happy") else "smirk")
    gif(drone_frames, os.path.join(OUT_DIR, "chunky_drone.gif"))
    print("ok")


if __name__ == "__main__":
    main()
