using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace XiaoJian
{
    internal sealed class QuoteCardForm : Form
    {
        internal const int LocateMessage = 0x8514;
        internal const int LocateResponse = 0x5143;
        private const int LocateHotKeyId = 0x5143;
        private static readonly Color TransparentKeyColor = Color.FromArgb(1, 2, 3);
        // The invisible interaction frame is kept as small as the text it covers, so it never
        // swallows clicks aimed at windows behind a transparent card. The padding keeps a
        // comfortable drag target; the minimums keep very short entries usable.
        private const int HitFramePadding = 6;
        private const int HitFrameMinimumWidth = 36;
        private const int HitFrameMinimumHeight = 28;
        // Extended window styles used by the mouse pass-through mode.
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        // The automatic fit only ever shrinks from a guess, so the guess has to be generous: about
        // a seventeen-character line, and never below twenty points whatever the card. The ceiling
        // above it is the card's own AutoFitMaxFontSize - these two only shape the guess.
        private const float AutoFitGuessCharacters = 17.0f;
        private const float AutoFitGuessFloor = 20.0f;
        private readonly AppState _app;
        private readonly CardState _card;
        private readonly QuoteRepository _repository;
        private readonly DailyQuotePicker _picker;
        private readonly QuoteView _quoteView;
        private readonly Label _menuLabel;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _topMostItem;
        private ToolStripMenuItem _cardListMenu;
        private readonly NotifyIcon _trayIcon;
        private ToolStripMenuItem _presetMenu;
        private readonly Icon _trayImage;
        private readonly Timer _dayTimer;
        private readonly Timer _locateTimer;
        private Font _quoteFont;
        private Image _backgroundImage;
        // The picture already scaled to the card, so a repaint is one blit instead of a bicubic
        // resample of the whole source. A 4000x3000 photograph on a 460x190 card costs tens of
        // milliseconds per frame to resample, and a resize drag repaints every frame.
        //
        // Rebuilt whenever the card's size, the layout, the colour behind the picture or the
        // picture itself changes - those are the only things it depends on.
        private Bitmap _backgroundCache;
        private Image _backgroundCacheImage;
        private Size _backgroundCacheSize;
        private int _backgroundCacheLayout;
        private Color _backgroundCacheColor;
        // A card may be dragged to 8192x8192, which would be a 268 MB bitmap. Past a point the
        // cache costs more than it saves, so those cards draw straight to the screen instead.
        private const long MaxCachedBackgroundPixels = 16L * 1024 * 1024;
        private int _backgroundCacheBuilds;
        private float _quoteFontSize = -1.0f;
        private Point _dragOrigin;
        // Where the card would be if nothing were pulling it. Kept apart from the real position so
        // that a snapped card comes unstuck as soon as the pointer moves away, instead of dragging
        // the snap along with it.
        private Point _dragFree;
        // Neighbour bounds are taken once, when the drag starts: no card can move while another one
        // is being dragged, so recomputing them on every mouse move would be work for nothing.
        private Rectangle[] _snapTargets = new Rectangle[0];
        private bool _dragging;
        private bool _initialized;
        private bool _resourcesDisposed;
        private bool _locatingCard;
        private bool _hotKeyRegistered;
        private TransparentHitBoxForm _transparentHitBox;
        private Rectangle _lastHitFrame;

        // Set by CardManager for the cards it owns. A card created on its own (the self test) leaves
        // this null, which simply hides the entries that need the other cards.
        internal CardManager Manager;

        public QuoteCardForm(AppState app, CardState card, QuoteRepository repository)
            : this(app, card, repository, false)
        {
        }

        internal QuoteCardForm(AppState app, CardState card, QuoteRepository repository, bool showTrayIcon)
        {
            _app = app;
            _card = card;
            _repository = repository;
            _picker = new DailyQuotePicker(app, card, repository);

            Text = "小笺";
            BackColor = SafeColor(card.BackgroundColorArgb, Color.White);
            ForeColor = SafeColor(card.TextColorArgb, Color.Black);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            TopMost = card.TopMost;
            Opacity = 0.97;
            Size = new Size(card.CardWidth, card.CardHeight);
            // Resizing is free: the card may be dragged to any size the desktop can hold, and the
            // only floor is the text it has to show. The floor needs a measured text block, so it
            // is filled in by UpdateCardSizeLimits once the text is laid out; all that belongs here
            // is the degenerate-state guard, and a ceiling that stops a drag from leaving the
            // desktop altogether.
            MinimumSize = new Size(CardState.MinCardWidth, CardState.MinCardHeight);
            MaximumSize = CardSizeLimits.MaximumFor(SystemInformation.VirtualScreen.Size);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

            _quoteView = new QuoteView();
            _quoteView.BackColor = Color.Transparent;
            _quoteView.ForeColor = ForeColor;
            _quoteView.Cursor = Cursors.SizeAll;
            _quoteView.AccessibleName = "当前内容";
            _quoteView.Vertical = card.VerticalText;
            _quoteView.LineSpacingPercent = card.LineSpacingPercent;
            _quoteView.LetterSpacingPercent = card.LetterSpacingPercent;
            _quoteView.TextOutlinePercent = card.TextOutlinePercent;
            _quoteView.OutlineColor = SafeColor(CardState.OutlineColorArgbFor(card), Color.White);
            _quoteView.TextShadowPercent = card.TextShadowPercent;
            _quoteView.ShadowColor = SafeColor(CardState.ShadowColorArgbFor(card), Color.Black);
            PushTextAlignmentToView();
            Controls.Add(_quoteView);

            _menuLabel = new Label();
            _menuLabel.AutoSize = false;
            _menuLabel.BackColor = Color.Transparent;
            _menuLabel.ForeColor = Color.FromArgb(165, 165, 165);
            _menuLabel.Font = UiFont(14.0f, FontStyle.Bold);
            _menuLabel.Text = "⋯";
            _menuLabel.TextAlign = ContentAlignment.MiddleCenter;
            _menuLabel.Cursor = Cursors.Hand;
            _menuLabel.AccessibleName = "设置";
            Controls.Add(_menuLabel);

            _menu = BuildMenu(out _startupItem, out _topMostItem);
            ContextMenuStrip = _menu;
            _quoteView.ContextMenuStrip = _menu;
            _menuLabel.Click += delegate { _menu.Show(_menuLabel, new Point(_menuLabel.Width, _menuLabel.Height), ToolStripDropDownDirection.BelowLeft); };

            AttachDragHandlers(this);
            AttachDragHandlers(_quoteView);
            _quoteView.DoubleClick += delegate { EditQuotes(); };

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "小笺";
            _trayImage = CreateTrayIcon();
            _trayIcon.Icon = _trayImage;
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.Visible = showTrayIcon;
            _trayIcon.DoubleClick += delegate { LocateCard(); };

            ApplyUiLanguage();

            _dayTimer = new Timer();
            _dayTimer.Interval = 60000;
            _dayTimer.Tick += delegate { RefreshForToday(); };

            _locateTimer = new Timer();
            _locateTimer.Interval = 6000;
            _locateTimer.Tick += delegate { EndLocateCard(); };

            ApplyAppearance(true);
            LayoutCard();
            RestoreLocation();
            RefreshForToday();
            _initialized = true;
            _dayTimer.Start();
        }

        internal bool TransparentHitBoxActive
        {
            get
            {
                return _transparentHitBox != null && !_transparentHitBox.IsDisposed &&
                    _transparentHitBox.Visible;
            }
        }

        internal bool ClearMenuLayoutActive
        {
            get
            {
                bool cardSettings = false;
                bool textSettings = false;
                foreach (ToolStripItem item in _menu.Items)
                {
                    if (item.Name == "CardSettings") cardSettings = true;
                    if (item.Name == "TextSettings") textSettings = true;
                    if (item.Name == "CardSize" || item.Name == "CustomAppearance") return false;
                }
                return _menu.ShowCheckMargin && cardSettings && textSettings;
            }
        }

        internal bool EnglishUiActive
        {
            get
            {
                ToolStripMenuItem edit = FindMenuItem(_menu.Items, "EditLibrary");
                ToolStripMenuItem bringBack = FindMenuItem(_menu.Items, "BringCardBack");
                ToolStripMenuItem english = FindMenuItem(_menu.Items, "LanguageEnglish");
                ToolStripMenuItem chinese = FindMenuItem(_menu.Items, "LanguageChinese");
                return Text == "XiaoJian" && edit != null &&
                    edit.Text == "Edit content library…" && bringBack != null &&
                    bringBack.Text.IndexOf("Find card", StringComparison.Ordinal) >= 0 &&
                    english != null && english.Checked && chinese != null && !chinese.Checked;
            }
        }

        internal bool LayerChoiceActive
        {
            get
            {
                ToolStripMenuItem layer = FindMenuItem(_menu.Items, "CardLayer");
                ToolStripMenuItem top = FindMenuItem(_menu.Items, "LayerTop");
                ToolStripMenuItem bottom = FindMenuItem(_menu.Items, "LayerBottom");
                return layer != null && top != null && bottom != null &&
                    top.Checked != bottom.Checked && top.Checked == TopMost;
            }
        }

        internal bool LocateModeActive
        {
            get
            {
                return _locatingCard && TopMost &&
                    (_transparentHitBox == null || !_transparentHitBox.Visible);
            }
        }

        internal Rectangle TransparentHitFrameBounds
        {
            get
            {
                if (_transparentHitBox == null || _transparentHitBox.IsDisposed ||
                    !_transparentHitBox.Visible) return Rectangle.Empty;
                return _transparentHitBox.Bounds;
            }
        }

        internal CardState Card { get { return _card; } }

        internal string CardQuote
        {
            get { return _quoteView == null ? "" : (_quoteView.Text ?? ""); }
        }

        // Only one window in a process can own a given hot key, so the first card that manages to
        // register keeps it. CardManager re-offers it after a card goes away.
        internal bool TryRegisterLocateHotKey()
        {
            if (_hotKeyRegistered) return true;
            if (!IsHandleCreated) CreateControl();
            if (!IsHandleCreated) return false;
            const uint MOD_ALT = 0x0001;
            const uint MOD_CONTROL = 0x0002;
            _hotKeyRegistered = NativeMethods.RegisterHotKey(Handle, LocateHotKeyId,
                MOD_ALT | MOD_CONTROL, (uint)Keys.Q);
            if (_hotKeyRegistered && _menu != null) ApplyMenuLanguage(_menu.Items);
            return _hotKeyRegistered;
        }

        internal void RefreshTrayTooltip()
        {
            UpdateTrayTooltip();
        }

        internal bool VerticalTextActive
        {
            get { return _card != null && _card.VerticalText && _quoteView != null && _quoteView.Vertical; }
        }

        // The block of pixels the text covers, whichever direction it runs in. Used by the self
        // test to prove vertical text really is measured as its own layout.
        internal Size QuoteBlockSize
        {
            get { return _quoteView == null ? Size.Empty : _quoteView.MeasureBlock(); }
        }

        // The smallest size this card may be dragged to, which the self test checks really does
        // follow the text instead of sitting at a fixed rectangle.
        internal Size MinimumCardSize
        {
            get { return MinimumSize; }
        }

        // The corner button, and whether it is being shown. The self test uses both to prove that
        // what gives way on a card too small for the button is the button, not the card.
        internal Rectangle MenuButtonBoundsInCard
        {
            get { return MenuButtonBounds; }
        }

        internal bool MenuButtonVisible
        {
            get { return _menuLabel != null && _menuLabel.Visible; }
        }

        // True while the rendered text still fits the text area. The self test uses it to prove the
        // card never bottoms out with its own text clipped.
        internal bool QuoteFitsCard
        {
            get { return _quoteView == null || _quoteView.FitsBlock(_quoteView.Font); }
        }

        // The size the card is showing right now, and the way to pick one by hand. The self test
        // uses both to prove a hand-picked size survives a card that is only as big as its text -
        // the exact situation in which the automatic fit used to swallow it.
        internal float QuoteFontSize
        {
            get { return CurrentFontSize(); }
        }

        internal void ApplyManualFontSize(float size)
        {
            SetManualFontSize(size);
        }

        internal void SetVerticalText(bool vertical)
        {
            if (_card == null) return;
            _card.VerticalText = vertical;
            _app.Save();
            if (_quoteView != null) _quoteView.Vertical = vertical;
            _quoteFontSize = -1.0f;
            FitQuoteFont();
        }

        // Alignment only moves the text that is already laid out, so unlike a font change it leaves
        // the font search and the card's minimum size alone - all it has to do is repaint, and the
        // menu ticks have to follow it.
        internal void SetTextAlignment(bool horizontal, int alignment)
        {
            if (_card == null) return;
            if (horizontal) _card.TextAlignHorizontal = alignment;
            else _card.TextAlignVertical = alignment;
            _app.Save();
            PushTextAlignmentToView();
            if (_menu != null) ApplyMenuLanguage(_menu.Items);
        }

        private void PushTextAlignmentToView()
        {
            if (_quoteView == null || _card == null) return;
            _quoteView.Justify = _card.TextAlignHorizontal == CardState.AlignJustify;
            _quoteView.HorizontalAlignment = ToStringAlignment(_card.TextAlignHorizontal);
            _quoteView.VerticalAlignment = ToStringAlignment(_card.TextAlignVertical);
        }

        // What the view was actually told to paint with. The self test compares these against the
        // setting: a card that stores "left" while still painting centred would otherwise pass.
        internal StringAlignment QuoteHorizontalAlignment
        {
            get { return _quoteView == null ? StringAlignment.Center : _quoteView.HorizontalAlignment; }
        }

        // Whether the view was actually told to stretch its lines. Justify is not a
        // StringAlignment, so it needs its own window onto the view.
        internal bool QuoteJustify
        {
            get { return _quoteView != null && _quoteView.Justify; }
        }

        // How many single characters the text view has measured with GDI since it was created.
        // A second pass over the same font sizes must add nothing to this.
        internal int QuoteGlyphMeasures
        {
            get { return _quoteView == null ? 0 : _quoteView.GlyphMeasures; }
        }

        // Re-runs the font search at the current size. Every menu entry calls FitQuoteFont itself,
        // so nothing in the product needs this - the self test needs to run the search twice.
        internal void RefitQuoteFont()
        {
            FitQuoteFont();
        }

        // Builds the scaled background if it is missing or stale; false means "nothing to blit".
        internal bool PrepareBackgroundCache()
        {
            return EnsureBackgroundCache();
        }

        internal int QuoteBackgroundCacheBuilds
        {
            get { return _backgroundCacheBuilds; }
        }

        internal Size QuoteBackgroundCacheSize
        {
            get { return _backgroundCacheSize; }
        }

        internal bool QuoteBackgroundCached
        {
            get { return _backgroundCache != null; }
        }

        // Re-reads the picture from disk. The card does this after the file changes; the self test
        // uses it to install one.
        internal void ReloadBackgroundImage()
        {
            LoadBackgroundImage();
        }

        // The radius the window is actually cut with, after both caps. The self test needs to see
        // it because only one of the two shapes - the region and the painted border - is
        // observable from outside, and they have to agree.
        internal int QuoteCornerRadius
        {
            get { return CardSizeLimits.CornerRadiusFor(_card.CornerRadius, Width, Height); }
        }

        internal StringAlignment QuoteVerticalAlignment
        {
            get { return _quoteView == null ? StringAlignment.Center : _quoteView.VerticalAlignment; }
        }

        private static StringAlignment ToStringAlignment(int alignment)
        {
            if (alignment == CardState.AlignFar) return StringAlignment.Far;
            // Justify starts the block at the near edge and stretches the lines themselves, so the
            // block is laid out as "near" and the stretching happens per line while painting.
            if (alignment == CardState.AlignNear || alignment == CardState.AlignJustify)
                return StringAlignment.Near;
            return StringAlignment.Center;
        }

        private static ToolStripMenuItem FindMenuItem(ToolStripItemCollection items, string name)
        {
            foreach (ToolStripItem item in items)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem == null) continue;
                if (menuItem.Name == name) return menuItem;
                ToolStripMenuItem nested = FindMenuItem(menuItem.DropDownItems, name);
                if (nested != null) return nested;
            }
            return null;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams parameters = base.CreateParams;
                if (_card == null || !_card.TransparentBackground) parameters.ClassStyle |= CS_DROPSHADOW;
                // Declared here as well as applied at run time, so the style survives the handle
                // re-creation that appearance changes trigger.
                if (ClickThroughEnabled) parameters.ExStyle |= WsExTransparent;
                return parameters;
            }
        }

        // True while the card should let every mouse event fall through to the window beneath it.
        // The card is unclickable in that state, so "find the card" temporarily lifts it.
        private bool ClickThroughEnabled
        {
            get { return _card != null && _card.ClickThrough && !_locatingCard; }
        }

        internal bool ClickThroughStyleActive
        {
            get
            {
                if (!IsHandleCreated) return false;
                return (NativeMethods.GetWindowLong(Handle, GwlExStyle) & WsExTransparent) != 0;
            }
        }

        // Applies or removes WS_EX_TRANSPARENT on the live window. WS_EX_TRANSPARENT is the only
        // reliable way to make a window click-through: returning HTTRANSPARENT from WM_NCHITTEST
        // would only forward the click to windows of the same thread, never to other processes.
        private void ApplyClickThroughStyle()
        {
            if (!IsHandleCreated) return;
            int style = NativeMethods.GetWindowLong(Handle, GwlExStyle);
            int updated = ClickThroughEnabled ? (style | WsExTransparent) : (style & ~WsExTransparent);
            if (updated != style) NativeMethods.SetWindowLong(Handle, GwlExStyle, updated);
        }

        // Applies the stored pass-through setting to the window, the tray icon and the interaction
        // layer. Called when the setting is toggled, and by the self test.
        internal void ApplyClickThroughState()
        {
            ApplyClickThroughStyle();
            UpdateTrayTooltip();
            UpdateTransparentHitBox();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_card.TransparentBackground && !_locatingCard) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color borderColor = _locatingCard
                ? Color.FromArgb(255, 112, 0)
                : SafeColor(_card.BorderColorArgb, Color.FromArgb(42, 42, 42));
            float borderWidth = _locatingCard ? 5.0f : _card.BorderWidth;
            if (borderWidth <= 0f && !_locatingCard) return;
            // The same radius the window region was cut with, from the same helper: a border drawn
            // on a different arc than the region shows up as a sliver of background at each corner.
            int radius = CardSizeLimits.CornerRadiusFor(_card.CornerRadius, Width, Height);
            using (Pen pen = new Pen(borderColor, borderWidth))
            {
                Rectangle rect = ClientRectangle;
                // Half the stroke straddles the path, so a thick border has to move inwards by
                // half its width or it gets clipped by the window edge. A one pixel border stays
                // where it always was - shifting it would move every existing card's outline.
                int inset = _locatingCard ? 3 : (int)(borderWidth / 2f);
                rect.Inflate(-inset, -inset);
                rect.Width -= 1;
                rect.Height -= 1;
                using (GraphicsPath path = RoundedRectangle(rect, radius))
                    e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_locatingCard)
            {
                e.Graphics.Clear(Color.FromArgb(255, 246, 150));
                return;
            }
            if (_card.TransparentBackground)
            {
                e.Graphics.Clear(TransparentKeyColor);
                return;
            }
            e.Graphics.Clear(BackColor);
            if (_backgroundImage == null) return;
            if (EnsureBackgroundCache())
            {
                e.Graphics.DrawImageUnscaled(_backgroundCache, 0, 0);
                return;
            }
            DrawBackgroundPicture(e.Graphics, ClientRectangle);
        }

        // Draws the picture - and the darkening scrim that keeps the text readable over it - into
        // the given rectangle. Used both when filling the cache and, for a card too large to cache,
        // straight onto the screen.
        private void DrawBackgroundPicture(Graphics graphics, Rectangle area)
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            switch (_card.BackgroundImageLayout)
            {
                case CardState.ImageLayoutContain:
                    // Whole picture visible, letterboxed. The background colour shows in the bars,
                    // which is why this one is only worth using on a card whose colour was chosen
                    // to go with the picture.
                    graphics.DrawImage(_backgroundImage, FitRectangle(area, _backgroundImage.Size));
                    break;
                case CardState.ImageLayoutStretch:
                    // Fills the card and keeps no aspect ratio. The only mode that never leaves a
                    // gap and never crops, at the cost of distorting.
                    graphics.DrawImage(_backgroundImage, area);
                    break;
                case CardState.ImageLayoutTile:
                    // Natural size, repeated. A tile brush ignores the destination rectangle's
                    // size, so the whole area is filled and the picture is clipped at the edge.
                    using (Brush tiles = new TextureBrush(_backgroundImage, WrapMode.Tile))
                        graphics.FillRectangle(tiles, area);
                    break;
                case CardState.ImageLayoutCenter:
                    // Natural size, centred, cropped if it is bigger than the card. No scaling at
                    // all, so a small icon stays crisp and a big photo keeps its middle.
                    graphics.DrawImage(_backgroundImage,
                        new Rectangle(area.Left + (area.Width - _backgroundImage.Width) / 2,
                            area.Top + (area.Height - _backgroundImage.Height) / 2,
                            _backgroundImage.Width, _backgroundImage.Height));
                    break;
                default:
                    graphics.DrawImage(_backgroundImage, CoverRectangle(area, _backgroundImage.Size));
                    break;
            }
            using (Brush overlay = new SolidBrush(Color.FromArgb(62, 0, 0, 0)))
                graphics.FillRectangle(overlay, area);
        }

        // Fills the cache if it is missing or stale. False means "draw straight to the screen":
        // either the card is too large to be worth a bitmap, or the bitmap could not be allocated.
        // Both are slow paths, not wrong ones.
        private bool EnsureBackgroundCache()
        {
            if (_backgroundImage == null)
            {
                ReleaseBackgroundCache();
                return false;
            }
            Size size = ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return false;
            if ((long)size.Width * size.Height > MaxCachedBackgroundPixels)
            {
                ReleaseBackgroundCache();
                return false;
            }
            if (_backgroundCache != null && _backgroundCacheImage == _backgroundImage &&
                _backgroundCacheSize == size &&
                _backgroundCacheLayout == _card.BackgroundImageLayout &&
                _backgroundCacheColor == BackColor)
                return true;
            Bitmap cache = null;
            try
            {
                cache = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                using (Graphics graphics = Graphics.FromImage(cache))
                {
                    // Opaque from the start: the modes that do not fill the whole rectangle
                    // (contain, centre, tile) show this colour in the gaps.
                    graphics.Clear(BackColor);
                    DrawBackgroundPicture(graphics, new Rectangle(Point.Empty, size));
                }
            }
            catch
            {
                // Out of memory on a very large card. Fall back to drawing directly rather than
                // leaving the card blank; the next paint will try again.
                if (cache != null) cache.Dispose();
                ReleaseBackgroundCache();
                return false;
            }
            ReleaseBackgroundCache();
            _backgroundCache = cache;
            _backgroundCacheImage = _backgroundImage;
            _backgroundCacheSize = size;
            _backgroundCacheLayout = _card.BackgroundImageLayout;
            _backgroundCacheColor = BackColor;
            _backgroundCacheBuilds++;
            return true;
        }

        private void ReleaseBackgroundCache()
        {
            if (_backgroundCache == null) return;
            _backgroundCache.Dispose();
            _backgroundCache = null;
            _backgroundCacheImage = null;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRoundedRegion();
            LayoutCard();
            FitQuoteFont();
            UpdateCardSizeLimits();
            PositionTransparentHitBox();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SaveCardBounds();
        }

        protected override void WndProc(ref Message message)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_HOTKEY = 0x0312;
            const int HTCLIENT = 1;
            if (message.Msg == LocateMessage)
            {
                LocateCard();
                message.Result = (IntPtr)LocateResponse;
                return;
            }
            if (message.Msg == WM_HOTKEY && message.WParam.ToInt32() == LocateHotKeyId)
            {
                LocateCard();
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
            if (message.Msg != WM_NCHITTEST || (int)message.Result != HTCLIENT || WindowState != FormWindowState.Normal)
                return;
            Point screenPoint = ResizeSupport.ScreenPoint(message.LParam);
            int hit = ResizeSupport.HitTest(ClientSize, PointToClient(screenPoint));
            if (hit != HTCLIENT) message.Result = (IntPtr)hit;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (_initialized && WindowState == FormWindowState.Normal)
            {
                _card.X = Left;
                _card.Y = Top;
            }
            PositionTransparentHitBox();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateTransparentHitBox();
            if (_trayIcon != null && _trayIcon.Visible && !_app.HintShown)
            {
                _app.HintShown = true;
                _app.Save();
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed && !Disposing) ShowFindCardHint();
                });
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryRegisterLocateHotKey();
            ApplyClickThroughStyle();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_hotKeyRegistered && IsHandleCreated)
                NativeMethods.UnregisterHotKey(Handle, LocateHotKeyId);
            _hotKeyRegistered = false;
            base.OnHandleDestroyed(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (WindowState == FormWindowState.Normal)
            {
                SaveCardBounds();
            }
            DisposeResources();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeResources();
            base.Dispose(disposing);
        }

        private void DisposeResources()
        {
            if (_resourcesDisposed) return;
            _resourcesDisposed = true;
            if (_dayTimer != null)
            {
                _dayTimer.Stop();
                _dayTimer.Dispose();
            }
            if (_locateTimer != null)
            {
                _locateTimer.Stop();
                _locateTimer.Dispose();
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            if (_trayImage != null) _trayImage.Dispose();
            if (_quoteFont != null) _quoteFont.Dispose();
            if (_backgroundImage != null) _backgroundImage.Dispose();
            ReleaseBackgroundCache();
            if (_transparentHitBox != null)
            {
                _transparentHitBox.ContextMenuStrip = null;
                _transparentHitBox.Dispose();
                _transparentHitBox = null;
            }
            if (_menu != null)
            {
                // A drop-down that is still on screen would otherwise be torn down while it is
                // still pumping its own mouse message, which ends in an unhandled exception.
                if (_menu.Visible) _menu.Close();
                _menu.Dispose();
            }
        }

        private void SaveCardBounds()
        {
            if (_card == null || WindowState != FormWindowState.Normal) return;
            _card.X = Left;
            _card.Y = Top;
            _card.CardWidth = Width;
            _card.CardHeight = Height;
            _app.Save();
        }

        private ContextMenuStrip BuildMenu(out ToolStripMenuItem startupItem, out ToolStripMenuItem topMostItem)
        {
            MenuState state = new MenuState();
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.BackColor = Color.FromArgb(28, 28, 28);
            menu.ForeColor = Color.White;
            menu.Font = UiFont(10.0f, FontStyle.Regular);
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            menu.Padding = new Padding(5);

            ToolStripMenuItem editItem = CreateMenuItem("EditLibrary");
            editItem.Click += delegate { EditQuotes(); };
            menu.Items.Add(editItem);

            ToolStripMenuItem anotherItem = CreateMenuItem("SwitchQuote");
            anotherItem.Click += delegate
            {
                _quoteView.Text = _picker.PickAnother();
                FitQuoteFont();
            };
            menu.Items.Add(anotherItem);

            ToolStripMenuItem newCardItem = CreateMenuItem("NewCard");
            newCardItem.Click += delegate
            {
                RunAfterMenu(delegate { if (Manager != null) Manager.CreateCard(); });
            };
            menu.Items.Add(newCardItem);

            ToolStripMenuItem cardListMenu = CreateMenuItem("CardList");
            _cardListMenu = cardListMenu;
            menu.Items.Add(cardListMenu);

            ToolStripMenuItem deleteCardItem = CreateMenuItem("DeleteCard");
            deleteCardItem.Click += delegate
            {
                QuoteCardForm self = this;
                RunAfterMenu(delegate
                {
                    if (Manager != null && !self.IsDisposed) Manager.RemoveCard(self);
                });
            };
            menu.Items.Add(deleteCardItem);
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(BuildCardSettingsMenu(state));

            menu.Items.Add(BuildTextSettingsMenu(state));

            ToolStripMenuItem localStartupItem = CreateMenuItem("Startup");
            localStartupItem.CheckOnClick = true;
            localStartupItem.Click += delegate { ToggleStartup(); };
            menu.Items.Add(localStartupItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem showItem = CreateMenuItem("BringCardBack");
            showItem.Click += delegate { LocateCard(); };
            ToolStripSeparator findSeparator = new ToolStripSeparator();
            menu.Items.Insert(0, findSeparator);
            menu.Items.Insert(0, showItem);

            ToolStripMenuItem folderItem = CreateMenuItem("OpenLibraryFolder");
            folderItem.Click += delegate { OpenQuoteFolder(); };
            menu.Items.Add(folderItem);

            ToolStripMenuItem languageMenu = CreateMenuItem("Language");
            ToolStripMenuItem chineseItem = CreateMenuItem("LanguageChinese");
            ToolStripMenuItem englishItem = CreateMenuItem("LanguageEnglish");
            chineseItem.Click += delegate { SetLanguage("zh-CN"); };
            englishItem.Click += delegate { SetLanguage("en"); };
            languageMenu.DropDownItems.Add(chineseItem);
            languageMenu.DropDownItems.Add(englishItem);
            menu.Items.Add(languageMenu);

            ToolStripMenuItem helpItem = CreateMenuItem("Help");
            helpItem.Click += delegate
            {
                MessageBox.Show(T(
                    "• 拖动卡片，可以改变位置\n• 拖动卡片的边缘或四角，可以把卡片调成任意大小；最小不会小于文字所占的区域，文字四周只留 12 像素内边距\n• “卡片大小 → 适应文字大小（最小）”让卡片恰好包住文字，不留多余背景\n• 卡片小到放不下右下角的“⋯”按钮时，按钮会自动隐藏；此时右键卡片任意位置，仍可打开全部菜单\n• 双击文字，编辑这张卡片自己的内容库；每张卡片各有一份，互不影响\n• “切换到另一条内容”会立即更换当前显示\n• “卡片设置”管理大小、背景和卡片层级\n• “置顶”让卡片盖在其他窗口上；“置底”允许其他窗口盖住卡片\n• “文字设置”管理字号、颜色、字体和排版对齐\n• “大一点 / 小一点 / 设为最小 / 恢复默认字号”会由你决定字号，并自动取消“文字自动适应卡片”；想交还给卡片，重新勾上它即可\n• 找不到卡片时，按 Ctrl+Alt+Q、双击右下角小图标，或再次打开软件\n• 寻找时，卡片会移到屏幕中央并以醒目颜色显示几秒\n• 勾选“开机自启动”表示已经在系统的“启动”文件夹里放好了启动方式，随时可以删掉\n• 透明背景模式下，文字区域仍可右键、拖动和调整大小\n• 透明背景模式下，卡片空白处不会挡住下层窗口的点击\n• 勾选“鼠标穿透”后，卡片仍显示在最上层，但鼠标点击会穿透到下层窗口\n• 鼠标穿透开启时，卡片不再响应鼠标；请在托盘图标的右键菜单里取消勾选，或按 Ctrl+Alt+Q 让卡片临时可点击\n• “新建一张卡片”可以再放一张卡片，每张卡片的位置、外观和内容都互相独立\n• “卡片列表”里可以显示、隐藏或定位每一张卡片\n• “竖排文字”让文字自上而下、由右向左排列，符合中文竖排习惯\n• “排版对齐”可选左对齐 / 水平居中 / 右对齐，以及顶部对齐 / 垂直居中 / 底部对齐；横排与竖排都适用\n• “内容来源”可选“跟随每日轮换”或“固定显示某一句”，每张卡片各自设置\n• 免安装、便携：设置、内容库和背景图片都放在程序旁边的 data 文件夹里，把整个文件夹拷走就带走了全部内容\n• 只有当程序所在位置不可写（例如只读盘或需要管理员权限的目录）时，才会改用用户目录存放",
                    "• Drag the card to move it\n• Drag any edge or corner to size the card however you like; it never goes smaller than its text, which keeps 12 pixels of padding on every side\n• “Card size → Fit to text (smallest)” makes the card exactly wrap its text, with no spare background\n• On a card too small for the “...” button, the button hides; right-click anywhere on the card for the full menu\n• Double-click the text to edit this card's own library; every card has its own and they never mix\n• “Switch to another entry” changes the current text immediately\n• “Card settings” controls size, background, and card layer\n• “Top” keeps the card above other windows; “Bottom” lets other windows cover it\n• “Text settings” controls size, color, font and text alignment\n• “Larger / Smaller / Smallest / Reset text size” hand the size to you and turn “Fit text to card automatically” off; tick it again to hand the size back\n• If the card is lost, press Ctrl+Alt+Q, double-click the tray icon, or open the app again\n• The card moves to the center and uses a bright highlight for a few seconds\n• A check beside “Start with Windows” means a launcher is in place in your Startup folder, where you can remove it\n• In transparent mode, the text area can still be right-clicked, dragged, and resized\n• In transparent mode, the empty part of the card lets clicks reach the window below\n• With “Mouse pass-through” on, the card stays on top but clicks reach the window below\n• While pass-through is on the card ignores the mouse; uncheck it from the tray icon menu, or press Ctrl+Alt+Q to make the card clickable again\n• “New card” adds another card; every card keeps its own position, look and content\n• The “Cards” list shows, hides or finds each card\n• “Vertical text” runs characters top to bottom in columns that read right to left\n• “Text alignment” picks left / centre / right and top / middle / bottom; it applies to horizontal and vertical text alike\n• “Content source” picks the daily rotation or one pinned sentence, per card\n• No installation, no registry: settings, libraries and background images sit in a “data” folder beside the program, so copying that folder takes everything with it\n• Only a location the program cannot write to falls back to your user folder"),
                    T("小笺 · 使用方法", "XiaoJian · Help"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(helpItem);

            ToolStripMenuItem exitItem = CreateMenuItem("Exit");
            exitItem.Click += delegate
            {
                // Quitting disposes the very menu that is handling this click, so the work is
                // posted and runs once the drop-down has finished with its message.
                RunAfterMenu(delegate
                {
                    if (Manager != null) Manager.Shutdown();
                    else Application.Exit();
                });
            };
            menu.Items.Add(exitItem);

            // The entries the Opening handler has to re-read, handed over rather than kept as
            // fields: nothing else in the form has any use for them.
            state.Another = anotherItem;
            state.NewCard = newCardItem;
            state.CardList = cardListMenu;
            state.DeleteCard = deleteCardItem;
            state.Show = showItem;
            state.FindSeparator = findSeparator;
            state.Startup = localStartupItem;
            menu.Opening += delegate { RefreshMenuState(menu, state); };
            ApplyMenuLanguage(menu.Items);
            ApplyMenuColors(menu.Items);
            startupItem = state.Startup;
            topMostItem = state.TopMost;
            return menu;
        }

        // The entries that have to be re-read every time the menu opens. Handed from the builders to
        // the Opening handler rather than kept as form fields: nothing outside the menu has any use
        // for them, and a field per menu entry is how this class got unreadable in the first place.
        private sealed class MenuState
        {
            public ToolStripMenuItem Startup, TopMost, Bottom;
            public ToolStripMenuItem Another, NewCard, CardList, DeleteCard, Show;
            public ToolStripSeparator FindSeparator;
            public ToolStripMenuItem Opacity, ImageLayout, Corner, Border, Transparent, ClickThrough;
            public ToolStripMenuItem AutoFit, AutoMax, Bold, Italic;
            public ToolStripMenuItem LineSpacing, LetterSpacing, Outline, Shadow;
            public ToolStripMenuItem Justify, Vertical, Daily, Fixed;
        }

        // The card's own settings: size, colours, the picture, the shape, the layer.
        //
        // Split out of BuildMenu, which had grown to nearly five hundred lines. Every bug in this
        // area - an entry with no click handler, an entry with no title, an entry that stays enabled
        // when it can do nothing - came from losing track of which entry was which.
        private ToolStripMenuItem BuildCardSettingsMenu(MenuState state)
        {
            ToolStripMenuItem cardSettingsMenu = CreateMenuItem("CardSettings");
            ToolStripMenuItem sizeMenu = CreateMenuItem("CardSize");
            sizeMenu.DropDownItems.Add(CreateSizeItem("SizeSmall", 360, 150));
            sizeMenu.DropDownItems.Add(CreateSizeItem("SizeMedium", 460, 190));
            sizeMenu.DropDownItems.Add(CreateSizeItem("SizeLarge", 580, 240));
            sizeMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem fitSizeItem = CreateMenuItem("SizeFitText");
            fitSizeItem.Click += delegate { FitCardToText(); };
            sizeMenu.DropDownItems.Add(fitSizeItem);
            ToolStripMenuItem backgroundColorItem = CreateMenuItem("BackgroundColor");
            backgroundColorItem.Click += delegate { ChooseBackgroundColor(); };
            ToolStripMenuItem opacityMenu = CreateMenuItem("CardOpacity");
            int[] opacitySteps = { 100, 97, 90, 80, 70 };
            for (int index = 0; index < opacitySteps.Length; index++)
            {
                int percent = opacitySteps[index];
                ToolStripMenuItem opacityItem = CreateMenuItem(
                    "CardOpacity" + percent.ToString(CultureInfo.InvariantCulture));
                opacityItem.Tag = percent;
                opacityItem.Click += delegate { SetCardOpacity(percent); };
                opacityMenu.DropDownItems.Add(opacityItem);
            }
            // The five above are the ones people actually use; this one is for everybody else.
            opacityMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem opacityCustomItem = CreateMenuItem("CardOpacityCustom");
            opacityCustomItem.Click += delegate { AskCardOpacity(); };
            opacityMenu.DropDownItems.Add(opacityCustomItem);
            ToolStripMenuItem transparentItem = CreateMenuItem("TransparentBackground");
            transparentItem.CheckOnClick = true;
            transparentItem.Checked = _card.TransparentBackground;
            transparentItem.Click += delegate
            {
                _card.TransparentBackground = transparentItem.Checked;
                _app.Save();
                ApplyAppearance(false);
                // Recreating the window destroys the drop-down that owns this click, so it waits
                // until the menu has closed before the handle is thrown away and rebuilt.
                RunAfterMenu(delegate
                {
                    RecreateHandle();
                    UpdateTransparentHitBox();
                });
            };
            ToolStripMenuItem clickThroughItem = CreateMenuItem("ClickThrough");
            clickThroughItem.CheckOnClick = true;
            clickThroughItem.Checked = _card.ClickThrough;
            clickThroughItem.Click += delegate
            {
                _card.ClickThrough = clickThroughItem.Checked;
                // Pass-through only makes sense while the card is on top of other windows.
                if (clickThroughItem.Checked && !TopMost) SetCardLayer(true);
                _app.Save();
                ApplyClickThroughState();
            };
            ToolStripMenuItem imageItem = CreateMenuItem("BackgroundImage");
            imageItem.Click += delegate { ChooseBackgroundImage(); };
            ToolStripMenuItem clearImageItem = CreateMenuItem("ClearBackgroundImage");
            clearImageItem.Click += delegate { ClearBackgroundImage(true); };
            ToolStripMenuItem imageLayoutMenu = CreateMenuItem("ImageLayout");
            int[] imageLayouts = { CardState.ImageLayoutCover, CardState.ImageLayoutContain,
                CardState.ImageLayoutStretch, CardState.ImageLayoutTile, CardState.ImageLayoutCenter };
            for (int index = 0; index < imageLayouts.Length; index++)
            {
                int layout = imageLayouts[index];
                ToolStripMenuItem layoutItem = CreateMenuItem(
                    "ImageLayout" + layout.ToString(CultureInfo.InvariantCulture));
                layoutItem.Tag = layout;
                layoutItem.Click += delegate { SetBackgroundImageLayout(layout); };
                imageLayoutMenu.DropDownItems.Add(layoutItem);
            }
            ToolStripMenuItem cornerMenu = CreateMenuItem("CornerRadius");
            int[] cornerSteps = { 0, 8, 18, 28, 40 };
            for (int index = 0; index < cornerSteps.Length; index++)
            {
                int radius = cornerSteps[index];
                ToolStripMenuItem cornerItem = CreateMenuItem(
                    "CornerRadius" + radius.ToString(CultureInfo.InvariantCulture));
                cornerItem.Tag = radius;
                cornerItem.Click += delegate { SetCornerRadius(radius); };
                cornerMenu.DropDownItems.Add(cornerItem);
            }
            cornerMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem cornerCustomItem = CreateMenuItem("CornerRadiusCustom");
            cornerCustomItem.Click += delegate { AskCornerRadius(); };
            cornerMenu.DropDownItems.Add(cornerCustomItem);
            ToolStripMenuItem borderMenu = CreateMenuItem("BorderWidth");
            int[] borderSteps = { 0, 1, 2, 3 };
            for (int index = 0; index < borderSteps.Length; index++)
            {
                int width = borderSteps[index];
                ToolStripMenuItem borderItem = CreateMenuItem(
                    "BorderWidth" + width.ToString(CultureInfo.InvariantCulture));
                borderItem.Tag = width;
                borderItem.Click += delegate { SetBorderWidth(width); };
                borderMenu.DropDownItems.Add(borderItem);
            }
            borderMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem borderColorItem = CreateMenuItem("BorderColor");
            borderColorItem.Click += delegate { ChooseBorderColor(); };
            borderMenu.DropDownItems.Add(borderColorItem);
            ToolStripMenuItem layerMenu = CreateMenuItem("CardLayer");
            ToolStripMenuItem localTopMostItem = CreateMenuItem("LayerTop");
            ToolStripMenuItem bottomItem = CreateMenuItem("LayerBottom");
            localTopMostItem.Checked = _card.TopMost;
            bottomItem.Checked = !_card.TopMost;
            localTopMostItem.Click += delegate
            {
                SetCardLayer(true);
                localTopMostItem.Checked = true;
                bottomItem.Checked = false;
            };
            bottomItem.Click += delegate
            {
                SetCardLayer(false);
                localTopMostItem.Checked = false;
                bottomItem.Checked = true;
            };
            layerMenu.DropDownItems.Add(localTopMostItem);
            layerMenu.DropDownItems.Add(bottomItem);
            ToolStripMenuItem resetAppearanceItem = CreateMenuItem("ResetAppearance");
            resetAppearanceItem.Click += delegate
            {
                // Resetting also rebuilds the window, so like the transparency toggle it has to
                // wait until the menu that owns the click has closed.
                RunAfterMenu(delegate { ResetAppearance(); });
            };
            cardSettingsMenu.DropDownItems.Add(sizeMenu);
            cardSettingsMenu.DropDownItems.Add(backgroundColorItem);
            cardSettingsMenu.DropDownItems.Add(opacityMenu);
            cardSettingsMenu.DropDownItems.Add(transparentItem);
            cardSettingsMenu.DropDownItems.Add(clickThroughItem);
            cardSettingsMenu.DropDownItems.Add(imageItem);
            cardSettingsMenu.DropDownItems.Add(clearImageItem);
            cardSettingsMenu.DropDownItems.Add(imageLayoutMenu);
            cardSettingsMenu.DropDownItems.Add(cornerMenu);
            cardSettingsMenu.DropDownItems.Add(borderMenu);
            cardSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
            cardSettingsMenu.DropDownItems.Add(layerMenu);
            cardSettingsMenu.DropDownItems.Add(resetAppearanceItem);

            state.Opacity = opacityMenu;
            state.ImageLayout = imageLayoutMenu;
            state.Corner = cornerMenu;
            state.Border = borderMenu;
            state.TopMost = localTopMostItem;
            state.Bottom = bottomItem;
            state.Transparent = transparentItem;
            state.ClickThrough = clickThroughItem;
            return cardSettingsMenu;
        }

        // The text settings: size, face, spacing, decoration, alignment, where the words come from.
        private ToolStripMenuItem BuildTextSettingsMenu(MenuState state)
        {
            ToolStripMenuItem textSettingsMenu = CreateMenuItem("TextSettings");
            ToolStripMenuItem larger = CreateMenuItem("TextLarger");
            larger.Click += delegate { SetManualFontSize(CurrentFontSize() + 2.0f); };
            ToolStripMenuItem smaller = CreateMenuItem("TextSmaller");
            smaller.Click += delegate { SetManualFontSize(CurrentFontSize() - 2.0f); };
            ToolStripMenuItem smallest = CreateMenuItem("TextSmallest");
            smallest.Click += delegate { SetManualFontSize(CardState.MinManualFontSize); };
            // "A little larger" is two points at a time, so going from 7 to 96 is forty-five
            // clicks. This entry takes the number directly.
            ToolStripMenuItem exactSize = CreateMenuItem("TextExactSize");
            exactSize.Click += delegate { AskFontSize(); };
            ToolStripMenuItem autoFitItem = CreateMenuItem("AutoFitText");
            autoFitItem.CheckOnClick = true;
            autoFitItem.Checked = _card.AutoFitText;
            autoFitItem.Click += delegate
            {
                _card.AutoFitText = autoFitItem.Checked;
                // Back under automatic control the hand-picked size is dropped, so switching the
                // entry on really does hand the size back to the card.
                if (_card.AutoFitText) _card.ManualFontSize = 0f;
                _app.Save();
                _quoteFontSize = -1.0f;
                FitQuoteFont();
            };
            ToolStripMenuItem reset = CreateMenuItem("ResetTextSize");
            reset.Click += delegate
            {
                // "Default" is the card's own size, which is what automatic fitting produces, so
                // the entry puts back both halves of the default rather than only the offset.
                _card.FontAdjustment = 0;
                _card.ManualFontSize = 0f;
                _card.AutoFitText = true;
                _quoteFontSize = -1.0f;
                _app.Save();
                FitQuoteFont();
            };
            ToolStripMenuItem textColorItem = CreateMenuItem("TextColor");
            textColorItem.Click += delegate { ChooseTextColor(); };
            ToolStripMenuItem fontItem = CreateMenuItem("ChooseFont");
            fontItem.Click += delegate { ChooseQuoteFont(); };

            // Bold and italic are one click away rather than a trip through the font dialog. They
            // are style bits on the same FontStyleValue the dialog writes, so the two agree.
            // Spacing. Percentages rather than pixel counts because the font size follows the
            // card: a fixed gap would be invisible on a big card and swallow a small one.
            ToolStripMenuItem lineMenu = CreateMenuItem("LineSpacing");
            int[] lineCaps = { 90, 100, 120, 150 };
            for (int index = 0; index < lineCaps.Length; index++)
            {
                int percent = lineCaps[index];
                ToolStripMenuItem lineItem = CreateMenuItem(
                    "LineSpacing" + percent.ToString(CultureInfo.InvariantCulture));
                lineItem.Tag = percent;
                lineItem.Click += delegate { SetLineSpacing(percent); };
                lineMenu.DropDownItems.Add(lineItem);
            }
            ToolStripMenuItem letterMenu = CreateMenuItem("LetterSpacing");
            int[] letterCaps = { 0, 10, 25, 50 };
            for (int index = 0; index < letterCaps.Length; index++)
            {
                int percent = letterCaps[index];
                ToolStripMenuItem letterItem = CreateMenuItem(
                    "LetterSpacing" + percent.ToString(CultureInfo.InvariantCulture));
                letterItem.Tag = percent;
                letterItem.Click += delegate { SetLetterSpacing(percent); };
                letterMenu.DropDownItems.Add(letterItem);
            }

            // A halo is what makes text readable when there is a picture or a wallpaper behind it.
            ToolStripMenuItem outlineMenu = CreateMenuItem("TextOutline");
            int[] outlineSteps = { 0, 4, 8, 12 };
            for (int index = 0; index < outlineSteps.Length; index++)
            {
                int percent = outlineSteps[index];
                ToolStripMenuItem outlineItem = CreateMenuItem(
                    "TextOutline" + percent.ToString(CultureInfo.InvariantCulture));
                outlineItem.Tag = percent;
                outlineItem.Click += delegate { SetTextOutline(percent); };
                outlineMenu.DropDownItems.Add(outlineItem);
            }

            // Saved typefaces. The list itself is rebuilt every time the menu opens, because the
            // entries are the presets' own names - no language table knows about those.
            _presetMenu = CreateMenuItem("PresetMenu");

            ToolStripMenuItem shadowMenu = CreateMenuItem("TextShadow");
            int[] shadowSteps = { 0, 3, 6, 10 };
            for (int index = 0; index < shadowSteps.Length; index++)
            {
                int percent = shadowSteps[index];
                ToolStripMenuItem shadowItem = CreateMenuItem(
                    "TextShadow" + percent.ToString(CultureInfo.InvariantCulture));
                shadowItem.Tag = percent;
                shadowItem.Click += delegate { SetTextShadow(percent); };
                shadowMenu.DropDownItems.Add(shadowItem);
            }

            ToolStripMenuItem boldItem = CreateMenuItem("TextBold");
            boldItem.CheckOnClick = true;
            boldItem.Checked = HasFontStyle(FontStyle.Bold);
            boldItem.Click += delegate { SetFontStyle(FontStyle.Bold, boldItem.Checked); };
            ToolStripMenuItem italicItem = CreateMenuItem("TextItalic");
            italicItem.CheckOnClick = true;
            italicItem.Checked = HasFontStyle(FontStyle.Italic);
            italicItem.Click += delegate { SetFontStyle(FontStyle.Italic, italicItem.Checked); };

            // The automatic fit shrinks from this ceiling, so on a card the size of the desktop the
            // text is capped here no matter how much room is left. Offering it as a choice is the
            // whole point: the ceiling used to be a constant buried in the search.
            ToolStripMenuItem autoMaxMenu = CreateMenuItem("AutoFitMax");
            float[] autoMaxCaps = { 29f, 36f, 48f, 64f, 96f };
            for (int index = 0; index < autoMaxCaps.Length; index++)
            {
                float cap = autoMaxCaps[index];
                ToolStripMenuItem capItem = CreateMenuItem(
                    "AutoFitMax" + ((int)cap).ToString(CultureInfo.InvariantCulture));
                capItem.Tag = cap;
                capItem.Click += delegate { SetAutoFitMax(cap); };
                autoMaxMenu.DropDownItems.Add(capItem);
            }

            ToolStripMenuItem verticalItem = CreateMenuItem("VerticalText");
            verticalItem.CheckOnClick = true;
            verticalItem.Checked = _card.VerticalText;
            verticalItem.Click += delegate { SetVerticalText(verticalItem.Checked); };

            ToolStripMenuItem alignMenu = CreateMenuItem("TextAlign");
            ToolStripMenuItem alignLeft = CreateMenuItem("AlignLeft");
            alignLeft.Click += delegate { SetTextAlignment(true, CardState.AlignNear); };
            ToolStripMenuItem alignCenter = CreateMenuItem("AlignCenter");
            alignCenter.Click += delegate { SetTextAlignment(true, CardState.AlignCenter); };
            ToolStripMenuItem alignRight = CreateMenuItem("AlignRight");
            alignRight.Click += delegate { SetTextAlignment(true, CardState.AlignFar); };
            ToolStripMenuItem alignJustify = CreateMenuItem("AlignJustify");
            alignJustify.Click += delegate { SetTextAlignment(true, CardState.AlignJustify); };
            ToolStripMenuItem alignTop = CreateMenuItem("AlignTop");
            alignTop.Click += delegate { SetTextAlignment(false, CardState.AlignNear); };
            ToolStripMenuItem alignMiddle = CreateMenuItem("AlignMiddle");
            alignMiddle.Click += delegate { SetTextAlignment(false, CardState.AlignCenter); };
            ToolStripMenuItem alignBottom = CreateMenuItem("AlignBottom");
            alignBottom.Click += delegate { SetTextAlignment(false, CardState.AlignFar); };
            alignMenu.DropDownItems.Add(alignLeft);
            alignMenu.DropDownItems.Add(alignCenter);
            alignMenu.DropDownItems.Add(alignRight);
            alignMenu.DropDownItems.Add(alignJustify);
            alignMenu.DropDownItems.Add(new ToolStripSeparator());
            alignMenu.DropDownItems.Add(alignTop);
            alignMenu.DropDownItems.Add(alignMiddle);
            alignMenu.DropDownItems.Add(alignBottom);

            ToolStripMenuItem contentSourceMenu = CreateMenuItem("ContentSource");
            ToolStripMenuItem dailyItem = CreateMenuItem("ContentDaily");
            ToolStripMenuItem fixedItem = CreateMenuItem("ContentFixed");
            dailyItem.Click += delegate { SwitchToDailyContent(); };
            fixedItem.Click += delegate { PinCurrentContent(); };
            contentSourceMenu.DropDownItems.Add(dailyItem);
            contentSourceMenu.DropDownItems.Add(fixedItem);

            textSettingsMenu.DropDownItems.Add(larger);
            textSettingsMenu.DropDownItems.Add(smaller);
            textSettingsMenu.DropDownItems.Add(smallest);
            textSettingsMenu.DropDownItems.Add(exactSize);
            textSettingsMenu.DropDownItems.Add(reset);
            textSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
            textSettingsMenu.DropDownItems.Add(autoFitItem);
            textSettingsMenu.DropDownItems.Add(autoMaxMenu);
            textSettingsMenu.DropDownItems.Add(boldItem);
            textSettingsMenu.DropDownItems.Add(italicItem);
            textSettingsMenu.DropDownItems.Add(lineMenu);
            textSettingsMenu.DropDownItems.Add(letterMenu);
            textSettingsMenu.DropDownItems.Add(outlineMenu);
            textSettingsMenu.DropDownItems.Add(shadowMenu);
            textSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
            textSettingsMenu.DropDownItems.Add(_presetMenu);
            textSettingsMenu.DropDownItems.Add(verticalItem);
            textSettingsMenu.DropDownItems.Add(alignMenu);
            textSettingsMenu.DropDownItems.Add(contentSourceMenu);
            textSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
            textSettingsMenu.DropDownItems.Add(textColorItem);
            textSettingsMenu.DropDownItems.Add(fontItem);

            state.AutoFit = autoFitItem;
            state.AutoMax = autoMaxMenu;
            state.Bold = boldItem;
            state.Italic = italicItem;
            state.LineSpacing = lineMenu;
            state.LetterSpacing = letterMenu;
            state.Outline = outlineMenu;
            state.Shadow = shadowMenu;
            state.Justify = alignJustify;
            state.Vertical = verticalItem;
            state.Daily = dailyItem;
            state.Fixed = fixedItem;
            return textSettingsMenu;
        }

        // Everything that depends on the card's current state rather than on the menu's structure.
        // It used to be the body of the Opening handler, buried in the middle of the construction
        // code; as its own method it can be read without scrolling past five hundred lines.
        private void RefreshMenuState(ContextMenuStrip menu, MenuState state)
        {
            try { state.Startup.Checked = StartupManager.IsEnabled(); }
            catch { state.Startup.Checked = false; }
            state.TopMost.Checked = TopMost;
            state.Bottom.Checked = !TopMost;
            state.AutoFit.Checked = _card.AutoFitText;
            // With automatic fitting off there is no fitted size, so the ceiling has
            // nothing to cap - greying it out says so instead of leaving an entry that
            // does nothing when clicked.
            state.AutoMax.Enabled = _card.AutoFitText;
            // The style bits can also be changed from the font dialog, and the ceiling from
            // the submenu, so both are read back every time the menu opens rather than trusted
            // to whatever the click left behind.
            state.Bold.Checked = HasFontStyle(FontStyle.Bold);
            state.Italic.Checked = HasFontStyle(FontStyle.Italic);
            foreach (ToolStripItem capEntry in state.AutoMax.DropDownItems)
            {
                ToolStripMenuItem capItem = capEntry as ToolStripMenuItem;
                if (capItem == null || !(capItem.Tag is float)) continue;
                capItem.Checked = Math.Abs(_card.AutoFitMaxFontSize - (float)capItem.Tag) < 0.01f;
            }
            MarkCurrent(state.LineSpacing, _card.LineSpacingPercent);
            MarkCurrent(state.LetterSpacing, _card.LetterSpacingPercent);
            MarkCurrent(state.Outline, _card.TextOutlinePercent);
            MarkCurrent(state.Shadow, _card.TextShadowPercent);
            MarkCurrent(state.Opacity, _card.CardOpacityPercent);
            MarkCurrent(state.ImageLayout, _card.BackgroundImageLayout);
            MarkCurrent(state.Corner, _card.CornerRadius);
            MarkCurrent(state.Border, _card.BorderWidth);
            // TransparencyKey and Opacity both drive the layered window; changing the opacity
            // while the colour key is in use would break the transparency, so it is greyed.
            state.Opacity.Enabled = !_card.TransparentBackground;
            // How a picture fills the card means nothing while there is no picture.
            state.ImageLayout.Enabled = !string.IsNullOrEmpty(_card.BackgroundImagePath);
            // A transparent card paints no border - there is nothing for one to sit on.
            state.Border.Enabled = !_card.TransparentBackground;
            // Stretching lines is a horizontal idea; in vertical text there is nothing for it
            // to mean, so the entry is greyed rather than left doing nothing.
            state.Justify.Enabled = !_card.VerticalText;
            RefreshPresetMenu();
            state.Vertical.Checked = _card.VerticalText;
            state.Daily.Checked = !_card.IsFixedContent;
            state.Fixed.Checked = _card.IsFixedContent;
            // A pinned card has nothing to rotate, so the entry is only offered on daily cards.
            state.Another.Enabled = !_card.IsFixedContent;
            state.Transparent.Checked = _card.TransparentBackground;
            state.ClickThrough.Checked = _card.ClickThrough;
            state.Show.Visible = menu.SourceControl == null;
            state.FindSeparator.Visible = state.Show.Visible;
            bool hasManager = Manager != null;
            state.NewCard.Visible = hasManager;
            state.CardList.Visible = hasManager;
            state.DeleteCard.Visible = hasManager && Manager.CanRemoveCard;
            if (hasManager) RefreshCardList();
            ApplyMenuLanguage(menu.Items);
            ApplyMenuColors(menu.Items);
        }

        private ToolStripMenuItem CreateMenuItem(string name)
        {
            ToolStripMenuItem item = new ToolStripMenuItem();
            item.Name = name;
            return item;
        }

        // A menu click is delivered while the drop-down is still inside its own message handling.
        // Anything that tears a window down — closing a card, or quitting the process — has to
        // wait until that message is finished. Disposing a ContextMenuStrip from inside one of its
        // own item handlers leaves the drop-down using objects that no longer exist, and the
        // process dies with an unhandled exception on the way out instead of exiting quietly.
        // Posting the work to the message queue pushes it past the end of the current message.
        private void RunAfterMenu(MethodInvoker action)
        {
            if (action == null) return;

            if (IsHandleCreated && !IsDisposed && !Disposing)
            {
                try
                {
                    BeginInvoke(action);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // ObjectDisposedException derives from this, so it is covered too: the handle
                    // went away between the check and the post. Fall through to the thread queue.
                }
            }
            Program.PostToUi(action);
        }

        private void ApplyMenuLanguage(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem == null) continue;
                switch (menuItem.Name)
                {
                    case "EditLibrary": menuItem.Text = T("编辑内容库…", "Edit content library…"); break;
                    case "SwitchQuote": menuItem.Text = T("切换到另一条内容", "Switch to another entry"); break;
                    case "NewCard": menuItem.Text = T("新建一张卡片", "New card"); break;
                    case "CardList": menuItem.Text = T("卡片列表", "Cards"); break;
                    case "DeleteCard": menuItem.Text = T("删除这张卡片", "Delete this card"); break;
                    case "VerticalText": menuItem.Text = T("竖排文字（自上而下、由右向左）", "Vertical text (top to bottom, right to left)"); break;
                    case "TextAlign": menuItem.Text = T("排版对齐", "Text alignment"); break;
                    case "AlignLeft":
                        menuItem.Text = T("左对齐", "Align left");
                        menuItem.Checked = _card.TextAlignHorizontal == CardState.AlignNear;
                        break;
                    case "AlignCenter":
                        menuItem.Text = T("水平居中", "Centre horizontally");
                        menuItem.Checked = _card.TextAlignHorizontal == CardState.AlignCenter;
                        break;
                    case "AlignRight":
                        menuItem.Text = T("右对齐", "Align right");
                        menuItem.Checked = _card.TextAlignHorizontal == CardState.AlignFar;
                        break;
                    case "AlignJustify":
                        menuItem.Text = T("两端对齐", "Justify");
                        menuItem.Checked = _card.TextAlignHorizontal == CardState.AlignJustify;
                        break;
                    case "AlignTop":
                        menuItem.Text = T("顶部对齐", "Align top");
                        menuItem.Checked = _card.TextAlignVertical == CardState.AlignNear;
                        break;
                    case "AlignMiddle":
                        menuItem.Text = T("垂直居中", "Centre vertically");
                        menuItem.Checked = _card.TextAlignVertical == CardState.AlignCenter;
                        break;
                    case "AlignBottom":
                        menuItem.Text = T("底部对齐", "Align bottom");
                        menuItem.Checked = _card.TextAlignVertical == CardState.AlignFar;
                        break;
                    case "ContentSource": menuItem.Text = T("内容来源", "Content source"); break;
                    case "ContentDaily": menuItem.Text = T("跟随每日轮换", "Follow the daily rotation"); break;
                    case "ContentFixed": menuItem.Text = T("固定显示某一句…", "Pin one sentence…"); break;
                    case "CardSettings": menuItem.Text = T("卡片设置", "Card settings"); break;
                    case "CardSize": menuItem.Text = T("卡片大小", "Card size"); break;
                    case "SizeSmall": menuItem.Text = T("小", "Small"); break;
                    case "SizeMedium": menuItem.Text = T("中", "Medium"); break;
                    case "SizeLarge": menuItem.Text = T("大", "Large"); break;
                    case "SizeFitText": menuItem.Text = T("适应文字大小（最小）", "Fit to text (smallest)"); break;
                    case "BackgroundColor": menuItem.Text = T("背景颜色…", "Background color…"); break;
                    case "TransparentBackground": menuItem.Text = T("透明背景（仅显示文字）", "Transparent background (text only)"); break;
                    case "ClickThrough": menuItem.Text = T("鼠标穿透（点击直达下层窗口）", "Mouse pass-through (clicks reach the window below)"); break;
                    case "BackgroundImage": menuItem.Text = T("选择背景图片…", "Choose background image…"); break;
                    case "ClearBackgroundImage": menuItem.Text = T("清除背景图片", "Remove background image"); break;
                    case "ImageLayout": menuItem.Text = T("背景图片显示方式", "Image layout"); break;
                    case "ImageLayout0": menuItem.Text = T("铺满（裁切多余）", "Cover (crop)"); break;
                    case "ImageLayout1": menuItem.Text = T("适应（完整显示）", "Contain (fit)"); break;
                    case "ImageLayout2": menuItem.Text = T("拉伸（填满，会变形）", "Stretch (distorts)"); break;
                    case "ImageLayout3": menuItem.Text = T("平铺", "Tile"); break;
                    case "ImageLayout4": menuItem.Text = T("居中（原大小）", "Centre (original size)"); break;
                    case "CornerRadius": menuItem.Text = T("圆角", "Corner radius"); break;
                    case "CornerRadius0": menuItem.Text = T("直角", "Square"); break;
                    case "CornerRadius8": menuItem.Text = T("小", "Small"); break;
                    case "CornerRadius18": menuItem.Text = T("标准（默认）", "Normal (default)"); break;
                    case "CornerRadius28": menuItem.Text = T("大", "Large"); break;
                    case "CornerRadius40": menuItem.Text = T("很圆", "Very round"); break;
                    case "CornerRadiusCustom": menuItem.Text = T("自定义…", "Custom…"); break;
                    case "BorderWidth": menuItem.Text = T("边框", "Border"); break;
                    case "BorderWidth0": menuItem.Text = T("无", "None"); break;
                    case "BorderWidth1": menuItem.Text = T("细（默认）", "Thin (default)"); break;
                    case "BorderWidth2": menuItem.Text = T("中", "Medium"); break;
                    case "BorderWidth3": menuItem.Text = T("粗", "Thick"); break;
                    case "BorderColor": menuItem.Text = T("边框颜色…", "Border colour…"); break;
                    case "CardOpacityCustom": menuItem.Text = T("自定义…", "Custom…"); break;
                    case "CardLayer": menuItem.Text = T("卡片层级", "Card layer"); break;
                    case "LayerTop": menuItem.Text = T("置顶（显示在其他窗口前面）", "Top (above other windows)"); break;
                    case "LayerBottom": menuItem.Text = T("置底（允许其他窗口盖住卡片）", "Bottom (other windows can cover the card)"); break;
                    case "ResetAppearance": menuItem.Text = T("恢复默认样式", "Reset appearance"); break;
                    case "TextSettings": menuItem.Text = T("文字设置", "Text settings"); break;
                    case "TextLarger": menuItem.Text = T("大一点", "Larger"); break;
                    case "TextSmaller": menuItem.Text = T("小一点", "Smaller"); break;
                    case "TextSmallest": menuItem.Text = T("设为最小", "Smallest"); break;
                    case "TextExactSize": menuItem.Text = T("字号…", "Font size…"); break;
                    case "AutoFitText": menuItem.Text = T("文字自动适应卡片", "Fit text to card automatically"); break;
                    case "AutoFitMax": menuItem.Text = T("自动字号上限", "Largest fitted size"); break;
                    case "AutoFitMax29": menuItem.Text = T("29 磅（默认）", "29 pt (default)"); break;
                    case "AutoFitMax36": menuItem.Text = T("36 磅", "36 pt"); break;
                    case "AutoFitMax48": menuItem.Text = T("48 磅", "48 pt"); break;
                    case "AutoFitMax64": menuItem.Text = T("64 磅", "64 pt"); break;
                    case "AutoFitMax96": menuItem.Text = T("96 磅", "96 pt"); break;
                    case "LineSpacing": menuItem.Text = T("行距", "Line spacing"); break;
                    case "LineSpacing90": menuItem.Text = T("紧凑", "Tight"); break;
                    case "LineSpacing100": menuItem.Text = T("标准（默认）", "Normal (default)"); break;
                    case "LineSpacing120": menuItem.Text = T("宽松", "Roomy"); break;
                    case "LineSpacing150": menuItem.Text = T("很宽", "Wide"); break;
                    case "LetterSpacing": menuItem.Text = T("字距", "Letter spacing"); break;
                    case "LetterSpacing0": menuItem.Text = T("标准（默认）", "Normal (default)"); break;
                    case "LetterSpacing10": menuItem.Text = T("略开", "A little wider"); break;
                    case "LetterSpacing25": menuItem.Text = T("拉开", "Wider"); break;
                    case "LetterSpacing50": menuItem.Text = T("很开", "Widest"); break;
                    case "TextOutline": menuItem.Text = T("文字描边", "Text outline"); break;
                    case "TextOutline0": menuItem.Text = T("无（默认）", "None (default)"); break;
                    case "TextOutline4": menuItem.Text = T("细", "Thin"); break;
                    case "TextOutline8": menuItem.Text = T("中", "Medium"); break;
                    case "TextOutline12": menuItem.Text = T("粗", "Thick"); break;
                    case "PresetMenu": menuItem.Text = T("字体预设", "Text presets"); break;
                    case "PresetSave": menuItem.Text = T("保存当前文字样式…", "Save this text style…"); break;
                    case "PresetDelete": menuItem.Text = T("删除预设", "Delete a preset"); break;
                    case "PresetEmpty": menuItem.Text = T("（还没有预设）", "(no presets yet)"); break;
                    case "TextShadow": menuItem.Text = T("文字阴影", "Text shadow"); break;
                    case "TextShadow0": menuItem.Text = T("无（默认）", "None (default)"); break;
                    case "TextShadow3": menuItem.Text = T("浅", "Light"); break;
                    case "TextShadow6": menuItem.Text = T("中", "Medium"); break;
                    case "TextShadow10": menuItem.Text = T("深", "Deep"); break;
                    case "CardOpacity": menuItem.Text = T("不透明度", "Opacity"); break;
                    case "CardOpacity100": menuItem.Text = T("100%", "100%"); break;
                    case "CardOpacity97": menuItem.Text = T("97%（默认）", "97% (default)"); break;
                    case "CardOpacity90": menuItem.Text = T("90%", "90%"); break;
                    case "CardOpacity80": menuItem.Text = T("80%", "80%"); break;
                    case "CardOpacity70": menuItem.Text = T("70%", "70%"); break;
                    case "TextBold": menuItem.Text = T("加粗", "Bold"); break;
                    case "TextItalic": menuItem.Text = T("斜体", "Italic"); break;
                    case "ResetTextSize": menuItem.Text = T("恢复默认字号", "Reset text size"); break;
                    case "TextColor": menuItem.Text = T("文字颜色…", "Text color…"); break;
                    case "ChooseFont": menuItem.Text = T("选择字体…", "Choose font…"); break;
                    case "Startup": menuItem.Text = menuItem.Checked ?
                        T("开机自启动（已开启）", "Start with Windows (on)") :
                        T("开机自启动（未开启）", "Start with Windows (off)"); break;
                    case "BringCardBack": menuItem.Text = _hotKeyRegistered ?
                        T("🔍 寻找卡片（Ctrl+Alt+Q）", "🔍 Find card (Ctrl+Alt+Q)") :
                        T("🔍 寻找卡片", "🔍 Find card"); break;
                    case "OpenLibraryFolder": menuItem.Text = T("打开内容库文件位置", "Open content library location"); break;
                    case "Language": menuItem.Text = "语言 / Language"; break;
                    case "LanguageChinese":
                        menuItem.Text = "简体中文";
                        menuItem.Checked = !UiText.IsEnglish(_app);
                        break;
                    case "LanguageEnglish":
                        menuItem.Text = "English";
                        menuItem.Checked = UiText.IsEnglish(_app);
                        break;
                    case "Help": menuItem.Text = T("使用方法", "Help"); break;
                    case "Exit": menuItem.Text = T("退出", "Exit"); break;
                }
                if (menuItem.DropDownItems.Count > 0) ApplyMenuLanguage(menuItem.DropDownItems);
            }
        }

        private static void ApplyMenuColors(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = Color.FromArgb(28, 28, 28);
                item.ForeColor = Color.White;
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.DropDownItems.Count > 0)
                {
                    menuItem.DropDown.BackColor = Color.FromArgb(28, 28, 28);
                    menuItem.DropDown.ForeColor = Color.White;
                    menuItem.DropDown.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
                    ToolStripDropDownMenu dropDownMenu = menuItem.DropDown as ToolStripDropDownMenu;
                    if (dropDownMenu != null)
                    {
                        dropDownMenu.ShowImageMargin = false;
                        dropDownMenu.ShowCheckMargin = true;
                    }
                    ApplyMenuColors(menuItem.DropDownItems);
                }
            }
        }

        private ToolStripMenuItem CreateSizeItem(string name, int width, int height)
        {
            ToolStripMenuItem item = CreateMenuItem(name);
            item.Click += delegate
            {
                Size = new Size(width, height);
                // The card refuses to go below its text, so a preset can be held larger than it
                // asked for; what gets stored is the size the card actually became.
                _card.CardWidth = Width;
                _card.CardHeight = Height;
                KeepOnScreen();
                _app.Save();
            };
            return item;
        }

        // Snaps the card to the smallest size it may have: the text area exactly holding the text.
        // This is the least-area point, not the drag floor - the drag floor keeps whichever shape
        // the card already has, whereas "fit to text" is the one action that picks a shape.
        internal void FitCardToText()
        {
            if (_quoteView == null) return;
            // The drag floor can sit above the least-area card - a card dragged narrow has a tall
            // floor - and Form.Size is clamped to MinimumSize, so setting the size alone would be
            // refused and this would silently do nothing. Clear the floor for the duration: the
            // point below is a size the text fits in by construction, and the floor is recomputed
            // for it at the end.
            MinimumSize = new Size(CardState.MinCardWidth, CardState.MinCardHeight);
            // With auto-fit on the floor does not move, so the first pass lands on it. With auto-fit
            // off the font follows the width, so the pass is repeated until it settles; the bound
            // keeps a pathological measurement from looping.
            for (int pass = 0; pass < 4; pass++)
            {
                Size wanted = SmallestCard();
                if (wanted.Width == Width && wanted.Height == Height) break;
                Size = wanted;
            }
            UpdateCardSizeLimits();
            _card.CardWidth = Width;
            _card.CardHeight = Height;
            KeepOnScreen();
            _app.Save();
        }

        private void SetCardLayer(bool topMost)
        {
            TopMost = topMost;
            _card.TopMost = topMost;
            if (_transparentHitBox != null) _transparentHitBox.TopMost = topMost;
            _app.Save();
            PositionTransparentHitBox();
        }

        private string T(string chinese, string english)
        {
            return UiText.Get(_app, chinese, english);
        }

        // The tray icon is the only way back once a card ignores the mouse, so it says so - and it
        // names the card count, because that is also where the other cards are managed from.
        private void UpdateTrayTooltip()
        {
            if (_trayIcon == null) return;
            string tooltip = Manager == null ? Text : Manager.TrayTooltip;
            if (_card != null && _card.ClickThrough)
                tooltip = T("小笺（鼠标穿透中）", "XiaoJian (mouse pass-through)");
            // Windows caps NotifyIcon.Text at 63 characters.
            _trayIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        private void SetLanguage(string language)
        {
            _app.UiLanguage = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ?
                "en" : "zh-CN";
            _repository.RefreshDefaultTextLanguage();
            _quoteView.Text = _picker.RefreshAfterEdit();
            _app.Save();
            // The language belongs to the application, so every card has to follow it.
            if (Manager != null) Manager.RefreshAllCards();
            ApplyUiLanguage();
            FitQuoteFont();
        }

        private void ApplyUiLanguage()
        {
            Text = T("小笺", "XiaoJian");
            _quoteView.AccessibleName = T("当前内容", "Current entry");
            _menuLabel.AccessibleName = T("设置", "Settings");
            UpdateTrayTooltip();
            if (_transparentHitBox != null && !_transparentHitBox.IsDisposed)
                _transparentHitBox.Text = T("小笺透明点击层", "XiaoJian transparent interaction layer");
            if (_menu != null)
            {
                ApplyMenuLanguage(_menu.Items);
                ApplyMenuColors(_menu.Items);
            }
        }

        private void ToggleStartup()
        {
            bool requested = _startupItem.Checked;
            try
            {
                StartupManager.SetEnabled(requested);
                bool actual = StartupManager.IsEnabled();
                if (actual != requested) throw new InvalidOperationException(
                    T("Windows 没有保存这项设置。", "Windows did not save this setting."));
                _startupItem.Checked = actual;
                ApplyMenuLanguage(_menu.Items);
                _app.StartupInitialized = true;
                _app.StartupEnableFailed = false;
                _app.Save();
            }
            catch (Exception ex)
            {
                try { _startupItem.Checked = StartupManager.IsEnabled(); }
                catch { _startupItem.Checked = false; }
                ApplyMenuLanguage(_menu.Items);
                MessageBox.Show(T("没能修改开机启动设置。", "Could not change the startup setting.") +
                    "\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void EditQuotes()
        {
            // Every card owns its library, so the editor says which one is open - with several
            // cards on screen the window would otherwise look like the same library every time.
            string label = Manager == null ? "" : Manager.LibraryLabel(this);
            using (QuoteEditorForm editor = new QuoteEditorForm(_repository, label))
            {
                if (editor.ShowDialog(this) == DialogResult.OK)
                {
                    _quoteView.Text = _picker.RefreshAfterEdit();
                    FitQuoteFont();
                }
            }
        }

        // Puts this card back on the shared rotation and shows today's entry straight away.
        private void SwitchToDailyContent()
        {
            _card.ContentMode = CardState.ContentModeDaily;
            _app.Save();
            _quoteView.Text = _picker.EnsureToday();
            FitQuoteFont();
        }

        // Pins this card to a single sentence, starting from whatever it shows right now.
        private void PinCurrentContent()
        {
            string current = _card.IsFixedContent && _card.FixedText.Length > 0 ?
                _card.FixedText : _quoteView.Text;
            string entered = TextInputForm.Prompt(this, _app,
                T("固定显示的内容", "Pinned sentence"),
                T("这张卡片会一直显示这句话，不再跟随每日轮换。",
                    "This card will always show this sentence instead of the daily rotation."),
                current);
            if (entered == null) return;
            if (entered.Trim().Length == 0)
            {
                MessageBox.Show(T("内容不能为空。", "The entry cannot be empty."), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _quoteView.Text = _picker.SetFixedText(entered);
            FitQuoteFont();
        }

        // Rebuilds the card entries. The items carry no stable name, so ApplyMenuLanguage leaves
        // their text alone and the list is rebuilt on every open, which keeps it current.
        internal void RefreshCardList()
        {
            if (_cardListMenu == null || Manager == null) return;
            // Old entries are disposed rather than dropped, so a long session cannot pile up menu
            // items that are never collected.
            while (_cardListMenu.DropDownItems.Count > 0)
            {
                ToolStripItem stale = _cardListMenu.DropDownItems[0];
                _cardListMenu.DropDownItems.RemoveAt(0);
                stale.Dispose();
            }
            List<QuoteCardForm> forms = Manager.Forms;
            for (int index = 0; index < forms.Count; index++)
            {
                QuoteCardForm target = forms[index];
                ToolStripMenuItem item = new ToolStripMenuItem();
                item.Name = "CardEntry" + index.ToString(CultureInfo.InvariantCulture);
                item.Text = Manager.DescribeCard(index, target);
                item.Checked = target.Visible;
                QuoteCardForm selected = target;
                item.Click += delegate
                {
                    // Hiding a card from inside this menu can leave the drop-down without an
                    // owner window, so the toggle also waits for the click message to finish.
                    RunAfterMenu(delegate
                    {
                        if (selected != null && !selected.IsDisposed) selected.ToggleCardVisible();
                    });
                };
                _cardListMenu.DropDownItems.Add(item);
            }
        }

        internal void ToggleCardVisible()
        {
            bool show = !Visible;
            Visible = show;
            _card.Visible = show;
            _app.Save();
            UpdateTransparentHitBox();
            if (Manager != null) Manager.UpdateTrayTooltip();
        }

        // Handing the tray icon over keeps the one icon per application, whichever card survives.
        internal void SetTrayIconVisible(bool visible)
        {
            if (_trayIcon == null) return;
            _trayIcon.Visible = visible;
            if (visible) UpdateTrayTooltip();
        }

        internal bool TrayIconVisible
        {
            get { return _trayIcon != null && _trayIcon.Visible; }
        }

        // Re-applies the language to a card that did not trigger the change itself. Each card
        // carries its own library, so each card has to translate its own first-run text.
        internal void RefreshUi()
        {
            _repository.RefreshDefaultTextLanguage();
            ApplyUiLanguage();
            Invalidate(true);
        }

        private void OpenQuoteFolder()
        {
            try
            {
                Process.Start("explorer.exe", "/select,\"" + _repository.FilePath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(T("没能打开内容库文件位置。", "Could not open the content library location.") +
                    "\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ChooseBackgroundColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = BackColor;
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _card.BackgroundColorArgb = dialog.Color.ToArgb();
                _card.TransparentBackground = false;
                _app.Save();
                ApplyAppearance(false);
                RecreateHandle();
                UpdateTransparentHitBox();
            }
        }

        private void ChooseBorderColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = SafeColor(_card.BorderColorArgb, Color.FromArgb(42, 42, 42));
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBorderColor(dialog.Color.ToArgb());
            }
        }

        // Any whole number, not just the five in the menu. The modal dialog closes the menu that
        // opened it, so nothing here has to be deferred.
        private void AskCardOpacity()
        {
            string entered = TextInputForm.Prompt(this, _app,
                T("不透明度", "Opacity"),
                T("输入 10 到 100 之间的整数（百分比）。",
                    "Type a whole number from 10 to 100 (percent)."),
                _card.CardOpacityPercent.ToString(CultureInfo.InvariantCulture));
            if (entered == null) return;
            int percent;
            if (!int.TryParse(entered.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out percent) ||
                percent < CardState.MinCardOpacityPercent ||
                percent > CardState.MaxCardOpacityPercent)
            {
                MessageBox.Show(this,
                    T("请输入 10 到 100 之间的整数。", "Type a whole number from 10 to 100."),
                    T("不透明度", "Opacity"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetCardOpacity(percent);
        }

        private void AskCornerRadius()
        {
            string entered = TextInputForm.Prompt(this, _app,
                T("圆角", "Corner radius"),
                T("输入圆角半径（0 到 40 像素）。0 是直角。",
                    "Type a corner radius from 0 to 40 pixels. 0 is square."),
                _card.CornerRadius.ToString(CultureInfo.InvariantCulture));
            if (entered == null) return;
            int radius;
            if (!int.TryParse(entered.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out radius) ||
                radius < CardState.MinCornerRadius || radius > CardState.MaxCornerRadius)
            {
                MessageBox.Show(this,
                    T("请输入 0 到 40 之间的整数。", "Type a whole number from 0 to 40."),
                    T("圆角", "Corner radius"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetCornerRadius(radius);
        }

        private void ChooseTextColor()
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = _quoteView.ForeColor;
                dialog.FullOpen = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _card.TextColorArgb = dialog.Color.ToArgb();
                _app.Save();
                ApplyAppearance(false);
            }
        }

        private void ChooseQuoteFont()
        {
            using (FontDialog dialog = new FontDialog())
            using (Font current = QuoteFont(CurrentFontSize()))
            {
                dialog.Font = current;
                dialog.FontMustExist = true;
                dialog.ShowColor = false;
                dialog.ShowEffects = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ApplyPickedFont(dialog.Font);
            }
        }

        // Split out of the dialog so the size handling can be exercised without a modal window:
        // a size picked in the font chooser is the user's, exactly like the four size entries.
        internal void ApplyPickedFont(Font picked)
        {
            if (picked == null) return;
            _card.FontName = picked.FontFamily.Name;
            _card.FontStyleValue = (int)picked.Style;
            // The size picked in the dialog has to be used. It used to be dropped: only the
            // family and the style were taken, and then FitQuoteFont worked the size out again
            // from the card - so a size chosen here did nothing at all, and the only reason it
            // ever looked like it worked is that it happened to agree with what automatic
            // fitting had already picked. That is "the font size dialog stops having any effect
            // above a certain number": the fitted size sits on the small side, so a small value
            // looks unchanged and a large one plainly does nothing.
            float before = CurrentFontSize();
            float size = picked.SizeInPoints;
            _app.Save();
            _quoteFontSize = -1.0f;
            if (Math.Abs(size - before) >= 0.05f)
            {
                // Taking the size into their own hands also turns automatic fitting off,
                // otherwise the very next fit hands it straight back.
                SetManualFontSize(size);
            }
            else
            {
                // Only the family or the style changed. The card keeps the size it had, and with
                // automatic fitting on it keeps working it out - the new face measures
                // differently, and that is what the fit is for.
                FitQuoteFont();
            }
        }

        private void ChooseBackgroundImage()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = T("选择卡片背景图片", "Choose a card background image");
                dialog.Filter = T("图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
                    "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*");
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                Image preview = null;
                try
                {
                    preview = LoadImageUnlocked(dialog.FileName);
                    string extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                    if (extension.Length == 0 || extension.Length > 5) extension = ".img";
                    string dataDirectory = Path.GetDirectoryName(_repository.FilePath);
                    // One background per card. A single shared name meant the last card to pick an
                    // image changed what every card was showing.
                    string destination = Path.Combine(dataDirectory,
                        "background-" + _card.Slot.ToString(CultureInfo.InvariantCulture) + extension);
                    if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(destination),
                        StringComparison.OrdinalIgnoreCase))
                        File.Copy(dialog.FileName, destination, true);

                    string previous = _card.BackgroundImagePath;
                    if (_backgroundImage != null) _backgroundImage.Dispose();
                    _backgroundImage = preview;
                    preview = null;
                    _card.BackgroundImagePath = destination;
                    _card.TransparentBackground = false;
                    _app.Save();
                    if (!string.Equals(previous, destination, StringComparison.OrdinalIgnoreCase))
                        TryDeleteOwnedBackground(previous);
                    ApplyAppearance(false);
                    RecreateHandle();
                    UpdateTransparentHitBox();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(T("没能使用这张背景图片。", "Could not use this background image.") +
                        "\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    if (preview != null) preview.Dispose();
                }
            }
        }

        private void ClearBackgroundImage(bool save)
        {
            string previous = _card.BackgroundImagePath;
            if (_backgroundImage != null)
            {
                _backgroundImage.Dispose();
                _backgroundImage = null;
            }
            _card.BackgroundImagePath = "";
            if (save) _app.Save();
            TryDeleteOwnedBackground(previous);
            ApplyAppearance(false);
        }

        private void ResetAppearance()
        {
            ClearBackgroundImage(false);
            _card.BackgroundColorArgb = Color.White.ToArgb();
            _card.TextColorArgb = Color.Black.ToArgb();
            _card.FontName = "Microsoft YaHei UI";
            _card.FontStyleValue = (int)FontStyle.Regular;
            _card.TransparentBackground = false;
            _card.CardOpacityPercent = CardState.DefaultCardOpacityPercent;
            _card.CornerRadius = CardState.DefaultCornerRadius;
            _card.BorderWidth = CardState.DefaultBorderWidth;
            _card.BorderColorArgb = CardState.DefaultBorderColorArgb;
            _card.BackgroundImageLayout = CardState.DefaultImageLayout;
            _app.Save();
            _quoteFontSize = -1.0f;
            ApplyAppearance(false);
            FitQuoteFont();
            RecreateHandle();
            UpdateTransparentHitBox();
        }

        private void ApplyAppearance(bool reloadImage)
        {
            if (_card.EnsureReadableTextColor()) _app.Save();
            BackColor = SafeColor(_card.BackgroundColorArgb, Color.White);
            ForeColor = SafeColor(_card.TextColorArgb, Color.Black);
            _quoteView.ForeColor = ForeColor;
            // The halo follows the card: the background colour on a solid card, white or black
            // when there is something behind the text.
            _quoteView.OutlineColor = SafeColor(CardState.OutlineColorArgbFor(_card), Color.White);
            _quoteView.ShadowColor = SafeColor(CardState.ShadowColorArgbFor(_card), Color.Black);
            _menuLabel.ForeColor = Color.FromArgb(175, ForeColor.R, ForeColor.G, ForeColor.B);
            if (_card.TransparentBackground)
            {
                TransparencyKey = TransparentKeyColor;
                // TransparencyKey and Opacity both drive the layered window, and setting the
                // opacity here would fight the colour key - so the card stays fully opaque in this
                // mode and the menu entry is greyed out.
                Opacity = 1.0;
            }
            else
            {
                TransparencyKey = Color.Empty;
                Opacity = Math.Max(0.05,
                    Math.Min(1.0, _card.CardOpacityPercent / 100.0));
            }
            if (reloadImage) LoadBackgroundImage();
            Invalidate(true);
        }

        private void UpdateTransparentHitBox()
        {
            if (_locatingCard || _card.ClickThrough || !_card.TransparentBackground || !Visible || IsDisposed)
            {
                if (_transparentHitBox != null) _transparentHitBox.Hide();
                return;
            }

            if (_transparentHitBox == null || _transparentHitBox.IsDisposed)
            {
                _transparentHitBox = new TransparentHitBoxForm();
                _transparentHitBox.ContextMenuStrip = _menu;
                AttachDragHandlers(_transparentHitBox);
                _transparentHitBox.DoubleClick += delegate { EditQuotes(); };
                _transparentHitBox.UserBoundsChanged += delegate
                {
                    if (_transparentHitBox == null || _transparentHitBox.IsDisposed) return;
                    ApplyHitFrame(_transparentHitBox.Bounds);
                };
                _transparentHitBox.UserResizeFinished += delegate
                {
                    SaveCardBounds();
                    PositionTransparentHitBox();
                };
            }
            PositionTransparentHitBox();
        }

        private void PositionTransparentHitBox()
        {
            if (_transparentHitBox == null || _transparentHitBox.IsDisposed) return;
            // In pass-through mode nothing may receive the mouse, so the interaction layer goes away.
            if (_locatingCard || _card.ClickThrough || !_card.TransparentBackground || !Visible)
            {
                _transparentHitBox.Hide();
                return;
            }

            _transparentHitBox.TopMost = TopMost;
            // While the pointer owns the frame rectangle, re-aligning it would fight the drag that
            // is in progress, so alignment waits until the gesture ends.
            if (!HitFrameOwnedByPointer())
                _transparentHitBox.SetHitFrameBounds(RectangleToScreen(QuoteHitFrameInClient()));
            _lastHitFrame = _transparentHitBox.Bounds;
            if (!_transparentHitBox.Visible) _transparentHitBox.Show();
            if (IsHandleCreated && _transparentHitBox.IsHandleCreated)
            {
                const uint SWP_NOSIZE = 0x0001;
                const uint SWP_NOMOVE = 0x0002;
                const uint SWP_NOACTIVATE = 0x0010;
                NativeMethods.SetWindowPos(_transparentHitBox.Handle, Handle, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
        }

        // The frame rectangle belongs to the pointer while a card drag is running, while the system
        // is inside a move/size modal loop, or while any mouse button is held down. Aligning the
        // frame in those moments would move it out from under the pointer and make the drag stutter.
        private bool HitFrameOwnedByPointer()
        {
            return _dragging || Control.MouseButtons != MouseButtons.None ||
                _transparentHitBox.IsUserInteracting;
        }

        // The area the rendered quote actually occupies. Aligning the invisible interaction frame
        // with this rectangle keeps the empty part of a transparent card click-through, so clicks
        // land on whatever is behind it instead of being swallowed by an oversized frame.
        private Rectangle QuoteHitFrameInClient()
        {
            if (_quoteView == null) return Rectangle.Empty;
            Rectangle view = _quoteView.Bounds;
            if (view.Width <= 0 || view.Height <= 0) return view;

            int width = HitFrameMinimumWidth;
            int height = HitFrameMinimumHeight;
            // The same measurement paints the text and sizes the frame, so the two can never
            // disagree - vertical columns included.
            Size block = _quoteView.MeasureBlock();
            if (block.Width > 0 && block.Height > 0)
            {
                width = Math.Max(width, block.Width + HitFramePadding * 2);
                height = Math.Max(height, block.Height + HitFramePadding * 2);
            }
            width = Math.Min(width, view.Width);
            height = Math.Min(height, view.Height);
            return new Rectangle(view.Left + (view.Width - width) / 2,
                view.Top + (view.Height - height) / 2, width, height);
        }

        // Converts a frame rectangle reported by the interaction layer into card bounds. The card
        // size follows the frame size one to one, so dragging an edge or corner still resizes the
        // card exactly as before, and dragging the frame still moves the card.
        private void ApplyHitFrame(Rectangle frame)
        {
            if (_lastHitFrame.IsEmpty)
            {
                _lastHitFrame = frame;
                return;
            }

            int width = Math.Max(MinimumSize.Width,
                Math.Min(MaximumSize.Width, Width + (frame.Width - _lastHitFrame.Width)));
            int height = Math.Max(MinimumSize.Height,
                Math.Min(MaximumSize.Height, Height + (frame.Height - _lastHitFrame.Height)));
            Bounds = new Rectangle(Left + (frame.Left - _lastHitFrame.Left),
                Top + (frame.Top - _lastHitFrame.Top), width, height);
            // Re-read the frame: re-aligning it above (or a clamped card size) may have changed it.
            _lastHitFrame = _transparentHitBox == null || _transparentHitBox.IsDisposed ?
                frame : _transparentHitBox.Bounds;
            _card.X = Left;
            _card.Y = Top;
            _card.CardWidth = Width;
            _card.CardHeight = Height;
        }

        private void LoadBackgroundImage()
        {
            if (_backgroundImage != null)
            {
                _backgroundImage.Dispose();
                _backgroundImage = null;
            }
            if (string.IsNullOrEmpty(_card.BackgroundImagePath) ||
                !File.Exists(_card.BackgroundImagePath)) return;
            try
            {
                _backgroundImage = LoadImageUnlocked(_card.BackgroundImagePath);
            }
            catch
            {
                _card.BackgroundImagePath = "";
                _app.Save();
            }
        }

        private static Image LoadImageUnlocked(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image source = Image.FromStream(stream))
                return new Bitmap(source);
        }

        private void TryDeleteOwnedBackground(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                string dataDirectory = Path.GetFullPath(Path.GetDirectoryName(_repository.FilePath));
                string candidate = Path.GetFullPath(path);
                if (!string.Equals(Path.GetDirectoryName(candidate), dataDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(candidate).StartsWith("background", StringComparison.OrdinalIgnoreCase))
                    return;
                // Another card may still be showing the very same file: images used to be stored
                // under one shared name, so a card that never chose one can be pointing at it.
                foreach (CardState other in _app.Cards)
                {
                    if (other == _card || string.IsNullOrEmpty(other.BackgroundImagePath)) continue;
                    if (string.Equals(Path.GetFullPath(other.BackgroundImagePath), candidate,
                        StringComparison.OrdinalIgnoreCase)) return;
                }
                File.Delete(candidate);
            }
            catch
            {
                // A locked old image can be left in place without affecting the card.
            }
        }

        private void BringCardToFront()
        {
            KeepOnScreen();
            Show();
            WindowState = FormWindowState.Normal;
            UpdateTransparentHitBox();
            Activate();
            BringToFront();
            PositionTransparentHitBox();
        }

        private void LocateCard()
        {
            if (IsDisposed || Disposing) return;
            _locateTimer.Stop();
            _locatingCard = true;
            // A found card must be grabbable, so pass-through is suspended for the highlight.
            ApplyClickThroughStyle();
            WindowState = FormWindowState.Normal;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2,
                area.Top + (area.Height - Height) / 2);
            Show();
            TopMost = true;
            TransparencyKey = Color.Empty;
            Opacity = 1.0;
            _quoteView.ForeColor = Color.Black;
            _menuLabel.ForeColor = Color.FromArgb(180, 0, 0, 0);
            if (_transparentHitBox != null) _transparentHitBox.Hide();
            Invalidate(true);
            Activate();
            BringToFront();
            SaveCardBounds();
            _locateTimer.Start();
        }

        private void EndLocateCard()
        {
            _locateTimer.Stop();
            if (!_locatingCard || IsDisposed || Disposing) return;
            _locatingCard = false;
            ApplyClickThroughStyle();
            TopMost = _card.TopMost;
            ApplyAppearance(false);
            UpdateTransparentHitBox();
            Invalidate(true);
        }

        private void ShowFindCardHint()
        {
            MessageBox.Show(this, T(
                "如果以后突然找不到卡片：\n\n按下  Ctrl + Alt + Q\n\n也可以双击屏幕右下角的小笺小图标，\n或者再次打开小笺。\n\n卡片会移动到屏幕中央，并醒目显示 6 秒。",
                "If the card ever goes missing:\n\nPress  Ctrl + Alt + Q\n\nYou can also double-click the XiaoJian tray icon,\nor open XiaoJian again.\n\nThe card will move to the center and highlight itself for 6 seconds."),
                T("请记住：怎样寻找卡片", "Please remember: how to find your card"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RefreshForToday()
        {
            _quoteView.Text = _picker.EnsureToday();
            FitQuoteFont();
        }

        // Takes the font size into the user's hands. With auto-fit on, the size on screen comes
        // from the card, and the entry that asked for a larger font would be undone by the very
        // next fit - the card is only as big as its text, so "the largest size that fits" is
        // already the size being shown. Picking a size therefore also turns auto-fit off, which
        // is visible in the menu, and the card is then grown to hold the text so the new size is
        // never handed back to the user as clipped text.
        private void SetManualFontSize(float size)
        {
            _card.AutoFitText = false;
            _card.ManualFontSize = Math.Max(CardState.MinManualFontSize,
                Math.Min(CardState.MaxManualFontSize, size));
            _quoteFontSize = -1.0f;
            _app.Save();
            FitQuoteFont();
            GrowCardToText();
            BackOffToFittingSize();
        }

        // The card has been grown as far as the desktop allows and the text still does not fit.
        // Rather than hand the user a size that is clipped away, the size is walked back to the
        // largest one that does fit - which is the ceiling this card can actually offer. A card
        // that already fills the whole desktop is the end of the road, and "a little larger"
        // should stop there, not quietly cut the text off.
        //
        // Without this, growing a font past what fits looks like "the text will not get any
        // bigger": the number in the menu keeps climbing and nothing on screen changes, because
        // every extra point is outside the card.
        private void BackOffToFittingSize()
        {
            if (_quoteView == null || _quoteFont == null) return;
            if (_quoteView.FitsBlock(_quoteFont)) return;
            for (float size = _quoteFontSize - 1.0f;
                 size >= CardState.MinManualFontSize; size -= 1.0f)
            {
                using (Font candidate = QuoteFont(size))
                {
                    if (!_quoteView.FitsBlock(candidate)) continue;
                    _card.ManualFontSize = size;
                    _quoteFontSize = -1.0f;
                    _app.Save();
                    FitQuoteFont();
                    return;
                }
            }
        }

        // Asks for the size directly. "A little larger" is two points at a time, so 7 to 96 is
        // forty-five clicks; a number is one. The modal dialog closes the menu that opened it,
        // which is why resizing the card afterwards is safe.
        private void AskFontSize()
        {
            string entered = TextInputForm.Prompt(this, _app,
                T("字号", "Font size"),
                T("输入字号（7 到 96 磅之间）。", "Type a size between 7 and 96 points."),
                CurrentFontSize().ToString("0.#", CultureInfo.InvariantCulture));
            if (entered == null) return;
            float size;
            if (!float.TryParse(entered.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out size) ||
                size < CardState.MinManualFontSize || size > CardState.MaxManualFontSize)
            {
                MessageBox.Show(this,
                    T("请输入 7 到 96 之间的数字。", "Type a number between 7 and 96."),
                    T("字号", "Font size"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetManualFontSize(size);
        }

        // Rebuilt on every open: the entries carry the presets' own names, which no language table
        // knows about, and the list changes as presets are saved and deleted. Old entries are
        // disposed rather than dropped, the same way the card list is handled.
        private void RefreshPresetMenu()
        {
            if (_presetMenu == null) return;
            while (_presetMenu.DropDownItems.Count > 0)
            {
                ToolStripItem stale = _presetMenu.DropDownItems[0];
                _presetMenu.DropDownItems.RemoveAt(0);
                stale.Dispose();
            }
            List<TextPreset> presets = _app.Presets;
            if (presets.Count == 0)
            {
                ToolStripMenuItem empty = CreateMenuItem("PresetEmpty");
                empty.Enabled = false;
                _presetMenu.DropDownItems.Add(empty);
            }
            else
            {
                for (int index = 0; index < presets.Count; index++)
                {
                    string name = presets[index].Name;
                    ToolStripMenuItem item = new ToolStripMenuItem();
                    item.Name = "PresetEntry" + index.ToString(CultureInfo.InvariantCulture);
                    item.Text = name;
                    item.Click += delegate { ApplyPreset(name); };
                    _presetMenu.DropDownItems.Add(item);
                }
                _presetMenu.DropDownItems.Add(new ToolStripSeparator());
                ToolStripMenuItem deleteMenu = CreateMenuItem("PresetDelete");
                for (int index = 0; index < presets.Count; index++)
                {
                    string name = presets[index].Name;
                    ToolStripMenuItem item = new ToolStripMenuItem();
                    item.Name = "PresetDeleteEntry" + index.ToString(CultureInfo.InvariantCulture);
                    item.Text = name;
                    item.Click += delegate { DeletePreset(name); };
                    deleteMenu.DropDownItems.Add(item);
                }
                _presetMenu.DropDownItems.Add(deleteMenu);
            }
            _presetMenu.DropDownItems.Add(new ToolStripSeparator());
            _presetMenu.DropDownItems.Add(CreateMenuItem("PresetSave"));
        }

        // Saving under a name that already exists replaces it, which is how a preset is edited:
        // adjust the card, save again under the same name.
        private void SavePreset()
        {
            string entered = TextInputForm.Prompt(this, _app,
                T("保存文字样式", "Save text style"),
                T("给这套文字样式起个名字。同名的会被覆盖。",
                    "Name this text style. An existing name is replaced."),
                "");
            if (entered == null) return;
            string name = entered.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, T("名字不能为空。", "The name cannot be empty."),
                    T("保存文字样式", "Save text style"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            TextPreset preset = TextPreset.FromCard(name, _card);
            int existing = IndexOfPreset(name);
            if (existing >= 0) _app.Presets[existing] = preset;
            else _app.Presets.Add(preset);
            _app.Save();
        }

        private void DeletePreset(string name)
        {
            if (MessageBox.Show(this,
                    T("删除预设「" + name + "」？", "Delete the preset \"" + name + "\"?"),
                    T("删除预设", "Delete preset"), MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            int existing = IndexOfPreset(name);
            if (existing < 0) return;
            _app.Presets.RemoveAt(existing);
            _app.Save();
        }

        private int IndexOfPreset(string name)
        {
            for (int index = 0; index < _app.Presets.Count; index++)
                if (string.Equals(_app.Presets[index].Name, name, StringComparison.Ordinal))
                    return index;
            return -1;
        }

        // Applying a preset is the same three steps every other text setting takes, plus the two
        // colours the view keeps its own copy of.
        internal void ApplyPreset(string name)
        {
            int existing = IndexOfPreset(name);
            if (existing < 0) return;
            _app.Presets[existing].ApplyTo(_card);
            // The preset's colour is used as saved. ApplyAppearance would run the readability
            // guard first, which silently overrides a low-contrast choice - and "the preset did not
            // apply" is exactly what that looks like. The derived copies are refreshed here so
            // nothing is left showing the previous colour.
            ForeColor = SafeColor(_card.TextColorArgb, Color.Black);
            _quoteView.ForeColor = ForeColor;
            _menuLabel.ForeColor = Color.FromArgb(175, ForeColor.R, ForeColor.G, ForeColor.B);
            _quoteView.LineSpacingPercent = _card.LineSpacingPercent;
            _quoteView.LetterSpacingPercent = _card.LetterSpacingPercent;
            _quoteView.TextOutlinePercent = _card.TextOutlinePercent;
            _quoteView.OutlineColor = SafeColor(CardState.OutlineColorArgbFor(_card), Color.White);
            _app.Save();
            _quoteFontSize = -1.0f;
            FitQuoteFont();
            GrowCardToText();
            BackOffToFittingSize();
        }

        // Ticks the entry whose value the card is using. Read from the card every time the menu
        // opens rather than trusted to whatever the last click left behind.
        private static void MarkCurrent(ToolStripMenuItem parent, int value)
        {
            if (parent == null) return;
            foreach (ToolStripItem entry in parent.DropDownItems)
            {
                ToolStripMenuItem item = entry as ToolStripMenuItem;
                if (item == null || !(item.Tag is int)) continue;
                item.Checked = (int)item.Tag == value;
            }
        }

        private bool HasFontStyle(FontStyle style)
        {
            return (_card.FontStyleValue & (int)style) != 0;
        }

        // Bold and italic change how wide the glyphs are, so the size that fitted before may not
        // fit now. The fit is re-run and the card grown, exactly as if a size had been picked by
        // hand: letting the text clip and waiting for the next resize is how "the text stopped
        // fitting" gets reported.
        // Internal so the self-test can run it: a menu click is the only other caller.
        internal void SetFontStyle(FontStyle style, bool on)
        {
            int value = _card.FontStyleValue;
            _card.FontStyleValue = on ? (value | (int)style) : (value & ~(int)style);
            _quoteFontSize = -1.0f;
            _app.Save();
            FitQuoteFont();
            GrowCardToText();
            BackOffToFittingSize();
        }

        // The ceiling automatic fitting shrinks from. Raising it on a card with room left makes the
        // text bigger; lowering it makes it smaller. Either way the card is only ever grown, never
        // shrunk, so the card stays where the user put it.
        internal void SetAutoFitMax(float cap)
        {
            _card.AutoFitMaxFontSize = Math.Max(CardState.MinAutoFitMaxFontSize,
                Math.Min(CardState.MaxManualFontSize, cap));
            _quoteFontSize = -1.0f;
            _app.Save();
            FitQuoteFont();
            GrowCardToText();
            BackOffToFittingSize();
        }

        // Spacing changes how much room the text needs, so the block is re-measured and the card
        // grown - the same three steps a font change takes. Tightening the spacing can also let a
        // larger font fit, which is why the fit is re-run rather than just repainted.
        internal void SetLineSpacing(int percent)
        {
            _card.LineSpacingPercent = Math.Max(CardState.MinLineSpacingPercent,
                Math.Min(CardState.MaxLineSpacingPercent, percent));
            _quoteView.LineSpacingPercent = _card.LineSpacingPercent;
            _app.Save();
            _quoteFontSize = -1.0f;
            FitQuoteFont();
            GrowCardToText();
            BackOffToFittingSize();
        }

        internal void SetLetterSpacing(int percent)
        {
            _card.LetterSpacingPercent = Math.Max(CardState.MinLetterSpacingPercent,
                Math.Min(CardState.MaxLetterSpacingPercent, percent));
            _quoteView.LetterSpacingPercent = _card.LetterSpacingPercent;
            _app.Save();
            _quoteFontSize = -1.0f;
            FitQuoteFont();
            GrowCardToText();
            BackOffToFittingSize();
        }

        // A halo changes the block the text occupies - it sits outside the glyphs - so the card is
        // re-measured and grown, the same three steps a font or spacing change takes.
        internal void SetTextOutline(int percent)
        {
            _card.TextOutlinePercent = Math.Max(0,
                Math.Min(CardState.MaxTextOutlinePercent, percent));
            _quoteView.TextOutlinePercent = _card.TextOutlinePercent;
            _app.Save();
            _quoteFontSize = -1.0f;
            FitQuoteFont();
            GrowCardToText();
            BackOffToFittingSize();
        }

        // A shadow changes the block the text occupies - it is drawn outside the glyphs - so the
        // card is re-measured and grown, the same three steps the other text settings take.
        internal void SetTextShadow(int percent)
        {
            _card.TextShadowPercent = Math.Max(0,
                Math.Min(CardState.MaxTextShadowPercent, percent));
            _quoteView.TextShadowPercent = _card.TextShadowPercent;
            _app.Save();
            _quoteFontSize = -1.0f;
            FitQuoteFont();
            GrowCardToText();
            BackOffToFittingSize();
        }

        // Opacity is only a repaint: it changes nothing about how the text is laid out.
        internal void SetCardOpacity(int percent)
        {
            _card.CardOpacityPercent = Math.Max(CardState.MinCardOpacityPercent,
                Math.Min(CardState.MaxCardOpacityPercent, percent));
            _app.Save();
            ApplyAppearance(false);
        }

        // The corner is the window region and the painted border at once, so the region has to be
        // cut again - a repaint alone would leave the old shape clipping the new outline.
        internal void SetCornerRadius(int radius)
        {
            _card.CornerRadius = Math.Max(CardState.MinCornerRadius,
                Math.Min(CardState.MaxCornerRadius, radius));
            _app.Save();
            UpdateRoundedRegion();
            Invalidate();
        }

        // Width and colour are a repaint each; neither touches the shape or the text.
        internal void SetBorderWidth(int width)
        {
            _card.BorderWidth = Math.Max(CardState.MinBorderWidth,
                Math.Min(CardState.MaxBorderWidth, width));
            _app.Save();
            Invalidate();
        }

        internal void SetBorderColor(int argb)
        {
            _card.BorderColorArgb = argb;
            _app.Save();
            Invalidate();
        }

        internal void SetBackgroundImageLayout(int layout)
        {
            _card.BackgroundImageLayout = Math.Max(CardState.MinImageLayout,
                Math.Min(CardState.MaxImageLayout, layout));
            _app.Save();
            Invalidate();
        }

        // The size the card is showing right now, which is what "a little larger" is relative to.
        // The font search is the only thing that knows it, so the field it records is read back.
        private float CurrentFontSize()
        {
            if (_quoteFontSize > 0f) return _quoteFontSize;
            if (_quoteView != null && _quoteView.Font != null) return _quoteView.Font.SizeInPoints;
            return CardState.MinManualFontSize;
        }

        // Grows the card until the text fits, and never shrinks it. With a hand-picked size the
        // font no longer follows the card, so the size the text needs does not move while the card
        // grows and one pass settles it - the loop bound is only there so a measurement that never
        // agrees with itself cannot hang the menu.
        private void GrowCardToText()
        {
            if (_quoteView == null) return;
            for (int pass = 0; pass < 4; pass++)
            {
                // Grow along the axis the text has to have, and leave the other one exactly where
                // the user put it: a wider card would fit in fewer lines, but widening it is not
                // what was asked for.
                Size wanted = DragLimit();
                // ... unless that axis has already reached the desktop and the text still does not
                // fit. Then the only way left is along the other axis - a wider horizontal card
                // wraps onto fewer lines, a taller vertical card needs fewer columns - so ask for
                // the shortest run of text that fits in the room the card already has.
                //
                // Not the least-area card: at a large font that is the one-character-per-line
                // sliver, narrower than the card already is, so it grows nothing and the card
                // stops dead. See FlowFitFor.
                if (wanted.Height > MaximumSize.Height || wanted.Width > MaximumSize.Width)
                {
                    bool vertical = _card.VerticalText;
                    int across = vertical ? MaximumSize.Width : MaximumSize.Height;
                    wanted = WithFloorMeasure(delegate(CardSizeLimits.Measure measure)
                    {
                        return CardSizeLimits.FlowFitFor(measure, vertical, across);
                    });
                }
                Size next = new Size(
                    Math.Min(MaximumSize.Width, Math.Max(Width, wanted.Width)),
                    Math.Min(MaximumSize.Height, Math.Max(Height, wanted.Height)));
                if (next.Width == Width && next.Height == Height) break;
                Size = next;
            }
            UpdateCardSizeLimits();
            _card.CardWidth = Width;
            _card.CardHeight = Height;
            KeepOnScreen();
            _app.Save();
        }

        private void LayoutCard()
        {
            if (_quoteView == null) return;
            // The text view takes the card minus the padding, and the padding is the same number
            // the floor is computed with. Nothing else is reserved: the bottom strip that used to
            // hold the date label is gone with the label, which is what lets the card be dragged
            // down to its text instead of stopping 40 pixels above it.
            _quoteView.SetBounds(CardSizeLimits.TextPadLeft, CardSizeLimits.TextPadTop,
                Math.Max(10, ClientSize.Width - CardSizeLimits.HorizontalMargin),
                Math.Max(10, ClientSize.Height - CardSizeLimits.VerticalMargin));
            UpdateMenuButton();
        }

        // The "..." button keeps the bottom-right corner on any card tall enough to have one to
        // spare. It is pinned inside the card as well, so a card at its floor - which is now
        // shorter than the button - never shows it hanging off an edge.
        private void UpdateMenuButton()
        {
            if (_menuLabel == null) return;
            // SetBounds(Rectangle) only arrived in .NET 4.5; this program targets 4.0.
            Rectangle button = MenuButtonBounds;
            _menuLabel.SetBounds(button.Left, button.Top, button.Width, button.Height);
            // Hiding the button takes nothing away: the menu is on a right-click anywhere on the
            // card and on the tray icon. Leaving it up would put it on top of the text, which is
            // the one thing a card this small has room for. Both thresholds are about the button,
            // never about how small the card may be.
            _menuLabel.Visible = ClientSize.Height >= CardSizeLimits.MenuButtonMinCardHeight &&
                ClientSize.Width >= CardSizeLimits.MenuButtonMinCardWidth;
        }

        // Where the corner button sits, clamped so it is always inside the card.
        private Rectangle MenuButtonBounds
        {
            get
            {
                int width = Math.Min(CardSizeLimits.MenuButtonWidth, ClientSize.Width);
                int height = Math.Min(CardSizeLimits.MenuButtonHeight, ClientSize.Height);
                int left = Math.Max(0, ClientSize.Width - CardSizeLimits.MenuButtonRightInset);
                int top = Math.Max(0, ClientSize.Height - CardSizeLimits.MenuButtonBottomInset);
                return new Rectangle(left, top, width, height);
            }
        }

        private void FitQuoteFont()
        {
            if (_quoteView == null || _quoteView.Width <= 0 || _quoteView.Height <= 0) return;
            float chosen;
            if (!_card.AutoFitText && _card.ManualFontSize > 0f)
            {
                // A size picked by hand is the answer, not a suggestion: the card is not allowed to
                // shrink it back. This is what makes "a little larger" work on a card that is only
                // as big as its text, where the automatic fit has nothing left to give.
                chosen = Math.Max(QuoteView.SmallestFontSize,
                    Math.Min(CardState.MaxManualFontSize, _card.ManualFontSize));
            }
            else
            {
                float start = Math.Min(_card.AutoFitMaxFontSize,
                    Math.Max(AutoFitGuessFloor, Width / AutoFitGuessCharacters)) + _card.FontAdjustment;
                chosen = Math.Max(QuoteView.SmallestFontSize, start);
                if (_card.AutoFitText)
                {
                    // The search steps down from a fractional starting size, so it never lands exactly
                    // on the floor. The floor is therefore tried as its own candidate: without that, a
                    // card whose text only fits at the smallest font is left showing clipped text.
                    bool found = false;
                    for (float size = start; size > QuoteView.SmallestFontSize && !found; size -= 1.0f)
                    {
                        using (Font candidate = QuoteFont(size))
                        {
                            // The view measures horizontal and vertical text the same way it paints
                            // them, so a fitted font really does fit in either direction.
                            if (_quoteView.FitsBlock(candidate))
                            {
                                chosen = size;
                                found = true;
                            }
                        }
                    }
                    if (!found)
                    {
                        using (Font floorFont = QuoteFont(QuoteView.SmallestFontSize))
                            if (_quoteView.FitsBlock(floorFont)) chosen = QuoteView.SmallestFontSize;
                    }
                }
            }
            if (Math.Abs(chosen - _quoteFontSize) >= 0.1f)
            {
                Font replacement = QuoteFont(chosen);
                _quoteView.Font = replacement;
                if (_quoteFont != null) _quoteFont.Dispose();
                _quoteFont = replacement;
                _quoteFontSize = chosen;
            }
            // The interaction frame hugs the text, so every font or text change re-aligns it - and
            // the smallest size the card may be dragged to moves with the text it has to hold.
            UpdateCardSizeLimits();
            PositionTransparentHitBox();
        }

        // The card may be dragged to any size, but never smaller than the text it holds. The block
        // comes from the same code that paints the text, so the frame, the font search and this
        // floor can never disagree - vertical columns included.
        //
        // The floor is recomputed on every resize (OnResize calls this), because it depends on how
        // long the card is along the direction the text flows: a wider horizontal card needs fewer
        // lines, so its height floor drops. That is what lets a horizontal card be dragged down to
        // one line and a vertical card in to one column.
        private void UpdateCardSizeLimits()
        {
            if (_quoteView == null) return;
            Size floor = DragLimit();
            // The card can already be smaller than its text - a font just made larger, or a card
            // written small by hand. The floor then stands aside instead of pushing MinimumSize past
            // the size the card has, because MinimumSize's own setter resizes the card, and with
            // auto-fit off that would re-measure the font and move the floor again.
            if (floor.Width > Width || floor.Height > Height)
            {
                // With auto-fit on the font is never larger than the card can show, so a card below
                // its floor can only have come from a hand-edited file: grow it to hold the text.
                if (!_card.AutoFitText) return;
                MinimumSize = ClampToCeiling(floor);
                return;
            }
            if (MinimumSize.Width == floor.Width && MinimumSize.Height == floor.Height) return;
            MinimumSize = ClampToCeiling(floor);
        }

        // A floor above the ceiling would leave MinimumSize and MaximumSize contradicting each
        // other, and the ceiling is the desktop anyway - a card bigger than that cannot be shown.
        private Size ClampToCeiling(Size floor)
        {
            return new Size(Math.Min(floor.Width, MaximumSize.Width),
                Math.Min(floor.Height, MaximumSize.Height));
        }

        // The floor the card is held to while it is being dragged: the smallest card that still
        // shows the whole text *at the shape the card has now*. Only the axis across the text's
        // flow is constrained - the height for horizontal text, the width for vertical - so the
        // card is free to be wide and short or narrow and tall. See DragLimitFor.
        //
        // This reads the size the card has now, which is safe here for the one reason that matters:
        // what it reads is the axis the text flows along, never the axis it constrains. Dragging
        // the card shorter cannot move the height floor, because the height floor never looks at
        // the height. The old floor did read the size the card had, and that is what let the card
        // shrink for ever: a smaller card moved the floor down with it.
        private Size DragLimit()
        {
            bool vertical = _card.VerticalText;
            int flowAxis = vertical ? Height : Width;
            return WithFloorMeasure(delegate(CardSizeLimits.Measure measure)
            {
                return CardSizeLimits.DragLimitFor(measure, vertical, flowAxis);
            });
        }

        // The least-area card for the text, which is a single point rather than the drag floor.
        // "Fit to text" snaps to it, and a font made larger grows the card to it. It is the compact
        // shape, so it is also the fallback when the text cannot be made to fit along one axis.
        private Size SmallestCard()
        {
            bool vertical = _card.VerticalText;
            return WithFloorMeasure(delegate(CardSizeLimits.Measure measure)
            {
                return CardSizeLimits.MinimumFor(measure, vertical);
            });
        }

        // Which font the floor is measured at. Neither the size the card has now nor the font it is
        // showing enters into it with auto-fit on: the search there bottoms out on the smallest font
        // it will ever choose, so that is the font the floor is measured at. Measuring at the font
        // in use would make the floor shrink along with the card - a smaller card, a smaller font, a
        // lower floor - and let the card shrink for ever. With auto-fit off the font is the user's
        // own choice and does not shrink with the card, so the font in use is the reference - and
        // because that size is absolute it stays put while the card grows to meet it.
        private Size WithFloorMeasure(Func<CardSizeLimits.Measure, Size> pick)
        {
            if (!_card.AutoFitText)
                return pick(_quoteView.MeasureBlock);
            using (Font reference = QuoteFont(QuoteView.SmallestFontSize))
            {
                QuoteView view = _quoteView;
                Font bound = reference;
                return pick(delegate(int width, int height)
                {
                    return view.MeasureBlock(bound, width, height);
                });
            }
        }

        private void RestoreLocation()
        {
            if (_card.X != int.MinValue && _card.Y != int.MinValue)
                Location = new Point(_card.X, _card.Y);
            else
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(area.Right - Width - 48, area.Top + 72);
            }
            KeepOnScreen();
        }

        private void KeepOnScreen()
        {
            Rectangle card = new Rectangle(Location, Size);
            Screen best = Screen.FromRectangle(card);
            Rectangle area = best.WorkingArea;
            int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            Location = new Point(x, y);
        }

        private void AttachDragHandlers(Control control)
        {
            control.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    _dragging = true;
                    _dragOrigin = Cursor.Position;
                    _dragFree = Location;
                    _snapTargets = NeighbourBounds();
                }
            };
            control.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!_dragging || e.Button != MouseButtons.Left) return;
                Point current = Cursor.Position;
                // The free position follows the pointer exactly; the visible one is that position
                // pulled onto a neighbour's line when it is close enough. Keeping them apart is what
                // lets the card come unstuck again as soon as the pointer moves away.
                _dragFree.Offset(current.X - _dragOrigin.X, current.Y - _dragOrigin.Y);
                _dragOrigin = current;
                Point snapped = CardSnap.Adjust(_dragFree, Size, _snapTargets,
                    Screen.FromControl(this).WorkingArea, CardSnap.Distance);
                Left = snapped.X;
                Top = snapped.Y;
            };
            control.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (!_dragging) return;
                _dragging = false;
                KeepOnScreen();
                _card.X = Left;
                _card.Y = Top;
                _app.Save();
                PositionTransparentHitBox();
            };
        }

        // Bounds of every other visible card. The screen is added as a target by CardSnap, so a card
        // can be lined up with the edge of the desktop as well as with its siblings.
        private Rectangle[] NeighbourBounds()
        {
            if (Manager == null) return new Rectangle[0];
            List<QuoteCardForm> forms = Manager.Forms;
            List<Rectangle> bounds = new List<Rectangle>();
            for (int index = 0; index < forms.Count; index++)
            {
                QuoteCardForm other = forms[index];
                if (other == this || other.IsDisposed || !other.Visible) continue;
                bounds.Add(other.Bounds);
            }
            return bounds.ToArray();
        }

        private void UpdateRoundedRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            // CreateRoundRectRgn takes the size of the corner *ellipse*, not its radius, and it
            // takes the whole rect rather than one that is one pixel short - so the diameter is
            // twice the radius and the bottom right corner needs the +1.
            //
            // This used to pass a fixed 19, which is a radius of 9.5 against a border painted at
            // 18: the region was much squarer than the outline, so the corners showed a crescent of
            // background outside the border. Both now come from CornerRadiusFor.
            int radius = CardSizeLimits.CornerRadiusFor(_card.CornerRadius, Width, Height);
            IntPtr regionHandle = radius <= 0
                ? NativeMethods.CreateRectRgn(0, 0, Width + 1, Height + 1)
                : NativeMethods.CreateRoundRectRgn(0, 0, Width + 1, Height + 1,
                    radius * 2, radius * 2);
            Region old = Region;
            Region = Region.FromHrgn(regionHandle);
            NativeMethods.DeleteObject(regionHandle);
            if (old != null) old.Dispose();
        }

        // Internal so the self test can drive it: the two guards below are exactly the cases a
        // card can reach from the menu, and neither is reachable from a test of the arithmetic.
        internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            // Two guards the caller should not have to repeat.
            //
            // Radius 0 means a square corner, and four zero sized arcs are not a reliable way to
            // say that - the path degenerates into four coincident points.
            //
            // And the diameter has to fit inside the rectangle. CornerRadiusFor caps the radius by
            // the window size, but this rect is a pixel short on each axis and is inset again for a
            // thick border: on a 38 pixel card at the largest radius the two top arcs would overlap
            // and the path folds over itself, drawing a bowtie at the corners.
            int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private Font QuoteFont(float size)
        {
            try
            {
                return new Font(_card.FontName, size, (FontStyle)_card.FontStyleValue, GraphicsUnit.Point);
            }
            catch
            {
                return UiFont(size, FontStyle.Regular);
            }
        }

        private static Color SafeColor(int argb, Color fallback)
        {
            try
            {
                Color color = Color.FromArgb(argb);
                return color.A == 0 ? fallback : color;
            }
            catch
            {
                return fallback;
            }
        }

        // The largest centred rectangle that fits inside the card at the picture's own aspect
        // ratio - the counterpart of CoverRectangle, which is the smallest one that covers it.
        private static Rectangle FitRectangle(Rectangle bounds, Size imageSize)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || imageSize.Width <= 0 || imageSize.Height <= 0)
                return bounds;
            double scale = Math.Min((double)bounds.Width / imageSize.Width,
                (double)bounds.Height / imageSize.Height);
            int width = Math.Max(1, (int)Math.Ceiling(imageSize.Width * scale));
            int height = Math.Max(1, (int)Math.Ceiling(imageSize.Height * scale));
            return new Rectangle(bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2, width, height);
        }

        private static Rectangle CoverRectangle(Rectangle bounds, Size imageSize)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || imageSize.Width <= 0 || imageSize.Height <= 0)
                return bounds;
            double scale = Math.Max((double)bounds.Width / imageSize.Width,
                (double)bounds.Height / imageSize.Height);
            int width = Math.Max(1, (int)Math.Ceiling(imageSize.Width * scale));
            int height = Math.Max(1, (int)Math.Ceiling(imageSize.Height * scale));
            return new Rectangle(bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2, width, height);
        }

        internal static Font UiFont(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
        }

        // The tray icon has to be the same picture as the window icon. This used to hand-draw a
        // dark disc with a white quote mark on it - the design from before card.ico was redrawn -
        // so when the program icon changed, the window and the right-click menu followed and the
        // tray did not. Three sources are tried in order, and the drawn one is only a last resort
        // for a build with no icon resource at all.
        //
        // Internal so the self-test can run it: ExtractIconEx is P/Invoke, and this is the only
        // place the extraction is ever exercised.
        internal static Icon CreateTrayIcon()
        {
            string path = null;
            try { path = Application.ExecutablePath; }
            catch { path = null; }
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                // The notification area is 16x16, so the small image is the right one to ask for;
                // the large one is kept only because it is better than nothing.
                IntPtr[] large = new IntPtr[1];
                IntPtr[] small = new IntPtr[1];
                try
                {
                    if (NativeMethods.ExtractIconEx(path, 0, large, small, 1) > 0)
                    {
                        IntPtr handle = small[0] != IntPtr.Zero ? small[0] : large[0];
                        if (handle != IntPtr.Zero)
                        {
                            try
                            {
                                using (Icon temporary = Icon.FromHandle(handle))
                                    return (Icon)temporary.Clone();
                            }
                            finally
                            {
                                NativeMethods.DestroyIcon(handle);
                            }
                        }
                    }
                }
                finally
                {
                    if (large[0] != IntPtr.Zero && large[0] != small[0])
                        NativeMethods.DestroyIcon(large[0]);
                }
                // The resource is there but could not be pulled apart: take whatever the shell
                // reads for this file.
                try
                {
                    Icon associated = Icon.ExtractAssociatedIcon(path);
                    if (associated != null) return associated;
                }
                catch { }
            }
            return DrawnTrayIcon();
        }

        // Only reached when the executable carries no icon at all. Kept because a build without
        // /win32icon is still a build that has to show something in the tray.
        private static Icon DrawnTrayIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (Brush background = new SolidBrush(Color.FromArgb(8, 8, 8)))
                    graphics.FillEllipse(background, 1, 1, 30, 30);
                using (Font font = new Font("Georgia", 20, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush foreground = new SolidBrush(Color.White))
                    graphics.DrawString("“", font, foreground, new PointF(8, 6));
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                        return (Icon)temporary.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
        }
    }

    // Pure layout arithmetic for traditional Chinese vertical typography. Kept separate from the
    // control so it can be reasoned about - and asserted on - without a window.
    internal static class VerticalTextLayout
    {
        public const string Ellipsis = "…";

        // How many characters fit into one column of the given height.
        public static int CharactersPerColumn(int availableHeight, int cellHeight)
        {
            if (cellHeight <= 0) return 1;
            int count = availableHeight / cellHeight;
            return count < 1 ? 1 : count;
        }

        // Splits text into columns of at most perColumn characters. Columns come back in reading
        // order, which means the first column is the right-most one on screen. An explicit line
        // break always starts a new column, so authored line breaks survive in vertical mode.
        public static List<string> SplitColumns(string text, int perColumn)
        {
            List<string> columns = new List<string>();
            if (string.IsNullOrEmpty(text)) return columns;
            if (perColumn < 1) perColumn = 1;
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] segments = normalized.Split('\n');
            StringBuilder current = new StringBuilder();
            for (int index = 0; index < segments.Length; index++)
            {
                if (index > 0)
                {
                    if (current.Length > 0) columns.Add(current.ToString());
                    current.Length = 0;
                }
                string segment = segments[index];
                for (int position = 0; position < segment.Length; position++)
                {
                    current.Append(segment[position]);
                    if (current.Length >= perColumn)
                    {
                        columns.Add(current.ToString());
                        current.Length = 0;
                    }
                }
            }
            if (current.Length > 0) columns.Add(current.ToString());
            return columns;
        }

        // Keeps the first maxColumns columns and marks the cut with an ellipsis, the way a single
        // line of horizontal text would be shortened.
        public static List<string> Truncate(List<string> columns, int maxColumns)
        {
            List<string> result = new List<string>();
            if (columns == null || columns.Count == 0 || maxColumns <= 0) return result;
            int keep = Math.Min(maxColumns, columns.Count);
            for (int index = 0; index < keep; index++) result.Add(columns[index]);
            if (keep >= columns.Count) return result;
            int last = keep - 1;
            string tail = result[last];
            result[last] = tail.Length > 0 ? tail.Substring(0, tail.Length - 1) + Ellipsis : Ellipsis;
            return result;
        }
    }

    // Where horizontal lines break. One implementation for both the measurement and the painting:
    // GDI's own word wrap cannot be asked for line spacing or letter spacing, so taking over the
    // wrapping is the price of those two - and having exactly one breaker is what keeps "what is
    // measured" and "what is painted" the same thing. That invariant is what this view lives or
    // dies by: a second wrapping rule would put the text and the frame that hugs it on different
    // layouts, which is exactly the class of bug the vertical layout had to climb out of.
    internal static class TextLines
    {
        public const string Ellipsis = "…";

        // Chinese line-breaking rules. A line may not begin with a closing mark, and may not end
        // with an opening one - either way the mark is left stranded away from the text it belongs
        // to. The break point moves back instead, which is what CSS's `line-break: strict` does:
        // the character before the mark goes down with it, so the line above ends one character
        // short rather than the line below starting with a comma.
        //
        // Hanging the mark past the edge would look better still, but the block would then be
        // wider than the text area and the card's own floor is measured from the block - so the
        // safe rule is the one that never overflows.
        private const string CannotStartLine = "，。、；：？！）」』】〉》”’…%,.;:?!)]}";
        private const string CannotEndLine = "（「『【〈《“‘([{";

        public static bool StartsLineIllegally(char character)
        {
            return CannotStartLine.IndexOf(character) >= 0;
        }

        public static bool EndsLineIllegally(char character)
        {
            return CannotEndLine.IndexOf(character) >= 0;
        }

        // Fills each line as far as it goes. `advance` returns the width of one character with
        // any letter spacing already included. A break is preferred at a space so an English
        // word is not cut in half; CJK has no spaces and is meant to break between any two
        // characters. An explicit newline always ends a line, so authored breaks survive.
        public static List<string> Split(string text, int available, Func<char, int> advance)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(text) || advance == null) return lines;
            if (available < 1) available = 1;
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            int index = 0;
            while (index < normalized.Length)
            {
                int used = 0;
                int end = index;
                int spaceAt = -1;
                while (end < normalized.Length && normalized[end] != '\n')
                {
                    int width = advance(normalized[end]);
                    if (used + width > available && end > index) break;
                    used += width;
                    end++;
                    if (normalized[end - 1] == ' ') spaceAt = end - 1;
                }
                if (end < normalized.Length && normalized[end] != '\n' && spaceAt > index)
                    end = spaceAt;
                // Line-breaking rules come after the space rule: they are not negotiable, and a
                // line never shrinks to nothing because of them.
                while (end > index + 1)
                {
                    bool badStart = end < normalized.Length && normalized[end] != '\n' &&
                        StartsLineIllegally(normalized[end]);
                    if (!badStart && !EndsLineIllegally(normalized[end - 1])) break;
                    end--;
                }
                // A space left at the end after backing up is not part of the line.
                while (end > index && normalized[end - 1] == ' ') end--;
                lines.Add(normalized.Substring(index, end - index));
                index = end;
                // A newline ends the line it sits on; two in a row leave an empty line between
                // them. Spaces at a break are dropped - they were doing the breaking.
                if (index < normalized.Length && normalized[index] == '\n') index++;
                else while (index < normalized.Length && normalized[index] == ' ') index++;
            }
            return lines;

        }

        // Shortens a line until it and an ellipsis fit, for the case where the text area is
        // shorter than the text - the same deal the vertical path makes.
        public static string Shorten(string line, int available, Func<char, int> advance)
        {
            if (string.IsNullOrEmpty(line) || advance == null) return line ?? "";
            int width = 0;
            int keep = 0;
            while (keep < line.Length)
            {
                int next = width + advance(line[keep]);
                if (next > available) break;
                width = next;
                keep++;
            }
            if (keep == line.Length) return line;
            int ellipsis = 0;
            for (int index = 0; index < Ellipsis.Length; index++) ellipsis += advance(Ellipsis[index]);
            int target = Math.Max(0, available - ellipsis);
            width = 0;
            keep = 0;
            while (keep < line.Length)
            {
                int next = width + advance(line[keep]);
                if (next > target) break;
                width = next;
                keep++;
            }
            return line.Substring(0, keep) + Ellipsis;
        }
    }

    // Draws the quote. Horizontal text wraps, centres and ellipsises like the Label it replaced;
    // vertical text is laid out as traditional Chinese typography - characters upright, running
    // top to bottom, columns progressing right to left. Measuring and painting live together here
    // because the font fitting and the invisible interaction frame both need the same numbers.
    internal sealed class QuoteView : Control
    {
        private const int TextMargin = 2;
        // Bounded proposals keep GDI away from absurd rectangles; a single glyph and a wrapped
        // quote both fit comfortably inside these.
        private const int MaxMeasure = 4096;
        // The smallest font the fit search will settle on. The card's own floor is measured at this
        // size, so it is also the point past which a card can never be squeezed - and it is the
        // same number a hand-picked size is held to, so the two can never drift apart.
        public const float SmallestFontSize = CardState.MinManualFontSize;
        private bool _vertical;
        // Where the laid-out block sits in the control. Alignment is applied while painting only -
        // measuring deliberately ignores it, so moving the text never changes the card's own
        // minimum size or the font the fit search settles on.
        private StringAlignment _horizontalAlignment = StringAlignment.Center;
        private StringAlignment _verticalAlignment = StringAlignment.Center;
        private int _lineSpacingPercent = CardState.DefaultLineSpacingPercent;
        private int _letterSpacingPercent = CardState.DefaultLetterSpacingPercent;
        private bool _justify;
        private int _outlinePercent = CardState.DefaultTextOutlinePercent;
        private Color _outlineColor = Color.White;
        private int _shadowPercent = CardState.DefaultTextShadowPercent;
        private Color _shadowColor = Color.Black;
        // One glyph is drawn the same way in both directions, so the flags live here rather than
        // being repeated per call site.
        private const TextFormatFlags GlyphFlags = TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        // Measured once per font size and reused. Two loops lean on this: the drag floor asks for
        // the block at about fifty different *widths* at one size, and the fit search asks at about
        // ten different *sizes* at one width. Measuring every character again for each of them
        // would make dragging a card crawl, and the table is what lets the line breaker be pure
        // arithmetic.
        //
        // Keyed by size, not by the Font object. The fit search makes a new Font for every candidate
        // size, so keying on the object cleared the whole table on every step - exactly the loop the
        // table exists to speed up. The family and style are still tracked, because those change
        // every glyph and the table has to go.
        private sealed class GlyphMetrics
        {
            public readonly Dictionary<char, int> Advances = new Dictionary<char, int>();
            public int LineHeight;
        }

        private const int MaxCachedFontSizes = 128;
        private readonly Dictionary<float, GlyphMetrics> _metricsBySize =
            new Dictionary<float, GlyphMetrics>();
        private string _metricsFamily;
        private FontStyle _metricsStyle;
        // How many single characters have been measured with GDI. Only the self test reads it; it
        // is the only way to see from outside that a second pass over the same sizes hit the cache.
        private int _glyphMeasures;

        public QuoteView()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent;
        }

        // Spacing, as percentages of what the font asks for. Changing either one changes the block
        // the text occupies, so the caller has to re-measure the card afterwards - which is what
        // QuoteCardForm's setters do.
        public int LineSpacingPercent
        {
            get { return _lineSpacingPercent; }
            set
            {
                int clamped = Math.Max(CardState.MinLineSpacingPercent,
                    Math.Min(CardState.MaxLineSpacingPercent, value));
                if (_lineSpacingPercent == clamped) return;
                _lineSpacingPercent = clamped;
                Invalidate();
            }
        }

        public int LetterSpacingPercent
        {
            get { return _letterSpacingPercent; }
            set
            {
                int clamped = Math.Max(CardState.MinLetterSpacingPercent,
                    Math.Min(CardState.MaxLetterSpacingPercent, value));
                if (_letterSpacingPercent == clamped) return;
                _letterSpacingPercent = clamped;
                Invalidate();
            }
        }

        // Thickness of the halo around every glyph, as a percentage of the line height. Zero is
        // none. A percentage rather than pixels for the same reason the spacing is one: the font
        // size follows the card.
        public int TextOutlinePercent
        {
            get { return _outlinePercent; }
            set
            {
                int clamped = Math.Max(0, Math.Min(CardState.MaxTextOutlinePercent, value));
                if (_outlinePercent == clamped) return;
                _outlinePercent = clamped;
                Invalidate();
            }
        }

        // Thickness of the drop shadow, as a percentage of the line height. Zero is none.
        public int TextShadowPercent
        {
            get { return _shadowPercent; }
            set
            {
                int clamped = Math.Max(0, Math.Min(CardState.MaxTextShadowPercent, value));
                if (_shadowPercent == clamped) return;
                _shadowPercent = clamped;
                Invalidate();
            }
        }

        // GDI has no translucent text, so the shadow is an opaque colour between the text and what
        // is behind it. The form works it out from the card and hands it over.
        public Color ShadowColor
        {
            get { return _shadowColor; }
            set
            {
                if (_shadowColor == value) return;
                _shadowColor = value;
                Invalidate();
            }
        }

        // The colour the halo is drawn in. Set by the form from the card: the background colour on
        // a solid card, white or black on a transparent one.
        public Color OutlineColor
        {
            get { return _outlineColor; }
            set
            {
                if (_outlineColor == value) return;
                _outlineColor = value;
                Invalidate();
            }
        }

        // Every line but the last is stretched to fill the text area. Horizontal only.
        public bool Justify
        {
            get { return _justify; }
            set
            {
                if (_justify == value) return;
                _justify = value;
                Invalidate();
            }
        }

        public bool Vertical
        {
            get { return _vertical; }
            set
            {
                if (_vertical == value) return;
                _vertical = value;
                Invalidate();
            }
        }

        public StringAlignment HorizontalAlignment
        {
            get { return _horizontalAlignment; }
            set
            {
                if (_horizontalAlignment == value) return;
                _horizontalAlignment = value;
                Invalidate();
            }
        }

        public StringAlignment VerticalAlignment
        {
            get { return _verticalAlignment; }
            set
            {
                if (_verticalAlignment == value) return;
                _verticalAlignment = value;
                Invalidate();
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        // The block the text covers, in pixels, for the font that is currently assigned.
        public Size MeasureBlock()
        {
            return MeasureBlock(Font);
        }

        // The natural size of the laid out text, before any ellipsis trimming. Returning the
        // natural size keeps the interaction frame honest when text overflows, and lets the font
        // search reject a size that is too large instead of silently trimming it.
        public Size MeasureBlock(Font font)
        {
            return MeasureBlock(font, Width, Height);
        }

        // The same block, but for a text area of an arbitrary size. The card's smallest allowed
        // size is searched by asking what the text would do at sizes the card does not have yet,
        // which is why the size is a parameter instead of this control's own bounds.
        public Size MeasureBlock(int viewWidth, int viewHeight)
        {
            return MeasureBlock(Font, viewWidth, viewHeight);
        }

        public Size MeasureBlock(Font font, int viewWidth, int viewHeight)
        {
            if (font == null || string.IsNullOrEmpty(Text)) return Size.Empty;
            if (!_vertical) return MeasureHorizontal(font, viewWidth);

            int cellWidth;
            int cellHeight;
            MeasureCell(font, out cellWidth, out cellHeight);
            List<string> columns = BuildColumns(font, viewWidth, viewHeight, cellHeight);
            if (columns.Count == 0) return Size.Empty;
            return new Size(columns.Count * cellWidth, WidestColumn(columns) * cellHeight);
        }

        public bool FitsBlock(Font font)
        {
            Size block = MeasureBlock(font);
            if (block.Width <= 0 || block.Height <= 0) return true;
            return block.Width <= Math.Max(1, Width - TextMargin * 2) &&
                block.Height <= Math.Max(1, Height - TextMargin * 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (string.IsNullOrEmpty(Text) || Font == null) return;
            if (_vertical) PaintVertical(e.Graphics);
            else PaintHorizontal(e.Graphics);
        }

        // ---- Measuring and wrapping ------------------------------------------------------------
        //
        // GDI's own word wrap cannot be asked for line spacing or letter spacing, so the wrapping
        // is done here instead. What matters is that exactly one place does it: MeasureBlock and
        // OnPaint both go through BuildLines, so "what is measured" and "what is painted" cannot
        // drift apart - which is the invariant the font fitting and the interaction frame rely on.

        // One line's natural height, measured once per font. GDI's line height rather than
        // Font.Height: the card's floor was built on the GDI number, and swapping it would move
        // every card that already exists.
        private int NaturalLineHeight(Font font)
        {
            return MetricsFor(font).LineHeight;
        }

        // The bucket for one font size, created on first use. The whole table is dropped when the
        // family or the style changes, because every advance in it belongs to the old typeface.
        private GlyphMetrics MetricsFor(Font font)
        {
            string family = font.FontFamily.Name;
            FontStyle style = font.Style;
            if (_metricsFamily != family || _metricsStyle != style)
            {
                _metricsFamily = family;
                _metricsStyle = style;
                _metricsBySize.Clear();
            }
            // Rounded so that the same size reached by two different routes - a whole step of the
            // search and a fractional starting point - lands in the same bucket.
            float key = (float)Math.Round(font.SizeInPoints, 2);
            GlyphMetrics metrics;
            if (_metricsBySize.TryGetValue(key, out metrics)) return metrics;
            // A card can be resized through a lot of sizes in one session. The cap is generous
            // enough that a normal drag never reaches it, and dropping everything is cheap.
            if (_metricsBySize.Count >= MaxCachedFontSizes) _metricsBySize.Clear();
            metrics = new GlyphMetrics();
            metrics.LineHeight = Math.Max(1, TextRenderer.MeasureText("字", font,
                new Size(MaxMeasure, MaxMeasure),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height);
            _metricsBySize[key] = metrics;
            return metrics;
        }

        // How many single characters this view has measured with GDI. The self test reads it to
        // prove that a second pass over the same font sizes is answered from the table.
        internal int GlyphMeasures
        {
            get { return _glyphMeasures; }
        }

        // Width of one character, measured once per size and reused.
        private int Advance(char character, Font font)
        {
            GlyphMetrics metrics = MetricsFor(font);
            int width;
            if (metrics.Advances.TryGetValue(character, out width)) return width;
            width = TextRenderer.MeasureText(character.ToString(), font,
                new Size(MaxMeasure, MaxMeasure),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            _glyphMeasures++;
            if (width <= 0) width = Math.Max(1, metrics.LineHeight / 3);
            metrics.Advances[character] = width;
            return width;
        }

        // Extra room after every character, in pixels. Derived from the line height so it scales
        // with the font: a percentage of a font size means nothing as a pixel count.
        private int LetterGap(Font font)
        {
            if (_letterSpacingPercent <= 0) return 0;
            int natural = NaturalLineHeight(font);
            return (int)Math.Round(natural * _letterSpacingPercent / 100.0);
        }

        // Extra room between lines, in pixels. Can be negative; 100% is zero.
        private int LineGap(Font font)
        {
            if (_lineSpacingPercent == CardState.DefaultLineSpacingPercent) return 0;
            int natural = NaturalLineHeight(font);
            return (int)Math.Round(natural * (_lineSpacingPercent - 100) / 100.0);
        }

        // How thick the halo is, in pixels. Zero when the card has none.
        private int OutlinePixels(Font font)
        {
            if (_outlinePercent <= 0) return 0;
            int natural = NaturalLineHeight(font);
            return Math.Max(1, (int)Math.Round(natural * _outlinePercent / 100.0));
        }

        // How far the shadow is offset, in pixels. Zero when the card has none.
        private int ShadowPixels(Font font)
        {
            if (_shadowPercent <= 0) return 0;
            int natural = NaturalLineHeight(font);
            return Math.Max(1, (int)Math.Round(natural * _shadowPercent / 100.0));
        }

        // Room the decorations take on every side of the glyph run. The halo grows outwards in all
        // directions and the shadow grows down-right, so the block is padded by the larger of the
        // two on each side - symmetric, which keeps the alignment arithmetic the same.
        private int BlockPadding(Font font)
        {
            return 2 * (OutlinePixels(font) + ShadowPixels(font));
        }

        // Width the glyphs themselves may use: the text area minus that padding on both sides.
        private int TextAreaWidth(int viewWidth, Font font)
        {
            return Math.Max(1, viewWidth - TextMargin * 2 - BlockPadding(font));
        }

        private List<string> BuildLines(Font font, int viewWidth)
        {
            int available = TextAreaWidth(viewWidth, font);
            int gap = LetterGap(font);
            return TextLines.Split(Text, available,
                delegate(char character) { return Advance(character, font) + gap; });
        }

        private int LineWidth(string line, Font font)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            int gap = LetterGap(font);
            int width = 0;
            for (int index = 0; index < line.Length; index++)
                width += Advance(line[index], font) + gap;
            // The gap after the last character is not part of the block.
            return Math.Max(0, width - gap);
        }

        private Size MeasureHorizontal(Font font, int viewWidth)
        {
            List<string> lines = BuildLines(font, viewWidth);
            if (lines.Count == 0) return Size.Empty;
            int padding = BlockPadding(font);
            int available = TextAreaWidth(viewWidth, font);
            int widest = 0;
            for (int index = 0; index < lines.Count; index++)
                widest = Math.Max(widest, LineWidth(lines[index], font));
            int natural = NaturalLineHeight(font);
            int height = lines.Count * natural + (lines.Count - 1) * LineGap(font);
            // The halo sits outside the glyphs, so it is part of the block the card has to hold.
            return new Size(Math.Min(widest, available) + padding,
                Math.Max(natural, height) + padding);
        }

        // Draws one glyph, with the halo if the card asks for one. GDI has no text outline, and
        // drawing the text as a GDI+ path instead would render it differently from the un-haloed
        // case - so the halo is the same glyph drawn around itself, eight directions at every
        // distance up to the stroke width.
        private void DrawGlyph(Graphics graphics, string glyph, int x, int y, int stroke)
        {
            int shadow = ShadowPixels(Font);
            if (shadow > 0)
                TextRenderer.DrawText(graphics, glyph, Font, new Point(x + shadow, y + shadow),
                    _shadowColor, GlyphFlags);
            if (stroke > 0)
            {
                Color halo = _outlineColor;
                for (int distance = 1; distance <= stroke; distance++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            TextRenderer.DrawText(graphics, glyph, Font,
                                new Point(x + dx * distance, y + dy * distance), halo, GlyphFlags);
                        }
                    }
                }
            }
            TextRenderer.DrawText(graphics, glyph, Font, new Point(x, y), ForeColor, GlyphFlags);
        }

        private void PaintHorizontal(Graphics graphics)
        {
            List<string> lines = BuildLines(Font, Width);
            if (lines.Count == 0) return;
            int stroke = OutlinePixels(Font);
            int available = TextAreaWidth(Width, Font);
            int availableHeight = Math.Max(1, Height - TextMargin * 2 - BlockPadding(Font));
            int natural = NaturalLineHeight(Font);
            int gap = LineGap(Font);
            int step = Math.Max(1, natural + gap);
            int visible = lines.Count;
            int blockHeight = visible * natural + (visible - 1) * gap;
            if (blockHeight > availableHeight)
            {
                // Drop the lines that do not fit and mark the cut, the same deal the vertical path
                // makes. Only reachable with automatic fitting off - the fit never picks a size
                // that overflows.
                visible = Math.Max(1, (availableHeight + gap) / step);
                if (visible > lines.Count) visible = lines.Count;
                blockHeight = visible * natural + (visible - 1) * gap;
            }
            int originY = TextMargin + stroke +
                AlignOffset(availableHeight - blockHeight, _verticalAlignment);
            int letterGap = LetterGap(Font);
            for (int index = 0; index < visible; index++)
            {
                string line = lines[index];
                if (index == visible - 1 && visible < lines.Count)
                    line = TextLines.Shorten(line, available,
                        delegate(char character) { return Advance(character, Font) + letterGap; });
                int width = LineWidth(line, Font);
                int x = TextMargin + stroke + AlignOffset(available - width, _horizontalAlignment);
                int y = originY + index * step;
                // Justification stretches the gaps between characters to fill the text area. The
                // last line is left alone - stretching it would pull a short closing line across
                // the whole card, which is the one thing justified text is never supposed to do.
                // The block's own width is still the natural one, so the card's floor does not
                // move when this is turned on.
                int gaps = 0;
                int gapShare = 0;
                int gapRemainder = 0;
                if (_justify && index < visible - 1 && line.Length > 1)
                {
                    gaps = line.Length - 1;
                    int spare = Math.Max(0, available - width);
                    gapShare = spare / gaps;
                    gapRemainder = spare % gaps;
                }
                // One glyph at a time. GDI draws a string at its natural advances and offers no way
                // to add to them, so letter spacing only exists if the glyphs are placed here. It
                // also makes the painted width exactly what LineWidth reports - which is what puts
                // right-aligned and centred text where the arithmetic says they go. Drawing the
                // whole line in one call made right-aligned text drift left by the accumulated
                // letter spacing.
                for (int position = 0; position < line.Length; position++)
                {
                    char character = line[position];
                    if (character != ' ' && !char.IsControl(character))
                        DrawGlyph(graphics, character.ToString(), x, y, stroke);
                    x += Advance(character, Font) + letterGap;
                    if (position < gaps)
                        x += gapShare + (position < gapRemainder ? 1 : 0);
                }
            }
        }

        // The vertical path places every character in a cell, so the halo is the same glyph drawn
        // around the centred position rather than around a top-left point.
        private void DrawGlyphInCell(Graphics graphics, string glyph, Rectangle cell)
        {
            int shadow = ShadowPixels(Font);
            if (shadow > 0)
            {
                Rectangle shiftedShadow = new Rectangle(cell.Left + shadow, cell.Top + shadow,
                    cell.Width, cell.Height);
                TextRenderer.DrawText(graphics, glyph, Font, shiftedShadow, _shadowColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | GlyphFlags);
            }
            int stroke = OutlinePixels(Font);
            if (stroke > 0)
            {
                Color halo = _outlineColor;
                for (int distance = 1; distance <= stroke; distance++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            Rectangle shifted = new Rectangle(cell.Left + dx * distance,
                                cell.Top + dy * distance, cell.Width, cell.Height);
                            TextRenderer.DrawText(graphics, glyph, Font, shifted, halo,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                GlyphFlags);
                        }
                    }
                }
            }
            TextRenderer.DrawText(graphics, glyph, Font, cell, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | GlyphFlags);
        }

        private void PaintVertical(Graphics graphics)
        {
            int cellWidth;
            int cellHeight;
            MeasureCell(Font, out cellWidth, out cellHeight);
            List<string> columns = BuildColumns(Font, Width, Height, cellHeight);
            if (columns.Count == 0) return;

            int blockWidth = columns.Count * cellWidth;
            int blockHeight = WidestColumn(columns) * cellHeight;
            // The block is placed inside the same inset the horizontal path paints into, so the two
            // directions agree about what "left" and "top" mean.
            int availableWidth = Math.Max(1, Width - TextMargin * 2);
            int availableHeight = Math.Max(1, Height - TextMargin * 2);
            int originX = TextMargin + AlignOffset(availableWidth - blockWidth, _horizontalAlignment);
            int originY = TextMargin + AlignOffset(availableHeight - blockHeight, _verticalAlignment);
            for (int index = 0; index < columns.Count; index++)
            {
                // Reading order runs right to left, so the first column sits furthest right.
                int columnLeft = originX + (columns.Count - 1 - index) * cellWidth;
                string column = columns[index];
                for (int row = 0; row < column.Length; row++)
                {
                    Rectangle cell = new Rectangle(columnLeft, originY + row * cellHeight,
                        cellWidth, cellHeight);
                    // The cell already carries room for the halo on both sides, so the glyph is
                    // centred in it and the halo is drawn around that centre.
                    DrawGlyphInCell(graphics, column[row].ToString(), cell);
                }
            }
        }

        // Leftover room on one axis turned into an offset. A block larger than the room it has is
        // pinned to the start rather than pushed off the far edge, where it would be invisible.
        private static int AlignOffset(int free, StringAlignment alignment)
        {
            if (free <= 0) return 0;
            if (alignment == StringAlignment.Far) return free;
            if (alignment == StringAlignment.Center) return free / 2;
            return 0;
        }

        // Columns that fit the text area, with the overflow marked by an ellipsis.
        private List<string> BuildColumns(Font font, int viewWidth, int viewHeight, int cellHeight)
        {
            int availableHeight = Math.Max(cellHeight, viewHeight - TextMargin * 2);
            int perColumn = VerticalTextLayout.CharactersPerColumn(availableHeight, cellHeight);
            List<string> columns = VerticalTextLayout.SplitColumns(Text, perColumn);
            if (columns.Count == 0) return columns;
            int cellWidth;
            int unusedHeight;
            MeasureCell(font, out cellWidth, out unusedHeight);
            int maxColumns = Math.Max(1, Math.Max(1, viewWidth - TextMargin * 2) / Math.Max(1, cellWidth));
            return VerticalTextLayout.Truncate(columns, maxColumns);
        }

        private static int WidestColumn(List<string> columns)
        {
            int widest = 0;
            for (int index = 0; index < columns.Count; index++)
                if (columns[index].Length > widest) widest = columns[index].Length;
            return widest;
        }

        // One character cell. The tallest and widest glyph in the text sets the grid, and a CJK
        // sample keeps Latin-only text from collapsing into a much tighter grid than the Chinese
        // text it sits next to.
        private void MeasureCell(Font font, out int cellWidth, out int cellHeight)
        {
            cellWidth = 1;
            cellHeight = 1;
            string sample = SampleCharacters(Text);
            for (int index = 0; index < sample.Length; index++)
                ExpandCell(sample[index].ToString(), font, ref cellWidth, ref cellHeight);
            ExpandCell("字", font, ref cellWidth, ref cellHeight);
            // Spacing scales the cell, not the glyph. The same two numbers decide how many
            // characters fit in a column and where each one is drawn, so measuring and painting
            // cannot disagree. Vertical text reads downwards, so letter spacing lengthens the cell
            // and line spacing widens the column.
            int natural = NaturalLineHeight(font);
            int stroke = BlockPadding(font);
            cellHeight = Math.Max(1, cellHeight +
                (int)Math.Round(natural * _letterSpacingPercent / 100.0) + stroke);
            cellWidth = Math.Max(1, cellWidth +
                (int)Math.Round(cellWidth * (_lineSpacingPercent - 100) / 100.0) + stroke);
        }

        private static void ExpandCell(string text, Font font, ref int cellWidth, ref int cellHeight)
        {
            Size measured = TextRenderer.MeasureText(text, font, new Size(MaxMeasure, MaxMeasure),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (measured.Width > cellWidth) cellWidth = measured.Width;
            if (measured.Height > cellHeight) cellHeight = measured.Height;
        }

        private static string SampleCharacters(string text)
        {
            StringBuilder sample = new StringBuilder();
            if (string.IsNullOrEmpty(text)) return "";
            HashSet<char> seen = new HashSet<char>();
            for (int index = 0; index < text.Length && sample.Length < 24; index++)
            {
                char character = text[index];
                if (character == '\r' || character == '\n' || char.IsWhiteSpace(character)) continue;
                if (seen.Add(character)) sample.Append(character);
            }
            return sample.ToString();
        }
    }

    // Owns every card window: one tray icon for the whole application, one entry per card in the
    // tray menu, and the Ctrl+Alt+Q hot key handed to whichever card can hold it.
    internal sealed class CardManager : IDisposable
    {
        private readonly AppState _state;
        // Where the per-card content libraries live. The manager keeps the directory rather than a
        // repository because there is one repository per card, built as each card is added.
        private readonly string _dataDirectory;
        private readonly List<QuoteCardForm> _forms = new List<QuoteCardForm>();
        private bool _shuttingDown;
        private bool _disposed;

        public CardManager(AppState state, string dataDirectory)
        {
            _state = state;
            _dataDirectory = dataDirectory;
        }

        public List<QuoteCardForm> Forms { get { return _forms; } }

        public bool CanRemoveCard { get { return _forms.Count > 1; } }

        public string TrayTooltip
        {
            get
            {
                int hidden = 0;
                for (int index = 0; index < _forms.Count; index++)
                    if (!_forms[index].Visible) hidden++;
                string text = _forms.Count <= 1 ?
                    UiText.Get(_state, "小笺", "XiaoJian") :
                    UiText.Get(_state, "小笺 · " + _forms.Count + " 张卡片",
                        "XiaoJian · " + _forms.Count + " cards");
                if (hidden > 0)
                    text += UiText.Get(_state, "（" + hidden + " 张已隐藏）", " (" + hidden + " hidden)");
                return text;
            }
        }

        public void Start()
        {
            if (_state.Cards.Count == 0) _state.CreateCard();
            foreach (CardState card in _state.Cards) AddForm(card);
            _state.Save();
            for (int index = 0; index < _forms.Count; index++)
                if (_forms[index].Card.Visible) _forms[index].Show();
            EnsureHotKey();
            UpdateTrayTooltip();
        }

        public void CreateCard()
        {
            CardState card = _state.CreateCard();
            _state.Save();
            QuoteCardForm form = AddForm(card);
            form.Show();
            form.BringToFront();
            UpdateTrayTooltip();
            RefreshAllMenus();
        }

        public void RemoveCard(QuoteCardForm form)
        {
            if (form == null || !_forms.Contains(form) || _forms.Count <= 1) return;
            CloseForm(form, true, false);
            _state.Save();
            UpdateTrayTooltip();
            RefreshAllMenus();
        }

        public void RefreshAllMenus()
        {
            for (int index = 0; index < _forms.Count; index++) _forms[index].RefreshCardList();
        }

        public void RefreshAllCards()
        {
            for (int index = 0; index < _forms.Count; index++) _forms[index].RefreshUi();
        }

        public void UpdateTrayTooltip()
        {
            for (int index = 0; index < _forms.Count; index++)
            {
                if (_forms[index].TrayIconVisible)
                {
                    _forms[index].RefreshTrayTooltip();
                    return;
                }
            }
        }

        // The name of a card as the menu counts them. The content-library editor puts it in its
        // title, so a window that only holds one card's entries still says which card they are.
        public string LibraryLabel(QuoteCardForm form)
        {
            int index = _forms.IndexOf(form);
            if (index < 0 || _forms.Count <= 1) return "";
            return UiText.Get(_state, "卡片 " + (index + 1), "Card " + (index + 1));
        }

        // The menu shows what each card is actually displaying, which is how you tell them apart.
        public string DescribeCard(int index, QuoteCardForm form)
        {
            string label = UiText.Get(_state, "卡片 " + (index + 1), "Card " + (index + 1));
            string quote = form == null ? "" : form.CardQuote;
            quote = (quote ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (quote.Length > 18) quote = quote.Substring(0, 18) + "…";
            return quote.Length == 0 ? label : label + " · " + quote;
        }

        private QuoteCardForm AddForm(CardState card)
        {
            // The first card carries the tray icon; when it goes away the icon moves on.
            QuoteRepository repository = QuoteRepository.ForCard(_state, _dataDirectory, card);
            QuoteCardForm form = new QuoteCardForm(_state, card, repository, _forms.Count == 0);
            form.Manager = this;
            form.FormClosed += delegate(object sender, FormClosedEventArgs e)
            {
                QuoteCardForm closed = sender as QuoteCardForm;
                if (closed == null || !_forms.Contains(closed)) return;
                // The window has already closed itself; only the bookkeeping is left to do.
                CloseForm(closed, false, true);
            };
            _forms.Add(form);
            return form;
        }

        // alreadyClosed is true when the window closed itself (Alt+F4 and friends) and all that
        // remains is the bookkeeping. Calling Close() a second time from inside the form's own
        // FormClosed event re-enters the close path and is what made quitting unstable.
        private void CloseForm(QuoteCardForm form, bool removeState, bool alreadyClosed)
        {
            bool ownedTray = form.TrayIconVisible;
            _forms.Remove(form);
            if (removeState) _state.RemoveCard(form.Card);
            if (ownedTray && !_shuttingDown && _forms.Count > 0) _forms[0].SetTrayIconVisible(true);
            if (!alreadyClosed && !form.IsDisposed) form.Close();

            if (!_shuttingDown && _forms.Count == 0)
            {
                // No card and no tray icon is left, so the process would otherwise sit in the
                // message loop with nothing the user can reach. Posting the exit keeps it clear of
                // the close that is still running, and keeps Application.Exit from having to ask a
                // half-closed window whether it is allowed to close.
                _shuttingDown = true;
                Program.BeginShutdown();
                Program.PostToUi(delegate { Application.Exit(); });
                return;
            }
            if (!_shuttingDown) EnsureHotKey();
        }

        private void EnsureHotKey()
        {
            for (int index = 0; index < _forms.Count; index++)
                if (_forms[index].TryRegisterLocateHotKey()) return;
        }

        // Called from the Exit menu entry. Every card is closed explicitly first, rather than left
        // to Application.Exit: that call asks each window for permission to close, and a card the
        // user has hidden does not answer, which would leave the process running with no card and
        // no tray icon left to reach it through.
        public void Shutdown()
        {
            _shuttingDown = true;
            Program.BeginShutdown();
            for (int index = _forms.Count - 1; index >= 0; index--)
            {
                QuoteCardForm form = _forms[index];
                _forms.RemoveAt(index);
                if (!form.IsDisposed) form.Close();
            }
            _forms.Clear();
            Application.Exit();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shuttingDown = true;
            for (int index = _forms.Count - 1; index >= 0; index--)
            {
                QuoteCardForm form = _forms[index];
                if (!form.IsDisposed) form.Dispose();
            }
            _forms.Clear();
        }
    }

    // A one-field dialog for pinning a sentence to a card. Deliberately small: the card menu needs
    // a single line of text and nothing else.
    internal sealed class TextInputForm : Form
    {
        private readonly TextBox _input;

        private TextInputForm(AppState state, string title, string prompt, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(430, 158);
            Font = QuoteCardForm.UiFont(10.0f, FontStyle.Regular);

            Label promptLabel = new Label();
            promptLabel.Text = prompt;
            promptLabel.SetBounds(16, 14, 398, 40);
            Controls.Add(promptLabel);

            _input = new TextBox();
            _input.Text = initial ?? "";
            _input.SetBounds(16, 58, 398, 26);
            Controls.Add(_input);

            Button confirm = new Button();
            confirm.Text = UiText.Get(state, "确定", "OK");
            confirm.DialogResult = DialogResult.OK;
            confirm.SetBounds(236, 106, 86, 32);
            Controls.Add(confirm);

            Button cancel = new Button();
            cancel.Text = UiText.Get(state, "取消", "Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(328, 106, 86, 32);
            Controls.Add(cancel);

            AcceptButton = confirm;
            CancelButton = cancel;
            _input.SelectAll();
        }

        public static string Prompt(IWin32Window owner, AppState state, string title, string prompt,
            string initial)
        {
            using (TextInputForm dialog = new TextInputForm(state, title, prompt, initial))
            {
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._input.Text : null;
            }
        }
    }

    internal sealed class TransparentHitBoxForm : Form
    {
        private bool _settingCardBounds;
        private bool _userInteracting;
        public event EventHandler UserBoundsChanged;
        public event EventHandler UserResizeFinished;

        public TransparentHitBoxForm()
        {
            Text = "小笺透明点击层";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Opacity = 0.01;
            MinimizeBox = false;
            MaximizeBox = false;
            // This layer hugs the rendered text instead of the whole card, so it can be small, and
            // it takes whatever size the card hands it: a fixed ceiling here would clamp the frame
            // of a large card and drag the card back down to fit it.
            MinimumSize = new Size(24, 20);
        }

        // True between WM_ENTERSIZEMOVE and WM_EXITSIZEMOVE, that is, while the user drags or
        // resizes this frame. The card leaves the frame alone until the gesture ends.
        public bool IsUserInteracting
        {
            get { return _userInteracting; }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int WM_NCHITTEST = 0x0084;
            const int WM_ENTERSIZEMOVE = 0x0231;
            const int WM_EXITSIZEMOVE = 0x0232;
            const int HTCLIENT = 1;
            if (message.Msg == WM_ENTERSIZEMOVE)
            {
                _userInteracting = true;
            }
            else if (message.Msg == WM_EXITSIZEMOVE)
            {
                // Cleared before ResizeEnd runs, so the card may re-align the frame afterwards.
                _userInteracting = false;
            }
            base.WndProc(ref message);
            if (message.Msg != WM_NCHITTEST || (int)message.Result != HTCLIENT || WindowState != FormWindowState.Normal)
                return;
            Point screenPoint = ResizeSupport.ScreenPoint(message.LParam);
            int hit = ResizeSupport.HitTest(ClientSize, PointToClient(screenPoint));
            if (hit != HTCLIENT) message.Result = (IntPtr)hit;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            RaiseUserBoundsChanged();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RaiseUserBoundsChanged();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (!_settingCardBounds && UserResizeFinished != null) UserResizeFinished(this, EventArgs.Empty);
        }

        private void RaiseUserBoundsChanged()
        {
            if (!_settingCardBounds && UserBoundsChanged != null) UserBoundsChanged(this, EventArgs.Empty);
        }

        public void SetHitFrameBounds(Rectangle bounds)
        {
            _settingCardBounds = true;
            try
            {
                Bounds = bounds;
                Region old = Region;
                Region = null;
                if (old != null) old.Dispose();
            }
            finally
            {
                _settingCardBounds = false;
            }
        }
    }

    internal sealed class QuoteEditorForm : Form
    {
        private readonly QuoteRepository _repository;
        private readonly TextBox _editor;
        private readonly Label _countLabel;
        private readonly string _originalText;

        public QuoteEditorForm(QuoteRepository repository)
            : this(repository, "")
        {
        }

        public QuoteEditorForm(QuoteRepository repository, string cardLabel)
        {
            _repository = repository;
            Text = cardLabel.Length == 0 ? T("编辑内容库", "Edit content library") :
                T("编辑内容库 · " + cardLabel, "Edit content library · " + cardLabel);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(660, 540);
            MinimumSize = new Size(520, 400);
            BackColor = Color.FromArgb(18, 18, 18);
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Point);
            KeyPreview = true;

            Label heading = new Label();
            heading.Text = cardLabel.Length == 0 ? T("我的内容库", "My content library") :
                T(cardLabel + " 的内容库", cardLabel + "'s content library");
            heading.Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold, GraphicsUnit.Point);
            heading.ForeColor = Color.White;
            heading.AutoSize = true;
            heading.Location = new Point(24, 20);
            Controls.Add(heading);

            Label help = new Label();
            help.Text = T("每行放一条内容，长段文字也可以。空行和重复内容会忽略；每天随机选一条。这些内容只属于这张卡片。",
                "Put one entry on each line; long text is fine.\nBlank and duplicate lines are ignored; one is chosen each day. These entries belong to this card only.");
            help.ForeColor = Color.FromArgb(165, 165, 165);
            help.AutoSize = false;
            help.Location = new Point(27, 62);
            help.Size = new Size(ClientSize.Width - 54, 38);
            help.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(help);

            _editor = new TextBox();
            _editor.Multiline = true;
            _editor.AcceptsReturn = true;
            _editor.AcceptsTab = false;
            _editor.ScrollBars = ScrollBars.Vertical;
            _editor.BorderStyle = BorderStyle.FixedSingle;
            _editor.BackColor = Color.FromArgb(8, 8, 8);
            _editor.ForeColor = Color.White;
            _editor.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Point);
            _editor.Location = new Point(28, 92);
            _editor.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _editor.Size = new Size(ClientSize.Width - 56, ClientSize.Height - 164);
            string entrySeparator = repository.IsParagraphMode ?
                Environment.NewLine + Environment.NewLine : Environment.NewLine;
            _editor.Text = string.Join(entrySeparator, repository.Load().ToArray());
            _originalText = _editor.Text;
            _editor.TextChanged += delegate { UpdateCount(); };
            Controls.Add(_editor);

            _countLabel = new Label();
            _countLabel.ForeColor = Color.FromArgb(145, 145, 145);
            _countLabel.AutoSize = false;
            _countLabel.TextAlign = ContentAlignment.MiddleLeft;
            _countLabel.Location = new Point(28, ClientSize.Height - 57);
            _countLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _countLabel.Size = new Size(180, 30);
            Controls.Add(_countLabel);

            Button cancel = CreateButton(T("取消", "Cancel"), false);
            cancel.Location = new Point(ClientSize.Width - 220, ClientSize.Height - 60);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;

            Button save = CreateButton(T("保存", "Save"), true);
            save.Location = new Point(ClientSize.Width - 118, ClientSize.Height - 60);
            save.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            save.Click += delegate { SaveQuotes(); };
            Controls.Add(save);
            AcceptButton = save;

            FormClosing += ConfirmDiscardIfNeeded;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.S)
                {
                    e.SuppressKeyPress = true;
                    SaveQuotes();
                }
            };
            Shown += delegate { _editor.Focus(); _editor.SelectionStart = _editor.TextLength; };
            UpdateCount();
        }

        private static Button CreateButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(90, 36);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? Color.White : Color.FromArgb(90, 90, 90);
            button.BackColor = primary ? Color.White : Color.FromArgb(28, 28, 28);
            button.ForeColor = primary ? Color.Black : Color.White;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private void SaveQuotes()
        {
            try
            {
                _repository.SaveEditorLines(_editor.Lines);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, T("内容库还不能保存", "The content library could not be saved"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateCount()
        {
            if (_countLabel == null) return;
            List<string> values = new List<string>();
            foreach (string line in _editor.Lines)
            {
                string value = (line ?? "").Trim();
                if (value.Length > 0 && !values.Contains(value)) values.Add(value);
            }
            _countLabel.Text = T("共 " + values.Count.ToString(CultureInfo.InvariantCulture) + " 条",
                values.Count.ToString(CultureInfo.InvariantCulture) + (values.Count == 1 ? " entry" : " entries"));
        }

        private void ConfirmDiscardIfNeeded(object sender, FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK || _editor.Text == _originalText) return;
            DialogResult answer = MessageBox.Show(
                T("内容库有尚未保存的修改，要直接放弃吗？",
                    "The content library has unsaved changes. Discard them?"),
                T("编辑内容库", "Edit content library"), MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) e.Cancel = true;
        }

        private string T(string chinese, string english)
        {
            return _repository.IsEnglish ? english : chinese;
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(28, 28, 28); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(28, 28, 28); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(28, 28, 28); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(28, 28, 28); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(58, 58, 58); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(80, 80, 80); } }
        public override Color MenuBorder { get { return Color.FromArgb(65, 65, 65); } }
        public override Color SeparatorDark { get { return Color.FromArgb(50, 50, 50); } }
        public override Color SeparatorLight { get { return Color.FromArgb(50, 50, 50); } }
        public override Color CheckBackground { get { return Color.FromArgb(70, 70, 70); } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(85, 85, 85); } }
    }

    // How small a card may be dragged. The background follows the pointer to any size the desktop
    // can hold, but never past the text it is holding: at the floor the text area holds the whole
    // text, instead of the card stopping at some fixed rectangle.
    //
    // The margins below are the single source of truth for the arithmetic, and LayoutCard() lays the
    // text view out with the same insets. The size self test asserts the two agree, because a margin
    // changed in one place and not the other would silently let a card clip its own text.
    //
    // There are two answers here and they are not the same shape, which is worth keeping straight:
    //
    //   * MinimumFor - the least-area card. There is no single smallest footprint: narrowing a text
    //     area re-wraps the text into more lines, so every width has its own height and the pairs
    //     trade one axis for the other - a very wide card is short, a very narrow one is tall. Of
    //     those pairs the one with the least area is taken, which leaves the least empty background.
    //     This is the shape "fit to text" snaps to.
    //   * DragLimitFor - the floor a drag is held to. It constrains only the axis across the text's
    //     flow, and reads the axis along the flow to do it. Using the least-area *point* as the drag
    //     floor was wrong: it cut both ends off the curve, so a horizontal card could never be
    //     dragged down to one line and a vertical card could never be pulled in to one column.
    //
    // Neither reads the axis it constrains, which is what keeps a card from being squeezed down a
    // floor that moves with it: see the notes on each.
    //
    // Least area on its own is not enough, and the previous release only got away with it by
    // accident: a wide fixed padding lifted every narrow candidate to the same width, so they
    // stopped competing. Take that padding away and the area rule starts preferring a text area too
    // narrow to hold even one character - the measured block comes back clamped to the view and
    // claims to fit. MinimumFor turns those candidates away instead (see SmallestGlyphWidth).
    // Snapping for a card being dragged past its neighbours.
    //
    // The two axes are decided independently: being level with another card must not also pull this
    // one sideways. Each axis looks at three lines on the moving card - near edge, centre, far edge -
    // against the same three on every neighbour and on the screen, and takes the smallest move that
    // brings any pair within the threshold.
    //
    // Pure arithmetic on rectangles, so the whole thing runs in the test harness.
    internal static class CardSnap
    {
        // How far apart two lines may be and still be pulled together, in pixels.
        public const int Distance = 8;

        // `proposed` nudged so that one of its lines lands on one of the targets. A line is only
        // pulled when it is within `distance`, and only the closest pair on each axis wins - so a
        // card that is nowhere near anything is returned unchanged.
        public static Point Adjust(Point proposed, Size size, Rectangle[] neighbours,
            Rectangle screen, int distance)
        {
            if (distance <= 0) return proposed;
            List<int> verticals = new List<int>();
            List<int> horizontals = new List<int>();
            AddLines(verticals, horizontals, screen);
            if (neighbours != null)
            {
                for (int index = 0; index < neighbours.Length; index++)
                    AddLines(verticals, horizontals, neighbours[index]);
            }
            int dx = OffsetFor(proposed.X, size.Width, verticals, distance);
            int dy = OffsetFor(proposed.Y, size.Height, horizontals, distance);
            return new Point(proposed.X + dx, proposed.Y + dy);
        }

        // A rectangle contributes three lines per axis: both edges and the centre.
        private static void AddLines(List<int> verticals, List<int> horizontals, Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            verticals.Add(rect.Left);
            verticals.Add(rect.Left + rect.Width / 2);
            verticals.Add(rect.Right);
            horizontals.Add(rect.Top);
            horizontals.Add(rect.Top + rect.Height / 2);
            horizontals.Add(rect.Bottom);
        }

        // The smallest move that lines one of the moving card's three lines up with one of the
        // targets. Zero when nothing is close enough.
        private static int OffsetFor(int origin, int extent, List<int> targets, int distance)
        {
            int best = 0;
            int bestMagnitude = distance + 1;
            for (int edge = 0; edge < 3; edge++)
            {
                int line = origin + (edge == 0 ? 0 : (edge == 1 ? extent / 2 : extent));
                for (int index = 0; index < targets.Count; index++)
                {
                    int delta = targets[index] - line;
                    int magnitude = delta < 0 ? -delta : delta;
                    if (magnitude > distance) continue;
                    if (magnitude >= bestMagnitude) continue;
                    bestMagnitude = magnitude;
                    best = delta;
                }
            }
            return best;
        }
    }

    internal static class CardSizeLimits
    {
        // The radius the card is actually drawn with. The card asks for one (CardState.CornerRadius,
        // default 18) and this settles what is possible: the window region and the painted border
        // both come through here, so they cannot disagree about the shape.
        //
        // Two caps. The card's own size comes first - a radius larger than half the shorter side
        // makes RoundedRectangle's four arcs overlap and draws a bowtie - and the card's request is
        // already clamped to CardState.MaxCornerRadius by Clamp(), which is where the ceiling
        // derived from the padding lives.
        public static int CornerRadiusFor(int requested, int width, int height)
        {
            int radius = Math.Max(0, Math.Min(CardState.MaxCornerRadius, requested));
            int limit = Math.Min(width, height) / 2;
            if (limit < 0) limit = 0;
            return Math.Min(radius, limit);
        }

        // LayoutCard insets the text view by these amounts, and they are the only padding between
        // the card and its own text. They are kept apart instead of folded into one number per axis
        // because the two axes have different reasons for existing:
        //
        //   * Left and right are the rounded corner's clearance and nothing else, so they are the
        //     same 12 as the vertical padding. They used to be 32 - three times what the corner
        //     needs - and that padding, not the text, was what stopped a small card from being
        //     narrowed: a four character card was 107 pixels wide to hold 37 pixels of text. That is
        //     the fixed minimum width this release removes.
        //   * Top and bottom are breathing space and nothing else. They used to be 20 each because
        //     the bottom of the card reserved a whole strip for the date label - a strip for a label
        //     that was never shown, which made every card at least 40 pixels taller than the text it
        //     held. That is the fixed minimum height the previous release removed.
        public const int TextPadLeft = 12;
        public const int TextPadRight = 12;
        public const int TextPadTop = 12;
        public const int TextPadBottom = 12;

        // The same insets as one number per axis, which is the form the floor arithmetic wants.
        public const int HorizontalMargin = TextPadLeft + TextPadRight;
        public const int VerticalMargin = TextPadTop + TextPadBottom;

        // The text view keeps its own inset and the text has to fit inside that, so the floor is
        // that much larger than the block. QuoteView.TextMargin is the source of truth for it and
        // the size self test asserts the two agree.
        public const int TextSlack = 4;

        // One line of the smallest font, leading included: 7pt of a CJK face is about 12 pixels.
        public const int SmallestLineHeight = 12;
        // One character of the smallest font across, which is the least a text area can be and
        // still hold any text at all: 7pt of a CJK face is about 9.3 pixels wide, and the widest
        // glyph a face can have is its em box, so 10 is the conservative number to guard with.
        // A candidate text area narrower than this is passed over - without it the area rule
        // happily picks one, because a block measured in a view too small for a single glyph comes
        // back clamped to that view and reports itself as fitting.
        public const int SmallestGlyphWidth = 10;

        // The "..." button sits in the bottom-right corner, measured from the same edges. It is a
        // convenience rather than the only way in - the same menu opens on a right-click anywhere
        // on the card, and from the tray icon - so it is never allowed to hold the card's size open
        // the way the date strip did.
        public const int MenuButtonWidth = 34;
        public const int MenuButtonHeight = 28;
        public const int MenuButtonRightInset = 56;
        public const int MenuButtonBottomInset = 43;
        // The button's own strip plus one line of text below it. Below this there is no room for
        // the button beside the text, so it steps aside rather than landing on top of it.
        public const int MenuButtonMinCardHeight = MenuButtonBottomInset + SmallestLineHeight;
        // ... and the same idea across. The button's left edge sits MenuButtonRightInset from the
        // right, so the card has to be wide enough for that edge to fall past the middle of the text
        // area; any narrower and the button covers the very line it is supposed to sit beside. With
        // the padding above that works out to twice the inset:
        //
        //     W - MenuButtonRightInset >= TextPadLeft + (W - HorizontalMargin) / 2
        //     W - 56 >= 12 + (W - 24) / 2   ->   W / 2 >= 56   ->   W >= 112
        //
        // Both thresholds are display decisions only. They decide whether the button is drawn,
        // never how small a card may be - the floor is the text's footprint, and the button has no
        // say in it.
        public const int MenuButtonMinCardWidth = MenuButtonRightInset * 2;

        // The layout never lets a text view collapse below this, so the search does not either.
        private const int SmallestView = 10;
        // Wide enough that vertical text is never truncated, used to ask what it really needs.
        private const int WidestView = 4096;

        // The smallest card that can show anything at all: one character across, one line down.
        // It does not depend on the text or on which way the text runs - it is the floor for the
        // axis the text is free to flow along, and the floor every other answer is measured from.
        public static Size SmallestTextCard
        {
            get
            {
                return new Size(
                    Math.Max(CardState.MinCardWidth,
                        SmallestGlyphWidth + TextSlack + HorizontalMargin),
                    Math.Max(CardState.MinCardHeight,
                        SmallestLineHeight + TextSlack + VerticalMargin));
            }
        }

        // The block the text covers in a text area of exactly this size. Passed in rather than
        // measured here, so the arithmetic can be exercised without GDI.
        public delegate Size Measure(int viewWidth, int viewHeight);

        // The smallest card the text can be dragged to *when the card is already flowAxis long
        // along the direction the text runs*. That is the width for horizontal text and the height
        // for vertical text.
        //
        // Horizontal text runs left to right, so this constrains the height: the width decides how
        // many lines the text wraps onto, and the lines decide the height. The width itself only
        // has to hold one character. Vertical text is the mirror image, constraining the width.
        //
        // Why the floor cannot be the single point MinimumFor returns: the sizes that hold the text
        // are a whole curve, not a point. A horizontal card can be wide and one line tall, or narrow
        // and many lines tall, and the two ends have almost the same area. Applying the least-area
        // point as a box cuts both ends off - a horizontal card can never reach one line, a vertical
        // card can never reach one column. That is exactly what "horizontal cards will not go any
        // shorter, vertical cards will not go any narrower" was.
        //
        // Depending on flowAxis is deliberate and is not the trap the old floor fell into: what it
        // reads is the *other* axis, never the axis it constrains. Dragging the card shorter cannot
        // lower the height floor, because the height floor does not look at the height.
        public static Size DragLimitFor(Measure measure, bool vertical, int flowAxis)
        {
            Size unit = SmallestTextCard;
            if (measure == null) return unit;
            int viewFlow = Math.Max(SmallestView,
                flowAxis - (vertical ? VerticalMargin : HorizontalMargin));
            Size block = vertical ? measure(WidestView, viewFlow) : measure(viewFlow, WidestView);
            if (block.Width <= 0 || block.Height <= 0) return unit;
            if (vertical)
                return new Size(Math.Max(unit.Width, block.Width + TextSlack + HorizontalMargin),
                    unit.Height);
            return new Size(unit.Width,
                Math.Max(unit.Height, block.Height + TextSlack + VerticalMargin));
        }

        // The smallest card that can hold this text. Every candidate text area is measured and the
        // one whose card has the least area wins; nothing here reads the size the card has now.
        public static Size MinimumFor(Measure measure, bool vertical)
        {
            Size best = new Size(CardState.MinCardWidth, CardState.MinCardHeight);
            long bestArea = long.MaxValue;
            for (int probe = SmallestView; probe <= WidestView; probe = NextProbe(probe))
            {
                // Horizontal text wraps to the width it is given, so the width is what is probed and
                // the height is whatever the wrap needs. Vertical text is the mirror image: its
                // columns are set by the height, so the height is probed and the width is what the
                // columns need. Either way the other axis is left wide open, so the block comes back
                // untrimmed and is the true footprint of the text at this probe.
                Size block = vertical ? measure(WidestView, probe) : measure(probe, WidestView);
                if (block.Width <= 0 || block.Height <= 0) continue;

                // A candidate is only usable if the view it was measured with really fits what the
                // measurement came back with. Vertical text is where this bites: its columns are set
                // by the height, and at the bottom of the probe range the view comes back shorter
                // than a single glyph - the block is then one glyph tall and a card built from it
                // would clip its own text.
                if ((vertical ? block.Height : block.Width) + TextSlack > probe) continue;

                // ... and only if it can hold one character at all. The guard above cannot see this
                // for horizontal text, because that text wraps and the block comes back clamped to
                // the view: a view narrower than a single glyph reports a block that fits it
                // exactly, and the area rule then prefers that candidate precisely for being so
                // narrow. A card built there would clip every character in the text.
                if (probe - TextSlack < (vertical ? SmallestLineHeight : SmallestGlyphWidth)) continue;

                // The degenerate floor is applied to the candidate, not to the winner: raising it
                // afterwards could lift the winner above a candidate that needed no raising at all.
                int width = Math.Max(CardState.MinCardWidth,
                    (vertical ? block.Width + TextSlack : probe) + HorizontalMargin);
                int height = Math.Max(CardState.MinCardHeight,
                    (vertical ? probe : block.Height + TextSlack) + VerticalMargin);
                long area = (long)width * height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = new Size(width, height);
                }
            }
            return best;
        }

        // The inverse of DragLimitFor: the smallest card that still shows the whole text when the
    // card may only grow along the direction the text flows.
    //
    // This is the answer for a card whose axis across the flow has already reached the desktop.
    // There the text cannot be made to fit by growing that axis any further, so the run of text
    // has to get longer instead - a wider horizontal card wraps onto fewer lines, a taller
    // vertical card needs fewer columns - until what is left over fits in the room the card
    // already has.
    //
    // It is NOT the least-area card. At a large font the least-area card is the
    // one-character-per-line sliver, which is narrower than the card already is, so using it
    // here grows nothing at all: the card stops moving, and every extra point of font the user
    // asks for is clipped away. That is what "the text will not get any bigger" looks like from
    // outside - the number goes up and nothing on screen changes.
    //
    // The footprint across the flow only ever gets smaller as the flow axis grows, so the first
    // candidate that fits is also the smallest one. When nothing fits - the text is longer than
    // any card could ever hold - the longest candidate is returned, because that is the one
    // that leaves the least of the text outside the card.
    public static Size FlowFitFor(Measure measure, bool vertical, int across)
    {
        Size unit = SmallestTextCard;
        if (measure == null) return unit;
        Size longest = unit;
        for (int probe = SmallestView; probe <= WidestView; probe = NextProbe(probe))
        {
            Size block = vertical ? measure(WidestView, probe) : measure(probe, WidestView);
            if (block.Width <= 0 || block.Height <= 0) continue;
            // The same two guards MinimumFor needs, for the same reasons: a candidate has to
            // really hold the block it measured, and the text area has to hold a character at
            // all - a view narrower than one reports a block that fits it exactly.
            if ((vertical ? block.Height : block.Width) + TextSlack > probe) continue;
            if (probe - TextSlack < (vertical ? SmallestLineHeight : SmallestGlyphWidth))
                continue;
            if (vertical)
            {
                longest = new Size(
                    Math.Max(unit.Width, block.Width + TextSlack + HorizontalMargin),
                    probe + VerticalMargin);
                if (block.Width + TextSlack + HorizontalMargin <= across) return longest;
            }
            else
            {
                longest = new Size(probe + HorizontalMargin,
                    Math.Max(unit.Height, block.Height + TextSlack + VerticalMargin));
                if (block.Height + TextSlack + VerticalMargin <= across) return longest;
            }
        }
        return longest;
    }

    // Geometric steps. The probes span three orders of magnitude, and the search only has to
        // land near the smallest card rather than on it: a floor a few pixels either side of the
        // optimum is still a floor the text fits in, which is the part that matters.
        private static int NextProbe(int probe)
        {
            return Math.Max(probe + 1, probe + probe / 8 + 1);
        }

        // A card may be dragged out to the whole desktop, but not past it: beyond that the only
        // thing that grows is the background image scaling.
        public static Size MaximumFor(Size desktop)
        {
            return new Size(
                Math.Max(CardState.MinCardWidth, Math.Min(CardState.MaxCardWidth, desktop.Width)),
                Math.Max(CardState.MinCardHeight, Math.Min(CardState.MaxCardHeight, desktop.Height)));
        }
    }

    internal static class ResizeSupport
    {
        private const int Grip = 12;
        public const int Client = 1;
        public const int Left = 10;
        public const int Right = 11;
        public const int Top = 12;
        public const int TopLeft = 13;
        public const int TopRight = 14;
        public const int Bottom = 15;
        public const int BottomLeft = 16;
        public const int BottomRight = 17;

        public static Point ScreenPoint(IntPtr packedPoint)
        {
            long value = packedPoint.ToInt64();
            int x = unchecked((short)(value & 0xFFFF));
            int y = unchecked((short)((value >> 16) & 0xFFFF));
            return new Point(x, y);
        }

        public static int HitTest(Size size, Point point)
        {
            // Small frames (the transparent interaction layer) need smaller grips, otherwise the
            // grips would leave no room left to drag the frame itself.
            int grip = Math.Min(Grip, Math.Min(size.Width, size.Height) / 4);
            if (grip < 3) grip = 3;
            bool left = point.X < grip;
            bool right = point.X >= size.Width - grip;
            bool top = point.Y < grip;
            bool bottom = point.Y >= size.Height - grip;
            if (left && top) return TopLeft;
            if (right && top) return TopRight;
            if (left && bottom) return BottomLeft;
            if (right && bottom) return BottomRight;
            if (left) return Left;
            if (right) return Right;
            if (top) return Top;
            if (bottom) return Bottom;
            return Client;
        }
    }

    internal static class NativeMethods
    {
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        // The square-cornered case. CreateRoundRectRgn with a zero sized ellipse is documented to
        // behave the same way, but that is a corner of the API nobody tests - asking for a plain
        // rectangle says what is meant.
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);

        // Pulls one icon out of a file that carries an icon group - an .exe built with
        // /win32icon does. phiconLarge and phiconSmall receive the sizes the shell metrics ask
        // for, which is what "the same picture as the window icon, at the size the tray wants"
        // means. Both buffers have to hold nIcons entries.
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int ExtractIconEx(string file, int index, IntPtr[] large,
            IntPtr[] small, int count);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        public static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        public static extern int SetWindowLong(IntPtr window, int index, int newValue);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
