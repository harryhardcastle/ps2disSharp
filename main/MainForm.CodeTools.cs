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
// ── Code Manager ──────────────────────────────────────────────────

        private CodeToolsDialog? _codeToolsDlg;

        private CodeToolsDialog EnsureCodeToolsDialog()
        {
            if (_codeToolsDlg == null || _codeToolsDlg.IsDisposed)
            {
                _codeToolsDlg = new CodeToolsDialog(
                    read: (addr, count) =>
                    {
                        if (TryReadEeMemory(addr, count, out byte[] liveData) && liveData.Length >= count)
                            return liveData;

                        if (_fileData == null) return null;
                        long off = (long)addr - _baseAddr;
                        if (off < 0 || off + count > _fileData.Length) return null;
                        var buf = new byte[count];
                        Array.Copy(_fileData, off, buf, 0, count);
                        return buf;
                    },
                    applyLocal:  PatchFileDataAndRows,
                    writePcsx2:  patches => WritePatchesToLivePcsx2(patches),
                    readEeRam: () =>
                    {
                        var validNames = new[] { "pcsx2", "pcsx2-qt" };

                        var proc = Process.GetProcesses()
                            .FirstOrDefault(p =>
                                validNames.Any(name =>
                                    string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)));
                        if (proc == null) return (null, "PCSX2 process not found.");

                        uint access = NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_INFO;
                        IntPtr hProc = NativeMethods.OpenProcess(access, false, (uint)proc.Id);
                        if (hProc == IntPtr.Zero) return (null, "Cannot open PCSX2 process.");

                        try
                        {
                            var ram = ReadEeRamViaSymbol(hProc, out string? err);
                            return (ram, err);
                        }
                        finally { NativeMethods.CloseHandle(hProc); }
                    },
                    navigateToCenter: addr =>
                    {
                        // Switch to Disassembler tab and center the address
                        if (_mainTabs != null && _mainTabs.Pages.Count > 0)
                            _mainTabs.SelectedIndex = 0;
                        if (TryGetRowIndexByAddress(addr, out int idx))
                            SelectRow(idx, center: true);
                        else
                        {
                            int ni = FindNearestRow(addr);
                            if (ni >= 0) SelectRow(ni, center: true);
                        }
                    });

                // Embed the CodeToolsDialog's custom tab host into the Code Manager tab panel
                // instead of showing it as a floating window.
                var codePanel = FindCodeManagerPanel();
                if (codePanel != null)
                {
                    var innerTabs = _codeToolsDlg.GetInnerTabHost();
                    if (innerTabs != null && innerTabs.Parent != codePanel)
                    {
                        codePanel.SuspendLayout();
                        innerTabs.Visible = false;
                        try
                        {
                            innerTabs.Dock = DockStyle.Fill;
                            innerTabs.Margin = new Padding(0);
                            innerTabs.BackColor = _themeCodeManagerBack;
                            codePanel.Padding = new Padding(0);
                            codePanel.BackColor = _themeCodeManagerBack;
                            codePanel.Controls.Add(innerTabs);
                            ApplyThemeToControlTree(codePanel);
                            ApplyScrollbarTheme(codePanel, _currentTheme == AppTheme.Dark);
                            _codeToolsDlg.ApplyEditorScrollbarTheme(_currentTheme == AppTheme.Dark);
                        }
                        finally
                        {
                            innerTabs.Visible = true;
                            codePanel.ResumeLayout(true);
                        }
                    }
                }
            }
            _codeToolsDlg.SetConstantWriteRate(_appSettings.ConstantWriteRate);
            return _codeToolsDlg;
        }

        private Panel? FindCodeManagerPanel()
        {
            if (_mainTabs == null || _mainTabs.Pages.Count < 3) return null;
            var tp = _mainTabs.Pages[2];
            return tp.Controls.Count > 0 ? tp.Controls[0] as Panel : null;
        }

        private void PatchFileDataAndRows(IReadOnlyList<(uint Addr, byte[] Bytes)> patches)
        {
            if (_fileData == null || patches.Count == 0) return;

            var eng = GetCachedDisasm();

            int firstInvalid = int.MaxValue;
            int lastInvalid  = int.MinValue;

            foreach (var (addr, bytes) in patches)
            {
                long off = (long)addr - _baseAddr;
                if (off < 0 || off + bytes.Length > _fileData.Length) continue;

                // Patch _fileData
                Array.Copy(bytes, 0, _fileData, (int)off, bytes.Length);

                // Re-disassemble the containing aligned word
                uint alignedAddr = addr & ~3u;
                if (!TryGetRowIndexByAddress(alignedAddr, out int rowIdx)) continue;

                long wordOff = (long)alignedAddr - _baseAddr;
                if (wordOff < 0 || wordOff + 4 > _fileData.Length) continue;

                uint newWord = BitConverter.ToUInt32(_fileData, (int)wordOff);
                _rows[rowIdx] = SlimRowFromWord(newWord, alignedAddr);

                if (rowIdx < firstInvalid) firstInvalid = rowIdx;
                if (rowIdx > lastInvalid)  lastInvalid  = rowIdx;
            }

            if (firstInvalid <= lastInvalid)
                _disasmList.RedrawItems(firstInvalid, lastInvalid, true);
        }

        private string? WritePatchesToLivePcsx2(IReadOnlyList<(uint Addr, byte[] Bytes)> patches, int repeatWrites = 1)
        {
            if (patches == null || patches.Count == 0)
                return null;

            var normalized = NormalizePatches(patches);
            if (normalized.Count == 0)
                return null;

            const int PauseBatchThreshold = 64;
            const int DirectFallbackThreshold = 256;

            bool resumeAfterPatch = false;
            bool pausedForPatch = false;
            string? pauseErr = null;

            try
            {
                if (normalized.Count >= PauseBatchThreshold)
                {
                    pausedForPatch = TryPauseVmForBulkPatch(out resumeAfterPatch, out pauseErr);
                    if (!pausedForPatch && !string.IsNullOrWhiteSpace(pauseErr))
                        LogPine($"Bulk patch pause skipped: {pauseErr}");
                }

                string? pineErr = TryWritePatchesViaPine(normalized);
                if (pineErr == null)
                    return null;

                bool allowDirectFallback = normalized.Count < DirectFallbackThreshold;
                if (!allowDirectFallback)
                {
                    LogPine($"PINE bulk write failed and direct fallback was skipped for safety ({normalized.Count} normalized patch(es)): {pineErr}");
                    return pineErr;
                }

                LogPine($"Falling back to process memory writes: {pineErr}");

                if (_liveProcId != 0 && _eeHostAddr != 0)
                    return WritePatchesToPcsx2(_liveProcId, _eeHostAddr, normalized, repeatWrites);

                return WritePatchesToPcsx2(normalized, repeatWrites);
            }
            finally
            {
                RestoreVmAfterBulkPatch(resumeAfterPatch);
            }
        }

        private bool TryPauseVmForBulkPatch(out bool resumeAfter, out string? error)
        {
            resumeAfter = false;
            error = null;

            try
            {
                if (!EnsureDebugServerConnected(forceRetry: true))
                {
                    error = $"Debug server not available on port {_debugServer.Port}.";
                    return false;
                }

                var status = _debugServer.GetStatus();
                if (status.Paused)
                    return true;

                _debugServer.Pause();
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    Thread.Sleep(10);
                    if (_debugServer.GetStatus().Paused)
                    {
                        resumeAfter = true;
                        return true;
                    }
                }

                error = "Timed out waiting for PCSX2 to pause before patching.";
                return false;
            }
            catch (Exception ex)
            {
                try { _debugServer.Disconnect(); } catch { }
                _debugServerAvailable = false;
                error = ex.Message;
                return false;
            }
        }

        private void RestoreVmAfterBulkPatch(bool resumeAfter)
        {
            if (!resumeAfter)
                return;

            try
            {
                _debugServer.Resume();
            }
            catch
            {
                try { _debugServer.Disconnect(); } catch { }
                _debugServerAvailable = false;
            }
        }

        private static List<(uint Addr, byte[] Bytes)> NormalizePatches(IReadOnlyList<(uint Addr, byte[] Bytes)> patches)
        {
            var result = new List<(uint Addr, byte[] Bytes)>();
            if (patches == null || patches.Count == 0)
                return result;

            var sorted = patches
                .Where(p => p.Bytes != null && p.Bytes.Length > 0)
                .OrderBy(p => p.Addr)
                .ToList();

            if (sorted.Count == 0)
                return result;

            uint currentAddr = sorted[0].Addr;
            var currentBytes = new List<byte>(sorted[0].Bytes);

            for (int i = 1; i < sorted.Count; i++)
            {
                uint nextAddr = sorted[i].Addr;
                byte[] nextBytes = sorted[i].Bytes;
                long currentEndExclusive = (long)currentAddr + currentBytes.Count;

                if (nextAddr <= currentEndExclusive)
                {
                    long overlap = currentEndExclusive - nextAddr;
                    int appendStart = 0;
                    if (overlap > 0)
                    {
                        int overwriteCount = Math.Min((int)overlap, nextBytes.Length);
                        int dstIndex = currentBytes.Count - (int)overlap;
                        for (int j = 0; j < overwriteCount; j++)
                            currentBytes[dstIndex + j] = nextBytes[j];
                        appendStart = overwriteCount;
                    }

                    for (int j = appendStart; j < nextBytes.Length; j++)
                        currentBytes.Add(nextBytes[j]);
                }
                else
                {
                    result.Add((currentAddr, currentBytes.ToArray()));
                    currentAddr = nextAddr;
                    currentBytes = new List<byte>(nextBytes);
                }
            }

            result.Add((currentAddr, currentBytes.ToArray()));
            return result;
        }

        private static bool TryWriteRemoteBytesSplit(IntPtr hProc, long destAddress, byte[] bytes, int offset, int length, int repeatWrites, out string? error)
        {
            error = null;
            if (length <= 0) return true;

            var slice = new byte[length];
            Buffer.BlockCopy(bytes, offset, slice, 0, length);
            if (TryWriteRemoteBytes(hProc, new IntPtr(destAddress), slice, repeatWrites, out error))
                return true;

            if (length <= 1)
                return false;

            int leftLen = length / 2;
            int rightLen = length - leftLen;
            if (!TryWriteRemoteBytesSplit(hProc, destAddress, bytes, offset, leftLen, repeatWrites, out error))
                return false;
            return TryWriteRemoteBytesSplit(hProc, destAddress + leftLen, bytes, offset + leftLen, rightLen, repeatWrites, out error);
        }

        private static bool TryWriteRemoteBytes(IntPtr hProc, IntPtr dest, byte[] bytes, int repeatWrites, out string? error)
        {
            error = null;
            if (bytes == null || bytes.Length == 0)
                return true;

            static bool Attempt(IntPtr hProc, IntPtr dest, byte[] bytes, int repeatWrites, out int lastError)
            {
                lastError = 0;
                for (int pass = 0; pass < Math.Max(1, repeatWrites); pass++)
                {
                    if (!NativeMethods.WriteProcessMemory(hProc, dest, bytes, bytes.Length, out int written) || written != bytes.Length)
                    {
                        lastError = Marshal.GetLastWin32Error();
                        return false;
                    }
                }
                return true;
            }

            if (Attempt(hProc, dest, bytes, repeatWrites, out int directErr))
                return true;

            const long PAGE_SIZE = 0x1000;
            long dest64 = dest.ToInt64();
            long pageBase = dest64 & ~(PAGE_SIZE - 1);
            long pageEnd = (dest64 + bytes.Length + PAGE_SIZE - 1) & ~(PAGE_SIZE - 1);
            IntPtr protectBase = new(pageBase);
            IntPtr protectSize = new(pageEnd - pageBase);
            if (NativeMethods.VirtualProtectEx(hProc, protectBase, protectSize, NativeMethods.PAGE_EXECUTE_READWRITE, out uint oldProt))
            {
                try
                {
                    if (Attempt(hProc, dest, bytes, repeatWrites, out _))
                        return true;
                }
                finally
                {
                    NativeMethods.VirtualProtectEx(hProc, protectBase, protectSize, oldProt, out _);
                }
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                IntPtr cur = new(dest64 + i);
                byte[] one = [bytes[i]];
                long curPageBase = (dest64 + i) & ~(PAGE_SIZE - 1);
                if (NativeMethods.VirtualProtectEx(hProc, new IntPtr(curPageBase), new IntPtr(PAGE_SIZE), NativeMethods.PAGE_EXECUTE_READWRITE, out uint oldByteProt))
                {
                    try
                    {
                        if (Attempt(hProc, cur, one, repeatWrites, out _))
                            continue;
                    }
                    finally
                    {
                        NativeMethods.VirtualProtectEx(hProc, new IntPtr(curPageBase), new IntPtr(PAGE_SIZE), oldByteProt, out _);
                    }
                }

                error = $"WriteProcessMemory failed at 0x{dest64 + i:X} (error {directErr}).";
                return false;
            }

            return true;
        }

        private static string? WritePatchesToPcsx2(uint procId, long eeHostAddr, IReadOnlyList<(uint Addr, byte[] Bytes)> patches, int repeatWrites = 1)
        {
            const uint ACCESS = NativeMethods.PROCESS_VM_READ   |
                                NativeMethods.PROCESS_VM_WRITE  |
                                NativeMethods.PROCESS_VM_OP     |
                                NativeMethods.PROCESS_QUERY_INFO;

            IntPtr hProc = NativeMethods.OpenProcess(ACCESS, false, procId);
            if (hProc == IntPtr.Zero)
                return "Cannot open attached PCSX2 for writing. Try running as Administrator.";

            try
            {
                if (eeHostAddr == 0)
                    return "EEmem pointer is null — is a game loaded?";

                int failed = 0;
                foreach (var (addr, bytes) in patches)
                {
                    if (bytes == null || bytes.Length == 0)
                        continue;

                    var dest = new IntPtr(eeHostAddr + addr);
                    bool ok = TryWriteRemoteBytesSplit(hProc, dest.ToInt64(), bytes, 0, bytes.Length, repeatWrites, out _);

                    if (ok)
                    {
                        NativeMethods.FlushInstructionCache(hProc, dest, (UIntPtr)bytes.Length);
                    }

                    if (!ok) failed++;
                }

                return failed > 0
                    ? $"{failed} of {patches.Count} write(s) failed."
                    : null;
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }
        }

        private static string? WritePatchesToPcsx2(IReadOnlyList<(uint Addr, byte[] Bytes)> patches, int repeatWrites = 1)
        {
            var validNames = new[] { "pcsx2", "pcsx2-qt" };

            var proc = Process.GetProcesses()
                .FirstOrDefault(p =>
                    validNames.Any(name =>
                        string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)));
            if (proc == null) return "PCSX2 process not found.";

            const uint ACCESS = NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_INFO;
            IntPtr hProc = NativeMethods.OpenProcess(ACCESS, false, (uint)proc.Id);
            if (hProc == IntPtr.Zero)
                return "Cannot open PCSX2 for writing. Try running as Administrator.";

            try
            {
                var mods = new IntPtr[256];
                if (!NativeMethods.EnumProcessModulesEx(
                        hProc, mods, mods.Length * IntPtr.Size, out _, 0x03))
                    return "EnumProcessModulesEx failed.";

                IntPtr eeSymAddr = FindPeExport(hProc, mods[0], "EEmem", out bool is64, out string? err);
                if (eeSymAddr == IntPtr.Zero) return err ?? "EEmem symbol not found.";

                long eeHostAddr;
                if (is64)
                {
                    var buf = new byte[8];
                    NativeMethods.ReadProcessMemory(hProc, eeSymAddr, buf, 8, out _);
                    eeHostAddr = BitConverter.ToInt64(buf, 0);
                }
                else
                {
                    var buf = new byte[4];
                    NativeMethods.ReadProcessMemory(hProc, eeSymAddr, buf, 4, out _);
                    eeHostAddr = BitConverter.ToUInt32(buf, 0);
                }

                return WritePatchesToPcsx2((uint)proc.Id, eeHostAddr, patches, repeatWrites);
            }
            finally { NativeMethods.CloseHandle(hProc); }
        }


        // ── Clipboard ─────────────────────────────────────────────────────

        private void CopySelected()
        {
            CopyLineForSelectedRow();
        }

        private void CopyLineForSelectedRow()
        {
            if (_selRow < 0 || _selRow >= _rows.Count) return;
            var r = ResolveRowForDisplay(_rows[_selRow]);
            string? label = GetLabelAt(r.Address);
            string text = string.IsNullOrWhiteSpace(label)
                ? $"{r.Address:X8} {r.HexWord}"
                : $"{r.Address:X8} {r.HexWord} {label}";
            Clipboard.SetText(text);
        }

        private void FreezeSelectedRowToCodes()
        {
            if (_selRow < 0 || _selRow >= _rows.Count) return;
            var r = ResolveRowForDisplay(_rows[_selRow]);
            string addr = r.Address.ToString("X8");
            string freezeAddr = $"2{addr[1..]}";
            var dlg = EnsureCodeToolsDialog();
            dlg.AppendCodeLine($"{freezeAddr} {r.HexWord}");
            dlg.ActivateCodesSilently();
        }

        // ── Save ──────────────────────────────────────────────────────────

        private void SaveDisasm()
        {
            if (_rows.Count == 0) return;

            string baseName = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(_currentProjectPath) ? _fileName : _currentProjectPath);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "project";

            using var dlg = new SaveFileDialog
            {
                Title = "Save Disassembly",
                Filter = "PCSX2Dis Project (*.ide)|*.ide|PS2dis Project (*.pis)|*.pis|Binary (*.bin)|*.bin|Assembly (*.asm)|*.asm|Text (*.txt)|*.txt|All files (*.*)|*.*",
                FilterIndex = 1,
                DefaultExt = "ide",
                AddExtension = true,
                FileName = baseName,
            };

            if (!string.IsNullOrWhiteSpace(_currentProjectPath))
                dlg.InitialDirectory = Path.GetDirectoryName(_currentProjectPath);

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string fileName = dlg.FileName;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = dlg.FilterIndex switch
                {
                    1 => ".ide",
                    2 => ".pis",
                    3 => ".bin",
                    4 => ".asm",
                    5 => ".txt",
                    _ => string.Empty,
                };
                if (!string.IsNullOrEmpty(ext))
                    fileName += ext;
            }

            try
            {
                switch (ext)
                {
                    case ".ide":
                        WritePcsx2DisProject(fileName);
                        _currentProjectPath = fileName;
                        break;

                    case ".pis":
                        WritePisProject(fileName);
                        break;

                    case ".bin":
                        WriteRawBinaryFile(fileName, _fileData);
                        break;

                    case ".asm":
                    case ".txt":
                    default:
                        var sb = new StringBuilder();
                        sb.AppendLine($"; ps2dis output — {_fileName}");
                        sb.AppendLine($"; Base: 0x{_baseAddr:X8}   Region: 0x{_disasmBase:X8} + 0x{_disasmLen:X}");
                        sb.AppendLine();
                        foreach (var raw in _rows)
                        {
                            var r = ResolveRowForDisplay(raw);
                            string? lbl = GetLabelAt(r.Address);
                            if (lbl != null) sb.AppendLine($"\n{lbl}:");
                            string cmt = _userComments.TryGetValue(r.Address, out var c) ? $"  ; {c}" : "";
                            sb.AppendLine($"  {r.Address:X8}  {r.HexWord}  {r.Mnemonic,-10} {r.Operands}{cmt}");
                        }
                        File.WriteAllText(fileName, sb.ToString());
                        break;
                }

                MessageBox.Show($"Saved to:\n{fileName}", "ps2dis",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save Disassembly", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── User labels ───────────────────────────────────────────────────

        private string? GetLabelAt(uint addr)
        {
            if (_userLabels.TryGetValue(addr, out var u)) return u;
            if (_objectTempLabels.TryGetValue(addr, out var o)) return o;
            if (_autoLabels.TryGetValue(addr, out var a)) return a;
            if (_stringLabels.TryGetValue(addr, out var s)) return s;

            // Xref temp-labels are display-only. Generate them lazily instead of storing
            // a second address->string dictionary for every analyzer target.
            if (_xrefs.ContainsKey(addr)) return $"_{addr:X8}";
            return null;
        }

        private string? GetRegularLabelAt(uint addr)
        {
            // Only saved/user labels count as regular labels for visual object-label suffixes.
            // Object labels should replace generated/temp labels (xref _, FUNC_, string labels, etc.),
            // but append to real labels as Label#ObjectLabel.
            return _userLabels.TryGetValue(addr, out var u) && !string.IsNullOrWhiteSpace(u) ? u : null;
        }

        private bool TryGetObjectLabelSuffixAt(uint addr, out string objectLabel)
        {
            if (_objectTempLabels.TryGetValue(addr, out var liveObjectLabel) && !string.IsNullOrWhiteSpace(liveObjectLabel))
            {
                objectLabel = liveObjectLabel.Trim();
                return true;
            }

            // Object entry names can also annotate the static pointer address, but only as
            // a suffix to an existing regular label. They are not standalone temp labels.
            var staticDefinition = _objectLabelDefinitions.FirstOrDefault(x => x.StaticAddress == addr);
            if (staticDefinition != null && !string.IsNullOrWhiteSpace(staticDefinition.Label))
            {
                objectLabel = staticDefinition.Label.Trim();
                return true;
            }

            objectLabel = string.Empty;
            return false;
        }

        private bool TryGetCombinedRegularObjectLabel(uint addr, out string regularLabel, out string objectLabel)
        {
            regularLabel = GetRegularLabelAt(addr) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(regularLabel) && TryGetObjectLabelSuffixAt(addr, out objectLabel))
                return true;

            objectLabel = string.Empty;
            return false;
        }

        private string? GetDisplayLabelAt(uint addr)
        {
            if (TryGetCombinedRegularObjectLabel(addr, out string regularLabel, out string objectLabel))
                return $"{regularLabel}#{objectLabel}";

            return GetLabelAt(addr);
        }

        /// <summary>
        /// Like <see cref="GetLabelAt"/> but excludes transient xref/temp labels.
        /// Used for instruction Command-column annotations so that only saved labels
        /// (user-defined, auto-generated FUNC_/_, or string labels) appear inline.
        /// </summary>
        private static bool IsTransientAnnotationLabel(string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return true;

            string cleaned = label.Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"^(?:FUNC_|_)[0-9A-Fa-f]{8}$");
        }

        private string? GetRealLabelAt(uint addr)
        {
            if (_userLabels.TryGetValue(addr, out var u) && !IsTransientAnnotationLabel(u)) return u;
            if (_objectTempLabels.TryGetValue(addr, out var o) && !IsTransientAnnotationLabel(o)) return o;
            if (_autoLabels.TryGetValue(addr, out var a) && !IsTransientAnnotationLabel(a)) return a;
            if (_stringLabels.TryGetValue(addr, out var s) && !IsTransientAnnotationLabel(s)) return s;
            return null;
        }

        private void RebuildLabelCache()
        {
            var list = new List<(string Name, uint Address)>();
            // Do not include transient xref temp-labels in the Labels browser/cache.
            // They are display-only and should not behave like stored/project labels.
            foreach (var kv in _stringLabels) if (!string.IsNullOrWhiteSpace(kv.Value)) list.Add((kv.Value, kv.Key));
            foreach (var kv in _userLabels)   if (!string.IsNullOrWhiteSpace(kv.Value)) list.Add((kv.Value, kv.Key));
            _cachedLabels = list.GroupBy(x => x.Address).Select(g => g.Last()).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Address).ToList();
        }

        private void ScanStringLabels()
        {
            _stringLabels = new Dictionary<uint, string>();
            if (_fileData == null) return;
            const int MinLen = 4, MaxShow = 48;
            int i = 0;
            while (i < _fileData.Length)
            {
                byte b = _fileData[i];
                if (b >= 0x20 && b <= 0x7E)
                {
                    int start = i;
                    while (i < _fileData.Length && _fileData[i] >= 0x20 && _fileData[i] <= 0x7E) i++;
                    int len = i - start;
                    if (len >= MinLen && i < _fileData.Length && _fileData[i] == 0x00)
                    {
                        string text = System.Text.Encoding.ASCII.GetString(_fileData, start, Math.Min(len, MaxShow));
                        if (len > MaxShow) text += "…";
                        uint addr = _baseAddr + (uint)start;
                        if (!_stringLabels.ContainsKey(addr))
                            _stringLabels[addr] = $"\"{text}\"";
                    }
                }
                else i++;
            }
            RebuildLabelCache();
        }

// ── Inline editing ────────────────────────────────────────────────

        private void OnDisasmMouseDown(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                return;

            var hit = _disasmList.HitTest(e.Location);
            if (hit.Item != null && hit.Item.Index >= 0)
                SelectRow(hit.Item.Index, syncHex: true, center: false);
        }

        private void OnDisasmMouseDoubleClick(object? s, MouseEventArgs e)
        {
            var hit = _disasmList.HitTest(e.Location);
            if (hit.Item == null) return;
            int col = hit.ColumnIndex;
            if (col < 0) return; // -1 = miss
            int row = hit.Item.Index;
            // Defer past the listview's own focus/selection handling so the textbox
            // doesn't immediately lose focus back to the list on the same click.
            BeginInvoke(() => BeginInlineEdit(row, col));
        }

        private void BeginInlineEdit(int row, int col)
        {
            if (row < 0 || row >= _rows.Count) return;

            var cellRect = _disasmList.GetSubItemRect(row, col);
            if (cellRect.Width < 4) return;

            if (_inlineEdit == null)
            {
                _inlineEdit = new TextBox
                {
                    BorderStyle = BorderStyle.FixedSingle,
                    Font        = GetStandardTextBoxFont(),
                    BackColor   = _themeEditValidBack,
                    ForeColor   = _themeWindowFore,
                };
                _inlineEdit.KeyDown += OnInlineKeyDown;
                _inlineEdit.Leave   += (_, _) => CommitInlineEdit();
                _disasmList.Parent!.Controls.Add(_inlineEdit);
            }

            _inlineRow = row;
            _inlineCol = col;
            _inlineEdit.ReadOnly = col == 0;
            _inlineEdit.TabStop = col != 0;

            // Convert listview client coords → parent tab page client coords
            var screenPt = _disasmList.PointToScreen(new Point(cellRect.Left, cellRect.Top));
            var panelPt  = _disasmList.Parent!.PointToClient(screenPt);

            _inlineEdit.Bounds  = new Rectangle(panelPt.X, panelPt.Y, cellRect.Width, cellRect.Height);
            _inlineEdit.Text    = GetInlineCellText(_rows[row], col);
            _inlineEdit.ForeColor = _themeWindowFore;
            _inlineEdit.Visible = true;
            if (col == 0)
                Clipboard.SetText(_inlineEdit.Text);
            _inlineEdit.BringToFront();
            _inlineEdit.SelectAll();
            _inlineEdit.Focus();
        }

        private string GetInlineCellText(SlimRow slim, int col)
        {
            if (col == 0)                                return slim.Address.ToString("X8");
            if (_showHex   && col == 1)                  return slim.HexWord;
            if (_showBytes && col == (_showHex ? 2 : 1)) return slim.BytesStr;
            if (col == CmdCol)
            {
                var r = ResolveRowForDisplay(slim);
                return r.Mnemonic switch
                {
                    ".word"  => FormatWordValue(r.Word).Replace(AnnotationSentinel.ToString(), string.Empty),
                    ".half"  => r.Operands.Replace(AnnotationSentinel.ToString(), string.Empty),
                    ".byte"  => r.Operands.Replace(AnnotationSentinel.ToString(), string.Empty),
                    ".float" => r.Operands.Replace(AnnotationSentinel.ToString(), string.Empty),
                    _         => NormalizeCommandAddressPrefixes($"{r.Mnemonic} {r.Operands}".Trim()),
                };
            }
            if (col == LblCol) { _userLabels.TryGetValue(slim.Address, out var l); return l ?? ""; }
            return "";
        }

        private void OnInlineKeyDown(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (_inlineEdit != null && _inlineEdit.ReadOnly) { CloseInlineEdit(); return; }
                if (_inlineRow < 0 || _inlineRow >= _rows.Count) { CloseInlineEdit(); return; }
                bool valid = ApplyInlineEdit(_inlineRow, _inlineCol, _inlineEdit!.Text.Trim());
                if (valid)
                    CloseInlineEdit();
                else
                    _inlineEdit!.BackColor = _themeEditInvalidBack; // invalid
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                CancelInlineEdit();
            }
        }

        // Close and accept (called after a successful Enter commit, or on Leave).
        private void CloseInlineEdit()
        {
            if (_inlineEdit == null) return;
            _inlineEdit.BackColor = _themeEditValidBack; // restore normal colour
            _inlineRow = _inlineCol = -1; // clear BEFORE hiding to block Leave re-entry
            _inlineEdit.Visible = false;
            _disasmList.Focus();
        }

        private void CancelInlineEdit()
        {
            if (_inlineEdit == null) return;
            _inlineEdit.BackColor = _themeEditValidBack;
            _inlineRow = _inlineCol = -1;
            _inlineEdit.Visible = false;
            _disasmList.Focus();
        }

        // Called by the Leave event — silently apply if valid, silently discard if not.
        private void CommitInlineEdit()
        {
            if (_inlineEdit == null || !_inlineEdit.Visible) return;
            if (_inlineEdit.ReadOnly) { CloseInlineEdit(); return; }
            int row = _inlineRow;
            int col = _inlineCol;
            string text = _inlineEdit.Text.Trim();
            _inlineRow = _inlineCol = -1; // clear BEFORE hiding to block Leave re-entry
            _inlineEdit.Visible = false;
            if (row >= 0 && row < _rows.Count)
                ApplyInlineEdit(row, col, text); // result ignored — silent on focus-loss
            _disasmList.Focus();
        }

        // Returns true if the edit was valid (or unchanged), false if assembly failed.
        private bool ApplyInlineEdit(int row, int col, string text)
        {
            var r   = _rows[row];
            var eng = GetCachedDisasm();
            var displayRow = ResolveRowForDisplay(r);

            int hexCol   = _showHex   ? 1 : -1;
            int bytesCol = _showBytes ? (_showHex ? 2 : 1) : -1;

            if (col == hexCol)
            {
                if (displayRow.Mnemonic == ".byte")
                {
                    if (!TryParseMaskedInlineByteValue(text, out uint byteValue))
                        return false;

                    if ((r.Word & 0xFFu) != (byteValue & 0xFFu))
                        ApplyTypedDisassemblyValueChange(row, byteValue);
                    return true;
                }

                if (displayRow.Mnemonic == ".half")
                {
                    if (!TryParseMaskedInlineHalfValue(text, out uint halfValue))
                        return false;

                    if ((r.Word & 0xFFFFu) != (halfValue & 0xFFFFu))
                        ApplyTypedDisassemblyValueChange(row, halfValue);
                    return true;
                }

                string h = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
                if (!uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out uint w))
                    return false;

                uint compareWord = displayRow.Mnemonic is ".word" or ".float" ? GetInlineEditBackingWord(r) : r.Word;
                if (w != compareWord)
                {
                    if (displayRow.Mnemonic is ".word" or ".float")
                        ApplyTypedDisassemblyValueChange(row, w);
                    else
                        CommitWordChange(row, w, r.Address, eng);
                }
                return true;
            }

            if (col == bytesCol)
            {
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4) return false;
                var bs = new byte[4];
                for (int i = 0; i < 4; i++)
                    if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bs[i]))
                        return false;
                uint w = (uint)(bs[0] | (bs[1] << 8) | (bs[2] << 16) | (bs[3] << 24));
                uint compareWord = r.DataSub != DataKind.None ? GetInlineEditBackingWord(r) : r.Word;
                if (w != compareWord)
                {
                    if (r.DataSub != DataKind.None)
                        CommitWordChangePreserveDisplayType(row, w, r.Address, eng, displayRow.Mnemonic);
                    else
                        CommitWordChange(row, w, r.Address, eng);
                }
                return true;
            }

            if (col == CmdCol)
            {
                if (displayRow.Mnemonic == ".float")
                {
                    if (!float.TryParse(text, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out float fv))
                        return false;
                    uint newValue = unchecked((uint)BitConverter.SingleToInt32Bits(fv));
                    if (newValue != GetInlineEditBackingWord(r))
                        ApplyTypedDisassemblyValueChange(row, newValue);
                    return true;
                }

                if (displayRow.Mnemonic == ".word")
                {
                    if (!TryParseInlineWordValue(text, out uint wordValue))
                        return false;
                    if (wordValue != GetInlineEditBackingWord(r))
                        ApplyTypedDisassemblyValueChange(row, wordValue);
                    return true;
                }

                if (displayRow.Mnemonic == ".half")
                {
                    if (!TryParseInlineHalfValue(text, out uint halfValue))
                        return false;
                    if ((r.Word & 0xFFFFu) != (halfValue & 0xFFFFu))
                        ApplyTypedDisassemblyValueChange(row, halfValue);
                    return true;
                }

                if (displayRow.Mnemonic == ".byte")
                {
                    if (!TryParseInlineByteValue(text, out uint byteValue))
                        return false;
                    if ((r.Word & 0xFFu) != (byteValue & 0xFFu))
                        ApplyTypedDisassemblyValueChange(row, byteValue);
                    return true;
                }

                uint? w = MipsAssembler.Assemble(text, r.Address);
                if (!w.HasValue) return false;
                if (w.Value != r.Word) CommitWordChangePreserveDisplayType(row, w.Value, r.Address, eng, displayRow.Mnemonic);
                return true;
            }

            if (col == LblCol)
            {
                if (text.Length == 0) _userLabels.Remove(r.Address);
                else _userLabels[r.Address] = text;
                RebuildLabelCache();
                _disasmList.RedrawItems(row, row, true);
                return true;
            }

            return true;
        }


        private static bool TryParseInlineWordValue(string text, out uint value)
        {
            text = text.Trim();
            int paren = text.IndexOf('(');
            if (paren >= 0) text = text[..paren].Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        private static bool TryParseInlineHalfValue(string text, out uint value)
        {
            text = text.Trim();
            int paren = text.IndexOf('(');
            if (paren >= 0) text = text[..paren].Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            if (ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ushort half))
            {
                value = half;
                return true;
            }
            value = 0;
            return false;
        }

        private static bool TryParseInlineByteValue(string text, out uint value)
        {
            text = text.Trim();
            int paren = text.IndexOf('(');
            if (paren >= 0) text = text[..paren].Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            if (byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                value = b;
                return true;
            }
            value = 0;
            return false;
        }


        private uint GetInlineEditBackingWord(SlimRow row)
        {
            uint aligned = row.Address & ~3u;
            if (TryReadWordAt(aligned, out uint word))
                return word;

            return row.DataSub switch
            {
                DataKind.Byte => (row.Word & 0xFFu) << (((int)(row.Address & 3u)) * 8),
                DataKind.Half => (row.Word & 0xFFFFu) << (((int)(row.Address & 2u)) * 8),
                _ => row.Word,
            };
        }

        private static string ExtractInlineHexDigits(string text)
        {
            text = text.Trim();
            int paren = text.IndexOf('(');
            if (paren >= 0) text = text[..paren].Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text[2..];

            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (Uri.IsHexDigit(ch))
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static bool TryParseMaskedInlineHalfValue(string text, out uint value)
        {
            string digits = ExtractInlineHexDigits(text);
            if (digits.Length == 0 || digits.Length > 4)
            {
                value = 0;
                return false;
            }

            if (ushort.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out ushort half))
            {
                value = half;
                return true;
            }

            value = 0;
            return false;
        }

        private static bool TryParseMaskedInlineByteValue(string text, out uint value)
        {
            string digits = ExtractInlineHexDigits(text);
            if (digits.Length == 0 || digits.Length > 2)
            {
                value = 0;
                return false;
            }

            if (byte.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                value = b;
                return true;
            }

            value = 0;
            return false;
        }

        private void TrackOriginalOpcodeBeforeUserChange(uint addr, uint previousWord, uint newWord)
        {
            if (previousWord == newWord)
                return;

            if (!_originalOpCode.ContainsKey(addr))
                _originalOpCode[addr] = previousWord;

            if (_originalOpCode.TryGetValue(addr, out uint originalWord) && originalWord == newWord)
                _originalOpCode.Remove(addr);
        }

        private bool HasOriginalOpcodeForAddress(uint addr)
            => _originalOpCode.ContainsKey(addr);

        private bool TryGetOriginalOpcodeForAddress(uint addr, out uint originalWord)
            => _originalOpCode.TryGetValue(addr, out originalWord);

        private void CommitWordChange(int row, uint newWord, uint addr, MipsDisassemblerEx eng)
        {
            uint previousWord = _rows[row].Word;
            TrackOriginalOpcodeBeforeUserChange(addr, previousWord, newWord);

            _rows[row] = SlimRowFromWord(newWord, addr);

            if (_fileData != null)
            {
                long off = (long)(addr - _baseAddr);
                if (off >= 0 && off + 4 <= _fileData.Length)
                    Array.Copy(BitConverter.GetBytes(newWord), 0, _fileData, (int)off, 4);
            }

            _disasmList.RedrawItems(row, row, true);

            if (_liveProcId != 0 && _eeHostAddr != 0)
            {
                var bytes = BitConverter.GetBytes(newWord);
                var err   = WritePatchesToLivePcsx2([(addr, bytes)]);
                if (err != null)
                    LogPine($"Inline write fallback failed at 0x{addr:X8}: {err}");
            }
        }

        private void CommitWordChangePreserveDisplayType(int row, uint newWord, uint addr, MipsDisassemblerEx eng, string displayMnemonic)
        {
            if (displayMnemonic == ".word" || displayMnemonic == ".half" || displayMnemonic == ".byte" || displayMnemonic == ".float")
            {
                uint previousWord = _rows[row].Word;
                TrackOriginalOpcodeBeforeUserChange(addr, previousWord, newWord);

                _rows[row] = SlimRow.DataRow(addr, newWord, SlimRow.DataKindFromMnemonic(displayMnemonic));

                if (_fileData != null)
                {
                    long off = (long)(addr - _baseAddr);
                    if (off >= 0 && off + 4 <= _fileData.Length)
                        Array.Copy(BitConverter.GetBytes(newWord), 0, _fileData, (int)off, 4);
                }

                _disasmList.RedrawItems(row, row, true);

                if (_liveProcId != 0 && _eeHostAddr != 0)
                {
                    var bytes = BitConverter.GetBytes(newWord);
                    var err   = WritePatchesToLivePcsx2([(addr, bytes)]);
                    if (err != null)
                        MessageBox.Show(err, "PCSX2 Write Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            CommitWordChange(row, newWord, addr, eng);
        }

        private void AddOrEditLabel()
        {
            if (_selRow < 0 || _selRow >= _rows.Count) return;
            var r = _rows[_selRow];
            uint addr = r.Address;

            using var dlg = new InputDialog(
                "Add / Edit Label",
                $"Label for 0x{addr:X8} (leave blank to remove):",
                GetRegularLabelAt(addr) ?? "");
            dlg.BackColor = _themeFormBack;
            dlg.ForeColor = _themeFormFore;
            ApplyThemeToControlTree(dlg);
            dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string newName = dlg.Value.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                _userLabels.Remove(addr);
            }
            else
            {
                _userLabels[addr] = newName;
            }
            RebuildLabelCache();

            // Redraw the affected row so the new label appears immediately
            if (_selRow >= 0 && _selRow < _rows.Count)
                _disasmList.RedrawItems(_selRow, _selRow, true);
        }

        // ── Helpers ───────────────────────────────────────────────────────

    }

    // ══════════════════════════════════════════════════════════════════════
    // Raw dump / region dialog
    // ══════════════════════════════════════════════════════════════════════

    internal sealed class RawDumpDialog : Form
    {
        public uint BaseAddress  { get; private set; }
        public uint DisasmLength { get; private set; }

        private readonly TextBox _tbBase;
        private readonly TextBox _tbLen;

        public RawDumpDialog(uint defaultBase = 0x00000000, uint defaultLen = 0x02000000)
        {
            Text            = "Disassembly Region";
            Size            = new Size(400, 155);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterParent;
            Font            = new Font("Tahoma", 8.25f);

            var lblBase = new Label { Text = "Base address (hex):", Location = new Point(12, 15), AutoSize = true };
            _tbBase = new TextBox
            {
                Text     = $"0x{defaultBase:X8}",
                Location = new Point(12, 35),
                Width    = 160,
                Font     = new Font("Courier New", 10f),
            };

            var lblLen = new Label { Text = "Disassemble length (hex):", Location = new Point(12, 65), AutoSize = true };
            _tbLen = new TextBox
            {
                Text     = $"0x{defaultLen:X}",
                Location = new Point(12, 85),
                Width    = 160,
                Font     = new Font("Courier New", 10f),
            };

            var ok  = new Button { Text = "OK",     DialogResult = DialogResult.OK,     Location = new Point(210, 85), Width = 76 };
            var can = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(296, 85), Width = 76 };
            ok.Click += OnOk;
            AcceptButton = ok; CancelButton = can;

            Controls.AddRange(new Control[] { lblBase, _tbBase, lblLen, _tbLen, ok, can });
        }

        private void OnOk(object? s, EventArgs e)
        {
            if (!TryParseHex(_tbBase.Text, out uint b) || !TryParseHex(_tbLen.Text, out uint l) || l == 0)
            {
                MessageBox.Show("Enter valid hex values for base address and length.", "ps2dis",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None; // keep dialog open
                return;
            }
            BaseAddress  = b;
            DisasmLength = l;
        }

        private static bool TryParseHex(string s, out uint v)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Simple single-line input dialog
    // ══════════════════════════════════════════════════════════════════════

    internal sealed class InputDialog : Form
    {
        private readonly TextBox _tb;
        public string Value => _tb.Text;

        public InputDialog(string title, string prompt, string initial)
        {
            Text            = title;
            Size            = new Size(390, 145);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterParent;
            Font            = new Font("Tahoma", 8.25f);

            Controls.Add(new Label { Text = prompt, Location = new Point(12, 0), AutoSize = true });
            _tb = new TextBox
            {
                Text = initial, Location = new Point(12, 34),
                Width = 350,
            };
            _tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); } };
            Controls.Add(_tb);
            var ok  = new Button { Text = "OK",     DialogResult = DialogResult.OK,     Location = new Point(200, 68), Width = 76 };
            var can = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(286, 68), Width = 76 };
            AcceptButton = ok; CancelButton = can;
            Controls.Add(ok); Controls.Add(can);
        }
    }

    internal sealed class GotoAddressDialog : Form
    {
        private readonly TextBox _tb;
        public string Value => _tb.Text;

        public GotoAddressDialog(string initial, Color? backColor = null, Color? foreColor = null, Color? tbBackColor = null, Color? tbForeColor = null)
        {
            Text = "Go to Address";
            ClientSize = new Size(238, 34);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            KeyPreview = true;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Tahoma", 8.25f);

            if (backColor.HasValue) BackColor = backColor.Value;
            if (foreColor.HasValue) ForeColor = foreColor.Value;

            string normalizedInitial = NormalizeHexText(initial);
            _tb = new TextBox
            {
                Text = normalizedInitial,
                Location = new Point(8, 6),
                Width = ClientSize.Width - 16,
                BorderStyle = BorderStyle.FixedSingle,
                CharacterCasing = CharacterCasing.Upper,
                MaxLength = 8,
                ShortcutsEnabled = true,
            };
            if (tbBackColor.HasValue) _tb.BackColor = tbBackColor.Value;
            if (tbForeColor.HasValue) _tb.ForeColor = tbForeColor.Value;
            _tb.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    BeginInvoke(new Action(() => { DialogResult = DialogResult.OK; Close(); }));
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    BeginInvoke(new Action(() => { DialogResult = DialogResult.Cancel; Close(); }));
                }
            };
            _tb.KeyPress += (_, e) =>
            {
                if (e.KeyChar == (char)Keys.Return || e.KeyChar == (char)Keys.Escape)
                {
                    e.Handled = true;
                    return;
                }

                if (char.IsControl(e.KeyChar))
                    return;

                char upper = char.ToUpperInvariant(e.KeyChar);
                if ((upper < '0' || upper > '9') && (upper < 'A' || upper > 'F'))
                    e.Handled = true;
            };
            _tb.TextChanged += (_, _) =>
            {
                string normalized = NormalizeHexText(_tb.Text);
                if (!string.Equals(_tb.Text, normalized, StringComparison.Ordinal))
                {
                    int caret = Math.Min(normalized.Length, _tb.SelectionStart);
                    _tb.Text = normalized;
                    _tb.SelectionStart = caret;
                }
            };
            _tb.PreviewKeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
                    e.IsInputKey = true;
            };
            Controls.Add(_tb);
            Shown += (_, _) => BeginInvoke(new Action(() => { ActiveControl = _tb; _tb.Focus(); _tb.SelectAll(); }));
        }

        private static string NormalizeHexText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var sb = new StringBuilder(8);
            foreach (char ch in text)
            {
                char upper = char.ToUpperInvariant(ch);
                if ((upper >= '0' && upper <= '9') || (upper >= 'A' && upper <= 'F'))
                {
                    sb.Append(upper);
                    if (sb.Length == 8)
                        break;
                }
            }
            return sb.ToString();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter)
            {
                BeginInvoke(new Action(() => { DialogResult = DialogResult.OK; Close(); }));
                return true;
            }
            if (keyData == Keys.Escape)
            {
                BeginInvoke(new Action(() => { DialogResult = DialogResult.Cancel; Close(); }));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Enter)
            {
                BeginInvoke(new Action(() => { DialogResult = DialogResult.OK; Close(); }));
                return true;
            }
            if (keyData == Keys.Escape)
            {
                BeginInvoke(new Action(() => { DialogResult = DialogResult.Cancel; Close(); }));
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            BeginInvoke(new Action(() => { ActiveControl = _tb; _tb.Focus(); _tb.SelectAll(); }));
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Label Browser dialog  (Ctrl+G)
    // ══════════════════════════════════════════════════════════════════════

    internal sealed class LabelBrowserDialog : Form
    {
        public uint SelectedAddress { get; private set; }
        public string InitialFilter { get; set; } = string.Empty;
        public bool InitialLabelsOnly { get; set; }
        public uint InitialSelectedAddress { get; set; }
        public string CurrentFilter => _search.Text;
        public bool LabelsOnly => _cbLabelsOnly.Checked;
        public uint CurrentSelectedAddress => _list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] >= 0 && _list.SelectedIndices[0] < _filtered.Count ? _filtered[_list.SelectedIndices[0]].Address : InitialSelectedAddress;

        private readonly List<(string Name, uint Address)> _allLabels;
        private          List<(string Name, uint Address)> _filtered;

        private readonly MainForm.CenteredSingleLineTextBox  _search;
        private readonly VirtualDisasmList _list;
        private readonly Label    _countLabel;
        private readonly CheckBox _cbLabelsOnly;
        private readonly Font     _listFont = new("Courier New", 9f);

        // Theme colors (set via ApplyThemeColors)
        private Color _windowBack = Color.White;
        private Color _windowFore = Color.Black;
        private Color _selBack = Color.FromArgb(0, 0, 128);
        private Color _selFore = Color.White;
        private Color _headerBack = SystemColors.Control;
        private Color _headerFore = SystemColors.ControlText;
        private Color _headerBorder = SystemColors.ControlDark;

        public LabelBrowserDialog(List<(string Name, uint Address)> labels)
        {
            _allLabels = labels;
            _filtered  = new List<(string, uint)>(labels);

            Text            = "Labels";
            Size            = new Size(520, 560);
            MinimumSize     = new Size(360, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            Font            = new Font("Tahoma", 8.25f);

            // ── Search box ────────────────────────────────────────────────
            var searchLabel = new Label
            {
                Text     = "Filter:",
                Location = new Point(8, 10),
                AutoSize = true,
            };

            _search = new MainForm.CenteredSingleLineTextBox
            {
                Location = new Point(48, 7),
                Width    = 360,
                Font     = new Font("Courier New", 10f),
            };
            _search.TextChanged += OnSearchChanged;
            _search.KeyDown     += OnSearchKeyDown;

            _countLabel = new Label
            {
                Text      = $"{labels.Count} labels",
                Location  = new Point(418, 10),
                AutoSize  = true,
            };

            _cbLabelsOnly = new MainForm.ThemedCheckBox
            {
                Text = "Labels Only",
                AutoSize = true,
                Location = new Point(8, ClientSize.Height - 34),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            _cbLabelsOnly.CheckedChanged += (_, _) => OnSearchChanged(null, EventArgs.Empty);

            // ── Label list (VirtualDisasmList) ────────────────────────────
            _list = new VirtualDisasmList
            {
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = false,
                Font          = _listFont,
                Location      = new Point(8, 34),
                Anchor        = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                MultiSelect   = false,
                VirtualMode   = true,
                OwnerDraw     = true,
                BorderStyle   = BorderStyle.None,
                HeaderStyle   = ColumnHeaderStyle.Nonclickable,
                AllowColumnResize = false,
            };
            _list.RowHeight = Math.Max(1, _listFont.Height + 4);
            _list.HeaderHeight = Math.Max(1, _listFont.Height + 6);
            _list.Size = new Size(ClientSize.Width - 16, ClientSize.Height - 90);
            _list.Columns.Add("Address", 72);
            _list.Columns.Add("Label", Math.Max(120, _list.Width - 72 - SystemInformation.VerticalScrollBarWidth));
            _list.DrawCell += OnDrawCell;
            _list.DrawHeader += OnDrawHeader;
            _list.SelectedIndexChanged += (_, _) => _list.Invalidate();
            _list.MouseDoubleClick += (_, _) => Accept();
            _list.KeyDown += OnListKeyDown;

            // ── Buttons ───────────────────────────────────────────────────
            var btnGo = new Button
            {
                Text     = "Go to Label",
                Anchor   = AnchorStyles.Bottom | AnchorStyles.Right,
                Size     = new Size(100, 26),
            };
            btnGo.Location = new Point(ClientSize.Width - 214, ClientSize.Height - 38);
            btnGo.Click   += (_, _) => Accept();

            var btnClose = new Button
            {
                Text     = "Close",
                Anchor   = AnchorStyles.Bottom | AnchorStyles.Right,
                Size     = new Size(80, 26),
            };
            btnClose.Location    = new Point(ClientSize.Width - 108, ClientSize.Height - 38);
            btnClose.Click      += (_, _) => Close();
            CancelButton         = btnClose;

            Controls.AddRange(new Control[] { searchLabel, _search, _countLabel, _list, _cbLabelsOnly, btnGo, btnClose });

            // Handle resize
            Resize += (_, _) =>
            {
                _list.Size        = new Size(ClientSize.Width - 16, ClientSize.Height - 90);
                btnGo.Location    = new Point(ClientSize.Width - 214, ClientSize.Height - 38);
                btnClose.Location = new Point(ClientSize.Width - 108, ClientSize.Height - 38);
                _cbLabelsOnly.Location = new Point(8, ClientSize.Height - 34);
                _countLabel.Location = new Point(ClientSize.Width - 110, 10);
                if (_list.Columns.Count >= 2)
                    _list.Columns[1].Width = Math.Max(120, _list.Width - _list.Columns[0].Width - SystemInformation.VerticalScrollBarWidth);
            };

            PopulateList();
            ActiveControl = _search;
        }

        private void OnDrawHeader(object? s, VirtualDisasmList.VirtualHeaderPaintEventArgs e)
        {
            using var bg = new SolidBrush(_headerBack);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var textRect = Rectangle.Inflate(e.Bounds, -2, 0);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, _listFont, textRect, _headerFore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using var pen = new Pen(_headerBorder);
            e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        private void OnDrawCell(object? s, VirtualDisasmList.VirtualCellPaintEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _filtered.Count) return;
            var (name, addr) = _filtered[e.ItemIndex];
            bool sel = e.Selected;
            Color back = sel ? _selBack : _windowBack;
            Color fore = sel ? _selFore : _windowFore;
            using var br = new SolidBrush(back);
            e.Graphics.FillRectangle(br, e.Bounds);
            string text = e.ColumnIndex == 0 ? $"{addr:X8}" : name;
            var rect = Rectangle.Inflate(e.Bounds, -2, 0);
            TextRenderer.DrawText(e.Graphics, text, _listFont, rect, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        // ── Sort state ────────────────────────────────────────────────────
        private int  _sortCol = 1;   // 0=address, 1=name
        private bool _sortAsc = true;

        private void SortFiltered()
        {
            _filtered = _sortCol switch
            {
                0 => _sortAsc
                    ? new List<(string, uint)>(_filtered.OrderBy(x => x.Address))
                    : new List<(string, uint)>(_filtered.OrderByDescending(x => x.Address)),
                _ => _sortAsc
                    ? new List<(string, uint)>(_filtered.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    : new List<(string, uint)>(_filtered.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)),
            };
        }

        // ── Filter ────────────────────────────────────────────────────────
        private void OnSearchChanged(object? s, EventArgs e)
        {
            string q = _search.Text.Trim().ToLowerInvariant();
            IEnumerable<(string Name, uint Address)> query = _allLabels;
            if (_cbLabelsOnly.Checked)
                query = query.Where(l => !(l.Name.Length >= 2 && l.Name[0] == '"' && l.Name[^1] == '"'));
            _filtered = string.IsNullOrEmpty(q)
                ? new List<(string, uint)>(query)
                : query.Where(l =>
                    l.Name.ToLowerInvariant().Contains(q) ||
                    l.Address.ToString("X8").ToLowerInvariant().Contains(q)
                  ).ToList();
            SortFiltered();
            PopulateList();
        }

        private void PopulateList()
        {
            uint keepAddr = CurrentSelectedAddress;
            _list.VirtualListSize = 0;
            _list.VirtualListSize = _filtered.Count;
            if (_list.Columns.Count >= 2)
                _list.Columns[1].Width = Math.Max(120, _list.Width - _list.Columns[0].Width - SystemInformation.VerticalScrollBarWidth);

            _countLabel.Text = _filtered.Count == _allLabels.Count
                ? $"{_allLabels.Count} labels"
                : $"{_filtered.Count} / {_allLabels.Count}";

            if (_filtered.Count > 0)
            {
                int idx = _filtered.FindIndex(x => x.Address == keepAddr);
                if (idx < 0) idx = _filtered.FindIndex(x => x.Address == InitialSelectedAddress);
                if (idx < 0) idx = 0;
                _list.SelectedIndices.Clear();
                _list.SelectedIndices.Add(idx);
                _list.EnsureVisible(idx);
            }
        }

        public void ApplyInitialState()
        {
            _search.Text = InitialFilter;
            _cbLabelsOnly.Checked = InitialLabelsOnly;
            PopulateList();
            RestoreSelection();
        }

        private void RestoreSelection()
        {
            if (_filtered.Count == 0) return;
            int idx = _filtered.FindIndex(x => x.Address == InitialSelectedAddress);
            if (idx < 0) idx = 0;
            _list.SelectedIndices.Clear();
            _list.SelectedIndices.Add(idx);
            _list.EnsureVisible(idx);
        }

        // ── Keyboard ──────────────────────────────────────────────────────
        private void OnSearchKeyDown(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && _list.VirtualListSize > 0)
            {
                _list.Focus();
                int idx = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
                _list.SelectedIndices.Clear();
                _list.SelectedIndices.Add(idx);
                _list.EnsureVisible(idx);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                Accept();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void OnListKeyDown(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)  { Accept();  e.Handled = true; }
            if (e.KeyCode == Keys.Escape) { Close();   e.Handled = true; }
        }

        // ── Accept ────────────────────────────────────────────────────────
        private void Accept()
        {
            if (_list.SelectedIndices.Count == 0) return;
            int idx = _list.SelectedIndices[0];
            if (idx < 0 || idx >= _filtered.Count) return;
            SelectedAddress = _filtered[idx].Address;
            DialogResult = DialogResult.OK;
            Close();
        }

        public void ApplyThemeColors(Color formBack, Color formFore, Color windowBack, Color windowFore)
        {
            BackColor = formBack;
            ForeColor = formFore;
            _windowBack = windowBack;
            _windowFore = windowFore;
            _search.BackColor = windowBack;
            _search.ForeColor = windowFore;
            _list.BackColor = windowBack;
            _list.ForeColor = windowFore;
            _countLabel.ForeColor = formFore;
            _cbLabelsOnly.ForeColor = formFore;

            bool dark = windowBack.GetBrightness() < 0.45f;
            _selBack = dark ? Color.FromArgb(64, 72, 84) : Color.FromArgb(0, 0, 128);
            _selFore = Color.White;
            _headerBack = dark ? Color.FromArgb(44, 48, 54) : SystemColors.Control;
            _headerFore = dark ? Color.FromArgb(232, 232, 232) : SystemColors.ControlText;
            _headerBorder = dark ? Color.FromArgb(82, 86, 94) : SystemColors.ControlDark;
            _list.HeaderBackColor = _headerBack;
            _list.HeaderBorderColor = _headerBorder;

            foreach (Control c in Controls)
            {
                if (c is Button btn)
                {
                    Color buttonBack = Color.FromArgb(formBack.A,
                        Math.Min(255, (int)Math.Round(formBack.R * 1.20f)),
                        Math.Min(255, (int)Math.Round(formBack.G * 1.20f)),
                        Math.Min(255, (int)Math.Round(formBack.B * 1.20f)));
                    btn.BackColor = buttonBack;
                    btn.ForeColor = formFore;
                    btn.FlatStyle = FlatStyle.Flat;
                    btn.FlatAppearance.BorderColor = windowFore;
                    btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(buttonBack.A,
                        Math.Min(255, (int)Math.Round(buttonBack.R * 1.20f)),
                        Math.Min(255, (int)Math.Round(buttonBack.G * 1.20f)),
                        Math.Min(255, (int)Math.Round(buttonBack.B * 1.20f)));
                }
                if (c is Label lbl && lbl != _countLabel)
                    lbl.ForeColor = formFore;
            }
            _list.Invalidate();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Edit Line Attributes dialog
    // ══════════════════════════════════════════════════════════════════════

    internal sealed class EditLineDialog : Form
    {
        public string NewLabel   { get; private set; } = "";
        public string NewComment { get; private set; } = "";
        public uint?  NewWord    { get; private set; }

        private readonly TextBox           _tbData;
        private readonly TextBox           _tbLabel;
        private readonly TextBox           _tbComment;
        private readonly TextBox           _tbCommand;
        private readonly MipsDisassemblerEx _disasm;
        private readonly uint              _pc;
        private readonly uint              _origWord;
        private          bool              _updating;

        public EditLineDialog(DisassemblyRow row, string? currentLabel,
                              string? currentComment, MipsDisassemblerEx disasm)
        {
            _disasm   = disasm;
            _pc       = row.Address;
            _origWord = row.Word;

            Text            = "Edit Line Attributes";
            Size            = new Size(470, 210);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterParent;
            Font            = new Font("Tahoma", 8.25f);

            var mono = new Font("Courier New", 9f);

            // ── Row 1: Address / Data / OK / Cancel ───────────────────────
            Controls.Add(new Label { Text = "Address", Location = new Point(8, 14), AutoSize = true });
            var tbAddr = new TextBox
            {
                Text      = row.Address.ToString("X8"),
                Location  = new Point(62, 11), Width = 82, Font = mono,
                ReadOnly  = true, BackColor = SystemColors.Control,
                TabStop   = false,
            };
            Controls.Add(tbAddr);

            Controls.Add(new Label { Text = "Data", Location = new Point(154, 14), AutoSize = true });
            _tbData = new TextBox
            {
                Text     = row.Word.ToString("X8"),
                Location = new Point(184, 11), Width = 82, Font = mono,
            };
            _tbData.Leave    += OnDataLeave;
            _tbData.KeyDown  += (_, e) => { if (e.KeyCode == Keys.Enter) { OnDataLeave(null, EventArgs.Empty); } };
            Controls.Add(_tbData);

            var ok  = new Button { Text = "OK",     Location = new Point(368, 9),  Width = 80, Height = 26 };
            var can = new Button { Text = "Cancel",  Location = new Point(368, 39), Width = 80, Height = 26,
                                   DialogResult = DialogResult.Cancel };
            ok.Click += OnOk;
            AcceptButton = ok; CancelButton = can;
            Controls.Add(ok); Controls.Add(can);

            // ── Row 2: Label ──────────────────────────────────────────────
            Controls.Add(new Label { Text = "Label", Location = new Point(8, 44), AutoSize = true });
            _tbLabel = new TextBox
            {
                Text     = currentLabel ?? "",
                Location = new Point(62, 41), Width = 290, Font = mono,
            };
            Controls.Add(_tbLabel);

            // ── Row 3: Comment ────────────────────────────────────────────
            Controls.Add(new Label { Text = "Comment", Location = new Point(8, 72), AutoSize = true });
            _tbComment = new TextBox
            {
                Text     = currentComment ?? "",
                Location = new Point(62, 69), Width = 290, Font = mono,
            };
            Controls.Add(_tbComment);

            // ── Row 4: Command ────────────────────────────────────────────
            // Re-disassemble with empty label map so operands use raw addresses (not label names)
            // that the assembler can round-trip back to machine code.
            var cleanRow = disasm.DisassembleSingleWord(row.Word, row.Address);
            string commandText = row.Mnemonic switch
            {
                ".word"  => row.Word.ToString("X8"),
                ".half"  => row.Operands,
                ".byte"  => row.Operands,
                ".float" => row.Operands,
                _        => $"{cleanRow.Mnemonic} {cleanRow.Operands}".Trim(),
            };

            Controls.Add(new Label { Text = "Command", Location = new Point(8, 100), AutoSize = true });
            _tbCommand = new TextBox
            {
                Text     = commandText,
                Location = new Point(62, 97), Width = 290, Font = mono,
            };
            _tbCommand.TextChanged += OnCommandChanged;
            Controls.Add(_tbCommand);
        }

        private void OnDataLeave(object? s, EventArgs e)
        {
            if (_updating) return;
            string hex = _tbData.Text.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint word)) return;
            _updating = true;
            var row = _disasm.DisassembleSingleWord(word, _pc);
            _tbCommand.Text = $"{row.Mnemonic} {row.Operands}".Trim();
            _tbCommand.BackColor = SystemColors.Window; // clear any red — hex edit is always valid
            _updating = false;
        }

        private void OnCommandChanged(object? s, EventArgs e)
        {
            if (_updating) return;
            uint? word;
            if (_tbCommand.Text == _origWord.ToString("X8") || _tbCommand.Text == BitConverter.Int32BitsToSingle(unchecked((int)_origWord)).ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture))
                word = _origWord;
            else
                word = MipsAssembler.Assemble(_tbCommand.Text, _pc);
            if (!word.HasValue)
            {
                _tbCommand.BackColor = Color.FromArgb(255, 200, 200); // red: instruction not assembleable
                return;
            }
            _tbCommand.BackColor = SystemColors.Window; // restore: assembled OK
            _updating = true;
            _tbData.Text = word.Value.ToString("X8");
            _updating = false;
        }

        private void OnOk(object? s, EventArgs e)
        {
            // Flush Command → Data in case focus hasn't left the command box yet
            OnCommandChanged(null, EventArgs.Empty);
            string hex = _tbData.Text.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint word))
            {
                MessageBox.Show("Data must be a valid hex value.", "ps2dis",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            NewLabel   = _tbLabel.Text.Trim();
            NewComment = _tbComment.Text.Trim();
            NewWord    = word != _origWord ? word : (uint?)null;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // GameShark / Code Manager dialog
    // ══════════════════════════════════════════════════════════════════════

    internal sealed class CodeToolsDialog : Form
    {
        // ── Codes / inject tab fields ─────────────────────────────────────
        private readonly Func<uint, int, byte[]?>                               _read;
        private readonly Action<IReadOnlyList<(uint Addr, byte[] Bytes)>>        _applyLocal;
        private readonly Func<IReadOnlyList<(uint Addr, byte[] Bytes)>, string?> _writePcsx2;
        private readonly RichTextBox _tbCodes;
        private readonly RichTextBox _tbInject;
        private Label? _lblEnterCodes;
        private int _constantWriteRateDisplay = AppSettings.DefaultConstantWriteRate;
        private readonly System.Windows.Forms.Timer _activeCodesTimer;
        private string _activeCodesText = string.Empty;
        private bool _darkEditorScrollbars = true;

        // ── Search tab fields ────────────────────────────────────────────
        private readonly Func<(byte[]? Ram, string? Err)> _readEeRam;
        private readonly Action<uint>                     _navigateToCenter;
        private readonly TextBox  _tbValue;
        private readonly MainForm.FlatComboBox _cbValueType;
        private readonly MainForm.FlatComboBox _cbScanType;
        private readonly Button   _btnFirstScan;
        private readonly Button   _btnNextScan;
        private readonly Button   _btnNewScan;
        private readonly VirtualDisasmList _lvResults;
        private readonly List<SearchResultRow> _resultRows = new();
        private readonly Label    _lblStatus;
        private readonly Label    _lblValue;
        private readonly TextBox  _tbRangeStart;
        private readonly TextBox  _tbRangeEnd;
        private readonly MainForm.FlatTabPage  _tpSearch;
        private CancellationTokenSource? _cts;
        private List<uint>? _searchAddresses;
        private byte[]?     _snapshotRam;
        private bool _scanActive;

        private sealed class SearchResultRow
        {
            public uint Addr;
            public string Value = "";
            public string Previous = "";
        }

        private readonly MainForm.FlatTabHost _tabs;

        public CodeToolsDialog(
            Func<uint, int, byte[]?> read,
            Action<IReadOnlyList<(uint Addr, byte[] Bytes)>> applyLocal,
            Func<IReadOnlyList<(uint Addr, byte[] Bytes)>, string?> writePcsx2,
            Func<(byte[]?, string?)> readEeRam,
            Action<uint> navigateToCenter)
        {
            _read       = read;
            _applyLocal = applyLocal;
            _writePcsx2 = writePcsx2;
            _readEeRam  = readEeRam;
            _navigateToCenter = navigateToCenter;

            Text            = "Code Manager";
            ClientSize      = new Size(720, 520);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterParent;
            MinimumSize     = new Size(520, 420);

            var mono = new Font("Courier New", 9f);
            const int pad = 4;
            const int btnW = 100;
            const int btnH = 28;

            _tabs = new MainForm.FlatTabHost
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.FromArgb(24, 27, 31),
                ForeColor = Color.FromArgb(232, 232, 232),
                TabStripHeight = 24,
                TabButtonHeight = 24,
                TabStripTopPadding = 0,
                TabButtonWidth = 120,
                Tag = "CodeManagerTabHost"
            };
            Padding = new Padding(1);
            Controls.Add(_tabs);
            _tabs.TabStripBackColorOverride = Color.FromArgb(24, 27, 31);
            _tabs.ContentBackColorOverride = Color.FromArgb(24, 27, 31);
            _tabs.DimTabs = true;
            _tabs.ApplyPalette(true, Color.FromArgb(24, 27, 31), Color.FromArgb(232, 232, 232));

            var tpCodes = new MainForm.FlatTabPage("Codes") { Padding = new Padding(0), Margin = new Padding(0), Tag = "CodeManagerPage" };
            _tabs.AddPage(tpCodes);
            var codesSurface = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0), Margin = new Padding(0), Tag = "CodeManagerSurface", BackColor = Color.FromArgb(24, 27, 31) };
            tpCodes.Controls.Add(codesSurface);
            _lblEnterCodes = new Label
            {
                Text = $"Enter Codes (writing codes {_constantWriteRateDisplay}x per second)",
                Location = new Point(pad, pad),
                AutoSize = true
            };
            codesSurface.Controls.Add(_lblEnterCodes);
            const int rtbPad = 4; // visual padding around the RichTextBox
            _tbCodes = new RichTextBox
            {
                Location = new Point(pad + rtbPad, pad + 20 + rtbPad),
                Size = new Size(Math.Max(120, tpCodes.ClientSize.Width - (pad * 2) - (rtbPad * 2)), Math.Max(80, tpCodes.ClientSize.Height - (pad + 20) - btnH - (pad * 2) - (rtbPad * 2))),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = mono,
                BackColor = Color.FromArgb(44, 48, 54),
                ForeColor = Color.FromArgb(232, 232, 232),
                WordWrap = false,
                AcceptsTab = true,
                BorderStyle = BorderStyle.None,
                DetectUrls = false
            };
            codesSurface.Controls.Add(_tbCodes);
            _tbCodes.ContextMenuStrip = CreateEditorContextMenu(_tbCodes);
            _tbCodes.HandleCreated += (_, _) =>
            {
                NativeMethods.SetRichTextBoxPadding(_tbCodes, 5);
                ApplyEditorScrollbarTheme(_tbCodes);
            };
            _tbCodes.Resize += (_, _) => NativeMethods.SetRichTextBoxPadding(_tbCodes, 5);
            var btnUpdateCodes = new Button
            {
                Text = "Update",
                Size = new Size(btnW, btnH),
                Location = new Point(pad, tpCodes.ClientSize.Height - btnH - pad),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            btnUpdateCodes.Click += (_, _) => UpdateActiveCodes(showMessage: false);
            codesSurface.Controls.Add(btnUpdateCodes);
            var lblCodesSupport = new Label
            {
                Text = "Supported code types: 0, 1, 2, D",
                Location = new Point(pad + btnW + 8, tpCodes.ClientSize.Height - btnH - pad + (btnH - 15) / 2),
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            codesSurface.Controls.Add(lblCodesSupport);

            var tpInject = new MainForm.FlatTabPage("Patch Memory") { Padding = new Padding(0), Margin = new Padding(0), Tag = "CodeManagerPage" };
            _tabs.AddPage(tpInject);
            var injectSurface = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0), Margin = new Padding(0), Tag = "CodeManagerSurface", BackColor = Color.FromArgb(24, 27, 31) };
            tpInject.Controls.Add(injectSurface);
            injectSurface.Controls.Add(new Label { Text = "Enter Codes", Location = new Point(pad, pad), AutoSize = true });
            _tbInject = new RichTextBox
            {
                Location = new Point(pad + rtbPad, pad + 20 + rtbPad),
                Size = new Size(Math.Max(120, tpInject.ClientSize.Width - (pad * 2) - (rtbPad * 2)), Math.Max(80, tpInject.ClientSize.Height - (pad + 20) - btnH - (pad * 2) - (rtbPad * 2))),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = mono,
                BackColor = Color.FromArgb(44, 48, 54),
                ForeColor = Color.FromArgb(232, 232, 232),
                WordWrap = false,
                AcceptsTab = true,
                BorderStyle = BorderStyle.None,
                DetectUrls = false
            };
            injectSurface.Controls.Add(_tbInject);
            _tbInject.ContextMenuStrip = CreateEditorContextMenu(_tbInject);
            _tbInject.HandleCreated += (_, _) =>
            {
                NativeMethods.SetRichTextBoxPadding(_tbInject, 5);
                ApplyEditorScrollbarTheme(_tbInject);
            };
            _tbInject.Resize += (_, _) => NativeMethods.SetRichTextBoxPadding(_tbInject, 5);
            var btnUpdateInject = new Button
            {
                Text = "Update",
                Size = new Size(btnW, btnH),
                Location = new Point(pad, tpInject.ClientSize.Height - btnH - pad),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            btnUpdateInject.Click += (_, _) => ApplyCodes(_tbInject, clearAfterApply: true, probeReadable: false);
            injectSurface.Controls.Add(btnUpdateInject);
            var lblInjectSupport = new Label
            {
                Text = "Supported code types: 0, 1, 2",
                Location = new Point(pad + btnW + 8, tpInject.ClientSize.Height - btnH - pad + (btnH - 15) / 2),
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            injectSurface.Controls.Add(lblInjectSupport);

            _activeCodesTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _activeCodesTimer.Tick += (_, _) =>
            {
                if (!IsDisposed && !string.IsNullOrWhiteSpace(_activeCodesText))
                    ApplyActiveCodes(showMessage: false);
            };
            _activeCodesTimer.Start();

            _tpSearch = new MainForm.FlatTabPage("Search") { Padding = new Padding(0), Margin = new Padding(0), Tag = "CodeManagerPage" };
            var tpSearch = _tpSearch;
            _tabs.AddPage(tpSearch);
            var searchSurface = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0), Margin = new Padding(0), Tag = "CodeManagerSurface", BackColor = Color.FromArgb(24, 27, 31) };
            tpSearch.Controls.Add(searchSurface);

            // Row 1: Value Type + Scan Type
            searchSurface.Controls.Add(new Label { Text = "Value Type", Location = new Point(pad, pad), AutoSize = true });
            _cbValueType = new MainForm.FlatComboBox
            {
                Location = new Point(pad, pad + 16),
                Size = new Size(140, 22),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cbValueType.Items.AddRange(new object[] { "Byte", "2 Bytes", "4 Bytes", "Float", "String", "Hex Pattern" });
            _cbValueType.SelectedIndex = 2;
            searchSurface.Controls.Add(_cbValueType);

            searchSurface.Controls.Add(new Label { Text = "Scan Type", Location = new Point(pad + 152, pad), AutoSize = true });
            _cbScanType = new MainForm.FlatComboBox
            {
                Location = new Point(pad + 152, pad + 16),
                Size = new Size(152, 22),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cbScanType.Items.AddRange(new object[] { "Exact Value", "Unknown Initial", "Changed", "Unchanged", "Increased", "Decreased" });
            _cbScanType.SelectedIndex = 0;
            searchSurface.Controls.Add(_cbScanType);

            // Memory Range
            searchSurface.Controls.Add(new Label { Text = "Memory Range", Location = new Point(pad + 316, pad), AutoSize = true });
            _tbRangeStart = new TextBox
            {
                Text = "00000000",
                Location = new Point(pad + 316, pad + 16),
                Size = new Size(80, 22),
                MaxLength = 8,
                CharacterCasing = CharacterCasing.Upper
            };
            _tbRangeStart.KeyPress += MemoryRangeHexKeyPress;
            _tbRangeStart.Leave += (_, _) => ClampMemoryRangeBox(_tbRangeStart);
            searchSurface.Controls.Add(_tbRangeStart);
            _tbRangeEnd = new TextBox
            {
                Text = "20000000",
                Location = new Point(pad + 316 + 84, pad + 16),
                Size = new Size(80, 22),
                MaxLength = 8,
                CharacterCasing = CharacterCasing.Upper
            };
            _tbRangeEnd.KeyPress += MemoryRangeHexKeyPress;
            _tbRangeEnd.Leave += (_, _) => ClampMemoryRangeBox(_tbRangeEnd);
            searchSurface.Controls.Add(_tbRangeEnd);

            // Row 2: Value input
            _lblValue = new Label { Text = "Value", Location = new Point(pad, pad + 42), AutoSize = true };
            searchSurface.Controls.Add(_lblValue);
            _tbValue = new TextBox
            {
                Location = new Point(pad, pad + 58),
                Size = new Size(312, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _tbValue.KeyDown += (_, ke) => { if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; OnScanClick(null, EventArgs.Empty); } };
            searchSurface.Controls.Add(_tbValue);

            // Row 3: Buttons (tight to value row)
            _btnFirstScan = new Button { Text = "First Scan", Size = new Size(100, btnH), Location = new Point(pad, pad + 84) };
            _btnFirstScan.Click += OnScanClick;
            searchSurface.Controls.Add(_btnFirstScan);

            _btnNextScan = new Button { Text = "Next Scan", Size = new Size(100, btnH), Location = new Point(pad + 106, pad + 84), Enabled = false };
            _btnNextScan.Click += OnScanClick;
            searchSurface.Controls.Add(_btnNextScan);

            _btnNewScan = new Button { Text = "New Scan", Size = new Size(100, btnH), Location = new Point(pad + 212, pad + 84), Enabled = false };
            _btnNewScan.Click += (_, _) => ResetSearch();
            searchSurface.Controls.Add(_btnNewScan);

            // Results list (tight to buttons)
            int resultsListTop = pad + 116;
            searchSurface.Controls.Add(new Label { Text = "Results", Location = new Point(pad, resultsListTop - 16), AutoSize = true });

            var resultsCtx = new ContextMenuStrip();
            resultsCtx.Items.Add("Freeze", null, (_, _) => FreezeSelectedResult());
            resultsCtx.Items.Add("Go to in Disassembler", null, (_, _) => GoToSelectedResult());
            _lvResults = new VirtualDisasmList
            {
                Location = new Point(pad, resultsListTop),
                Size = new Size(Math.Max(120, tpSearch.ClientSize.Width - (pad * 2)), Math.Max(80, tpSearch.ClientSize.Height - resultsListTop - 28 - pad)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = mono,
                BackColor = Color.FromArgb(30, 33, 38),
                ForeColor = Color.FromArgb(232, 232, 232),
                HeaderHeight = 20,
                RowHeight = 18,
                FullRowSelect = true,
                OwnerDraw = true,
                VirtualMode = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _lvResults.Columns.Add("Address", 84);
            _lvResults.Columns.Add("Value", 120);
            _lvResults.RetrieveVirtualItem += OnSearchResultsRetrieveVirtualItem;
            _lvResults.DrawHeader += OnSearchResultsDrawHeader;
            _lvResults.DrawCell += OnSearchResultsDrawCell;
            _lvResults.MouseDoubleClick += OnResultDoubleClick;
            _lvResults.Resize += (_, _) => ResizeResultsColumns();
            _lvResults.ContextMenuStrip = resultsCtx;
            _lvResults.MouseUp += (_, me) =>
            {
                if (me.Button == MouseButtons.Right)
                {
                    var hit = _lvResults.HitTest(new Point(me.X, me.Y));
                    if (hit?.Item != null)
                        _lvResults.SelectedIndex = hit.Item.Index;
                }
            };
            searchSurface.Controls.Add(_lvResults);

            _lblStatus = new Label
            {
                Text = "Enter a value and click First Scan.",
                Location = new Point(pad, tpSearch.ClientSize.Height - 28 - pad),
                Size = new Size(Math.Max(120, tpSearch.ClientSize.Width - (pad * 2)), 28),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            searchSurface.Controls.Add(_lblStatus);

            tpSearch.Resize += (_, _) => LayoutSearchTab();
            LayoutSearchTab();
            _cbScanType.SelectedIndexChanged += (_, _) => UpdateScanTypeUI();
            _cbValueType.SelectedIndexChanged += (_, _) => UpdateScanTypeUI();
            UpdateScanTypeUI();

            FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        public void ApplyEditorScrollbarTheme(bool dark)
        {
            _darkEditorScrollbars = dark;
            ApplyEditorScrollbarTheme(_tbCodes);
            ApplyEditorScrollbarTheme(_tbInject);
        }

        private void ApplyEditorScrollbarTheme(RichTextBox editor)
        {
            if (editor == null || !editor.IsHandleCreated)
                return;

            try
            {
                string theme = _darkEditorScrollbars ? "DarkMode_Explorer" : "";
                NativeMethods.SetWindowTheme(editor.Handle, theme, null);
                NativeMethods.SendMessage(editor.Handle, NativeMethods.WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
                NativeMethods.RedrawWindow(editor.Handle, IntPtr.Zero, IntPtr.Zero,
                    NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_FRAME | NativeMethods.RDW_ALLCHILDREN);
            }
            catch { }
        }

        private static ContextMenuStrip CreateEditorContextMenu(RichTextBox editor)
        {
            var menu = new ContextMenuStrip();
            var miCopy = new ToolStripMenuItem("Copy", null, (_, _) =>
            {
                if (editor.SelectionLength > 0)
                    editor.Copy();
            });
            var miPaste = new ToolStripMenuItem("Paste", null, (_, _) =>
            {
                if (Clipboard.ContainsText())
                    editor.Paste();
            });

            menu.Items.Add(miCopy);
            menu.Items.Add(miPaste);
            menu.Opening += (_, _) =>
            {
                miCopy.Enabled = editor.SelectionLength > 0;
                miPaste.Enabled = Clipboard.ContainsText();
            };
            return menu;
        }

        public void SelectTab(int index)
        {
            // When embedded in the main window the form itself is never shown as a window;
            // just switch the inner tab directly.
            if (index >= 0 && index < _tabs.Pages.Count)
                _tabs.SelectedIndex = index;
        }

        /// <summary>Returns the inner custom tab host so it can be hosted directly inside the main window's Code Manager tab panel.</summary>
        public Control GetInnerTabHost() => _tabs;

        public void SetCodesText(string text)
        {
            _tbCodes.Text = text ?? string.Empty;
        }

        public void ActivateCodesSilently()
        {
            UpdateActiveCodes(showMessage: false);
        }

        public void AppendCodeLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (_tbCodes.TextLength > 0 && !_tbCodes.Text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                _tbCodes.AppendText(Environment.NewLine);
            _tbCodes.AppendText(line.Trim());
        }

        public string GetCodesText() => _tbCodes.Text;

        private void UpdateActiveCodes(bool showMessage)
        {
            _activeCodesText = _tbCodes.Text;
            ApplyActiveCodes(showMessage);
        }

        private void ApplyActiveCodes(bool showMessage)
        {
            var patches = ParseCodesText(_activeCodesText, out int applied, out int skipped, probeReadable: true);
            ApplyParsedPatches(patches, clearEditorAfterApply: false, editorToClear: null, showMessage: showMessage, applied: applied, skipped: skipped);
        }

        private void ApplyCodes(TextBoxBase source, bool clearAfterApply, bool showMessage = true, bool probeReadable = true)
        {
            ApplyCodesText(source.Text, clearAfterApply, source, showMessage, probeReadable);
        }

        private void ApplyCodesText(string sourceText, bool clearEditorAfterApply, TextBoxBase? editorToClear, bool showMessage = true, bool probeReadable = true)
        {
            var patches = ParseCodesText(sourceText, out int applied, out int skipped, probeReadable);
            ApplyParsedPatches(patches, clearEditorAfterApply, editorToClear, showMessage, applied, skipped);
        }

        private List<(uint Addr, byte[] Bytes)> ParseCodesText(string sourceText, out int applied, out int skipped, bool probeReadable = true)
        {
            var patches = new List<(uint Addr, byte[] Bytes)>();
            applied = 0;
            skipped = 0;

            (uint CondAddr, uint CondValue)? blockCondition = null;

            foreach (var rawLine in sourceText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();

                int hashIdx = line.IndexOf('#');
                if (hashIdx >= 0)
                    line = line[..hashIdx].TrimEnd();

                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith(";"))
                {
                    blockCondition = null;
                    continue;
                }

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    blockCondition = null;
                    continue;
                }

                bool addrOk = uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint rawAddr);
                bool valueOk = uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out uint value);
                if (!addrOk || !valueOk)
                {
                    blockCondition = null;
                    skipped++;
                    continue;
                }

                int typeNibble = (int)(rawAddr >> 28);
                if (typeNibble == 0xD)
                {
                    uint condAddr = rawAddr & 0x0FFFFFFFu;
                    blockCondition = (condAddr, value);
                    continue;
                }

                uint ps2Addr;
                byte[] newBytes;

                if (typeNibble is 0 or 1 or 2)
                {
                    ps2Addr = rawAddr & 0x0FFFFFFFu;
                    newBytes = typeNibble switch
                    {
                        0 => new[] { (byte)(value & 0xFF) },
                        1 => new[] { (byte)(value & 0xFF), (byte)(value >> 8) },
                        _ => new[] { (byte)(value & 0xFF), (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) }
                    };
                }
                else
                {
                    ps2Addr = rawAddr;
                    newBytes = new[] { (byte)(value & 0xFF), (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
                }

                if (probeReadable && _read(ps2Addr, newBytes.Length) == null)
                {
                    skipped++;
                    continue;
                }

                if (blockCondition.HasValue)
                {
                    var (condAddr, condValue) = blockCondition.Value;
                    var condBytes = _read(condAddr, 4);
                    if (condBytes == null || condBytes.Length < 4)
                        continue;

                    uint actual = (uint)(condBytes[0] | (condBytes[1] << 8) | (condBytes[2] << 16) | (condBytes[3] << 24));
                    if (actual != condValue)
                        continue;
                }

                patches.Add((ps2Addr, newBytes));
                applied++;
            }

            return patches;
        }

        private void ApplyParsedPatches(IReadOnlyList<(uint Addr, byte[] Bytes)> patches, bool clearEditorAfterApply, TextBoxBase? editorToClear, bool showMessage = true, int applied = 0, int skipped = 0)
        {
            if (patches.Count > 0)
            {
                _applyLocal(patches);
                string? pcsx2Err = _writePcsx2(patches);
                if (pcsx2Err != null && showMessage)
                    MessageBox.Show($"Local disassembly updated, but PCSX2 write failed:\r\n{pcsx2Err}",
                        "GameShark Tools", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (clearEditorAfterApply && editorToClear != null)
                    editorToClear.Clear();
            }

            if (showMessage && (applied > 0 || skipped > 0))
                MessageBox.Show($"Applied {applied} code(s), skipped {skipped} invalid line(s).",
                    "GameShark Tools", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void LayoutSearchTab()
        {
            if (_tpSearch == null || _lvResults == null || _lblStatus == null) return;
            const int pad = 12;
            int statusHeight = 28;
            int statusTop = Math.Max(200, _tpSearch.ClientSize.Height - statusHeight - pad);
            _lblStatus.Location = new Point(pad, statusTop);
            _lblStatus.Size = new Size(Math.Max(120, _tpSearch.ClientSize.Width - (pad * 2)), statusHeight);

            int resultsTop = pad + 116;
            int resultsBottom = Math.Max(resultsTop + 80, statusTop - 8);
            _lvResults.Location = new Point(pad, resultsTop);
            _lvResults.Size = new Size(Math.Max(120, _tpSearch.ClientSize.Width - (pad * 2)), Math.Max(80, resultsBottom - resultsTop));
            ResizeResultsColumns();
        }

        private void ResizeResultsColumns()
        {
            if (_lvResults == null || _lvResults.Columns.Count < 2) return;
            int addrWidth = _lvResults.Columns[0].Width;
            int scrollbarW = _resultRows.Count > _lvResults.VisibleRowCapacity ? SystemInformation.VerticalScrollBarWidth : 0;
            int remaining = Math.Max(120, _lvResults.ClientSize.Width - addrWidth - scrollbarW - 2);
            _lvResults.Columns[1].Width = remaining;
            _lvResults.Invalidate();
        }

        private void UpdateScanTypeUI()
        {
            string scanType = _cbScanType.SelectedItem?.ToString() ?? "";
            bool needsValue = scanType is "Exact Value";
            _tbValue.Visible = needsValue;
            _lblValue.Visible = needsValue;
            if (!_scanActive)
            {
                _btnFirstScan.Enabled = true;
                _btnNextScan.Enabled = false;
            }
        }

        private void ResetSearch()
        {
            _cts?.Cancel();
            _tbValue.Text = string.Empty;
            _resultRows.Clear();
            _lvResults.VirtualListSize = 0;
            _lvResults.Invalidate();
            _searchAddresses = null;
            _snapshotRam = null;
            _scanActive = false;
            _btnFirstScan.Enabled = true;
            _btnNextScan.Enabled = false;
            _btnNewScan.Enabled = false;
            _cbValueType.Enabled = true;
            _lblStatus.Text = "Enter a value and click First Scan.";
        }

        private int GetValueByteCount()
        {
            string vt = _cbValueType.SelectedItem?.ToString() ?? "4 Bytes";
            return vt switch { "Byte" => 1, "2 Bytes" => 2, "4 Bytes" => 4, "Float" => 4, _ => 1 };
        }

        private int GetAlignmentStep()
        {
            string vt = _cbValueType.SelectedItem?.ToString() ?? "4 Bytes";
            return vt switch { "Byte" => 1, "2 Bytes" => 2, "4 Bytes" => 4, "Float" => 4, _ => 1 };
        }

        private void OnSearchResultsRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _resultRows.Count)
            { e.Item = new ListViewItem(string.Empty); return; }
            var row = _resultRows[e.ItemIndex];
            var item = new ListViewItem(row.Addr == uint.MaxValue ? "..." : row.Addr.ToString("X8"));
            item.SubItems.Add(row.Value);
            e.Item = item;
        }

        private void OnSearchResultsDrawHeader(object? sender, VirtualDisasmList.VirtualHeaderPaintEventArgs e)
        {
            using var b = new SolidBrush(_lvResults.HeaderBackColor);
            e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, Rectangle.Inflate(e.Bounds, -4, 0), _lvResults.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void OnSearchResultsDrawCell(object? sender, VirtualDisasmList.VirtualCellPaintEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _resultRows.Count) return;
            var row = _resultRows[e.ItemIndex];
            bool dark = _lvResults.BackColor.GetBrightness() < 0.45f;
            Color back = e.Selected
                ? (dark ? Color.FromArgb(58, 74, 98) : Color.FromArgb(0, 0, 128))
                : _lvResults.BackColor;
            Color fore = e.Selected
                ? Color.White
                : _lvResults.ForeColor;
            using var b = new SolidBrush(back);
            e.Graphics.FillRectangle(b, e.Bounds);
            string text = e.ColumnIndex switch { 0 => row.Addr == uint.MaxValue ? "..." : row.Addr.ToString("X8"), _ => row.Value };
            TextRenderer.DrawText(e.Graphics, text, _lvResults.Font, Rectangle.Inflate(e.Bounds, -4, 0), fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private async void OnScanClick(object? sender, EventArgs e)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            bool isFirstScan = !_scanActive;
            string scanType = _cbScanType.SelectedItem?.ToString() ?? "Exact Value";
            string valueType = _cbValueType.SelectedItem?.ToString() ?? "4 Bytes";
            int byteCount = GetValueByteCount();
            int step = GetAlignmentStep();

            _btnFirstScan.Enabled = false;
            _btnNextScan.Enabled = false;
            _resultRows.Clear();
            _lvResults.VirtualListSize = 0;

            try
            {
                _lblStatus.Text = "Reading PCSX2 memory\u2026";
                var (ram, errMsg) = await Task.Run(_readEeRam, token);
                if (token.IsCancellationRequested) return;
                if (ram == null) { _lblStatus.Text = errMsg ?? "Failed to read PCSX2 memory."; return; }

                List<uint> results;

                if (isFirstScan)
                {
                    int rStart = (int)GetSearchRangeStart();
                    int rEnd = (int)GetSearchRangeEnd();

                    if (scanType == "Unknown Initial")
                    {
                        _snapshotRam = ram;
                        int limit = Math.Min(rEnd, ram.Length - byteCount + 1);
                        int start = Math.Max(0, rStart);
                        if (start % step != 0) start += step - (start % step);
                        results = await Task.Run(() =>
                        {
                            var list = new List<uint>((limit - start) / step);
                            for (int i = start; i < limit; i += step) { if (token.IsCancellationRequested) break; list.Add((uint)i); }
                            return list;
                        }, token);
                        if (token.IsCancellationRequested) return;
                    }
                    else if (scanType == "Exact Value")
                    {
                        var search = BuildSearchPattern(_tbValue.Text.Trim(), valueType, out string? parseErr);
                        if (search == null) { _lblStatus.Text = parseErr ?? "Invalid value."; return; }
                        var (pattern, mask, searchStep) = search.Value;
                        _snapshotRam = ram;
                        _lblStatus.Text = $"Searching {ram.Length / 1024 / 1024} MB\u2026";
                        results = await Task.Run(() => SearchBytes(ram, pattern, mask, searchStep, token, rStart, rEnd), token);
                        if (token.IsCancellationRequested) return;
                    }
                    else { _lblStatus.Text = "Use 'Exact Value' or 'Unknown Initial' for the first scan."; return; }

                    _searchAddresses = results;
                    _scanActive = true;
                    _btnFirstScan.Enabled = false;
                    _btnNextScan.Enabled = true;
                    _btnNewScan.Enabled = true;
                    _cbValueType.Enabled = false;
                }
                else
                {
                    if (_searchAddresses == null || _snapshotRam == null) { _lblStatus.Text = "No previous scan data. Click 'New Scan' first."; return; }
                    var prev = _searchAddresses;
                    var snap = _snapshotRam;

                    if (scanType == "Exact Value")
                    {
                        var search = BuildSearchPattern(_tbValue.Text.Trim(), valueType, out string? parseErr);
                        if (search == null) { _lblStatus.Text = parseErr ?? "Invalid value."; return; }
                        var (pattern, mask, _) = search.Value;
                        _lblStatus.Text = $"Refining {prev.Count:N0} address(es)\u2026";
                        results = await Task.Run(() => FilterAddresses(ram, prev, pattern, mask, token), token);
                    }
                    else
                    {
                        int bc = byteCount; string mode = scanType;
                        _lblStatus.Text = $"Comparing {prev.Count:N0} address(es)\u2026";
                        results = await Task.Run(() =>
                        {
                            var list = new List<uint>(prev.Count / 4);
                            int limit2 = Math.Min(ram.Length, snap.Length) - bc;
                            foreach (uint addr in prev)
                            {
                                if (token.IsCancellationRequested) break;
                                if ((int)addr > limit2) continue;
                                bool match;
                                if (mode is "Increased" or "Decreased")
                                {
                                    long curVal = 0, snapVal = 0;
                                    for (int j = bc - 1; j >= 0; j--) { curVal = (curVal << 8) | ram[(int)addr + j]; snapVal = (snapVal << 8) | snap[(int)addr + j]; }
                                    match = mode == "Increased" ? curVal > snapVal : curVal < snapVal;
                                }
                                else
                                {
                                    bool same = true;
                                    for (int j = 0; j < bc; j++) if (ram[(int)addr + j] != snap[(int)addr + j]) { same = false; break; }
                                    match = mode == "Changed" ? !same : same;
                                }
                                if (match) list.Add(addr);
                            }
                            return list;
                        }, token);
                    }
                    if (token.IsCancellationRequested) return;
                    _searchAddresses = results;
                    _snapshotRam = ram;
                    _btnNextScan.Enabled = true;
                }

                int show = Math.Min(results.Count, 5000);
                for (int i = 0; i < show; i++)
                {
                    uint addr = results[i];
                    string curVal = FormatResultValue(ram, (int)addr, valueType, byteCount);
                    string prevVal = (!isFirstScan && _snapshotRam != null && _snapshotRam != ram) ? FormatResultValue(_snapshotRam, (int)addr, valueType, byteCount) : "";
                    if (isFirstScan) prevVal = "";
                    _resultRows.Add(new SearchResultRow { Addr = addr, Value = curVal, Previous = prevVal });
                }
                if (results.Count > show)
                    _resultRows.Add(new SearchResultRow { Addr = uint.MaxValue, Value = $"({results.Count - show:N0} more)", Previous = "" });
                _lvResults.VirtualListSize = _resultRows.Count;
                ResizeResultsColumns();
                _lblStatus.Text = $"{results.Count:N0} result(s) found.";
            }
            catch (OperationCanceledException) { _lblStatus.Text = "Search cancelled."; }
            catch (Exception ex) { _lblStatus.Text = $"Error: {ex.Message}"; }
            finally
            {
                if (!_scanActive) _btnFirstScan.Enabled = true;
                else _btnNextScan.Enabled = true;
            }
        }

        private string FormatResultValue(byte[] ram, int index, string valueType, int byteCount)
        {
            if (index < 0 || index + byteCount > ram.Length) return "";
            try
            {
                return valueType switch
                {
                    "Float" when index + 4 <= ram.Length => BitConverter.ToSingle(ram, index).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                    "String" => ReadStringAt(ram, index, 32),
                    "Hex Pattern" => BitConverter.ToString(ram, index, Math.Min(16, ram.Length - index)).Replace("-", " "),
                    "Byte" => ram[index].ToString("X2"),
                    "2 Bytes" when index + 2 <= ram.Length => BitConverter.ToUInt16(ram, index).ToString("X4"),
                    "4 Bytes" when index + 4 <= ram.Length => BitConverter.ToUInt32(ram, index).ToString("X8"),
                    _ => BitConverter.ToString(ram, index, Math.Min(byteCount, ram.Length - index)).Replace("-", " ")
                };
            }
            catch { return ""; }
        }

        private static string ReadStringAt(byte[] ram, int index, int maxLen)
        {
            int end = Math.Min(index + maxLen, ram.Length);
            int len = 0;
            for (int i = index; i < end; i++) { if (ram[i] == 0) break; len++; }
            return len == 0 ? "(empty)" : Encoding.UTF8.GetString(ram, index, len);
        }

        private (byte[] Pattern, byte[] Mask, int Step)? BuildSearchPattern(string value, string valueType, out string? error)
        {
            error = null;
            byte[] MakeExact(byte[] b) { var m = new byte[b.Length]; Array.Fill(m, (byte)0xFF); return m; }

            switch (valueType)
            {
                case "Byte":
                    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    { if (byte.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out byte bv)) return (new[] { bv }, new byte[] { 0xFF }, 1); }
                    else if (byte.TryParse(value, out byte bv2)) return (new[] { bv2 }, new byte[] { 0xFF }, 1);
                    else if (byte.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out byte bv3)) return (new[] { bv3 }, new byte[] { 0xFF }, 1);
                    error = "Invalid byte value (0-255 or hex)."; return null;
                case "2 Bytes":
                {
                    ushort sv;
                    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    { if (!ushort.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out sv)) { error = "Invalid 2-byte value."; return null; } }
                    else if (!ushort.TryParse(value, out sv) && !ushort.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out sv))
                    { error = "Invalid 2-byte value."; return null; }
                    var b = BitConverter.GetBytes(sv); return (b, MakeExact(b), 2);
                }
                case "4 Bytes":
                {
                    uint iv;
                    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    { if (!uint.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out iv)) { error = "Invalid 4-byte value."; return null; } }
                    else if (int.TryParse(value, out int signed)) iv = unchecked((uint)signed);
                    else if (!uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out iv))
                    { error = "Invalid 4-byte value (decimal or hex)."; return null; }
                    var b = BitConverter.GetBytes(iv); return (b, MakeExact(b), 4);
                }
                case "Float":
                    if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fv))
                    { error = "Invalid float value."; return null; }
                    { var b = BitConverter.GetBytes(fv); return (b, MakeExact(b), 4); }
                case "String":
                    if (value.Length == 0) { error = "Enter a string to search for."; return null; }
                    { var sb = Encoding.UTF8.GetBytes(value); return (sb, MakeExact(sb), 1); }
                case "Hex Pattern":
                {
                    string[] tokens;
                    var trimmed = value.Trim();
                    if (trimmed.Contains(' ')) tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    else
                    {
                        if (trimmed.Length == 0) { error = "Enter hex bytes (e.g. DE AD ?? BE EF)."; return null; }
                        if (trimmed.Length % 2 != 0) { error = "Hex must have even number of digits."; return null; }
                        tokens = new string[trimmed.Length / 2];
                        for (int i = 0; i < tokens.Length; i++) tokens[i] = trimmed.Substring(i * 2, 2);
                    }
                    if (tokens.Length == 0) { error = "Empty pattern."; return null; }
                    var pb = new byte[tokens.Length]; var pm = new byte[tokens.Length];
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        if (tokens[i] is "??" or "?" or "**") { pb[i] = 0x00; pm[i] = 0x00; }
                        else if (byte.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out pb[i])) { pm[i] = 0xFF; }
                        else { error = $"Invalid token \"{tokens[i]}\" at position {i}."; return null; }
                    }
                    return (pb, pm, 1);
                }
                default: error = "Unknown value type."; return null;
            }
        }

        private static void MemoryRangeHexKeyPress(object? sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\b') return;
            if (!Uri.IsHexDigit(e.KeyChar)) { e.Handled = true; }
        }

        private static void ClampMemoryRangeBox(TextBox tb)
        {
            if (uint.TryParse(tb.Text, System.Globalization.NumberStyles.HexNumber, null, out uint val))
            {
                if (val > 0x02000000) val = 0x02000000;
                tb.Text = val.ToString("X8");
            }
            else
                tb.Text = "00000000";
        }

        private uint GetSearchRangeStart()
        {
            if (uint.TryParse(_tbRangeStart.Text, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                return Math.Min(v, 0x02000000);
            return 0;
        }

        private uint GetSearchRangeEnd()
        {
            if (uint.TryParse(_tbRangeEnd.Text, System.Globalization.NumberStyles.HexNumber, null, out uint v))
                return Math.Min(v, 0x02000000);
            return 0x02000000;
        }

        private static List<uint> SearchBytes(byte[] ram, byte[] pattern, byte[] mask, int step, CancellationToken token, int rangeStart = 0, int rangeEnd = -1)
        {
            var results = new List<uint>();
            if (rangeEnd < 0) rangeEnd = ram.Length;
            int limit = Math.Min(rangeEnd, ram.Length - pattern.Length + 1);
            int start = Math.Max(0, rangeStart);
            // Align start to step
            if (start % step != 0) start += step - (start % step);
            for (int i = start; i < limit; i += step)
            {
                if (token.IsCancellationRequested) break;
                bool match = true;
                for (int j = 0; j < pattern.Length; j++) if ((ram[i + j] & mask[j]) != (pattern[j] & mask[j])) { match = false; break; }
                if (match) results.Add((uint)i);
            }
            return results;
        }

        private static List<uint> FilterAddresses(byte[] ram, List<uint> candidates, byte[] pattern, byte[] mask, CancellationToken token)
        {
            var results = new List<uint>();
            int limit = ram.Length - pattern.Length;
            foreach (uint addr in candidates)
            {
                if (token.IsCancellationRequested) break;
                if ((int)addr > limit) continue;
                bool match = true;
                for (int j = 0; j < pattern.Length; j++) if ((ram[(int)addr + j] & mask[j]) != (pattern[j] & mask[j])) { match = false; break; }
                if (match) results.Add(addr);
            }
            return results;
        }

        private void OnResultDoubleClick(object? sender, EventArgs e) => GoToSelectedResult();

        private void GoToSelectedResult()
        {
            int idx = _lvResults.SelectedIndex;
            if (idx < 0 || idx >= _resultRows.Count) return;
            uint addr = _resultRows[idx].Addr;
            if (addr == uint.MaxValue) return;
            _navigateToCenter(addr);
        }

        private void FreezeSelectedResult()
        {
            int idx = _lvResults.SelectedIndex;
            if (idx < 0 || idx >= _resultRows.Count) return;
            var row = _resultRows[idx];
            if (row.Addr == uint.MaxValue) return;
            string addrHex = row.Addr.ToString("X8");
            string freezeAddr = $"2{addrHex[1..]}";

            string valueType = _cbValueType.SelectedItem?.ToString() ?? "4 Bytes";
            string cleanVal;
            if (valueType == "Float" && float.TryParse(row.Value, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture, out float fv))
            {
                // Convert displayed float back to IEEE 754 hex for cheat device
                cleanVal = BitConverter.SingleToInt32Bits(fv).ToString("X8");
            }
            else
            {
                cleanVal = row.Value.Replace(" ", "");
                if (cleanVal.Length > 8) cleanVal = cleanVal[..8];
                cleanVal = cleanVal.PadLeft(8, '0');
            }

            AppendCodeLine($"{freezeAddr} {cleanVal}");
            ActivateCodesSilently();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
        }

        /// <summary>
        /// Updates the active codes timer interval based on the constant write rate setting.
        /// </summary>
        public void SetConstantWriteRate(int writesPerSecond)
        {
            if (writesPerSecond < 1) writesPerSecond = 1;
            int intervalMs = 1000 / writesPerSecond;
            if (_activeCodesTimer != null)
                _activeCodesTimer.Interval = Math.Max(1, intervalMs);
            _constantWriteRateDisplay = writesPerSecond;
            if (_lblEnterCodes != null && !_lblEnterCodes.IsDisposed)
                _lblEnterCodes.Text = $"Enter Codes (writing codes {writesPerSecond}x per second)";
        }

        /// <summary>
        /// Clears the Codes text box and stops any active code patches from being written.
        /// </summary>
        public void ClearActiveCodesAndStop()
        {
            _activeCodesText = string.Empty;
            _tbCodes.Clear();
        }
    }

    internal enum FindMode { String, HexPattern }

    internal sealed class FindDialog : Form
    {
        private readonly TextBox _txtSearch;
        private readonly RadioButton _rbString;
        private readonly RadioButton _rbHex;
        private readonly CheckBox _chkCaseSensitive;
        private readonly CheckBox _chkWrapAround;

        public event EventHandler? FindNext;

        public string SearchText => _txtSearch.Text.Trim();
        public FindMode Mode => _rbHex.Checked ? FindMode.HexPattern : FindMode.String;
        public bool CaseSensitive => _chkCaseSensitive.Checked;
        public bool WrapAround => _chkWrapAround.Checked;

        public void FocusSearchBox()
        {
            if (!_txtSearch.IsDisposed)
            {
                ActiveControl = _txtSearch;
                _txtSearch.Focus();
                _txtSearch.SelectAll();
            }
        }

        public FindDialog()
        {
            Text = "Find";
            ClientSize = new Size(410, 160);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            Font = new Font("Tahoma", 8.25f);

            var lblSearch = new Label { Text = "Search:", Location = new Point(12, 12), AutoSize = true };
            Controls.Add(lblSearch);

            _txtSearch = new TextBox
            {
                Location = new Point(12, 30),
                Width = 386,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _txtSearch.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    FindNext?.Invoke(this, EventArgs.Empty);
                }
            };
            Controls.Add(_txtSearch);

            var gbMode = new GroupBox
            {
                Text = "Search Mode",
                Location = new Point(12, 60),
                Size = new Size(190, 56),
            };

            _rbString = new RadioButton { Text = "String", Location = new Point(12, 18), AutoSize = true, Checked = true };
            _rbHex = new RadioButton { Text = "Hex Pattern", Location = new Point(12, 36), AutoSize = true };
            gbMode.Controls.Add(_rbString);
            gbMode.Controls.Add(_rbHex);
            Controls.Add(gbMode);

            _rbString.CheckedChanged += (_, _) => _chkCaseSensitive.Enabled = _rbString.Checked;

            _chkCaseSensitive = new MainForm.ThemedCheckBox { Text = "Case sensitive", Location = new Point(214, 78), AutoSize = true };
            _chkWrapAround = new MainForm.ThemedCheckBox { Text = "Wrap around", Location = new Point(214, 98), AutoSize = true, Checked = true };
            Controls.Add(_chkCaseSensitive);
            Controls.Add(_chkWrapAround);

            var btnFind = new Button
            {
                Text = "Find Next",
                Location = new Point(310, 128),
                Width = 88,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
            };
            btnFind.Click += (_, _) => FindNext?.Invoke(this, EventArgs.Empty);
            Controls.Add(btnFind);

            AcceptButton = btnFind;

            Shown += (_, _) => FocusSearchBox();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    internal sealed class OptionsDialog : Form
    {
        private readonly MainForm.FlatComboBox _cbFont;
        private readonly MainForm.FlatComboBox _cbFontSize;
        private readonly MainForm.FlatComboBox _cbTheme;
        private readonly MainForm.FlatComboBox _cbRowSpacing;
        private readonly MainForm.FlatComboBox _cbRefreshRate;
        private readonly MainForm.FlatComboBox _cbConstantWriteRate;
        private readonly CheckBox _chkShowMemoryView;
        private readonly CheckBox _chkShowTabsInTitleBar;
        private readonly TextBox _tbDebugHost;
        private readonly TextBox _tbPinePort;
        private readonly TextBox _tbMcpPort;

        /// <summary>Raised when the user clicks Apply so the caller can read properties and act.</summary>
        public event EventHandler? ApplyRequested;

        public string SelectedFontFamily => _cbFont.SelectedItem?.ToString() ?? AppSettings.DefaultFontFamily;
        public float SelectedFontSize => float.TryParse(_cbFontSize.SelectedItem?.ToString() ?? _cbFontSize.Text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) && v >= 6f && v <= 30f ? v : AppSettings.DefaultFontSize;
        public string SelectedTheme => _cbTheme.SelectedItem?.ToString() ?? AppSettings.DefaultTheme;
        public string SelectedRowSpacing => AppSettings.NormalizeRowSpacing(_cbRowSpacing.SelectedItem?.ToString() ?? _cbRowSpacing.Text);
        public int SelectedRefreshRate => int.TryParse(_cbRefreshRate.SelectedItem?.ToString(), out int v) && AppSettings.IsSupportedRefreshRate(v)
            ? v
            : AppSettings.DefaultRefreshRate;
        public int SelectedConstantWriteRate => int.TryParse(_cbConstantWriteRate.SelectedItem?.ToString(), out int v) && AppSettings.IsSupportedConstantWriteRate(v)
            ? v
            : AppSettings.DefaultConstantWriteRate;
        public bool SelectedShowMemoryView => _chkShowMemoryView.Checked;
        public bool SelectedShowTabsInTitleBar => _chkShowTabsInTitleBar.Checked;
        public string SelectedDebugHost => AppSettings.NormalizeDebugHost(_tbDebugHost.Text);
        public int SelectedPinePort => ParsePort(_tbPinePort.Text, AppSettings.DefaultPinePort);
        public int SelectedMcpPort => ParsePort(_tbMcpPort.Text, AppSettings.DefaultMcpPort);

        public OptionsDialog(AppSettings settings, bool dark)
        {
            const int optionsTabCount = 3;
            const int optionsTabButtonWidth = 118;
            const int optionsButtonStripHeight = 42;
            const int optionsTabStripHeight = 24;
            const int optionsContentBottomPadding = 12;
            const int optionsContentHeight = 190 + 2 + 20 + optionsContentBottomPadding;
            Size optionsClientSize = new Size(optionsTabCount * optionsTabButtonWidth, optionsTabStripHeight + optionsContentHeight + optionsButtonStripHeight);

            Text = $"Options - Version {AppSettings.AppVersion}";
            ClientSize = optionsClientSize;
            Tag = "OptionsDialog";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimumSize = SizeFromClientSize(optionsClientSize);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Tahoma", 8.25f);
            KeyPreview = true;

            SuspendLayout();

            const int innerLabelX = 16;
            const int innerControlX = 126;
            const int innerControlW = 172;
            const int rowH = 34;
            const int topRowY = 20;

            const int buttonStripHeight = 42;
            const int buttonBottomMargin = 8;
            const int buttonGap = 8;

            var tabs = new MainForm.FlatTabHost
            {
                Location = new Point(0, 0),
                Size = new Size(ClientSize.Width, ClientSize.Height - buttonStripHeight),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                TabStop = true,
                TabStripTopPadding = 0,
                TabButtonWidth = optionsTabButtonWidth,
                Margin = new Padding(0),
                Tag = "OptionsTabHost",
            };

            var tabUi = new MainForm.FlatTabPage("Interface") { Tag = "OptionsTabPage" };
            var tabPcsx2 = new MainForm.FlatTabPage("PCSX2") { Tag = "OptionsTabPage" };
            var tabDebug = new MainForm.FlatTabPage("DEBUG") { Tag = "OptionsTabPage" };

            var pnlUi = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0), AutoScroll = true, Tag = "OptionsSurface" };
            pnlUi.Controls.Add(new Label { Text = "Font:", Location = new Point(innerLabelX, topRowY + 3), AutoSize = true });
            _cbFont = new MainForm.FlatComboBox
            {
                Location = new Point(innerControlX, topRowY),
                Width = innerControlW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                TabStop = true,
            };
            string[] monoFonts = {
                "Anonymous Pro", "Cascadia Code", "Cascadia Mono", "Consolas",
                "Courier New", "DejaVu Sans Mono", "Droid Sans Mono",
                "Fantasque Sans Mono", "Fira Code", "Fira Mono",
                "Go Mono", "Hack", "Hasklig", "IBM Plex Mono",
                "Inconsolata", "Input Mono", "Iosevka", "Iosevka Term",
                "JetBrains Mono", "Julia Mono", "Liberation Mono",
                "Lucida Console", "Menlo", "Meslo LG M", "Monaco",
                "Monaspace Neon", "Noto Sans Mono", "Operator Mono",
                "Overpass Mono", "PT Mono", "Pragmata Pro",
                "Red Hat Mono", "Roboto Mono", "SF Mono",
                "Source Code Pro", "Space Mono", "Sudo",
                "Ubuntu Mono", "Victor Mono", "Zed Mono"
            };
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ff in System.Drawing.FontFamily.Families)
                installed.Add(ff.Name);
            foreach (string name in monoFonts)
                if (installed.Contains(name))
                    _cbFont.Items.Add(name);
            if (!string.IsNullOrWhiteSpace(settings.FontFamily) &&
                _cbFont.Items.Cast<object>().All(item => !string.Equals(item?.ToString(), settings.FontFamily, StringComparison.OrdinalIgnoreCase)))
            {
                _cbFont.Items.Insert(0, settings.FontFamily);
            }
            SelectComboItem(_cbFont, settings.FontFamily, AppSettings.DefaultFontFamily);
            pnlUi.Controls.Add(_cbFont);

            int fontSizeRowY = topRowY + rowH;
            pnlUi.Controls.Add(new Label { Text = "Font Size:", Location = new Point(innerLabelX, fontSizeRowY + 3), AutoSize = true });
            _cbFontSize = new MainForm.FlatComboBox
            {
                Location = new Point(innerControlX, fontSizeRowY),
                Width = innerControlW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                TabStop = true,
            };
            string[] sizes = { "7", "8", "8.5", "9", "9.5", "10", "10.5", "11", "12", "13", "14", "16", "18", "20" };
            _cbFontSize.Items.AddRange(sizes);
            string sizeText = settings.FontSize.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            if (sizeText.EndsWith(".0", StringComparison.Ordinal))
                sizeText = sizeText[..^2];
            SelectComboItem(_cbFontSize, sizeText, AppSettings.DefaultFontSize.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            pnlUi.Controls.Add(_cbFontSize);

            int themeRowY = fontSizeRowY + rowH;
            pnlUi.Controls.Add(new Label { Text = "Theme:", Location = new Point(innerLabelX, themeRowY + 3), AutoSize = true });
            _cbTheme = new MainForm.FlatComboBox
            {
                Location = new Point(innerControlX, themeRowY),
                Width = innerControlW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                TabStop = true,
            };
            _cbTheme.Items.AddRange(new object[] { "Dark", "Light" });
            SelectComboItem(_cbTheme, settings.Theme, AppSettings.DefaultTheme);
            pnlUi.Controls.Add(_cbTheme);

            int rowSpacingRowY = themeRowY + rowH;
            pnlUi.Controls.Add(new Label { Text = "Row Spacing:", Location = new Point(innerLabelX, rowSpacingRowY + 3), AutoSize = true });
            _cbRowSpacing = new MainForm.FlatComboBox
            {
                Location = new Point(innerControlX, rowSpacingRowY),
                Width = innerControlW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                TabStop = true,
            };
            foreach (string spacing in AppSettings.SupportedRowSpacings)
                _cbRowSpacing.Items.Add(spacing);
            SelectComboItem(_cbRowSpacing, AppSettings.NormalizeRowSpacing(settings.RowSpacing), AppSettings.DefaultRowSpacing);
            pnlUi.Controls.Add(_cbRowSpacing);

            int showMemoryRowY = rowSpacingRowY + rowH;
            _chkShowMemoryView = new MainForm.ThemedCheckBox
            {
                Text = "Show Memory View",
                Location = new Point(innerLabelX, showMemoryRowY + 2),
                AutoSize = true,
                Checked = settings.ShowMemoryView,
                TabStop = true,
            };
            pnlUi.Controls.Add(_chkShowMemoryView);

            int showTabsInTitleBarRowY = showMemoryRowY + rowH;
            _chkShowTabsInTitleBar = new MainForm.ThemedCheckBox
            {
                Text = "Show Tabs in Title Bar",
                Location = new Point(innerLabelX, showTabsInTitleBarRowY + 2),
                AutoSize = true,
                Checked = settings.ShowTabsInTitleBar,
                TabStop = true,
            };
            pnlUi.Controls.Add(_chkShowTabsInTitleBar);
            tabUi.Controls.Add(pnlUi);

            var pnlPcsx2 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0), AutoScroll = true, Tag = "OptionsSurface" };
            pnlPcsx2.Controls.Add(new Label { Text = "Refresh Rate:", Location = new Point(innerLabelX, topRowY + 3), AutoSize = true });
            _cbRefreshRate = new MainForm.FlatComboBox
            {
                Location = new Point(innerControlX, topRowY),
                Width = innerControlW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                TabStop = true,
            };
            foreach (int refreshRate in AppSettings.SupportedRefreshRates)
                _cbRefreshRate.Items.Add(refreshRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SelectComboItem(_cbRefreshRate,
                (AppSettings.IsSupportedRefreshRate(settings.RefreshRate) ? settings.RefreshRate : AppSettings.DefaultRefreshRate).ToString(System.Globalization.CultureInfo.InvariantCulture),
                AppSettings.DefaultRefreshRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            pnlPcsx2.Controls.Add(_cbRefreshRate);

            int writeRateRowY = topRowY + rowH;
            pnlPcsx2.Controls.Add(new Label { Text = "Constant Write Rate:", Location = new Point(innerLabelX, writeRateRowY + 3), AutoSize = true });
            _cbConstantWriteRate = new MainForm.FlatComboBox
            {
                Location = new Point(innerControlX, writeRateRowY),
                Width = innerControlW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                TabStop = true,
            };
            foreach (int rate in AppSettings.SupportedConstantWriteRates)
                _cbConstantWriteRate.Items.Add(rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SelectComboItem(_cbConstantWriteRate,
                (AppSettings.IsSupportedConstantWriteRate(settings.ConstantWriteRate) ? settings.ConstantWriteRate : AppSettings.DefaultConstantWriteRate).ToString(System.Globalization.CultureInfo.InvariantCulture),
                AppSettings.DefaultConstantWriteRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            pnlPcsx2.Controls.Add(_cbConstantWriteRate);
            tabPcsx2.Controls.Add(pnlPcsx2);

            var pnlDebug = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0), AutoScroll = true, Tag = "OptionsSurface" };
            pnlDebug.Controls.Add(new Label { Text = "Host:", Location = new Point(innerLabelX, topRowY + 3), AutoSize = true });
            _tbDebugHost = new TextBox
            {
                Location = new Point(innerControlX, topRowY),
                Width = innerControlW,
                Text = AppSettings.NormalizeDebugHost(settings.DebugHost),
                TabStop = true,
            };
            pnlDebug.Controls.Add(_tbDebugHost);

            int pinePortRowY = topRowY + rowH;
            pnlDebug.Controls.Add(new Label { Text = "PINE Port:", Location = new Point(innerLabelX, pinePortRowY + 3), AutoSize = true });
            _tbPinePort = new TextBox
            {
                Location = new Point(innerControlX, pinePortRowY),
                Width = innerControlW,
                Text = AppSettings.NormalizePort(settings.PinePort, AppSettings.DefaultPinePort).ToString(System.Globalization.CultureInfo.InvariantCulture),
                TabStop = true,
            };
            pnlDebug.Controls.Add(_tbPinePort);

            int mcpPortRowY = pinePortRowY + rowH;
            pnlDebug.Controls.Add(new Label { Text = "MCP Port:", Location = new Point(innerLabelX, mcpPortRowY + 3), AutoSize = true });
            _tbMcpPort = new TextBox
            {
                Location = new Point(innerControlX, mcpPortRowY),
                Width = innerControlW,
                Text = AppSettings.NormalizePort(settings.McpPort, AppSettings.DefaultMcpPort).ToString(System.Globalization.CultureInfo.InvariantCulture),
                TabStop = true,
            };
            pnlDebug.Controls.Add(_tbMcpPort);
            tabDebug.Controls.Add(pnlDebug);

            tabs.AddPage(tabUi);
            tabs.AddPage(tabPcsx2);
            tabs.AddPage(tabDebug);
            Controls.Add(tabs);

            int buttonsY = ClientSize.Height - buttonBottomMargin - 28;
            var btnReset = new Button
            {
                Text = "Reset to Defaults",
                Location = new Point(8, buttonsY),
                Width = 120,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
            };
            btnReset.Click += (_, _) =>
            {
                SelectComboItem(_cbFont, AppSettings.DefaultFontFamily, AppSettings.DefaultFontFamily);
                SelectComboItem(_cbFontSize,
                    AppSettings.DefaultFontSize.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    AppSettings.DefaultFontSize.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                SelectComboItem(_cbTheme, AppSettings.DefaultTheme, AppSettings.DefaultTheme);
                SelectComboItem(_cbRowSpacing, AppSettings.DefaultRowSpacing, AppSettings.DefaultRowSpacing);
                SelectComboItem(_cbRefreshRate,
                    AppSettings.DefaultRefreshRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AppSettings.DefaultRefreshRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SelectComboItem(_cbConstantWriteRate,
                    AppSettings.DefaultConstantWriteRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AppSettings.DefaultConstantWriteRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _chkShowMemoryView.Checked = AppSettings.DefaultShowMemoryView;
                _chkShowTabsInTitleBar.Checked = AppSettings.DefaultShowTabsInTitleBar;
                _tbDebugHost.Text = AppSettings.DefaultDebugHost;
                _tbPinePort.Text = AppSettings.DefaultPinePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _tbMcpPort.Text = AppSettings.DefaultMcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                tabs.SelectedIndex = 0;
            };
            Controls.Add(btnReset);

            var btnCancel = new Button
            {
                Text = "Close",
                Location = new Point(ClientSize.Width - 8 - 78, buttonsY),
                Width = 78,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };
            Controls.Add(btnCancel);

            var btnOk = new Button
            {
                Text = "Apply",
                Location = new Point(ClientSize.Width - 8 - 78 - buttonGap - 78, buttonsY),
                Width = 78,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };
            btnOk.Click += (_, _) => ApplyRequested?.Invoke(this, EventArgs.Empty);
            Controls.Add(btnOk);

            CancelButton = btnCancel;
            Shown += (_, _) => ActiveControl = tabs;
            ResumeLayout(false);
        }

        private static int ParsePort(string? text, int fallback)
        {
            if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
                return AppSettings.NormalizePort(value, fallback);

            return fallback;
        }

        private static void SelectComboItem(ComboBox comboBox, string? preferredText, string fallbackText)
        {
            string target = string.IsNullOrWhiteSpace(preferredText) ? fallbackText : preferredText.Trim();
            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (string.Equals(comboBox.Items[i]?.ToString(), target, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }

            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (string.Equals(comboBox.Items[i]?.ToString(), fallbackText, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }

            if (comboBox.Items.Count > 0)
                comboBox.SelectedIndex = 0;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
