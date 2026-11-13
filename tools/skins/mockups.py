#!/usr/bin/env python3
"""Превью скина пользователя: лист поз и макеты (мобильный чат, рабочий стол). Не подключено к приложению."""
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SKIN = sys.argv[1] if len(sys.argv) > 1 else "halo"
SRC = os.path.join(ROOT, "assets", "skins", SKIN)
OUT = os.path.join(ROOT, "docs", "skins", SKIN)
os.makedirs(OUT, exist_ok=True)
man = json.load(open(os.path.join(SRC, "manifest.json"), encoding="utf-8"))
F = lambda s: ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", s)
BG, CARD, LINE, TXT, MUTED, ACC = (21, 19, 28), (29, 26, 39), (47, 42, 61), (236, 233, 245), (154, 147, 173), (224, 71, 154)


def pose(name, height):
    im = Image.open(os.path.join(SRC, man["poses"][name]["file"])).convert("RGBA")
    k = height / im.height
    return im.resize((max(1, round(im.width * k)), height), Image.LANCZOS)


# --- 1. лист поз с подписями
names = list(man["poses"].keys())
cell_w, cell_h = 300, 360
sheet = Image.new("RGBA", (cell_w * 3, cell_h * 2 + 10), BG)
d = ImageDraw.Draw(sheet)
for i, n in enumerate(names):
    x, y = (i % 3) * cell_w, (i // 3) * cell_h
    p = pose(n, 270 if n != "lie" else 150)
    sheet.alpha_composite(p, (x + (cell_w - p.width) // 2, y + 280 - p.height))
    d.text((x + 14, y + 290), n, fill=TXT, font=F(17))
    d.text((x + 14, y + 314), man["poses"][n]["about"], fill=MUTED, font=F(12))
sheet.save(os.path.join(OUT, "poses.png"))

# --- 2. мобильный чат: сидит на панели ввода
W, H = 430, 900
m = Image.new("RGBA", (W, H), BG)
d = ImageDraw.Draw(m)
d.rectangle([0, 0, W, 64], fill=CARD)
d.text((20, 20), "Вера", fill=TXT, font=F(20))
d.text((86, 25), "OpenRouter · gpt-5-mini", fill=MUTED, font=F(13))
def bubble(y, text, mine):
    tw = max(d.textlength(t, font=F(15)) for t in text.split("\n")) + 28
    x0 = W - tw - 16 if mine else 16
    d.rounded_rectangle([x0, y, x0 + tw, y + 20 * (text.count("\n") + 1) + 18], 16, fill=ACC if mine else CARD, outline=None if mine else LINE)
    d.multiline_text((x0 + 14, y + 9), text, fill=(255, 255, 255) if mine else TXT, font=F(15), spacing=5)
bubble(96, "Напомни через 20 минут\nсделать перерыв", True)
bubble(170, "Напоминание: перерыв — сегодня 15:40", False)
bubble(220, "Хе-хе, поставила~ Только не\nзалипни там без меня!", False)
bar_y = H - 96
d.rectangle([0, bar_y, W, H], fill=CARD)
d.line([0, bar_y, W, bar_y], fill=LINE, width=2)
d.rounded_rectangle([16, bar_y + 26, W - 112, bar_y + 72], 22, fill=(38, 34, 50), outline=LINE)
d.text((36, bar_y + 38), "Напиши Вере…", fill=MUTED, font=F(15))
d.ellipse([W - 102, bar_y + 26, W - 58, bar_y + 72], fill=(38, 34, 50))
d.ellipse([W - 52, bar_y + 26, W - 6, bar_y + 72], fill=ACC)
sit = pose("sit", 190)
base = man["poses"]["sit"]["baseline"] * 190 / man["poses"]["sit"]["size"][1]
seat_offset = int(base * 0.86)   # сиденье чуть выше низа спрайта — ноги свисают на панель
# сидит на верхнем краю панели над правой частью поля ввода, не закрывая кнопки
m.alpha_composite(sit, (W - 112 - sit.width - 4, bar_y - sit.height + 14))
m.save(os.path.join(OUT, "mockup_mobile.png"))

# --- 3. рабочий стол: стоит у края экрана с пузырём
D = Image.new("RGBA", (900, 560), (52, 70, 98))
d = ImageDraw.Draw(D)
for i in range(560):
    c = (52 + i // 14, 70 + i // 20, 98 + i // 12)
    d.line([0, i, 900, i], fill=c)
d.rectangle([0, 512, 900, 560], fill=(24, 24, 30))
st = pose("shrug", 300)
D.alpha_composite(st, (900 - st.width - 60, 512 - st.height))
bx0, by0 = 900 - st.width - 210, 120
d.rounded_rectangle([bx0, by0, bx0 + 300, by0 + 82], 6, fill=(255, 250, 255), outline=(44, 30, 52), width=3)
d.multiline_text((bx0 + 14, by0 + 12), "Эй, семпай~ Что у тебя\nна экране? Хе-хе", fill=(44, 30, 52), font=F(16), spacing=6)
D.save(os.path.join(OUT, "mockup_desktop.png"))
print("ok", OUT)
