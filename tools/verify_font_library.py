#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""源码级静态断言：字号调整（四项菜单）与「每张卡片独立内容库」。

为什么需要它：macOS 上跑不了 exe，而这两件事都横跨三个文件、且失效时一声不吭——
    字号   「大一点」看着像没反应，其实是自动适应把它吃了回去（Forms.cs）
    内容库 每张卡片到底读哪个文件，只有 Program.cs / CardManager / QuoteRepository
            三处凑齐了才成立；漏掉任何一处，症状都是「两张卡片显示同一句话」，
            而这两类症状都不会编译报错。

断言全部作用在「剥掉注释之后」的源码上：本项目的注释习惯是把常量名/函数名写出来
解释为什么这么写，不剥注释会把「注释里提到过」当成「代码里写了」。

用法：
    python3 verify_font_library.py                # 跑全部断言
    python3 verify_font_library.py --reverse      # 注入缺陷做反向验证
    python3 verify_font_library.py --src <目录>   # 指定源码目录
"""
import os
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
PROGRAM = os.path.join(SRC, "Program.cs")


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
            out.append(text[i:min(j + 1, n)])
            i = j + 1
            continue
        if c == '"':
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == '"':
                    break
                j += 1
            out.append(text[i:min(j + 1, n)])
            i = j + 1
            continue
        if c == "'":
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == "'":
                    break
                j += 1
            out.append(text[i:min(j + 1, n)])
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


def block_at(src, open_index):
    """从 '{' 开始取到配对的 '}'，返回 (body, end_index)。"""
    depth = 0
    i = open_index
    while i < len(src):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return src[open_index:i + 1], i
        i += 1
    raise ValueError("括号不配对")


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
    """同名成员有多个时，按方法体里的特征串挑一个。"""
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


# ------------------------------------------------------------------- 断言本体

def check(forms, storage, program):
    problems = []

    # F1 「文字设置」四项必须全部走「手动字号」，且旧的偏移写法不能残留。
    # 「文字设置」的构建代码在 BuildTextSettingsMenu 里（0.9.11 从 BuildMenu 拆出来的）。
    # 注意不能再用 "TextSettings" 当特征串去挑 BuildMenu —— `BuildTextSettingsMenu(state)`
    # 这个名字里就含着它，挑到的会是顶层那个方法。
    text_menu = safe_body(forms,
        "private ToolStripMenuItem BuildTextSettingsMenu(MenuState state)", problems,
        "F1 BuildTextSettingsMenu")
    if text_menu is None:
        problems.append("F1 取不到「文字设置」菜单的构建代码")
    else:
        for expect, what in (
            ('larger.Click += delegate { SetManualFontSize(CurrentFontSize() + 2.0f); };', "大一点"),
            ('smaller.Click += delegate { SetManualFontSize(CurrentFontSize() - 2.0f); };', "小一点"),
            ('smallest.Click += delegate { SetManualFontSize(CardState.MinManualFontSize); };', "设为最小"),
        ):
            if expect not in text_menu:
                problems.append("F1 「%s」没有接管字号：%s" % (what, expect))
        reset = body_by_marker(text_menu, "reset.Click += delegate", "_card.FontAdjustment = 0;")
        if reset is None:
            problems.append("F1 「恢复默认字号」没有清 FontAdjustment")
        else:
            for fragment in ("_card.ManualFontSize = 0f;", "_card.AutoFitText = true;",
                             "_quoteFontSize = -1.0f;"):
                if fragment not in reset:
                    problems.append("F1 「恢复默认字号」缺少 %s" % fragment)
    if "ChangeFontAdjustment" in forms:
        problems.append("F1 旧的 ChangeFontAdjustment 还留着：字号有两条互不相同的路径")

    # F2 SetManualFontSize：关自动适应、记绝对字号、失效缓存、重排、撑卡片。
    set_manual = safe_body(forms, "private void SetManualFontSize(float size)",
                           problems, "F2 SetManualFontSize")
    if set_manual is not None:
        for fragment in ("_card.AutoFitText = false;",
                         "_card.ManualFontSize = Math.Max(CardState.MinManualFontSize,",
                         "Math.Min(CardState.MaxManualFontSize, size));",
                         "_quoteFontSize = -1.0f;",
                         "FitQuoteFont();",
                         "GrowCardToText();"):
            if fragment not in set_manual:
                problems.append("F2 SetManualFontSize 缺少 %s" % fragment)

    # F3 FitQuoteFont：手动字号是绝对值，卡片不许把它改回去。
    fit_font = safe_body(forms, "private void FitQuoteFont()", problems, "F3 FitQuoteFont")
    if fit_font is not None:
        if "if (!_card.AutoFitText && _card.ManualFontSize > 0f)" not in fit_font:
            problems.append("F3 FitQuoteFont 没有把手动字号当成绝对值")
        if "Math.Min(CardState.MaxManualFontSize, _card.ManualFontSize)" not in fit_font:
            problems.append("F3 FitQuoteFont 没有把手动字号夹在上限内")
        if "QuoteView.SmallestFontSize" not in fit_font:
            problems.append("F3 FitQuoteFont 没有用 SmallestFontSize 当下限")
        # 自动适应那条路必须原样保留（往下找 + 单独试下限字号）
        if "if (_quoteView.FitsBlock(candidate))" not in fit_font:
            problems.append("F3 FitQuoteFont 丢了自动适应的搜索")
        if "_quoteView.FitsBlock(floorFont)" not in fit_font:
            problems.append("F3 FitQuoteFont 丢了「单独试最小字号」这一档")

    # F4 GrowCardToText：只增不减，并且落盘、留屏。
    # 「只增不减」现在由 next 里的 Math.Max(Width/Height, …) 承担：早退条件换成了
    # 「next 与当前尺寸相同就停」，两者表达同一件事，但断言必须认新的写法。
    grow = safe_body(forms, "private void GrowCardToText()", problems, "F4 GrowCardToText")
    if grow is not None:
        for fragment in ("Math.Max(Width, wanted.Width)",
                         "Math.Max(Height, wanted.Height)",
                         "if (next.Width == Width && next.Height == Height) break;",
                         "Size = next;",
                         "UpdateCardSizeLimits();",
                         "_card.CardWidth = Width;",
                         "KeepOnScreen();"):
            if fragment not in grow:
                problems.append("F4 GrowCardToText 缺少 %s" % fragment)
        if "Math.Min(Width" in grow or "Math.Min(Height" in grow:
            problems.append("F4 GrowCardToText 会缩小卡片：手选字号不该反过来裁卡片")
        # 只沿「文字必须有」的那根轴长大；撞到桌面上限时沿**流向轴**把文字摊长 —— 那一根轴
        # 撑不下时，这是唯一的出路。退回「面积最小」那张卡是错的：大字号下它是「一个字一行」
        # 的窄条，比卡片现在还窄，Math.Max 之后什么都没长，卡片就此停住。
        if "CardSizeLimits.FlowFitFor(measure, vertical, across)" not in grow:
            problems.append("F4 GrowCardToText 撞到桌面上限时没有沿流向轴把文字摊长："
                            "退回「面积最小」那张卡只会让卡片停住，字号每加一点都只是多裁一点")
    # 卡片长到桌面那么大还是装不下时，字号必须退回装得下的一档，而不是把裁掉的文字交给
    # 用户。这一条在 SetManualFontSize 里，不在 GrowCardToText 里 —— 只看 grow 的方法体
    # 是抓不到它的。
    set_manual = safe_body(forms, "private void SetManualFontSize(float size)", problems,
                           "F4 SetManualFontSize")
    if set_manual is not None and "BackOffToFittingSize();" not in set_manual:
        problems.append("F4 SetManualFontSize 没有在撑不下时把字号退回装得下的一档："
                        "「大一点」会一路把文字裁下去，看着就像「字号再也长不大了」")

    # F4b 字体对话框里选的字号必须真的用上。
    # 改动前 ChooseQuoteFont() 只从对话框收了字体名和字形，**字号被丢掉**，然后
    # FitQuoteFont() 又按卡片自己算一遍 —— 用户在对话框里选的字号根本没有用上。之所以看着
    # 「小的生效、大的失效」，是因为自动适应算出来的字号恰好落在小的一侧，选那个值时看不出
    # 差别，选大的就露馅了。
    #
    # 逻辑放在 ApplyPickedFont 里（对话框只是把选中的字体交给它），所以「对话框打开时显示
    # 当前字号」查 ChooseQuoteFont，「字号怎么用」查 ApplyPickedFont。
    apply = safe_body(forms, "internal void ApplyPickedFont(Font picked)", problems,
                      "F4b ApplyPickedFont")
    if "internal void ApplyPickedFont(Font picked)" not in forms:
        problems.append("F4b 找不到 ApplyPickedFont：字体对话框选中的内容必须有一个能单独调用的"
                        "应用步骤，否则这段逻辑没法在自检里跑到")
    elif apply is not None:
        if "picked.SizeInPoints" not in apply:
            problems.append("F4b ApplyPickedFont 没有收下对话框里的字号：用户在字体对话框里"
                            "选的字号会被丢掉，然后 FitQuoteFont 又按卡片自己算一遍")
        if "SetManualFontSize(size);" not in apply:
            problems.append("F4b ApplyPickedFont 选了新字号却没有走 SetManualFontSize："
                            "自动适应还开着，下一轮适应会把它原样吃回去")
        if "Math.Abs(size - before) >= 0.05f" not in apply:
            problems.append("F4b ApplyPickedFont 没有区分「换了字号」和「只换了字体」："
                            "只换字体名的人会被莫名取消「文字自动适应卡片」")
        if "FitQuoteFont();" not in apply:
            problems.append("F4b ApplyPickedFont 没有「只换字体就重新适应字号」这一支："
                            "换了字体之后度量不同，卡片应该自己重算")
    choose = safe_body(forms, "private void ChooseQuoteFont()", problems, "F4b ChooseQuoteFont")
    if choose is not None:
        if "ApplyPickedFont(dialog.Font);" not in choose:
            problems.append("F4b ChooseQuoteFont 没有把选中的字体交给 ApplyPickedFont："
                            "对话框里选的东西根本没被用上")
        # 对话框打开时应该显示卡片**当前**的字号，否则「只换字体」会顺手把字号跳到 12。
        if "QuoteFont(CurrentFontSize())" not in choose:
            problems.append("F4b ChooseQuoteFont 用固定 12 磅当对话框初值："
                            "只想换字体名的人会被顺手改成 12 磅")

    # F5 勾回「文字自动适应卡片」要顺手丢掉手动字号。
    auto_fit = body_by_marker(forms, "autoFitItem.Click += delegate",
                              "_card.AutoFitText = autoFitItem.Checked;")
    if auto_fit is None:
        problems.append("F5 取不到「文字自动适应卡片」的点击处理")
    elif "if (_card.AutoFitText) _card.ManualFontSize = 0f;" not in auto_fit:
        problems.append("F5 勾回自动适应时没有清掉手动字号，卡片会一直用旧的手选值")

    # F6 CardState：槽位与手动字号的字段与夹取。
    #
    # 查的是 **CardState 类体**，不是整个文件：TextPreset 也有 ManualFontSize 字段，
    # 查整个文件的话，把 CardState 那一份删掉、预设那一份还在，断言照样「过」。
    card_state = safe_body(storage, "internal sealed class CardState", problems, "F6 CardState")
    if card_state is None: card_state = ""
    for fragment, what in (
        ("public const float MinManualFontSize = 7f;", "最小手动字号常量"),
        ("public const float MaxManualFontSize = 96f;", "最大手动字号常量"),
        ("public int Slot = 0;", "槽位字段"),
        ("public float ManualFontSize = 0f;", "手动字号字段"),
        ("Slot = Math.Max(0, Slot);", "槽位夹取"),
        ("ManualFontSize = ManualFontSize <= 0f ? 0f :", "手动字号夹取"),
    ):
        if fragment not in card_state:
            problems.append("F6 CardState 缺少%s：%s" % (what, fragment))
    clamp = safe_body(storage, "public void Clamp()", problems, "F6 CardState.Clamp")
    if clamp is not None and "ManualFontSize" not in clamp:
        problems.append("F6 CardState.Clamp 没有处理 ManualFontSize")

    # F7 三个键必须 Save / Load 一一对应，否则重启就丢。
    append_card = safe_body(storage, "private static void AppendCard(", problems, "F7 AppendCard")
    if append_card is not None:
        for key in ("Slot=", "ManualFontSize="):
            if key not in append_card:
                problems.append("F7 AppendCard 没有写出 %s" % key)
    if '"NextCardSlot=" + NextCardSlot' not in storage:
        problems.append("F7 Save 没有写出 NextCardSlot")
    apply_card = safe_body(storage, "private static bool ApplyCardField(", problems, "F7 ApplyCardField")
    if apply_card is not None:
        for key in ('case "Slot":', 'case "ManualFontSize":'):
            if key not in apply_card:
                problems.append("F7 ApplyCardField 没有读回 %s" % key)
    apply_global = safe_body(storage, "private static void ApplyGlobalField(",
                             problems, "F7 ApplyGlobalField")
    if apply_global is not None and 'case "NextCardSlot":' not in apply_global:
        problems.append("F7 ApplyGlobalField 没有读回 NextCardSlot")

    # F8 内容库按卡片分开：路径由槽位决定，迁移把旧共享库交给第一张卡片。
    content_path = safe_body(storage, "public static string CardContentPath(string dataDirectory, CardState card)",
                             problems, "F8 CardContentPath")
    if content_path is not None:
        if '"content-"' not in content_path:
            problems.append("F8 内容库文件名不再按槽位命名")
        if "Math.Max(1, card.Slot)" not in content_path:
            problems.append("F8 槽位为 0 时没有兜底到 1 号库")
    for_card = safe_body(storage, "public static QuoteRepository ForCard(", problems, "F8 ForCard")
    if for_card is not None and "CardContentPath(dataDirectory, card)" not in for_card:
        problems.append("F8 ForCard 没有走 CardContentPath，两张卡片可能读到同一个文件")
    migrate = safe_body(storage, "public static void MigrateSharedLibrary(",
                        problems, "F8 MigrateSharedLibrary")
    if migrate is not None:
        for fragment in ('"quotes.txt"', "CardContentPath(dataDirectory, state.Cards[0])",
                         "File.Move(legacy, target)", "File.Copy(legacy, target, false)"):
            if fragment not in migrate:
                problems.append("F8 MigrateSharedLibrary 缺少 %s" % fragment)

    # F9 组合根：先迁移，再按卡建仓库。
    if 'QuoteRepository.MigrateSharedLibrary(dataDirectory, state);' not in program:
        problems.append("F9 启动时没有迁移旧共享内容库")
    if 'new QuoteRepository(Path.Combine(dataDirectory, "quotes.txt"), state)' in program:
        problems.append("F9 启动时还在建那个共享内容库")
    if "new CardManager(state, dataDirectory)" not in program:
        problems.append("F9 CardManager 没有拿到数据目录")
    manager = body_by_marker(forms, "internal sealed class CardManager", "AddForm")
    if manager is None:
        problems.append("F9 取不到 CardManager")
    else:
        if "private readonly string _dataDirectory;" not in manager:
            problems.append("F9 CardManager 没有保存数据目录")
        if "private readonly QuoteRepository _repository;" in manager:
            problems.append("F9 CardManager 还留着一个共享仓库：所有卡片会读同一份内容库")
        if "QuoteRepository repository = QuoteRepository.ForCard(_state, _dataDirectory, card);" \
                not in manager:
            problems.append("F9 AddForm 没有按卡片建仓库")
    if "MigrateSharedLibrary" in program and "new CardManager(" in program and \
            program.index("MigrateSharedLibrary") > program.index("new CardManager("):
        problems.append("F9 迁移发生在卡片建好之后：第一张卡片会先读到空内容库")

    # F10 编辑器要说明正在编辑哪一张卡片的内容库。
    if 'Text = cardLabel.Length == 0 ? T("编辑内容库", "Edit content library") :' not in forms:
        problems.append("F10 编辑器标题没有带卡片名")
    if "Manager.LibraryLabel(this)" not in forms:
        problems.append("F10 打开编辑器时没有问卡片是哪一张")

    # F11 背景图也得按卡片分开，且删旧图前要看别的卡片是否还在用。
    if '"background-" + _card.Slot.ToString(CultureInfo.InvariantCulture) + extension' not in forms:
        problems.append("F11 背景图还是共用同一个文件名")
    delete_bg = safe_body(forms, "private void TryDeleteOwnedBackground(",
                          problems, "F11 TryDeleteOwnedBackground")
    if delete_bg is not None:
        if "foreach (CardState other in _app.Cards)" not in delete_bg:
            problems.append("F11 删旧背景图前没有检查别的卡片是否还在用")
        if "other == _card" not in delete_bg:
            problems.append("F11 删旧背景图时没有跳过自己")

    # F12 最小字号只有一个来源，避免「下限」和「手选区间」各写一个 7。
    if "public const float SmallestFontSize = CardState.MinManualFontSize;" not in forms:
        problems.append("F12 QuoteView.SmallestFontSize 与 CardState.MinManualFontSize 不是同一个来源")

    # F13 帮助文本要说明这两件事，否则用户找不到「字号为什么被自动改回去」的答案。
    if "每张卡片各有一份" not in forms:
        problems.append("F13 帮助里没说内容库是每张卡片一份")
    if "会由你决定字号" not in forms:
        problems.append("F13 帮助里没说四项菜单会接管字号")

    # F14 自动字号上限、字号输入框、加粗/斜体。
    #
    # 自动适应只会从「起点」往下缩，所以起点就是上限。改动前那个起点是 `Math.Min(29.0f, …)`
    # —— 一张铺满桌面的卡片上四个字只占宽度的十分之一，而且没有任何办法调。现在上限是每张
    # 卡片自己的 AutoFitMaxFontSize，默认仍是 29（升级不改变现有卡片）。
    if "Math.Min(29.0f," in forms:
        problems.append("F14 FitQuoteFont 又写死了 29 磅的起点：大卡片上的字会被这个常数钉死")
    if "Math.Min(_card.AutoFitMaxFontSize," not in forms:
        problems.append("F14 FitQuoteFont 没有把卡片的 AutoFitMaxFontSize 当作上限")
    if "public float AutoFitMaxFontSize = DefaultAutoFitMaxFontSize;" not in storage:
        problems.append("F14 CardState 少了 AutoFitMaxFontSize 字段（默认值必须是那个常数）")
    if "AutoFitMaxFontSize = Math.Max(MinAutoFitMaxFontSize," not in storage:
        problems.append("F14 CardState.Clamp 没有夹住 AutoFitMaxFontSize：手改配置文件能推出范围")
    # 预设也写同名的键，所以这两条必须限定在卡片的那两个方法体里。
    append_card_f14 = safe_body(storage, "private static void AppendCard(",
                                problems, "F14 AppendCard")
    if append_card_f14 is not None and 'prefix + "AutoFitMaxFontSize="' not in append_card_f14:
        problems.append("F14 保存时没写 AutoFitMaxFontSize（卡片那一份）：重启就丢")
    apply_card_f14 = safe_body(storage, "private static bool ApplyCardField(",
                               problems, "F14 ApplyCardField")
    if apply_card_f14 is not None and 'case "AutoFitMaxFontSize":' not in apply_card_f14:
        problems.append("F14 读取时不认 AutoFitMaxFontSize（卡片那一份）：重启就丢")


    # 菜单项必须建出来、必须接上处理函数、必须有标题（少一个 case 就是一条空白）。
    for key, wiring, label in (
            ("TextExactSize", "exactSize.Click += delegate { AskFontSize(); };", "字号…"),
            ("TextBold", "boldItem.Click += delegate { SetFontStyle(FontStyle.Bold, boldItem.Checked); };",
             "加粗"),
            ("TextItalic",
             "italicItem.Click += delegate { SetFontStyle(FontStyle.Italic, italicItem.Checked); };",
             "斜体"),
            ("AutoFitMax", "capItem.Click += delegate { SetAutoFitMax(cap); };", "自动字号上限")):
        if 'CreateMenuItem("%s")' % key not in forms:
            problems.append("F14 找不到「%s」菜单项（%s）" % (label, key))
        if wiring not in forms:
            problems.append("F14 「%s」菜单项没有接上处理函数" % label)
        if 'case "%s":' % key not in forms:
            problems.append("F14 「%s」没有标题：语言切换后菜单上是一条空白" % label)
    # 五个上限档位都要有标题，少一个就是空白条目。菜单项的名字是按磅数拼出来的
    # （`"AutoFitMax" + ((int)cap)`），所以查那个拼接和档位表，而不是查五个字面量。
    if "float[] autoMaxCaps = { 29f, 36f, 48f, 64f, 96f };" not in forms:
        problems.append("F14 自动字号上限的档位不是 29/36/48/64/96")
    if '"AutoFitMax" + ((int)cap).ToString(CultureInfo.InvariantCulture)' not in forms:
        problems.append("F14 上限档位的菜单项名没有按磅数生成：标题就对不上了")
    for cap in (29, 36, 48, 64, 96):
        if 'case "AutoFitMax%d":' % cap not in forms:
            problems.append("F14 自动字号上限的 %d 磅档没有标题" % cap)
    # 自动适应关掉时上限没有意义，菜单要置灰，而不是留一个点了没反应的条目。
    if "state.AutoMax.Enabled = _card.AutoFitText;" not in forms:
        problems.append("F14 自动适应关掉时「自动字号上限」没有置灰：点了不会发生任何事")

    # 字号输入框必须校验范围。TextInputForm 只会原样把字符串交回来。
    ask = safe_body(forms, "private void AskFontSize()", problems, "F14 AskFontSize")
    if "private void AskFontSize()" not in forms:
        problems.append("F14 找不到 AskFontSize（「字号…」的处理）")
    elif ask is not None:
        if "CardState.MinManualFontSize" not in ask or "CardState.MaxManualFontSize" not in ask:
            problems.append("F14 AskFontSize 没有校验 7..96 的范围：输入 1000 会被当成合法值")
        if "SetManualFontSize(size);" not in ask:
            problems.append("F14 AskFontSize 没有走 SetManualFontSize：自动适应还开着，"
                            "下一轮适应会把字号吃回去")
    # 改字形会改变字宽，所以必须重新适应并让卡片长大，不能把裁掉的字交给用户。
    style = safe_body(forms, "internal void SetFontStyle(FontStyle style, bool on)", problems,
                      "F14 SetFontStyle")
    if "internal void SetFontStyle(FontStyle style, bool on)" not in forms:
        problems.append("F14 找不到 SetFontStyle")
    elif style is not None:
        for fragment in ("_quoteFontSize = -1.0f;", "FitQuoteFont();", "GrowCardToText();",
                         "BackOffToFittingSize();"):
            if fragment not in style:
                problems.append("F14 SetFontStyle 缺少 %s：改了字形之后字号/卡片不会重新适应"
                                % fragment)
        if "value & ~(int)style" not in style:
            problems.append("F14 SetFontStyle 关不掉某一档字形：加粗和斜体是独立的位，"
                            "关一个不能把另一个也清掉")

    # F15 行距与字距。
    #
    # GDI 自己的换行（TextRenderer + WordBreak）既不能调行距也不能调字距，所以换行必须自己接管。
    # 关键不是「加了两个设置」，而是**只有一处换行**：MeasureBlock 和 OnPaint 都走 BuildLines，
    # 量出来的和画出来的才不会分家。
    for needle in ("TextFormatFlags.WordBreak", "TextFormatFlags.EndEllipsis"):
        if needle in forms:
            problems.append("F15 Forms.cs 里还有 %s：换行又交回 GDI 了，行距/字距会失效，"
                            "而且「量出来的」和「画出来的」会变成两套规则" % needle)
    if "internal static class TextLines" not in forms:
        problems.append("F15 找不到 TextLines（横排换行与截断的唯一实现）")
    for fragment in ("return TextLines.Split(Text, available,",
                     "List<string> lines = BuildLines(Font, Width);"):
        if fragment not in forms:
            problems.append("F15 横排没有走 BuildLines/TextLines：量测与绘制会各算各的")
    measure_h = safe_body(forms, "private Size MeasureHorizontal(Font font, int viewWidth)",
                          problems, "F15 MeasureHorizontal")
    if measure_h is not None and "BuildLines(font, viewWidth)" not in measure_h:
        problems.append("F15 MeasureHorizontal 没有用 BuildLines 量：和绘制用的不是同一套换行")

    # 竖排：间距加在「格」上，量测和绘制读的是同一对 cellWidth/cellHeight。
    cell = safe_body(forms, "private void MeasureCell(Font font, out int cellWidth, out int cellHeight)",
                     problems, "F15 MeasureCell")
    if cell is not None:
        # 查完整的算式，不是只查字段名：去掉整项之后字段名还在，弱断言会「过」。
        for fragment in ("(int)Math.Round(natural * _letterSpacingPercent / 100.0)",
                         "(int)Math.Round(cellWidth * (_lineSpacingPercent - 100) / 100.0)"):
            if fragment not in cell:
                problems.append("F15 MeasureCell 没有把间距算进格子：竖排的间距不会生效（缺 %s）"
                                % fragment)

    # 数据层：字段、夹取、保存、读取。
    for fragment, label in (
            ("public int LineSpacingPercent = DefaultLineSpacingPercent;", "行距字段"),
            ("public int LetterSpacingPercent = DefaultLetterSpacingPercent;", "字距字段"),
            ("LineSpacingPercent = Math.Max(MinLineSpacingPercent,", "行距夹取"),
            ("LetterSpacingPercent = Math.Max(MinLetterSpacingPercent,", "字距夹取"),
            ('lines.Add(prefix + "LineSpacingPercent="', "行距保存"),
            ('lines.Add(prefix + "LetterSpacingPercent="', "字距保存"),
            ('case "LineSpacingPercent":', "行距读取"),
            ('case "LetterSpacingPercent":', "字距读取")):
        # 预设也会写同名的键，所以这几条必须限定在卡片的那两个方法体里 —— 查整个文件的话，
        # 把卡片那一份删掉、预设那一份还在，断言照样「过」。
        if fragment not in storage:
            problems.append("F15 缺少%s：%s" % (label, fragment))
    # 卡片那一份单独确认：预设的键不算。
    append_card = safe_body(storage,
        "private static void AppendCard(List<string> lines, string prefix, CardState card)",
        problems, "F15 AppendCard")
    if append_card is not None:
        for fragment, label in (('prefix + "LineSpacingPercent="', "行距保存"),
                                ('prefix + "LetterSpacingPercent="', "字距保存"),
                                ('prefix + "TextOutlinePercent="', "描边保存")):
            if fragment not in append_card:
                problems.append("F15 卡片没有保存%s" % label)
    apply_card = safe_body(storage,
        "private static bool ApplyCardField(CardState card, string key, string value)",
        problems, "F15 ApplyCardField")
    if apply_card is not None:
        for fragment, label in (('case "LineSpacingPercent":', "行距读取"),
                                ('case "LetterSpacingPercent":', "字距读取"),
                                ('case "TextOutlinePercent":', "描边读取")):
            if fragment not in apply_card:
                problems.append("F15 卡片没有读取%s" % label)

    # 菜单：两个子菜单、各四档、都要有标题。
    for key, label in (("LineSpacing", "行距"), ("LetterSpacing", "字距")):
        if 'CreateMenuItem("%s")' % key not in forms:
            problems.append("F15 找不到「%s」子菜单" % label)
        if 'case "%s":' % key not in forms:
            problems.append("F15 「%s」没有标题：语言切换后是一条空白" % label)
    for percent in (90, 100, 120, 150):
        if 'case "LineSpacing%d":' % percent not in forms:
            problems.append("F15 行距的 %d%% 档没有标题" % percent)
    for percent in (0, 10, 25, 50):
        if 'case "LetterSpacing%d":' % percent not in forms:
            problems.append("F15 字距的 %d%% 档没有标题" % percent)
    if "MarkCurrent(state.LineSpacing, _card.LineSpacingPercent);" not in forms:
        problems.append("F15 打开菜单时没有标出行距当前档")
    if "MarkCurrent(state.LetterSpacing, _card.LetterSpacingPercent);" not in forms:
        problems.append("F15 打开菜单时没有标出字距当前档")

    # 改间距会改变文字需要的地方，所以必须重新适应并让卡片能长大。
    for name, view_field in (("SetLineSpacing", "_quoteView.LineSpacingPercent"),
                             ("SetLetterSpacing", "_quoteView.LetterSpacingPercent")):
        body = safe_body(forms, "internal void %s(int percent)" % name, problems, "F15 " + name)
        if body is None: continue
        for fragment in (view_field, "FitQuoteFont();",
                         "GrowCardToText();", "BackOffToFittingSize();"):
            if fragment not in body:
                problems.append("F15 %s 缺少 %s：改了间距之后不会重新适应" % (name, fragment))

    # F16 逐字绘制（右对齐的根因）与文字描边。
    #
    # 「右对齐有问题」：LineWidth 把字距算进去了，但 TextRenderer 画整串时**根本不加字距** ——
    # 量出来的比画出来的宽，右对齐（和居中）就整体左偏，偏的正好是累加的字距。
    # 修法是逐字绘制：画出来的宽度于是严格等于 LineWidth，对齐落在算出来的位置。
    paint_h = safe_body(forms, "private void PaintHorizontal(Graphics graphics)", problems,
                        "F16 PaintHorizontal")
    if paint_h is not None:
        if "TextRenderer.DrawText(graphics, line," in paint_h:
            problems.append("F16 PaintHorizontal 又整串画一行：字距只进测量不进绘制，"
                            "右对齐会左偏，字距也根本看不见")
        if "for (int position = 0; position < line.Length; position++)" not in paint_h:
            problems.append("F16 PaintHorizontal 没有逐字绘制")
        if "x += Advance(character, Font) + letterGap;" not in paint_h:
            problems.append("F16 逐字绘制时没有按「字宽 + 字距」前进：画出来的宽度和 LineWidth 对不上")
        if "DrawGlyph(" not in paint_h:
            problems.append("F16 PaintHorizontal 没有走 DrawGlyph：描边不会生效")

    # 描边：八方向、每个距离都画一遍，最后才是文字本身。
    glyph = safe_body(forms, "private void DrawGlyph(Graphics graphics, string glyph, int x, int y, int stroke)",
                      problems, "F16 DrawGlyph")
    if "private void DrawGlyph(" not in forms:
        problems.append("F16 找不到 DrawGlyph")
    elif glyph is not None:
        if "for (int distance = 1; distance <= stroke; distance++)" not in glyph:
            problems.append("F16 DrawGlyph 没有按距离逐圈画：粗描边中间会漏")
        if "for (int dx = -1; dx <= 1; dx++)" not in glyph:
            problems.append("F16 DrawGlyph 不是八方向：描边会缺角")
        if "TextRenderer.DrawText(graphics, glyph, Font, new Point(x, y), ForeColor, GlyphFlags);" \
                not in glyph:
            problems.append("F16 DrawGlyph 最后没有用文字色把字形画上去")

    # 描边是块的一部分（它长在字形外面），所以量测和格子都要留出位置。
    measure_h = safe_body(forms, "private Size MeasureHorizontal(Font font, int viewWidth)",
                          problems, "F16 MeasureHorizontal")
    if measure_h is not None and "BlockPadding(font)" not in measure_h:
        problems.append("F16 MeasureHorizontal 没有把描边/阴影算进块：开了之后文字会被裁")
    cell = safe_body(forms, "private void MeasureCell(Font font, out int cellWidth, out int cellHeight)",
                     problems, "F16 MeasureCell")
    if cell is not None and "BlockPadding(font)" not in cell:
        problems.append("F16 MeasureCell 没有把描边/阴影算进格子：竖排开之后会挤在一起")
    if "int TextAreaWidth(int viewWidth, Font font)" not in forms:
        problems.append("F16 找不到 TextAreaWidth（描边要占掉两侧的位置）")

    # 数据层。
    for fragment, label in (
            ("public int TextOutlinePercent = DefaultTextOutlinePercent;", "描边字段"),
            ("TextOutlinePercent = Math.Max(0, Math.Min(MaxTextOutlinePercent, TextOutlinePercent));",
             "描边夹取"),
            ("public static int OutlineColorArgbFor(CardState card)", "描边颜色")):
        if fragment not in storage:
            problems.append("F16 缺少%s：%s" % (label, fragment))
    # 描边的保存要限定在**卡片**那一份方法体里：预设也写同名的键，查整个文件的话，
    # 把卡片那一份删掉、预设那一份还在，断言照样「过」。
    append_card = safe_body(storage, "private static void AppendCard(",
                            problems, "F16 AppendCard")
    if append_card is not None and 'prefix + "TextOutlinePercent="' not in append_card:
        problems.append("F16 卡片没有保存描边：重启就丢")

    # 菜单与接线。
    if 'CreateMenuItem("TextOutline")' not in forms:
        problems.append("F16 找不到「文字描边」子菜单")
    if 'case "TextOutline":' not in forms:
        problems.append("F16 「文字描边」没有标题")
    for percent in (0, 4, 8, 12):
        if 'case "TextOutline%d":' % percent not in forms:
            problems.append("F16 描边的 %d%% 档没有标题" % percent)
    if "MarkCurrent(state.Outline, _card.TextOutlinePercent);" not in forms:
        problems.append("F16 打开菜单时没有标出描边当前档")
    if "_quoteView.TextOutlinePercent = card.TextOutlinePercent;" not in forms:
        problems.append("F16 构造函数没有把描边交给视图：重启后描边会消失")
    if "_quoteView.OutlineColor = SafeColor(CardState.OutlineColorArgbFor(_card), Color.White);" \
            not in forms:
        problems.append("F16 外观变化时没有重算描边颜色：换了背景色描边会不对")
    outline_setter = safe_body(forms, "internal void SetTextOutline(int percent)", problems,
                               "F16 SetTextOutline")
    if outline_setter is not None:
        for fragment in ("FitQuoteFont();", "GrowCardToText();", "BackOffToFittingSize();"):
            if fragment not in outline_setter:
                problems.append("F16 SetTextOutline 缺少 %s：开了描边之后不会重新适应" % fragment)

    # F17 字体预设。
    #
    # 预设只带「字体长相」，**不带竖排与对齐** —— 那两样属于卡片的排版方式，
    # 竖排卡片和横排卡片应该能共用一个预设。
    if "internal sealed class TextPreset" not in storage:
        problems.append("F17 找不到 TextPreset")
    else:
        apply_to = safe_body(storage, "public void ApplyTo(CardState card)", problems,
                             "F17 TextPreset.ApplyTo")
        if apply_to is not None:
            for fragment in ("card.FontName = FontName;", "card.FontStyleValue = FontStyleValue;",
                             "card.AutoFitMaxFontSize = AutoFitMaxFontSize;",
                             "card.TextColorArgb = TextColorArgb;",
                             "card.LineSpacingPercent = LineSpacingPercent;",
                             "card.LetterSpacingPercent = LetterSpacingPercent;",
                             "card.TextOutlinePercent = TextOutlinePercent;",
                             "card.Clamp();"):
                if fragment not in apply_to:
                    problems.append("F17 ApplyTo 没有把 %s 带过去" % fragment)
            # 预设不该动这两样。
            for forbidden in ("card.VerticalText", "card.TextAlignHorizontal", "card.TextAlignVertical"):
                if forbidden in apply_to:
                    problems.append("F17 ApplyTo 动了 %s：竖排/对齐不该由预设决定" % forbidden)
            # 自动适应的预设不能把手选字号塞给卡片，否则那张卡片再也不自动适应了。
            if "card.ManualFontSize = AutoFitText ? 0f : ManualFontSize;" not in apply_to:
                problems.append("F17 ApplyTo 没有在自动适应时清掉手选字号："
                                "套用之后那张卡片会一直用固定字号")
        from_card = safe_body(storage, "public static TextPreset FromCard(string name, CardState card)",
                              problems, "F17 TextPreset.FromCard")
        if from_card is not None and "name.Trim()" not in from_card:
            problems.append("F17 FromCard 没有去掉名字两端的空格")

    # 存盘：PresetN.* 与 CardN.* 是同一套解析方式，不能互相吃掉。
    for fragment, label in (
            ("public readonly List<TextPreset> Presets = new List<TextPreset>();", "预设列表"),
            ('lines.Add("PresetCount="', "预设计数"),
            ("private static void AppendPreset(List<string> lines, string prefix, TextPreset preset)",
             "预设保存"),
            ("private static bool TryParsePresetKey(string key, out int index, out string field)",
             "预设键解析"),
            ("private static void ApplyPresetField(TextPreset preset, string field, string value)",
             "预设读取"),
            ("if (TryParsePresetKey(entry[0], out index, out field))", "预设键分支")):
        if fragment not in storage:
            problems.append("F17 缺少%s：%s" % (label, fragment))
    # 名字为空的预设没法在菜单里显示，不该被当成预设。
    if "if (preset.Name.Length == 0) continue;" not in storage:
        problems.append("F17 读取时没有跳过没有名字的预设：菜单里会出现一条空白")

    # 菜单与接线。
    for key, label in (("PresetMenu", "字体预设"), ("PresetSave", "保存当前文字样式"),
                       ("PresetDelete", "删除预设"), ("PresetEmpty", "（还没有预设）")):
        if 'case "%s":' % key not in forms:
            problems.append("F17 「%s」没有标题：语言切换后是一条空白" % label)
    if '_presetMenu = CreateMenuItem("PresetMenu");' not in forms:
        problems.append("F17 找不到「字体预设」菜单项")
    if "RefreshPresetMenu();" not in forms:
        problems.append("F17 打开菜单时没有重建预设列表")
    refresh = safe_body(forms, "private void RefreshPresetMenu()", problems, "F17 RefreshPresetMenu")
    if refresh is not None:
        if "stale.Dispose();" not in refresh:
            problems.append("F17 重建预设列表时没有释放旧条目：长会话会越堆越多")
        # 应用列表和删除列表各一份，都要用预设名当标题：只数一次的话，把其中一处改成
        # 固定标题、另一处还在，断言照样「过」。
        if refresh.count("item.Text = name;") != 2:
            problems.append("F17 预设条目没有用预设自己的名字当标题")
    apply_preset = safe_body(forms, "internal void ApplyPreset(string name)", problems,
                             "F17 ApplyPreset")
    if apply_preset is not None:
        for fragment in ("_app.Presets[existing].ApplyTo(_card);", "_quoteView.LineSpacingPercent",
                         "_quoteView.TextOutlinePercent", "FitQuoteFont();", "GrowCardToText();",
                         "BackOffToFittingSize();"):
            if fragment not in apply_preset:
                problems.append("F17 ApplyPreset 缺少 %s：套用预设之后不会重新适应" % fragment)

    # F18 行首禁则、两端对齐、卡片不透明度、文字阴影。
    #
    # 禁则：贪心换行会把「，」挤到行首，甚至断出一行只有一个「。」。规则是断点往前挪一个
    # （CSS 的 line-break: strict 就是这么做的），不是让标点悬挂出界 —— 悬挂会让块比文字区
    # 还宽，而卡片的下限是照块量的。
    if "CannotStartLine" not in forms or "CannotEndLine" not in forms:
        problems.append("F18 TextLines 没有行首/行尾禁则的字符集")
    split = safe_body(forms, "public static List<string> Split(string text, int available, "
                             "Func<char, int> advance)", problems, "F18 TextLines.Split")
    if split is not None:
        if "StartsLineIllegally(normalized[end])" not in split:
            problems.append("F18 断行时没有检查「下一行会不会以收尾标点开头」")
        if "EndsLineIllegally(normalized[end - 1])" not in split:
            problems.append("F18 断行时没有检查「本行会不会以起首标点结尾」")
        if "end--;" not in split:
            problems.append("F18 禁则没有把断点往前挪")
        if "end > index + 1" not in split:
            problems.append("F18 禁则没有下限：整行都是标点时会断出空行")

    # 两端对齐：横轴第四档，绘制时拉伸非末行，块本身不变。
    if "public const int AlignJustify = 3;" not in storage:
        problems.append("F18 CardState 少了 AlignJustify")
    if "public bool Justify" not in forms:
        problems.append("F18 QuoteView 少了 Justify")
    if "_quoteView.Justify = _card.TextAlignHorizontal == CardState.AlignJustify;" not in forms:
        problems.append("F18 没有把 AlignJustify 交给视图")
    if ("alignment == CardState.AlignNear || alignment == CardState.AlignJustify" not in forms):
        problems.append("F18 ToStringAlignment 没有把 AlignJustify 当成 Near：块会跑到中间去")
    paint_h = safe_body(forms, "private void PaintHorizontal(Graphics graphics)", problems,
                        "F18 PaintHorizontal")
    if paint_h is not None:
        if "_justify && index < visible - 1" not in paint_h:
            problems.append("F18 两端对齐没有跳过最后一行：短末行会被拉满整张卡片")
        if "gapShare + (position < gapRemainder ? 1 : 0)" not in paint_h:
            problems.append("F18 两端对齐没有把余数摊到前面几个间隙：右边会差一像素")
    if "state.Justify.Enabled = !_card.VerticalText;" not in forms:
        problems.append("F18 竖排时「两端对齐」没有置灰：点了不会有任何反应")

    # 卡片不透明度。
    for fragment, label in (
            ("public int CardOpacityPercent = DefaultCardOpacityPercent;", "不透明度字段"),
            ("CardOpacityPercent = Math.Max(MinCardOpacityPercent,", "不透明度夹取"),
            ('lines.Add(prefix + "CardOpacityPercent="', "不透明度保存"),
            ('case "CardOpacityPercent":', "不透明度读取")):
        if fragment not in storage:
            problems.append("F18 缺少%s：%s" % (label, fragment))
    if "Opacity = Math.Max(0.05," not in forms:
        problems.append("F18 ApplyAppearance 没有用卡片的不透明度：窗口还是写死 0.97")
    if "state.Opacity.Enabled = !_card.TransparentBackground;" not in forms:
        problems.append("F18 透明背景时「不透明度」没有置灰：两个都改分层窗口，会互相打架")

    # 文字阴影。
    for fragment, label in (
            ("public int TextShadowPercent = DefaultTextShadowPercent;", "阴影字段"),
            ("TextShadowPercent = Math.Max(0, Math.Min(MaxTextShadowPercent, TextShadowPercent));",
             "阴影夹取"),
            ('lines.Add(prefix + "TextShadowPercent="', "阴影保存"),
            ('case "TextShadowPercent":', "阴影读取"),
            ("public static int ShadowColorArgbFor(CardState card)", "阴影颜色")):
        if fragment not in storage:
            problems.append("F18 缺少%s：%s" % (label, fragment))
    if "int BlockPadding(Font font)" not in forms:
        problems.append("F18 找不到 BlockPadding（描边与阴影要一起算进块）")
    if "return 2 * (OutlinePixels(font) + ShadowPixels(font));" not in forms:
        problems.append("F18 BlockPadding 没有把描边和阴影都算进去")
    glyph = safe_body(forms, "private void DrawGlyph(Graphics graphics, string glyph, int x, int y, "
                             "int stroke)", problems, "F18 DrawGlyph")
    if glyph is not None:
        if "_shadowColor" not in glyph:
            problems.append("F18 DrawGlyph 没有画阴影")
        # F16 有一条注入会把「最后画文字本身」那一行删掉，那时这里没有 ForeColor 可比 ——
        # 那种情况由 F16 报，这里跳过，不要自己抛异常。
        if "_shadowColor" in glyph and "ForeColor" in glyph and \
                glyph.index("_shadowColor") > glyph.index("ForeColor"):
            problems.append("F18 DrawGlyph 把阴影画在文字之后：会盖住文字本身")
    if 'CreateMenuItem("TextShadow")' not in forms:
        problems.append("F18 找不到「文字阴影」子菜单")
    if 'CreateMenuItem("CardOpacity")' not in forms:
        problems.append("F18 找不到「不透明度」子菜单")
    for percent in (0, 3, 6, 10):
        if 'case "TextShadow%d":' % percent not in forms:
            problems.append("F18 阴影的 %d%% 档没有标题" % percent)
    for percent in (100, 97, 90, 80, 70):
        if 'case "CardOpacity%d":' % percent not in forms:
            problems.append("F18 不透明度的 %d%% 档没有标题" % percent)
    if "MarkCurrent(state.Shadow, _card.TextShadowPercent);" not in forms:
        problems.append("F18 打开菜单时没有标出阴影当前档")
    if "MarkCurrent(state.Opacity, _card.CardOpacityPercent);" not in forms:
        problems.append("F18 打开菜单时没有标出不透明度当前档")

    # F19 自由不透明度、背景图显示方式、圆角与边框。
    #
    # 不透明度：菜单里五档只是常用值，不是全部 —— 自定义那一项才是「自由调整」。
    if "public const int MinCardOpacityPercent = 10;" not in storage:
        problems.append("F19 不透明度下限不是 10：自定义输入会被夹在一个很窄的范围里")
    if 'CreateMenuItem("CardOpacityCustom")' not in forms:
        problems.append("F19 找不到「不透明度 → 自定义…」")
    if 'case "CardOpacityCustom":' not in forms:
        problems.append("F19 「不透明度 → 自定义…」没有标题")
    if "opacityCustomItem.Click += delegate { AskCardOpacity(); };" not in forms:
        problems.append("F19 「不透明度 → 自定义…」没有接上处理函数：点了什么都不会发生")
    ask_opacity = safe_body(forms, "private void AskCardOpacity()", problems, "F19 AskCardOpacity")
    if ask_opacity is not None:
        # 输入框只把字符串原样交回，范围校验必须自己做 —— 否则 1000 会被当成合法值。
        for fragment in ("CardState.MinCardOpacityPercent", "CardState.MaxCardOpacityPercent",
                         "SetCardOpacity(percent);"):
            if fragment not in ask_opacity:
                problems.append("F19 AskCardOpacity 缺少 %s：输入的值不会被校验或不会生效" % fragment)

    # 背景图显示方式：五种，每种走不同的绘制路径。
    for name in ("ImageLayoutCover", "ImageLayoutContain", "ImageLayoutStretch",
                 "ImageLayoutTile", "ImageLayoutCenter"):
        if "public const int %s = " % name not in storage:
            problems.append("F19 缺少 CardState.%s" % name)
    # 五种显示方式搬到了 DrawBackgroundPicture —— 缓存和「卡片太大不缓存」两条路共用它。
    paint_bg = safe_body(forms, "private void DrawBackgroundPicture(Graphics graphics, Rectangle area)",
                         problems, "F19 DrawBackgroundPicture")
    if paint_bg is not None:
        for fragment in ("case CardState.ImageLayoutContain:", "case CardState.ImageLayoutStretch:",
                         "case CardState.ImageLayoutTile:", "case CardState.ImageLayoutCenter:",
                         "FitRectangle(area, _backgroundImage.Size)",
                         "new TextureBrush(_backgroundImage, WrapMode.Tile)",
                         "CoverRectangle(area, _backgroundImage.Size)"):
            if fragment not in paint_bg:
                problems.append("F19 OnPaintBackground 缺少 %s：这一档显示方式没有实现" % fragment)
    # 「适应」是把图片缩到装得下，「铺满」是缩到盖住 —— 两个取最大值/最小值，写反了
    # 就变成「适应」反而裁掉图片。
    fit = safe_body(forms, "private static Rectangle FitRectangle(Rectangle bounds, Size imageSize)",
                    problems, "F19 FitRectangle")
    if fit is not None and "Math.Min((double)bounds.Width / imageSize.Width," not in fit:
        problems.append("F19 FitRectangle 没有取两轴比例的最小值：那成了「铺满」，会裁掉图片")
    cover = safe_body(forms, "private static Rectangle CoverRectangle(Rectangle bounds, Size imageSize)",
                      problems, "F19 CoverRectangle")
    if cover is not None and "Math.Max((double)bounds.Width / imageSize.Width," not in cover:
        problems.append("F19 CoverRectangle 没有取两轴比例的最大值：那成了「适应」，会留下空白边")
    if "state.ImageLayout.Enabled = !string.IsNullOrEmpty(_card.BackgroundImagePath);" not in forms:
        problems.append("F19 没有背景图时「显示方式」没有置灰：点了不会有任何变化")

    # 圆角与边框。
    if "public const int MaxCornerRadius = 40;" not in storage:
        problems.append("F19 圆角上限不是 40（内边距 12 能盖住的最大整数值）")
    radius_for = safe_body(forms,
        "public static int CornerRadiusFor(int requested, int width, int height)",
        problems, "F19 CornerRadiusFor")
    if radius_for is not None:
        # 两个上限缺一不可：请求值超过上限、以及卡片比圆角还小（四个弧会重叠成蝴蝶结）。
        if "Math.Min(CardState.MaxCornerRadius, requested)" not in radius_for:
            problems.append("F19 CornerRadiusFor 没有夹在 CardState.MaxCornerRadius 里")
        if "Math.Min(radius, limit)" not in radius_for:
            problems.append("F19 CornerRadiusFor 没有按卡片尺寸收窄：小卡片上四个弧会重叠")
    # 文件里有两个 OnPaint（卡片一个、别的控件一个），按方法体里的 `_locatingCard` 挑卡片那个。
    paint = body_by_marker(forms, "protected override void OnPaint(PaintEventArgs e)",
                           "_locatingCard")
    if paint is None:
        problems.append("F19 找不到卡片自己的 OnPaint（方法体里应当提到 _locatingCard）")
    else:
        for fragment in ("_card.BorderWidth", "_card.BorderColorArgb",
                         "CardSizeLimits.CornerRadiusFor(_card.CornerRadius, Width, Height)"):
            if fragment not in paint:
                problems.append("F19 OnPaint 没有读 %s：边框设置不会生效" % fragment)
        if "if (borderWidth <= 0f && !_locatingCard) return;" not in paint:
            problems.append("F19 OnPaint 在边框粗细为 0 时仍然画：会留下一条 1 像素的线")
    corner_setter = safe_body(forms, "internal void SetCornerRadius(int radius)", problems,
                              "F19 SetCornerRadius")
    if corner_setter is not None and "UpdateRoundedRegion();" not in corner_setter:
        problems.append("F19 SetCornerRadius 没有重切窗口区域：新的圆角会被旧区域裁掉")
    if "state.Border.Enabled = !_card.TransparentBackground;" not in forms:
        problems.append("F19 透明背景时「边框」没有置灰：透明卡片不画边框，点了没有反应")
    for fragment, label in (
            ("public int CornerRadius = DefaultCornerRadius;", "圆角字段"),
            ("public int BorderWidth = DefaultBorderWidth;", "边框宽度字段"),
            ("public int BorderColorArgb = DefaultBorderColorArgb;", "边框颜色字段"),
            ("public int BackgroundImageLayout = DefaultImageLayout;", "显示方式字段"),
            ("CornerRadius = Math.Max(MinCornerRadius, Math.Min(MaxCornerRadius, CornerRadius));",
             "圆角夹取"),
            ("BorderWidth = Math.Max(MinBorderWidth, Math.Min(MaxBorderWidth, BorderWidth));",
             "边框夹取"),
            ("BackgroundImageLayout = Math.Max(MinImageLayout,", "显示方式夹取"),
            ('lines.Add(prefix + "CornerRadius="', "圆角保存"),
            ('lines.Add(prefix + "BorderWidth="', "边框保存"),
            ('lines.Add(prefix + "BorderColorArgb="', "边框颜色保存"),
            ('lines.Add(prefix + "BackgroundImageLayout="', "显示方式保存"),
            ('case "CornerRadius":', "圆角读取"),
            ('case "BorderWidth":', "边框读取"),
            ('case "BorderColorArgb":', "边框颜色读取"),
            ('case "BackgroundImageLayout":', "显示方式读取")):
        if fragment not in storage:
            problems.append("F19 缺少%s：%s" % (label, fragment))
    for key, label in (("ImageLayout", "背景图片显示方式"), ("CornerRadius", "圆角"),
                       ("BorderWidth", "边框"), ("BorderColor", "边框颜色")):
        if 'CreateMenuItem("%s")' % key not in forms:
            problems.append("F19 找不到「%s」菜单" % label)
        if 'case "%s":' % key not in forms:
            problems.append("F19 「%s」没有标题" % label)
    for value in (0, 1, 2, 3, 4):
        if 'case "ImageLayout%d":' % value not in forms:
            problems.append("F19 显示方式的第 %d 档没有标题" % value)
    for value in (0, 8, 18, 28, 40):
        if 'case "CornerRadius%d":' % value not in forms:
            problems.append("F19 圆角的 %d 像素档没有标题" % value)
    for value in (0, 1, 2, 3):
        if 'case "BorderWidth%d":' % value not in forms:
            problems.append("F19 边框的 %d 像素档没有标题" % value)
    for fragment, label in (
            ("MarkCurrent(state.ImageLayout, _card.BackgroundImageLayout);", "显示方式"),
            ("MarkCurrent(state.Corner, _card.CornerRadius);", "圆角"),
            ("MarkCurrent(state.Border, _card.BorderWidth);", "边框")):
        if fragment not in forms:
            problems.append("F19 打开菜单时没有标出%s当前档" % label)
    # 「重置外观」把这几项一起还原，否则重置之后卡片还是歪的。
    reset = safe_body(forms, "private void ResetAppearance()", problems, "F19 ResetAppearance")
    if reset is not None:
        for fragment in ("_card.CornerRadius = CardState.DefaultCornerRadius;",
                         "_card.BorderWidth = CardState.DefaultBorderWidth;",
                         "_card.BackgroundImageLayout = CardState.DefaultImageLayout;"):
            if fragment not in reset:
                problems.append("F19 重置外观没有还原 %s" % fragment)

    # F20 两处缓存：字号宽度表按字号分桶，背景图缩放结果按尺寸缓存。
    #
    # 缓存的两种失效方式是「永远不命中」和「过期了还在用」。前者让性能白费，后者让画面错。
    # 两条都在这里盯着。
    for fragment, label in (
            ("private sealed class GlyphMetrics", "字号度量桶"),
            ("private readonly Dictionary<float, GlyphMetrics> _metricsBySize =", "按字号索引的表"),
            ("float key = (float)Math.Round(font.SizeInPoints, 2);", "按字号取值"),
            ("private string _metricsFamily;", "字体族记录"),
            ("_metricsFamily != family || _metricsStyle != style", "换字体族就清表"),
            ("if (_metricsBySize.Count >= MaxCachedFontSizes) _metricsBySize.Clear();", "表的容量上限")):
        if fragment not in forms:
            problems.append("F20 缺少%s：%s" % (label, fragment))
    # 按 Font 对象做键就等于没缓存：字号搜索每个候选都 new 一个 Font，每步都会清表。
    if "_metricsFont" in forms:
        problems.append("F20 字号表又按 Font 对象做键了：搜索每换一档就会清空整张表")
    advance = safe_body(forms, "private int Advance(char character, Font font)", problems,
                        "F20 Advance")
    if advance is not None:
        if "GlyphMetrics metrics = MetricsFor(font);" not in advance:
            problems.append("F20 Advance 没有从按字号的表里取度量")
        if "metrics.Advances.TryGetValue(character, out width)" not in advance:
            problems.append("F20 Advance 没有查表：每个字符每次都要重新量")
    line_height = safe_body(forms, "private int NaturalLineHeight(Font font)", problems,
                            "F20 NaturalLineHeight")
    if line_height is not None and "return MetricsFor(font).LineHeight;" not in line_height:
        problems.append("F20 NaturalLineHeight 没有走同一张表：行高和字宽会各量各的")

    # 背景图缓存。
    if "private Bitmap _backgroundCache;" not in forms:
        problems.append("F20 找不到背景图缓存字段")
    ensure = safe_body(forms, "private bool EnsureBackgroundCache()", problems,
                       "F20 EnsureBackgroundCache")
    if ensure is not None:
        # 键的四个分量缺一不可：换尺寸、换显示方式、换底色、换图片，任何一种都得重建。
        for fragment, label in (
                ("_backgroundCacheImage == _backgroundImage", "图片换了要重建"),
                ("_backgroundCacheSize == size", "尺寸变了要重建"),
                ("_backgroundCacheLayout == _card.BackgroundImageLayout", "显示方式变了要重建"),
                ("_backgroundCacheColor == BackColor", "底色变了要重建"),
                ("if ((long)size.Width * size.Height > MaxCachedBackgroundPixels)", "超大卡片不缓存"),
                ("DrawBackgroundPicture(graphics, new Rectangle(Point.Empty, size));",
                 "缓存里画的必须是同一套绘制")):
            if fragment not in ensure:
                problems.append("F20 背景缓存缺少「%s」：%s" % (label, fragment))
    release = safe_body(forms, "private void ReleaseBackgroundCache()", problems,
                        "F20 ReleaseBackgroundCache")
    if release is not None and "_backgroundCache.Dispose();" not in release:
        problems.append("F20 ReleaseBackgroundCache 没有释放位图：每次换尺寸漏一张")
    # 关窗时也要放掉，否则最后一张会留到进程结束。
    if ("            if (_backgroundImage != null) _backgroundImage.Dispose();\n"
            "            ReleaseBackgroundCache();") not in forms:
        problems.append("F20 卡片销毁时没有释放背景缓存")
    paint_bg = safe_body(forms, "protected override void OnPaintBackground(PaintEventArgs e)",
                         problems, "F20 OnPaintBackground")
    if paint_bg is not None:
        if "if (EnsureBackgroundCache())" not in paint_bg:
            problems.append("F20 OnPaintBackground 没有先取缓存")
        if "e.Graphics.DrawImageUnscaled(_backgroundCache, 0, 0);" not in paint_bg:
            problems.append("F20 OnPaintBackground 没有把缓存贴上去：又回到每次重绘都缩放")
        # 缓存不可用时必须还有一条直绘的路，否则超大卡片会画不出背景。
        if "DrawBackgroundPicture(e.Graphics, ClientRectangle);" not in paint_bg:
            problems.append("F20 OnPaintBackground 没有「不缓存」的兜底：超大卡片会没有背景")

    # F21 拖动吸附 + 菜单结构。
    #
    # 吸附：两轴各自判断（对齐纵向不能同时把卡片横着拽走），只有最近的落点赢，
    # 并且要能「脱开」——所以自由位置和可见位置是两个变量。
    if "internal static class CardSnap" not in forms:
        problems.append("F21 找不到 CardSnap（拖动吸附）")
    if "public const int Distance = 8;" not in forms:
        problems.append("F21 吸附距离不是 8：太大会让卡片拖不动，太小等于没有")
    snap = safe_body(forms,
        "public static Point Adjust(Point proposed, Size size, Rectangle[] neighbours,",
        problems, "F21 CardSnap.Adjust")
    if snap is not None:
        for fragment, label in (
                ("int dx = OffsetFor(proposed.X, size.Width, verticals, distance);", "横向单独算"),
                ("int dy = OffsetFor(proposed.Y, size.Height, horizontals, distance);", "纵向单独算"),
                ("AddLines(verticals, horizontals, screen);", "桌面也算目标"),
                ("return new Point(proposed.X + dx, proposed.Y + dy);", "两轴结果各归各")):
            if fragment not in snap:
                problems.append("F21 CardSnap.Adjust 缺少「%s」：%s" % (label, fragment))
    lines_of = safe_body(forms,
        "private static void AddLines(List<int> verticals, List<int> horizontals, Rectangle rect)",
        problems, "F21 CardSnap.AddLines")
    if lines_of is not None:
        if "if (rect.Width <= 0 || rect.Height <= 0) return;" not in lines_of:
            problems.append("F21 零尺寸矩形没有跳过：隐藏的卡片会把别的卡吸到它身上")
        if "verticals.Add(rect.Left + rect.Width / 2);" not in lines_of:
            problems.append("F21 没有把中心线当目标：两张卡只能边对边，不能中心对中心")
    offset = safe_body(forms,
        "private static int OffsetFor(int origin, int extent, List<int> targets, int distance)",
        problems, "F21 CardSnap.OffsetFor")
    if offset is not None:
        if "for (int edge = 0; edge < 3; edge++)" not in offset:
            problems.append("F21 只比了卡片的一条边：中心线和另一条边都没参与")
        if "if (magnitude >= bestMagnitude) continue;" not in offset:
            problems.append("F21 没有取最近的那个：远处的目标会盖掉近处的")

    # 拖动路径必须真的用上它，而且自由位置与可见位置要分开，否则吸住之后拖不开。
    drag = safe_body(forms, "private void AttachDragHandlers(Control control)", problems,
                     "F21 AttachDragHandlers")
    if drag is not None:
        for fragment, label in (
                ("_dragFree = Location;", "拖动开始时记下自由位置"),
                ("_dragFree.Offset(current.X - _dragOrigin.X, current.Y - _dragOrigin.Y);",
                 "自由位置跟着指针走"),
                ("CardSnap.Adjust(_dragFree, Size, _snapTargets,", "用吸附算可见位置"),
                ("Screen.FromControl(this).WorkingArea", "把桌面工作区当目标")):
            if fragment not in drag:
                problems.append("F21 拖动处理器缺少「%s」：%s" % (label, fragment))
        if "Left += current.X - _dragOrigin.X;" in drag:
            problems.append("F21 拖动还在直接累加位移：吸住之后会拖不开")
    neighbours = safe_body(forms, "private Rectangle[] NeighbourBounds()", problems,
                           "F21 NeighbourBounds")
    if neighbours is not None:
        if "if (other == this || other.IsDisposed || !other.Visible) continue;" not in neighbours:
            problems.append("F21 邻居列表没有排除自己或不可见的卡片")

    # 菜单结构：一个 480 行的构建方法拆成了三个。
    if "private sealed class MenuState" not in forms:
        problems.append("F21 找不到 MenuState：菜单项又变回一堆局部变量了")
    for signature, label in (
            ("private ToolStripMenuItem BuildCardSettingsMenu(MenuState state)", "卡片设置"),
            ("private ToolStripMenuItem BuildTextSettingsMenu(MenuState state)", "文字设置"),
            ("private void RefreshMenuState(ContextMenuStrip menu, MenuState state)", "菜单状态刷新")):
        if signature not in forms:
            problems.append("F21 找不到 %s 的构建方法：%s" % (label, signature))
    menu = safe_body(forms,
        "private ContextMenuStrip BuildMenu(out ToolStripMenuItem startupItem, "
        "out ToolStripMenuItem topMostItem)", problems, "F21 BuildMenu")
    if menu is not None:
        # 拆开的证据：主菜单只调用两个构建方法，自己不再建那些条目。
        if "menu.Items.Add(BuildCardSettingsMenu(state));" not in menu:
            problems.append("F21 主菜单没有挂上卡片设置")
        if "menu.Items.Add(BuildTextSettingsMenu(state));" not in menu:
            problems.append("F21 主菜单没有挂上文字设置")
        if 'CreateMenuItem("TextLarger")' in menu or 'CreateMenuItem("CardOpacity")' in menu:
            problems.append("F21 子菜单的条目又建回主菜单里了：拆分没有生效")
        # 结构断言：主菜单只负责骨架。这条是防止它慢慢长回去。
        if menu.count("\n") > 200:
            problems.append("F21 BuildMenu 又长到 %d 行：它应该只搭骨架" % menu.count("\n"))
    refresh = safe_body(forms, "private void RefreshMenuState(ContextMenuStrip menu, MenuState state)",
                        problems, "F21 RefreshMenuState")
    if refresh is not None:
        for fragment in ("state.AutoMax.Enabled = _card.AutoFitText;",
                         "state.Opacity.Enabled = !_card.TransparentBackground;",
                         "state.Border.Enabled = !_card.TransparentBackground;",
                         "ApplyMenuLanguage(menu.Items);"):
            if fragment not in refresh:
                problems.append("F21 刷新菜单状态时缺少 %s" % fragment)
    if "menu.Opening += delegate { RefreshMenuState(menu, state); };" not in forms:
        problems.append("F21 打开菜单时没有调用 RefreshMenuState")

    # F22 两处「边界情况没有守」的修复：圆角路径与写盘。
    #
    # 圆角路径原来把半径直接乘二丢给四个 AddArc。两个从菜单就能到达的情况会出问题：
    # 半径 0（直角）退化成四个重合点；半径大于被画的矩形（小卡片 + 最大圆角 + 粗边框）
    # 会让上边两个弧互相覆盖，路径在角上翻折。
    rounded = safe_body(forms, "internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)",
                        problems, "F22 RoundedRectangle")
    if rounded is None:
        problems.append("F22 找不到 RoundedRectangle（或它不是 internal，自检就调不到）")
    else:
        if "int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));" not in rounded:
            problems.append("F22 圆角直径没有夹在矩形内：小卡片上两个弧会重叠，路径翻折")
        if "if (diameter <= 0)" not in rounded:
            problems.append("F22 没有单独处理半径 0：四个零尺寸的弧不是直角")
        if "path.AddRectangle(bounds);" not in rounded:
            problems.append("F22 半径 0 时没有退回矩形路径")

    # 写盘：两个文件都是整份重写，原地写会在写到一半时留下截断的文件。
    if "internal static class AtomicFile" not in storage:
        problems.append("F22 找不到 AtomicFile")
    else:
        atomic = safe_body(storage, "private static void Replace(string temp, string path)",
                           problems, "F22 AtomicFile.Replace")
        if atomic is not None and "File.Replace(temp, path, null);" not in atomic:
            problems.append("F22 没有用 File.Replace 换文件：换名不是一步完成的")
    if "AtomicFile.WriteLines(_path, lines.ToArray());" not in storage:
        problems.append("F22 状态文件没有走原子写")
    if "AtomicFile.WriteLines(_path, normalized.ToArray());" not in storage:
        problems.append("F22 内容库没有走原子写")
    if "File.WriteAllLines(_path" in storage or "File.WriteAllText(_path" in storage:
        problems.append("F22 还有地方在直接原地写 _path：写到一半会留下截断的文件")

    return problems


# --------------------------------------------------- 反向验证：注入缺陷

INJECTIONS = [
    ("大一点退回只改起点",
     FORMS, "larger.Click += delegate { SetManualFontSize(CurrentFontSize() + 2.0f); };",
     "larger.Click += delegate { _card.FontAdjustment += 2; _app.Save(); FitQuoteFont(); };", "F1"),
    ("小一点不接管字号",
     FORMS, "smaller.Click += delegate { SetManualFontSize(CurrentFontSize() - 2.0f); };",
     "smaller.Click += delegate { _card.FontAdjustment -= 2; _app.Save(); FitQuoteFont(); };", "F1"),
    ("设为最小写成偏移 -24",
     FORMS, "smallest.Click += delegate { SetManualFontSize(CardState.MinManualFontSize); };",
     "smallest.Click += delegate { _card.FontAdjustment = -24; _app.Save(); FitQuoteFont(); };", "F1"),
    ("恢复默认字号不还回自动适应",
     FORMS, "                _card.AutoFitText = true;\n                _quoteFontSize = -1.0f;\n                _app.Save();\n                FitQuoteFont();",
     "                _quoteFontSize = -1.0f;\n                _app.Save();\n                FitQuoteFont();", "F1"),
    ("SetManualFontSize 不关自动适应",
     FORMS, "            _card.AutoFitText = false;\n            _card.ManualFontSize = Math.Max(CardState.MinManualFontSize,",
     "            _card.ManualFontSize = Math.Max(CardState.MinManualFontSize,", "F2"),
    ("SetManualFontSize 不失效字号缓存",
     # 锚点要带上下文：SetManualFontSize / SetFontStyle / SetAutoFitMax 三个方法都以同样四行结尾。
     FORMS, "                Math.Min(CardState.MaxManualFontSize, size));\n"
            "            _quoteFontSize = -1.0f;\n"
            "            _app.Save();\n"
            "            FitQuoteFont();\n"
            "            GrowCardToText();",
     "                Math.Min(CardState.MaxManualFontSize, size));\n"
     "            _app.Save();\n"
     "            FitQuoteFont();\n"
     "            GrowCardToText();", "F2"),
    ("FitQuoteFont 不认手动字号",
     FORMS, "if (!_card.AutoFitText && _card.ManualFontSize > 0f)",
     "if (false)", "F3"),
    ("FitQuoteFont 丢了下限档",
     FORMS, "                        if (_quoteView.FitsBlock(floorFont)) chosen = QuoteView.SmallestFontSize;",
     "", "F3"),
    ("GrowCardToText 改成会缩小",
     FORMS, "                Size = next;", "                Size = wanted;", "F4"),
    ("GrowCardToText 撞到上限时退回面积最小的卡片（卡片会停住）",
     FORMS, "return CardSizeLimits.FlowFitFor(measure, vertical, across);",
     "return CardSizeLimits.MinimumFor(measure, vertical);", "F4"),
    # F4b：字体对话框把字号丢掉 —— 「字体大小设置对话框里超过一定数字大小就失效」。
    ("ApplyPickedFont 丢掉对话框里的字号",
     FORMS, "float size = picked.SizeInPoints;", "float size = 12.0f;", "F4b"),
    ("ApplyPickedFont 选了新字号却不走 SetManualFontSize",
     # 锚点必须带上下文：`SetManualFontSize(size);` 在 ApplyManualFontSize 里也有一份，
     # 只写这一行的话注入会打到那个 wrapper 上，ApplyPickedFont 一点没变，断言一条都报不出。
     FORMS, "                SetManualFontSize(size);\n            }",
     "                FitQuoteFont();\n            }", "F4b"),
    ("ApplyPickedFont 不区分「换了字号」和「只换了字体」",
     FORMS, "if (Math.Abs(size - before) >= 0.05f)", "if (false)", "F4b"),
    ("ApplyPickedFont 丢掉「只换字体就重新适应」这一支",
     FORMS, "                FitQuoteFont();\n            }\n        }\n\n        private void ChooseBackgroundImage()",
     "            }\n        }\n\n        private void ChooseBackgroundImage()", "F4b"),
    ("ChooseQuoteFont 用固定 12 磅当对话框初值",
     FORMS, "using (Font current = QuoteFont(CurrentFontSize()))",
     "using (Font current = QuoteFont(12.0f))", "F4b"),
    ("ChooseQuoteFont 不把选中的字体交给 ApplyPickedFont",
     FORMS, "ApplyPickedFont(dialog.Font);", "", "F4b"),
    ("撑不下时没把字号退回装得下的一档",
     # 锚点要带上下文：三个字号相关的 setter 都以 `BackOffToFittingSize();` 结尾，
     # 只写这一行的话注入会打到 SetFontStyle 上，SetManualFontSize 一点没变。
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
     "            GrowCardToText();", "F4"),
    ("勾回自动适应时不清手动字号",
     FORMS, "                if (_card.AutoFitText) _card.ManualFontSize = 0f;\n", "", "F5"),
    ("CardState 少了手动字号字段",
     # TextPreset 也有同名字段，靠紧跟其后的那一行注释区分。
     STORAGE, "        public float ManualFontSize = 0f;\n"
              "        // How large automatic fitting may go", "", "F6"),
    ("Clamp 不夹手动字号",
     STORAGE, "            ManualFontSize = ManualFontSize <= 0f ? 0f :\n                Math.Max(MinManualFontSize, Math.Min(MaxManualFontSize, ManualFontSize));",
     "            ManualFontSize = ManualFontSize;", "F6"),
    ("Save 漏写 ManualFontSize",
     STORAGE, '            lines.Add(prefix + "ManualFontSize=" +\n                card.ManualFontSize.ToString(CultureInfo.InvariantCulture));\n',
     "", "F7"),
    ("Load 漏读 Slot",
     STORAGE, '                case "Slot": if (int.TryParse(value, out number)) card.Slot = number; return true;\n',
     "", "F7"),
    ("Save 漏写 NextCardSlot",
     STORAGE, '            lines.Add("NextCardSlot=" + NextCardSlot.ToString(CultureInfo.InvariantCulture));\n',
     "", "F7"),
    ("内容库文件名不再按槽位",
     STORAGE, 'return Path.Combine(dataDirectory, "content-" + slot.ToString(CultureInfo.InvariantCulture) + ".txt");',
     'return Path.Combine(dataDirectory, "quotes.txt");', "F8"),
    ("ForCard 不走 CardContentPath",
     STORAGE, "            return new QuoteRepository(CardContentPath(dataDirectory, card), state);",
     '            return new QuoteRepository(Path.Combine(dataDirectory, "quotes.txt"), state);', "F8"),
    ("迁移改成复制（旧文件留着）",
     STORAGE, "                    File.Move(legacy, target);",
     "                    File.Copy(legacy, target, false);", "F8"),
    ("启动时不迁移旧共享库",
     PROGRAM, "                QuoteRepository.MigrateSharedLibrary(dataDirectory, state);\n", "", "F9"),
    ("CardManager 退回共享仓库",
     FORMS, "        private readonly string _dataDirectory;",
     "        private readonly string _dataDirectory;\n        private readonly QuoteRepository _repository;",
     "F9"),
    ("AddForm 不再按卡片建仓库",
     FORMS, "            QuoteRepository repository = QuoteRepository.ForCard(_state, _dataDirectory, card);",
     "            QuoteRepository repository = QuoteRepository.ForCard(_state, _dataDirectory, _state.Cards[0]);",
     "F9"),
    ("编辑器标题不带卡片名",
     FORMS, '            Text = cardLabel.Length == 0 ? T("编辑内容库", "Edit content library") :\n                T("编辑内容库 · " + cardLabel, "Edit content library · " + cardLabel);',
     '            Text = T("编辑内容库", "Edit content library");', "F10"),
    ("背景图退回共用文件名",
     FORMS, '"background-" + _card.Slot.ToString(CultureInfo.InvariantCulture) + extension);',
     '"background" + extension);', "F11"),
    ("删旧背景图不看别的卡片",
     FORMS, "                foreach (CardState other in _app.Cards)\n                {\n                    if (other == _card || string.IsNullOrEmpty(other.BackgroundImagePath)) continue;\n                    if (string.Equals(Path.GetFullPath(other.BackgroundImagePath), candidate,\n                        StringComparison.OrdinalIgnoreCase)) return;\n                }\n",
     "", "F11"),
    # F14：自动字号上限、字号输入框、加粗/斜体。
    ("自动字号上限退回写死的 29 磅",
     FORMS, "Math.Min(_card.AutoFitMaxFontSize,", "Math.Min(29.0f,", "F14"),
    ("CardState 不夹自动字号上限",
     STORAGE, "AutoFitMaxFontSize = Math.Max(MinAutoFitMaxFontSize,\n"
              "                Math.Min(MaxManualFontSize, AutoFitMaxFontSize));", "", "F14"),
    ("保存时不写自动字号上限",
     STORAGE, '            lines.Add(prefix + "AutoFitMaxFontSize=" +\n'
              '                card.AutoFitMaxFontSize.ToString(CultureInfo.InvariantCulture));\n',
     "", "F14"),
    ("读取时不认自动字号上限",
     STORAGE, '                case "AutoFitMaxFontSize":\n'
              '                    if (float.TryParse(value, NumberStyles.Float, '
              'CultureInfo.InvariantCulture, out size))\n'
              '                        card.AutoFitMaxFontSize = size;\n'
              '                    return true;\n',
     "", "F14"),
    ("字号输入框不校验范围",
     FORMS, "size < CardState.MinManualFontSize || size > CardState.MaxManualFontSize",
     "size < 0f || size > 0f", "F14"),
    ("SetFontStyle 改了字形却不重新适应",
     FORMS, "            _quoteFontSize = -1.0f;\n"
            "            _app.Save();\n"
            "            FitQuoteFont();\n"
            "            GrowCardToText();\n"
            "            BackOffToFittingSize();\n"
            "        }\n\n"
            "        // The ceiling automatic fitting shrinks from.",
     "            _quoteFontSize = -1.0f;\n"
     "            _app.Save();\n"
     "            FitQuoteFont();\n"
     "        }\n\n"
     "        // The ceiling automatic fitting shrinks from.", "F14"),
    ("SetFontStyle 关一档会连带清掉另一档",
     FORMS, "value & ~(int)style", "0", "F14"),
    ("自动适应关掉时上限菜单不置灰",
     FORMS, "state.AutoMax.Enabled = _card.AutoFitText;", "", "F14"),
    ("加粗菜单项没有标题",
     FORMS, 'case "TextBold":', 'case "TextBoldGone":', "F14"),
    ("字号…菜单项没有接上处理函数",
     FORMS, "exactSize.Click += delegate { AskFontSize(); };", "", "F14"),
    # F15：行距与字距。
    ("横排换行退回 GDI 的 WordBreak",
     FORMS, "            List<string> lines = BuildLines(Font, Width);",
     "            List<string> lines = new List<string> { Text };", "F15"),
    ("量测与绘制用了两套换行",
     FORMS, "            List<string> lines = BuildLines(font, viewWidth);",
     "            List<string> lines = new List<string> { Text };", "F15"),
    ("竖排不把字距算进格子",
     FORMS, "            cellHeight = Math.Max(1, cellHeight +\n"
            "                (int)Math.Round(natural * _letterSpacingPercent / 100.0) + stroke);",
     "            cellHeight = Math.Max(1, cellHeight + stroke);", "F15"),
    ("竖排不把行距算进列宽",
     FORMS, "            cellWidth = Math.Max(1, cellWidth +\n"
            "                (int)Math.Round(cellWidth * (_lineSpacingPercent - 100) / 100.0) + stroke);",
     "            cellWidth = Math.Max(1, cellWidth + stroke);", "F15"),
    ("保存时不写行距",
     STORAGE, '            lines.Add(prefix + "LineSpacingPercent=" +\n'
              '                card.LineSpacingPercent.ToString(CultureInfo.InvariantCulture));\n',
     "", "F15"),
    ("读取时不认字距",
     STORAGE, '                case "LetterSpacingPercent":\n'
              '                    if (int.TryParse(value, out number)) '
              'card.LetterSpacingPercent = number;\n'
              '                    return true;\n',
     "", "F15"),
    ("行距不夹在范围内",
     STORAGE, "LineSpacingPercent = Math.Max(MinLineSpacingPercent,\n"
              "                Math.Min(MaxLineSpacingPercent, LineSpacingPercent));", "", "F15"),
    ("行距档位没有标题",
     FORMS, 'case "LineSpacing120":', 'case "LineSpacing120Gone":', "F15"),
    ("打开菜单时不标出当前行距",
     FORMS, "MarkCurrent(state.LineSpacing, _card.LineSpacingPercent);", "", "F15"),
    ("改字距之后不重新适应",
     FORMS, "            _quoteView.LetterSpacingPercent = _card.LetterSpacingPercent;\n"
            "            _app.Save();\n"
            "            _quoteFontSize = -1.0f;\n"
            "            FitQuoteFont();\n"
            "            GrowCardToText();\n"
            "            BackOffToFittingSize();",
     "            _quoteView.LetterSpacingPercent = _card.LetterSpacingPercent;\n"
     "            _app.Save();", "F15"),
    # F16：逐字绘制与描边。
    ("横排又退回整串绘制（右对齐会左偏）",
     FORMS, "                for (int position = 0; position < line.Length; position++)",
     "                for (int position = 0; position < 0; position++)", "F16"),
    ("逐字前进不带字距",
     FORMS, "                    x += Advance(character, Font) + letterGap;",
     "                    x += Advance(character, Font);", "F16"),
    ("描边不按距离逐圈画",
     # 横排与竖排各有一份，靠循环体里的坐标写法区分。
     FORMS, "                for (int distance = 1; distance <= stroke; distance++)\n"
            "                {\n"
            "                    for (int dx = -1; dx <= 1; dx++)\n"
            "                    {\n"
            "                        for (int dy = -1; dy <= 1; dy++)\n"
            "                        {\n"
            "                            if (dx == 0 && dy == 0) continue;\n"
            "                            TextRenderer.DrawText(graphics, glyph, Font,\n"
            "                                new Point(x + dx * distance, y + dy * distance), halo, GlyphFlags);",
     "                for (int distance = stroke; distance <= stroke; distance++)\n"
     "                {\n"
     "                    for (int dx = -1; dx <= 1; dx++)\n"
     "                    {\n"
     "                        for (int dy = -1; dy <= 1; dy++)\n"
     "                        {\n"
     "                            if (dx == 0 && dy == 0) continue;\n"
     "                            TextRenderer.DrawText(graphics, glyph, Font,\n"
     "                                new Point(x + dx * distance, y + dy * distance), halo, GlyphFlags);", "F16"),
    ("描边只画四个正方向",
     # 横排与竖排各有一份八方向循环，靠 `new Point(` 收尾区分。
     FORMS, "                    for (int dx = -1; dx <= 1; dx++)\n"
            "                    {\n"
            "                        for (int dy = -1; dy <= 1; dy++)\n"
            "                        {\n"
            "                            if (dx == 0 && dy == 0) continue;\n"
            "                            TextRenderer.DrawText(graphics, glyph, Font,\n"
            "                                new Point(",
     "                    for (int dx = 0; dx <= 1; dx++)\n"
     "                    {\n"
     "                        for (int dy = -1; dy <= 1; dy++)\n"
     "                        {\n"
     "                            if (dx == 0 && dy == 0) continue;\n"
     "                            TextRenderer.DrawText(graphics, glyph, Font,\n"
     "                                new Point(", "F16"),
    ("描边最后不画文字本身",
     FORMS, "            TextRenderer.DrawText(graphics, glyph, Font, new Point(x, y), ForeColor, GlyphFlags);",
     "", "F16"),
    ("量测不把描边算进块",
     FORMS, "            int padding = BlockPadding(font);\n"
            "            int available = TextAreaWidth(viewWidth, font);",
     "            int padding = 0;\n"
     "            int available = TextAreaWidth(viewWidth, font);", "F16"),
    ("竖排格子不把描边算进去",
     FORMS, "            int stroke = BlockPadding(font);\n"
            "            cellHeight = Math.Max(1, cellHeight +",
     "            int stroke = 0;\n"
     "            cellHeight = Math.Max(1, cellHeight +", "F16"),
    ("保存时不写描边",
     STORAGE, '            lines.Add(prefix + "TextOutlinePercent=" +\n'
              '                card.TextOutlinePercent.ToString(CultureInfo.InvariantCulture));\n',
     "", "F16"),
    ("读取时不认描边",
     STORAGE, '                case "TextOutlinePercent":\n'
              '                    if (int.TryParse(value, out number)) '
              'card.TextOutlinePercent = number;\n'
              '                    return true;\n',
     "", "F16"),
    ("描边不夹在范围内",
     STORAGE, "TextOutlinePercent = Math.Max(0, Math.Min(MaxTextOutlinePercent, TextOutlinePercent));",
     "", "F16"),
    ("描边档位没有标题",
     FORMS, 'case "TextOutline8":', 'case "TextOutline8Gone":', "F16"),
    ("打开菜单时不标出当前描边",
     FORMS, "MarkCurrent(state.Outline, _card.TextOutlinePercent);", "", "F16"),
    ("构造函数不把描边交给视图",
     FORMS, "            _quoteView.TextOutlinePercent = card.TextOutlinePercent;\n", "", "F16"),
    ("开描边之后不重新适应",
     FORMS, "            _quoteView.TextOutlinePercent = _card.TextOutlinePercent;\n"
            "            _app.Save();\n"
            "            _quoteFontSize = -1.0f;\n"
            "            FitQuoteFont();\n"
            "            GrowCardToText();\n"
            "            BackOffToFittingSize();",
     "            _quoteView.TextOutlinePercent = _card.TextOutlinePercent;\n"
     "            _app.Save();", "F16"),
    # F17：字体预设。
    ("预设动了竖排",
     STORAGE, "            card.TextOutlinePercent = TextOutlinePercent;",
     "            card.TextOutlinePercent = TextOutlinePercent;\n            card.VerticalText = false;",
     "F17"),
    ("自动适应的预设也塞手选字号",
     STORAGE, "card.ManualFontSize = AutoFitText ? 0f : ManualFontSize;",
     "card.ManualFontSize = ManualFontSize;", "F17"),
    ("预设名不去空格",
     STORAGE, "preset.Name = name == null ? \"\" : name.Trim();",
     "preset.Name = name == null ? \"\" : name;", "F17"),
    ("保存时不写预设",
     STORAGE, '            lines.Add("PresetCount=" + Presets.Count.ToString(CultureInfo.InvariantCulture));\n',
     "", "F17"),
    ("读取时不认预设键",
     STORAGE, "if (TryParsePresetKey(entry[0], out index, out field))",
     "if (false)", "F17"),
    ("读取时不跳过没有名字的预设",
     STORAGE, "                if (preset.Name.Length == 0) continue;\n", "", "F17"),
    ("重建预设列表时不释放旧条目",
     # 卡片列表里也有一模一样的清理循环，靠 `_presetMenu` 那一行区分。
     FORMS, "                ToolStripItem stale = _presetMenu.DropDownItems[0];\n"
            "                _presetMenu.DropDownItems.RemoveAt(0);\n"
            "                stale.Dispose();\n",
     "                ToolStripItem stale = _presetMenu.DropDownItems[0];\n"
     "                _presetMenu.DropDownItems.RemoveAt(0);\n", "F17"),
    ("打开菜单时不重建预设列表",
     FORMS, "            RefreshPresetMenu();\n", "", "F17"),
    ("预设条目不用预设名当标题",
     # 应用列表与删除列表各有一份，靠 item.Name 区分。
     FORMS, "                    item.Name = \"PresetEntry\" + index.ToString(CultureInfo.InvariantCulture);\n"
            "                    item.Text = name;",
     "                    item.Name = \"PresetEntry\" + index.ToString(CultureInfo.InvariantCulture);\n"
     "                    item.Text = \"预设\";", "F17"),
    ("套用预设之后不重新适应",
     FORMS, "            _app.Presets[existing].ApplyTo(_card);\n"
            "            // The preset's colour is used as saved. ApplyAppearance would run the readability\n"
            "            // guard first, which silently overrides a low-contrast choice - and \"the preset did not\n"
            "            // apply\" is exactly what that looks like. The derived copies are refreshed here so\n"
            "            // nothing is left showing the previous colour.\n"
            "            ForeColor = SafeColor(_card.TextColorArgb, Color.Black);\n"
            "            _quoteView.ForeColor = ForeColor;\n"
            "            _menuLabel.ForeColor = Color.FromArgb(175, ForeColor.R, ForeColor.G, ForeColor.B);\n"
            "            _quoteView.LineSpacingPercent = _card.LineSpacingPercent;\n"
            "            _quoteView.LetterSpacingPercent = _card.LetterSpacingPercent;\n"
            "            _quoteView.TextOutlinePercent = _card.TextOutlinePercent;\n"
            "            _quoteView.OutlineColor = SafeColor(CardState.OutlineColorArgbFor(_card), Color.White);\n"
            "            _app.Save();\n"
            "            _quoteFontSize = -1.0f;\n"
            "            FitQuoteFont();\n"
            "            GrowCardToText();\n"
            "            BackOffToFittingSize();",
     "            _app.Presets[existing].ApplyTo(_card);\n"
     "            _app.Save();", "F17"),
    ("预设菜单没有标题",
     FORMS, 'case "PresetMenu":', 'case "PresetMenuGone":', "F17"),
    # F18：禁则、两端对齐、不透明度、阴影。
    ("断行不检查行首禁则",
     FORMS, "                        StartsLineIllegally(normalized[end]);",
     "                        false;", "F18"),
    ("禁则没有下限（会断出空行）",
     FORMS, "                while (end > index + 1)", "                while (end > index)", "F18"),
    ("两端对齐不跳过最后一行",
     FORMS, "                if (_justify && index < visible - 1 && line.Length > 1)",
     "                if (_justify && line.Length > 1)", "F18"),
    ("两端对齐把余数丢掉",
     FORMS, "                        x += gapShare + (position < gapRemainder ? 1 : 0);",
     "                        x += gapShare;", "F18"),
    ("竖排时两端对齐不置灰",
     FORMS, "            state.Justify.Enabled = !_card.VerticalText;", "", "F18"),
    ("窗口不透明度写死",
     FORMS, "                Opacity = Math.Max(0.05,", "                Opacity = 0.97;", "F18"),
    ("透明背景时不透明度不置灰",
     FORMS, "            state.Opacity.Enabled = !_card.TransparentBackground;", "", "F18"),
    ("不透明度不夹在范围内",
     STORAGE, "            CardOpacityPercent = Math.Max(MinCardOpacityPercent,\n"
              "                Math.Min(MaxCardOpacityPercent, CardOpacityPercent));", "", "F18"),
    ("保存时不写不透明度",
     STORAGE, '            lines.Add(prefix + "CardOpacityPercent=" +\n'
              '                card.CardOpacityPercent.ToString(CultureInfo.InvariantCulture));\n',
     "", "F18"),
    ("BlockPadding 漏掉阴影",
     FORMS, "            return 2 * (OutlinePixels(font) + ShadowPixels(font));",
     "            return 2 * OutlinePixels(font);", "F18"),
    ("阴影画在文字之后",
     FORMS, "            int shadow = ShadowPixels(Font);\n"
            "            if (shadow > 0)\n"
            "                TextRenderer.DrawText(graphics, glyph, Font, new Point(x + shadow, y + shadow),\n"
            "                    _shadowColor, GlyphFlags);\n"
            "            if (stroke > 0)",
     "            if (stroke > 0)", "F18"),
    ("阴影档位没有标题",
     FORMS, 'case "TextShadow6":', 'case "TextShadow6Gone":', "F18"),
    ("不透明度档位没有标题",
     FORMS, 'case "CardOpacity90":', 'case "CardOpacity90Gone":', "F18"),
    ("打开菜单时不标出当前阴影",
     FORMS, "MarkCurrent(state.Shadow, _card.TextShadowPercent);", "", "F18"),
    # F19：自由不透明度、背景图显示方式、圆角与边框。
    ("不透明度下限退回 30",
     STORAGE, "public const int MinCardOpacityPercent = 10;",
     "public const int MinCardOpacityPercent = 30;", "F19"),
    ("自定义不透明度不校验范围",
     FORMS, "percent < CardState.MinCardOpacityPercent ||",
     "percent < 0 ||", "F19"),
    ("自定义不透明度没接上处理函数",
     FORMS, "opacityCustomItem.Click += delegate { AskCardOpacity(); };", "", "F19"),
    ("「适应」用了「铺满」的算法（会裁掉图片）",
     FORMS, "graphics.DrawImage(_backgroundImage, FitRectangle(area, _backgroundImage.Size));",
     "graphics.DrawImage(_backgroundImage, CoverRectangle(area, _backgroundImage.Size));", "F19"),
    ("平铺用了缩放绘制",
     FORMS, "using (Brush tiles = new TextureBrush(_backgroundImage, WrapMode.Tile))",
     "using (Brush tiles = new SolidBrush(BackColor))", "F19"),
    ("显示方式不夹在五档之内",
     STORAGE, "BackgroundImageLayout = Math.Max(MinImageLayout,\n"
              "                Math.Min(MaxImageLayout, BackgroundImageLayout));", "", "F19"),
    ("保存时不写显示方式",
     STORAGE, '            lines.Add(prefix + "BackgroundImageLayout=" +\n'
              '                card.BackgroundImageLayout.ToString(CultureInfo.InvariantCulture));\n',
     "", "F19"),
    ("没有背景图时显示方式不置灰",
     FORMS, "state.ImageLayout.Enabled = !string.IsNullOrEmpty(_card.BackgroundImagePath);", "", "F19"),
    ("圆角不重切窗口区域",
     FORMS, "            _card.CornerRadius = Math.Max(CardState.MinCornerRadius,\n"
            "                Math.Min(CardState.MaxCornerRadius, radius));\n"
            "            _app.Save();\n"
            "            UpdateRoundedRegion();",
     "            _card.CornerRadius = Math.Max(CardState.MinCornerRadius,\n"
     "                Math.Min(CardState.MaxCornerRadius, radius));\n"
     "            _app.Save();", "F19"),
    ("边框粗细为 0 时仍然画",
     FORMS, "if (borderWidth <= 0f && !_locatingCard) return;", "", "F19"),
    ("圆角不按卡片尺寸收窄（小卡片上会画成蝴蝶结）",
     FORMS, "            return Math.Min(radius, limit);", "            return radius;", "F19"),
    ("圆角上限大到内边距盖不住",
     STORAGE, "public const int MaxCornerRadius = 40;", "public const int MaxCornerRadius = 200;",
     "F19"),
    ("重置外观不还原圆角",
     FORMS, "            _card.CornerRadius = CardState.DefaultCornerRadius;\n", "", "F19"),
    ("边框档位没有标题",
     FORMS, 'case "BorderWidth2":', 'case "BorderWidth2Gone":', "F19"),
    ("打开菜单时不标出当前显示方式",
     FORMS, "MarkCurrent(state.ImageLayout, _card.BackgroundImageLayout);", "", "F19"),
    # F20：两处缓存。
    ("字号表按 Font 对象做键（等于没缓存）",
     FORMS, "float key = (float)Math.Round(font.SizeInPoints, 2);",
     "float key = font.GetHashCode();", "F20"),
    ("换字体族不清表（沿用旧字形的宽度）",
     FORMS, "_metricsFamily != family || _metricsStyle != style", "false", "F20"),
    ("字号表没有容量上限",
     FORMS, "            if (_metricsBySize.Count >= MaxCachedFontSizes) _metricsBySize.Clear();\n",
     "", "F20"),
    ("Advance 不查表",
     FORMS, "            if (metrics.Advances.TryGetValue(character, out width)) return width;\n",
     "", "F20"),
    ("背景图又回到每次重绘都缩放",
     FORMS, "                e.Graphics.DrawImageUnscaled(_backgroundCache, 0, 0);\n"
            "                return;",
     "                DrawBackgroundPicture(e.Graphics, ClientRectangle);\n"
     "                return;", "F20"),
    ("缓存不看尺寸（改大小后还是旧图）",
     FORMS, "                _backgroundCacheSize == size &&\n", "", "F20"),
    ("缓存不看显示方式",
     FORMS, "                _backgroundCacheLayout == _card.BackgroundImageLayout &&\n", "", "F20"),
    ("超大卡片也去分配位图",
     FORMS, "            if ((long)size.Width * size.Height > MaxCachedBackgroundPixels)\n",
     "            if (false)\n", "F20"),
    ("销毁卡片时不释放背景缓存",
     FORMS, "            if (_backgroundImage != null) _backgroundImage.Dispose();\n"
            "            ReleaseBackgroundCache();",
     "            if (_backgroundImage != null) _backgroundImage.Dispose();", "F20"),
    ("缓存不可用时没有直绘兜底",
     FORMS, "            DrawBackgroundPicture(e.Graphics, ClientRectangle);\n", "", "F20"),
    # F21：拖动吸附与菜单结构。
    ("拖动不再吸附",
     FORMS, "                Point snapped = CardSnap.Adjust(_dragFree, Size, _snapTargets,\n"
            "                    Screen.FromControl(this).WorkingArea, CardSnap.Distance);",
     "                Point snapped = _dragFree;", "F21"),
    ("拖动又直接累加位移（吸住就拖不开）",
     FORMS, "                _dragFree.Offset(current.X - _dragOrigin.X, current.Y - _dragOrigin.Y);",
     "                Left += current.X - _dragOrigin.X;", "F21"),
    ("吸附距离大到离谱",
     FORMS, "public const int Distance = 8;", "public const int Distance = 200;", "F21"),
    ("邻居列表把自己也算进去",
     FORMS, "if (other == this || other.IsDisposed || !other.Visible) continue;",
     "if (other.IsDisposed || !other.Visible) continue;", "F21"),
    ("零尺寸矩形也参与吸附",
     FORMS, "            if (rect.Width <= 0 || rect.Height <= 0) return;\n", "", "F21"),
    ("只比卡片的一条边（中心线不参与）",
     FORMS, "for (int edge = 0; edge < 3; edge++)", "for (int edge = 0; edge < 1; edge++)", "F21"),
    ("吸附取最远而不是最近",
     FORMS, "                    if (magnitude >= bestMagnitude) continue;", "", "F21"),
    ("主菜单没有挂上文字设置",
     FORMS, "            menu.Items.Add(BuildTextSettingsMenu(state));\n", "", "F21"),
    ("子菜单条目又建回主菜单",
     FORMS, "            menu.Items.Add(BuildTextSettingsMenu(state));\n",
     "            menu.Items.Add(BuildTextSettingsMenu(state));\n"
     "            menu.Items.Add(CreateMenuItem(\"TextLarger\"));\n", "F21"),
    ("打开菜单时不刷新状态",
     FORMS, "            menu.Opening += delegate { RefreshMenuState(menu, state); };\n", "", "F21"),
    ("刷新状态时漏掉不透明度置灰",
     FORMS, "            state.Opacity.Enabled = !_card.TransparentBackground;\n", "", "F21"),
    # F22：圆角路径与写盘。
    ("圆角直径不再夹在矩形内",
     FORMS, "int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));",
     "int diameter = radius * 2;", "F22"),
    ("半径 0 退回四个零尺寸弧",
     FORMS, "                path.AddRectangle(bounds);\n", "", "F22"),
    ("状态文件改回原地写",
     STORAGE, "AtomicFile.WriteLines(_path, lines.ToArray());",
     "File.WriteAllLines(_path, lines.ToArray(), new UTF8Encoding(false));", "F22"),
    ("内容库改回原地写",
     STORAGE, "AtomicFile.WriteLines(_path, normalized.ToArray());",
     "File.WriteAllLines(_path, normalized.ToArray(), new UTF8Encoding(false));", "F22"),
    ("换文件不用 File.Replace",
     STORAGE, "                File.Replace(temp, path, null);",
     "                File.Copy(temp, path, true);", "F22"),
    ("最小字号改成另一个字面量",
     FORMS, "public const float SmallestFontSize = CardState.MinManualFontSize;",
     "public const float SmallestFontSize = 8.0f;", "F12"),
]


def run_reverse():
    raw = {FORMS: open(FORMS, encoding="utf-8").read(),
           STORAGE: open(STORAGE, encoding="utf-8").read(),
           PROGRAM: open(PROGRAM, encoding="utf-8").read()}
    ok = 0
    bad = []
    for name, path, old, new, expect in INJECTIONS:
        if old not in raw[path]:
            bad.append("%s：注入点没找到（脚本与源码脱节）" % name)
            continue
        # 锚点必须唯一。`str.replace(old, new, 1)` 只替换**第一处**，锚点在两处都出现时
        # 注入会打到另一处，目标代码一点没变 —— 于是「注入后一条都没报出来」，看着像断言
        # 失灵，实际是样本打偏了。踩过一次（SetManualFontSize(size); 在 ApplyManualFontSize
        # 和 ApplyPickedFont 里各有一份）。
        hits = raw[path].count(old)
        if hits != 1:
            bad.append("%s：锚点在源码里出现 %d 次，注入会打到别处（补上上下文让它唯一）"
                       % (name, hits))
            continue
        mutated = {key: value for key, value in raw.items()}
        mutated[path] = raw[path].replace(old, new, 1)
        problems = check(strip_comments(mutated[FORMS]), strip_comments(mutated[STORAGE]),
                         strip_comments(mutated[PROGRAM]))
        if problems:
            ok += 1
            print("  [OK ] %-34s → 报出 %d 条（预期 %s）" % (name, len(problems), expect))
        else:
            bad.append("%s：注入后一条都没报出来" % name)
    for b in bad:
        print("  [!! ] %s" % b)
    return ok, len(INJECTIONS), bad


def main():
    forms = strip_comments(open(FORMS, encoding="utf-8").read())
    storage = strip_comments(open(STORAGE, encoding="utf-8").read())
    program = strip_comments(open(PROGRAM, encoding="utf-8").read())

    problems = check(forms, storage, program)

    if problems:
        print("== 字号与内容库静态断言：失败 ==")
        for p in problems:
            print("  [!!] " + p)
    else:
        print("== 字号与内容库静态断言：全部通过（13 组） ==")

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
