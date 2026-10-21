#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 小笺 · XiaoJian 的程序图标 card.ico。

设计取自软件自己的样子，不另起一套配色：
    卡片      白底（QuoteCardForm 的默认 BackColor 就是 Color.White）
    描边      #2A2A2A，也就是 OnPaint 里那支 Pen 的颜色
    圆角      与 CardSizeLimits.CornerRadius 同一个比例
    内容      竖排三列（右起、列长递减 —— 软件里竖排就是这个方向）
    印章      朱红方块 + 纸色内框，像一枚压在笺纸上的印

为什么要有这个脚本：原来那张 card.ico 是随手拿的图，改名之后软件叫「小笺」，
图标却和名字、和卡片本身都无关。图标是资源，改一次要能重来一次，所以画法留在
脚本里，而不是留在一个二进制文件里。

用法：
    python3 make_ico.py                # 写出 card.ico 与预览图
    python3 make_ico.py --out <路径>
"""
import os
import struct
import sys
import zlib

from PIL import Image, ImageDraw, ImageFilter, ImageFont

SONG = "/System/Library/Fonts/Supplemental/Songti.ttc"

# 软件自己的配色
INK = (42, 42, 42)            # = OnPaint 的 Color.FromArgb(42, 42, 42)
PAPER_TOP = (255, 255, 255)   # = 默认 BackColor
PAPER_BOT = (241, 241, 243)   # 只做一点点体积感，不改变「白卡」这个印象
SEAL = (200, 55, 45)

# 尺寸档：16/24 不带投影，32 起加一点，大尺寸才明显
SIZES = (16, 24, 32, 48, 64, 128, 256)

# 比例（都相对卡片宽度，这样每一档都能按自己的尺寸重画）
INSET_RATIO = 0.055           # 卡片四周留白（给投影）
RADIUS_RATIO = 0.20           # 圆角
BORDER_RATIO = 0.045          # 描边粗细
BAR_WIDTH = 0.100             # 竖列笔画粗细
BAR_GAP = 0.205               # 列间距
BAR_TOP = 0.25
BAR_BOTTOM = 0.63
BAR_LENS = (1.0, 0.70, 0.44)  # 右起：整列、七成、四成半
SEAL_SIZE = 0.170
SEAL_Y = 0.795
SS = 4                        # 每档按 4 倍超采样再缩回去


def _font(face, size):
    return ImageFont.truetype(SONG, size, index=face)


def _rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def _bars_for(size):
    """小尺寸上笔画少了糊成一团，所以 16/24 用两列，32 起三列。"""
    if size <= 24:
        return 2, 0.115, (1.0, 0.68)
    return 3, BAR_WIDTH, BAR_LENS


def _border_for(size):
    """描边要按档加粗：同一比例在 16px 上只剩不到 1 像素，缩下去就成了一圈灰雾。

    这里给的是「相对卡片宽度」的粗细，换算成像素后每一档都落在 1.2~1.6 像素之间，
    所以 16px 的卡片也有一圈实心的深色轮廓，而不是模糊的边。
    """
    if size <= 24:
        return 0.082
    if size <= 32:
        return 0.062
    return BORDER_RATIO


def _shadow_alpha(size):
    if size <= 24:
        return 0
    if size <= 48:
        return 60
    return 88


def render(size):
    """把图标画在 size×size 的 RGBA 画布上（内部按 SS 倍超采样）。"""
    s = size * SS
    inset = int(s * INSET_RATIO)
    w = s - 2 * inset
    radius = int(w * RADIUS_RATIO)
    border = max(2, int(w * _border_for(size)))

    out = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # 投影
    alpha = _shadow_alpha(size)
    if alpha:
        sh = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        off = int(s * 0.024)
        ImageDraw.Draw(sh).rounded_rectangle(
            [inset, inset + off, inset + w, inset + w + off], radius=radius,
            fill=(28, 28, 30, alpha))
        out = Image.alpha_composite(out, sh.filter(ImageFilter.GaussianBlur(s * 0.020)))

    # 纸面
    grad = Image.new("RGB", (1, w))
    for y in range(w):
        t = y / max(1, w - 1)
        grad.putpixel((0, y), tuple(
            int(PAPER_TOP[i] + (PAPER_BOT[i] - PAPER_TOP[i]) * t) for i in range(3)))
    sheet = grad.resize((w, w), Image.BILINEAR).convert("RGBA")
    sheet.putalpha(_rounded_mask(w, radius))
    out.alpha_composite(sheet, (inset, inset))

    # 描边
    frame = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    ImageDraw.Draw(frame).rounded_rectangle(
        [0, 0, w - 1, w - 1], radius=radius, outline=INK + (255,), width=border)
    out.alpha_composite(frame, (inset, inset))

    # 竖排三列（右起）
    d = ImageDraw.Draw(out)
    n, bw, lens = _bars_for(size)
    sw = w * bw
    for i, L in enumerate(lens[:n]):
        x = inset + w * 0.5 + (1 - i) * w * BAR_GAP
        y1 = inset + w * BAR_TOP
        y2 = y1 + w * (BAR_BOTTOM - BAR_TOP) * L
        d.rounded_rectangle([x - sw / 2, y1, x + sw / 2, y2], radius=sw / 2, fill=INK + (255,))

    # 朱印
    ss = w * SEAL_SIZE
    cx, cy = inset + w * 0.5, inset + w * SEAL_Y
    x0, y0 = cx - ss / 2, cy - ss / 2
    d.rounded_rectangle([x0, y0, x0 + ss, y0 + ss], radius=int(ss * 0.14), fill=SEAL + (255,))
    t = max(2, int(ss * 0.14))
    d.rounded_rectangle([x0 + t, y0 + t, x0 + ss - t, y0 + ss - t],
                        radius=max(1, int(ss * 0.06)), outline=PAPER_TOP + (255,), width=t)

    return out.resize((size, size), Image.LANCZOS)


# ---------------------------------------------------------------- ICO 写出

def _bmp_entry(img):
    """32bpp BGRA 的 DIB（含 AND 掩码），高度写成两倍。"""
    w, h = img.size
    px = img.convert("RGBA").load()
    rows = []
    for y in range(h - 1, -1, -1):            # DIB 自下而上
        row = bytearray()
        for x in range(w):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    xor = b"".join(rows)

    mask_row = ((w + 31) // 32) * 4           # 1bpp，行按 4 字节对齐
    andmask = bytearray()
    for y in range(h - 1, -1, -1):
        bits = bytearray(mask_row)
        for x in range(w):
            if px[x, y][3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        andmask += bits

    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0,
                         len(xor), 0, 0, 0, 0)
    return header + xor + bytes(andmask)


def _png_entry(img):
    """PNG 负载。自己写，避免依赖 Pillow 的 ICO 写出行为。"""
    w, h = img.size
    raw = bytearray()
    px = img.convert("RGBA").load()
    for y in range(h):
        raw.append(0)                          # filter type 0
        for x in range(w):
            raw += bytes(px[x, y])

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) +
            chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def write_ico(path, images):
    """按尺寸分负载：<=48 用 DIB（老工具与 XP 也认），>=64 用 PNG。"""
    entries = []
    for img in images:
        w, h = img.size
        payload = _bmp_entry(img) if w <= 48 else _png_entry(img)
        entries.append((w, h, payload))

    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = len(header) + 16 * len(entries)
    directory = bytearray()
    body = bytearray()
    for w, h, payload in entries:
        directory += struct.pack("<BBBBHHII", w if w < 256 else 0, h if h < 256 else 0,
                                 0, 0, 1, 32, len(payload), offset)
        body += payload
        offset += len(payload)
    with open(path, "wb") as fh:
        fh.write(header + bytes(directory) + bytes(body))
    return len(header) + len(directory) + len(body)


# ---------------------------------------------------------------- 预览

def preview(path):
    """一张给人看的对照图：浅底一行、深底一行，小尺寸另给放大。"""
    pad, up = 12, 4
    big = [256, 128, 64, 48]
    small = [32, 24, 16]
    colw = 256 + pad
    W = pad + len(big) * colw + pad + len(small) * (small[0] * up + pad) + pad
    H = pad + 2 * (256 + pad)
    sheet = Image.new("RGB", (W, H), (255, 255, 255))
    ImageDraw.Draw(sheet).rectangle([0, pad + 256 + pad, W, H], fill=(26, 26, 28))
    for row, dark in enumerate((False, True)):
        y = pad + row * (256 + pad)
        x = pad
        for px in big:
            art = render(px)
            sheet.paste(art, (x, y + (256 - px) // 2), art)
            x += colw
        for px in small:
            art = render(px).resize((px * up, px * up), Image.NEAREST)
            sheet.paste(art, (x, y + 40), art)
            x += px * up + pad
    sheet.save(path)
    return sheet.size


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "card.ico")
    for i, a in enumerate(sys.argv):
        if a == "--out" and i + 1 < len(sys.argv):
            out = sys.argv[i + 1]
    imgs = [render(px) for px in SIZES]
    total = write_ico(out, imgs)
    print("写出 %s（%d 档，%d 字节）" % (out, len(SIZES), total))
    print("预览 %s" % (preview(os.path.join(here, "icon-preview.png")),))


if __name__ == "__main__":
    main()
