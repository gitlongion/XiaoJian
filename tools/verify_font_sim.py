#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""字号调整链路仿真：证明「字体大小调整失灵」确实存在，并证明修复后不再失灵。

模型与 Forms.cs 一一对应：
  LayoutCard      view = (W - 64, H - 24)
  TextMargin = 2  available = viewW - 4
  MeasureText     点 -> 像素 em = size * 96 / 72；行高 = 1.2 * em；CJK 宽 em，ASCII 宽 em/2
  FitsBlock       block.Height <= viewH - 4（宽度被 MeasureHorizontal 夹到 available，恒成立）
  MinimumFor      CardSizeLimits.MinimumFor：探针从小到大，取面积最小的候选卡片
  FitQuoteFont    旧：字号只从「卡片自己算出的起点」往下找，自动适应会把它吃回去
                  新：手动字号是绝对值，卡片不许改小它

边距与退化态下限不再写死，而是从真实源码里读（见下面 _load）。这一版把纵向边距从 40 改成
24、退化态高度从 56 改成 32；模拟器要是还用老数字，它造出来的「拖到下限」卡片会比真实下限
高出 40 像素，根本不再「恰好包住文字」——第 2b 组验的东西就名不副实了。

用法：
  python3 verify_font_sim.py              # 跑断言
  python3 verify_font_sim.py --old        # 只用旧实现跑，用来看缺陷复现
  python3 verify_font_sim.py --src <目录>  # 从源码里读常量（推荐）
"""

import os
import re
import sys

# 读不到源码时才退回这组字面量。
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


_READ_CONSTS = {}


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


def _load(name, source, default):
    """先读常量本身，再把它登记进名字表，好让 `A + B` 这种写法也能算出来。"""
    value = _const(source, name, default)
    _READ_CONSTS[name] = value
    return value


TEXT_PAD_LEFT = _load("TextPadLeft", "Forms.cs", 32)
TEXT_PAD_RIGHT = _load("TextPadRight", "Forms.cs", 32)
TEXT_PAD_TOP = _load("TextPadTop", "Forms.cs", 12)
TEXT_PAD_BOTTOM = _load("TextPadBottom", "Forms.cs", 12)
H_MARGIN = _load("HorizontalMargin", "Forms.cs", TEXT_PAD_LEFT + TEXT_PAD_RIGHT)
V_MARGIN = _load("VerticalMargin", "Forms.cs", TEXT_PAD_TOP + TEXT_PAD_BOTTOM)
MIN_CARD_W = _load("MinCardWidth", "Storage.cs", 32)
MIN_CARD_H = _load("MinCardHeight", "Storage.cs", 32)
SMALLEST_GLYPH_WIDTH = _load("SmallestGlyphWidth", "Forms.cs", 10)
TEXT_MARGIN = 2
TEXT_SLACK = TEXT_MARGIN * 2    # 就是 CardSizeLimits.TextSlack
SMALLEST = 7.0
LARGEST = 96.0
# 自动适应只从「起点」往下缩，所以起点就是上限。默认 29 是改动前写死在搜索里的那个常数。
DEFAULT_AUTO_MAX = 29.0
GUESS_CHARS = 17.0
GUESS_FLOOR = 20.0
SMALLEST_VIEW = 10
WIDEST_VIEW = 4096

# CardSizeLimits.MaximumFor(桌面)。真实上限取整个虚拟桌面，这里取两种有代表性的尺寸：
# 大屏上「大一点」应该能一路长到字号上限，小屏上应该停在「卡片已经铺满桌面」那一档。
DESKTOPS = [(1920, 1080), (1366, 768)]

# 边距必须真的是两个内边距之和：改了一个忘了另一个，模拟器验的就不是交付的算术。
if H_MARGIN != TEXT_PAD_LEFT + TEXT_PAD_RIGHT or V_MARGIN != TEXT_PAD_TOP + TEXT_PAD_BOTTOM:
    print("边距常量与内边距对不上：H=%d V=%d，内边距 %d+%d / %d+%d"
          % (H_MARGIN, V_MARGIN, TEXT_PAD_LEFT, TEXT_PAD_RIGHT, TEXT_PAD_TOP, TEXT_PAD_BOTTOM))
    sys.exit(2)

if _SRC is not None and os.path.isfile(os.path.join(_SRC, "Forms.cs")):
    print("常量取自源码 %s：H_MARGIN=%d V_MARGIN=%d SLACK=%d FLOOR=(%d,%d)"
          % (_SRC, H_MARGIN, V_MARGIN, TEXT_SLACK, MIN_CARD_W, MIN_CARD_H))
else:
    print("常量取字面量副本（没找到源码，传 --src <目录> 可以读真实常量）："
          "H_MARGIN=%d V_MARGIN=%d SLACK=%d FLOOR=(%d,%d)"
          % (H_MARGIN, V_MARGIN, TEXT_SLACK, MIN_CARD_W, MIN_CARD_H))


def em(size):
    return size * 96.0 / 72.0


def line_height(size):
    return max(1, int(round(em(size) * 1.20)))


def advance(ch, size):
    return em(size) if ord(ch) > 0x2E80 else em(size) * 0.5


def wrapped_height(text, size, available):
    if not text:
        return 0
    lines, used = 1, 0.0
    for ch in text:
        w = advance(ch, size)
        if used + w > available and used > 0:
            lines += 1
            used = w
        else:
            used += w
    return lines * line_height(size)


def next_probe(probe):
    return max(probe + 1, probe + probe // 8 + 1)


class Card(object):
    """一张卡片：尺寸、文字、自动适应开关、手动字号。"""

    def __init__(self, width, height, text, auto=True, manual=0.0, adjustment=0.0,
                 desktop=(1920, 1080), auto_max=DEFAULT_AUTO_MAX):
        self.w = width
        self.h = height
        self.text = text
        self.auto = auto
        self.manual = manual
        self.adjustment = adjustment
        self.auto_max = auto_max
        self.max_w, self.max_h = desktop
        self._size = -1.0

    # -- 与 QuoteView 对应 ---------------------------------------------------

    def view(self):
        return max(10, self.w - H_MARGIN), max(10, self.h - V_MARGIN)

    def block(self, size, view_w, view_h):
        available = max(1, view_w - TEXT_MARGIN * 2)
        height = wrapped_height(self.text, size, available)
        if height <= 0:
            return (0, 0)
        widest = 0.0
        used = 0.0
        for ch in self.text:
            w = advance(ch, size)
            if used + w > available and used > 0:
                widest = max(widest, used)
                used = w
            else:
                used += w
        widest = max(widest, used)
        return (int(min(widest, available)), height)

    def fits(self, size):
        view_w, view_h = self.view()
        block_w, block_h = self.block(size, view_w, view_h)
        if block_w <= 0 or block_h <= 0:
            return True
        return block_w <= max(1, view_w - TEXT_MARGIN * 2) and block_h <= max(1, view_h - TEXT_MARGIN * 2)

    def minimum_for(self, size):
        """CardSizeLimits.MinimumFor(measure, vertical=False) 的横排分支。"""
        best = (MIN_CARD_W, MIN_CARD_H)
        best_area = None
        probe = SMALLEST_VIEW
        while probe <= WIDEST_VIEW:
            block_w, block_h = self.block(size, probe, WIDEST_VIEW)
            if block_w <= 0 or block_h <= 0:
                probe = next_probe(probe)
                continue
            # 候选必须真的装得下自己量出来的块。横排这里恒成立（block() 已经把宽度夹到
            # available = probe - 4），但守卫要跟源码一致：竖排正是被它救回来的 —— 列数
            # 由高度决定，探针触底时视图比一个字还矮，量出来的块仍是一个字高。
            if block_w + TEXT_SLACK > probe:
                probe = next_probe(probe)
                continue
            # 文字区还得装得下一个字。横排会折行，块宽被夹到视图宽，所以上面那条守卫对它
            # 恒成立 —— 一个比一个字还窄的视图照样报出「刚好装下」，而面积最小偏偏最喜欢它。
            if probe - TEXT_SLACK < SMALLEST_GLYPH_WIDTH:
                probe = next_probe(probe)
                continue
            width = max(MIN_CARD_W, probe + H_MARGIN)
            height = max(MIN_CARD_H, block_h + TEXT_MARGIN * 2 + V_MARGIN)
            area = width * height
            if best_area is None or area < best_area:
                best, best_area = (width, height), area
            probe = next_probe(probe)
        return best

    def drag_limit(self, size):
        """CardSizeLimits.DragLimitFor + WithFloorMeasure（自动关 → 按当前字号量）。

        只约束横跨文字流向的那根轴：横排约束高度，宽度是「一个字」的常数下限。
        """
        view_flow = max(SMALLEST_VIEW, self.w - H_MARGIN)
        _, block_h = self.block(size, view_flow, WIDEST_VIEW)
        unit_h = max(MIN_CARD_H, 12 + TEXT_MARGIN * 2 + V_MARGIN)
        return (max(MIN_CARD_W, SMALLEST_GLYPH_WIDTH + TEXT_SLACK + H_MARGIN),
                max(unit_h, block_h + TEXT_SLACK + V_MARGIN))

    def flow_fit(self, size, across):
        """CardSizeLimits.FlowFitFor：只沿流向轴长大时，还能装下全文的最小卡片。

        这是 DragLimitFor 的逆运算，也是「横跨那根轴已经顶到桌面」时唯一的出路。
        **不是面积最小的那张卡**：大字号下面积最小是「一个字一行」的窄条，比卡片现在还窄，
        拿它当目标一点都长不大 —— 卡片就此停住，字号每加一点都只是多裁掉一点。
        """
        longest = (MIN_CARD_W, MIN_CARD_H)
        probe = SMALLEST_VIEW
        while probe <= WIDEST_VIEW:
            block_w, block_h = self.block(size, probe, WIDEST_VIEW)
            if block_w <= 0 or block_h <= 0:
                probe = next_probe(probe)
                continue
            if block_w + TEXT_SLACK > probe:
                probe = next_probe(probe)
                continue
            if probe - TEXT_SLACK < SMALLEST_GLYPH_WIDTH:
                probe = next_probe(probe)
                continue
            longest = (probe + H_MARGIN, max(MIN_CARD_H, block_h + TEXT_SLACK + V_MARGIN))
            if block_h + TEXT_SLACK + V_MARGIN <= across:
                return longest
            probe = next_probe(probe)
        return longest

    # -- 与 QuoteCardForm 对应 ----------------------------------------------

    def fit_font(self):
        """Forms.cs FitQuoteFont()。"""
        if self.w <= 0 or self.h <= 0:
            return self._size
        # --regress-fit：假装忘了手动字号是绝对值，退回「一切由卡片决定」。
        if not self.auto and self.manual > 0.0 and not Card.REGRESS_FIT:
            chosen = max(SMALLEST, min(LARGEST, self.manual))
        else:
            start = min(self.auto_max, max(GUESS_FLOOR, self.w / GUESS_CHARS)) + self.adjustment
            chosen = max(SMALLEST, start)
            if self.auto or Card.REGRESS_FIT:
                found = False
                size = start
                while size > SMALLEST and not found:
                    if self.fits(size):
                        chosen, found = size, True
                    size -= 1.0
                if not found and self.fits(SMALLEST):
                    chosen = SMALLEST
        self._size = chosen
        return chosen

    def font_size(self):
        return self._size if self._size > 0 else self.fit_font()

    def floor(self):
        """DragLimit()：拖动下限（UpdateCardSizeLimits 用的那个）。自动适应时按最小字号量，
        手动字号时按当前字号量。"""
        reference = SMALLEST if self.auto else self.font_size()
        return self.drag_limit(reference)

    def update_limits(self):
        """UpdateCardSizeLimits()。自动字号关掉时，下限高于卡片就「让位」而不是把卡片撑大。"""
        f = self.floor()
        if f[0] > self.w or f[1] > self.h:
            if not self.auto:
                return
            self.w, self.h = max(self.w, f[0]), max(self.h, f[1])
        return

    def grow_to_text(self):
        """GrowCardToText()：只增不减，直到装得下。"""
        for _ in range(4):
            wanted_w, wanted_h = self.floor()
            # 横跨流向那根轴已经顶到桌面 → 唯一的出路是沿流向轴把文字摊长。
            # --regress-flow 退回改动前的目标（「面积最小」那张卡），用来确认断言真的抓得住。
            if wanted_h > self.max_h or wanted_w > self.max_w:
                if Card.REGRESS_FLOW:
                    wanted_w, wanted_h = self.minimum_for(self.font_size())
                else:
                    wanted_w, wanted_h = self.flow_fit(self.font_size(), self.max_h)
            next_w = min(self.max_w, max(self.w, wanted_w))
            next_h = min(self.max_h, max(self.h, wanted_h))
            if (next_w, next_h) == (self.w, self.h):
                break
            self.w, self.h = next_w, next_h
            self.fit_font()

    def back_off(self):
        """BackOffToFittingSize()：卡片长到桌面那么大还是装不下，就把字号退回装得下的最大一档，
        而不是把裁掉的文字交给用户。"""
        if self.fits(self.font_size()):
            return self.font_size()
        size = self.font_size() - 1.0
        while size >= SMALLEST:
            if self.fits(size):
                self.manual = size
                self._size = -1.0
                self.fit_font()
                return size
            size -= 1.0
        return self.font_size()

    # -- 菜单四项 -----------------------------------------------------------

    # --regress：把「手动字号」偷偷退回成旧的「只改起点」，用来确认断言真的抓得住缺陷。
    # --regress-fit：让 FitQuoteFont 忘掉手动字号。
    # --regress-grow：让「大一点」不再把卡片撑到装得下。
    # --regress-reset：让「恢复默认字号」只清偏移，不把自动适应交还回去。
    # --regress-flow：让 GrowCardToText 撞到桌面上限时退回「面积最小」那张卡（改动前的目标），
    #                 并且不把字号退回装得下的一档 —— 也就是「文字增大到一定大小后无法再变大」。
    # --regress-dialog：让字体对话框把字号丢掉（改动前的行为），字号只有碰巧和自动适应算出来
    #                   的一样时才看不出问题 —— 也就是「字体大小对话框里超过一定数字就失效」。
    REGRESS = False
    REGRESS_FIT = False
    REGRESS_GROW = False
    REGRESS_RESET = False
    REGRESS_FLOW = False
    REGRESS_DIALOG = False

    def set_manual(self, size, grow=True):
        """SetManualFontSize()。"""
        if Card.REGRESS:
            self.adjustment = max(-24, min(8, self.adjustment + (size - self.font_size())))
            self._size = -1.0
            return self.fit_font()
        self.auto = False
        self.manual = max(SMALLEST, min(LARGEST, size))
        self._size = -1.0
        self.fit_font()
        self.update_limits()
        if grow and not Card.REGRESS_GROW:
            self.grow_to_text()
        if not (Card.REGRESS_GROW or Card.REGRESS_FLOW):
            self.back_off()
        return self.font_size()

    def choose_font(self, picked):
        """ChooseQuoteFont()：字体对话框。字体名与字形照常收下，字号必须真的用上。

        改动前只收了字体名和字形，字号被丢掉，然后 FitQuoteFont() 又按卡片自己算一遍 ——
        用户在对话框里选的字号就此失效，只有碰巧和自动适应算出来的一样时才看不出问题。

        只换了字体名/字形（字号没变）时不碰自动适应的开关：卡片保持自己算出的字号，
        换字体之后度量不同，那正是自动适应要处理的事。
        """
        before = self.font_size()
        if Card.REGRESS_DIALOG:
            self._size = -1.0
            return self.fit_font()
        self._size = -1.0
        if abs(picked - before) < 0.05:
            self.fit_font()
            return self.font_size()
        return self.set_manual(picked)

    def set_auto_max(self, cap):
        """SetAutoFitMax()：上限夹在 [最小字号, 最大手动字号] 里，然后重新适应。"""
        self.auto_max = max(SMALLEST, min(LARGEST, cap))
        self._size = -1.0
        self.fit_font()
        self.update_limits()
        self.grow_to_text()
        return self.font_size()

    def larger(self):
        return self.set_manual(self.font_size() + 2.0)

    def smaller(self):
        return self.set_manual(self.font_size() - 2.0)

    def smallest(self):
        return self.set_manual(SMALLEST)

    def reset(self):
        """ResetTextSize()：字号与自动适应一起回到默认。"""
        self.adjustment = 0.0
        self.manual = 0.0
        if not Card.REGRESS_RESET:
            self.auto = True
        self._size = -1.0
        return self.fit_font()

    # -- 旧实现（对照） -----------------------------------------------------

    def larger_old(self, delta):
        """旧实现：只改起点，自动适应会把它吃回去。"""
        self.adjustment = max(-24, min(8, self.adjustment + delta))
        self._size = -1.0
        return self.fit_font()


SHORT = "千里之行，始于足下。"
MEDIUM = "我们终此一生，就是要摆脱他人的期待，找到真正的自己。"
LONG = ("人的一生中，最光辉的一天并非是功成名就的那一天，"
        "而是从悲叹与绝望中产生对人生挑战与勇敢意志的那一天。")
TINY = "上善若水"

TEXTS = [("4 字", TINY), ("10 字", SHORT), ("26 字", MEDIUM), ("50 字", LONG)]

_passed = 0
_failures = 0


def check(ok, message):
    global _passed, _failures
    if ok:
        _passed += 1
        print("  [OK ] " + message)
    else:
        _failures += 1
        print("  [FAIL] " + message)


def section(title):
    print()
    print("== " + title + " ==")


def scenarios():
    """(说明, 卡片) —— 每种文字各造一张「默认卡片」和一张「拖到下限的卡片」。"""
    out = []
    for name, text in TEXTS:
        out.append(("默认卡片 460x190 · " + name, Card(460, 190, text)))
        floor_w, floor_h = Card(460, 190, text).minimum_for(SMALLEST)
        out.append(("拖到下限 %dx%d · %s" % (floor_w, floor_h, name),
                    Card(floor_w, floor_h, text)))
    out.append(("大卡片 900x500 · 10 字", Card(900, 500, SHORT)))
    out.append(("小卡片 200x110 · 26 字", Card(200, 110, MEDIUM)))
    return out


def old_is_dead(card):
    """旧实现下「大一点」是否无效。"""
    card._size = -1.0
    card.adjustment = 0.0
    before = card.fit_font()
    after = card.larger_old(2)
    return abs(after - before) < 0.05


def run():
    only_old = "--old" in sys.argv
    Card.REGRESS = "--regress" in sys.argv
    Card.REGRESS_FIT = "--regress-fit" in sys.argv
    Card.REGRESS_GROW = "--regress-grow" in sys.argv
    Card.REGRESS_RESET = "--regress-reset" in sys.argv
    Card.REGRESS_FLOW = "--regress-flow" in sys.argv
    Card.REGRESS_DIALOG = "--regress-dialog" in sys.argv

    section("1. 缺陷复现：旧实现下「大一点」会被自动适应吃回去")
    dead = 0
    for name, card in scenarios():
        if old_is_dead(card):
            dead += 1
    check(dead > 0,
        "旧实现在 %d/%d 种场景下「大一点」完全无效" % (dead, len(scenarios())))
    # 卡片恰好包住文字时最严重：四档全部失效
    worst = 0
    for name, card in scenarios():
        results = []
        for delta in (2, -2):
            card._size = -1.0
            card.adjustment = 0.0
            base = card.fit_font()
            results.append(abs(card.larger_old(delta) - base) < 0.05)
        if all(results):
            worst += 1
    check(worst > 0,
        "其中 %d/%d 种场景下「大一点」和「小一点」同时无效（字号被钉死）" % (worst, len(scenarios())))

    if only_old:
        print()
        print("（--old：只跑缺陷复现）")
        return

    section("2. 修复后：四项在每一种场景下都真的改变字号")
    all_ok = True
    for name, card in scenarios():
        base = card.fit_font()
        bigger = card.larger()
        smaller = card.smaller()
        least = card.smallest()
        reference = Card(card.w, card.h, card.text, auto=True)
        reference._size = -1.0
        restored = card.reset()
        # 每一项都必须真的动字号：大一点要变大、小一点要变小、最小要落在下限、
        # 重置要回到「卡片自己算出的值」。
        ok = (bigger > base + 0.05 and smaller < bigger - 0.05 and
              abs(least - SMALLEST) < 0.05 and
              abs(restored - reference.fit_font()) < 0.05)
        if not ok:
            all_ok = False
            print("     %s：基准 %.1f 大一点 %.1f 小一点 %.1f 最小 %.1f 重置 %.1f（卡片自算 %.1f）" %
                  (name, base, bigger, smaller, least, restored, reference.fit_font()))
    check(all_ok, "「大一点 / 小一点 / 设为最小 / 恢复默认字号」四档全部真的改变字号（%d 种场景）"
        % len(scenarios()))

    section("2b. 用户抱怨的那一项：在「拖到下限」的卡片上点「大一点」必须变大")
    complaint = 0
    total_floor = 0
    for name, text in TEXTS:
        floor_w, floor_h = Card(460, 190, text).minimum_for(SMALLEST)
        card = Card(floor_w, floor_h, text)
        base = card.fit_font()
        total_floor += 1
        if card.larger() > base + 0.05:
            complaint += 1
        else:
            print("     %s：下限卡片 %dx%d 上「大一点」无效（%.1f）" % (name, floor_w, floor_h, base))
    check(complaint == total_floor,
        "%d/%d 张「恰好包住文字」的卡片上「大一点」都生效" % (complaint, total_floor))

    section("3. 字号变大之后，卡片长到装得下，文字不被裁")
    grown_ok = True
    for name, card in scenarios():
        card.fit_font()
        card.larger()
        if not card.fits(card.font_size()):
            grown_ok = False
            print("     %s：字号 %.1f 在 %dx%d 上装不下" % (name, card.font_size(), card.w, card.h))
    check(grown_ok, "「大一点」之后每一张卡片都装得下自己的文字")

    section("4. 手动字号是绝对值，与卡片尺寸无关")
    independent = True
    for name, card in scenarios():
        card.fit_font()
        card.larger()
        picked = card.font_size()
        for width, height in ((card.w + 200, card.h + 150), (card.w, card.h),
                              (max(MIN_CARD_W, card.w - 60), max(MIN_CARD_H, card.h - 40))):
            probe = Card(width, height, card.text, auto=False, manual=picked)
            if abs(probe.fit_font() - picked) > 0.05:
                independent = False
                print("     %s：卡片变成 %dx%d 后字号从 %.1f 变成 %.1f" %
                      (name, width, height, picked, probe.fit_font()))
    check(independent, "把卡片拖大或拖小，手选的字号一动不动")

    section("5. 字号有上下限；撞到边界之前卡片必须一路长到装得下")
    floor_card = Card(460, 190, SHORT)
    floor_card.fit_font()
    for _ in range(60):
        floor_card.larger()
    check(floor_card.font_size() <= LARGEST + 0.001,
        "一路点「大一点」最多到 %.0f 磅（实测 %.1f）" % (LARGEST, floor_card.font_size()))
    tiny_card = Card(460, 190, SHORT)
    tiny_card.fit_font()
    for _ in range(60):
        tiny_card.smaller()
    check(abs(tiny_card.font_size() - SMALLEST) < 0.05,
        "一路点「小一点」停在最小字号 %.0f 磅（实测 %.1f）" % (SMALLEST, tiny_card.font_size()))

    # 这一条就是「文字增大到一定大小后无法再变大」。
    #
    # 卡片只有一根轴能被拖动下限约束（横排是高度），而高度顶到桌面之后，唯一的出路是沿
    # 文字流向把文字摊长 —— 也就是 FlowFitFor。此前这里退回的是「面积最小」那张卡，
    # 而大字号下面积最小是「一个字一行」的窄条，比卡片现在还窄：Math.Max 之后什么都没长，
    # 卡片就此停住，字号每加一点都只是多裁掉一点文字。表现就是「文字长到某个字号就再也
    # 长不大了」。
    clipped = []
    stalled = []
    for desktop in DESKTOPS:
        for name, text in TEXTS:
            card = Card(460, 190, text, desktop=desktop)
            card.fit_font()
            for _ in range(60):
                before = card.font_size()
                card.larger()
                if not card.fits(card.font_size()):
                    clipped.append("%s · 桌面 %dx%d · 字号 %.1f 时卡片 %dx%d 装不下文字"
                                   % (name, desktop[0], desktop[1], card.font_size(),
                                      card.w, card.h))
                    break
                if card.font_size() <= before + 0.05:
                    # 字号停在这里分两种情况，只有一种是缺陷：
                    #   撞到 %.0f 磅的设计上限 —— 正常；
                    #   卡片已经铺满桌面 —— 正常（横排唯一还能长的就是宽度，宽度到顶就真没地方了）；
                    #   都没撞到却停了 —— 缺陷：说明还有地方可以长，只是没长。
                    room = card.w < desktop[0]
                    if card.font_size() < LARGEST - 0.05 and room:
                        stalled.append("%s · 桌面 %dx%d · 字号停在 %.1f（上限 %.0f），"
                                       "宽度却只到 %d —— 还有地方可以长"
                                       % (name, desktop[0], desktop[1], card.font_size(),
                                          LARGEST, card.w))
                    break
    check(not clipped,
          "连点 60 次「大一点」：任何一档字号下文字都不被裁（%d 种文字 × %d 种桌面）"
          % (len(TEXTS), len(DESKTOPS)))
    check(not stalled,
          "字号没长到上限、宽度也还有余量就停住了 —— 说明还有地方可以长（%d 处）"
          % len(stalled))
    for line in (clipped + stalled)[:6]:
        print("     " + line)

    section("6. 单调性：连点「大一点」只会越来越大，不会反弹")
    monotonic = True
    for name, card in scenarios():
        card.fit_font()
        previous = card.font_size()
        for _ in range(6):
            current = card.larger()
            if current < previous - 0.05:
                monotonic = False
                print("     %s：%.1f -> %.1f 反而变小" % (name, previous, current))
            previous = current
    check(monotonic, "连续点「大一点」全程单调不减")

    section("7. 「恢复默认字号」把字号和自动适应一起交还给卡片")
    handed_back = True
    for name, card in scenarios():
        card.fit_font()
        card.larger()
        reset_size = card.reset()
        reference = Card(card.w, card.h, card.text, auto=True)
        reference._size = -1.0
        if not card.auto or card.manual != 0.0 or abs(reset_size - reference.fit_font()) > 0.05:
            handed_back = False
            print("     %s：重置后 auto=%s manual=%.1f 字号 %.1f（卡片自己算的是 %.1f）" %
                  (name, card.auto, card.manual, reset_size, reference.fit_font()))
    check(handed_back, "重置之后自动适应重新打开，字号等于卡片自己算出的值")

    section("8. 字体对话框里选的字号必须真的用上")
    # 「字体大小设置对话框里超过一定数字大小就失效」。
    #
    # 改动前 ChooseQuoteFont() 只从对话框收了字体名和字形，**字号被丢掉**，然后
    # FitQuoteFont() 又按卡片自己算一遍。于是用户在对话框里选的字号根本没有用上 ——
    # 之所以看着「小的生效、大的失效」，是因为自动适应算出来的字号恰好落在小的一侧，
    # 选那个值时看不出差别，选大的就露馅了。
    dialog_honoured = True
    dialog_clipped = []
    for name, card in scenarios():
        card.fit_font()
        before = card.font_size()
        asked = before + 6.0
        got = card.choose_font(asked)
        if abs(got - asked) > 0.05:
            dialog_honoured = False
            print("     %s：对话框里选了 %.1f，实际是 %.1f" % (name, asked, got))
        if card.auto:
            print("     %s：选了大字号却还是自动适应开着 —— 下一轮适应会把它吃回去" % name)
            dialog_honoured = False
        if not card.fits(got):
            dialog_clipped.append("%s：选 %.1f 之后卡片 %dx%d 装不下文字"
                                  % (name, got, card.w, card.h))
    check(dialog_honoured,
          "字体对话框里选的字号真的用上了，并且自动适应跟着关掉（%d 种场景）" % len(scenarios()))
    check(not dialog_clipped,
          "对话框里选了大字号之后卡片长到装得下，文字不被裁（%d 处）" % len(dialog_clipped))
    for line in dialog_clipped[:6]:
        print("     " + line)

    # 只换字体名/字形（字号没动）时不碰自动适应的开关：卡片保持自己算出的字号，换字体
    # 之后度量不同，那正是自动适应要处理的事。
    kept_auto = True
    for name, card in scenarios():
        card.fit_font()
        same = card.font_size()
        got = card.choose_font(same)
        if not card.auto:
            kept_auto = False
            print("     %s：字号没变，自动适应却被关掉了" % name)
        if abs(got - same) > 0.05:
            kept_auto = False
            print("     %s：只换字体，字号却从 %.1f 变成 %.1f" % (name, same, got))
    check(kept_auto, "字号没变时只换字体，自动适应保持打开、字号保持原样")

    section("9. 自动字号上限：大卡片上的字不该被一个常数钉死")
    # 自动适应只会从起点往下缩，所以起点就是上限。改动前那个起点是 `Math.Min(29.0f, …)` ——
    # 一张铺满桌面的卡片上，四个字只占宽度的十分之一，而且没有任何办法调。
    #
    # 现在上限是每张卡片自己的 AutoFitMaxFontSize，默认仍是 29（升级不改变现有卡片）。
    big = Card(900, 500, TINY, auto=True, auto_max=DEFAULT_AUTO_MAX)
    big.fit_font()
    capped = big.font_size()
    capped_share = sum(advance(ch, capped) for ch in TINY) / big.w
    raised = Card(900, 500, TINY, auto=True)
    raised.set_auto_max(LARGEST)
    raised_share = sum(advance(ch, raised.font_size()) for ch in TINY) / raised.w
    check(raised.font_size() > capped + 0.5,
        "把上限从 %.0f 放到 %.0f，大卡片上的字真的变大了（%.1f → %.1f 磅）"
        % (DEFAULT_AUTO_MAX, LARGEST, capped, raised.font_size()))
    check(raised_share > capped_share + 0.05,
        "占卡片宽度的比例也跟着上去了（%.0f%% → %.0f%%）"
        % (100 * capped_share, 100 * raised_share))
    check(raised.fits(raised.font_size()),
        "放大之后文字仍然完整地装得下")

    # 上限是「上」限，不是「设定值」：它只在卡片本来能放更大字的时候起作用。
    small = Card(300, 120, LONG, auto=True)
    small.set_auto_max(LARGEST)
    check(small.font_size() < LARGEST - 0.5,
        "小卡片上把上限放到 %.0f 也不会溢出，实际停在 %.1f 磅（装不下就往下缩）"
        % (LARGEST, small.font_size()))
    check(small.fits(small.font_size()), "小卡片上文字仍然完整")

    # 夹在 [最小字号, 最大手动字号] 里，手改配置文件也推不出去。
    clamped = Card(900, 500, TINY, auto=True)
    clamped.set_auto_max(1000.0)
    check(abs(clamped.auto_max - LARGEST) < 0.01, "上限被夹在最大手动字号 %.0f 上" % LARGEST)
    clamped.set_auto_max(1.0)
    check(abs(clamped.auto_max - SMALLEST) < 0.01, "上限被夹在最小字号 %.0f 上" % SMALLEST)

    # 上限只管「自动」那一档：手选字号时它不参与，所以关掉自动适应时菜单会置灰。
    manual = Card(900, 500, TINY, auto=False, manual=12.0)
    manual.fit_font()
    manual.set_auto_max(LARGEST)
    check(abs(manual.font_size() - 12.0) < 0.05,
        "手选字号时上限不参与（仍是 %.1f 磅）" % manual.font_size())

    print()
    print("通过 %d 项，失败 %d 项" % (_passed, _failures))
    return 0 if _failures == 0 else 1


if __name__ == "__main__":
    sys.exit(run())
