#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""源码级静态断言：退出路径的「不许在菜单点击里拆自己的窗口」不变量。

为什么需要它：macOS 上跑不了 exe，退出崩溃只能靠读源码 + 结构断言来防回归。
断言全部作用在「剥掉注释之后」的源码上——本项目的注释习惯是把函数名/元素名写出来
解释为什么这么写（例如 CloseForm 的注释里就写着 Application.Exit），不剥注释会把
「注释里提到过」当成「代码里调用了」，报出根本不存在的缺陷。

用法：
    python3 verify_shutdown_path.py                # 跑全部断言
    python3 verify_shutdown_path.py --reverse      # 注入缺陷做反向验证
    python3 verify_shutdown_path.py --src <目录>   # 指定源码目录
"""
import os
import re
import sys

# 源码位置：默认取「脚本同目录下的 src/XiaoJian」，也就是补丁应用后的仓库布局。
# 可以用 --src 指定，或在源码还在别处时指过去。
_HERE = os.path.dirname(os.path.abspath(__file__))
_DEFAULT_SRC = os.path.join(_HERE, "src", "XiaoJian")
if not os.path.isdir(_DEFAULT_SRC):
    _DEFAULT_SRC = "/tmp/XiaoJian/src/XiaoJian"

SRC = _DEFAULT_SRC
for _i, _a in enumerate(sys.argv):
    if _a == "--src" and _i + 1 < len(sys.argv):
        SRC = sys.argv[_i + 1]

FORMS = os.path.join(SRC, "Forms.cs")
PROGRAM = os.path.join(SRC, "Program.cs")

# 会「拆掉自己所在窗口」的调用。出现在菜单点击处理里就必须先 RunAfterMenu 延后。
DANGEROUS = [
    "Application.Exit()",
    "Manager.Shutdown()",
    "Manager.CreateCard(",
    "Manager.RemoveCard(",
    "RecreateHandle()",
    "ToggleCardVisible()",
    ".Close()",
]


# ---------------------------------------------------------------- 源码预处理

def strip_comments(text):
    """剥掉 // 与 /* */ 注释，但完整保留字符串字面量与字符字面量。

    注释用等长空格替换（换行保留），这样行号与原文一一对应，报错能直接指到行。
    保留字符串字面量是必须的：L"http://schemas.microsoft.com/..." 里的 // 不能被当注释截断。
    """
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        # 逐字字符串 @"..."，内部用 "" 表示一个引号
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
        if c == '"':
            j = i + 1
            while j < n:
                if text[j] == "\\":
                    j += 2
                    continue
                if text[j] == '"':
                    break
                j += 1
            out.append(text[i:j + 1])
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
    """i 指向 " 或 ' 或 @，返回字面量之后的索引。"""
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
    """从 '{' 开始配平，返回 (花括号内部文本, 右花括号索引)。跳过字符串与字符字面量。"""
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


def member_body(text, signature):
    """取某个成员（方法/属性）的花括号内部文本。signature 必须在文件中唯一。"""
    if text.count(signature) != 1:
        raise ValueError("签名不唯一：%s（出现 %d 次）" % (signature, text.count(signature)))
    i = text.index(signature)
    j = text.index("{", i)
    return block_at(text, j)[0]


def line_of(text, needle):
    return text[:text.index(needle)].count("\n") + 1


def safe_body(src, signature, problems, label):
    """取成员体；签名找不到/不唯一时记成一条问题，而不是抛异常打断整轮检查。

    对「修复前的源码」跑这份脚本时，RunAfterMenu / Shutdown 之类的方法本来就不存在，
    这时候应该老老实实报「缺少 XX」，而不是崩在成员查找上。
    """
    try:
        return member_body(src, signature)
    except ValueError as exc:
        problems.append("%s 取不到：%s" % (label, exc))
        return None


def deferred_region(handler_body):
    """RunAfterMenu(delegate { ... }) 里那段文本；没有就返回 None。"""
    idx = handler_body.find("RunAfterMenu(")
    if idx < 0:
        return None
    brace = handler_body.find("{", idx)
    if brace < 0:
        return None
    try:
        return block_at(handler_body, brace)[0]
    except ValueError:
        return None


def outside_deferred(handler_body):
    """handler_body 去掉延后区块后剩下的部分——也就是「点击后立刻执行」的那部分。"""
    idx = handler_body.find("RunAfterMenu(")
    if idx < 0:
        return handler_body
    brace = handler_body.find("{", idx)
    if brace < 0:
        return handler_body
    try:
        inner, end = block_at(handler_body, brace)
    except ValueError:
        return handler_body
    return handler_body[:brace] + handler_body[end + 1:]


def method_bodies(src):
    """方法名 -> 方法体。用于看点击处理器「间接」调用了什么。"""
    out = {}
    pattern = re.compile(
        r"(?:private|internal|protected|public)\s+(?:static\s+)?"
        r"[A-Za-z_][\w<>,\[\]\.]*\s+([A-Za-z_]\w*)\s*\([^()]*\)\s*\{")
    for m in pattern.finditer(src):
        brace = src.index("{", m.end() - 1)
        try:
            body = block_at(src, brace)[0]
        except ValueError:
            continue
        out.setdefault(m.group(1), body)
    return out


# ---------------------------------------------------------------- 断言

def check_forms(src):
    """Forms.cs 的退出路径断言。src 是已剥注释的文本。"""
    problems = []
    methods = method_bodies(src)

    # --- 1. 菜单点击处理：碰窗口的必须延后 -----------------------------
    handlers = []
    # 菜单拆成三个方法之后，点击处理器也分散在三处 —— 少列一个，S1 会因为「处理器太少」
    # 报一条假问题（真实原因是它没去看那个方法）。
    for owner in ("private ContextMenuStrip BuildMenu",
                  "private ToolStripMenuItem BuildCardSettingsMenu",
                  "private ToolStripMenuItem BuildTextSettingsMenu",
                  "internal void RefreshCardList"):
        body = safe_body(src, owner, problems, "菜单构建方法")
        if body is None:
            continue
        for m in re.finditer(r"\.Click\s*\+=\s*delegate", body):
            brace = body.find("{", m.end())
            if brace < 0:
                problems.append("S1 %s：Click 处理器没有函数体" % owner)
                continue
            hbody = block_at(body, brace)[0]
            back = body[max(0, m.start() - 60):m.end()]
            found = re.search(r"([A-Za-z_]\w*)\.Click\s*\+=", back)
            handlers.append((owner, found.group(1) if found else "?", hbody))

    if len(handlers) < 15:
        problems.append("S1 只找到 %d 个菜单点击处理器，明显偏少" % len(handlers))

    for owner, name, hbody in handlers:
        # 规则 A：直接调用「拆自己窗口」的 API，必须落在延后区块里面
        stray = [token for token in DANGEROUS if token in outside_deferred(hbody)]
        if stray:
            problems.append("S1 %s 的 %s：%s 在菜单点击里直接执行，没有放进 RunAfterMenu"
                            % (owner, name, "/".join(stray)))
            continue
        # 规则 B：调用了「会重建/销毁窗口」的方法，而那个方法前面没有模态对话框挡着。
        # 模态对话框会先把下拉菜单关掉，所以那条路径是安全的（例如选背景颜色）。
        for callee in sorted(set(re.findall(r"\b([A-Za-z_]\w*)\s*\(", hbody))):
            mb = methods.get(callee)
            if mb is None:
                continue
            destructive = "RecreateHandle()" in mb or "Application.Exit()" in mb
            if destructive and "ShowDialog" not in mb and "RunAfterMenu(" not in hbody:
                problems.append("S1 %s 的 %s 通过 %s() 重建/销毁窗口，没有走 RunAfterMenu"
                                % (owner, name, callee))

    # 退出这一项必须走 Manager.Shutdown，而不是裸的 Application.Exit
    exit_item = None
    for owner, name, hbody in handlers:
        if "Manager.Shutdown()" in hbody:
            exit_item = hbody
    if exit_item is None:
        problems.append("S2 没有找到走 Manager.Shutdown() 的退出处理器")
    elif "RunAfterMenu(" not in exit_item:
        problems.append("S2 退出处理器没有延后")

    # --- 2. RunAfterMenu 自身 -----------------------------------------
    run_after = safe_body(src, "private void RunAfterMenu(MethodInvoker action)",
                          problems, "RunAfterMenu")
    if run_after is not None:
        if "BeginInvoke(action)" not in run_after:
            problems.append("S3 RunAfterMenu 没有用 BeginInvoke 把工作推到当前消息之后")
        if "Program.PostToUi(action)" not in run_after:
            problems.append("S3 RunAfterMenu 缺少线程级兜底 PostToUi")
    if src.count("private void RunAfterMenu(") != 1:
        problems.append("S3 RunAfterMenu 定义了不止一次（或一次都没有）")

    # --- 3. DisposeResources 必须先关菜单再释放 ------------------------
    dispose_res = safe_body(src, "private void DisposeResources()", problems, "DisposeResources")
    if dispose_res is not None:
        if "_menu.Close()" not in dispose_res or "_menu.Dispose()" not in dispose_res:
            problems.append("S4 DisposeResources 没有先 Close 再 Dispose 菜单")
        elif dispose_res.index("_menu.Close()") > dispose_res.index("_menu.Dispose()"):
            problems.append("S4 DisposeResources 里 Close 排在了 Dispose 之后")
        if "_menu.Visible" not in dispose_res:
            problems.append("S4 DisposeResources 关菜单前没有判断 Visible")

    # --- 4. CloseForm 不许二次关闭 -------------------------------------
    close_form = safe_body(src,
                           "private void CloseForm(QuoteCardForm form, bool removeState, bool alreadyClosed)",
                           problems, "CloseForm")
    if close_form is not None:
        if "if (!alreadyClosed && !form.IsDisposed) form.Close();" not in close_form:
            problems.append("S5 CloseForm 没有用 alreadyClosed 挡住二次 Close")
        if re.search(r"^\s*if\s*\(!form\.IsDisposed\)\s*form\.Close\(\);", close_form, re.M):
            problems.append("S5 CloseForm 里还有无条件的 form.Close()")
        if "Program.PostToUi(delegate { Application.Exit(); })" not in close_form:
            problems.append("S5 最后一张卡片关闭后没有延后退出")

    # 订阅点必须传 alreadyClosed=true
    if "CloseForm(closed, false, true);" not in src:
        problems.append("S6 FormClosed 订阅没有把 alreadyClosed 传成 true")
    if "CloseForm(form, true, false);" not in src:
        problems.append("S6 RemoveCard 没有把 alreadyClosed 传成 false")

    # --- 5. Shutdown 先显式关卡片再退 ----------------------------------
    shutdown = safe_body(src, "public void Shutdown()", problems, "CardManager.Shutdown")
    if shutdown is not None:
        if "Program.BeginShutdown()" not in shutdown:
            problems.append("S7 Shutdown 没有先举起「正在退出」的标志")
        if "_forms.RemoveAt(index)" not in shutdown or "form.Close()" not in shutdown:
            problems.append("S7 Shutdown 没有自己逐张关掉卡片（只靠 Application.Exit 会漏掉隐藏卡片）")
        if "Application.Exit()" not in shutdown:
            problems.append("S7 Shutdown 最后没有调用 Application.Exit")

    # --- 6. 退出标志的两处来源 ------------------------------------------
    for site in ("Program.BeginShutdown()", "_shuttingDown = true;"):
        if site not in src:
            problems.append("S8 缺少退出标志设置：%s" % site)

    return problems


def check_program(src):
    """Program.cs 的退出路径断言。"""
    problems = []

    if "internal static void BeginShutdown()" not in src:
        problems.append("P1 缺少 Program.BeginShutdown")
    else:
        body = member_body(src, "internal static void BeginShutdown()")
        if "_shuttingDown = true;" not in body:
            problems.append("P1 BeginShutdown 没有举起标志")

    if "internal static void PostToUi(MethodInvoker action)" not in src:
        problems.append("P2 缺少 Program.PostToUi")
    else:
        body = member_body(src, "internal static void PostToUi(MethodInvoker action)")
        if "SynchronizationContext.Current" not in body:
            problems.append("P2 PostToUi 没有走 SynchronizationContext（最后一个窗口关掉后就寄不出消息了）")
        if "context.Post(" not in body:
            problems.append("P2 PostToUi 没有真正 Post")
        if "action();" not in body:
            problems.append("P2 PostToUi 缺少没有同步上下文时的兜底")

    # 异常兜底必须装，而且必须在 Run() 之前装
    if "InstallExceptionHandling();" not in src:
        problems.append("P3 Main 没有安装异常兜底")
    elif "Application.Run()" in src and src.index("InstallExceptionHandling();") > src.index("Application.Run()"):
        problems.append("P3 异常兜底装在了 Application.Run() 之后")

    if "private static void InstallExceptionHandling()" not in src:
        problems.append("P3 缺少 InstallExceptionHandling")
    else:
        body = member_body(src, "private static void InstallExceptionHandling()")
        if "UnhandledExceptionMode.CatchException" not in body:
            problems.append("P3 没有把未处理异常模式改成 CatchException（默认就是弹 JIT 对话框）")
        if "Application.ThreadException" not in body:
            problems.append("P3 没有挂 UI 线程异常")
        if "AppDomain.CurrentDomain.UnhandledException" not in body:
            problems.append("P3 没有挂非 UI 线程异常")

    # 上报路径：写日志 + 退出期间不弹窗 + 自己不许再抛
    if "private static void ReportUnhandledException(" not in src:
        problems.append("P4 缺少 ReportUnhandledException")
    else:
        body = member_body(src, "private static void ReportUnhandledException(Exception error, bool fatal)")
        if "WriteErrorLog(" not in body:
            problems.append("P4 上报时没有写日志")
        if "if (_shuttingDown && !fatal) return;" not in body:
            problems.append("P4 退出期间还会弹模态框，会把退出卡住")

    if "private static string WriteErrorLog(" not in src:
        problems.append("P4 缺少 WriteErrorLog")
    else:
        body = member_body(src, "private static string WriteErrorLog(Exception error)")
        # 改名之后日志也跟着改名。旧名不能再被写出来——否则同一台机器上会同时存在两份日志，
        # 用户按新名去找故障现场会找不到。
        if '"XiaoJian_error.log"' not in body:
            problems.append("P4 日志文件名不对（应为 XiaoJian_error.log）")
        if "MyQuoteCard_error.log" in body:
            problems.append("P4 还在用改名前的日志文件名 MyQuoteCard_error.log")
        # 两个候选目录必须写同一个文件名：exe 旁边写不进去时才退到临时目录，用户是照着
        # 「程序旁边那个日志名」去找的，兜底那份换个名字就等于日志凭空消失。
        if body.count('"XiaoJian_error.log"') != 2:
            problems.append("P4 日志文件名出现 %d 次（exe 旁边与临时目录各一次，应为 2）"
                            % body.count('"XiaoJian_error.log"'))
        if body.count("catch (Exception)") < 1:
            problems.append("P4 写日志没有兜住自身异常（日志失败不能顶掉原故障）")

    # 源码里不许再出现裸的 Application.Exit()
    for m in re.finditer(r"Application\.Exit\(\)", src):
        line = src[:m.start()].count("\n") + 1
        problems.append("P5 Program.cs 第 %d 行还有裸的 Application.Exit()" % line)

    return problems


# ---------------------------------------------------------------- 反向验证

INJECTIONS = [
    ("把退出改回裸的 Application.Exit",
     FORMS, "RunAfterMenu(delegate\n                {\n                    if (Manager != null) Manager.Shutdown();\n                    else Application.Exit();\n                });",
     "Application.Exit();", "S1/S2"),
    ("新建卡片不再延后",
     FORMS, "RunAfterMenu(delegate { if (Manager != null) Manager.CreateCard(); });",
     "if (Manager != null) Manager.CreateCard();", "S1"),
    ("删除卡片不再延后",
     FORMS, "QuoteCardForm self = this;\n                RunAfterMenu(delegate\n                {\n                    if (Manager != null && !self.IsDisposed) Manager.RemoveCard(self);\n                });",
     "QuoteCardForm self = this;\n                if (Manager != null && !self.IsDisposed) Manager.RemoveCard(self);", "S1"),
    ("重置外观不再延后（间接 RecreateHandle）",
     FORMS, "RunAfterMenu(delegate { ResetAppearance(); });",
     "ResetAppearance();", "S1"),
    ("透明背景不再延后 RecreateHandle",
     FORMS, "RunAfterMenu(delegate\n                {\n                    RecreateHandle();\n                    UpdateTransparentHitBox();\n                });",
     "RecreateHandle();\n                UpdateTransparentHitBox();", "S1"),
    ("DisposeResources 去掉 Close 只留 Dispose",
     FORMS, "if (_menu.Visible) _menu.Close();\n                _menu.Dispose();",
     "_menu.Dispose();", "S4"),
    ("CloseForm 去掉 alreadyClosed 判断",
     FORMS, "if (!alreadyClosed && !form.IsDisposed) form.Close();",
     "if (!form.IsDisposed) form.Close();", "S5"),
    ("FormClosed 订阅把 alreadyClosed 传成 false",
     FORMS, "CloseForm(closed, false, true);",
     "CloseForm(closed, false, false);", "S6"),
    ("Shutdown 不再自己关卡片",
     FORMS, "for (int index = _forms.Count - 1; index >= 0; index--)\n            {\n                QuoteCardForm form = _forms[index];\n                _forms.RemoveAt(index);\n                if (!form.IsDisposed) form.Close();\n            }\n            _forms.Clear();\n",
     "", "S7"),
    ("去掉异常兜底安装",
     PROGRAM, "InstallExceptionHandling();", "", "P3"),
    ("异常上报在退出期间也弹窗",
     PROGRAM, "if (_shuttingDown && !fatal) return;", "", "P4"),
    ("日志文件改回改名前的旧名",
     PROGRAM, '"XiaoJian_error.log"', '"MyQuoteCard_error.log"', "P4"),
    ("日志文件改成别的名字",
     PROGRAM,
     'Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XiaoJian_error.log"),\n'
     '                Path.Combine(Path.GetTempPath(), "XiaoJian_error.log")',
     'Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "quote-card.log"),\n'
     '                Path.Combine(Path.GetTempPath(), "quote-card.log")', "P4"),
    ("兜底日志换成另一个文件名（两处名字不一致）",
     PROGRAM,
     'Path.Combine(Path.GetTempPath(), "XiaoJian_error.log")',
     'Path.Combine(Path.GetTempPath(), "XiaoJian_fallback.log")', "P4"),
]


def run_reverse():
    forms_raw = open(FORMS, encoding="utf-8").read()
    program_raw = open(PROGRAM, encoding="utf-8").read()
    ok = 0
    bad = []
    for name, path, old, new, expect in INJECTIONS:
        raw = forms_raw if path == FORMS else program_raw
        if old not in raw:
            bad.append("%s：注入点没找到（脚本与源码脱节）" % name)
            continue
        mutated = raw.replace(old, new, 1)
        problems = check_forms(strip_comments(mutated)) if path == FORMS else check_program(strip_comments(mutated))
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
    program = strip_comments(open(PROGRAM, encoding="utf-8").read())

    problems = check_forms(forms) + check_program(program)
    checks = 8 + 5

    if problems:
        print("== 退出路径静态断言：失败 ==")
        for p in problems:
            print("  [!!] " + p)
    else:
        print("== 退出路径静态断言：全部通过（%d 组） ==" % checks)

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
