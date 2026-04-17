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
        // ── Column management ─────────────────────────────────────────────

        private void RebuildColumns()
        {
            CancelInlineEdit();
            _disasmList.Columns.Clear();
            _disasmList.Columns.Add("Address",  64);
            if (_showHex)   _disasmList.Columns.Add("Hex",   64);
            if (_showBytes) _disasmList.Columns.Add("Bytes", 100);
            _disasmList.Columns.Add("Label",    280); //344
            _disasmList.Columns.Add("Command",  448);
            ApplyDisasmViewMetrics();
            UpdateDisassemblyColumnWidths();
        }

        private void UpdateDisassemblyColumnWidths()
        {
            if (_disasmList.Columns.Count == 0) return;

            int addressWidth = ScaleColumnWidthFromDefault("00000000", 64);
            if (_disasmList.Columns[0].Width != addressWidth)
                _disasmList.Columns[0].Width = addressWidth;
            if (_showHex && _disasmList.Columns.Count > 1)
            {
                int hexWidth = ScaleColumnWidthFromDefault("00000000", 64);
                if (_disasmList.Columns[1].Width != hexWidth)
                    _disasmList.Columns[1].Width = hexWidth;
            }

            int used = 0;
            for (int i = 0; i < _disasmList.Columns.Count - 1; i++)
                used += _disasmList.Columns[i].Width;

            bool needScrollbar = _disasmList.HasVerticalScrollbar;
            int scrollbarAllowance = needScrollbar ? SystemInformation.VerticalScrollBarWidth : 0;
            int client = Math.Max(0, _disasmList.ClientSize.Width);
            int fill = Math.Max(180, client - used - scrollbarAllowance);
            _disasmList.Columns[CmdCol].Width = fill;
        }

        private void OnMenuBarMouseDown(object? sender, MouseEventArgs e)
        {
            if (ContainsFocus) return;

            if (_menuBar.GetItemAt(e.Location) is ToolStripMenuItem item)
            {
                BeginInvoke(new Action(() =>
                {
                    Activate();
                    item.Select();
                    item.ShowDropDown();
                }));
            }
        }

        private void ApplyHeaderFont(ListView list, Font font)
        {
            IntPtr header = NativeMethods.SendMessage(list.Handle, NativeMethods.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero)
                NativeMethods.SendMessage(header, NativeMethods.WM_SETFONT, font.ToHfont(), (IntPtr)1);
        }


        private Font CreateDefaultReferenceMonoFont()
        {
            try
            {
                var requested = new Font(AppSettings.DefaultFontFamily, AppSettings.DefaultFontSize, FontStyle.Regular);
                if (requested.FontFamily.Name.Equals(AppSettings.DefaultFontFamily, StringComparison.OrdinalIgnoreCase))
                    return requested;

                requested.Dispose();
            }
            catch
            {
                // Fall through to the active monospace family.
            }

            return new Font(_mono.FontFamily, AppSettings.DefaultFontSize, FontStyle.Regular);
        }

        private static int MeasureTextWidth(Font font, string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            return TextRenderer.MeasureText(text, font, new Size(32767, 32767),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        }

        private int ScaleColumnWidthFromDefault(string sampleText, int defaultWidth)
        {
            using var referenceFont = CreateDefaultReferenceMonoFont();
            int referenceTextWidth = Math.Max(1, MeasureTextWidth(referenceFont, sampleText));
            int currentTextWidth = Math.Max(1, MeasureTextWidth(_mono, sampleText));
            int padding = Math.Max(0, defaultWidth - referenceTextWidth);
            return Math.Max(currentTextWidth + padding, currentTextWidth + 4);
        }

        private void UpdateHexColumnWidths()
        {
            if (_hexList == null || _hexList.IsDisposed || _hexList.Columns.Count < 18)
                return;

            int addressWidth = ScaleColumnWidthFromDefault("00000000", 88);
            if (_hexList.Columns[0].Width != addressWidth)
                _hexList.Columns[0].Width = addressWidth;

            int byteColWidth = ScaleColumnWidthFromDefault("00", 20);
            for (int i = 1; i <= 16; i++)
            {
                if (_hexList.Columns[i].Width != byteColWidth)
                    _hexList.Columns[i].Width = byteColWidth;
            }
        }

        private void ApplyDisasmViewMetrics()
        {
            if (_disasmList == null || _disasmList.IsDisposed) return;
            _disasmList.Font = _mono;
            _disasmList.RowHeight = Math.Max(1, _disasmRowHeight);
            _disasmList.HeaderHeight = Math.Max(1, _mono.Height + 8);
            _disasmList.Invalidate();
        }

        private void ApplyHexViewMetrics()
        {
            if (_hexList == null || _hexList.IsDisposed) return;
            _hexList.Font = _mono;
            _hexList.RowHeight = Math.Max(1, _disasmRowHeight);
            _hexList.HeaderHeight = 0;
            UpdateHexColumnWidths();
            _hexList.Invalidate();
        }

        private int GetDisasmRowHeight()
        {
            if (_disasmList == null || _disasmList.IsDisposed) return Math.Max(1, _disasmRowHeight);
            return Math.Max(1, _disasmList.RowHeight);
        }

        private void DrawDisasmHeader(object? sender, VirtualDisasmList.VirtualHeaderPaintEventArgs e)
        {
            using var back = new SolidBrush(_headerBack);
            e.Graphics.FillRectangle(back, e.Bounds);

            string text = (e.Header.Text ?? string.Empty).Replace("□", string.Empty).Trim();
            var contentBounds = new Rectangle(
                e.Bounds.X + 4,
                e.Bounds.Y + 1,
                Math.Max(0, e.Bounds.Width - 8),
                Math.Max(0, e.Bounds.Height - 2));

            var flags = TextFormatFlags.Left
                      | TextFormatFlags.VerticalCenter
                      | TextFormatFlags.NoPrefix
                      | TextFormatFlags.SingleLine
                      | TextFormatFlags.EndEllipsis
                      | TextFormatFlags.NoPadding;

            TextRenderer.DrawText(e.Graphics, text, _mono, contentBounds, _headerFore, flags);

            using var pen = new Pen(_headerBorder);
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            using var divPen = new Pen(Color.FromArgb(80, _headerBorder));
            // Skip the right-edge divider on the last column to avoid a stray line in the filled gap
            if (e.ColumnIndex < _disasmList.Columns.Count - 1)
                e.Graphics.DrawLine(divPen, e.Bounds.Right - 1, e.Bounds.Top + 2, e.Bounds.Right - 1, e.Bounds.Bottom - 3);
        }

        // ══════════════════════════════════════════════════════════════════
        // File loading
        // ══════════════════════════════════════════════════════════════════

        private static bool IsPcsx2DisProjectBinary(byte[] data)
        {
            if (data == null || data.Length < 8)
                return false;

            ushort magic = BitConverter.ToUInt16(data, 0);
            return magic == 0x3713;
        }

        private static bool TryExtractProjectMemoryImage(byte[] data, out byte[] memoryImage, out string error)
        {
            memoryImage = Array.Empty<byte>();
            error = string.Empty;

            if (!IsPcsx2DisProjectBinary(data))
            {
                error = "File is not a valid PCSX2dis project.";
                return false;
            }

            if (data.Length < 8)
            {
                error = "Project file is truncated.";
                return false;
            }

            uint memLength = BitConverter.ToUInt32(data, 4);
            long end = 8L + memLength;
            if (end > data.Length)
            {
                error = "Project memory image is truncated.";
                return false;
            }

            memoryImage = new byte[memLength];
            if (memLength > 0)
                Buffer.BlockCopy(data, 8, memoryImage, 0, (int)memLength);
            return true;
        }

        private void OpenBinary()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Open Binary",
                Filter = "All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                LoadFile(dlg.FileName, isRawDump: false);
        }

        private async void ImportLabelsFromElf()
        {
            if (_fileData == null)
            {
                MessageBox.Show(this, "Load a file or attach to PCSX2 first.",
                    "Import Labels", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dlg = new OpenFileDialog
            {
                Title  = "Import Labels",
                Filter = "Label files (*.elf;*.pis)|*.elf;*.pis|PS2 ELF (*.elf)|*.elf|PS2dis dump (*.pis)|*.pis|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();

            if (ext == ".pis")
            {
                await ImportLabelsFromPisFile(dlg.FileName);
                return;
            }

            byte[] elfData;
            try   { elfData = File.ReadAllBytes(dlg.FileName); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            if (!ElfParser.IsElf(elfData))
            { MessageBox.Show(this, "File is not a valid ELF.", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            var elf = ElfParser.Parse(elfData);

            _sbProgress.Text     = "Importing labels…";
            _progressBar.Value   = 25;
            _progressBar.Visible = true;
            _progressBar.Maximum = 100;

            bool mergeWithProjectLabels = !string.IsNullOrWhiteSpace(_currentProjectPath);
            var result = await Task.Run(() => CollectElfLabels(elf));

            _progressBar.Value = 80;
            int imported = 0;
            foreach (var (address, elfLabel) in result.labels)
            {
                string finalLabel = elfLabel;
                if (mergeWithProjectLabels && _userLabels.TryGetValue(address, out var existingLabel) && !string.IsNullOrWhiteSpace(existingLabel))
                    finalLabel = MergeProjectAndElfLabel(existingLabel, elfLabel);

                if (_userLabels.TryGetValue(address, out var currentLabel) && string.Equals(currentLabel, finalLabel, StringComparison.Ordinal))
                    continue;

                _userLabels[address] = finalLabel;
                imported++;
            }

            RebuildLabelCache();
            _disasmList.Invalidate();
            _progressBar.Value = 100;
            _progressBar.Visible = false;
            _sbProgress.Text     = $"Imported {imported} label(s).";

            string mergeNote = mergeWithProjectLabels
                ? "\nMatching project labels were merged as ImportedLabel #ProjectLabel."
                : string.Empty;

            MessageBox.Show(this,
                $"Imported {imported} label(s).\n{result.skipped} symbol(s) were skipped.{mergeNote}",
                "Import Labels", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task ImportLabelsFromPisFile(string filePath)
        {
            _sbProgress.Text     = "Importing labels from .pis…";
            _progressBar.Value   = 10;
            _progressBar.Visible = true;
            _progressBar.Maximum = 100;

            byte[] pisData;
            try { pisData = File.ReadAllBytes(filePath); }
            catch (Exception ex)
            {
                _progressBar.Visible = false;
                MessageBox.Show(this, ex.Message, "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            bool mergeWithProjectLabels = !string.IsNullOrWhiteSpace(_currentProjectPath);
            var labels = await Task.Run(() => ParsePisLabels(pisData));

            _progressBar.Value = 80;
            int imported = 0;
            foreach (var (address, pisLabel) in labels)
            {
                string finalLabel = pisLabel;
                if (mergeWithProjectLabels && _userLabels.TryGetValue(address, out var existingLabel) && !string.IsNullOrWhiteSpace(existingLabel))
                    finalLabel = MergeProjectAndElfLabel(existingLabel, pisLabel);

                if (_userLabels.TryGetValue(address, out var currentLabel) && string.Equals(currentLabel, finalLabel, StringComparison.Ordinal))
                    continue;

                _userLabels[address] = finalLabel;
                imported++;
            }

            RebuildLabelCache();
            _disasmList.Invalidate();
            _progressBar.Value = 100;
            _progressBar.Visible = false;
            _sbProgress.Text = $"Imported {imported} label(s) from .pis file.";

            MessageBox.Show(this,
                $"Imported {imported} label(s) from .pis file.\n{labels.Count} total symbol(s) found.",
                "Import Labels", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private const int PisHeaderSize = 0x20;
        private const int PisExtendedHeaderSize = 0x2C;
        private const int PisMemoryChunkHeaderSize = 0x0C;
        private const int PisMemoryChunkDataSize = 0x10000;
        private const int PisEeRamSize = 0x02000000;
        private const int PisEeRamChunkCount = PisEeRamSize / PisMemoryChunkDataSize;
        private const uint PisMemoryChunkMagic = 0x00010004u;
        private const uint PisMemoryChunkTypeEeRam = 0x00000001u;
        private const int PisTailMinimumRunLength = 4;

        private static bool IsPisFile(byte[] data)
            => data.Length >= PisHeaderSize
            && data[0] == (byte)'P'
            && data[1] == (byte)'I'
            && data[2] == (byte)'S'
            && data[3] == 0x1F;

        private static bool TryParsePisFile(byte[] data, out byte[] eeRam, out List<(uint Address, string Label)> labels, out string errorMessage)
        {
            eeRam = Array.Empty<byte>();
            labels = new List<(uint Address, string Label)>();
            errorMessage = string.Empty;

            if (!IsPisFile(data))
            {
                errorMessage = "File is not a valid .pis file.";
                return false;
            }

            if (TryExtractChunkedPisEeRam(data, out eeRam, out int chunkedTailOffset))
            {
                FindPisLabelSectionOffset(data, chunkedTailOffset, chunkedTailOffset, out labels);
                return true;
            }

            int payloadOffset = GetPisPayloadOffset(data);
            if (payloadOffset >= data.Length)
            {
                errorMessage = "The .pis file does not contain a valid EE RAM payload.";
                return false;
            }

            int expectedMemoryEnd = Math.Min(data.Length, payloadOffset + PisEeRamSize);
            int labelSectionOffset = FindPisLabelSectionOffset(data, payloadOffset, expectedMemoryEnd, out labels);
            int memoryEnd = labelSectionOffset > payloadOffset ? Math.Min(labelSectionOffset, expectedMemoryEnd) : expectedMemoryEnd;
            if (memoryEnd <= payloadOffset)
            {
                errorMessage = "The .pis file does not contain a valid EE RAM payload.";
                labels = new List<(uint Address, string Label)>();
                return false;
            }

            eeRam = new byte[memoryEnd - payloadOffset];
            if (eeRam.Length > 0)
                Buffer.BlockCopy(data, payloadOffset, eeRam, 0, eeRam.Length);
            return true;
        }

        private static bool TryExtractChunkedPisEeRam(byte[] data, out byte[] eeRam, out int tailOffset)
        {
            eeRam = Array.Empty<byte>();
            tailOffset = -1;

            if (data.Length < PisHeaderSize + PisMemoryChunkHeaderSize + PisMemoryChunkDataSize)
                return false;

            byte[] ram = new byte[PisEeRamSize];
            var seenChunks = new bool[PisEeRamChunkCount];
            int validChunkCount = 0;
            int fileOffset = PisHeaderSize;
            bool sawChunkRecords = false;

            while (fileOffset + PisMemoryChunkHeaderSize + PisMemoryChunkDataSize <= data.Length)
            {
                if (!TryReadPisMemoryChunkHeader(data, fileOffset, out uint chunkMagic, out uint chunkType, out uint chunkIndex))
                    break;
                if (chunkMagic != PisMemoryChunkMagic || chunkType != PisMemoryChunkTypeEeRam)
                    break;

                sawChunkRecords = true;

                if (chunkIndex < PisEeRamChunkCount && !seenChunks[chunkIndex])
                {
                    int chunkDataOffset = fileOffset + PisMemoryChunkHeaderSize;
                    int ramOffset = checked((int)chunkIndex * PisMemoryChunkDataSize);
                    Buffer.BlockCopy(data, chunkDataOffset, ram, ramOffset, PisMemoryChunkDataSize);
                    seenChunks[chunkIndex] = true;
                    validChunkCount++;
                }

                fileOffset += PisMemoryChunkHeaderSize + PisMemoryChunkDataSize;

                if (validChunkCount == PisEeRamChunkCount)
                {
                    while (fileOffset + PisMemoryChunkHeaderSize + PisMemoryChunkDataSize <= data.Length
                        && TryReadPisMemoryChunkHeader(data, fileOffset, out uint extraMagic, out uint extraType, out _)
                        && extraMagic == PisMemoryChunkMagic
                        && extraType == PisMemoryChunkTypeEeRam)
                    {
                        fileOffset += PisMemoryChunkHeaderSize + PisMemoryChunkDataSize;
                    }

                    eeRam = ram;
                    tailOffset = fileOffset;
                    return true;
                }
            }

            if (!sawChunkRecords || validChunkCount != PisEeRamChunkCount)
                return false;

            eeRam = ram;
            tailOffset = fileOffset;
            return true;
        }

        private static bool TryReadPisMemoryChunkHeader(byte[] data, int offset, out uint chunkMagic, out uint chunkType, out uint chunkIndex)
        {
            chunkMagic = 0;
            chunkType = 0;
            chunkIndex = 0;

            if (offset < 0 || offset + PisMemoryChunkHeaderSize > data.Length)
                return false;

            chunkMagic = BitConverter.ToUInt32(data, offset);
            chunkType = BitConverter.ToUInt32(data, offset + 4);
            chunkIndex = BitConverter.ToUInt32(data, offset + 8);
            return true;
        }

        private static int GetPisPayloadOffset(byte[] data)
        {
            if (data.Length >= PisExtendedHeaderSize
                && BitConverter.ToUInt32(data, PisHeaderSize) == PisMemoryChunkMagic
                && BitConverter.ToUInt32(data, PisHeaderSize + 4) == PisMemoryChunkTypeEeRam
                && BitConverter.ToUInt32(data, PisHeaderSize + 8) == 0x00000000u)
            {
                return PisExtendedHeaderSize;
            }

            return PisHeaderSize;
        }

        private static int FindPisLabelSectionOffset(byte[] data, int payloadOffset, int tailStartOffset, out List<(uint Address, string Label)> labels)
        {
            labels = new List<(uint Address, string Label)>();
            if (!IsPisFile(data) || tailStartOffset < payloadOffset || tailStartOffset >= data.Length)
                return -1;

            int stringFirstOffset = FindPisLabelSectionOffsetCore(data, tailStartOffset, stringFirstLayout: true, out var stringFirstLabels, out int stringFirstEntryCount);
            int addressFirstOffset = FindPisLabelSectionOffsetCore(data, tailStartOffset, stringFirstLayout: false, out var addressFirstLabels, out int addressFirstEntryCount);

            if (stringFirstOffset >= 0 && (stringFirstEntryCount >= addressFirstEntryCount || addressFirstOffset < 0))
            {
                labels = stringFirstLabels;
                return stringFirstOffset;
            }

            if (addressFirstOffset >= 0)
            {
                labels = addressFirstLabels;
                return addressFirstOffset;
            }

            return -1;
        }

        private static int FindPisLabelSectionOffsetCore(byte[] data, int tailStartOffset, bool stringFirstLayout, out List<(uint Address, string Label)> labels, out int totalEntries)
        {
            labels = new List<(uint Address, string Label)>();
            totalEntries = 0;

            int earliestRunStart = -1;
            var mergedByAddress = new Dictionary<uint, string>();

            for (int startOffset = tailStartOffset; startOffset + 5 <= data.Length;)
            {
                if (!TryReadPisLabelEntry(data, startOffset, stringFirstLayout, out uint firstAddress, out string firstLabel, out int firstEntrySize))
                {
                    startOffset++;
                    continue;
                }

                var runByAddress = new Dictionary<uint, string>
                {
                    [NormalizeImportedLabelAddress(firstAddress)] = firstLabel
                };

                int entryCount = 1;
                int pos = startOffset + firstEntrySize;
                while (TryReadPisLabelEntry(data, pos, stringFirstLayout, out uint address, out string label, out int entrySize))
                {
                    uint normalized = NormalizeImportedLabelAddress(address);
                    if (!runByAddress.TryGetValue(normalized, out var existingLabel) || label.Length > existingLabel.Length)
                        runByAddress[normalized] = label;

                    entryCount++;
                    pos += entrySize;
                }

                if (entryCount >= PisTailMinimumRunLength)
                {
                    totalEntries += entryCount;
                    if (earliestRunStart < 0 || startOffset < earliestRunStart)
                        earliestRunStart = startOffset;

                    foreach (var pair in runByAddress)
                    {
                        if (!mergedByAddress.TryGetValue(pair.Key, out var existingLabel) || pair.Value.Length > existingLabel.Length)
                            mergedByAddress[pair.Key] = pair.Value;
                    }
                }

                startOffset = pos > startOffset ? pos : startOffset + 1;
            }

            if (earliestRunStart >= 0)
            {
                labels = mergedByAddress
                    .OrderBy(kv => kv.Key)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
            }

            return earliestRunStart;
        }

        private static bool TryReadPisLabelEntry(byte[] data, int offset, bool stringFirstLayout, out uint address, out string label, out int bytesRead)
        {
            address = 0;
            label = string.Empty;
            bytesRead = 0;

            if (offset < 0 || offset + 5 > data.Length)
                return false;

            if (stringFirstLayout)
            {
                int nameLength = data[offset];
                if (nameLength <= 0 || nameLength > 255 || offset + 1 + nameLength + 4 > data.Length)
                    return false;

                for (int i = 0; i < nameLength; i++)
                {
                    byte b = data[offset + 1 + i];
                    if (b < 0x20 || b > 0x7E)
                        return false;
                }

                uint rawAddress = BitConverter.ToUInt32(data, offset + 1 + nameLength);
                if (rawAddress >= PisEeRamSize)
                    return false;

                string parsedLabel = CleanupPisImportedLabel(Encoding.ASCII.GetString(data, offset + 1, nameLength));
                if (string.IsNullOrWhiteSpace(parsedLabel))
                    return false;

                address = rawAddress;
                label = parsedLabel;
                bytesRead = 1 + nameLength + 4;
                return true;
            }

            uint addressFirstRawAddress = BitConverter.ToUInt32(data, offset);
            int addressFirstNameLength = data[offset + 4];
            if (addressFirstNameLength <= 0 || addressFirstNameLength > 255 || offset + 5 + addressFirstNameLength > data.Length)
                return false;
            if (addressFirstRawAddress >= PisEeRamSize)
                return false;

            for (int i = 0; i < addressFirstNameLength; i++)
            {
                byte b = data[offset + 5 + i];
                if (b < 0x20 || b > 0x7E)
                    return false;
            }

            string addressFirstParsedLabel = CleanupPisImportedLabel(Encoding.ASCII.GetString(data, offset + 5, addressFirstNameLength));
            if (string.IsNullOrWhiteSpace(addressFirstParsedLabel))
                return false;

            address = addressFirstRawAddress;
            label = addressFirstParsedLabel;
            bytesRead = 5 + addressFirstNameLength;
            return true;
        }

        private static string CleanupPisImportedLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return string.Empty;

            string cleaned = label
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Replace('	', ' ')
                .Trim();

            if (cleaned.Length == 0)
                return string.Empty;

            var sb = new StringBuilder(cleaned.Length);
            foreach (char ch in cleaned)
            {
                if (!char.IsControl(ch))
                    sb.Append(ch);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Parses labels from a ps2dis .pis file.
        /// </summary>
        private static List<(uint Address, string Label)> ParsePisLabels(byte[] data)
        {
            if (!TryParsePisFile(data, out _, out var labels, out _))
                return new List<(uint Address, string Label)>();

            return labels;
        }

        private void ClearStoredLabelsForSourceSwitch()
        {
            _autoLabels = new Dictionary<uint, string>();
            _userLabels = new Dictionary<uint, string>();
            _userComments = new Dictionary<uint, string>();
            _stringLabels = new Dictionary<uint, string>();
            _originalOpCode = new Dictionary<uint, uint>();
            _cachedLabels = new List<(string Name, uint Address)>();
            ClearObjectLabelState(clearDefinitions: true);
            _currentProjectPath = null;
        }

        private void OpenPcsx2DisProject()
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "Open PCSX2dis Project",
                Filter = "PCSX2Dis Project (*.ide;*.pcsx2dis)|*.ide;*.pcsx2dis|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            SetActivityStatus("Opening PCSX2dis project...", 0);
            Application.DoEvents();

            try
            {
                SetActivityStatus("Opening PCSX2dis project...", 50);
                Application.DoEvents();

                int imported = ImportLabelsFromProjectBinary(dlg.FileName, loadCodes: true);
                _currentProjectPath = dlg.FileName;
                string projectName = Path.GetFileNameWithoutExtension(dlg.FileName);
                Text = $"ps2dis# \u2014 {projectName}";
                RebuildLabelCache();
                _disasmList.Invalidate();

                string projectFileName = Path.GetFileName(dlg.FileName);
                SetActivityStatus($"{projectFileName} opened - {imported:N0} labels imported.");
            }
            catch (Exception ex)
            {
                SetActivityStatus("Open project failed.");
                MessageBox.Show(this, ex.Message, "Open PCSX2dis Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SavePcsx2DisProjectAs()
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save PCSX2dis Project",
                Filter = "PCSX2Dis Project (*.ide)|*.ide|All files (*.*)|*.*",
                FileName = string.IsNullOrWhiteSpace(_currentProjectPath) ? "project.ide" : Path.GetFileName(_currentProjectPath)
            };
            if (!string.IsNullOrWhiteSpace(_currentProjectPath))
                dlg.InitialDirectory = Path.GetDirectoryName(_currentProjectPath);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            SetActivityStatus("Saving PCSX2dis project...", 50);
            Application.DoEvents();

            try
            {
                WritePcsx2DisProject(dlg.FileName);
                _currentProjectPath = dlg.FileName;

                SetActivityStatus($"Project saved — {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                SetActivityStatus("Save project failed.");
                MessageBox.Show(this, ex.Message, "Save PCSX2dis Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void QuickSaveProject()
        {
            if (string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                SavePcsx2DisProjectAs();
                return;
            }

            SetActivityStatus("Saving PCSX2dis project...", 50);
            Application.DoEvents();

            try
            {
                WritePcsx2DisProject(_currentProjectPath);
                SetActivityStatus($"Project saved — {Path.GetFileName(_currentProjectPath)}");
            }
            catch (Exception ex)
            {
                SetActivityStatus("Save project failed.");
                MessageBox.Show(this, ex.Message, "Save PCSX2dis Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int ImportLabelsFromProjectBinary(string fileName, bool loadCodes)
        {
            using var fs = File.OpenRead(fileName);
            using var br = new BinaryReader(fs, Encoding.Default, leaveOpen: false);

            if (fs.Length < 12)
                return 0;

            ushort magic = br.ReadUInt16();
            ushort version = br.ReadUInt16();
            _objectLabelDefinitions = new List<ObjectLabelDefinition>();
            _objectTempLabels = new Dictionary<uint, string>();
            if (magic != 0x3713)
                return 0;

            uint memLength = br.ReadUInt32();
            long afterMemory = 8L + memLength;
            if (afterMemory + 4 > fs.Length)
                return 0;

            fs.Position = afterMemory;
            uint numLabels = br.ReadUInt32();
            int imported = 0;

            for (uint i = 0; i < numLabels && fs.Position < fs.Length; i++)
            {
                if (fs.Position + 7 > fs.Length)
                    break;

                ushort strLen = br.ReadUInt16();
                uint addr = br.ReadUInt32();
                br.ReadByte(); // autoGenerated

                if (fs.Position + strLen > fs.Length)
                    break;

                string rawLabel = Encoding.Default.GetString(br.ReadBytes(strLen));
                string label = CleanupImportedLabel(rawLabel);
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                uint normalized = NormalizeImportedLabelAddress(addr);
                _userLabels[normalized] = label;
                imported++;
            }

            if (fs.Position + 4 > fs.Length)
                return imported;

            uint commentCount = br.ReadUInt32();
            for (uint i = 0; i < commentCount && fs.Position < fs.Length; i++)
            {
                if (fs.Position + 6 > fs.Length) break;
                ushort strLen = br.ReadUInt16();
                uint addr = br.ReadUInt32();
                if (fs.Position + strLen > fs.Length) break;
                string c = Encoding.Default.GetString(br.ReadBytes(strLen));
                if (!string.IsNullOrWhiteSpace(c))
                    _userComments[NormalizeImportedLabelAddress(addr)] = c;
            }

            uint coveredWords = 0;
            uint totalWords = memLength / 4;
            while (coveredWords < totalWords && fs.Position + 5 <= fs.Length)
            {
                uint rangeLen = br.ReadUInt32();
                br.ReadByte();
                coveredWords += rangeLen;
            }

            if (loadCodes && fs.Position + 4 <= fs.Length)
            {
                int codeLen = br.ReadInt32();
                if (codeLen > 0 && fs.Position + codeLen <= fs.Length)
                {
                    string codes = Encoding.Default.GetString(br.ReadBytes(codeLen));
                    var dlg = EnsureCodeToolsDialog();
                    dlg.SetCodesText(codes);
                    dlg.ActivateCodesSilently();
                }
                else if (codeLen >= 0 && fs.Position + Math.Max(0, codeLen) <= fs.Length)
                {
                    fs.Position += Math.Max(0, codeLen);
                }
            }

            if (fs.Position + 4 <= fs.Length)
            {
                int breakpointCount = br.ReadInt32();
                if (breakpointCount > 0 && fs.Position + (breakpointCount * 8L) <= fs.Length)
                    fs.Position += breakpointCount * 8L;

                if (fs.Position + (12 * 4) <= fs.Length)
                    fs.Position += 12 * 4;

                if (fs.Position + 4 <= fs.Length) br.ReadInt32(); // reg overrides
                if (fs.Position + 4 <= fs.Length) br.ReadInt32(); // struct defs
                if (fs.Position + 4 <= fs.Length) br.ReadInt32(); // struct insts

                if (version >= 0x0005 && fs.Position + 4 <= fs.Length)
                {
                    uint objectCount = br.ReadUInt32();
                    for (uint i = 0; i < objectCount && fs.Position < fs.Length; i++)
                    {
                        if (fs.Position + 8 > fs.Length)
                            break;

                        uint staticAddress = br.ReadUInt32();
                        ushort labelLen = br.ReadUInt16();
                        if (fs.Position + labelLen + 2 > fs.Length)
                            break;
                        string label = Encoding.Default.GetString(br.ReadBytes(labelLen));
                        ushort rawLen = br.ReadUInt16();
                        if (fs.Position + rawLen + 4 > fs.Length)
                            break;
                        string rawText = Encoding.Default.GetString(br.ReadBytes(rawLen));
                        uint fieldCount = br.ReadUInt32();

                        var definition = new ObjectLabelDefinition
                        {
                            StaticAddress = staticAddress,
                            Label = label,
                            RawText = rawText,
                        };

                        bool truncated = false;
                        for (uint fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                        {
                            if (fs.Position + 6 > fs.Length)
                            {
                                truncated = true;
                                break;
                            }

                            uint offset = br.ReadUInt32();
                            ushort fieldLabelLen = br.ReadUInt16();
                            if (fs.Position + fieldLabelLen > fs.Length)
                            {
                                truncated = true;
                                break;
                            }

                            string fieldLabel = Encoding.Default.GetString(br.ReadBytes(fieldLabelLen));
                            if (!string.IsNullOrWhiteSpace(fieldLabel))
                            {
                                definition.Fields.Add(new ObjectLabelField
                                {
                                    Offset = offset,
                                    Label = fieldLabel,
                                });
                            }
                        }

                        _objectLabelDefinitions.Add(definition);
                        if (truncated)
                            break;
                    }
                }
            }

            if (_objectLabelDefinitions.Count > 0)
                ApplyObjectLabelDefinitions(_objectLabelDefinitions, showDialogs: false);

            return imported;
        }

        private void WritePcsx2DisProject(string fileName)
        {
            using var fs = File.Create(fileName);
            using var bw = new BinaryWriter(fs, Encoding.Default, leaveOpen: false);

            ushort magic = 0x3713;
            ushort version = 0x0005;
            bw.Write(magic);
            bw.Write(version);

            byte[] data = _fileData ?? Array.Empty<byte>();
            bw.Write((uint)data.Length);
            if (data.Length > 0)
                bw.Write(data);

            var labelsToSave = new Dictionary<uint, (string Name, byte Auto)>();
            foreach (var kv in _stringLabels)
            {
                string lbl = kv.Value;
                if (string.IsNullOrWhiteSpace(lbl)) continue;
                labelsToSave[kv.Key] = (lbl, 1);
            }
            foreach (var kv in _userLabels)
            {
                string lbl = CleanupImportedLabel(kv.Value);
                if (string.IsNullOrWhiteSpace(lbl)) continue;
                labelsToSave[kv.Key] = (lbl, 0);
            }

            bw.Write((uint)labelsToSave.Count);
            foreach (var kv in labelsToSave.OrderBy(x => x.Key))
            {
                byte[] nameBytes = Encoding.Default.GetBytes(kv.Value.Name);
                bw.Write((ushort)nameBytes.Length);
                bw.Write(kv.Key);
                bw.Write(kv.Value.Auto);
                bw.Write(nameBytes);
            }

            var commentsToSave = _userComments
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .OrderBy(kv => kv.Key)
                .ToList();
            bw.Write((uint)commentsToSave.Count);
            foreach (var kv in commentsToSave)
            {
                byte[] txt = Encoding.Default.GetBytes(kv.Value);
                bw.Write((ushort)txt.Length);
                bw.Write(kv.Key);
                bw.Write(txt);
            }

            var typeByWord = new byte[Math.Max(1, data.Length / 4)];
            Array.Fill(typeByWord, (byte)6); // DATATYPE_CODE
            foreach (var row in _rows)
            {
                if (row.Address < _baseAddr) continue;
                long off = (long)(row.Address - _baseAddr);
                int wi = (int)(off / 4);
                if (wi < 0 || wi >= typeByWord.Length) continue;
                typeByWord[wi] = row.Mnemonic switch
                {
                    ".byte" => 1,
                    ".half" => 2,
                    ".word" => 3,
                    ".float" => 7,
                    _ => 6,
                };
            }
            int rangeStart = 0;
            byte curType = typeByWord.Length > 0 ? typeByWord[0] : (byte)6;
            for (int i = 0; i < typeByWord.Length; i++)
            {
                if (typeByWord[i] == curType) continue;
                bw.Write((uint)(i - rangeStart));
                bw.Write(curType);
                rangeStart = i;
                curType = typeByWord[i];
            }
            bw.Write((uint)(typeByWord.Length - rangeStart));
            bw.Write(curType);

            string codeText = (_codeToolsDlg != null && !_codeToolsDlg.IsDisposed) ? _codeToolsDlg.GetCodesText() : string.Empty;
            byte[] codeBytes = Encoding.Default.GetBytes(codeText ?? string.Empty);
            bw.Write(codeBytes.Length);
            bw.Write(codeBytes);

            bw.Write(0); // breakpoint count
            for (int i = 0; i < 12; i++) bw.Write(0u);
            bw.Write(0); // reg overrides
            bw.Write(0); // struct defs
            bw.Write(0); // struct insts

            var objectsToSave = CloneObjectLabelDefinitions(_objectLabelDefinitions)
                .Where(x => !string.IsNullOrWhiteSpace(x.Label))
                .OrderBy(x => x.StaticAddress)
                .ToList();
            bw.Write((uint)objectsToSave.Count);
            foreach (var definition in objectsToSave)
            {
                bw.Write(definition.StaticAddress);
                byte[] labelBytes = Encoding.Default.GetBytes(definition.Label ?? string.Empty);
                bw.Write((ushort)labelBytes.Length);
                bw.Write(labelBytes);
                string normalizedRawText = ObjectLabelDefinitionParser.BuildCompactDefinitionText(definition);
                byte[] rawBytes = Encoding.Default.GetBytes(normalizedRawText);
                bw.Write((ushort)rawBytes.Length);
                bw.Write(rawBytes);
                bw.Write((uint)definition.Fields.Count);
                foreach (var field in definition.Fields.OrderBy(x => x.Offset))
                {
                    bw.Write(field.Offset);
                    byte[] fieldLabelBytes = Encoding.Default.GetBytes(field.Label ?? string.Empty);
                    bw.Write((ushort)fieldLabelBytes.Length);
                    bw.Write(fieldLabelBytes);
                }
            }

            bw.Write((uint)Math.Max(0, _selRow));
            bw.Write(_selRow >= 0 && _selRow < _rows.Count ? _rows[_selRow].Address : _baseAddr);
        }

        private static string CleanupImportedLabel(string label)
        {
            string original = label.Trim();
            if (original.Length >= 2 && original[0] == '"' && original[^1] == '"')
                return string.Empty;

            label = original.Trim('"', '\'', ';');
            label = label.Replace("\r", "").Replace("\n", "");
            var sb = new StringBuilder(label.Length);
            foreach (char ch in label)
            {
                if (char.IsLetterOrDigit(ch) || ch is '_' or '.' or ':' or '#' or '$' or '@' or '[' or ']' or '(' or ')' or '-' or '+' or '?' or '!')
                    sb.Append(ch);
                else if (ch == ' ')
                    sb.Append(ch);
            }
            string cleaned = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
                return string.Empty;
            if (cleaned.StartsWith("FUNC_", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"^__?[0-9A-Fa-f]{8}$"))
                return string.Empty;
            return cleaned;
        }

        private static string MergeProjectAndElfLabel(string existingLabel, string elfLabel)
        {
            string left = CleanupImportedLabel(existingLabel);
            string right = CleanupImportedLabel(elfLabel);

            if (string.IsNullOrWhiteSpace(left)) return right;
            if (string.IsNullOrWhiteSpace(right)) return left;
            if (string.Equals(left, right, StringComparison.Ordinal)) return left;
            if (right.EndsWith($" #{left}", StringComparison.Ordinal)) return right;
            return $"{right} #{left}";
        }

        private static int ScoreElfSymbol(ElfSymbol sym, string cleanedLabel)
        {
            int score = sym.Type switch
            {
                2 => 300, // FUNC
                1 => 220, // OBJECT
                0 => 180, // NOTYPE
                _ => 0,
            };

            score += sym.Bind switch
            {
                1 => 35,  // GLOBAL
                2 => 25,  // WEAK
                _ => 10,  // LOCAL / others
            };

            if (cleanedLabel.StartsWith("ENTRYPOINT", StringComparison.OrdinalIgnoreCase))
                score += 120;
            if (!cleanedLabel.StartsWith("_", StringComparison.Ordinal))
                score += 10;
            if (char.IsLetter(cleanedLabel[0]))
                score += 5;
            if (cleanedLabel.StartsWith("@", StringComparison.Ordinal))
                score -= 200;
            if (cleanedLabel.StartsWith(".", StringComparison.Ordinal))
                score -= 100;

            return score;
        }

        private bool TryGetElfImportLabel(ElfSymbol sym, out uint address, out string label, out int score)
        {
            address = 0;
            label = string.Empty;
            score = 0;

            if (sym.Value == 0 || sym.Shndx == 0)
                return false;

            if (sym.Type > 2)
                return false;

            label = CleanupImportedLabel(sym.Name);
            if (string.IsNullOrWhiteSpace(label))
                return false;

            if (label.StartsWith("@", StringComparison.Ordinal) || label.StartsWith(".", StringComparison.Ordinal))
                return false;

            address = NormalizeImportedLabelAddress(sym.Value);
            if (!LooksLikeImportedLabelAddress(address))
                return false;

            score = ScoreElfSymbol(sym, label);
            return true;
        }

        private (Dictionary<uint, string> labels, int skipped) CollectElfLabels(ElfInfo elf)
        {
            var bestByAddress = new Dictionary<uint, (string Label, int Score)>();
            int skipped = 0;

            foreach (var sym in elf.Symbols)
            {
                if (!TryGetElfImportLabel(sym, out uint address, out string label, out int score))
                {
                    skipped++;
                    continue;
                }

                if (!bestByAddress.TryGetValue(address, out var existing) ||
                    score > existing.Score ||
                    (score == existing.Score && label.Length > existing.Label.Length))
                {
                    bestByAddress[address] = (label, score);
                }
            }

            return (bestByAddress.ToDictionary(kv => kv.Key, kv => kv.Value.Label), skipped);
        }

        private int ApplyElfLabels(ElfInfo elf, bool mergeWithExistingProjectLabels, out int skipped)
        {
            var (labels, localSkipped) = CollectElfLabels(elf);
            skipped = localSkipped;
            int imported = 0;

            foreach (var (address, elfLabel) in labels)
            {
                string finalLabel = elfLabel;
                if (mergeWithExistingProjectLabels && _userLabels.TryGetValue(address, out var existingLabel) && !string.IsNullOrWhiteSpace(existingLabel))
                    finalLabel = MergeProjectAndElfLabel(existingLabel, elfLabel);

                if (_userLabels.TryGetValue(address, out var currentLabel) && string.Equals(currentLabel, finalLabel, StringComparison.Ordinal))
                    continue;

                _userLabels[address] = finalLabel;
                imported++;
            }

            return imported;
        }

        private static uint NormalizeImportedLabelAddress(uint addr)
            => addr & 0x1FFFFFFFu;

        private bool LooksLikeImportedLabelAddress(uint addr)
        {
            if (_fileData == null) return addr <= 0x02000000;
            long off = (long)addr - _baseAddr;
            return off >= 0 && off < _fileData.Length;
        }

        private static byte[] ReadFileWithProgress(string path, Action<int>? reportProgress = null)
        {
            const int BufferSize = 1024 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long totalLength = fs.Length;
            if (totalLength <= 0)
            {
                reportProgress?.Invoke(100);
                return Array.Empty<byte>();
            }

            if (totalLength > int.MaxValue)
                throw new IOException("File is too large to load into memory.");

            var data = new byte[(int)totalLength];
            int totalRead = 0;
            reportProgress?.Invoke(0);

            while (totalRead < data.Length)
            {
                int take = Math.Min(BufferSize, data.Length - totalRead);
                int read = fs.Read(data, totalRead, take);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of file while reading.");

                totalRead += read;
                reportProgress?.Invoke((int)((long)totalRead * 100 / Math.Max(1, data.Length)));
            }

            return data;
        }

        internal void LoadFile(string path, bool isRawDump)
        {
            StopLiveMode();
            SetActivityStatus("Opening file...", 0);
            try
            {
                byte[] data = ReadFileWithProgress(path, pct => SetActivityStatus("Opening file...", pct));
                ClearStoredLabelsForSourceSwitch();

                _fileName  = Path.GetFileName(path);
                _elfInfo   = null;
                int elfLabelsImported = 0;
                int elfLabelsSkipped = 0;
                int pisLabelsImported = 0;
                bool loadedPis = false;

                if (!isRawDump && IsPisFile(data))
                {
                    if (!TryParsePisFile(data, out var pisEeRam, out var pisLabels, out string pisError))
                        throw new IOException(pisError);

                    _fileData = pisEeRam;
                    _baseAddr = 0x00000000;
                    _disasmBase = 0x00000000;
                    _disasmLen = (uint)pisEeRam.Length;
                    loadedPis = true;

                    foreach (var (address, label) in pisLabels)
                    {
                        if (string.IsNullOrWhiteSpace(label))
                            continue;
                        _userLabels[address] = label;
                    }
                    pisLabelsImported = _userLabels.Count;
                }
                else if (!isRawDump && IsPcsx2DisProjectBinary(data))
                {
                    if (!TryExtractProjectMemoryImage(data, out var projectMemory, out string projectError))
                        throw new IOException(projectError);

                    using var dlg = new RawDumpDialog(0, (uint)projectMemory.Length);
                    dlg.BackColor = _themeFormBack;
                    dlg.ForeColor = _themeFormFore;
                    ApplyThemeToControlTree(dlg);
                    dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                    {
                        SetReadyStatus();
                        return;
                    }

                    _baseAddr = dlg.BaseAddress;
                    _disasmBase = dlg.BaseAddress;
                    _disasmLen = dlg.DisasmLength;
                    _fileData = projectMemory;
                    int imported = ImportLabelsFromProjectBinary(path, loadCodes: true);
                    _currentProjectPath = path;
                    pisLabelsImported = imported;
                }
                else if (!isRawDump && ElfParser.IsElf(data))
                {
                    _elfInfo = ElfParser.Parse(data);

                    if (ElfParser.TryBuildLoadImage(data, _elfInfo, out var loadImage, out var loadBase))
                    {
                        _fileData = loadImage;
                        _baseAddr = loadBase;
                        _disasmBase = loadBase;
                        _disasmLen = (uint)loadImage.Length;
                    }
                    else
                    {
                        _fileData = data;
                        _baseAddr = _elfInfo.LoadAddress;
                        _disasmBase = _elfInfo.LoadAddress;
                        _disasmLen = (uint)data.Length;
                    }

                    elfLabelsImported = ApplyElfLabels(_elfInfo, mergeWithExistingProjectLabels: false, out elfLabelsSkipped);
                }
                else
                {
                    if (!isRawDump)
                    {
                        using var dlg = new RawDumpDialog(_baseAddr, (uint)data.Length);
                        dlg.BackColor = _themeFormBack;
                        dlg.ForeColor = _themeFormFore;
                        ApplyThemeToControlTree(dlg);
                        dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
                        if (dlg.ShowDialog(this) != DialogResult.OK)
                        {
                            SetReadyStatus();
                            return;
                        }

                        _baseAddr   = dlg.BaseAddress;
                        _disasmBase = dlg.BaseAddress;
                        _disasmLen  = dlg.DisasmLength;
                    }

                    _fileData = data;
                }

                _hexRowCount = 1;
                _hexViewOffset = 0;
                _hexList.TopIndex = 0;
                _hexList.VirtualListSize = Math.Max(1, ((_fileData?.Length ?? 0) + 15) / 16);
                AdjustHexSplitter();
                UpdateHexScrollBar();

                _miSave.Enabled = true;
                Text = $"ps2dis# — {_fileName}";
                int loadedLength = _fileData?.Length ?? data.Length;
                bool loadedProjectImage = !loadedPis && _currentProjectPath != null && string.Equals(Path.GetFullPath(_currentProjectPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
                string infoSuffix = _elfInfo != null
                    ? $"    Entry: 0x{_elfInfo.EntryPoint:X8}    ELF labels: {elfLabelsImported:N0}" +
                      (elfLabelsSkipped > 0 ? $" ({elfLabelsSkipped:N0} skipped)" : string.Empty)
                    : (loadedPis
                        ? $"    PIS labels: {pisLabelsImported:N0}"
                        : (loadedProjectImage ? $"    Project labels: {pisLabelsImported:N0}" : string.Empty));
                _sbInfo.Text = $"{_fileName}    {loadedLength:N0} bytes    Base: 0x{_baseAddr:X8}{infoSuffix}";
                _sbSize.Text = $"{loadedLength / (1024.0 * 1024.0):F1} MB";

                CaptureKernelWindowForPreservation();
                CancelXrefAnalysis();
                ScanStringLabels();
                RebuildLabelCache();
                SyncLabelsWindowObjectTabVisibility();
                ClearXrefResults();
                QueueXrefAnalyzerAfterDisassembly(quiet: true, extendedCleanup: true);
                StartDisassembly();
            }
            catch (Exception ex)
            {
                SetReadyStatus();
                MessageBox.Show($"Failed to load file:\n{ex.Message}", "ps2dis",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        // ══════════════════════════════════════════════════════════════════
        // PCSX2 live memory attach
        // ══════════════════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════════════════
        // PINE IPC
        // ══════════════════════════════════════════════════════════════════

        private void ShowPineDebugWindow()
        {
            if (_pineDebugWindow == null || _pineDebugWindow.IsDisposed)
                _pineDebugWindow = new PineDebugWindow();

            if (!_pineDebugWindow.Visible)
            {
                Rectangle ownerBounds = Bounds;
                Size debugSize = _pineDebugWindow.Size;
                int x = ownerBounds.Left + Math.Max(0, (ownerBounds.Width - debugSize.Width) / 2);
                int y = ownerBounds.Top + Math.Max(0, (ownerBounds.Height - debugSize.Height) / 2);

                _pineDebugWindow.StartPosition = FormStartPosition.Manual;
                _pineDebugWindow.Location = new Point(x, y);
                _pineDebugWindow.Show(this);
                // Apply current theme to the newly opened window
                ApplyThemeToControlTree(_pineDebugWindow);
                ApplyThemeToWindowChrome(_pineDebugWindow, forceFrameRefresh: true);
            }

            while (_pineLogBacklog.Count > 0)
                _pineDebugWindow.AppendLine(_pineLogBacklog.Dequeue());

            // Update connection status labels
            _pineDebugWindow.SetPineStatus(_pineAvailable);
            _pineDebugWindow.SetMcpStatus(_debugServerAvailable);

            _pineDebugWindow.BringToFront();
        }

        private void LogPine(string message)
        {
            if (_pineDebugWindow == null || _pineDebugWindow.IsDisposed || !_pineDebugWindow.Visible)
                return;

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            _pineDebugWindow.AppendLine(line);
        }

        private void UpdatePineDebugWindowStatus()
        {
            if (_pineDebugWindow == null || _pineDebugWindow.IsDisposed || !_pineDebugWindow.Visible)
                return;
            _pineDebugWindow.SetPineStatus(_pineAvailable);
            _pineDebugWindow.SetMcpStatus(_debugServerAvailable);
        }

        private bool EnsurePineConnected(bool forceRetry = false)
        {
            if (_pine.IsConnected)
            {
                _pineAvailable = true;
                UpdatePineDebugWindowStatus();
                return true;
            }

            if (!forceRetry && DateTime.UtcNow < _nextPineRetryUtc)
            {
                LogPine("Skipping PINE reconnect until retry window.");
                return false;
            }

            try
            {
                _pine.Connect();
                string version = _pine.GetVersionSafe();
                string title = _pine.GetTitleSafe();
                uint status = _pine.GetStatusSafe();
                _pineAvailable = true;
                _nextPineRetryUtc = DateTime.MinValue;
                LogPine($"Connected to PINE 127.0.0.1:28011 | Version='{version}' | Title='{title}' | Status={status}");
                UpdatePineDebugWindowStatus();
                return true;
            }
            catch (Exception ex)
            {
                _pineAvailable = false;
                _nextPineRetryUtc = DateTime.UtcNow.AddSeconds(5);
                LogPine($"PINE connect failed: {ex.Message}");
                UpdatePineDebugWindowStatus();
                return false;
            }
        }

        private uint OffsetToPineAddress(int offset)
        {
            uint baseAddr = _baseAddr;
            return unchecked(baseAddr + (uint)Math.Max(0, offset));
        }

        private bool TryUpdateVisibleDisassemblerRowsViaPine(VisibleLiveSnapshot? snapshot = null)
        {
            if (_fileData == null || _rows.Count == 0)
                return false;
            if (_reinterpretNesting > 0)
                return false;
            if (!EnsurePineConnected())
                return false;

            if (!TryGetVisibleDisasmReadWindow(out int startRow, out int endRow, out int startOffset, out int readLength))
                return false;

            try
            {
                byte[] data = _pine.ReadMemory(OffsetToPineAddress(startOffset), readLength);
                if (data == null || data.Length <= 0)
                    return false;

                int copyLength = Math.Min(readLength, data.Length);
                bool blockChanged = false;
                for (int i = 0; i < copyLength; i++)
                {
                    if (_fileData[startOffset + i] != data[i])
                    {
                        blockChanged = true;
                        break;
                    }
                }

                if (!blockChanged)
                {
                    _lastVisibleDisasmChangeCount = 0;
                }
                else
                {
                    // Guard against PINE returning all-zeros for kernel/low memory
                    if (IsSuspiciousAllZeroPineRead(data, _fileData, startOffset, copyLength))
                    {
                        _lastVisibleDisasmChangeCount = 0;
                        return true;
                    }
                    Buffer.BlockCopy(data, 0, _fileData, startOffset, copyLength);
                    _lastVisibleDisasmChangeCount = RefreshVisibleDisassemblyRows(startRow, endRow);

                    if (_lastVisibleDisasmChangeCount > 0)
                    {
                        _disasmList.BeginUpdate();
                        try
                        {
                            _disasmList.RedrawItems(startRow, endRow, true);
                            _disasmList.Invalidate();
                        }
                        finally
                        {
                            _disasmList.EndUpdate();
                        }
                    }
                }

                if (_lastVisibleDisasmChangeCount > 0 || DateTime.UtcNow >= _nextPineVisibleReadLogUtc)
                {
                    LogPine($"Live visible-row refresh rows {startRow}-{endRow} @ 0x{OffsetToPineAddress(startOffset):X8}, len {readLength}, changed {_lastVisibleDisasmChangeCount}.");
                    _nextPineVisibleReadLogUtc = DateTime.UtcNow.AddSeconds(1);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogPine($"Live visible-row refresh failed at 0x{OffsetToPineAddress(startOffset):X8}, len {readLength}: {ex.Message}");
                _pine.Disconnect();
                _pineAvailable = false;
                _nextPineRetryUtc = DateTime.UtcNow.AddSeconds(1);
                UpdatePineDebugWindowStatus();
                return false;
            }
        }

        private bool TryReadVisibleViaPine(VisibleLiveSnapshot? snapshot = null, bool includeDisasmRange = true)
        {
            if (_fileData == null)
                return false;
            if (!EnsurePineConnected())
                return false;

            bool anyRead = false;
            foreach (var (start, length) in GetVisibleReadRanges(snapshot, includeDisasmRange))
            {
                if (length <= 0)
                    continue;

                if (start < PreservedKernelWindowLength)
                {
                    int suffixStart = Math.Max(start, PreservedKernelWindowLength);
                    int suffixLength = (start + length) - suffixStart;
                    if (suffixLength <= 0)
                        continue;

                    try
                    {
                        byte[] suffixData = _pine.ReadMemory(OffsetToPineAddress(suffixStart), suffixLength);
                        if (!IsSuspiciousAllZeroPineRead(suffixData, _fileData, suffixStart, suffixData.Length))
                        {
                            Buffer.BlockCopy(suffixData, 0, _fileData, suffixStart, suffixData.Length);
                            anyRead = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogPine($"Read failed at 0x{suffixStart:X8}, len {suffixLength}: {ex.Message}");
                        _pine.Disconnect();
                        _pineAvailable = false;
                        return anyRead;
                    }
                    continue;
                }
                try
                {
                    byte[] data = _pine.ReadMemory(OffsetToPineAddress(start), length);
                    if (!IsSuspiciousAllZeroPineRead(data, _fileData, start, data.Length))
                    {
                        Buffer.BlockCopy(data, 0, _fileData, start, data.Length);
                        anyRead = true;
                    }
                }
                catch (Exception ex)
                {
                    LogPine($"Read failed at 0x{start:X8}, len {length}: {ex.Message}");
                    _pine.Disconnect();
                    _pineAvailable = false;
                    return anyRead;
                }
            }
            return anyRead;
        }

        private void CaptureKernelWindowForPreservation()
        {
            if (_fileData == null || _fileData.Length < PreservedKernelWindowLength)
            {
                _preservedKernelWindow = null;
                return;
            }

            _preservedKernelWindow = new byte[PreservedKernelWindowLength];
            Buffer.BlockCopy(_fileData, 0, _preservedKernelWindow, 0, PreservedKernelWindowLength);
        }

        private void RestorePreservedKernelWindow(byte[] target)
        {
            if (_preservedKernelWindow == null || target.Length < PreservedKernelWindowLength)
                return;

            Buffer.BlockCopy(_preservedKernelWindow, 0, target, 0, PreservedKernelWindowLength);
        }

        /// <summary>
        /// Returns true if <paramref name="pineData"/> is entirely zero but the existing
        /// <paramref name="target"/> slice already contains non-zero bytes.  Used to avoid
        /// overwriting valid kernel/low-memory data with spurious PINE zero-fills.
        /// </summary>
        private static bool IsSuspiciousAllZeroPineRead(byte[] pineData, byte[] target, int targetOffset, int length)
        {
            if (length <= 0) return false;
            for (int i = 0; i < length; i++)
                if (pineData[i] != 0) return false;
            for (int i = 0; i < length; i++)
                if (target[targetOffset + i] != 0) return true;
            return false;
        }

        private void PrefetchPinnedEeRanges()
        {
            if (_fileData == null || !_pineAvailable)
                return;

            // Seed critical regions that often appear blank when the initial
            // attach falls back to process reads. Keep this bounded so attach
            // stays responsive.
            // Do not live-refresh the low kernel window here. On some attach paths
            // PCSX2/PINE surfaces it as zeros, which wipes the previously loaded data.
            // Keep the existing bytes intact unless a future explicit import path is added.
            CaptureKernelWindowForPreservation();

            try
            {
                if (_hexList.Items.Count > 0)
                    _hexList.RedrawItems(0, _hexList.Items.Count - 1, true);
                TryUpdateVisibleDisassemblerRowsViaPine();
            }
            catch
            {
            }
        }

        private string? TryWritePatchesViaPine(IReadOnlyList<(uint Addr, byte[] Bytes)> patches)
        {
            if (patches == null || patches.Count == 0)
                return null;
            LogPine($"Write request: {patches.Count} patch(es).");
            if (!EnsurePineConnected())
                return "PINE unavailable.";

            int failed = 0;
            foreach (var (addr, bytes) in patches)
            {
                if (bytes == null || bytes.Length == 0)
                    continue;
                try
                {
                    _pine.WriteMemory(addr, bytes);
                    byte[] verify = _pine.ReadMemory(addr, bytes.Length);
                    if (!verify.SequenceEqual(bytes))
                    {
                        failed++;
                        LogPine($"Write verify mismatch at 0x{addr:X8}: wrote {BitConverter.ToString(bytes).Replace("-", "")} read {BitConverter.ToString(verify).Replace("-", "")}");
                    }
                    else
                    {
                        LogPine($"Write OK at 0x{addr:X8}: {BitConverter.ToString(bytes).Replace("-", "")}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    LogPine($"Write failed at 0x{addr:X8}: {ex.Message}");
                    _pine.Disconnect();
                    _pineAvailable = false;
                    break;
                }
            }

            return failed > 0 ? $"{failed} of {patches.Count} PINE write(s) failed." : null;
        }

        private void AttachToPcsx2()
        {
            // Clear any existing file/dump data and live state before attaching,
            // so stale data from a previously-opened ELF/dump isn't shown.
            DetachFromPcsx2();

            _sbProgress.Text     = "Attaching to PCSX2…";
            _progressBar.Value   = 0;
            _progressBar.Visible = true;
            SetActivityStatus("Attaching to PCSX2...", 0);

            Task.Run(() =>
            {
                byte[]? eeMem  = null;
                string? errMsg = null;
                uint procId = 0;
                long eeHostAddr = 0;

                try
                {
                    CaptureKernelWindowForPreservation();
                    if (TryLocatePcsx2EeRam(out procId, out eeHostAddr, out errMsg))
                    {
                        IntPtr hProc = NativeMethods.OpenProcess(
                            NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_INFO,
                            false, procId);

                        if (hProc == IntPtr.Zero)
                            errMsg = "Cannot open PCSX2. Try running ps2dis# as Administrator.";
                        else
                        {
                            try   { eeMem = ReadEeRamFromAddress(hProc, eeHostAddr, out errMsg, pct => SetActivityStatus("Attaching to PCSX2...", pct)); }
                            finally { NativeMethods.CloseHandle(hProc); }
                        }
                    }
                }
                catch (Exception ex) { errMsg = ex.Message; }

                BeginInvoke(() =>
                {
                    _progressBar.Visible = false;
                    if (eeMem == null)
                    {
                        MessageBox.Show(errMsg ?? "Read failed.",
                            "Attach to PCSX2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _sbProgress.Text = "Ready.";
                        SetReadyStatus();
                        return;
                    }
                    _liveProcId = procId;
                    _eeHostAddr = eeHostAddr;
                    RestorePreservedKernelWindow(eeMem);
                    InstallEeRam(eeMem);
                    LogPine($"Attach complete. PID={procId}, EEmemHost=0x{eeHostAddr:X}");
                    _pineAvailable = EnsurePineConnected(forceRetry: true);
                    if (_pineAvailable)
                    {
                        PrefetchPinnedEeRanges();
                    }
                    _debugServerAvailable = EnsureDebugServerConnected(forceRetry: true);
                    if (_miBreakpointsMenu != null)
                    {
                        _miBreakpointsMenu.Enabled = true;
                        _miBreakpointsMenu.Visible = true;
                    }
                    if (_miAttach != null) _miAttach.Enabled = false;
                    if (_miDetach != null) _miDetach.Enabled = true;
                    _mainTabs.SetTabEnabled(2, true); // Enable Code Manager now that we're attached
                    _mainTabs.SetTabVisible(2, true);
                    _sbProgress.Text = _debugServerAvailable
                        ? (_pineAvailable ? "Attached to PCSX2 (PINE + debug server connected)." : "Attached to PCSX2 (debug server connected; PINE unavailable).")
                        : (_pineAvailable ? "Attached to PCSX2 (PINE connected; debug server unavailable)." : "Attached to PCSX2 (PINE unavailable; using process memory).");
                    RefreshDebuggerUiTick(force: true);
                });
            });
        }

        private void DetachFromPcsx2()
        {
            // Stop live refresh timer
            StopLiveMode();

            // Clear active codes and stop writing to PCSX2
            if (_codeToolsDlg != null && !_codeToolsDlg.IsDisposed)
                _codeToolsDlg.ClearActiveCodesAndStop();

            // Cancel any background disassembly / xref analysis
            _disCts?.Cancel();
            _disCts?.Dispose();
            _disCts = null;
            _disassemblyRunning = false;
            CancelXrefAnalysis();

            // Clear all breakpoints and access monitors
            _userBreakpoints.Clear();
            _readMemcheckAddress = null;
            _writeMemcheckAddress = null;
            _readMemcheckHits = -1;
            _writeMemcheckHits = -1;
            _watchpointsSuspended = false;
            StopAccessMonitor(quiet: true);
            _activeBreakpointAddress = null;
            _pausedBreakpointUiLatched = false;
            _pausedBreakpointUiAddress = null;
            _breakpointUiFrozen = false;
            _breakpointUiFrozenAddress = null;
            _lastDebuggerPaused = false;

            // Disconnect PINE
            try { _pine.Disconnect(); } catch { }
            _pineAvailable = false;
            _nextPineRetryUtc = DateTime.MinValue;

            // Close PINE debug window
            if (_pineDebugWindow != null && !_pineDebugWindow.IsDisposed)
            {
                try { _pineDebugWindow.Close(); } catch { }
                _pineDebugWindow = null;
            }

            // Disconnect debug server
            try { _debugServer.Disconnect(); } catch { }
            _debugServerAvailable = false;
            _nextDebugServerRetryUtc = DateTime.MinValue;

            // Clear live state
            _liveProcId = 0;
            _eeHostAddr = 0;
            _liveReading = false;

            // Clear file/disassembly data
            _fileData = null;
            _elfInfo = null;
            _fileName = "";
            _baseAddr = 0;
            _disasmBase = 0;
            _disasmLen = 0x02000000;
            _rows = new List<SlimRow>();
            _autoLabels = new Dictionary<uint, string>();
            _userLabels = new Dictionary<uint, string>();
            _userComments = new Dictionary<uint, string>();
            _stringLabels = new Dictionary<uint, string>();
            _originalOpCode = new Dictionary<uint, uint>();
            _cachedLabels = new List<(string Name, uint Address)>();
            ClearObjectLabelState(clearDefinitions: true);
            _cachedDisasm = null;
            _preservedKernelWindow = null;
            _xrefs = new Dictionary<uint, uint[]>();
            _xrefTarget = 0;
            _xrefIdx = 0;
            _xrefAnalysisRunning = false;
            _queuedXrefAnalyze = false;
            _queuedXrefAnalyzeQuiet = true;
            _queuedXrefAnalyzeExtendedCleanup = false;
            _addrToRow = new Dictionary<uint, int>();
            _navBack.Clear();
            _currentProjectPath = null;
            _selRow = -1;
            _jumpSkipStart = -1;
            _jumpSkipEnd = -1;
            _highlightDestIdx = -1;

            // Reset UI
            _disasmList.VirtualListSize = 0;
            _disasmList.Invalidate();
            _hexList.VirtualListSize = 1;
            _hexList.TopIndex = 0;
            _hexList.Invalidate();
            _hexRowCount = 0;
            _hexViewOffset = 0;
            _asciiBytesBar?.Invalidate();

            if (_miSave != null) _miSave.Enabled = false;
            if (_miBreakpointsMenu != null)
            {
                _miBreakpointsMenu.Enabled = false;
                _miBreakpointsMenu.Visible = false;
            }
            if (_miAttach != null) _miAttach.Enabled = true;
            if (_miDetach != null) _miDetach.Enabled = false;
            SetBreakpointSidebarVisible(false);
            if (_mainTabs.SelectedIndex == 2)
                _mainTabs.SelectedIndex = 0;
            _mainTabs.SetTabEnabled(2, false);
            _mainTabs.SetTabVisible(2, false);

            // Reset title and status
            Text = "ps2dis#";
            _sbProgress.Text = "Detached.";
            _sbAddr.Text = "";
            _sbInfo.Text = "";
            _sbSize.Text = "";
            SetReadyStatus();
            SyncLabelsWindowObjectTabVisibility();

            // Reset pause status UI
            _menuPauseStatusActive = false;
            if (_menuStatusLabel != null)
            {
                _menuStatusLabel.Text = "";
                _menuStatusLabel.ForeColor = _headerFore;
                if (_menuPauseStatusSavedFont != null)
                    _menuStatusLabel.Font = _menuPauseStatusSavedFont;
            }

            ForceManagedMemoryTrim();
            QueueManagedMemoryTrimBurst(extended: true);
        }

        private bool TryLocatePcsx2EeRam(out uint procId, out long eeHostAddr, out string? errMsg)
        {
            procId = 0;
            eeHostAddr = 0;
            var validNames = new[] { "pcsx2", "pcsx2-qt" };

            var proc = Process.GetProcesses()
                .FirstOrDefault(p => validNames.Any(name =>
                    string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)));
            if (proc == null)
            {
                errMsg = "No PCSX2 process found.\n\nStart PCSX2 with a game loaded.";
                return false;
            }

            IntPtr hProc = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_INFO,
                false, (uint)proc.Id);
            if (hProc == IntPtr.Zero)
            {
                errMsg = "Cannot open PCSX2. Try running ps2dis# as Administrator.";
                return false;
            }

            try
            {
                eeHostAddr = ResolveEeRamHostAddress(hProc, out errMsg);
                procId = (uint)proc.Id;
                return eeHostAddr != 0;
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }
        }

        private static long ResolveEeRamHostAddress(IntPtr hProc, out string? errMsg)
        {
            var mods = new IntPtr[256];
            if (!NativeMethods.EnumProcessModulesEx(hProc, mods, mods.Length * IntPtr.Size, out _, 0x03))
            { errMsg = "EnumProcessModulesEx failed."; return 0; }

            IntPtr moduleBase = mods[0];
            IntPtr eeSymAddr = FindPeExport(hProc, moduleBase, "EEmem", out bool is64, out errMsg);
            if (eeSymAddr == IntPtr.Zero) return 0;

            if (is64)
            {
                var buf = new byte[8];
                if (!NativeMethods.ReadProcessMemory(hProc, eeSymAddr, buf, 8, out int r) || r < 8)
                { errMsg = "Cannot read EEmem pointer (64-bit)."; return 0; }
                errMsg = null;
                return BitConverter.ToInt64(buf, 0);
            }
            else
            {
                var buf = new byte[4];
                if (!NativeMethods.ReadProcessMemory(hProc, eeSymAddr, buf, 4, out int r) || r < 4)
                { errMsg = "Cannot read EEmem pointer (32-bit)."; return 0; }
                errMsg = null;
                return BitConverter.ToUInt32(buf, 0);
            }
        }

        private static bool IsReadableProtection(uint protect)
        {
            const uint PAGE_NOACCESS          = 0x01;
            const uint PAGE_READONLY          = 0x02;
            const uint PAGE_READWRITE         = 0x04;
            const uint PAGE_WRITECOPY         = 0x08;
            const uint PAGE_EXECUTE           = 0x10;
            const uint PAGE_EXECUTE_READ      = 0x20;
            const uint PAGE_EXECUTE_READWRITE = 0x40;
            const uint PAGE_EXECUTE_WRITECOPY = 0x80;
            const uint PAGE_GUARD             = 0x100;

            if ((protect & PAGE_GUARD) != 0 || (protect & PAGE_NOACCESS) != 0)
                return false;

            uint baseProt = protect & 0xFF;
            return baseProt == PAGE_READONLY ||
                   baseProt == PAGE_READWRITE ||
                   baseProt == PAGE_WRITECOPY ||
                   baseProt == PAGE_EXECUTE ||
                   baseProt == PAGE_EXECUTE_READ ||
                   baseProt == PAGE_EXECUTE_READWRITE ||
                   baseProt == PAGE_EXECUTE_WRITECOPY;
        }

        private static bool TryReadRemoteSlice(IntPtr hProc, long remoteAddress, byte[] buffer, int bufferOffset, int length, out int bytesRead)
        {
            bytesRead = 0;
            if (length <= 0 || bufferOffset < 0 || bufferOffset + length > buffer.Length)
                return false;

            var temp = new byte[length];
            if (NativeMethods.ReadProcessMemory(hProc, new IntPtr(remoteAddress), temp, length, out int read) && read > 0)
            {
                Buffer.BlockCopy(temp, 0, buffer, bufferOffset, read);
                bytesRead = read;
                return read == length;
            }

            bytesRead = 0;
            return false;
        }

        private static int ReadRemoteReliable(IntPtr hProc, long remoteAddress, byte[] buffer, int bufferOffset, int length)
        {
            if (length <= 0)
                return 0;

            if (TryReadRemoteSlice(hProc, remoteAddress, buffer, bufferOffset, length, out int directRead))
                return directRead;

            if (directRead > 0)
                return directRead;

            if (length <= 1)
                return 0;

            int leftLen = length / 2;
            int rightLen = length - leftLen;
            int leftRead = ReadRemoteReliable(hProc, remoteAddress, buffer, bufferOffset, leftLen);
            int rightRead = ReadRemoteReliable(hProc, remoteAddress + leftLen, buffer, bufferOffset + leftLen, rightLen);
            return leftRead + rightRead;
        }

        private static byte[]? ReadEeRamFromAddress(IntPtr hProc, long eeHostAddr, out string? errMsg, Action<int>? reportProgress = null)
        {
            if (eeHostAddr == 0)
            { errMsg = "EEmem pointer is null — is a game loaded in PCSX2?"; return null; }

            const int EE_SIZE = 0x2000000;
            const int CHUNK_SIZE = 0x10000;
            var eeMem = new byte[EE_SIZE];

            if (TryReadRemoteSlice(hProc, eeHostAddr, eeMem, 0, EE_SIZE, out int wholeRead) && wholeRead == EE_SIZE)
            {
                reportProgress?.Invoke(100);
                errMsg = null;
                return eeMem;
            }

            int totalRead = 0;
            reportProgress?.Invoke(0);
            for (int offset = 0; offset < EE_SIZE; offset += CHUNK_SIZE)
            {
                int take = Math.Min(CHUNK_SIZE, EE_SIZE - offset);
                totalRead += ReadRemoteReliable(hProc, eeHostAddr + offset, eeMem, offset, take);
                reportProgress?.Invoke((offset + take) * 100 / EE_SIZE);
            }

            if (totalRead <= 0)
            {
                errMsg = $"ReadProcessMemory failed for EE RAM (0 / {EE_SIZE:N0} bytes).";
                return null;
            }

            errMsg = totalRead < EE_SIZE
                ? $"ReadProcessMemory partially succeeded for EE RAM ({totalRead:N0} / {EE_SIZE:N0} bytes)."
                : null;
            return eeMem;
        }

        private static byte[]? ReadEeRamViaSymbol(IntPtr hProc, out string? errMsg)
        {
            long eeHostAddr = ResolveEeRamHostAddress(hProc, out errMsg);
            if (eeHostAddr == 0) return null;
            return ReadEeRamFromAddress(hProc, eeHostAddr, out errMsg, null);
        }

        private bool ReadEeRamVisible(VisibleLiveSnapshot? snapshot = null)
        {
            if (_fileData == null)
                return false;

            if (TryReadVisibleViaPine(snapshot))
                return true;

            if (_liveProcId == 0 || _eeHostAddr == 0)
                return false;

            IntPtr hProc = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_INFO,
                false, _liveProcId);
            if (hProc == IntPtr.Zero)
                return false;

            try
            {
                bool anyRead = false;
                foreach (var (start, length) in GetVisibleReadRanges(snapshot))
                {
                    if (length <= 0) continue;

                    if (start < PreservedKernelWindowLength)
                    {
                        int suffixStart = Math.Max(start, PreservedKernelWindowLength);
                        int suffixLength = (start + length) - suffixStart;
                        if (suffixLength > 0)
                        {
                            int read = ReadRemoteReliable(hProc, _eeHostAddr + suffixStart, _fileData, suffixStart, suffixLength);
                            if (read > 0)
                                anyRead = true;
                        }
                        continue;
                    }

                    int fullRead = ReadRemoteReliable(hProc, _eeHostAddr + start, _fileData, start, length);
                    if (fullRead > 0)
                        anyRead = true;
                }
                return anyRead;
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }
        }

        private VisibleLiveSnapshot CaptureVisibleLiveSnapshot(int disasmPadding = 4)
        {
            var snapshot = new VisibleLiveSnapshot
            {
                DisasmStartRow = 0,
                DisasmEndRow = -1,
                DisasmReadStartOffset = 0,
                DisasmReadLength = 0,
                HexStartOffset = _hexList.TopIndex * 16,
                HexLength = Math.Max(16, _hexRowCount * 16),
            };

            if (_fileData == null || _rows.Count == 0)
                return snapshot;

            GetDisasmVisibleRowRange(out int startRow, out int endRow, padding: disasmPadding);
            snapshot.DisasmStartRow = startRow;
            snapshot.DisasmEndRow = endRow;

            if (startRow <= endRow)
            {
                long startOff64 = (long)_rows[startRow].Address - _baseAddr;
                long endOff64 = ((long)_rows[endRow].Address - _baseAddr) + 4L;
                int startOff = (int)Math.Max(0L, startOff64);
                int endOff = (int)Math.Min((long)_fileData.Length, endOff64);
                if (endOff > startOff)
                {
                    snapshot.DisasmReadStartOffset = startOff;
                    snapshot.DisasmReadLength = endOff - startOff;
                }
            }

            return snapshot;
        }

        private List<(int Start, int Length)> GetVisibleReadRanges(VisibleLiveSnapshot? snapshot = null, bool includeDisasmRange = true)
        {
            var ranges = new List<(int Start, int Length)>();
            if (_fileData == null) return ranges;
            snapshot ??= CaptureVisibleLiveSnapshot(disasmPadding: 2);

            void AddRange(int start, int length)
            {
                if (length <= 0) return;
                start = Math.Max(0, start);
                int end = Math.Min(_fileData.Length, start + length);
                if (end <= start) return;

                if (ranges.Count > 0)
                {
                    var last = ranges[^1];
                    int lastEnd = last.Start + last.Length;
                    if (start <= lastEnd + 64)
                    {
                        ranges[^1] = (last.Start, Math.Max(lastEnd, end) - last.Start);
                        return;
                    }
                }
                ranges.Add((start, end - start));
            }

            if (includeDisasmRange && _rows.Count > 0 && snapshot.DisasmStartRow <= snapshot.DisasmEndRow && snapshot.DisasmReadLength > 0)
                AddRange(snapshot.DisasmReadStartOffset, snapshot.DisasmReadLength);

            // Only include the hex range when the Memory View tab is visible
            bool memViewActive = _mainTabs == null || _mainTabs.SelectedIndex == 1;
            if (memViewActive)
                AddRange(snapshot.HexStartOffset, snapshot.HexLength);
            return ranges;
        }

        // Parse the PE export table of a loaded module in another process and
        // return the VA of the named exported symbol (data or function).
        private static IntPtr FindPeExport(IntPtr hProc, IntPtr moduleBase,
                                           string name, out bool is64, out string? errMsg)
        {
            is64 = true;

            // DOS header → e_lfanew
            var dos = new byte[64];
            if (!NativeMethods.ReadProcessMemory(hProc, moduleBase, dos, 64, out int r) || r < 64)
            { errMsg = "Cannot read PE DOS header."; return IntPtr.Zero; }

            int e_lfanew = BitConverter.ToInt32(dos, 60);

            // NT headers (signature + file header + start of optional header)
            var nt = new byte[0x200];
            if (!NativeMethods.ReadProcessMemory(hProc,
                    new IntPtr(moduleBase.ToInt64() + e_lfanew), nt, nt.Length, out r) || r < 0x100)
            { errMsg = "Cannot read PE NT headers."; return IntPtr.Zero; }

            if (nt[0] != 'P' || nt[1] != 'E')
            { errMsg = "Invalid PE signature."; return IntPtr.Zero; }

            // Optional header magic: 0x10B = PE32, 0x20B = PE32+
            ushort magic = BitConverter.ToUInt16(nt, 24);
            is64 = (magic == 0x20B);

            // DataDirectory[0] = Export Directory RVA/size
            // PE32 : optional header at +24, DataDirectory at +24+96  = 120
            // PE32+: optional header at +24, DataDirectory at +24+112 = 136
            int ddOff = is64 ? 136 : 120;
            uint expRVA  = BitConverter.ToUInt32(nt, ddOff);
            if (expRVA == 0)
            { errMsg = "PCSX2 module has no export directory — EEmem symbol unavailable."; return IntPtr.Zero; }

            // IMAGE_EXPORT_DIRECTORY (40 bytes)
            var ed = new byte[40];
            if (!NativeMethods.ReadProcessMemory(hProc,
                    new IntPtr(moduleBase.ToInt64() + expRVA), ed, 40, out r) || r < 40)
            { errMsg = "Cannot read export directory."; return IntPtr.Zero; }

            uint nNames      = BitConverter.ToUInt32(ed, 24);
            uint addrOfFuncs = BitConverter.ToUInt32(ed, 28);
            uint addrOfNames = BitConverter.ToUInt32(ed, 32);
            uint addrOfOrds  = BitConverter.ToUInt32(ed, 36);

            // Name pointer table
            var namePtrs = new byte[nNames * 4];
            if (!NativeMethods.ReadProcessMemory(hProc,
                    new IntPtr(moduleBase.ToInt64() + addrOfNames), namePtrs, namePtrs.Length, out r)
                || r < namePtrs.Length)
            { errMsg = "Cannot read export name table."; return IntPtr.Zero; }

            // Ordinal table
            var ords = new byte[nNames * 2];
            if (!NativeMethods.ReadProcessMemory(hProc,
                    new IntPtr(moduleBase.ToInt64() + addrOfOrds), ords, ords.Length, out r)
                || r < ords.Length)
            { errMsg = "Cannot read export ordinal table."; return IntPtr.Zero; }

            byte[] nameBytes = Encoding.ASCII.GetBytes(name);

            for (uint i = 0; i < nNames; i++)
            {
                uint nameRVA = BitConverter.ToUInt32(namePtrs, (int)(i * 4));
                var  nb      = new byte[nameBytes.Length + 1];
                if (!NativeMethods.ReadProcessMemory(hProc,
                        new IntPtr(moduleBase.ToInt64() + nameRVA), nb, nb.Length, out _))
                    continue;

                bool match = true;
                for (int j = 0; j < nameBytes.Length; j++)
                    if (nb[j] != nameBytes[j]) { match = false; break; }
                if (!match || nb[nameBytes.Length] != 0) continue;

                // Found — look up function RVA via ordinal
                ushort ord    = BitConverter.ToUInt16(ords, (int)(i * 2));
                var    fBuf   = new byte[4];
                if (!NativeMethods.ReadProcessMemory(hProc,
                        new IntPtr(moduleBase.ToInt64() + addrOfFuncs + ord * 4u), fBuf, 4, out _))
                { errMsg = "Cannot read export function RVA."; return IntPtr.Zero; }

                uint funcRVA = BitConverter.ToUInt32(fBuf, 0);
                errMsg = null;
                return new IntPtr(moduleBase.ToInt64() + funcRVA);
            }

            errMsg = $"Symbol '{name}' not found in PCSX2 export table.";
            return IntPtr.Zero;
        }

        private void SetStatusText(string text)
        {
            if (string.Equals(_lastStatusText, text, StringComparison.Ordinal))
                return;

            _lastStatusText = text;
            _sbProgress.Text = text;
        }

        private void UpdateMenuStatusLayout()
        {
            if (_menuBar == null || _menuStatusLabel == null || _menuStatusSpring == null)
                return;

            int reservedWidth = 420;
            int usedWidth = 0;
            foreach (ToolStripItem item in _menuBar.Items)
            {
                if (ReferenceEquals(item, _menuStatusSpring) || ReferenceEquals(item, _menuStatusLabel))
                    continue;
                usedWidth += item.Width + item.Margin.Horizontal;
            }

            int available = Math.Max(0, _menuBar.DisplayRectangle.Width - usedWidth - _menuStatusLabel.Margin.Horizontal - 8);
            int labelWidth = Math.Max(160, Math.Min(reservedWidth, available));
            int springWidth = Math.Max(0, available - labelWidth);
            _menuStatusSpring.Width = springWidth;
            _menuStatusLabel.Width = labelWidth;
        }

        private void ApplyThemeToWindowChrome(Form target, bool forceFrameRefresh = false)
        {
            if (target == null || target.IsDisposed || !target.IsHandleCreated)
                return;

            try
            {
                int darkVal = _currentTheme == AppTheme.Dark ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(target.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
            }
            catch { }

            try
            {
                int captionColor = ColorTranslator.ToWin32(_themeTitleBarBack);
                NativeMethods.DwmSetWindowAttribute(target.Handle, NativeMethods.DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            }
            catch { }

            try
            {
                int textColor = ColorTranslator.ToWin32(_themeTitleBarText);
                NativeMethods.DwmSetWindowAttribute(target.Handle, NativeMethods.DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            }
            catch { }

            try { NativeMethods.SendMessage(target.Handle, NativeMethods.WM_NCACTIVATE, (IntPtr)0, IntPtr.Zero); } catch { }
            try { NativeMethods.SendMessage(target.Handle, NativeMethods.WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero); } catch { }

            if (forceFrameRefresh)
            {
                try
                {
                    NativeMethods.SetWindowPos(target.Handle, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOACTIVATE);
                }
                catch { }
                try
                {
                    NativeMethods.RedrawWindow(target.Handle, IntPtr.Zero, IntPtr.Zero,
                        NativeMethods.RDW_FRAME | NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_ALLCHILDREN);
                }
                catch { }
                try { NativeMethods.UpdateWindow(target.Handle); } catch { }
                try { NativeMethods.DrawMenuBar(target.Handle); } catch { }
                try { NativeMethods.DwmFlush(); } catch { }
            }
        }

        private void RefreshTitleBarTheme(bool forceFrameRefresh = false)
        {
            ApplyThemeToWindowChrome(this, forceFrameRefresh);
        }

        private void SetActivityStatus(string text, int? percent = null)
        {
            if (IsDisposed)
                return;

            void Apply()
            {
                string safeText = string.IsNullOrWhiteSpace(text) ? "Ready" : text.Trim();
                if (_menuPauseStatusActive && !safeText.StartsWith("PAUSED:", StringComparison.Ordinal))
                    return;
                string newText = percent.HasValue
                    ? $"{safeText} {Math.Max(0, Math.Min(100, percent.Value))}%"
                    : safeText;

                if (string.Equals(_menuStatusLabel.Text, newText, StringComparison.Ordinal))
                {
                    if (safeText == "Ready")
                        _activityStatusResetTimer.Stop();
                    return;
                }

                _menuStatusLabel.Text = newText;
                UpdateMenuStatusLayout();
                _menuBar.Invalidate();

                if (safeText == "Ready" || percent.HasValue)
                {
                    _activityStatusResetTimer.Stop();
                }
                else
                {
                    _activityStatusResetTimer.Stop();
                    _activityStatusResetTimer.Start();
                }
            }

            if (InvokeRequired)
                BeginInvoke((Action)Apply);
            else
                Apply();
        }

        private void SetReadyStatus() => SetActivityStatus("Ready");

        private int GetConfiguredUiRefreshRate()
        {
            int refreshRate = _appSettings?.RefreshRate ?? AppSettings.DefaultRefreshRate;
            return AppSettings.IsSupportedRefreshRate(refreshRate) ? refreshRate : AppSettings.DefaultRefreshRate;
        }

        private int GetLiveRefreshIntervalMs()
        {
            int refreshRate = Math.Max(1, GetConfiguredUiRefreshRate());
            return Math.Max(1, (int)Math.Round(1000d / refreshRate, MidpointRounding.AwayFromZero));
        }

        private void UpdateLiveRefreshTimerInterval()
        {
            if (_liveTimer == null)
                return;

            _liveTimer.Interval = GetLiveRefreshIntervalMs();
        }

        private void UpdateConstantWriteTimerInterval()
        {
            if (_codeToolsDlg != null && !_codeToolsDlg.IsDisposed)
                _codeToolsDlg.SetConstantWriteRate(_appSettings.ConstantWriteRate);
        }

        private void StartLiveMode()
        {
            if (_liveTimer == null)
            {
                _liveTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, DefaultLiveRefreshIntervalMs) };
                UpdateLiveRefreshTimerInterval();
                _liveTimer.Tick += (_, _) => OnLiveTick();
            }
            UpdateLiveRefreshTimerInterval();
            _liveTimer.Start();
            _sbProgress.Text = "Live \u25CF";
        }

        private void StopLiveMode()
        {
            _liveTimer?.Stop();
        }

        private void OnLiveTick()
        {
            if (_liveReading || _fileData == null)
                return;
            if (!Visible || WindowState == FormWindowState.Minimized)
                return;

            int activeTab = _mainTabs?.SelectedIndex ?? 0;
            // Only refresh for Disassembler (0) or Memory View (1)
            if (activeTab != 0 && activeTab != 1)
                return;

            RefreshDebuggerUiTick();

            _liveReading = true;
            try
            {

                if (activeTab == 1)
                {
                    // Memory View: read visible hex bytes via PINE, compare per-row,
                    // invalidate a single bounding rect of changed rows, then Update()
                    // synchronously so the paint completes in one pass (no flicker).
                    if (_fileData == null || !EnsurePineConnected())
                        return;
                    int topHexRow = _hexList.TopIndex;
                    int startOffset = topHexRow * 16;
                    int visibleBytes = Math.Max(16, _hexRowCount * 16);
                    if (startOffset < 0 || startOffset + visibleBytes > _fileData.Length)
                        return;
                    try
                    {
                        byte[] data = _pine.ReadMemory(OffsetToPineAddress(startOffset), visibleBytes);
                        if (data == null || data.Length <= 0)
                            return;
                        int copyLen = Math.Min(data.Length, _fileData.Length - startOffset);
                        if (IsSuspiciousAllZeroPineRead(data, _fileData, startOffset, copyLen))
                            return;

                        // Find first and last changed rows
                        int rowCount = (copyLen + 15) / 16;
                        int firstDirty = -1, lastDirty = -1;
                        for (int r = 0; r < rowCount; r++)
                        {
                            int rStart = r * 16;
                            int rEnd = Math.Min(rStart + 16, copyLen);
                            for (int i = rStart; i < rEnd; i++)
                            {
                                if (_fileData[startOffset + i] != data[i])
                                {
                                    if (firstDirty < 0) firstDirty = r;
                                    lastDirty = r;
                                    break;
                                }
                            }
                        }

                        if (firstDirty >= 0)
                        {
                            Buffer.BlockCopy(data, 0, _fileData, startOffset, copyLen);
                            // Compute a single bounding rectangle spanning all dirty rows
                            try
                            {
                                var topRect = _hexList.GetItemRect(topHexRow + firstDirty);
                                var botRect = _hexList.GetItemRect(topHexRow + lastDirty);
                                var dirtyRect = Rectangle.Union(topRect, botRect);
                                _hexList.Invalidate(dirtyRect, false);
                                _hexList.Update(); // paint synchronously — no intermediate erase visible
                            }
                            catch { _hexList.Invalidate(); }
                        }
                    }
                    catch { /* non-fatal */ }
                    return;
                }

                bool updated = TryUpdateVisibleDisassemblerRowsViaPine();
                if (!updated)
                {
                    if (!_pineAvailable && !_pine.IsConnected)
                    {
                        SetStatusText("Live: waiting for PINE");
                        return;
                    }

                    if (_rows.Count == 0)
                    {
                        SetStatusText("Live: waiting for disassembly");
                        return;
                    }

                    SetStatusText("Live: visible-row refresh idle");
                    return;
                }

                // Keep disassembly annotations and the ASCII byte bar live even when the breakpoint
                // sidebar is closed. Annotation suffixes can dereference addresses outside the visible
                // instruction-word window, so they must repaint every live tick whenever the
                // Disassembler tab is active.
                _disasmList?.Invalidate();
                _asciiBytesBar?.Invalidate();

                bool frozen = _breakpointUiFrozen && _breakpointUiFrozenAddress.HasValue;
                SetStatusText(frozen
                    ? $"Live ● PAUSED  {DateTime.Now:HH:mm:ss}  rows Δ{Math.Max(0, _lastVisibleDisasmChangeCount)}"
                    : $"Live ●  {DateTime.Now:HH:mm:ss}  rows Δ{Math.Max(0, _lastVisibleDisasmChangeCount)}");
            }
            catch (Exception ex)
            {
                LogPine($"Live tick failed: {ex.Message}");
                SetStatusText("Live: tick failed");
            }
            finally
            {
                _liveReading = false;
            }
        }

        private void GetDisasmVisibleRowRange(out int start, out int end, int padding = 0)
        {
            start = 0;
            end = -1;
            if (_rows.Count == 0)
                return;

            int top = _disasmList.TopIndex >= 0 ? _disasmList.TopIndex : (_selRow >= 0 ? _selRow : 0);
            top = Math.Max(0, Math.Min(top, _rows.Count - 1));

            int vis = _disasmList.VisibleRowCapacity;
            if (vis <= 0)
            {
                int rowH = _disasmList.RowHeight > 0 ? _disasmList.RowHeight : 1;
                vis = Math.Max(1, (_disasmList.ClientSize.Height - _disasmList.HeaderHeight) / rowH);
            }

            // Include a partially visible trailing row so every on-screen row is refreshed.
            vis = Math.Max(1, vis + 1);

            start = Math.Max(0, top - padding);
            end = Math.Min(_rows.Count - 1, top + vis - 1 + padding);
        }

        private bool TryGetVisibleDisasmReadWindow(out int startRow, out int endRow, out int startOffset, out int readLength)
        {
            startRow = 0;
            endRow = -1;
            startOffset = 0;
            readLength = 0;

            if (_fileData == null || _rows.Count == 0)
                return false;

            GetDisasmVisibleRowRange(out startRow, out endRow, padding: 0);
            if (startRow < 0 || endRow < startRow || startRow >= _rows.Count)
                return false;

            endRow = Math.Min(endRow, _rows.Count - 1);

            long firstOffset64 = ((long)_rows[startRow].Address & ~3L) - _baseAddr;
            long lastOffset64 = (((long)_rows[endRow].Address & ~3L) - _baseAddr) + 4L;

            if (lastOffset64 <= 0 || firstOffset64 >= _fileData.Length)
                return false;

            startOffset = (int)Math.Max(0L, firstOffset64);
            int endOffset = (int)Math.Min((long)_fileData.Length, lastOffset64);
            readLength = endOffset - startOffset;

            if (readLength <= 0)
                return false;

            if (startOffset < PreservedKernelWindowLength)
            {
                int safeStart = Math.Max(startOffset, PreservedKernelWindowLength);
                int safeEnd = Math.Max(safeStart, endOffset);
                startOffset = safeStart;
                readLength = safeEnd - safeStart;
                if (readLength <= 0)
                    return false;
            }

            return true;
        }

        private void InstallEeRam(byte[] eeMem)
        {
            RestorePreservedKernelWindow(eeMem);
            _fileData   = eeMem;
            _fileName   = "PCSX2 EE RAM";
            _elfInfo    = null;
            _baseAddr   = 0x00000000;
            _disasmBase = 0x00000000;
            _disasmLen  = (uint)eeMem.Length;

            _hexRowCount = 1;
            _hexViewOffset = 0;
            _hexList.TopIndex = 0;
            _hexList.VirtualListSize = Math.Max(1, ((_fileData?.Length ?? 0) + 15) / 16);
            AdjustHexSplitter();
            UpdateHexScrollBar();

            _miSave.Enabled = true;
            Text         = "ps2dis# \u2014 PCSX2 LIVE";
            _sbInfo.Text = $"PCSX2 EE RAM    {eeMem.Length:N0} bytes    Base: 0x00000000";
            _sbSize.Text = $"{eeMem.Length / (1024.0 * 1024.0):F1} MB";

            CaptureKernelWindowForPreservation();
            CancelXrefAnalysis();
            ScanStringLabels();
            ClearXrefResults();
            QueueXrefAnalyzerAfterDisassembly(quiet: true, extendedCleanup: true);
            StartDisassembly();
            StartLiveMode();
        }

        // ══════════════════════════════════════════════════════════════════
        // Disassembly (runs on background thread, updates UI progressively)
        // ══════════════════════════════════════════════════════════════════

        private void StartDisassembly()
        {
            if (_fileData == null) return;

            // Cancel any in-progress run
            _disCts?.Cancel();
            _disCts?.Dispose();
            var runCts = new CancellationTokenSource();
            _disCts = runCts;
            _disassemblyRunning = true;
            var token = runCts.Token;

            // Clear immediately
            _autoLabels = new Dictionary<uint, string>();
            _rows = new List<SlimRow>();
            _selRow = -1;
            _disasmList.VirtualListSize = 0;
            _sbProgress.Text = "Disassembling…";

            // Capture state for background thread — only disassemble the active region.
            // Avoid materializing a second full row list with decoded strings; that balloons RAM.
            byte[] data = _fileData;
            uint baseAddr = _baseAddr;
            uint startOff = _disasmBase > baseAddr ? (_disasmBase - baseAddr) : 0u;
            startOff &= ~3u;
            uint requestedLen = _disasmLen == 0 ? (uint)data.Length : _disasmLen;
            uint maxLen = (uint)Math.Max(0, data.Length - (int)Math.Min(startOff, (uint)data.Length));
            uint endOff = Math.Min((uint)data.Length & ~3u, (startOff + requestedLen) & ~3u);
            if (endOff <= startOff)
                endOff = Math.Min((uint)data.Length & ~3u, (startOff + Math.Min(maxLen, 0x00200000u)) & ~3u);
            bool useAbi = _useAbi;

            var userLabelsSnapshot = _userLabels.Count == 0 ? new Dictionary<uint, string>() : new Dictionary<uint, string>(_userLabels);
            IReadOnlyDictionary<uint, string> stringLabelsSnapshot = _stringLabels;
            var stringRangesSnapshot = BuildStringRanges(data, baseAddr, startOff, endOff, stringLabelsSnapshot);

            Task.Run(() =>
            {
                int wordCount = (int)Math.Max(0u, (endOff - startOff) >> 2);
                var instrRows = new List<SlimRow>(wordCount);
                var autoLabels = new Dictionary<uint, string>();
                var opts = new DisassemblerOptions { BaseAddress = baseAddr, UseAbiNames = useAbi };
                var eng = new MipsDisassemblerEx(opts);

                // Use DecodeKindAndTarget instead of DisassembleSingleWord:
                // the background scan only needs Kind+Target (for auto-label generation).
                // Full mnemonic/operands strings are computed on demand at display time.
                // This eliminates ~1 GB of transient GC allocations for a 32 MB EE RAM image
                // (was: 8M × new Dictionary + 8M × format strings ≈ 2 GB peak heap).
                uint address = baseAddr + startOff;
                for (uint off = startOff; off + 3 < endOff; off += 4, address += 4)
                {
                    if (token.IsCancellationRequested) return;

                    uint word = BitConverter.ToUInt32(data, (int)off);
                    var (kind, target) = eng.DecodeKindAndTarget(word, address);

                    instrRows.Add(CreateInitialRowForKind(word, address, kind, target));
                }

                instrRows = ExpandStringLabeledByteRows(instrRows, data, baseAddr, startOff, endOff, stringRangesSnapshot);
                instrRows = ExpandLabelAlignedRows(instrRows, data, baseAddr, startOff, endOff, userLabelsSnapshot, stringLabelsSnapshot);
                ReclassifyIsolatedDataRowsAsInstructions(instrRows);

                foreach (var row in instrRows)
                {
                    if (row.Target == 0)
                        continue;

                    uint target = NormalizeFollowAddress(row.Target);
                    if ((target & 3u) != 0)
                        continue;
                    if (target < baseAddr || target >= baseAddr + endOff)
                        continue;
                    if (userLabelsSnapshot.ContainsKey(target) || stringLabelsSnapshot.ContainsKey(target) || autoLabels.ContainsKey(target))
                        continue;

                    if (row.Kind == InstructionType.Call)
                        autoLabels[target] = $"FUNC_{target:X8}";
                }

                if (token.IsCancellationRequested)
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (ReferenceEquals(_disCts, runCts))
                        {
                            _disassemblyRunning = false;
                            _disCts?.Dispose();
                            _disCts = null;
                            StartQueuedXrefAnalyzerIfReady();
                        }
                    }));
                    return;
                }

                BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (ReferenceEquals(_disCts, runCts))
                    {
                        _disassemblyRunning = false;
                        _disCts?.Dispose();
                        _disCts = null;
                    }
                    _autoLabels = autoLabels;
                    _rows = instrRows;
                    int rowCount = _rows.Count;
                    _rows.TrimExcess();
                    _cachedDisasm = null; // force rebuild with correct base address
                    ReloadRows();
                    _sbProgress.Text = $"{rowCount:N0} rows";

                    // Break the completion closure's references to large temporary objects
                    // before we queue the next-tick memory trim.
                    instrRows = null!;
                    autoLabels = null!;
                    QueueManagedMemoryTrimBurst();
                    StartQueuedXrefAnalyzerIfReady();
                });
            }, token);
        }

        /// <summary>
        /// Post-pass that looks for isolated .word data rows sandwiched within
        /// what appears to be a sequence of valid instructions (i.e. inside a
        /// function body) and promotes them back to InstructionType.Alu so they
        /// render with the normal instruction colors.
        ///
        /// Also collapses 4 consecutive aligned .byte rows back into a single
        /// instruction row when the surrounding context is clearly code.
        /// </summary>
        private static void ReclassifyIsolatedDataRowsAsInstructions(List<SlimRow> rows)
        {
            if (rows.Count < 3)
                return;

            const int windowRadius = 6;

            // Helper: is this row a "real instruction"? (not data, not a padding nop of zero)
            static bool IsRealInstruction(SlimRow o)
            {
                if (o.Kind == InstructionType.Data) return false;
                if (o.Kind == InstructionType.Nop && o.Word == 0) return false;
                return true;
            }

            // PASS 1: collapse runs of 4 aligned bytes back to a word when surrounded by code.
            // We build a new list instead of calling List.RemoveRange repeatedly — the latter
            // is O(n) per call, which on an 8M-row dump becomes O(n²) ≈ minutes of CPU.
            bool anyBytesCollapsed = false;
            for (int i = 0; i + 3 < rows.Count; i++)
            {
                var r = rows[i];
                if (r.DataSub == DataKind.Byte && (r.Address & 3u) == 0 &&
                    rows[i + 1].DataSub == DataKind.Byte && rows[i + 1].Address == r.Address + 1 &&
                    rows[i + 2].DataSub == DataKind.Byte && rows[i + 2].Address == r.Address + 2 &&
                    rows[i + 3].DataSub == DataKind.Byte && rows[i + 3].Address == r.Address + 3)
                {
                    anyBytesCollapsed = true;
                    break;
                }
            }

            if (anyBytesCollapsed)
            {
                var rebuilt = new List<SlimRow>(rows.Count);
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    if (r.DataSub == DataKind.Byte && (r.Address & 3u) == 0 &&
                        i + 3 < rows.Count &&
                        rows[i + 1].DataSub == DataKind.Byte && rows[i + 1].Address == r.Address + 1 &&
                        rows[i + 2].DataSub == DataKind.Byte && rows[i + 2].Address == r.Address + 2 &&
                        rows[i + 3].DataSub == DataKind.Byte && rows[i + 3].Address == r.Address + 3)
                    {
                        // Count real instructions in surrounding window
                        int instrBefore = 0, instrAfter = 0;
                        int windowStart = Math.Max(0, i - windowRadius);
                        for (int j = windowStart; j < i; j++)
                            if (IsRealInstruction(rows[j])) instrBefore++;
                        int windowEnd = Math.Min(rows.Count, i + 4 + windowRadius);
                        for (int j = i + 4; j < windowEnd; j++)
                            if (IsRealInstruction(rows[j])) instrAfter++;

                        if (instrBefore >= 2 && instrAfter >= 2)
                        {
                            // Collapse 4 bytes into one word row classified as Alu
                            uint b0 = rows[i].Word & 0xFFu;
                            uint b1 = rows[i + 1].Word & 0xFFu;
                            uint b2 = rows[i + 2].Word & 0xFFu;
                            uint b3 = rows[i + 3].Word & 0xFFu;
                            uint word = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);

                            rebuilt.Add(new SlimRow
                            {
                                Address = r.Address,
                                Word = word,
                                Kind = InstructionType.Alu,
                                DataSub = DataKind.None,
                                Target = 0,
                            });
                            i += 3; // skip the next 3 bytes
                            continue;
                        }
                    }
                    rebuilt.Add(r);
                }

                rows.Clear();
                rows.AddRange(rebuilt);
            }

            // PASS 2: promote isolated .word data rows to instruction rows when
            // surrounded by real instructions.
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Kind != InstructionType.Data || r.DataSub != DataKind.Word)
                    continue;

                int instrCount = 0;
                int dataCount = 0;
                int start = Math.Max(0, i - windowRadius);
                int end = Math.Min(rows.Count - 1, i + windowRadius);

                for (int j = start; j <= end; j++)
                {
                    if (j == i) continue;
                    var o = rows[j];
                    if (o.Kind == InstructionType.Data)
                        dataCount++;
                    else if (IsRealInstruction(o))
                        instrCount++;
                }

                if (instrCount >= 4 && instrCount > dataCount)
                {
                    bool hasInstrBefore = false, hasInstrAfter = false;
                    for (int j = Math.Max(0, i - 2); j < i; j++)
                        if (IsRealInstruction(rows[j])) hasInstrBefore = true;
                    for (int j = i + 1; j <= Math.Min(rows.Count - 1, i + 2); j++)
                        if (IsRealInstruction(rows[j])) hasInstrAfter = true;

                    if (hasInstrBefore && hasInstrAfter)
                    {
                        rows[i] = new SlimRow
                        {
                            Address = r.Address,
                            Word = r.Word,
                            Kind = InstructionType.Alu,
                            DataSub = DataKind.None,
                            Target = r.Target,
                        };
                    }
                }
            }
        }

        private SlimRow CreateInitialRowForWord(uint word, uint address, DisassemblyRow decoded)
        {
            if (decoded.Kind != InstructionType.Data)
                return new SlimRow { Address = address, Word = word, Kind = decoded.Kind, Target = decoded.Target };
            if (LooksLikeFloatWord(word))
                return SlimRow.DataRow(address, word, DataKind.Float);
            return SlimRow.DataRow(address, word, DataKind.Word);
        }

        /// <summary>Zero-allocation variant — takes pre-computed Kind/Target from DecodeKindAndTarget.</summary>
        private SlimRow CreateInitialRowForKind(uint word, uint address, InstructionType kind, uint target)
        {
            if (kind != InstructionType.Data)
                return new SlimRow { Address = address, Word = word, Kind = kind, Target = target };
            if (LooksLikeFloatWord(word))
                return SlimRow.DataRow(address, word, DataKind.Float);
            return SlimRow.DataRow(address, word, DataKind.Word);
        }

        private static bool IsPrintableAsciiByte(byte b)
            => b >= 0x20 && b <= 0x7E;


        private static string FormatByteValue(byte value)
        {
            char ch = value switch
            {
                >= 0x20 and <= 0x7E => (char)value,
                0x00 => '\0',
                _ => '·'
            };

            return ch.ToString();
        }

        private static bool IsWholeWordByteRow(SlimRow row)
            => row.DataSub == DataKind.Byte && (row.Address & 3u) == 0;

        private static string FormatByteRowValue(uint word)
        {
            var parts = new List<string>(4);
            for (int i = 0; i < 4; i++)
            {
                byte value = (byte)(word >> (i * 8));
                if (value == 0x00)
                    break;
                parts.Add(FormatByteValue(value));
            }

            return parts.Count == 0 ? FormatByteValue(0x00) : string.Join(" ", parts);
        }


        private static Dictionary<uint, int> BuildStringRanges(byte[] data, uint baseAddr, uint startOff, uint endOff, IReadOnlyDictionary<uint, string> stringLabels)
        {
            var ranges = new Dictionary<uint, int>();
            if (data.Length == 0 || stringLabels.Count == 0)
                return ranges;

            int rangeStart = (int)Math.Min(startOff, (uint)data.Length);
            int rangeEnd = (int)Math.Min(endOff, (uint)data.Length);
            if (rangeEnd <= rangeStart)
                return ranges;

            foreach (var addr in stringLabels.Keys)
            {
                long off = (long)addr - baseAddr;
                if (off < rangeStart || off >= rangeEnd)
                    continue;

                int strStart = (int)off;
                int len = 0;
                while (strStart + len < rangeEnd)
                {
                    byte b = data[strStart + len];
                    if (b == 0x00)
                        break;
                    if (!IsPrintableAsciiByte(b))
                    {
                        len = 0;
                        break;
                    }
                    len++;
                }

                if (len > 0 && strStart + len < rangeEnd && data[strStart + len] == 0x00)
                    ranges[addr] = len;
            }

            return ranges;
        }

        private static List<SlimRow> ExpandStringLabeledByteRows(List<SlimRow> rows, byte[] data, uint baseAddr, uint startOff, uint endOff,
                                                                         IReadOnlyDictionary<uint, int> stringRanges)
        {
            if (rows.Count == 0 || data.Length == 0 || stringRanges.Count == 0)
                return rows;

            int start = (int)Math.Min(startOff, (uint)data.Length);
            int end = (int)Math.Min(endOff, (uint)data.Length);
            bool needsExpansion = false;
            foreach (var row in rows)
            {
                if (row.Kind == InstructionType.Data && stringRanges.TryGetValue(row.Address, out int stringLen) && stringLen > 0)
                {
                    int rowOff = (int)(row.Address - baseAddr);
                    if (rowOff >= start && rowOff < end)
                    {
                        needsExpansion = true;
                        break;
                    }
                }
            }
            if (!needsExpansion)
                return rows;

            var expanded = new List<SlimRow>(rows.Count);

            int rowIdx = 0;
            while (rowIdx < rows.Count)
            {
                var row = rows[rowIdx];
                if (!stringRanges.TryGetValue(row.Address, out int stringLen))
                {
                    expanded.Add(row);
                    rowIdx++;
                    continue;
                }

                // Never convert instruction rows to bytes — only data rows should be expanded.
                if (row.Kind != InstructionType.Data)
                {
                    expanded.Add(row);
                    rowIdx++;
                    continue;
                }

                int rowOff = (int)(row.Address - baseAddr);
                if (rowOff < start || rowOff >= end || stringLen <= 0)
                {
                    expanded.Add(row);
                    rowIdx++;
                    continue;
                }

                int endByteExclusive = Math.Min(end, rowOff + stringLen);
                for (int byteOff = rowOff; byteOff < endByteExclusive; byteOff++)
                {
                    byte value = data[byteOff];
                    expanded.Add(SlimRow.DataRow(baseAddr + (uint)byteOff, value, DataKind.Byte));
                }

                int consumeUntil = ((endByteExclusive + 3) & ~3);
                if (consumeUntil <= rowOff)
                    consumeUntil = rowOff + 4;

                while (rowIdx < rows.Count && (int)(rows[rowIdx].Address - baseAddr) < consumeUntil)
                    rowIdx++;
            }

            return expanded;
        }

        private static List<SlimRow> ExpandWholeWordByteRows(List<SlimRow> rows, byte[] data, uint baseAddr, uint startOff, uint endOff)
        {
            if (rows.Count == 0 || data.Length == 0)
                return rows;

            var expanded = new List<SlimRow>(rows.Count);
            foreach (var row in rows)
            {
                if (!IsWholeWordByteRow(row))
                {
                    expanded.Add(row);
                    continue;
                }

                uint aligned = row.Address & ~3u;
                long off = (long)(aligned - baseAddr);
                if (off < startOff || off < 0 || off + 4 > endOff || off + 4 > data.Length)
                {
                    expanded.Add(row);
                    continue;
                }

                for (int i = 0; i < 4; i++)
                {
                    byte value = data[(int)off + i];
                    expanded.Add(SlimRow.DataRow(aligned + (uint)i, value, DataKind.Byte, row.Target));
                }
            }

            return expanded;
        }

        private static bool LooksLikeFloatWord(uint word)
        {
            float value = BitConverter.Int32BitsToSingle(unchecked((int)word));
            return LooksLikeUsefulFloat(value);
        }

        private static bool LooksLikeUsefulFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            float abs = Math.Abs(value);
            return abs >= 0.0001f && abs <= 1000000f;
        }

        /// <summary>
        /// Splits word/halfword/instruction rows when a label (user or string) falls on
        /// a non-aligned byte within that row's 4-byte span.  Chooses the smallest
        /// granularity that exposes all labels: halfwords if possible, otherwise bytes.
        /// </summary>
        private static List<SlimRow> ExpandLabelAlignedRows(
            List<SlimRow> rows, byte[] data, uint baseAddr, uint startOff, uint endOff,
            IReadOnlyDictionary<uint, string> userLabels,
            IReadOnlyDictionary<uint, string> stringLabels)
        {
            if (rows.Count == 0) return rows;

            bool HasLabelAt(uint address)
                => userLabels.ContainsKey(address) || stringLabels.ContainsKey(address);

            if (userLabels.Count == 0 && stringLabels.Count == 0)
                return rows;

            bool needsExpansion = false;
            foreach (var row in rows)
            {
                if (row.DataSub == DataKind.Byte || row.DataSub == DataKind.Half)
                    continue;

                uint aligned = row.Address & ~3u;
                long off = (long)(aligned - baseAddr);
                if (off < startOff || off < 0 || off + 4 > endOff || off + 4 > data.Length)
                    continue;

                if (HasLabelAt(aligned + 1) || HasLabelAt(aligned + 2) || HasLabelAt(aligned + 3))
                {
                    needsExpansion = true;
                    break;
                }
            }
            if (!needsExpansion)
                return rows;

            var expanded = new List<SlimRow>(rows.Count);
            foreach (var row in rows)
            {
                // Only consider word-aligned rows that cover 4 bytes
                if (row.DataSub == DataKind.Byte || row.DataSub == DataKind.Half)
                {
                    expanded.Add(row);
                    continue;
                }

                uint aligned = row.Address & ~3u;
                long off = (long)(aligned - baseAddr);
                if (off < startOff || off < 0 || off + 4 > endOff || off + 4 > data.Length)
                {
                    expanded.Add(row);
                    continue;
                }

                // Check if any label falls on bytes 1, 2, or 3 within this word
                bool hasB1 = HasLabelAt(aligned + 1);
                bool hasB2 = HasLabelAt(aligned + 2);
                bool hasB3 = HasLabelAt(aligned + 3);

                if (!hasB1 && !hasB2 && !hasB3)
                {
                    expanded.Add(row);
                    continue;
                }

                // Decide granularity: if labels only on halfword boundaries (byte 2), use halfwords
                bool needBytes = hasB1 || hasB3;
                if (!needBytes)
                {
                    // Split into two halfwords
                    uint word = BitConverter.ToUInt32(data, (int)off);
                    expanded.Add(SlimRow.DataRow(aligned, (ushort)(word & 0xFFFF), DataKind.Half, row.Target));
                    expanded.Add(SlimRow.DataRow(aligned + 2, (ushort)(word >> 16), DataKind.Half, row.Target));
                }
                else
                {
                    // Split into 4 individual bytes
                    for (int i = 0; i < 4; i++)
                    {
                        byte val = data[(int)off + i];
                        expanded.Add(SlimRow.DataRow(aligned + (uint)i, val, DataKind.Byte, row.Target));
                    }
                }
            }

            return expanded;
        }

        private void ReloadRows()
        {
            _selRow = -1;
            _jumpSkipStart = -1;
            _jumpSkipEnd   = -1;
            if (_rows.Count <= AddrIndexMaxRows)
            {
                _addrToRow = new Dictionary<uint, int>(_rows.Count);
                for (int i = 0; i < _rows.Count; i++)
                    _addrToRow.TryAdd(_rows[i].Address, i);
                _addrIndexDirty = false;
            }
            else
            {
                _addrToRow = null;
                _addrIndexDirty = true;
            }
            _disasmList.VirtualListSize = 0;
            _disasmList.VirtualListSize = _rows.Count;
            _disasmList.Invalidate();

            // Navigate to pending address (e.g. from GotoNextXref crossing a window boundary)
            if (_pendingNavAddr != 0)
            {
                uint target = _pendingNavAddr;
                int visibleOffset = _pendingNavVisibleOffset;
                bool center = _pendingNavCenter;
                _pendingNavAddr = 0;
                _pendingNavVisibleOffset = 0;
                _pendingNavCenter = false;
                int navIdx = -1;
                if (!TryGetRowIndexByAddress(target, out navIdx))
                    navIdx = FindNearestRow(target);

                if (navIdx >= 0 && navIdx < _rows.Count)
                {
                    SelectRow(navIdx, center: center);
                    if (!center)
                        ApplyVisibleRowOffset(navIdx, visibleOffset);
                }
                else if (_rows.Count > 0) SelectRow(0);
            }
            else if (_rows.Count > 0) SelectRow(0);
        }


        private void InitializeHexPanelForFourRows()
        {
            // Hex list now fills its own tab; just ensure row count is sane on startup.
            AdjustHexSplitter();
        }

        // Recompute how many hex rows fit in the Memory View tab and update the virtual list.
        private void AdjustHexSplitter()
        {
            if (WindowState == FormWindowState.Minimized)
                return;

            _hexRowCount = Math.Max(1, _hexList.VisibleRowCapacity);
            int totalRows = _fileData != null ? Math.Max(1, (_fileData.Length + 15) / 16) : 1;
            if (_hexList.VirtualListSize != totalRows)
                _hexList.VirtualListSize = totalRows;

            int maxTop = Math.Max(0, totalRows - _hexRowCount);
            if (_hexList.TopIndex > maxTop)
                _hexList.TopIndex = maxTop;

            UpdateHexScrollBar();
        }

        private void UpdateHexScrollBar()
        {
            _hexViewOffset = _hexList.TopIndex * 16;
        }


    }
}
