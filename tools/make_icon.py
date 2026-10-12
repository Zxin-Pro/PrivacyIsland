#!/usr/bin/env python3
"""生成 PrivacyIsland 的 icon.ico（多尺寸：16/24/32/48/64/256，PNG 条目）。"""
import io
import math
import struct
from PIL import Image, ImageDraw

OUT = "/var/minis/workspace/PrivacyIsland/icon.ico"
PREVIEW = "/tmp/icon_preview_256.png"


def render(size: int) -> Image.Image:
    SS = 4  # 超采样倍率
    W = size * SS
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def px(v: float) -> float:
        return v * W

    # ---- 深色胶囊背景 ----
    pad_x = px(0.015)
    y0 = px(0.155)
    y1 = px(0.845)
    radius = (y1 - y0) / 2.0
    line_w = max(2, int(W * (0.018 if size > 20 else 0.024)))
    d.rounded_rectangle(
        [pad_x, y0, W - pad_x, y1],
        radius=radius,
        fill=(28, 28, 30, 235),
        outline=(255, 255, 255, 46),
        width=line_w,
    )

    # ---- 眼睛外轮廓（两条正弦弧围成的眼形） ----
    cx, cy = W / 2.0, W / 2.0
    ew, eh = px(0.62), px(0.44)
    if size <= 20:
        ew, eh = px(0.68), px(0.50)  # 小尺寸放大眼形，保证可辨识

    pts = []
    n = 72
    for i in range(n + 1):  # 上弧：左 → 右
        t = i / n
        x = -ew / 2 + ew * t
        y = -eh / 2 * math.sin(math.pi * t)
        pts.append((cx + x, cy + y))
    for i in range(n - 1, 0, -1):  # 下弧：右 → 左
        t = i / n
        x = -ew / 2 + ew * t
        y = eh / 2 * math.sin(math.pi * t)
        pts.append((cx + x, cy + y))

    eye_line_w = max(2, int(W * (0.052 if size > 20 else 0.066)))
    d.polygon(pts, outline=(255, 255, 255, 255), width=eye_line_w)

    # ---- 绿色瞳孔 ----
    pr = px(0.135) if size > 20 else px(0.15)
    d.ellipse([cx - pr, cy - pr, cx + pr, cy + pr], fill=(52, 199, 89, 255))

    # ---- 高光 ----
    if size > 20:
        hr = px(0.032)
        hx, hy = cx - pr * 0.30, cy - pr * 0.34
        d.ellipse([hx - hr, hy - hr, hx + hr, hy + hr], fill=(255, 255, 255, 255))

    return img.resize((size, size), Image.LANCZOS)


def main():
    sizes = [16, 24, 32, 48, 64, 256]
    entries = []
    offset = 6 + 16 * len(sizes)
    payload = b""

    for s in sizes:
        img = render(s)
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        data = buf.getvalue()
        entries.append((s, len(data), offset))
        offset += len(data)
        payload += data

    with open(OUT, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(sizes)))  # ICONDIR
        for s, length, off in entries:
            w = 0 if s >= 256 else s
            h = 0 if s >= 256 else s
            f.write(struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, length, off))
        f.write(payload)

    render(256).save(PREVIEW)
    print(f"icon.ico written: {OUT}")
    for s in sizes:
        render(s).save(f"/tmp/icon_{s}.png")
    print("previews saved")


if __name__ == "__main__":
    main()
