#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""源码级静态断言：卡片尺寸不变量（可自由缩放 / 最小为文字区域大小）。

为什么需要它：macOS 上跑不了 exe，而「最小尺寸」这件事横跨两个文件、三处代码——
    LayoutCard()  决定文字视图比卡片小多少（边距）
    CardSizeLimits 用同一个边距把文字块换算成卡片的最小尺寸
    UpdateCardSizeLimits / ApplyHitFrame 负责把结果真的用上
任何一处对不上，症状都是「卡片把自己的文字裁掉」或者「拖不到文字大小」，而这两种
症状都不会编译报错。所以这里把它们钉成断言。

断言全部作用在「剥掉注释之后」的源码上：本项目的注释习惯是把常量名/函数名写出来
解释为什么这么写，不剥注释会把「注释里提到过」当成「代码里写了」。

用法：
    python3 mqc_size_check.py                # 跑全部断言
    python3 mqc_size_check.py --reverse      # 注入缺陷做反向验证
    python3 mqc_size_check.py --src <目录>   # 指定源码目录
"""
import math
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_DEFAULT_SRC = os.path.join(_HERE, "src", "XiaoJian")
if not os.path.isdir(_DEFAULT_SRC):
    # 交付目录里三个 .cs 就摆在脚本旁边
    # 交付目录里几个 .cs 就摆在脚本旁边；仓库布局里脚本在 tools/，源码在上两级。
    _DEFAULT_SRC = _HERE if os.path.isfile(os.path.join(_HERE, "Forms.cs")) \
        else os.path.normpath(os.path.join(_HERE, "..", "src", "XiaoJian"))

SRC = _DEFAULT_SRC
for _i, _a in enumerate(sys.argv):
    if _a == "--src" and _i + 1 < len(sys.argv):
        SRC = sys.argv[_i + 1]

FORMS = os.path.join(SRC, "Forms.cs")
STORAGE = os.path.join(SRC, "Storage.cs")


# ---------------------------------------------------------------- 源码预处理

def strip_comments(text):
    """剥掉 // 与 /* */ 注释，但完整保留字符串字面量与字符字面量。

    注释用等长空格替换（换行保留），这样行号与原文一一对应。
    保留字符串字面量是必须的：L"http://schemas.microsoft.com/..." 里的 // 不能被当注释。
    """
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == "@" and i + 1 < n and text[i + 1] == '"':
            j = i + 2
            while j < n:
                if text[j] == '"':
                    if j + 1 < n and text[j + 1] == '"':
                        j += 2
                        continue
                    break
                j += 1
            out.append(text[i:j + 1])
            i = j + 1
            continue
        if c in ('"', "'"):
            quote = c
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == quote:
                    break
                j += 1
            out.append(text[i:j + 1])
            i = j + 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            if j < 0:
                j = n
            out.append(" " * (j - i))
            i = j
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("".join(ch if ch == "\n" else " " for ch in text[i:j]))
            i = j
            continue
        out.append(c)
        i += 1
    return "".join(out)


def _skip_literal(text, i):
    if text[i] == "@":
        j = i + 2
        while j < len(text):
            if text[j] == '"':
                if j + 1 < len(text) and text[j + 1] == '"':
                    j += 2
                    continue
                break
            j += 1
        return j + 1
    quote = text[i]
    j = i + 1
    while j < len(text):
        if text[j] == "\\":
            j += 2
            continue
        if text[j] == quote:
            break
        j += 1
    return j + 1


def block_at(text, open_index):
    """从 '{' 开始配平，返回 (花括号内部文本, 右花括号索引)。跳过字面量。"""
    depth = 0
    k = open_index
    while k < len(text):
        c = text[k]
        if c == '"' or c == "'" or (c == "@" and k + 1 < len(text) and text[k + 1] == '"'):
            k = _skip_literal(text, k)
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return text[open_index + 1:k], k
        k += 1
    raise ValueError("花括号不配平，起点 %d" % open_index)


def safe_body(src, signature, problems, label):
    """取成员体；签名找不到/不唯一时记成一条问题，而不是抛异常打断整轮检查。"""
    if src.count(signature) != 1:
        problems.append("%s 取不到（出现 %d 次）：%s" % (label, src.count(signature), signature))
        return None
    try:
        i = src.index(signature)
        j = src.index("{", i)
        return block_at(src, j)[0]
    except ValueError as exc:
        problems.append("%s 解析失败：%s" % (label, exc))
        return None


def body_by_marker(src, signature, marker):
    """同名成员有多个（例如两个窗体各有一个 OnResize）时，按方法体里的特征串挑一个。"""
    start = 0
    while True:
        i = src.find(signature, start)
        if i < 0:
            return None
        try:
            body = block_at(src, src.index("{", i))[0]
        except ValueError:
            return None
        if marker in body:
            return body
        start = i + 1


def const_int(src, name):
    """取 `const int NAME = 123;` 里的 123。"""
    m = re.search(r"const\s+int\s+%s\s*=\s*(-?\d+)\s*;" % re.escape(name), src)
    return int(m.group(1)) if m else None


def const_table(src):
    """把文件里所有 `const int NAME = <整数或常量名相加>;` 收成一张表。

    HorizontalMargin 写成了 TextPadLeft + TextPadRight 这样的和式，只看字面量的 const_int
    读不出来。这张表让「边距等于两个内边距之和」这句话本身可以被断言 —— 改了一个忘了
    另一个，就是这里报出来。

    表达式字符集要放宽到 + * ( ) - / ：只认 `+` 的时候，MenuButtonMinCardWidth 这种
    `MenuButtonRightInset * 2` 的写法会被静默跳过，于是 Z15「找不到阈值」的报错看起来
    像断言写错了，实际是解析器读不出来（踩过一次）。eval 的名字空间只有这张表本身，
    查不到的名字抛 NameError 被吞掉，不会执行任何真实代码。
    """
    table = {}
    for _ in range(4):                      # 常量之间可以互相引用，多跑几轮收敛
        for name, body in re.findall(r"const\s+int\s+(\w+)\s*=\s*([^;]+);", src):
            if name in table or not re.match(r"^[\s\w+*()/-]+$", body):
                continue
            try:
                table[name] = int(eval(body, {"__builtins__": {}}, table))
            except Exception:
                pass
    return table


# ---------------------------------------------------------------- 断言

def check_sizes(forms, storage):
    problems = []

    # 先把要用到的成员体取出来，后面各条断言共用。取不到就记一条问题并跳过那一组，
    # 而不是抛异常打断整轮检查（对「改动前的源码」跑这份脚本时，新方法本来就不存在）。
    layout = safe_body(forms, "private void LayoutCard()", problems, "LayoutCard")
    min_for = safe_body(forms, "public static Size MinimumFor(Measure measure, bool vertical)",
                        problems, "CardSizeLimits.MinimumFor(measure, vertical)")
    probe = safe_body(forms, "private static int NextProbe(int probe)", problems,
                      "CardSizeLimits.NextProbe")
    floor_for = safe_body(forms, "private Size DragLimit()", problems, "QuoteCardForm.DragLimit")
    smallest_card = safe_body(forms, "private Size SmallestCard()", problems, "SmallestCard")
    with_floor = safe_body(forms,
                           "private Size WithFloorMeasure(Func<CardSizeLimits.Measure, Size> pick)",
                           problems, "WithFloorMeasure")
    drag_for = safe_body(forms, "public static Size DragLimitFor(Measure measure, bool vertical, "
                                "int flowAxis)", problems, "CardSizeLimits.DragLimitFor")
    grow = safe_body(forms, "private void GrowCardToText()", problems, "GrowCardToText")
    update = safe_body(forms, "private void UpdateCardSizeLimits()", problems,
                       "UpdateCardSizeLimits")
    ceiling = safe_body(forms, "private Size ClampToCeiling(Size floor)", problems, "ClampToCeiling")
    hit = safe_body(forms, "private void ApplyHitFrame(Rectangle frame)", problems, "ApplyHitFrame")
    box = safe_body(forms, "public TransparentHitBoxForm()", problems, "TransparentHitBoxForm")
    ctor = safe_body(forms, "internal QuoteCardForm(AppState app, CardState card, "
                            "QuoteRepository repository, bool showTrayIcon)", problems,
                     "QuoteCardForm 构造函数")
    clamp = safe_body(storage, "public void Clamp()", problems, "CardState.Clamp")

    # --- Z1 布局边距 == 尺寸常量 ---------------------------------------
    # LayoutCard 把文字视图按 (TextPadLeft, TextPadTop, W-w, H-h) 摆进卡片；卡片的最小尺寸
    # 必须用同一组数字。两边对不上，卡片就会裁掉自己的文字，或者留一圈拖不掉的空白。
    # 这一版起纵向边距不再是一个写死的 40，而是上下两个内边距相加 —— LayoutCard 与
    # MinimumFor 读的都是同一个和，所以「和」本身也要断言。
    limits = const_table(forms)
    pads = [limits.get(n) for n in ("TextPadLeft", "TextPadRight", "TextPadTop", "TextPadBottom")]
    horizontal = limits.get("HorizontalMargin")
    vertical = limits.get("VerticalMargin")
    line = limits.get("SmallestLineHeight")
    slack = limits.get("TextSlack")
    # 先解包，后面的 Z14 也要用它们 —— 上一版源码里这些常量还不存在（都是 None），
    # 那一组必须照样能跑出「真正的毛病」而不是抛异常。
    pad_left, pad_right, pad_top, pad_bottom = pads
    if any(p is None for p in pads) or horizontal is None or vertical is None:
        problems.append("Z1 CardSizeLimits 里找不到 TextPadLeft/Right/Top/Bottom 或 "
                        "HorizontalMargin / VerticalMargin 常量")
    else:
        if horizontal != pad_left + pad_right:
            problems.append("Z1 HorizontalMargin=%d 不等于 TextPadLeft+TextPadRight=%d"
                            % (horizontal, pad_left + pad_right))
        if vertical != pad_top + pad_bottom:
            problems.append("Z1 VerticalMargin=%d 不等于 TextPadTop+TextPadBottom=%d"
                            % (vertical, pad_top + pad_bottom))
    if layout is not None:
        # 摆法必须逐字用常量：写成字面量就等于把「唯一真值源」拆成两份。
        if "_quoteView.SetBounds(CardSizeLimits.TextPadLeft, CardSizeLimits.TextPadTop," not in layout:
            problems.append("Z1 LayoutCard 没有用 TextPadLeft/TextPadTop 摆文字视图"
                            "（摆法与 CardSizeLimits 脱节）")
        if "ClientSize.Width - CardSizeLimits.HorizontalMargin" not in layout:
            problems.append("Z1 LayoutCard 的宽度没有减 CardSizeLimits.HorizontalMargin")
        if "ClientSize.Height - CardSizeLimits.VerticalMargin" not in layout:
            problems.append("Z1 LayoutCard 的高度没有减 CardSizeLimits.VerticalMargin")

    # --- Z2 MinimumFor(measure, vertical)：取「面积最小的那张卡片」 -------------
    # 下限必须只由文字和字体决定，绝不许读卡片当前的尺寸：依赖当前尺寸的下限会跟着卡片一起
    # 移动，卡片就能顺着一条不断下移的下限一直缩下去。
    if min_for is not None:
        _sig = forms.split("public static Size MinimumFor(", 1)
        if len(_sig) > 1 and "Size current" in _sig[1].split(")", 1)[0]:
            problems.append("Z2 MinimumFor 又收下了当前尺寸：下限会跟着卡片移动，"
                            "卡片能一路缩下去")
        # 探针方向：横排探宽度（折行由宽度决定），竖排探高度（分列由高度决定）；
        # 另一轴留到 WidestView，块才是「不裁字」的真实足迹。
        if "Size block = vertical ? measure(WidestView, probe) : measure(probe, WidestView);" \
                not in min_for:
            problems.append("Z2 MinimumFor 的探针方向不对：横排要探宽度、竖排要探高度，"
                            "另一轴必须留空（否则量到的是被裁过的块）")
        if "if (block.Width <= 0 || block.Height <= 0) continue;" not in min_for:
            problems.append("Z2 MinimumFor 没有跳过空块（空文本/空视图会把面积算成 0）")
        if "int width = Math.Max(CardState.MinCardWidth," not in min_for:
            problems.append("Z2 MinimumFor 的宽度没有兜在退化态下限上")
        if "(vertical ? block.Width + TextSlack : probe) + HorizontalMargin" not in min_for:
            problems.append("Z2 MinimumFor 的宽度不是「横排用探针 / 竖排用块宽 + TextSlack」"
                            "再加横向边距")
        if "(vertical ? probe : block.Height + TextSlack) + VerticalMargin" not in min_for:
            problems.append("Z2 MinimumFor 的高度不是「竖排用探针 / 横排用块高 + TextSlack」"
                            "再加纵向边距")
        # 退化态下限必须加在「候选」身上。加在赢家身上时，赢家可能被抬到一个本来不需要抬的
        # 候选之上 —— 小文字量（2 个字）时正好会踩到。
        if min_for.count("Math.Max(CardState.MinCard") != 2:
            problems.append("Z2 MinimumFor 没有把退化态下限加在候选身上")
        if "Size best = new Size(CardState.MinCardWidth, CardState.MinCardHeight);" not in min_for:
            problems.append("Z2 MinimumFor 没有把退化态下限当初始值"
                            "（空文字时一个候选都量不出块）")
        # 面积必须用 long：4096 x 4096 的两倍就溢出了 int，溢出会让「面积最小」选错候选。
        if "(long)width * height" not in min_for:
            problems.append("Z2 MinimumFor 的面积没有用 long 乘：大卡片会溢出 int，选错候选")
        if "if (area < bestArea)" not in min_for:
            problems.append("Z2 MinimumFor 不是在取面积最小的那张卡片")
        if "SmallestView" not in min_for or "WidestView" not in min_for:
            problems.append("Z2 MinimumFor 的探针范围不是 SmallestView..WidestView")

    # --- Z3 NextProbe：探针步进必须严格递增，否则循环出不来 --------------------
    if probe is not None:
        if "probe + probe / 8 + 1" not in probe:
            problems.append("Z3 NextProbe 不是几何步进（等步长跑 10..4096 要四千次量文字）")
        if "Math.Max(probe + 1," not in probe:
            problems.append("Z3 NextProbe 没有兜住「至少 +1」：小探针会原地打转，死循环")
    else:
        problems.append("Z3 找不到 CardSizeLimits.NextProbe")
    if min_for is not None and "for (int probe = SmallestView; probe <= WidestView; probe = NextProbe(probe))" \
            not in min_for:
        problems.append("Z3 MinimumFor 的循环没有用 NextProbe 步进")

    # --- Z4 回归锁：固定尺寸不许回到它原来的五个位置 -------------------------
    # 这次要拿掉的就是 340x140 下限与 700x320 上限。它们原来分别待在这五处，所以就在这五处
    # 上锁；全文件扫描会误伤（自检里那句「不得大于 340」的断言、以及无关的亮度阈值 140）。
    locked = [
        ("CardState.Clamp", clamp),
        ("QuoteCardForm 构造函数", ctor),
        ("UpdateCardSizeLimits", update),
        ("ApplyHitFrame", hit),
        ("TransparentHitBoxForm 构造函数", box),
    ]
    for where, body in locked:
        if body is None:
            continue
        for literal in ("340", "700", "140", "320"):
            if re.search(r"\b%s\b" % literal, body):
                problems.append("Z4 %s 里又出现了固定尺寸数字 %s" % (where, literal))

    # --- Z5 最小尺寸必须在两条路径上刷新 -------------------------------------
    resize = body_by_marker(forms, "protected override void OnResize(EventArgs e)",
                            "UpdateRoundedRegion()")
    if resize is None:
        problems.append("Z5 找不到 QuoteCardForm.OnResize（带 UpdateRoundedRegion 的那个）")
    elif "UpdateCardSizeLimits();" not in resize:
        problems.append("Z5 OnResize 没有刷新最小尺寸（拖边改大小后最小值会停在旧数字）")
    fit = safe_body(forms, "private void FitQuoteFont()", problems, "FitQuoteFont")
    if fit is not None and "UpdateCardSizeLimits();" not in fit:
        problems.append("Z5 FitQuoteFont 没有刷新最小尺寸（换字体/换竖排后最小值不跟着走）")

    # --- Z6 刷新时不许无条件赋值 --------------------------------------------
    if update is not None:
        if "Size floor = DragLimit();" not in update:
            problems.append("Z6 UpdateCardSizeLimits 没有从 DragLimit 取下限")
        if "if (MinimumSize.Width == floor.Width && MinimumSize.Height == floor.Height) return;" \
                not in update:
            problems.append("Z6 UpdateCardSizeLimits 无条件给 MinimumSize 赋值，"
                            "会在 Resize 里反复触发 SetBounds")
        if update.count("MinimumSize = ") != 2:
            problems.append("Z6 UpdateCardSizeLimits 里 MinimumSize 的赋值不是两处"
                            "（「已经太小 → 顶大」与「常规刷新」）")
        if "if (!_card.AutoFitText) return;" not in update:
            problems.append("Z6 自动字号关掉时下限没有让路：卡片会被锁死，再也改不了大小")
        # 下限一旦大于卡片，MinimumSize 的 setter 会反过来改卡片尺寸 —— 不夹在桌面上限之内，
        # 长文字会把 MinimumSize 顶到 MaximumSize 之上，两个约束互相矛盾。
        if update.count("ClampToCeiling(floor)") != 2:
            problems.append("Z6 UpdateCardSizeLimits 没有把下限夹在桌面上限之内"
                            "（MinimumSize 会超过 MaximumSize）")

    # --- Z6b 下限夹在桌面上限之内 ------------------------------------------
    if ceiling is not None:
        if "Math.Min(floor.Width, MaximumSize.Width)" not in ceiling or \
                "Math.Min(floor.Height, MaximumSize.Height)" not in ceiling:
            problems.append("Z6b ClampToCeiling 没有按 MaximumSize 的两个方向夹住下限")

    # --- Z7 拖拽必须用动态上下限，而不是字面量 --------------------------------
    if hit is not None:
        if hit.count("MinimumSize.") < 2 or hit.count("MaximumSize.") < 2:
            problems.append("Z7 ApplyHitFrame 没有同时用 MinimumSize / MaximumSize 夹住新尺寸")
        if re.search(r"Math\.(Min|Max)\(\s*\d{2,}", hit):
            problems.append("Z7 ApplyHitFrame 里出现了字面量尺寸")

    # --- Z8 透明点击层不许自带固定上限 ---------------------------------------
    # 点击层以卡片为属主、尺寸跟着文字走。它自己带一个 700x320 的帽子时，大卡片的点击层会被
    # 截到 700 宽，卡片再按点击层回读的尺寸把自己压回去 —— 表现就是「卡片拉不大」。
    if box is not None and "MaximumSize" in box:
        problems.append("Z8 TransparentHitBoxForm 自己设了 MaximumSize，大卡片会被它压回来")

    # --- Z9 预设尺寸要记实际结果 --------------------------------------------
    # 卡片拒绝小于文字，所以预设值可能被顶大；存下来的必须是卡片真正的尺寸。
    size_item = safe_body(forms, "private ToolStripMenuItem CreateSizeItem(string name, int width, int height)",
                          problems, "CreateSizeItem")
    if size_item is not None:
        if "_card.CardWidth = Width;" not in size_item or "_card.CardHeight = Height;" not in size_item:
            problems.append("Z9 CreateSizeItem 把请求的尺寸而不是实际尺寸写进了配置")
    fit_text = safe_body(forms, "internal void FitCardToText()", problems, "FitCardToText")
    if fit_text is not None:
        if "Size wanted = SmallestCard();" not in fit_text:
            problems.append("Z9 FitCardToText 没有用 SmallestCard 算贴合尺寸："
                            "它要挑的是一个形状，用拖动下限只会把当前形状钉住")
        # 拖动下限可能比「面积最小」那张卡片还高（一张拖窄了的卡片下限很高），而 Form.Size
        # 会被 MinimumSize 夹住 —— 不清空下限就设尺寸，这一步会静悄悄地什么都不做。
        if "MinimumSize = new Size(CardState.MinCardWidth, CardState.MinCardHeight);" \
                not in fit_text:
            problems.append("Z9 FitCardToText 没有先把 MinimumSize 清回退化态下限："
                            "Form.Size 会被夹住，卡片缩不到贴合尺寸")
        if "_card.CardWidth = Width;" not in fit_text or "_card.CardHeight = Height;" not in fit_text:
            problems.append("Z9 FitCardToText 没有把实际尺寸写回配置")
        if "for (int pass = 0; pass < 4; pass++)" not in fit_text:
            problems.append("Z9 FitCardToText 只量了一次；自动字号关掉时字体跟着宽度走，"
                            "量一次得到的不是真正的贴合尺寸")

    # --- Z10 存储层的钳制必须来自常量 ---------------------------------------
    if clamp is not None:
        if "Math.Max(MinCardWidth, Math.Min(MaxCardWidth, CardWidth))" not in clamp:
            problems.append("Z10 CardState.Clamp 没有用 MinCardWidth / MaxCardWidth 夹宽度")
        if "Math.Max(MinCardHeight, Math.Min(MaxCardHeight, CardHeight))" not in clamp:
            problems.append("Z10 CardState.Clamp 没有用 MinCardHeight / MaxCardHeight 夹高度")

    # --- Z11 卡片的构造里只放退化态下限 + 桌面上限 ----------------------------
    if ctor is not None:
        if "MinimumSize = new Size(CardState.MinCardWidth, CardState.MinCardHeight);" not in ctor:
            problems.append("Z11 构造函数没有把退化态下限写进 MinimumSize")
        if "MaximumSize = CardSizeLimits.MaximumFor(" not in ctor:
            problems.append("Z11 构造函数没有把上限交给 CardSizeLimits.MaximumFor（桌面尺寸）")

    # --- Z12 下限必须量在一个「不跟着卡片走」的字号上 ------------------------
    # 这是整件事的关键：自动字号开着时，卡片越小字号越小；如果拿卡片当前的字号去量下限，
    # 下限就跟着卡片一起变小——卡片能一直缩下去，最后停在一条装不下文字的缝里。
    # 参照字号必须是「字号搜索的下限」这个与卡片无关的常量。
    if with_floor is not None:
        if "QuoteView.SmallestFontSize" not in with_floor:
            problems.append("Z12 WithFloorMeasure 没有用 SmallestFontSize 当参照字号；"
                            "拿卡片当前字号去量，卡片越小下限越低，能一直缩下去")
        if "_card.AutoFitText" not in with_floor:
            problems.append("Z12 WithFloorMeasure 没有区分自动字号开关：关掉自动字号时"
                            "字体是用户自己选的，本来就不会跟着卡片缩")
        if "_quoteView.MeasureBlock" not in with_floor:
            problems.append("Z12 WithFloorMeasure 没有用 QuoteView 自己的测量函数"
                            "（画字与算尺寸会脱节）")
        if "MeasureBlock(bound, width, height)" not in with_floor:
            problems.append("Z12 WithFloorMeasure 在自动字号分支里没有把参照字号传进测量函数")
    # 下限本身不许收尺寸参数 —— 这条现在要查的是两个新的取值函数。
    for _name, _sig in (("DragLimit", "private Size DragLimit()"),
                        ("SmallestCard", "private Size SmallestCard()")):
        if _sig not in forms:
            problems.append("Z12 %s 又收下了尺寸参数：下限会跟着卡片移动" % _name)
    # 自动字号开着时，参照字号是常数；卡片当前的字号不许进入测量。
    if floor_for is not None and "int flowAxis = vertical ? Height : Width;" not in floor_for:
        problems.append("Z12 DragLimit 没有取当前卡片沿文字流向的长度："
                        "下限就不知道该按哪个折行结果来算")

    # --- Z16 拖动下限只约束「横跨文字流向」的那根轴 --------------------------
    # 这是这一版修的两条抱怨的共同根因。能装下文字的尺寸是一整条**曲线**：横排卡片可以宽而
    # 一行高，也可以窄而多行高。把「面积最小」那个**点**当成拖动下限用，等于把曲线剪成两截 ——
    # 横排卡片永远拖不到一行，竖排卡片永远收不到一列。
    if drag_for is not None:
        # 两条 return 各钉住一根轴。片段要取「一整行」而不是整条语句：源码在逗号后面折了行，
        # 跨行拼出来的串永远匹配不上（踩过一次）。
        # 横排约束高度：宽度方向只给「一个字」的下限，绝不读块宽。
        if "return new Size(unit.Width," not in drag_for:
            problems.append("Z16 DragLimitFor 的横排分支没有把宽度钉在 SmallestTextCard 上："
                            "宽度下限跟着块宽走，就是拿点当下限")
        if "Math.Max(unit.Height, block.Height + TextSlack + VerticalMargin)" not in drag_for:
            problems.append("Z16 DragLimitFor 的横排分支没有把高度算在块高上："
                            "它约束的是高度，块高加内缩才是那条下限")
        # 竖排约束宽度：高度方向只给「一个字」的下限，绝不读块高。
        if "Math.Max(unit.Width, block.Width + TextSlack + HorizontalMargin)" not in drag_for:
            problems.append("Z16 DragLimitFor 的竖排分支没有把宽度算在块宽上："
                            "它约束的是宽度，块宽加内缩才是那条下限")
        if "unit.Height);" not in drag_for:
            problems.append("Z16 DragLimitFor 的竖排分支没有把高度钉在 SmallestTextCard 上："
                            "高度下限跟着块高走，就是拿点当下限")
        # 量块时按流向轴取视图：横排给宽度、竖排给高度，另一轴留空。
        if "Size block = vertical ? measure(WidestView, viewFlow) : measure(viewFlow, WidestView);" \
                not in drag_for:
            problems.append("Z16 DragLimitFor 量块时没有按流向轴取视图："
                            "横排要给宽度、竖排要给高度，另一轴必须留空")
        # viewFlow 要减掉的是「横跨」那根轴的边距（竖排减 VerticalMargin），不是流向轴的。
        if "flowAxis - (vertical ? VerticalMargin : HorizontalMargin)" not in drag_for:
            problems.append("Z16 DragLimitFor 减边距减错了轴：竖排的列由高度决定，"
                            "要减的是 VerticalMargin")
    # SmallestTextCard 必须是两个轴各自的「一个字」下限，不能写成字面量。
    # 注意这条要查的是 SmallestTextCard **自己的方法体**：MinimumFor 里也有同样的
    # Math.Max(CardState.MinCardWidth, …)，查整个文件会把它的那份算进来，注入就再也打不中。
    unit_body = safe_body(forms, "public static Size SmallestTextCard", problems,
                          "CardSizeLimits.SmallestTextCard")
    if "public static Size SmallestTextCard" not in forms:
        problems.append("Z16 找不到 CardSizeLimits.SmallestTextCard")
    elif unit_body is not None:
        for _part, _expr in (("宽度", "SmallestGlyphWidth + TextSlack + HorizontalMargin"),
                             ("高度", "SmallestLineHeight + TextSlack + VerticalMargin")):
            if _expr not in unit_body:
                problems.append("Z16 SmallestTextCard 的%s没有用 %s 算："
                                "它是「一个字」的下限，写死数字就会悄悄过期"
                                % (_part, _expr))
        if "Math.Max(CardState.MinCardWidth," not in unit_body or \
                "Math.Max(CardState.MinCardHeight," not in unit_body:
            problems.append("Z16 SmallestTextCard 没有兜在 CardState 的退化态下限上")
    # 三处取值各就各位：拖动下限管拖动，面积最小的点管「适应文字大小」，撑大走拖动下限。
    if update is not None and "DragLimit()" not in update:
        problems.append("Z16 UpdateCardSizeLimits 没有用 DragLimit()")
    if fit_text is not None and "SmallestCard()" not in fit_text:
        problems.append("Z16 FitCardToText 没有用 SmallestCard()")
    if grow is not None:
        if "Size wanted = DragLimit();" not in grow:
            problems.append("Z16 GrowCardToText 没有用 DragLimit()：字号调大后只该沿被约束的"
                            "那根轴长大")
        # 撞到桌面上限时的出路是**沿流向轴把文字摊长**（FlowFitFor），不是「面积最小」那张卡。
        # 大字号下面积最小是「一个字一行」的窄条，比卡片现在还窄：Math.Max 之后什么都没长，
        # 卡片就此停住，字号每加一点都只是多裁掉一点 —— 表现就是「文字长到某个字号再也长不大」。
        if "CardSizeLimits.FlowFitFor(measure, vertical, across)" not in grow:
            problems.append("Z16 GrowCardToText 撞到桌面上限时没有用 FlowFitFor："
                            "退回「面积最小」那张卡只会让卡片停住，文字被越裁越多")
        # 撑不下时要把字号退回装得下的一档，而不是把裁掉的文字交给用户。
        # 查的是 SetManualFontSize **自己的方法体**。SetFontStyle / SetAutoFitMax 也以这一句
        # 结尾，查整个文件的话把它从 SetManualFontSize 里删掉照样「过」。
        manual_setter = safe_body(forms, "private void SetManualFontSize(float size)", problems,
                                  "Z16 SetManualFontSize")
        if manual_setter is not None and "BackOffToFittingSize();" not in manual_setter:
            problems.append("Z16 SetManualFontSize 没有在撑不下时把字号退回装得下的一档："
                            "「大一点」会一路把文字裁下去")

    # --- Z17 FlowFitFor 本身：DragLimitFor 的逆运算 -------------------------
    flow_fit = safe_body(forms, "public static Size FlowFitFor(Measure measure, bool vertical, "
                                "int across)", problems, "CardSizeLimits.FlowFitFor")
    if "public static Size FlowFitFor(Measure measure, bool vertical, int across)" not in forms:
        problems.append("Z17 找不到 CardSizeLimits.FlowFitFor")
    elif flow_fit is not None:
        # 朝向轴越长、折出来的行/列越少，所以「第一个装得下的就是最小的」——顺序不能反。
        if "for (int probe = SmallestView; probe <= WidestView; probe = NextProbe(probe))" \
                not in flow_fit:
            problems.append("Z17 FlowFitFor 的探针方向不对：必须从小到大找第一个装得下的")
        # 横排按宽度折行（块高决定是否装得下），竖排按高度分列（块宽决定）——两者判据不同轴。
        if "vertical ? measure(WidestView, probe) : measure(probe, WidestView);" not in flow_fit:
            problems.append("Z17 FlowFitFor 量块时没有按流向轴取视图")
        if "block.Height + TextSlack + VerticalMargin <= across" not in flow_fit:
            problems.append("Z17 FlowFitFor 的横排判据不是「块高 + 内缩 <= 可用高度」")
        if "block.Width + TextSlack + HorizontalMargin <= across" not in flow_fit:
            problems.append("Z17 FlowFitFor 的竖排判据不是「块宽 + 内缩 <= 可用宽度」")
        # 全都装不下时返回最长的那一个：那是留在卡片外的文字最少的一个。
        # 它必须是方法体里**最后一条**语句 —— 两个分支里各有一个 `return longest;`（找到
        # 就装得下），只有末尾这个是「全都装不下」的兜底，所以不能按出现次数数。
        if not flow_fit.rstrip().endswith("return longest;"):
            problems.append("Z17 FlowFitFor 没有「全都装不下就返回最长候选」这条兜底："
                            "它必须是方法体的最后一条语句")

    # --- Z13 「适应文字大小」菜单项必须挂上、也必须起得出名字 ------------------
    # 菜单项的标题是在 BuildMenu 里按 key 填的；少一个 case，菜单上就是一条空白。
    if 'CreateMenuItem("SizeFitText")' not in forms:
        problems.append("Z13 找不到「适应文字大小」菜单项（SizeFitText）")
    if 'fitSizeItem.Click += delegate { FitCardToText(); };' not in forms:
        problems.append("Z13 「适应文字大小」菜单项没有挂上 FitCardToText")
    if 'case "SizeFitText":' not in forms:
        problems.append("Z13 「适应文字大小」没有标题：语言切换后菜单上是一条空白")
    if "适应文字大小" not in forms:
        problems.append("Z13 「适应文字大小」没有中文标题")
    if "Fit to text" not in forms:
        problems.append("Z13 「适应文字大小」没有英文标题")

    # --- Z14 最小高度不再被一个固定值撑住 ----------------------------------
    # 这一版修的就是「卡片背景最小高度还是有固定限制」。原因不是下限公式，而是边距：卡片
    # 底部留了 40 像素给一个从不显示的日期标签（_dateLabel），于是下限永远是「文字 + 44」，
    # 一行最小字号正好卡在 56 —— 用户看到的「固定最小高度」就是这个 56。
    # 现在上下各留一个内边距，MinimumFor 用它们的和，那个标签连同它的空带一起删掉。
    #
    # 每一条都独立成立，所以对「改动前 / 上一版交付」的源码跑时，报出来的是真正的毛病
    # （56 还在、日期标签还在、底部空带还在），而不是一句「找不到新常量」。
    min_height = const_int(storage, "MinCardHeight")
    if min_height is None:
        problems.append("Z14 找不到 CardState.MinCardHeight")
    elif min_height >= 56:
        problems.append("Z14 MinCardHeight=%d 没有低于旧的固定最小高度 56" % min_height)
    if "_dateLabel" in forms:
        problems.append("Z14 那个从不显示的日期标签又回来了：它会再一次把卡片撑高")
    if vertical is not None and vertical >= 40:
        problems.append("Z14 VerticalMargin=%d 没有低于旧的 40：底部那条空带还在" % vertical)
    if pad_top is not None and pad_bottom is not None and (pad_top >= 20 or pad_bottom >= 20):
        problems.append("Z14 上下内边距回到了旧版的 20（现在是 %d/%d）：底部空带又回来了"
                        % (pad_top, pad_bottom))
    if min_height is not None and vertical is not None and line is not None and slack is not None:
        # 一行最小字号真正需要的高度：字高 + 内缩 + 上下内边距。
        one_line = line + slack + vertical
        if min_height >= one_line:
            problems.append("Z14 MinCardHeight=%d 压在「一行最小字号」的 %d 之上："
                            "下限会被这个固定值顶住，卡片又缩不下去了" % (min_height, one_line))
    # 「⋯」按钮是这次让位的那一个，不是卡片。两个方向各有一条阈值。
    if "MenuButtonMinCardHeight" not in limits:
        problems.append("Z14 找不到「⋯」按钮的显示阈值 MenuButtonMinCardHeight")
    if "_menuLabel.Visible = ClientSize.Height >= CardSizeLimits.MenuButtonMinCardHeight &&" \
            not in forms:
        problems.append("Z14 「⋯」按钮在小卡片上没有让位：它会盖在文字上")
    if "Math.Max(0, ClientSize.Height - CardSizeLimits.MenuButtonBottomInset)" not in forms:
        problems.append("Z14 「⋯」按钮的高度没有被夹在卡片内：小卡片上会挂到下面去")
    if "Math.Max(0, ClientSize.Width - CardSizeLimits.MenuButtonRightInset)" not in forms:
        problems.append("Z14 「⋯」按钮的左边没有被夹在卡片内：小卡片上会挂到右边去")
    if "Math.Min(CardSizeLimits.MenuButtonWidth, ClientSize.Width)" not in forms:
        problems.append("Z14 「⋯」按钮的宽度没有被夹在卡片内")
    # 候选必须真的装得下自己：竖排的列由高度决定，探针触底时视图比一个字还矮。
    if min_for is not None:
        if "(vertical ? block.Height : block.Width) + TextSlack > probe" not in min_for:
            problems.append("Z14 MinimumFor 不再检查候选是否装得下自己的文字："
                            "竖排在探针触底时会挑出一个裁字的候选")

    # --- Z15 最小宽度不再被边距撑住 ----------------------------------------
    # 这一版修的是「卡片的最小宽度还是不行」。根因同样不是下限公式，而是左右各 32 像素的内边距：
    # 圆角实际只切掉约 5.3 像素，32 是它的三倍；一行四个字的下限宽度 107 里，64 是这条内边距。
    # 横向内边距现在与纵向相同（只是圆角的让位），退化态宽度也降到「一个字 + 内缩 + 左右内边距」
    # 之下 —— 否则它自己就变成新的固定宽度。
    min_width = const_int(storage, "MinCardWidth")
    glyph = limits.get("SmallestGlyphWidth")
    # 半径不再是 CardSizeLimits 上的一个常量，而是每张卡片自己的设置，上限在 CardState 上。
    # 内边距要盖过的是**上限**，不是默认值 —— 否则把上限调到 40 而内边距只盖住 20，谁也发现不了。
    corner = const_int(storage, "MaxCornerRadius")
    if min_width is None:
        problems.append("Z15 找不到 CardState.MinCardWidth")
    elif glyph is not None and slack is not None and horizontal is not None:
        one_glyph = glyph + slack + horizontal
        if min_width >= one_glyph:
            problems.append("Z15 MinCardWidth=%d 压在「一个字最小字号」的 %d 之上："
                            "下限会被这个固定值顶住，卡片又窄不下去了" % (min_width, one_glyph))
    if horizontal is not None and vertical is not None and horizontal > vertical:
        problems.append("Z15 横向边距 %d 大于纵向边距 %d：左右内边距又在撑宽度了"
                        % (horizontal, vertical))
    if pad_left is not None and pad_right is not None and (pad_left >= 32 or pad_right >= 32):
        problems.append("Z15 左右内边距回到了旧版的 32（现在是 %d/%d）："
                        "「最小宽度改不下去」就是这么来的" % (pad_left, pad_right))
    if corner is None:
        problems.append("Z15 找不到 CardState.MaxCornerRadius")
    elif corner <= 0:
        # 把 CornerRadius 调成 0 不会让上面任何一条报错：直角卡片「让开圆角」这句话变成
        # 同义反复，左右内边距的理由也就无从核对了。半径必须是正数。
        problems.append("Z15 圆角半径上限 %g 不再是正数：卡片变成直角，"
                        "左右内边距「让开圆角」的理由无从核对" % corner)
    elif pad_left is not None:
        # 圆角切进最深处的距离是 R*(1-1/sqrt(2))，不是 R 本身。内边距要盖过的是这个数，
        # 拿 R/2 当阈值既松又和几何没关系（R=18 时真值是 5.27，R/2 是 9）。
        cut = corner * (1.0 - 1.0 / math.sqrt(2.0))
        if pad_left < cut:
            problems.append("Z15 左侧内边距 %d 小于圆角实际切进的 %g 像素：文字会压到圆角上"
                            % (pad_left, cut))
    # 画出来的边框和裁出来的窗口区域必须来自同一个函数。这两处一旦各算各的，表现是
    # 每个角上露出一条背景色的月牙 —— 不报错、不崩溃，只是难看，而且极难联想到「半径算了两遍」。
    if "int radius = CardSizeLimits.CornerRadiusFor(_card.CornerRadius, Width, Height);" \
            not in forms:
        problems.append("Z15 绘制或窗口区域没有走 CornerRadiusFor：两处半径会各算各的")
    if "RoundedRectangle(rect, radius)" not in forms:
        problems.append("Z15 OnPaint 没有用算出来的半径画边框")
    if "radius * 2, radius * 2)" not in forms:
        problems.append("Z15 窗口区域没有按「半径的两倍」当椭圆尺寸："
                        "CreateRoundRectRgn 收的是直径，不是半径")
    if "NativeMethods.CreateRectRgn(0, 0, Width + 1, Height + 1)" not in forms:
        problems.append("Z15 半径为 0 时没有退回矩形区域：直角卡片会被裁掉四个角")
    # 文字区至少得装得下一个字。横排会折行，块宽被夹到视图宽，所以 Z14 那条守卫对它恒成立 ——
    # 一个比一个字还窄的视图照样报出「刚好装下」，而「面积最小」偏偏最喜欢它。
    if min_for is not None:
        if "probe - TextSlack < (vertical ? SmallestLineHeight : SmallestGlyphWidth)" \
                not in min_for:
            problems.append("Z15 MinimumFor 不再检查文字区是否装得下一个字："
                            "横排会挑出一个比一个字还窄的候选，卡片裁掉每个字")
    if "MenuButtonMinCardWidth" not in limits:
        problems.append("Z15 找不到「⋯」按钮的宽度阈值 MenuButtonMinCardWidth")
    if "ClientSize.Width >= CardSizeLimits.MenuButtonMinCardWidth" not in forms:
        problems.append("Z15 「⋯」按钮在窄卡片上没有让位：它会盖住整行文字")

    return problems


# ---------------------------------------------------------------- 反向验证

def _inject_forms(old, new):
    return (FORMS, old, new)


INJECTIONS = [
    ("LayoutCard 宽度减掉 60（与常量脱节）",
     FORMS, "ClientSize.Width - CardSizeLimits.HorizontalMargin", "ClientSize.Width - 60", "Z1"),
    ("LayoutCard 左边距改成字面量 30",
     FORMS, "_quoteView.SetBounds(CardSizeLimits.TextPadLeft, CardSizeLimits.TextPadTop,",
     "_quoteView.SetBounds(30, CardSizeLimits.TextPadTop,", "Z1"),
    ("LayoutCard 高度减掉 36（与常量脱节）",
     FORMS, "ClientSize.Height - CardSizeLimits.VerticalMargin", "ClientSize.Height - 36", "Z1"),
    ("VerticalMargin 比两个内边距之和大 16（底部空带回来了）",
     FORMS, "public const int VerticalMargin = TextPadTop + TextPadBottom;",
     "public const int VerticalMargin = TextPadTop + TextPadBottom + 16;", "Z1/Z14"),
    ("底部内边距改回旧版的 20",
     FORMS, "public const int TextPadBottom = 12;", "public const int TextPadBottom = 20;",
     "Z14"),
    ("退化态高度改回旧的 56",
     STORAGE, "public const int MinCardHeight = 32;", "public const int MinCardHeight = 56;",
     "Z14"),
    ("日期标签又回来（并重新占住底部空带）",
     FORMS, "            UpdateMenuButton();",
     "            _dateLabel.SetBounds(25, ClientSize.Height - 40, 80, 24);\n"
     "            UpdateMenuButton();", "Z14"),
    ("「⋯」按钮在小卡片上不再让位（高度）",
     FORMS, "_menuLabel.Visible = ClientSize.Height >= CardSizeLimits.MenuButtonMinCardHeight &&\n"
     "                ClientSize.Width >= CardSizeLimits.MenuButtonMinCardWidth;",
     "_menuLabel.Visible = true;", "Z14/Z15"),
    ("「⋯」按钮不再让位（宽度）",
     FORMS, "ClientSize.Width >= CardSizeLimits.MenuButtonMinCardWidth;",
     "ClientSize.Width >= 0;", "Z15"),
    ("「⋯」按钮不被夹在卡片高度内",
     FORMS, "Math.Max(0, ClientSize.Height - CardSizeLimits.MenuButtonBottomInset)",
     "ClientSize.Height - CardSizeLimits.MenuButtonBottomInset", "Z14"),
    ("「⋯」按钮不被夹在卡片宽度内",
     FORMS, "Math.Max(0, ClientSize.Width - CardSizeLimits.MenuButtonRightInset)",
     "ClientSize.Width - CardSizeLimits.MenuButtonRightInset", "Z14"),
    ("「⋯」按钮宽度不夹在卡片内",
     FORMS, "Math.Min(CardSizeLimits.MenuButtonWidth, ClientSize.Width)",
     "CardSizeLimits.MenuButtonWidth", "Z14"),
    ("MinimumFor 不再检查候选装不装得下自己的文字",
     # 锚点要带上下文：这条守卫在 MinimumFor 与 FlowFitFor 里各有一份。
     FORMS, "// would clip its own text.\n"
            "                if ((vertical ? block.Height : block.Width) + TextSlack > probe) continue;",
     "// would clip its own text.\n                ;", "Z14"),
    ("MinimumFor 不再检查文字区装不装得下一个字",
     FORMS, "if (probe - TextSlack < (vertical ? SmallestLineHeight : SmallestGlyphWidth)) continue;",
     "", "Z15"),
    ("左右内边距改回旧版的 32（最小宽度又被撑住）",
     FORMS, "public const int TextPadLeft = 12;", "public const int TextPadLeft = 32;", "Z15"),
    ("右侧内边距改回旧版的 32",
     FORMS, "public const int TextPadRight = 12;", "public const int TextPadRight = 32;", "Z15"),
    ("退化态宽度改回旧的 80",
     STORAGE, "public const int MinCardWidth = 32;", "public const int MinCardWidth = 80;",
     "Z15"),
    ("「一个字」的宽度守卫调成 0（形同虚设）",
     FORMS, "public const int SmallestGlyphWidth = 10;",
     "public const int SmallestGlyphWidth = 0;", "Z15"),
    ("圆角半径上限被删（内边距的理由无从核对）",
     STORAGE, "public const int MaxCornerRadius = 40;", "public const int MaxCornerRadius = 0;",
     "Z15"),
    ("圆角半径上限大到内边距盖不住",
     STORAGE, "public const int MaxCornerRadius = 40;", "public const int MaxCornerRadius = 200;",
     "Z15"),
    ("边框绘制改回字面量半径",
     FORMS, "RoundedRectangle(rect, radius)", "RoundedRectangle(rect, 18)", "Z15"),
    ("窗口区域改回字面量椭圆尺寸",
     FORMS, "                    radius * 2, radius * 2);",
     "                    19, 19);", "Z15"),
    ("MinimumFor 又收下当前尺寸（下限会跟着卡片跑）",
     FORMS, "public static Size MinimumFor(Measure measure, bool vertical)",
     "public static Size MinimumFor(Measure measure, bool vertical, Size current)", "Z2"),
    ("MinimumFor 探针方向搞反（量到被裁过的块）",
     # 锚点要带上下文：这一行在 MinimumFor 与 FlowFitFor 里各有一份，只写这一行注入会打到
     # FlowFitFor 上，Z2 一条都不报（报的是 Z17）。
     FORMS, "// untrimmed and is the true footprint of the text at this probe.\n"
            "                Size block = vertical ? measure(WidestView, probe) : "
            "measure(probe, WidestView);",
     "// untrimmed and is the true footprint of the text at this probe.\n"
     "                Size block = measure(probe, probe);", "Z2"),
    ("MinimumFor 不跳过空块",
     # 同上：这一行在两处都有，靠缩进区分（MinimumFor 里是 16 空格，FlowFitFor 里是 12）。
     FORMS, "Size block = vertical ? measure(WidestView, probe) : measure(probe, WidestView);\n"
            "                if (block.Width <= 0 || block.Height <= 0) continue;",
     "Size block = vertical ? measure(WidestView, probe) : measure(probe, WidestView);\n"
     "                ;", "Z2"),
    ("MinimumFor 横排宽度算成块宽（跟着指针跑）",
     FORMS, "(vertical ? block.Width + TextSlack : probe) + HorizontalMargin",
     "(vertical ? block.Width + TextSlack : block.Width) + HorizontalMargin", "Z2"),
    ("MinimumFor 竖排高度算成块高",
     FORMS, "(vertical ? probe : block.Height + TextSlack) + VerticalMargin",
     "(vertical ? block.Height + TextSlack : probe) + VerticalMargin", "Z2"),
    ("MinimumFor 面积用 int 乘（大卡片溢出）",
     FORMS, "long area = (long)width * height;", "long area = width * height;", "Z2"),
    ("MinimumFor 改成取面积最大",
     FORMS, "if (area < bestArea)", "if (area > bestArea)", "Z2"),
    ("MinimumFor 把退化态下限加在赢家身上",
     FORMS, "Size best = new Size(CardState.MinCardWidth, CardState.MinCardHeight);",
     "Size best = Size.Empty;", "Z2"),
    ("MinimumFor 丢掉退化态下限",
     FORMS, "Math.Max(CardState.MinCardWidth,\n                    (vertical ? block.Width + TextSlack : probe) + HorizontalMargin);",
     "(vertical ? block.Width + TextSlack : probe) + HorizontalMargin;", "Z2"),
    ("NextProbe 退化成等步长 +1（跑四千次量文字）",
     FORMS, "return Math.Max(probe + 1, probe + probe / 8 + 1);", "return probe + 1;", "Z3"),
    ("NextProbe 会原地打转（死循环）",
     FORMS, "return Math.Max(probe + 1, probe + probe / 8 + 1);", "return probe + probe / 8;", "Z3"),
    ("探针循环不再用 NextProbe 步进",
     # 锚点要带上下文：这个循环头在 MinimumFor 与 FlowFitFor 里各有一份。
     FORMS, "long bestArea = long.MaxValue;\n"
            "            for (int probe = SmallestView; probe <= WidestView; probe = NextProbe(probe))",
     "long bestArea = long.MaxValue;\n"
     "            for (int probe = SmallestView; probe <= WidestView; probe = probe + 64)", "Z3"),
    ("恢复固定的 340x140 下限",
     # 锚点要带上下文：这行在构造函数与 FitCardToText 里各有一份，这里要的是构造函数那一份
     # （Z4/Z11），FitCardToText 那一份另有专门的注入。
     FORMS, "// desktop altogether.\n"
            "            MinimumSize = new Size(CardState.MinCardWidth, CardState.MinCardHeight);\n"
            "            MaximumSize = CardSizeLimits.MaximumFor(",
     "// desktop altogether.\n"
     "            MinimumSize = new Size(340, 140);\n"
     "            MaximumSize = CardSizeLimits.MaximumFor(", "Z4/Z11"),
    ("恢复固定的 700x320 上限",
     FORMS, "MaximumSize = CardSizeLimits.MaximumFor(SystemInformation.VirtualScreen.Size);",
     "MaximumSize = new Size(700, 320);", "Z4/Z11"),
    ("OnResize 不再刷新最小尺寸",
     FORMS, "LayoutCard();\n            FitQuoteFont();\n            UpdateCardSizeLimits();\n"
            "            PositionTransparentHitBox();",
     "LayoutCard();\n            FitQuoteFont();\n            PositionTransparentHitBox();", "Z5"),
    ("FitQuoteFont 不再刷新最小尺寸",
     FORMS, "UpdateCardSizeLimits();\n            PositionTransparentHitBox();\n        }\n\n"
            "        // The card may be dragged to any size",
     "PositionTransparentHitBox();\n        }\n\n        // The card may be dragged to any size", "Z5"),
    ("UpdateCardSizeLimits 无条件赋值",
     FORMS, "if (MinimumSize.Width == floor.Width && MinimumSize.Height == floor.Height) return;\n"
            "            MinimumSize = ClampToCeiling(floor);",
     "MinimumSize = ClampToCeiling(floor);", "Z6"),
    ("自动字号关掉时下限不让路（卡片被锁死）",
     FORMS, "if (!_card.AutoFitText) return;", "if (!_card.AutoFitText) { }", "Z6"),
    ("下限不再夹在桌面上限之内",
     # 锚点要带上下文：这行在 UpdateCardSizeLimits 里有两处（「已经太小」与「常规刷新」）。
     FORMS, "if (MinimumSize.Width == floor.Width && MinimumSize.Height == floor.Height) return;\n"
            "            MinimumSize = ClampToCeiling(floor);",
     "if (MinimumSize.Width == floor.Width && MinimumSize.Height == floor.Height) return;\n"
     "            MinimumSize = floor;", "Z6"),
    ("ClampToCeiling 只夹一个方向",
     FORMS, "Math.Min(floor.Height, MaximumSize.Height)", "floor.Height", "Z6b"),
    ("ApplyHitFrame 改用字面量夹尺寸",
     FORMS, "int width = Math.Max(MinimumSize.Width,\n"
            "                Math.Min(MaximumSize.Width, Width + (frame.Width - _lastHitFrame.Width)));",
     "int width = Math.Max(340, Math.Min(700, Width + (frame.Width - _lastHitFrame.Width)));", "Z7"),
    ("点击层恢复固定上限",
     FORMS, "            MinimumSize = new Size(24, 20);\n        }",
     "            MinimumSize = new Size(24, 20);\n            MaximumSize = new Size(700, 320);\n        }",
     "Z8"),
    ("预设尺寸把请求值写进配置",
     FORMS, "_card.CardWidth = Width;\n                _card.CardHeight = Height;",
     "_card.CardWidth = width;\n                _card.CardHeight = height;", "Z9"),
    ("FitCardToText 只量一次",
     # 锚点要带上下文：这个循环头在 FitCardToText 与 GrowCardToText 里各有一份，要的是前者。
     FORMS, "// keeps a pathological measurement from looping.\n"
            "            for (int pass = 0; pass < 4; pass++)",
     "// keeps a pathological measurement from looping.\n"
     "            for (int pass = 0; pass < 1; pass++)", "Z9"),
    ("FitCardToText 丢掉 SmallestCard",
     FORMS, "Size wanted = SmallestCard();", "Size wanted = MinimumSize;", "Z9"),
    ("FitCardToText 不清空 MinimumSize（缩不到贴合尺寸）",
     # 锚点必须带上下文：这行在构造函数里也有一份，只写这一行的话注入会打到构造函数上，
     # Z9 那条断言一条都不报（实际报出的是 Z11）—— 也就是说这条注入从来没验到过它
     # 声称要验的东西。
     FORMS, "MinimumSize = new Size(CardState.MinCardWidth, CardState.MinCardHeight);\n"
            "            // With auto-fit on the floor does not move, so the first pass lands on it.",
     "if (MinimumSize.Width > 0) { }\n"
     "            // With auto-fit on the floor does not move, so the first pass lands on it.", "Z9"),
    ("WithFloorMeasure 改用卡片当前字号当参照",
     FORMS, "using (Font reference = QuoteFont(QuoteView.SmallestFontSize))",
     "using (Font reference = QuoteFont(Font.SizeInPoints))", "Z12"),
    ("WithFloorMeasure 不把参照字号传给测量函数",
     FORMS, "return view.MeasureBlock(bound, width, height);",
     "return view.MeasureBlock(width, height);", "Z12"),
    ("WithFloorMeasure 不区分自动字号开关",
     FORMS, "if (!_card.AutoFitText)\n                return pick(_quoteView.MeasureBlock);",
     "if (false)\n                return pick(_quoteView.MeasureBlock);", "Z12"),
    ("DragLimit 不收当前流向长度（下限算不出折行结果）",
     FORMS, "int flowAxis = vertical ? Height : Width;",
     "int flowAxis = vertical ? 200 : 400;", "Z12"),
    # Z16：把「面积最小」那个点当拖动下限用回来 —— 这一版修的正是这件事。
    ("横排分支把宽度算在块宽上（退回拿点当下限）",
     FORMS, "return new Size(unit.Width,\n"
            "                Math.Max(unit.Height, block.Height + TextSlack + VerticalMargin));",
     "return new Size(Math.Max(unit.Width, block.Width + TextSlack + HorizontalMargin),\n"
     "                Math.Max(unit.Height, block.Height + TextSlack + VerticalMargin));", "Z16"),
    ("竖排分支把高度算在块高上（退回拿点当下限）",
     FORMS, "return new Size(Math.Max(unit.Width, block.Width + TextSlack + HorizontalMargin),\n"
            "                    unit.Height);",
     "return new Size(Math.Max(unit.Width, block.Width + TextSlack + HorizontalMargin),\n"
     "                    Math.Max(unit.Height, block.Height + TextSlack + VerticalMargin));", "Z16"),
    ("量块时给错视图（横排给高度）",
     FORMS, "Size block = vertical ? measure(WidestView, viewFlow) : measure(viewFlow, WidestView);",
     "Size block = vertical ? measure(WidestView, viewFlow) : measure(WidestView, viewFlow);", "Z16"),
    ("viewFlow 减错边距（横排减成纵向边距）",
     FORMS, "flowAxis - (vertical ? VerticalMargin : HorizontalMargin)",
     "flowAxis - (vertical ? HorizontalMargin : VerticalMargin)", "Z16"),
    ("SmallestTextCard 的宽度写成字面量",
     FORMS, "SmallestGlyphWidth + TextSlack + HorizontalMargin)",
     "38 + TextSlack + HorizontalMargin)", "Z16"),
    ("SmallestTextCard 不兜退化态下限",
     FORMS, "Math.Max(CardState.MinCardWidth,\n"
            "                        SmallestGlyphWidth + TextSlack + HorizontalMargin),",
     "(CardState.MinCardWidth + SmallestGlyphWidth + TextSlack + HorizontalMargin),", "Z16"),
    ("UpdateCardSizeLimits 改用 SmallestCard（下限又变成一个点）",
     FORMS, "Size floor = DragLimit();", "Size floor = SmallestCard();", "Z16"),
    ("GrowCardToText 不用 DragLimit",
     FORMS, "Size wanted = DragLimit();", "Size wanted = SmallestCard();", "Z16"),
    ("GrowCardToText 撞到上限时退回面积最小的卡片（卡片会停住）",
     FORMS, "return CardSizeLimits.FlowFitFor(measure, vertical, across);",
     "return CardSizeLimits.MinimumFor(measure, vertical);", "Z16"),
    ("GrowCardToText 撞到上限时不把字号退回装得下的一档",
     # 锚点要带上下文：三个字号相关的 setter 都以 `BackOffToFittingSize();` 结尾。
     FORMS, "                Math.Min(CardState.MaxManualFontSize, size));\n"
            "            _quoteFontSize = -1.0f;\n"
            "            _app.Save();\n"
            "            FitQuoteFont();\n"
            "            GrowCardToText();\n"
            "            BackOffToFittingSize();",
     "                Math.Min(CardState.MaxManualFontSize, size));\n"
     "            _quoteFontSize = -1.0f;\n"
     "            _app.Save();\n"
     "            FitQuoteFont();\n"
     "            GrowCardToText();", "Z16"),
    # Z17：FlowFitFor 是「横跨那根轴顶到桌面」时唯一的出路。
    ("FlowFitFor 探针方向搞反（从大到小找）",
     FORMS, "for (int probe = SmallestView; probe <= WidestView; probe = NextProbe(probe))\n"
            "        {\n            Size block = vertical ? measure(WidestView, probe) : "
            "measure(probe, WidestView);",
     "for (int probe = WidestView; probe >= SmallestView; probe = NextProbe(probe))\n"
     "        {\n            Size block = vertical ? measure(WidestView, probe) : "
     "measure(probe, WidestView);", "Z17"),
    ("FlowFitFor 横排判据用错轴（拿块宽去比可用高度）",
     FORMS, "if (block.Height + TextSlack + VerticalMargin <= across) return longest;",
     "if (block.Width + TextSlack + HorizontalMargin <= across) return longest;", "Z17"),
    ("FlowFitFor 丢掉「全都装不下就返回最长」的兜底",
     FORMS, "        return longest;\n    }\n\n    // Geometric steps. The probes span three orders",
     "        return unit;\n    }\n\n    // Geometric steps. The probes span three orders", "Z17"),    ("存储层恢复固定钳制",
     STORAGE, "CardWidth = Math.Max(MinCardWidth, Math.Min(MaxCardWidth, CardWidth));",
     "CardWidth = Math.Max(340, Math.Min(700, CardWidth));", "Z4/Z10"),
    ("适应文字大小菜单项没挂上",
     FORMS, "fitSizeItem.Click += delegate { FitCardToText(); };", "", "Z13"),
    ("适应文字大小菜单项没有标题",
     FORMS, 'case "SizeFitText": menuItem.Text = T("适应文字大小（最小）", "Fit to text (smallest)"); break;',
     "", "Z13"),
]


def run_reverse():
    raw = {FORMS: open(FORMS, encoding="utf-8").read(),
           STORAGE: open(STORAGE, encoding="utf-8").read()}
    ok = 0
    bad = []
    for name, path, old, new, expect in INJECTIONS:
        if old not in raw[path]:
            bad.append("%s：注入点没找到（脚本与源码脱节）" % name)
            continue
        # 锚点必须唯一。`str.replace(old, new, 1)` 只替换**第一处**，锚点在两处都出现时注入
        # 会打到另一处，目标代码一点没变 —— 于是报出的是**别的**那组断言，看着像「报出了」，
        # 实际要验的那条从来没被验过。踩过一次（FitCardToText 里清 MinimumSize 那行在
        # 构造函数里也有一份，注入打到构造函数，Z9 一条都没报，报的是 Z11）。
        hits = raw[path].count(old)
        if hits != 1:
            bad.append("%s：锚点在源码里出现 %d 次，注入会打到别处（补上上下文让它唯一）"
                       % (name, hits))
            continue
        mutated = raw[path].replace(old, new, 1)
        if path == FORMS:
            problems = check_sizes(strip_comments(mutated), strip_comments(raw[STORAGE]))
        else:
            problems = check_sizes(strip_comments(raw[FORMS]), strip_comments(mutated))
        if problems:
            ok += 1
            print("  [OK ] %-40s → 报出 %d 条（预期 %s）" % (name, len(problems), expect))
        else:
            bad.append("%s：注入后一条都没报出来" % name)
    for b in bad:
        print("  [!! ] %s" % b)
    return ok, len(INJECTIONS), bad


def main():
    forms = strip_comments(open(FORMS, encoding="utf-8").read())
    storage = strip_comments(open(STORAGE, encoding="utf-8").read())

    problems = check_sizes(forms, storage)

    if problems:
        print("== 卡片尺寸静态断言：失败 ==")
        for p in problems:
            print("  [!!] " + p)
    else:
        print("== 卡片尺寸静态断言：全部通过（14 组） ==")

    if "--reverse" in sys.argv:
        print()
        print("== 反向验证：注入缺陷，确认脚本报得出来 ==")
        ok, total, bad = run_reverse()
        print("注入 %d 处，报出 %d 处" % (total, ok))
        if bad:
            for b in bad:
                print("  [!!] " + b)
            return 1
        if ok != total:
            return 1

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
