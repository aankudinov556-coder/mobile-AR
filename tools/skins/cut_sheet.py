#!/usr/bin/env python3
"""
Нарезка листа спрайтов пользователя на отдельные позы.
- находит позы по прозрачности, мелкие значки (?, ~, ...) приклеивает к ближайшей позе;
- чистит шум палитры (оттенки, отличающиеся на 1–3 единицы → один цвет), края альфы делает чёткими;
- кладёт каждую позу в assets/skins/<skin>/<pose>.png + manifest.json.

Запуск: python3 tools/skins/cut_sheet.py <sheet.png> <skin-id>
"""
import collections
import json
import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# порядок поз на листе (слева направо, сверху вниз) → имя и назначение
POSES = [
    ("think_sit", "сидит, ладонь у щеки, «?» — думает"),
    ("shrug", "стоит, разводит руками «~» — спокойно / дразнится"),
    ("laugh", "глаза >< — смеётся"),
    ("sit", "сидит, обняв колени — ожидание (панель чата)"),
    ("giggle", "ладошка у рта — хи-хи"),
    ("lie", "лежит, «...» — скучает / спит"),
]


def components(img, cell=4, gap=2):
    w, h = img.size
    a = img.getchannel("A").load()
    gw, gh = w // cell, h // cell
    occ = [[any(a[x * cell + dx, y * cell + dy] > 40 for dx in range(cell) for dy in range(cell)) for x in range(gw)] for y in range(gh)]
    seen = [[False] * gw for _ in range(gh)]
    out = []
    for y in range(gh):
        for x in range(gw):
            if not occ[y][x] or seen[y][x]:
                continue
            q = collections.deque([(x, y)])
            seen[y][x] = True
            x0 = x1 = x
            y0 = y1 = y
            n = 0
            while q:
                cx, cy = q.popleft()
                n += 1
                x0, x1, y0, y1 = min(x0, cx), max(x1, cx), min(y0, cy), max(y1, cy)
                for dx in range(-gap, gap + 1):
                    for dy in range(-gap, gap + 1):
                        nx, ny = cx + dx, cy + dy
                        if 0 <= nx < gw and 0 <= ny < gh and occ[ny][nx] and not seen[ny][nx]:
                            seen[ny][nx] = True
                            q.append((nx, ny))
            out.append([n, x0 * cell, y0 * cell, (x1 + 1) * cell, (y1 + 1) * cell])
    return out


def box_dist(a, b):
    dx = max(b[1] - a[3], a[1] - b[3], 0)
    dy = max(b[2] - a[4], a[2] - b[4], 0)
    return (dx * dx + dy * dy) ** 0.5


def clean(img):
    """Чёткая альфа + слияние почти одинаковых оттенков (шум сжатия/масштабирования)."""
    img = img.convert("RGBA")
    px = img.load()
    w, h = img.size
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a < 110:
                px[x, y] = (0, 0, 0, 0)
            else:
                # квантуем шагом 6 — убирает ±1–3 шум, не трогая рисунок
                px[x, y] = (min(255, round(r / 6) * 6), min(255, round(g / 6) * 6), min(255, round(b / 6) * 6), 255)
    return img


def main(sheet_path, skin):
    sheet = Image.open(sheet_path).convert("RGBA")
    comps = components(sheet)
    comps.sort(key=lambda c: -c[0])
    big = comps[:len(POSES)]
    small = comps[len(POSES):]
    # порядок чтения: строки сверху вниз, внутри — слева направо
    half = sheet.size[1] / 2
    big.sort(key=lambda c: (0 if (c[2] + c[4]) / 2 < half else 1, c[1]))
    groups = [[b] for b in big]
    for s in small:
        i = min(range(len(big)), key=lambda k: box_dist(big[k], s))
        groups[i].append(s)

    out_dir = os.path.join(ROOT, "assets", "skins", skin)
    os.makedirs(out_dir, exist_ok=True)
    manifest = {"id": skin, "source": os.path.basename(sheet_path), "poses": {}}
    for (name, desc), group in zip(POSES, groups):
        x0 = min(g[1] for g in group)
        y0 = min(g[2] for g in group)
        x1 = max(g[3] for g in group)
        y1 = max(g[4] for g in group)
        crop = clean(sheet.crop((x0, y0, x1, y1)))
        bbox = crop.getbbox()
        crop = crop.crop(bbox)
        # нижняя точка самой позы (без значков) — опорная линия «ноги/сиденье»
        main_box = group[0]
        baseline = (main_box[4] - y0) - bbox[1]
        crop.save(os.path.join(out_dir, f"{name}.png"), optimize=True)
        manifest["poses"][name] = {"file": f"{name}.png", "size": list(crop.size), "baseline": int(min(baseline, crop.size[1])), "about": desc}
        print(name, crop.size)
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "user")
