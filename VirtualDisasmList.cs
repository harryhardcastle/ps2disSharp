using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PS2Disassembler
{
    internal sealed class VirtualDisasmList : Control
    {
        internal sealed class VirtualColumn
        {
            public string Text { get; set; } = string.Empty;
            public int Width { get; set; }
        }

        internal sealed class VirtualColumnCollection : List<VirtualColumn>
        {
            public void Add(string text, int width) => Add(new VirtualColumn { Text = text, Width = width });
        }

        internal sealed class VirtualItem
        {
            public int Index { get; }
            public VirtualItem(int index) => Index = index;
        }

        internal sealed class VirtualItemCollection
        {
            private readonly VirtualDisasmList _owner;
            public VirtualItemCollection(VirtualDisasmList owner) => _owner = owner;
            public int Count => _owner.VirtualListSize;
            public VirtualItem this[int index] => new(index);
        }

        internal sealed class SelectedIndexCollection
        {
            private readonly VirtualDisasmList _owner;
            public SelectedIndexCollection(VirtualDisasmList owner) => _owner = owner;
            public int Count => _owner.SelectedIndex >= 0 ? 1 : 0;
            public int this[int index] => index == 0 && _owner.SelectedIndex >= 0
                ? _owner.SelectedIndex
                : throw new ArgumentOutOfRangeException(nameof(index));
            public void Clear() => _owner.SelectedIndex = -1;
            public void Add(int index) => _owner.SelectedIndex = index;
        }

        internal sealed class VirtualHitTestInfo
        {
            public VirtualItem? Item { get; init; }
            public int ColumnIndex { get; init; } = -1;
        }

        internal sealed class VirtualCellPaintEventArgs : EventArgs
        {
            public Graphics Graphics { get; }
            public Rectangle Bounds { get; }
            public int ItemIndex { get; }
            public int ColumnIndex { get; }
            public bool Selected { get; }

            public VirtualCellPaintEventArgs(Graphics graphics, Rectangle bounds, int itemIndex, int columnIndex, bool selected)
            {
                Graphics = graphics;
                Bounds = bounds;
                ItemIndex = itemIndex;
                ColumnIndex = columnIndex;
                Selected = selected;
            }
        }

        internal sealed class VirtualHeaderPaintEventArgs : EventArgs
        {
            public Graphics Graphics { get; }
            public Rectangle Bounds { get; }
            public VirtualColumn Header { get; }
            public int ColumnIndex { get; }

            public VirtualHeaderPaintEventArgs(Graphics graphics, Rectangle bounds, VirtualColumn header, int columnIndex)
            {
                Graphics = graphics;
                Bounds = bounds;
                Header = header;
                ColumnIndex = columnIndex;
            }
        }

        private readonly DarkVScrollBar _vScroll;
        private int _virtualListSize;
        private int _selectedIndex = -1;
        private int _topIndex;
        private int _headerHeight = 18;
        private int _rowHeight = 16;
        private bool _updating;
        private Color _headerBackColor = SystemColors.Control;
        private Color _headerBorderColor = SystemColors.ControlDark;

        // Column resize
        private int _resizingColumn = -1;
        private int _resizeDragStartX;
        private int _resizeDragStartWidth;
        private const int ResizeHitZone = 5;
        public bool AllowColumnResize { get; set; } = true;

        public VirtualDisasmList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);

            TabStop = true;
            DoubleBuffered = true;
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;

            Columns = new VirtualColumnCollection();
            Items = new VirtualItemCollection(this);
            SelectedIndices = new SelectedIndexCollection(this);

            _vScroll = new DarkVScrollBar { SmallChange = 1, LargeChange = 1 };
            _vScroll.Scroll += (_, _) =>
            {
                _topIndex = Math.Max(0, Math.Min(_vScroll.Value, Math.Max(0, VirtualListSize - 1)));
                Invalidate();
            };
            Controls.Add(_vScroll);
            PositionScrollBar();
            UpdateScrollBar();
            UpdateScrollbarTheme();
        }

        public new event EventHandler? SelectedIndexChanged;
        public event EventHandler<VirtualCellPaintEventArgs>? DrawCell;
        public event EventHandler<VirtualHeaderPaintEventArgs>? DrawHeader;
        public event RetrieveVirtualItemEventHandler? RetrieveVirtualItem;
        public event EventHandler? ColumnWidthChanged;

        public VirtualColumnCollection Columns { get; }
        public VirtualItemCollection Items { get; }
        public SelectedIndexCollection SelectedIndices { get; }

        public bool FullRowSelect { get; set; }
        public bool GridLines { get; set; }
        public bool MultiSelect { get; set; }
        public bool VirtualMode { get; set; }
        public bool OwnerDraw { get; set; }
        public bool Scrollable { get; set; } = true;
        public View View { get; set; } = View.Details;
        public ColumnHeaderStyle HeaderStyle { get; set; } = ColumnHeaderStyle.Nonclickable;
        public BorderStyle BorderStyle { get; set; } = BorderStyle.None;
        public bool SuppressDefaultSelection { get; set; }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            if (!IsHandleCreated && _vScroll == null)
                return;
            UpdateScrollbarTheme();
        }

        protected override void OnForeColorChanged(EventArgs e)
        {
            base.OnForeColorChanged(e);
            if (!IsHandleCreated && _vScroll == null)
                return;
            UpdateScrollbarTheme();
        }

        private void UpdateScrollbarTheme()
        {
            if (_vScroll == null)
                return;

            bool dark = BackColor.GetBrightness() < 0.45f;
            _vScroll.ApplyTheme(
                dark,
                dark ? Color.FromArgb(45, 49, 56) : SystemColors.Control,
                dark ? Color.FromArgb(78, 86, 98) : SystemColors.ControlDark,
                dark ? Color.FromArgb(116, 126, 142) : SystemColors.ControlDarkDark);
        }

        public int HeaderHeight
        {
            get => _headerHeight;
            set { _headerHeight = Math.Max(0, value); PositionScrollBar(); UpdateScrollBar(); Invalidate(); }
        }

        public int RowHeight
        {
            get => _rowHeight;
            set { _rowHeight = Math.Max(1, value); UpdateScrollBar(); Invalidate(); }
        }

        public Color HeaderBackColor
        {
            get => _headerBackColor;
            set { _headerBackColor = value; Invalidate(); }
        }

        public Color HeaderBorderColor
        {
            get => _headerBorderColor;
            set { _headerBorderColor = value; Invalidate(); }
        }

        public int VirtualListSize
        {
            get => _virtualListSize;
            set
            {
                _virtualListSize = Math.Max(0, value);
                if (_selectedIndex >= _virtualListSize) _selectedIndex = _virtualListSize - 1;
                if (_topIndex >= _virtualListSize) _topIndex = Math.Max(0, _virtualListSize - 1);
                UpdateScrollBar();
                Invalidate();
            }
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                int newValue = Math.Max(-1, Math.Min(value, Math.Max(-1, VirtualListSize - 1)));
                if (_selectedIndex == newValue) return;
                _selectedIndex = newValue;
                if (!_updating)
                    SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        public VirtualItem? TopItem => VirtualListSize > 0 ? new VirtualItem(_topIndex) : null;
        public VirtualItem? FocusedItem { get; set; }

        public int TopIndex
        {
            get => _topIndex;
            set
            {
                int maxTop = Math.Max(0, VirtualListSize - Math.Max(1, VisibleRowCapacity));
                _topIndex = Math.Max(0, Math.Min(value, maxTop));
                UpdateScrollBar();
                Invalidate();
            }
        }

        public int VisibleRowCapacity => Math.Max(1, (Math.Max(0, ClientSize.Height - HeaderHeight) / Math.Max(1, RowHeight)));
        public bool HasVerticalScrollbar => _vScroll.Visible;

        public void BeginUpdate() => _updating = true;
        public void EndUpdate() { _updating = false; UpdateScrollBar(); Invalidate(); }
        public void RedrawItems(int start, int end, bool invalidateOnly)
        {
            if (start > end)
                return;

            start = Math.Max(start, _topIndex);
            end = Math.Min(end, _topIndex + VisibleRowCapacity - 1);
            if (start > end)
                return;

            int y = HeaderHeight + ((start - _topIndex) * RowHeight);
            int height = ((end - start) + 1) * RowHeight;
            Invalidate(new Rectangle(0, y, ContentWidth, height));
            if (!invalidateOnly)
                Update();
        }

        public ListViewItem? GetVirtualItem(int index)
        {
            if (index < 0 || index >= VirtualListSize)
                return null;

            if (RetrieveVirtualItem == null)
                return null;

            var args = new RetrieveVirtualItemEventArgs(index);
            RetrieveVirtualItem(this, args);
            return args.Item;
        }

        public void EnsureVisible(int index)
        {
            if (index < 0 || index >= VirtualListSize) return;
            if (index < _topIndex) TopIndex = index;
            else
            {
                int bottom = _topIndex + VisibleRowCapacity - 1;
                if (index > bottom)
                    TopIndex = index - VisibleRowCapacity + 1;
            }
        }

        public Rectangle GetItemRect(int index)
        {
            if (index < 0 || index >= VirtualListSize) return Rectangle.Empty;
            int y = HeaderHeight + ((index - _topIndex) * RowHeight);
            return new Rectangle(0, y, ContentWidth, RowHeight);
        }

        public Rectangle GetSubItemRect(int row, int col)
        {
            Rectangle rowRect = GetItemRect(row);
            if (rowRect == Rectangle.Empty || col < 0 || col >= Columns.Count) return Rectangle.Empty;
            int x = 0;
            for (int i = 0; i < col; i++) x += Columns[i].Width;
            return new Rectangle(x, rowRect.Y, Columns[col].Width, rowRect.Height);
        }

        public VirtualHitTestInfo HitTest(Point p)
        {
            if (p.Y < HeaderHeight || p.X < 0 || p.X >= ContentWidth)
                return new VirtualHitTestInfo();

            int row = _topIndex + ((p.Y - HeaderHeight) / Math.Max(1, RowHeight));
            if (row < 0 || row >= VirtualListSize)
                return new VirtualHitTestInfo();

            int x = 0;
            for (int i = 0; i < Columns.Count; i++)
            {
                x += Columns[i].Width;
                if (p.X < x)
                    return new VirtualHitTestInfo { Item = new VirtualItem(row), ColumnIndex = i };
            }

            return new VirtualHitTestInfo { Item = new VirtualItem(row), ColumnIndex = Columns.Count - 1 };
        }


        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or
                Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or
                Keys.Enter or Keys.Space)
                return true;

            return base.IsInputKey(keyData);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PositionScrollBar();
            UpdateScrollBar();
        }

        private void PositionScrollBar()
        {
            if (_vScroll == null) return;
            int sbWidth = _vScroll.Width > 0 ? _vScroll.Width : SystemInformation.VerticalScrollBarWidth;
            int hdrH = HeaderStyle == ColumnHeaderStyle.None ? 0 : _headerHeight;
            _vScroll.SetBounds(
                ClientSize.Width - sbWidth,
                hdrH,
                sbWidth,
                Math.Max(0, ClientSize.Height - hdrH));
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int lines = SystemInformation.MouseWheelScrollLines;
            int deltaRows = Math.Sign(-e.Delta) * Math.Max(1, lines);
            TopIndex += deltaRows;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            // Column resize: left-click near a column divider in the header
            if (AllowColumnResize &&
                e.Button == MouseButtons.Left &&
                e.Y < HeaderHeight &&
                HeaderStyle != ColumnHeaderStyle.None)
            {
                int col = GetResizeColumnAt(e.X);
                if (col >= 0)
                {
                    _resizingColumn      = col;
                    _resizeDragStartX    = e.X;
                    _resizeDragStartWidth = Columns[col].Width;
                    Capture = true;
                    return;
                }
            }

            if (e.Button is not (MouseButtons.Left or MouseButtons.Right or MouseButtons.Middle))
                return;

            if (SuppressDefaultSelection)
                return;

            var hit = HitTest(e.Location);
            if (hit.Item != null)
                SelectedIndex = hit.Item.Index;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_resizingColumn >= 0)
            {
                int newWidth = Math.Max(20, _resizeDragStartWidth + (e.X - _resizeDragStartX));
                if (Columns[_resizingColumn].Width != newWidth)
                {
                    Columns[_resizingColumn].Width = newWidth;
                    Invalidate();
                    ColumnWidthChanged?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            // Show resize cursor when hovering near a column divider in the header
            if (AllowColumnResize && HeaderStyle != ColumnHeaderStyle.None && e.Y >= 0 && e.Y < HeaderHeight)
                Cursor = GetResizeColumnAt(e.X) >= 0 ? Cursors.VSplit : Cursors.Default;
            else if (Cursor != Cursors.Default)
                Cursor = Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_resizingColumn >= 0)
            {
                _resizingColumn = -1;
                Capture = false;
                Cursor  = Cursors.Default;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_resizingColumn < 0)
                Cursor = Cursors.Default;
        }

        /// <summary>Returns the index of the column whose right edge is within the resize hit zone of mouseX, or -1.</summary>
        private int GetResizeColumnAt(int mouseX)
        {
            int x = 0;
            // Exclude the last column from resize detection — it auto-fills and should not show a resize edge.
            int lastResizable = Columns.Count - 2;
            for (int i = 0; i <= lastResizable && i < Columns.Count; i++)
            {
                x += Columns[i].Width;
                if (Math.Abs(mouseX - x) <= ResizeHitZone)
                    return i;
            }
            return -1;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (VirtualListSize <= 0) return;

            int idx = SelectedIndex >= 0 ? SelectedIndex : 0;
            switch (e.KeyCode)
            {
                case Keys.Up: idx = Math.Max(0, idx - 1); e.Handled = true; break;
                case Keys.Down: idx = Math.Min(VirtualListSize - 1, idx + 1); e.Handled = true; break;
                case Keys.PageUp: idx = Math.Max(0, idx - VisibleRowCapacity); e.Handled = true; break;
                case Keys.PageDown: idx = Math.Min(VirtualListSize - 1, idx + VisibleRowCapacity); e.Handled = true; break;
                case Keys.Home: idx = 0; e.Handled = true; break;
                case Keys.End: idx = VirtualListSize - 1; e.Handled = true; break;
                default: return;
            }

            SelectedIndex = idx;
            EnsureVisible(idx);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            using var borderPen = new Pen(_headerBorderColor);
            using var headerBack = new SolidBrush(_headerBackColor);

            if (HeaderStyle != ColumnHeaderStyle.None)
            {
                // Fill entire header row background first (covers area above scrollbar)
                e.Graphics.FillRectangle(headerBack, new Rectangle(0, 0, ClientSize.Width, HeaderHeight));
                int hx = 0;
                for (int i = 0; i < Columns.Count; i++)
                {
                    var colRect = new Rectangle(hx, 0, Columns[i].Width, HeaderHeight);
                    DrawHeader?.Invoke(this, new VirtualHeaderPaintEventArgs(e.Graphics, colRect, Columns[i], i));
                    hx += Columns[i].Width;
                }
                e.Graphics.DrawLine(borderPen, 0, HeaderHeight - 1, ClientSize.Width, HeaderHeight - 1);
            }

            int first = _topIndex;
            int visible = VisibleRowCapacity + 1;
            int last = Math.Min(VirtualListSize - 1, first + visible - 1);

            for (int row = first; row <= last; row++)
            {
                int y = HeaderHeight + ((row - first) * RowHeight);
                int x = 0;
                bool selected = row == SelectedIndex;

                for (int col = 0; col < Columns.Count; col++)
                {
                    var cellRect = new Rectangle(x, y, Columns[col].Width, RowHeight);
                    DrawCell?.Invoke(this, new VirtualCellPaintEventArgs(e.Graphics, cellRect, row, col, selected));
                    x += Columns[col].Width;
                }
            }
        }

        private int ContentWidth => Math.Max(0, ClientSize.Width - (_vScroll.Visible ? _vScroll.Width : 0));

        private void UpdateScrollBar()
        {
            int totalRows = VirtualListSize;
            int visibleRows = VisibleRowCapacity;
            bool show = Scrollable && totalRows > visibleRows;
            _vScroll.Visible = show;

            if (!show)
            {
                _topIndex = 0;
                return;
            }

            _vScroll.Minimum = 0;
            _vScroll.SmallChange = 1;
            _vScroll.LargeChange = Math.Max(1, visibleRows);
            _vScroll.Maximum = Math.Max(0, totalRows - 1);

            int maxValue = Math.Max(_vScroll.Minimum, _vScroll.Maximum - _vScroll.LargeChange + 1);
            _topIndex = Math.Max(0, Math.Min(_topIndex, maxValue));
            _vScroll.Value = Math.Max(_vScroll.Minimum, Math.Min(_topIndex, maxValue));
        }
    }

    /// <summary>
    /// ListView subclass with flicker-free painting and custom mouse handling
    /// for byte-level selection in the hex view.
    /// </summary>
    internal sealed class FlickerFreeListView : ListView
    {
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_LBUTTONUP = 0x0202;

        /// <summary>When true, left-clicks do not trigger default item selection.</summary>
        public bool SuppressDefaultSelection { get; set; }

        public FlickerFreeListView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int hoverMask = NativeMethods.LVS_EX_TRACKSELECT |
                                NativeMethods.LVS_EX_ONECLICKACTIVATE |
                                NativeMethods.LVS_EX_TWOCLICKACTIVATE |
                                NativeMethods.LVS_EX_UNDERLINEHOT |
                                NativeMethods.LVS_EX_UNDERLINECOLD |
                                NativeMethods.LVS_EX_INFOTIP |
                                NativeMethods.LVS_EX_LABELTIP;
                int styleMask = NativeMethods.LVS_EX_DOUBLEBUFFER | hoverMask;
                NativeMethods.SendMessage(
                    Handle,
                    NativeMethods.LVM_SETEXTENDEDLISTVIEWSTYLE,
                    (IntPtr)styleMask,
                    (IntPtr)NativeMethods.LVS_EX_DOUBLEBUFFER);
            }
            catch
            {
                // Best-effort only. The managed double-buffering path is still enabled.
            }
        }

        protected override void WndProc(ref Message m)
        {
            // Prevent the ListView from doing built-in row selection on left click;
            // the owner handles byte-level selection instead.
            if (SuppressDefaultSelection)
            {
                if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK)
                {
                    Focus();
                    var (x, y) = ExtractPoint(m.LParam);
                    OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
                    return;
                }
                if (m.Msg == WM_LBUTTONUP)
                {
                    var (x, y) = ExtractPoint(m.LParam);
                    OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
                    return;
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Called by OwnerDraw DrawItem — fills the full row width (including area
        /// to the right of columns) with BackColor so no un-erased gap remains.
        /// Also fills the margin below the last row.
        /// </summary>
        public void PaintRowBackground(DrawListViewItemEventArgs e)
        {
            int cw = Math.Max(1, ClientSize.Width);
            var fullRow = new Rectangle(0, e.Bounds.Y, cw, e.Bounds.Height);
            using var br = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(br, fullRow);

            int lastVirtualRow = Math.Max(0, VirtualListSize - 1);
            if (e.ItemIndex >= lastVirtualRow)
            {
                int bottomY = e.Bounds.Bottom;
                int ch = ClientSize.Height;
                if (bottomY < ch)
                    e.Graphics.FillRectangle(br, 0, bottomY, cw, ch - bottomY);
            }
        }

        private static (int x, int y) ExtractPoint(IntPtr lParam)
        {
            int lp = (int)lParam;
            return ((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        }
    }

    internal sealed class DarkVScrollBar : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _largeChange = 10;
        private int _smallChange = 1;
        private int _value;
        private Color _trackColor = SystemColors.Control;
        private Color _thumbColor = SystemColors.ControlDark;
        private Color _thumbHotColor = SystemColors.ControlDarkDark;
        private bool _thumbHot;
        private bool _dragging;
        private int _dragOffsetY;

        public event ScrollEventHandler? Scroll;

        public int Minimum { get => _minimum; set { _minimum = value; CoerceValue(); Invalidate(); } }
        public int Maximum { get => _maximum; set { _maximum = Math.Max(value, _minimum); CoerceValue(); Invalidate(); } }
        public int LargeChange { get => _largeChange; set { _largeChange = Math.Max(1, value); CoerceValue(); Invalidate(); } }
        public int SmallChange { get => _smallChange; set { _smallChange = Math.Max(1, value); Invalidate(); } }
        public new int Width { get => base.Width; set => base.Width = Math.Max(12, value); }

        public int Value
        {
            get => _value;
            set
            {
                int coerced = Coerce(value);
                if (coerced == _value) return;
                int old = _value;
                _value = coerced;
                Invalidate();
                Scroll?.Invoke(this, new ScrollEventArgs(ScrollEventType.ThumbPosition, old, _value, ScrollOrientation.VerticalScroll));
            }
        }

        public DarkVScrollBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            base.Width = SystemInformation.VerticalScrollBarWidth;
            Cursor = Cursors.Default;
            TabStop = false;
        }

        public void ApplyTheme(bool dark, Color trackColor, Color thumbColor, Color thumbHotColor)
        {
            _trackColor = trackColor;
            _thumbColor = thumbColor;
            _thumbHotColor = thumbHotColor;
            Invalidate();
        }

        private int Coerce(int value)
        {
            int maxValue = Math.Max(_minimum, _maximum - _largeChange + 1);
            return Math.Max(_minimum, Math.Min(value, maxValue));
        }

        private void CoerceValue() => _value = Coerce(_value);

        private Rectangle GetThumbRect()
        {
            int trackHeight = Math.Max(1, ClientSize.Height);
            int range = Math.Max(1, _maximum - _minimum + 1);
            int thumbHeight = Math.Max(24, (int)Math.Round(trackHeight * (_largeChange / (double)(range + _largeChange - 1))));
            thumbHeight = Math.Min(trackHeight, thumbHeight);
            int maxValue = Math.Max(_minimum, _maximum - _largeChange + 1);
            int movable = Math.Max(0, trackHeight - thumbHeight);
            int y = maxValue == _minimum ? 0 : (int)Math.Round((Value - _minimum) / (double)Math.Max(1, maxValue - _minimum) * movable);
            return new Rectangle(0, y, ClientSize.Width, thumbHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var back = new SolidBrush(_trackColor);
            e.Graphics.FillRectangle(back, ClientRectangle);
            Rectangle thumb = GetThumbRect();
            Color thumbFill = _thumbHot || _dragging ? _thumbHotColor : _thumbColor;
            using var thumbBrush = new SolidBrush(thumbFill);
            e.Graphics.FillRectangle(thumbBrush, thumb);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle thumb = GetThumbRect();
            bool hot = thumb.Contains(e.Location);
            if (hot != _thumbHot && !_dragging)
            {
                _thumbHot = hot;
                Invalidate();
            }

            if (_dragging)
            {
                int trackHeight = Math.Max(1, ClientSize.Height);
                int thumbHeight = thumb.Height;
                int movable = Math.Max(0, trackHeight - thumbHeight);
                int pos = Math.Max(0, Math.Min(e.Y - _dragOffsetY, movable));
                int maxValue = Math.Max(_minimum, _maximum - _largeChange + 1);
                Value = maxValue == _minimum ? _minimum : _minimum + (int)Math.Round(pos / (double)Math.Max(1, movable) * (maxValue - _minimum));
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_dragging && _thumbHot)
            {
                _thumbHot = false;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Rectangle thumb = GetThumbRect();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffsetY = e.Y - thumb.Y;
                Capture = true;
                _thumbHot = true;
                Invalidate();
                return;
            }

            if (e.Y < thumb.Top)
                Value -= LargeChange;
            else if (e.Y > thumb.Bottom)
                Value += LargeChange;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragging = false;
                Capture = false;
                _thumbHot = GetThumbRect().Contains(e.Location);
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Value -= Math.Sign(e.Delta) * SmallChange;
        }
    }

}