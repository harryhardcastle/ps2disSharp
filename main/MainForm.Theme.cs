using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Forms;

namespace PS2Disassembler
{
    public sealed partial class MainForm : Form
    {
        private static bool IsFprCategory(DebugRegisterCategory category)
        {
            string name = category.Name ?? string.Empty;
            if (name.IndexOf("fpr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("fpu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("cop1", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return category.Registers.Any(r => r.Name.StartsWith("f", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsGprCategory(DebugRegisterCategory category)
        {
            string name = category.Name ?? string.Empty;
            if (name.IndexOf("gpr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("general", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return category.Registers.Any(r => r.Name.Equals("zero", StringComparison.OrdinalIgnoreCase) ||
                                               r.Name.Equals("sp", StringComparison.OrdinalIgnoreCase) ||
                                               r.Name.Equals("ra", StringComparison.OrdinalIgnoreCase) ||
                                               r.Name.StartsWith("a", StringComparison.OrdinalIgnoreCase) ||
                                               r.Name.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
                                               r.Name.StartsWith("t", StringComparison.OrdinalIgnoreCase) ||
                                               r.Name.StartsWith("s", StringComparison.OrdinalIgnoreCase));
        }

        private static Color ScaleColor(Color color, float factor)
        {
            int Scale(int c) => Math.Max(0, Math.Min(255, (int)Math.Round(c * factor)));
            return Color.FromArgb(color.A, Scale(color.R), Scale(color.G), Scale(color.B));
        }

        private static Color BrightenColor(Color color, float factor)
        {
            int Brighten(int c) => Math.Max(0, Math.Min(255, (int)Math.Round(c + ((255 - c) * factor))));
            return Color.FromArgb(color.A, Brighten(color.R), Brighten(color.G), Brighten(color.B));
        }


        private Color GetOptionsDialogBackColor()
        {
            return _currentTheme == AppTheme.Dark
                ? Color.FromArgb(33, 36, 42)
                : Color.FromArgb(232, 235, 240);
        }

        private Color GetOptionsSurfaceBackColor()
        {
            return _currentTheme == AppTheme.Dark
                ? Color.FromArgb(48, 52, 60)
                : Color.FromArgb(250, 251, 253);
        }


        private Color GetCheckBoxBackColor(Control? parent)
        {
            return Equals(parent?.Tag, "OptionsSurface") ? GetOptionsSurfaceBackColor() : _themeFormBack;
        }

        private void SetRegisterPalette(float lightFactor, float darkBrightenFactor)
        {
            Color Adjust(Color c) => _currentTheme == AppTheme.Dark ? BrightenColor(c, darkBrightenFactor) : ScaleColor(c, lightFactor);

            tokenRegisterAColor = Adjust(Color.FromArgb(80, 167, 238));
            tokenRegisterTColor = Adjust(Color.FromArgb(230, 170, 220));
            tokenRegisterVColor = Adjust(Color.FromArgb(250, 119, 109));
            tokenRegisterSColor = Adjust(Color.FromArgb(241, 196, 15));
            tokenRegisterSPColor = Adjust(Color.FromArgb(245, 171, 53));
            tokenRegisterRAColor = Adjust(Color.FromArgb(231, 76, 60));
            tokenRegisterFColor = Adjust(Color.FromArgb(119, 221, 119));
            tokenRegisterGPColor = Adjust(Color.FromArgb(46, 204, 113));
            tokenRegisterKColor = Adjust(Color.FromArgb(155, 89, 182));
            tokenRegisterATColor = Adjust(Color.FromArgb(173, 216, 230));
            tokenRegisterZeroColor = Adjust(Color.FromArgb(128, 128, 128));
            tokenRegisterFPColor = Adjust(Color.FromArgb(220, 220, 170));
            tokenRegisterOtherColor = Adjust(Color.FromArgb(79, 193, 255));
        }

        private void ApplyTheme(AppTheme theme, bool forceFrameRefresh = false)
        {
            _currentTheme = theme;
            bool dark = theme == AppTheme.Dark;

            if (dark)
            {
                ColBg       = Color.FromArgb(32, 36, 42);
                ColSkip     = Color.FromArgb(49, 67, 92);
                ColXref     = Color.FromArgb(84, 84, 84); //64, 72, 84
                ColComment  = Color.FromArgb(125, 190, 140);
                ColHexBg    = Color.FromArgb(24, 27, 31);
                ColSel      = Color.FromArgb(64, 72, 84); //128, 112, 44
                ColSelFg    = Color.White;
                ColAddr     = Color.FromArgb(200, 200, 195); //255, 126, 126
                ColHexFg    = ColAddr;
                ColLabel    = Color.FromArgb(140, 140, 135); //255, 160, 140
                ColNop      = Color.FromArgb(145, 145, 145);
                ColBranch   = Color.FromArgb(110, 215, 145);
                ColJump     = Color.FromArgb(255, 135, 135);
                ColCall     = Color.FromArgb(255, 170, 120);
                ColFpu      = Color.FromArgb(120, 180, 255);
                ColMmi      = Color.FromArgb(190, 140, 255);
                ColSys      = Color.FromArgb(225, 185, 120);
                ColData     = Color.FromArgb(175, 175, 175);
                ColMem      = Color.FromArgb(120, 175, 255);
                ColAscii    = Color.FromArgb(135, 220, 135);
                ColZeroByte = Color.FromArgb(128, 128, 128);

                _headerBack = Color.FromArgb(44, 48, 54);
                _headerFore = Color.FromArgb(232, 232, 232);
                _headerBorder = Color.FromArgb(82, 86, 94);
                _themeFormBack = Color.FromArgb(40, 44, 50);
                _themeFormFore = Color.FromArgb(232, 232, 232);
                _themeWindowBack = Color.FromArgb(30, 33, 38);
                _themeWindowFore = Color.FromArgb(232, 232, 232);
                _themeEditValidBack = Color.FromArgb(60, 60, 60); //70, 70, 36
                _themeEditInvalidBack = Color.FromArgb(105, 50, 50);
                _themeHighlightDest = Color.FromArgb(44, 50, 60); //44, 66, 96
                _themeBreakpointBack = Color.FromArgb(40, 120, 40); //70, 140, 70
                _themeBreakpointActiveBack = Color.FromArgb(120, 40, 40); //190, 60, 60
                _themeTitleBarBack = Color.FromArgb(36, 39, 44);
                _themeTitleBarText = Color.FromArgb(238, 238, 238);
                _themeCodeManagerRtbBack = Color.FromArgb(44, 48, 54);
                _themeCodeManagerBack = ColHexBg;

                SetRegisterPalette(0.85f, 0.16f);
            }
            else
            {
                ColBg       = Color.FromArgb(0xa0, 0xd0, 0xff);
                ColSkip     = Color.FromArgb(130, 170, 215);
                ColXref     = Color.FromArgb(180, 180, 180);
                ColComment  = Color.FromArgb(56, 91, 56);
                ColHexBg    = Color.White;
                ColSel      = Color.FromArgb(0, 0, 128);
                ColSelFg    = Color.White;
                ColAddr     = Color.FromArgb(128, 0, 0);
                ColHexFg    = ColAddr;
                ColLabel    = Color.FromArgb(128, 0, 0);
                ColNop      = Color.FromArgb(0x00, 0x00, 0xb0);
                ColBranch   = Color.FromArgb(0x00, 0x00, 0xb0);
                ColJump     = Color.FromArgb(0x00, 0x00, 0xb0);
                ColCall     = Color.FromArgb(0x00, 0x00, 0xb0);
                ColFpu      = Color.FromArgb(0x00, 0x00, 0xb0);
                ColMmi      = Color.FromArgb(0x00, 0x00, 0xb0);
                ColSys      = Color.FromArgb(0x00, 0x00, 0xb0);
                ColData     = Color.FromArgb(0x00, 0x00, 0xb0);
                ColMem      = Color.FromArgb(0x00, 0x00, 0xb0);
                ColAscii    = Color.FromArgb(0, 70, 0);
                ColZeroByte = Color.FromArgb(200, 200, 200);

                _headerBack = SystemColors.Control;
                _headerFore = SystemColors.ControlText;
                _headerBorder = SystemColors.ControlDark;
                _themeFormBack = SystemColors.Control;
                _themeFormFore = SystemColors.ControlText;
                _themeWindowBack = Color.White;
                _themeWindowFore = Color.Black;
                _themeEditValidBack = Color.FromArgb(255, 255, 200);
                _themeEditInvalidBack = Color.FromArgb(255, 160, 160);
                _themeHighlightDest = Color.FromArgb(205, 225, 255);
                _themeBreakpointBack = Color.FromArgb(0, 180, 0);
                _themeBreakpointActiveBack = Color.FromArgb(220, 0, 0);
                _themeTitleBarBack = Color.FromArgb(242, 242, 242);
                _themeTitleBarText = Color.Black;
                _themeCodeManagerRtbBack = Color.White;
                _themeCodeManagerBack = Color.FromArgb(228, 228, 228);

                SetRegisterPalette(0.455f, 0.0f);
            }

            if (_miThemeLight != null) _miThemeLight.Checked = theme == AppTheme.Light;
            if (_miThemeDark != null) _miThemeDark.Checked = theme == AppTheme.Dark;

            BackColor = _themeFormBack;
            ForeColor = _themeFormFore;

            if (_menuBar != null)
            {
                _menuBar.BackColor = _headerBack;
                _menuBar.ForeColor = _headerFore;
                _menuBar.Renderer = new ThemedToolStripRenderer(dark, _headerFore);
                ApplyThemeToToolStripItems(_menuBar.Items);
                if (_menuStatusLabel != null && !_menuPauseStatusActive)
                {
                    _menuStatusLabel.ForeColor = _headerFore;
                    _menuStatusLabel.Font = _menuBar.Font;
                    _menuPauseStatusSavedColor = _headerFore;
                    _menuPauseStatusSavedFont = _menuBar.Font;
                }
                if (_menuPauseStatusActive)
                {
                    _menuPauseStatusSavedColor = _headerFore;
                    _menuPauseStatusSavedFont = _menuBar.Font;
                    _menuStatusLabel.ForeColor = ColJump;
                    if (_menuStatusBoldFont != null)
                        _menuStatusLabel.Font = _menuStatusBoldFont;
                }
            }

            if (_statusStrip != null)
            {
                _statusStrip.BackColor = _headerBack;
                _statusStrip.ForeColor = _headerFore;
                _statusStrip.Renderer = new ThemedToolStripRenderer(dark, _headerFore);
                ApplyThemeToToolStripItems(_statusStrip.Items);
            }

            if (_hexList != null)
            {
                _hexList.BackColor = ColHexBg;
                _hexList.ForeColor = _themeWindowFore;
                _hexList.Invalidate();
            }

            if (_hexVScroll != null)
            {
                _hexVScroll.ApplyTheme(dark,
                    dark ? Color.FromArgb(45, 49, 56) : SystemColors.Control,
                    dark ? Color.FromArgb(78, 86, 98) : SystemColors.ControlDark,
                    dark ? Color.FromArgb(116, 126, 142) : SystemColors.ControlDarkDark);
                _hexVScroll.Invalidate();
            }

            if (_mainTabs != null)
            {
                _mainTabs.BackColor = _themeFormBack;
                _mainTabs.ForeColor = _themeFormFore;
                foreach (FlatTabPage tp in _mainTabs.Pages)
                {
                    tp.BackColor = ReferenceEquals(tp, _memoryViewPage) ? _hexList.BackColor : _themeFormBack;
                    tp.ForeColor = _themeFormFore;
                }
                _mainTabs.ApplyPalette(_currentTheme == AppTheme.Dark, _themeFormBack, _themeFormFore);
                _mainTabs.Invalidate(true);
            }

            if (_disasmList != null)
            {
                _disasmList.BackColor = ColBg;
                _disasmList.ForeColor = _themeWindowFore;
                _disasmList.HeaderBackColor = _headerBack;
                _disasmList.HeaderBorderColor = _headerBorder;
                _disasmList.Invalidate();
            }

            if (_asciiBytesBar != null)
            {
                _asciiBytesBar.BackColor = ColHexBg;
                _asciiBytesBar.Invalidate();
            }

            ApplyThemeToControlTree(this);

            if (_memoryViewPage != null && _hexList != null)
            {
                _memoryViewPage.BackColor = _hexList.BackColor;
                _memoryViewPage.Invalidate();
            }

            foreach (Form owned in OwnedForms)
            {
                ApplyThemeToControlTree(owned);
                ApplyScrollbarTheme(owned, dark);
                ApplyThemeToWindowChrome(owned, forceFrameRefresh);
            }

            // Dark scrollbars on all list/text controls
            ApplyScrollbarTheme(this, dark);

            // Also theme the Code Manager inline panel if it's been populated
            var cmPanel = FindCodeManagerPanel();
            if (cmPanel != null)
            {
                ApplyThemeToControlTree(cmPanel);
                ApplyScrollbarTheme(cmPanel, dark);
            }
            _codeToolsDlg?.ApplyEditorScrollbarTheme(dark);
            if (_codeDesignerWorkspace != null && !_codeDesignerWorkspace.IsDisposed)
            {
                _codeDesignerWorkspace.ApplyTheme(dark);
                ApplyScrollbarTheme(_codeDesignerWorkspace, dark);
            }

            RefreshTitleBarTheme(forceFrameRefresh);

            Invalidate(true);
            _hexList?.Invalidate();
            _disasmList?.Invalidate();
            // Force synchronous repaint so colors take effect immediately
            if (IsHandleCreated) Refresh();
        }
        private void ApplyWindowChromeTheme(bool forceFrameRefresh = false)
        {
            if (!IsHandleCreated)
                return;

            try
            {
                int darkVal = _currentTheme == AppTheme.Dark ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
            }
            catch { }

            try
            {
                int captionColor = ColorTranslator.ToWin32(_themeTitleBarBack);
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            }
            catch { }

            try
            {
                int textColor = ColorTranslator.ToWin32(_themeTitleBarText);
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            }
            catch { }

            // Toggle WM_NCACTIVATE off/on to force DWM to repaint the title bar immediately
            try { NativeMethods.SendMessage(Handle, NativeMethods.WM_NCACTIVATE, (IntPtr)0, IntPtr.Zero); } catch { }
            try { NativeMethods.SendMessage(Handle, NativeMethods.WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero); } catch { }

            if (forceFrameRefresh)
            {
                try
                {
                    NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                }
                catch { }

                try
                {
                    NativeMethods.RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero,
                        NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_FRAME | NativeMethods.RDW_ALLCHILDREN);
                }
                catch { }
            }
        }

        private void ApplyScrollbarTheme(Control root, bool dark)
        {
            // SetWindowTheme("DarkMode_Explorer") makes the OS draw dark scrollbars on
            // ListView / TreeView / etc. on Windows 10 1809+ and Windows 11.
            // SetWindowTheme("") reverts to the default light appearance.
            string theme = dark ? "DarkMode_Explorer" : "";
            ApplyScrollbarThemeRecursive(root, dark, theme);
        }

        private static void ApplyScrollbarThemeRecursive(Control c, bool dark, string theme)
        {
            if (c.IsHandleCreated)
            {
                if (c is ListView || c is VirtualDisasmList || c is TreeView || c is RichTextBox || c is TextBox || c is ComboBox)
                {
                    try { NativeMethods.SetWindowTheme(c.Handle, theme, null); } catch { }
                }
            }
            foreach (Control child in c.Controls)
                ApplyScrollbarThemeRecursive(child, dark, theme);
        }

        private void ApplyThemeToToolStripItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor = _headerFore;
                item.BackColor = _headerBack;
                if (item is ToolStripDropDownItem dd && dd.HasDropDownItems)
                {
                    dd.DropDown.BackColor = _headerBack;
                    dd.DropDown.ForeColor = _headerFore;
                    ApplyThemeToToolStripItems(dd.DropDownItems);
                }
            }
        }

        private static bool IsInsideCodeManager(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
            {
                if (p.Tag is string tag && (tag == "CodeManagerSurface" || tag == "CodeManagerPanel" || tag == "CodeManagerHost"))
                    return true;
            }
            return false;
        }

        private Font GetStandardTextBoxFont()
        {
            return _txtReadBreakpoint?.Font ?? Font ?? new Font("Tahoma", 8.25f);
        }

        private static Color BrightenColorByPercent(Color color, float factor)
        {
            factor = Math.Max(0f, factor);
            return Color.FromArgb(color.A,
                Math.Min(255, (int)Math.Round(color.R * factor)),
                Math.Min(255, (int)Math.Round(color.G * factor)),
                Math.Min(255, (int)Math.Round(color.B * factor)));
        }

        private void ApplyThemeToControlTree(Control root)
        {
            if (root is MenuStrip or StatusStrip)
                return;

            switch (root)
            {
                case VirtualDisasmList v:
                    if (ReferenceEquals(v, _hexList))
                    {
                        v.BackColor = ColHexBg;
                    }
                    else if (Equals(v.Tag, "AccessMonitorList"))
                    {
                        v.BackColor = _themeWindowBack;
                    }
                    else if (_currentTheme == AppTheme.Light &&
                        (Equals(v.Tag, "RegisterList") || IsInsideCodeManager(v)))
                    {
                        v.BackColor = _themeWindowBack;
                    }
                    else
                    {
                        v.BackColor = ColBg;
                    }
                    v.ForeColor = _themeWindowFore;
                    v.HeaderBackColor = _headerBack;
                    v.HeaderBorderColor = _headerBorder;
                    break;
                case ListView lv:
                    lv.BackColor = ColHexBg;
                    lv.ForeColor = _themeWindowFore;
                    break;
                case ThemedCallStackTextBox callStack:
                    callStack.ApplyTheme(_currentTheme == AppTheme.Dark,
                        _themeWindowBack,
                        _themeWindowFore,
                        _currentTheme == AppTheme.Dark ? Color.FromArgb(45, 49, 56) : SystemColors.Control,
                        _currentTheme == AppTheme.Dark ? Color.FromArgb(78, 86, 98) : SystemColors.ControlDark,
                        _currentTheme == AppTheme.Dark ? Color.FromArgb(116, 126, 142) : SystemColors.ControlDarkDark);
                    break;
                case RichTextBox rtb:
                    if (IsInsideCodeManager(rtb))
                    {
                        rtb.BackColor = _themeCodeManagerRtbBack;
                        rtb.ForeColor = _themeWindowFore;
                        rtb.BorderStyle = BorderStyle.None;
                    }
                    else
                    {
                        rtb.BackColor = _themeWindowBack;
                        rtb.ForeColor = _themeWindowFore;
                        rtb.BorderStyle = BorderStyle.None;
                    }
                    break;
                case TextBox tb:
                    tb.Font = GetStandardTextBoxFont();
                    tb.BackColor = _themeWindowBack;
                    tb.ForeColor = _themeWindowFore;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.BackColor = _themeWindowBack;
                    cb.ForeColor = _themeWindowFore;
                    cb.FlatStyle = FlatStyle.Flat;
                    break;
                case Button btn:
                    if (Equals(btn.Tag, FlatTabHost.TabButtonTag))
                        break;
                    Color buttonBack = BrightenColorByPercent(_headerBack, 1.20f);
                    Color buttonHover = BrightenColorByPercent(Color.FromArgb(
                        Math.Min(255, _headerBack.R + 18),
                        Math.Min(255, _headerBack.G + 18),
                        Math.Min(255, _headerBack.B + 30)), 1.20f);
                    btn.BackColor = buttonBack;
                    btn.ForeColor = _headerFore;
                    btn.UseVisualStyleBackColor = false;
                    btn.FlatStyle = FlatStyle.Flat;
                    btn.FlatAppearance.BorderColor = _headerBorder;
                    btn.FlatAppearance.BorderSize = 1;
                    btn.FlatAppearance.MouseOverBackColor = buttonHover;
                    btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(
                        Math.Max(0, buttonBack.R - 10),
                        Math.Max(0, buttonBack.G - 10),
                        Math.Min(255, buttonBack.B + 40));
                    // Attach custom disabled-text painting for dark theme so text stays legible
                    AttachDisabledButtonPainter(btn, _currentTheme == AppTheme.Dark);
                    break;
                case ThemedCheckBox tcb:
                    tcb.ForeColor = _themeFormFore;
                    tcb.BackColor = GetCheckBoxBackColor(tcb.Parent);
                    tcb.FlatStyle = FlatStyle.Flat;
                    tcb.ApplyPalette(_currentTheme == AppTheme.Dark, GetCheckBoxBackColor(tcb.Parent), _themeFormFore);
                    break;
                case CheckBox cbx:
                    cbx.ForeColor = _themeFormFore;
                    cbx.BackColor = GetCheckBoxBackColor(cbx.Parent);
                    cbx.FlatStyle = FlatStyle.Flat;
                    break;
                case RadioButton rb:
                    rb.ForeColor = _themeFormFore;
                    rb.BackColor = Equals(rb.Parent?.Tag, "OptionsSurface") ? GetOptionsSurfaceBackColor() : _themeFormBack;
                    rb.FlatStyle = FlatStyle.Flat;
                    break;
                case Label lbl:
                    lbl.ForeColor = _themeFormFore;
                    lbl.BackColor = Color.Transparent;
                    break;
                case GroupBox gb:
                    gb.ForeColor = _themeFormFore;
                    gb.BackColor = _themeFormBack;
                    break;
                case SplitContainer split:
                    split.BackColor = _themeFormBack;
                    split.ForeColor = _themeFormFore;
                    break;
                case TabControl tc:
                    tc.BackColor = _themeFormBack;
                    tc.ForeColor = _themeFormFore;
                    break;
                case FlatTabHost th:
                    if (Equals(th.Tag, "CodeManagerTabHost"))
                    {
                        th.BackColor = _themeCodeManagerBack;
                        th.TabStripBackColorOverride = _themeCodeManagerBack;
                        th.ContentBackColorOverride = _themeCodeManagerBack;
                        th.Margin = new Padding(0);
                        th.DimTabs = true;
                        th.ApplyPalette(_currentTheme == AppTheme.Dark, _themeCodeManagerBack, _themeFormFore);
                        // Force the internal FlowLayoutPanel (tab strip) and content host to match
                        foreach (Control inner in th.Controls)
                            inner.BackColor = _themeCodeManagerBack;
                    }
                    else if (Equals(th.Tag, "OptionsTabHost"))
                    {
                        Color optionsDialogBack = GetOptionsDialogBackColor();
                        Color optionsSurfaceBack = GetOptionsSurfaceBackColor();
                        th.BackColor = optionsDialogBack;
                        th.TabStripBackColorOverride = optionsDialogBack;
                        th.ContentBackColorOverride = optionsSurfaceBack;
                        th.Margin = new Padding(0);
                        th.DimTabs = false;
                        th.ApplyPalette(_currentTheme == AppTheme.Dark, optionsDialogBack, _themeFormFore);
                        foreach (Control inner in th.Controls)
                        {
                            if (inner is FlowLayoutPanel)
                                inner.BackColor = optionsDialogBack;
                            else
                                inner.BackColor = optionsSurfaceBack;
                        }
                    }
                    else
                    {
                        th.DimTabs = false;
                        th.ApplyPalette(_currentTheme == AppTheme.Dark, _themeFormBack, _themeFormFore);
                    }
                    if (th.IsHandleCreated) th.Invalidate(true);
                    break;
                case FlatTabPage tp:
                    if (Equals(tp.Tag, "CodeManagerPage") || Equals(tp.Tag, "CodeManagerHostPage"))
                        tp.BackColor = _themeCodeManagerBack;
                    else if (Equals(tp.Tag, "OptionsTabPage"))
                        tp.BackColor = GetOptionsSurfaceBackColor();
                    else if (ReferenceEquals(tp, _memoryViewPage))
                        tp.BackColor = _hexList.BackColor;
                    else
                        tp.BackColor = _themeFormBack;
                    tp.ForeColor = _themeFormFore;
                    if (tp.IsHandleCreated) tp.Invalidate(true);
                    break;
                case Panel pnl:
                    if (Equals(pnl.Tag, "CodeManagerPanel") || Equals(pnl.Tag, "CodeManagerSurface") || Equals(pnl.Tag, "CodeManagerHost"))
                    {
                        pnl.BackColor = _themeCodeManagerBack;
                        if (Equals(pnl.Tag, "CodeManagerPanel"))
                            pnl.Padding = new Padding(0);
                    }
                    else if (Equals(pnl.Tag, "OptionsSurface"))
                    {
                        pnl.BackColor = GetOptionsSurfaceBackColor();
                    }
                    else if (pnl.Parent is FlatTabHost)
                    {
                        // Don't override the content host panel inside a FlatTabHost — ApplyPalette manages it
                    }
                    else
                        pnl.BackColor = _themeFormBack;
                    pnl.ForeColor = _themeFormFore;
                    break;
                case Form form:
                    form.BackColor = form is OptionsDialog ? GetOptionsDialogBackColor() : _themeFormBack;
                    form.ForeColor = _themeFormFore;
                    break;
                default:
                    // Don't override FlowLayoutPanel colors inside a FlatTabHost — ApplyPalette manages those
                    if (root is FlowLayoutPanel && root.Parent is FlatTabHost)
                        break;
                    // Don't override DarkVScrollBar — themed separately via ApplyTheme
                    if (root is DarkVScrollBar)
                        break;
                    root.BackColor = _themeFormBack;
                    root.ForeColor = _themeFormFore;
                    break;
            }

            if (root.ContextMenuStrip != null)
            {
                root.ContextMenuStrip.BackColor = _headerBack;
                root.ContextMenuStrip.ForeColor = _headerFore;
                root.ContextMenuStrip.Renderer = new ThemedToolStripRenderer(_currentTheme == AppTheme.Dark, _headerFore);
                ApplyThemeToToolStripItems(root.ContextMenuStrip.Items);
            }

            foreach (Control child in root.Controls)
                ApplyThemeToControlTree(child);
        }

        private const string DisabledPainterKey = "_DisabledPainterAttached";

        /// <summary>
        /// Attaches (or detaches) a custom Paint handler that draws disabled button text
        /// in light grey instead of the default black, keeping it legible on dark backgrounds.
        /// </summary>
        private static void AttachDisabledButtonPainter(Button btn, bool dark)
        {
            // Remove any previously attached handler to avoid stacking
            if (btn.Tag is string s && s == DisabledPainterKey)
            {
                // Already attached — Paint handler is idempotent and theme-aware via closure capture,
                // so we recreate it. Remove old by clearing and re-adding below.
            }

            // Remove all previously attached disabled painters by resetting the paint event.
            // We use a simple approach: store a delegate in the button's Tag isn't feasible since
            // Tag is already used for tab-button detection. Instead we use a wrapper approach.
            // The paint handler checks the current theme dynamically.
            if (!(btn.Tag is string s2 && s2 == DisabledPainterKey))
            {
                btn.Tag = DisabledPainterKey;
                btn.Paint += (sender, e) =>
                {
                    if (sender is not Button b || b.Enabled)
                        return;

                    // Only override in dark theme (check background brightness)
                    if (b.BackColor.GetBrightness() >= 0.45f)
                        return;

                    // Repaint the full button background + border, then draw text in light grey
                    using var bgBrush = new SolidBrush(b.BackColor);
                    e.Graphics.FillRectangle(bgBrush, b.ClientRectangle);

                    if (b.FlatStyle == FlatStyle.Flat && b.FlatAppearance.BorderSize > 0)
                    {
                        using var borderPen = new Pen(b.FlatAppearance.BorderColor, b.FlatAppearance.BorderSize);
                        e.Graphics.DrawRectangle(borderPen, 0, 0, b.Width - 1, b.Height - 1);
                    }

                    Color disabledFore = Color.FromArgb(140, 140, 140);
                    TextRenderer.DrawText(e.Graphics, b.Text, b.Font, b.ClientRectangle, disabledFore,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                };
            }
        }

        internal sealed class ThemedCheckBox : CheckBox
        {
            private bool _dark;
            private Color _surfaceBack;
            private Color _textFore;

            public ThemedCheckBox()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                AutoSize = true;
                UseVisualStyleBackColor = false;
            }

            public void ApplyPalette(bool dark, Color surfaceBack, Color textFore)
            {
                _dark = dark;
                _surfaceBack = surfaceBack;
                _textFore = textFore;
                if (AutoSize)
                    Size = GetPreferredSize(Size.Empty);
                Invalidate();
            }

            public override Size GetPreferredSize(Size proposedSize)
            {
                Size textSize = TextRenderer.MeasureText(Text ?? string.Empty, Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                int width = 14 + 6 + textSize.Width + Padding.Horizontal + 2;
                int height = Math.Max(14, textSize.Height) + Padding.Vertical;
                return new Size(width, height);
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

            protected override void OnPaddingChanged(EventArgs e)
            {
                base.OnPaddingChanged(e);
                if (AutoSize)
                    Size = GetPreferredSize(Size.Empty);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);

                Color surfaceBack = _surfaceBack.IsEmpty ? BackColor : _surfaceBack;
                Color textFore = _textFore.IsEmpty ? ForeColor : _textFore;
                bool enabled = Enabled;
                Color boxBack = _dark ? ScaleColor(surfaceBack, 0.75f) : Color.White;
                Color border = _dark ? Color.FromArgb(104, 110, 120) : Color.FromArgb(150, 156, 166);
                Color check = _dark ? Color.FromArgb(232, 236, 242) : Color.FromArgb(46, 84, 140);

                if (!enabled)
                {
                    boxBack = _dark ? ScaleColor(boxBack, 0.88f) : ScaleColor(boxBack, 0.96f);
                    border = _dark ? Color.FromArgb(74, 80, 88) : Color.FromArgb(178, 182, 188);
                    textFore = _dark ? Color.FromArgb(132, 136, 144) : Color.FromArgb(132, 136, 144);
                    check = _dark ? Color.FromArgb(148, 152, 160) : Color.FromArgb(144, 148, 156);
                }

                int boxSize = 13;
                int boxY = Math.Max(0, (ClientSize.Height - boxSize) / 2);
                var boxRect = new Rectangle(0, boxY, boxSize, boxSize);
                using (var boxBrush = new SolidBrush(boxBack))
                    e.Graphics.FillRectangle(boxBrush, boxRect);
                using (var borderPen = new Pen(border))
                    e.Graphics.DrawRectangle(borderPen, boxRect);

                if (CheckState == CheckState.Checked)
                {
                    using var pen = new Pen(check, 2f)
                    {
                        StartCap = System.Drawing.Drawing2D.LineCap.Round,
                        EndCap = System.Drawing.Drawing2D.LineCap.Round
                    };
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new Point(boxRect.Left + 3, boxRect.Top + 7),
                        new Point(boxRect.Left + 5, boxRect.Top + 9),
                        new Point(boxRect.Left + 10, boxRect.Top + 4)
                    });
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                }
                else if (CheckState == CheckState.Indeterminate)
                {
                    var dashRect = new Rectangle(boxRect.Left + 3, boxRect.Top + 5, boxRect.Width - 6, 3);
                    using var dashBrush = new SolidBrush(check);
                    e.Graphics.FillRectangle(dashBrush, dashRect);
                }

                Rectangle textRect = new Rectangle(boxRect.Right + 6, 0, Math.Max(0, ClientSize.Width - (boxRect.Right + 6)), ClientSize.Height);
                TextFormatFlags textFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                if (!AutoSize)
                    textFlags |= TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(e.Graphics, Text ?? string.Empty, Font, textRect, textFore, textFlags);

                if (Focused && ShowFocusCues)
                {
                    Rectangle focusRect = textRect;
                    focusRect.Width = Math.Max(0, Math.Min(textRect.Width, TextRenderer.MeasureText(Text ?? string.Empty, Font).Width));
                    focusRect.Inflate(1, -2);
                    ControlPaint.DrawFocusRectangle(e.Graphics, focusRect, textFore, BackColor);
                }
            }
        }

        internal sealed class FlatTabPage : Panel
        {
            public FlatTabPage(string text)
            {
                base.Text = text;
                Dock = DockStyle.Fill;
                Padding = new Padding(0);
                Margin = new Padding(0);
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }


        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
        }


        internal sealed class FlatComboBox : ComboBox
        {
            public FlatComboBox()
            {
                SetStyle(ControlStyles.UserPaint, true);
                DrawMode = DrawMode.OwnerDrawFixed;
                DropDownStyle = ComboBoxStyle.DropDownList;
                ItemHeight = 20;
                FlatStyle = FlatStyle.Flat;
                IntegralHeight = false;
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                e.DrawBackground();
                Rectangle bounds = e.Bounds;
                bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                Color back = selected
                    ? (BackColor.GetBrightness() < 0.45f ? Color.FromArgb(76, 96, 124) : Color.FromArgb(210, 224, 242))
                    : BackColor;
                using var bg = new SolidBrush(back);
                e.Graphics.FillRectangle(bg, bounds);
                string text = e.Index >= 0 && e.Index < Items.Count ? Convert.ToString(Items[e.Index]) ?? string.Empty : Text;
                TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(bounds, -6, 0), ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if ((e.State & DrawItemState.Focus) == DrawItemState.Focus)
                    e.DrawFocusRectangle();
            }

            private void PaintComboFace(Graphics g)
            {
                Rectangle rect = ClientRectangle;
                using var back = new SolidBrush(BackColor);
                g.FillRectangle(back, rect);
                Rectangle textRect = new Rectangle(6, 0, Math.Max(0, rect.Width - 24), rect.Height);
                TextRenderer.DrawText(g, Text, Font, textRect, ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Point[] arrow = new[]
                {
                    new Point(rect.Right - 14, rect.Height / 2 - 1),
                    new Point(rect.Right - 8, rect.Height / 2 - 1),
                    new Point(rect.Right - 11, rect.Height / 2 + 3)
                };
                using var arrowBrush = new SolidBrush(ForeColor);
                g.FillPolygon(arrowBrush, arrow);
                using var pen = new Pen(BackColor.GetBrightness() < 0.45f ? Color.FromArgb(84, 92, 104) : Color.FromArgb(180, 186, 196));
                g.DrawRectangle(pen, 0, 0, rect.Width - 1, rect.Height - 1);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                const int WM_PAINT = 0x000F;
                const int WM_SETFOCUS = 0x0007;
                const int WM_KILLFOCUS = 0x0008;
                if (m.Msg is WM_PAINT or WM_SETFOCUS or WM_KILLFOCUS)
                {
                    using var g = CreateGraphics();
                    PaintComboFace(g);
                }
            }
        }

        private sealed class ThemedCallStackTextBox : UserControl
        {
            private readonly RichTextBox _inner;
            private readonly DarkVScrollBar _scrollBar;
            private bool _syncingScroll;

            public ThemedCallStackTextBox()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw, true);

                _inner = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    BorderStyle = BorderStyle.None,
                    Multiline = true,
                    ReadOnly = true,
                    WordWrap = false,
                    ScrollBars = RichTextBoxScrollBars.None,
                    DetectUrls = false,
                    Margin = Padding.Empty,
                    HideSelection = false,
                    Cursor = Cursors.IBeam,
                };
                _inner.VScroll += (_, _) => UpdateScrollBar();
                _inner.TextChanged += (_, _) => UpdateScrollBar();
                _inner.Resize += (_, _) =>
                {
                    NativeMethods.SetRichTextBoxPadding(_inner, 3);
                    UpdateScrollBar();
                };
                _inner.MouseWheel += (_, e) =>
                {
                    ScrollByLines(-Math.Sign(e.Delta) * 3);
                    UpdateScrollBar();
                };
                _inner.MouseDoubleClick += (_, e) => OnMouseDoubleClick(e);

                _scrollBar = new DarkVScrollBar { Dock = DockStyle.Right, SmallChange = 1, LargeChange = 1, Visible = false };
                _scrollBar.Scroll += (_, _) =>
                {
                    if (_syncingScroll)
                        return;
                    int firstVisible = GetFirstVisibleLine();
                    int delta = _scrollBar.Value - firstVisible;
                    if (delta != 0)
                        ScrollByLines(delta);
                    UpdateScrollBar();
                };

                Controls.Add(_inner);
                Controls.Add(_scrollBar);
                MinimumSize = new Size(0, 110);
            }

            public override string Text
            {
                get => _inner?.Text ?? base.Text;
                set
                {
                    base.Text = value;
                    if (_inner != null)
                    {
                        _inner.Text = value ?? string.Empty;
                        UpdateScrollBar();
                    }
                }
            }

            public override Font Font
            {
                get => _inner?.Font ?? base.Font;
                set
                {
                    base.Font = value;
                    if (_inner != null)
                    {
                        _inner.Font = value;
                        UpdateScrollBar();
                    }
                }
            }

            public string[] Lines => _inner.Lines;

            public int GetCharIndexFromPosition(Point p) => _inner.GetCharIndexFromPosition(_inner.PointToClient(PointToScreen(p)));

            public int GetLineFromCharIndex(int index) => _inner.GetLineFromCharIndex(index);

            public void ApplyTheme(bool dark, Color backColor, Color foreColor, Color trackColor, Color thumbColor, Color thumbHotColor)
            {
                BackColor = backColor;
                _inner.BackColor = backColor;
                _inner.ForeColor = foreColor;
                _scrollBar.ApplyTheme(dark, trackColor, thumbColor, thumbHotColor);
                _scrollBar.Invalidate();
                Invalidate(true);
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                NativeMethods.SetRichTextBoxPadding(_inner, 3);
                UpdateScrollBar();
            }

            private void ScrollByLines(int delta)
            {
                if (delta == 0 || !_inner.IsHandleCreated)
                    return;
                NativeMethods.SendMessage(_inner.Handle, NativeMethods.EM_LINESCROLL, IntPtr.Zero, (IntPtr)delta);
            }

            private int GetFirstVisibleLine()
            {
                if (!_inner.IsHandleCreated)
                    return 0;
                return (int)NativeMethods.SendMessage(_inner.Handle, NativeMethods.EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            }

            private void UpdateScrollBar()
            {
                if (_scrollBar == null)
                    return;

                int lineCount = Math.Max(1, _inner.Lines.Length);
                int visibleLines = Math.Max(1, _inner.ClientSize.Height / Math.Max(1, _inner.Font.Height));
                int firstVisible = Math.Max(0, GetFirstVisibleLine());
                bool needScroll = lineCount > visibleLines;

                _syncingScroll = true;
                try
                {
                    _scrollBar.Visible = needScroll;
                    _scrollBar.Minimum = 0;
                    _scrollBar.Maximum = Math.Max(0, lineCount - 1);
                    _scrollBar.LargeChange = visibleLines;
                    _scrollBar.SmallChange = 1;
                    _scrollBar.Value = Math.Min(_scrollBar.Maximum, firstVisible);
                }
                finally
                {
                    _syncingScroll = false;
                }
            }
        }

        internal sealed class CenteredSingleLineTextBox : TextBox
        {
            public CenteredSingleLineTextBox()
            {
                AutoSize = false;
                Multiline = false;
                BorderStyle = BorderStyle.FixedSingle;
                AdjustHeight();
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                AdjustHeight();
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                AdjustHeight();
            }

            private void AdjustHeight()
            {
                Height = Math.Max(18, Font.Height + 4);
            }
        }

        internal sealed class FlatTabHost : UserControl
        {
            public const string TabButtonTag = "FlatTabHost.TabButton";
            private readonly FlowLayoutPanel _tabStrip;
            private readonly Panel _contentHost;
            private readonly List<FlatTabPage> _pages = new();
            private readonly List<Button> _buttons = new();
            private int _selectedIndex = -1;
            private bool _dark;
            private Color _surfaceBack = SystemColors.Control;
            private Color _contentBack = SystemColors.Control;
            private Color _surfaceFore = SystemColors.ControlText;
            private Color? _tabStripBackOverride;
            private Color? _contentBackOverride;
            private readonly Font _tabFontRegular = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.5f, FontStyle.Regular);
            private readonly Font _tabFontSelected = new Font(SystemFonts.MessageBoxFont.FontFamily, 8.5f, FontStyle.Bold);
            private int _tabStripHeight = 24;
            private int _tabButtonHeight = 24;
            private int _tabStripTopPadding;
            private int _tabButtonWidth = 118;
            private bool _paletteInitialized;
            private bool _dimTabs; // when true, tab buttons are rendered dimmer (for sub-tabs)
            private bool _showTabStrip = true;

            public event EventHandler? SelectedIndexChanged;
            public IReadOnlyList<FlatTabPage> Pages => _pages;

            public int TabStripHeight
            {
                get => _tabStripHeight;
                set
                {
                    _tabStripHeight = Math.Max(18, value);
                    UpdateLayoutMetrics();
                }
            }

            public int TabButtonHeight
            {
                get => _tabButtonHeight;
                set
                {
                    _tabButtonHeight = Math.Max(16, value);
                    UpdateLayoutMetrics();
                }
            }

            public int TabStripTopPadding
            {
                get => _tabStripTopPadding;
                set
                {
                    _tabStripTopPadding = Math.Max(0, value);
                    UpdateLayoutMetrics();
                }
            }

            public int TabButtonWidth
            {
                get => _tabButtonWidth;
                set
                {
                    _tabButtonWidth = Math.Max(72, value);
                    foreach (var button in _buttons)
                        button.Width = _tabButtonWidth;
                }
            }

            public bool DimTabs
            {
                get => _dimTabs;
                set { _dimTabs = value; UpdateSelection(); }
            }

            public bool ShowTabStrip
            {
                get => _showTabStrip;
                set
                {
                    if (_showTabStrip == value)
                        return;

                    _showTabStrip = value;
                    _tabStrip.Visible = value;
                    UpdateLayoutMetrics();
                    PerformLayout();
                }
            }

            public Color? TabStripBackColorOverride
            {
                get => _tabStripBackOverride;
                set
                {
                    _tabStripBackOverride = value;
                    _tabStrip.BackColor = value ?? _surfaceBack;
                    Invalidate(true);
                }
            }

            public Color? ContentBackColorOverride
            {
                get => _contentBackOverride;
                set
                {
                    _contentBackOverride = value;
                    _contentHost.BackColor = value ?? _contentBack;
                    Invalidate(true);
                }
            }

            public int SelectedIndex
            {
                get => _selectedIndex;
                set
                {
                    if (value < -1 || value >= _pages.Count || value == _selectedIndex) return;
                    if (value >= 0 && _disabledTabs.Contains(value)) return;
                    _selectedIndex = value;
                    UpdateSelection();
                    SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            private readonly HashSet<int> _disabledTabs = new();

            public void SetTabEnabled(int index, bool enabled)
            {
                if (index < 0 || index >= _buttons.Count) return;
                if (enabled)
                    _disabledTabs.Remove(index);
                else
                    _disabledTabs.Add(index);
                _buttons[index].Enabled = enabled;
                UpdateSelection();
            }

            public void SetTabVisible(int index, bool visible)
            {
                if (index < 0 || index >= _buttons.Count) return;
                _buttons[index].Visible = visible;
                // If hiding the currently selected tab, switch to the first visible tab
                if (!visible && _selectedIndex == index)
                {
                    for (int j = 0; j < _buttons.Count; j++)
                    {
                        if (j != index && _buttons[j].Visible && !_disabledTabs.Contains(j))
                        {
                            SelectedIndex = j;
                            break;
                        }
                    }
                }
                UpdateSelection();
            }

            public FlatTabHost()
            {
                DoubleBuffered = true;
                BackColor = SystemColors.Control;
                ForeColor = SystemColors.ControlText;

                _tabStrip = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    Height = _tabStripHeight,
                    AutoSize = false,
                    WrapContents = false,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(0),
                    Margin = new Padding(0)
                };
                _contentHost = new DoubleBufferedPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(0),
                    Margin = new Padding(0)
                };
                Controls.Add(_contentHost);
                Controls.Add(_tabStrip);
                ApplyPalette(BackColor.GetBrightness() < 0.45f, BackColor, ForeColor);
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                ApplyPalette(_paletteInitialized ? _dark : BackColor.GetBrightness() < 0.45f, _surfaceBack, _surfaceFore);
                UpdateSelection();
            }

            public void AddPage(FlatTabPage page)
            {
                page.Dock = DockStyle.Fill;
                page.Padding = new Padding(0);
                page.Margin = new Padding(0);
                _pages.Add(page);
                _contentHost.Controls.Add(page);
                page.Visible = false;

                var button = CreateTabButton(page.Text, _buttons.Count);
                _buttons.Add(button);
                _tabStrip.Controls.Add(button);
                UpdateLayoutMetrics();

                if (_selectedIndex < 0)
                    SelectedIndex = 0;
                else
                    UpdateSelection();
            }

            public void ApplyPalette(bool dark, Color backColor, Color foreColor)
            {
                _paletteInitialized = true;
                _dark = dark;
                _surfaceBack = backColor;
                _surfaceFore = foreColor;
                _contentBack = _contentBackOverride ?? (dark ? Color.FromArgb(24, 27, 31) : Color.White);
                BackColor = backColor;
                ForeColor = foreColor;
                _tabStrip.BackColor = _tabStripBackOverride ?? backColor;
                _contentHost.BackColor = _contentBack;
                foreach (var page in _pages)
                {
                    if (!Equals(page.Tag, "CodeManagerPage"))
                        page.BackColor = _contentBack;
                    page.ForeColor = foreColor;
                }
                UpdateSelection();
                Invalidate(true);
            }

            private void UpdateLayoutMetrics()
            {
                _tabStrip.Visible = _showTabStrip;
                _tabStrip.Height = _showTabStrip ? _tabStripHeight + _tabStripTopPadding : 0;
                _tabStrip.Padding = _showTabStrip ? new Padding(0, _tabStripTopPadding, 0, 0) : new Padding(0);
                foreach (var button in _buttons)
                {
                    button.Width = _tabButtonWidth;
                    button.Height = _tabButtonHeight;
                    button.Margin = new Padding(0);
                }
                Invalidate(true);
            }

            private Button CreateTabButton(string text, int index)
            {
                var button = new NoFocusTabButton
                {
                    Text = text,
                    Tag = TabButtonTag,
                    Width = _tabButtonWidth,
                    Height = _tabButtonHeight,
                    Margin = new Padding(0),
                    FlatStyle = FlatStyle.Flat,
                    TabStop = false,
                    UseVisualStyleBackColor = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    Padding = new Padding(0),
                };
                button.FlatAppearance.BorderSize = 0;
                button.Click += (_, _) => SelectedIndex = index;
                return button;
            }

            private sealed class NoFocusTabButton : Button
            {
                public bool IsSelectedTab { get; set; }
                public NoFocusTabButton()
                {
                    SetStyle(ControlStyles.Selectable, false);
                }
                protected override bool ShowFocusCues => false;
                protected override void OnPaint(PaintEventArgs e)
                {
                    base.OnPaint(e);
                    if (IsSelectedTab)
                    {
                        var c = ForeColor;
                        var borderColor = Color.FromArgb(
                            (int)(c.R * 0.97f),
                            (int)(c.G * 0.97f),
                            (int)(c.B * 0.97f));
                        using var pen = new Pen(borderColor, 1);
                        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
                    }
                }
            }

            private static Color Brighten(Color c, float factor)
            {
                int r = Math.Min(255, c.R + (int)((255 - c.R) * factor));
                int g = Math.Min(255, c.G + (int)((255 - c.G) * factor));
                int b = Math.Min(255, c.B + (int)((255 - c.B) * factor));
                return Color.FromArgb(r, g, b);
            }

            private static Color Darken(Color c, float factor)
            {
                int r = Math.Max(0, (int)(c.R * (1f - factor)));
                int g = Math.Max(0, (int)(c.G * (1f - factor)));
                int b = Math.Max(0, (int)(c.B * (1f - factor)));
                return Color.FromArgb(r, g, b);
            }

            private void UpdateSelection()
            {
                bool dark = _paletteInitialized ? _dark : BackColor.GetBrightness() < 0.45f;
                Color inactiveBack, inactiveFore, activeBack;
                if (_dimTabs)
                {
                    // Dimmer sub-tab colors
                    inactiveBack = dark ? Color.FromArgb(52, 58, 68) : Color.FromArgb(206, 212, 220);
                    inactiveFore = dark ? Color.FromArgb(180, 186, 196) : Color.FromArgb(96, 100, 108);
                    activeBack   = dark ? Color.FromArgb(72, 80, 92)  : Color.FromArgb(228, 234, 242);
                }
                else
                {
                    inactiveBack = dark ? Color.FromArgb(78, 86, 98) : Color.FromArgb(226, 232, 240);
                    inactiveFore = dark ? Color.FromArgb(228, 232, 238) : Color.FromArgb(76, 80, 88);
                    activeBack   = dark ? Color.FromArgb(98, 108, 124) : Color.FromArgb(242, 246, 252);
                }
                Color activeFore = _surfaceFore;

                for (int i = 0; i < _pages.Count; i++)
                {
                    bool selected = i == _selectedIndex;
                    _pages[i].Visible = selected;
                    if (selected)
                        _pages[i].BringToFront();
                    if (i < _buttons.Count)
                    {
                        var button = _buttons[i];
                        bool disabled = _disabledTabs.Contains(i);
                        Color bg = selected ? activeBack : disabled ? Darken(inactiveBack, 0.15f) : inactiveBack;
                        button.BackColor = bg;
                        button.ForeColor = selected ? activeFore : inactiveFore;
                        button.Font = selected ? _tabFontSelected : _tabFontRegular;
                        button.FlatAppearance.MouseOverBackColor = Brighten(bg, 0.10f);
                        button.FlatAppearance.MouseDownBackColor = Brighten(bg, 0.05f);
                        if (button is NoFocusTabButton nfb)
                            nfb.IsSelectedTab = selected;
                    }
                }
            }
        }

        private sealed class ThemedToolStripRenderer : ToolStripProfessionalRenderer
        {
            private readonly bool _dark;
            private readonly Color _textColor;
            private readonly Color _menuBack;
            private readonly Color _hoverBack;
            private readonly Color _pressedBack;
            private readonly Color _border;

            public ThemedToolStripRenderer(bool dark, Color textColor) : base(new ThemedToolStripColorTable(dark))
            {
                _dark = dark;
                _textColor = textColor;
                _menuBack = dark ? Color.FromArgb(44, 48, 54) : SystemColors.Control;
                _hoverBack = dark ? Color.FromArgb(64, 72, 84) : Color.FromArgb(190, 205, 225);
                _pressedBack = dark ? Color.FromArgb(54, 62, 74) : Color.FromArgb(170, 188, 212);
                _border = dark ? Color.FromArgb(88, 96, 108) : SystemColors.ControlDark;
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using var b = new SolidBrush(_menuBack);
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                Rectangle rect = new Rectangle(Point.Empty, e.Item.Size);
                Color fill = _menuBack;
                if (e.Item.Pressed)
                    fill = _pressedBack;
                else if (e.Item.Selected)
                    fill = _hoverBack;

                using var b = new SolidBrush(fill);
                e.Graphics.FillRectangle(b, rect);

                if ((e.Item.Selected || e.Item.Pressed) && e.Item.Owner is ToolStripDropDown)
                {
                    using var p = new Pen(_border);
                    rect.Width -= 1;
                    rect.Height -= 1;
                    e.Graphics.DrawRectangle(p, rect);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = _textColor;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using var b = new SolidBrush(_menuBack);
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                // Use local (item-relative) coordinates — the Graphics context is clipped to the item's own area.
                int w = e.Item.Width;
                int h = e.Item.Height;

                using var b = new SolidBrush(_menuBack);
                e.Graphics.FillRectangle(b, 0, 0, w, h);

                int y = h / 2;
                using var p = new Pen(_dark ? Color.FromArgb(70, 76, 86) : SystemColors.ControlDark);
                e.Graphics.DrawLine(p, 8, y, w - 8, y);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = _textColor;
                base.OnRenderArrow(e);
            }
        }

        private sealed class ThemedToolStripColorTable : ProfessionalColorTable
        {
            private readonly bool _dark;
            public ThemedToolStripColorTable(bool dark) => _dark = dark;

            private Color Dark(Color d, Color l) => _dark ? d : l;
            private static readonly Color DkBase    = Color.FromArgb(44, 48, 54);
            private static readonly Color DkDeep    = Color.FromArgb(36, 39, 45);
            private static readonly Color DkHover   = Color.FromArgb(64, 72, 84);
            private static readonly Color DkPress   = Color.FromArgb(54, 62, 74);
            private static readonly Color DkBorder  = Color.FromArgb(88, 96, 108);
            private static readonly Color DkSepDk   = Color.FromArgb(88, 96, 108);
            private static readonly Color DkSepLt   = Color.FromArgb(60, 66, 74);
            private static readonly Color DkCheck   = Color.FromArgb(70, 120, 180);

            public override Color MenuStripGradientBegin    => Dark(DkBase, SystemColors.Control);
            public override Color MenuStripGradientEnd      => Dark(DkBase, SystemColors.Control);
            public override Color ToolStripDropDownBackground => Dark(DkDeep, SystemColors.Control);
            public override Color ImageMarginGradientBegin  => ToolStripDropDownBackground;
            public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
            public override Color ImageMarginGradientEnd    => ToolStripDropDownBackground;
            public override Color MenuItemSelected                 => Dark(DkHover, Color.FromArgb(180, 200, 240));
            public override Color MenuItemSelectedGradientBegin    => MenuItemSelected;
            public override Color MenuItemSelectedGradientEnd      => MenuItemSelected;
            public override Color ButtonSelectedHighlight          => Dark(DkHover, SystemColors.Highlight);
            public override Color ButtonSelectedHighlightBorder    => Dark(DkBorder, SystemColors.Highlight);
            public override Color ButtonSelectedGradientBegin      => Dark(DkHover, SystemColors.Highlight);
            public override Color ButtonSelectedGradientEnd        => Dark(DkHover, SystemColors.Highlight);
            public override Color ButtonSelectedGradientMiddle     => Dark(DkHover, SystemColors.Highlight);
            public override Color ButtonPressedHighlight           => Dark(DkPress, SystemColors.Highlight);
            public override Color ButtonPressedHighlightBorder     => Dark(DkBorder, SystemColors.Highlight);
            public override Color ButtonPressedGradientBegin       => Dark(DkPress, SystemColors.Highlight);
            public override Color ButtonPressedGradientEnd         => Dark(DkPress, SystemColors.Highlight);
            public override Color ButtonPressedGradientMiddle      => Dark(DkPress, SystemColors.Highlight);
            public override Color CheckBackground               => Dark(DkCheck, Color.FromArgb(200, 215, 240));
            public override Color CheckPressedBackground        => Dark(DkPress, Color.FromArgb(160, 185, 215));
            public override Color CheckSelectedBackground       => Dark(DkCheck, Color.FromArgb(180, 200, 235));
            public override Color MenuItemBorder                => Dark(DkBorder, SystemColors.Highlight);
            public override Color MenuBorder                    => Dark(DkBorder, SystemColors.ControlDark);
            public override Color SeparatorDark                 => Dark(DkSepDk, SystemColors.ControlDark);
            public override Color SeparatorLight                => Dark(DkSepLt, SystemColors.ControlLight);
            public override Color StatusStripGradientBegin      => MenuStripGradientBegin;
            public override Color StatusStripGradientEnd        => MenuStripGradientEnd;
            public override Color ToolStripBorder               => Dark(DkBorder, SystemColors.ControlDark);
            public override Color ToolStripContentPanelGradientBegin => Dark(DkBase, SystemColors.Control);
            public override Color ToolStripContentPanelGradientEnd   => Dark(DkBase, SystemColors.Control);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S))
            {
                QuickSaveProject();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Shift | Keys.G))
            {
                ShowGoto();
                return true;
            }
            if (keyData == (Keys.Control | Keys.G))
            {
                ShowLabelBrowser();
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                ShowFind();
                return true;
            }
            if (keyData == (Keys.Control | Keys.O))
            {
                OpenBinary();
                return true;
            }
            if (keyData == (Keys.Control | Keys.B))
            {
                if (IsLiveAttached() && _miBreakpointsSidebar != null)
                {
                    _miBreakpointsSidebar.Checked = !_miBreakpointsSidebar.Checked;
                }
                return true;
            }
            if (keyData == (Keys.Control | Keys.I))
            {
                ImportLabelsFromElf();
                return true;
            }
            if (keyData == Keys.G && _disasmList.ContainsFocus)
            {
                ShowGoto();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_ACTIVATE = 1;
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_ACTIVATE;
                return;
            }

            base.WndProc(ref m);

            // Status text is displayed in the right side of the menu bar.
            // Do not draw custom status text over the native title bar.

        }

        private void ScheduleTitleBarStatusDraw(bool immediate = false)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            if (immediate)
            {
                DrawTitleBarStatusText();
                UpdateTitleBarStatusOverlay();
            }

            try
            {
                if (_titleBarStatusDrawTimer != null)
                {
                    _titleBarStatusDrawTimer.Stop();
                    _titleBarStatusDrawTimer.Start();
                }
                else if (!immediate)
                {
                    DrawTitleBarStatusText();
                    UpdateTitleBarStatusOverlay();
                }
            }
            catch { }
        }

        private void UpdateTitleBarStatusOverlay()
        {
            if (IsDisposed || !IsHandleCreated || WindowState == FormWindowState.Minimized)
            {
                try { _titleBarStatusOverlay?.Hide(); } catch { }
                return;
            }

            string status = (_titleBarStatusText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(status))
            {
                try { _titleBarStatusOverlay?.Hide(); } catch { }
                return;
            }

            try
            {
                if (_titleBarStatusOverlay == null || _titleBarStatusOverlay.IsDisposed)
                    _titleBarStatusOverlay = new TitleBarStatusOverlay();

                int buttonWidth = Math.Max(36, SystemInformation.CaptionButtonSize.Width);
                int captionHeight = Math.Max(24, SystemInformation.CaptionHeight);
                int overlayWidth = Math.Min(440, Math.Max(160, Width / 2));
                if (!NativeMethods.GetWindowRect(Handle, out var wr))
                    return;

                int x = wr.Right - (buttonWidth * 3) - overlayWidth - 10;
                int y = wr.Top + 1;
                _titleBarStatusOverlay.SetContent(status, _themeTitleBarBack, _menuPauseStatusActive ? ColJump : _themeTitleBarText, Font, _menuPauseStatusActive);
                _titleBarStatusOverlay.Bounds = new Rectangle(x, y, overlayWidth, captionHeight);
                if (!_titleBarStatusOverlay.Visible)
                    _titleBarStatusOverlay.Show(this);
                _titleBarStatusOverlay.Invalidate();
            }
            catch { }
        }

        private void DrawTitleBarStatusText()
        {
            if (IsDisposed || !IsHandleCreated || WindowState == FormWindowState.Minimized)
                return;

            string status = (_titleBarStatusText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(status))
                return;

            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = NativeMethods.GetWindowDC(Handle);
                if (hdc == IntPtr.Zero) return;

                using Graphics g = Graphics.FromHdc(hdc);
                int buttonWidth = Math.Max(36, SystemInformation.CaptionButtonSize.Width);
                int captionHeight = Math.Max(24, SystemInformation.CaptionHeight);
                int right = Math.Max(120, Width - (buttonWidth * 3) - 10);
                int left = Math.Max(96, right - Math.Min(440, Math.Max(160, Width / 2)));
                var rect = new Rectangle(left, 0, Math.Max(10, right - left), captionHeight + 2);

                using var backBrush = new SolidBrush(_themeTitleBarBack);
                g.FillRectangle(backBrush, rect);
                using var font = _menuPauseStatusActive
                    ? new Font(Font, FontStyle.Bold)
                    : new Font(Font.FontFamily, Font.Size, FontStyle.Regular);
                TextRenderer.DrawText(
                    g,
                    status,
                    font,
                    rect,
                    _menuPauseStatusActive ? ColJump : _themeTitleBarText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            catch { }
            finally
            {
                if (hdc != IntPtr.Zero)
                    NativeMethods.ReleaseDC(Handle, hdc);
            }
        }

        private sealed class TitleBarStatusOverlay : Form
        {
            private string _text = string.Empty;
            private Color _back = SystemColors.ControlDarkDark;
            private Color _fore = Color.White;
            private Font? _baseFont;
            private bool _bold;

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    const int WS_EX_TRANSPARENT = 0x00000020;
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
                    return cp;
                }
            }

            public TitleBarStatusOverlay()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                DoubleBuffered = true;
            }

            public void SetContent(string text, Color back, Color fore, Font baseFont, bool bold)
            {
                _text = text ?? string.Empty;
                _back = back;
                _fore = fore;
                _baseFont = baseFont;
                _bold = bold;
                BackColor = back;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.Clear(_back);
                using var font = _baseFont == null
                    ? new Font(SystemFonts.CaptionFont, _bold ? FontStyle.Bold : FontStyle.Regular)
                    : new Font(_baseFont, _bold ? FontStyle.Bold : FontStyle.Regular);
                TextRenderer.DrawText(
                    e.Graphics,
                    _text,
                    font,
                    ClientRectangle,
                    _fore,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x0084;
                const int HTTRANSPARENT = -1;
                if (m.Msg == WM_NCHITTEST)
                {
                    m.Result = new IntPtr(HTTRANSPARENT);
                    return;
                }
                base.WndProc(ref m);
            }
        }

        // ── Font / Options ──────────────────────────────────────────────

        private int GetDisasmRowSpacingPaddingPixels()
        {
            string rowSpacing = AppSettings.NormalizeRowSpacing(_appSettings?.RowSpacing);
            return rowSpacing switch
            {
                "Compact" => 0,
                "Large" => 2,
                _ => 1,
            };
        }

        private int CalculateDisasmRowHeight()
            => Math.Max(1, _mono.Height + (GetDisasmRowSpacingPaddingPixels() * 2));

        private void ApplyDisasmRowSpacingSetting()
        {
            _disasmRowHeight = CalculateDisasmRowHeight();
            ApplyDisasmViewMetrics();
            ApplyHexViewMetrics();
            if (_asciiBytesBar != null && !_asciiBytesBar.IsDisposed)
            {
                _asciiBytesBar.Height = CurrentDisasmRowHeight;
                _asciiBytesBar.Invalidate();
            }
            AdjustHexSplitter();
            _disasmList?.Invalidate();
            _hexList?.Invalidate();
        }

        private void ApplyFontFromSettings(string fontFamily, float fontSize)
        {
            Font newFont;
            try
            {
                newFont = new Font(fontFamily, fontSize, FontStyle.Regular);
                // Verify the font was actually created with the requested family
                if (!newFont.FontFamily.Name.Equals(fontFamily, StringComparison.OrdinalIgnoreCase))
                {
                    // Requested family not found — fall back to default
                    newFont.Dispose();
                    newFont = new Font(AppSettings.DefaultFontFamily, fontSize, FontStyle.Regular);
                }
            }
            catch
            {
                newFont = new Font(AppSettings.DefaultFontFamily, fontSize, FontStyle.Regular);
            }

            var oldFont = _mono;
            _mono = newFont;
            _disasmRowHeight = CalculateDisasmRowHeight();

            if (_disasmList != null && !_disasmList.IsDisposed)
            {
                ApplyDisasmViewMetrics();
                UpdateDisassemblyColumnWidths();
            }

            if (_hexList != null && !_hexList.IsDisposed)
            {
                ApplyHexViewMetrics();
                AdjustHexSplitter();
            }

            if (_asciiBytesBar != null && !_asciiBytesBar.IsDisposed)
            {
                _asciiBytesBar.Height = CurrentDisasmRowHeight;
                _asciiBytesBar.Invalidate();
            }

            // Refresh any inline edit that may exist
            if (_inlineEdit != null && !_inlineEdit.IsDisposed)
                _inlineEdit.Font = GetStandardTextBoxFont();

            _disasmList?.Invalidate();
            _hexList?.Invalidate();

            if (oldFont != newFont)
            {
                try { oldFont.Dispose(); } catch { }
            }
        }

        private void ShowOptionsDialog()
        {
            using var dlg = new OptionsDialog(_appSettings, _currentTheme == AppTheme.Dark);
            dlg.BackColor = GetOptionsDialogBackColor();
            dlg.ForeColor = _themeFormFore;
            ApplyThemeToControlTree(dlg);
            dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);

            dlg.ApplyRequested += (_, _) =>
            {
                // Apply changes from dialog
                bool fontChanged = _appSettings.FontFamily != dlg.SelectedFontFamily
                                || Math.Abs(_appSettings.FontSize - dlg.SelectedFontSize) > 0.01f;
                bool themeChanged = _appSettings.Theme != dlg.SelectedTheme;
                bool rowSpacingChanged = !string.Equals(AppSettings.NormalizeRowSpacing(_appSettings.RowSpacing), dlg.SelectedRowSpacing, StringComparison.OrdinalIgnoreCase);
                bool refreshRateChanged = _appSettings.RefreshRate != dlg.SelectedRefreshRate;
                bool constantWriteRateChanged = _appSettings.ConstantWriteRate != dlg.SelectedConstantWriteRate;
                bool memoryViewVisibilityChanged = _appSettings.ShowMemoryView != dlg.SelectedShowMemoryView;
                bool mainViewNavigationModeChanged = _appSettings.ShowTabsInTitleBar != dlg.SelectedShowTabsInTitleBar;
                bool debugEndpointChanged = !string.Equals(_appSettings.DebugHost, dlg.SelectedDebugHost, StringComparison.OrdinalIgnoreCase)
                                         || _appSettings.PinePort != dlg.SelectedPinePort
                                         || _appSettings.McpPort != dlg.SelectedMcpPort;

                _appSettings.FontFamily = dlg.SelectedFontFamily;
                _appSettings.FontSize = dlg.SelectedFontSize;
                _appSettings.Theme = dlg.SelectedTheme;
                _appSettings.RowSpacing = dlg.SelectedRowSpacing;
                _appSettings.RefreshRate = dlg.SelectedRefreshRate;
                _appSettings.ConstantWriteRate = dlg.SelectedConstantWriteRate;
                _appSettings.ShowMemoryView = dlg.SelectedShowMemoryView;
                _appSettings.ShowTabsInTitleBar = dlg.SelectedShowTabsInTitleBar;
                _appSettings.DebugHost = dlg.SelectedDebugHost;
                _appSettings.PinePort = dlg.SelectedPinePort;
                _appSettings.McpPort = dlg.SelectedMcpPort;
                _appSettings.Save();

                if (debugEndpointChanged)
                {
                    ApplyDebugConnectionSettings();
                    ResetLiveDebugConnectionsAfterEndpointChange();
                }

                if (fontChanged)
                    ApplyFontFromSettings(_appSettings.FontFamily, _appSettings.FontSize);
                else if (rowSpacingChanged)
                    ApplyDisasmRowSpacingSetting();

                if (refreshRateChanged)
                    UpdateLiveRefreshTimerInterval();

                if (constantWriteRateChanged)
                    UpdateConstantWriteTimerInterval();

                if (mainViewNavigationModeChanged)
                    ApplyMainViewNavigationModeSetting();

                if (memoryViewVisibilityChanged)
                    ApplyMemoryViewVisibilitySetting();

                if (themeChanged)
                {
                    var newTheme = _appSettings.Theme == "Light" ? AppTheme.Light : AppTheme.Dark;
                    ApplyTheme(newTheme, forceFrameRefresh: false);
                    BeginInvoke((Action)(() => RefreshTitleBarTheme(forceFrameRefresh: true)));

                    // Re-theme the options dialog itself so it stays consistent
                    dlg.BackColor = GetOptionsDialogBackColor();
                    dlg.ForeColor = _themeFormFore;
                    ApplyThemeToControlTree(dlg);
                    ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
                }
            };

            dlg.ShowDialog(this);
        }

        private void ApplyMainViewNavigationModeSetting()
        {
            bool showTabsInTitleBar = _appSettings?.ShowTabsInTitleBar ?? AppSettings.DefaultShowTabsInTitleBar;

            if (_mainTabs != null)
                _mainTabs.ShowTabStrip = !showTabsInTitleBar;

            SyncMainViewMenuState();
        }

        private void ApplyMemoryViewVisibilitySetting()
        {
            bool showMemoryView = _appSettings?.ShowMemoryView ?? AppSettings.DefaultShowMemoryView;

            _mainTabs?.SetTabVisible(1, showMemoryView);
            if (_miGoToMemoryView != null)
                _miGoToMemoryView.Visible = showMemoryView;

            if (!showMemoryView && _mainTabs != null && _mainTabs.SelectedIndex == 1)
                _mainTabs.SelectedIndex = 0;

            SyncMainViewMenuState();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _debugServer.Dispose(); } catch { }
                try { _titleBarStatusDrawTimer?.Dispose(); } catch { }
                try { _titleBarStatusOverlay?.Dispose(); } catch { }
                _mono.Dispose();
                _disCts?.Dispose();
                _xrefCts?.Dispose();
            }
            base.Dispose(disposing);
        }


    }
}
