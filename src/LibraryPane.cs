using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;

namespace BtrLibrary
{
    /// <summary>TreeView without the horizontal scrollbar (long names get an ellipsis instead).</summary>
    internal sealed class NoHScrollTreeView : TreeView
    {
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.Style |= 0x8000; /* TVS_NOHSCROLL */ return cp; }
        }
    }

    /// <summary>The BTR Library task pane: brand bar, search, Vendor › Category › Part tree, details card.</summary>
    public sealed class LibraryPane : UserControl
    {
        private readonly ISldWorks _app;
        private List<LibraryItem> _items = new List<LibraryItem>();

        private readonly BtrHeader _header = new BtrHeader();
        private readonly Panel _toolbar = new Panel();
        private readonly TextBox _search = new TextBox();
        private FieldHost _searchHost;
        private readonly BtrButton _sync = new BtrButton("Sync");
        private readonly BtrButton _refresh = new BtrButton("↻");
        private readonly BtrButton _more = new BtrButton("⋯");
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();

        private readonly SplitContainer _split = new SplitContainer();
        private readonly Card _treeCard = new Card();
        private readonly TreeView _tree = new NoHScrollTreeView();
        private readonly Card _detailCard = new Card();
        private TableLayoutPanel _details;
        private readonly ModelViewer _preview = new ModelViewer();
        private Image _thumb;
        private readonly Label _title = new Label();
        private readonly Label _sub = new Label();
        private readonly Label _desc = new Label();
        private readonly LinkLabel _url = new LinkLabel();
        private readonly SectionLabel _configLabel = new SectionLabel("Configuration");
        private readonly ComboBox _config = new ComboBox();
        private FieldHost _configHost;
        private readonly BtrButton _insert = new BtrButton("Insert", BtnKind.Primary);
        private readonly BtrButton _open = new BtrButton("Open");
        private readonly BtrButton _folder = new BtrButton("Show file");

        private readonly Panel _footer = new Panel();
        private readonly Label _status = new Label();
        private readonly LinkLabel _root = new LinkLabel();

        private readonly System.Windows.Forms.Timer _debounce = new System.Windows.Forms.Timer { Interval = 200 };
        private bool _fillingDetails, _buildingPreviews;

        /// <summary>Set by the add-in: brings the task pane back if SOLIDWORKS hid it while converting a part.</summary>
        public Action ReShow { get; set; }
        private readonly ToolTip _tips = new ToolTip();

        private Font _fVendor, _fCategory, _fItem;
        private float _s = 1;
        private bool _syncing, _autoSyncDone, _statusBad;

        public LibraryPane(ISldWorks app)
        {
            _app = app;
            BuildUi();

            HandleCreated += (s, e) =>
            {
                ApplyScale();
                ApplyTheme();
                SetCueBanner(_search, "Search parts (kraken, 24t, wcp-0736…)");
                Reload();
                if (Settings.AutoSync && !_autoSyncDone) { _autoSyncDone = true; StartSync(quiet: true); }
            };

            Theme.Changed += OnThemeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        private LibraryItem Selected => _tree.SelectedNode?.Tag as LibraryItem;

        // ================================================================ layout

        private void BuildUi()
        {
            Font = BtrFonts.Ui(10f);

            // --- toolbar: search + Sync + refresh + options
            _search.BorderStyle = BorderStyle.None;
            _search.Font = BtrFonts.Ui(10.5f);
            _searchHost = new FieldHost(_search) { Dock = DockStyle.Fill };
            _search.Multiline = false;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(6, 0, 0, 0), Margin = Padding.Empty };
            foreach (var b in new[] { _sync, _refresh, _more }) buttons.Controls.Add(b);
            _more.Margin = Padding.Empty;

            _toolbar.Dock = DockStyle.Top;
            _toolbar.Padding = new Padding(10, 10, 10, 8);
            _toolbar.Controls.Add(_searchHost);
            _toolbar.Controls.Add(buttons);

            _tips.SetToolTip(_sync, "Download any parts from the team parts list that this computer doesn't have yet");
            _tips.SetToolTip(_refresh, "Rescan the library folder");
            _tips.SetToolTip(_more, "Options");

            // --- options menu
            var auto = new ToolStripMenuItem("Sync automatically on startup") { CheckOnClick = true, Checked = Settings.AutoSync };
            auto.CheckedChanged += (s, e) => Settings.AutoSync = auto.Checked;
            var theme = new ToolStripMenuItem("Theme");
            var themeChoices = new[] { new[] { "Auto (match Windows)", "auto" }, new[] { "Light", "light" }, new[] { "Dark", "dark" } };
            foreach (var choice in themeChoices)
            {
                string value = choice[1];
                var item = new ToolStripMenuItem(choice[0]) { Tag = value };
                item.Click += (s, e) => { Settings.Theme = value; Theme.Refresh(); UpdateThemeChecks(theme); };
                theme.DropDownItems.Add(item);
            }
            UpdateThemeChecks(theme);
            _menu.Items.Add("Choose library folder…", null, (s, e) => ChooseRoot());
            _menu.Items.Add("Set parts list URL…", null, (s, e) => ChooseManifestUrl());
            _menu.Items.Add("Build 3D previews", null, (s, e) => BuildAllPreviews());
            _menu.Items.Add("Export previews for sharing…", null, (s, e) => ExportPreviews());
            _menu.Items.Add(auto);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(theme);
            _menu.Renderer = new ToolStripProfessionalRenderer(new BtrMenuColors()) { RoundedEdges = false };

            // --- tree card
            _tree.Dock = DockStyle.Fill;
            _tree.BorderStyle = BorderStyle.None;
            _tree.HideSelection = false;
            _tree.ShowLines = false;
            _tree.FullRowSelect = true;
            _tree.ShowNodeToolTips = true;
            _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _tree.HandleCreated += (s, e) => Theme.ApplyNativeTheme(_tree);
            _treeCard.Padding = new Padding(6);
            _treeCard.Controls.Add(_tree);

            // --- details card
            var details = _details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 10, 10, 10) };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 160)); // preview; sized by FitDetails
            for (int i = 0; i < 7; i++) details.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _preview.Dock = DockStyle.Fill;
            _preview.Margin = new Padding(0, 0, 0, 8);
            _tips.SetToolTip(_preview, "Drag to rotate, right-drag to pan, wheel to zoom, double-click to reset");

            _title.AutoSize = true;
            _title.Font = BtrFonts.CondBold(14f);
            _title.Margin = new Padding(0);
            _sub.AutoSize = true;
            _sub.Font = BtrFonts.Cond(9f);
            _sub.Margin = new Padding(0, 0, 0, 4);
            _desc.AutoSize = true;
            _desc.Font = BtrFonts.Ui(9.5f);
            _desc.Margin = new Padding(0, 0, 0, 2);
            _url.AutoSize = true;
            _url.Text = "Vendor page";
            _url.Font = BtrFonts.UiMedium(9.5f);
            _url.LinkBehavior = LinkBehavior.HoverUnderline;
            _url.Margin = new Padding(0, 0, 0, 2);

            _config.DropDownStyle = ComboBoxStyle.DropDownList;
            _config.FlatStyle = FlatStyle.Flat;
            _config.DrawMode = DrawMode.OwnerDrawFixed;
            _config.Font = BtrFonts.Ui(10f);
            _config.DrawItem += DrawConfigItem;
            _configHost = new FieldHost(_config) { Dock = DockStyle.Top, Padding = new Padding(4, 3, 4, 2) };
            _configLabel.Dock = DockStyle.Top;

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
            foreach (var b in new[] { _insert, _open, _folder }) { b.Margin = new Padding(0, 0, 6, 6); actions.Controls.Add(b); }
            _tips.SetToolTip(_insert, "Insert into the active assembly (or part). You can also drag a part from the list into the graphics area.");
            _tips.SetToolTip(_open, "Open the library file in its own window");
            _tips.SetToolTip(_folder, "Show the file in Windows Explorer");

            details.Controls.Add(_preview, 0, 0);
            details.Controls.Add(_title, 0, 1);
            details.Controls.Add(_sub, 0, 2);
            details.Controls.Add(_desc, 0, 3);
            details.Controls.Add(_url, 0, 4);
            details.Controls.Add(_configLabel, 0, 5);
            details.Controls.Add(_configHost, 0, 6);
            details.Controls.Add(actions, 0, 7);
            _configLabel.Anchor = _configHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _detailCard.Controls.Add(details);

            // --- split: tree above details
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Horizontal;
            _split.FixedPanel = FixedPanel.Panel2;
            _split.IsSplitterFixed = true; // the details card sizes itself to its content (FitDetails)
            _split.SplitterWidth = 10;
            _split.Panel1.Padding = new Padding(10, 0, 10, 0);
            _split.Panel2.Padding = new Padding(10, 0, 10, 2);
            _treeCard.Dock = DockStyle.Fill;
            _detailCard.Dock = DockStyle.Fill;
            _split.Panel1.Controls.Add(_treeCard);
            _split.Panel2.Controls.Add(_detailCard);

            // --- footer
            var footerRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
            footerRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _status.AutoSize = true;
            _status.Font = BtrFonts.Ui(9.5f);
            _root.AutoSize = true;
            _root.Font = BtrFonts.Cond(9f);
            _root.LinkBehavior = LinkBehavior.HoverUnderline;
            _tips.SetToolTip(_root, "Click to choose the library folder");
            footerRows.Controls.Add(_status, 0, 0);
            footerRows.Controls.Add(_root, 0, 1);
            _footer.Dock = DockStyle.Bottom;
            _footer.AutoSize = true;
            _footer.Padding = new Padding(12, 6, 12, 8);
            _footer.Controls.Add(footerRows);

            // Fill first, then edges (WinForms docks in reverse order).
            Controls.Add(_split);
            Controls.Add(_toolbar);
            Controls.Add(_header);
            Controls.Add(_footer);

            // --- events
            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down && _tree.Nodes.Count > 0) { _tree.Focus(); e.Handled = true; }
                if (e.KeyCode == Keys.Escape) { _search.Clear(); e.Handled = true; }
            };
            _debounce.Tick += (s, e) => { _debounce.Stop(); PopulateTree(_search.Text); };
            _sync.Click += (s, e) => StartSync(quiet: false);
            _refresh.Click += (s, e) => Reload();
            _more.Click += (s, e) => _menu.Show(_more, new Point(_more.Width - _menu.Width, _more.Height));

            _tree.DrawNode += DrawNode;
            _tree.AfterSelect += (s, e) => ShowDetails(e.Node?.Tag as LibraryItem);
            _tree.NodeMouseDoubleClick += (s, e) => { if (e.Node?.Tag is LibraryItem) DoInsert(); };
            _tree.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && Selected != null) { DoInsert(); e.Handled = true; } };
            _tree.ItemDrag += OnItemDrag;
            _preview.ActionClicked += (s, e) => Load3D();
            _config.SelectedIndexChanged += (s, e) => { if (!_fillingDetails) Schedule3D(); };

            _insert.Click += (s, e) => DoInsert();
            _open.Click += (s, e) => { var it = Selected; if (it != null) Run(() => { Inserter.Open(_app, it, CurrentConfig); SetStatus("Opened " + it.Name); }); };
            _folder.Click += (s, e) => { if (Selected != null) Process.Start("explorer.exe", $"/select,\"{Selected.Path}\""); };
            _url.LinkClicked += (s, e) => { if (Selected?.Url != null) Process.Start(Selected.Url); };
            _root.LinkClicked += (s, e) => ChooseRoot();

            Resize += (s, e) => FitDetails();
            ShowDetails(null);
        }

        private static void UpdateThemeChecks(ToolStripMenuItem theme)
        {
            foreach (ToolStripMenuItem i in theme.DropDownItems) i.Checked = (string)i.Tag == Settings.Theme;
        }

        /// <summary>
        /// Sizes the details card to fit its content: a preview that scales with the pane width, then the
        /// (possibly wrapped) title, description and buttons. The tree gets the rest. If the pane is too short,
        /// the preview shrinks first so text is never clipped.
        /// </summary>
        private void FitDetails()
        {
            if (_details == null || !IsHandleCreated || _split.Height <= 0) return;
            int inner = _split.Panel2.ClientSize.Width - _split.Panel2.Padding.Horizontal
                        - _detailCard.Padding.Horizontal - _details.Padding.Horizontal;
            if (inner <= 20) return;

            var wrap = new Size(Math.Max(60, inner), 0);
            _title.MaximumSize = _sub.MaximumSize = _desc.MaximumSize = wrap;

            int minPreview = (int)(70 * _s);
            int previewH = Math.Max((int)(150 * _s), Math.Min((int)(300 * _s), (int)(inner * 0.62)));
            int Needed(int ph)
            {
                _details.RowStyles[0] = new RowStyle(SizeType.Absolute, ph + _preview.Margin.Vertical);
                int content = _details.GetPreferredSize(new Size(inner + _details.Padding.Horizontal, 0)).Height;
                return content + _detailCard.Padding.Vertical + _split.Panel2.Padding.Vertical + 2;
            }

            int minTree = (int)(140 * _s);   // preferred; the tree shrinks below this before any text is clipped
            int floorTree = (int)(60 * _s);
            int available = _split.Height - _split.SplitterWidth - minTree;
            int need = Needed(previewH);
            if (need > available && previewH > minPreview)
            {
                previewH = Math.Max(minPreview, previewH - (need - available));
                need = Needed(previewH);
            }

            try
            {
                _split.Panel1MinSize = 0;
                _split.SplitterDistance = Math.Max(floorTree, _split.Height - _split.SplitterWidth - need);
            }
            catch (InvalidOperationException) { /* not laid out yet */ }
            catch (ArgumentOutOfRangeException) { /* pane collapsed */ }
        }

        // ================================================================ theme

        private void ApplyScale()
        {
#if MONO_CHECK
            using (var g = CreateGraphics()) _s = g.DpiX / 96f;
#else
            _s = DeviceDpi / 96f;
#endif
            _header.UpdateScale(_s);
            foreach (var b in new[] { _sync, _refresh, _more, _insert, _open, _folder }) { b.UiScale = _s; b.FitToText(); }
            _treeCard.UiScale = _detailCard.UiScale = _searchHost.UiScale = _configHost.UiScale = _configLabel.UiScale = _s;
            _tree.ItemHeight = (int)Math.Round(24 * _s);
            int rowH = Math.Max(_sync.Height, _search.PreferredHeight + _searchHost.Padding.Vertical);
            foreach (var b in new[] { _sync, _refresh, _more }) b.Height = rowH;
            _searchHost.Height = rowH;
            _toolbar.Height = rowH + _toolbar.Padding.Vertical;
            _config.ItemHeight = (int)Math.Round(20 * _s);

            _fVendor?.Dispose(); _fCategory?.Dispose(); _fItem?.Dispose();
            _fVendor = BtrFonts.CondBold(11f);
            _fCategory = BtrFonts.Cond(9f);
            _fItem = BtrFonts.Ui(10f);
            
            _tree.Font = _fItem;
            _preview.MessageFont = _fCategory;
            _preview.UiScale = _s;
            FitDetails();
            _preview.Redraw();
        }

        private void ApplyTheme()
        {
            var p = Theme.P;
            BackColor = _toolbar.BackColor = _footer.BackColor = p.Bg;
            _split.BackColor = _split.Panel1.BackColor = _split.Panel2.BackColor = p.Bg;
            foreach (Control c in _footer.Controls) c.BackColor = p.Bg;
            foreach (Control c in _toolbar.Controls.OfType<FlowLayoutPanel>()) c.BackColor = p.Bg;

            _search.BackColor = _config.BackColor = p.Field;
            _search.ForeColor = _config.ForeColor = p.Ink;

            _tree.BackColor = p.Panel;
            _tree.ForeColor = p.Ink;
            _tree.LineColor = p.Line;
            Theme.ApplyNativeTheme(_tree);

            foreach (Control c in _detailCard.Controls) SetPanelBack(c, p.Panel);
            _preview.BackColor = _preview.Plate = p.Plate;
            _preview.Border = p.Line;
            _preview.Ink2 = p.Ink2;
            _preview.Accent = p.Accent;
            _preview.Redraw();
            _title.ForeColor = p.Ink;
            _sub.ForeColor = p.Ink2;
            _desc.ForeColor = p.Ink2;
            foreach (var l in new[] { _url, _root })
            {
                l.LinkColor = p.Blue;
                l.ActiveLinkColor = p.Accent;
                l.VisitedLinkColor = p.Blue;
            }
            _status.ForeColor = _statusBad ? p.Bad : p.Ink2;

            _menu.BackColor = p.Panel;
            ColorMenu(_menu.Items, p);

            Invalidate(true);
        }

        private static void SetPanelBack(Control c, Color back)
        {
            if (c is TableLayoutPanel || c is FlowLayoutPanel || c is Label || c is SectionLabel || c is BtrButton || c is FieldHost)
                c.BackColor = back;
            foreach (Control child in c.Controls)
                if (!(child is ComboBox) && !(child is TextBox) && !(child is PictureBox)) SetPanelBack(child, back);
        }

        private static void ColorMenu(ToolStripItemCollection items, Palette p)
        {
            foreach (ToolStripItem i in items)
            {
                i.ForeColor = p.Ink;
                i.BackColor = p.Panel;
                if (i is ToolStripMenuItem m && m.HasDropDownItems)
                {
                    m.DropDown.BackColor = p.Panel;
                    ColorMenu(m.DropDownItems, p);
                }
            }
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke((Action)ApplyTheme); else ApplyTheme();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General || !IsHandleCreated || IsDisposed) return;
            BeginInvoke((Action)Theme.Refresh);
        }

#if !MONO_CHECK // .NET Framework 4.7+ only; skipped by the Linux compile check
        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyScale();
            Invalidate(true);
        }
#endif

        // ================================================================ owner drawing

        private void DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Bounds.Height <= 0) return;
            var p = Theme.P;
            var g = e.Graphics;
            var node = e.Node;
            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            var item = node.Tag as LibraryItem;

            var row = new Rectangle(e.Bounds.X, e.Bounds.Y, _tree.ClientSize.Width - e.Bounds.X - 2, e.Bounds.Height);
            using (var bg = new SolidBrush(p.Panel)) g.FillRectangle(bg, new Rectangle(0, e.Bounds.Y, _tree.ClientSize.Width, e.Bounds.Height));

            Color ink = p.Ink;
            if (selected)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var sel = new RectangleF(row.X, row.Y + 1, row.Width, row.Height - 2);
                using (var path = Theme.RoundRect(sel, 6 * _s))
                using (var b = new SolidBrush(item != null ? p.Blue : Theme.Mix(p.Panel, p.Accent, p.Dark ? 0.16f : 0.12f)))
                    g.FillPath(b, path);
                if (item != null) ink = Color.White;
                g.SmoothingMode = SmoothingMode.Default;
            }

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int pad = (int)(6 * _s);

            if (node.Level == 0)
            {
                // Vendor: bold condensed uppercase + count
                var name = ((string)node.Name).ToUpperInvariant();
                var r = new Rectangle(row.X + pad, row.Y, row.Width - pad, row.Height);
                TextRenderer.DrawText(g, name, _fVendor, r, ink, flags);
                int w = TextRenderer.MeasureText(g, name, _fVendor, r.Size, flags).Width;
                TextRenderer.DrawText(g, node.Nodes.Cast<TreeNode>().Sum(n => n.Nodes.Count).ToString(), _fCategory,
                    new Rectangle(r.X + w + (int)(4 * _s), r.Y, r.Width - w, r.Height), p.Ink2, flags);
            }
            else if (node.Level == 1)
            {
                // Category: the h2 look — orange square + small uppercase muted text
                int sq = Math.Max(5, (int)Math.Round(7 * _s));
                using (var b = new SolidBrush(p.Orange))
                    g.FillRectangle(b, row.X + pad, row.Y + (row.Height - sq) / 2, sq, sq);
                var r = new Rectangle(row.X + pad + sq + (int)(6 * _s), row.Y, row.Width - pad - sq, row.Height);
                TextRenderer.DrawText(g, node.Text.ToUpperInvariant(), _fCategory, r, selected ? p.Ink : p.Ink2, flags);
            }
            else
            {
                int badgeW = 0;
                var r = new Rectangle(row.X + pad, row.Y, row.Width - pad * 2 - badgeW, row.Height);
                TextRenderer.DrawText(g, node.Text, _fItem, r, ink, flags);
            }
        }

        private void DrawConfigItem(object sender, DrawItemEventArgs e)
        {
            var p = Theme.P;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? p.Blue : p.Field)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index < 0) return;
            var text = _config.Items[e.Index].ToString();
            var ink = sel ? Color.White : (_config.Enabled ? p.Ink : p.Ink2);
            TextRenderer.DrawText(e.Graphics, text, _config.Font, new Rectangle(e.Bounds.X + 3, e.Bounds.Y, e.Bounds.Width - 3, e.Bounds.Height),
                ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // ================================================================ library loading & search

        private void Reload(string doneMessage = null)
        {
            string root = Settings.LibraryRoot;
            _root.Text = "LIBRARY: " + root;

            if (!Directory.Exists(root))
            {
                _items = new List<LibraryItem>();
                PopulateTree("");
                SetStatus("Library folder not found yet. Click Sync to download the team parts, or pick a folder below.");
                return;
            }

            SetStatus("Scanning library…");
            Task.Run(() => LibraryIndex.Scan(root)).ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    if (t.IsFaulted)
                    {
                        SetStatus("Scan failed: " + t.Exception?.GetBaseException().Message, bad: true);
                        return;
                    }
                    _items = t.Result;
                    PopulateTree(_search.Text);
                    int vendors = _items.Select(i => i.Vendor).Distinct().Count();
                    SetStatus(doneMessage ?? $"{_items.Count} parts from {vendors} vendor(s)");
                }));
            });
        }

        private void PopulateTree(string filter)
        {
            var words = (filter ?? "").ToLowerInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var matches = _items.Where(i => words.All(w => i.SearchText.Contains(w))).ToList();
            string keep = Selected?.Path;

            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            TreeNode reselect = null;

            foreach (var vendor in matches.GroupBy(i => i.Vendor).OrderBy(g => g.Key, NaturalComparer.Instance))
            {
                // Node.Name holds the vendor; Text is sized generously for hit-testing the owner-drawn row.
                var vn = _tree.Nodes.Add(vendor.Key, vendor.Key.ToUpperInvariant() + "   " + vendor.Count());

                foreach (var cat in vendor.GroupBy(i => i.Category).OrderBy(g => g.Key, NaturalComparer.Instance))
                {
                    var cn = vn.Nodes.Add(cat.Key);
                    foreach (var item in cat.OrderBy(i => i.Name, NaturalComparer.Instance))
                    {
                        var n = cn.Nodes.Add(item.Name);
                        n.Tag = item;
                        n.ToolTipText = item.Description ?? item.Path;
                        if (item.Path == keep) reselect = n;
                    }
                }
                if (words.Length > 0 || matches.Count <= 60) vn.ExpandAll();
            }

            _tree.EndUpdate();
            if (reselect != null) _tree.SelectedNode = reselect;
            else ShowDetails(null);

            if (words.Length > 0) SetStatus(matches.Count == 0 ? "No parts match that search" : $"{matches.Count} match(es)");
        }

        // ================================================================ details

        private string CurrentConfig => _config.Items.Count > 0 ? _config.Text : "";

        private void ShowDetails(LibraryItem item)
        {
            _preview.Mesh = null;
            _preview.ActionText = null;
            _preview.Fallback = null;
            _thumb?.Dispose();
            _thumb = null;
            _preview.Message = null;
            _fillingDetails = true;
            _config.Items.Clear();
            _fillingDetails = false;

            bool has = item != null;
            _insert.Enabled = _open.Enabled = _folder.Enabled = has;
            _url.Visible = has && !string.IsNullOrEmpty(item.Url);

            if (!has)
            {
                _title.Text = "Pick a part";
                _sub.Text = "SEARCH ABOVE OR BROWSE BY VENDOR";
                _desc.Text = "";
                _config.Enabled = false;
                _configLabel.Visible = _configHost.Visible = false;
                _desc.Visible = false;
                FitDetails();
                return;
            }

            _title.Text = item.Name;
            _sub.Text = $"{item.Vendor} › {item.Category}".ToUpperInvariant()
                        + (item.IsNeutral ? "  ·  STEP" : "");
            _desc.Text = item.Description ?? "";
            _thumb = Thumbnails.Get(item, 256);
            _preview.Fallback = _thumb;

            string[] configs;
            try { configs = Inserter.GetConfigurations(_app, item); }
            catch { configs = new string[0]; }

            if (configs.Length > 0)
            {
                _fillingDetails = true;
                _config.Items.AddRange(configs.OrderBy(c => c, NaturalComparer.Instance).Cast<object>().ToArray());
                _config.SelectedIndex = 0;
                _fillingDetails = false;
            }
            _config.Enabled = configs.Length > 1;
            _configLabel.Visible = _configHost.Visible = configs.Length > 1;
            _desc.Visible = _desc.Text.Length > 0;

            FitDetails();
            Schedule3D();
        }

        /// <summary>
        /// Shows the saved 3D preview if there is one. Otherwise shows the thumbnail with a "Load 3D" button:
        /// building a preview means SOLIDWORKS opens (and for STEP, converts) the part, which is slow and can
        /// make SOLIDWORKS hide the task pane, so it only happens when asked for.
        /// </summary>
        private void Schedule3D()
        {
            var item = Selected;
            if (item == null) return;
            var cached = MeshCache.TryLoad(item, CurrentConfig);
            _preview.Mesh = cached;
            _preview.Message = null;
            _preview.ActionText = cached == null ? "LOAD 3D" : null;
        }

        private void Load3D()
        {
            var item = Selected;
            if (item == null) return;
            _preview.ActionText = null;
            _preview.Message = item.IsNeutral && !Inserter.IsConverted(item) ? "CONVERTING STEP…" : "LOADING 3D…";
            _preview.Refresh(); // paint the caption before SOLIDWORKS blocks the UI thread
            var old = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                var mesh = Inserter.GetPreviewMesh(_app, item, CurrentConfig);
                if (Selected != item) return;
                _preview.Mesh = mesh.Count > 0 ? mesh : null;
                _preview.Message = mesh.Count > 0 ? null : "NO 3D PREVIEW";
            }
            catch (Exception ex)
            {
                if (Selected == item) _preview.Message = "NO 3D PREVIEW";
                SetStatus("3D preview failed: " + ex.Message, bad: true);
            }
            finally
            {
                Cursor.Current = old;
                BringBack();
            }
        }

        /// <summary>Builds every missing 3D preview in one go (one-time cost per computer).</summary>
        private void BuildAllPreviews()
        {
            if (_buildingPreviews) return;
            var todo = _items.Where(i => !MeshCache.Has(i, DefaultConfig(i))).ToList();
            if (todo.Count == 0) { SetStatus("All 3D previews are already built."); return; }

            if (MessageBox.Show(this,
                    $"Build 3D previews for {todo.Count} part(s)?\n\nSOLIDWORKS will open (and convert STEP files for) each part in the " +
                    "background. This can take a while and SOLIDWORKS will be busy until it finishes. It only needs doing once per computer.",
                    "BTR Library", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            _buildingPreviews = true;
            var old = Cursor.Current;
            int built = 0;
            var failed = new List<string>();
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                for (int n = 0; n < todo.Count; n++)
                {
                    var item = todo[n];
                    SetStatus($"Building 3D previews… {n + 1}/{todo.Count}: {item.Name}");
                    _status.Refresh();
                    try
                    {
                        if (Inserter.GetPreviewMesh(_app, item, DefaultConfig(item)).Count > 0) built++;
                        else failed.Add(item.Name);
                    }
                    catch (Exception ex) { failed.Add(item.Name + " – " + ex.Message); }
                }
            }
            finally
            {
                Cursor.Current = old;
                _buildingPreviews = false;
                BringBack();
            }

            SetStatus($"Built {built} 3D preview(s)" + (failed.Count > 0 ? $", {failed.Count} failed" : ""), bad: failed.Count > 0);
            Schedule3D();
            if (failed.Count > 0)
                MessageBox.Show(this, "No 3D preview for:\n\n" + string.Join("\n", failed.Take(15)),
                    "BTR Library", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// Copies the built previews into a folder to publish next to library.json (as "previews/"), so Sync
        /// hands them to everyone else. Files are named by part contents, so they match on any PC.
        /// </summary>
        private void ExportPreviews()
        {
            var have = new List<string>();
            int missing = 0;
            foreach (var item in _items)
            {
                string file;
                try { file = MeshCache.PathFor(item, DefaultConfig(item)); }
                catch { continue; }
                if (File.Exists(file)) have.Add(file); else missing++;
            }
            if (have.Count == 0)
            {
                MessageBox.Show(this, "There are no 3D previews to export yet. Run ⋯ › Build 3D previews first.",
                    "BTR Library", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (missing > 0 && MessageBox.Show(this,
                    $"{missing} part(s) don't have a 3D preview yet, so teammates won't get one for them.\n\n" +
                    "Export the other " + have.Count + " anyway? (Cancel, then ⋯ › Build 3D previews, to include everything.)",
                    "BTR Library", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose the \"previews\" folder next to library.json (e.g. manifest\\previews in the btr-library repo).";
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                int copied = 0;
                foreach (var f in have)
                {
                    var dst = Path.Combine(dlg.SelectedPath, Path.GetFileName(f));
                    if (File.Exists(dst)) continue;
                    File.Copy(f, dst);
                    copied++;
                }
                SetStatus($"Exported {copied} new 3D preview(s) ({have.Count - copied} were already there).");
                MessageBox.Show(this,
                    $"Exported {copied} new preview(s) to:\n{dlg.SelectedPath}\n\n" +
                    "Commit that folder next to library.json. Teammates get the previews on their next Sync.",
                    "BTR Library", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>The configuration the details card picks by default (first in natural order), or "".</summary>
        private string DefaultConfig(LibraryItem item)
        {
            string[] configs;
            try { configs = Inserter.GetConfigurations(_app, item); }
            catch { return ""; }
            return configs.OrderBy(c => c, NaturalComparer.Instance).FirstOrDefault() ?? "";
        }

        /// <summary>SOLIDWORKS may hide the task pane while it opens a part; show it again once it settles.</summary>
        private void BringBack()
        {
            if (ReShow == null || !IsHandleCreated) return;
            BeginInvoke((Action)(() => { try { ReShow(); } catch { /* cosmetic */ } }));
        }

        // ================================================================ actions

        private void DoInsert()
        {
            var item = Selected;
            if (item == null) return;
            Run(() => SetStatus(Inserter.Insert(_app, item, CurrentConfig)));
        }

        /// <summary>Dragging a file into the SOLIDWORKS graphics area inserts it with interactive placement,
        /// the same as dragging from Windows Explorer. (Drag uses the file's default configuration.)</summary>
        private void OnItemDrag(object sender, ItemDragEventArgs e)
        {
            if (!((e.Item as TreeNode)?.Tag is LibraryItem item)) return;
            string path = null;
            Run(() => path = Inserter.EnsureNative(_app, item));
            if (path == null) return;
            DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
        }

        private void StartSync(bool quiet)
        {
            if (_syncing) return;
            string root = Settings.LibraryRoot;
            _syncing = true;
            _sync.Enabled = false;

            var progress = new Progress<string>(m =>
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => SetStatus(m)));
            });

            Task.Run(() => LibrarySync.RunAsync(root, progress, CancellationToken.None)).ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    _syncing = false;
                    _sync.Enabled = true;

                    if (t.IsFaulted)
                    {
                        var msg = t.Exception?.GetBaseException().Message;
                        SetStatus("Sync failed: " + msg, bad: true);
                        if (!quiet) MessageBox.Show(this, "Sync failed:\n\n" + msg, "BTR Library", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var r = t.Result;
                    Reload(r.ToString());
                    if (r.Failures.Count > 0 && !quiet)
                        MessageBox.Show(this, "Some parts couldn't be downloaded:\n\n" + string.Join("\n", r.Failures.Take(15)),
                            "BTR Library", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            });
        }

        private void ChooseManifestUrl()
        {
            string url = Microsoft.VisualBasic.Interaction.InputBox(
                "URL of a custom parts list (library.json), e.g. a raw GitHub link.\n\nLeave empty to use the team list from the ButlerTechRobotics/btr-library repo on GitHub.",
                "BTR Library – parts list", Settings.ManifestUrl);
            if (url == null) return;
            Settings.ManifestUrl = url.Trim();
            StartSync(quiet: false);
        }

        private void ChooseRoot()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose the BTR Library folder (Vendor\\Category\\Part files). A shared team drive works well.";
                dlg.SelectedPath = Directory.Exists(Settings.LibraryRoot) ? Settings.LibraryRoot : "";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Settings.LibraryRoot = dlg.SelectedPath;
                Reload();
            }
        }

        private void Run(Action action)
        {
            var old = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                action();
            }
            catch (InvalidOperationException ex)
            {
                SetStatus(ex.Message, bad: true);
                MessageBox.Show(this, ex.Message, "BTR Library", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message, bad: true);
                MessageBox.Show(this, ex.ToString(), "BTR Library", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Cursor.Current = old;
            }
        }

        private void SetStatus(string text, bool bad = false)
        {
            _statusBad = bad;
            _status.Text = text;
            _status.ForeColor = bad ? Theme.P.Bad : Theme.P.Ink2;
            _status.MaximumSize = new Size(Math.Max(100, Width - (int)(24 * _s)), 0);
        }

        // Placeholder text in the search box.
        private const int EM_SETCUEBANNER = 0x1501;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        private static void SetCueBanner(TextBox box, string text)
        {
            try { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text); } catch { /* cosmetic only */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= OnThemeChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                _debounce.Dispose();
                _tips.Dispose();
                _menu.Dispose();
                _thumb?.Dispose();
                _fVendor?.Dispose(); _fCategory?.Dispose(); _fItem?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
