#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从真实源码里抽出「不依赖 WinForms 的纯逻辑」，生成一个能在 .NET 8 上直接运行的验证工程。

只做抽取，不改写逻辑：AppState / CardState / QuoteRepository / DailyQuotePicker / UiText
来自 Storage.cs（剥掉 StartupManager 与 SelfTests），VerticalTextLayout 来自 Forms.cs。
"""
import os, re, sys

_HERE = os.path.dirname(os.path.abspath(__file__))
# 默认读仓库里的源码；--src 可以指向别处（比如某个旧版本，用来做对照）。
SRC = os.path.normpath(os.path.join(_HERE, "..", "..", "src", "XiaoJian"))
for _i, _a in enumerate(sys.argv):
    if _a == "--src" and _i + 1 < len(sys.argv):
        SRC = sys.argv[_i + 1]
OUT = _HERE


def strip_class(text, decl):
    """删掉从 decl 那一行开始到匹配右花括号为止的整个类型定义。"""
    start = text.index(decl)
    # 从声明后的第一个 '{' 开始配平
    i = text.index("{", start)
    depth = 0
    j = i
    n = len(text)
    while j < n:
        c = text[j]
        if c == '"':
            if text.startswith('@"', j - 1):
                j += 2
                while j < n:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        break
                    j += 1
            else:
                j += 1
                while j < n:
                    if text[j] == '\\':
                        j += 2
                        continue
                    if text[j] == '"':
                        break
                    j += 1
        elif c == "'":
            j += 1
            while j < n:
                if text[j] == '\\':
                    j += 2
                    continue
                if text[j] == "'":
                    break
                j += 1
        elif c == '/' and j + 1 < n and text[j + 1] == '/':
            while j < n and text[j] != '\n':
                j += 1
        elif c == '/' and j + 1 < n and text[j + 1] == '*':
            j = text.index('*/', j) + 1
        elif c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                break
        j += 1
    return text[:start] + text[j + 1:]


def extract_class(text, decl):
    end = len(text)
    stripped = strip_class(text + "\n@@END@@", decl)
    return text[len(text) - (len(text) - len(stripped)) if False else 0:0]  # unused


def cut_class(text, decl):
    """返回被切掉的那段（含声明行前的缩进）。"""
    before = strip_class(text, decl)
    removed_len = len(text) - len(before)
    start = text.index(decl)
    return text[start:start + removed_len]


storage = open(os.path.join(SRC, "Storage.cs"), encoding="utf-8").read()
forms = open(os.path.join(SRC, "Forms.cs"), encoding="utf-8").read()

vertical = cut_class(forms, "    internal static class VerticalTextLayout")
# 换行与截断：横排的测量和绘制共用它，纯算术，能直接在 .NET 8 上跑。
linebreak = cut_class(forms, "    internal static class TextLines")
# 卡片尺寸算术：纯静态、不碰 WinForms，直接搬过来跑真实代码。
sizes = cut_class(forms, "    internal static class CardSizeLimits")
# 拖动时的吸附对齐：矩形上的纯算术，同样能在这里真跑。
snap = cut_class(forms, "    internal static class CardSnap")

storage = strip_class(storage, "internal static class StartupManager")
storage = strip_class(storage, "internal static class SelfTests")
storage = storage.replace("using Microsoft.Win32;\n", "")

# 剥掉 SelfTests 之后，剩下的文本以「关命名空间」的右花括号结尾。并入的类型必须插在那个
# 右花括号之前，否则它们会落到 XiaoJian 命名空间外面，看不见 CardState 这些内部类型。
lines = storage.rstrip("\n").split("\n")
while lines and lines[-1].strip() == "":
    lines.pop()
if not lines or lines[-1] != "}":
    raise SystemExit("Storage.cs 结尾找不到「关命名空间」的右花括号，抽取脚本需要更新")
storage_body = "\n".join(lines[:-1]) + "\n"
storage_tail = lines[-1] + "\n"

os.makedirs(OUT, exist_ok=True)
with open(os.path.join(OUT, "Extracted.cs"), "w", encoding="utf-8") as f:
    f.write("// 自动生成：从真实源码抽取的纯逻辑，请勿手工修改。\n")
    f.write("using System.Drawing;\n")   # CardSizeLimits 用 Size，Storage.cs 自己没引
    f.write(storage_body)
    f.write(vertical)
    f.write("\n")
    f.write(linebreak)
    f.write("\n")
    f.write(sizes)
    f.write("\n")
    f.write(snap)
    f.write("\n")
    f.write(storage_tail)

text = open(os.path.join(OUT, "Extracted.cs"), encoding="utf-8").read()
print("已生成 Extracted.cs: %d 字节" % os.path.getsize(os.path.join(OUT, "Extracted.cs")))
print("  - 已剥离 StartupManager / SelfTests / using Microsoft.Win32")
print("  - 已并入 VerticalTextLayout / CardSizeLimits")
for name in ["class CardState", "class AppState", "class QuoteRepository",
             "class DailyQuotePicker", "class UiText", "class DataLocation",
             "class VerticalTextLayout", "class TextLines", "class CardSizeLimits",
             "class CardSnap"]:
    print("  - 含 %-22s : %s" % (name, name in text))
# 并入的类型必须在命名空间里面，否则看不见 CardState —— 这条断言是踩过坑之后加的。
for name in ["class VerticalTextLayout", "class TextLines", "class CardSizeLimits",
             "class CardSnap"]:
    start = text.index(name)
    depth = text[:start].count("{") - text[:start].count("}")
    print("  - %-22s 命名空间深度 %d（必须 >= 1）" % (name, depth))
    if depth < 1:
        raise SystemExit("抽取结果把 %s 放到了命名空间外面" % name)
print("  - 残留 WinForms 引用:", "System.Windows.Forms" in text)
