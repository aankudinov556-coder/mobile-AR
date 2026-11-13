#!/usr/bin/env python3
"""
Генератор пиксель-арт спрайта Веры (оригинальный чиби-персонаж).

Выход (1x, 64x72):
  assets/vera/base_<pose>.png   — задние волосы, тело, лицо (поза рук)
  assets/vera/eyes_<kind>.png
  assets/vera/mouth_<kind>.png
  assets/vera/blush_<lvl>.png
  assets/vera/front.png         — чёлка, боковые пряди, заколка
  assets/vera/hand_<pose>.png   — рука поверх лица (для поз)
  build/icon.ico, build/icon.png, assets/tray.png, assets/icon.png
  tools/preview/*.png                         — превью (не в git)

Запуск: python3 tools/gen_sprite.py
"""
import os
from PIL import Image

W, H = 64, 72
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "assets", "vera")   # общие слои для ПК и Android
ASSETS = os.path.join(ROOT, "assets")
BUILD = os.path.join(ROOT, "build")
PREVIEW = os.path.join(ROOT, "tools", "preview")

# ---------------------------------------------------------------- palette
OUT = (44, 30, 52)
HAIR = (246, 244, 252)
HAIR_S = (214, 210, 232)
HAIR_D = (172, 164, 204)
HAIR_H = (255, 255, 255)
SKIN = (205, 145, 98)
SKIN_S = (176, 116, 76)
SKIN_L = (226, 172, 126)
BLUSH = (236, 104, 112)
BLUSH_L = (228, 132, 120)
IRIS = (196, 72, 156)
IRIS_D = (112, 36, 104)
IRIS_L = (250, 150, 214)
WHITE = (255, 255, 255)
PUPIL = (46, 20, 56)
JACKET = (44, 40, 58)
JACKET_S = (30, 27, 42)
JACKET_H = (72, 66, 94)
SHIRT = (246, 245, 252)
SHIRT_S = (206, 203, 224)
RIBBON = (226, 52, 86)
RIBBON_D = (158, 30, 62)
SKIRT = (74, 66, 98)
SKIRT_S = (54, 48, 74)
BOOT = (38, 33, 48)
BOOT_H = (82, 74, 102)
SOCK = (236, 234, 244)
CLIP = (255, 112, 168)
CLIP_D = (204, 58, 124)
MOUTH = (118, 34, 58)
TONGUE = (232, 104, 120)


def mx(x):
    """Зеркальная координата относительно центра персонажа."""
    return W - 1 - x


# ---------------------------------------------------------------- canvas
class Layer:
    def __init__(self):
        self.c = [[None] * W for _ in range(H)]  # (rgb, tag)

    def copy(self):
        l = Layer()
        l.c = [row[:] for row in self.c]
        return l

    def get(self, x, y):
        if 0 <= x < W and 0 <= y < H:
            return self.c[y][x]
        return None

    def set(self, x, y, col, tag=None, clip=None):
        if 0 <= x < W and 0 <= y < H:
            if clip is not None:
                cur = self.c[y][x]
                if cur is None or cur[1] not in clip:
                    return
            self.c[y][x] = (col, tag)

    def tag(self, x, y):
        v = self.get(x, y)
        return v[1] if v else None

    def ellipse(self, cx, cy, rx, ry, col, tag=None, clip=None):
        for y in range(int(cy - ry) - 1, int(cy + ry) + 2):
            for x in range(int(cx - rx) - 1, int(cx + rx) + 2):
                dx = (x + 0.5 - cx) / rx
                dy = (y + 0.5 - cy) / ry
                if dx * dx + dy * dy <= 1.0:
                    self.set(x, y, col, tag, clip)

    def poly(self, pts, col, tag=None, clip=None):
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        for y in range(int(min(ys)) - 1, int(max(ys)) + 2):
            for x in range(int(min(xs)) - 1, int(max(xs)) + 2):
                if point_in_poly(x + 0.5, y + 0.5, pts):
                    self.set(x, y, col, tag, clip)

    def rect(self, x0, y0, x1, y1, col, tag=None, clip=None):
        for y in range(y0, y1 + 1):
            for x in range(x0, x1 + 1):
                self.set(x, y, col, tag, clip)

    def hline(self, x0, x1, y, col, tag=None, clip=None):
        for x in range(min(x0, x1), max(x0, x1) + 1):
            self.set(x, y, col, tag, clip)

    def vline(self, x, y0, y1, col, tag=None, clip=None):
        for y in range(min(y0, y1), max(y0, y1) + 1):
            self.set(x, y, col, tag, clip)

    def pts(self, pts, col, tag=None, clip=None):
        for (x, y) in pts:
            self.set(x, y, col, tag, clip)

    def mirror_pts(self, pts, col, tag=None, clip=None):
        self.pts(pts, col, tag, clip)
        self.pts([(mx(x), y) for (x, y) in pts], col, tag, clip)

    def paste(self, other):
        for y in range(H):
            for x in range(W):
                if other.c[y][x] is not None:
                    self.c[y][x] = other.c[y][x]

    def outlined(self, under=None, merge=()):
        """Внешний 1px контур (4-связность). Не рисуется там, где под слоем лежит
        материал из merge (например, чёлка поверх массы волос)."""
        res = self.copy()
        for y in range(H):
            for x in range(W):
                if self.c[y][x] is not None:
                    continue
                near = False
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    v = self.get(x + dx, y + dy)
                    if v is not None and v[1] != "outline":
                        near = True
                        break
                if not near:
                    continue
                if under is not None and under.tag(x, y) in merge:
                    continue
                res.c[y][x] = (OUT, "outline")
        return res

    def image(self, scale=1):
        img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        p = img.load()
        for y in range(H):
            for x in range(W):
                v = self.c[y][x]
                if v is not None:
                    col = v[0]
                    p[x, y] = (col[0], col[1], col[2], col[3] if len(col) > 3 else 255)
        if scale != 1:
            img = img.resize((W * scale, H * scale), Image.NEAREST)
        return img


def point_in_poly(x, y, pts):
    inside = False
    n = len(pts)
    j = n - 1
    for i in range(n):
        xi, yi = pts[i]
        xj, yj = pts[j]
        if (yi > y) != (yj > y):
            xint = (xj - xi) * (y - yi) / (yj - yi) + xi
            if x < xint:
                inside = not inside
        j = i
    return inside


def mirror_poly(pts):
    return [(W - x, y) for (x, y) in pts]


def stamp(layer, x0, y0, rows, cmap, mirror=False):
    """Рисует ASCII-шаблон. mirror=True — зеркально (для второго глаза)."""
    for dy, row in enumerate(rows):
        for dx, ch in enumerate(row):
            if ch in (" ", "."):
                continue
            col = cmap[ch]
            if mirror:
                x = mx(x0 + dx)
            else:
                x = x0 + dx
            layer.set(x, y0 + dy, col, "feature")


# ---------------------------------------------------------------- parts
def hair_back():
    l = Layer()
    # масса волос на голове
    l.ellipse(32, 21, 20.5, 17.5, HAIR, "hair")
    # волосы сзади до плеч с рваными, чуть разлетающимися концами
    left = [(32, 22), (13, 21), (11, 29), (10, 36), (7, 44), (11, 43), (11, 48),
            (15, 45), (17, 50), (19, 45), (22, 47), (25, 43), (32, 45)]
    right = [(W - x, y) for (x, y) in reversed(left)]
    l.poly(left + right, HAIR, "hair")
    # тень: нижняя часть задних волос
    l.poly([(4, 31), (24, 31), (24, 52), (4, 52)], HAIR_S, "hair", clip={"hair"})
    l.poly(mirror_poly([(4, 31), (24, 31), (24, 52), (4, 52)]), HAIR_S, "hair", clip={"hair"})
    l.poly([(4, 41), (24, 39), (24, 52), (4, 52)], HAIR_D, "hair", clip={"hair"})
    l.poly(mirror_poly([(4, 41), (24, 39), (24, 52), (4, 52)]), HAIR_D, "hair", clip={"hair"})
    # «ахоге» — непослушная прядь сверху
    l.poly([(30, 5), (26, 0), (31, 2), (34, 5)], HAIR, "hair")
    return l


def face():
    l = Layer()
    # лицо: широкие щёки, мягкий подбородок
    l.ellipse(32, 28, 14, 11.5, SKIN, "skin")
    l.poly([(20, 32), (44, 32), (39, 39), (34, 41), (30, 41), (25, 39)], SKIN, "skin")
    # тень под чёлкой
    l.hline(19, 44, 18, SKIN_S, "skin", clip={"skin"})
    l.hline(19, 44, 19, SKIN_S, "skin", clip={"skin"})
    # мягкий свет на щеках
    return l


def neck():
    l = Layer()
    l.rect(29, 39, 34, 42, SKIN_S, "skin")
    return l


def body(pose, sitting=False):
    l = Layer()
    # куртка-оверсайз (капюшон за шеей)
    l.ellipse(32, 42.5, 10.5, 3, JACKET_S, "jacket")
    torso = [(23, 42), (41, 42), (44, 59), (20, 59)]
    l.poly(torso, JACKET, "jacket")
    # рубашка в вырезе
    l.poly([(28, 41), (36, 41), (35, 58), (29, 58)], SHIRT, "shirt")
    l.poly([(28, 41), (30, 41), (31, 58), (29, 58)], SHIRT_S, "shirt", clip={"shirt"})
    # лацканы
    l.poly([(26, 42), (29, 42), (31, 50), (29, 58), (27, 58)], JACKET_H, "jacket")
    l.poly(mirror_poly([(26, 42), (29, 42), (31, 50), (29, 58), (27, 58)]), JACKET, "jacket")
    l.vline(27, 48, 58, JACKET_S, "jacket")
    l.vline(mx(27), 48, 58, JACKET_S, "jacket")
    # бантик
    l.pts([(30, 42), (31, 43), (32, 43), (33, 42), (30, 44), (33, 44)], RIBBON, "ribbon")
    l.pts([(29, 42), (29, 43), (29, 44), (34, 42), (34, 43), (34, 44)], RIBBON_D, "ribbon")
    l.pts([(31, 45), (32, 45), (31, 46), (32, 46), (30, 47), (33, 47)], RIBBON, "ribbon")
    l.pts([(31, 44), (32, 44)], RIBBON_D, "ribbon")
    # подол куртки
    l.hline(21, 43, 58, JACKET_S, "jacket", clip={"jacket"})
    # юбка
    if sitting:
        # сидя юбка расходится шире и ложится на край панели
        l.poly([(22, 57), (42, 57), (46, 62), (18, 62)], SKIRT, "skirt")
        for x in (23, 28, 35, 40):
            l.vline(x, 59, 62, SKIRT_S, "skirt", clip={"skirt"})
        l.hline(19, 45, 62, SKIRT_S, "skirt", clip={"skirt"})
        return l
    l.poly([(23, 57), (41, 57), (43, 62), (21, 62)], SKIRT, "skirt")
    for x in (25, 29, 34, 38):
        l.vline(x, 59, 62, SKIRT_S, "skirt", clip={"skirt"})
    # ноги
    l.rect(25, 62, 29, 66, SKIN, "skin")
    l.rect(34, 62, 38, 66, SKIN, "skin")
    l.vline(29, 62, 66, SKIN_S, "skin")
    l.vline(34, 62, 66, SKIN_S, "skin")
    # гольфы
    l.rect(25, 65, 29, 66, SOCK, "sock")
    l.rect(34, 65, 38, 66, SOCK, "sock")
    # ботинки
    l.poly([(24, 66), (30, 66), (31, 69), (31, 70), (23, 70), (23, 68)], BOOT, "boot")
    l.poly(mirror_poly([(24, 66), (30, 66), (31, 69), (31, 70), (23, 70), (23, 68)]), BOOT, "boot")
    l.hline(24, 26, 67, BOOT_H, "boot")
    l.hline(mx(24), mx(26), 67, BOOT_H, "boot")
    return l


SLEEVE_L = [(23, 42), (25, 46), (23, 57), (16, 57), (17, 50), (20, 44)]


def _sleeve(l, pts, mirror=False, shade=JACKET_S):
    P = mirror_poly(pts) if mirror else pts
    l.poly(P, JACKET, "jacket")
    # манжета
    cuff = [(15, 56), (24, 56), (24, 58), (15, 58)]
    l.poly(mirror_poly(cuff) if mirror else cuff, JACKET_S, "jacket", clip={"jacket"})
    edge = [(16, 55), (17, 55), (18, 55), (19, 55), (20, 55), (21, 55), (22, 55)]
    l.pts([(mx(x) if mirror else x, y) for (x, y) in edge], JACKET_H, "jacket", clip={"jacket"})
    fold = [(18, 49), (19, 49), (19, 50), (20, 51)]
    l.pts([(mx(x) if mirror else x, y) for (x, y) in fold], shade, "jacket", clip={"jacket"})


def arm_left_down():
    """Левая рука (со стороны зрителя) опущена: широкий рукав + ладошка."""
    l = Layer()
    l.ellipse(19.5, 58.8, 2.8, 2.4, SKIN, "skin")
    l.pts([(18, 60), (19, 60)], SKIN_S, "skin")
    _sleeve(l, SLEEVE_L)
    return l


def arm_right_down():
    l = Layer()
    l.ellipse(W - 19.5, 58.8, 2.8, 2.4, SKIN, "skin")
    l.pts([(mx(18), 60), (mx(19), 60)], SKIN_S, "skin")
    _sleeve(l, SLEEVE_L, mirror=True)
    return l


def arm_right_wave():
    """Правая рука поднята — машет."""
    l = Layer()
    l.poly([(40, 44), (44, 42), (49, 35), (55, 37), (50, 47), (44, 51)], JACKET, "jacket")
    l.poly([(48, 36), (49, 35), (55, 37), (54, 40), (49, 38)], JACKET_H, "jacket", clip={"jacket"})
    l.pts([(46, 45), (47, 44), (48, 43)], JACKET_S, "jacket", clip={"jacket"})
    l.ellipse(52.5, 32.5, 3, 3, SKIN, "skin")
    l.pts([(50, 29), (52, 28), (54, 29), (55, 31)], SKIN, "skin")
    l.pts([(51, 31), (53, 31)], SKIN_S, "skin")
    return l


def arm_left_raise_to_mouth():
    """Плечо руки, поднятой ко рту (сама кисть рисуется поверх лица отдельно)."""
    l = Layer()
    l.poly([(23, 42), (25, 46), (24, 52), (18, 52), (19, 46)], JACKET, "jacket")
    return l


def hand_mouth():
    l = Layer()
    # предплечье в рукаве — от локтя к лицу
    l.poly([(18, 52), (24, 53), (30, 43), (25, 41)], JACKET, "jacket")
    l.poly([(25, 41), (30, 43), (29, 45), (24, 43)], JACKET_H, "jacket", clip={"jacket"})
    # кисть прикрывает рот
    l.poly([(25, 37), (30, 36), (31, 38), (30, 41), (26, 41), (24, 39)], SKIN, "skin")
    l.pts([(27, 38), (28, 38), (29, 38)], SKIN_S, "skin")
    l.pts([(27, 40), (28, 40)], SKIN_S, "skin")
    return l


def hand_chin():
    """Задумчивость: кисть у подбородка."""
    l = Layer()
    l.poly(mirror_poly([(18, 52), (24, 53), (30, 44), (25, 42)]), JACKET, "jacket")
    l.poly(mirror_poly([(25, 42), (30, 44), (29, 46), (24, 44)]), JACKET_S, "jacket", clip={"jacket"})
    l.poly(mirror_poly([(25, 39), (30, 39), (31, 41), (29, 43), (25, 43), (24, 41)]), SKIN, "skin")
    l.pts([(36, 41), (37, 41)], SKIN_S, "skin")
    return l


def arm_right_raise_to_chin():
    l = Layer()
    l.poly(mirror_poly([(23, 42), (25, 46), (24, 52), (18, 52), (19, 46)]), JACKET, "jacket")
    return l


def front_hair():
    l = Layer()
    # чёлка: рваные пряди
    bang = [(14, 20), (15, 12), (20, 7), (32, 4), (44, 7), (49, 12), (50, 20),
            # нижний зигзаг справа налево
            (47, 26), (45, 20), (43, 24), (41, 19), (38, 25), (36, 19),
            (34, 23), (32, 18), (29, 25), (27, 19), (25, 23), (23, 19),
            (20, 25), (18, 20), (16, 27)]
    l.poly(bang, HAIR, "hair")
    # боковые пряди перед лицом
    lock = [(14, 18), (19, 21), (21, 30), (20, 38), (18, 45), (16, 41), (14, 44), (13, 34)]
    l.poly(lock, HAIR, "hair")
    l.poly(mirror_poly(lock), HAIR, "hair")
    # тени-пряди: от «впадин» чёлки вверх
    for (x, y, n) in [(18, 20, 3), (23, 19, 4), (27, 19, 2), (32, 18, 4), (36, 19, 2), (41, 19, 4), (45, 20, 3)]:
        for k in range(1, n + 1):
            l.set(x, y - k, HAIR_S, "hair", clip={"hair"})
    # кончики прядей — чуть темнее
    for (x, y) in [(20, 24), (25, 22), (29, 24), (34, 22), (38, 24), (43, 23)]:
        l.set(x, y - 1, HAIR_S, "hair", clip={"hair"})
    l.poly([(16, 30), (19, 32), (18, 44), (16, 40)], HAIR_S, "hair", clip={"hair"})
    l.poly(mirror_poly([(16, 30), (19, 32), (18, 44), (16, 40)]), HAIR_S, "hair", clip={"hair"})
    l.poly([(15, 20), (17, 19), (16, 26)], HAIR_S, "hair", clip={"hair"})
    l.poly(mirror_poly([(15, 20), (17, 19), (16, 26)]), HAIR_S, "hair", clip={"hair"})
    # блик-«нимб»
    l.mirror_pts([(18, 12), (19, 11), (20, 11), (21, 10), (22, 10), (24, 9), (25, 9), (26, 9)], HAIR_H, "hair")
    l.mirror_pts([(19, 12), (20, 12), (22, 11), (23, 11)], HAIR_S, "hair", clip={"hair"})
    # тень у основания чёлки
    l.mirror_pts([(16, 14), (16, 15), (15, 16)], HAIR_S, "hair", clip={"hair"})
    return l


def clip_layer():
    l = Layer()
    # заколка-звёздочка
    cx, cy = 17, 17
    l.pts([(cx, cy - 2), (cx, cy - 1), (cx - 2, cy), (cx - 1, cy), (cx, cy), (cx + 1, cy), (cx + 2, cy),
           (cx, cy + 1), (cx, cy + 2)], CLIP, "clip")
    l.pts([(cx + 1, cy + 1), (cx - 1, cy - 1)], CLIP_D, "clip")
    l.set(cx, cy, WHITE, "clip")
    return l


# ---------------------------------------------------------------- eyes
EYE_MAP = {
    "L": OUT, "D": IRIS_D, "I": IRIS, "l": IRIS_L, "P": PUPIL, "H": WHITE, "W": WHITE,
    "S": SKIN_S, "B": BLUSH,
}
EYE_X = 21  # левый глаз (со стороны зрителя): x 21..28, правый — зеркально
EYE_Y = 25

EYES = {
    "normal": [
        "LLLLLLL.",
        ".LDDDDDL",
        "L.DHHPDL",
        "..DHPPDL",
        "..IPPPI.",
        "..IIIII.",
        "..IlIlI.",
        "..IlllI.",
        "...LLL..",
    ],
    "smug": [
        "........",
        "........",
        "LLLLLLL.",
        ".LLLLLLL",
        "..DPPHD.",
        "..IPPPI.",
        "..IlIlI.",
        "..IlllI.",
        "...LLL..",
    ],
    "happy": [
        "........",
        "........",
        "........",
        "...LLL..",
        "..L...L.",
        ".L.....L",
        "........",
        "........",
        "........",
    ],
    "closed": [
        "........",
        "........",
        "........",
        "........",
        "........",
        "........",
        "LL....LL",
        "..LLLL..",
        "........",
    ],
    "surprised": [
        "..LLLL..",
        ".LWWWWL.",
        "LWWDDWWL",
        "LWDHPDWL",
        "LWDPPDWL",
        "LWWIIWWL",
        ".LWWWWL.",
        "..LLLL..",
        "........",
    ],
    "angry": [
        "........",
        "LL......",
        ".LLLL...",
        "..LLLLLL",
        "..DPPHDL",
        "..IPPPI.",
        "..IlIlI.",
        "..IlllI.",
        "...LLL..",
    ],
    "side": [   # смотрит в сторону (застенчиво)
        "LLLLLLL.",
        ".LDDDDDL",
        "L.DDDHPL",
        "..DDPPPL",
        "..IIPPP.",
        "..IIIII.",
        "..IlIlI.",
        "..IlllI.",
        "...LLL..",
    ],
    "up": [     # смотрит вверх — думает
        "LLLLLLL.",
        ".LDPPHDL",
        "L.DPPPDL",
        "..DIPIDL",
        "..IIIII.",
        "..IIIII.",
        "..IlIlI.",
        "..IlllI.",
        "...LLL..",
    ],
}


def eyes_layer(kind):
    l = Layer()
    rows = EYES[kind]
    stamp(l, EYE_X, EYE_Y, rows, EYE_MAP)
    if kind == "side":
        # зрачки смотрят в одну сторону: правый глаз — та же радужка, но контур зеркальный
        mirrored = [r[::-1] for r in rows]
        iris_rows = []
        for r, m in zip(rows, mirrored):
            row = list(m)
            for i in range(8):
                if r[i] in "DIlPH" and m[i] in "DIlPH":
                    row[i] = r[i]
            iris_rows.append("".join(row))
        stamp(l, mx(EYE_X + 7), EYE_Y, iris_rows, EYE_MAP)
    else:
        stamp(l, EYE_X, EYE_Y, rows, EYE_MAP, mirror=True)
    return l


# ---------------------------------------------------------------- mouths
MOUTH_MAP = {"L": OUT, "M": MOUTH, "T": TONGUE, "W": WHITE, "S": SKIN_S}
MOUTH_Y = 36
MOUTHS = {
    # x-offset относительно 29
    "smirk": (29, [
        ".....L",
        "LLLLL.",
        ".W....",
    ]),
    "cat": (28, [
        "L.L.L.",
        ".L.L..",
    ]),
    "flat": (30, [
        "LLLL",
    ]),
    "open": (29, [
        ".LLLL.",
        "LMMMML",
        "LMTTML",
        ".LLLL.",
    ]),
    "open_fang": (29, [
        ".LLLLL",
        "LWMMML",
        "LMMTML",
        ".LLLL.",
    ]),
    "laugh": (28, [
        "LLLLLLLL",
        "LWMMMMWL",
        ".LMTTML.",
        "..LLLL..",
    ]),
    "o": (30, [
        ".LL.",
        "LMML",
        ".LL.",
    ]),
    "pout": (30, [
        ".LL.",
        "L..L",
    ]),
    "wobbly": (29, [
        "L.LL.L",
        ".L..L.",
    ]),
    "small_open": (30, [
        "LLLL",
        "LMML",
        ".LL.",
    ]),
}


def mouth_layer(kind):
    l = Layer()
    x0, rows = MOUTHS[kind]
    stamp(l, x0, MOUTH_Y, rows, MOUTH_MAP)
    return l


def blush_layer(level):
    l = Layer()
    if level == 0:
        return l
    col = BLUSH_L if level == 1 else BLUSH
    y = 34
    for x in (21, 23, 25):
        l.set(x, y, col, "blush")
        l.set(mx(x), y, col, "blush")
    if level >= 2:
        for x in (20, 22, 24, 26):
            l.set(x, y + 1, col, "blush")
            l.set(mx(x), y + 1, col, "blush")
        # «штрихи» румянца
        l.mirror_pts([(21, 33), (23, 33), (25, 33)], BLUSH, "blush")
    return l


# ---------------------------------------------------------------- assemble
POSES = ["idle", "wave", "giggle", "think"]


def legs_sit(frame):
    """Свисающие ноги сидящей Веры. frame 0 — спокойно, 1 — болтает левой, 2 — правой."""
    l = Layer()

    def leg(x0, kick):
        if not kick:
            l.rect(x0, 60, x0 + 4, 65, SKIN, "skin")
            l.vline(x0 + 4 if x0 < 32 else x0, 60, 65, SKIN_S, "skin")
            l.rect(x0, 64, x0 + 4, 65, SOCK, "sock")
            l.poly([(x0 - 1, 66), (x0 + 5, 66), (x0 + 6, 69), (x0 + 6, 70), (x0 - 2, 70), (x0 - 2, 68)], BOOT, "boot")
            l.hline(x0, x0 + 2, 67, BOOT_H, "boot")
        else:
            # нога вынесена вперёд: голень короче, носок ботинка смотрит на зрителя
            l.rect(x0, 60, x0 + 4, 62, SKIN, "skin")
            l.rect(x0, 62, x0 + 4, 63, SOCK, "sock")
            l.ellipse(x0 + 2.5, 65.5, 3.6, 2.7, BOOT, "boot")
            l.pts([(x0 + 1, 64), (x0 + 2, 64), (x0 + 1, 65)], BOOT_H, "boot")

    leg(25, frame == 1)
    leg(34, frame == 2)
    return l


# ---------------------------------------------------------------- поза «сидит на полу» (ноги поджаты)
def floor_boots():
    """Ботинки, выглядывающие по бокам из-под юбки."""
    l = Layer()
    for sx in (0, 1):
        pts = [(13, 62), (19, 61), (21, 64), (20, 66), (12, 66), (11, 64)]
        l.poly(mirror_poly(pts) if sx else pts, BOOT, "boot")
        hl = [(13, 63), (14, 63), (15, 63)]
        l.pts([(mx(x), y) for x, y in hl] if sx else hl, BOOT_H, "boot")
    return l


def floor_body():
    """Торс + юбка, раскрытая по полу."""
    l = body("idle", sitting=True)
    skirt = [(22, 57), (42, 57), (46, 60), (50, 63), (48, 66), (16, 66), (14, 63), (18, 60)]
    l.poly(skirt, SKIRT, "skirt")
    for x in (18, 23, 28, 36, 41, 46):
        l.vline(x, 60, 65, SKIRT_S, "skirt", clip={"skirt"})
    l.hline(15, 49, 65, SKIRT_S, "skirt", clip={"skirt"})
    l.hline(21, 43, 57, SKIRT_S, "skirt", clip={"skirt"})
    return l


def floor_knees():
    """Колени спереди — над подолом юбки."""
    l = Layer()
    for cx in (28.3, W - 28.3):
        l.ellipse(cx, 64.6, 3.1, 2.1, SKIN, "skin")
    for x0 in (26, mx(30)):
        l.hline(x0, x0 + 4, 66, SKIN_S, "skin", clip={"skin"})
    l.pts([(27, 63), (28, 63), (mx(27), 63), (mx(28), 63)], SKIN_L, "skin", clip={"skin"})
    return l


LAP_SLEEVE = [(23, 42), (25, 46), (29, 54), (28, 57), (23, 57), (18, 51), (20, 44)]


def arm_lap(right=False):
    """Рука опущена на колени, ладошка лежит на юбке."""
    l = Layer()
    l.ellipse(W - 29.5 if right else 29.5, 58.2, 2.7, 2.1, SKIN, "skin")
    l.poly(mirror_poly(LAP_SLEEVE) if right else LAP_SLEEVE, JACKET, "jacket")
    cuff = [(22, 55), (28, 55), (28, 57), (23, 57)]
    l.poly(mirror_poly(cuff) if right else cuff, JACKET_S, "jacket", clip={"jacket"})
    edge = [(21, 54), (22, 54), (23, 54), (24, 54), (25, 54), (26, 54), (27, 54)]
    l.pts([(mx(x), y) for x, y in edge] if right else edge, JACKET_H, "jacket", clip={"jacket"})
    return l


def build_floor(pose):
    """Тело Веры, сидящей на полу (без глаз/рта/румянца, чёлки и руки у лица — они общими слоями)."""
    base = Layer()
    base.paste(hair_back().outlined())
    base.paste(floor_boots().outlined())
    if pose == "giggle":
        base.paste(arm_left_raise_to_mouth().outlined(base, merge=()))
    base.paste(floor_body().outlined())
    base.paste(floor_knees().outlined(base, merge=()))
    if pose != "giggle":
        base.paste(arm_lap(False).outlined())
    if pose == "wave":
        base.paste(arm_right_wave().outlined())
    elif pose == "think":
        base.paste(arm_right_raise_to_chin().outlined())
    else:
        base.paste(arm_lap(True).outlined())
    base.paste(neck().outlined(base, merge={"jacket", "shirt", "ribbon"}))
    base.paste(face().outlined(base, merge={"skin"}))
    base.hline(29, 34, 40, SKIN_S, "skin", clip={"skin"})
    return base


def build_sit(pose):
    """Тело сидящей Веры (без ног — их рисует отдельный слой под юбкой)."""
    hb = hair_back().outlined()
    base = Layer()
    base.paste(hb)
    if pose == "giggle":
        base.paste(arm_left_raise_to_mouth().outlined(base, merge=()))
    base.paste(body(pose, sitting=True).outlined())
    if pose in ("idle", "wave", "think"):
        base.paste(arm_left_down().outlined())
    if pose in ("idle", "giggle"):
        base.paste(arm_right_down().outlined())
    if pose == "wave":
        base.paste(arm_right_wave().outlined())
    if pose == "think":
        base.paste(arm_right_raise_to_chin().outlined())
    base.paste(neck().outlined(base, merge={"jacket", "shirt", "ribbon"}))
    base.paste(face().outlined(base, merge={"skin"}))
    base.hline(29, 34, 40, SKIN_S, "skin", clip={"skin"})
    return base


def compose_sit(pose="idle", eyes="normal", mouth="smirk", blush=0, legs=0):
    sit = build_sit(pose)
    full = legs_sit(legs).outlined()
    full.paste(sit)
    full.paste(eyes_layer(eyes))
    full.paste(mouth_layer(mouth))
    full.paste(blush_layer(blush))
    full.paste(build_front(sit))
    full.paste(hand_layer(pose, full))
    return full


def build_base(pose):
    hb = hair_back().outlined()
    base = Layer()
    base.paste(hb)
    # руки за телом
    if pose == "giggle":
        base.paste(arm_left_raise_to_mouth().outlined(base, merge=()))
    b = body(pose)
    base.paste(b.outlined())
    if pose in ("idle", "wave", "think"):
        base.paste(arm_left_down().outlined())
    if pose in ("idle", "giggle"):
        base.paste(arm_right_down().outlined())
    if pose == "wave":
        base.paste(arm_right_wave().outlined())
    if pose == "think":
        base.paste(arm_right_raise_to_chin().outlined())
    base.paste(neck().outlined(base, merge={"jacket", "shirt", "ribbon"}))
    f = face()
    base.paste(f.outlined(base, merge={"skin"}))
    # тень от волос на шее
    base.hline(29, 34, 40, SKIN_S, "skin", clip={"skin"})
    return base


def build_front(base):
    fh = front_hair()
    front = fh.outlined(base, merge={"hair"})
    cl = clip_layer()
    front.paste(cl.outlined(front, merge={"hair_none"}))
    return front


def hand_layer(pose, under):
    if pose == "giggle":
        return hand_mouth().outlined(under, merge=())
    if pose == "think":
        return hand_chin().outlined(under, merge=())
    return Layer()


def compose(pose="idle", eyes="normal", mouth="smirk", blush=0):
    base = build_base(pose)
    front = build_front(base)
    full = base.copy()
    full.paste(eyes_layer(eyes))
    full.paste(mouth_layer(mouth))
    full.paste(blush_layer(blush))
    full.paste(front)
    full.paste(hand_layer(pose, full))
    return full


def save(layer, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    layer.image().save(path)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    os.makedirs(PREVIEW, exist_ok=True)
    os.makedirs(BUILD, exist_ok=True)

    idle_base = build_base("idle")
    for pose in POSES:
        base = build_base(pose)
        save(base, os.path.join(OUT_DIR, f"base_{pose}.png"))
        tmp = base.copy()
        tmp.paste(eyes_layer("normal"))
        tmp.paste(mouth_layer("smirk"))
        tmp.paste(build_front(base))
        save(hand_layer(pose, tmp), os.path.join(OUT_DIR, f"hand_{pose}.png"))
    save(build_front(idle_base), os.path.join(OUT_DIR, "front.png"))
    # сидячая Вера для мобильного чата: legs_sit_N рисуется ПОД sit_<pose>
    for pose in POSES:
        save(build_sit(pose), os.path.join(OUT_DIR, f"sit_{pose}.png"))
    for fr in (0, 1, 2):
        save(legs_sit(fr).outlined(), os.path.join(OUT_DIR, f"legs_sit_{fr}.png"))
    # сидит на полу (ПК: когда скучно)
    for pose in POSES:
        save(build_floor(pose), os.path.join(OUT_DIR, f"floor_{pose}.png"))
    for k in EYES:
        save(eyes_layer(k), os.path.join(OUT_DIR, f"eyes_{k}.png"))
    for k in MOUTHS:
        save(mouth_layer(k), os.path.join(OUT_DIR, f"mouth_{k}.png"))
    for lvl in (0, 1, 2):
        save(blush_layer(lvl), os.path.join(OUT_DIR, f"blush_{lvl}.png"))

    # превью выражений
    combos = [
        ("idle", "normal", "smirk", 0), ("giggle", "smug", "smirk", 1), ("wave", "happy", "laugh", 1),
        ("think", "up", "flat", 0), ("idle", "surprised", "o", 0), ("idle", "angry", "pout", 2),
        ("idle", "side", "wobbly", 2), ("idle", "closed", "cat", 0), ("idle", "normal", "open_fang", 0),
        ("idle", "smug", "open", 1),
    ]
    sheet = Image.new("RGBA", (W * 8 * 5 + 40, H * 8 * 2 + 20), (236, 236, 244, 255))
    for i, (p, e, m, b) in enumerate(combos):
        img = compose(p, e, m, b).image(8)
        sheet.alpha_composite(img, ((i % 5) * (W * 8 + 8), (i // 5) * (H * 8 + 8)))
    sheet.save(os.path.join(PREVIEW, "sheet.png"))
    row = Image.new("RGBA", (W * 4 * 4 + 30, H * 4), (40, 40, 52, 255))
    for i, c in enumerate([("idle", "normal", "smirk", 0), ("giggle", "smug", "smirk", 1), ("wave", "happy", "laugh", 1), ("think", "up", "flat", 0)]):
        row.alpha_composite(compose(*c).image(4), (i * (W * 4 + 10), 0))
    row.save(os.path.join(PREVIEW, "row4x.png"))
    big = compose("idle", "smug", "smirk", 1).image(10)
    bg = Image.new("RGBA", big.size, (40, 40, 52, 255))
    bg.alpha_composite(big)
    bg.save(os.path.join(PREVIEW, "big_dark.png"))

    # превью сидячей позы на «панели»
    seat = Image.new("RGBA", (W * 8 * 4 + 24, H * 8 + 40), (24, 22, 32, 255))
    from PIL import ImageDraw
    d = ImageDraw.Draw(seat)
    d.rectangle([0, 62 * 8, seat.width, seat.height], fill=(48, 44, 62, 255))
    for i, c in enumerate([("idle", "normal", "smirk", 0, 0), ("giggle", "smug", "smirk", 1, 1), ("wave", "happy", "laugh", 1, 2), ("think", "up", "flat", 0, 0)]):
        seat.alpha_composite(compose_sit(*c).image(8), (i * (W * 8 + 8), 0))
    seat.save(os.path.join(PREVIEW, "sit.png"))

    # витрина эмоций для README
    docs = os.path.join(ROOT, "docs")
    os.makedirs(docs, exist_ok=True)
    show = [("idle", "normal", "smirk", 0), ("giggle", "smug", "smirk", 1), ("wave", "happy", "laugh", 1),
            ("think", "up", "flat", 0), ("idle", "surprised", "o", 0), ("idle", "side", "wobbly", 2), ("idle", "angry", "pout", 2)]
    strip = Image.new("RGBA", (len(show) * (W * 3 + 6), H * 3), (0, 0, 0, 0))
    for i, c in enumerate(show):
        strip.alpha_composite(compose(*c).image(3), (i * (W * 3 + 6), 0))
    strip.save(os.path.join(docs, "expressions.png"))

    # картинка для пустого чата
    compose("wave", "happy", "cat", 1).image(1).save(os.path.join(OUT_DIR, "preview.png"))

    # иконки: голова крупным планом
    full = compose("idle", "smug", "smirk", 1).image(1)
    head = full.crop((10, 0, 54, 44))  # 44x44
    canvas = Image.new("RGBA", (48, 48), (0, 0, 0, 0))
    canvas.alpha_composite(head, (2, 3))
    icon256 = canvas.resize((256, 256), Image.NEAREST)
    icon256.save(os.path.join(BUILD, "icon.png"))
    icon256.save(os.path.join(ASSETS, "icon.png"))
    icon256.save(os.path.join(BUILD, "icon.ico"),
                 sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    canvas.resize((32, 32), Image.NEAREST).save(os.path.join(ASSETS, "tray.png"))
    canvas.resize((64, 64), Image.NEAREST).save(os.path.join(ASSETS, "tray@2x.png"))
    print("ok")


if __name__ == "__main__":
    main()
