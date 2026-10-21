#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""复现「文字增大到一定大小后无法再变大」。

忠实照 Forms.cs 现在的实现建模（横排）：

    SetManualFontSize(size)   auto=False; manual=clamp(size); _quoteFontSize=-1;
                              FitQuoteFont();  GrowCardToText();
    FitQuoteFont()            自动关且手动>0 → chosen = clamp(manual)
                              UpdateCardSizeLimits()
    UpdateCardSizeLimits()    floor = DragLimit()          ← 只约束高度，宽度是常数
                              floor > 当前尺寸 且自动关   → 让位（MinimumSize 不动）
                              否则 MinimumSize = floor
    GrowCardToText()          wanted = DragLimit()
                              wanted 任一轴 > MaximumSize  → wanted = SmallestCard()（面积最小）
                              next = (clamp(Max, max(W, wanted.W)),
                                      clamp(Max, max(H, wanted.H)))
                              next == 当前 → 停；否则 Size = next（被 MinimumSize 夹住）

用法：
    python3 repro_larger.py                 # 默认桌面 1920x1080
    python3 repro_larger.py --screen 1366x768
    python3 repro_larger.py --text 50 --width 200 --height 200
"""
import math
import sys

H_MARGIN = 24          # CardSizeLimits.HorizontalMargin
V_MARGIN = 24          # CardSizeLimits.VerticalMargin
TEXT_MARGIN = 2        # QuoteView.TextMargin
TEXT_SLACK = 4         # CardSizeLimits.TextSlack
MIN_CARD_W = 32
MIN_CARD_H = 32
SMALLEST_GLYPH = 10
SMALLEST_LINE = 12
SMALLEST_VIEW = 10
WIDEST_VIEW = 4096
SMALLEST_FONT = 7.0
MAX_MANUAL = 96.0
CHAR_ADVANCE = 1.33
LINE_HEIGHT = 1.50

UNIT_W = max(MIN_CARD_W, SMALLEST_GLYPH + TEXT_SLACK + H_MARGIN)   # 38
UNIT_H = max(MIN_CARD_H, SMALLEST_LINE + TEXT_SLACK + V_MARGIN)    # 40

SHORT = "千里之行，始于足下。"
MEDIUM = "我们终此一生，就是要摆脱他人的期待，找到真正的自己。"
LONG = ("人的一生中，最光辉的一天并非是功成名就的那一天，"
        "而是从悲叹与绝望中产生对人生挑战与勇敢意志的那一天。")


def next_probe(p):
    return max(p + 1, p + p // 8 + 1)


def block(text, size, view_w, view_h):
    """QuoteView.MeasureBlock（横排）：按宽度折行。块宽被夹到视图宽。"""
    advance = CHAR_ADVANCE * size
    available = max(1, view_w - TEXT_MARGIN * 2)
    per_line = max(1, int(available // advance)) if advance > 0 else len(text)
    lines = max(1, math.ceil(len(text) / per_line))
    return int(min(len(text) * advance, available)), int(lines * LINE_HEIGHT * size)


def drag_limit(text, size, w, h, vertical=False):
    """CardSizeLimits.DragLimitFor + WithFloorMeasure（自动关 → 按当前字号量）。"""
    if vertical:
        view_flow = max(SMALLEST_VIEW, h - V_MARGIN)
        bw, bh = block(text, size, WIDEST_VIEW, view_flow)
        return (max(UNIT_W, bw + TEXT_SLACK + H_MARGIN), UNIT_H)
    view_flow = max(SMALLEST_VIEW, w - H_MARGIN)
    bw, bh = block(text, size, view_flow, WIDEST_VIEW)
    return (UNIT_W, max(UNIT_H, bh + TEXT_SLACK + V_MARGIN))


def flow_fit(text, size, across, vertical=False):
    """CardSizeLimits.FlowFitFor：只沿流向轴长大时，还能装下全文的最小卡片。"""
    for probe in range(SMALLEST_VIEW, WIDEST_VIEW + 1):
        probe = SMALLEST_VIEW if probe == SMALLEST_VIEW else next_probe(prev)
        if vertical:
            bw, bh = block(text, size, WIDEST_VIEW, probe)
            if bh + TEXT_SLACK > probe:
                prev = probe
                continue
            if probe - TEXT_SLACK < SMALLEST_LINE:
                prev = probe
                continue
            card = (max(UNIT_W, bw + TEXT_SLACK + H_MARGIN), probe + V_MARGIN)
            if bw + TEXT_SLACK + H_MARGIN <= across:
                return card
            longest = card
        else:
            bw, bh = block(text, size, probe, WIDEST_VIEW)
            if bw + TEXT_SLACK > probe:
                prev = probe
                continue
            if probe - TEXT_SLACK < SMALLEST_GLYPH:
                prev = probe
                continue
            card = (probe + H_MARGIN, max(UNIT_H, bh + TEXT_SLACK + V_MARGIN))
            if bh + TEXT_SLACK + V_MARGIN <= across:
                return card
            longest = card
        prev = probe
    return longest


class Card(object):
    def __init__(self, w, h, text, maximum, vertical=False):
        self.w, self.h, self.text = w, h, text
        self.max_w, self.max_h = maximum
        self.vertical = vertical
        self.auto = False
        self.manual = 0.0
        self.size = -1.0
        self.minimum = (MIN_CARD_W, MIN_CARD_H)

    # ---- FitQuoteFont ---------------------------------------------------
    def fit_font(self):
        if self.w <= 0 or self.h <= 0:
            return self.size
        if not self.auto and self.manual > 0.0:
            chosen = max(SMALLEST_FONT, min(MAX_MANUAL, self.manual))
        else:
            start = min(29.0, max(20.0, self.w / 17.0))
            chosen = max(SMALLEST_FONT, start)
        self.size = chosen
        return chosen

    def font(self):
        return self.size if self.size > 0 else self.fit_font()

    # ---- UpdateCardSizeLimits -------------------------------------------
    def update_limits(self):
        f = drag_limit(self.text, self.font(), self.w, self.h)
        if f[0] > self.w or f[1] > self.h:
            if not self.auto:
                return self.minimum          # 让位
            self.minimum = f
            self.w, self.h = max(self.w, f[0]), max(self.h, f[1])
            return self.minimum
        self.minimum = f
        return self.minimum

    # ---- GrowCardToText -------------------------------------------------
    def grow_to_text(self):
        for _ in range(4):
            wanted = drag_limit(self.text, self.font(), self.w, self.h)
            if wanted[1] > self.max_h or wanted[0] > self.max_w:
                across = self.max_w if self.vertical else self.max_h
                wanted = flow_fit(self.text, self.font(), across, self.vertical)
            nw = min(self.max_w, max(self.w, wanted[0]))
            nh = min(self.max_h, max(self.h, wanted[1]))
            # Size = next 会被 MinimumSize 夹住（操作系统那一道）
            nw = max(self.minimum[0], nw)
            nh = max(self.minimum[1], nh)
            if (nw, nh) == (self.w, self.h):
                break
            self.w, self.h = nw, nh
            self.fit_font()
            self.update_limits()

    # ---- 菜单 -----------------------------------------------------------
    def set_manual(self, size):
        self.auto = False
        self.manual = max(SMALLEST_FONT, min(MAX_MANUAL, size))
        self.size = -1.0
        self.fit_font()
        self.update_limits()
        self.grow_to_text()
        self.update_limits()
        return self.font()

    def larger(self):
        return self.set_manual(self.font() + 2.0)

    def text_whole(self):
        view_w, view_h = max(10, self.w - H_MARGIN), max(10, self.h - V_MARGIN)
        bw, bh = block(self.text, self.font(), view_w, view_h)
        return bw <= max(1, view_w - TEXT_MARGIN * 2) and bh <= max(1, view_h - TEXT_MARGIN * 2)


def main():
    screen = (1920, 1080)
    text = MEDIUM
    w, h = 200, 200
    for i, a in enumerate(sys.argv):
        if a == "--screen" and i + 1 < len(sys.argv):
            screen = tuple(int(x) for x in sys.argv[i + 1].split("x"))
        elif a == "--width" and i + 1 < len(sys.argv):
            w = int(sys.argv[i + 1])
        elif a == "--height" and i + 1 < len(sys.argv):
            h = int(sys.argv[i + 1])
        elif a == "--text" and i + 1 < len(sys.argv):
            text = {"4": "上善若水", "10": SHORT, "26": MEDIUM, "50": LONG}[sys.argv[i + 1]]

    print("桌面上限 %dx%d，起始卡片 %dx%d，文字 %d 字" % (screen[0], screen[1], w, h, len(text)))
    card = Card(w, h, text, screen)
    card.set_manual(SMALLEST_FONT)
    print("  %-6s %-12s %-10s %s" % ("点击", "字号", "卡片", "文字完整"))
    last = card.font()
    for n in range(1, 61):
        before = (card.w, card.h, card.font())
        card.larger()
        now = (card.w, card.h, card.font())
        if n <= 6 or n % 5 == 0 or now[2] <= before[2]:
            print("  第%2d次  %5.1f      %4dx%-4d  %s%s"
                  % (n, card.font(), card.w, card.h,
                     "是" if card.text_whole() else "否",
                     "   ← 字号没变" if now[2] <= before[2] else ""))
        if now[2] <= before[2]:
            print()
            print("*** 卡住了：第 %d 次点「大一点」，字号停在 %.1f（上限 %.0f）"
                  % (n, card.font(), MAX_MANUAL))
            print("    卡片 %dx%d，桌面 %dx%d，文字完整=%s"
                  % (card.w, card.h, screen[0], screen[1],
                     "是" if card.text_whole() else "否"))
            print("    拖动下限 %s，沿流向轴凑 %s"
                  % (drag_limit(text, card.font(), card.w, card.h),
                     flow_fit(text, card.font(),
                              screen[0] if card.vertical else screen[1], card.vertical)))
            return 1
    print("  60 次都还能变大，字号到 %.1f" % card.font())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
