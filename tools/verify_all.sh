#!/bin/sh
# 一次跑完全部验证。
#
# 这个程序是 Windows 的 WinForms，在 macOS / Linux 上编译得出来、跑不起来。所以这里覆盖的是
# 「交叉编译 + 源码级静态断言 + 反向注入 + 纯逻辑测试（.NET 8 上跑真实抽取的代码）+
# 排版仿真 + 图标 + PE 结构」。真机行为（拖拽手感、UAC、启动文件夹、任务栏图标渲染、
# File.Replace 遇到杀毒软件）仍然只能在 Windows 上验。
#
# 用法：
#   sh tools/verify_all.sh
#
# 环境变量（都有默认值，一般不用管）：
#   PYTHON   Python 3 解释器，默认 python3
#   DOTNET   dotnet 命令，默认 dotnet；交叉编译 .NET Framework 需要能还原 net40 参考程序集
#   PREV     某个旧版本的源码目录（可选）。指过去之后，第 11 步会拿它当对照，
#            确认断言真的会报问题 —— 没有旧版本时这一步会明说「跳过」
set -e

PY=${PYTHON:-python3}
DOTNET=${DOTNET:-dotnet}
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/.." && pwd)
SRC=$REPO/src/XiaoJian
PREV=${PREV:-}

export DOTNET_CLI_TELEMETRY_OPTOUT=1

count() {
    # grep -c 在「一条都没有」时返回 1，会把 set -e 触发掉，所以这里吞掉退出码。
    "$PY" "$1" --src "$2" 2>&1 | grep -c '\[!!\]' || true
}

# 「旧版本」的对照只数**行为**类问题。
#
# 断言里有一部分描述的是结构（某个方法叫什么、菜单在哪一层建），而结构是随版本变的：
# 把 BuildMenu 拆成几个方法之后，新断言去旧源码里找 BuildCardSettingsMenu 必然「取不到」——
# 那不是回归，把它算进条数只会让这一行看起来像旧版本有 bug。
count_behaviour() {
    "$PY" "$1" --src "$2" 2>&1 | grep '\[!!\]' | grep -vc '取不到' || true
}

echo "源码目录：$SRC"
echo "构建目录：$REPO"
echo

echo "############ 1/11 交叉编译（.NET Framework 4.0 / AnyCPU / winexe） ############"
cd "$REPO" && "$DOTNET" build build-macos.csproj -c Release --nologo 2>&1 | tail -4

echo
echo "############ 2/11 卡片尺寸：拖动下限只约束「横跨文字流向」的那根轴 ############"
"$PY" "$HERE/verify_size.py" --src "$SRC" --reverse 2>&1 | head -2
"$PY" "$HERE/verify_size.py" --src "$SRC" --reverse 2>&1 | tail -2

echo
echo "############ 3/11 字号调整与每卡内容库：源码级断言 + 反向注入 ############"
"$PY" "$HERE/verify_font_library.py" --src "$SRC" --reverse 2>&1 | head -2
"$PY" "$HERE/verify_font_library.py" --src "$SRC" --reverse 2>&1 | tail -2

echo
echo "############ 4/11 便携式、排版对齐、改名与升级路径：源码级断言 + 反向注入 ############"
"$PY" "$HERE/verify_portable_align.py" --src "$SRC" --reverse 2>&1 | head -2
"$PY" "$HERE/verify_portable_align.py" --src "$SRC" --reverse 2>&1 | tail -2

echo
echo "############ 5/11 字号链路仿真（含旧实现对照 + 注入回归） ############"
"$PY" "$HERE/verify_font_sim.py" --src "$SRC" 2>&1 | tail -2
echo "  -- 反向注入 --"
for flag in --regress --regress-fit --regress-grow --regress-flow --regress-dialog --regress-reset; do
    printf "  %-16s " "$flag"
    "$PY" "$HERE/verify_font_sim.py" $flag --src "$SRC" 2>&1 | tail -1
done

echo
echo "############ 6/11 退出路径源码级断言 + 反向注入 ############"
"$PY" "$HERE/verify_shutdown_path.py" --src "$SRC" --reverse 2>&1 | head -2
"$PY" "$HERE/verify_shutdown_path.py" --src "$SRC" --reverse 2>&1 | tail -2

echo
echo "############ 7/11 纯逻辑测试工程（.NET 8，跑真实抽取的代码） ############"
# Extracted.cs 由 extract.py 从 src/ 现场生成，不是仓库里的文件：它必须永远等于当前源码，
# 否则这套测试验的就是一份过期的副本。
cd "$HERE/harness" && "$PY" extract.py --src "$SRC" 2>&1 | head -2
cd "$HERE/harness" && "$DOTNET" run --project Harness.csproj 2>&1 | tail -2

echo
echo "############ 8/11 拖动缩放收敛模拟：下限只读流向轴，含旧公式对照 ############"
"$PY" "$HERE/verify_shrink_sim.py" --src "$SRC" 2>&1 | tail -2
echo "  -- 旧公式对照：拿「面积最小」那个点当拖动下限 --"
printf "  %-16s " "--old"
"$PY" "$HERE/verify_shrink_sim.py" --old --src "$SRC" 2>&1 | tail -1

echo
echo "############ 9/11 程序图标 card.ico：多档结构 + 逐档内容 + 反向注入 ############"
"$PY" "$HERE/verify_icon.py" --ico "$SRC/card.ico" --src "$SRC" 2>&1 | tail -3
"$PY" "$HERE/verify_icon.py" --ico "$SRC/card.ico" --src "$SRC" --reverse 2>&1 | tail -1

echo
echo "############ 10/11 PE / 元数据校验（含内嵌图标与 card.ico 逐字节对照） ############"
"$PY" "$HERE/verify_exe.py" "$REPO/dist/Release/XiaoJian.exe" 2>&1 | tail -3

echo
echo "############ 11/11 对照构建：旧版本源码必须被断言报出问题 ############"
# 这一步验的不是「当前代码对不对」，而是「断言有没有牙」。
#
# 需要显式指一个旧版本：PREV=<目录> sh tools/verify_all.sh
# 不指就明说跳过 —— **不要让它打印「0 条」了事**：那和「全部通过」长得一模一样，
# 而实际什么都没验（这个坑踩过一次：基线目录还在、.cs 被清掉了，脚本报出 0 条）。
if [ -n "$PREV" ] && [ -f "$PREV/src/XiaoJian/Forms.cs" ]; then
    echo "旧版本：$PREV"
    echo "便携/对齐/改名报出 $(count_behaviour "$HERE/verify_portable_align.py" "$PREV/src/XiaoJian") 条（行为类），本版 0 条"
    echo "退出路径报出 $(count_behaviour "$HERE/verify_shutdown_path.py" "$PREV/src/XiaoJian") 条（行为类），本版 0 条"
    echo "卡片尺寸报出 $(count "$HERE/verify_size.py" "$PREV/src/XiaoJian") 条，本版 0 条"
    echo "字号与内容库报出 $(count "$HERE/verify_font_library.py" "$PREV/src/XiaoJian") 条，本版 0 条"
elif [ -n "$PREV" ]; then
    echo "!! PREV 指向的目录里没有 src/XiaoJian/Forms.cs：${PREV}"
    echo "   （这一步没有验任何东西，不是通过）"
else
    echo "（跳过：没有设置 PREV。设成某个旧版本的源码目录再跑，才能确认断言真的会报）"
fi

echo
echo "全部验证跑完"
