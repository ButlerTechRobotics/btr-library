using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BtrLibrary
{
    /// <summary>
    /// Butler Tech Robotics theme. Same palette as BTR Blockout, BTR Calc and BTR Lighten:
    /// 144 orange, 325 blue, charcoal brand bar. Values copied from the apps' CSS variables.
    /// </summary>
    public sealed class Palette
    {
        public bool Dark;
        public Color Bg, Panel, Panel2, Line, Ink, Ink2, Field;
        public Color Accent, AccentInk, AccentSoft, Orange, Blue;
        public Color Bar, Bar2, BarLine, BarFg, BarMuted;
        public Color Ok, Warn, Bad, Plate;

        public static readonly Palette Light = new Palette
        {
            Dark = false,
            Bg = H("#eef1f5"), Panel = H("#f7f8fa"), Panel2 = H("#eceff3"), Line = H("#d3d9e1"),
            Ink = H("#181a1f"), Ink2 = H("#5d6675"), Field = H("#ffffff"),
            Accent = H("#e85d12"), AccentInk = H("#ffffff"), AccentSoft = Color.FromArgb(0x20, H("#e85d12")),
            Orange = H("#f26a1b"), Blue = H("#0b3bbf"),
            Bar = H("#181818"), Bar2 = H("#262626"), BarLine = H("#3a3a3a"), BarFg = H("#ffffff"), BarMuted = H("#b9b9b9"),
            Ok = H("#2f8a3f"), Warn = H("#b36b00"), Bad = H("#c9302c"), Plate = H("#ffffff"),
        };

        public static readonly Palette DarkMode = new Palette
        {
            Dark = true,
            Bg = H("#15171b"), Panel = H("#1b1d21"), Panel2 = H("#23262c"), Line = H("#32363e"),
            Ink = H("#eceef2"), Ink2 = H("#9aa2af"), Field = H("#15171b"),
            Accent = H("#ff7a2e"), AccentInk = H("#1a0d04"), AccentSoft = Color.FromArgb(0x2a, H("#ff7a2e")),
            Orange = H("#ff7a2e"), Blue = H("#3d6bff"),
            Bar = H("#101010"), Bar2 = H("#1f1f1f"), BarLine = H("#333333"), BarFg = H("#ffffff"), BarMuted = H("#a8a8a8"),
            Ok = H("#57c26a"), Warn = H("#f0b429"), Bad = H("#ff5f56"), Plate = H("#1b1e23"),
        };

        private static Color H(string hex) => ColorTranslator.FromHtml(hex);
    }

    public static class Theme
    {
        public static Palette P { get; private set; } = Resolve();
        public static event EventHandler Changed;

        public static void Refresh()
        {
            var next = Resolve();
            if (next == P) return;
            P = next;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static Palette Resolve()
        {
            switch (Settings.Theme)
            {
                case "light": return Palette.Light;
                case "dark": return Palette.DarkMode;
                default: return WindowsAppsUseDark() ? Palette.DarkMode : Palette.Light;
            }
        }

        private static bool WindowsAppsUseDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        /// <summary>Dark scrollbars / expand arrows for native controls (TreeView etc.).</summary>
        public static void ApplyNativeTheme(Control c)
        {
            if (!c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, P.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>
    /// Loads the embedded Barlow / Barlow Condensed fonts so the pane uses the same type as the web apps,
    /// even on PCs that don't have them installed. Falls back to Bahnschrift / Segoe UI.
    /// </summary>
    public static class BtrFonts
    {
        private static readonly PrivateFontCollection Collection = new PrivateFontCollection();
        private static readonly List<IntPtr> Pinned = new List<IntPtr>();
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            foreach (var name in Assets.Names.Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    byte[] data;
                    using (var s = Assets.Open(name))
                    using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }

                    IntPtr mem = Marshal.AllocCoTaskMem(data.Length); // kept for the process lifetime
                    Marshal.Copy(data, 0, mem, data.Length);
                    Pinned.Add(mem);

                    Collection.AddMemoryFont(mem, data.Length);       // GDI+ (owner-drawn text)
                    uint count = 0;
                    AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref count); // GDI (TextBox, TreeView...)
                }
                catch { /* fall back to system fonts */ }
            }
        }

        private static FontFamily Family(params string[] names)
        {
            EnsureLoaded();
            foreach (var n in names)
            {
                var f = Collection.Families.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (f != null && Usable(f)) return f;
                try
                {
                    var installed = new FontFamily(n);
                    if (installed.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return installed;
                }
                catch (ArgumentException) { }
            }
            return SystemFonts.MessageBoxFont.FontFamily;
        }

        private static bool Usable(FontFamily f)
        {
            try { using (var test = new Font(f, 10f)) return test.Height > 0; }
            catch { return false; }
        }

        /// <summary>Body text — var(--f-ui).</summary>
        public static Font Ui(float pt, FontStyle style = FontStyle.Regular) =>
            new Font(Family("Barlow", "Segoe UI"), pt, style);

        public static Font UiMedium(float pt) =>
            new Font(Family("Barlow Medium", "Barlow", "Segoe UI Semibold"), pt);

        /// <summary>Buttons, labels, headings — var(--f-cond) at weight 600.</summary>
        public static Font Cond(float pt) =>
            new Font(Family("Barlow Condensed SemiBold", "Bahnschrift SemiBold Condensed", "Segoe UI Semibold"), pt);

        public static Font CondBold(float pt) =>
            new Font(Family("Barlow Condensed Bold", "Barlow Condensed SemiBold", "Bahnschrift Condensed", "Segoe UI"), pt, FontStyle.Regular);

        [DllImport("gdi32.dll")]
        private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);
    }

    // ---------------------------------------------------------------- controls

    /// <summary>Charcoal brand bar with the BTR logo and the orange/blue stripes, like the web apps' header.</summary>
    public sealed class BtrHeader : Control
    {
        private Image _brand;
        private float _scale = -1;

        public BtrHeader()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
        }

        public void UpdateScale(float s)
        {
            if (Math.Abs(s - _scale) < 0.01f) return;
            _scale = s;
            _brand?.Dispose();
            _brand = Assets.LoadImage(s >= 1.75f ? "brand_20.png" : s >= 1.25f ? "brand_15.png" : "brand_10.png");
            Height = (int)Math.Round(58 * s);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P;
            float s = _scale <= 0 ? 1 : _scale;
            int stripe = Math.Max(2, (int)Math.Round(3 * s));
            var g = e.Graphics;

            using (var b = new SolidBrush(p.Bar)) g.FillRectangle(b, ClientRectangle);
            using (var b = new SolidBrush(p.Blue)) g.FillRectangle(b, 0, Height - stripe * 2, Width, stripe);
            using (var b = new SolidBrush(p.Orange)) g.FillRectangle(b, 0, Height - stripe, Width, stripe);

            if (_brand == null) return;
            // Brand art is rendered at 1x/1.5x/2x; draw at the 1x logical size (236 x 44).
            int w = (int)Math.Round(236 * s), h = (int)Math.Round(44 * s);
            int avail = Height - stripe * 2;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_brand, new Rectangle((int)(10 * s), (avail - h) / 2, w, h));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _brand?.Dispose();
            base.Dispose(disposing);
        }
    }

    public enum BtnKind { Normal, Primary }

    /// <summary>Rounded, uppercase condensed button — the .btn / .btn.primary style.</summary>
    public sealed class BtrButton : Control
    {
        private bool _hover, _down;
        public BtnKind Kind { get; set; }
        public float UiScale { get; set; } = 1;

        public BtrButton(string text, BtnKind kind = BtnKind.Normal)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Text = text;
            Kind = kind;
            Cursor = Cursors.Hand;
            TabStop = true;
            Margin = new Padding(0, 0, 6, 0);
            Font = BtrFonts.Cond(10.5f);
        }

        public void FitToText()
        {
            var sz = TextRenderer.MeasureText(Text.ToUpperInvariant(), Font);
            Size = new Size(sz.Width + (int)(22 * UiScale), sz.Height + (int)(10 * UiScale));
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitToText(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitToText(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? p.Panel);

            Color fill, border, ink;
            if (Kind == BtnKind.Primary) { fill = p.Accent; border = p.Accent; ink = p.AccentInk; }
            else { fill = p.Panel2; border = _hover ? p.Ink2 : p.Line; ink = p.Ink; }
            if (_down) fill = Theme.Mix(fill, p.Ink, 0.12f);
            if (!Enabled)
            {
                var bg = Parent?.BackColor ?? p.Panel;
                fill = Theme.Mix(fill, bg, 0.55f); border = Theme.Mix(border, bg, 0.55f); ink = Theme.Mix(ink, bg, 0.55f);
            }

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(r, 8 * UiScale))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(border)) g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
                using (var path = Theme.RoundRect(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), 7 * UiScale))
                using (var pen = new Pen(p.Accent, 2)) g.DrawPath(pen, path);

            TextRenderer.DrawText(g, Text.ToUpperInvariant(), Font, ClientRectangle, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Rounded panel — the .card style (panel fill, line border, 12px radius).</summary>
    public sealed class Card : Panel
    {
        public float UiScale { get; set; } = 1;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? p.Bg);
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12 * UiScale))
            {
                using (var b = new SolidBrush(p.Panel)) g.FillPath(b, path);
                using (var pen = new Pen(p.Line)) g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Rounded field border around a TextBox/ComboBox — the input style.</summary>
    public sealed class FieldHost : Panel
    {
        public float UiScale { get; set; } = 1;
        public bool Focus2 { get; set; }

        public FieldHost(Control inner)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(7, 5, 7, 4);
            inner.Dock = DockStyle.Fill;
            Controls.Add(inner);
            inner.GotFocus += (s, e) => { Focus2 = true; Invalidate(); };
            inner.LostFocus += (s, e) => { Focus2 = false; Invalidate(); };
            inner.SizeChanged += (s, e) => Height = inner.Height + Padding.Vertical;
            Height = inner.Height + Padding.Vertical;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? p.Bg);
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6 * UiScale))
            {
                using (var b = new SolidBrush(p.Field)) g.FillPath(b, path);
                using (var pen = new Pen(Focus2 ? p.Accent : p.Line, Focus2 ? 2 : 1)) g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Small uppercase heading with the orange square — the h2 style.</summary>
    public sealed class SectionLabel : Control
    {
        public float UiScale { get; set; } = 1;

        public SectionLabel(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            Font = BtrFonts.CondBold(9.5f);
            Height = TextRenderer.MeasureText("X", Font).Height + 4;
            Margin = new Padding(0, 8, 0, 3);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.P;
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? p.Panel);
            int sq = (int)Math.Round(8 * UiScale);
            using (var b = new SolidBrush(p.Orange))
            using (var path = Theme.RoundRect(new RectangleF(0, (Height - sq) / 2f, sq, sq), 2 * UiScale))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(b, path);
            }
            var r = new Rectangle(sq + (int)(8 * UiScale), 0, Width, Height);
            TextRenderer.DrawText(g, Text.ToUpperInvariant(), Font, r, p.Ink2,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Menu colors for right-click / options menus.</summary>
    public sealed class BtrMenuColors : ProfessionalColorTable
    {
        private static Palette P => Theme.P;
        public override Color ToolStripDropDownBackground => P.Panel;
        public override Color ImageMarginGradientBegin => P.Panel;
        public override Color ImageMarginGradientMiddle => P.Panel;
        public override Color ImageMarginGradientEnd => P.Panel;
        public override Color MenuBorder => P.Line;
        public override Color MenuItemBorder => P.Accent;
        public override Color MenuItemSelected => P.AccentSoft.A == 255 ? P.AccentSoft : Theme.Mix(P.Panel, P.Accent, 0.16f);
        public override Color SeparatorDark => P.Line;
        public override Color SeparatorLight => P.Line;
        public override Color CheckBackground => Theme.Mix(P.Panel, P.Accent, 0.25f);
        public override Color CheckSelectedBackground => Theme.Mix(P.Panel, P.Accent, 0.35f);
        public override Color CheckPressedBackground => Theme.Mix(P.Panel, P.Accent, 0.35f);
    }
}
