#!/usr/bin/env python3
"""
Концепты (НЕ подключены к приложению) — только для просмотра:
  1) Вера сидит на полу (ноги поджаты, ладошки на коленях, тень, искорки);
  2) скин «Вера-дрон» — оригинальная робо-девочка: визор-экран с эмоциями,
     антенны-ушки, хвост-кабель с сердечком-штекером, графит + розовый неон.

Результат: docs/concepts/*.png и *.gif
Запуск: python3 tools/concepts/gen_concepts.py
"""
import math
import os
import random
import sys
from contextlib import contextmanager

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(ROOT, "tools"))
import gen_sprite as g  # noqa: E402

Layer, mx, mirror_poly = g.Layer, g.mx, g.mirror_poly
W, H = g.W, g.H
OUT_DIR = os.path.join(ROOT, "docs", "concepts")

BG = (29, 26, 39, 255)
try:
    FONT = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 15)
except OSError:
    FONT = ImageFont.load_default()
FLOOR = (38, 34, 50, 255)
SPARK = (255, 128, 176)
NEON = (255, 79, 168)
NEON_B = (255, 196, 226)
NEON_D = (196, 38, 120)

# ============================================================================ палитра дрона
DRONE_PALETTE = dict(
    HAIR=(238, 238, 246), HAIR_S=(200, 198, 218), HAIR_D=(236, 128, 186), HAIR_H=(255, 255, 255),
    SKIN=(176, 180, 198), SKIN_S=(122, 126, 148), SKIN_L=(214, 218, 232),
    JACKET=(50, 48, 62), JACKET_S=(33, 31, 43), JACKET_H=(86, 82, 104),
    SHIRT=(70, 66, 86), SHIRT_S=(52, 48, 66),
    RIBBON=NEON, RIBBON_D=NEON_D,
    SKIRT=(60, 58, 74), SKIRT_S=(42, 40, 54),
    BOOT=(30, 28, 38), BOOT_H=NEON,
    SOCK=(62, 60, 76),
    CLIP=NEON, CLIP_D=NEON_D,
    BLUSH=NEON, BLUSH_L=NEON_D,
)


@contextmanager
def palette(over):
    """Временная подмена цветов генератора (функции gen_sprite читают их во время вызова)."""
    saved = {k: getattr(g, k) for k in over}
    for k, v in over.items():
        setattr(g, k, v)
    try:
        yield
    finally:
        for k, v in saved.items():
            setattr(g, k, v)


# ============================================================================ поза «на полу»
def floor_boots():
    """Ботинки, выглядывающие по бокам из-под юбки (ноги поджаты под себя)."""
    l = Layer()
    for sx in (0, 1):
        pts = [(13, 62), (19, 61), (21, 64), (20, 66), (12, 66), (11, 64)]
        l.poly(mirror_poly(pts) if sx else pts, g.BOOT, "boot")
        hl = [(13, 63), (14, 63), (15, 63)]
        l.pts([(mx(x), y) for x, y in hl] if sx else hl, g.BOOT_H, "boot")
    return l


def floor_body():
    """Торс + юбка, раскрытая по полу."""
    l = g.body("idle", sitting=True)
    skirt = [(22, 57), (42, 57), (46, 60), (50, 63), (48, 66), (16, 66), (14, 63), (18, 60)]
    l.poly(skirt, g.SKIRT, "skirt")
    for x in (18, 23, 28, 36, 41, 46):
        l.vline(x, 60, 65, g.SKIRT_S, "skirt", clip={"skirt"})
    l.hline(15, 49, 65, g.SKIRT_S, "skirt", clip={"skirt"})
    l.hline(21, 43, 57, g.SKIRT_S, "skirt", clip={"skirt"})
    return l


def floor_knees():
    """Колени спереди — над подолом юбки."""
    l = Layer()
    for cx in (28.3, W - 28.3):
        l.ellipse(cx, 64.6, 3.1, 2.1, g.SKIN, "skin")
    for x0 in (26, mx(30)):
        l.hline(x0, x0 + 4, 66, g.SKIN_S, "skin", clip={"skin"})
    l.pts([(27, 63), (28, 63), (mx(27), 63), (mx(28), 63)], g.SKIN_L, "skin", clip={"skin"})
    return l


LAP_SLEEVE = [(23, 42), (25, 46), (29, 54), (28, 57), (23, 57), (18, 51), (20, 44)]


def arm_lap(right=False):
    """Рука опущена на колени, ладошка лежит на юбке."""
    l = Layer()
    l.ellipse(W - 29.5 if right else 29.5, 58.2, 2.7, 2.1, g.SKIN, "skin")
    l.poly(mirror_poly(LAP_SLEEVE) if right else LAP_SLEEVE, g.JACKET, "jacket")
    cuff = [(22, 55), (28, 55), (28, 57), (23, 57)]
    l.poly(mirror_poly(cuff) if right else cuff, g.JACKET_S, "jacket", clip={"jacket"})
    edge = [(21, 54), (22, 54), (23, 54), (24, 54), (25, 54), (26, 54), (27, 54)]
    l.pts([(mx(x), y) for x, y in edge] if right else edge, g.JACKET_H, "jacket", clip={"jacket"})
    return l


def compose_floor(pose="lap", eyes="normal", mouth="smirk", blush=0, face_fn=None, extras=None):
    base = Layer()
    if extras and "behind" in extras:
        base.paste(extras["behind"]())
    base.paste(g.hair_back().outlined())
    base.paste(floor_boots().outlined())
    if pose == "giggle":
        base.paste(g.arm_left_raise_to_mouth().outlined(base, merge=()))
    base.paste(floor_body().outlined())
    base.paste(floor_knees().outlined(base, merge=()))
    if pose != "giggle":
        base.paste(arm_lap(False).outlined())
    if pose == "wave":
        base.paste(g.arm_right_wave().outlined())
    elif pose == "think":
        base.paste(g.arm_right_raise_to_chin().outlined())
    else:
        base.paste(arm_lap(True).outlined())
    base.paste(g.neck().outlined(base, merge={"jacket", "shirt", "ribbon"}))
    base.paste(g.face().outlined(base, merge={"skin"}))
    base.hline(29, 34, 40, g.SKIN_S, "skin", clip={"skin"})
    full = base
    if face_fn:
        full.paste(face_fn(eyes, mouth, full))
    else:
        full.paste(g.eyes_layer(eyes))
        full.paste(g.mouth_layer(mouth))
        full.paste(g.blush_layer(blush))
    full.paste(g.build_front(base))
    hand_pose = {"giggle": "giggle", "think": "think"}.get(pose, "idle")
    full.paste(g.hand_layer(hand_pose, full))
    if extras and "front" in extras:
        full.paste(extras["front"](full))
    return full


# ============================================================================ Вера-дрон
VISOR = (22, 18, 34)
VISOR_HL = (64, 58, 90)

LED_EYES = {
    "normal": [".PPPP.", "PBBBBP", "PBBBBP", "PBBBBP", ".PPPP."],
    "smug":   ["......", "PPPPP.", ".BBBBP", "..PPPP", "......"],
    "happy":  ["......", "..PP..", ".P..P.", "P....P", "......"],
    "surprised": [".PPPP.", "P....P", "P.BB.P", "P....P", ".PPPP."],
    "angry":  ["P.....", ".PP...", "...PP.", ".PP...", "P....."],
    "closed": ["......", "......", "PPPPPP", "......", "......"],
    "heart":  ["PP.PP.", "PBPBP.", "PBBBP.", ".PBP..", "..P..."],
}
LED_MOUTH = {
    "smirk": (30, ["P...P", ".PPP."]),
    "open":  (30, [".PPP.", "P...P", ".PPP."]),
    "flat":  (30, ["PPPPP"]),
    "cat":   (29, ["P.P.P.P", ".P...P."]),
    "o":     (31, [".PP.", "P..P", ".PP."]),
}
LED_MAP = {"P": NEON, "B": NEON_B, "D": NEON_D}


def visor_layer(eyes, mouth, under):
    l = Layer()
    # экран во всё лицо, скруглённый
    l.poly([(20, 24), (44, 24), (46, 27), (46, 34), (43, 38), (21, 38), (18, 34), (18, 27)], VISOR, "visor")
    v = l.outlined(under, merge=())
    # блик
    v.pts([(21, 26), (22, 26), (23, 26), (21, 27), (20, 28)], VISOR_HL, "visor")
    # «глаза»
    rows = LED_EYES.get(eyes, LED_EYES["normal"])
    for dy, row in enumerate(rows):
        for dx, ch in enumerate(row):
            if ch in LED_MAP:
                v.set(22 + dx, 28 + dy, LED_MAP[ch], "led")
                # правый глаз: зеркально (для > < и сердечек тоже красиво)
                v.set(mx(22 + dx), 28 + dy, LED_MAP[ch], "led")
    x0, mrows = LED_MOUTH.get(mouth, LED_MOUTH["smirk"])
    for dy, row in enumerate(mrows):
        for dx, ch in enumerate(row):
            if ch in LED_MAP:
                v.set(x0 + dx, 34 + dy, LED_MAP[ch], "led")
    return v


def thick_curve(layer, pts, r, col, tag):
    """Толстая кривая Безье через контрольные точки."""
    (x0, y0), (x1, y1), (x2, y2), (x3, y3) = pts
    for i in range(80):
        t = i / 79
        a = (1 - t) ** 3
        b = 3 * (1 - t) ** 2 * t
        c = 3 * (1 - t) * t * t
        d = t ** 3
        x = a * x0 + b * x1 + c * x2 + d * x3
        y = a * y0 + b * y1 + c * y2 + d * y3
        layer.ellipse(x, y, r, r, col, tag)
    return layer


def heart_tip(l, cx, cy):
    rows = [".P.P.", "PBPPP", "PPPPP", ".PPP.", "..P.."]
    for dy, row in enumerate(rows):
        for dx, ch in enumerate(row):
            if ch in LED_MAP:
                l.set(cx - 2 + dx, cy - 2 + dy, LED_MAP[ch], "tail_tip")


def tail_layer(sitting, r=1.35):
    """Хвост-кабель с сегментами и розовым сердечком-штекером."""
    l = Layer()
    if sitting:
        pts = [(42, 62), (52, 68), (60, 64), (58, 54)]
        tip = (58, 52)
    else:
        pts = [(40, 56), (50, 64), (58, 60), (57, 49)]
        tip = (57, 47)
    thick_curve(l, pts, r, DRONE_PALETTE["JACKET"], "tail")
    # сегменты-кольца
    (x0, y0), (x1, y1), (x2, y2), (x3, y3) = pts
    for i in range(1, 9):
        t = i / 9
        a, b, c, d = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3
        x = round(a * x0 + b * x1 + c * x2 + d * x3)
        y = round(a * y0 + b * y1 + c * y2 + d * y3)
        l.set(x, y, DRONE_PALETTE["JACKET_H"], "tail")
    l = l.outlined()
    # коннектор + сердечко
    l.rect(tip[0] - 1, tip[1] + 2, tip[0] + 1, tip[1] + 3, DRONE_PALETTE["SKIN_S"], "tail")
    heart_tip(l, tip[0], tip[1])
    return l


def drone_front(full):
    """Антенны-ушки, ядро на груди, неоновая окантовка."""
    l = Layer()
    for sx in (0, 1):
        def X(x):
            return mx(x) if sx else x
        # модуль-«ушко»
        for y in range(23, 30):
            for x in range(9, 13):
                l.set(X(x), y, DRONE_PALETTE["JACKET"], "ear")
        l.pts([(X(10), 24), (X(11), 24)], DRONE_PALETTE["JACKET_H"], "ear")
        l.set(X(10), 27, NEON, "ear")
        l.set(X(11), 27, NEON_B, "ear")
        # антенна
        for y in range(15, 23):
            l.set(X(10 - (23 - y) // 4), y, DRONE_PALETTE["SKIN_S"], "ear")
        l.set(X(8), 14, NEON, "ear")
        l.set(X(8), 13, NEON_B, "ear")
    out = l.outlined(full, merge=())
    # ядро на груди
    out.ellipse(32, 50.5, 1.8, 1.8, NEON, "core")
    out.set(32, 50, NEON_B, "core")
    # неоновая окантовка подола куртки и манжет (только по пикселям куртки)
    for x in range(20, 45):
        if full.tag(x, 58) == "jacket":
            out.set(x, 58, NEON, "trim")
    return out


def drone_stand(eyes="normal", mouth="smirk", pose="idle"):
    with palette(DRONE_PALETTE):
        base = g.build_base(pose)
        full = Layer()
        full.paste(tail_layer(sitting=False))
        full.paste(base)
        # суставы на ногах
        for x0 in (25, 34):
            full.hline(x0, x0 + 4, 63, DRONE_PALETTE["SKIN_S"], "skin", clip={"skin"})
        full.paste(visor_layer(eyes, mouth, full))
        full.paste(g.build_front(base))
        hand_pose = pose if pose in ("giggle", "think") else "idle"
        full.paste(g.hand_layer(hand_pose, full))
        full.paste(drone_front(full))
    return full


def drone_floor(eyes="normal", mouth="smirk", pose="lap"):
    with palette(DRONE_PALETTE):
        return compose_floor(
            pose, eyes, mouth, 0,
            face_fn=lambda e, m, under: visor_layer(e, m, under),
            extras={"behind": lambda: tail_layer(sitting=True), "front": drone_front},
        )


# ============================================================================ превью
def sparkle(d, x, y, s, col):
    d.polygon([(x, y - 2 * s), (x + s // 2 + 1, y - s // 2 - 1), (x + 2 * s, y), (x + s // 2 + 1, y + s // 2 + 1),
               (x, y + 2 * s), (x - s // 2 - 1, y + s // 2 + 1), (x - 2 * s, y), (x - s // 2 - 1, y - s // 2 - 1)], fill=col)


def shadow(img, cx, cy, rx, ry):
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=(10, 6, 16, 110))
    img.alpha_composite(sh)


def card(sprites, scale, titles=None, floor_line=None, sparks=0, seed=1, pad=16):
    w = len(sprites) * (W * scale + pad) + pad
    h = H * scale + pad * 2 + (26 if titles else 0)
    img = Image.new("RGBA", (w, h), BG)
    d = ImageDraw.Draw(img)
    rnd = random.Random(seed)
    for i, (layer, bottom) in enumerate(sprites):
        x0 = pad + i * (W * scale + pad)
        if floor_line is not None:
            shadow(img, x0 + W * scale // 2, pad + bottom * scale, 21 * scale, int(2.6 * scale))
        img.alpha_composite(layer.image(scale), (x0, pad))
        for _ in range(sparks):
            sx = x0 + rnd.choice([rnd.randint(2, 12), rnd.randint(52, 62)]) * scale
            sy = pad + rnd.randint(30, 60) * scale
            sparkle(d, sx, sy, max(2, scale), SPARK + (255,))
        if titles:
            tw = d.textlength(titles[i], font=FONT)
            d.text((x0 + (W * scale - tw) / 2, pad + H * scale + 4), titles[i], fill=(220, 214, 236), font=FONT)
    return img


def gif_floor(make, path, scale=6):
    frames = []
    rnd = random.Random(7)
    sparks = []
    for f in range(32):
        blink = f in (9, 10, 26)
        bob = 1 if (f // 8) % 2 else 0
        layer = make(blink, f)
        img = Image.new("RGBA", (W * scale + 40, H * scale + 30), BG)
        shadow(img, 20 + W * scale // 2, 12 + 66 * scale, 21 * scale, int(2.6 * scale))
        img.alpha_composite(layer.image(scale), (20, 12 + bob * scale))
        d = ImageDraw.Draw(img)
        if f % 6 == 0:
            sparks.append([rnd.choice([rnd.randint(4, 14), rnd.randint(50, 60)]), rnd.randint(34, 58), 0])
        for s in sparks:
            life = s[2]
            if life < 10:
                size = [1, 2, 3, 3, 2, 2, 1, 1, 1, 1][life]
                sparkle(d, 20 + s[0] * scale, 12 + (s[1] - life // 2) * scale, size * scale // 2 + 1, SPARK + (255,))
            s[2] += 1
        sparks = [s for s in sparks if s[2] < 10]
        frames.append(img.convert("RGB"))
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=120, loop=0, optimize=True)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    # --- 1. Вера на полу
    vera_floor = [
        (compose_floor("lap", "normal", "smirk", 0), 66),
        (compose_floor("giggle", "smug", "smirk", 1), 66),
        (compose_floor("wave", "happy", "laugh", 1), 66),
        (compose_floor("think", "up", "flat", 0), 66),
    ]
    card(vera_floor, 6, ["спокойно", "дразнится", "машет", "думает"], floor_line=True, sparks=2, seed=3).save(
        os.path.join(OUT_DIR, "vera_floor.png"))

    def vera_frame(blink, f):
        eyes = "closed" if blink else ("normal" if f < 16 else "smug")
        return compose_floor("lap" if f < 16 else "giggle", eyes, "smirk", 0 if f < 16 else 1)
    gif_floor(vera_frame, os.path.join(OUT_DIR, "vera_floor.gif"))

    # --- 2. Вера-дрон стоя: эмоции на визоре
    emotions = [("normal", "smirk", "idle", "спокойно"), ("smug", "smirk", "giggle", "дразнится"),
                ("happy", "cat", "wave", "радуется"), ("surprised", "o", "idle", "удивлена"),
                ("angry", "flat", "idle", ">_<"), ("heart", "cat", "idle", "смущена"), ("closed", "flat", "idle", "спит")]
    stand = [(drone_stand(e, m, p), 70) for e, m, p, _ in emotions]
    card(stand, 4, [t for *_, t in emotions], floor_line=True, sparks=0).save(os.path.join(OUT_DIR, "vera_drone.png"))

    # --- 3. Вера-дрон на полу
    dfloor = [(drone_floor("normal", "smirk", "lap"), 66), (drone_floor("smug", "smirk", "giggle"), 66),
              (drone_floor("happy", "cat", "wave"), 66)]
    card(dfloor, 6, ["спокойно", "дразнится", "машет"], floor_line=True, sparks=2, seed=5).save(
        os.path.join(OUT_DIR, "vera_drone_floor.png"))

    def drone_frame(blink, f):
        seq = ["normal", "normal", "heart", "smug"]
        eyes = "closed" if blink else seq[(f // 8) % 4]
        return drone_floor(eyes, "cat" if eyes == "heart" else "smirk", "giggle" if eyes == "smug" else "lap")
    gif_floor(drone_frame, os.path.join(OUT_DIR, "vera_drone_floor.gif"))

    # --- 4. сравнение: Вера и Вера-дрон
    compare = [(g.compose("idle", "normal", "smirk", 0), 70), (drone_stand("normal", "smirk"), 70),
               (compose_floor("lap", "normal", "smirk", 0), 66), (drone_floor("normal", "smirk"), 66)]
    card(compare, 5, ["Вера", "Вера-дрон", "Вера (пол)", "дрон (пол)"], floor_line=True).save(
        os.path.join(OUT_DIR, "compare.png"))
    print("ok")


if __name__ == "__main__":
    main()
