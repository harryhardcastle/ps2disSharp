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
// ══════════════════════════════════════════════════════════════════
        // Hex panel — fully virtual, no ListViewItem allocation
        // ══════════════════════════════════════════════════════════════════

        internal static void EnsureVirtualItemHasAllSubItems(ListView owner, ListViewItem item)
        {
            int needed = owner.Columns.Count;
            while (item.SubItems.Count < needed)
                item.SubItems.Add(string.Empty);
        }

        internal static void EnsureVirtualItemHasAllSubItems(VirtualDisasmList owner, ListViewItem item)
        {
            int needed = owner.Columns.Count;
            while (item.SubItems.Count < needed)
                item.SubItems.Add(string.Empty);
        }

        // Cached disassembler for use in the UI thread hot-path (ResolveRowForDisplay)
        // Recreated whenever _baseAddr or _useAbi changes.
        private MipsDisassemblerEx? _cachedDisasm;
        private uint  _cachedDisasmBase;
        private bool  _cachedDisasmUseAbi;

        private MipsDisassemblerEx GetCachedDisasm()
        {
            if (_cachedDisasm == null || _cachedDisasmBase != _baseAddr || _cachedDisasmUseAbi != _useAbi)
            {
                _cachedDisasm     = new MipsDisassemblerEx(new DisassemblerOptions { BaseAddress = _baseAddr, UseAbiNames = _useAbi });
                _cachedDisasmBase = _baseAddr;
                _cachedDisasmUseAbi = _useAbi;
            }
            return _cachedDisasm;
        }

        private DisassemblyRow ResolveRowForDisplay(SlimRow row)
        {
            if (_fileData != null && row.DataSub != DataKind.None)
            {
                uint aligned = row.Address & ~3u;
                long off = (long)(aligned - _baseAddr);
                if (off >= 0 && off + 4 <= _fileData.Length)
                {
                    uint word = BitConverter.ToUInt32(_fileData, (int)off);
                    return row.DataSub switch
                    {
                        DataKind.Word  => new DisassemblyRow { Address = aligned, Word = word, Mnemonic = ".word", Operands = FormatWordValue(word), Kind = InstructionType.Data, Target = row.Target },
                        DataKind.Half  => new DisassemblyRow { Address = row.Address, Word = (ushort)(word >> (((int)(row.Address & 2u)) * 8)), Mnemonic = ".half", Operands = $"{(ushort)(word >> (((int)(row.Address & 2u)) * 8)):X4}({(short)(ushort)(word >> (((int)(row.Address & 2u)) * 8))})", Kind = InstructionType.Data, Target = row.Target },
                        DataKind.Byte  => new DisassemblyRow { Address = row.Address, Word = (byte)(word >> (((int)(row.Address & 3u)) * 8)), Mnemonic = ".byte", Operands = FormatByteValue((byte)(word >> (((int)(row.Address & 3u)) * 8))), Kind = InstructionType.Data, Target = row.Target },
                        DataKind.Float => new DisassemblyRow { Address = aligned, Word = word, Mnemonic = ".float", Operands = BitConverter.Int32BitsToSingle(unchecked((int)word)).ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture), Kind = InstructionType.Data, Target = row.Target },
                        _              => new DisassemblyRow { Address = row.Address, Word = row.Word, Kind = row.Kind, Target = row.Target },
                    };
                }
            }
            if (row.Kind == InstructionType.Data)
                return new DisassemblyRow { Address = row.Address, Word = row.Word, Mnemonic = row.Mnemonic, Kind = row.Kind, Target = row.Target };
            return GetCachedDisasm().DisassembleSingleWord(row.Word, row.Address);
        }

        private SlimRow RefreshRowFromMemory(SlimRow row)
        {
            if (_fileData == null)
                return row;

            uint aligned = row.Address & ~3u;
            long off = (long)(aligned - _baseAddr);
            if (off < 0 || off + 4 > _fileData.Length)
                return row;

            uint word = BitConverter.ToUInt32(_fileData, (int)off);

            return row.DataSub switch
            {
                DataKind.Word  => SlimRow.DataRow(aligned, word, DataKind.Word, row.Target),
                DataKind.Half  => SlimRow.DataRow(row.Address, (ushort)(word >> (((int)(row.Address & 2u)) * 8)), DataKind.Half, row.Target),
                DataKind.Byte  => SlimRow.DataRow(row.Address, (byte)(word >> (((int)(row.Address & 3u)) * 8)), DataKind.Byte, row.Target),
                DataKind.Float => SlimRow.DataRow(aligned, word, DataKind.Float, row.Target),
                _              => SlimRowFromWord(word, row.Address),
            };
        }

        private SlimRow SlimRowFromWord(uint word, uint address)
        {
            var (kind, target) = GetCachedDisasm().DecodeKindAndTarget(word, address);
            return new SlimRow { Address = address, Word = word, Kind = kind, Target = target };
        }

        private int RefreshVisibleDisassemblyRows(int start, int end)
        {
            if (_fileData == null || _rows.Count == 0)
                return 0;

            start = Math.Max(0, start);
            end = Math.Min(_rows.Count - 1, end);

            int changedRows = 0;
            for (int i = start; i <= end; i++)
            {
                var before = _rows[i];
                var after = RefreshRowFromMemory(before);
                if (after.Word != before.Word ||
                    after.Kind != before.Kind ||
                    after.DataSub != before.DataSub ||
                    after.Target != before.Target)
                {
                    changedRows++;
                }
                _rows[i] = after;
            }

            return changedRows;
        }

        private bool IsLiveAttached()
        {
            return _fileData != null && (_pineAvailable || (_liveProcId != 0 && _eeHostAddr != 0));
        }

        private DisassemblyRow GetLiveDisplayRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count)
                return default;

            return ResolveRowForDisplay(_rows[rowIndex]);
        }

        private bool TryGetRowIndexByAddress(uint addr, out int idx)
        {
            if (!_addrIndexDirty && _addrToRow != null && _addrToRow.TryGetValue(addr, out idx))
                return true;

            int exact = FindNearestRow(addr);
            if (exact >= 0 && exact < _rows.Count && _rows[exact].Address == addr)
            {
                idx = exact;
                return true;
            }

            idx = -1;
            return false;
        }

        private uint ResolveRegisterAddressValue(int rowIdx, uint reg)
            => ResolveRegisterAddressValue(rowIdx, reg, depth: 0);

        private uint ResolveRegisterAddressValue(int rowIdx, uint reg, int depth)
        {
            if (reg == 0 || depth > 6)
                return 0;

            for (int i = rowIdx - 1; i >= Math.Max(0, rowIdx - 32); i--)
            {
                uint w = _rows[i].Word;
                if (TryResolveRegisterWriteValue(i, reg, depth, w, out uint resolvedValue))
                    return resolvedValue;

                if (WritesRegister(w, reg))
                    return 0;
            }

            return 0;
        }

        private bool TryResolveRegisterWriteValue(int rowIdx, uint targetReg, int depth, uint word, out uint value)
        {
            value = 0;

            uint op = (word >> 26) & 0x3F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            uint fn = word & 0x3F;
            int si = (short)(word & 0xFFFF);
            uint ui = word & 0xFFFF;

            if (op == 0x0F && rt == targetReg)
            {
                value = ui << 16;
                return true;
            }

            if (rt == targetReg && op is 0x08 or 0x09 or 0x0D)
            {
                uint baseValue;
                if (rs == 0)
                {
                    baseValue = 0;
                }
                else
                {
                    baseValue = ResolveRegisterAddressValue(rowIdx, rs, depth + 1);
                    if (baseValue == 0)
                        return false;
                }

                value = op switch
                {
                    0x0D => baseValue | ui,
                    0x08 or 0x09 => baseValue + unchecked((uint)si),
                    _ => 0u,
                };
                return true;
            }

            if (op == 0x00 && rd == targetReg)
            {
                switch (fn)
                {
                    case 0x21: // addu
                    case 0x2D: // daddu
                        if (rt == 0 && rs != 0)
                        {
                            value = ResolveRegisterAddressValue(rowIdx, rs, depth + 1);
                            return value != 0;
                        }
                        if (rs == 0 && rt != 0)
                        {
                            value = ResolveRegisterAddressValue(rowIdx, rt, depth + 1);
                            return value != 0;
                        }
                        break;
                    case 0x25: // or
                        if (rt == 0 && rs != 0)
                        {
                            value = ResolveRegisterAddressValue(rowIdx, rs, depth + 1);
                            return value != 0;
                        }
                        if (rs == 0 && rt != 0)
                        {
                            value = ResolveRegisterAddressValue(rowIdx, rt, depth + 1);
                            return value != 0;
                        }
                        break;
                }
            }

            return false;
        }

        private string GetHexCellText(int rowIndex, int columnIndex)
        {
            if (_fileData == null || rowIndex < 0)
                return string.Empty;

            int off = rowIndex * 16;
            if (off >= _fileData.Length && columnIndex != 0)
                return columnIndex == 17 ? string.Empty : "  ";

            if (columnIndex == 0)
                return (_baseAddr + (uint)off).ToString("X8");

            if (columnIndex >= 1 && columnIndex <= 16)
            {
                int idx = off + (columnIndex - 1);
                return idx < _fileData.Length ? _fileData[idx].ToString("X2") : "  ";
            }

            if (columnIndex == 17)
            {
                var ascii = new StringBuilder(16);
                for (int c = 0; c < 16 && off + c < _fileData.Length; c++)
                {
                    byte b = _fileData[off + c];
                    ascii.Append(b >= 32 && b < 127 ? (char)b : '·');
                }
                return ascii.ToString();
            }

            return string.Empty;
        }

        private void SyncHexToAddr(uint addr)
        {
            if (_fileData == null)
                return;

            int maxRow = Math.Max(0, ((_fileData.Length + 15) / 16) - Math.Max(1, _hexList.VisibleRowCapacity));
            int rowIndex = Math.Max(0, Math.Min((int)(addr >= _baseAddr ? (addr - _baseAddr) / 16 : 0), maxRow));
            _hexList.TopIndex = rowIndex;
            _hexViewOffset = rowIndex * 16;
            _hexList.SelectedIndexChanged -= OnHexSelChanged;
            _hexList.SelectedIndices.Clear();
            _hexList.SelectedIndices.Add(rowIndex);
            _hexList.SelectedIndexChanged += OnHexSelChanged;
            ApplyHexViewMetrics();
            _hexList.Invalidate();
            UpdateHexScrollBar();
        }

        private void GoToMemoryViewAddress(uint addr)
        {
            if (!(_appSettings?.ShowMemoryView ?? AppSettings.DefaultShowMemoryView))
                return;

            if (_mainTabs != null && _mainTabs.Pages.Count > 1)
                _mainTabs.SelectedIndex = 1;
            AdjustHexSplitter();
            SyncHexToAddr(addr);
            _hexList.Focus();
        }

        private void GoToSelectedRowInMemoryView()
        {
            if (_disasmList.SelectedIndices.Count == 0) return;
            int idx = _disasmList.SelectedIndices[0];
            if (idx < 0 || idx >= _rows.Count) return;
            GoToMemoryViewAddress(_rows[idx].Address);
        }

        private static int GetDisassemblyEditWidthBytes(SlimRow row)
        {
            return row.DataSub switch
            {
                DataKind.Byte => 1,
                DataKind.Half => 2,
                _ => 4,
            };
        }

        private static uint MaskValueForWidth(uint value, int widthBytes)
        {
            return widthBytes switch
            {
                1 => value & 0xFFu,
                2 => value & 0xFFFFu,
                _ => value,
            };
        }

        private static byte[] GetLittleEndianBytesForWidth(uint value, int widthBytes)
        {
            value = MaskValueForWidth(value, widthBytes);
            return widthBytes switch
            {
                1 => new[] { (byte)value },
                2 => new[] { (byte)value, (byte)(value >> 8) },
                _ => BitConverter.GetBytes(value),
            };
        }

        private void ApplyTypedDisassemblyValueChange(int rowIndex, uint newValue)
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count)
                return;

            var row = _rows[rowIndex];
            int widthBytes = GetDisassemblyEditWidthBytes(row);
            uint maskedNewValue = MaskValueForWidth(newValue, widthBytes);

            uint currentValue = MaskValueForWidth(row.Word, widthBytes);
            TrackOriginalOpcodeBeforeUserChange(row.Address, currentValue, maskedNewValue);
            if (currentValue == maskedNewValue)
                return;

            byte[] bytes = GetLittleEndianBytesForWidth(maskedNewValue, widthBytes);

            if (_fileData != null)
            {
                long off = (long)(row.Address - _baseAddr);
                if (off >= 0 && off + widthBytes <= _fileData.Length)
                    Buffer.BlockCopy(bytes, 0, _fileData, (int)off, widthBytes);
            }

            if (_liveProcId != 0 && _eeHostAddr != 0)
            {
                var err = WritePatchesToLivePcsx2([(row.Address, bytes)]);
                if (err != null)
                    LogPine($"Inline write fallback failed at 0x{row.Address:X8}: {err}");
            }

            _rows[rowIndex] = row.DataSub == DataKind.None
                ? SlimRowFromWord(maskedNewValue, row.Address)
                : RefreshRowFromMemory(row);

            _disasmList.RedrawItems(rowIndex, rowIndex, true);
            _asciiBytesBar?.Invalidate();
        }

        private void NopSelectedOpcode()
        {
            if (_selRow < 0 || _selRow >= _rows.Count)
                return;

            ApplyTypedDisassemblyValueChange(_selRow, 0u);
        }

        private void RestoreOriginalOpcodeForSelectedRow()
        {
            if (_selRow < 0 || _selRow >= _rows.Count)
                return;

            var row = _rows[_selRow];
            if (!TryGetOriginalOpcodeForAddress(row.Address, out uint originalWord))
                return;

            ApplyTypedDisassemblyValueChange(_selRow, originalWord);
        }

        private void DrawHexCell(object? s, VirtualDisasmList.VirtualCellPaintEventArgs e)
        {
            int byteOffset = e.ItemIndex * 16;
            bool hasSelection = TryGetHexSelectionBounds(out int selMin, out int selMax);
            int rowStart = byteOffset;
            int rowEnd = byteOffset + 15;
            bool rowSelected = hasSelection && rowStart <= selMax && rowEnd >= selMin;
            Color rowBack = rowSelected ? BlendHexSelectionBackground() : ColHexBg;
            string txt = GetHexCellText(e.ItemIndex, e.ColumnIndex);

            bool cellSelected = false;
            if (hasSelection)
            {
                if (e.ColumnIndex >= 1 && e.ColumnIndex <= 16 && _hexSelMode == HexSelMode.Hex)
                {
                    int cellByte = byteOffset + (e.ColumnIndex - 1);
                    cellSelected = cellByte >= selMin && cellByte <= selMax;
                }
                else if (e.ColumnIndex == 17 && _hexSelMode == HexSelMode.Ascii)
                {
                    cellSelected = rowSelected;
                }
            }

            bool cellHighlight = cellSelected && !(e.ColumnIndex == 17 && _hexSelMode == HexSelMode.Ascii);
            using var br = new SolidBrush(cellHighlight ? ColSel : rowBack);
            e.Graphics.FillRectangle(br, e.Bounds);
            Color fg = cellHighlight                      ? ColSelFg    :
                       e.ColumnIndex == 0                ? ColAddr     :
                       e.ColumnIndex == 17               ? ColAscii    :
                       txt is "00" or "  "            ? ColZeroByte :
                       _currentTheme == AppTheme.Dark ? Color.FromArgb(210, 210, 210) : Color.Black;

            if (e.ColumnIndex == 17 && _hexSelMode == HexSelMode.Ascii && hasSelection)
            {
                int charW = TextRenderer.MeasureText("X", _mono, new Size(999, 99), TextFormatFlags.NoPadding).Width;
                if (charW < 1) charW = 7;
                for (int c = 0; c < txt.Length && c < 16; c++)
                {
                    int thisByte = byteOffset + c;
                    bool charSel = thisByte >= selMin && thisByte <= selMax;
                    var charRect = new Rectangle(e.Bounds.X + c * charW, e.Bounds.Y, charW, e.Bounds.Height);
                    if (charSel)
                    {
                        using var selBr = new SolidBrush(ColSel);
                        e.Graphics.FillRectangle(selBr, charRect);
                    }
                    Color cFg = charSel ? ColSelFg : ColAscii;
                    TextRenderer.DrawText(e.Graphics, txt[c].ToString(), _mono, charRect, cFg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                return;
            }

            TextRenderer.DrawText(e.Graphics, txt, _mono, e.Bounds, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private bool TryGetHexSelectionBounds(out int selMin, out int selMax)
        {
            if (_hexSelAnchor < 0 || _hexSelCurrent < 0)
            {
                selMin = selMax = -1;
                return false;
            }

            selMin = Math.Min(_hexSelAnchor, _hexSelCurrent);
            selMax = Math.Max(_hexSelAnchor, _hexSelCurrent);
            return true;
        }

        private Color BlendHexSelectionBackground()
        {
            // Keep a subtle row tint so selected all-zero rows are still clearly drawn,
            // while leaving byte-level highlighting to the selected cells.
            int alpha = _currentTheme == AppTheme.Dark ? 48 : 24;
            int inv = 255 - alpha;
            return Color.FromArgb(
                (ColHexBg.R * inv + ColSel.R * alpha) / 255,
                (ColHexBg.G * inv + ColSel.G * alpha) / 255,
                (ColHexBg.B * inv + ColSel.B * alpha) / 255);
        }

        private Rectangle GetHexRowInvalidateRect(int row)
        {
            if (row < 0 || row >= _hexList.VirtualListSize)
                return Rectangle.Empty;

            Rectangle rect = _hexList.GetItemRect(row);
            if (rect.Height <= 0 || rect.Bottom <= 0 || rect.Top >= _hexList.ClientSize.Height)
                return Rectangle.Empty;

            return Rectangle.Intersect(rect, new Rectangle(0, 0, _hexList.ClientSize.Width, _hexList.ClientSize.Height));
        }

        private void InvalidateHexRowRange(int firstRow, int lastRow)
        {
            if (_hexList.IsDisposed || _hexList.VirtualListSize <= 0)
                return;

            int first = Math.Max(0, Math.Min(firstRow, lastRow));
            int last = Math.Min(_hexList.VirtualListSize - 1, Math.Max(firstRow, lastRow));
            if (first > last)
                return;

            Rectangle dirty = Rectangle.Empty;
            for (int row = first; row <= last; row++)
            {
                Rectangle rowRect = GetHexRowInvalidateRect(row);
                if (rowRect == Rectangle.Empty)
                    continue;
                dirty = dirty == Rectangle.Empty ? rowRect : Rectangle.Union(dirty, rowRect);
            }

            if (dirty == Rectangle.Empty)
            {
                _hexList.Invalidate();
                return;
            }

            _hexList.Invalidate(dirty, false);
        }

        private void InvalidateHexSelectionRange(int anchor, int current)
        {
            if (anchor < 0 || current < 0)
                return;

            int firstRow = Math.Min(anchor, current) / 16;
            int lastRow = Math.Max(anchor, current) / 16;
            InvalidateHexRowRange(firstRow, lastRow);
        }

        private void InvalidateHexSelectionDelta(int oldAnchor, int oldCurrent, int newAnchor, int newCurrent)
        {
            InvalidateHexSelectionRange(oldAnchor, oldCurrent);
            InvalidateHexSelectionRange(newAnchor, newCurrent);
        }

        private void OnHexSelChanged(object? s, EventArgs e)
        {
            if (_hexList.SelectedIndices.Count == 0) return;
            uint addr = _baseAddr + (uint)(_hexList.SelectedIndices[0] * 16);
            int di = FindFirstRowAtOrAfter(addr);
            if (di >= 0) SelectRow(di, syncHex: false);
        }

        private int HexHitTestByteOffset(int mouseX, int mouseY, out HexSelMode mode)
        {
            mode = HexSelMode.None;
            var hit = _hexList.HitTest(new Point(mouseX, mouseY));
            if (hit.Item == null) return -1;
            int row = hit.Item.Index;
            int baseOff = row * 16;
            int columnIndex = hit.ColumnIndex;

            if (columnIndex >= 1 && columnIndex <= 16)
            {
                mode = HexSelMode.Hex;
                int byteIdx = baseOff + (columnIndex - 1);
                if (_fileData != null && byteIdx < _fileData.Length)
                    return byteIdx;
                return -1;
            }

            if (columnIndex == 17)
            {
                mode = HexSelMode.Ascii;
                Rectangle asciiRect = _hexList.GetSubItemRect(row, columnIndex);
                int charW = TextRenderer.MeasureText("X", _mono, new Size(999, 99), TextFormatFlags.NoPadding).Width;
                if (charW < 1) charW = 7;
                int charIdx = Math.Max(0, Math.Min(15, (mouseX - asciiRect.X) / charW));
                int byteIdx = baseOff + charIdx;
                if (_fileData != null && byteIdx < _fileData.Length)
                    return byteIdx;
            }

            return -1;
        }

        private void OnHexMouseDown(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int offset = HexHitTestByteOffset(e.X, e.Y, out var mode);
                if (offset < 0 || mode == HexSelMode.None) return;
                int oldAnchor = _hexSelAnchor;
                int oldCurrent = _hexSelCurrent;
                _hexSelMode = mode;
                _hexSelAnchor = offset;
                _hexSelCurrent = offset;
                _hexSelecting = true;
                // Suppress built-in ListView row selection to avoid visual interference
                _hexList.SelectedIndexChanged -= OnHexSelChanged;
                _hexList.SelectedIndices.Clear();
                _hexList.SelectedIndexChanged += OnHexSelChanged;
                _hexList.Capture = true; // ensure MouseMove fires even across row boundaries
                InvalidateHexSelectionDelta(oldAnchor, oldCurrent, _hexSelAnchor, _hexSelCurrent);
                _hexList.Focus();
            }
            else if (e.Button == MouseButtons.Right)
            {
                int offset = HexHitTestByteOffset(e.X, e.Y, out var mode);
                if (offset >= 0 && mode != HexSelMode.None)
                {
                    int oldAnchor = _hexSelAnchor;
                    int oldCurrent = _hexSelCurrent;
                    _hexSelMode = mode;
                    _hexSelAnchor = offset;
                    _hexSelCurrent = offset;
                    InvalidateHexSelectionDelta(oldAnchor, oldCurrent, _hexSelAnchor, _hexSelCurrent);
                }
            }
        }

        private void OnHexMouseMove(object? s, MouseEventArgs e)
        {
            if (!_hexSelecting || e.Button != MouseButtons.Left)
            {
                if (_hexSelecting)
                {
                    _hexSelecting = false;
                    _hexList.Capture = false;
                }
                return;
            }
            // Clamp Y to the control bounds for drag selection
            int clampedY = Math.Max(0, Math.Min(e.Y, _hexList.ClientSize.Height - 1));
            int offset = HexHitTestByteOffset(e.X, clampedY, out var mode);
            if (offset < 0) return;
            if (mode != _hexSelMode) return;
            if (offset != _hexSelCurrent)
            {
                int oldAnchor = _hexSelAnchor;
                int oldCurrent = _hexSelCurrent;
                _hexSelCurrent = offset;
                InvalidateHexSelectionDelta(oldAnchor, oldCurrent, _hexSelAnchor, _hexSelCurrent);
            }
        }

        private void OnHexMouseUp(object? s, MouseEventArgs e)
        {
            _hexSelecting = false;
            _hexList.Capture = false;
        }

        private void OnHexKeyDown(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.G && !e.Control && !e.Shift && !e.Alt)
            {
                e.Handled = e.SuppressKeyPress = true;
                ShowHexGoto();
                return;
            }
            if (e.Control && e.KeyCode == Keys.C)
            {
                e.Handled = e.SuppressKeyPress = true;
                CopyHexSelection();
                return;
            }
        }

        private void ShowHexGoto()
        {
            using var dlg = new GotoAddressDialog(_lastGotoAddress,
                backColor: _themeFormBack, foreColor: _themeFormFore,
                tbBackColor: _themeWindowBack, tbForeColor: _themeWindowFore);
            dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
            var result = dlg.ShowDialog(this);
            _lastGotoAddress = dlg.Value;
            if (result != DialogResult.OK) return;
            string val = dlg.Value.Trim();
            if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) val = val[2..];
            if (!uint.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out uint addr))
            { MessageBox.Show("Invalid hex address.", "ps2dis", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            addr = NormalizeMipsAddress(addr);
            SyncHexToAddr(addr);
        }

        private void CopyHexSelection()
        {
            if (_fileData == null || _hexSelAnchor < 0 || _hexSelCurrent < 0) return;
            int selMin = Math.Min(_hexSelAnchor, _hexSelCurrent);
            int selMax = Math.Max(_hexSelAnchor, _hexSelCurrent);
            selMin = Math.Max(0, selMin);
            selMax = Math.Min(_fileData.Length - 1, selMax);
            if (selMin > selMax) return;

            if (_hexSelMode == HexSelMode.Hex)
            {
                var sb = new StringBuilder();
                for (int i = selMin; i <= selMax; i++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(_fileData[i].ToString("X2"));
                }
                Clipboard.SetText(sb.ToString());
            }
            else if (_hexSelMode == HexSelMode.Ascii)
            {
                var sb = new StringBuilder();
                for (int i = selMin; i <= selMax; i++)
                {
                    byte b = _fileData[i];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                Clipboard.SetText(sb.ToString());
            }
        }

        private void GoToHexSelectionInDisassembler()
        {
            // Use selection start if available, otherwise use the first visible row address
            uint addr;
            if (_hexSelAnchor >= 0)
                addr = _baseAddr + (uint)_hexSelAnchor;
            else if (_hexList.SelectedIndices.Count > 0)
                addr = _baseAddr + (uint)(_hexList.SelectedIndices[0] * 16);
            else
                addr = _baseAddr + (uint)(_hexList.TopIndex * 16);

            addr = NormalizeMipsAddress(addr);
            if (_mainTabs != null)
                _mainTabs.SelectedIndex = 0;

            if (TryGetRowIndexByAddress(addr, out int idx))
                SelectRow(idx, center: true);
            else
            {
                int nearest = FindNearestRow(addr);
                if (nearest >= 0)
                    SelectRow(nearest, center: true);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Disasm panel — virtual + owner-draw
        // ══════════════════════════════════════════════════════════════════

        private void OnRetrieveVirtualItem(object? s, RetrieveVirtualItemEventArgs e)
        {
            var r    = GetLiveDisplayRow(e.ItemIndex);
            var item = new ListViewItem(r.Address.ToString("X8"));
            if (_showHex)   item.SubItems.Add(GetDisplayHexText(r));
            if (_showBytes) item.SubItems.Add(r.BytesStr);

            string command = GetCommandText(r, e.ItemIndex).Replace(AnnotationSentinel.ToString(), string.Empty);
            item.SubItems.Add(GetLabelAt(r.Address) ?? "");
            item.SubItems.Add(command);
            EnsureVirtualItemHasAllSubItems(_disasmList, item);
            e.Item = item;
        }

        private string GetCommandText(DisassemblyRow r, int rowIdx)
        {
            // r is already resolved by GetLiveDisplayRow; no need to re-resolve here.
            string text = r.Mnemonic switch
            {
                ".word"  => FormatWordValue(r.Word),
                ".half"  => r.Operands,
                ".byte"  => r.Operands,
                ".float" => r.Operands,
                _        => FormatInstructionText(r, rowIdx)
            };

            // Data rows (.float, .half, .byte) should not have instruction-based
            // annotations appended — the raw word doesn't decode as a meaningful
            // opcode in those cases. .word has its own annotation logic inside
            // FormatWordValue.
            if (r.Mnemonic is ".float" or ".half" or ".byte" or ".word")
                return StripTempLabelsFromAnnotations(text);

            uint w = r.Word;
            uint op = (w >> 26) & 0x3F;

            if (op == 0x0F) // LUI — only show upper half
            {
                uint ui = w & 0xFFFF;
                text += $" ({ui << 16:X8})";
                return StripTempLabelsFromAnnotations(text);
            }

            if (op == 0x09) // ADDIU — show own computed address + data at that address
            {
                uint rs = (w >> 21) & 0x1F;
                int si = (short)(w & 0xFFFF);
                uint baseVal = ResolveRegisterAddressValue(rowIdx, rs);
                if (baseVal != 0)
                {
                    uint addiuAddr = (uint)((int)baseVal + si);
                    text = AppendAddressAnnotationForDisplay(text, addiuAddr);
                    return StripTempLabelsFromAnnotations(text);
                }
            }

            uint dataAddr = ComputeDataAddress(r, rowIdx);
            if (dataAddr != 0)
            {
                uint dataOp = (r.Word >> 26) & 0x3F;
                if (TryGetLoadStoreAccessSpec(dataOp, out _, out uint sizeBytes))
                    text = AppendLoadStoreAnnotationForDisplay(text, dataAddr, sizeBytes);
                else
                    text = AppendAddressAnnotationForDisplay(text, dataAddr);
            }

            return StripTempLabelsFromAnnotations(text);
        }

        private string FormatInstructionText(DisassemblyRow r, int rowIdx)
        {
            string baseText = NormalizeCommandAddressPrefixes($"{r.Mnemonic} {r.Operands}".Trim());
            uint target = NormalizeFollowAddress(r.Target);
            if (target == 0)
                return baseText;

            string? label = GetRealLabelAt(target);   // real labels only — no xref temp-labels
            if (TryGetRowIndexByAddress(target, out int targetIdx))
            {
                // Use address-based word distance, not row-index distance,
                // so byte rows between branch and target don't inflate the count.
                int delta = (int)((long)target - (long)r.Address) / 4;
                string arrow = delta >= 0 ? "▼" : "▲";
                string deltaText = delta >= 0 ? $"+{delta}{arrow}" : $"{delta}{arrow}";
                return string.IsNullOrWhiteSpace(label)
                    ? $"{baseText}{AnnotationSentinel} ({deltaText})"
                    : $"{baseText}{AnnotationSentinel} ({deltaText}) {label}";
            }

            return string.IsNullOrWhiteSpace(label) ? baseText : $"{baseText}{AnnotationSentinel} {label}";
        }

        private static string NormalizeCommandAddressPrefixes(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(?<![A-Za-z0-9_])0x([0-9A-Fa-f]{8})(?![A-Za-z0-9_])",
                m => "$" + m.Groups[1].Value.ToUpperInvariant());
        }

        private static string StripTempLabelsFromAnnotations(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            const string tempLabelPattern = @"(?:FUNC_|_)[0-9A-Fa-f]{8}";

            string cleaned = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"(?<=\)|\])\s+" + tempLabelPattern + @"(?![A-Za-z0-9_])",
                string.Empty);

            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"(?<=,|\s|\(|\[)\s*" + tempLabelPattern + @"(?![A-Za-z0-9_])",
                string.Empty);

            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"\s+" + tempLabelPattern + @"(?![A-Za-z0-9_])",
                string.Empty);

            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\(\s*,", "(");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\(\s+\)", "()");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s{2,}", " ");
            cleaned = cleaned.Replace(" ,", ",").Replace("( ", "(").Replace(" )", ")");
            return cleaned.TrimEnd();
        }

        private bool LooksLikeRegisterToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            token = token.Trim().TrimStart('$').TrimEnd(',', ')', '(', '[', ']');
            if (token.Length == 0)
                return false;
            return GetRegisterColor(token).HasValue;
        }

        private Color? GetRegisterColor(string token)
        {
            token = token.Trim().TrimStart('$').TrimEnd(',', ')', '(', '[', ']');
            if (token.Length == 0)
                return null;

            token = token.ToLowerInvariant();
            if (token == "zero") return tokenRegisterZeroColor;
            if (token == "at") return tokenRegisterATColor;
            if (token == "gp") return tokenRegisterGPColor;
            if (token == "sp") return tokenRegisterSPColor;
            if (token == "fp" || token == "s8") return tokenRegisterFPColor;
            if (token == "ra") return tokenRegisterRAColor;
            if (token.StartsWith("a") && token.Length == 2 && token[1] >= '0' && token[1] <= '3') return tokenRegisterAColor;
            if (token.StartsWith("v") && token.Length == 2 && token[1] >= '0' && token[1] <= '1') return tokenRegisterVColor;
            if (token.StartsWith("t") && token.Length == 2 && token[1] >= '0' && token[1] <= '9') return tokenRegisterTColor;
            if (token.StartsWith("s") && token.Length == 2 && token[1] >= '0' && token[1] <= '7') return tokenRegisterSColor;
            if (token.StartsWith("k") && token.Length == 2 && token[1] >= '0' && token[1] <= '1') return tokenRegisterKColor;
            if (token.StartsWith("f") && token.Length > 1 && int.TryParse(token[1..], out int fnum) && fnum >= 0 && fnum <= 31) return tokenRegisterFColor;
            if (token.StartsWith("r") && token.Length > 1 && int.TryParse(token[1..], out int rnum) && rnum >= 0 && rnum <= 31) return tokenRegisterOtherColor;
            return null;
        }

        private List<(string Text, Color Color)> BuildCommandSegments(string text, Color defaultColor)
        {
            var segments = new List<(string Text, Color Color)>();
            if (string.IsNullOrEmpty(text))
                return segments;

            var sb = new StringBuilder();
            Color currentColor = defaultColor;

            static bool IsTokenChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$' || ch == '.';

            void Flush()
            {
                if (sb.Length == 0)
                    return;
                segments.Add((sb.ToString(), currentColor));
                sb.Clear();
            }

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (!IsTokenChar(ch))
                {
                    if (currentColor != defaultColor)
                    {
                        Flush();
                        currentColor = defaultColor;
                    }
                    sb.Append(ch);
                    continue;
                }

                int start = i;
                while (i < text.Length && IsTokenChar(text[i]))
                    i++;
                string token = text[start..i];
                i--;

                Color tokenColor = GetRegisterColor(token) ?? defaultColor;
                if (tokenColor != currentColor)
                {
                    Flush();
                    currentColor = tokenColor;
                }
                sb.Append(token);
            }

            Flush();
            return segments;
        }

        private string FormatWordValue(uint word)
        {
            string text = $"{word:X8}({unchecked((int)word)})";
            string? label = GetLabelAt(word);
            if (!string.IsNullOrWhiteSpace(label) && !label.StartsWith("loc_", StringComparison.OrdinalIgnoreCase))
                text += $"{AnnotationSentinel} {label}";
            return text;
        }

        private static string AppendDataAnnotation(string text, uint address, uint dataWord, string? label)
        {
            if (!string.IsNullOrWhiteSpace(label))
                return $"{text}{AnnotationSentinel} {label}";

            return $"{text}{AnnotationSentinel} ({address:X8} {dataWord:X8})";
        }

        private static string AppendDataOnlyAnnotation(string text, string dataText, string? label)
        {
            if (!string.IsNullOrWhiteSpace(label))
                return $"{text}{AnnotationSentinel} {label}";

            return $"{text}{AnnotationSentinel} ({dataText})";
        }

        private static string AppendAddressAnnotation(string text, uint address, string? label)
        {
            if (!string.IsNullOrWhiteSpace(label))
                return $"{text}{AnnotationSentinel} {label}";

            return $"{text}{AnnotationSentinel} ({address:X8})";
        }

        private static bool ShouldDereferenceAnnotationAddress(uint address)
        {
            if (address == 0)
                return false;

            if (address <= 0x01FFFFFFu)
                return true;

            uint topNibble = address & 0xF0000000u;
            return topNibble == 0x20000000u || topNibble == 0x80000000u;
        }

        private string AppendAddressAnnotationForDisplay(string text, uint address)
        {
            string? label = GetRealLabelAt(address);
            if (!ShouldDereferenceAnnotationAddress(address))
                return AppendAddressAnnotation(text, address, label);

            return TryReadWordAt(address, out uint dataWord)
                ? AppendDataAnnotation(text, address, dataWord, label)
                : AppendAddressAnnotation(text, address, label);
        }

        private string AppendLoadStoreAnnotationForDisplay(string text, uint address, uint sizeBytes)
        {
            string? label = GetRealLabelAt(address);
            if (!ShouldDereferenceAnnotationAddress(address))
                return AppendAddressAnnotation(text, address, label);

            return TryReadValueAt(address, sizeBytes, out string valueText)
                ? AppendDataOnlyAnnotation(text, valueText, label)
                : AppendAddressAnnotation(text, address, label);
        }

        private bool TryReadValueAt(uint addr, uint sizeBytes, out string valueText)
        {
            valueText = string.Empty;
            if (sizeBytes == 0)
                return false;

            int readSize = sizeBytes > int.MaxValue ? int.MaxValue : (int)sizeBytes;
            if (!TryReadBytesAt(addr, readSize, out byte[] data) || data.Length < readSize)
                return false;

            var sb = new StringBuilder(readSize * 2);
            for (int i = readSize - 1; i >= 0; i--)
                sb.Append(data[i].ToString("X2"));
            valueText = sb.ToString();
            return true;
        }

        private bool TryReadWordAt(uint addr, out uint value)
        {
            value = 0;
            if (!TryReadBytesAt(addr, 4, out byte[] data) || data.Length < 4)
                return false;

            value = BitConverter.ToUInt32(data, 0);
            return true;
        }

        private bool TryReadBytesAt(uint addr, int size, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (size <= 0)
                return false;

            if (IsLiveAttached() && TryReadEeMemory(addr, size, out byte[] liveData) && liveData.Length >= size)
            {
                data = liveData;
                return true;
            }

            if (_fileData == null)
                return false;

            long off = (long)addr - _baseAddr;
            if (off < 0 || off + size > _fileData.Length)
                return false;

            data = new byte[size];
            Buffer.BlockCopy(_fileData, (int)off, data, 0, size);
            return true;
        }
        /// <summary>
        /// Sentinel character inserted at annotation source sites (AppendDataAnnotation,
        /// FormatInstructionText, FormatWordValue) so TrySplitCommandAnnotation can find
        /// the exact boundary without heuristic guessing.
        /// </summary>
        private const char AnnotationSentinel = '\x01';

        private bool TrySplitCommandAnnotation(string text, out string mainText, out string annotation)
        {
            // ── Primary: sentinel-based split (100% reliable) ──
            int sentinel = text.IndexOf(AnnotationSentinel);
            if (sentinel >= 0)
            {
                mainText = text[..sentinel];
                annotation = text[(sentinel + 1)..]; // skip sentinel char
                return annotation.Length > 0;
            }

            // ── Fallback heuristics for text not produced by our annotation helpers ──
            int split = text.IndexOf(" (", StringComparison.Ordinal);
            if (split > 0)
            {
                mainText = text[..split];
                annotation = text[split..];
                return true;
            }

            int quotedStart = text.LastIndexOf(" \"", StringComparison.Ordinal);
            if (quotedStart > 0 && text.Length > quotedStart + 2 && text[^1] == '"')
            {
                mainText = text[..quotedStart];
                annotation = text[quotedStart..];
                return true;
            }

            int lastSpace = text.LastIndexOf(' ');
            if (lastSpace > 0 && lastSpace + 1 < text.Length)
            {
                string suffix = text[(lastSpace + 1)..];
                string prefix = text[..lastSpace];
                if (LooksLikeTrailingAnnotationLabel(prefix, suffix))
                {
                    // Extend backwards to capture multi-word labels
                    int labelStart = lastSpace;
                    while (labelStart > 0)
                    {
                        int prevSpace = text.LastIndexOf(' ', labelStart - 1);
                        if (prevSpace <= 0) break;
                        string wordBefore = text[(prevSpace + 1)..labelStart];
                        if (LooksLikeRegisterToken(wordBefore)) break;
                        if (wordBefore.StartsWith("$", StringComparison.Ordinal) ||
                            wordBefore.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) break;
                        if (wordBefore.Length > 0 && (wordBefore[^1] is ',' or ')' or ']')) break;
                        bool validLabel = wordBefore.Length > 0;
                        foreach (char ch in wordBefore)
                        {
                            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '.' or ':' or '@' or '-'))
                            { validLabel = false; break; }
                        }
                        if (!validLabel) break;
                        string candidatePrefix = text[..prevSpace];
                        if (!candidatePrefix.Contains(',') && !candidatePrefix.Contains(')') &&
                            !candidatePrefix.Contains(']') && !candidatePrefix.Contains('$') &&
                            !candidatePrefix.Contains("0x", StringComparison.OrdinalIgnoreCase))
                            break;
                        labelStart = prevSpace;
                    }
                    mainText = text[..labelStart];
                    annotation = text[labelStart..];
                    return true;
                }
            }

            mainText = text;
            annotation = string.Empty;
            return false;
        }

        private bool LooksLikeTrailingAnnotationLabel(string prefix, string suffix)
        {
            if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(suffix))
                return false;

            suffix = suffix.Trim();
            if (suffix.Length == 0)
                return false;

            if (LooksLikeRegisterToken(suffix))
                return false;

            if (suffix.StartsWith("$", StringComparison.Ordinal) || suffix.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return false;

            if (int.TryParse(suffix, out _) || uint.TryParse(suffix, System.Globalization.NumberStyles.HexNumber, null, out _))
                return false;

            foreach (char ch in suffix)
            {
                bool ok = char.IsLetterOrDigit(ch) || ch is '_' or '.' or ':' or '@' or '$' or '"' or '\'' or '-';
                if (!ok)
                    return false;
            }

            if (!prefix.Any(char.IsWhiteSpace))
                return false;

            return prefix.Contains(',')
                || prefix.Contains(')')
                || prefix.Contains(']')
                || prefix.Contains('$')
                || prefix.Contains("0x", StringComparison.OrdinalIgnoreCase);
        }

        private void DrawDisasmCell(object? s, VirtualDisasmList.VirtualCellPaintEventArgs e)
        {
            DrawDisasmCellCore(e.Graphics, e.Bounds, e.ItemIndex, e.ColumnIndex, GetVirtualDisasmCellText(e.ItemIndex, e.ColumnIndex));
        }

        private string GetDisplayHexText(DisassemblyRow row)
        {
            string hex = row.HexWord;
            return HasOriginalOpcodeForAddress(row.Address) ? hex + "*" : hex;
        }

        private string GetVirtualDisasmCellText(int rowIndex, int columnIndex)
        {
            if (rowIndex < 0 || rowIndex >= _rows.Count || columnIndex < 0)
                return string.Empty;

            var r = GetLiveDisplayRow(rowIndex);
            if (columnIndex == CmdCol)
                return GetCommandText(r, rowIndex);

            var item = _disasmList.GetVirtualItem(rowIndex);
            if (item != null)
            {
                EnsureVirtualItemHasAllSubItems(_disasmList, item);
                if (columnIndex < item.SubItems.Count)
                    return item.SubItems[columnIndex].Text ?? string.Empty;
            }

            if (columnIndex == 0) return r.Address.ToString("X8");
            int c = 1;
            if (_showHex)
            {
                if (columnIndex == c) return GetDisplayHexText(r);
                c++;
            }
            if (_showBytes)
            {
                if (columnIndex == c) return r.BytesStr;
                c++;
            }
            if (columnIndex == c)
                return GetLabelAt(r.Address) ?? string.Empty;
            if (columnIndex == c + 1)
                return GetCommandText(r, rowIndex);
            return string.Empty;
        }

        // ── Mnemonic tab stop (8 monospace character widths) ─────────────────
        private int _mnemonicTabWidth = -1;

        private int GetMnemonicTabWidth()
        {
            if (_mnemonicTabWidth >= 0) return _mnemonicTabWidth;
            // Measure 8 characters in the monospace font (NoPadding for accurate width).
            _mnemonicTabWidth = TextRenderer.MeasureText(
                "MMMMMMMM", _mono, new Size(9999, 32), TextFormatFlags.NoPadding).Width;
            return _mnemonicTabWidth;
        }

        /// <summary>
        /// Draw one text segment, advancing <paramref name="x"/>.
        /// When <paramref name="last"/> is true, clips with EndEllipsis instead of hard-clipping.
        /// </summary>
        private static void DrawSegment(Graphics g, string text, Font font, Color color,
            ref int x, Rectangle rowRect, TextFormatFlags baseFlags, bool last)
        {
            if (string.IsNullOrEmpty(text) || x >= rowRect.Right) return;
            var avail = new Rectangle(x, rowRect.Y, rowRect.Right - x, rowRect.Height);
            if (last)
            {
                TextRenderer.DrawText(g, text, font, avail, color, baseFlags | TextFormatFlags.EndEllipsis);
            }
            else
            {
                Size sz = TextRenderer.MeasureText(text, font, new Size(9999, rowRect.Height), baseFlags);
                int w = Math.Min(sz.Width, avail.Width);
                TextRenderer.DrawText(g, text, font, new Rectangle(x, rowRect.Y, w, rowRect.Height), color, baseFlags);
                x += sz.Width;
            }
        }

        private void DrawFormattedCommandText(Graphics graphics, Rectangle bounds, string text, bool selected,
            Color defaultColor, Color selectedTextColor, Color annotationColor)
        {
            var rect = bounds;
            rect.Offset(0, DisasmTextVerticalOffset);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            int x = rect.X;
            bool forceSelectedColors = selected;
            Color baseCommandColor = forceSelectedColors ? selectedTextColor : ColAddr;
            Color annotColor = forceSelectedColors ? selectedTextColor : annotationColor;

            TrySplitCommandAnnotation(text, out string mainText, out string annotation);
            if (string.IsNullOrEmpty(annotation))
                mainText = text;

            bool useTabStop = !string.IsNullOrWhiteSpace(mainText) && !mainText.StartsWith(".", StringComparison.Ordinal);
            int spacePos = useTabStop ? mainText.IndexOf(' ') : -1;

            if (useTabStop && spacePos > 0)
            {
                string mnemText = mainText[..spacePos];
                string opsText = mainText[(spacePos + 1)..];

                DrawSegment(graphics, mnemText, _mono, baseCommandColor, ref x, rect, flags, last: false);
                x = Math.Max(x + 2, rect.X + GetMnemonicTabWidth());

                bool hasAnnot = !string.IsNullOrEmpty(annotation);
                var opsSegs = forceSelectedColors
                    ? new List<(string Text, Color Color)> { (opsText, selectedTextColor) }
                    : BuildCommandSegments(opsText, defaultColor);
                for (int i = 0; i < opsSegs.Count && x < rect.Right; i++)
                {
                    bool last = (i == opsSegs.Count - 1) && !hasAnnot;
                    DrawSegment(graphics, opsSegs[i].Text, _mono, opsSegs[i].Color, ref x, rect, flags, last);
                }

                if (hasAnnot)
                    DrawSegment(graphics, annotation, _mono, annotColor, ref x, rect, flags, last: true);
            }
            else
            {
                var segs = new List<(string Text, Color Color)>();
                if (!string.IsNullOrEmpty(annotation))
                {
                    if (forceSelectedColors)
                        segs.Add((mainText, selectedTextColor));
                    else
                        segs.AddRange(BuildCommandSegments(mainText, defaultColor));
                    segs.Add((annotation, annotColor));
                }
                else
                {
                    if (forceSelectedColors)
                        segs.Add((text, selectedTextColor));
                    else
                        segs.AddRange(BuildCommandSegments(text, defaultColor));
                }

                for (int i = 0; i < segs.Count && x < rect.Right; i++)
                    DrawSegment(graphics, segs[i].Text, _mono, segs[i].Color, ref x, rect, flags, last: i == segs.Count - 1);
            }
        }

        private void DrawDisasmCellCore(Graphics graphics, Rectangle bounds, int idx, int columnIndex, string cellText)
        {
            if (idx < 0 || idx >= _rows.Count) return;
            bool sel = idx == _selRow;
            bool isDest = !sel && idx == _highlightDestIdx;
            var r = GetLiveDisplayRow(idx);
            bool isXref = r.Address == _xrefTarget && _xrefTarget != 0;
            bool hasBreakpoint = _userBreakpoints.Contains(r.Address);
            bool isActiveBreakpoint = _activeBreakpointAddress.HasValue && NormalizeMipsAddress(_activeBreakpointAddress.Value) == r.Address;
            Color rowBack = isActiveBreakpoint
                ? _themeBreakpointActiveBack
                : hasBreakpoint
                    ? _themeBreakpointBack
                    : isXref
                        ? ColXref
                        : sel
                            ? ColSel
                            : isDest
                                ? _themeHighlightDest
                                : ColBg;
            using var br = new SolidBrush(rowBack);
            graphics.FillRectangle(br, bounds);
            Color fg = isActiveBreakpoint
                ? Color.White
                : hasBreakpoint
                    ? Color.White
                    : sel
                        ? ColSelFg
                        : PickFg(columnIndex, r);
            Font font = _mono;
            var rect = Rectangle.Inflate(bounds, -2, 0);
            rect.Offset(0, DisasmTextVerticalOffset);

            bool isInlineEditingThisCell = _inlineEdit != null && _inlineEdit.Visible && _inlineRow == idx && _inlineCol == columnIndex;
            if (!isInlineEditingThisCell && columnIndex == CmdCol)
            {
                string rawCellText = cellText;
                TrySplitCommandAnnotation(rawCellText, out string mainTextRaw, out string annotationRaw);
                string mainText = mainTextRaw.Replace(AnnotationSentinel.ToString(), string.Empty);
                string annotationText = annotationRaw.Replace(AnnotationSentinel.ToString(), string.Empty);
                string displayCellText = rawCellText.Replace(AnnotationSentinel.ToString(), string.Empty);

                bool forceWhite = sel || hasBreakpoint || isActiveBreakpoint;
                bool isBranchJump = r.Kind is InstructionType.Branch or InstructionType.Jump or InstructionType.Call;

                // Annotation color: label names appended to branch/jump targets, .word annotations, etc.
                // Dark theme: grey tones.  Light theme: use the theme annotation color variables.
                Color annotColor = forceWhite ? ColSelFg : (_currentTheme == AppTheme.Dark
                    ? (isBranchJump ? Color.FromArgb(128, 128, 128) : Color.FromArgb(85, 85, 85))
                    : Color.FromArgb(0x00, 0x00, 0xb0));

                // Word/halfword datatypes: dim the decimal value in parentheses while preserving
                // any trailing annotation label exactly as-is, even when it contains spaces/numbers.
                if ((_rows[idx].DataSub == DataKind.Word || _rows[idx].DataSub == DataKind.Half) && !forceWhite)
                {
                    int parenPos = mainText.IndexOf('(');
                    if (parenPos > 0)
                    {
                        string hexPart = mainText[..parenPos];
                        string intPart = mainText[parenPos..];
                        Color hexColor = ColData;
                        Color dimColor = Color.FromArgb(
                            (hexColor.R + rowBack.R) / 2,
                            (hexColor.G + rowBack.G) / 2,
                            (hexColor.B + rowBack.B) / 2);
                        var flags2 = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
                        int x2 = rect.X;
                        DrawSegment(graphics, hexPart, _mono, hexColor, ref x2, rect, flags2, last: false);

                        if (!string.IsNullOrEmpty(annotationText))
                        {
                            DrawSegment(graphics, intPart, _mono, dimColor, ref x2, rect, flags2, last: false);
                            DrawSegment(graphics, annotationText, _mono, annotColor, ref x2, rect, flags2, last: true);
                        }
                        else
                        {
                            DrawSegment(graphics, intPart, _mono, dimColor, ref x2, rect, flags2, last: true);
                        }
                        goto doneCmd;
                    }
                }

                DrawFormattedCommandText(graphics, rect, rawCellText, forceWhite,
                    defaultColor: ColAddr,
                    selectedTextColor: ColSelFg,
                    annotationColor: annotColor);
                doneCmd:;
            }
            else
            {
                TextRenderer.DrawText(graphics, cellText, font, rect, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }

        private Color PickFg(int col, DisassemblyRow r)
        {
            if (col == 0)      return ColAddr;
            if (col == LblCol) return ColLabel;
            if (col == CmdCol) return r.Kind switch
            {
                InstructionType.Nop    => ColNop,
                _                      => ColHexFg,
            };
            return ColAddr;
        }

        private void PaintAsciiBytesBar(object? sender, PaintEventArgs e)
        {
            var bar = sender as Panel;
            if (bar == null) return;

            using var bgBr = new SolidBrush(ColHexBg);
            e.Graphics.FillRectangle(bgBr, bar.ClientRectangle);

            if (_fileData == null || _selRow < 0 || _selRow >= _rows.Count)
                return;

            uint addr = _rows[_selRow].Address;
            long off = (long)addr - _baseAddr;
            if (off < 0) return;

            int barW = bar.ClientSize.Width;
            int charW = TextRenderer.MeasureText("X", _mono, new Size(999, 99), TextFormatFlags.NoPadding).Width;
            if (charW < 1) charW = 7;
            int maxChars = barW / charW;
            int count = Math.Min(maxChars, (int)Math.Min(_fileData.Length - off, maxChars));

            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            for (int i = 0; i < count; i++)
            {
                int byteOff = (int)off + i;
                if (byteOff >= _fileData.Length) break;
                byte b = _fileData[byteOff];
                char ch = (b >= 0x20 && b <= 0x7E) ? (char)b : '.';
                Color fg = (b == 0) ? ColZeroByte : ColAscii;
                var charRect = new Rectangle(i * charW, 0, charW, bar.ClientSize.Height);
                TextRenderer.DrawText(e.Graphics, ch.ToString(), _mono, charRect, fg, flags);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Selection
        // ══════════════════════════════════════════════════════════════════

        private void OnDisasmSelChanged(object? s, EventArgs e)
        {
            if (_disasmList.SelectedIndices.Count == 0) return;
            UpdateSel(_disasmList.SelectedIndices[0], syncHex: true);
        }

        private void SelectRow(int idx, bool syncHex = true, bool center = false)
        {
            if (idx < 0 || idx >= _rows.Count) return;
            _disasmList.SelectedIndexChanged -= OnDisasmSelChanged;
            _disasmList.SelectedIndex = idx;
            _disasmList.FocusedItem = _disasmList.Items[idx];
            if (center)
                CenterDisasmRow(idx);
            else
                _disasmList.EnsureVisible(idx);
            _disasmList.Focus();
            _disasmList.SelectedIndexChanged += OnDisasmSelChanged;
            UpdateSel(idx, syncHex);
        }

        private void CenterDisasmRow(int idx)
        {
            if (idx < 0 || idx >= _rows.Count) return;
            try
            {
                _disasmList.EnsureVisible(idx);
                int rowH = GetDisasmRowHeight();
                if (rowH <= 0) return;
                int visible = Math.Max(1, _disasmList.ClientSize.Height / rowH);
                int targetTop = Math.Max(0, idx - (visible / 2));
                if (_disasmList.VirtualListSize > 0)
                    targetTop = Math.Min(targetTop, Math.Max(0, _disasmList.VirtualListSize - visible));
                if (targetTop >= 0 && targetTop < _disasmList.VirtualListSize)
                    _disasmList.TopIndex = targetTop;
                _disasmList.EnsureVisible(idx);
            }
            catch
            {
                _disasmList.EnsureVisible(idx);
            }
        }

        private void UpdateSel(int idx, bool syncHex)
        {
            int old          = _selRow;
            int oldSkipStart = _jumpSkipStart;
            int oldSkipEnd   = _jumpSkipEnd;

            if (!_suppressHistoryPush && old >= 0 && old < _rows.Count && old != idx)
            {
                uint oldAddr = _rows[old].Address;
                if (_navBack.Count == 0 || _navBack.Peek() != oldAddr)
                    _navBack.Push(oldAddr);
            }

            _selRow = idx;

            int oldDest = _highlightDestIdx;

            _jumpSkipStart = -1;
            _jumpSkipEnd   = -1;
            _highlightDestIdx = -1;
            if (idx >= 0 && idx < _rows.Count)
            {
                uint target = NormalizeFollowAddress(_rows[idx].Target);
                if (target != 0 && TryGetRowIndexByAddress(target, out int tgt) && tgt != idx)
                    _highlightDestIdx = tgt;
            }

            if (old >= 0 && old < _rows.Count) _disasmList.RedrawItems(old, old, true);
            if (oldDest >= 0 && oldDest < _rows.Count) _disasmList.RedrawItems(oldDest, oldDest, true);

            if (idx >= 0 && idx < _rows.Count) _disasmList.RedrawItems(idx, idx, true);
            if (_highlightDestIdx >= 0 && _highlightDestIdx < _rows.Count) _disasmList.RedrawItems(_highlightDestIdx, _highlightDestIdx, true);

            var r = _rows[idx];
            _sbAddr.Text = r.Address.ToString("X8");
            if (syncHex) SyncHexToAddr(r.Address);
            _asciiBytesBar?.Invalidate();
        }

        private int GetSelectedVisibleRowOffset()
        {
            if (_selRow < 0 || _selRow >= _rows.Count) return 0;
            try
            {
                int top = _disasmList.TopIndex >= 0 ? _disasmList.TopIndex : _selRow;
                return Math.Max(0, _selRow - top);
            }
            catch
            {
                return 0;
            }
        }

        private void ApplyVisibleRowOffset(int idx, int visibleOffset)
        {
            if (idx < 0 || idx >= _rows.Count) return;
            try
            {
                int rowH = GetDisasmRowHeight();
                int visibleRows = Math.Max(1, _disasmList.ClientSize.Height / Math.Max(1, rowH));
                int top = Math.Max(0, idx - Math.Max(0, visibleOffset));
                if (_disasmList.VirtualListSize > 0)
                    top = Math.Min(top, Math.Max(0, _disasmList.VirtualListSize - visibleRows));
                _disasmList.TopIndex = top;
                _disasmList.EnsureVisible(idx);
            }
            catch
            {
                _disasmList.EnsureVisible(idx);
            }
        }

        private bool IsRowVisible(int idx)
        {
            if (idx < 0 || idx >= _rows.Count) return false;
            try
            {
                int top = _disasmList.TopIndex;
                int rowH = GetDisasmRowHeight();
                int visibleRows = Math.Max(1, _disasmList.ClientSize.Height / Math.Max(1, rowH));
                return idx >= top && idx < top + visibleRows;
            }
            catch
            {
                return false;
            }
        }

        private void NavigateToAddress(uint addr, bool keepVisibleRow)
        {
            if (addr == 0) return;

            // Normalize kernel/segment addresses (e.g. 0x80000280 → 0x00000280)
            addr = NormalizeMipsAddress(addr);

            int visibleOffset = keepVisibleRow ? GetSelectedVisibleRowOffset() : 0;

            if (TryGetRowIndexByAddress(addr, out int idx) ||
                ((idx = FindNearestRow(addr)) >= 0 && idx < _rows.Count))
            {
                if (IsRowVisible(idx))
                    SelectRow(idx, center: false);
                else
                {
                    SelectRow(idx, center: !keepVisibleRow);
                    if (keepVisibleRow)
                        ApplyVisibleRowOffset(idx, visibleOffset);
                }
                return;
            }

            if (_fileData == null) return;
            uint fileOff = addr >= _baseAddr ? addr - _baseAddr : 0;
            if (fileOff >= (uint)_fileData.Length)
            {
                MessageBox.Show($"Target 0x{addr:X8} is outside the file range.", "ps2dis");
                return;
            }

            _pendingNavAddr = addr;
            _pendingNavVisibleOffset = visibleOffset;
            _pendingNavCenter = !keepVisibleRow;
            _disasmBase = _baseAddr;
            _disasmLen = 0x02000000;
            StartDisassembly();
        }

        private void FollowTarget()
        {
            if (_selRow < 0 || _selRow >= _rows.Count) return;
            var r = ResolveRowForDisplay(_rows[_selRow]);

            uint addr = 0;
            if (r.Kind == InstructionType.Data)
            {
                if (string.Equals(r.Mnemonic, ".word", StringComparison.Ordinal))
                    addr = NormalizeFollowAddress(r.Word);
            }
            else
            {
                addr = NormalizeFollowAddress(ComputeDataAddress(r, _selRow));
                if (addr == 0 && r.Target != 0)
                    addr = NormalizeFollowAddress(r.Target);
            }

            if (addr == 0) return;

            NavigateToAddress(addr, keepVisibleRow: true);
        }

        private uint NormalizeFollowAddress(uint addr)
        {
            if (addr == 0 || _fileData == null)
                return 0u;

            uint normalized     = NormalizeMipsAddress(addr);
            uint normalizedBase = NormalizeMipsAddress(_baseAddr);
            uint normalizedEnd  = normalizedBase + (uint)_fileData.Length;
            if (normalizedEnd <= normalizedBase)
                return 0u;
            if (normalized < normalizedBase || normalized >= normalizedEnd)
                return 0u;

            // Return in the same segment as _baseAddr so it matches row addresses.
            return (_baseAddr & 0xE0000000u) | normalized;
        }

        /// <summary>
        /// Strips the PS2 MIPS virtual-address segment bits so that addresses in
        /// KSEG0 (0x80000000), KSEG1 (0xA0000000), or the 0x20000000 region all
        /// map to the corresponding physical address (e.g. 0x80000280 → 0x00000280).
        /// </summary>
        private static uint NormalizeMipsAddress(uint addr)
        {
            // Top 3 bits encode the MIPS segment; mask them off.
            return addr & 0x1FFFFFFFu;
        }

        /// <summary>
        /// For real memory-access instructions and clear address-build helpers, compute the
        /// effective address by combining the instruction's immediate with the upper
        /// half loaded by a nearby <c>lui</c>. Returns 0 when the row is not clearly
        /// participating in a data access.
        /// </summary>
        private bool TryGetAccessMonitorSpec(DisassemblyRow r, int rowIdx, out uint address, out string type, out uint sizeBytes)
        {
            address = 0;
            type = "readwrite";
            sizeBytes = 4u;

            uint word = r.Word;
            uint op = (word >> 26) & 0x3F;

            if (TryGetLoadStoreAccessSpec(op, out string directType, out uint directSizeBytes))
            {
                uint directAddress = ComputeDataAddress(r, rowIdx);
                if (directAddress != 0 && ShouldDereferenceAnnotationAddress(directAddress))
                {
                    address = directAddress;
                    type = directType;
                    sizeBytes = directSizeBytes;
                    return true;
                }
            }

            uint rt = (word >> 16) & 0x1F;
            uint ui = word & 0xFFFF;
            int si = (short)(word & 0xFFFF);
            uint rs = (word >> 21) & 0x1F;

            if (op == 0x0F && rt != 0)
                return TryResolveAccessSpecFromBuilderForwardTrace(rowIdx, rt, ui << 16, out address, out type, out sizeBytes);

            if (op is 0x08 or 0x09 or 0x0D)
            {
                uint baseValue = rs == 0 ? 0 : ResolveRegisterAddressValue(rowIdx, rs);
                if (baseValue == 0)
                    return false;

                uint candidate = op == 0x0D ? (baseValue | ui) : (baseValue + (uint)si);
                return TryResolveAccessSpecFromBuilderForwardTrace(rowIdx, rt, candidate, out address, out type, out sizeBytes);
            }

            return false;
        }

        private bool TryResolveAccessSpecFromBuilderForwardTrace(int rowIdx, uint trackedReg, uint trackedValue, out uint address, out string type, out uint sizeBytes)
        {
            address = 0;
            type = "readwrite";
            sizeBytes = 4u;

            if (trackedReg == 0)
                return false;

            for (int i = rowIdx + 1; i < Math.Min(_rows.Count, rowIdx + 10); i++)
            {
                uint w = _rows[i].Word;
                uint op = (w >> 26) & 0x3F;
                uint rs = (w >> 21) & 0x1F;
                uint rt = (w >> 16) & 0x1F;
                uint rd = (w >> 11) & 0x1F;
                uint fn = w & 0x3F;
                int si = (short)(w & 0xFFFF);
                uint ui = w & 0xFFFF;

                if (TryGetLoadStoreAccessSpec(op, out string tracedType, out uint tracedSizeBytes) && rs == trackedReg)
                {
                    uint tracedAddress = trackedValue + (uint)si;
                    if (!ShouldDereferenceAnnotationAddress(tracedAddress))
                        return false;

                    address = tracedAddress;
                    type = tracedType;
                    sizeBytes = tracedSizeBytes;
                    return true;
                }

                if (op is 0x08 or 0x09)
                {
                    if (rs == trackedReg && rt != 0)
                    {
                        trackedValue += (uint)si;
                        trackedReg = rt;
                        continue;
                    }
                }
                else if (op == 0x0D)
                {
                    if (rs == trackedReg && rt != 0)
                    {
                        trackedValue |= ui;
                        trackedReg = rt;
                        continue;
                    }
                }
                else if (op == 0x00)
                {
                    bool copiedTracked =
                        (fn == 0x21 || fn == 0x25) && rd != 0 &&
                        ((rs == trackedReg && rt == 0) || (rt == trackedReg && rs == 0));

                    if (copiedTracked)
                    {
                        trackedReg = rd;
                        continue;
                    }
                }

                if (WritesRegister(w, trackedReg))
                    break;
            }

            return false;
        }

        private static bool TryGetLoadStoreAccessSpec(uint op, out string type, out uint sizeBytes)
        {
            type = "readwrite";
            sizeBytes = 4u;

            switch (op)
            {
                case 0x20: // lb
                case 0x24: // lbu
                    type = "read";
                    sizeBytes = 1u;
                    return true;
                case 0x21: // lh
                case 0x25: // lhu
                    type = "read";
                    sizeBytes = 2u;
                    return true;
                case 0x22: // lwl
                case 0x23: // lw
                case 0x26: // lwr
                case 0x27: // lwu
                case 0x31: // lwc1
                    type = "read";
                    sizeBytes = 4u;
                    return true;
                case 0x1A: // ldl
                case 0x1B: // ldr
                case 0x37: // ld
                    type = "read";
                    sizeBytes = 8u;
                    return true;
                case 0x1E: // lq
                case 0x36: // lqc2
                    type = "read";
                    sizeBytes = 16u;
                    return true;
                case 0x28: // sb
                    type = "write";
                    sizeBytes = 1u;
                    return true;
                case 0x29: // sh
                    type = "write";
                    sizeBytes = 2u;
                    return true;
                case 0x2A: // swl
                case 0x2B: // sw
                case 0x2E: // swr
                case 0x39: // swc1
                    type = "write";
                    sizeBytes = 4u;
                    return true;
                case 0x2C: // sdl
                case 0x2D: // sdr
                case 0x3F: // sd
                    type = "write";
                    sizeBytes = 8u;
                    return true;
                case 0x1F: // sq
                case 0x3E: // sqc2
                    type = "write";
                    sizeBytes = 16u;
                    return true;
                default:
                    return false;
            }
        }

        private uint ComputeDataAddress(DisassemblyRow r, int rowIdx)
        {
            uint w = r.Word;
            uint op = (w >> 26) & 0x3F;
            uint rs = (w >> 21) & 0x1F;
            uint rt = (w >> 16) & 0x1F;
            int si = (short)(w & 0xFFFF);
            uint ui = w & 0xFFFF;

            // LUI: return the upper half this instruction loads (no forward trace)
            if (op == 0x0F)
                return ui << 16;

            bool isLoadStore = IsLoadStore(op);
            bool isAddrBuild = op is 0x08 or 0x09 or 0x0D;
            if (!isLoadStore && !isAddrBuild)
                return 0;
            if (rs == 0)
                return 0;

            // Resolve the base register value by scanning backward
            uint baseValue = ResolveRegisterAddressValue(rowIdx, rs);
            if (baseValue == 0)
                return 0;

            // Return the address THIS instruction computes (no forward trace)
            return isLoadStore ? baseValue + (uint)si
                : op == 0x0D ? baseValue | ui
                : baseValue + (uint)si;
        }

        private bool TryResolveAddressFromBuilderForwardTrace(int rowIdx, uint trackedReg, uint trackedValue, out uint address)
        {
            address = 0;
            if (trackedReg == 0)
                return false;

            for (int i = rowIdx + 1; i < Math.Min(_rows.Count, rowIdx + 10); i++)
            {
                uint w = _rows[i].Word;
                uint op = (w >> 26) & 0x3F;
                uint rs = (w >> 21) & 0x1F;
                uint rt = (w >> 16) & 0x1F;
                uint rd = (w >> 11) & 0x1F;
                uint fn = w & 0x3F;
                int si = (short)(w & 0xFFFF);
                uint ui = w & 0xFFFF;

                if (IsLoadStore(op) && rs == trackedReg)
                {
                    address = trackedValue + (uint)si;
                    return true;
                }

                if (op is 0x08 or 0x09)
                {
                    if (rs == trackedReg && rt != 0)
                    {
                        trackedValue += (uint)si;
                        trackedReg = rt;
                        continue;
                    }
                }
                else if (op == 0x0D)
                {
                    if (rs == trackedReg && rt != 0)
                    {
                        trackedValue |= ui;
                        trackedReg = rt;
                        continue;
                    }
                }
                else if (op == 0x00)
                {
                    bool copiedTracked =
                        (fn == 0x21 || fn == 0x25) && rd != 0 &&
                        ((rs == trackedReg && rt == 0) || (rt == trackedReg && rs == 0));

                    if (copiedTracked)
                    {
                        trackedReg = rd;
                        continue;
                    }
                }

                if (WritesRegister(w, trackedReg))
                    break;
            }

            return false;
        }

        private static bool WritesRegister(uint word, uint reg)
        {
            if (reg == 0) return false;

            uint op = (word >> 26) & 0x3F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            uint fn = word & 0x3F;

            return op switch
            {
                0x00 => fn switch
                {
                    0x08 or 0x09 or 0x11 or 0x13 or 0x18 or 0x19 or 0x1A or 0x1B or 0x2A or 0x2B => false,
                    _ => rd == reg,
                },
                0x01 => rt is 0x0A or 0x0B ? rt == reg : false,
                0x02 or 0x03 => false,
                0x04 or 0x05 or 0x06 or 0x07 or 0x14 or 0x15 or 0x16 or 0x17 => false,
                0x28 or 0x29 or 0x2A or 0x2B or 0x2E or 0x2F or 0x39 or 0x3E or 0x3F => false,
                0x10 => rs is 0x00 or 0x02 or 0x06 ? rt == reg : false,
                0x11 => rs is 0x00 or 0x02 or 0x06 ? rt == reg : false,
                0x12 => rs is 0x00 or 0x02 or 0x06 ? rt == reg : false,
                _ => rt == reg,
            };
        }

        private static bool IsLoadStore(uint op)
            => op is >= 0x20 and <= 0x2F    // lb..swr / cache
            or 0x1A or 0x1B or 0x1F         // ldl ldr sq
            or 0x31 or 0x36 or 0x37         // lwc1 lqc2 ld
            or 0x39 or 0x3E or 0x3F;        // swc1 sqc2 sd

        // ══════════════════════════════════════════════════════════════════
        // Keyboard
        // ══════════════════════════════════════════════════════════════════

        private static Control? FindFocusedLeafControl(Control root)
        {
            var c = root;
            while (c is ContainerControl cc && cc.ActiveControl != null)
                c = cc.ActiveControl;
            return c;
        }

        private void OnFormKeyDown(object? s, KeyEventArgs e)
        {
            if (e.Alt && e.KeyCode == Keys.A) { RunXrefAnalyzer(); e.Handled = e.SuppressKeyPress = true; return; }
            // Handle Space + F3 at form level so KeyPreview catches them before the ListView's Win32 internals
            if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.Space && _disasmList.ContainsFocus)
                { SetXrefTarget(); e.Handled = e.SuppressKeyPress = true; return; }
            if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.F3)
                { GotoNextXref(); e.Handled = e.SuppressKeyPress = true; return; }
            if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.O)
            {
                var focused = FindFocusedLeafControl(this);
                if (focused is not RichTextBox and not TextBox and not ComboBox)
                {
                    ShowOptionsDialog();
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                }
            }
            if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.M && IsLiveAttached())
            {
                var focused = FindFocusedLeafControl(this);
                if (focused is not RichTextBox and not TextBox and not ComboBox)
                {
                    ShowAccessMonitorWindow();
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                }
            }
            if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.P && !IsLiveAttached())
            {
                var focused = FindFocusedLeafControl(this);
                if (focused is not RichTextBox and not TextBox and not ComboBox)
                {
                    AttachToPcsx2();
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                }
            }
            if (!e.Control) return;
            switch (e.KeyCode)
            {
                case Keys.O: OpenBinary();          e.Handled = true; break;
                case Keys.G:
                    if (e.Shift) ShowGoto();
                    else         ShowLabelBrowser();
                    e.Handled = e.SuppressKeyPress = true;
                    break;
                case Keys.F: ShowFind();          e.Handled = true; break;
                case Keys.C:
                    // Let text editing controls (RichTextBox, TextBox) handle their own Ctrl+C
                    {
                        var focused = FindFocusedLeafControl(this);
                        if (focused is RichTextBox or TextBox)
                            return;
                    }
                    if (_hexList.ContainsFocus)
                    {
                        CopyHexSelection();
                        e.Handled = e.SuppressKeyPress = true;
                    }
                    else
                    {
                        CopySelected();
                        e.Handled = true;
                    }
                    break;
            }
        }


        private void OnFormKeyUp(object? s, KeyEventArgs e)
        {
            if (_heldReinterpretMode.HasValue && e.KeyCode is Keys.U or Keys.W or Keys.H or Keys.B or Keys.F)
                StopHeldReinterpret();
        }

        private void OnListKeyDown(object? s, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                case Keys.Right:                          FollowTarget();       break;
                case Keys.Left:                           GoBack();             break;
                case Keys.L:                              AddOrEditLabel();     break;
                case Keys.G when !e.Control && !e.Shift: ShowGoto();           break;
                case Keys.U when !e.Control && !e.Shift: StartHeldReinterpret(DisplayReinterpret.Instruction); break;
                case Keys.W when !e.Control && !e.Shift: StartHeldReinterpret(DisplayReinterpret.Word);        break;
                case Keys.H when !e.Control && !e.Shift: StartHeldReinterpret(DisplayReinterpret.Half);        break;
                case Keys.B when !e.Control && !e.Shift: StartHeldReinterpret(DisplayReinterpret.Byte);        break;
                case Keys.F when !e.Control && !e.Shift: StartHeldReinterpret(DisplayReinterpret.Float);       break;
                default: return; // don't suppress
            }
            e.Handled = e.SuppressKeyPress = true;
        }

        private void OnListKeyUp(object? s, KeyEventArgs e)
        {
            if (_heldReinterpretMode.HasValue && e.KeyCode is Keys.U or Keys.W or Keys.H or Keys.B or Keys.F)
            {
                StopHeldReinterpret();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void StartHeldReinterpret(DisplayReinterpret mode)
        {
            if (_heldReinterpretMode == mode)
                return;

            _heldReinterpretMode = mode;
            ConvertSelection(mode, advanceSelection: true);
            _reinterpretTimer?.Stop();
            _reinterpretTimer?.Start();
        }

        private void StopHeldReinterpret()
        {
            _reinterpretTimer?.Stop();
            _heldReinterpretMode = null;
        }

        private enum DisplayReinterpret { Instruction, Word, Half, Byte, Float }

        // ── Data-type reinterpretation (U / W / H / B / F keys) ───────────────
        private void ConvertSelection(DisplayReinterpret mode, bool advanceSelection = false)
        {
            if (_selRow < 0 || _selRow >= _rows.Count) return;
            if (_fileData == null) return;

            uint addr    = _rows[_selRow].Address;
            uint aligned = addr & ~3u;
            long off     = (long)(aligned - _baseAddr);
            if (off < 0 || off + 4 > _fileData.Length) return;

            uint word = BitConverter.ToUInt32(_fileData, (int)off);
            if (!TryGetRowIndexByAddress(aligned, out int firstIdx)) return;

            int count = 0;
            while (firstIdx + count < _rows.Count &&
                   _rows[firstIdx + count].Address >= aligned &&
                   _rows[firstIdx + count].Address < aligned + 4)
                count++;
            if (count == 0) return;

            var eng  = GetCachedDisasm();
            var newRows = new List<SlimRow>();
            const int advanceBytes = 4;

            switch (mode)
            {
                case DisplayReinterpret.Instruction:
                    newRows.Add(SlimRowFromWord(word, aligned));
                    break;
                case DisplayReinterpret.Word:
                    newRows.Add(SlimRow.DataRow(aligned, word, DataKind.Word));
                    break;
                case DisplayReinterpret.Half:
                    for (int i = 0; i < 2; i++)
                    {
                        ushort hv = (ushort)(word >> (i * 16));
                        newRows.Add(SlimRow.DataRow((uint)(aligned + i * 2), hv, DataKind.Half));
                    }
                    break;
                case DisplayReinterpret.Byte:
                    for (int i = 0; i < 4; i++)
                    {
                        byte bv = (byte)(word >> (i * 8));
                        newRows.Add(SlimRow.DataRow((uint)(aligned + i), bv, DataKind.Byte));
                    }
                    break;
                case DisplayReinterpret.Float:
                    newRows.Add(SlimRow.DataRow(aligned, word, DataKind.Float));
                    break;
                default:
                    return;
            }

            uint nextAddr = aligned + (uint)advanceBytes;
            _reinterpretNesting++;
            try
            {
                _disasmList.BeginUpdate();
                _rows.RemoveRange(firstIdx, count);
                _rows.InsertRange(firstIdx, newRows);
                _addrIndexDirty = true;

                if (_disasmList.VirtualListSize != _rows.Count)
                    _disasmList.VirtualListSize = _rows.Count;

                _jumpSkipStart = -1;
                _jumpSkipEnd = -1;

                int selectIdx = firstIdx;
                if (advanceSelection && TryGetRowIndexByAddress(nextAddr, out int nextIdx))
                    selectIdx = nextIdx;

                _suppressHistoryPush = true;
                try
                {
                    _disasmList.SelectedIndexChanged -= OnDisasmSelChanged;
                    _disasmList.SelectedIndices.Clear();
                    if (selectIdx >= 0 && selectIdx < _rows.Count)
                    {
                        _disasmList.SelectedIndices.Add(selectIdx);
                        UpdateSel(selectIdx, syncHex: false);
                        _disasmList.EnsureVisible(selectIdx);
                    }
                    _disasmList.SelectedIndexChanged += OnDisasmSelChanged;
                }
                finally
                {
                    _suppressHistoryPush = false;
                }

                if (selectIdx >= 0 && selectIdx < _rows.Count)
                    _selRow = selectIdx;

                if (selectIdx >= 0 && selectIdx < _rows.Count && !IsRowVisible(selectIdx))
                    _disasmList.EnsureVisible(selectIdx);

                if (firstIdx >= 0 && firstIdx < _rows.Count)
                    _disasmList.RedrawItems(firstIdx, Math.Min(_rows.Count - 1, firstIdx + Math.Max(newRows.Count, 2)), true);
            }
            finally
            {
                _disasmList.EndUpdate();
                _reinterpretNesting = Math.Max(0, _reinterpretNesting - 1);
            }
        }

        // ── Xref analyzer ─────────────────────────────────────────────────

        private static uint NormalizeAnalyzedTarget(uint addr, uint baseAddr, uint dataEnd)
        {
            if (addr == 0)
                return 0;

            // Use the same 29-bit mask (strip top 3 MIPS segment bits) that
            // NormalizeMipsAddress uses, so KSEG0/KSEG1/KUSEG all map to physical.
            uint normalized     = addr     & 0x1FFFFFFFu;
            uint normalizedBase = baseAddr & 0x1FFFFFFFu;
            uint normalizedEnd  = dataEnd  & 0x1FFFFFFFu;

            // Handle wrap-around (shouldn't happen on PS2 but be safe)
            if (normalizedEnd <= normalizedBase)
                return 0;

            if (normalized < normalizedBase || normalized >= normalizedEnd)
                return 0;

            // Return the address in the same segment as baseAddr so it matches
            // row addresses stored in _rows.
            return (baseAddr & 0xE0000000u) | normalized;
        }

        private void CancelXrefAnalysis()
        {
            try { _xrefCts?.Cancel(); } catch { }
            try { _xrefCts?.Dispose(); } catch { }
            _xrefCts = null;
            _xrefAnalysisRunning = false;
        }

        private void QueueXrefAnalyzerAfterDisassembly(bool quiet, bool extendedCleanup = false)
        {
            if (_queuedXrefAnalyze)
            {
                _queuedXrefAnalyzeQuiet &= quiet;
                _queuedXrefAnalyzeExtendedCleanup |= extendedCleanup;
                return;
            }

            _queuedXrefAnalyze = true;
            _queuedXrefAnalyzeQuiet = quiet;
            _queuedXrefAnalyzeExtendedCleanup = extendedCleanup;
        }

        private void StartQueuedXrefAnalyzerIfReady()
        {
            if (!_queuedXrefAnalyze || _disassemblyRunning || _fileData == null)
                return;

            bool quiet = _queuedXrefAnalyzeQuiet;
            bool extendedCleanup = _queuedXrefAnalyzeExtendedCleanup;
            _queuedXrefAnalyze = false;
            _queuedXrefAnalyzeQuiet = true;
            _queuedXrefAnalyzeExtendedCleanup = false;
            RunXrefAnalyzer(quiet: quiet, rebuildDisassembly: false, extendedCleanup: extendedCleanup);
        }

        private void ClearXrefResults()
        {
            bool hadResults = _xrefs.Count != 0 || _xrefTarget != 0 || _xrefIdx != 0;
            _xrefTarget = 0;
            _xrefIdx = 0;
            if (!hadResults)
                return;

            _xrefs = new Dictionary<uint, uint[]>();
            _disasmList.Invalidate();
        }

        private void FinalizeAnalysisCleanup(bool extendedCleanup)
        {
            _cachedDisasm = null;
            if (_rows.Count > 0)
                _rows.TrimExcess();
            if (_cachedLabels.Count > 0)
                _cachedLabels.TrimExcess();
            _addrToRow = null;
            _addrIndexDirty = true;
            QueueManagedMemoryTrimBurst(extended: extendedCleanup);
        }

        private void RunXrefAnalyzer(bool quiet = false, bool rebuildDisassembly = false, bool extendedCleanup = false)
        {
            if (_fileData == null)
            {
                if (!quiet) MessageBox.Show("No file loaded.", "Analyzer");
                return;
            }

            if (rebuildDisassembly && _selRow >= 0 && _selRow < _rows.Count)
            {
                _pendingNavAddr = _rows[_selRow].Address;
                _pendingNavVisibleOffset = GetSelectedVisibleRowOffset();
                _pendingNavCenter = false;
            }

            if (rebuildDisassembly)
            {
                QueueXrefAnalyzerAfterDisassembly(quiet, extendedCleanup: extendedCleanup);
                StartDisassembly();
                return;
            }

            if (_disassemblyRunning)
            {
                QueueXrefAnalyzerAfterDisassembly(quiet, extendedCleanup: extendedCleanup);
                _sbProgress.Text = "Analyzer queued…";
                return;
            }

            CancelXrefAnalysis();
            ClearXrefResults();
            var runCts = new CancellationTokenSource();
            _xrefCts = runCts;
            _xrefAnalysisRunning = true;
            var token = runCts.Token;

            _sbProgress.Text = "Analyzing…";
            _progressBar.Value = 0;
            _progressBar.Visible = true;
            SetActivityStatus("Analyzing...", 0);

            byte[] data = _fileData;
            uint baseAddr = _baseAddr;

            Task.Run(() =>
            {
                Dictionary<uint, uint[]>? compactXrefs = null;
                string? error = null;

                try
                {
                    int total = Math.Max(1, data.Length / 4);
                    int report = Math.Max(total / 100, 1);
                    uint dataEnd = baseAddr + (uint)data.Length;

                    void ReportProgress(int i)
                    {
                        if (i / 4 % report != 0)
                            return;

                        int pct = Math.Min(99, (i / 4) * 100 / total);
                        BeginInvoke((Action)(() =>
                        {
                            if (token.IsCancellationRequested)
                                return;
                            _progressBar.Value = pct;
                            SetActivityStatus("Analyzing...", pct);
                        }));
                    }

                    void ScanTargets(Action<uint, uint> onTarget)
                    {
                        var luiVal = new uint[32];
                        var hasLui = new bool[32];

                        for (int i = 0; i + 3 < data.Length; i += 4)
                        {
                            token.ThrowIfCancellationRequested();
                            ReportProgress(i);

                            uint w = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
                            uint pc = baseAddr + (uint)i;
                            uint op = (w >> 26) & 0x3F;
                            uint rs = (w >> 21) & 0x1F;
                            uint rt = (w >> 16) & 0x1F;
                            uint rd = (w >> 11) & 0x1F;
                            uint funct = w & 0x3F;
                            int si = (short)(w & 0xFFFF);
                            uint ui = w & 0xFFFF;

                            uint target = 0;

                            bool isBranch = op is 0x04 or 0x05 or 0x06 or 0x07
                                              or 0x14 or 0x15 or 0x16 or 0x17;
                            bool isRegimm = op == 0x01 && rt is 0x00 or 0x01 or 0x02 or 0x03
                                                                  or 0x10 or 0x11 or 0x12 or 0x13;
                            bool isCop1Branch = op == 0x11 && rs == 0x08 && (rt & 0x1C) <= 0x04;
                            bool isJump = op is 0x02 or 0x03;

                            bool isLoad = op is 0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27
                                              or 0x1A or 0x1B or 0x1E or 0x31 or 0x36 or 0x37;
                            bool isStore = op is 0x28 or 0x29 or 0x2A or 0x2B or 0x2C or 0x2D or 0x2E
                                               or 0x1F or 0x39 or 0x3E or 0x3F;

                            bool isJr = op == 0x00 && funct == 0x08;
                            if (isJr)
                                Array.Clear(hasLui, 0, hasLui.Length);

                            if (isBranch || isRegimm || isCop1Branch)
                                target = (uint)(pc + 4 + (si << 2));
                            else if (isJump)
                                target = ((pc + 4) & 0xF0000000) | ((w & 0x03FFFFFF) << 2);
                            else if (op == 0x0F)
                            {
                                luiVal[rt] = ui << 16;
                                hasLui[rt] = true;
                            }
                            else if (op == 0x09 && hasLui[rs])
                            {
                                target = (uint)((int)luiVal[rs] + si);
                                luiVal[rt] = target;
                                hasLui[rt] = true;
                                if (rt != rs)
                                    hasLui[rs] = false;
                            }
                            else if (op == 0x0D && hasLui[rs])
                            {
                                target = luiVal[rs] | ui;
                                luiVal[rt] = target;
                                hasLui[rt] = true;
                                if (rt != rs)
                                    hasLui[rs] = false;
                            }
                            else if ((isLoad || isStore) && hasLui[rs])
                            {
                                target = (uint)((int)luiVal[rs] + si);
                            }
                            else if (op == 0x00 && funct != 0x08 && funct != 0x09)
                            {
                                if (rd != 0)
                                    hasLui[rd] = false;
                            }
                            else if (op != 0x00 && op != 0x01 && op != 0x0F
                                  && !isBranch && !isJump && !isStore && !isCop1Branch)
                            {
                                if (rt != 0)
                                    hasLui[rt] = false;
                            }

                            // If no instruction-based target was found, check whether the
                            // raw 32-bit word is a plausible data pointer (e.g. a vtable
                            // entry, function pointer table, or .word address constant).
                            // Require 4-byte alignment to reduce false positives.
                            if (target == 0 && (w & 3) == 0 && w != 0)
                            {
                                uint normalizedW = NormalizeAnalyzedTarget(w, baseAddr, dataEnd);
                                if (normalizedW != 0)
                                    target = w;
                            }

                            uint normalizedTarget = NormalizeAnalyzedTarget(target, baseAddr, dataEnd);
                            if (normalizedTarget != 0)
                                onTarget(normalizedTarget, pc);
                        }
                    }

                    var targetCounts = new Dictionary<uint, int>();
                    ScanTargets((targetAddr, fromAddr) =>
                    {
                        if (targetCounts.TryGetValue(targetAddr, out int count))
                            targetCounts[targetAddr] = count + 1;
                        else
                            targetCounts[targetAddr] = 1;
                    });

                    compactXrefs = new Dictionary<uint, uint[]>(targetCounts.Count);
                    foreach (var kv in targetCounts)
                    {
                        compactXrefs[kv.Key] = new uint[kv.Value];
                        targetCounts[kv.Key] = 0;
                    }

                    ScanTargets((targetAddr, fromAddr) =>
                    {
                        uint[] refs = compactXrefs[targetAddr];
                        int idx = targetCounts[targetAddr];
                        refs[idx] = fromAddr;
                        targetCounts[targetAddr] = idx + 1;
                    });

                    targetCounts.Clear();
                    targetCounts.TrimExcess();
                }
                catch (OperationCanceledException)
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (ReferenceEquals(_xrefCts, runCts))
                        {
                            _xrefAnalysisRunning = false;
                            _xrefCts?.Dispose();
                            _xrefCts = null;
                            _progressBar.Visible = false;
                            SetReadyStatus();
                            StartQueuedXrefAnalyzerIfReady();
                        }
                    }));
                    return;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                BeginInvoke((Action)(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    if (ReferenceEquals(_xrefCts, runCts))
                    {
                        _xrefAnalysisRunning = false;
                        _xrefCts?.Dispose();
                        _xrefCts = null;
                    }

                    _xrefs = compactXrefs ?? new Dictionary<uint, uint[]>();
                    compactXrefs = null;

                    if (_rows.Count > 0 && _disasmList.TopIndex >= 0)
                    {
                        int visStart = _disasmList.TopIndex;
                        int visEnd = Math.Min(_rows.Count - 1, visStart + _disasmList.VisibleRowCapacity + 1);
                        RefreshVisibleDisassemblyRows(visStart, visEnd);
                    }

                    _disasmList.Invalidate();
                    _progressBar.Value = 100;
                    _progressBar.Visible = false;

                    if (error != null)
                    {
                        _sbProgress.Text = $"Analyzer error: {error}";
                        SetActivityStatus("Analyzer error");
                        MessageBox.Show($"Analyzer failed:\n{error}",
                            "Analyzer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else
                    {
                        _sbProgress.Text = $"Analyzer: {_xrefs.Count:N0} targets mapped.";
                        SetReadyStatus();
                    }

                    FinalizeAnalysisCleanup(extendedCleanup);
                    StartQueuedXrefAnalyzerIfReady();
                }));
            }, token);
        }

        // Space — mark current row as xref watch target (goes grey)
        private void SetXrefTarget()
        {
            if (_selRow < 0 || _selRow >= _rows.Count) return;
            uint addr = _rows[_selRow].Address;

            if (_xrefTarget == addr)
            {
                _xrefTarget = 0;
                _sbProgress.Text = "Xref mark cleared.";
            }
            else
            {
                _xrefTarget = addr;
                _xrefIdx    = 0;
                int count = _xrefs.TryGetValue(addr, out var refs) ? refs.Length : -1;
                _sbProgress.Text = count >= 0
                    ? $"Marked ${addr:X8} — {count} referrer(s). Press F3 to navigate."
                    : $"Marked ${addr:X8} — run Analyzer first (Alt+A).";
            }
            _disasmList.Invalidate();
            _disasmList.Update(); // force immediate repaint
        }

        // F3 — jump to the next caller of the xref target
        private void GotoNextXref()
        {
            if (_xrefTarget == 0)
            {
                MessageBox.Show("No row is marked.\n\nPress Space on a row to mark it, then press F3 to navigate to its callers.",
                    "F3 — Xref Navigate", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_xrefs.Count == 0)
            {
                MessageBox.Show("Xref data not ready.\n\nWait for the analyzer to finish (watch the status bar), or press Alt+A to run it manually.",
                    "F3 — Xref Navigate", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_xrefs.TryGetValue(_xrefTarget, out var callers) || callers.Length == 0)
            {
                MessageBox.Show($"No references to ${_xrefTarget:X8} found in the binary.\n\n" +
                    "The analyzer tracks static branches, jumps (BEQ/BNE/J/JAL etc.),\n" +
                    "LUI+ADDIU/ORI address construction, and raw data pointers (.word).\n" +
                    "Register-indirect calls (JALR/JR) are not tracked.",
                    "F3 — Xref Navigate", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _sbProgress.Text = $"No references to ${_xrefTarget:X8}.";
                return;
            }

            uint callerAddr = callers[_xrefIdx % callers.Length];
            _xrefIdx = (_xrefIdx + 1) % callers.Length;

            _sbProgress.Text = $"Xref {_xrefIdx}/{callers.Length} → ${callerAddr:X8}";

            int visibleOffset = GetSelectedVisibleRowOffset();

            // Caller is in the current window — navigate directly
            if (TryGetRowIndexByAddress(callerAddr, out int row))
            {
                SelectRow(row, center: false);
                ApplyVisibleRowOffset(row, visibleOffset);
                return;
            }

            // Caller is outside the current window — re-disassemble the full file
            if (_fileData == null) return;
            uint off = callerAddr >= _baseAddr ? callerAddr - _baseAddr : 0;
            if (off >= (uint)_fileData.Length)
            {
                MessageBox.Show($"Caller ${callerAddr:X8} is outside the file range.", "F3 — Xref Navigate");
                return;
            }

            _pendingNavAddr = callerAddr;
            _pendingNavVisibleOffset = visibleOffset;
            _pendingNavCenter = false;
            _disasmBase     = _baseAddr;
            _disasmLen      = (uint)_fileData.Length;
            StartDisassembly();
        }

        private void GoBack()
        {
            while (_navBack.Count > 0)
            {
                uint addr = _navBack.Pop();
                if (_selRow >= 0 && _selRow < _rows.Count && _rows[_selRow].Address == addr)
                    continue;

                _suppressHistoryPush = true;
                try
                {
                    NavigateToAddress(addr, keepVisibleRow: true);
                    return;
                }
                finally
                {
                    _suppressHistoryPush = false;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Dialogs
        // ══════════════════════════════════════════════════════════════════


        // Binary search: last row whose Address <= addr (-1 if none)
        private int FindNearestRow(uint addr)
        {
            int lo = 0, hi = _rows.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_rows[mid].Address <= addr) { best = mid; lo = mid + 1; }
                else                            { hi = mid - 1; }
            }
            return best;
        }

        private int FindFirstRowAtOrAfter(uint addr)
        {
            int lo = 0, hi = _rows.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_rows[mid].Address >= addr) { best = mid; hi = mid - 1; }
                else                            { lo = mid + 1; }
            }
            return best;
        }

        private void ShowGoto()
        {
            using var dlg = new GotoAddressDialog(_lastGotoAddress,
                backColor: _themeFormBack, foreColor: _themeFormFore,
                tbBackColor: _themeWindowBack, tbForeColor: _themeWindowFore);
            dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
            var result = dlg.ShowDialog(this);
            _lastGotoAddress = dlg.Value;
            if (result != DialogResult.OK) return;
            string val = dlg.Value.Trim();
            if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) val = val[2..];
            if (!uint.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out uint addr))
            { MessageBox.Show("Invalid hex address.", "ps2dis", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            // Normalize kernel/segment addresses (e.g. 0x80000280 → 0x00000280)
            addr = NormalizeMipsAddress(addr);

            int visibleOffset = GetSelectedVisibleRowOffset();

            // Exact match first, then nearest preceding row
            if (TryGetRowIndexByAddress(addr, out int idx))
            {
                SelectRow(idx, center: false);
                ApplyVisibleRowOffset(idx, visibleOffset);
                return;
            }
            int nearest = FindNearestRow(addr);
            if (nearest >= 0)
            {
                SelectRow(nearest, center: false);
                ApplyVisibleRowOffset(nearest, visibleOffset);
                return;
            }

            if (_fileData == null) return;
            uint off = addr >= _baseAddr ? addr - _baseAddr : 0;
            if (off >= _fileData.Length)
                MessageBox.Show($"0x{addr:X8} is outside the file range.", "ps2dis");
        }

        private void ShowFind()
        {
            if (_findDialog != null && !_findDialog.IsDisposed)
            {
                _findDialog.BringToFront();
                _findDialog.FocusSearchBox();
                return;
            }

            _findDialog = new FindDialog();
            _findDialog.FindNext += OnFindNext;
            _findDialog.FormClosed += (_, _) => _findDialog = null;
            ApplyThemeToControlTree(_findDialog);
            // Center on parent (CenterParent is unreliable for non-modal Show)
            _findDialog.StartPosition = FormStartPosition.Manual;
            _findDialog.Location = new Point(
                Left + (Width - _findDialog.Width) / 2,
                Top + (Height - _findDialog.Height) / 2);
            _findDialog.Show(this);
            ApplyThemeToWindowChrome(_findDialog, forceFrameRefresh: true);
        }

        private async void OnFindNext(object? sender, EventArgs e)
        {
            if (_findDialog == null || _rows.Count == 0) return;

            string query = _findDialog.SearchText;
            if (string.IsNullOrEmpty(query)) return;

            FindMode mode = _findDialog.Mode;
            bool caseSensitive = _findDialog.CaseSensitive;
            bool wrapAround = _findDialog.WrapAround;

            int start = (_selRow + 1) % Math.Max(1, _rows.Count);
            int count = wrapAround ? _rows.Count : Math.Max(0, _rows.Count - start);

            try
            {
                UseWaitCursor = true;
                if (_findDialog != null && !_findDialog.IsDisposed)
                    _findDialog.UseWaitCursor = true;

                if (mode == FindMode.HexPattern)
                {
                    var patterns = ParseHexPatternCandidates(query);
                    if (patterns.Count == 0)
                    {
                        MessageBox.Show("Invalid hex pattern.", "Find", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var (searchData, searchBaseAddr, searchError) = await Task.Run(ReadFindSearchDataSnapshot);
                    if (!string.IsNullOrEmpty(searchError))
                    {
                        MessageBox.Show(searchError, "Find", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    if (searchData != null && searchData.Length > 0)
                    {
                        int startOffset = start < _rows.Count ? (int)(_rows[start].Address - searchBaseAddr) : 0;
                        int bestHitOffset = await Task.Run(() =>
                        {
                            int localBestHitOffset = -1;
                            int localBestDistance = int.MaxValue;
                            for (int patternIndex = 0; patternIndex < patterns.Count; patternIndex++)
                            {
                                int hitOffset = FindHexPatternInData(searchData, patterns[patternIndex], startOffset, wrapAround);
                                if (hitOffset < 0)
                                    continue;

                                int distance = ComputeForwardPatternDistance(searchData.Length, startOffset, hitOffset, wrapAround);
                                if (distance < localBestDistance)
                                {
                                    localBestDistance = distance;
                                    localBestHitOffset = hitOffset;
                                }
                            }
                            return localBestHitOffset;
                        });

                        if (bestHitOffset >= 0)
                        {
                            uint hitAddr = searchBaseAddr + (uint)bestHitOffset;
                            if (TryGetRowIndexByAddress(hitAddr, out int idx) || ((idx = FindNearestRow(hitAddr)) >= 0))
                            {
                                SelectRow(idx, center: true);
                                return;
                            }
                        }
                    }

                    MessageBox.Show("Hex pattern not found.", "Find", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var (stringSearchData, stringSearchBaseAddr, stringSearchError) = await Task.Run(ReadFindSearchDataSnapshot);
                if (!string.IsNullOrEmpty(stringSearchError))
                {
                    MessageBox.Show(stringSearchError, "Find", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (stringSearchData != null && stringSearchData.Length > 0)
                {
                    byte[] queryBytes = Encoding.UTF8.GetBytes(query);
                    if (queryBytes.Length > 0)
                    {
                        int startOffset = start < _rows.Count ? (int)(_rows[start].Address - stringSearchBaseAddr) : 0;
                        int hitOffset = await Task.Run(() => FindStringInData(stringSearchData, queryBytes, startOffset, wrapAround, caseSensitive));
                        if (hitOffset >= 0)
                        {
                            uint hitAddr = stringSearchBaseAddr + (uint)hitOffset;
                            if (TryGetRowIndexByAddress(hitAddr, out int idx) || ((idx = FindNearestRow(hitAddr)) >= 0))
                            {
                                SelectRow(idx, center: true);
                                return;
                            }
                        }
                    }
                }

                // String search — check cheap fields first, avoid expensive GetCommandText
                StringComparison cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var disasm = GetCachedDisasm();
                for (int i = 0; i < count; i++)
                {
                    int idx = (start + i) % _rows.Count;
                    var sr = _rows[idx];

                    // 1. Address (trivial)
                    string addr = sr.Address.ToString("X8");
                    if (addr.Contains(query, cmp))
                    { SelectRow(idx, center: true); return; }

                    // 2. Hex word (trivial)
                    string hexWord = sr.Word.ToString("X8");
                    if (hexWord.Contains(query, cmp))
                    { SelectRow(idx, center: true); return; }

                    // 3. Label (dictionary lookup — cheap)
                    string? label = GetLabelAt(sr.Address);
                    if (label != null && label.Contains(query, cmp))
                    { SelectRow(idx, center: true); return; }

                    // 4. Mnemonic + operands (lightweight disassembly, no ComputeDataAddress)
                    if (sr.DataSub == DataKind.None && sr.Kind != InstructionType.Data)
                    {
                        var dr = disasm.DisassembleSingleWord(sr.Word, sr.Address);
                        if ((!string.IsNullOrEmpty(dr.Mnemonic) && dr.Mnemonic.Contains(query, cmp)) ||
                            (!string.IsNullOrEmpty(dr.Operands) && dr.Operands.Contains(query, cmp)))
                        { SelectRow(idx, center: true); return; }
                    }
                }

                MessageBox.Show($"\"{query}\" not found.", "Find", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                UseWaitCursor = false;
                if (_findDialog != null && !_findDialog.IsDisposed)
                    _findDialog.UseWaitCursor = false;
            }
        }

        private (byte[]? Data, uint BaseAddress, string? Error) ReadFindSearchDataSnapshot()
        {
            uint baseAddress = _baseAddr;

            if (_fileData == null || _fileData.Length == 0)
                return (null, baseAddress, "No loaded memory data to search.");

            if (!IsLiveAttached())
                return (_fileData, baseAddress, null);

            var validNames = new[] { "pcsx2", "pcsx2-qt" };
            var proc = Process.GetProcesses()
                .FirstOrDefault(p => validNames.Any(name => string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)));
            if (proc == null)
                return (_fileData, baseAddress, null);

            uint access = NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_INFO;
            IntPtr hProc = NativeMethods.OpenProcess(access, false, (uint)proc.Id);
            if (hProc == IntPtr.Zero)
                return (_fileData, baseAddress, null);

            try
            {
                var ram = ReadEeRamViaSymbol(hProc, out string? err);
                if (ram != null && ram.Length > 0)
                    return (ram, 0u, null);
                return (_fileData, baseAddress, err);
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }
        }

        private static int FindStringInData(byte[] data, byte[] pattern, int startOffset, bool wrap, bool caseSensitive)
        {
            if (data == null || pattern == null || pattern.Length == 0 || data.Length < pattern.Length)
                return -1;

            startOffset = Math.Clamp(startOffset, 0, Math.Max(0, data.Length - 1));
            int limit = data.Length - pattern.Length;
            if (limit < 0)
                return -1;

            bool MatchesAt(int index)
            {
                for (int i = 0; i < pattern.Length; i++)
                {
                    byte a = data[index + i];
                    byte b = pattern[i];
                    if (caseSensitive)
                    {
                        if (a != b) return false;
                    }
                    else
                    {
                        if (a >= (byte)'A' && a <= (byte)'Z') a = (byte)(a + 0x20);
                        if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 0x20);
                        if (a != b) return false;
                    }
                }
                return true;
            }

            for (int i = startOffset; i <= limit; i++)
                if (MatchesAt(i))
                    return i;

            if (wrap)
            {
                int wrapLimit = Math.Min(startOffset - 1, limit);
                for (int i = 0; i <= wrapLimit; i++)
                    if (MatchesAt(i))
                        return i;
            }

            return -1;
        }

        private static List<byte[]> ParseHexPatternCandidates(string hex)
        {
            var patterns = new List<byte[]>();
            if (string.IsNullOrWhiteSpace(hex))
                return patterns;

            static void AddCandidate(List<byte[]> target, byte[]? candidate)
            {
                if (candidate == null || candidate.Length == 0)
                    return;
                if (target.Any(existing => existing.AsSpan().SequenceEqual(candidate)))
                    return;
                target.Add(candidate);
            }

            static string NormalizeHexToken(string token)
            {
                token = token.Trim();
                if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    token = token[2..];
                return token;
            }

            string[] tokenized = hex
                .Replace("-", " ")
                .Replace(",", " ")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string[] tokens = tokenized.Select(NormalizeHexToken).Where(t => t.Length > 0).ToArray();
            string rawJoined = string.Concat(tokens);

            bool explicitWordTokens = tokens.Length > 0 && tokens.All(t => t.Length == 8);
            bool implicitWordPattern = tokens.Length == 1 && rawJoined.Length >= 8 && rawJoined.Length % 8 == 0;
            if (explicitWordTokens || implicitWordPattern)
            {
                string[] wordTokens = explicitWordTokens
                    ? tokens
                    : Enumerable.Range(0, rawJoined.Length / 8).Select(i => rawJoined.Substring(i * 8, 8)).ToArray();
                AddCandidate(patterns, ParseDisplayedWordPattern(wordTokens));
            }

            AddCandidate(patterns, ParseRawHexPattern(rawJoined));
            return patterns;
        }

        private static byte[]? ParseRawHexPattern(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
                return null;

            try
            {
                byte[] result = new byte[hex.Length / 2];
                for (int i = 0; i < result.Length; i++)
                    result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? ParseDisplayedWordPattern(IEnumerable<string> wordTokens)
        {
            try
            {
                var bytes = new List<byte>();
                foreach (string token in wordTokens)
                {
                    if (!uint.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out uint word))
                        return null;
                    bytes.AddRange(BitConverter.GetBytes(word));
                }
                return bytes.Count > 0 ? bytes.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        private static int ComputeForwardPatternDistance(int dataLength, int startOffset, int hitOffset, bool wrap)
        {
            if (!wrap || hitOffset >= startOffset)
                return Math.Max(0, hitOffset - startOffset);
            return Math.Max(0, (dataLength - startOffset) + hitOffset);
        }

        private static int FindHexPatternInData(byte[] data, byte[] pattern, int startOffset, bool wrap)
        {
            int len = data.Length;
            int pLen = pattern.Length;
            if (pLen == 0 || pLen > len) return -1;
            int searchLen = wrap ? len : Math.Max(0, len - startOffset);
            for (int i = 0; i < searchLen; i++)
            {
                int offset = (startOffset + i) % len;
                if (offset + pLen > len && !wrap) break;
                bool match = true;
                for (int j = 0; j < pLen; j++)
                {
                    if (data[(offset + j) % len] != pattern[j])
                    { match = false; break; }
                }
                if (match) return offset;
            }
            return -1;
        }

        private void ShowSetRegion()
        {
            using var dlg = new RawDumpDialog(_baseAddr, _fileData != null ? (uint)_fileData.Length : 0);
            dlg.BackColor = _themeFormBack;
            dlg.ForeColor = _themeFormFore;
            ApplyThemeToControlTree(dlg);
            dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _baseAddr   = dlg.BaseAddress;
            _disasmBase = dlg.BaseAddress;
            StartDisassembly();
        }

        private void GotoEntry()
        {
            if (_elfInfo == null) { MessageBox.Show("No ELF loaded.", "ps2dis"); return; }
            if (TryGetRowIndexByAddress(_elfInfo.EntryPoint, out int idx))
                SelectRow(idx);
            else
                MessageBox.Show($"Entry 0x{_elfInfo.EntryPoint:X8} not found in disassembly.", "ps2dis");
        }


    }
}
