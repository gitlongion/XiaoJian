#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""源码级静态断言：便携式设计（数据目录 + 开机自启动）与「排版对齐方式」。

为什么需要它：macOS 上跑不了 exe，而这两件事都属于「编译通过、症状却很难发现」的类型——
    便携    数据到底落在哪里由 Program.cs 与 DataLocation 两处共同决定，只要有一处
            回落到 %LOCALAPPDATA%，程序就悄悄不再是便携的，而它照样能跑。
    自启动  注册表写入同样不会报错，只是把「便携」这个前提悄悄破坏了。
    对齐    对齐只作用于绘制；一旦它渗进测量，卡片的下限与字号就会跟着对齐一起变，
            这是本项目已经踩过一次的那类坑（见 verify_shrink_sim.py）。

断言全部作用在「剥掉注释之后」的源码上：本项目的注释习惯是把常量名/函数名写出来
解释为什么这么写，不剥注释会把「注释里提到过」当成「代码里写了」。

用法：
    python3 verify_portable_align.py                # 跑全部断言
    python3 verify_portable_align.py --reverse      # 注入缺陷做反向验证
    python3 verify_portable_align.py --src <目录>   # 指定源码目录
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
    保留字符串字面量是必须的：帮助文案里那些「• 」开头的说明不能被当成注释。
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

    # ---------------------------------------------------- P1 便携式数据目录

    if "DataLocation.Resolve(args)" not in program:
        problems.append("P1 Program.cs 没有用 DataLocation.Resolve 决定数据目录")
    if "DataLocation.AdoptLegacyData(dataDirectory)" not in program:
        problems.append("P1 Program.cs 没有把旧用户目录的内容带过来")
    if "DataLocation.Resolve(args)" in program and \
            program.index("DataLocation.Resolve(args)") > program.index("AppState.Load("):
        problems.append("P1 数据目录在读取配置之后才决定：配置会先落到别处")
    if "SpecialFolder.LocalApplicationData" in program:
        problems.append("P1 Program.cs 自己拼了用户目录：便携判断必须只有 DataLocation 一处")

    resolve = safe_body(storage, "public static string Resolve(string[] args)",
                        problems, "P1 DataLocation.Resolve")
    if resolve is not None:
        for fragment in ("ArgumentValue(args, ArgumentPrefix)",
                         "Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortableFolderName)",
                         "TryPrepare(portable)",
                         "SpecialFolder.LocalApplicationData"):
            if fragment not in resolve:
                problems.append("P1 Resolve 缺少 %s" % fragment)
        # 这一段跑在「异常兜底装好之前」，坏路径直接抛出去就是 Windows 的裸错误框，没有解释。
        if "TryPrepare(full)" not in resolve:
            problems.append("P1 Resolve 对 --data-dir 指定的目录没做可写检查："
                            "写不了的路径会把程序打挂，而不是退回默认位置")
        # 顺序就是优先级：显式指定 > 程序旁边 > 用户目录。
        if all(f in resolve for f in ("ArgumentValue(args, ArgumentPrefix)",
                                      "Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortableFolderName)",
                                      "SpecialFolder.LocalApplicationData")):
            order = [resolve.index("ArgumentValue(args, ArgumentPrefix)"),
                     resolve.index("Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortableFolderName)"),
                     resolve.index("SpecialFolder.LocalApplicationData")]
            if order != sorted(order):
                problems.append("P1 Resolve 的优先级顺序不对：应为 显式指定 > 程序旁 data > 用户目录")
        # 用户目录只能是兜底，必须出现在「便携目录不可写」的判断之后。
        if "TryPrepare(portable)" in resolve and \
                resolve.index("SpecialFolder.LocalApplicationData") < resolve.index("TryPrepare(portable)"):
            problems.append("P1 Resolve 先取用户目录才试便携目录：便携永远不会生效")

    prepare = safe_body(storage, "private static bool TryPrepare(string directory)",
                        problems, "P1 TryPrepare")
    if prepare is not None:
        if "FileMode.Create" not in prepare or "File.Delete(probe)" not in prepare:
            problems.append("P1 TryPrepare 没有真写一个探针文件："
                            "Program Files 能建目录却写不进文件，只建目录判断不出来")
        if "catch" not in prepare:
            problems.append("P1 TryPrepare 没有兜住异常：不可写的目录会直接抛出去")

    adopt = safe_body(storage, "internal static int Adopt(string directory, string legacy)",
                      problems, "P1 Adopt")
    if adopt is not None:
        for fragment in ("SameDirectory(directory, legacy)",
                         "if (File.Exists(Path.Combine(directory, StateFileName))) return 0;",
                         "if (!File.Exists(Path.Combine(legacy, StateFileName))) return 0;",
                         "File.Copy(source, destination, false);",
                         "if (File.Exists(destination)) continue;"):
            if fragment not in adopt:
                problems.append("P1 Adopt 缺少 %s" % fragment)

    # ---------------------------------------------------- P2 开机自启动不写注册表

    startup = safe_body(storage, "internal static class StartupManager",
                        problems, "P2 StartupManager")
    if startup is not None:
        for banned in ("CreateSubKey", "SetValue", "RegistryValueKind"):
            if banned in startup:
                problems.append("P2 StartupManager 还在写注册表（%s）：便携程序不该动注册表" % banned)
        for fragment in ("Environment.SpecialFolder.Startup",
                         "LegacyRegistryValue()",
                         "RemoveLegacyEntry()",
                         'Type.GetTypeFromProgID("WScript.Shell")'):
            if fragment not in startup:
                problems.append("P2 StartupManager 缺少 %s" % fragment)
        # 关掉自启动必须两个启动器都清掉，只删一个会留下仍然生效的那一个。
        disable = body_by_marker(startup, "public static void SetEnabled(bool enabled)", "RemoveLegacyEntry();")
        if disable is None:
            problems.append("P2 取不到 SetEnabled")
        else:
            for fragment in ("DeleteQuietly(ShortcutPath);", "DeleteQuietly(CommandPath);"):
                if fragment not in disable:
                    problems.append("P2 SetEnabled 关掉时没有清 %s" % fragment)
        # .cmd 兜底必须用 ANSI 写：cmd.exe 按 OEM 代码页读批处理，UTF-8 会让中文路径变成乱码。
        command = safe_body(storage, "private static void WriteCommandFile()", problems, "P2 WriteCommandFile")
        if command is not None:
            if "Encoding.Default" not in command:
                problems.append("P2 WriteCommandFile 没有用 Encoding.Default：中文路径在 cmd 下会乱码")
            if "Encoding.UTF8" in command:
                problems.append("P2 WriteCommandFile 用了 UTF-8：cmd.exe 读不了")

    # ---------------------------------------------------- P3 排版对齐

    for fragment in ("public const int AlignNear = 0;",
                     "public const int AlignCenter = 1;",
                     "public const int AlignFar = 2;",
                     "public int TextAlignHorizontal = AlignCenter;",
                     "public int TextAlignVertical = AlignCenter;"):
        if fragment not in storage:
            problems.append("P3 CardState 缺少 %s" % fragment)

    clamp = safe_body(storage, "public void Clamp()", problems, "P3 CardState.Clamp")
    if clamp is not None:
        # 横轴有第四档「两端对齐」，所以它夹到 AlignJustify；纵轴仍然只有三档。
        for fragment in ("TextAlignHorizontal = Math.Max(AlignNear, Math.Min(AlignJustify, TextAlignHorizontal));",
                         "TextAlignVertical = Math.Max(AlignNear, Math.Min(AlignFar, TextAlignVertical));"):
            if fragment not in clamp:
                problems.append("P3 Clamp 没有夹住对齐取值（%s）" % fragment[:32])

    append = safe_body(storage, "private static void AppendCard(List<string> lines, string prefix, CardState card)",
                       problems, "P3 AppendCard")
    if append is not None:
        for key in ("TextAlignHorizontal=", "TextAlignVertical="):
            if key not in append:
                problems.append("P3 AppendCard 没有写出 %s" % key)

    apply_field = safe_body(storage, "private static bool ApplyCardField(CardState card, string key, string value)",
                            problems, "P3 ApplyCardField")
    if apply_field is not None:
        for key in ("TextAlignHorizontal", "TextAlignVertical"):
            if ('case "%s"' % key) not in apply_field:
                problems.append("P3 ApplyCardField 没有读回 %s" % key)

    create = safe_body(storage, "public CardState CreateCard()", problems, "P3 CreateCard")
    if create is not None:
        for fragment in ("card.TextAlignHorizontal = last.TextAlignHorizontal;",
                         "card.TextAlignVertical = last.TextAlignVertical;"):
            if fragment not in create:
                problems.append("P3 新建卡片没有继承 %s" % fragment.split(".")[1])

    # 视图侧：属性 + 两条绘制路径都要用上它。
    for fragment in ("public StringAlignment HorizontalAlignment",
                     "public StringAlignment VerticalAlignment",
                     "private StringAlignment _horizontalAlignment = StringAlignment.Center;",
                     "private StringAlignment _verticalAlignment = StringAlignment.Center;"):
        if fragment not in forms:
            problems.append("P3 QuoteView 缺少 %s" % fragment)

    # 横排现在自己算每一行的位置（行距和字距要求接管换行），所以对齐不再走 TextFormatFlags，
    # 而是和竖排一样用 AlignOffset。两条轴各摆一次，并且不许写死居中。
    paint_h = safe_body(forms, "private void PaintHorizontal(Graphics graphics)", problems, "P3 PaintHorizontal")
    if paint_h is not None:
        if "AlignOffset(available - width, _horizontalAlignment)" not in paint_h:
            problems.append("P3 PaintHorizontal 的横轴没有按对齐方式摆每一行")
        if "AlignOffset(availableHeight - blockHeight, _verticalAlignment)" not in paint_h:
            problems.append("P3 PaintHorizontal 的纵轴没有按对齐方式摆文字块")
        if "TextFormatFlags.HorizontalCenter" in paint_h or "TextFormatFlags.VerticalCenter" in paint_h:
            problems.append("P3 PaintHorizontal 仍写死居中：对齐设置不会生效")
        # 换行必须走 TextLines，不能退回 GDI 自己的 WordBreak —— 否则行距/字距又没地方放了，
        # 而且「量出来的」和「画出来的」会变成两套规则。
        if "BuildLines(" not in paint_h:
            problems.append("P3 PaintHorizontal 没有用 BuildLines 换行：行距/字距会失效")

    align = safe_body(forms, "private static int AlignOffset(int free, StringAlignment alignment)",
                      problems, "P3 AlignOffset")
    if align is not None:
        for fragment in ("StringAlignment.Far", "StringAlignment.Center"):
            if fragment not in align:
                problems.append("P3 AlignOffset 缺少 %s 这一档" % fragment)

    paint_v = safe_body(forms, "private void PaintVertical(Graphics graphics)", problems, "P3 PaintVertical")
    if paint_v is not None:
        if "AlignOffset(availableWidth - blockWidth, _horizontalAlignment)" not in paint_v:
            problems.append("P3 PaintVertical 的横轴没有按对齐方式摆")
        if "AlignOffset(availableHeight - blockHeight, _verticalAlignment)" not in paint_v:
            problems.append("P3 PaintVertical 的纵轴没有按对齐方式摆")
        if "(Width - blockWidth) / 2" in paint_v or "(Height - blockHeight) / 2" in paint_v:
            problems.append("P3 PaintVertical 仍写死居中：竖排对齐设置不会生效")
        if "TextMargin + AlignOffset(" not in paint_v:
            problems.append("P3 PaintVertical 没有用与横排一致的内缩：两种排版对「左边」的理解会不一致")

    offset = safe_body(forms, "private static int AlignOffset(int free, StringAlignment alignment)",
                       problems, "P3 AlignOffset")
    if offset is not None:
        if "if (free <= 0) return 0;" not in offset:
            problems.append("P3 AlignOffset 没有兜住「文字比地方还大」：远对齐会把文字推出可视区")
        for fragment in ("StringAlignment.Far", "StringAlignment.Center"):
            if fragment not in offset:
                problems.append("P3 AlignOffset 缺少 %s 分支" % fragment)

    # 对齐只能作用于绘制：一旦渗进测量，卡片下限与字号会跟着对齐一起变。
    for signature, label in (
        ("public Size MeasureBlock(Font font, int viewWidth, int viewHeight)", "MeasureBlock"),
        ("private Size MeasureHorizontal(Font font, int viewWidth)", "MeasureHorizontal"),
        ("private List<string> BuildColumns(Font font, int viewWidth, int viewHeight, int cellHeight)", "BuildColumns"),
        ("public bool FitsBlock(Font font)", "FitsBlock"),
    ):
        body = safe_body(forms, signature, problems, "P3 " + label)
        if body is not None and "Alignment" in body:
            problems.append("P3 %s 里出现了对齐：对齐必须只影响绘制，否则卡片下限会跟着变" % label)

    # 菜单：六项都要接上，语言表里六项都要设勾。
    # 「文字设置」的构建代码在 BuildTextSettingsMenu 里（0.9.11 从 BuildMenu 拆出来的）。
    menu = safe_body(forms, "private ToolStripMenuItem BuildTextSettingsMenu(MenuState state)",
                     problems, "P3 BuildTextSettingsMenu")
    if menu is not None:
        for expect, what in (
            ("alignLeft.Click += delegate { SetTextAlignment(true, CardState.AlignNear); };", "左对齐"),
            ("alignCenter.Click += delegate { SetTextAlignment(true, CardState.AlignCenter); };", "水平居中"),
            ("alignRight.Click += delegate { SetTextAlignment(true, CardState.AlignFar); };", "右对齐"),
            ("alignTop.Click += delegate { SetTextAlignment(false, CardState.AlignNear); };", "顶部对齐"),
            ("alignMiddle.Click += delegate { SetTextAlignment(false, CardState.AlignCenter); };", "垂直居中"),
            ("alignBottom.Click += delegate { SetTextAlignment(false, CardState.AlignFar); };", "底部对齐"),
        ):
            if expect not in menu:
                problems.append("P3 「%s」没有接上 SetTextAlignment" % what)
        if "textSettingsMenu.DropDownItems.Add(alignMenu);" not in menu:
            problems.append("P3 「排版对齐」子菜单没有挂进「文字设置」")

    set_align = safe_body(forms, "internal void SetTextAlignment(bool horizontal, int alignment)",
                          problems, "P3 SetTextAlignment")
    if set_align is not None:
        for fragment in ("_card.TextAlignHorizontal = alignment;",
                         "_card.TextAlignVertical = alignment;",
                         "_app.Save();",
                         "PushTextAlignmentToView();",
                         "ApplyMenuLanguage(_menu.Items);"):
            if fragment not in set_align:
                problems.append("P3 SetTextAlignment 缺少 %s" % fragment)

    push = safe_body(forms, "private void PushTextAlignmentToView()", problems, "P3 PushTextAlignmentToView")
    if push is not None:
        for fragment in ("_quoteView.HorizontalAlignment = ToStringAlignment(_card.TextAlignHorizontal);",
                         "_quoteView.VerticalAlignment = ToStringAlignment(_card.TextAlignVertical);"):
            if fragment not in push:
                problems.append("P3 PushTextAlignmentToView 缺少 %s" % fragment[:48])
    # 构造时把三样都交给视图：竖排、行距、字距，最后推一次对齐。
    for fragment in ("_quoteView.Vertical = card.VerticalText;",
                     "_quoteView.LineSpacingPercent = card.LineSpacingPercent;",
                     "_quoteView.LetterSpacingPercent = card.LetterSpacingPercent;",
                     "_quoteView.TextOutlinePercent = card.TextOutlinePercent;",
                     "_quoteView.TextShadowPercent = card.TextShadowPercent;"):
        if fragment not in forms:
            problems.append("P3 构造函数没有把 %s 交给视图" % fragment.split(".")[1].split(" ")[0])
    # 查完整序列：`PushTextAlignmentToView();` 在别处也有调用（切对齐时），只查这一句的话
    # 把它从构造函数里删掉照样「过」。
    if ("            _quoteView.Vertical = card.VerticalText;\n"
            "            _quoteView.LineSpacingPercent = card.LineSpacingPercent;\n"
            "            _quoteView.LetterSpacingPercent = card.LetterSpacingPercent;\n"
            "            _quoteView.TextOutlinePercent = card.TextOutlinePercent;\n"
            "            _quoteView.OutlineColor = SafeColor(CardState.OutlineColorArgbFor(card), "
            "Color.White);\n"
            "            _quoteView.TextShadowPercent = card.TextShadowPercent;\n"
            "            _quoteView.ShadowColor = SafeColor(CardState.ShadowColorArgbFor(card), "
            "Color.Black);\n"
            "            PushTextAlignmentToView();") not in forms:
        problems.append("P3 构造函数没有把竖排/行距/字距/描边交给视图再推对齐："
                        "重启后卡片会画回默认")

    # 语言表：六个 case 都要在，并且都要把 Checked 设成「当前是不是它」。
    language = safe_body(forms, "private void ApplyMenuLanguage(ToolStripItemCollection items)",
                         problems, "P3 ApplyMenuLanguage")
    if language is not None:
        for name, field, value in (("AlignLeft", "TextAlignHorizontal", "AlignNear"),
                                   ("AlignCenter", "TextAlignHorizontal", "AlignCenter"),
                                   ("AlignRight", "TextAlignHorizontal", "AlignFar"),
                                   ("AlignTop", "TextAlignVertical", "AlignNear"),
                                   ("AlignMiddle", "TextAlignVertical", "AlignCenter"),
                                   ("AlignBottom", "TextAlignVertical", "AlignFar")):
            case = ('case "%s"' % name) in language
            if not case:
                problems.append("P3 语言表缺少 %s" % name)
                continue
            expect = "menuItem.Checked = _card.%s == CardState.%s;" % (field, value)
            if expect not in language:
                problems.append("P3 %s 没有把勾标设成当前值（%s）" % (name, expect))
        if 'case "TextAlign"' not in language:
            problems.append("P3 语言表缺少「排版对齐」子菜单标题")

    # 帮助文案要让用户找得到这两个新东西。
    if "排版对齐" not in forms:
        problems.append("P3 帮助文案没有提到「排版对齐」")
    if "data 文件夹" not in forms:
        problems.append("P3 帮助文案没有说明内容存在程序旁的 data 文件夹")
    if "写入 Windows 启动项" in forms:
        problems.append("P3 帮助文案还在说「写入 Windows 启动项」：已经改成启动文件夹了")

    # ---------------------------------------------------- P4 改名与升级路径
    #
    # 改名的风险全在「升级」这一侧：旧版写下的东西还得认得出来。所以这一组一半在钉新名字，
    # 一半在钉「旧名字必须原样留着」。任何一条反过来（把旧名字也改了、或者把新名字写成了
    # 旧名字），就地升级都会丢内容或者留下一个指向不存在 exe 的启动器。
    if "namespace XiaoJian" not in storage or "namespace XiaoJian" not in forms \
            or "namespace XiaoJian" not in program:
        problems.append("P4 命名空间没有全部改成 XiaoJian")
    if "自言卡" in forms or "自言卡" in program or "自言卡" in storage:
        problems.append("P4 还有「自言卡」这个旧名字留在源码里")
    if '"MyQuoteCard"' in forms:
        problems.append("P4 Forms.cs 里还有旧名字的字面量")

    # 旧名字：这几个是升级路径的一部分，必须原样留着。
    for fragment, why in (
            ('LegacyValueName = "MyQuoteCard"', "旧版写在注册表里的自启动项要认得出来"),
            ('PerUserFolderName = "MyQuoteCard"', "旧版的每用户数据目录要就地读得到"),
            ('LegacyShortcutName = "MyQuoteCard.lnk"', "旧版放在启动文件夹里的启动方式要认得出来"),
            ('LegacyCommandName = "MyQuoteCard.cmd"', "旧版的 .cmd 启动方式要认得出来"),
            ('LegacyExecutableName = "MyQuoteCard.exe"', "旧启动器指向的 exe 名要认得出来")):
        if fragment not in storage:
            problems.append("P4 缺少 %s —— %s" % (fragment, why))

    # 新名字：启动器、错误日志、互斥量、窗口标题。
    for fragment, why in (
            ('private const string ShortcutName = "XiaoJian.lnk";',
             "启动文件夹里的启动方式要用新名字"),
            ('private const string CommandName = "XiaoJian.cmd";', ".cmd 兜底要用新名字")):
        if fragment not in storage:
            problems.append("P4 缺少 %s —— %s" % (fragment, why))
    # 日志名有两个候选路径（exe 旁边 / 临时目录），两个都要用新名字：只改一个的话，
    # 出问题时写出来的文件还是旧名字，用户按提示去找会找不到。
    log_hits = program.count('"XiaoJian_error.log"')
    if log_hits != 2:
        problems.append("P4 错误日志名不对：两个候选路径都该用 XiaoJian_error.log，"
                        "现在只出现 %d 次" % log_hits)
    if "MyQuoteCard_error.log" in program:
        problems.append("P4 错误日志还在用旧名字")
    if '"Local\\\\XiaoJian.SingleInstance.1A7C4B92"' not in program:
        problems.append("P4 单实例互斥量没有用新名字")

    # 升级时把旧启动器接过来：留着不管，它指向的 MyQuoteCard.exe 改名后已经不存在了。
    carry = safe_body(storage, "public static void CarryOverLegacyLauncher()",
                      problems, "P4 CarryOverLegacyLauncher")
    if carry is not None:
        if "LegacyShortcutPath" not in carry or "LegacyCommandPath" not in carry:
            problems.append("P4 CarryOverLegacyLauncher 没有去看旧的两个启动器")
        if "SetEnabled(true)" not in carry:
            problems.append("P4 CarryOverLegacyLauncher 没有把旧启动器改写成启动本程序："
                            "开机自启动会因为改名而悄悄失效")
        if "DeleteQuietly(LegacyShortcutPath)" not in carry or \
                "DeleteQuietly(LegacyCommandPath)" not in carry:
            problems.append("P4 CarryOverLegacyLauncher 没有删掉旧启动器文件")
    if "StartupManager.CarryOverLegacyLauncher();" not in program:
        problems.append("P4 Program.cs 没有调用 CarryOverLegacyLauncher：升级后自启动会失效")
    # 认旧启动器时，旧 exe 名必须也算「本程序」，否则升级后菜单会显示成「未开启」。
    points = safe_body(storage, "private static bool PointsAtThisProgram(string target)",
                       problems, "P4 PointsAtThisProgram")
    if points is not None and "LegacyExecutableName" not in points:
        problems.append("P4 PointsAtThisProgram 不认旧 exe 名：升级后自启动会被显示成「未开启」")

    return problems


# ------------------------------------------------------------------- 反向验证

INJECTIONS = [
    # ---- 便携式数据目录 ----
    ("Program.cs 退回只认用户目录",
     PROGRAM, "            string dataDirectory = DataLocation.Resolve(args);",
     "            string dataDirectory = Path.Combine(Environment.GetFolderPath(\n"
     "                Environment.SpecialFolder.LocalApplicationData), \"MyQuoteCard\");", "P1"),
    ("不再带过来旧用户目录的内容",
     PROGRAM, "            DataLocation.AdoptLegacyData(dataDirectory);\n",
     "", "P1"),
    ("Resolve 直接返回用户目录",
     STORAGE, "            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortableFolderName);\n"
              "            if (TryPrepare(portable)) return portable;\n",
     "", "P1"),
    ("TryPrepare 只建目录不试写",
     STORAGE, "                string probe = Path.Combine(directory, WriteProbeName);\n"
              "                using (FileStream stream = new FileStream(probe, FileMode.Create, FileAccess.Write))\n"
              "                    stream.WriteByte(0);\n"
              "                File.Delete(probe);\n",
     "", "P1"),
    ("--data-dir 不做可写检查",
     STORAGE, "                    if (TryPrepare(full)) return full;",
     "                    return full;", "P1"),
    ("迁移去掉「已迁过」护栏",
     STORAGE, "                if (File.Exists(Path.Combine(directory, StateFileName))) return 0;\n",
     "", "P1"),
    ("迁移改成覆盖已有内容",
     STORAGE, "                        File.Copy(source, destination, false);",
     "                        File.Copy(source, destination, true);", "P1"),
    # ---- 开机自启动 ----
    ("自启动退回写注册表",
     STORAGE, "            RemoveLegacyEntry();\n            if (!enabled)",
     "            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(LegacyKeyPath))\n"
     "                key.SetValue(LegacyValueName, \"x\", RegistryValueKind.String);\n"
     "            RemoveLegacyEntry();\n            if (!enabled)", "P2"),
    ("关自启动只删一个启动器",
     STORAGE, "                DeleteQuietly(ShortcutPath);\n                DeleteQuietly(CommandPath);\n",
     "                DeleteQuietly(ShortcutPath);\n", "P2"),
    ("自启动不再清旧注册表项",
     STORAGE, "            RemoveLegacyEntry();\n            if (!enabled)",
     "            if (!enabled)", "P2"),
    (".cmd 兜底改用 UTF-8",
     STORAGE, "new StreamWriter(CommandPath, false, Encoding.Default)",
     "new StreamWriter(CommandPath, false, new UTF8Encoding(false))", "P2"),
    ("自启动不再用启动文件夹",
     STORAGE, "                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),\n"
              "                    ShortcutName);\n"
              "            }\n"
              "        }\n"
              "\n"
              "        private static string CommandPath\n"
              "        {\n"
              "            get\n"
              "            {\n"
              "                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),\n"
              "                    CommandName);\n"
              "            }\n"
              "        }\n"
              "\n"
              "        private static string LegacyShortcutPath\n"
              "        {\n"
              "            get\n"
              "            {\n"
              "                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),\n"
              "                    LegacyShortcutName);\n"
              "            }\n"
              "        }\n"
              "\n"
              "        private static string LegacyCommandPath\n"
              "        {\n"
              "            get\n"
              "            {\n"
              "                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),\n"
              "                    LegacyCommandName);\n",
     "                return Path.Combine(Path.GetTempPath(), ShortcutName);\n"
     "            }\n"
     "        }\n"
     "\n"
     "        private static string CommandPath\n"
     "        {\n"
     "            get\n"
     "            {\n"
     "                return Path.Combine(Path.GetTempPath(), CommandName);\n"
     "            }\n"
     "        }\n"
     "\n"
     "        private static string LegacyShortcutPath\n"
     "        {\n"
     "            get\n"
     "            {\n"
     "                return Path.Combine(Path.GetTempPath(), LegacyShortcutName);\n"
     "            }\n"
     "        }\n"
     "\n"
     "        private static string LegacyCommandPath\n"
     "        {\n"
     "            get\n"
     "            {\n"
     "                return Path.Combine(Path.GetTempPath(), LegacyCommandName);\n", "P2"),
    # ---- 改名与升级路径 ----
    ("旧数据目录名也跟着改了（就地升级读不到老内容）",
     STORAGE, 'PerUserFolderName = "MyQuoteCard";', 'PerUserFolderName = "XiaoJian";', "P4"),
    ("旧注册表值名也跟着改了（认不出老自启动项）",
     STORAGE, 'LegacyValueName = "MyQuoteCard";', 'LegacyValueName = "XiaoJian";', "P4"),
    ("旧启动器名跟着改了（认不出老启动器）",
     STORAGE, 'LegacyShortcutName = "MyQuoteCard.lnk";', 'LegacyShortcutName = "XiaoJian.lnk";',
     "P4"),
    ("旧 exe 名跟着改了（认不出老启动器指向的目标）",
     STORAGE, 'LegacyExecutableName = "MyQuoteCard.exe";', 'LegacyExecutableName = "XiaoJian.exe";',
     "P4"),
    ("新启动器还用旧名字",
     STORAGE, 'ShortcutName = "XiaoJian.lnk";', 'ShortcutName = "MyQuoteCard.lnk";', "P4"),
    ("升级时不接旧启动器",
     PROGRAM, "            StartupManager.CarryOverLegacyLauncher();\n", "", "P4"),
    ("接了旧启动器却不改写（自启动静默失效）",
     STORAGE, "            if (ours) SetEnabled(true);\n", "", "P4"),
    ("接了旧启动器却不删旧文件",
     STORAGE, "            DeleteQuietly(LegacyShortcutPath);\n"
              "            DeleteQuietly(LegacyCommandPath);\n        }\n",
     "        }\n", "P4"),
    ("不认旧 exe 名（升级后自启动显示成未开启）",
     STORAGE, "            string previous = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "
              "LegacyExecutableName);\n"
              "            return target.IndexOf(previous, StringComparison.OrdinalIgnoreCase) >= 0;",
     "            return false;", "P4"),
    ("错误日志还用旧名字",
     PROGRAM, 'Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XiaoJian_error.log"),\n'
              '                Path.Combine(Path.GetTempPath(), "XiaoJian_error.log")',
     'Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MyQuoteCard_error.log"),\n'
     '                Path.Combine(Path.GetTempPath(), "MyQuoteCard_error.log")', "P4"),
    ("单实例互斥量还用旧名字",
     PROGRAM, '"Local\\\\XiaoJian.SingleInstance.1A7C4B92"',
     '"Local\\\\MyQuoteCard.SingleInstance.1A7C4B92"', "P4"),
    # ---- 排版对齐 ----
    ("Clamp 不夹对齐取值",
     STORAGE, "            TextAlignHorizontal = Math.Max(AlignNear, Math.Min(AlignJustify, TextAlignHorizontal));\n"
              "            TextAlignVertical = Math.Max(AlignNear, Math.Min(AlignFar, TextAlignVertical));\n",
     "", "P3"),
    ("AppendCard 少写一个对齐键",
     STORAGE, '            lines.Add(prefix + "TextAlignVertical=" + card.TextAlignVertical.ToString(CultureInfo.InvariantCulture));\n',
     "", "P3"),
    ("ApplyCardField 少读一个对齐键",
     STORAGE, '                case "TextAlignHorizontal": if (int.TryParse(value, out number)) card.TextAlignHorizontal = number; return true;\n',
     "", "P3"),
    ("新建卡片不继承对齐",
     STORAGE, "                card.TextAlignHorizontal = last.TextAlignHorizontal;\n"
              "                card.TextAlignVertical = last.TextAlignVertical;\n",
     "", "P3"),
    ("横排退回写死居中",
     FORMS, "                int x = TextMargin + stroke + AlignOffset(available - width, _horizontalAlignment);",
     "                int x = TextMargin + stroke + Math.Max(0, (available - width) / 2);", "P3"),
    ("横排纵轴不再按对齐摆",
     # 竖排里也有一份 AlignOffset，靠后面那一行区分：横排接下来算 letterGap，竖排接下来是 for。
     FORMS, "                AlignOffset(availableHeight - blockHeight, _verticalAlignment);\n"
            "            int letterGap = LetterGap(Font);",
     "                0;\n            int letterGap = LetterGap(Font);", "P3"),
    ("竖排退回写死居中",
     FORMS, "            int originX = TextMargin + AlignOffset(availableWidth - blockWidth, _horizontalAlignment);\n"
            "            int originY = TextMargin + AlignOffset(availableHeight - blockHeight, _verticalAlignment);",
     "            int originX = (Width - blockWidth) / 2;\n            int originY = (Height - blockHeight) / 2;", "P3"),
    ("AlignOffset 去掉越界保护",
     FORMS, "            if (free <= 0) return 0;\n",
     "", "P3"),
    ("对齐渗进测量",
     # 锚点要带上下文：这一行在 BuildLines 和 MeasureHorizontal 里各有一份，只写这一行的话
     # 注入会打到 BuildLines 上，MeasureHorizontal 一点没变。
     FORMS, "            int padding = BlockPadding(font);\n"
            "            int available = TextAreaWidth(viewWidth, font);",
     "            int padding = BlockPadding(font);\n"
     "            int available = TextAreaWidth(viewWidth, font);\n"
     "            if (_horizontalAlignment == StringAlignment.Far) available = 1;", "P3"),
    ("菜单少接一项对齐",
     FORMS, "            alignBottom.Click += delegate { SetTextAlignment(false, CardState.AlignFar); };\n",
     "", "P3"),
    ("对齐子菜单没挂进文字设置",
     FORMS, "            textSettingsMenu.DropDownItems.Add(alignMenu);\n",
     "", "P3"),
    ("语言表不设对齐勾标",
     FORMS, "                        menuItem.Checked = _card.TextAlignHorizontal == CardState.AlignNear;\n",
     "", "P3"),
    ("构造函数不把行距交给视图",
     FORMS, "            _quoteView.LineSpacingPercent = card.LineSpacingPercent;\n", "", "P3"),
    ("构造函数不推对齐给视图",
     FORMS, "            _quoteView.ShadowColor = SafeColor(CardState.ShadowColorArgbFor(card), "
            "Color.Black);\n"
            "            PushTextAlignmentToView();\n",
     "            _quoteView.ShadowColor = SafeColor(CardState.ShadowColorArgbFor(card), "
     "Color.Black);\n", "P3"),
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
        # 锚点必须唯一：str.replace(old, new, 1) 只替换第一处，锚点在两处都出现时注入会打到
        # 别处，要验的那条一条都报不出来（踩过好几次，见 verify_size.py 的同名检查）。
        if raw[path].count(old) != 1:
            bad.append("%s：锚点在源码里出现 %d 次，注入会打到别处（补上上下文让它唯一）"
                       % (name, raw[path].count(old)))
            continue
        mutated = {key: value for key, value in raw.items()}
        mutated[path] = raw[path].replace(old, new, 1)
        problems = check(strip_comments(mutated[FORMS]), strip_comments(mutated[STORAGE]),
                         strip_comments(mutated[PROGRAM]))
        if problems:
            ok += 1
            print("  [OK ] %-32s → 报出 %d 条（预期 %s）" % (name, len(problems), expect))
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
        print("== 便携式与排版对齐静态断言：失败 ==")
        for p in problems:
            print("  [!!] " + p)
    else:
        print("== 便携式与排版对齐静态断言：全部通过（4 组） ==")

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
