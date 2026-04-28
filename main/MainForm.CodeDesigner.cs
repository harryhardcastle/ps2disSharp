using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PS2Disassembler
{
    public sealed partial class MainForm
    {
        private CodeDesignerWorkspace EnsureCodeDesignerWorkspace()
        {
            if (_codeDesignerWorkspace != null && !_codeDesignerWorkspace.IsDisposed)
                return _codeDesignerWorkspace;

            if (_codeDesignerPage == null)
                throw new InvalidOperationException("Code Designer page has not been created.");

            _codeDesignerWorkspace = new CodeDesignerWorkspace();
            _codeDesignerWorkspace.Dock = DockStyle.Fill;
            _codeDesignerPage.Controls.Clear();
            _codeDesignerPage.Controls.Add(_codeDesignerWorkspace);
            _codeDesignerWorkspace.ApplyTheme(_currentTheme == AppTheme.Dark);
            ApplyScrollbarTheme(_codeDesignerWorkspace, _currentTheme == AppTheme.Dark);
            return _codeDesignerWorkspace;
        }
    }

    internal sealed class CodeDesignerWorkspace : UserControl
    {
        private const int ToolbarButtonGap = 4;
        private const int ToolbarEdgePadding = 4;

        private readonly Panel _toolbarHost;
        private readonly FlowLayoutPanel _toolbar;
        private readonly Button _btnNew;
        private readonly Button _btnOpen;
        private readonly Button _btnSave;
        private readonly Button _btnSaveAs;
        private readonly Panel _toolbarSpacer;
        private readonly Button _btnCompile;
        private readonly Panel _copyButtonHost;
        private readonly Button _btnCopy;
        private readonly CodeDesignerModeRadioButton _rbPs2;
        private readonly CodeDesignerModeRadioButton _rbPnach;
        private readonly MainForm.FlatComboBox _cmbFontSize;
        private readonly TextBox _txtFormat;
        private readonly FlowLayoutPanel _tabStrip;
        private readonly Panel _contentHost;
        private readonly List<CodeDesignerTab> _tabs = new();
        private CodeDesignerTab? _activeTab;
        private bool _dark;

        public bool OutputPnach => _rbPnach.Checked;
        public string FormatPrefix => string.IsNullOrWhiteSpace(_txtFormat.Text) ? "-" : _txtFormat.Text.Trim()[0].ToString();
        public int EditorFontSize => _cmbFontSize.SelectedItem is int i ? i : 10;

        public CodeDesignerWorkspace()
        {
            Dock = DockStyle.Fill;
            Padding = Padding.Empty;
            Margin = Padding.Empty;

            _toolbarHost = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(0, 0, ToolbarEdgePadding, 0),
                Margin = Padding.Empty,
            };

            _toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(ToolbarEdgePadding, 4, 0, 2),
                Margin = Padding.Empty,
            };

            _btnNew = MakeToolbarButton("New");
            _btnOpen = MakeToolbarButton("Open");
            _btnSave = MakeToolbarButton("Save");
            _btnSaveAs = MakeToolbarButton("Save As", 72);
            _toolbarSpacer = new Panel { Width = 1, Height = 1, Margin = Padding.Empty };
            _btnCompile = MakeToolbarButton("COMPILE", 78);
            _btnCopy = MakeToolbarButton("COPY", 62);
            _btnCopy.Margin = Padding.Empty;
            // Wrap COPY in a non-painting host that reserves trailing whitespace.
            // FlowLayoutPanel can clip the final child's outer margin at the edge,
            // which made the COPY button itself appear cut off. Keeping the gap
            // inside a host control lets any edge clipping hit empty space instead
            // of the button rectangle.
            _copyButtonHost = new Panel
            {
                Width = _btnCopy.Width + ToolbarEdgePadding + ToolbarButtonGap,
                Height = _btnCopy.Height,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };
            _copyButtonHost.Controls.Add(_btnCopy);
            _btnNew.Click += (_, _) => NewTab();
            _btnOpen.Click += (_, _) => OpenIntoNewTab();
            _btnSave.Click += (_, _) => ActiveInstance?.Save();
            _btnSaveAs.Click += (_, _) => ActiveInstance?.SaveAs();
            _btnCompile.Click += (_, _) => ActiveInstance?.Compile();
            _btnCopy.Click += (_, _) => ActiveInstance?.CopyOutput();

            _rbPs2 = new CodeDesignerModeRadioButton { Text = "PS2", AutoSize = true, Checked = true, Margin = new Padding(10, 7, 4, 0) };
            _rbPnach = new CodeDesignerModeRadioButton { Text = "pnach", AutoSize = true, Margin = new Padding(3, 7, 8, 0) };
            _cmbFontSize = new MainForm.FlatComboBox
            {
                Width = 56,
                Height = 20,
                ItemHeight = 16,
                Margin = new Padding(4, 6, 8, 0),
            };
            for (int i = 5; i <= 36; i++) _cmbFontSize.Items.Add(i);
            _cmbFontSize.SelectedItem = 10;
            _cmbFontSize.SelectedIndexChanged += (_, _) =>
            {
                foreach (var tab in _tabs)
                    tab.Instance.SetEditorFontSize(EditorFontSize);
            };

            _txtFormat = new TextBox
            {
                Width = 28,
                MaxLength = 1,
                Text = "-",
                TextAlign = HorizontalAlignment.Center,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(4, 5, 4, 0),
            };
            _toolbar.Controls.AddRange(new Control[]
            {
                _btnNew, _btnOpen, _btnSave, _btnSaveAs,
                _toolbarSpacer,
                _rbPs2, _rbPnach,
                new Label { Text = "Font Size:", AutoSize = true, Margin = new Padding(4, 8, 2, 0) },
                _cmbFontSize,
                new Label { Text = "Format:", AutoSize = true, Margin = new Padding(4, 8, 2, 0) },
                _txtFormat,
                _btnCompile,
                _copyButtonHost,
            });

            _tabStrip = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 28,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(4, 0, 4, 0),
                Margin = Padding.Empty,
            };
            _toolbar.SizeChanged += (_, _) => ReflowToolbar();
            _tabStrip.SizeChanged += (_, _) => ReflowTabs();

            _contentHost = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty };

            _toolbarHost.Controls.Add(_toolbar);

            Controls.Add(_contentHost);
            Controls.Add(_tabStrip);
            Controls.Add(_toolbarHost);

            ReflowToolbar();
            ReflowTabs();
            UpdateEmptyState();
        }

        private void ReflowToolbar()
        {
            if (_toolbarSpacer == null || _toolbarSpacer.IsDisposed || _toolbar == null || _toolbar.IsDisposed)
                return;

            int leftWidth = 0;
            int rightWidth = 0;
            bool afterSpacer = false;
            foreach (Control c in _toolbar.Controls)
            {
                if (ReferenceEquals(c, _toolbarSpacer))
                {
                    afterSpacer = true;
                    continue;
                }

                int w = GetFlowControlWidth(c);
                if (afterSpacer)
                    rightWidth += w;
                else
                    leftWidth += w;
            }

            int available = Math.Max(0, _toolbar.ClientSize.Width - _toolbar.Padding.Horizontal - leftWidth - rightWidth);
            if (_toolbarSpacer.Width != available)
            {
                _toolbarSpacer.Width = available;
                _toolbarSpacer.Height = 1;
            }
        }

        private static int GetFlowControlWidth(Control c)
        {
            int width = c.AutoSize ? c.PreferredSize.Width : c.Width;
            return Math.Max(0, width) + c.Margin.Horizontal;
        }

        private void ReflowTabs()
        {
            int count = _tabs.Count;
            if (count <= 0 || _tabStrip == null || _tabStrip.IsDisposed)
                return;

            int available = Math.Max(0, _tabStrip.ClientSize.Width - _tabStrip.Padding.Horizontal - (count * 3));
            int width = Math.Min(150, Math.Max(1, available / count));
            foreach (var tab in _tabs)
                tab.SetHeaderWidth(width);
        }

        private CodeDesignerInstance? ActiveInstance => _activeTab?.Instance;

        private Button MakeToolbarButton(string text, int width = 62)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, ToolbarButtonGap, 0),
                Padding = Padding.Empty,
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false,
            };
        }

        private void NewTab(string? filePath = null, string? text = null)
        {
            var instance = new CodeDesignerInstance(this);
            instance.Dock = DockStyle.Fill;
            instance.SetEditorFontSize(EditorFontSize);
            if (!string.IsNullOrEmpty(text))
                instance.SetText(text);
            if (!string.IsNullOrWhiteSpace(filePath))
                instance.SetFilePath(filePath);

            var tab = new CodeDesignerTab(instance, string.IsNullOrWhiteSpace(filePath) ? "Untitled" : Path.GetFileName(filePath));
            tab.SelectRequested += (_, _) => SelectTab(tab);
            tab.CloseRequested += (_, _) => CloseTab(tab);
            _tabs.Add(tab);
            _tabStrip.Controls.Add(tab.Header);
            ReflowTabs();
            // Apply theme before activating the tab so opening a file can run one
            // full-document highlight pass after theme colors are set.
            ApplyTheme(_dark);
            SelectTab(tab);
        }

        private void OpenIntoNewTab()
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Assembly Files (*.txt;*.cds;*.asm;*.s)|*.txt;*.cds;*.asm;*.s|All files (*.*)|*.*",
                Title = "Open Code Designer File"
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK)
                return;

            string text = File.ReadAllText(dlg.FileName, Encoding.Latin1);
            NewTab(dlg.FileName, text);
        }

        private void CloseTab(CodeDesignerTab tab)
        {
            if (!_tabs.Contains(tab)) return;
            tab.Instance.SetActive(false);
            _tabs.Remove(tab);
            _tabStrip.Controls.Remove(tab.Header);
            if (_contentHost.Controls.Contains(tab.Instance))
                _contentHost.Controls.Remove(tab.Instance);
            tab.Instance.Dispose();
            tab.Header.Dispose();

            ReflowTabs();

            if (_tabs.Count == 0)
            {
                _activeTab = null;
                UpdateEmptyState();
            }
            else if (ReferenceEquals(_activeTab, tab))
            {
                SelectTab(_tabs[Math.Max(0, _tabs.Count - 1)]);
            }
        }

        private void SelectTab(CodeDesignerTab tab)
        {
            if (!_tabs.Contains(tab)) return;
            if (_activeTab != null && !ReferenceEquals(_activeTab, tab))
                _activeTab.Instance.SetActive(false);
            _activeTab = tab;
            _contentHost.Controls.Clear();
            _contentHost.Controls.Add(tab.Instance);
            foreach (var t in _tabs)
                t.SetSelected(ReferenceEquals(t, tab), _dark);
            tab.Instance.SetActive(true);
            tab.Instance.FocusInput();
        }

        internal void RenameTab(CodeDesignerInstance instance, string title)
        {
            var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Instance, instance));
            if (tab == null) return;
            tab.Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title;
            ReflowTabs();
        }

        public void ApplyTheme(bool dark)
        {
            _dark = dark;
            Color back = dark ? Color.FromArgb(36, 39, 44) : Color.FromArgb(228, 228, 228);
            Color panel = dark ? Color.FromArgb(44, 48, 54) : Color.White;
            Color fore = dark ? Color.FromArgb(238, 238, 238) : Color.Black;
            // Code Designer toolbar controls should stand out from the light theme surface.
            Color button = dark ? Color.FromArgb(70, 74, 80) : Color.FromArgb(250, 250, 250);
            Color hover = dark ? Color.FromArgb(84, 88, 96) : Color.FromArgb(255, 255, 255);

            BackColor = back;
            ForeColor = fore;
            _toolbarHost.BackColor = back;
            _toolbar.BackColor = back;
            _tabStrip.BackColor = back;
            _contentHost.BackColor = panel;
            _rbPs2.ApplyPalette(dark, back, fore);
            _rbPnach.ApplyPalette(dark, back, fore);
            _cmbFontSize.BackColor = button;
            _cmbFontSize.ForeColor = fore;
            _txtFormat.BackColor = button;
            _txtFormat.ForeColor = fore;

            foreach (Control c in _toolbar.Controls)
            {
                if (c is CodeDesignerModeRadioButton rb)
                {
                    rb.ApplyPalette(dark, back, fore);
                }
                else if (c is Label lbl)
                {
                    lbl.BackColor = back;
                    lbl.ForeColor = fore;
                }
                else if (c is Button b)
                {
                    b.BackColor = button;
                    b.ForeColor = fore;
                    b.FlatAppearance.BorderColor = dark ? Color.FromArgb(90, 96, 105) : Color.FromArgb(190, 194, 202);
                    b.FlatAppearance.MouseOverBackColor = hover;
                    b.FlatAppearance.MouseDownBackColor = hover;
                }
                else if (c is Panel pnl)
                {
                    pnl.BackColor = back;
                }
            }

            ApplySpecialToolbarButtonColors();
            _cmbFontSize.Invalidate();

            foreach (var tab in _tabs)
            {
                tab.SetSelected(ReferenceEquals(tab, _activeTab), dark);
                tab.Instance.ApplyTheme(dark);
            }

            ReflowToolbar();
            ReflowTabs();

            if (_tabs.Count == 0)
                UpdateEmptyState();
        }


        private void ApplySpecialToolbarButtonColors()
        {
            Color compileBlue = Color.FromArgb(88, 101, 196);
            Color compileBlueHover = Color.FromArgb(71, 82, 196);
            Color copyGreen = Color.FromArgb(67, 141, 100);
            Color copyGreenHover = Color.FromArgb(57, 154, 110);

            _btnCompile.BackColor = compileBlue;
            _btnCompile.ForeColor = Color.White;
            _btnCompile.FlatAppearance.BorderSize = 0;
            _btnCompile.FlatAppearance.MouseOverBackColor = compileBlueHover;
            _btnCompile.FlatAppearance.MouseDownBackColor = compileBlueHover;

            _btnCopy.BackColor = copyGreen;
            _btnCopy.ForeColor = Color.White;
            _btnCopy.FlatAppearance.BorderSize = 0;
            _btnCopy.FlatAppearance.MouseOverBackColor = copyGreenHover;
            _btnCopy.FlatAppearance.MouseDownBackColor = copyGreenHover;
        }

        private void DrawThemeComboItem(ComboBox combo, DrawItemEventArgs e)
        {
            if (e.Index < 0)
                return;

            Color back = _dark ? Color.FromArgb(70, 74, 80) : Color.FromArgb(232, 232, 232);
            Color selected = _dark ? Color.FromArgb(84, 88, 96) : Color.FromArgb(220, 230, 245);
            Color fore = _dark ? Color.FromArgb(238, 238, 238) : Color.Black;
            bool isSelected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(isSelected ? selected : back))
                e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? string.Empty, combo.Font,
                e.Bounds, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void UpdateEmptyState()
        {
            _contentHost.Controls.Clear();
            if (_tabs.Count != 0)
                return;

            Color back = _dark ? Color.FromArgb(44, 48, 54) : Color.White;
            Color fore = _dark ? Color.FromArgb(185, 187, 190) : Color.FromArgb(80, 80, 80);
            var label = new Label
            {
                Text = "Click New or Open to begin.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = back,
                ForeColor = fore,
            };
            _contentHost.Controls.Add(label);
        }

        private sealed class CodeDesignerModeRadioButton : RadioButton
        {
            private bool _dark;
            private Color _surfaceBack = Color.Transparent;
            private Color _textFore = Color.Black;

            public CodeDesignerModeRadioButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                AutoSize = true;
                UseVisualStyleBackColor = false;
                FlatStyle = FlatStyle.Flat;
            }

            public void ApplyPalette(bool dark, Color surfaceBack, Color textFore)
            {
                _dark = dark;
                _surfaceBack = surfaceBack;
                _textFore = textFore;
                BackColor = surfaceBack;
                ForeColor = textFore;
                if (AutoSize)
                    Size = GetPreferredSize(Size.Empty);
                Invalidate();
            }

            public override Size GetPreferredSize(Size proposedSize)
            {
                Size textSize = TextRenderer.MeasureText(Text ?? string.Empty, Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                return new Size(13 + 6 + textSize.Width + Padding.Horizontal + 2,
                    Math.Max(13, textSize.Height) + Padding.Vertical);
            }

            protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
            {
                if (AutoSize)
                {
                    Size preferred = GetPreferredSize(Size.Empty);
                    width = preferred.Width;
                    height = preferred.Height;
                }
                base.SetBoundsCore(x, y, width, height, specified);
            }

            protected override void OnCheckedChanged(EventArgs e)
            {
                base.OnCheckedChanged(e);
                Invalidate();
            }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                if (AutoSize)
                    Size = GetPreferredSize(Size.Empty);
                Invalidate();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                if (AutoSize)
                    Size = GetPreferredSize(Size.Empty);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Color surface = _surfaceBack.IsEmpty ? BackColor : _surfaceBack;
                Color text = _textFore.IsEmpty ? ForeColor : _textFore;
                e.Graphics.Clear(surface);

                bool enabled = Enabled;
                Color border = _dark ? Color.FromArgb(104, 110, 120) : Color.FromArgb(150, 156, 166);
                Color fill = _dark ? Color.FromArgb(36, 39, 44) : Color.FromArgb(250, 250, 250);
                Color dot = _dark ? Color.FromArgb(238, 238, 238) : Color.FromArgb(46, 84, 140);
                if (!enabled)
                {
                    text = Color.FromArgb(132, 136, 144);
                    border = _dark ? Color.FromArgb(74, 80, 88) : Color.FromArgb(178, 182, 188);
                    dot = Color.FromArgb(144, 148, 156);
                }

                int circleSize = 12;
                int circleY = Math.Max(0, (ClientSize.Height - circleSize) / 2);
                var circle = new Rectangle(0, circleY, circleSize, circleSize);
                using (var fillBrush = new SolidBrush(fill))
                    e.Graphics.FillEllipse(fillBrush, circle);
                using (var borderPen = new Pen(border))
                    e.Graphics.DrawEllipse(borderPen, circle);

                if (Checked)
                {
                    var dotRect = new Rectangle(circle.Left + 3, circle.Top + 3, circle.Width - 6, circle.Height - 6);
                    using var dotBrush = new SolidBrush(dot);
                    e.Graphics.FillEllipse(dotBrush, dotRect);
                }

                Rectangle textRect = new Rectangle(circle.Right + 6, 0, Math.Max(0, ClientSize.Width - (circle.Right + 6)), ClientSize.Height);
                TextRenderer.DrawText(e.Graphics, Text ?? string.Empty, Font, textRect, text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                if (Focused && ShowFocusCues)
                {
                    Rectangle focusRect = textRect;
                    focusRect.Width = Math.Max(0, Math.Min(textRect.Width, TextRenderer.MeasureText(Text ?? string.Empty, Font).Width));
                    focusRect.Inflate(1, -2);
                    ControlPaint.DrawFocusRectangle(e.Graphics, focusRect, text, surface);
                }
            }
        }

        private sealed class CodeDesignerTab
        {
            private readonly Label _titleLabel;
            private readonly Button _closeButton;
            private string _fullTitle;
            public Panel Header { get; }
            public CodeDesignerInstance Instance { get; }
            public event EventHandler? SelectRequested;
            public event EventHandler? CloseRequested;

            public string Title
            {
                get => _fullTitle;
                set
                {
                    _fullTitle = string.IsNullOrWhiteSpace(value) ? "Untitled" : value;
                    UpdateDisplayedTitle();
                }
            }

            public CodeDesignerTab(CodeDesignerInstance instance, string title)
            {
                Instance = instance;
                _fullTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title;
                Header = new Panel { Width = 150, Height = 26, Margin = new Padding(0, 0, 3, 0), Padding = Padding.Empty };
                _titleLabel = new Label
                {
                    Text = _fullTitle,
                    AutoEllipsis = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Location = new Point(8, 0),
                    Size = new Size(118, 26),
                };
                _closeButton = new Button
                {
                    Text = "×",
                    Location = new Point(126, 3),
                    Size = new Size(21, 20),
                    FlatStyle = FlatStyle.Flat,
                    TabStop = false,
                };
                _closeButton.FlatAppearance.BorderSize = 0;
                Header.Controls.Add(_titleLabel);
                Header.Controls.Add(_closeButton);
                Header.Click += (_, _) => SelectRequested?.Invoke(this, EventArgs.Empty);
                _titleLabel.Click += (_, _) => SelectRequested?.Invoke(this, EventArgs.Empty);
                _closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
                SetHeaderWidth(150);
            }

            public void SetHeaderWidth(int width)
            {
                width = Math.Max(1, width);
                Header.Width = width;
                _closeButton.Visible = width >= 34;
                int closeW = _closeButton.Visible ? _closeButton.Width + 4 : 0;
                _closeButton.Location = new Point(Math.Max(2, width - _closeButton.Width - 3), 3);
                _titleLabel.Location = new Point(8, 0);
                _titleLabel.Size = new Size(Math.Max(0, width - 10 - closeW), 26);
                UpdateDisplayedTitle();
            }

            private void UpdateDisplayedTitle()
            {
                string full = _fullTitle ?? string.Empty;
                if (_titleLabel.Width <= 4 || string.IsNullOrEmpty(full))
                {
                    _titleLabel.Text = string.Empty;
                    return;
                }

                Size fullSize = TextRenderer.MeasureText(full, _titleLabel.Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                if (fullSize.Width <= _titleLabel.Width)
                {
                    _titleLabel.Text = full;
                    return;
                }

                string suffix = "..";
                int suffixWidth = TextRenderer.MeasureText(suffix, _titleLabel.Font).Width;
                int available = Math.Max(0, _titleLabel.Width - suffixWidth);
                int keep = full.Length;
                while (keep > 0)
                {
                    string prefix = full.Substring(0, keep);
                    int width = TextRenderer.MeasureText(prefix, _titleLabel.Font, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                    if (width <= available)
                    {
                        _titleLabel.Text = prefix + suffix;
                        return;
                    }
                    keep--;
                }
                _titleLabel.Text = suffix;
            }

            public void SetSelected(bool selected, bool dark)
            {
                // 25% brighter than the previous dark selected tab color (54,58,64).
                Color back = selected
                    ? (dark ? Color.FromArgb(68, 73, 80) : Color.White)
                    : (dark ? Color.FromArgb(42, 46, 52) : Color.FromArgb(215, 215, 215));
                Color fore = dark ? Color.FromArgb(238, 238, 238) : Color.Black;
                Header.BackColor = back;
                _titleLabel.BackColor = back;
                _titleLabel.ForeColor = fore;
                _closeButton.BackColor = back;
                _closeButton.ForeColor = fore;
                UpdateDisplayedTitle();
            }
        }
    }

    internal sealed class CodeDesignerInstance : UserControl
    {
        private readonly CodeDesignerWorkspace _workspace;
        private readonly RichTextBox _input;
        private readonly RichTextBox _output;
        private readonly Panel _inputFrame;
        private readonly Panel _outputFrame;
        private readonly TableLayoutPanel _inputLayout;
        private readonly CodeDesignerLineNumberPanel _lineNumbers;
        private readonly Label _outputLines;
        private string? _filePath;
        private readonly System.Windows.Forms.Timer _syntaxTimer;
        private bool _syntaxPending;
        private bool _applyingSyntax;
        private bool _darkTheme;
        private int _pendingSyntaxStart;
        private int _pendingSyntaxLength;
        private int _lastInputLength;
        private bool _pendingVisibleOnly;
        private bool _isActive;
        private bool _needsFullSyntaxOnActivate;
        private int _lastKnownFirstVisibleLine;
        private readonly HashSet<int> _compileErrorLines = new();
        private bool _applyingCompileErrorStyle;

        public CodeDesignerInstance(CodeDesignerWorkspace workspace)
        {
            _workspace = workspace;
            Dock = DockStyle.Fill;
            Padding = Padding.Empty;
            Margin = Padding.Empty;

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72f));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28f));

            var left = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty };
            var inputLabel = new Label { Text = "INPUT", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
            _inputFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = Padding.Empty };
            _inputLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
            };
            _inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54f));
            _inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _inputLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _input = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 10f),
                AcceptsTab = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                DetectUrls = false,
            };
            _lineNumbers = new CodeDesignerLineNumberPanel(_input) { Dock = DockStyle.Fill, Width = 54 };
            _inputLayout.Controls.Add(_lineNumbers, 0, 0);
            _inputLayout.Controls.Add(_input, 1, 0);
            _inputFrame.Controls.Add(_inputLayout);
            left.Controls.Add(_inputFrame);
            left.Controls.Add(inputLabel);

            var right = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty };
            var outputHeader = new Panel { Dock = DockStyle.Top, Height = 22 };
            var outputLabel = new Label { Text = "OUTPUT", Dock = DockStyle.Left, Width = 80, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
            _outputLines = new Label { Text = "LINES: 0", Dock = DockStyle.Right, Width = 88, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, 4, 0) };
            outputHeader.Controls.Add(_outputLines);
            outputHeader.Controls.Add(outputLabel);
            _outputFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), Margin = Padding.Empty };
            _output = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                DetectUrls = false,
            };
            _input.ContextMenuStrip = BuildEditorContextMenu(_input, allowPaste: true);
            _output.ContextMenuStrip = BuildEditorContextMenu(_output, allowPaste: false);
            _outputFrame.Controls.Add(_output);

            _syntaxTimer = new System.Windows.Forms.Timer { Interval = 35 };
            _syntaxTimer.Tick += (_, _) =>
            {
                _syntaxTimer.Stop();
                _syntaxTimer.Interval = 35;
                if (_syntaxPending)
                    ApplySyntaxHighlighting();
            };
            _input.TextChanged += (_, _) =>
            {
                if (_applyingSyntax || _applyingCompileErrorStyle) return;
                ClearCompileErrorForCurrentLine();
                QueueChangedLineSyntax();
                _lastInputLength = _input.TextLength;
                UpdateLineNumberWidth();
            };
            _input.VScroll += (_, _) =>
            {
                _lastKnownFirstVisibleLine = GetFirstVisibleInputLine();
                _lineNumbers.Invalidate();
            };
            _input.Resize += (_, _) => { _lineNumbers.Invalidate(); };
            _input.HandleCreated += (_, _) =>
            {
                ApplyEditorScrollbarTheme();
                if (_needsFullSyntaxOnActivate || (_syntaxPending && !_pendingVisibleOnly))
                    QueueFullSyntax(immediate: false);
                else if (_isActive)
                    QueueVisibleSyntax();
            };
            _output.HandleCreated += (_, _) => ApplyEditorScrollbarTheme();

            right.Controls.Add(_outputFrame);
            right.Controls.Add(outputHeader);

            main.Controls.Add(left, 0, 0);
            main.Controls.Add(right, 1, 0);
            Controls.Add(main);
        }

        private ContextMenuStrip BuildEditorContextMenu(RichTextBox box, bool allowPaste)
        {
            var menu = new ContextMenuStrip();
            var copy = new ToolStripMenuItem("Copy", null, (_, _) => { if (box.SelectionLength > 0) box.Copy(); });
            menu.Items.Add(copy);
            ToolStripMenuItem? paste = null;
            if (allowPaste)
            {
                paste = new ToolStripMenuItem("Paste", null, (_, _) => { if (Clipboard.ContainsText()) box.Paste(); });
                menu.Items.Add(paste);
            }
            menu.Opening += (_, _) =>
            {
                copy.Enabled = box.SelectionLength > 0;
                if (paste != null)
                    paste.Enabled = Clipboard.ContainsText();
            };
            return menu;
        }

        public void FocusInput() => _input.Focus();

        public void SetActive(bool active)
        {
            if (_isActive == active)
                return;
            _isActive = active;
            if (active)
            {
                if (_needsFullSyntaxOnActivate)
                {
                    _needsFullSyntaxOnActivate = false;
                    QueueFullSyntax(immediate: true);
                }
                else
                {
                    QueueVisibleSyntax();
                }
            }
        }

        public void CopyOutput()
        {
            if (!string.IsNullOrEmpty(_output.Text))
                Clipboard.SetText(_output.Text);
        }
        public void SetText(string text)
        {
            ClearAllCompileErrorHighlights(recolorAfterClear: false);
            _input.Text = text ?? string.Empty;
            _lastInputLength = _input.TextLength;
            UpdateLineNumberWidth();
            _needsFullSyntaxOnActivate = true;
            if (_isActive)
            {
                _needsFullSyntaxOnActivate = false;
                QueueFullSyntax(immediate: _input.IsHandleCreated);
            }
        }
        public void SetFilePath(string path)
        {
            _filePath = path;
            _workspace.RenameTab(this, Path.GetFileName(path));
        }

        public void SetEditorFontSize(int size)
        {
            if (size < 5) size = 5;
            if (size > 36) size = 36;
            _input.Font = new Font(_input.Font.FontFamily, size, FontStyle.Regular);
            _output.Font = new Font(_output.Font.FontFamily, Math.Max(5, size - 1), FontStyle.Regular);
            _lineNumbers.Font = new Font(_input.Font.FontFamily, Math.Max(5, size - 1), FontStyle.Regular);
            UpdateLineNumberWidth();

            // Changing the RichTextBox font can reset existing selection colors
            // back to the control ForeColor. Re-apply the full syntax pass for
            // the active document, and defer inactive documents until selected.
            if (_isActive)
            {
                _needsFullSyntaxOnActivate = false;
                QueueFullSyntax(immediate: _input.IsHandleCreated);
            }
            else
            {
                _needsFullSyntaxOnActivate = true;
            }
        }

        public void ApplyTheme(bool dark)
        {
            _darkTheme = dark;
            Color panel = dark ? Color.FromArgb(44, 48, 54) : Color.White;
            Color edit = dark ? Color.FromArgb(35, 39, 42) : Color.White;
            Color frame = dark ? Color.FromArgb(70, 74, 80) : Color.FromArgb(210, 214, 220);
            Color margin = dark ? Color.FromArgb(48, 51, 56) : Color.FromArgb(238, 240, 244);
            Color fore = dark ? Color.FromArgb(238, 238, 238) : Color.Black;
            Color secondary = dark ? Color.FromArgb(185, 187, 190) : Color.FromArgb(80, 80, 80);
            Color button = dark ? Color.FromArgb(70, 74, 80) : Color.FromArgb(232, 232, 232);
            Color hover = dark ? Color.FromArgb(84, 88, 96) : Color.FromArgb(220, 230, 245);
            BackColor = panel;
            _inputFrame.BackColor = frame;
            _outputFrame.BackColor = frame;
            _lineNumbers.BackColor = margin;
            _lineNumbers.ForeColor = secondary;
            foreach (Control c in GetAllControls(this))
            {
                if (c is Label lbl)
                {
                    lbl.BackColor = panel;
                    lbl.ForeColor = secondary;
                }
                else if (c is RichTextBox rtb)
                {
                    rtb.BackColor = edit;
                    rtb.ForeColor = fore;
                }
                else if (c is Button b)
                {
                    b.BackColor = button;
                    b.ForeColor = fore;
                    b.FlatAppearance.BorderColor = dark ? Color.FromArgb(90, 96, 105) : Color.FromArgb(170, 170, 170);
                    b.FlatAppearance.MouseOverBackColor = hover;
                    b.FlatAppearance.MouseDownBackColor = hover;
                }
                else if (c is Panel || c is TableLayoutPanel)
                {
                    if (!ReferenceEquals(c, _inputFrame) && !ReferenceEquals(c, _outputFrame))
                        c.BackColor = panel;
                    c.ForeColor = fore;
                }
            }

            ApplyEditorScrollbarTheme();
            UpdateLineNumberWidth();

            // Applying a theme resets RichTextBox selection colors back to the
            // control ForeColor. Recolor the full active document so Code
            // Designer syntax colors survive theme changes; defer inactive tabs
            // until they are selected so hidden documents do not cause lag.
            if (_isActive)
            {
                _needsFullSyntaxOnActivate = false;
                QueueFullSyntax(immediate: false);
            }
            else
            {
                _needsFullSyntaxOnActivate = true;
            }
        }

        public void ApplyEditorScrollbarTheme()
        {
            string theme = _darkTheme ? "DarkMode_Explorer" : "";
            ApplyScrollbarThemeToControl(_input, theme);
            ApplyScrollbarThemeToControl(_output, theme);
        }

        private static void ApplyScrollbarThemeToControl(Control control, string theme)
        {
            if (control.IsDisposed || !control.IsHandleCreated) return;
            try
            {
                NativeMethods.SetWindowTheme(control.Handle, theme, null);
                NativeMethods.SetWindowPos(control.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                control.Invalidate();
            }
            catch { }
        }

        private void UpdateLineNumberWidth()
        {
            if (_inputLayout.ColumnStyles.Count == 0) return;
            int lineCount = GetInputLineCountFast();
            int digits = Math.Max(2, lineCount.ToString(CultureInfo.InvariantCulture).Length);
            int textWidth = TextRenderer.MeasureText(new string('9', digits), _lineNumbers.Font).Width;
            int width = Math.Max(42, textWidth + 18);
            if (Math.Abs(_inputLayout.ColumnStyles[0].Width - width) < 1f) return;
            _inputLayout.ColumnStyles[0].Width = width;
            _lineNumbers.Width = width;
            _lineNumbers.Invalidate();
        }

        private void ScheduleSyntaxHighlight(bool immediate = false)
        {
            QueueVisibleSyntax(immediate);
        }

        private void QueueChangedLineSyntax()
        {
            if (!_isActive) return;
            int delta = Math.Abs(_input.TextLength - _lastInputLength);
            if (delta > 4096)
            {
                QueueVisibleSyntax();
                return;
            }

            int caret = Math.Max(0, Math.Min(_input.SelectionStart, _input.TextLength));
            int line = _input.GetLineFromCharIndex(caret);
            QueueLineRangeSyntax(Math.Max(0, line - 1), line + 2);
        }

        private void QueueVisibleSyntax(bool immediate = false, bool scrollDelay = false)
        {
            if (!_isActive) return;
            // Do not let theme/activation/scroll refreshes replace a pending full-file
            // highlight pass. Opening a file intentionally colors the whole document once.
            if (_syntaxPending && !_pendingVisibleOnly)
                return;
            GetVisibleSyntaxRange(out int start, out int length);
            _pendingSyntaxStart = start;
            _pendingSyntaxLength = length;
            _pendingVisibleOnly = true;
            _syntaxPending = true;
            if (immediate)
            {
                _syntaxTimer.Stop();
                ApplySyntaxHighlighting();
            }
            else
            {
                _syntaxTimer.Stop();
                _syntaxTimer.Interval = scrollDelay ? 60 : 35;
                _syntaxTimer.Start();
            }
        }

        private void QueueFullSyntax(bool immediate = false)
        {
            if (!_isActive) return;
            _pendingSyntaxStart = 0;
            _pendingSyntaxLength = _input.TextLength;
            _pendingVisibleOnly = false;
            _syntaxPending = true;
            if (immediate && _input.IsHandleCreated)
            {
                _syntaxTimer.Stop();
                ApplySyntaxHighlighting();
            }
            else
            {
                _syntaxTimer.Stop();
                _syntaxTimer.Interval = _input.IsHandleCreated ? 35 : 75;
                _syntaxTimer.Start();
            }
        }

        private void QueueLineRangeSyntax(int firstLine, int lastLine)
        {
            if (!_isActive) return;
            int lineCount = GetInputLineCountFast();
            firstLine = Math.Max(0, Math.Min(firstLine, lineCount - 1));
            lastLine = Math.Max(firstLine, Math.Min(lastLine, lineCount - 1));

            int start = _input.GetFirstCharIndexFromLine(firstLine);
            if (start < 0) start = 0;
            int end = GetLineEndIndex(lastLine);

            _pendingSyntaxStart = start;
            _pendingSyntaxLength = Math.Max(0, end - start);
            _pendingVisibleOnly = false;
            _syntaxPending = true;
            _syntaxTimer.Stop();
            _syntaxTimer.Start();
        }

        private int GetFirstVisibleInputLine()
        {
            if (_input.IsDisposed || !_input.IsHandleCreated) return _lastKnownFirstVisibleLine;
            try
            {
                return NativeMethods.SendMessage(_input.Handle, (int)NativeMethods.EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            }
            catch { return _lastKnownFirstVisibleLine; }
        }

        private void GetVisibleSyntaxRange(out int start, out int length)
        {
            if (_input.TextLength == 0)
            {
                start = 0;
                length = 0;
                return;
            }

            int firstChar = _input.GetCharIndexFromPosition(new Point(0, 0));
            int lastChar = _input.GetCharIndexFromPosition(new Point(Math.Max(0, _input.ClientSize.Width - 1), Math.Max(0, _input.ClientSize.Height - 1)));
            int lineCount = GetInputLineCountFast();
            int firstLine = Math.Max(0, _input.GetLineFromCharIndex(firstChar) - 1);
            int lastLine = Math.Min(Math.Max(firstLine, _input.GetLineFromCharIndex(lastChar) + 1), Math.Max(0, lineCount - 1));
            int lineStart = _input.GetFirstCharIndexFromLine(firstLine);
            if (lineStart < 0) lineStart = 0;
            int end = GetLineEndIndex(lastLine);
            start = lineStart;
            length = Math.Max(0, end - start);
        }

        private int GetInputLineCountFast()
        {
            if (_input.TextLength == 0) return 1;
            return _input.GetLineFromCharIndex(Math.Max(0, _input.TextLength - 1)) + 1;
        }

        private int GetLineEndIndex(int line)
        {
            int nextLineStart = _input.GetFirstCharIndexFromLine(line + 1);
            if (nextLineStart >= 0)
                return Math.Min(_input.TextLength, nextLineStart);

            return _input.TextLength;
        }


        private void ApplySyntaxHighlighting()
        {
            if (!_isActive || _applyingSyntax || _input.IsDisposed) return;
            if (!_input.IsHandleCreated)
            {
                // Keep the requested range pending until the RichTextBox can actually
                // accept SelectionColor changes. This is especially important for
                // open-file full-document coloring during initial tab creation.
                _syntaxTimer.Stop();
                _syntaxTimer.Interval = 75;
                _syntaxTimer.Start();
                return;
            }

            _syntaxPending = false;
            _applyingSyntax = true;

            try
            {
                if (_pendingVisibleOnly)
                    GetVisibleSyntaxRange(out _pendingSyntaxStart, out _pendingSyntaxLength);
                int appliedStart = _pendingSyntaxStart;
                int appliedLength = _pendingSyntaxLength;
                CodeDesignerSyntaxHighlighter.ApplyRange(_input, _darkTheme, appliedStart, appliedLength);
                ReapplyCompileErrorHighlights(appliedStart, appliedLength);
                _lineNumbers.Invalidate();
            }
            finally
            {
                _applyingSyntax = false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _syntaxTimer.Dispose();
            base.Dispose(disposing);
        }

        private IEnumerable<Control> GetAllControls(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control grandChild in GetAllControls(child))
                    yield return grandChild;
            }
        }

        private void ClearDocument()
        {
            if (_input.TextLength > 0)
            {
                var result = MessageBox.Show(FindForm(), "Start a new file? Unsaved changes will be lost.", "New File Confirmation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes) return;
            }
            _input.Clear();
            _output.Clear();
            ScheduleSyntaxHighlight(immediate: true);
            _filePath = null;
            _workspace.RenameTab(this, "Untitled");
            UpdateLineCount();
        }

        private void OpenIntoThisTab()
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Assembly Files (*.txt;*.cds;*.asm;*.s)|*.txt;*.cds;*.asm;*.s|All files (*.*)|*.*",
                Title = "Open Code Designer File"
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            SetText(File.ReadAllText(dlg.FileName, Encoding.Latin1));
            SetFilePath(dlg.FileName);
            _output.Clear();
            UpdateLineCount();
        }

        public void Save()
        {
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                SaveAs();
                return;
            }
            File.WriteAllText(_filePath, _input.Text, Encoding.Latin1);
            _workspace.RenameTab(this, Path.GetFileName(_filePath));
        }

        public void SaveAs()
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "CDS File (*.cds)|*.cds|Text File (*.txt)|*.txt|Assembly Files (*.asm;*.s)|*.asm;*.s|All files (*.*)|*.*",
                FileName = string.IsNullOrWhiteSpace(_filePath) ? "untitled.cds" : Path.GetFileName(_filePath),
                Title = "Save Code Designer File"
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            _filePath = dlg.FileName;
            Save();
        }

        public void Compile()
        {
            ClearAllCompileErrorHighlights(recolorAfterClear: true);
            var compiler = new SimpleCodeDesignerCompiler(_input.Text, _filePath, _workspace.OutputPnach, _workspace.FormatPrefix);
            string result = compiler.Compile();
            _output.Text = result;
            UpdateLineCount();
            ApplyCompileErrorHighlightsFromOutput(result);
        }

        private void ApplyCompileErrorHighlightsFromOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output) || output.IndexOf("Errors", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            string? currentFile = string.IsNullOrWhiteSpace(_filePath) ? null : Path.GetFileName(_filePath);
            foreach (Match match in Regex.Matches(output, @"^\s*(\d+)\s+([^:\r\n]+):", RegexOptions.Multiline))
            {
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oneBasedLine))
                    continue;

                string diagnosticFile = match.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(currentFile) &&
                    !diagnosticFile.Equals(currentFile, StringComparison.OrdinalIgnoreCase))
                    continue;

                int zeroBasedLine = oneBasedLine - 1;
                if (zeroBasedLine < 0 || zeroBasedLine >= GetInputLineCountFast())
                    continue;

                _compileErrorLines.Add(zeroBasedLine);
            }

            ReapplyCompileErrorHighlights(0, _input.TextLength);
        }

        private void ClearCompileErrorForCurrentLine()
        {
            if (_compileErrorLines.Count == 0 || _input.IsDisposed)
                return;

            int line = _input.GetLineFromCharIndex(Math.Max(0, Math.Min(_input.SelectionStart, _input.TextLength)));
            if (!_compileErrorLines.Remove(line))
                return;

            ClearCompileErrorLineStyle(line);
            QueueLineRangeSyntax(line, line);
        }

        private void ClearAllCompileErrorHighlights(bool recolorAfterClear)
        {
            if (_compileErrorLines.Count == 0)
                return;

            var oldLines = _compileErrorLines.ToArray();
            _compileErrorLines.Clear();
            foreach (int line in oldLines)
                ClearCompileErrorLineStyle(line);

            if (recolorAfterClear && _isActive)
                QueueFullSyntax(immediate: false);
        }

        private void ClearCompileErrorLineStyle(int line)
        {
            if (!_input.IsHandleCreated || _input.TextLength == 0)
                return;

            if (!TryGetInputLineRange(line, out int start, out int length))
                return;

            int selectionStart = _input.SelectionStart;
            int selectionLength = _input.SelectionLength;
            Color normalFore = _darkTheme ? Color.FromArgb(220, 221, 222) : Color.Black;
            _applyingCompileErrorStyle = true;
            try
            {
                _input.Select(start, length);
                _input.SelectionBackColor = _input.BackColor;
                _input.SelectionColor = normalFore;
            }
            finally
            {
                if (selectionStart <= _input.TextLength)
                    _input.Select(selectionStart, Math.Min(selectionLength, _input.TextLength - selectionStart));
                _applyingCompileErrorStyle = false;
            }
        }

        private void ReapplyCompileErrorHighlights(int rangeStart, int rangeLength)
        {
            if (_compileErrorLines.Count == 0 || !_input.IsHandleCreated || _input.TextLength == 0)
                return;

            int rangeEnd = rangeStart + Math.Max(0, rangeLength);
            int selectionStart = _input.SelectionStart;
            int selectionLength = _input.SelectionLength;
            _applyingCompileErrorStyle = true;
            try
            {
                foreach (int line in _compileErrorLines.ToArray())
                {
                    if (!TryGetInputLineRange(line, out int start, out int length))
                        continue;

                    int end = start + length;
                    if (end < rangeStart || start > rangeEnd)
                        continue;

                    _input.Select(start, length);
                    _input.SelectionBackColor = Color.FromArgb(190, 32, 32);
                    _input.SelectionColor = Color.White;
                }
            }
            finally
            {
                if (selectionStart <= _input.TextLength)
                    _input.Select(selectionStart, Math.Min(selectionLength, _input.TextLength - selectionStart));
                _applyingCompileErrorStyle = false;
            }
        }

        private bool TryGetInputLineRange(int line, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (line < 0 || _input.TextLength == 0)
                return false;

            start = _input.GetFirstCharIndexFromLine(line);
            if (start < 0 || start > _input.TextLength)
                return false;

            int next = _input.GetFirstCharIndexFromLine(line + 1);
            int end = next >= 0 ? next : _input.TextLength;
            length = Math.Max(0, end - start);
            return length > 0;
        }

        private void UpdateLineCount()
        {
            string text = _output.Text;
            if (string.IsNullOrEmpty(text))
            {
                _outputLines.Text = "LINES: 0";
                return;
            }

            int count = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') count++;
            _outputLines.Text = $"LINES: {count}";
        }
    }


    internal sealed class CodeDesignerLineNumberPanel : Control
    {
        private readonly RichTextBox _editor;

        public CodeDesignerLineNumberPanel(RichTextBox editor)
        {
            _editor = editor;
            DoubleBuffered = true;
            Font = new Font("Consolas", 9f);
            editor.TextChanged += (_, _) => Invalidate();
            editor.VScroll += (_, _) => Invalidate();
            editor.Resize += (_, _) => Invalidate();
            editor.FontChanged += (_, _) => { Font = new Font(editor.Font.FontFamily, Math.Max(5, editor.Font.Size - 1), FontStyle.Regular); Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            if (_editor.IsDisposed || _editor.TextLength == 0)
                return;

            int firstChar = _editor.GetCharIndexFromPosition(new Point(0, 0));
            int firstLine = Math.Max(0, _editor.GetLineFromCharIndex(firstChar));
            int lastChar = _editor.GetCharIndexFromPosition(new Point(0, Math.Max(0, _editor.ClientSize.Height - 1)));
            int lastLine = Math.Max(firstLine, _editor.GetLineFromCharIndex(lastChar) + 1);
            using var brush = new SolidBrush(ForeColor);
            using var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Near };

            for (int line = firstLine; line <= lastLine; line++)
            {
                int charIndex = _editor.GetFirstCharIndexFromLine(line);
                if (charIndex < 0) continue;
                Point p = _editor.GetPositionFromCharIndex(charIndex);
                if (p.Y > Height) break;
                e.Graphics.DrawString((line + 1).ToString(CultureInfo.InvariantCulture), Font, brush, new RectangleF(0, p.Y, Width - 6, Font.Height), format);
            }
        }
    }

    internal static class CodeDesignerSyntaxHighlighter
    {
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        private const int EM_LINESCROLL = 0x00B6;
        private const int EM_GETSCROLLPOS = 0x04DD;
        private const int EM_SETSCROLLPOS = 0x04DE;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);

        private static readonly string[] Instructions =
        {
            "addiu","sw","jal","nop","lw","jr","addu","add","sub","and","or","xor","nor","slt","sltu","subu",
            "addi","andi","ori","xori","lui","slti","sltiu","beq","bne","beql","bnel","b",
            "sll","srl","sra","sllv","srlv","srav",
            "mult","multu","div","divu","mfhi","mflo","mthi","mtlo",
            "lb","lbu","lh","lhu","sb","sh","sd","ld","lwu","lq","sq",
            "jalr","bltz","bgez","blez","bgtz","bltzal","bgezal",
            "syscall","break","sync","eret","mfc0","mtc0","dmfc0","dmtc0",
            "lwc1","swc1","mfc1","mtc1","add.s","sub.s","mul.s","div.s","abs.s","neg.s","mov.s","sqrt.s",
            "c.eq.s","c.lt.s","c.le.s","bc1t","bc1f","cvt.s.w","cvt.w.s",
            "ldc1","sdc1","add.d","sub.d","mul.d","div.d","abs.d","neg.d","cvt.s.d","cvt.d.s","cvt.w.d","cvt.d.w","cvt.l.s","cvt.l.d",
            "setreg","dadd","daddi","daddiu","dsub","dsubu","dsll","dsrl","dsra","dsllv","dsrlv","dsrav","dsll32","dsrl32","dsra32","dmult","dmultu","ddiv","ddivu"
        };

        private static readonly string[] Directives =
        {
            "address","print","import","hexcode","float",".word",".byte",".half",".ascii",".asciiz",".space",".text",".data",".globl",".align"
        };

        public static void Apply(RichTextBox box, bool dark)
        {
            ApplyRange(box, dark, 0, box.TextLength);
        }

        public static void ApplyRange(RichTextBox box, bool dark, int start, int length)
        {
            if (box.IsDisposed || !box.IsHandleCreated || box.TextLength == 0 || length <= 0)
                return;

            start = Math.Max(0, Math.Min(start, box.TextLength));
            length = Math.Max(0, Math.Min(length, box.TextLength - start));
            if (length <= 0) return;

            string fullText = box.Text;
            string text = fullText.Substring(start, length);
            int selectionStart = box.SelectionStart;
            int selectionLength = box.SelectionLength;
            POINT scrollPoint = GetScrollPosition(box);
            Color defaultFore = dark ? Color.FromArgb(220, 221, 222) : Color.Black;

            SendMessage(box.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                box.Select(start, length);
                box.SelectionColor = defaultFore;

                ApplyRegex(box, text, start, @"^\s*([\w.:]+):", dark ? Color.FromArgb(211, 176, 110) : Color.FromArgb(128, 0, 0), RegexOptions.Multiline, group: 1, includeColon: true);
                ApplyRegex(box, text, start, @"\$([0-9a-fA-F]+)\b", dark ? Color.FromArgb(212, 212, 212) : Color.FromArgb(100, 70, 0), RegexOptions.IgnoreCase);
                ApplyRegex(box, text, start, @"\b(0x[0-9a-fA-F]+)\b", dark ? defaultFore : Color.FromArgb(100, 70, 0), RegexOptions.IgnoreCase);
                ApplyRegex(box, text, start, @"(?<![\w.:$])(\-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b", dark ? defaultFore : Color.FromArgb(100, 70, 0), RegexOptions.IgnoreCase);

                ApplyKeywordRegex(box, text, start, Directives, token =>
                {
                    if (token.Equals("print", StringComparison.OrdinalIgnoreCase) || token.Equals("import", StringComparison.OrdinalIgnoreCase))
                        return dark ? Color.FromArgb(180, 120, 210) : Color.FromArgb(100, 0, 120);
                    if (token.Equals("hexcode", StringComparison.OrdinalIgnoreCase) || token.Equals("float", StringComparison.OrdinalIgnoreCase))
                        return dark ? Color.FromArgb(173, 216, 230) : Color.FromArgb(0, 95, 145);
                    return dark ? Color.FromArgb(100, 220, 100) : Color.FromArgb(0, 128, 0);
                });

                ApplyKeywordRegex(box, text, start, Instructions, _ => dark ? Color.FromArgb(220, 221, 222) : Color.FromArgb(0, 0, 176));
                ApplyRegisters(box, text, start, dark);
                ApplyRegex(box, text, start, @"^\s*(?:hexcode|float)\s+([\w.:$0-9a-fA-FxX+-]+)", defaultFore, RegexOptions.IgnoreCase | RegexOptions.Multiline, group: 1);

                ApplyRegex(box, text, start, @"""(?:\\.|[^""\\])*""", dark ? defaultFore : Color.FromArgb(160, 70, 0), RegexOptions.None);
                ApplyComments(box, text, start, dark ? Color.FromArgb(142, 146, 151) : Color.FromArgb(56, 91, 56));

                if (selectionStart <= box.TextLength)
                    box.Select(selectionStart, Math.Min(selectionLength, box.TextLength - selectionStart));
                RestoreScrollPosition(box, scrollPoint);
            }
            finally
            {
                SendMessage(box.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                box.Invalidate();
            }
        }

        private static POINT GetScrollPosition(RichTextBox box)
        {
            POINT p = new POINT();
            if (box.IsDisposed || !box.IsHandleCreated) return p;
            try { SendMessage(box.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p); }
            catch { }
            return p;
        }

        private static void RestoreScrollPosition(RichTextBox box, POINT p)
        {
            if (box.IsDisposed || !box.IsHandleCreated) return;
            try { SendMessage(box.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p); }
            catch { }
        }

        private static int GetFirstVisibleLine(RichTextBox box)
        {
            if (box.IsDisposed || !box.IsHandleCreated) return 0;
            try { return SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32(); }
            catch { return 0; }
        }

        private static void RestoreFirstVisibleLine(RichTextBox box, int targetLine)
        {
            if (box.IsDisposed || !box.IsHandleCreated) return;
            try
            {
                int currentLine = GetFirstVisibleLine(box);
                int delta = targetLine - currentLine;
                if (delta != 0)
                    SendMessage(box.Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(delta));
            }
            catch { }
        }

        private static void ApplyKeywordRegex(RichTextBox box, string text, int baseOffset, IEnumerable<string> keywords, Func<string, Color> getColor)
        {
            string pattern = @"(?<![\w.])(" + string.Join("|", keywords.Select(Regex.Escape).OrderByDescending(k => k.Length)) + @")(?![\w.])";
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                SetColor(box, baseOffset + m.Index, m.Length, getColor(m.Value));
        }

        private static void ApplyRegisters(RichTextBox box, string text, int baseOffset, bool dark)
        {
            string regPattern = @"(?<![\w.$:])\$?((?:zero|at|v[01]|a[0-3]|t[0-9]|s[0-7]|f\d{1,2}|k[01]|gp|sp|fp|ra))(?![\w.])";
            foreach (Match m in Regex.Matches(text, regPattern, RegexOptions.IgnoreCase))
            {
                string reg = m.Groups[1].Value.ToLowerInvariant();
                SetColor(box, baseOffset + m.Index, m.Length, GetRegisterColor(reg, dark));
            }
        }

        private static Color GetRegisterColor(string reg, bool dark)
        {
            Color c;
            if (reg == "zero") c = Color.FromArgb(128, 128, 128);
            else if (reg == "at") c = Color.FromArgb(173, 216, 230);
            else if (reg.StartsWith("v")) c = Color.FromArgb(250, 119, 109);
            else if (reg.StartsWith("a")) c = Color.FromArgb(80, 167, 238);
            else if (reg.StartsWith("t")) c = Color.FromArgb(230, 170, 220);
            else if (reg.StartsWith("s")) c = Color.FromArgb(241, 196, 15);
            else if (reg.StartsWith("f")) c = Color.FromArgb(119, 221, 119);
            else if (reg.StartsWith("k")) c = Color.FromArgb(155, 89, 182);
            else if (reg == "gp") c = Color.FromArgb(46, 204, 113);
            else if (reg == "sp") c = Color.FromArgb(245, 171, 53);
            else if (reg == "fp") c = Color.FromArgb(220, 220, 170);
            else if (reg == "ra") c = Color.FromArgb(231, 76, 60);
            else c = Color.FromArgb(79, 193, 255);

            return dark ? c : Scale(c, 0.455f);
        }

        private static Color Scale(Color c, float factor)
            => Color.FromArgb(Clamp((int)(c.R * factor)), Clamp((int)(c.G * factor)), Clamp((int)(c.B * factor)));

        private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

        private static void ApplyComments(RichTextBox box, string text, int baseOffset, Color color)
        {
            foreach (Match m in Regex.Matches(text, @"//.*?$", RegexOptions.Multiline))
                SetColor(box, baseOffset + m.Index, m.Length, color);
            foreach (Match m in Regex.Matches(text, @"#.*?$", RegexOptions.Multiline))
                SetColor(box, baseOffset + m.Index, m.Length, color);
            foreach (Match m in Regex.Matches(text, @"/\*.*?\*/", RegexOptions.Singleline))
                SetColor(box, baseOffset + m.Index, m.Length, color);
        }

        private static void ApplyRegex(RichTextBox box, string text, int baseOffset, string pattern, Color color, RegexOptions options, int group = 0, bool includeColon = false)
        {
            foreach (Match m in Regex.Matches(text, pattern, options))
            {
                Group g = group == 0 ? m.Groups[0] : m.Groups[group];
                int len = g.Length + (includeColon && g.Index + g.Length < text.Length && text[g.Index + g.Length] == ':' ? 1 : 0);
                SetColor(box, baseOffset + g.Index, len, color);
            }
        }

        private static void SetColor(RichTextBox box, int start, int length, Color color)
        {
            if (length <= 0 || start < 0 || start >= box.TextLength) return;
            if (start + length > box.TextLength) length = box.TextLength - start;
            box.Select(start, length);
            box.SelectionColor = color;
        }
    }

    internal sealed class SimpleCodeDesignerCompiler
    {
        private readonly string[] _lines;
        private readonly HashSet<string> _importStack = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _importedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string? _filePath;
        private readonly bool _pnach;
        private readonly string _formatPrefix;
        private readonly Dictionary<string, uint> _labels = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _errors = new();

        private sealed class SourceLine
        {
            public string Text = string.Empty;
            public string FileName = "main_input.asm";
            public int LineNumber;
        }

        public SimpleCodeDesignerCompiler(string text, string? filePath, bool pnach, string formatPrefix)
            : this(SplitLinesFast(text), filePath, pnach, formatPrefix)
        {
        }

        public SimpleCodeDesignerCompiler(string[] lines, string? filePath, bool pnach, string formatPrefix)
        {
            _lines = lines ?? Array.Empty<string>();
            _filePath = filePath;
            _pnach = pnach;
            _formatPrefix = string.IsNullOrWhiteSpace(formatPrefix) ? "-" : formatPrefix.Trim()[0].ToString();
        }

        private static string[] SplitLinesFast(string? text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        public string Compile()
        {
            _errors.Clear();
            _labels.Clear();
            _importStack.Clear();
            _importedFiles.Clear();
            string rootFile = _filePath ?? Path.Combine(Directory.GetCurrentDirectory(), "main_input.asm");
            string rootDir = Path.GetDirectoryName(rootFile) ?? Directory.GetCurrentDirectory();
            var source = PreprocessImports(_lines, rootFile, rootDir, 0);
            uint pc = 0;
            bool inBlock = false;

            foreach (var line in source)
            {
                string text = StripComments(line.Text, ref inBlock);
                if (text.Length == 0) continue;
                if (HandleAddress(text, ref pc)) continue;
                string remaining = ExtractLabel(text, pc, line, addErrorOnDuplicate: true);
                if (remaining.Length == 0) continue;
                pc += GetLineSize(remaining);
            }

            if (_errors.Count > 0)
                return "Errors (Pass 1):\r\n" + string.Join("\r\n", _errors);

            var output = new List<string>();
            pc = 0;
            inBlock = false;
            foreach (var line in source)
            {
                try
                {
                    string text = StripComments(line.Text, ref inBlock);
                    if (text.Length == 0) continue;
                    if (HandleAddress(text, ref pc)) continue;
                    text = ExtractLabel(text, pc, line, addErrorOnDuplicate: false);
                    if (text.Length == 0) continue;
                    EmitLine(text, line, ref pc, output);
                }
                catch (Exception ex)
                {
                    _errors.Add($"{line.LineNumber} {Path.GetFileName(line.FileName)}: {ex.Message}");
                }
            }

            if (_errors.Count > 0)
                return "Errors (Pass 2):\r\n" + string.Join("\r\n", _errors);
            return output.Count == 0 ? "No input provided." : string.Join("\r\n", output);
        }

        private List<SourceLine> PreprocessImports(string[] lines, string fileName, string baseDir, int depth)
        {
            var result = new List<SourceLine>(lines.Length);
            if (depth > 16)
            {
                _errors.Add($"1 {Path.GetFileName(fileName)}: Maximum import depth exceeded. Check for circular imports.");
                return result;
            }

            string normalizedFile = Path.GetFullPath(string.IsNullOrWhiteSpace(fileName) ? "main_input.asm" : fileName);
            bool hasRealFile = File.Exists(normalizedFile);
            if (hasRealFile)
            {
                if (!_importStack.Add(normalizedFile))
                {
                    _errors.Add($"1 {Path.GetFileName(fileName)}: Circular import skipped: {Path.GetFileName(normalizedFile)}");
                    return result;
                }
                if (depth > 0 && !_importedFiles.Add(normalizedFile))
                    return result;
            }

            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i] ?? string.Empty;
                    var m = Regex.Match(line.Trim(), @"^import\s+(?:""([^""]+)""|([^\s;#]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (m.Success)
                    {
                        string importName = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                        string full = ResolveImportPath(importName, baseDir);
                        if (File.Exists(full))
                        {
                            string normalizedImport = Path.GetFullPath(full);
                            if (_importStack.Contains(normalizedImport) || _importedFiles.Contains(normalizedImport))
                                continue;
                            string[] imported = File.ReadAllLines(normalizedImport, Encoding.Latin1);
                            result.AddRange(PreprocessImports(imported, normalizedImport, Path.GetDirectoryName(normalizedImport) ?? baseDir, depth + 1));
                        }
                        else
                        {
                            _errors.Add($"{i + 1} {Path.GetFileName(fileName)}: Import not found: {importName}");
                        }
                    }
                    else
                    {
                        result.Add(new SourceLine { Text = line, FileName = fileName, LineNumber = i + 1 });
                    }
                }
            }
            finally
            {
                if (hasRealFile)
                    _importStack.Remove(normalizedFile);
            }

            return result;
        }

        private string ResolveImportPath(string importName, string baseDir)
        {
            string path = importName.Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(path))
                return path;

            string fromBase = Path.Combine(baseDir, path);
            if (File.Exists(fromBase))
                return fromBase;

            if (!string.IsNullOrWhiteSpace(_filePath))
            {
                string? fileDir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(fileDir))
                {
                    string fromFile = Path.Combine(fileDir, path);
                    if (File.Exists(fromFile))
                        return fromFile;
                }
            }

            return Path.Combine(Directory.GetCurrentDirectory(), path);
        }


        private string StripComments(string line, ref bool inBlock)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < line.Length)
            {
                if (inBlock)
                {
                    int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0) break;
                    inBlock = false;
                    i = end + 2;
                    continue;
                }

                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    inBlock = true;
                    i += 2;
                    continue;
                }
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/') break;
                if (line[i] == '#' && !IsInsideString(line, i)) break;
                sb.Append(line[i]);
                i++;
            }
            return sb.ToString().Trim();
        }

        private bool IsInsideString(string line, int index)
        {
            bool inside = false;
            for (int i = 0; i < index; i++)
            {
                if (line[i] == '\\') { i++; continue; }
                if (line[i] == '"') inside = !inside;
            }
            return inside;
        }

        private bool HandleAddress(string text, ref uint pc)
        {
            if (!text.StartsWith("address", StringComparison.OrdinalIgnoreCase)) return false;
            string[] parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) pc = ParseNumber(parts[1]);
            return true;
        }

        private string ExtractLabel(string text, uint pc, SourceLine line, bool addErrorOnDuplicate)
        {
            // Code Designer sources commonly use either "Label:" / ":Label:" or a
            // standalone leading-colon label like ":Label".  The latter is also the
            // operand style used by branches (b :Label), so support it only at the
            // start of a source line.
            var m = Regex.Match(text, "^([\\w.:]+):");
            int consumed = 0;
            string label = string.Empty;

            if (m.Success)
            {
                label = CleanLabelName(m.Groups[1].Value);
                consumed = m.Length;
            }
            else
            {
                var leadingColon = Regex.Match(text, "^:([A-Za-z_][\\w.]*)\\b");
                if (!leadingColon.Success) return text;
                label = CleanLabelName(leadingColon.Groups[1].Value);
                consumed = leadingColon.Length;
            }

            if (label.Length == 0) return text.Substring(consumed).Trim();

            if (addErrorOnDuplicate)
            {
                if (_labels.ContainsKey(label))
                    _errors.Add($"{line.LineNumber} {Path.GetFileName(line.FileName)}: Duplicate label '{label}'");
                else
                    _labels[label] = pc;
            }
            return text.Substring(consumed).Trim();
        }

        private static string CleanLabelName(string text)
        {
            return (text ?? string.Empty).Trim().Trim(':');
        }

        private uint GetLineSize(string text)
        {
            string lower = text.TrimStart().ToLowerInvariant();
            if (lower.StartsWith("print") || lower.StartsWith(".ascii"))
            {
                var m = Regex.Match(text, "(?:print|\\.ascii|\\.asciiz)\\s+\"((?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
                if (!m.Success) return 0;
                string value = DecodeString(m.Groups[1].Value);
                if (lower.StartsWith(".asciiz")) value += "\0";
                return (uint)(((value.Length + 3) / 4) * 4);
            }
            if (lower.StartsWith("setreg")) return 8;
            if (lower.StartsWith(".space"))
            {
                string[] parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    uint count = ResolveOperandNoThrow(parts[1]);
                    return (uint)(((count + 3) / 4) * 4);
                }
            }
            if (lower.StartsWith(".word") || lower.StartsWith(".float"))
                return (uint)(Math.Max(1, SplitOperands(text).Skip(1).Count()) * 4);
            if (lower.StartsWith(".half"))
                return (uint)(((Math.Max(1, SplitOperands(text).Skip(1).Count()) * 2 + 3) / 4) * 4);
            if (lower.StartsWith(".byte"))
                return (uint)(((Math.Max(1, SplitOperands(text).Skip(1).Count()) + 3) / 4) * 4);
            return 4;
        }

        private void EmitLine(string text, SourceLine line, ref uint pc, List<string> output)
        {
            string lower = text.ToLowerInvariant();
            if (lower.StartsWith("print"))
            {
                var m = Regex.Match(text, "print\\s+\"((?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
                if (!m.Success) throw new InvalidOperationException("Invalid print syntax.");
                string s = DecodeString(m.Groups[1].Value);
                for (int i = 0; i < s.Length; i += 4)
                {
                    string chunk = s.Substring(i, Math.Min(4, s.Length - i));
                    byte[] bytes = new byte[4];
                    Encoding.Latin1.GetBytes(chunk, 0, chunk.Length, bytes, 0);
                    uint word = BitConverter.ToUInt32(bytes, 0);
                    output.Add(FormatOutput(pc, word));
                    pc += 4;
                }
                return;
            }

            if (lower.StartsWith("hexcode"))
            {
                string[] parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) throw new InvalidOperationException("Invalid hexcode syntax.");
                uint word = ResolveOperand(parts[1]);
                output.Add(FormatOutput(pc, word));
                pc += 4;
                return;
            }

            if (lower.StartsWith("float"))
            {
                string[] parts = text.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) throw new InvalidOperationException("Invalid float syntax.");
                float f = ParseFloatLiteral(parts[1]);
                uint word = unchecked((uint)BitConverter.SingleToInt32Bits(f));
                output.Add(FormatOutput(pc, word));
                pc += 4;
                return;
            }

            if (lower.StartsWith("setreg"))
            {
                string[] parts = text.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) throw new InvalidOperationException("Invalid setreg syntax.");
                string reg = parts[1];
                uint value = ResolveOperand(parts[2]);
                uint lui = MipsAssembler.Assemble($"lui {reg}, 0x{(value >> 16):X}", pc) ?? throw new InvalidOperationException("Could not assemble setreg lui.");
                output.Add(FormatOutput(pc, lui)); pc += 4;
                uint ori = MipsAssembler.Assemble($"ori {reg}, {reg}, 0x{(value & 0xFFFF):X}", pc) ?? throw new InvalidOperationException("Could not assemble setreg ori.");
                output.Add(FormatOutput(pc, ori)); pc += 4;
                return;
            }

            if (lower.StartsWith(".word"))
            {
                foreach (string operand in SplitOperands(text).Skip(1))
                {
                    uint word = ResolveOperand(operand);
                    output.Add(FormatOutput(pc, word));
                    pc += 4;
                }
                return;
            }

            if (lower.StartsWith(".float"))
            {
                foreach (string operand in SplitOperands(text).Skip(1))
                {
                    float f = ParseFloatLiteral(operand);
                    uint word = unchecked((uint)BitConverter.SingleToInt32Bits(f));
                    output.Add(FormatOutput(pc, word));
                    pc += 4;
                }
                return;
            }

            if (lower.StartsWith(".ascii") || lower.StartsWith(".asciiz"))
            {
                var m = Regex.Match(text, "(?:\\.ascii|\\.asciiz)\\s+\"((?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
                if (!m.Success) throw new InvalidOperationException("Invalid ASCII string syntax.");
                string value = DecodeString(m.Groups[1].Value);
                if (lower.StartsWith(".asciiz")) value += "\0";
                EmitStringBytes(value, ref pc, output);
                return;
            }

            if (lower.StartsWith(".byte"))
            {
                var bytes = new List<byte>();
                foreach (string operand in SplitOperands(text).Skip(1))
                    bytes.Add((byte)(ResolveOperand(operand) & 0xFF));
                EmitRawBytes(bytes, ref pc, output);
                return;
            }

            if (lower.StartsWith(".half"))
            {
                var bytes = new List<byte>();
                foreach (string operand in SplitOperands(text).Skip(1))
                {
                    ushort value = (ushort)(ResolveOperand(operand) & 0xFFFF);
                    bytes.Add((byte)(value & 0xFF));
                    bytes.Add((byte)(value >> 8));
                }
                EmitRawBytes(bytes, ref pc, output);
                return;
            }

            if (lower.StartsWith(".space"))
            {
                string[] parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) throw new InvalidOperationException("Invalid .space syntax.");
                uint count = ResolveOperand(parts[1]);
                var bytes = new List<byte>();
                for (uint i = 0; i < count; i++)
                    bytes.Add(0);
                EmitRawBytes(bytes, ref pc, output);
                return;
            }

            if (TryEmitBranchInstruction(text, ref pc, output))
                return;

            string asm = NormalizeAssembly(SubstituteLabels(text));
            if (asm.StartsWith("b ", StringComparison.OrdinalIgnoreCase))
                asm = "beq zero, zero," + asm.Substring(1);
            uint? wordValue = MipsAssembler.Assemble(asm, pc);
            if (!wordValue.HasValue)
                throw new InvalidOperationException($"Unable to assemble '{text}'.");
            output.Add(FormatOutput(pc, wordValue.Value));
            pc += 4;
        }

        private void EmitStringBytes(string value, ref uint pc, List<string> output)
        {
            var bytes = new byte[value.Length];
            Encoding.Latin1.GetBytes(value, 0, value.Length, bytes, 0);
            EmitRawBytes(bytes, ref pc, output);
        }

        private void EmitRawBytes(IList<byte> rawBytes, ref uint pc, List<string> output)
        {
            for (int i = 0; i < rawBytes.Count; i += 4)
            {
                byte[] bytes = new byte[4];
                int count = Math.Min(4, rawBytes.Count - i);
                for (int b = 0; b < count; b++)
                    bytes[b] = rawBytes[i + b];
                uint word = BitConverter.ToUInt32(bytes, 0);
                output.Add(FormatOutput(pc, word));
                pc += 4;
            }
        }

        private bool TryEmitBranchInstruction(string text, ref uint pc, List<string> output)
        {
            string asm = NormalizeAssembly(text);
            int sep = asm.IndexOfAny(new[] { ' ', '\t' });
            string mnemonic = sep < 0 ? asm.ToLowerInvariant() : asm.Substring(0, sep).Trim().ToLowerInvariant();
            string operandText = sep < 0 ? string.Empty : asm.Substring(sep + 1).Trim();
            string[] operands = operandText.Length == 0
                ? Array.Empty<string>()
                : operandText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            uint word;
            if (mnemonic == "b")
            {
                if (operands.Length < 1) throw new InvalidOperationException("Branch target is missing.");
                short offset = ResolveBranchOffset(operands[0], pc);
                word = (0x04u << 26) | (0u << 21) | (0u << 16) | (ushort)offset;
            }
            else if (mnemonic is "beq" or "bne" or "beql" or "bnel")
            {
                if (operands.Length < 3) return false;
                uint op = mnemonic switch
                {
                    "beq" => 0x04u,
                    "bne" => 0x05u,
                    "beql" => 0x14u,
                    _ => 0x15u
                };
                uint rs = ParseGpr(operands[0]);
                uint rt = ParseGpr(operands[1]);
                short offset = ResolveBranchOffset(operands[2], pc);
                word = (op << 26) | (rs << 21) | (rt << 16) | (ushort)offset;
            }
            else if (mnemonic is "blez" or "bgtz" or "blezl" or "bgtzl")
            {
                if (operands.Length < 2) return false;
                uint op = mnemonic switch
                {
                    "blez" => 0x06u,
                    "bgtz" => 0x07u,
                    "blezl" => 0x16u,
                    _ => 0x17u
                };
                uint rs = ParseGpr(operands[0]);
                short offset = ResolveBranchOffset(operands[1], pc);
                word = (op << 26) | (rs << 21) | (ushort)offset;
            }
            else if (mnemonic is "bltz" or "bgez" or "bltzl" or "bgezl" or "bltzal" or "bgezal" or "bltzall" or "bgezall")
            {
                if (operands.Length < 2) return false;
                uint rt = mnemonic switch
                {
                    "bltz" => 0x00u,
                    "bgez" => 0x01u,
                    "bltzl" => 0x02u,
                    "bgezl" => 0x03u,
                    "bltzal" => 0x10u,
                    "bgezal" => 0x11u,
                    "bltzall" => 0x12u,
                    _ => 0x13u
                };
                uint rs = ParseGpr(operands[0]);
                short offset = ResolveBranchOffset(operands[1], pc);
                word = (0x01u << 26) | (rs << 21) | (rt << 16) | (ushort)offset;
            }
            else if (mnemonic is "bc1f" or "bc1t")
            {
                if (operands.Length < 1) return false;
                short offset = ResolveBranchOffset(operands[0], pc);
                uint taken = mnemonic == "bc1t" ? 1u : 0u;
                word = (0x11u << 26) | (0x08u << 21) | (taken << 16) | (ushort)offset;
            }
            else
            {
                return false;
            }

            output.Add(FormatOutput(pc, word));
            pc += 4;
            return true;
        }

        private short ResolveBranchOffset(string operand, uint pc)
        {
            uint target = ResolveBranchTarget(operand);
            int delta = unchecked((int)target - (int)(pc + 4));
            if ((delta & 3) != 0)
                throw new InvalidOperationException($"Branch target is not word-aligned: {operand}");
            int offset = delta / 4;
            if (offset < short.MinValue || offset > short.MaxValue)
                throw new InvalidOperationException($"Branch offset out of range: {operand}");
            return (short)offset;
        }

        private uint ResolveBranchTarget(string operand)
        {
            string cleaned = CleanLabelName((operand ?? string.Empty).Trim());
            if (cleaned.Length > 0 && _labels.TryGetValue(cleaned, out uint labelAddress))
                return labelAddress;
            if (_labels.TryGetValue((operand ?? string.Empty).Trim(), out labelAddress))
                return labelAddress;
            return ParseNumber(operand);
        }

        private static uint ParseGpr(string operand)
        {
            string op = (operand ?? string.Empty).Trim().TrimEnd(':').TrimStart('$').ToLowerInvariant();
            string[] names =
            {
                "zero","at","v0","v1","a0","a1","a2","a3",
                "t0","t1","t2","t3","t4","t5","t6","t7",
                "s0","s1","s2","s3","s4","s5","s6","s7",
                "t8","t9","k0","k1","gp","sp","fp","ra"
            };
            for (int i = 0; i < names.Length; i++)
                if (op == names[i]) return (uint)i;
            if (uint.TryParse(op, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint n) && n < 32)
                return n;
            throw new InvalidOperationException($"Unknown register: {operand}");
        }

        private string NormalizeAssembly(string text)
        {
            string asm = text.Trim();
            if (asm.Length == 0) return asm;

            int sep = asm.IndexOfAny(new[] { ' ', '\t' });
            string mnemonic = sep < 0 ? asm.ToLowerInvariant() : asm.Substring(0, sep).Trim().ToLowerInvariant();
            string operandText = sep < 0 ? string.Empty : asm.Substring(sep + 1).Trim();
            string[] operands = operandText.Length == 0
                ? Array.Empty<string>()
                : operandText.Split(',', StringSplitOptions.TrimEntries);

            operands = NormalizeLooseOperands(mnemonic, operands);

            if (operands.Length >= 2 && IsCop1Move(mnemonic))
            {
                bool op1F = IsFpuRegister(operands[0]);
                bool op2F = IsFpuRegister(operands[1]);
                if (op1F && !op2F)
                    (operands[0], operands[1]) = (operands[1], operands[0]);
            }

            if (operands.Length > 2 && IsUnaryFpu(mnemonic))
                operands = operands.Take(2).ToArray();

            return operands.Length == 0 ? mnemonic : mnemonic + " " + string.Join(", ", operands);
        }

        private static string[] NormalizeLooseOperands(string mnemonic, string[] operands)
        {
            if (operands.Length == 0) return operands;

            for (int i = 0; i < operands.Length; i++)
                operands[i] = operands[i].Trim();

            if (IsTwoRegisterBranch(mnemonic) && operands.Length == 2)
            {
                string[] tail = operands[1].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tail.Length >= 2)
                    operands = new[] { operands[0], tail[0].TrimEnd(':'), tail[1] };
            }

            if (IsTwoRegisterBranch(mnemonic) && operands.Length >= 3)
                operands[1] = operands[1].TrimEnd(':');

            if (IsMemoryInstruction(mnemonic) && operands.Length == 3 && IsZeroRegister(operands[1]) && operands[2].Contains('('))
                operands = new[] { operands[0], operands[2] };

            return operands;
        }

        private static bool IsTwoRegisterBranch(string mnemonic)
            => mnemonic is "beq" or "bne" or "beql" or "bnel";

        private static bool IsMemoryInstruction(string mnemonic)
            => mnemonic is "lb" or "lbu" or "lh" or "lhu" or "lw" or "lwu" or "lwl" or "lwr" or "ld" or "ldl" or "ldr" or "lq"
                or "sb" or "sh" or "sw" or "swl" or "swr" or "sd" or "sdl" or "sdr" or "sq"
                or "lwc1" or "swc1" or "ldc1" or "sdc1";

        private static bool IsZeroRegister(string operand)
        {
            string op = (operand ?? string.Empty).Trim().TrimStart('$').TrimEnd(':');
            return op.Equals("zero", StringComparison.OrdinalIgnoreCase) || op == "0";
        }

        private static bool IsCop1Move(string mnemonic)
            => mnemonic is "mtc1" or "mfc1" or "cfc1" or "ctc1";

        private static bool IsUnaryFpu(string mnemonic)
            => mnemonic is "sqrt.s" or "abs.s" or "mov.s" or "neg.s" or "cvt.w.s";

        private static bool IsFpuRegister(string operand)
        {
            string op = operand.Trim().TrimStart('$').ToLowerInvariant();
            return op.Length >= 2 && op[0] == 'f' && uint.TryParse(op.Substring(1), out uint n) && n < 32;
        }

        private static IEnumerable<string> SplitOperands(string text)
        {
            int sep = text.IndexOfAny(new[] { ' ', '\t' });
            if (sep < 0) return new[] { text.Trim() };
            return new[] { text.Substring(0, sep).Trim() }
                .Concat(text.Substring(sep + 1)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        private string SubstituteLabels(string text)
        {
            if (_labels.Count == 0 || string.IsNullOrEmpty(text))
                return text;

            text = Regex.Replace(text, @":([A-Za-z_][\w.:]*)", m =>
            {
                string name = CleanLabelName(m.Groups[1].Value);
                return _labels.TryGetValue(name, out uint addr) ? "0x" + addr.ToString("X8") : m.Value;
            }, RegexOptions.CultureInvariant);

            return Regex.Replace(text, @"(?<![\w.:$])([A-Za-z_][\w.:]*)(?![\w.:])", m =>
            {
                string name = CleanLabelName(m.Groups[1].Value);
                return _labels.TryGetValue(name, out uint addr) ? "0x" + addr.ToString("X8") : m.Value;
            }, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        private uint ResolveOperand(string text)
        {
            string original = (text ?? string.Empty).Trim();
            string key = CleanLabelName(original);
            if (_labels.TryGetValue(key, out uint addr))
                return addr;
            return ParseNumber(original);
        }

        private uint ResolveOperandNoThrow(string text)
        {
            try { return ResolveOperand(text); }
            catch { return 0; }
        }

        private static float ParseFloatLiteral(string text)
        {
            string value = (text ?? string.Empty).Trim().TrimStart('$');
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                throw new InvalidOperationException($"Invalid float value: {text}");
            return result;
        }

        private uint ParseNumber(string text)
        {
            string s = (text ?? string.Empty).Trim();
            if (s.StartsWith("$", StringComparison.Ordinal))
                return uint.Parse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(s, "^[0-9A-Fa-f]{6,8}$"))
                return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed))
                return unchecked((uint)signed);
            return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private string DecodeString(string s)
            => s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\0", "\0");

        private string FormatOutput(uint address, uint word)
        {
            string addr = address.ToString("X8");
            if (_formatPrefix.Length == 1 && _formatPrefix != "-")
                addr = _formatPrefix[0] + addr.Substring(1);
            string value = word.ToString("X8");
            return _pnach ? $"patch=1,EE,{addr},extended,{value}" : $"{addr} {value}";
        }
    }
}
