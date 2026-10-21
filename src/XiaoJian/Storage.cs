using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace XiaoJian
{
    // Writes a file in a way that cannot leave a half-written one behind.
    //
    // Both files this program keeps - the cards and the quote library - are rewritten whole on
    // every change. Writing in place means a crash, a full disk or a power cut during the write
    // leaves a truncated file, and a truncated quote library is somebody's text gone. Writing to a
    // sibling first and swapping it in makes the previous contents survive any failure.
    internal static class AtomicFile
    {
        public static void Write(string path, string contents)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            Replace(temp, path);
        }

        public static void WriteLines(string path, string[] lines)
        {
            string temp = path + ".tmp";
            File.WriteAllLines(temp, lines, new UTF8Encoding(false));
            Replace(temp, path);
        }

        private static void Replace(string temp, string path)
        {
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                // One step: the old file is unlinked and the new one takes its name.
                File.Replace(temp, path, null);
            }
            catch (IOException)
            {
                // A shell extension or a virus scanner is holding the file open. Copying over it is
                // not atomic, but the temp file is already complete, so a failure here still leaves
                // the original readable - and losing the save outright is worse.
                File.Copy(temp, path, true);
                TryDelete(temp);
            }
            catch (UnauthorizedAccessException)
            {
                File.Copy(temp, path, true);
                TryDelete(temp);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // A leftover .tmp is harmless: the next write overwrites it.
            }
        }
    }

    internal static class UiText
    {
        public static bool IsEnglish(AppState state)
        {
            return state != null && string.Equals(state.UiLanguage, "en", StringComparison.OrdinalIgnoreCase);
        }

        public static string Get(AppState state, string chinese, string english)
        {
            return IsEnglish(state) ? english : chinese;
        }
    }

    // Everything that belongs to one card: where it sits, how it looks, and where its text comes
    // from. Application wide settings (language, startup entry, hint state) live on AppState, so
    // adding a card never duplicates them.
    internal sealed class CardState
    {
        public const int ContentModeDaily = 0;
        public const int ContentModeFixed = 1;

        // The card is resizable to any size. These four numbers are only a guard against a
        // hand-edited configuration file: the smallest size a user can actually reach is the text
        // block plus the margins the layout reserves around it, and that needs a measured text
        // block to know, so the card works it out at run time (CardSizeLimits) and keeps it in
        // MinimumSize. The upper bound exists for the same reason - a nonsense number in the file
        // must not reach GDI - and the live cap is the desktop, applied by the form.
        //
        // The floor is the layout's own minimum: MinimumFor turns away every candidate text area
        // narrower than CardSizeLimits.SmallestGlyphWidth (10) or shorter than SmallestLineHeight
        // (12), and the padding around the text is 12 on all four sides. The smallest card the text
        // alone can ask for is therefore 10 + 4 + 24 = 38 across and 12 + 4 + 24 = 40 tall. The two
        // numbers here are only the guard for a card that measures nothing at all, so they have to
        // stay below those - otherwise they become the fixed minimum this release is removing.
        public const int MinCardWidth = 32;
        public const int MinCardHeight = 32;
        public const int MaxCardWidth = 8192;
        public const int MaxCardHeight = 8192;

        // The smallest and largest font a user may pick by hand, in points. The smallest one is
        // also the size the automatic fit search bottoms out on, so the two can never disagree
        // about how small a card is allowed to squeeze its text.
        public const float MinManualFontSize = 7f;
        public const float MaxManualFontSize = 96f;

        // The ceiling the automatic fit starts from, in points. The fit only ever shrinks from a
        // guess, so this is really "how large the text is allowed to get on a big card". 29 was a
        // constant in the search itself; on a card the size of the desktop it left four characters
        // occupying a tenth of the width. Kept as the default so an upgrade does not resize
        // anybody's cards.
        public const float DefaultAutoFitMaxFontSize = 29f;
        public const float MinAutoFitMaxFontSize = MinManualFontSize;

        // Spacing, both as a percentage of what the font asks for. Percentages rather than pixels
        // because the font size follows the card: a fixed pixel gap would be invisible on a big
        // card and swallow a small one.
        //
        // LineSpacingPercent is the gap *between* lines (100 = the font's own), and
        // LetterSpacingPercent is extra room after every character (0 = none). In vertical text
        // they swap axes: the reading direction is down the column, so letter spacing runs down
        // the column and line spacing runs across the columns.
        public const int MinLineSpacingPercent = 70;
        public const int MaxLineSpacingPercent = 250;
        public const int DefaultLineSpacingPercent = 100;
        public const int MinLetterSpacingPercent = 0;
        public const int MaxLetterSpacingPercent = 100;
        public const int DefaultLetterSpacingPercent = 0;

        // Thickness of the halo around every glyph, as a percentage of the line height. Zero is
        // none. GDI cannot outline text, so the halo is the glyph drawn eight times around itself
        // in a contrasting colour - which is why it is a percentage and not a pixel count: it has
        // to stay a sensible fraction of the glyph at every size.
        public const int MaxTextOutlinePercent = 20;
        public const int DefaultTextOutlinePercent = 0;

        // A shadow is the glyph drawn once more, offset, in a colour between the text and what is
        // behind it. GDI cannot draw translucent text, so the "soft" comes from the colour rather
        // than from an alpha channel - which is why this is a colour and not an opacity.
        public const int MaxTextShadowPercent = 20;
        public const int DefaultTextShadowPercent = 0;

        // How opaque the whole card is, as a percentage. 97 was a constant in ApplyAppearance; the
        // menu now takes any whole number in the range rather than five fixed steps.
        //
        // The floor used to be 30. Ten is still recognisably a card, and anything lower is a card
        // that cannot be found again if the pointer happens to be elsewhere - the tray icon is the
        // only way back.
        public const int MinCardOpacityPercent = 10;
        public const int MaxCardOpacityPercent = 100;
        public const int DefaultCardOpacityPercent = 97;

        // The corner radius, in pixels. It used to be a single constant on CardSizeLimits; the
        // default is that same 18, so an upgrade does not change how anybody's card looks.
        //
        // The ceiling is not a taste call. The text sits TextPadLeft in from the edge, and a corner
        // of radius R only clears the point (pad, pad) while R * (sqrt(2) - 1) <= pad * sqrt(2),
        // which for a 12 pixel pad is R <= 40.97. Past that the text is clipped by the card's own
        // corner - so 40 is the largest whole number that is still safe.
        public const int MinCornerRadius = 0;
        public const int MaxCornerRadius = 40;
        public const int DefaultCornerRadius = 18;

        // The painted border, in pixels. Zero is no border at all.
        public const int MinBorderWidth = 0;
        public const int MaxBorderWidth = 8;
        public const int DefaultBorderWidth = 1;
        public const int DefaultBorderColorArgb = unchecked((int)0xFF2A2A2A);

        // How the background picture fills the card. Cover is what every card did before this was
        // a setting: scale up until it covers, crop the overhang.
        public const int ImageLayoutCover = 0;
        public const int ImageLayoutContain = 1;
        public const int ImageLayoutStretch = 2;
        public const int ImageLayoutTile = 3;
        public const int ImageLayoutCenter = 4;
        public const int MinImageLayout = ImageLayoutCover;
        public const int MaxImageLayout = ImageLayoutCenter;
        public const int DefaultImageLayout = ImageLayoutCover;

        // Where the text sits inside the card, one choice per axis. Both axes mean the same thing
        // for horizontal and for vertical typography: "left" is the left edge of the card, not the
        // start of the reading order - for vertical text the block of columns is what moves.
        public const int AlignNear = 0;
        public const int AlignCenter = 1;
        public const int AlignFar = 2;
        // Horizontal only: every line but the last is stretched to fill the text area. In vertical
        // text there is nothing for it to mean, so the menu entry is disabled there.
        public const int AlignJustify = 3;

        // Which content library belongs to this card. The slot is handed out once and never
        // reused, so the file a card reads stays the same file across sessions even after other
        // cards have been added and removed. 0 means "not assigned yet" and is filled in on load.
        public int Slot = 0;
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public int CardWidth = 460;
        public int CardHeight = 190;
        public bool TopMost = false;
        public bool ClickThrough = false;
        public bool TransparentBackground = false;
        public int BackgroundColorArgb = unchecked((int)0xFFFFFFFF);
        public int TextColorArgb = unchecked((int)0xFF000000);
        public string FontName = "Microsoft YaHei UI";
        public int FontStyleValue = 0;
        // An offset in points applied to the size the card works out for itself. Kept because
        // settings files written by earlier versions carry it and it still nudges the automatic
        // fit; the entries in the text menu now pick an absolute size instead (ManualFontSize).
        public int FontAdjustment = 0;
        // The font size the user picked by hand, in points, or 0 while no size has been picked.
        // A hand-picked size is absolute: it does not follow the card, which is what makes the
        // "larger" and "smaller" entries work at all - an offset from the card's own size is
        // swallowed by the automatic fit the moment the card is only as big as its text.
        public float ManualFontSize = 0f;
        // How large automatic fitting may go, in points. The fit shrinks from this guess until the
        // text fits, so it is both the starting point and the ceiling: a big card can only show
        // big text if this is raised.
        public float AutoFitMaxFontSize = DefaultAutoFitMaxFontSize;
        // Spacing, as percentages of what the font asks for. See the constants for why.
        public int LineSpacingPercent = DefaultLineSpacingPercent;
        public int LetterSpacingPercent = DefaultLetterSpacingPercent;
        public int TextOutlinePercent = DefaultTextOutlinePercent;
        public int TextShadowPercent = DefaultTextShadowPercent;
        public int CardOpacityPercent = DefaultCardOpacityPercent;
        public int CornerRadius = DefaultCornerRadius;
        public int BorderWidth = DefaultBorderWidth;
        public int BorderColorArgb = DefaultBorderColorArgb;
        public string BackgroundImagePath = "";
        public int BackgroundImageLayout = DefaultImageLayout;
        public bool AutoFitText = true;
        // Traditional Chinese vertical typography: characters stay upright and run top to bottom,
        // with columns progressing from right to left.
        public bool VerticalText = false;
        // How the laid-out text block sits in the card. Independent of VerticalText: alignment
        // moves the block, it never changes how the text is measured, so the card's own minimum
        // size is unaffected by it.
        public int TextAlignHorizontal = AlignCenter;
        public int TextAlignVertical = AlignCenter;
        // Daily rotation through the shared content library, or one sentence pinned to this card.
        public int ContentMode = ContentModeDaily;
        public string FixedText = "";
        public bool Visible = true;
        public string TodayDate = "";
        public string TodayQuote = "";
        public string LastQuote = "";

        public bool IsFixedContent { get { return ContentMode == ContentModeFixed; } }

        public void Clamp()
        {
            CardWidth = Math.Max(MinCardWidth, Math.Min(MaxCardWidth, CardWidth));
            CardHeight = Math.Max(MinCardHeight, Math.Min(MaxCardHeight, CardHeight));
            FontAdjustment = Math.Max(-24, Math.Min(8, FontAdjustment));
            AutoFitMaxFontSize = Math.Max(MinAutoFitMaxFontSize,
                Math.Min(MaxManualFontSize, AutoFitMaxFontSize));
            LineSpacingPercent = Math.Max(MinLineSpacingPercent,
                Math.Min(MaxLineSpacingPercent, LineSpacingPercent));
            LetterSpacingPercent = Math.Max(MinLetterSpacingPercent,
                Math.Min(MaxLetterSpacingPercent, LetterSpacingPercent));
            TextOutlinePercent = Math.Max(0, Math.Min(MaxTextOutlinePercent, TextOutlinePercent));
            TextShadowPercent = Math.Max(0, Math.Min(MaxTextShadowPercent, TextShadowPercent));
            CardOpacityPercent = Math.Max(MinCardOpacityPercent,
                Math.Min(MaxCardOpacityPercent, CardOpacityPercent));
            CornerRadius = Math.Max(MinCornerRadius, Math.Min(MaxCornerRadius, CornerRadius));
            BorderWidth = Math.Max(MinBorderWidth, Math.Min(MaxBorderWidth, BorderWidth));
            BackgroundImageLayout = Math.Max(MinImageLayout,
                Math.Min(MaxImageLayout, BackgroundImageLayout));
            Slot = Math.Max(0, Slot);
            // 0 means "no size picked by hand" rather than a size of zero points, and anything at
            // or below it is a hand-edited file rather than a size - so it becomes 0 as well.
            ManualFontSize = ManualFontSize <= 0f ? 0f :
                Math.Max(MinManualFontSize, Math.Min(MaxManualFontSize, ManualFontSize));
            if (string.IsNullOrWhiteSpace(FontName)) FontName = "Microsoft YaHei UI";
            FontStyleValue = Math.Max(0, Math.Min(15, FontStyleValue));
            TextAlignHorizontal = Math.Max(AlignNear, Math.Min(AlignJustify, TextAlignHorizontal));
            TextAlignVertical = Math.Max(AlignNear, Math.Min(AlignFar, TextAlignVertical));
            if (ContentMode != ContentModeFixed) ContentMode = ContentModeDaily;
        }

        public bool EnsureReadableTextColor()
        {
            if (!string.IsNullOrEmpty(BackgroundImagePath) || TransparentBackground) return false;
            int backgroundBrightness = Brightness(BackgroundColorArgb);
            int textBrightness = Brightness(TextColorArgb);
            if (Math.Abs(backgroundBrightness - textBrightness) >= 70) return false;
            int readableColor = backgroundBrightness >= 140 ?
                unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
            if (TextColorArgb == readableColor) return false;
            TextColorArgb = readableColor;
            return true;
        }

        // The halo has to separate the text from whatever is behind it. On a solid card the
        // background colour does that and is invisible against the card; on a transparent card or
        // one with a picture behind it, the text's own brightness decides between white and black.
        public static int OutlineColorArgbFor(CardState card)
        {
            if (!card.TransparentBackground && string.IsNullOrEmpty(card.BackgroundImagePath))
                return card.BackgroundColorArgb;
            return Brightness(card.TextColorArgb) >= 140 ?
                unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
        }

        // The shadow sits between the text and whatever is behind it, which is what a soft shadow
        // looks like without an alpha channel to soften it with.
        public static int ShadowColorArgbFor(CardState card)
        {
            return Blend(card.TextColorArgb, OutlineColorArgbFor(card), 45);
        }

        private static int Blend(int from, int to, int percent)
        {
            int red = Mix((from >> 16) & 255, (to >> 16) & 255, percent);
            int green = Mix((from >> 8) & 255, (to >> 8) & 255, percent);
            int blue = Mix(from & 255, to & 255, percent);
            return unchecked((int)0xFF000000) | (red << 16) | (green << 8) | blue;
        }

        private static int Mix(int from, int to, int percent)
        {
            int mixed = from + (to - from) * percent / 100;
            return mixed < 0 ? 0 : (mixed > 255 ? 255 : mixed);
        }

        private static int Brightness(int argb)
        {
            int red = (argb >> 16) & 255;
            int green = (argb >> 8) & 255;
            int blue = argb & 255;
            return (red * 299 + green * 587 + blue * 114) / 1000;
        }
    }

    // A named bundle of the typography settings, so several cards can be made to look the same
    // without setting each of them by hand.
    //
    // What it deliberately does not carry: the text direction and the alignment. Those belong to
    // how a card is laid out rather than to the typeface - a vertical card and a horizontal one can
    // share a preset and keep their own direction.
    internal sealed class TextPreset
    {
        public string Name = "";
        public string FontName = "Microsoft YaHei UI";
        public int FontStyleValue = 0;
        public float ManualFontSize = 0f;
        public bool AutoFitText = true;
        public float AutoFitMaxFontSize = CardState.DefaultAutoFitMaxFontSize;
        public int TextColorArgb = unchecked((int)0xFF000000);
        public int LineSpacingPercent = CardState.DefaultLineSpacingPercent;
        public int LetterSpacingPercent = CardState.DefaultLetterSpacingPercent;
        public int TextOutlinePercent = CardState.DefaultTextOutlinePercent;

        public static TextPreset FromCard(string name, CardState card)
        {
            TextPreset preset = new TextPreset();
            preset.Name = name == null ? "" : name.Trim();
            preset.FontName = card.FontName;
            preset.FontStyleValue = card.FontStyleValue;
            preset.ManualFontSize = card.ManualFontSize;
            preset.AutoFitText = card.AutoFitText;
            preset.AutoFitMaxFontSize = card.AutoFitMaxFontSize;
            preset.TextColorArgb = card.TextColorArgb;
            preset.LineSpacingPercent = card.LineSpacingPercent;
            preset.LetterSpacingPercent = card.LetterSpacingPercent;
            preset.TextOutlinePercent = card.TextOutlinePercent;
            return preset;
        }

        // The hand-picked size goes with the preset only when the card was using one; a card that
        // fits automatically should keep fitting automatically on the card it is applied to.
        public void ApplyTo(CardState card)
        {
            card.FontName = FontName;
            card.FontStyleValue = FontStyleValue;
            card.AutoFitText = AutoFitText;
            card.ManualFontSize = AutoFitText ? 0f : ManualFontSize;
            card.AutoFitMaxFontSize = AutoFitMaxFontSize;
            card.TextColorArgb = TextColorArgb;
            card.LineSpacingPercent = LineSpacingPercent;
            card.LetterSpacingPercent = LetterSpacingPercent;
            card.TextOutlinePercent = TextOutlinePercent;
            card.Clamp();
        }
    }

    internal sealed class AppState
    {
        private readonly string _path;

        public string UiLanguage = "zh-CN";
        public int ThemeDefaultVersion = 0;
        public int TransparencyDefaultVersion = 0;
        public bool ParagraphMode = false;
        public bool StartupInitialized = false;
        public bool StartupEnableFailed = false;
        public bool HintShown = false;
        // The next content-library slot to hand out. Slots are never reused, so a library file
        // keeps belonging to the card that created it even after cards are removed.
        public int NextCardSlot = 1;
        public readonly List<CardState> Cards = new List<CardState>();
        // Saved typefaces, in the order they were added. Kept application-wide rather than per
        // card: the point of a preset is to be applied to more than one card.
        public readonly List<TextPreset> Presets = new List<TextPreset>();

        private AppState(string path)
        {
            _path = path;
        }

        public static AppState Load(string path)
        {
            AppState state = new AppState(path);
            if (!File.Exists(path))
            {
                state.ThemeDefaultVersion = 2;
                state.TransparencyDefaultVersion = 2;
                state.Cards.Add(new CardState());
                state.AssignMissingSlots();
                return state;
            }

            List<string[]> entries = new List<string[]>();
            try
            {
                foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int separator = rawLine.IndexOf('=');
                    if (separator <= 0) continue;
                    entries.Add(new string[] { rawLine.Substring(0, separator), rawLine.Substring(separator + 1) });
                }
            }
            catch
            {
                // A damaged settings file should never prevent the card from opening.
            }

            Dictionary<int, CardState> indexed = new Dictionary<int, CardState>();
            Dictionary<int, TextPreset> presetIndex = new Dictionary<int, TextPreset>();
            List<string[]> legacy = new List<string[]>();
            foreach (string[] entry in entries)
            {
                int index;
                string field;
                if (TryParseCardKey(entry[0], out index, out field))
                {
                    CardState card;
                    if (!indexed.TryGetValue(index, out card))
                    {
                        card = new CardState();
                        indexed[index] = card;
                    }
                    ApplyCardField(card, field, entry[1]);
                }
                else if (TryParsePresetKey(entry[0], out index, out field))
                {
                    TextPreset preset;
                    if (!presetIndex.TryGetValue(index, out preset))
                    {
                        preset = new TextPreset();
                        presetIndex[index] = preset;
                    }
                    ApplyPresetField(preset, field, entry[1]);
                }
                else
                {
                    legacy.Add(entry);
                }
            }

            if (indexed.Count == 0)
            {
                // Files written before cards could be added stored exactly one card inline, so the
                // old flat keys become the first card and nothing the user configured is lost.
                CardState migrated = new CardState();
                foreach (string[] entry in legacy) ApplyCardField(migrated, entry[0], entry[1]);
                state.Cards.Add(migrated);
            }
            else
            {
                List<int> order = new List<int>(indexed.Keys);
                order.Sort();
                foreach (int index in order) state.Cards.Add(indexed[index]);
            }

            List<int> presetOrder = new List<int>(presetIndex.Keys);
            presetOrder.Sort();
            foreach (int index in presetOrder)
            {
                TextPreset preset = presetIndex[index];
                // A preset with no name could never be shown in the menu, so it is not a preset.
                if (preset.Name.Length == 0) continue;
                state.Presets.Add(preset);
            }

            foreach (string[] entry in legacy) ApplyGlobalField(state, entry[0], entry[1]);

            foreach (CardState card in state.Cards) card.Clamp();
            state.AssignMissingSlots();
            if (!string.Equals(state.UiLanguage, "en", StringComparison.OrdinalIgnoreCase))
                state.UiLanguage = "zh-CN";

            if (state.ThemeDefaultVersion < 1)
            {
                foreach (CardState card in state.Cards)
                {
                    bool stillUsingOldDefault = card.BackgroundColorArgb == unchecked((int)0xFF080808) &&
                        card.TextColorArgb == unchecked((int)0xFFFFFFFF) &&
                        string.IsNullOrEmpty(card.BackgroundImagePath) && !card.TransparentBackground;
                    if (stillUsingOldDefault)
                    {
                        card.BackgroundColorArgb = unchecked((int)0xFFFFFFFF);
                        card.TextColorArgb = unchecked((int)0xFF000000);
                    }
                }
                state.ThemeDefaultVersion = 1;
            }
            if (state.ThemeDefaultVersion < 2)
            {
                foreach (CardState card in state.Cards) card.EnsureReadableTextColor();
                state.ThemeDefaultVersion = 2;
            }
            if (state.TransparencyDefaultVersion < 2)
            {
                // Pre-release builds shared one settings file. Reset this choice once so the
                // updated public build visibly starts with its intended non-transparent card.
                foreach (CardState card in state.Cards) card.TransparentBackground = false;
                state.TransparencyDefaultVersion = 2;
            }
            return state;
        }

        // Adds a card that starts out looking like the previous one, offset so it does not hide
        // exactly behind it. Content always starts on the daily rotation so a new card is never
        // blank.
        public CardState CreateCard()
        {
            CardState card = new CardState();
            if (Cards.Count > 0)
            {
                CardState last = Cards[Cards.Count - 1];
                card.CardWidth = last.CardWidth;
                card.CardHeight = last.CardHeight;
                card.TopMost = last.TopMost;
                card.FontName = last.FontName;
                card.FontStyleValue = last.FontStyleValue;
                card.BackgroundColorArgb = last.BackgroundColorArgb;
                card.TextColorArgb = last.TextColorArgb;
                card.AutoFitText = last.AutoFitText;
                card.VerticalText = last.VerticalText;
                card.TextAlignHorizontal = last.TextAlignHorizontal;
                card.TextAlignVertical = last.TextAlignVertical;
                if (last.X != int.MinValue && last.Y != int.MinValue)
                {
                    card.X = last.X + 36;
                    card.Y = last.Y + 36;
                }
            }
            Cards.Add(card);
            card.Slot = NextCardSlot++;
            return card;
        }

        // Every card needs a slot before its content library can be found. Cards written before
        // libraries were per-card have none, so they take the lowest free numbers in card order -
        // which hands the very first card slot 1, and slot 1 is where the old shared library
        // lands, so existing content stays with the card that was already showing it.
        private void AssignMissingSlots()
        {
            HashSet<int> taken = new HashSet<int>();
            foreach (CardState card in Cards)
                if (card.Slot > 0) taken.Add(card.Slot);
            if (NextCardSlot < 1) NextCardSlot = 1;
            foreach (CardState card in Cards)
            {
                if (card.Slot > 0) continue;
                while (taken.Contains(NextCardSlot)) NextCardSlot++;
                card.Slot = NextCardSlot;
                taken.Add(NextCardSlot);
                NextCardSlot++;
            }
            foreach (CardState card in Cards)
                if (card.Slot >= NextCardSlot) NextCardSlot = card.Slot + 1;
        }

        // The last card is never removed: the application would otherwise have no window left to
        // reach its own menus from.
        public bool RemoveCard(CardState card)
        {
            if (card == null || Cards.Count <= 1) return false;
            return Cards.Remove(card);
        }

        public void Save()
        {
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            List<string> lines = new List<string>();
            lines.Add("UiLanguage=" + Encode(UiLanguage));
            lines.Add("ThemeDefaultVersion=" + ThemeDefaultVersion.ToString(CultureInfo.InvariantCulture));
            lines.Add("TransparencyDefaultVersion=" + TransparencyDefaultVersion.ToString(CultureInfo.InvariantCulture));
            lines.Add("ParagraphMode=" + ParagraphMode.ToString());
            lines.Add("StartupInitialized=" + StartupInitialized.ToString());
            lines.Add("StartupEnableFailed=" + StartupEnableFailed.ToString());
            lines.Add("HintShown=" + HintShown.ToString());
            lines.Add("NextCardSlot=" + NextCardSlot.ToString(CultureInfo.InvariantCulture));
            lines.Add("CardCount=" + Cards.Count.ToString(CultureInfo.InvariantCulture));
            for (int index = 0; index < Cards.Count; index++)
                AppendCard(lines, "Card" + index.ToString(CultureInfo.InvariantCulture) + ".", Cards[index]);
            lines.Add("PresetCount=" + Presets.Count.ToString(CultureInfo.InvariantCulture));
            for (int index = 0; index < Presets.Count; index++)
                AppendPreset(lines, "Preset" + index.ToString(CultureInfo.InvariantCulture) + ".",
                    Presets[index]);
            AtomicFile.WriteLines(_path, lines.ToArray());
        }

        private static void AppendPreset(List<string> lines, string prefix, TextPreset preset)
        {
            lines.Add(prefix + "Name=" + Encode(preset.Name));
            lines.Add(prefix + "FontName=" + Encode(preset.FontName));
            lines.Add(prefix + "FontStyleValue=" +
                preset.FontStyleValue.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "ManualFontSize=" +
                preset.ManualFontSize.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "AutoFitText=" + preset.AutoFitText.ToString());
            lines.Add(prefix + "AutoFitMaxFontSize=" +
                preset.AutoFitMaxFontSize.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "TextColorArgb=" +
                preset.TextColorArgb.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "LineSpacingPercent=" +
                preset.LineSpacingPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "LetterSpacingPercent=" +
                preset.LetterSpacingPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "TextOutlinePercent=" +
                preset.TextOutlinePercent.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendCard(List<string> lines, string prefix, CardState card)
        {
            lines.Add(prefix + "Slot=" + card.Slot.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "X=" + card.X.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "Y=" + card.Y.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "CardWidth=" + card.CardWidth.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "CardHeight=" + card.CardHeight.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "TopMost=" + card.TopMost.ToString());
            lines.Add(prefix + "ClickThrough=" + card.ClickThrough.ToString());
            lines.Add(prefix + "TransparentBackground=" + card.TransparentBackground.ToString());
            lines.Add(prefix + "BackgroundColorArgb=" + card.BackgroundColorArgb.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "TextColorArgb=" + card.TextColorArgb.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "FontName=" + Encode(card.FontName));
            lines.Add(prefix + "FontStyleValue=" + card.FontStyleValue.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "FontAdjustment=" + card.FontAdjustment.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "ManualFontSize=" +
                card.ManualFontSize.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "AutoFitMaxFontSize=" +
                card.AutoFitMaxFontSize.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "LineSpacingPercent=" +
                card.LineSpacingPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "LetterSpacingPercent=" +
                card.LetterSpacingPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "TextOutlinePercent=" +
                card.TextOutlinePercent.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "TextShadowPercent=" +
                card.TextShadowPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "CardOpacityPercent=" +
                card.CardOpacityPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "CornerRadius=" +
                card.CornerRadius.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "BorderWidth=" +
                card.BorderWidth.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "BorderColorArgb=" +
                card.BorderColorArgb.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "BackgroundImageLayout=" +
                card.BackgroundImageLayout.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "BackgroundImagePath=" + Encode(card.BackgroundImagePath));
            lines.Add(prefix + "AutoFitText=" + card.AutoFitText.ToString());
            lines.Add(prefix + "VerticalText=" + card.VerticalText.ToString());
            lines.Add(prefix + "TextAlignHorizontal=" + card.TextAlignHorizontal.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "TextAlignVertical=" + card.TextAlignVertical.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "ContentMode=" + card.ContentMode.ToString(CultureInfo.InvariantCulture));
            lines.Add(prefix + "FixedText=" + Encode(card.FixedText));
            lines.Add(prefix + "Visible=" + card.Visible.ToString());
            lines.Add(prefix + "TodayDate=" + Encode(card.TodayDate));
            lines.Add(prefix + "TodayQuote=" + Encode(card.TodayQuote));
            lines.Add(prefix + "LastQuote=" + Encode(card.LastQuote));
        }

        // Preset keys look like "Preset1.Name". Same shape as the card keys, different prefix.
        private static bool TryParsePresetKey(string key, out int index, out string field)
        {
            index = -1;
            field = "";
            const string prefix = "Preset";
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
            int dot = key.IndexOf('.');
            if (dot <= prefix.Length) return false;
            string digits = key.Substring(prefix.Length, dot - prefix.Length);
            for (int i = 0; i < digits.Length; i++)
                if (digits[i] < '0' || digits[i] > '9') return false;
            int parsed;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                return false;
            if (parsed < 0 || parsed > 999) return false;
            index = parsed;
            field = key.Substring(dot + 1);
            return true;
        }

        // Card keys look like "Card2.FontName"; anything else is an application level setting.
        private static bool TryParseCardKey(string key, out int index, out string field)
        {
            index = -1;
            field = "";
            if (!key.StartsWith("Card", StringComparison.Ordinal)) return false;
            int dot = key.IndexOf('.');
            if (dot <= 4) return false;
            string digits = key.Substring(4, dot - 4);
            for (int i = 0; i < digits.Length; i++)
                if (digits[i] < '0' || digits[i] > '9') return false;
            int parsed;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)) return false;
            if (parsed < 0 || parsed > 999) return false;
            index = parsed;
            field = key.Substring(dot + 1);
            return true;
        }

        private static bool ApplyCardField(CardState card, string key, string value)
        {
            int number;
            bool flag;
            float size;
            switch (key)
            {
                case "Slot": if (int.TryParse(value, out number)) card.Slot = number; return true;
                case "X": if (int.TryParse(value, out number)) card.X = number; return true;
                case "Y": if (int.TryParse(value, out number)) card.Y = number; return true;
                case "CardWidth": if (int.TryParse(value, out number)) card.CardWidth = number; return true;
                case "CardHeight": if (int.TryParse(value, out number)) card.CardHeight = number; return true;
                case "TopMost": if (bool.TryParse(value, out flag)) card.TopMost = flag; return true;
                case "ClickThrough": if (bool.TryParse(value, out flag)) card.ClickThrough = flag; return true;
                case "TransparentBackground": if (bool.TryParse(value, out flag)) card.TransparentBackground = flag; return true;
                case "BackgroundColorArgb": if (int.TryParse(value, out number)) card.BackgroundColorArgb = number; return true;
                case "TextColorArgb": if (int.TryParse(value, out number)) card.TextColorArgb = number; return true;
                case "FontName": card.FontName = Decode(value); return true;
                case "FontStyleValue": if (int.TryParse(value, out number)) card.FontStyleValue = number; return true;
                case "FontAdjustment": if (int.TryParse(value, out number)) card.FontAdjustment = number; return true;
                case "ManualFontSize":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out size))
                        card.ManualFontSize = size;
                    return true;
                case "AutoFitMaxFontSize":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out size))
                        card.AutoFitMaxFontSize = size;
                    return true;
                case "LineSpacingPercent":
                    if (int.TryParse(value, out number)) card.LineSpacingPercent = number;
                    return true;
                case "LetterSpacingPercent":
                    if (int.TryParse(value, out number)) card.LetterSpacingPercent = number;
                    return true;
                case "TextOutlinePercent":
                    if (int.TryParse(value, out number)) card.TextOutlinePercent = number;
                    return true;
                case "TextShadowPercent":
                    if (int.TryParse(value, out number)) card.TextShadowPercent = number;
                    return true;
                case "CardOpacityPercent":
                    if (int.TryParse(value, out number)) card.CardOpacityPercent = number;
                    return true;
                case "CornerRadius":
                    if (int.TryParse(value, out number)) card.CornerRadius = number;
                    return true;
                case "BorderWidth":
                    if (int.TryParse(value, out number)) card.BorderWidth = number;
                    return true;
                case "BorderColorArgb":
                    if (int.TryParse(value, out number)) card.BorderColorArgb = number;
                    return true;
                case "BackgroundImageLayout":
                    if (int.TryParse(value, out number)) card.BackgroundImageLayout = number;
                    return true;
                case "BackgroundImagePath": card.BackgroundImagePath = Decode(value); return true;
                case "AutoFitText": if (bool.TryParse(value, out flag)) card.AutoFitText = flag; return true;
                case "VerticalText": if (bool.TryParse(value, out flag)) card.VerticalText = flag; return true;
                case "TextAlignHorizontal": if (int.TryParse(value, out number)) card.TextAlignHorizontal = number; return true;
                case "TextAlignVertical": if (int.TryParse(value, out number)) card.TextAlignVertical = number; return true;
                case "ContentMode": if (int.TryParse(value, out number)) card.ContentMode = number; return true;
                case "FixedText": card.FixedText = Decode(value); return true;
                case "Visible": if (bool.TryParse(value, out flag)) card.Visible = flag; return true;
                case "TodayDate": card.TodayDate = Decode(value); return true;
                case "TodayQuote": card.TodayQuote = Decode(value); return true;
                case "LastQuote": card.LastQuote = Decode(value); return true;
            }
            return false;
        }

        private static void ApplyPresetField(TextPreset preset, string field, string value)
        {
            int number;
            float size;
            bool flag;
            switch (field)
            {
                case "Name": preset.Name = Decode(value).Trim(); return;
                case "FontName": preset.FontName = Decode(value); return;
                case "FontStyleValue": if (int.TryParse(value, out number)) preset.FontStyleValue = number; return;
                case "ManualFontSize":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out size))
                        preset.ManualFontSize = size;
                    return;
                case "AutoFitText": if (bool.TryParse(value, out flag)) preset.AutoFitText = flag; return;
                case "AutoFitMaxFontSize":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out size))
                        preset.AutoFitMaxFontSize = size;
                    return;
                case "TextColorArgb": if (int.TryParse(value, out number)) preset.TextColorArgb = number; return;
                case "LineSpacingPercent":
                    if (int.TryParse(value, out number)) preset.LineSpacingPercent = number;
                    return;
                case "LetterSpacingPercent":
                    if (int.TryParse(value, out number)) preset.LetterSpacingPercent = number;
                    return;
                case "TextOutlinePercent":
                    if (int.TryParse(value, out number)) preset.TextOutlinePercent = number;
                    return;
            }
        }

        private static void ApplyGlobalField(AppState state, string key, string value)
        {
            int number;
            bool flag;
            switch (key)
            {
                case "UiLanguage": state.UiLanguage = Decode(value); break;
                case "ThemeDefaultVersion": if (int.TryParse(value, out number)) state.ThemeDefaultVersion = number; break;
                case "TransparencyDefaultVersion": if (int.TryParse(value, out number)) state.TransparencyDefaultVersion = number; break;
                case "ParagraphMode": if (bool.TryParse(value, out flag)) state.ParagraphMode = flag; break;
                case "StartupInitialized": if (bool.TryParse(value, out flag)) state.StartupInitialized = flag; break;
                case "StartupEnableFailed": if (bool.TryParse(value, out flag)) state.StartupEnableFailed = flag; break;
                case "HintShown": if (bool.TryParse(value, out flag)) state.HintShown = flag; break;
                case "NextCardSlot": if (int.TryParse(value, out number)) state.NextCardSlot = number; break;
            }
        }

        private static string Encode(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static string Decode(string text)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
            catch { return ""; }
        }
    }

    internal sealed class QuoteRepository
    {
        private readonly string _path;
        private readonly AppState _state;
        private const string FirstRunText = "双击文字编辑内容库；右键卡片打开设置。";
        private const string EnglishFirstRunText = "Double-click the text to edit your library; right-click the card for settings.";
        private const string PreviousFirstRunText = "右键卡片，编辑你的内容，也把卡片变成你的样子。";
        private const string EarlierFirstRunText = "右键卡片，写下你的句子，也把卡片变成你的样子。";

        public QuoteRepository(string path, AppState state)
        {
            _path = path;
            _state = state;
            EnsureExists();
            MigrateFirstRunText();
        }

        // One library per card. The file is named after the card's slot rather than its position,
        // so inserting or removing cards never moves a library to a different card.
        public static string CardContentPath(string dataDirectory, CardState card)
        {
            int slot = card == null ? 1 : Math.Max(1, card.Slot);
            return Path.Combine(dataDirectory, "content-" + slot.ToString(CultureInfo.InvariantCulture) + ".txt");
        }

        public static QuoteRepository ForCard(AppState state, string dataDirectory, CardState card)
        {
            return new QuoteRepository(CardContentPath(dataDirectory, card), state);
        }

        // Every version up to now kept one shared library in quotes.txt. The first card inherits
        // it - renamed, not copied, so the folder holds exactly one library per card and no stale
        // file is left behind that the application would quietly ignore. A rename that fails
        // (a locked file, a read-only folder) falls back to a copy: losing content to a file
        // operation is never worth the tidier folder.
        public static void MigrateSharedLibrary(string dataDirectory, AppState state)
        {
            if (state == null || state.Cards.Count == 0) return;
            try
            {
                string legacy = Path.Combine(dataDirectory, "quotes.txt");
                if (!File.Exists(legacy)) return;
                string target = CardContentPath(dataDirectory, state.Cards[0]);
                if (File.Exists(target)) return;
                try
                {
                    File.Move(legacy, target);
                }
                catch
                {
                    File.Copy(legacy, target, false);
                }
            }
            catch
            {
                // The shared library is only a starting point: a card without a file of its own
                // gets the first-run text, which is still better than refusing to start.
            }
        }

        public string FilePath { get { return _path; } }
        public bool IsParagraphMode { get { return _state.ParagraphMode; } }
        public bool IsEnglish { get { return UiText.IsEnglish(_state); } }

        public List<string> Load()
        {
            EnsureExists();
            try
            {
                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                return _state.ParagraphMode ? ParseParagraphLines(lines) : NormalizeSingleLines(lines);
            }
            catch
            {
                return new List<string> { DefaultFirstRunText };
            }
        }

        public void SaveEditorLines(IEnumerable<string> lines)
        {
            List<string> entries = _state.ParagraphMode ? ParseParagraphLines(lines) : NormalizeSingleLines(lines);
            SaveEntries(entries);
        }

        public void SaveEntries(IEnumerable<string> entries)
        {
            List<string> normalized = NormalizeEntries(entries, _state.ParagraphMode);
            if (normalized.Count == 0) throw new InvalidOperationException(
                UiText.Get(_state, "请至少保留一条内容。", "Please keep at least one entry."));
            if (_state.ParagraphMode)
                AtomicFile.Write(_path, string.Join(Environment.NewLine + Environment.NewLine,
                    normalized.ToArray()));
            else
                AtomicFile.WriteLines(_path, normalized.ToArray());
        }

        private void EnsureExists()
        {
            if (File.Exists(_path)) return;
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            AtomicFile.WriteLines(_path, new string[] { DefaultFirstRunText });
        }

        public void RefreshDefaultTextLanguage()
        {
            MigrateFirstRunText();
        }

        private string DefaultFirstRunText
        {
            get { return UiText.Get(_state, FirstRunText, EnglishFirstRunText); }
        }

        private void MigrateFirstRunText()
        {
            try
            {
                string current = File.ReadAllText(_path, Encoding.UTF8).Trim();
                if (current == FirstRunText || current == EnglishFirstRunText ||
                    current == PreviousFirstRunText || current == EarlierFirstRunText)
                    AtomicFile.WriteLines(_path, new string[] { DefaultFirstRunText });
            }
            catch
            {
                // Existing user content must never be disturbed when migration cannot be verified.
            }
        }

        private static List<string> NormalizeSingleLines(IEnumerable<string> lines)
        {
            List<string> entries = new List<string>();
            if (lines == null) return entries;
            foreach (string line in lines)
            {
                string value = (line ?? "").Trim();
                if (value.Length > 0) entries.Add(value);
            }
            return NormalizeEntries(entries, false);
        }

        private static List<string> ParseParagraphLines(IEnumerable<string> lines)
        {
            List<string> entries = new List<string>();
            StringBuilder current = new StringBuilder();
            if (lines != null)
            {
                foreach (string rawLine in lines)
                {
                    string line = (rawLine ?? "").Trim();
                    if (line.Length == 0)
                    {
                        if (current.Length > 0)
                        {
                            entries.Add(current.ToString());
                            current.Length = 0;
                        }
                    }
                    else
                    {
                        if (current.Length > 0) current.Append(Environment.NewLine);
                        current.Append(line);
                    }
                }
            }
            if (current.Length > 0) entries.Add(current.ToString());
            return NormalizeEntries(entries, true);
        }

        private static List<string> NormalizeEntries(IEnumerable<string> entries, bool keepLineBreaks)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (entries == null) return result;
            foreach (string entry in entries)
            {
                string value = (entry ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
                string[] parts = value.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> cleaned = new List<string>();
                foreach (string part in parts)
                {
                    string text = part.Trim();
                    if (text.Length > 0) cleaned.Add(text);
                }
                value = string.Join(keepLineBreaks ? Environment.NewLine : " ", cleaned.ToArray()).Trim();
                if (value.Length == 0 || !seen.Add(value)) continue;
                result.Add(value);
            }
            return result;
        }
    }

    // Decides what one card shows. The daily rotation runs through that card's own content
    // library; a card pinned to a fixed sentence ignores its library entirely.
    internal sealed class DailyQuotePicker
    {
        private readonly AppState _state;
        private readonly CardState _card;
        private readonly QuoteRepository _repository;

        public DailyQuotePicker(AppState state, CardState card, QuoteRepository repository)
        {
            _state = state;
            _card = card;
            _repository = repository;
        }

        public string FixedPlaceholder
        {
            get
            {
                return UiText.Get(_state, "双击文字，写下这张卡片的内容。",
                    "Double-click the text to write this card's entry.");
            }
        }

        public string EnsureToday()
        {
            if (_card.IsFixedContent)
                return _card.FixedText.Length > 0 ? _card.FixedText : FixedPlaceholder;

            List<string> quotes = _repository.Load();
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            bool currentStillExists = quotes.Contains(_card.TodayQuote);
            if (_card.TodayDate == today && currentStillExists && _card.TodayQuote.Length > 0)
                return _card.TodayQuote;

            string previous = _card.TodayQuote;
            _card.LastQuote = previous;
            _card.TodayQuote = Pick(quotes, previous);
            _card.TodayDate = today;
            _state.Save();
            return _card.TodayQuote;
        }

        // Also the way out of a pinned card: choosing another entry puts the card back on the
        // daily rotation, which is what the menu offers.
        public string PickAnother()
        {
            _card.ContentMode = CardState.ContentModeDaily;
            List<string> quotes = _repository.Load();
            string previous = _card.TodayQuote;
            _card.LastQuote = previous;
            _card.TodayQuote = Pick(quotes, previous);
            _card.TodayDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _state.Save();
            return _card.TodayQuote;
        }

        public string SetFixedText(string text)
        {
            _card.ContentMode = CardState.ContentModeFixed;
            _card.FixedText = (text ?? "").Trim();
            _state.Save();
            return _card.FixedText.Length > 0 ? _card.FixedText : FixedPlaceholder;
        }

        public string RefreshAfterEdit()
        {
            if (_card.IsFixedContent)
                return _card.FixedText.Length > 0 ? _card.FixedText : FixedPlaceholder;

            List<string> quotes = _repository.Load();
            if (!quotes.Contains(_card.TodayQuote))
            {
                _card.TodayQuote = Pick(quotes, _card.TodayQuote);
                _card.TodayDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _state.Save();
            }
            return _card.TodayQuote;
        }

        private string Pick(List<string> quotes, string avoid)
        {
            if (quotes == null || quotes.Count == 0) return UiText.Get(_state,
                "请在内容库中添加一条内容。", "Please add an entry to the content library.");
            List<string> candidates = quotes;
            if (quotes.Count > 1 && !string.IsNullOrEmpty(avoid))
            {
                candidates = quotes.Where(delegate(string q) { return q != avoid; }).ToList();
                if (candidates.Count == 0) candidates = quotes;
            }

            byte[] bytes = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            uint value = BitConverter.ToUInt32(bytes, 0);
            return candidates[(int)(value % (uint)candidates.Count)];
        }
    }

    // Where the program keeps its files. Portable is the point: settings, content libraries and
    // background images live in a "data" folder next to the executable, so the whole thing can be
    // carried on a stick or copied between machines and arrives with its content. Only when the
    // program sits somewhere it may not write to - a read-only share, or Program Files without
    // elevation - does it fall back to the per-user application data folder.
    internal static class DataLocation
    {
        public const string PortableFolderName = "data";
        // The per-user folder keeps the name the previous release used, and that is deliberate: it
        // is the folder that release wrote, so an upgrade which cannot use a portable folder - a
        // Program Files install without elevation - goes on reading the same files instead of
        // starting empty. It is the fallback, not the normal case; the normal case is "data"
        // beside the executable.
        public const string PerUserFolderName = "MyQuoteCard";
        public const string ArgumentPrefix = "--data-dir=";
        private const string StateFileName = "state.ini";
        private const string WriteProbeName = ".write-test";

        public static string Resolve(string[] args)
        {
            string requested = ArgumentValue(args, ArgumentPrefix);
            if (!string.IsNullOrEmpty(requested))
            {
                // A path that cannot be used - a typo, a drive that is not there, a read-only
                // share - falls through to the default rather than taking the program down. This
                // runs before the program has installed its own error reporting, so throwing here
                // would surface as the bare Windows error box with no explanation.
                try
                {
                    string expanded = Environment.ExpandEnvironmentVariables(requested);
                    if (!Path.IsPathRooted(expanded))
                        expanded = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, expanded);
                    string full = Path.GetFullPath(expanded);
                    if (TryPrepare(full)) return full;
                }
                catch (Exception)
                {
                    // Fall through to the standard locations.
                }
            }

            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortableFolderName);
            if (TryPrepare(portable)) return portable;

            string roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                PerUserFolderName);
            Directory.CreateDirectory(roaming);
            return roaming;
        }

        // True when the folder in use is the one next to the executable. The program says which
        // mode it is in rather than leaving the user to guess where the files went.
        public static bool IsPortable(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;
            return SameDirectory(directory,
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PortableFolderName));
        }

        // Older versions wrote everything to the per-user application data folder. The first time
        // the portable folder is used it is filled from there, so an upgrade does not look like a
        // fresh install. The old files are copied rather than moved: the source is left alone in
        // case this run is the one that turns out not to be allowed to write.
        public static void AdoptLegacyData(string directory)
        {
            Adopt(directory, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                PerUserFolderName));
        }

        // Returns how many files were carried over, so the behaviour can be asserted without
        // touching the real user profile.
        internal static int Adopt(string directory, string legacy)
        {
            int copied = 0;
            try
            {
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(legacy)) return 0;
                if (SameDirectory(directory, legacy)) return 0;
                if (File.Exists(Path.Combine(directory, StateFileName))) return 0;
                if (!File.Exists(Path.Combine(legacy, StateFileName))) return 0;
                foreach (string source in Directory.GetFiles(legacy))
                {
                    string destination = Path.Combine(directory, Path.GetFileName(source));
                    if (File.Exists(destination)) continue;
                    try
                    {
                        File.Copy(source, destination, false);
                        copied++;
                    }
                    catch (Exception)
                    {
                        // One file that cannot be carried over must not stop the others.
                    }
                }
            }
            catch (Exception)
            {
                // An upgrade that cannot read the old folder still starts; it just starts empty.
            }
            return copied;
        }

        // Creating the folder is not enough to prove it is usable: a Program Files install happily
        // creates one and then refuses every file inside it. The check is therefore a real write.
        private static bool TryPrepare(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, WriteProbeName);
                using (FileStream stream = new FileStream(probe, FileMode.Create, FileAccess.Write))
                    stream.WriteByte(0);
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SameDirectory(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ArgumentValue(string[] args, string prefix)
        {
            if (args == null) return "";
            foreach (string argument in args)
            {
                if (argument == null) continue;
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return argument.Substring(prefix.Length).Trim('"');
            }
            return "";
        }
    }

    // The program does not install itself and does not write to the registry. Starting with Windows
    // is still available, but it is done the portable way: a launcher in the user's Startup folder,
    // which is a file that can be seen, copied or deleted. The registry key older versions used is
    // read so the menu entry keeps telling the truth after an upgrade, and it is deleted the first
    // time the setting is touched - it is never written to.
    internal static class StartupManager
    {
        private const string LegacyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // The value name the previous release wrote. It is only ever read and deleted - this
        // program does not write to the registry at all - so it keeps the old name in order to go
        // on recognising an upgrade's setting.
        private const string LegacyValueName = "MyQuoteCard";
        private const string ShortcutName = "XiaoJian.lnk";
        private const string CommandName = "XiaoJian.cmd";
        // The launcher file names, and the executable name, the previous release shipped under.
        // Kept so an upgrade's autostart can be carried over instead of quietly breaking.
        private const string LegacyShortcutName = "MyQuoteCard.lnk";
        private const string LegacyCommandName = "MyQuoteCard.cmd";
        private const string LegacyExecutableName = "MyQuoteCard.exe";

        public static bool IsEnabled()
        {
            if (PointsAtThisProgram(ShortcutTarget(ShortcutPath))) return true;
            if (PointsAtThisProgram(CommandTarget(CommandPath))) return true;
            if (PointsAtThisProgram(ShortcutTarget(LegacyShortcutPath))) return true;
            if (PointsAtThisProgram(CommandTarget(LegacyCommandPath))) return true;
            return PointsAtThisProgram(LegacyRegistryValue());
        }

        public static void SetEnabled(bool enabled)
        {
            RemoveLegacyEntry();
            if (!enabled)
            {
                DeleteQuietly(ShortcutPath);
                DeleteQuietly(CommandPath);
                DeleteQuietly(LegacyShortcutPath);
                DeleteQuietly(LegacyCommandPath);
                return;
            }

            // A shortcut is what Windows itself puts in the Startup folder, and it starts the
            // program without flashing a console window. Where the script host is locked down the
            // fallback is a one-line batch file, which is uglier but always works.
            if (TryWriteShortcut()) return;
            WriteCommandFile();
        }

        // The previous release's launcher is named after the previous release and points at the
        // executable this one replaced. Left where it is, it would sit in the Startup folder
        // starting nothing, and the setting would look as if it had turned itself off. It is
        // rewritten to start this copy instead, and the old file removed. A launcher that is not
        // this program's own setting - one pointing at some other installation - is left alone.
        public static void CarryOverLegacyLauncher()
        {
            bool ours = PointsAtThisProgram(ShortcutTarget(LegacyShortcutPath)) ||
                PointsAtThisProgram(CommandTarget(LegacyCommandPath));
            if (!ours && !File.Exists(LegacyShortcutPath) && !File.Exists(LegacyCommandPath)) return;
            if (ours) SetEnabled(true);
            DeleteQuietly(LegacyShortcutPath);
            DeleteQuietly(LegacyCommandPath);
        }

        private static string ShortcutPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    ShortcutName);
            }
        }

        private static string CommandPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    CommandName);
            }
        }

        private static string LegacyShortcutPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    LegacyShortcutName);
            }
        }

        private static string LegacyCommandPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    LegacyCommandName);
            }
        }

        private static string ExecutablePath
        {
            get { return System.Windows.Forms.Application.ExecutablePath; }
        }

        // The launcher has to start *this* copy of the program: a user who keeps two - one portable
        // on a stick, one installed - would otherwise find the wrong one starting.
        private static bool PointsAtThisProgram(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            if (target.IndexOf(ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // A launcher written by the previous release names the executable this one replaced, in
            // this folder. That is this program's own setting, carried over rather than dropped.
            string previous = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LegacyExecutableName);
            return target.IndexOf(previous, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ShortcutTarget(string path)
        {
            if (!File.Exists(path)) return "";
            try
            {
                object shortcut = OpenShortcut(path);
                if (shortcut == null) return path;
                object target = shortcut.GetType().InvokeMember("TargetPath",
                    BindingFlags.GetProperty, null, shortcut, null);
                return target as string;
            }
            catch (Exception)
            {
                // Without the script host the shortcut cannot be read. Its presence is then the
                // best answer available, and this program is the only thing that writes it.
                return path;
            }
        }

        private static string CommandTarget(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string LegacyRegistryValue()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacyKeyPath, false))
                {
                    if (key == null) return "";
                    string value = key.GetValue(LegacyValueName) as string;
                    return value == null ? "" : value;
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static void RemoveLegacyEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacyKeyPath, true))
                {
                    if (key != null) key.DeleteValue(LegacyValueName, false);
                }
            }
            catch (Exception)
            {
                // The key may be locked down. Nothing depends on the cleanup succeeding.
            }
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A launcher that cannot be removed is reported by IsEnabled, which is what the
                // menu entry reads.
            }
        }

        private static object OpenShortcut(string path)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object shell = Activator.CreateInstance(shellType);
            return shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { path });
        }

        private static bool TryWriteShortcut()
        {
            try
            {
                object shortcut = OpenShortcut(ShortcutPath);
                if (shortcut == null) return false;
                Type type = shortcut.GetType();
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                    new object[] { ExecutablePath });
                type.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut,
                    new object[] { "--startup" });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { AppDomain.CurrentDomain.BaseDirectory });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                    new object[] { "XiaoJian" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return File.Exists(ShortcutPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Written in the ANSI code page, not UTF-8: cmd.exe reads batch files in the OEM code page,
        // so a UTF-8 path with Chinese characters in it would be garbled into a failed launch.
        private static void WriteCommandFile()
        {
            using (StreamWriter writer = new StreamWriter(CommandPath, false, Encoding.Default))
            {
                writer.WriteLine("@echo off");
                writer.WriteLine("start \"\" \"" + ExecutablePath + "\" --startup");
            }
        }
    }

    internal static class SelfTests
    {
        private static string B(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "XiaoJianTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string statePath = Path.Combine(root, "state.ini");
                string quotePath = Path.Combine(root, "quotes.txt");
                AppState state = AppState.Load(statePath);
                if (state.Cards.Count != 1) return 34;
                if (ResizeSupport.HitTest(new System.Drawing.Size(460, 190),
                        new System.Drawing.Point(0, 0)) != ResizeSupport.TopLeft ||
                    ResizeSupport.HitTest(new System.Drawing.Size(460, 190),
                        new System.Drawing.Point(459, 189)) != ResizeSupport.BottomRight ||
                    ResizeSupport.HitTest(new System.Drawing.Size(460, 190),
                        new System.Drawing.Point(230, 95)) != ResizeSupport.Client) return 25;
                CardState card = state.Cards[0];
                if (card.BackgroundColorArgb != unchecked((int)0xFFFFFFFF) ||
                    card.TextColorArgb != unchecked((int)0xFF000000) ||
                    state.ThemeDefaultVersion != 2 || card.TransparentBackground ||
                    state.TransparencyDefaultVersion != 2) return 10;
                QuoteRepository repository = new QuoteRepository(quotePath, state);
                repository.SaveEntries(new string[] { "第一句", "第二句", "第二句", "  " });
                if (repository.Load().Count != 2) return 11;

                DailyQuotePicker picker = new DailyQuotePicker(state, card, repository);
                string first = picker.EnsureToday();
                string again = picker.EnsureToday();
                if (first != again) return 12;
                string other = picker.PickAnother();
                if (other == first) return 13;

                card.BackgroundColorArgb = unchecked((int)0xFF123456);
                card.TextColorArgb = unchecked((int)0xFFF0E0D0);
                card.FontName = "Arial";
                card.FontStyleValue = 1;
                card.BackgroundImagePath = Path.Combine(root, "background.png");
                card.TransparentBackground = true;
                card.AutoFitText = false;
                state.Save();

                AppState reloaded = AppState.Load(statePath);
                if (reloaded.Cards.Count != 1) return 35;
                CardState reloadedCard = reloaded.Cards[0];
                if (reloadedCard.TodayQuote != other || reloadedCard.TodayDate.Length != 10) return 14;
                if (reloadedCard.BackgroundColorArgb != unchecked((int)0xFF123456) ||
                    reloadedCard.TextColorArgb != unchecked((int)0xFFF0E0D0) ||
                    reloadedCard.FontName != "Arial" || reloadedCard.FontStyleValue != 1 ||
                    !reloadedCard.TransparentBackground || reloadedCard.AutoFitText ||
                    reloaded.ThemeDefaultVersion != 2 ||
                    reloaded.TransparencyDefaultVersion != 2) return 15;

                // Several cards must keep their geometry, look and content source apart.
                string multiPath = Path.Combine(root, "multi-state.ini");
                AppState multi = AppState.Load(multiPath);
                multi.Cards[0].X = 100;
                multi.Cards[0].Y = 120;
                multi.Cards[0].FontName = "Arial";
                multi.Cards[0].VerticalText = true;
                CardState second = multi.CreateCard();
                if (second.X != 136 || second.Y != 156 || !second.VerticalText) return 36;
                second.X = 640;
                second.Y = 40;
                second.VerticalText = false;
                second.FontName = "SimSun";
                second.ContentMode = CardState.ContentModeFixed;
                second.FixedText = "固定的一句话";
                CardState third = multi.CreateCard();
                // Well below the rectangle the fixed floor used to allow, so the round trip proves
                // a small card is stored and restored as-is.
                third.CardWidth = 200;
                third.CardHeight = 110;
                if (multi.Cards.Count != 3) return 37;
                multi.Save();

                AppState multiReloaded = AppState.Load(multiPath);
                if (multiReloaded.Cards.Count != 3) return 38;
                if (multiReloaded.Cards[0].X != 100 || multiReloaded.Cards[0].Y != 120 ||
                    multiReloaded.Cards[0].FontName != "Arial" || !multiReloaded.Cards[0].VerticalText) return 39;
                if (multiReloaded.Cards[1].X != 640 || multiReloaded.Cards[1].VerticalText ||
                    multiReloaded.Cards[1].FontName != "SimSun" ||
                    !multiReloaded.Cards[1].IsFixedContent ||
                    multiReloaded.Cards[1].FixedText != "固定的一句话") return 40;
                if (multiReloaded.Cards[2].CardWidth != 200 || multiReloaded.Cards[2].CardHeight != 110) return 41;
                // Slots are handed out once and survive a round trip, which is what keeps a card
                // reading the same content library across sessions.
                if (multiReloaded.Cards[0].Slot != 1 || multiReloaded.Cards[1].Slot != 2 ||
                    multiReloaded.Cards[2].Slot != 3) return 83;
                if (multiReloaded.NextCardSlot != 4) return 84;
                if (!multiReloaded.RemoveCard(multiReloaded.Cards[2]) ||
                    multiReloaded.Cards.Count != 2) return 42;
                if (!multiReloaded.RemoveCard(multiReloaded.Cards[1])) return 43;
                if (multiReloaded.RemoveCard(multiReloaded.Cards[0])) return 44;

                // The only bounds left in the stored model are a guard against a hand-edited file:
                // a nonsense number must be pulled back, and a genuinely small card must survive.
                string wildPath = Path.Combine(root, "wild-size-state.ini");
                AppState wildState = AppState.Load(wildPath);
                wildState.Cards[0].CardWidth = 99999;
                wildState.Cards[0].CardHeight = 1;
                wildState.Save();
                AppState wildReloaded = AppState.Load(wildPath);
                if (wildReloaded.Cards[0].CardWidth != CardState.MaxCardWidth ||
                    wildReloaded.Cards[0].CardHeight != CardState.MinCardHeight) return 64;

                // A pinned card ignores the library; the daily rotation must not touch it.
                AppState fixedState = AppState.Load(Path.Combine(root, "fixed-state.ini"));
                QuoteRepository fixedRepository = new QuoteRepository(
                    Path.Combine(root, "fixed-quotes.txt"), fixedState);
                fixedRepository.SaveEntries(new string[] { "库里的句子" });
                CardState fixedCard = fixedState.Cards[0];
                DailyQuotePicker fixedPicker = new DailyQuotePicker(fixedState, fixedCard, fixedRepository);
                if (fixedPicker.SetFixedText("钉住的话") != "钉住的话") return 45;
                if (fixedPicker.EnsureToday() != "钉住的话") return 46;
                fixedPicker.SetFixedText("");
                if (fixedPicker.EnsureToday() != fixedPicker.FixedPlaceholder) return 47;
                if (fixedPicker.PickAnother() != "库里的句子") return 48;
                if (fixedCard.IsFixedContent) return 49;

                // Every card keeps its own content library, in a file named after its slot. The one
                // shared library older versions wrote has to end up on the first card - renamed, so
                // no stale file is left behind - and a second migration must not disturb it.
                string contentRoot = Path.Combine(root, "content-root");
                Directory.CreateDirectory(contentRoot);
                AppState libraryState = AppState.Load(Path.Combine(contentRoot, "state.ini"));
                CardState libraryFirst = libraryState.Cards[0];
                CardState librarySecond = libraryState.CreateCard();
                if (libraryFirst.Slot == librarySecond.Slot) return 70;
                string firstLibrary = QuoteRepository.CardContentPath(contentRoot, libraryFirst);
                string secondLibrary = QuoteRepository.CardContentPath(contentRoot, librarySecond);
                if (string.Equals(firstLibrary, secondLibrary, StringComparison.OrdinalIgnoreCase)) return 71;
                if (!firstLibrary.EndsWith("content-1.txt", StringComparison.OrdinalIgnoreCase)) return 72;
                File.WriteAllLines(Path.Combine(contentRoot, "quotes.txt"),
                    new string[] { "旧版共享库的一句话" }, new UTF8Encoding(false));
                QuoteRepository.MigrateSharedLibrary(contentRoot, libraryState);
                if (!File.Exists(firstLibrary)) return 73;
                if (File.Exists(Path.Combine(contentRoot, "quotes.txt"))) return 74;
                if (new QuoteRepository(firstLibrary, libraryState).Load()[0] != "旧版共享库的一句话") return 75;
                QuoteRepository.MigrateSharedLibrary(contentRoot, libraryState);
                if (new QuoteRepository(firstLibrary, libraryState).Load()[0] != "旧版共享库的一句话") return 76;
                QuoteRepository secondLibraryRepository =
                    QuoteRepository.ForCard(libraryState, contentRoot, librarySecond);
                secondLibraryRepository.SaveEntries(new string[] { "第二张卡片的内容" });
                if (secondLibraryRepository.Load()[0] != "第二张卡片的内容") return 77;
                if (new QuoteRepository(firstLibrary, libraryState).Load()[0] != "旧版共享库的一句话") return 78;

                // A hand-picked font size is stored as it is and pulled back only when the file
                // holds something a card could never use.
                if (libraryFirst.ManualFontSize != 0f) return 79;
                libraryFirst.ManualFontSize = 19.5f;
                libraryState.Save();
                AppState libraryReloaded = AppState.Load(Path.Combine(contentRoot, "state.ini"));
                if (libraryReloaded.Cards.Count != 2) return 80;
                if (Math.Abs(libraryReloaded.Cards[0].ManualFontSize - 19.5f) > 0.01f) return 81;
                if (libraryReloaded.Cards[0].Slot != libraryFirst.Slot ||
                    libraryReloaded.Cards[1].Slot != librarySecond.Slot) return 82;
                libraryReloaded.Cards[0].ManualFontSize = 999f;
                libraryReloaded.Cards[0].Clamp();
                if (libraryReloaded.Cards[0].ManualFontSize != CardState.MaxManualFontSize) return 85;
                libraryReloaded.Cards[0].ManualFontSize = -5f;
                libraryReloaded.Cards[0].Clamp();
                if (libraryReloaded.Cards[0].ManualFontSize != 0f) return 86;

                // Cards written before multiple cards existed keep every field they had.
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
                AppState migratedState = AppState.Load(legacyPath);
                if (migratedState.Cards.Count != 1) return 50;
                CardState migratedCard = migratedState.Cards[0];
                if (migratedCard.X != 310 || migratedCard.Y != 88 ||
                    migratedCard.CardWidth != 500 || migratedCard.CardHeight != 220 ||
                    !migratedCard.TopMost || !migratedCard.ClickThrough ||
                    !migratedCard.TransparentBackground || migratedCard.AutoFitText ||
                    migratedCard.FontAdjustment != 4 || migratedCard.FontName != "SimSun" ||
                    migratedCard.FontStyleValue != 2 ||
                    migratedCard.TodayQuote != "旧卡片的一句话" ||
                    migratedCard.TodayDate != "2026-01-02") return 51;
                if (!UiText.IsEnglish(migratedState) || !migratedState.HintShown) return 52;
                migratedState.Save();
                AppState migratedAgain = AppState.Load(legacyPath);
                if (migratedAgain.Cards.Count != 1 || migratedAgain.Cards[0].X != 310 ||
                    migratedAgain.Cards[0].TodayQuote != "旧卡片的一句话") return 53;

                // Vertical typography: columns fill top to bottom, explicit breaks start a column.
                List<string> columns = VerticalTextLayout.SplitColumns("一二三四五", 2);
                if (columns.Count != 3 || columns[0] != "一二" || columns[1] != "三四" ||
                    columns[2] != "五") return 54;
                List<string> broken = VerticalTextLayout.SplitColumns("上\n下", 8);
                if (broken.Count != 2 || broken[0] != "上" || broken[1] != "下") return 55;
                if (VerticalTextLayout.SplitColumns("", 4).Count != 0) return 56;
                if (VerticalTextLayout.CharactersPerColumn(100, 25) != 4) return 57;
                if (VerticalTextLayout.CharactersPerColumn(10, 25) != 1) return 58;

                string transparencyPath = Path.Combine(root, "transparency-state.ini");
                AppState oldTransparencyState = AppState.Load(transparencyPath);
                oldTransparencyState.Cards[0].TransparentBackground = true;
                oldTransparencyState.TransparencyDefaultVersion = 1;
                oldTransparencyState.Save();
                AppState resetTransparencyState = AppState.Load(transparencyPath);
                if (resetTransparencyState.Cards[0].TransparentBackground ||
                    resetTransparencyState.TransparencyDefaultVersion != 2) return 27;

                string contrastPath = Path.Combine(root, "contrast-state.ini");
                AppState contrastState = AppState.Load(contrastPath);
                contrastState.Cards[0].BackgroundColorArgb = unchecked((int)0xFFFFFFFF);
                contrastState.Cards[0].TextColorArgb = unchecked((int)0xFFFFFFFF);
                contrastState.ThemeDefaultVersion = 1;
                contrastState.Save();
                AppState repairedContrast = AppState.Load(contrastPath);
                if (repairedContrast.Cards[0].TextColorArgb != unchecked((int)0xFF000000) ||
                    repairedContrast.ThemeDefaultVersion != 2) return 19;
                using (QuoteCardForm form = new QuoteCardForm(reloaded, reloadedCard, repository, false))
                {
                    form.CreateControl();
                    if (form.Text != "小笺") return 16;
                    if (!form.ClearMenuLayoutActive) return 18;
                    if (!form.LayerChoiceActive) return 23;
                    form.Location = new System.Drawing.Point(-10000, -10000);
                    form.Show();
                    System.Windows.Forms.Application.DoEvents();
                    if (!form.TransparentHitBoxActive) return 17;
                    System.Drawing.Rectangle hitFrame = form.TransparentHitFrameBounds;
                    if (hitFrame.IsEmpty ||
                        hitFrame.Width >= form.Bounds.Width || hitFrame.Height >= form.Bounds.Height ||
                        !form.Bounds.Contains(hitFrame)) return 28;
                    if (form.ClickThroughStyleActive) return 29;
                    reloadedCard.ClickThrough = true;
                    form.ApplyClickThroughState();
                    if (!form.ClickThroughStyleActive) return 30;
                    if (form.TransparentHitBoxActive) return 31;
                    reloadedCard.ClickThrough = false;
                    form.ApplyClickThroughState();
                    if (form.ClickThroughStyleActive) return 32;
                    if (!form.TransparentHitBoxActive) return 33;
                    System.Drawing.Size horizontal = form.QuoteBlockSize;
                    if (horizontal.Width <= 0 || horizontal.Height <= 0) return 59;
                    form.SetVerticalText(true);
                    if (!form.VerticalTextActive) return 60;
                    System.Drawing.Size vertical = form.QuoteBlockSize;
                    if (vertical.Width <= 0 || vertical.Height <= 0) return 61;
                    System.Drawing.Rectangle verticalFrame = form.TransparentHitFrameBounds;
                    if (verticalFrame.IsEmpty || !form.Bounds.Contains(verticalFrame)) return 62;
                    form.SetVerticalText(false);
                    if (form.VerticalTextActive) return 63;

                    // Free resizing: the floor is content-driven and never sits above the card it
                    // belongs to. Fitting to the text then has to land the card on that floor, with
                    // the text still whole - a card that bottomed out clipping its own text, or one
                    // that could not be made smaller than the old fixed rectangle, fails here.
                    if (form.MinimumCardSize.Width > form.Width ||
                        form.MinimumCardSize.Height > form.Height) return 65;
                    System.Drawing.Size beforeFit = form.Size;
                    form.FitCardToText();
                    if (form.Width > beforeFit.Width || form.Height > beforeFit.Height) return 66;
                    if (form.Width < form.MinimumCardSize.Width ||
                        form.Height < form.MinimumCardSize.Height) return 67;
                    if (!form.QuoteFitsCard) return 68;
                    // Both dimensions land below the old fixed floor - that is what "resizable to
                    // any size, down to the text" means.
                    if (form.Width >= 340 || form.Height >= 140) return 69;
                    IntPtr locateResult;
                    IntPtr locateSent = NativeMethods.SendMessageTimeout(form.Handle,
                        (uint)QuoteCardForm.LocateMessage, IntPtr.Zero, IntPtr.Zero,
                        0x0002, 1000, out locateResult);
                    System.Windows.Forms.Application.DoEvents();
                    if (locateSent == IntPtr.Zero ||
                        locateResult.ToInt32() != QuoteCardForm.LocateResponse ||
                        !form.LocateModeActive) return 26;

                    // A size picked by hand has to hold on a card that is only as big as its text:
                    // that is exactly where the automatic fit used to hand back the same size
                    // however often the entry was clicked. The card grows to hold the new size
                    // instead of showing it clipped.
                    float beforeFont = form.QuoteFontSize;
                    form.ApplyManualFontSize(beforeFont + 4.0f);
                    if (reloadedCard.AutoFitText) return 87;
                    if (reloadedCard.ManualFontSize < beforeFont + 3.5f) return 88;
                    if (form.QuoteFontSize < beforeFont + 3.5f) return 89;
                    if (!form.QuoteFitsCard) return 90;
                    // A smaller size is just as much the user's, and must not be undone either.
                    form.ApplyManualFontSize(beforeFont - 2.0f);
                    if (form.QuoteFontSize > beforeFont - 1.5f) return 91;
                    // "Smallest" is an absolute size like the other entries, not an offset from
                    // whatever the card happened to work out for itself.
                    form.ApplyManualFontSize(CardState.MinManualFontSize);
                    if (Math.Abs(form.QuoteFontSize - CardState.MinManualFontSize) > 0.05f) return 92;
                    if (Math.Abs(reloadedCard.ManualFontSize - CardState.MinManualFontSize) > 0.05f) return 93;

                    // Neither axis may be held open by a fixed number any more. The degenerate floor
                    // is the guard for a card that measures nothing at all, so it has to sit below
                    // the smallest card the text alone can ask for - one glyph plus the slack and the
                    // padding - otherwise it becomes the very fixed minimum these releases removed.
                    // Read through locals so the compiler cannot fold these away: they are here to
                    // catch a later edit to any of the constants, not to describe today's values.
                    int degenerateHeight = CardState.MinCardHeight;
                    int oneLineHeight = CardSizeLimits.SmallestLineHeight + CardSizeLimits.TextSlack +
                        CardSizeLimits.VerticalMargin;
                    int degenerateWidth = CardState.MinCardWidth;
                    int oneGlyphWidth = CardSizeLimits.SmallestGlyphWidth + CardSizeLimits.TextSlack +
                        CardSizeLimits.HorizontalMargin;
                    if (degenerateHeight >= oneLineHeight) return 116;
                    if (degenerateWidth >= oneGlyphWidth) return 125;
                    // ... and the padding either side of the text is no longer the larger of the two.
                    // It was 32 against 12, and that padding - not the text - was the fixed minimum
                    // width: a four character card was 107 pixels wide to hold 37 pixels of text.
                    int acrossPadding = CardSizeLimits.HorizontalMargin;
                    int downPadding = CardSizeLimits.VerticalMargin;
                    if (acrossPadding > downPadding) return 126;
                    // The padding is the rounded corner's clearance, so it has to be at least what
                    // the corner cuts away - and now that the radius is a setting, what it has to
                    // clear is the *largest* radius a card can be given, not the default one.
                    //
                    // The cut at the deepest point is R * (1 - 1/sqrt(2)): 11.7 pixels at the
                    // ceiling of 40, against a 12 pixel pad. This used to compare against R/2,
                    // which is both looser and unrelated to the geometry - it would have let a
                    // ceiling of 40 through while the padding covered only 20.
                    double cornerCut = CardState.MaxCornerRadius * (1.0 - 1.0 / Math.Sqrt(2.0));
                    int padLeft = CardSizeLimits.TextPadLeft;
                    int padRight = CardSizeLimits.TextPadRight;
                    if (padLeft < cornerCut || padRight < cornerCut) return 130;

                    // The floor arithmetic, pinned on a block whose measurement is known so no GDI is
                    // involved: the numbers below are the arithmetic itself, not whatever a font
                    // happens to measure today. The probe walk is geometric (10 12 14 16 19 22 25 29
                    // 33 38 43 49 56 64 73 83 94 106 120 136 154 174 ...), so a fixed 160x100 block
                    // is first accepted at probe 174 across and at 106 down, and the floor is that
                    // probe plus the padding on the probed axis.
                    CardSizeLimits.Measure fixedBlock = delegate(int viewWidth, int viewHeight)
                    {
                        return new System.Drawing.Size(160, 100);
                    };
                    System.Drawing.Size flatFloor = CardSizeLimits.MinimumFor(fixedBlock, false);
                    if (flatFloor.Width != 174 + CardSizeLimits.HorizontalMargin ||
                        flatFloor.Height != 100 + CardSizeLimits.TextSlack +
                            CardSizeLimits.VerticalMargin) return 127;
                    System.Drawing.Size uprightFloor = CardSizeLimits.MinimumFor(fixedBlock, true);
                    if (uprightFloor.Width != 160 + CardSizeLimits.TextSlack +
                            CardSizeLimits.HorizontalMargin ||
                        uprightFloor.Height != 106 + CardSizeLimits.VerticalMargin) return 129;

                    // The drag floor is a curve, not a point, and this is where that shows. A block
                    // that wraps the way horizontal text does - ten glyphs 10 wide and 12 tall, so a
                    // text area holding n of them across is ceil(10/n) lines tall. Widening the card
                    // must lower the height floor; and the width floor must stay at the one-character
                    // card however tall the card is, because the floor never reads the axis it holds.
                    CardSizeLimits.Measure wrapping = delegate(int viewWidth, int viewHeight)
                    {
                        int across = Math.Max(1, viewWidth / 10);
                        int lines = (10 + across - 1) / across;
                        return new System.Drawing.Size(Math.Min(10, across) * 10, lines * 12);
                    };
                    System.Drawing.Size unit = CardSizeLimits.SmallestTextCard;
                    System.Drawing.Size floorNarrow = CardSizeLimits.DragLimitFor(wrapping, false,
                        CardSizeLimits.HorizontalMargin + 20);
                    System.Drawing.Size floorWide = CardSizeLimits.DragLimitFor(wrapping, false,
                        CardSizeLimits.HorizontalMargin + 100);
                    if (floorNarrow.Width != unit.Width || floorWide.Width != unit.Width) return 138;
                    if (floorNarrow.Height != 5 * 12 + CardSizeLimits.TextSlack +
                            CardSizeLimits.VerticalMargin) return 139;
                    if (floorWide.Height != unit.Height) return 140;

                    // ... and the vertical mirror. Columns are set by the height, so a taller card
                    // needs fewer of them and its width floor drops.
                    CardSizeLimits.Measure stacking = delegate(int viewWidth, int viewHeight)
                    {
                        int down = Math.Max(1, viewHeight / 12);
                        int stacks = (10 + down - 1) / down;
                        return new System.Drawing.Size(stacks * 10, Math.Min(10, down) * 12);
                    };
                    System.Drawing.Size widthShort = CardSizeLimits.DragLimitFor(stacking, true,
                        CardSizeLimits.VerticalMargin + 24);
                    System.Drawing.Size widthTall = CardSizeLimits.DragLimitFor(stacking, true,
                        CardSizeLimits.VerticalMargin + 120);
                    if (widthShort.Height != unit.Height || widthTall.Height != unit.Height) return 141;
                    if (widthShort.Width != 5 * 10 + CardSizeLimits.TextSlack +
                            CardSizeLimits.HorizontalMargin) return 142;
                    if (widthTall.Width != unit.Width) return 143;

                    // FlowFitFor 是 DragLimitFor 的逆运算，也是「横跨流向那根轴顶到桌面」时唯一
                    // 的出路：沿流向轴把文字摊长，直到剩下的那块装得下。因为折出来的行/列只会
                    // 越来越少，第一个装得下的候选也就是最小的那个 —— 顺序反了就变成「越长越好」。
                    //
                    // 那一行 20 宽 12 高的十个字：视图给 n 个就折成 ceil(10/n) 行。
                    //   across = 64 → 折 3 行（块高 36）够 → 探针 49，卡片 73x64
                    //   across = 52 → 折 2 行（块高 24）才够 → 探针 56，卡片 80x52
                    // 可用高度越紧，卡片越宽 —— 这正是这个方法存在的理由。
                    System.Drawing.Size flowLoose = CardSizeLimits.FlowFitFor(wrapping, false,
                        3 * 12 + CardSizeLimits.TextSlack + CardSizeLimits.VerticalMargin);
                    System.Drawing.Size flowTight = CardSizeLimits.FlowFitFor(wrapping, false,
                        2 * 12 + CardSizeLimits.TextSlack + CardSizeLimits.VerticalMargin);
                    if (flowLoose.Width != 49 + CardSizeLimits.HorizontalMargin ||
                        flowLoose.Height != 3 * 12 + CardSizeLimits.TextSlack +
                            CardSizeLimits.VerticalMargin) return 144;
                    if (flowTight.Width != 56 + CardSizeLimits.HorizontalMargin ||
                        flowTight.Height != 2 * 12 + CardSizeLimits.TextSlack +
                            CardSizeLimits.VerticalMargin) return 145;
                    if (flowTight.Width <= flowLoose.Width) return 146;
                    // 全都装不下时返回最长的那一个：那是留在卡片外的文字最少的一个。
                    if (CardSizeLimits.FlowFitFor(wrapping, false,
                            CardSizeLimits.VerticalMargin + 1).Width <= flowTight.Width) return 147;

                    // ... 竖排的镜像：列数由高度决定，可用宽度越紧，卡片越高。
                    System.Drawing.Size stackLoose = CardSizeLimits.FlowFitFor(stacking, true,
                        10 * 10 + CardSizeLimits.TextSlack + CardSizeLimits.HorizontalMargin);
                    System.Drawing.Size stackTight = CardSizeLimits.FlowFitFor(stacking, true,
                        5 * 10 + CardSizeLimits.TextSlack + CardSizeLimits.HorizontalMargin);
                    if (stackLoose.Height != 16 + CardSizeLimits.VerticalMargin) return 148;
                    if (stackTight.Height != 29 + CardSizeLimits.VerticalMargin) return 149;
                    if (stackTight.Height <= stackLoose.Height) return 150;

                    // A floor is only a floor if the card can be dragged down onto it and still show
                    // the whole text there - not merely a number the layout claims to allow.
                    //
                    // The floor constrains one axis only. Horizontal text runs left to right, so what
                    // it constrains is the height: the width decides how many lines the text wraps
                    // onto, and the lines decide the height. The width is left free down to the
                    // one-character card. That the width floor is that constant - rather than a width
                    // read off the block - is the whole point of this release.
                    System.Drawing.Size tight = form.MinimumCardSize;
                    // unit, declared with the floor arithmetic above, is the one-character card the
                    // width floor is supposed to sit on.
                    System.Drawing.Size tightBlock = form.QuoteBlockSize;
                    if (tight.Width != unit.Width) return 118;
                    if (tight.Height != Math.Max(unit.Height,
                            tightBlock.Height + CardSizeLimits.TextSlack + CardSizeLimits.VerticalMargin))
                        return 117;
                    form.Size = new System.Drawing.Size(tight.Width, tight.Height);
                    if (form.Width != tight.Width || form.Height != tight.Height) return 119;
                    if (!form.QuoteFitsCard) return 120;
                    // What gives way on a card that small is the corner button, never the card.
                    if (form.MenuButtonVisible) return 121;
                    System.Drawing.Size roomy = new System.Drawing.Size(
                        Math.Max(tight.Width, CardSizeLimits.MenuButtonMinCardWidth),
                        Math.Max(tight.Height, CardSizeLimits.MenuButtonMinCardHeight));
                    form.Size = roomy;
                    if (!form.MenuButtonVisible) return 122;
                    // ... and the button stays inside the card even at the very bottom corner.
                    if (!form.Bounds.Contains(form.MenuButtonBoundsInCard)) return 123;
                    // Each threshold on its own takes the button away again - as far as the card's
                    // own floor leaves room to get below it.
                    if (roomy.Height > tight.Height)
                    {
                        form.Size = new System.Drawing.Size(roomy.Width,
                            CardSizeLimits.MenuButtonMinCardHeight - 1);
                        if (form.MenuButtonVisible) return 124;
                    }
                    if (roomy.Width > tight.Width)
                    {
                        form.Size = new System.Drawing.Size(CardSizeLimits.MenuButtonMinCardWidth - 1,
                            roomy.Height);
                        if (form.MenuButtonVisible) return 128;
                    }
                    form.Size = new System.Drawing.Size(tight.Width, tight.Height);
                    if (form.MenuButtonVisible) return 131;

                    // Both complaints this release answers were the same mistake: the floor was one
                    // point applied as a box, so the ends of the curve were unreachable. A horizontal
                    // card could not be dragged down to a single line and a vertical card could not be
                    // pulled in to a single column, however wide or tall it was made. Widening a
                    // horizontal card must therefore lower its height floor - and the card must really
                    // be draggable onto that lower floor with the text still whole.
                    //
                    // Automatic fitting has to be back on first. The font size entries above turned it
                    // off, and with it off the floor stands aside rather than being applied - the card
                    // may legitimately be smaller than the text, and growing it would re-measure the
                    // font and move the floor again. Reading MinimumCardSize then returns the value
                    // left over from an earlier resize, not the floor this width asks for. This is the
                    // same path the menu's "default font size" entry takes; SetVerticalText recomputes
                    // the font unconditionally, so it serves as the refresh.
                    reloadedCard.AutoFitText = true;
                    reloadedCard.ManualFontSize = 0f;
                    form.SetVerticalText(false);
                    int tallFloor = form.MinimumCardSize.Height;
                    form.Size = new System.Drawing.Size(200, tallFloor);
                    int shortFloor = form.MinimumCardSize.Height;
                    if (shortFloor >= tallFloor) return 132;
                    form.Size = new System.Drawing.Size(200, shortFloor);
                    if (form.Height != shortFloor) return 133;
                    if (!form.QuoteFitsCard) return 134;

                    // The mirror image, and the other half of the complaint: a vertical card made
                    // taller must be pullable in to a narrower floor.
                    form.SetVerticalText(true);
                    int wideFloor = form.MinimumCardSize.Width;
                    form.Size = new System.Drawing.Size(wideFloor, 200);
                    int narrowFloor = form.MinimumCardSize.Width;
                    if (narrowFloor >= wideFloor) return 135;
                    form.Size = new System.Drawing.Size(narrowFloor, 200);
                    if (form.Width != narrowFloor) return 136;
                    if (!form.QuoteFitsCard) return 137;
                    form.SetVerticalText(false);
                    form.Size = new System.Drawing.Size(tight.Width, tight.Height);

                    // "The text will not get any bigger." The floor only constrains one axis, and
                    // once that axis has reached the desktop the only way left is to lengthen the
                    // run of text - FlowFitFor. Reaching for the least-area card there is wrong: at
                    // a large font it is the one-character-per-line sliver, narrower than the card
                    // already is, so nothing grows at all and every extra point of font is simply
                    // clipped away. That is what the complaint describes: the number keeps
                    // climbing and nothing on screen changes.
                    //
                    // Every click has to raise the size, and the text has to stay whole the whole
                    // way - unless the card has genuinely filled the desktop, which is the end of
                    // the road and a legitimate place to stop.
                    float beforeRaising = form.QuoteFontSize;
                    for (int step = 0; step < 20; step++)
                    {
                        float asked = form.QuoteFontSize + 2.0f;
                        System.Drawing.Size beforeRaisingSize = form.Size;
                        form.ApplyManualFontSize(asked);
                        if (!form.QuoteFitsCard) return 151;
                        bool filledDesktop = form.Width >= form.MaximumSize.Width ||
                            form.Height >= form.MaximumSize.Height;
                        if (!filledDesktop && form.QuoteFontSize < asked - 0.05f) return 152;
                        if (form.Width < beforeRaisingSize.Width ||
                            form.Height < beforeRaisingSize.Height) return 153;
                    }
                    if (form.QuoteFontSize <= beforeRaising + 1.0f) return 154;

                    // "The font size dialog stops having any effect above a certain number."
                    // The chooser used to keep only the family and the style and throw the size
                    // away, then FitQuoteFont worked the size out again from the card - so a size
                    // picked there did nothing at all. It only ever looked like it worked because
                    // automatic fitting's answer sits on the small side: picking a small value
                    // changed nothing visible, picking a large one plainly did nothing.
                    //
                    // ApplyPickedFont is the dialog's apply step without the modal window, so this
                    // is the same code the chooser runs.
                    float dialogBase = form.QuoteFontSize;
                    using (System.Drawing.Font sameSize =
                        new System.Drawing.Font(reloadedCard.FontName, dialogBase,
                            (System.Drawing.FontStyle)reloadedCard.FontStyleValue))
                    {
                        form.ApplyPickedFont(sameSize);
                    }
                    // Only the face changed, so the card keeps the size it had - and with automatic
                    // fitting on it keeps working that size out.
                    if (!reloadedCard.AutoFitText) return 155;
                    if (Math.Abs(form.QuoteFontSize - dialogBase) > 0.05f) return 156;
                    float dialogAsked = dialogBase + 8.0f;
                    using (System.Drawing.Font largerFace =
                        new System.Drawing.Font(reloadedCard.FontName, dialogAsked,
                            (System.Drawing.FontStyle)reloadedCard.FontStyleValue))
                    {
                        form.ApplyPickedFont(largerFace);
                    }
                    if (reloadedCard.AutoFitText) return 157;
                    if (Math.Abs(form.QuoteFontSize - dialogAsked) > 0.05f) return 158;
                    if (!form.QuoteFitsCard) return 159;
                    // A size past the ceiling is held at the ceiling, not silently ignored - and
                    // the card still has to hold the text.
                    using (System.Drawing.Font huge =
                        new System.Drawing.Font(reloadedCard.FontName, 200.0f,
                            (System.Drawing.FontStyle)reloadedCard.FontStyleValue))
                    {
                        form.ApplyPickedFont(huge);
                    }
                    if (form.QuoteFontSize <= dialogAsked + 0.05f) return 160;
                    if (form.QuoteFontSize > CardState.MaxManualFontSize + 0.05f) return 161;
                    if (!form.QuoteFitsCard) return 162;

                    // "The right-click icon updated, but the tray icon did not." The window icon
                    // comes out of the executable's own icon resource, which is why it followed
                    // card.ico; the tray icon was hand-drawn here as a dark disc with a white quote
                    // mark - the design from before card.ico was redrawn - so it stayed behind.
                    //
                    // ExtractIconEx is P/Invoke, so this is the only place the extraction is ever
                    // exercised. That the picture is the card rather than the old disc is decided
                    // by the mean brightness of every pixel: card.ico's entries measure 132..156
                    // (white paper), the drawn disc about 23 (near-black, most of its square, and
                    // the transparent rest counts as zero). 90 sits far from both.
                    using (System.Drawing.Icon tray = QuoteCardForm.CreateTrayIcon())
                    {
                        if (tray == null) return 163;
                        if (tray.Width <= 0 || tray.Height <= 0) return 164;
                        using (System.Drawing.Bitmap trayBitmap =
                            new System.Drawing.Bitmap(tray.Width, tray.Height))
                        {
                            using (System.Drawing.Graphics graphics =
                                System.Drawing.Graphics.FromImage(trayBitmap))
                                graphics.DrawIcon(tray,
                                    new System.Drawing.Rectangle(0, 0, tray.Width, tray.Height));
                            long total = 0;
                            for (int y = 0; y < trayBitmap.Height; y++)
                                for (int x = 0; x < trayBitmap.Width; x++)
                                {
                                    System.Drawing.Color pixel = trayBitmap.GetPixel(x, y);
                                    total += (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                                }
                            long mean = total / ((long)trayBitmap.Width * trayBitmap.Height);
                            if (mean < 90) return 165;
                        }
                    }

                    // The ceiling the automatic fit shrinks from used to be a 29-point constant
                    // buried in the search. On a card the size of the desktop that left four
                    // characters occupying a tenth of the width, and there was no way to ask for
                    // more. It is the card's own setting now.
                    form.SetVerticalText(false);
                    reloadedCard.AutoFitText = true;
                    reloadedCard.ManualFontSize = 0f;
                    form.Size = new System.Drawing.Size(900, 500);
                    form.SetAutoFitMax(CardState.DefaultAutoFitMaxFontSize);
                    float cappedSize = form.QuoteFontSize;
                    form.SetAutoFitMax(CardState.MaxManualFontSize);
                    if (form.QuoteFontSize <= cappedSize + 0.5f) return 166;
                    if (!form.QuoteFitsCard) return 167;
                    if (Math.Abs(reloadedCard.AutoFitMaxFontSize - CardState.MaxManualFontSize) >
                        0.05f) return 168;
                    // A hand-edited file cannot push it outside the range a size may take.
                    form.SetAutoFitMax(1000f);
                    if (Math.Abs(reloadedCard.AutoFitMaxFontSize - CardState.MaxManualFontSize) >
                        0.05f) return 169;
                    form.SetAutoFitMax(1f);
                    if (Math.Abs(reloadedCard.AutoFitMaxFontSize - CardState.MinAutoFitMaxFontSize) >
                        0.05f) return 170;

                    // Bold and italic are style bits on the same value the font dialog writes, so
                    // the two never disagree. Changing them changes the glyph widths, which is why
                    // the fit has to run again and the card has to be able to grow.
                    reloadedCard.FontStyleValue = 0;
                    form.SetAutoFitMax(CardState.DefaultAutoFitMaxFontSize);
                    form.SetFontStyle(System.Drawing.FontStyle.Bold, true);
                    if ((reloadedCard.FontStyleValue & (int)System.Drawing.FontStyle.Bold) == 0)
                        return 171;
                    if (!form.QuoteFitsCard) return 172;
                    form.SetFontStyle(System.Drawing.FontStyle.Italic, true);
                    if ((reloadedCard.FontStyleValue & (int)System.Drawing.FontStyle.Italic) == 0)
                        return 173;
                    if (!form.QuoteFitsCard) return 174;
                    // Turning one off leaves the other alone: they are independent bits.
                    form.SetFontStyle(System.Drawing.FontStyle.Bold, false);
                    if ((reloadedCard.FontStyleValue & (int)System.Drawing.FontStyle.Bold) != 0)
                        return 175;
                    if ((reloadedCard.FontStyleValue & (int)System.Drawing.FontStyle.Italic) == 0)
                        return 176;
                    form.SetFontStyle(System.Drawing.FontStyle.Italic, false);
                    if (reloadedCard.FontStyleValue != 0) return 177;
                    if (!form.QuoteFitsCard) return 178;

                    // Line and letter spacing change how much room the text needs, so the block the
                    // card has to hold has to change with them. The card is narrow here on purpose:
                    // with three characters on one line neither setting has anything to do.
                    form.SetVerticalText(false);
                    reloadedCard.AutoFitText = false;
                    form.ApplyManualFontSize(20f);
                    form.Size = new System.Drawing.Size(80, 400);
                    form.SetLineSpacing(CardState.DefaultLineSpacingPercent);
                    System.Drawing.Size defaultSpacingBlock = form.QuoteBlockSize;
                    form.SetLineSpacing(CardState.MaxLineSpacingPercent);
                    System.Drawing.Size looseBlock = form.QuoteBlockSize;
                    if (looseBlock.Height <= defaultSpacingBlock.Height) return 179;
                    if (!form.QuoteFitsCard) return 180;
                    form.SetLineSpacing(CardState.DefaultLineSpacingPercent);
                    if (form.QuoteBlockSize.Height != defaultSpacingBlock.Height) return 181;

                    form.SetLetterSpacing(CardState.DefaultLetterSpacingPercent);
                    System.Drawing.Size narrowBlock = form.QuoteBlockSize;
                    form.SetLetterSpacing(CardState.MaxLetterSpacingPercent);
                    System.Drawing.Size wideBlock = form.QuoteBlockSize;
                    if (wideBlock.Width <= narrowBlock.Width) return 182;
                    if (!form.QuoteFitsCard) return 183;

                    // Both are percentages with a range, and a hand-edited file cannot leave it.
                    form.SetLineSpacing(1000);
                    if (reloadedCard.LineSpacingPercent != CardState.MaxLineSpacingPercent) return 184;
                    form.SetLetterSpacing(-5);
                    if (reloadedCard.LetterSpacingPercent != CardState.MinLetterSpacingPercent)
                        return 185;
                    form.SetLineSpacing(CardState.DefaultLineSpacingPercent);
                    form.SetLetterSpacing(CardState.DefaultLetterSpacingPercent);
                    if (!form.QuoteFitsCard) return 186;

                    // The halo sits outside the glyphs, so it is part of the block the card has to
                    // hold - both dimensions grow, and turning it off puts the block back.
                    form.SetTextOutline(0);
                    System.Drawing.Size plainBlock = form.QuoteBlockSize;
                    form.SetTextOutline(12);
                    System.Drawing.Size haloBlock = form.QuoteBlockSize;
                    if (haloBlock.Width <= plainBlock.Width) return 187;
                    if (haloBlock.Height <= plainBlock.Height) return 188;
                    if (!form.QuoteFitsCard) return 189;
                    form.SetTextOutline(0);
                    if (form.QuoteBlockSize != plainBlock) return 190;
                    // Clamped at both ends, so a hand-edited file cannot escape the range.
                    form.SetTextOutline(1000);
                    if (reloadedCard.TextOutlinePercent != CardState.MaxTextOutlinePercent) return 191;
                    form.SetTextOutline(-5);
                    if (reloadedCard.TextOutlinePercent != 0) return 192;
                    if (!form.QuoteFitsCard) return 193;

                    // Line-breaking rules. The greedy breaker put a comma at the start of a line
                    // (and once made a line that was nothing but a full stop), which is the kind of
                    // mistake a Chinese reader spots instantly.
                    string bannedAtStart = "，。、；：？！）」』】〉》”’…";
                    // 六个字一行，每字 30 像素：这个宽度会把「，」正好挤到行首。
                    const int cjkCell = 30;
                    List<string> cjkLines = TextLines.Split("人生天地之间，若白驹之过隙，忽然而已。",
                        6 * cjkCell, delegate(char character) { return cjkCell; });
                    if (cjkLines.Count == 0) return 211;
                    string rejoined = "";
                    for (int index = 0; index < cjkLines.Count; index++)
                    {
                        if (cjkLines[index].Length == 0) continue;
                        if (bannedAtStart.IndexOf(cjkLines[index][0]) >= 0) return 212;
                        rejoined += cjkLines[index];
                    }
                    // The rule only moves the break; it never drops or duplicates a character.
                    if (rejoined != "人生天地之间，若白驹之过隙，忽然而已。") return 213;

                    // Justify stretches the lines while painting, so the block must not move -
                    // otherwise the card's drag floor would shift just because of an alignment.
                    form.SetVerticalText(false);
                    reloadedCard.AutoFitText = false;
                    form.ApplyManualFontSize(20f);
                    form.Size = new System.Drawing.Size(120, 400);
                    form.SetTextAlignment(true, CardState.AlignCenter);
                    System.Drawing.Size centeredBlock = form.QuoteBlockSize;
                    form.SetTextAlignment(true, CardState.AlignJustify);
                    if (!form.QuoteJustify) return 214;
                    if (form.QuoteBlockSize != centeredBlock) return 215;
                    form.SetTextAlignment(true, CardState.AlignCenter);
                    if (form.QuoteJustify) return 216;

                    // A shadow is drawn outside the glyphs, so the block grows with it.
                    form.SetTextShadow(0);
                    System.Drawing.Size shadowlessBlock = form.QuoteBlockSize;
                    form.SetTextShadow(10);
                    System.Drawing.Size shadowedBlock = form.QuoteBlockSize;
                    if (shadowedBlock.Width <= shadowlessBlock.Width) return 217;
                    if (shadowedBlock.Height <= shadowlessBlock.Height) return 218;
                    if (!form.QuoteFitsCard) return 219;
                    form.SetTextShadow(0);
                    if (form.QuoteBlockSize != shadowlessBlock) return 220;
                    form.SetTextShadow(1000);
                    if (reloadedCard.TextShadowPercent != CardState.MaxTextShadowPercent) return 221;

                    // Opacity is a window property only: it must not touch the block.
                    reloadedCard.TransparentBackground = false;
                    System.Drawing.Size beforeOpacity = form.QuoteBlockSize;
                    form.SetCardOpacity(80);
                    if (Math.Abs(form.Opacity - 0.80) > 0.02) return 222;
                    if (form.QuoteBlockSize != beforeOpacity) return 223;
                    form.SetCardOpacity(1000);
                    if (reloadedCard.CardOpacityPercent != CardState.MaxCardOpacityPercent) return 224;
                    form.SetCardOpacity(1);
                    if (reloadedCard.CardOpacityPercent != CardState.MinCardOpacityPercent) return 225;
                    form.SetCardOpacity(CardState.DefaultCardOpacityPercent);
                    if (Math.Abs(form.Opacity - 0.97) > 0.02) return 226;

                    // Corner radius, border and image layout. The radius has two caps - what the
                    // card asks for and how big the card is - and the window region and the painted
                    // border both come through the same helper, so they cannot disagree.
                    if (CardSizeLimits.CornerRadiusFor(1000, 400, 300) !=
                        CardState.MaxCornerRadius) return 227;
                    if (CardSizeLimits.CornerRadiusFor(1000, 60, 40) != 20) return 228;
                    if (CardSizeLimits.CornerRadiusFor(-5, 400, 300) != 0) return 229;
                    // The padding has to clear the corner at the largest radius a card can be
                    // given, not just at the default - this is the arithmetic MaxCornerRadius was
                    // chosen by. R/2 would be a loose stand-in that stops meaning anything here.
                    double radiusClearance = CardState.MaxCornerRadius * (1.0 - 1.0 / Math.Sqrt(2.0));
                    if (radiusClearance > CardSizeLimits.TextPadLeft) return 230;
                    if (radiusClearance > CardSizeLimits.TextPadRight) return 231;

                    form.SetCornerRadius(1000);
                    if (reloadedCard.CornerRadius != CardState.MaxCornerRadius) return 232;
                    if (form.QuoteCornerRadius != CardState.MaxCornerRadius) return 233;
                    form.SetCornerRadius(-5);
                    if (reloadedCard.CornerRadius != 0) return 234;
                    if (form.QuoteCornerRadius != 0) return 235;
                    // A corner is decoration: it must not change how much room the text needs.
                    System.Drawing.Size plainCornerBlock = form.QuoteBlockSize;
                    form.SetCornerRadius(CardState.DefaultCornerRadius);
                    if (form.QuoteBlockSize != plainCornerBlock) return 236;

                    form.SetBorderWidth(1000);
                    if (reloadedCard.BorderWidth != CardState.MaxBorderWidth) return 237;
                    form.SetBorderWidth(-3);
                    if (reloadedCard.BorderWidth != 0) return 238;
                    form.SetBorderWidth(CardState.DefaultBorderWidth);
                    if (form.QuoteBlockSize != plainCornerBlock) return 239;

                    form.SetBackgroundImageLayout(99);
                    if (reloadedCard.BackgroundImageLayout != CardState.ImageLayoutCenter) return 240;
                    form.SetBackgroundImageLayout(-1);
                    if (reloadedCard.BackgroundImageLayout != CardState.ImageLayoutCover) return 241;
                    form.SetBackgroundImageLayout(CardState.ImageLayoutTile);
                    if (form.QuoteBlockSize != plainCornerBlock) return 242;

                    // Opacity takes any whole number now, and the floor moved from 30 to 10.
                    form.SetCardOpacity(55);
                    if (Math.Abs(form.Opacity - 0.55) > 0.02) return 243;
                    form.SetCardOpacity(1);
                    if (reloadedCard.CardOpacityPercent != CardState.MinCardOpacityPercent) return 244;
                    form.SetCardOpacity(CardState.DefaultCardOpacityPercent);

                    // All four survive a restart.
                    reloadedCard.CornerRadius = 28;
                    reloadedCard.BorderWidth = 3;
                    reloadedCard.BorderColorArgb = unchecked((int)0xFF336699);
                    reloadedCard.BackgroundImageLayout = CardState.ImageLayoutContain;
                    reloaded.Save();
                    AppState restyled = AppState.Load(statePath);
                    if (restyled.Cards[0].CornerRadius != 28) return 245;
                    if (restyled.Cards[0].BorderWidth != 3) return 246;
                    if (restyled.Cards[0].BorderColorArgb != unchecked((int)0xFF336699)) return 247;
                    if (restyled.Cards[0].BackgroundImageLayout != CardState.ImageLayoutContain) return 248;

                    // Two caches. The only two things that can go wrong with a cache are "it never
                    // hits" and "it goes stale", and both are visible from here.
                    //
                    // The fit search makes a new Font for every candidate size. While the advance
                    // table was keyed on the Font object, that cleared the whole table on every
                    // step - the very loop the table exists to speed up.
                    reloadedCard.AutoFitText = true;
                    reloadedCard.ManualFontSize = 0f;
                    form.Size = new System.Drawing.Size(320, 200);
                    form.RefitQuoteFont();
                    form.RefitQuoteFont();
                    int steadyMeasures = form.QuoteGlyphMeasures;
                    form.RefitQuoteFont();
                    if (form.QuoteGlyphMeasures != steadyMeasures) return 249;
                    // The counter is not simply stuck: a size the search has never tried has to be
                    // measured. At 700 wide the search starts at 41.2 points, which none of the
                    // candidates at 320 wide could have been.
                    form.Size = new System.Drawing.Size(700, 300);
                    form.RefitQuoteFont();
                    if (form.QuoteGlyphMeasures <= steadyMeasures) return 250;

                    // The background picture is scaled once per size/layout/colour/picture and
                    // blitted from then on. Scaling it inside the paint meant a bicubic resample of
                    // the whole source on every frame of a resize drag.
                    string picturePath = System.IO.Path.Combine(root, "background-test.png");
                    using (System.Drawing.Bitmap picture = new System.Drawing.Bitmap(800, 600))
                    {
                        using (System.Drawing.Graphics graphics =
                            System.Drawing.Graphics.FromImage(picture))
                            graphics.Clear(System.Drawing.Color.SteelBlue);
                        picture.Save(picturePath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    reloadedCard.BackgroundImagePath = picturePath;
                    reloadedCard.BackgroundImageLayout = CardState.ImageLayoutCover;
                    form.ReloadBackgroundImage();
                    form.Size = new System.Drawing.Size(320, 200);
                    int buildsBefore = form.QuoteBackgroundCacheBuilds;
                    if (!form.PrepareBackgroundCache()) return 251;
                    if (form.QuoteBackgroundCacheBuilds != buildsBefore + 1) return 252;
                    if (form.QuoteBackgroundCacheSize != form.ClientSize) return 253;
                    // Same size, same layout, same colour, same picture: no rebuild.
                    if (!form.PrepareBackgroundCache()) return 254;
                    if (form.QuoteBackgroundCacheBuilds != buildsBefore + 1) return 255;
                    // A new size has to rebuild, or the picture would be stretched from the old one.
                    form.Size = new System.Drawing.Size(360, 220);
                    if (!form.PrepareBackgroundCache()) return 256;
                    if (form.QuoteBackgroundCacheBuilds != buildsBefore + 2) return 257;
                    if (form.QuoteBackgroundCacheSize != form.ClientSize) return 258;
                    // A new layout has to rebuild too: it moves the picture, it does not just
                    // repaint it.
                    form.SetBackgroundImageLayout(CardState.ImageLayoutTile);
                    if (!form.PrepareBackgroundCache()) return 259;
                    if (form.QuoteBackgroundCacheBuilds != buildsBefore + 3) return 260;
                    // Clearing the picture releases the cache rather than leaving a stale blit.
                    reloadedCard.BackgroundImagePath = "";
                    form.ReloadBackgroundImage();
                    if (form.PrepareBackgroundCache()) return 261;
                    if (form.QuoteBackgroundCached) return 262;

                    // The drag snap threshold. Too large and a card cannot be put anywhere near
                    // another one; too small and it never engages. This is a feel decision, but it
                    // is pinned so that changing it has to be a deliberate act.
                    //
                    // Through a local: both operands are constants, so the compiler folds the
                    // comparison and calls the return unreachable (CS0162) - the check would then
                    // never exist at all.
                    int snapReach = CardSnap.Distance;
                    if (snapReach <= 0 || snapReach > 24) return 263;

                    // The rounded-corner path has two cases a card can reach straight from the
                    // menu, and neither is reachable from a test of the arithmetic: radius 0
                    // (square corner) and a radius larger than the rectangle being drawn (a small
                    // card at the largest radius with a thick border). Both used to go straight
                    // into four AddArc calls - the first degenerates into four coincident points,
                    // the second puts two arcs on top of each other and folds the path over itself.
                    using (System.Drawing.Drawing2D.GraphicsPath square =
                        QuoteCardForm.RoundedRectangle(new System.Drawing.Rectangle(0, 0, 37, 37), 0))
                    {
                        if (square.PointCount < 4) return 264;
                        System.Drawing.RectangleF box = square.GetBounds();
                        if (box.Width < 36.5f || box.Width > 37.5f) return 265;
                    }
                    using (System.Drawing.Drawing2D.GraphicsPath oversized =
                        QuoteCardForm.RoundedRectangle(new System.Drawing.Rectangle(0, 0, 37, 37), 40))
                    {
                        System.Drawing.RectangleF box = oversized.GetBounds();
                        if (box.Width < 36.5f || box.Width > 37.5f) return 266;
                        if (box.Height < 36.5f || box.Height > 37.5f) return 267;
                    }

                    // A preset carries the typeface and nothing else: the direction and the
                    // alignment belong to how the card is laid out, so a vertical card and a
                    // horizontal one can share one preset and keep their own direction.
                    reloadedCard.FontName = "SimSun";
                    reloadedCard.FontStyleValue = (int)System.Drawing.FontStyle.Bold;
                    reloadedCard.AutoFitText = true;
                    reloadedCard.ManualFontSize = 0f;
                    reloadedCard.AutoFitMaxFontSize = 36f;
                    reloadedCard.TextColorArgb = unchecked((int)0xFF112233);
                    reloadedCard.LineSpacingPercent = 120;
                    reloadedCard.LetterSpacingPercent = 25;
                    reloadedCard.TextOutlinePercent = 8;
                    reloadedCard.VerticalText = true;
                    reloadedCard.TextAlignHorizontal = CardState.AlignFar;
                    reloaded.Presets.Clear();
                    reloaded.Presets.Add(TextPreset.FromCard("测试预设", reloadedCard));
                    // Now change everything a preset covers, plus the two things it must not.
                    reloadedCard.FontName = "Arial";
                    reloadedCard.FontStyleValue = 0;
                    reloadedCard.AutoFitMaxFontSize = 29f;
                    reloadedCard.TextColorArgb = unchecked((int)0xFF000000);
                    reloadedCard.LineSpacingPercent = 100;
                    reloadedCard.LetterSpacingPercent = 0;
                    reloadedCard.TextOutlinePercent = 0;
                    reloadedCard.VerticalText = false;
                    reloadedCard.TextAlignHorizontal = CardState.AlignNear;
                    form.ApplyPreset("测试预设");
                    if (reloadedCard.FontName != "SimSun") return 194;
                    if (reloadedCard.FontStyleValue != (int)System.Drawing.FontStyle.Bold) return 195;
                    if (Math.Abs(reloadedCard.AutoFitMaxFontSize - 36f) > 0.05f) return 196;
                    if (reloadedCard.TextColorArgb != unchecked((int)0xFF112233)) return 197;
                    if (reloadedCard.LineSpacingPercent != 120) return 198;
                    if (reloadedCard.LetterSpacingPercent != 25) return 199;
                    if (reloadedCard.TextOutlinePercent != 8) return 200;
                    // The two it must leave alone.
                    if (reloadedCard.VerticalText) return 201;
                    if (reloadedCard.TextAlignHorizontal != CardState.AlignNear) return 202;
                    if (!form.QuoteFitsCard) return 203;

                    // A preset that fits automatically must not hand a hand-picked size to the
                    // card it is applied to.
                    TextPreset fitting = TextPreset.FromCard("自动", reloadedCard);
                    reloadedCard.ManualFontSize = 40f;
                    reloadedCard.AutoFitText = false;
                    fitting.ApplyTo(reloadedCard);
                    if (!reloadedCard.AutoFitText) return 204;
                    if (reloadedCard.ManualFontSize != 0f) return 205;

                    // Presets survive a restart, name and all.
                    reloaded.Save();
                    AppState withPresets = AppState.Load(statePath);
                    if (withPresets.Presets.Count != 1) return 206;
                    if (withPresets.Presets[0].Name != "测试预设") return 207;
                    if (withPresets.Presets[0].FontName != "SimSun") return 208;
                    if (withPresets.Presets[0].LineSpacingPercent != 120) return 209;
                    if (withPresets.Presets[0].TextOutlinePercent != 8) return 210;

                    // Alignment moves the text without changing how much room it needs, so neither
                    // the measured block nor the card's floor may move with it. If they did, the
                    // card would silently resize itself every time an alignment was picked - which
                    // is exactly the trap the font work had to climb out of.
                    System.Drawing.Size alignBlock = form.QuoteBlockSize;
                    System.Drawing.Size alignFloor = form.MinimumCardSize;
                    form.SetTextAlignment(true, CardState.AlignNear);
                    form.SetTextAlignment(false, CardState.AlignFar);
                    if (reloadedCard.TextAlignHorizontal != CardState.AlignNear) return 110;
                    if (reloadedCard.TextAlignVertical != CardState.AlignFar) return 111;
                    // The setting has to reach the thing that paints, not just the settings file.
                    if (form.QuoteHorizontalAlignment != System.Drawing.StringAlignment.Near) return 112;
                    if (form.QuoteVerticalAlignment != System.Drawing.StringAlignment.Far) return 113;
                    if (form.QuoteBlockSize != alignBlock) return 114;
                    if (form.MinimumCardSize != alignFloor) return 115;
                    form.SetTextAlignment(true, CardState.AlignCenter);
                    form.SetTextAlignment(false, CardState.AlignCenter);
                    form.Hide();
                }
                // Alignment is a three-way choice per axis, and it has to survive a restart.
                CardState alignCard = reloaded.Cards[0];
                alignCard.TextAlignHorizontal = CardState.AlignFar;
                alignCard.TextAlignVertical = CardState.AlignNear;
                reloaded.Save();
                AppState alignReloaded = AppState.Load(statePath);
                if (alignReloaded.Cards[0].TextAlignHorizontal != CardState.AlignFar) return 94;
                if (alignReloaded.Cards[0].TextAlignVertical != CardState.AlignNear) return 95;

                // A new card starts out looking like the one before it, alignment included.
                CardState alignNew = alignReloaded.CreateCard();
                if (alignNew.TextAlignHorizontal != CardState.AlignFar) return 96;
                if (alignNew.TextAlignVertical != CardState.AlignNear) return 97;

                // A hand-edited file must not be able to turn a three-way choice into a fourth:
                // an unknown number has to land on one of the three.
                alignNew.TextAlignHorizontal = 9;
                alignNew.TextAlignVertical = -4;
                alignNew.Clamp();
                if (alignNew.TextAlignHorizontal != CardState.AlignFar ||
                    alignNew.TextAlignVertical != CardState.AlignNear) return 98;

                // Portable by default: the folder beside the program is used when it can be written
                // to, and an explicit request is honoured wherever it points.
                string portableRoot = Path.Combine(root, "portable");
                string resolved = DataLocation.Resolve(new string[] { "--data-dir=" + portableRoot });
                if (!string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(portableRoot),
                    StringComparison.OrdinalIgnoreCase)) return 100;
                if (!Directory.Exists(portableRoot)) return 101;
                if (DataLocation.IsPortable(portableRoot)) return 102;
                if (!DataLocation.IsPortable(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    DataLocation.PortableFolderName))) return 103;

                // An upgrade keeps its content: the old per-user folder is carried into the portable
                // one the first time, a second run copies nothing twice, and an installation that
                // already has content is never overwritten by the older folder.
                string legacyRoot = Path.Combine(root, "legacy");
                Directory.CreateDirectory(legacyRoot);
                File.WriteAllText(Path.Combine(legacyRoot, "state.ini"), "UiLanguage=zh-CN");
                File.WriteAllText(Path.Combine(legacyRoot, "content-1.txt"), "旧内容");
                if (DataLocation.Adopt(portableRoot, legacyRoot) != 2) return 104;
                if (!File.Exists(Path.Combine(portableRoot, "content-1.txt"))) return 105;
                if (DataLocation.Adopt(portableRoot, legacyRoot) != 0) return 106;
                File.WriteAllText(Path.Combine(portableRoot, "content-1.txt"), "新内容");
                if (DataLocation.Adopt(portableRoot, legacyRoot) != 0) return 107;
                if (File.ReadAllText(Path.Combine(portableRoot, "content-1.txt")) != "新内容") return 108;

                reloaded.UiLanguage = "en";
                reloaded.Save();
                AppState englishState = AppState.Load(statePath);
                if (!UiText.IsEnglish(englishState)) return 20;
                QuoteRepository englishRepository = new QuoteRepository(quotePath, englishState);
                using (QuoteCardForm englishForm = new QuoteCardForm(
                    englishState, englishState.Cards[0], englishRepository, false))
                {
                    englishForm.CreateControl();
                    if (!englishForm.EnglishUiActive) return 21;
                    if (!englishForm.LayerChoiceActive) return 24;
                }
                using (QuoteEditorForm englishEditor = new QuoteEditorForm(englishRepository))
                {
                    if (englishEditor.Text != "Edit content library") return 22;
                }
                return 0;
            }
            catch
            {
                return 99;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
