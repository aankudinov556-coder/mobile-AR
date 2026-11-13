#!/usr/bin/env python3
"""
Концепт «Вера-дрон» v2 (НЕ подключён к приложению): оригинальная робо-девочка,
полностью нечеловеческая. Стиль — крупный пиксель (сетка 32×36), толстый тёмный
контур, светлая обводка-стикер, ограниченная палитра, неоновые светодиоды.

Дизайн: округлый шлем-голова с экраном-лицом (эмоции светодиодами), антенны с
огоньками, белая броня торса, графитовая куртка-панели, светящееся ядро-сердце,
тонкие металлические руки и ноги с шарнирами, хвост-кабель с сердечком-штекером.
Без крыльев.

Результат: docs/concepts/drone_*.png / *.gif
Запуск: python3 tools/concepts/gen_drone.py
"""
import os
import random

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT_DIR = os.path.join(ROOT, "docs", "concepts")
CW, CH = 32, 36  # рабочая сетка

# ---------------------------------------------------------------- палитра
INK = (14, 11, 20)
STICKER = (236, 236, 244)
H0 = (40, 37, 54)       # шлем
H1 = (58, 54, 78)
H2 = (96, 90, 124)      # блик по краю
HSPEC = (164, 160, 196)
SCREEN = (14, 11, 22)
GLOSS = (48, 42, 72)
ARM = (230, 232, 244)   # белая броня
ARM_S = (176, 180, 208)
ARM_D = (128, 132, 170)
MET = (150, 152, 184)   # металл рук/ног
MET_D = (98, 100, 134)
JKT = (36, 33, 48)
JKT_H = (70, 66, 94)
PINK = (255, 79, 168)
PINK_B = (255, 198, 228)
PINK_D = (138, 36, 92)

BG = (29, 26, 39, 255)
SPARK = (255, 128, 176, 255)
try:
    FONT = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 15)
except OSError:
    FONT = ImageFont.load_default()


def mx(x):
    return CW - 1 - x


class Canvas:
    def __init__(self):
        self.px = [[None] * CW for _ in range(CH)]

    def set(self, x, y, col, only=None):
        if 0 <= x < CW and 0 <= y < CH:
            if only is not None and self.px[y][x] not in only:
                return
            self.px[y][x] = col

    def get(self, x, y):
        return self.px[y][x] if 0 <= x < CW and 0 <= y < CH else None

    def rect(self, x0, y0, x1, y1, col, only=None):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                self.set(x, y, col, only)

    def ellipse(self, cx, cy, rx, ry, col, only=None):
        for y in range(int(cy - ry) - 1, int(cy + ry) + 2):
            for x in range(int(cx - rx) - 1, int(cx + rx) + 2):
                if ((x + 0.5 - cx) / rx) ** 2 + ((y + 0.5 - cy) / ry) ** 2 <= 1:
                    self.set(x, y, col, only)

    def poly(self, pts, col, only=None):
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        for y in range(int(min(ys)) - 1, int(max(ys)) + 2):
            for x in range(int(min(xs)) - 1, int(max(xs)) + 2):
                px, py = x + 0.5, y + 0.5
                inside = False
                j = len(pts) - 1
                for i in range(len(pts)):
                    xi, yi = pts[i]
                    xj, yj = pts[j]
                    if (yi > py) != (yj > py) and px < (xj - xi) * (py - yi) / (yj - yi) + xi:
                        inside = not inside
                    j = i
                if inside:
                    self.set(x, y, col, only)

    def line(self, x0, y0, x1, y1, col, only=None):
        dx, dy = abs(x1 - x0), -abs(y1 - y0)
        sx, sy = (1 if x0 < x1 else -1), (1 if y0 < y1 else -1)
        err = dx + dy
        while True:
            self.set(x0, y0, col, only)
            if x0 == x1 and y0 == y1:
                break
            e2 = 2 * err
            if e2 >= dy:
                err += dy
                x0 += sx
            if e2 <= dx:
                err += dx
                y0 += sy

    def stamp(self, x0, y0, rows, cmap, mirror=False):
        for dy, row in enumerate(rows):
            for dx, ch in enumerate(row):
                if ch in cmap:
                    self.set(x0 + dx, y0 + dy, cmap[ch])
                    if mirror:
                        self.set(mx(x0 + dx), y0 + dy, cmap[ch])

    def image(self, scale=1):
        """Картинка с толстым контуром и обводкой-стикером (поля по 2 клетки)."""
        w, h = CW + 4, CH + 4
        img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        p = img.load()

        def solid(x, y):
            return 0 <= x < CW and 0 <= y < CH and self.px[y][x] is not None

        for y in range(CH):
            for x in range(CW):
                if solid(x, y):
                    p[x + 2, y + 2] = self.px[y][x] + (255,)
        # тёмный контур снаружи силуэта (4-связность)
        ink = []
        for y in range(-1, CH + 1):
            for x in range(-1, CW + 1):
                if solid(x, y):
                    continue
                if any(solid(x + dx, y + dy) for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                    ink.append((x + 2, y + 2))
        for x, y in ink:
            p[x, y] = INK + (255,)
        # стикер (8-связность)
        stick = []
        for y in range(h):
            for x in range(w):
                if p[x, y][3]:
                    continue
                if any(0 <= x + dx < w and 0 <= y + dy < h and p[x + dx, y + dy][3]
                       for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
                    stick.append((x, y))
        for x, y in stick:
            p[x, y] = STICKER + (255,)
        return img.resize((w * scale, h * scale), Image.NEAREST) if scale != 1 else img


# ---------------------------------------------------------------- лицо-экран
LED = {"P": PINK, "B": PINK_B, "D": PINK_D}
EYES = {
    "normal": ["BP", "PP", "PP"],
    "smug": ["...", "PPP", ".PP"],
    "happy": [".P.", "P.P", "..."],
    "heart": ["P.P", "PPP", ".P."],
    "surprised": ["PPP", "P.P", "PPP"],
    "angry": ["P..", ".PP", "P.."],
    "closed": ["...", "PPP", "..."],
    "sleepy": ["...", "...", "PPP"],
    "look": [".BP", ".PP", ".PP"],
}
MOUTHS = {
    "smile": ["P..P", ".PP."],
    "grin": ["PPPP", ".PP."],
    "open": [".PP.", "P..P", ".PP."],
    "flat": [".PP."],
    "o": [".PP.", ".PP."],
    "wavy": ["P.P.", ".P.P"],
}


def draw_face(c, dy, eyes, mouth, blush=False):
    rows = EYES[eyes]
    c.stamp(10, 11 + dy, rows, LED, mirror=True)
    m = MOUTHS[mouth]
    c.stamp(14, 15 + dy, m, LED)
    if blush:
        c.set(9, 14 + dy, PINK_D)
        c.set(mx(9), 14 + dy, PINK_D)


# ---------------------------------------------------------------- части тела
def head(c, dy=0, antenna_on=True):
    # антенны: стебли + огоньки
    for sx in (False, True):
        X = (lambda x: mx(x)) if sx else (lambda x: x)
        c.line(X(9), 5 + dy, X(7), 2 + dy, MET_D)
        c.set(X(7), 1 + dy, PINK_B if antenna_on else PINK_D)
        c.set(X(6), 1 + dy, PINK if antenna_on else PINK_D)
        c.set(X(7), 0 + dy, PINK if antenna_on else PINK_D)
    # шлем
    c.ellipse(16, 12.5 + dy, 10.4, 8.8, H0)
    c.rect(7, 13 + dy, 24, 19 + dy, H0, only={H0})
    # объём: светлый край слева сверху, тень снизу
    c.ellipse(15, 11 + dy, 10, 8, H1, only={H0})
    c.ellipse(16, 12.8 + dy, 9.6, 8.2, H0, only={H1})
    for x, y in [(8, 6), (9, 5), (10, 4), (11, 4), (12, 3), (13, 3), (7, 7), (6, 9)]:
        c.set(x, y + dy, H2)
    c.set(11, 5 + dy, HSPEC)
    c.set(12, 5 + dy, HSPEC)
    # «наушники»-сенсоры по бокам
    for sx in (False, True):
        X = (lambda x: mx(x)) if sx else (lambda x: x)
        c.rect(X(5) if not sx else X(6), 11 + dy, X(6) if not sx else X(5), 15 + dy, JKT_H)
        c.set(X(5), 13 + dy, PINK)
    # экран-лицо с рамкой
    c.poly([(9, 8 + dy), (23, 8 + dy), (24, 9 + dy), (24, 17 + dy), (23, 18 + dy), (9, 18 + dy), (8, 17 + dy), (8, 9 + dy)], H2)
    c.poly([(10, 9 + dy), (22, 9 + dy), (23, 10 + dy), (23, 16 + dy), (22, 17 + dy), (10, 17 + dy), (9, 16 + dy), (9, 10 + dy)], SCREEN)
    c.set(10, 10 + dy, GLOSS)
    c.set(11, 10 + dy, GLOSS)
    c.set(10, 11 + dy, GLOSS)
    # наклейка-звёздочка на шлеме
    c.stamp(19, 4 + dy, [".P.", "PBP", ".P."], LED)


def torso(c, dy=0):
    c.rect(14, 20 + dy, 17, 21 + dy, MET_D)                          # шея-шарнир
    c.poly([(11, 21 + dy), (21, 21 + dy), (23, 23 + dy), (23, 28 + dy), (9, 28 + dy), (9, 23 + dy)], ARM)
    c.poly([(18, 21 + dy), (21, 21 + dy), (23, 23 + dy), (23, 28 + dy), (19, 28 + dy)], ARM_S, only={ARM})
    c.rect(10, 27 + dy, 22, 27 + dy, ARM_S, only={ARM, ARM_S})
    # куртка-панели (оверсайз), с неоновой окантовкой
    for sx in (False, True):
        pts = [(9, 22), (12, 21), (13, 28), (9, 28)]
        if sx:
            pts = [(CW - x, y) for x, y in pts]
        c.poly([(x, y + dy) for x, y in pts], JKT)
    c.line(12, 21 + dy, 13, 27 + dy, JKT_H)
    c.line(mx(12), 21 + dy, mx(13), 27 + dy, JKT_H)
    for x in (9, 10, 11, 12, mx(9), mx(10), mx(11), mx(12)):
        c.set(x, 28 + dy, PINK)
    # воротник и ядро-сердце
    c.rect(13, 21 + dy, 18, 21 + dy, JKT)
    c.set(15, 21 + dy, PINK)
    c.set(16, 21 + dy, PINK)
    c.stamp(14, 23 + dy, ["P..P", "PBPP", ".PP."], LED)


def _arm(c, pts, dy, mirror=False):
    P = [((CW - x) if mirror else x, y + dy) for x, y in pts]
    c.poly(P, MET)


def arm_down(c, right, dy=0):
    _arm(c, [(7, 22), (9.6, 22), (8.6, 27), (6, 27)], dy, mirror=right)
    X = (lambda x: mx(x)) if right else (lambda x: x)
    c.set(X(7), 24 + dy, MET_D)
    c.ellipse(CW - 6.5 if right else 6.5, 28 + dy, 1.8, 1.4, ARM)
    c.set(X(6), 28 + dy, ARM_S)


def arm_wave(c, dy=0):
    _arm(c, [(22.5, 22), (24.5, 23.5), (27.6, 18.5), (25.6, 17)], dy)
    c.set(25, 20 + dy, MET_D)
    c.ellipse(27, 15.6 + dy, 2, 2, ARM)
    c.set(26, 13 + dy, ARM)
    c.set(28, 13 + dy, ARM)
    c.set(28, 16 + dy, ARM_S)


def arm_to_face(c, dy=0):
    """«Хи-хи»: ладошка прикрывает низ экрана."""
    _arm(c, [(7.5, 23), (9.8, 24), (13.6, 18.6), (11.6, 17.4)], dy)
    c.set(10, 21 + dy, MET_D)
    c.ellipse(13.6, 16.6 + dy, 2.1, 1.7, ARM)
    c.set(15, 17 + dy, ARM_S)


def arm_chin(c, dy=0):
    _arm(c, [(7.5, 23), (9.8, 24), (12.6, 20.6), (10.6, 19.4)], dy, mirror=True)
    c.ellipse(CW - 12.6, 19.2 + dy, 1.9, 1.4, ARM)


def lower_stand(c):
    c.poly([(9, 28), (23, 28), (25, 30), (7, 30)], JKT)               # бронеюбка
    c.line(8, 29, 24, 29, JKT_H)
    c.set(7, 30, PINK)
    c.set(mx(7), 30, PINK)
    for x0 in (12, 18):
        c.rect(x0, 31, x0 + 1, 32, MET)
        c.set(x0, 32, MET_D)
    for sx in (False, True):
        pts = [(10, 33), (14, 33), (15, 34), (15, 35), (10, 35)]
        if sx:
            pts = [(CW - x, y) for x, y in pts]
        c.poly(pts, JKT)
    for x in (10, 11, 12, 13, 14, mx(10), mx(11), mx(12), mx(13), mx(14)):
        c.set(x, 35, PINK)


def lower_floor(c, dy):
    """Сидит на полу: ноги поджаты, бронеюбка лежит на полу, колени-шарниры спереди."""
    c.poly([(9, 28 + dy), (23, 28 + dy), (27, 31 + dy), (26, 33 + dy), (6, 33 + dy), (5, 31 + dy)], JKT)
    c.line(7, 30 + dy, 25, 30 + dy, JKT_H)
    c.set(6, 32 + dy, PINK)
    c.set(mx(6), 32 + dy, PINK)
    for cx in (12.5, CW - 12.5):                                         # колени-шарниры
        c.ellipse(cx, 32 + dy, 2.1, 1.3, ARM)
        c.set(int(cx), 32 + dy, ARM_S)
    for sx in (False, True):                                             # ступни поджаты по бокам
        X = (lambda x: mx(x)) if sx else (lambda x: x)
        c.set(X(5), 32 + dy, JKT)
        c.set(X(4), 33 + dy, JKT)
        c.set(X(5), 33 + dy, JKT)
        c.set(X(4), 33 + dy, PINK)


def hands_lap(c, dy):
    for right in (False, True):
        X = (lambda x: mx(x)) if right else (lambda x: x)
        _arm(c, [(7, 22.5), (9.5, 22), (11.5, 27.5), (9.2, 28)], dy, mirror=right)
        c.ellipse(CW - 11.5 if right else 11.5, 28.6 + dy, 1.9, 1.3, ARM)


def tail(c, floor=False, frame=0):
    """Хвост-кабель: сегменты + сердечко-штекер. frame — покачивание кончика."""
    if floor:
        pts = [(22, 33), (26, 35), (29, 34), (30, 31), (30, 28 - frame)]
        tip = (29, 25 - frame)
    else:
        pts = [(22, 27), (25, 30), (28, 29), (29, 26), (29, 23 - frame)]
        tip = (28, 20 - frame)
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        c.line(x0, y0, x1, y1, JKT)
        c.line(x0 + 1, y0, x1 + 1, y1, JKT)
    for i, (x, y) in enumerate(pts[1:-1]):
        c.set(x, y, JKT_H)
    c.rect(tip[0] + 1, tip[1] + 3, tip[0] + 2, tip[1] + 3, MET_D)        # коннектор
    c.stamp(tip[0], tip[1], ["P.P", "PBP", ".P."], LED)


# ---------------------------------------------------------------- сборка
def drone(pose="idle", eyes="normal", mouth="smile", floor=False, blush=False, frame=0, antenna_on=True):
    c = Canvas()
    dy = 3 if floor else 0
    tail(c, floor, frame)
    if floor:
        lower_floor(c, dy)
    else:
        lower_stand(c)
    torso(c, dy)
    if floor and pose in ("idle", "lap"):
        hands_lap(c, dy)
    else:
        if pose != "giggle":
            arm_down(c, False, dy)
        if pose not in ("wave", "think"):
            arm_down(c, True, dy)
        if pose == "wave":
            arm_wave(c, dy)
    head(c, dy, antenna_on)
    draw_face(c, dy, eyes, mouth, blush)
    if pose == "giggle":
        arm_to_face(c, dy)
    if pose == "think":
        arm_chin(c, dy)
    return c


# ---------------------------------------------------------------- превью
def sparkle(d, x, y, s):
    d.polygon([(x, y - 2 * s), (x + s // 2 + 1, y - s // 2 - 1), (x + 2 * s, y), (x + s // 2 + 1, y + s // 2 + 1),
               (x, y + 2 * s), (x - s // 2 - 1, y + s // 2 + 1), (x - 2 * s, y), (x - s // 2 - 1, y - s // 2 - 1)], fill=SPARK)


def sheet(items, scale, path, seed=1, sparks=True):
    pad = 18
    cw, ch = (CW + 4) * scale, (CH + 4) * scale
    img = Image.new("RGBA", (len(items) * (cw + pad) + pad, ch + pad * 2 + 26), BG)
    d = ImageDraw.Draw(img)
    rnd = random.Random(seed)
    for i, (canvas, title) in enumerate(items):
        x0 = pad + i * (cw + pad)
        sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ImageDraw.Draw(sh).ellipse([x0 + cw * 0.18, pad + ch - scale * 3.2, x0 + cw * 0.82, pad + ch - scale * 0.6], fill=(8, 5, 14, 120))
        img.alpha_composite(sh)
        img.alpha_composite(canvas.image(scale), (x0, pad))
        tw = d.textlength(title, font=FONT)
        d.text((x0 + (cw - tw) / 2, pad + ch + 4), title, fill=(220, 214, 236), font=FONT)
        if sparks and rnd.random() < 0.8:
            sparkle(d, x0 + rnd.choice([scale * 2, cw - scale * 2]), pad + rnd.randint(ch // 4, ch // 2), scale // 2 + 2)
    img.save(path)


def gif(frames_fn, path, scale=10, n=32):
    frames = []
    cw, ch = (CW + 4) * scale, (CH + 4) * scale
    for f in range(n):
        canvas, bob = frames_fn(f)
        img = Image.new("RGBA", (cw + 40, ch + 30), BG)
        sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ImageDraw.Draw(sh).ellipse([20 + cw * 0.18, 15 + ch - scale * 3.2, 20 + cw * 0.82, 15 + ch - scale * 0.6], fill=(8, 5, 14, 120))
        img.alpha_composite(sh)
        img.alpha_composite(canvas.image(scale), (20, 15 + bob))
        frames.append(img.convert("RGB"))
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=130, loop=0, optimize=True)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    s = 10
    sheet([
        (drone("idle", "normal", "smile"), "спокойно"),
        (drone("giggle", "smug", "grin", blush=True), "дразнится"),
        (drone("wave", "happy", "open"), "машет"),
        (drone("think", "look", "flat"), "думает"),
        (drone("idle", "surprised", "o"), "удивлена"),
    ], s, os.path.join(OUT_DIR, "drone_poses.png"), seed=3)

    sheet([
        (drone("idle", "heart", "smile", blush=True), "смущена ♥"),
        (drone("idle", "angry", "wavy"), ">_<"),
        (drone("idle", "closed", "flat", antenna_on=False), "спит"),
        (drone("idle", "smug", "smile"), "ухмылка"),
        (drone("idle", "happy", "grin"), "хи-хи"),
    ], s, os.path.join(OUT_DIR, "drone_emotions.png"), seed=5)

    sheet([
        (drone("lap", "normal", "smile", floor=True), "на полу"),
        (drone("giggle", "smug", "grin", floor=True, blush=True), "на полу, хи-хи"),
        (drone("wave", "happy", "open", floor=True), "на полу, машет"),
        (drone("lap", "sleepy", "flat", floor=True, antenna_on=False), "задремала"),
    ], s, os.path.join(OUT_DIR, "drone_floor.png"), seed=7)

    def idle(f):
        blink = f in (8, 9, 24)
        eyes = "closed" if blink else ("normal" if f < 16 else "smug")
        return drone("idle" if f < 16 else "giggle", eyes, "smile" if f < 16 else "grin", frame=(f // 4) % 2,
                     antenna_on=(f // 3) % 4 != 0, blush=f >= 16), (s if (f // 8) % 2 else 0)
    gif(idle, os.path.join(OUT_DIR, "drone_idle.gif"))

    def sit(f):
        seq = ["normal", "heart", "happy", "normal"]
        eyes = "closed" if f in (6, 7, 22) else seq[(f // 8) % 4]
        return drone("lap", eyes, "smile", floor=True, frame=(f // 4) % 2, antenna_on=(f // 3) % 4 != 0,
                     blush=eyes == "heart"), (s if (f // 8) % 2 else 0)
    gif(sit, os.path.join(OUT_DIR, "drone_floor.gif"))
    print("ok")


if __name__ == "__main__":
    main()
