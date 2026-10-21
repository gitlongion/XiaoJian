using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
// 这个工程故意没有 using System.Drawing（别处都用全名），但吸附那一组断言里矩形和点出现得
// 太密，写全名会把断言本身淹掉。用别名而不是整个命名空间，避免 Size/Point 与局部变量撞名。
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;
using XiaoJian;

// 在 .NET 8 上直接执行 XiaoJian 的纯逻辑部分，验证多卡片持久化、旧格式迁移、
// 内容来源与竖排分列。这里跑的是从真实源码抽取的代码，不是重新实现。
internal static class Harness
{
    private static int _failures;
    private static int _passed;

    private static void Check(bool ok, string name)
    {
        if (ok) { _passed++; Console.WriteLine("  [OK ] " + name); }
        else { _failures++; Console.WriteLine("  [FAIL] " + name); }
    }

    private static string B(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
    }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "mqc-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Section("1. 全新配置：一张卡片与默认外观");
            string statePath = Path.Combine(root, "state.ini");
            AppState state = AppState.Load(statePath);
            Check(state.Cards.Count == 1, "新文件加载出 1 张卡片");
            CardState card = state.Cards[0];
            Check(card.BackgroundColorArgb == unchecked((int)0xFFFFFFFF) &&
                card.TextColorArgb == unchecked((int)0xFF000000), "默认背景白 / 文字黑");
            Check(state.ThemeDefaultVersion == 2 && state.TransparencyDefaultVersion == 2,
                "默认版本号迁移到 2");
            Check(!card.TransparentBackground && !card.VerticalText && !card.IsFixedContent,
                "默认非透明 / 非竖排 / 每日轮换");
            Check(card.CardWidth == 460 && card.CardHeight == 190, "默认尺寸 460x190");

            Section("2. 内容库去重");
            string quotePath = Path.Combine(root, "quotes.txt");
            QuoteRepository repository = new QuoteRepository(quotePath, state);
            repository.SaveEntries(new string[] { "第一句", "第二句", "第二句", "  " });
            Check(repository.Load().Count == 2, "重复与空行被去掉，剩 2 条");

            Section("3. 每日轮换");
            DailyQuotePicker picker = new DailyQuotePicker(state, card, repository);
            string first = picker.EnsureToday();
            Check(picker.EnsureToday() == first, "同一天内保持不变");
            string other = picker.PickAnother();
            Check(other != first, "换一条得到不同内容");
            Check(repository.Load().Contains(other), "换到的内容来自内容库");

            Section("4. 保存 / 加载往返");
            card.BackgroundColorArgb = unchecked((int)0xFF123456);
            card.TextColorArgb = unchecked((int)0xFFF0E0D0);
            card.FontName = "Arial";
            card.FontStyleValue = 1;
            card.BackgroundImagePath = Path.Combine(root, "background.png");
            card.TransparentBackground = true;
            card.AutoFitText = false;
            state.Save();
            AppState reloaded = AppState.Load(statePath);
            Check(reloaded.Cards.Count == 1, "往返后仍是 1 张卡片");
            CardState reloadedCard = reloaded.Cards[0];
            Check(reloadedCard.TodayQuote == other && reloadedCard.TodayDate.Length == 10,
                "当日内容与日期保留");
            Check(reloadedCard.BackgroundColorArgb == unchecked((int)0xFF123456) &&
                reloadedCard.TextColorArgb == unchecked((int)0xFFF0E0D0) &&
                reloadedCard.FontName == "Arial" && reloadedCard.FontStyleValue == 1 &&
                reloadedCard.TransparentBackground && !reloadedCard.AutoFitText,
                "外观字段逐个保留");

            Section("5. 多卡片：新建、继承与独立");
            string multiPath = Path.Combine(root, "multi-state.ini");
            AppState multi = AppState.Load(multiPath);
            multi.Cards[0].X = 100;
            multi.Cards[0].Y = 120;
            multi.Cards[0].FontName = "Arial";
            multi.Cards[0].VerticalText = true;
            CardState second = multi.CreateCard();
            Check(second.X == 136 && second.Y == 156, "新卡片相对上一张偏移 36px");
            Check(second.VerticalText && second.FontName == "Arial", "新卡片继承外观");
            second.X = 640;
            second.Y = 40;
            second.VerticalText = false;
            second.FontName = "SimSun";
            second.ContentMode = CardState.ContentModeFixed;
            second.FixedText = "固定的一句话";
            CardState third = multi.CreateCard();
            third.CardWidth = 580;
            third.CardHeight = 240;
            Check(multi.Cards.Count == 3, "共 3 张卡片");
            multi.Save();

            AppState multiReloaded = AppState.Load(multiPath);
            Check(multiReloaded.Cards.Count == 3, "往返后仍是 3 张卡片");
            Check(multiReloaded.Cards[0].X == 100 && multiReloaded.Cards[0].Y == 120 &&
                multiReloaded.Cards[0].FontName == "Arial" && multiReloaded.Cards[0].VerticalText,
                "卡片 1 的位置/字体/竖排独立保留");
            Check(multiReloaded.Cards[1].X == 640 && !multiReloaded.Cards[1].VerticalText &&
                multiReloaded.Cards[1].FontName == "SimSun" &&
                multiReloaded.Cards[1].IsFixedContent &&
                multiReloaded.Cards[1].FixedText == "固定的一句话",
                "卡片 2 的内容来源与固定文本独立保留");
            Check(multiReloaded.Cards[2].CardWidth == 580 && multiReloaded.Cards[2].CardHeight == 240,
                "卡片 3 的尺寸独立保留");

            Section("6. 删除卡片：最后一张不可删");
            Check(multiReloaded.RemoveCard(multiReloaded.Cards[2]) && multiReloaded.Cards.Count == 2,
                "可以删除一张");
            Check(multiReloaded.RemoveCard(multiReloaded.Cards[1]) && multiReloaded.Cards.Count == 1,
                "可以再删除一张");
            Check(!multiReloaded.RemoveCard(multiReloaded.Cards[0]), "最后一张拒绝删除");
            multiReloaded.Save();
            Check(AppState.Load(multiPath).Cards.Count == 1, "删除结果已落盘");

            Section("7. 内容来源：固定某一句");
            AppState fixedState = AppState.Load(Path.Combine(root, "fixed-state.ini"));
            QuoteRepository fixedRepository = new QuoteRepository(
                Path.Combine(root, "fixed-quotes.txt"), fixedState);
            fixedRepository.SaveEntries(new string[] { "库里的句子" });
            CardState fixedCard = fixedState.Cards[0];
            DailyQuotePicker fixedPicker = new DailyQuotePicker(fixedState, fixedCard, fixedRepository);
            Check(fixedPicker.SetFixedText("钉住的话") == "钉住的话", "设置固定文本");
            Check(fixedPicker.EnsureToday() == "钉住的话", "固定卡片忽略内容库");
            Check(fixedCard.TodayQuote != "钉住的话", "固定模式不写当日轮换状态");
            fixedPicker.SetFixedText("");
            Check(fixedPicker.EnsureToday() == fixedPicker.FixedPlaceholder, "空固定文本回退到提示语");
            Check(fixedPicker.PickAnother() == "库里的句子", "换一条重新回到每日轮换");
            Check(!fixedCard.IsFixedContent, "换一条后不再是固定模式");

            Section("8. 旧格式（单卡片扁平键）迁移");
            string legacyPath = Path.Combine(root, "legacy-state.ini");
            File.WriteAllLines(legacyPath, new string[]
            {
                "TodayDate=" + B("2026-01-02"),
                "TodayQuote=" + B("旧卡片的一句话"),
                "LastQuote=" + B(""),
                "X=310", "Y=88", "CardWidth=500", "CardHeight=220",
                "TopMost=True", "FontAdjustment=4",
                "BackgroundColorArgb=" + unchecked((int)0xFF102030).ToString(CultureInfo.InvariantCulture),
                "TextColorArgb=" + unchecked((int)0xFFFFFFFF).ToString(CultureInfo.InvariantCulture),
                "FontName=" + B("SimSun"), "FontStyleValue=2",
                "BackgroundImagePath=" + B(""),
                "TransparentBackground=True", "ClickThrough=True",
                "TransparencyDefaultVersion=2", "AutoFitText=False",
                "ParagraphMode=False", "UiLanguage=" + B("en"),
                "ThemeDefaultVersion=2", "StartupInitialized=False",
                "StartupEnableFailed=False", "HintShown=True"
            }, new UTF8Encoding(false));

            AppState migrated = AppState.Load(legacyPath);
            Check(migrated.Cards.Count == 1, "旧文件迁移成 1 张卡片");
            CardState mc = migrated.Cards[0];
            Check(mc.X == 310 && mc.Y == 88 && mc.CardWidth == 500 && mc.CardHeight == 220,
                "位置与尺寸保留");
            Check(mc.TopMost && mc.ClickThrough && mc.TransparentBackground && !mc.AutoFitText,
                "开关类字段保留");
            Check(mc.FontAdjustment == 4 && mc.FontName == "SimSun" && mc.FontStyleValue == 2,
                "字号/字体保留");
            Check(mc.TodayQuote == "旧卡片的一句话" && mc.TodayDate == "2026-01-02",
                "当日内容保留");
            Check(UiText.IsEnglish(migrated) && migrated.HintShown, "全局字段保留（语言/提示）");
            Check(!mc.VerticalText && !mc.IsFixedContent, "旧文件默认非竖排 / 每日轮换");

            migrated.Save();
            AppState migratedAgain = AppState.Load(legacyPath);
            Check(migratedAgain.Cards.Count == 1 && migratedAgain.Cards[0].X == 310 &&
                migratedAgain.Cards[0].TodayQuote == "旧卡片的一句话",
                "迁移后重新保存再加载仍然正确");

            Section("9. 一次性版本迁移");
            string tp = Path.Combine(root, "transparency-state.ini");
            AppState oldTp = AppState.Load(tp);
            oldTp.Cards[0].TransparentBackground = true;
            oldTp.TransparencyDefaultVersion = 1;
            oldTp.Save();
            AppState newTp = AppState.Load(tp);
            Check(!newTp.Cards[0].TransparentBackground && newTp.TransparencyDefaultVersion == 2,
                "透明背景默认值只重置一次");

            string cp = Path.Combine(root, "contrast-state.ini");
            AppState oldContrast = AppState.Load(cp);
            oldContrast.Cards[0].BackgroundColorArgb = unchecked((int)0xFFFFFFFF);
            oldContrast.Cards[0].TextColorArgb = unchecked((int)0xFFFFFFFF);
            oldContrast.ThemeDefaultVersion = 1;
            oldContrast.Save();
            AppState fixedContrast = AppState.Load(cp);
            Check(fixedContrast.Cards[0].TextColorArgb == unchecked((int)0xFF000000) &&
                fixedContrast.ThemeDefaultVersion == 2, "对比度修复迁移");

            Section("10. 竖排分列（纯布局）");
            List<string> columns = VerticalTextLayout.SplitColumns("一二三四五", 2);
            Check(columns.Count == 3 && columns[0] == "一二" && columns[1] == "三四" &&
                columns[2] == "五", "每列 2 字 → 3 列，顺序为阅读顺序");
            List<string> broken = VerticalTextLayout.SplitColumns("上\n下", 8);
            Check(broken.Count == 2 && broken[0] == "上" && broken[1] == "下",
                "显式换行强制开新列");
            Check(VerticalTextLayout.SplitColumns("", 4).Count == 0, "空文本没有列");
            Check(VerticalTextLayout.SplitColumns("一二三四", 2).Count == 2, "正好整除");
            Check(VerticalTextLayout.SplitColumns("一二三四五", 1).Count == 5, "每列 1 字");
            Check(VerticalTextLayout.CharactersPerColumn(100, 25) == 4, "100/25 → 每列 4 字");
            Check(VerticalTextLayout.CharactersPerColumn(10, 25) == 1, "高度不足时至少 1 字");
            Check(VerticalTextLayout.CharactersPerColumn(100, 0) == 1, "字高为 0 时兜底 1 字");

            List<string> wide = VerticalTextLayout.SplitColumns("一二三四五六", 2);
            List<string> trimmed = VerticalTextLayout.Truncate(wide, 2);
            Check(trimmed.Count == 2 && trimmed[0] == "一二" &&
                trimmed[1] == "三" + VerticalTextLayout.Ellipsis,
                "超出列数时用省略号截断");
            Check(VerticalTextLayout.Truncate(wide, 9).Count == wide.Count, "列数够时不截断");
            Check(VerticalTextLayout.Truncate(wide, 0).Count == 0, "最大列数为 0 时为空");

            Section("11. 卡片尺寸下限：文字自己的最小足迹");
            Check(CardSizeLimits.HorizontalMargin == 24 && CardSizeLimits.VerticalMargin == 24 &&
                CardSizeLimits.TextSlack == 4,
                "边距常量是 24 / 24 / 4（LayoutCard 四边各 12，加文字视图自己的 2px 内缩）");
            Check(CardSizeLimits.HorizontalMargin ==
                CardSizeLimits.TextPadLeft + CardSizeLimits.TextPadRight,
                "横向边距就是左右两个内边距之和");
            Check(CardSizeLimits.VerticalMargin ==
                CardSizeLimits.TextPadTop + CardSizeLimits.TextPadBottom,
                "纵向边距就是上下两个内边距之和（不再是一个写死的 40）");
            // 横向内边距曾经是 32，比纵向的 12 大三倍；那个内边距——而不是文字——才是「最小宽度
            // 改不下去」的原因。它现在只是圆角的让位，不许再比纵向的大。
            Check(CardSizeLimits.HorizontalMargin <= CardSizeLimits.VerticalMargin,
                "横向边距不再大于纵向边距（" + CardSizeLimits.HorizontalMargin + " / " +
                CardSizeLimits.VerticalMargin + "）");
            // 圆角半径现在是每张卡片的设置，内边距要盖过的是**上限**。R/2 是个既松又和几何
            // 无关的替身：上限调到 40 时它只要求 20，而真正切进来的是 R*(1-1/sqrt2) ≈ 11.7。
            double worstCornerCut = CardState.MaxCornerRadius * (1.0 - 1.0 / Math.Sqrt(2.0));
            Check(CardSizeLimits.TextPadLeft >= worstCornerCut &&
                CardSizeLimits.TextPadRight >= worstCornerCut,
                "横向内边距盖住上限半径切进来的那点（上限 " + CardState.MaxCornerRadius +
                "，切进 " + worstCornerCut.ToString("0.0") + " 像素，内边距 " +
                CardSizeLimits.TextPadLeft + "）");
            Check(CardState.MinCardHeight < 56,
                "退化态高度低于旧的固定 56（现在是 " + CardState.MinCardHeight + "）");
            Check(CardState.MinCardHeight < CardSizeLimits.SmallestLineHeight +
                CardSizeLimits.TextSlack + CardSizeLimits.VerticalMargin,
                "退化态高度低于「一行最小字号」的高度，不会自己变成新的固定高度");
            Check(CardState.MinCardWidth < CardSizeLimits.SmallestGlyphWidth +
                CardSizeLimits.TextSlack + CardSizeLimits.HorizontalMargin,
                "退化态宽度低于「一个字最小字号」的宽度，不会自己变成新的固定宽度（" +
                CardState.MinCardWidth + " < " + (CardSizeLimits.SmallestGlyphWidth +
                CardSizeLimits.TextSlack + CardSizeLimits.HorizontalMargin) + "）");

            // 空文字：一个候选都量不出块，只剩退化态下限。
            CardSizeLimits.Measure empty = (w, h) => System.Drawing.Size.Empty;
            Check(CardSizeLimits.MinimumFor(empty, false) ==
                new System.Drawing.Size(CardState.MinCardWidth, CardState.MinCardHeight),
                "没有文字时只剩退化态下限");

            // 定长块：宽度和高度都不随视图变，所以「面积最小」只取决于探针 —— 探针越大卡片越大，
            // 下限必然落在第一个可用探针上。探针序列（NextProbe = p + p/8 + 1）在低位是
            //   10 12 14 16 19 22 25 29 33 38 43 49 56 64 73 83 94 106 120 136 154 174 …
            //   横排：探针是视图宽，必须 >= 160 + 4 = 164，第一个可用探针是 174，
            //         宽度 174 + 24 = 198；高度与探针无关 = 100 + 4 + 24 = 128。
            //   竖排：探针是视图高，必须 >= 100 + 4 = 104，第一个可用探针是 106，
            //         高度 106 + 24 = 130；宽度与探针无关 = 160 + 4 + 24 = 188。
            CardSizeLimits.Measure fixed160 = (w, h) => new System.Drawing.Size(160, 100);
            System.Drawing.Size flat = CardSizeLimits.MinimumFor(fixed160, false);
            System.Drawing.Size upright = CardSizeLimits.MinimumFor(fixed160, true);
            Check(flat == new System.Drawing.Size(198, 128),
                "横排定长块：宽度取第一个装得下块的探针，高度 = 块高 + TextSlack + 纵向边距（" + flat + "）");
            Check(upright == new System.Drawing.Size(188, 130),
                "竖排定长块：宽度 = 块宽 + TextSlack + 横向边距，高度取第一个装得下块的探针（" + upright + "）");

            // 上面两个数字是「第一个可用探针」推出来的，不是随便填的：把探针退回一格，块就装不下，
            // 卡片会裁掉自己的文字。这条性质成立，才说明下限确实顶在第一个可用探针上，而不是被
            // 别的候选（更大或更小）抢走。
            int flatProbe = flat.Width - CardSizeLimits.HorizontalMargin;
            int uprightProbe = upright.Height - CardSizeLimits.VerticalMargin;
            Check(flatProbe == 174 && uprightProbe == 106,
                "下限用的探针就是第一个装得下块的探针（横 " + flatProbe + " / 竖 " + uprightProbe + "）");
            Check(160 + CardSizeLimits.TextSlack > PrevProbe(flatProbe),
                "横排：再小一格的探针 " + PrevProbe(flatProbe) + " 装不下 160 宽的块，被跳过了");
            Check(100 + CardSizeLimits.TextSlack > PrevProbe(uprightProbe),
                "竖排：再小一格的探针 " + PrevProbe(uprightProbe) + " 装不下 100 高的块，被跳过了");

            // 候选必须真的装得下自己量出来的块。探针小于「块 + TextSlack」时，按它算出来的
            // 卡片会裁掉自己的文字；竖排最容易被踩到 —— 列数由高度决定，探针触底时视图比
            // 一个字还矮，量出来的块仍是一个字高。老版靠 56 的退化态下限盖住了这个洞。
            CardSizeLimits.Measure oneCell = (w, h) => new System.Drawing.Size(12, 16);
            int oneCellHeight = 16 + CardSizeLimits.TextSlack + CardSizeLimits.VerticalMargin;
            Check(CardSizeLimits.MinimumFor(oneCell, true).Height >= oneCellHeight,
                "竖排一个字：高度至少是「字高 + 内缩 + 上下内边距」（" +
                CardSizeLimits.MinimumFor(oneCell, true).Height + "）");
            Check(CardSizeLimits.MinimumFor(oneCell, false).Height >= oneCellHeight,
                "横排一个字：高度同样由文字算出来（" +
                CardSizeLimits.MinimumFor(oneCell, false).Height + "）");

            // 文字区至少得装得下一个字。横排会折行，块宽被夹到视图宽，所以上面那条「块装得下
            // 视图」的守卫看不出视图比一个字还窄 —— 而「面积最小」恰好会挑中这种候选，因为
            // 它最窄。老版的 80 像素退化态宽度把这个洞盖住了；宽度降下来它就露出来了。
            CardSizeLimits.Measure sliverBlock = (w, h) => new System.Drawing.Size(1, 12);
            int sliverFloorWidth = CardSizeLimits.MinimumFor(sliverBlock, false).Width;
            Check(sliverFloorWidth >= CardSizeLimits.SmallestGlyphWidth + CardSizeLimits.TextSlack +
                CardSizeLimits.HorizontalMargin,
                "横排：文字区至少有一个字那么宽（下限宽 " + sliverFloorWidth + "）");
            CardSizeLimits.Measure thinColumn = (w, h) => new System.Drawing.Size(12, 1);
            int thinFloorHeight = CardSizeLimits.MinimumFor(thinColumn, true).Height;
            Check(thinFloorHeight >= CardSizeLimits.SmallestLineHeight + CardSizeLimits.TextSlack +
                CardSizeLimits.VerticalMargin,
                "竖排：文字区至少有一行那么高（下限高 " + thinFloorHeight + "）");

            // 会折行的文字：宽度越窄、行数越多、卡片越高。下限必须在「面积最小」的那一点上，
            // 既不是一行（很宽很扁），也不是一个字一行（很窄很高）。
            int chars = 20;
            int slack = CardSizeLimits.TextSlack;
            int marginX = CardSizeLimits.HorizontalMargin;
            int marginY = CardSizeLimits.VerticalMargin;
            CardSizeLimits.Measure wrap = (w, h) => FakeText(chars, w);
            System.Drawing.Size floor = CardSizeLimits.MinimumFor(wrap, false);
            Check(FakeText(chars, floor.Width - marginX).Height <= floor.Height - marginY - slack,
                "下限处文字装得下（块高 " + FakeText(chars, floor.Width - marginX).Height +
                " <= 文字区高 " + (floor.Height - marginY) + "）");
            Check(FakeText(chars, floor.Width - marginX).Width <= floor.Width - marginX - slack,
                "下限处文字宽度也装得下（块宽 " + FakeText(chars, floor.Width - marginX).Width +
                " <= 文字区宽 " + (floor.Width - marginX) + "）");
            Check(floor.Width > CardState.MinCardWidth,
                "下限不是「一个字一行」的窄条（宽 " + floor.Width + "）");
            System.Drawing.Size oneLine = new System.Drawing.Size(chars * 8 + marginX, 16 + slack + marginY);
            System.Drawing.Size sliver = new System.Drawing.Size(CardState.MinCardWidth,
                chars * 16 + slack + marginY);
            Check((long)floor.Width * floor.Height < (long)oneLine.Width * oneLine.Height &&
                (long)floor.Width * floor.Height < (long)sliver.Width * sliver.Height,
                "下限的面积比「挤成一行」和「挤成一列」都小（" + floor.Width + "x" + floor.Height + "）");
            Check(floor.Width >= CardState.MinCardWidth && floor.Height >= CardState.MinCardHeight,
                "下限不会低于退化态边界");

            // 竖排：列数由高度决定，所以探针换到高度上，宽度由文字真正需要多少列算出来。
            CardSizeLimits.Measure verticalWrap = (w, h) => FakeColumns(chars, w, h);
            System.Drawing.Size vfloor = CardSizeLimits.MinimumFor(verticalWrap, true);
            System.Drawing.Size vblock = FakeColumns(chars, vfloor.Width - marginX, vfloor.Height - marginY);
            Check(vblock.Height <= vfloor.Height - marginY - slack &&
                vblock.Width <= vfloor.Width - marginX - slack,
                "竖排下限处整列文字都装得下（不截断）");
            Check(vfloor.Height > CardState.MinCardHeight && vfloor.Width > CardState.MinCardWidth,
                "竖排下限也不是一条缝（" + vfloor.Width + "x" + vfloor.Height + "）");

            // 拖动下限只约束「横跨文字流向」的那根轴，自由的那根轴是「一个字一行」那张卡。
            // 拿面积最小的**点**当拖动下限，就是把曲线两端都剪掉：横排卡片压不到一行、竖排卡片
            // 收不到一列。这一版修的就是这件事。
            System.Drawing.Size unit = CardSizeLimits.SmallestTextCard;
            Check(unit.Width == Math.Max(CardState.MinCardWidth,
                    CardSizeLimits.SmallestGlyphWidth + slack + marginX) &&
                unit.Height == Math.Max(CardState.MinCardHeight,
                    CardSizeLimits.SmallestLineHeight + slack + marginY),
                "SmallestTextCard 就是「一个字一行」那张卡（" + unit.Width + "x" + unit.Height + "）");
            System.Drawing.Size dragNarrow = CardSizeLimits.DragLimitFor(wrap, false,
                marginX + 24);
            System.Drawing.Size dragWide = CardSizeLimits.DragLimitFor(wrap, false,
                marginX + 100);
            Check(dragNarrow.Width == unit.Width && dragWide.Width == unit.Width,
                "横排拖动下限的宽度是常数（一字卡片），不随卡片宽度变");
            Check(dragWide.Height < dragNarrow.Height,
                "拖宽之后高度下限降下来（" + dragNarrow.Height + " → " + dragWide.Height + "）");
            System.Drawing.Size vDragShort = CardSizeLimits.DragLimitFor(verticalWrap, true,
                marginY + 24);
            System.Drawing.Size vDragTall = CardSizeLimits.DragLimitFor(verticalWrap, true,
                marginY + 120);
            Check(vDragShort.Height == unit.Height && vDragTall.Height == unit.Height,
                "竖排拖动下限的高度是常数（一字卡片），不随卡片高度变");
            Check(vDragTall.Width < vDragShort.Width,
                "拖高之后宽度下限收进来（" + vDragShort.Width + " → " + vDragTall.Width + "）");

            // FlowFitFor 是 DragLimitFor 的逆运算：那一根轴顶到桌面之后，唯一的出路是沿流向轴
            // 把文字摊长。可用空间越紧，卡片越要往长里走。
            //
            // 以前这里退回「面积最小」那张卡，而大字号下面积最小是「一个字一行」的窄条，比卡片
            // 现在还窄：Math.Max 之后什么都没长，卡片就此停住，字号每加一点都只是多裁掉一点
            // 文字 —— 这正是「文字增大到一定大小后无法再变大」。
            int acrossLoose = 5 * 16 + slack + marginY;   // 折 5 行就够
            int acrossTight = 2 * 16 + slack + marginY;   // 只折 2 行才够
            System.Drawing.Size flowLoose = CardSizeLimits.FlowFitFor(wrap, false, acrossLoose);
            System.Drawing.Size flowTight = CardSizeLimits.FlowFitFor(wrap, false, acrossTight);
            Check(flowLoose.Width < flowTight.Width,
                "FlowFitFor：可用高度越紧，卡片越宽（" + acrossLoose + "→" + flowLoose.Width +
                "，" + acrossTight + "→" + flowTight.Width + "）");
            Check(FakeText(chars, flowLoose.Width - marginX).Height <=
                    flowLoose.Height - marginY - slack &&
                FakeText(chars, flowTight.Width - marginX).Height <=
                    flowTight.Height - marginY - slack,
                "FlowFitFor 返回的两档卡片都真的装得下整段文字");
            // 全都装不下时返回最长的那一个：那是留在卡片外的文字最少的一个。
            Check(CardSizeLimits.FlowFitFor(wrap, false,
                    CardSizeLimits.SmallestLineHeight + slack + marginY - 1).Width >
                flowTight.Width,
                "全都装不下时 FlowFitFor 要最长的那一个，不是退化态");

            // 竖排镜像：可用宽度越紧，卡片越高。
            int vAcrossLoose = 10 * 12 + slack + marginX;
            int vAcrossTight = 5 * 12 + slack + marginX;
            System.Drawing.Size stackLoose = CardSizeLimits.FlowFitFor(verticalWrap, true,
                vAcrossLoose);
            System.Drawing.Size stackTight = CardSizeLimits.FlowFitFor(verticalWrap, true,
                vAcrossTight);
            Check(stackLoose.Height < stackTight.Height && stackTight.Width < stackLoose.Width,
                "FlowFitFor 竖排：可用宽度越紧，卡片越高、更窄（" + vAcrossLoose + "→" +
                stackLoose.Width + "x" + stackLoose.Height + "，" + vAcrossTight + "→" +
                stackTight.Width + "x" + stackTight.Height + "）");
            Check(FakeColumns(chars, stackTight.Width - marginX, stackTight.Height - marginY)
                    .Width <= stackTight.Width - marginX - slack,
                "竖排那一档也真的装得下整列文字（不截断）");

            Check(CardSizeLimits.MaximumFor(new System.Drawing.Size(1920, 1080)) ==
                new System.Drawing.Size(1920, 1080), "上限就是桌面尺寸");
            Check(CardSizeLimits.MaximumFor(new System.Drawing.Size(100000, 100000)) ==
                new System.Drawing.Size(CardState.MaxCardWidth, CardState.MaxCardHeight),
                "桌面尺寸离谱时兜在 MaxCard 上");
            Check(CardSizeLimits.MaximumFor(new System.Drawing.Size(10, 10)).Width >=
                CardSizeLimits.MinimumFor(empty, false).Width, "上限不会低于下限");

            CardState sized = new CardState();
            sized.CardWidth = 200;
            sized.CardHeight = 110;
            sized.Clamp();
            Check(sized.CardWidth == 200 && sized.CardHeight == 110,
                "200x110 这样的小卡片不再被固定下限顶大");
            sized.CardWidth = 99999;
            sized.CardHeight = 1;
            sized.Clamp();
            Check(sized.CardWidth == CardState.MaxCardWidth &&
                sized.CardHeight == CardState.MinCardHeight,
                "配置文件里的离谱数字被兜回退化态边界");

            // 自动字号上限。自动适应只从「起点」往下缩，所以起点就是上限：默认仍是 29（升级
            // 不改变现有卡片），但可以调高，让大卡片上的字真的变大。
            CardState capped = new CardState();
            Check(Math.Abs(capped.AutoFitMaxFontSize - CardState.DefaultAutoFitMaxFontSize) < 0.01f,
                "自动字号上限默认是 " + CardState.DefaultAutoFitMaxFontSize +
                " 磅（升级不改变现有卡片）");
            capped.AutoFitMaxFontSize = 1000f;
            capped.Clamp();
            Check(Math.Abs(capped.AutoFitMaxFontSize - CardState.MaxManualFontSize) < 0.01f,
                "自动字号上限被夹在最大手动字号 " + CardState.MaxManualFontSize + " 上");
            capped.AutoFitMaxFontSize = 1f;
            capped.Clamp();
            Check(Math.Abs(capped.AutoFitMaxFontSize - CardState.MinAutoFitMaxFontSize) < 0.01f,
                "自动字号上限被夹在最小字号 " + CardState.MinAutoFitMaxFontSize + " 上");
            Check(CardState.MinAutoFitMaxFontSize == CardState.MinManualFontSize,
                "上限的下界就是最小字号：再小就没有字号可给了");

            Section("11b. 横排换行器（测量与绘制共用的那一份）");
            // 每个字符 10px 宽，字母间距按 advance 直接给出——换行器只跟这个函数打交道。
            Func<char, int> ten = c => 10;
            Func<char, int> twenty = c => 20;

            List<string> simple = TextLines.Split("abcdefghij", 50, ten);
            Check(simple.Count == 2 && simple[0] == "abcde" && simple[1] == "fghij",
                "10 个字、每行 5 个 → 2 行且对半（" + string.Join("|", simple) + "）");
            Check(TextLines.Split("abc", 1000, ten).Count == 1, "装得下就只有一行");
            Check(TextLines.Split("", 100, ten).Count == 0, "空文本没有行");
            // 一个字比整行还宽时不能死循环：让它自己占一行。
            Check(TextLines.Split("abc", 4, ten).Count == 3,
                "单个字符比整行还宽也照样前进，不会卡住");

            // 空格是首选断点：英文单词不能被劈开。
            List<string> words = TextLines.Split("aa bb cc", 100, ten);
            Check(words.Count == 1, "aa bb cc（8 个字 = 80px）在 100px 里放得下 → 一行");
            words = TextLines.Split("aaaa bbbb", 45, ten);
            Check(words.Count == 2 && words[0] == "aaaa" && words[1] == "bbbb",
                "45px 装不下 9 个字符时在空格处断开（" + string.Join("|", words) + "）");
            Check(words[0].IndexOf(' ') < 0 && words[1].IndexOf(' ') < 0,
                "断点处的空格被丢掉，不留在行尾或行首");

            // 硬换行永远生效，连续两个换行留一个空行。
            List<string> hard = TextLines.Split("ab\ncd", 100, ten);
            Check(hard.Count == 2 && hard[0] == "ab" && hard[1] == "cd",
                "文本里的换行符照样断行（" + string.Join("|", hard) + "）");
            List<string> blank = TextLines.Split("ab\n\ncd", 100, ten);
            Check(blank.Count == 3 && blank[1] == "",
                "两个换行之间留一个空行（" + string.Join("|", blank) + "）");
            Check(TextLines.Split("ab\r\ncd", 100, ten).Count == 2, "CRLF 当成一个换行");

            // 字母间距就是 advance 的一部分：同一个换行器，间距大了行就变少。
            List<string> doubled = TextLines.Split("abcdefghij", 100, twenty);
            Check(doubled.Count == 2, "字宽翻倍、行宽不变 → 仍然是 2 行但每行 5 个");
            Check(TextLines.Split("abcdefghij", 50, twenty).Count == 5,
                "间距把字撑宽之后行数跟着变多（5 行）");

            // 截断：放不下时切到能放下省略号为止，并且结果真的不超过行宽。
            string cut = TextLines.Shorten("abcdefghij", 55, ten);
            Check(cut.EndsWith(TextLines.Ellipsis), "放不下时行尾补省略号（" + cut + "）");
            int cutWidth = 0;
            for (int i = 0; i < cut.Length; i++) cutWidth += ten(cut[i]);
            Check(cutWidth <= 55, "截断之后仍然不超过行宽（" + cutWidth + " <= 55）");
            Check(TextLines.Shorten("abc", 100, ten) == "abc", "放得下就原样返回，不补省略号");

            Section("11c. 字体预设（纯逻辑 + 存盘往返）");
            CardState presetSource = new CardState();
            presetSource.FontName = "SimSun";
            presetSource.FontStyleValue = 1;
            presetSource.AutoFitText = true;
            presetSource.AutoFitMaxFontSize = 48f;
            presetSource.TextColorArgb = unchecked((int)0xFF223344);
            presetSource.LineSpacingPercent = 120;
            presetSource.LetterSpacingPercent = 25;
            presetSource.TextOutlinePercent = 8;
            presetSource.VerticalText = true;
            presetSource.TextAlignHorizontal = CardState.AlignFar;
            TextPreset presetSaved = TextPreset.FromCard("  标题  ", presetSource);
            Check(presetSaved.Name == "标题", "预设名两端的空格被去掉（\"" + presetSaved.Name + "\"）");

            CardState presetApplied = new CardState();
            presetApplied.VerticalText = false;
            presetApplied.TextAlignHorizontal = CardState.AlignNear;
            presetApplied.TextAlignVertical = CardState.AlignNear;
            presetSaved.ApplyTo(presetApplied);
            Check(presetApplied.FontName == "SimSun" && presetApplied.FontStyleValue == 1 &&
                Math.Abs(presetApplied.AutoFitMaxFontSize - 48f) < 0.01f &&
                presetApplied.TextColorArgb == unchecked((int)0xFF223344),
                "预设把字体名/字形/上限/颜色都带过去");
            Check(presetApplied.LineSpacingPercent == 120 && presetApplied.LetterSpacingPercent == 25 &&
                presetApplied.TextOutlinePercent == 8, "预设把行距/字距/描边都带过去");
            Check(!presetApplied.VerticalText && presetApplied.TextAlignHorizontal == CardState.AlignNear &&
                presetApplied.TextAlignVertical == CardState.AlignNear,
                "预设不动竖排与对齐：竖排卡片和横排卡片可以共用一个预设");

            CardState presetManual = new CardState();
            presetManual.AutoFitText = false;
            presetManual.ManualFontSize = 40f;
            presetSaved.ApplyTo(presetManual);
            Check(presetManual.AutoFitText && presetManual.ManualFontSize == 0f,
                "自动适应的预设不把手选字号塞给卡片");

            CardState presetPinned = new CardState();
            presetPinned.AutoFitText = false;
            presetPinned.ManualFontSize = 33f;
            TextPreset presetPinnedBundle = TextPreset.FromCard("手选", presetPinned);
            CardState presetTarget = new CardState();
            presetPinnedBundle.ApplyTo(presetTarget);
            Check(!presetTarget.AutoFitText && Math.Abs(presetTarget.ManualFontSize - 33f) < 0.01f,
                "手选字号的预设把手选字号带过去");

            // 存盘往返：PresetN.* 与 CardN.* 是同一套解析方式，不能互相吃掉。
            string presetFile = Path.Combine(root, "presets.ini");
            AppState presetHolder = AppState.Load(presetFile);
            presetHolder.Presets.Add(presetSaved);
            presetHolder.Presets.Add(presetPinnedBundle);
            presetHolder.Cards[0].FontName = "KaiTi";
            presetHolder.Save();
            AppState presetRoundTrip = AppState.Load(presetFile);
            Check(presetRoundTrip.Presets.Count == 2,
                "两个预设都读回来了（" + presetRoundTrip.Presets.Count + " 个）");
            Check(presetRoundTrip.Presets[0].Name == "标题" &&
                presetRoundTrip.Presets[0].FontName == "SimSun" &&
                presetRoundTrip.Presets[0].LineSpacingPercent == 120 &&
                presetRoundTrip.Presets[0].TextOutlinePercent == 8,
                "预设的每个字段都按原值往返");
            Check(presetRoundTrip.Presets[1].Name == "手选" &&
                !presetRoundTrip.Presets[1].AutoFitText &&
                Math.Abs(presetRoundTrip.Presets[1].ManualFontSize - 33f) < 0.01f,
                "手选字号也按原值往返");
            Check(presetRoundTrip.Cards[0].FontName == "KaiTi",
                "预设键没有把卡片的键吃掉");

            // 中文断行的行首/行尾禁则。这一条是拿真实引文跑出来的：贪心换行会把逗号
            // 挤到行首，甚至出现「一行只有一个句号」。
            Func<char, int> cjkAdvance = c => 30;
            List<string> brokenLines = TextLines.Split("人生天地之间，若白驹之过隙，忽然而已。",
                6 * 30, cjkAdvance);
            int badStartCount = 0, badEndCount = 0;
            for (int i = 0; i < brokenLines.Count; i++)
            {
                if (brokenLines[i].Length > 0 && TextLines.StartsLineIllegally(brokenLines[i][0]))
                    badStartCount++;
                if (brokenLines[i].Length > 0 &&
                    TextLines.EndsLineIllegally(brokenLines[i][brokenLines[i].Length - 1]))
                    badEndCount++;
            }
            Check(badStartCount == 0,
                "行首不以收尾标点开头（" + string.Join("|", brokenLines) + "）");
            Check(badEndCount == 0,
                "行尾不以起首标点结尾（" + string.Join("|", brokenLines) + "）");
            Check(string.Join("", brokenLines) == "人生天地之间，若白驹之过隙，忽然而已。",
                "禁则只挪断点，一个字都不丢也不多");

            // 连续两个收尾标点（。」）时也要一起下移，不能只挪一个。
            List<string> markPairs = TextLines.Split("春眠不觉晓。」处处闻啼鸟。", 5 * 30, cjkAdvance);
            Check(markPairs.Count > 0 && markPairs[0].Length > 0 &&
                !TextLines.StartsLineIllegally(markPairs[0][0]) &&
                !TextLines.EndsLineIllegally(markPairs[0][markPairs[0].Length - 1]),
                "连续标点一起下移（" + string.Join("|", markPairs) + "）");

            // 整行都是标点也不能被禁则削成空行。
            List<string> marksOnly = TextLines.Split("。。。。。", 2 * 30, cjkAdvance);
            bool sawBlank = false;
            for (int i = 0; i < marksOnly.Count; i++) if (marksOnly[i].Length == 0) sawBlank = true;
            Check(!sawBlank, "全是标点也不会断出空行（" + string.Join("|", marksOnly) + "）");

            Section("11d. 圆角、边框与背景图显示方式（纯逻辑 + 存盘往返）");
            // 圆角有两个上限：卡片请求的、以及卡片自己的尺寸。第二个不是保险，
            // 半径大于短边一半时 RoundedRectangle 的四个弧会重叠，画出来是个蝴蝶结。
            Check(CardSizeLimits.CornerRadiusFor(1000, 400, 300) == CardState.MaxCornerRadius,
                "请求超过上限时取上限（" + CardSizeLimits.CornerRadiusFor(1000, 400, 300) + "）");
            Check(CardSizeLimits.CornerRadiusFor(18, 400, 300) == 18, "请求在上限内时原样使用");
            Check(CardSizeLimits.CornerRadiusFor(-5, 400, 300) == 0, "负数被夹成直角");
            Check(CardSizeLimits.CornerRadiusFor(1000, 60, 40) == 20,
                "卡片比圆角还小时按短边的一半收窄（60x40 → 20）");
            Check(CardSizeLimits.CornerRadiusFor(8, 60, 40) == 8,
                "小卡片上的小圆角不受短边限制影响");
            // 内边距要盖过上限半径切进来的深度，这是 40 这个上限的来历。
            double clearance = CardState.MaxCornerRadius * (1.0 - 1.0 / Math.Sqrt(2.0));
            Check(clearance <= CardSizeLimits.TextPadLeft,
                "上限半径切进 " + clearance.ToString("0.0") + " 像素，内边距 "
                + CardSizeLimits.TextPadLeft + " 盖得住");

            CardState styled2 = new CardState();
            styled2.CornerRadius = 1000;
            styled2.BorderWidth = 1000;
            styled2.BackgroundImageLayout = 99;
            styled2.CardOpacityPercent = 1;
            styled2.Clamp();
            Check(styled2.CornerRadius == CardState.MaxCornerRadius &&
                styled2.BorderWidth == CardState.MaxBorderWidth &&
                styled2.BackgroundImageLayout == CardState.ImageLayoutCenter,
                "越界的圆角/边框/显示方式被夹回上限");
            styled2.CornerRadius = -5;
            styled2.BorderWidth = -3;
            styled2.BackgroundImageLayout = -1;
            styled2.CardOpacityPercent = 1000;
            styled2.Clamp();
            Check(styled2.CornerRadius == 0 && styled2.BorderWidth == 0 &&
                styled2.BackgroundImageLayout == CardState.ImageLayoutCover &&
                styled2.CardOpacityPercent == CardState.MaxCardOpacityPercent,
                "负数被夹回下限，过大的不透明度夹回 100");

            // 存盘往返：新增的四个键与旧的键不能互相吃掉。
            string restylePath = Path.Combine(root, "restyle.ini");
            AppState restyleState = AppState.Load(restylePath);
            restyleState.Cards[0].CornerRadius = 28;
            restyleState.Cards[0].BorderWidth = 3;
            restyleState.Cards[0].BorderColorArgb = unchecked((int)0xFF336699);
            restyleState.Cards[0].BackgroundImageLayout = CardState.ImageLayoutTile;
            restyleState.Cards[0].CardOpacityPercent = 55;
            restyleState.Save();
            AppState restyleBack = AppState.Load(restylePath);
            Check(restyleBack.Cards[0].CornerRadius == 28 &&
                restyleBack.Cards[0].BorderWidth == 3 &&
                restyleBack.Cards[0].BorderColorArgb == unchecked((int)0xFF336699) &&
                restyleBack.Cards[0].BackgroundImageLayout == CardState.ImageLayoutTile &&
                restyleBack.Cards[0].CardOpacityPercent == 55,
                "圆角/边框/边框颜色/显示方式/不透明度都按原值往返");
            // 老配置文件里没有这些键，读出来必须是默认值而不是 0。
            Check(restyleBack.Cards[0].CornerRadius == 28 &&
                new CardState().CornerRadius == CardState.DefaultCornerRadius &&
                new CardState().BorderWidth == CardState.DefaultBorderWidth &&
                new CardState().BackgroundImageLayout == CardState.ImageLayoutCover,
                "新建卡片的默认值仍是 18 / 1 / 铺满");

            Section("11e. 拖动吸附（纯算术）");
            Rectangle snapDesktop = new Rectangle(0, 0, 1920, 1080);
            Size snapCardSize = new Size(460, 190);
            // 邻居：左 100、中心 330、右 560；上 100、中心 195、下 290
            Rectangle snapNeighbour = new Rectangle(100, 100, 460, 190);
            Rectangle[] snapOne = new Rectangle[] { snapNeighbour };
            int snapReach = CardSnap.Distance;

            Point snapFar = CardSnap.Adjust(new Point(1000, 600), snapCardSize, snapOne, snapDesktop, snapReach);
            Check(snapFar == new Point(1000, 600), "离谁都远时一动不动");

            // 并排摆：左边落在邻居右边（560）。5 像素之内，吸过去。
            Point snapBeside = CardSnap.Adjust(new Point(555, 600), snapCardSize, snapOne, snapDesktop, snapReach);
            Check(snapBeside == new Point(560, 600), "左边靠近邻居右边 → 贴住，纵向不动（" + snapBeside + "）");

            // 齐平：上边落在邻居上边（100）。
            Point snapLevel = CardSnap.Adjust(new Point(1000, 104), snapCardSize, snapOne, snapDesktop, snapReach);
            Check(snapLevel == new Point(1000, 100), "上边靠近邻居上边 → 齐平，横向不动（" + snapLevel + "）");

            // 两轴各自判断，互不牵连。
            Point snapCorner = CardSnap.Adjust(new Point(555, 104), snapCardSize, snapOne, snapDesktop, snapReach);
            Check(snapCorner == new Point(560, 100), "两轴同时吸附（" + snapCorner + "）");

            // 中心线也对齐：卡片中心 330 落在邻居中心上。
            Point centred = CardSnap.Adjust(new Point(233, 600), new Size(200, 190), snapOne, snapDesktop, snapReach);
            Check(centred == new Point(230, 600), "中心线对中心线（" + centred + "）");

            // 桌面边缘也算目标。
            Point edge = CardSnap.Adjust(new Point(3, 600), snapCardSize, snapOne, snapDesktop, snapReach);
            Check(edge == new Point(0, 600), "贴到桌面左边缘（" + edge + "）");

            // 两个候选都在范围内时取更近的那个。
            Rectangle[] snapTwo = new Rectangle[] { new Rectangle(0, 0, 560, 300), new Rectangle(563, 0, 400, 300) };
            Point snapNearest = CardSnap.Adjust(new Point(562, 600), snapCardSize, snapTwo, snapDesktop, snapReach);
            Check(snapNearest == new Point(563, 600), "两个都在范围内时取更近的（" + snapNearest + "）");

            // 阈值为 0 就是不吸附。
            Point snapOff = CardSnap.Adjust(new Point(555, 600), snapCardSize, snapOne, snapDesktop, 0);
            Check(snapOff == new Point(555, 600), "阈值为 0 时不吸附");

            // 没有邻居也要能用：只吸桌面。
            Point snapAlone = CardSnap.Adjust(new Point(5, 600), snapCardSize, new Rectangle[0], snapDesktop, snapReach);
            Check(snapAlone == new Point(0, 600), "没有邻居时仍能吸桌面边缘（" + snapAlone + "）");

            // 尺寸为零的矩形不贡献目标线（隐藏的卡片会是这样）。
            Point snapBlank = CardSnap.Adjust(new Point(1000, 600), snapCardSize,
                new Rectangle[] { new Rectangle(1000, 600, 0, 0) }, snapDesktop, snapReach);
            Check(snapBlank == new Point(1000, 600), "零尺寸的矩形被忽略，不会把卡片吸到它身上");

            Section("11f. 原子写：不能留下写了一半的文件");
            string atomicPath = Path.Combine(root, "atomic.txt");
            AtomicFile.Write(atomicPath, "第一版");
            Check(File.ReadAllText(atomicPath) == "第一版", "新文件写得进去");
            Check(!File.Exists(atomicPath + ".tmp"), "写完不留 .tmp");
            AtomicFile.Write(atomicPath, "第二版");
            Check(File.ReadAllText(atomicPath) == "第二版", "覆盖已有文件");
            Check(!File.ReadAllText(atomicPath).Contains("第一版"),
                "旧内容没有残留在文件末尾（原地写才会这样）");
            AtomicFile.WriteLines(atomicPath, new string[] { "甲", "乙" });
            Check(File.ReadAllLines(atomicPath).Length == 2, "按行写也是整份替换");
            Check(!File.Exists(atomicPath + ".tmp"), "按行写同样不留 .tmp");

            Section("12. 状态键名一致性（Load/Save 必须一一对应）");
            // 由外部脚本另行核对，这里只确认关键键确实生效
            string keysPath = Path.Combine(root, "keys.ini");
            AppState keys = AppState.Load(keysPath);
            keys.Cards[0].VerticalText = true;
            keys.Cards[0].ContentMode = CardState.ContentModeFixed;
            keys.Cards[0].FixedText = "x";
            keys.Cards[0].Visible = false;
            keys.Save();
            AppState keysAgain = AppState.Load(keysPath);
            Check(keysAgain.Cards[0].VerticalText && keysAgain.Cards[0].IsFixedContent &&
                keysAgain.Cards[0].FixedText == "x" && !keysAgain.Cards[0].Visible,
                "新增字段（竖排/内容来源/固定文本/可见性）可往返");

            Section("13. 每张卡片自己的内容库与自己的字号");
            string libraryRoot = Path.Combine(root, "library-root");
            Directory.CreateDirectory(libraryRoot);
            AppState library = AppState.Load(Path.Combine(libraryRoot, "state.ini"));
            CardState firstCard = library.Cards[0];
            CardState secondCard = library.CreateCard();
            CardState thirdCard = library.CreateCard();
            Check(firstCard.Slot == 1 && secondCard.Slot == 2 && thirdCard.Slot == 3,
                "新卡片依次拿到互不相同的槽位");

            string firstFile = QuoteRepository.CardContentPath(libraryRoot, firstCard);
            string secondFile = QuoteRepository.CardContentPath(libraryRoot, secondCard);
            Check(!string.Equals(firstFile, secondFile, StringComparison.OrdinalIgnoreCase) &&
                firstFile.EndsWith("content-1.txt", StringComparison.OrdinalIgnoreCase),
                "内容库文件按槽位命名，两张卡片不会读同一个文件");

            // 旧版把所有人的内容写在一个 quotes.txt 里，它必须整份交给第一张卡片。
            File.WriteAllLines(Path.Combine(libraryRoot, "quotes.txt"),
                new string[] { "旧版共享的一句话" }, new UTF8Encoding(false));
            QuoteRepository.MigrateSharedLibrary(libraryRoot, library);
            Check(File.Exists(firstFile) && !File.Exists(Path.Combine(libraryRoot, "quotes.txt")),
                "共享内容库被迁移（改名）给第一张卡片，不留旧文件");
            Check(new QuoteRepository(firstFile, library).Load()[0] == "旧版共享的一句话",
                "迁移后内容一字不差");
            QuoteRepository.MigrateSharedLibrary(libraryRoot, library);
            Check(new QuoteRepository(firstFile, library).Load()[0] == "旧版共享的一句话",
                "再迁移一次不会破坏已有内容");

            QuoteRepository secondLibrary = QuoteRepository.ForCard(library, libraryRoot, secondCard);
            secondLibrary.SaveEntries(new string[] { "第二张的内容" });
            Check(secondLibrary.Load()[0] == "第二张的内容" &&
                new QuoteRepository(firstFile, library).Load()[0] == "旧版共享的一句话",
                "两张卡片的内容互不干扰");

            // 删掉一张卡片，剩下的卡片仍然读自己那一份。
            library.RemoveCard(thirdCard);
            library.Save();
            AppState libraryAgain = AppState.Load(Path.Combine(libraryRoot, "state.ini"));
            Check(libraryAgain.Cards.Count == 2 &&
                libraryAgain.Cards[0].Slot == firstCard.Slot &&
                libraryAgain.Cards[1].Slot == secondCard.Slot &&
                libraryAgain.NextCardSlot == 4,
                "删卡片不动别人的槽位，新槽位也不回收");

            // 手动字号：绝对值，能存能读，离谱的值被兜回可用区间。
            Check(firstCard.ManualFontSize == 0f, "默认没有手动字号");
            libraryAgain.Cards[0].ManualFontSize = 19.5f;
            libraryAgain.Cards[0].AutoFitText = false;
            libraryAgain.Save();
            AppState sizeAgain = AppState.Load(Path.Combine(libraryRoot, "state.ini"));
            Check(Math.Abs(sizeAgain.Cards[0].ManualFontSize - 19.5f) < 0.01f &&
                !sizeAgain.Cards[0].AutoFitText,
                "手动字号按原值往返，且自动适应是关着的");
            sizeAgain.Cards[0].ManualFontSize = 999f;
            sizeAgain.Cards[0].Clamp();
            Check(sizeAgain.Cards[0].ManualFontSize == CardState.MaxManualFontSize,
                "过大的手动字号被兜回上限");
            sizeAgain.Cards[0].ManualFontSize = -5f;
            sizeAgain.Cards[0].Clamp();
            Check(sizeAgain.Cards[0].ManualFontSize == 0f,
                "负数手动字号回到「没有手动字号」，不会变成 0 号字");
            Check(CardState.MinManualFontSize == 7f && CardState.MaxManualFontSize > CardState.MinManualFontSize,
                "手动字号的区间就是卡片能用字号的范围");

            Section("14. 排版对齐与便携式数据目录");

            // 横轴四选一（多了两端对齐），纵轴三选一；越界的值兜回各自的范围之内。
            AppState align = AppState.Load(Path.Combine(root, "align-state.ini"));
            CardState alignCard = align.Cards[0];
            Check(alignCard.TextAlignHorizontal == CardState.AlignCenter &&
                alignCard.TextAlignVertical == CardState.AlignCenter,
                "默认水平与垂直都居中，与旧版表现一致");
            alignCard.TextAlignHorizontal = CardState.AlignFar;
            alignCard.TextAlignVertical = CardState.AlignNear;
            align.Save();
            AppState alignAgain = AppState.Load(Path.Combine(root, "align-state.ini"));
            Check(alignAgain.Cards[0].TextAlignHorizontal == CardState.AlignFar &&
                alignAgain.Cards[0].TextAlignVertical == CardState.AlignNear,
                "对齐方式按原值往返");
            CardState inherited = alignAgain.CreateCard();
            Check(inherited.TextAlignHorizontal == CardState.AlignFar &&
                inherited.TextAlignVertical == CardState.AlignNear,
                "新卡片继承上一张卡片的对齐方式");
            inherited.TextAlignHorizontal = 7;
            inherited.TextAlignVertical = -3;
            inherited.Clamp();
            Check(inherited.TextAlignHorizontal == CardState.AlignJustify &&
                inherited.TextAlignVertical == CardState.AlignNear,
                "手改配置文件里的越界对齐值被兜回各自的范围之内（横轴到两端对齐为止）");
            Check(CardState.AlignNear == 0 && CardState.AlignCenter == 1 &&
                CardState.AlignFar == 2 && CardState.AlignJustify == 3,
                "对齐取值是 Near/Center/Far/Justify 四个，顺序不能改");

            // 便携式：默认写在程序旁边的 data 目录，只有那个位置不可写时才退回用户目录。
            string portable = Path.Combine(root, "portable-data");
            Check(string.Equals(Path.GetFullPath(DataLocation.Resolve(
                    new string[] { "--data-dir=" + portable })),
                Path.GetFullPath(portable), StringComparison.OrdinalIgnoreCase),
                "--data-dir= 指定的目录被原样采用");
            Check(Directory.Exists(portable), "指定的目录会被建出来");
            Check(!DataLocation.IsPortable(portable), "不在程序旁边就不算便携目录");
            Check(DataLocation.IsPortable(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, DataLocation.PortableFolderName)),
                "程序旁边的 data 目录被认作便携目录");

            // 升级迁移：旧用户目录的内容整份搬到便携目录，只搬一次，且绝不覆盖已有内容。
            string oldHome = Path.Combine(root, "old-home");
            Directory.CreateDirectory(oldHome);
            File.WriteAllText(Path.Combine(oldHome, "state.ini"), "UiLanguage=zh-CN");
            File.WriteAllText(Path.Combine(oldHome, "content-1.txt"), "旧版内容");
            File.WriteAllText(Path.Combine(oldHome, "background-1.png"), "x");
            Check(DataLocation.Adopt(portable, oldHome) == 3, "旧用户目录的内容被整份带到便携目录");
            Check(File.ReadAllText(Path.Combine(portable, "content-1.txt")) == "旧版内容",
                "带过来的内容一字不差");
            Check(DataLocation.Adopt(portable, oldHome) == 0, "已经迁过就不再重复搬");
            File.WriteAllText(Path.Combine(portable, "content-1.txt"), "新内容");
            Check(DataLocation.Adopt(portable, oldHome) == 0 &&
                File.ReadAllText(Path.Combine(portable, "content-1.txt")) == "新内容",
                "便携目录已有的内容不会被旧目录覆盖");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("  [FAIL] 异常: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("通过 {0} 项，失败 {1} 项", _passed, _failures);
        return _failures == 0 ? 0 : 1;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
    }

    // 探针序列里 p 的前一格，用来验证「再小一格就装不下」。序列是几何级数，反推要小心整数除法，
    // 所以直接用穷举找：从 SmallestView=10 一路走过去，记下最后一个小于 p 的探针。
    private static int PrevProbe(int probe)
    {
        int previous = 10;
        for (int p = 10; p < probe; p = Math.Max(p + 1, p + p / 8 + 1)) previous = p;
        return previous;
    }

    // 假的横排文字：每字 8px 宽、每行 16px 高，按视图宽度折行。形状与 TextRenderer 的
    // WordBreak 一致（视图越窄、行数越多、块越高），只是数字好算。
    private static System.Drawing.Size FakeText(int chars, int viewWidth)
    {
        int perLine = Math.Max(1, viewWidth / 8);
        int lines = (chars + perLine - 1) / perLine;
        return new System.Drawing.Size(Math.Min(chars, perLine) * 8, lines * 16);
    }

    // 假的竖排文字：每列 16px 高装一个字、列宽 12px，列数由视图高度决定，宽度不够时截列。
    // 与真实 BuildColumns 一样先扣掉视图自己的 2px 内缩，否则会算出一个装不下的「下限」。
    private static System.Drawing.Size FakeColumns(int chars, int viewWidth, int viewHeight)
    {
        int perColumn = Math.Max(1, (viewHeight - 4) / 16);
        int columns = (chars + perColumn - 1) / perColumn;
        int maxColumns = Math.Max(1, (viewWidth - 4) / 12);
        if (columns > maxColumns) columns = maxColumns;
        return new System.Drawing.Size(columns * 12, Math.Min(chars, perColumn) * 16);
    }
}
