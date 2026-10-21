#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""拖动缩放的收敛性模拟（带状态）。

这是**模型**，不是真机：macOS 上量不了 GDI 的文字块，所以这里用一组写明的文字度量模型
代替 TextRenderer，其余每一步都照着源码的算术走，连状态机的形状都一样：

    QuoteCardForm 构造      MinimumSize = (MinCardWidth, MinCardHeight)   ← 退化态下限
    ApplyHitFrame           新尺寸 = clamp(MinimumSize, 当前尺寸 + 位移, MaximumSize)
    OnResize → FitQuoteFont 按新尺寸重挑字号（AutoFit 时往下找第一个装得下的，下限 7pt）
    UpdateCardSizeLimits    下限 = CardSizeLimits.DragLimitFor(量文字, 是否竖排, 当前流向长度)
                              下限 <= 当前尺寸 → MinimumSize = 夹在桌面上限内的下限
                              下限 >  当前尺寸 → AutoFit 开：撑大卡片并重入（深度上限 3）
                                                AutoFit 关：让位，MinimumSize 保持原值
    FitCardToText           MinimumSize = CardSizeLimits.MinimumFor(量文字, 是否竖排)
    LayoutCard              视图 = (max(10, 宽-MARGIN_X), max(10, 高-MARGIN_Y))

CardSizeLimits 里有两个答案，它们形状不一样，这是这一版改动的全部要点：

  MinimumFor   「适应文字大小（最小）」用的那个点。探针从 SmallestView 按几何级数走到
               WidestView（横排探宽度、竖排探高度，另一轴留 4096 才量得到不裁字的真实
               足迹），取「面积最小」的那张卡片。它是一个**点**。
  DragLimitFor 拖动时的下限。只约束「横跨文字流向」的那根轴 —— 横排约束高度、竖排约束
               宽度 —— 并且**读另一根轴**（也就是文字流过的长度）来算它。它是一条**曲线**
               上按当前流向长度取的那一点。

把 MinimumFor 那个点当成拖动下限用，就是把曲线剪成两截：横排卡片永远拖不到一行，竖排卡片
永远收不到一列。这正是「横排最小高度 / 竖排最小宽度调不下去」那条抱怨。

关键不变量（这轮改写的目标）：
  A. 拖动下限只读「文字流过的轴」，**绝不读它约束的那根轴**。横排拖高时高度下限纹丝不动
     （否则卡片就顺着一条不断下移的下限一直缩）；横排拖宽时高度下限会抬升，那是文字换行
     必需的。抬升不会反过来再动宽度下限 —— 两根轴之间没有环，卡片缩不到缝里去。
  B. 下限处文字必须装得下 —— 宽度和高度都要。
  C. 「适应文字大小（最小）」挑的仍是「面积最小」那个点，不是窄条也不是扁条。
  D. 只拖被约束的那根轴，卡片沿该轴只许变小或停住，绝不许被顶回来。
  H. 横排卡片拖宽，高度下限必须降下来 —— 一路降到一行；竖排卡片拖高，宽度下限必须降下来
     —— 一路降到一列。这是「最小高度/最小宽度调不下去」那条抱怨的反面。
  I. 另一根轴的下限是**常数**（一字卡片），不随卡片尺寸变。它随流向轴变的部分只准单调
     上升，因为文字只会越折越多。

要回答的问题：
  1. 横排卡片拖宽之后，高度能不能一路拖到一行？竖排拖高之后宽度能不能拖到一列？
  2. 只拖被约束的那根轴，卡片会停在下限上还是缩成一条看不见字的缝？
  3. 停下时文字是否还完整（AutoFit 开时）？
  4. 反向往大拖，会不会被最小值棘轮卡住？
  5. 拖的过程中，被约束的那根轴会不会「弹回来」（下限比当前尺寸还大）？

度量模型（1pt = 1.333px，中文方块字）：
    字宽 = 1.33 × 字号     行高 = 1.50 × 字号
真机上这两个系数不同，但结论不依赖它们的具体取值——依赖的是「字宽随字号单调增」和
「视图越窄、折行越多、块越高」，这两条在真机上同样成立。

用法：
    python3 mqc_shrink_sim.py             # 跑模拟 + 全部断言
    python3 mqc_shrink_sim.py --old       # 用被换掉的那版下限公式（跟着当前尺寸走）
    python3 mqc_shrink_sim.py --src <目录>  # 从源码里读常量（推荐，见下）
"""
import math
import os
import re
import sys

# 常量默认从真实源码里读。写死的副本会悄悄过期：这一版把纵向边距从 40 改成 24、退化态高度
# 从 56 改成 32，如果模拟器还用老数字，它验的就不是交付的那份算术了。读不到源码时才退回
# 下面这组字面量（交付目录里三个 .cs 摆在脚本旁边时也能跑）。
_SRC = None
for _i, _a in enumerate(sys.argv):
    if _a == "--src" and _i + 1 < len(sys.argv):
        _SRC = sys.argv[_i + 1]
if _SRC is None:
    _HERE = os.path.dirname(os.path.abspath(__file__))
    _SRC = _HERE if os.path.isfile(os.path.join(_HERE, "Forms.cs")) else None


def _strip_comments(text):
    """剥掉 // 与 /* */ 注释，但保留字符串字面量（URL 里的 // 不能被当注释）。"""
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            out.append(c)
            i += 1
            while i < n:
                if text[i] == "\\":
                    out.append(text[i:i + 2])
                    i += 2
                    continue
                out.append(text[i])
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i = text.index("*/", i) + 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def _const(source, name, default):
    """从剥掉注释的源码里读 `const int name = 表达式;`，表达式允许全是常量的加法。"""
    if _SRC is None:
        return default
    path = os.path.join(_SRC, source)
    if not os.path.isfile(path):
        return default
    with open(path, encoding="utf-8") as handle:
        text = _strip_comments(handle.read())
    match = re.search(r"const\s+int\s+%s\s*=\s*([^;]+);" % re.escape(name), text)
    if not match:
        return default
    body = match.group(1)
    if not re.match(r"^[\s\w+]+$", body):
        return default
    try:
        return int(eval(body, {"__builtins__": {}}, _READ_CONSTS))
    except Exception:
        return default


_READ_CONSTS = {}


def _load(name, source, default):
    """先读常量本身，再把它登记进名字表，好让 `A + B` 这种写法也能算出来。"""
    value = _const(source, name, default)
    _READ_CONSTS[name] = value
    return value


TEXT_PAD_LEFT = _load("TextPadLeft", "Forms.cs", 12)
TEXT_PAD_RIGHT = _load("TextPadRight", "Forms.cs", 12)
TEXT_PAD_TOP = _load("TextPadTop", "Forms.cs", 12)
TEXT_PAD_BOTTOM = _load("TextPadBottom", "Forms.cs", 12)
MARGIN_X = _load("HorizontalMargin", "Forms.cs", TEXT_PAD_LEFT + TEXT_PAD_RIGHT)
MARGIN_Y = _load("VerticalMargin", "Forms.cs", TEXT_PAD_TOP + TEXT_PAD_BOTTOM)
TEXT_SLACK = _load("TextSlack", "Forms.cs", 4)
SMALLEST_LINE_HEIGHT = _load("SmallestLineHeight", "Forms.cs", 12)
SMALLEST_GLYPH_WIDTH = _load("SmallestGlyphWidth", "Forms.cs", 10)
CORNER_RADIUS = _load("CornerRadius", "Forms.cs", 18)
FLOOR_X = _load("MinCardWidth", "Storage.cs", 32)
FLOOR_Y = _load("MinCardHeight", "Storage.cs", 32)
TEXT_MARGIN = 2        # QuoteView.TextMargin
SMALLEST_VIEW = 10     # CardSizeLimits.SmallestView
WIDEST_VIEW = 4096     # CardSizeLimits.WidestView
MIN_FONT = 7.0         # QuoteView.SmallestFontSize
CHAR_ADVANCE = 1.33    # px / pt
LINE_HEIGHT = 1.50     # px / pt
DEPTH_CAP = 3          # 重入上限
START = (460, 190)     # 默认卡片尺寸
CEILING = (3840, 2160)  # CardSizeLimits.MaximumFor(桌面)

OLD_FORMULA = "--old" in sys.argv

if OLD_FORMULA:
    # --old 要复现的是「上一版的行为」，所以连常量一起换回去：上一版左右各 32、上下各 20，
    # 退化态是 80 x 56 —— 也就是这次去掉的那两个固定最小尺寸。
    TEXT_PAD_LEFT = TEXT_PAD_RIGHT = 32
    TEXT_PAD_TOP = TEXT_PAD_BOTTOM = 20
    MARGIN_X = TEXT_PAD_LEFT + TEXT_PAD_RIGHT
    MARGIN_Y = TEXT_PAD_TOP + TEXT_PAD_BOTTOM
    FLOOR_X = 80
    FLOOR_Y = 56
    # 上一版没有「文字区至少装得下一个字」这条守卫：它是 80 像素的退化态宽度盖住的。
    SMALLEST_GLYPH_WIDTH = 0
    SMALLEST_LINE_HEIGHT = 0

if _SRC is not None and os.path.isfile(os.path.join(_SRC, "Forms.cs")) and not OLD_FORMULA:
    print("常量取自源码 %s：MARGIN_X=%d MARGIN_Y=%d SLACK=%d FLOOR=(%d,%d)"
          % (_SRC, MARGIN_X, MARGIN_Y, TEXT_SLACK, FLOOR_X, FLOOR_Y))
elif OLD_FORMULA:
    print("--old：用上一版的常量与下限公式：MARGIN_X=%d MARGIN_Y=%d SLACK=%d FLOOR=(%d,%d)"
          % (MARGIN_X, MARGIN_Y, TEXT_SLACK, FLOOR_X, FLOOR_Y))
else:
    print("常量取字面量副本（没找到源码，传 --src <目录> 可以读真实常量）："
          "MARGIN_X=%d MARGIN_Y=%d SLACK=%d FLOOR=(%d,%d)"
          % (MARGIN_X, MARGIN_Y, TEXT_SLACK, FLOOR_X, FLOOR_Y))

# 边距必须真的是两个内边距之和：改了一个忘了另一个，模拟器验的就不是交付的算术。
if not OLD_FORMULA and (MARGIN_X != TEXT_PAD_LEFT + TEXT_PAD_RIGHT or
                        MARGIN_Y != TEXT_PAD_TOP + TEXT_PAD_BOTTOM):
    raise SystemExit("边距常量与内边距常量对不上：HorizontalMargin=%d vs %d+%d，"
                     "VerticalMargin=%d vs %d+%d"
                     % (MARGIN_X, TEXT_PAD_LEFT, TEXT_PAD_RIGHT,
                        MARGIN_Y, TEXT_PAD_TOP, TEXT_PAD_BOTTOM))


def view_of(card_w, card_h):
    return max(SMALLEST_VIEW, card_w - MARGIN_X), max(SMALLEST_VIEW, card_h - MARGIN_Y)


def next_probe(probe):
    """CardSizeLimits.NextProbe。"""
    return max(probe + 1, probe + probe // 8 + 1)


def block(text_length, font, view_w, view_h, vertical=False):
    """QuoteView.MeasureBlock（模型）。横排按宽度折行，竖排按高度分列。"""
    advance = CHAR_ADVANCE * font
    if not vertical:
        available = max(1, view_w - TEXT_MARGIN * 2)
        per_line = max(1, int(available // advance)) if advance > 0 else text_length
        lines = max(1, math.ceil(text_length / per_line))
        return min(text_length * advance, available), lines * LINE_HEIGHT * font
    cell = LINE_HEIGHT * font
    per_column = max(1, int(max(cell, view_h - TEXT_MARGIN * 2) // cell))
    columns = math.ceil(text_length / per_column)
    max_columns = max(1, int(max(1, view_w - TEXT_MARGIN * 2) // max(1, advance)))
    if columns > max_columns:
        columns = max_columns
    return columns * advance, min(text_length, per_column) * cell


def fits(text_length, font, view_w, view_h, vertical=False):
    w, h = block(text_length, font, view_w, view_h, vertical)
    return (w <= max(1, view_w - TEXT_MARGIN * 2) and
            h <= max(1, view_h - TEXT_MARGIN * 2))


def choose_font(text_length, card_w, card_h, adjust, auto_fit, vertical=False):
    """FitQuoteFont（模型），含「显式再试一次下限」那一步。"""
    view_w, view_h = view_of(card_w, card_h)
    start = min(29.0, max(20.0, card_w / 17.0)) + adjust
    chosen = max(MIN_FONT, start)
    if auto_fit:
        found = False
        size = start
        while size > MIN_FONT and not found:
            if fits(text_length, size, view_w, view_h, vertical):
                chosen = size
                found = True
            size -= 1.0
        if not found and fits(text_length, MIN_FONT, view_w, view_h, vertical):
            chosen = MIN_FONT
    return chosen


def _candidate_fits(probe, bw, bh, vertical):
    """候选是否真的装得下：探针那一轴上，块加内缩不许超过探针本身。

    竖排是踩点：列数由高度决定，探针触底时视图比一个字还矮，量出来的块仍是一个字高，
    按它算出来的卡片（高度 = 探针 + 边距）装不下自己的文字。老版靠 56 的退化态下限
    把这个洞盖住了，下限一降就露出来，所以候选要在算面积之前被剔掉。
    """
    return (bh if vertical else bw) + TEXT_SLACK <= probe


def _candidate_holds_a_glyph(probe, vertical):
    """文字区至少装得下一个字 —— 上面那条守卫看不出这件事。

    横排会折行，量出来的块宽被夹到视图宽，所以「块装得下视图」对它恒成立：一个比一个字
    还窄的视图照样报出一个刚好装下的块，而「面积最小」偏偏最喜欢它，因为它最窄。
    老版靠 80 像素的退化态宽度把这个洞盖住了。
    """
    return probe - TEXT_SLACK >= (SMALLEST_LINE_HEIGHT if vertical else SMALLEST_GLYPH_WIDTH)


def minimum_for(text_length, font, vertical):
    """CardSizeLimits.MinimumFor（模型）。与卡片当前尺寸无关。"""
    best = (FLOOR_X, FLOOR_Y)
    best_area = None
    probe = SMALLEST_VIEW
    while probe <= WIDEST_VIEW:
        if vertical:
            bw, bh = block(text_length, font, WIDEST_VIEW, probe, True)
            card = (bw + TEXT_SLACK + MARGIN_X, probe + MARGIN_Y)
        else:
            bw, bh = block(text_length, font, probe, WIDEST_VIEW, False)
            card = (probe + MARGIN_X, bh + TEXT_SLACK + MARGIN_Y)
        if bw <= 0 or bh <= 0:
            probe = next_probe(probe)
            continue
        if not _candidate_fits(probe, bw, bh, vertical):
            probe = next_probe(probe)
            continue
        if not _candidate_holds_a_glyph(probe, vertical):
            probe = next_probe(probe)
            continue
        # 退化态下限加在候选身上，而不是加在赢家身上：加在赢家身上会把赢家抬到
        # 一个本来不需要抬的候选之上。
        card = (max(FLOOR_X, card[0]), max(FLOOR_Y, card[1]))
        area = card[0] * card[1]
        if best_area is None or area < best_area:
            best_area = area
            best = card
        probe = next_probe(probe)
    return best


def smallest_text_card():
    """CardSizeLimits.SmallestTextCard：一字一行、什么都还没写的卡片。

    它不依赖文字，也不依赖文字往哪个方向流 —— 它是「自由的那根轴」的下限，也是下面每个
    答案的起点。退化态下限（MinCardWidth/MinCardHeight）兜在它下面。
    """
    return (max(FLOOR_X, SMALLEST_GLYPH_WIDTH + TEXT_SLACK + MARGIN_X),
            max(FLOOR_Y, SMALLEST_LINE_HEIGHT + TEXT_SLACK + MARGIN_Y))


def drag_limit_for(text_length, font, vertical, flow_axis):
    """CardSizeLimits.DragLimitFor（模型）：拖动时的下限，是一点，不是整条曲线。

    flow_axis 是卡片**沿文字流向**的长度 —— 横排是宽度、竖排是高度。下限只约束另一根轴：

        横排（文字从左到右流）→ 约束高度。宽度决定折成几行，行数决定高度。
        竖排（文字从上往下流）→ 约束宽度。高度决定分成几列，列数决定宽度。

    自由的那根轴取下限 `smallest_text_card`：横排的宽度只要求放得下一个字。

    依赖 flow_axis 是**故意**的，也不是老版掉进去的那个坑：它读的是另一根轴，绝不是它约束
    的那根。横排拖高时宽度没变 → 高度下限没变；横排拖宽时高度下限会降，但高度下限从不看
    高度，所以两根轴之间没有环。
    """
    unit = smallest_text_card()
    view_flow = max(SMALLEST_VIEW, flow_axis - (MARGIN_Y if vertical else MARGIN_X))
    if vertical:
        bw, bh = block(text_length, font, WIDEST_VIEW, view_flow, True)
        if bw <= 0 or bh <= 0:
            return unit
        return (max(unit[0], bw + TEXT_SLACK + MARGIN_X), unit[1])
    bw, bh = block(text_length, font, view_flow, WIDEST_VIEW, False)
    if bw <= 0 or bh <= 0:
        return unit
    return (unit[0], max(unit[1], bh + TEXT_SLACK + MARGIN_Y))


def floor_fine(text_length, font, vertical, step=1):
    """同样的目标函数，但用 1px 细网格扫一遍，用来核对几何探针离最优有多远。"""
    best = None
    best_area = None
    for probe in range(SMALLEST_VIEW, WIDEST_VIEW + 1, step):
        if vertical:
            bw, bh = block(text_length, font, WIDEST_VIEW, probe, True)
            card = (bw + TEXT_SLACK + MARGIN_X, probe + MARGIN_Y)
        else:
            bw, bh = block(text_length, font, probe, WIDEST_VIEW, False)
            card = (probe + MARGIN_X, bh + TEXT_SLACK + MARGIN_Y)
        if bw <= 0 or bh <= 0:
            continue
        if not _candidate_fits(probe, bw, bh, vertical):
            continue
        if not _candidate_holds_a_glyph(probe, vertical):
            continue
        card = (max(FLOOR_X, card[0]), max(FLOOR_Y, card[1]))
        area = card[0] * card[1]
        if best_area is None or area < best_area:
            best_area = area
            best = card
    return best


def narrowest_fitting(text_length, font, view_h, current_view_w):
    """被换掉那版里的 CardSizeLimits.NarrowestFitting。"""
    room = max(1, view_h - TEXT_SLACK)
    high = max(SMALLEST_VIEW, current_view_w)
    if block(text_length, font, high, view_h)[1] > room:
        return high
    low = SMALLEST_VIEW
    while low < high:
        middle = low + (high - low) // 2
        if block(text_length, font, middle, view_h)[1] <= room:
            high = middle
        else:
            low = middle + 1
    return low


def old_minimum_for(text_length, font, w, h, vertical):
    """被换掉的那版下限：块高量在「当前宽度」上，宽度二分找「还装得下当前高度」的最窄视图。

    两个毛病都在这里：下限读进了卡片当前的尺寸（A 不成立），而且宽度和高度各说各话
    （B 不成立）—— 在它自己算出来的宽度上，文字其实装不下。
    """
    view_w, view_h = view_of(w, h)
    fw = narrowest_fitting(text_length, font, view_h, view_w)
    bh = block(text_length, font, fw, view_h)[1]
    return (max(FLOOR_X, fw + MARGIN_X), max(FLOOR_Y, bh + MARGIN_Y + TEXT_SLACK))


class Card(object):
    """一张卡片的尺寸状态机，形状与 QuoteCardForm 一致。"""

    def __init__(self, text_length, size=START, adjust=0, auto_fit=True, vertical=False):
        self.n = text_length
        self.adjust = adjust
        self.auto = auto_fit
        self.vertical = vertical
        self.w, self.h = size
        self.minimum = (FLOOR_X, FLOOR_Y)      # 构造函数里的退化态下限

    # ---- QuoteCardForm.DragLimit / SmallestCard / CardSizeLimits --------
    def reference_font(self):
        if self.auto:
            return MIN_FONT
        return choose_font(self.n, self.w, self.h, self.adjust, False, self.vertical)

    def flow_axis(self):
        """卡片沿文字流向的长度：横排是宽度，竖排是高度。"""
        return self.h if self.vertical else self.w

    def floor(self):
        """拖动下限 —— UpdateCardSizeLimits 用的那个（CardSizeLimits.DragLimitFor）。"""
        if OLD_FORMULA:
            # 上一版把「面积最小」那个点直接当拖动下限用，曲线被剪成两截的根源。
            return old_minimum_for(self.n, self.reference_font(), self.w, self.h, self.vertical)
        return drag_limit_for(self.n, self.reference_font(), self.vertical, self.flow_axis())

    def smallest_card(self):
        """面积最小的那张卡片 —— 「适应文字大小（最小）」用的那个点。"""
        return minimum_for(self.n, self.reference_font(), self.vertical)

    def _ceiling(self, size):
        return min(size[0], CEILING[0]), min(size[1], CEILING[1])

    # ---- UpdateCardSizeLimits ------------------------------------------
    def refresh_limits(self):
        for _ in range(DEPTH_CAP + 1):
            f = self.floor()
            if f[0] > self.w or f[1] > self.h:
                if not self.auto:
                    return                       # 让位：MinimumSize 保持原值
                self.minimum = self._ceiling(f)
                # MinimumSize 的 setter 会把卡片撑到下限，然后重新触发一次刷新
                self.w, self.h = max(self.w, f[0]), max(self.h, f[1])
                continue
            self.minimum = self._ceiling(f)
            return

    # ---- ApplyHitFrame + OnResize --------------------------------------
    def drag(self, dx, dy):
        self.w = max(self.minimum[0], min(CEILING[0], self.w + dx))
        self.h = max(self.minimum[1], min(CEILING[1], self.h + dy))
        self.refresh_limits()

    # ---- FitCardToText --------------------------------------------------
    def fit_to_text(self):
        # 先把 MinimumSize 清回退化态下限。拖动下限可能比「面积最小」那张卡片还高（一张拖窄
        # 了的卡片，下限很高），而 Size 的 setter 会夹到 MinimumSize 上 —— 不清掉的话这次
        # 「适应文字大小」根本缩不下去，源码里补的就是这一步。
        self.minimum = (FLOOR_X, FLOOR_Y)
        for _ in range(4):
            wanted = self.smallest_card()
            if wanted == (self.w, self.h):
                break
            self.w = max(self.minimum[0], min(CEILING[0], wanted[0]))
            self.h = max(self.minimum[1], min(CEILING[1], wanted[1]))
        self.refresh_limits()

    # ---- 观测 -----------------------------------------------------------
    def font(self):
        return choose_font(self.n, self.w, self.h, self.adjust, self.auto, self.vertical)

    def text_whole(self):
        view_w, view_h = view_of(self.w, self.h)
        return fits(self.n, self.font(), view_w, view_h, self.vertical)

    def describe(self):
        view_w, view_h = view_of(self.w, self.h)
        bw, bh = block(self.n, self.font(), view_w, view_h, self.vertical)
        return "停在 %4dx%-4d 字号 %4.1f  视图 %4dx%-4d  文字块 %6.1fx%-6.1f" % (
            self.w, self.h, self.font(), view_w, view_h, bw, bh)

    def shrink_step(self):
        """往小拖的那一步：只拖**被约束**的那根轴 —— 横排拖高、竖排拖宽。

        对角拖动是另一回事：往窄里拖会让文字折行、把高度顶上去。那是文字真需要更多地方，
        不是回弹，所以它单独归一组断言，不混进这条主线 —— 这条主线要回答的正是用户抱怨的
        那件事：只拖被约束的那根轴，能不能一路拖到底。
        """
        return (-2, 0) if self.vertical else (0, -2)

    def settle(self, step=None, limit=3000):
        if step is None:
            step = self.shrink_step()
        for _ in range(limit):
            before = (self.w, self.h)
            self.drag(step[0], step[1])
            if (self.w, self.h) == before:
                return True
        return False


def run(label, text_length, **kw):
    card = Card(text_length, **kw)
    settled = card.settle()
    print("  %-26s %s  文字完整=%-2s 收敛=%s" % (
        label, card.describe(), "是" if card.text_whole() else "否",
        "是" if settled else "否"))
    return card, settled


# ---------------------------------------------------------------- 审计

def audit_size_independent():
    """A1：`MinimumFor`（面积最小的那个点）只由文字和字体决定，换个起始尺寸结果一样。

    这一条管的是「适应文字大小（最小）」用的那个点，不是拖动下限 —— 拖动下限**故意**跟着
    流向轴走，见 A2。
    """
    bad = []
    sizes = [(120, 80), (200, 110), (300, 200), (460, 190), (900, 600), (1600, 1000)]
    for n in (1, 2, 4, 12, 40, 120, 400):
        for vertical in (False, True):
            reference = minimum_for(n, MIN_FONT, vertical)
            for size in sizes[1:]:
                if Card(n, size=size, vertical=vertical).smallest_card() != reference:
                    bad.append((n, vertical, reference,
                                Card(n, size=size, vertical=vertical).smallest_card()))
    return bad


def audit_floor_reads_only_flow_axis():
    """A2：拖动下限只读「文字流过的轴」，**绝不读它约束的那根轴**。

    横排约束高度，所以固定宽度、把高度从最小扫到 3000，下限必须一模一样。它要是跟着高度
    动，卡片就顺着一条不断下移的下限一直缩下去 —— 老版正是这么错的。

    竖排是镜像：固定高度、扫宽度，下限必须一模一样。

    自由的那根轴必须是常数（一字卡片上的那个值），不随任何东西变。
    """
    bad = []
    unit = smallest_text_card()
    for n in (1, 2, 4, 12, 40, 120, 400):
        for vertical in (False, True):
            # 「另一根轴」的取值范围：从一字卡片的下限起步 —— 那是它允许的最小值 —— 一路
            # 到远超任何真实卡片的高度/宽度。
            others = (unit[0] if vertical else unit[1], 120, 400, 1200, 3000)
            for flow in (40, 60, 120, 260, 520, 900):
                seen = set()
                for other in others:
                    size = (other, flow) if vertical else (flow, other)
                    f = Card(n, size=size, vertical=vertical).floor()
                    seen.add(f[1] if not vertical else f[0])
                if len(seen) != 1:
                    bad.append((n, vertical, flow, sorted(seen)))
    return bad


def audit_flow_axis_mirror():
    """H+I：横排拖宽 / 竖排拖高时，被约束的那根轴的下限必须**单调下降**，并且**降得到一行/一列**。

    单调 = 拖宽绝不会反过来要求更高的卡片。降得到一行 = 这条抱怨的正面回答：横排卡片够宽
    时，最小高度就是「一行字 + 内边距」，而不是「面积最小那个点的高度」。

    自由的那根轴在这里必然恒定（它由 SmallestTextCard 直接给出），所以这一条在源码级断言里
    查（verify_size.py 的 Z 组），模拟器只查这两条真的会错的。
    """
    bad = []
    unit = smallest_text_card()
    for n in (4, 12, 40, 120):
        for vertical in (False, True):
            one = unit[1] if not vertical else unit[0]
            prev = None
            reached = None
            for flow in range(40, 4400, 2):
                # 另一根轴留 400：要「拖宽之后高度能不能降」，前提是卡片本来够高、文字装得下。
                # 留 40 的话旧公式只会看到一个装不下自己的卡片，下限被退化态一兜，这条断言就
                # 被平凡地满足了 —— 那样它对旧公式就没有牙。
                size = (400, flow) if vertical else (flow, 400)
                f = Card(n, size=size, vertical=vertical).floor()
                v = f[1] if not vertical else f[0]
                if prev is not None and v > prev:
                    bad.append(("下限随流向轴上升", n, vertical, flow, prev, v))
                    break
                prev = v
                if v <= one:
                    reached = v
                    break
            if reached is None:
                bad.append(("降不到一行/一列", n, vertical, prev, one))
    return bad


def audit_floor_holds_text():
    """B：下限处（横排与竖排）文字必须装得下，宽度和高度都要。"""
    bad = []
    for n in (1, 2, 4, 12, 40, 120, 400):
        for vertical in (False, True):
            fw, fh = minimum_for(n, MIN_FONT, vertical)
            view_w, view_h = view_of(fw, fh)
            if not fits(n, MIN_FONT, view_w, view_h, vertical):
                bad.append((n, vertical, (fw, fh),
                            block(n, MIN_FONT, view_w, view_h, vertical)))
    return bad


def smallest_valid_probe(text_length, font, vertical):
    """第一个真的装得下自己文字的探针 —— 也就是「一个字一行 / 一列」的那个极值。

    探针从 SmallestView 起步时，视图可能比一个字还矮/还窄，量出来的块放不进它自己那个视图，
    这样的候选会被 MinimumFor 剔掉（见 _candidate_fits 与 _candidate_holds_a_glyph）。所以
    对照值也要从「第一个合法探针」取，而不是直接拿 SmallestView —— 后者在新下限下已经不是
    一个能用的候选了。
    """
    probe = SMALLEST_VIEW
    while probe <= WIDEST_VIEW:
        if vertical:
            bw, bh = block(text_length, font, WIDEST_VIEW, probe, True)
        else:
            bw, bh = block(text_length, font, probe, WIDEST_VIEW, False)
        if (bw > 0 and bh > 0 and _candidate_fits(probe, bw, bh, vertical) and
                _candidate_holds_a_glyph(probe, vertical)):
            return probe
        probe = next_probe(probe)
    return WIDEST_VIEW


def audit_floor_is_compact():
    """C：下限不能是「一个字一行」的窄条，也不能离最优太远。

    窄条（探针取最小）是同一个目标函数上的另一个极值，面积一定更大；这条断言就是钉住
    「几何探针挑到的是面积最小的那一张」，而不是挑到随便哪一张。
    另外拿 1px 细网格扫出的真正最优做对照，要求几何探针的结果不超过它的 1.25 倍。
    """
    bad = []
    for n in (1, 2, 4, 12, 40, 120, 400):
        for vertical in (False, True):
            f = minimum_for(n, MIN_FONT, vertical)
            # 探针取最小 → 横排一个字一行、竖排一个字一列
            p = smallest_valid_probe(n, MIN_FONT, vertical)
            if vertical:
                bw, bh = block(n, MIN_FONT, WIDEST_VIEW, p, True)
                sliver = (max(FLOOR_X, bw + TEXT_SLACK + MARGIN_X), max(FLOOR_Y, p + MARGIN_Y))
            else:
                bw, bh = block(n, MIN_FONT, p, WIDEST_VIEW, False)
                sliver = (max(FLOOR_X, p + MARGIN_X), max(FLOOR_Y, bh + TEXT_SLACK + MARGIN_Y))
            if f[0] * f[1] > sliver[0] * sliver[1]:
                bad.append(("比窄条还大", n, vertical, f, sliver))
                continue
            best = floor_fine(n, MIN_FONT, vertical)
            if f[0] * f[1] > best[0] * best[1] * 1.25:
                bad.append(("离最优太远", n, vertical, f, best))
    return bad


OLD_FIXED_MIN_HEIGHT = 56   # 上一版去掉的那个固定最小高度
OLD_FIXED_MIN_WIDTH = 80    # 上一版的退化态最小宽度
OLD_MARGIN_X = 64           # 上一版左右各 32


def old_minimum_for_reference(text_length, vertical):
    """用上一版的常量与公式算同一个下限，只作对照。

    上一版：左右各 32（MARGIN_X=64）、上下各 20（MARGIN_Y=40）、退化态 80x56，而且没有
    「文字区至少装得下一个字」这条守卫 —— 它是被 80 像素的退化态宽度盖住的。
    """
    # 这里要临时改模块级常量，必须先声明 global：否则下面这些名字在函数里变成局部变量，
    # 读它们的这一行自己就会抛 UnboundLocalError（踩过一次）。
    global MARGIN_X, MARGIN_Y, FLOOR_X, FLOOR_Y, SMALLEST_GLYPH_WIDTH, SMALLEST_LINE_HEIGHT
    saved = (MARGIN_X, MARGIN_Y, FLOOR_X, FLOOR_Y, SMALLEST_GLYPH_WIDTH, SMALLEST_LINE_HEIGHT)
    MARGIN_X, MARGIN_Y = OLD_MARGIN_X, 40
    FLOOR_X, FLOOR_Y = OLD_FIXED_MIN_WIDTH, OLD_FIXED_MIN_HEIGHT
    SMALLEST_GLYPH_WIDTH = SMALLEST_LINE_HEIGHT = 0
    try:
        return minimum_for(text_length, MIN_FONT, vertical)
    finally:
        (MARGIN_X, MARGIN_Y, FLOOR_X, FLOOR_Y,
         SMALLEST_GLYPH_WIDTH, SMALLEST_LINE_HEIGHT) = saved


def audit_floor_width_is_content_driven():
    """G：同一段文字，新下限必须比上一版窄，而且不能因此变得更大。

    「最小宽度改不下去」的根因是左右各 32 像素的内边距：一行四个字的下限宽度是 107 像素，
    其中 64 是内边距，文字自己只占 37。这条断言把两件事一起钉住 —— 宽度真的降下来了，而且
    降下来的方式不是把卡片换个方向撑大（面积不许变大，那是「最少空背景」的底线）。
    """
    bad = []
    for n in (1, 2, 4, 12, 40):
        for vertical in (False, True):
            new = minimum_for(n, MIN_FONT, vertical)
            old = old_minimum_for_reference(n, vertical)
            if new[0] >= old[0]:
                bad.append(("宽度没降", n, vertical, new, old))
            if new[0] * new[1] > old[0] * old[1]:
                bad.append(("面积反而变大", n, vertical, new, old))
    return bad


def audit_floor_height_is_content_driven():
    """F：一行小字的下限高度必须低于旧的固定 56。

    这一版之前，卡片底部留了 40 像素给一个从不显示的日期标签，于是每张卡片都比自己的文字
    至少高 40 像素：一行最小字号的下限永远停在 56。这条断言钉住「下限由文字算出来，不是
    被一个固定的 56 撑住」，也就是这次改动真正要解决的那件事。
    """
    bad = []
    for n in (1, 2, 4):
        for vertical in (False, True):
            f = minimum_for(n, MIN_FONT, vertical)
            if f[1] >= OLD_FIXED_MIN_HEIGHT:
                bad.append((n, vertical, f))
    return bad


def audit_no_bounce():
    """D：只拖被约束的那根轴，卡片沿该轴只许变小或停住，绝不许被顶回来。

    横排拖高：宽度全程不动，高度不许变大。竖排拖宽：高度全程不动，宽度不许变大。

    另一根轴（自由的那根）不参与这一条 —— 它是常数下限，不会动。
    """
    bad = []
    for n in (4, 12, 40, 120):
        for vertical in (False, True):
            for start in ((900, 600), (460, 190), (300, 200), (200, 110), (120, 80)):
                card = Card(n, size=start, vertical=vertical)
                if card.floor()[0] > card.w or card.floor()[1] > card.h:
                    continue                     # 起点本身就在下限之下，先让它自己长大
                prev = (card.w, card.h)
                for _ in range(2000):
                    card.drag(*card.shrink_step())
                    grew = (card.w > prev[0]) if vertical else (card.h > prev[1])
                    if grew:
                        bad.append((n, vertical, start, prev, (card.w, card.h)))
                        break
                    if (card.w, card.h) == prev:
                        break
                    prev = (card.w, card.h)
    return bad


def audit_diagonal_converges():
    """对角往小拖（拖右下角）：宽度/高度单调不增、最终停住、停下时文字完整。

    横排对角拖窄时，文字折行会**把高度顶上去**。这不是回弹，是文字真的需要更多地方 ——
    老版靠「宽度不许低于面积最小那个点的宽度」把它藏起来了，现在宽度能自由收窄，代价就是
    窄到一定程度卡片必须变高。要钉的是它有底：宽度下限是常数，所以高度必然停在某个有限
    值上，不许一路升上去缩成一条竖线。

    D 查的是「只拖被约束的那根轴」（也就是用户抱怨的那件事），这一条查的是角上抓手的
    另一件事：两个轴同时动时不许发散的、也必须真的停下来。
    """
    bad = []
    for n in (4, 12, 40, 120):
        for vertical in (False, True):
            for start in ((900, 600), (460, 190), (300, 200)):
                card = Card(n, size=start, vertical=vertical)
                # 自由的那根轴：横排是宽度，竖排是高度。它只许单调不增。
                prev_free = card.w if not vertical else card.h
                settled = False
                for _ in range(4000):
                    before = (card.w, card.h)
                    card.drag(-2, -2)
                    free = card.w if not vertical else card.h
                    if free > prev_free:
                        bad.append(("自由轴被顶回来", n, vertical, start, prev_free, free))
                        break
                    prev_free = free
                    if (card.w, card.h) == before:
                        settled = True
                        break
                if not settled:
                    bad.append(("不收敛", n, vertical, start, (card.w, card.h)))
                    continue
                if card.auto and not card.text_whole():
                    bad.append(("停下时文字被裁", n, vertical, start, (card.w, card.h)))
    return bad


def audit_fit_to_text_shrinks():
    """「适应文字大小（最小）」必须真的缩到「面积最小」那个点，缩不动就是失败。

    上一版这个动作和拖动下限共用同一个点，所以 Size = 点 永远不会被 MinimumSize 夹住。
    现在两者分开了：一张拖窄了的横排卡片，高度下限比面积最小的那张卡片高得多，而 Form.Size
    会被 MinimumSize 夹住 —— 不清空下限就设尺寸的话，这一步会静悄悄地什么都不做。
    """
    bad = []
    for n in (4, 12, 40, 120):
        for vertical in (False, True):
            # 先把卡片拖到「自由轴最小、被约束的轴被文字顶到最高」的极端形状
            card = Card(n, size=(smallest_text_card()[0] if not vertical else 1200,
                                 smallest_text_card()[1] if vertical else 1200),
                        vertical=vertical)
            card.drag(-2, -2)
            for _ in range(60):
                before = (card.w, card.h)
                card.drag(-2, -2)
                if (card.w, card.h) == before:
                    break
            wide, tall = (card.w, card.h)
            card.fit_to_text()
            want = minimum_for(n, MIN_FONT, vertical)
            if (card.w, card.h) != want:
                bad.append(("没缩到点上", n, vertical, (wide, tall), (card.w, card.h), want))
            elif not card.text_whole():
                bad.append(("点上文字被裁", n, vertical, (card.w, card.h)))
    return bad


def audit_settled_is_floor(cases):
    """停下来之后，MinimumSize 必须正好等于下限，卡片也必须正好贴在它上面。

    自动字号关掉时有个例外：字体是用户自己选的，卡片可能本来就比下限小（字号刚被调大）。
    这时源码让下限「让位」而不是把卡片撑大 —— 撑大又会让字号跟着宽度再变，转回来又动下限。
    """
    bad = []
    for name, auto, card, _settled in cases:
        if not auto:
            continue
        if card.minimum != card._ceiling(card.floor()):
            bad.append((name, card.minimum, card._ceiling(card.floor())))
    return bad


def main():
    if OLD_FORMULA:
        print("（用被换掉的那版下限公式：面积最小那个点直接当拖动下限）")
        print()
    cases = []

    print("== 一路往小拖（只拖被约束的那根轴：横排拖高、竖排拖宽），看停在哪里 ==")
    for length in (4, 12, 40, 120):
        cases.append(("自动适应开 / %d 字" % length, True) + run("%d 字" % length, length))
    print()

    print("== 下限是一条曲线，不是一个点：拖宽之后高度下限降下来 ==")
    print("   横排，40 字。宽度不同，文字折成的行数不同，所以最小高度也不同。")
    unit = smallest_text_card()
    for w in (40, 120, 260, 460, 900, 1600):
        f = Card(40, size=(w, 400)).floor()
        print("     宽度 %5d → 最小高度 %4d（一字卡片的下限是 %d）" % (w, f[1], unit[1]))
    print("   对照：面积最小那个点是 %s —— 老版把它当拖动下限，两个端点都被剪掉了"
          % (minimum_for(40, MIN_FONT, False),))
    print()

    print("== 竖排 ==")
    for length in (4, 12, 40):
        cases.append(("竖排 / %d 字" % length, True) +
                     run("%d 字" % length, length, vertical=True))
    print()

    print("== 竖排的镜像：拖高之后宽度下限收进来 ==")
    for h in (40, 120, 260, 600, 1200):
        f = Card(40, size=(400, h), vertical=True).floor()
        print("     高度 %5d → 最小宽度 %4d（一字卡片的下限是 %d）" % (h, f[0], unit[0]))
    print()

    print("== 关掉「文字自动适应卡片」（字号不跟着缩，文字超出时按老规矩裁掉） ==")
    for length in (4, 40, 120):
        cases.append(("自动适应关 / %d 字" % length, False) +
                     run("%d 字" % length, length, auto_fit=False))
    print()

    print("== 字号调整量 -24（用户把字调到最小） ==")
    cases.append(("调整量 -24 / 40 字", True) + run("40 字", 40, adjust=-24))
    print()

    print("== 反向：从小往大拖，不许被最小值棘轮卡住 ==")
    ratchet = False
    for length in (4, 40):
        card, _ = run("%d 字先缩到最小" % length, length)
        w0, h0 = card.w, card.h
        for _ in range(40):
            card.drag(2, 2)
        print("  %-26s 再往大拖 40 步 → %dx%d 字号 %.1f"
              % ("同一张卡片", card.w, card.h, card.font()))
        if card.w <= w0 or card.h <= h0:
            ratchet = True
            print("  [!!] 往大拖没有变大，被最小值卡住了")
    print()

    print("== 断言 ==")
    bad = 0
    for name, auto, card, settled in cases:
        if not settled:
            print("  [!!] %s：没有收敛" % name)
            bad += 1
        if card.w < FLOOR_X or card.h < FLOOR_Y:
            print("  [!!] %s：小于退化态下限（%dx%d）" % (name, card.w, card.h))
            bad += 1
        if auto:
            if not card.text_whole():
                print("  [!!] %s：自动适应开着，停下时文字却被裁掉了" % name)
                bad += 1
            # 下限高度就是按「一行最小字号 + 内边距」算的，所以拖到底字号必须掉到搜索底部。
            # 容一档余量：一行 10.5px 的字在 12px 的行高里还塞得下 8pt。
            # 竖排的列数由高度决定，同一个下限上还能塞进更大的字，所以只对横排作要求。
            if not card.vertical and card.font() > MIN_FONT + 2.0:
                print("  [!!] %s：拖到底字号却还是 %.1f，说明下限比文字实际需要的还松"
                      % (name, card.font()))
                bad += 1
        else:
            # 自动适应关：卡片不许被强行撑大，但必须仍然能自由缩小
            if card.w > START[0] or card.h > START[1]:
                print("  [!!] %s：自动适应关，卡片却被撑大了（%dx%d）" % (name, card.w, card.h))
                bad += 1
        # 停下后必须真的拖不动了 —— 只拖被约束的那根轴，也就是用户抱怨的那件事
        before = (card.w, card.h)
        card.drag(*card.shrink_step())
        if (card.w, card.h) != before:
            print("  [!!] %s：停下后还能继续缩小（%dx%d → %dx%d）"
                  % (name, before[0], before[1], card.w, card.h))
            bad += 1
    if ratchet:
        bad += 1

    print("  -- A1 「适应文字大小（最小）」那个点与卡片尺寸无关 --")
    drift = audit_size_independent()
    if drift:
        bad += 1
        seen = []
        for item in drift:
            if item not in seen:
                seen.append(item)
        print("  [!!] %d 组里出现 %d 种不同结果，例如：" % (len(drift), len(seen)))
        for n, vertical, a, b in seen[:6]:
            print("       %d 字（%s）：换个体量，下限从 %s 变成 %s"
                  % (n, "竖排" if vertical else "横排", a, b))
    else:
        print("  通过（7 种文字量 × 横竖排 × 6 个起始尺寸）")

    print("  -- A2 拖动下限只读流向轴，绝不读它约束的那根轴 --")
    crossread = audit_floor_reads_only_flow_axis()
    if crossread:
        bad += 1
        for n, vertical, flow, values in crossread[:6]:
            print("  [!!] %d 字（%s）流向轴 %d：扫另一根轴得到 %d 种不同的下限 %s"
                  % (n, "竖排" if vertical else "横排", flow, len(values), values))
    else:
        print("  通过（7 种文字量 × 横竖排 × 6 个流向长度 × 5 个另一轴取值，下限恒定）")

    print("  -- B 下限处文字装得下 --")
    clipped = audit_floor_holds_text()
    if clipped:
        bad += 1
        for n, vertical, f, blk in clipped[:6]:
            print("  [!!] %d 字（%s）：下限 %dx%d 处文字块是 %.0fx%.0f，装不下"
                  % (n, "竖排" if vertical else "横排", f[0], f[1], blk[0], blk[1]))
    else:
        print("  通过（7 种文字量 × 横竖排）")

    print("  -- C 「面积最小」那个点不是窄条也不是扁条 --")
    lumpy = audit_floor_is_compact()
    if lumpy:
        bad += 1
        for why, n, vertical, f, other in lumpy[:6]:
            print("  [!!] %d 字（%s）：%s —— 下限 %dx%d，对照 %dx%d"
                  % (n, "竖排" if vertical else "横排", why, f[0], f[1], other[0], other[1]))
    else:
        print("  通过（比单行/单列小，且离 1px 细网格的最优不超过 1.25 倍）")

    print("  -- D 只拖被约束的那根轴，不抖动 --")
    bounce = audit_no_bounce()
    if bounce:
        bad += 1
        for n, vertical, start, prev, now in bounce[:6]:
            print("  [!!] %d 字（%s）从 %dx%d 出发：拖到 %dx%d 后弹回 %dx%d"
                  % (n, "竖排" if vertical else "横排", start[0], start[1],
                     prev[0], prev[1], now[0], now[1]))
    else:
        print("  通过（4 种文字量 × 横竖排 × 5 个起点，被约束的那根轴全程单调不增）")

    print("  -- E 停住时 MinimumSize 正好等于下限 --")
    stuck = audit_settled_is_floor(cases)
    if stuck:
        bad += 1
        for name, got, want in stuck[:6]:
            print("  [!!] %s：MinimumSize 是 %s，下限是 %s" % (name, got, want))
    else:
        print("  通过")

    print("  -- H 拖宽降得到一行 / 拖高降得到一列，且单调 --")
    mirror = audit_flow_axis_mirror()
    if mirror:
        bad += 1
        for item in mirror[:6]:
            if len(item) == 6:
                _, n, vertical, flow, prev, now = item
                print("  [!!] %d 字（%s）流向轴 %d：下限从 %g 升到 %g —— 拖宽反而要求更高"
                      % (n, "竖排" if vertical else "横排", flow, prev, now))
            else:
                _, n, vertical, prev, one = item
                print("  [!!] %d 字（%s）：下限最低只到 %g，够不到一行/一列的 %g"
                      % (n, "竖排" if vertical else "横排", prev, one))
    else:
        print("  通过（4 种文字量 × 横竖排：拖宽/拖高时下限单调不升，且都能降到一行/一列）")

    print("  -- I 对角拖窄：会因折行变高，但必须有底、必须停下 --")
    diagonal = audit_diagonal_converges()
    if diagonal:
        bad += 1
        for item in diagonal[:6]:
            print("  [!!] %s" % (item,))
    else:
        print("  通过（自由轴单调不增，都收敛，停下时文字完整）")

    print("  -- J 「适应文字大小（最小）」缩得到那个点 --")
    stuck_fit = audit_fit_to_text_shrinks()
    if stuck_fit:
        bad += 1
        for item in stuck_fit[:6]:
            print("  [!!] %s" % (item,))
    else:
        print("  通过（4 种文字量 × 横竖排：从极端形状出发都能缩到「面积最小」那个点）")

    print("  -- F 一行小字的下限高度低于旧的固定 56 --")
    tall = audit_floor_height_is_content_driven()
    if tall:
        bad += 1
        for n, vertical, f in tall[:6]:
            print("  [!!] %d 字（%s）：下限高度 %g 仍在 %d 上，等于又被固定高度撑住了"
                  % (n, "竖排" if vertical else "横排", f[1], OLD_FIXED_MIN_HEIGHT))
    else:
        print("  通过（1/2/4 字 × 横竖排的下限高度都低于 %d）" % OLD_FIXED_MIN_HEIGHT)

    print("  -- G 同一段文字的下限比上一版窄，且面积没有变大 --")
    wide = audit_floor_width_is_content_driven()
    if wide:
        bad += 1
        for why, n, vertical, new, old in wide[:6]:
            print("  [!!] %d 字（%s）：%s —— 新版 %dx%d，上一版 %dx%d"
                  % (n, "竖排" if vertical else "横排", why,
                     new[0], new[1], old[0], old[1]))
    else:
        print("  通过（1/2/4/12/40 字 × 横竖排：宽度都降了，面积都没变大）")

    print()
    if bad:
        print("失败 %d 条" % bad)
        return 1
    print("  全部通过：拖到底停在「文字区正好装下文字」，文字不裁、字号不破底、不抖动、不缩成缝")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
