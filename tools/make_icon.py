"""Gera src/NetMonitor/Assets/NetMonitor.ico (e um PNG de 256 px para documentação).

Desenho: escudo (proteção/firewall) sobre fundo azul arredondado, com uma seta de
download (âmbar) e uma de upload (rosa) — as mesmas cores usadas na interface.
Uso: python3 tools/make_icon.py   (requer Pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT_ICO = ROOT / "src/NetMonitor/Assets/NetMonitor.ico"
OUT_PNG = ROOT / "docs/icon.png"

BLUE_TOP = (37, 99, 235)     # #2563EB
BLUE_BOTTOM = (30, 58, 138)  # #1E3A8A
DOWN = (245, 158, 11)        # #F59E0B
UP = (236, 72, 153)          # #EC4899
WHITE = (255, 255, 255)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def render(size: int, detailed: bool) -> Image.Image:
    s = 1024  # desenha grande e reduz (antialiasing)
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Fundo: quadrado arredondado com degradê vertical
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        gd.line([(0, y), (s, y)], fill=lerp(BLUE_TOP, BLUE_BOTTOM, y / s) + (255,))
    mask = Image.new("L", (s, s), 0)
    margin = 40 if detailed else 0
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - margin, s - margin], radius=220, fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)

    if detailed:
        # Escudo branco
        cx, top, w, h = s / 2, 175, 560, 690
        shield = [
            (cx - w / 2, top + 70), (cx, top), (cx + w / 2, top + 70),
            (cx + w / 2, top + h * 0.45),
            (cx + w * 0.36, top + h * 0.72), (cx, top + h), (cx - w * 0.36, top + h * 0.72),
            (cx - w / 2, top + h * 0.45),
        ]
        d.polygon(shield, fill=WHITE)
        arrow_top, arrow_bottom, half = top + 165, top + 505, 52
    else:
        arrow_top, arrow_bottom, half = 150, 874, 105

    # Setas ↓ (esquerda, âmbar) e ↑ (direita, rosa)
    def arrow(x, up, color):
        shaft_w = half * 0.85
        head = half * 2.0
        if up:
            d.polygon([(x - head, arrow_top + head * 1.05), (x, arrow_top), (x + head, arrow_top + head * 1.05)], fill=color)
            d.rectangle([x - shaft_w / 2, arrow_top + head, x + shaft_w / 2, arrow_bottom], fill=color)
        else:
            d.polygon([(x - head, arrow_bottom - head * 1.05), (x, arrow_bottom), (x + head, arrow_bottom - head * 1.05)], fill=color)
            d.rectangle([x - shaft_w / 2, arrow_top, x + shaft_w / 2, arrow_bottom - head], fill=color)

    gap = 118 if detailed else 225
    arrow(s / 2 - gap, False, DOWN)
    arrow(s / 2 + gap, True, UP)

    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [render(n, detailed=n >= 32) for n in sizes]
    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    frames[-1].save(OUT_ICO, format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    OUT_PNG.parent.mkdir(parents=True, exist_ok=True)
    frames[-1].save(OUT_PNG)
    print("ok:", OUT_ICO, OUT_PNG)


if __name__ == "__main__":
    main()
