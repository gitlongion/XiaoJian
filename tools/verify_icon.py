#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""程序图标 card.ico 的结构与内容断言。

为什么要单独验图标：macOS 上跑不了 exe，图标是唯一一个「改了却看不见」的资源 ——
编译照样通过，PE 里照样有 RT_ICON，只有真的把图取出来看才知道画的是什么。旧图标
是一张单档 256 的图，任务栏和标题栏全靠系统把 256 缩到 16，糊成一团白点。这一版
要保证的是三件事：

    1. 文件本身是**多档**的（16/24/32/48/64/128/256），而不是一档缩到底；
    2. 每一档的容器格式正确（≤48 走 DIB、≥64 走 PNG），宽度高度对得上；
    3. 每一档**画出来还是那张卡片** —— 四角透空、纸面是白的、左边有深色描边、
       下方有朱印。第 3 条是这份脚本真正值钱的部分：前两条只说明文件合法，
       第 3 条才说明图标没被换成一张白图或者一张全透明的图。

用法：
    python3 verify_icon.py                     # 默认找脚本旁边的 card.ico
    python3 verify_icon.py --ico <路径>
    python3 verify_icon.py --reverse            # 注入缺陷，验证断言真的会报
"""
import os
import re
import shutil
import struct
import sys
import tempfile
import zlib

SIZES = (16, 24, 32, 48, 64, 128, 256)
# 软件自己的配色，和 make_ico.py 同源
INK = (42, 42, 42)
SEAL = (200, 55, 45)
PAPER_MIN = 232          # 「白纸」的亮度下限
INK_MAX = 130            # 「深色描边」的亮度上限
CORNER_ALPHA_MAX = 24    # 角上允许残留的极淡投影


# ---------------------------------------------------------------- 定位

def _default_ico():
    here = os.path.dirname(os.path.abspath(__file__))
    for cand in (os.path.join(here, "card.ico"),
                 os.path.join(here, "src", "XiaoJian", "card.ico"),
                 "/tmp/XiaoJian/src/XiaoJian/card.ico"):
        if os.path.isfile(cand):
            return cand
    return os.path.join(here, "card.ico")


ICO = _default_ico()
SRC_DIR = os.path.dirname(ICO)
for _i, _a in enumerate(sys.argv):
    if _a == "--ico" and _i + 1 < len(sys.argv):
        ICO = sys.argv[_i + 1]
        SRC_DIR = os.path.dirname(ICO)
# --src 单独指定「去哪儿找 build.bat / build-macos.csproj」。对旧图标做对照跑时，
# 图标样本可能被放在别处，但构建脚本仍然要看仓库里那一份。
for _i, _a in enumerate(sys.argv):
    if _a == "--src" and _i + 1 < len(sys.argv):
        SRC_DIR = sys.argv[_i + 1]


# ---------------------------------------------------------------- 容器解析

def parse_ico(data):
    """读 ICO 目录。返回 (reserved, type, count, entries)。"""
    reserved, typ, count = struct.unpack_from("<HHH", data, 0)
    entries = []
    off = 6
    for i in range(count):
        w, h, colors, res, planes, bpp, size, offset = struct.unpack_from("<BBBBHHII", data, off)
        entries.append({
            "index": i,
            # 规范里 256 写作 0，这里把「声明的实际尺寸」与「目录里的字节」都留着，
            # 断言 3 要检查的正是这两个值的关系。
            "w": w or 256, "h": h or 256, "raw_w": w, "raw_h": h,
            "planes": planes, "bpp": bpp, "size": size, "offset": offset,
            "payload": data[offset:offset + size],
        })
        off += 16
    return reserved, typ, count, entries


def _unfilter(raw, w, h, channels):
    """PNG 行还原（5 种滤波器全实现，这样换成别的工具生成的 PNG 也读得动）。"""
    stride = w * channels
    out = bytearray()
    prev = bytearray(stride)
    pos = 0
    for _ in range(h):
        ft = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride
        if ft == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ft == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        elif ft != 0:
            raise ValueError("未知的 PNG 滤波器 %d" % ft)
        out += line
        prev = line
    return out


def decode_png(payload):
    """返回 (w, h, rows)，rows[y][x] = (r,g,b,a)。"""
    if payload[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是 PNG")
    pos = 8
    w = h = None
    idat = bytearray()
    while pos + 8 <= len(payload):
        length, tag = struct.unpack_from(">I4s", payload, pos)
        body = payload[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            w, h, depth, ctype, comp, filt, inter = struct.unpack(">IIBBBBB", body)
            if depth != 8 or ctype != 6 or inter != 0:
                raise ValueError("只支持 8 位 RGBA 非隔行 PNG")
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
        pos += 12 + length
    raw = zlib.decompress(bytes(idat))
    flat = _unfilter(raw, w, h, 4)
    rows = [[tuple(flat[(y * w + x) * 4:(y * w + x) * 4 + 4]) for x in range(w)]
            for y in range(h)]
    return w, h, rows


def decode_dib(payload):
    """返回 (w, h, rows)。32bpp 自下而上，后接 1bpp AND 掩码。"""
    (hdr, w, h2, planes, bpp, comp, size_img, _x, _y, _u, _v) = \
        struct.unpack_from("<IiiHHIIiiII", payload, 0)
    if hdr != 40:
        raise ValueError("BITMAPINFOHEADER 大小是 %d，不是 40" % hdr)
    h = h2 // 2
    pos = 40
    rows = [None] * h
    for y in range(h):
        line = payload[pos + (h - 1 - y) * w * 4: pos + (h - y) * w * 4]
        rows[y] = [(line[x * 4 + 2], line[x * 4 + 1], line[x * 4], line[x * 4 + 3])
                   for x in range(w)]
        # DIB 里存的是 BGRA，转成 RGBA
    return w, h, rows


def decode(entry):
    if entry["payload"][:8] == b"\x89PNG\r\n\x1a\n":
        return decode_png(entry["payload"])
    return decode_dib(entry["payload"])


def payload_dims(entry):
    """从负载本身读出真实尺寸，和目录里声明的那个是两回事。"""
    p = entry["payload"]
    try:
        if p[:8] == b"\x89PNG\r\n\x1a\n":
            return struct.unpack_from(">II", p, 16)
        _, w, h2 = struct.unpack_from("<Iii", p, 0)
        return w, h2 // 2
    except struct.error:
        return None


def lum(px):
    return (px[0] * 299 + px[1] * 587 + px[2] * 114) // 1000


# ---------------------------------------------------------------- 断言

def audit(data, src_dir):
    problems = []
    if len(data) < 6:
        return ["I1 文件太短，连 ICO 头都不完整"]

    reserved, typ, count, entries = parse_ico(data)

    # --- I1 头部与档数 ---
    if reserved != 0:
        problems.append("I1 ICO 头的保留字段是 %d，规范要求 0" % reserved)
    if typ != 1:
        problems.append("I1 资源类型是 %d，图标应当是 1（光标才是 2）" % typ)
    if count != len(SIZES):
        problems.append("I1 目录里有 %d 档，期望 %d 档（%s）"
                        % (count, len(SIZES), "/".join(str(s) for s in SIZES)))
    if count <= 1:
        problems.append("I1 只有 %d 档：任务栏/标题栏只能把 256 缩到 16，"
                        "小尺寸必然糊 —— 这正是这一版要修的问题" % count)

    # --- I2 档位齐全 ---
    declared = sorted(e["w"] for e in entries)
    if declared != sorted(SIZES):
        problems.append("I2 档位是 %s，期望 %s"
                        % (declared, sorted(SIZES)))
    for e in entries:
        if e["w"] != e["h"]:
            problems.append("I2 第 %d 档不是正方形：%dx%d" % (e["index"], e["w"], e["h"]))

    # --- I3 目录声明与负载实际尺寸一致，且 256 档写作 0 ---
    # 只看目录不够：把 256 档的目录字节写成 128/128，目录里就再也找不到「256」这个值，
    # 光按值去找会漏过去。所以这里改成「从负载里读出真实尺寸，再和目录对」。
    for e in entries:
        dims = payload_dims(e)
        if dims is None:
            problems.append("I3 第 %d 档读不出负载里的真实尺寸" % e["index"])
            continue
        if dims != (e["w"], e["h"]):
            problems.append("I3 第 %d 档目录声明 %dx%d，负载里其实是 %dx%d："
                            "Windows 按目录取图，取出来尺寸对不上"
                            % (e["index"], e["w"], e["h"], dims[0], dims[1]))
        if dims == (256, 256) and (e["raw_w"] != 0 or e["raw_h"] != 0):
            problems.append("I3 256 档在目录里写成了 %d/%d，规范要求写 0"
                            % (e["raw_w"], e["raw_h"]))

    # --- I4 位深与平面数 ---
    for e in entries:
        if e["bpp"] != 32:
            problems.append("I4 第 %d 档（%dpx）位深是 %d，期望 32"
                            % (e["index"], e["w"], e["bpp"]))
        if e["planes"] != 1:
            problems.append("I4 第 %d 档平面数是 %d，期望 1" % (e["index"], e["planes"]))

    # --- I5 负载边界 ---
    end = 6 + 16 * count          # 第一档应当紧跟在目录之后
    for e in entries:
        if e["size"] == 0:
            problems.append("I5 第 %d 档负载长度为 0" % e["index"])
        if e["offset"] < 6 + 16 * count:
            problems.append("I5 第 %d 档的偏移 %d 落进了目录区" % (e["index"], e["offset"]))
        if e["offset"] + e["size"] > len(data):
            problems.append("I5 第 %d 档的负载超出文件末尾（%d+%d > %d）"
                            % (e["index"], e["offset"], e["size"], len(data)))
        if e["offset"] != end:
            problems.append("I5 第 %d 档的偏移 %d 与上一档末尾 %d 不连续"
                            % (e["index"], e["offset"], end))
        end = e["offset"] + e["size"]
    if end != len(data):
        problems.append("I5 最后一档结束于 %d，文件长 %d：多出 %d 字节的垃圾"
                        % (end, len(data), len(data) - end))

    # --- I6/I7 容器格式 ---
    for e in entries:
        p = e["payload"]
        if e["w"] <= 48:
            if p[:8] == b"\x89PNG\r\n\x1a\n":
                problems.append("I6 第 %d 档（%dpx）用了 PNG 负载："
                                "这个尺寸应当用 DIB，老工具与 XP 才认" % (e["index"], e["w"]))
                continue
            try:
                hdr, w, h2, planes, bpp, comp, size_img = struct.unpack_from("<IiiHHII", p, 0)
            except struct.error:
                problems.append("I6 第 %d 档负载太短，读不出 BITMAPINFOHEADER" % e["index"])
                continue
            if hdr != 40:
                problems.append("I6 第 %d 档 BITMAPINFOHEADER 大小是 %d，期望 40"
                                % (e["index"], hdr))
            if w != e["w"] or h2 != e["h"] * 2:
                problems.append("I6 第 %d 档头里的尺寸是 %dx%d，期望 %dx%d"
                                "（DIB 的高度要写两倍，XOR 图 + AND 掩码）"
                                % (e["index"], w, h2, e["w"], e["h"] * 2))
            if planes != 1 or bpp != 32:
                problems.append("I6 第 %d 档头里写的是 %d 平面 %d 位" % (e["index"], planes, bpp))
            if comp != 0:
                problems.append("I6 第 %d 档压缩方式不是 BI_RGB" % e["index"])
            mask_row = ((e["w"] + 31) // 32) * 4
            want = 40 + e["w"] * e["h"] * 4 + mask_row * e["h"]
            if len(p) != want:
                problems.append("I6 第 %d 档负载长 %d，期望 %d（含 AND 掩码）"
                                % (e["index"], len(p), want))
            if size_img not in (0, e["w"] * e["h"] * 4):
                problems.append("I6 第 %d 档 biSizeImage=%d，期望 %d 或 0"
                                % (e["index"], size_img, e["w"] * e["h"] * 4))
        else:
            if p[:8] != b"\x89PNG\r\n\x1a\n":
                problems.append("I7 第 %d 档（%dpx）不是 PNG 负载" % (e["index"], e["w"]))
                continue
            try:
                w, h, depth, ctype = struct.unpack_from(">IIBB", p, 16)
            except struct.error:
                problems.append("I7 第 %d 档读不出 IHDR" % e["index"])
                continue
            if (w, h) != (e["w"], e["h"]):
                problems.append("I7 第 %d 档 IHDR 是 %dx%d，目录声明 %dx%d"
                                % (e["index"], w, h, e["w"], e["h"]))
            if depth != 8 or ctype != 6:
                problems.append("I7 第 %d 档 IHDR 位深 %d / 颜色类型 %d，期望 8 / 6（RGBA）"
                                % (e["index"], depth, ctype))
            if not p.rstrip(b"\x00").endswith(b"IEND\xaeB`\x82"):
                problems.append("I7 第 %d 档没有以 IEND 收尾：PNG 被截断了" % e["index"])

    # --- I8 内容 ---
    # 前面几条只说明「文件合法」，这一条说明「画的还是那张卡片」。
    for e in entries:
        try:
            w, h, rows = decode(e)
        except Exception as exc:
            problems.append("I8 第 %d 档解不开：%s" % (e["index"], exc))
            continue
        if (w, h) != (e["w"], e["h"]):
            problems.append("I8 第 %d 档解出来是 %dx%d" % (e["index"], w, h))
            continue

        for label, (x, y) in (("左上", (0, 0)), ("右上", (w - 1, 0)),
                              ("左下", (0, h - 1)), ("右下", (w - 1, h - 1))):
            if rows[y][x][3] > CORNER_ALPHA_MAX:
                problems.append("I8 第 %d 档 %s 角不透明（alpha=%d）："
                                "卡片是圆角的，四角应当透空"
                                % (e["index"], label, rows[y][x][3]))

        mid = rows[h // 2]
        opaque = [(x, p) for x, p in enumerate(mid) if p[3] >= 240]
        if not opaque:
            problems.append("I8 第 %d 档中间一行没有一个实心像素" % e["index"])
            continue
        # 描边要看「左边第一个实心像素」而不是「最深的像素」：竖列的笔画也是 #2A2A2A，
        # 比描边更深更实，按最深的找会一路找到右边那一列上去（踩过一次）。
        edge_x, edge_px = opaque[0]
        if edge_x > w * 0.15:
            problems.append("I8 第 %d 档左边缘的实心像素在 x=%d（宽 %d）："
                            "卡片没有铺满画布，缩到小尺寸会被裁"
                            % (e["index"], edge_x, w))
        elif lum(edge_px) > INK_MAX:
            problems.append("I8 第 %d 档左边缘第一个实心像素亮度 %d，超过 %d："
                            "卡片那圈 #2A2A2A 描边没画出来"
                            % (e["index"], lum(edge_px), INK_MAX))
        brightest = max(opaque, key=lambda t: lum(t[1]))
        if lum(brightest[1]) < PAPER_MIN:
            problems.append("I8 第 %d 档中间一行最亮的像素亮度 %d，低于 %d："
                            "纸面不是白的" % (e["index"], lum(brightest[1]), PAPER_MIN))

        seal_hit = False
        for y in range(h // 2, h):
            for x in range(w):
                r, g, b, a = rows[y][x]
                if a >= 200 and r > 150 and r - max(g, b) > 60:
                    seal_hit = True
                    break
            if seal_hit:
                break
        if not seal_hit:
            problems.append("I8 第 %d 档下半部分找不到朱红像素：朱印没画出来" % e["index"])

    # --- I9 构建脚本仍然指向它 ---
    names = [f for f in os.listdir(src_dir) if f.endswith(".ico")] if os.path.isdir(src_dir) else []
    if "card.ico" not in names:
        problems.append("I9 源码目录里没有 card.ico：%s" % src_dir)
    for fname, needle in (("build.bat", "card.ico"), ("build-macos.csproj", "card.ico")):
        path = os.path.join(src_dir, fname)
        if not os.path.isfile(path):
            path = os.path.join(os.path.dirname(src_dir), fname)
        if not os.path.isfile(path):
            path = os.path.join(os.path.dirname(os.path.dirname(src_dir)), fname)
        if not os.path.isfile(path):
            problems.append("I9 找不到 %s，无法核对图标引用" % fname)
            continue
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            body = fh.read()
        if needle not in body:
            problems.append("I9 %s 里不再引用 %s：新图标不会被编进 exe" % (fname, needle))

    # --- I10 托盘图标必须来自 exe 的图标资源，而不是另画一张 ---
    # 「右键图标更新了，但托盘图标和程序图标没有更新」：窗口图标是从 exe 的资源里取的，
    # 托盘图标却是 CreateTrayIcon() 里手绘的一张（深色圆底加白色引号，换 card.ico 之前的
    # 样式）。换了图标文件之后窗口跟着变了，托盘还留在旧样子上。
    #
    # 手绘那张只能当兜底，而且整个方法体里取 exe 图标必须走在它前面 —— 所以这几条都查
    # CreateTrayIcon 的**方法体**，不是整个文件（NativeMethods 里也有 ExtractIconEx 的声明）。
    forms = _read_source(src_dir)
    if forms is None:
        problems.append("I10 找不到 Forms.cs，无法核对托盘图标的来源")
    else:
        # 修饰符可能是 private 也可能是 internal（自检要直接调它），所以只认签名。
        if "Icon CreateTrayIcon()" not in forms:
            problems.append("I10 找不到 CreateTrayIcon")
        else:
            tray = _method_body(forms, "Icon CreateTrayIcon()")
            if "ExtractIconEx" not in tray:
                problems.append("I10 CreateTrayIcon 没有从 exe 的图标资源里取图标"
                                "（ExtractIconEx）：托盘会显示一张和程序图标不一样的图")
            if "ExtractAssociatedIcon" not in tray:
                problems.append("I10 CreateTrayIcon 没有 ExtractAssociatedIcon 兜底："
                                "资源在但拆不开时托盘会什么都没有")
            if "Application.ExecutablePath" not in tray:
                problems.append("I10 CreateTrayIcon 取图标时没有指向本程序："
                                "换一台机器或改个名就对不上了")
            if not tray.rstrip().endswith("return DrawnTrayIcon();"):
                problems.append("I10 CreateTrayIcon 里手绘兜底不是最后一步："
                                "手绘的那张会盖掉 exe 里的图标")
        # 手绘图标的代码只许有一处。多出一处就意味着「有一处忘了换」。
        for needle, what in (("FillEllipse(", "画椭圆的那一支"), ("GetHicon()", "GetHicon")):
            hits = forms.count(needle)
            if hits != 1:
                problems.append("I10 手绘图标的代码不止一处（%s 出现 %d 次）："
                                "又会出现「有一处忘了换」" % (what, hits))

    return problems


def _read_source(src_dir):
    """按 I9 的同一条回退链找 Forms.cs。"""
    here = src_dir
    for _ in range(3):
        path = os.path.join(here, "Forms.cs")
        if os.path.isfile(path):
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                return fh.read()
        parent = os.path.dirname(here)
        if parent == here:
            break
        here = parent
    return None


def _method_body(text, signature):
    """按花括号配平取一个方法体（不含外层花括号）。"""
    i = text.index(signature)
    j = text.index("{", i)
    depth = 0
    for k in range(j, len(text)):
        if text[k] == "{":
            depth += 1
        elif text[k] == "}":
            depth -= 1
            if depth == 0:
                return text[j + 1:k]
    return ""


# ---------------------------------------------------------------- 反向验证

def _dib_bytes(rows):
    """把 rows 重新编成 32bpp DIB（含 AND 掩码），用来造内容层面的注入样本。"""
    h = len(rows)
    w = len(rows[0])
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = rows[y][x]
            xor += bytes((b, g, r, a))
    mask_row = ((w + 31) // 32) * 4
    andmask = bytearray()
    for y in range(h - 1, -1, -1):
        bits = bytearray(mask_row)
        for x in range(w):
            if rows[y][x][3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        andmask += bits
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0,
                         len(xor), 0, 0, 0, 0)
    return header + bytes(xor) + bytes(andmask)


def _png_bytes(rows):
    """同上，编成 PNG。"""
    h = len(rows)
    w = len(rows[0])
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        for x in range(w):
            raw += bytes(rows[y][x])

    def chunk(tag, body):
        return (struct.pack(">I", len(body)) + tag + body +
                struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF))

    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def _encode(w, h, rows):
    return _dib_bytes(rows) if w <= 48 else _png_bytes(rows)


def _rebuild(layout):
    """按 (w, h, payload) 列表重新拼一个 ICO。"""
    header = struct.pack("<HHH", 0, 1, len(layout))
    offset = len(header) + 16 * len(layout)
    directory = bytearray()
    body = bytearray()
    for w, h, payload in layout:
        directory += struct.pack("<BBBBHHII", w if w < 256 else 0, h if h < 256 else 0,
                                 0, 0, 1, 32, len(payload), offset)
        body += payload
        offset += len(payload)
    return header + bytes(directory) + bytes(body)


def _first_dib_index(entries):
    for e in entries:
        if e["w"] <= 48:
            return e["index"]
    return 0


def _layout(data):
    _, _, _, entries = parse_ico(data)
    return [(e["w"], e["h"], e["payload"]) for e in entries]


def _swapped(data, size, transform):
    """把某一档解出来、按 transform 改像素、再编回去。"""
    layout = _layout(data)
    for i, (w, h, payload) in enumerate(layout):
        if w != size:
            continue
        _, _, rows = decode(parse_ico(data)[3][i])
        layout[i] = (w, h, _encode(w, h, transform(w, h, rows)))
    return _rebuild(layout)


def injections(data):
    """(说明, 变异后的字节, 期望报出的断言前缀)。"""
    out = []
    _, _, count, entries = parse_ico(data)
    di = _first_dib_index(entries)
    e = entries[di]
    big = [x for x in entries if x["w"] >= 64][0]

    def patch(offset, fmt, *values):
        b = bytearray(data)
        struct.pack_into(fmt, b, offset, *values)
        return bytes(b)

    def dirent(entry):
        """目录项的起始偏移：6 + 16*i。"""
        return 6 + 16 * entry["index"]

    biggest = [x for x in entries if x["w"] == 256][0]

    out.append(("目录只声明一档（退回单档图标）", patch(4, "<H", 1), "I1"))
    out.append(("256 档的宽高不再写作 0（写成 128）",
                patch(dirent(biggest), "<BB", 128, 128), "I3"))
    out.append(("DIB 档的 biHeight 忘了写两倍", patch(e["offset"] + 8, "<i", e["h"]), "I6"))
    # 位深/平面数在目录里和 DIB 头里各存一份，两处都要能报出来，所以各注入一次
    out.append(("目录里的位深改成 24", patch(dirent(e) + 6, "<H", 24), "I4"))
    out.append(("DIB 头里的位深改成 24", patch(e["offset"] + 14, "<H", 24), "I6"))
    out.append(("目录里的平面数改成 2", patch(dirent(e) + 4, "<H", 2), "I4"))
    out.append(("DIB 头里的平面数改成 2", patch(e["offset"] + 12, "<H", 2), "I6"))
    out.append(("PNG 档 IHDR 的宽度被改小一半",
                patch(big["offset"] + 16, ">I", big["w"] // 2), "I3"))
    out.append(("PNG 档的签名被抹掉",
                data[:big["offset"]] + b"\x00" * 8 + data[big["offset"] + 8:], "I7"))
    # 目录里的长度字段才是「负载有多长」，改 DIB 头里的 biSizeImage 是另一回事
    out.append(("目录里的负载长度被截短 8 字节",
                patch(dirent(e) + 8, "<I", e["size"] - 8), "I5"))
    out.append(("DIB 头里的 biSizeImage 被改错", patch(e["offset"] + 20, "<I", 8), "I6"))
    out.append(("文件末尾多出 16 字节垃圾", data + b"\x00" * 16, "I5"))
    out.append(("两档的偏移重叠",
                patch(dirent(entries[1]) + 12, "<I", entries[0]["offset"]), "I5"))
    out.append(("第 0 档的负载长度写成 0", patch(dirent(entries[0]) + 8, "<I", 0), "I5"))

    # --- 内容层面的注入：结构完全合法，只有把图画出来才看得见 ---
    out.append(("64 档换成全白不透明方块（丢了描边与朱印）",
                _swapped(data, 64, lambda w, h, r: [[(255, 255, 255, 255)] * w] * h), "I8"))
    out.append(("64 档换成全透明（图标等于没有）",
                _swapped(data, 64, lambda w, h, r: [[(0, 0, 0, 0)] * w] * h), "I8"))
    out.append(("16 档的朱印涂成纸色",
                _swapped(data, 16, lambda w, h, r: [
                    [(255, 255, 255, p[3]) if (p[3] >= 200 and p[0] > 150
                                               and p[0] - max(p[1], p[2]) > 60) else p
                     for p in row] for row in r]), "I8"))
    out.append(("16 档的描边涂成纸色",
                _swapped(data, 16, lambda w, h, r: [
                    [(255, 255, 255, p[3]) if (p[3] >= 240 and lum(p) <= 130) else p
                     for p in row] for row in r]), "I8"))
    return out


def reverse(src_path):
    with open(src_path, "rb") as fh:
        data = fh.read()
    cases = injections(data)
    caught = 0
    for label, mutated, want in cases:
        problems = audit(mutated, SRC_DIR)
        hit = any(want in p for p in problems)
        if hit:
            caught += 1
            print("  [OK ] %-34s → 报出 %d 条（%s）"
                  % (label, len(problems), problems[0].split(" ")[0]))
        elif problems:
            print("  [?? ] %-34s → 报了 %d 条但不是 %s（%s）"
                  % (label, len(problems), want, problems[0][:44]))
        else:
            print("  [!! ] %-34s → 一条都没报出来" % label)
    print("注入 %d 处，报出 %d 处" % (len(cases), caught))
    return caught == len(cases)


# 源码级注入：改 Forms.cs 而不是图标字节。锚点同样必须唯一 —— `str.replace(old, new, 1)`
# 只替换第一处，锚点在两处都出现时注入会打到另一处，要验的那条一条都报不出来。
SOURCE_INJECTIONS = [
    ("CreateTrayIcon 不调用 ExtractIconEx",
     "                if (NativeMethods.ExtractIconEx(path, 0, large, small, 1) > 0)",
     "                if (false)", "I10"),
    ("CreateTrayIcon 取图标时不指向本程序",
     "try { path = Application.ExecutablePath; }", "try { path = null; }", "I10"),
    ("CreateTrayIcon 不把手绘图降级成兜底",
     "            return DrawnTrayIcon();", "            return null;", "I10"),
    ("又多出一处手绘图标",
     "        private static Icon DrawnTrayIcon()",
     "        private static Icon DrawnTrayIcon2()\n"
     "        {\n"
     "            using (Bitmap b = new Bitmap(8, 8))\n"
     "            using (Graphics g = Graphics.FromImage(b))\n"
     "                g.FillEllipse(Brushes.Black, 0, 0, 8, 8);\n"
     "            return null;\n"
     "        }\n\n"
     "        private static Icon DrawnTrayIcon()", "I10"),
]


def reverse_source(forms, data):
    """注入到 Forms.cs：把整份源码拷进临时目录再改，这样 I9 那几条仍然过得去。"""
    caught = 0
    for label, old, new, want in SOURCE_INJECTIONS:
        if old not in forms:
            print("  [!! ] %-34s → 注入点没找到（脚本与源码脱节）" % label)
            continue
        if forms.count(old) != 1:
            print("  [!! ] %-34s → 锚点出现 %d 次，注入会打到别处" % (label, forms.count(old)))
            continue
        mutated = forms.replace(old, new, 1)
        tmp = tempfile.mkdtemp(prefix="mqc-icon-")
        try:
            if os.path.isdir(SRC_DIR):
                for fname in os.listdir(SRC_DIR):
                    src = os.path.join(SRC_DIR, fname)
                    if os.path.isfile(src):
                        shutil.copy(src, os.path.join(tmp, fname))
            with open(os.path.join(tmp, "Forms.cs"), "w", encoding="utf-8") as fh:
                fh.write(mutated)
            problems = audit(data, tmp)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        hit = any(want in p for p in problems)
        if hit:
            caught += 1
            print("  [OK ] %-34s → 报出 %d 条（%s）"
                  % (label, len(problems), problems[0].split(" ")[0]))
        elif problems:
            print("  [?? ] %-34s → 报了 %d 条但不是 %s（%s）"
                  % (label, len(problems), want, problems[0][:44]))
        else:
            print("  [!! ] %-34s → 一条都没报出来" % label)
    return caught


def main():
    if not os.path.isfile(ICO):
        print("找不到图标：%s" % ICO)
        return 2
    with open(ICO, "rb") as fh:
        data = fh.read()
    if "--reverse" in sys.argv:
        print("== 图标断言反向验证 ==")
        ok = reverse(ICO)
        print("  -- 源码级注入（Forms.cs）--")
        forms = _read_source(SRC_DIR)
        if forms is None:
            print("  （跳过：找不到 Forms.cs）")
        else:
            caught = reverse_source(forms, data)
            print("源码注入 %d 处，报出 %d 处" % (len(SOURCE_INJECTIONS), caught))
            ok = ok and caught == len(SOURCE_INJECTIONS)
        return 0 if ok else 1
    problems = audit(data, SRC_DIR)
    if problems:
        print("== 图标断言：失败 ==")
        for p in problems:
            print("  [!!] %s" % p)
        return 1
    _, _, count, entries = parse_ico(data)
    print("== 图标断言：全部通过（%d 档：%s，%d 字节） =="
          % (count, "/".join(str(e["w"]) for e in entries), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
