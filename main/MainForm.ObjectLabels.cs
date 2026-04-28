using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PS2Disassembler
{
    internal sealed class ObjectLabelField
    {
        public uint Offset { get; set; }
        public string Label { get; set; } = string.Empty;

        public ObjectLabelField Clone()
            => new ObjectLabelField { Offset = Offset, Label = Label };
    }

    internal sealed class ObjectLabelDefinition
    {
        public uint StaticAddress { get; set; }
        public string Label { get; set; } = string.Empty;
        public string RawText { get; set; } = string.Empty;
        public List<ObjectLabelField> Fields { get; } = new List<ObjectLabelField>();

        public ObjectLabelDefinition Clone()
        {
            var clone = new ObjectLabelDefinition
            {
                StaticAddress = StaticAddress,
                Label = Label,
                RawText = RawText,
            };
            foreach (var field in Fields)
                clone.Fields.Add(field.Clone());
            return clone;
        }
    }

    internal sealed class ObjectLabelUpdateResult
    {
        public int DefinitionCount { get; set; }
        public int AppliedObjectCount { get; set; }
        public int TempLabelCount { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }

    internal static class ObjectLabelDefinitionParser
    {
        private static readonly Regex HeaderRegex = new Regex(@"^\s*([0-9A-Fa-f]{1,8})\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex FieldRegex = new Regex(@"^\s*([0-9A-Fa-f]{1,8})\s*(?::|--)\s*(.*?)\s*$", RegexOptions.Compiled);

        public static ObjectLabelDefinition Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Object definition is empty.");

            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                                 .Replace('\r', '\n')
                                 .Split('\n');

            ObjectLabelDefinition? definition = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string stripped = StripComment(lines[i]).TrimEnd();
                if (string.IsNullOrWhiteSpace(stripped))
                    continue;

                if (definition == null)
                {
                    Match headerMatch = HeaderRegex.Match(stripped);
                    if (!headerMatch.Success)
                        throw new InvalidOperationException($"Line {i + 1}: expected ADDRESS:Label.");

                    definition = new ObjectLabelDefinition
                    {
                        StaticAddress = ParseHex32(headerMatch.Groups[1].Value),
                        Label = headerMatch.Groups[2].Value.Trim(),
                    };

                    if (string.IsNullOrWhiteSpace(definition.Label))
                        throw new InvalidOperationException($"Line {i + 1}: object label cannot be empty.");

                    continue;
                }

                Match fieldMatch = FieldRegex.Match(stripped);
                if (!fieldMatch.Success)
                    throw new InvalidOperationException($"Line {i + 1}: expected OFFSET:Label.");

                string label = fieldMatch.Groups[2].Value.Trim();
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                definition.Fields.Add(new ObjectLabelField
                {
                    Offset = ParseHex32(fieldMatch.Groups[1].Value),
                    Label = label,
                });
            }

            if (definition == null)
                throw new InvalidOperationException("Object definition is empty.");

            definition.RawText = BuildNormalizedDefinitionText(definition);
            return definition;
        }

        private static uint ParseHex32(string text)
        {
            if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint value))
            {
                throw new InvalidOperationException($"Invalid hex value '{text}'.");
            }
            return value;
        }

        private static string StripComment(string line)
        {
            int idx = line.IndexOf('#');
            return idx >= 0 ? line.Substring(0, idx) : line;
        }

        public static string BuildNormalizedDefinitionText(ObjectLabelDefinition definition)
            => BuildCompactDefinitionText(definition);

        public static string BuildCompactDefinitionText(ObjectLabelDefinition definition)
        {
            var sb = new StringBuilder();
            string staticAddressText = definition.StaticAddress <= 0xFFFFFF
                ? definition.StaticAddress.ToString("X")
                : definition.StaticAddress.ToString("X8");
            sb.AppendLine($"{staticAddressText}:{definition.Label?.Trim() ?? string.Empty}");
            foreach (ObjectLabelField field in definition.Fields
                .Where(x => !string.IsNullOrWhiteSpace(x.Label))
                .OrderBy(x => x.Offset))
            {
                string offsetText = field.Offset <= 0xFFFF
                    ? field.Offset.ToString("X")
                    : field.Offset.ToString("X8");
                sb.AppendLine($"{offsetText}:{field.Label.Trim()}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string BuildEditorDefinitionText(ObjectLabelDefinition definition)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{definition.StaticAddress:X8}:{definition.Label?.Trim() ?? string.Empty}");
            foreach (ObjectLabelField field in definition.Fields
                .Where(x => !string.IsNullOrWhiteSpace(x.Label))
                .OrderBy(x => x.Offset))
            {
                string offsetText = field.Offset <= 0xFFFF ? field.Offset.ToString("X4") : field.Offset.ToString("X8");
                sb.AppendLine($"{offsetText}:{field.Label.Trim()}");
            }
            return sb.ToString().TrimEnd();
        }
    }

    public sealed partial class MainForm
    {
        private List<ObjectLabelDefinition> _objectLabelDefinitions = new List<ObjectLabelDefinition>();
        private Dictionary<uint, string> _objectTempLabels = new Dictionary<uint, string>();
        private readonly Dictionary<uint, uint?> _objectLabelPointerSnapshot = new Dictionary<uint, uint?>();
        private System.Windows.Forms.Timer? _objectLabelLiveTimer;
        private bool _objectLabelsAvailableFromProject;
        private int _labelsWindowSelectedTabIndex;

        private bool ShouldShowObjectLabelsTab() => IsLiveAttached() || _objectLabelsAvailableFromProject;

        private List<ObjectLabelDefinition> CloneObjectLabelDefinitions(IEnumerable<ObjectLabelDefinition> source)
            => source.Select(x => x.Clone()).ToList();

        private void ClearObjectLabelState(bool clearDefinitions)
        {
            _objectTempLabels = new Dictionary<uint, string>();
            _objectLabelPointerSnapshot.Clear();
            _objectLabelLiveTimer?.Stop();
            if (clearDefinitions)
            {
                _objectLabelDefinitions = new List<ObjectLabelDefinition>();
                _objectLabelsAvailableFromProject = false;
            }
        }

        private void ClearGeneratedObjectTempLabels()
        {
            if (_objectTempLabels.Count == 0)
                return;

            _objectTempLabels = new Dictionary<uint, string>();
            RefreshObjectLabelDisplays();
        }

        private static bool IsValidObjectLabelPointer(uint staticAddress, uint objectAddress)
        {
            return objectAddress != 0
                && objectAddress != staticAddress
                && ShouldDereferenceAnnotationAddress(objectAddress);
        }

        private bool TryReadTrackedObjectPointer(ObjectLabelDefinition definition, out uint? objectAddress)
        {
            objectAddress = null;
            if (definition == null)
                return false;

            if (!TryReadWordAt(definition.StaticAddress, out uint value))
                return false;

            objectAddress = value;
            return true;
        }

        private bool TryGetActiveObjectRuntimeAddress(ObjectLabelDefinition definition, out uint objectAddress)
        {
            objectAddress = 0;
            if (!TryReadTrackedObjectPointer(definition, out uint? rawPointer) || !rawPointer.HasValue)
                return false;

            uint value = rawPointer.Value;
            if (!IsValidObjectLabelPointer(definition.StaticAddress, value))
                return false;

            objectAddress = value;
            return true;
        }

        private void RefreshObjectLabelPointerSnapshot(IEnumerable<ObjectLabelDefinition> definitions)
        {
            _objectLabelPointerSnapshot.Clear();
            foreach (ObjectLabelDefinition definition in definitions)
            {
                if (TryReadTrackedObjectPointer(definition, out uint? objectAddress))
                    _objectLabelPointerSnapshot[definition.StaticAddress] = objectAddress;
                else
                    _objectLabelPointerSnapshot[definition.StaticAddress] = null;
            }
        }

        private void EnsureObjectLabelLiveTimerState()
        {
            bool shouldRun = IsLiveAttached() && _objectLabelDefinitions.Count > 0;
            if (!shouldRun)
            {
                _objectLabelLiveTimer?.Stop();
                _objectLabelPointerSnapshot.Clear();
                return;
            }

            if (_objectLabelLiveTimer == null)
            {
                _objectLabelLiveTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _objectLabelLiveTimer.Tick += (_, _) => OnObjectLabelLiveTimerTick();
            }

            if (_objectLabelPointerSnapshot.Count == 0)
                RefreshObjectLabelPointerSnapshot(_objectLabelDefinitions);

            if (!_objectLabelLiveTimer.Enabled)
                _objectLabelLiveTimer.Start();
        }

        private void OnObjectLabelLiveTimerTick()
        {
            if (!IsLiveAttached() || _objectLabelDefinitions.Count == 0)
            {
                EnsureObjectLabelLiveTimerState();
                return;
            }

            bool changed = false;
            var activeStaticAddresses = new HashSet<uint>(_objectLabelDefinitions.Select(x => x.StaticAddress));
            foreach (uint staleAddress in _objectLabelPointerSnapshot.Keys.Where(x => !activeStaticAddresses.Contains(x)).ToList())
            {
                _objectLabelPointerSnapshot.Remove(staleAddress);
                changed = true;
            }

            foreach (ObjectLabelDefinition definition in _objectLabelDefinitions)
            {
                uint? currentPointer = null;
                if (TryReadTrackedObjectPointer(definition, out uint? objectAddress))
                    currentPointer = objectAddress;

                if (!_objectLabelPointerSnapshot.TryGetValue(definition.StaticAddress, out uint? previousPointer) || previousPointer != currentPointer)
                {
                    _objectLabelPointerSnapshot[definition.StaticAddress] = currentPointer;
                    changed = true;
                }
            }

            if (changed)
                ApplyObjectLabelDefinitions(_objectLabelDefinitions, showDialogs: false);
        }

        private void RefreshObjectLabelDisplays()
        {
            if (_disasmList != null)
            {
                int top = Math.Max(0, _disasmList.TopIndex);
                int bottom = Math.Min(Math.Max(0, _disasmList.VirtualListSize - 1), top + _disasmList.VisibleRowCapacity - 1);
                if (bottom >= top)
                    _disasmList.RedrawItems(top, bottom, invalidateOnly: false);
                else
                    _disasmList.Invalidate();
            }

            _hexList?.Invalidate();
            _asciiBytesBar?.Invalidate();
        }

        private void ConvertObjectDefinitionsToWordData(IEnumerable<ObjectLabelDefinition> definitions)
        {
            foreach (ObjectLabelDefinition definition in definitions)
                ConvertAddressToWordData(definition.StaticAddress);
        }

        private ObjectLabelUpdateResult ApplyObjectLabelDefinitions(IReadOnlyList<ObjectLabelDefinition> definitions, bool showDialogs)
        {
            ConvertObjectDefinitionsToWordData(definitions);
            ClearGeneratedObjectTempLabels();

            var result = new ObjectLabelUpdateResult
            {
                DefinitionCount = definitions.Count,
            };

            var tempLabels = new Dictionary<uint, string>();

            if ((_fileData == null || _fileData.Length == 0) && !IsLiveAttached())
            {
                _objectTempLabels = tempLabels;
                RefreshObjectLabelDisplays();
                result.Errors.Add("No binary or live EE memory is currently loaded.");
                if (showDialogs)
                {
                    MessageBox.Show(this,
                        "No binary or live EE memory is currently loaded.",
                        "Object Labels",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                return result;
            }

            int tempLabelCount = 0;

            foreach (var definition in definitions)
            {
                if (!TryReadTrackedObjectPointer(definition, out uint? objectAddressValue) || !objectAddressValue.HasValue)
                {
                    result.Errors.Add($"{definition.StaticAddress:X8}:{definition.Label} - could not read the object pointer.");
                    continue;
                }

                uint objectAddress = objectAddressValue.Value;
                if (!IsValidObjectLabelPointer(definition.StaticAddress, objectAddress))
                {
                    result.Errors.Add($"{definition.StaticAddress:X8}:{definition.Label} - pointer {objectAddress:X8} is not a valid populated address.");
                    continue;
                }

                result.AppliedObjectCount++;
                AddObjectTempLabel(tempLabels, objectAddress, definition.Label, ref tempLabelCount);

                foreach (var field in definition.Fields)
                {
                    uint fieldAddress = objectAddress + field.Offset;
                    if (fieldAddress < objectAddress)
                    {
                        result.Errors.Add($"{definition.StaticAddress:X8}:{definition.Label} - field {field.Offset:X4} overflowed the object address.");
                        continue;
                    }

                    AddObjectTempLabel(tempLabels, fieldAddress, field.Label, ref tempLabelCount);
                }
            }

            result.TempLabelCount = tempLabelCount;

            _objectTempLabels = tempLabels;
            RefreshObjectLabelDisplays();

            if (result.Errors.Count == 0)
                SetActivityStatus($"Object labels updated — {result.AppliedObjectCount:N0} object(s), {result.TempLabelCount:N0} temp label(s).");
            else
                SetActivityStatus($"Object labels updated with warnings — {result.AppliedObjectCount:N0} object(s), {result.TempLabelCount:N0} temp label(s).");

            if (showDialogs && result.Errors.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Some object labels could not be updated:");
                sb.AppendLine();
                foreach (string error in result.Errors.Take(12))
                    sb.AppendLine(error);
                if (result.Errors.Count > 12)
                    sb.AppendLine($"...and {result.Errors.Count - 12} more.");

                MessageBox.Show(this, sb.ToString(), "Object Labels",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            RefreshObjectLabelPointerSnapshot(definitions);
            EnsureObjectLabelLiveTimerState();
            return result;
        }

        private static void AddObjectTempLabel(Dictionary<uint, string> labels, uint address, string label, ref int count)
        {
            if (string.IsNullOrWhiteSpace(label))
                return;

            labels[address] = label.Trim();
            count++;
        }

        internal sealed class AddObjectLabelCandidate
        {
            public ObjectLabelDefinition Definition { get; init; } = new ObjectLabelDefinition();
            public uint ObjectAddress { get; init; }
            public uint Offset { get; init; }
            public override string ToString()
                => $"{Definition.Label} ({Definition.StaticAddress:X8} -> {ObjectAddress:X8})";
        }

        private List<AddObjectLabelCandidate> GetAddObjectLabelCandidates(uint selectedAddress)
        {
            var candidates = new List<AddObjectLabelCandidate>();
            foreach (ObjectLabelDefinition definition in _objectLabelDefinitions)
            {
                if (!TryGetActiveObjectRuntimeAddress(definition, out uint objectAddress))
                    continue;
                if (selectedAddress < objectAddress)
                    continue;

                candidates.Add(new AddObjectLabelCandidate
                {
                    Definition = definition,
                    ObjectAddress = objectAddress,
                    Offset = selectedAddress - objectAddress,
                });
            }

            return candidates
                .OrderBy(x => x.Offset)
                .ThenBy(x => x.Definition.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Definition.StaticAddress)
                .ToList();
        }

        private void DefineObjectFromSelectedRow()
        {
            if (!IsLiveAttached())
                return;

            if (_selRow < 0 || _selRow >= _rows.Count)
                return;

            uint selectedAddress = _rows[_selRow].Address;
            using var dlg = new DefineObjectDialog(selectedAddress);
            dlg.ApplyThemeColors(_themeFormBack, _themeFormFore, _themeWindowBack, _themeWindowFore, _currentTheme == AppTheme.Dark);
            dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            string objectName = dlg.ObjectName.Trim();
            if (string.IsNullOrWhiteSpace(objectName))
                return;

            int existingIndex = _objectLabelDefinitions.FindIndex(x => x.StaticAddress == selectedAddress);
            ObjectLabelDefinition definition;
            if (existingIndex >= 0)
            {
                definition = _objectLabelDefinitions[existingIndex];
                definition.Label = objectName;
                definition.RawText = ObjectLabelDefinitionParser.BuildCompactDefinitionText(definition);
                _objectLabelDefinitions[existingIndex] = definition;
            }
            else
            {
                definition = new ObjectLabelDefinition
                {
                    StaticAddress = selectedAddress,
                    Label = objectName,
                };
                definition.RawText = ObjectLabelDefinitionParser.BuildCompactDefinitionText(definition);
                _objectLabelDefinitions.Add(definition);
            }

            _objectLabelDefinitions = _objectLabelDefinitions
                .OrderBy(x => x.StaticAddress)
                .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ConvertAddressToWordData(selectedAddress);

            if (_labelsWindow != null && !_labelsWindow.IsDisposed)
                _labelsWindow.SetData(_cachedLabels, _objectLabelDefinitions, ShouldShowObjectLabelsTab());

            ApplyObjectLabelDefinitions(_objectLabelDefinitions, showDialogs: false);
            SetActivityStatus($"Defined object '{objectName}' at {selectedAddress:X8}.");
        }

        private void AddObjectLabelFromSelectedRow()
        {
            if (!IsLiveAttached())
                return;

            if (_selRow < 0 || _selRow >= _rows.Count)
                return;

            uint selectedAddress = _rows[_selRow].Address;
            List<AddObjectLabelCandidate> candidates = GetAddObjectLabelCandidates(selectedAddress);
            using var dlg = new AddObjectFieldLabelDialog(selectedAddress, candidates);
            dlg.ApplyThemeColors(_themeFormBack, _themeFormFore, _themeWindowBack, _themeWindowFore, _currentTheme == AppTheme.Dark);
            dlg.Load += (_, _) => ApplyThemeToWindowChrome(dlg, forceFrameRefresh: true);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedStaticAddress == null)
                return;

            int definitionIndex = _objectLabelDefinitions.FindIndex(x => x.StaticAddress == dlg.SelectedStaticAddress.Value);
            if (definitionIndex < 0)
                return;

            ObjectLabelDefinition definition = _objectLabelDefinitions[definitionIndex];
            string newLabel = dlg.EnteredLabel.Trim();
            uint offset = dlg.SelectedOffset;
            ObjectLabelField? existingField = definition.Fields.FirstOrDefault(x => x.Offset == offset);
            bool removed = false;
            if (string.IsNullOrWhiteSpace(newLabel))
            {
                if (existingField != null)
                {
                    definition.Fields.Remove(existingField);
                    removed = true;
                }
            }
            else if (existingField != null)
            {
                existingField.Label = newLabel;
            }
            else
            {
                definition.Fields.Add(new ObjectLabelField { Offset = offset, Label = newLabel });
            }

            definition.RawText = ObjectLabelDefinitionParser.BuildCompactDefinitionText(definition);
            _objectLabelDefinitions[definitionIndex] = definition;
            if (_labelsWindow != null && !_labelsWindow.IsDisposed)
                _labelsWindow.SetData(_cachedLabels, _objectLabelDefinitions, ShouldShowObjectLabelsTab());

            ApplyObjectLabelDefinitions(_objectLabelDefinitions, showDialogs: false);
            if (removed)
                SetActivityStatus($"Removed object label from {definition.Label} +{offset:X}.");
            else if (!string.IsNullOrWhiteSpace(newLabel))
                SetActivityStatus($"Updated object label '{newLabel}' at {definition.Label} +{offset:X}.");
            else
                SetActivityStatus($"No object label exists at {definition.Label} +{offset:X}.");
        }

        private void ShowLabelBrowser()
        {
            if (_cachedLabels.Count == 0)
                RebuildLabelCache();

            if (_labelsWindow == null || _labelsWindow.IsDisposed)
            {
                _labelsWindow = new LabelsWindowDialog(_cachedLabels, _objectLabelDefinitions, ShouldShowObjectLabelsTab());
                _labelsWindow.InitialFilter = _labelBrowserFilter;
                _labelsWindow.InitialLabelsOnly = _labelBrowserLabelsOnly;
                _labelsWindow.InitialSelectedAddress = _labelBrowserSelectedAddress;
                _labelsWindow.InitialSelectedTabIndex = _labelsWindowSelectedTabIndex;
                _labelsWindow.UpdateObjectsCallback = defs =>
                {
                    _objectLabelDefinitions = CloneObjectLabelDefinitions(defs);
                    return ApplyObjectLabelDefinitions(defs, showDialogs: false);
                };
                _labelsWindow.NavigateToAddressCallback = NavigateToAddressFromLabelsWindow;
                _labelsWindow.ApplyThemeColors(_themeFormBack, _themeFormFore, _themeWindowBack, _themeWindowFore, _currentTheme == AppTheme.Dark);
                _labelsWindow.Load += (sender, _) =>
                {
                    if (sender is Form frm)
                        ApplyThemeToWindowChrome(frm, forceFrameRefresh: true);
                };
                _labelsWindow.FormClosed += (_, _) =>
                {
                    PersistLabelBrowserWindowState();
                    _labelsWindow = null;
                };
                _labelsWindow.ApplyInitialState();
                CenterLabelsWindow();
                _labelsWindow.Show(this);
                EnsureObjectLabelLiveTimerState();
            }
            else
            {
                _labelsWindow.SetData(_cachedLabels, _objectLabelDefinitions, ShouldShowObjectLabelsTab());
                _labelsWindow.UpdateObjectsCallback = defs =>
                {
                    _objectLabelDefinitions = CloneObjectLabelDefinitions(defs);
                    return ApplyObjectLabelDefinitions(defs, showDialogs: false);
                };
                _labelsWindow.NavigateToAddressCallback = NavigateToAddressFromLabelsWindow;
                _labelsWindow.ApplyThemeColors(_themeFormBack, _themeFormFore, _themeWindowBack, _themeWindowFore, _currentTheme == AppTheme.Dark);
                if (!_labelsWindow.Visible)
                {
                    CenterLabelsWindow();
                    _labelsWindow.Show(this);
                }
            }

            EnsureObjectLabelLiveTimerState();
            _labelsWindow?.BringToFront();
            _labelsWindow?.Activate();
        }

        private void PersistLabelBrowserWindowState()
        {
            if (_labelsWindow == null)
                return;

            _labelBrowserFilter = _labelsWindow.CurrentFilter;
            _labelBrowserLabelsOnly = _labelsWindow.LabelsOnly;
            _labelBrowserSelectedAddress = _labelsWindow.CurrentSelectedAddress;
            _labelsWindowSelectedTabIndex = _labelsWindow.SelectedTabIndex;
            _objectLabelDefinitions = CloneObjectLabelDefinitions(_labelsWindow.ObjectDefinitions);
        }


        private void CenterLabelsWindow()
        {
            if (_labelsWindow == null || _labelsWindow.IsDisposed)
                return;

            Rectangle ownerBounds = Bounds;
            int x = ownerBounds.Left + Math.Max(0, (ownerBounds.Width - _labelsWindow.Width) / 2);
            int y = ownerBounds.Top + Math.Max(0, (ownerBounds.Height - _labelsWindow.Height) / 2);
            _labelsWindow.StartPosition = FormStartPosition.Manual;
            _labelsWindow.Location = new Point(Math.Max(0, x), Math.Max(0, y));
        }

        private void NavigateToAddressFromLabelsWindow(uint addr)
        {
            PersistLabelBrowserWindowState();
            if (_mainTabs != null)
                _mainTabs.SelectedIndex = 0;

            if (TryGetRowIndexByAddress(addr, out int idx)) { SelectRow(idx, center: true); return; }
            int nearest = FindNearestRow(addr);
            if (nearest >= 0) { SelectRow(nearest, center: true); return; }

            MessageBox.Show($"${addr:X8} is outside the file range.", "ps2dis");
        }

        private void SyncLabelsWindowObjectTabVisibility()
        {
            if (_labelsWindow == null || _labelsWindow.IsDisposed)
                return;

            _labelsWindow.SetShowObjectLabels(ShouldShowObjectLabelsTab());
            _labelsWindow.SetData(_cachedLabels, _objectLabelDefinitions, ShouldShowObjectLabelsTab());
            _labelsWindow.ApplyThemeColors(_themeFormBack, _themeFormFore, _themeWindowBack, _themeWindowFore, _currentTheme == AppTheme.Dark);
        }
    }

    internal sealed class AddObjectDefinitionDialog : Form
    {
        private readonly RichTextBox _tbDefinition;
        private readonly Button _btnAccept;
        private readonly Panel _footer;
        private readonly string _placeholderText;
        private bool _showingPlaceholder;
        private bool _dark;
        private Color _titleBarBack = SystemColors.ActiveCaption;
        private Color _titleBarFore = SystemColors.ActiveCaptionText;
        private Color _definitionFore = SystemColors.ControlText;
        private Color _placeholderFore = SystemColors.GrayText;
        public ObjectLabelDefinition? ObjectDefinition { get; private set; }

        public AddObjectDefinitionDialog(string title = "Add Object Definition", string actionText = "Add Object", string initialText = "", string placeholderText = "")
        {
            _placeholderText = placeholderText ?? string.Empty;
            Text = title;
            Size = new Size(520, 420);
            MinimumSize = new Size(420, 300);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Tahoma", 8.25f);

            _tbDefinition = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Font = new Font(AppSettings.DefaultFontFamily, 10f),
                AcceptsTab = true,
                WordWrap = false,
                DetectUrls = false,
                Text = initialText ?? string.Empty,
            };
            _tbDefinition.HandleCreated += (_, _) => NativeMethods.SetRichTextBoxPadding(_tbDefinition, 5);
            _tbDefinition.Resize += (_, _) => NativeMethods.SetRichTextBoxPadding(_tbDefinition, 5);
            _tbDefinition.KeyDown += OnDefinitionKeyDown;
            _tbDefinition.KeyPress += OnDefinitionKeyPress;
            if (string.IsNullOrWhiteSpace(initialText) && !string.IsNullOrWhiteSpace(_placeholderText))
                ShowPlaceholder();

            _footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                Padding = new Padding(8, 8, 8, 8),
            };

            _btnAccept = new Button
            {
                Text = actionText,
                Dock = DockStyle.Right,
                Width = 110,
            };
            _btnAccept.Click += (_, _) => TryAccept();

            _footer.Controls.Add(_btnAccept);
            Controls.Add(_tbDefinition);
            Controls.Add(_footer);

            HandleCreated += (_, _) => ApplyWindowChromeTheme(forceFrameRefresh: true);
            Shown += (_, _) => ApplyWindowChromeTheme(forceFrameRefresh: true);
        }

        private void TryAccept()
        {
            try
            {
                ObjectDefinition = ObjectLabelDefinitionParser.Parse(GetDefinitionText());
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private string GetDefinitionText()
            => _showingPlaceholder ? string.Empty : _tbDefinition.Text;

        private void OnDefinitionKeyPress(object? sender, KeyPressEventArgs e)
        {
            if (_showingPlaceholder && !char.IsControl(e.KeyChar))
                ClearPlaceholder();
        }

        private void OnDefinitionKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_showingPlaceholder)
                return;

            if ((e.Control && e.KeyCode == Keys.V) || e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
                ClearPlaceholder();
        }

        private void ShowPlaceholder()
        {
            _showingPlaceholder = true;
            _tbDefinition.Text = _placeholderText;
            _tbDefinition.ForeColor = _placeholderFore;
            _tbDefinition.Select(0, 0);
        }

        private void ClearPlaceholder()
        {
            if (!_showingPlaceholder)
                return;

            _showingPlaceholder = false;
            _tbDefinition.Clear();
            _tbDefinition.ForeColor = _definitionFore;
        }

        public void ApplyThemeColors(Color formBack, Color formFore, Color windowBack, Color windowFore, bool dark, Color titleBarBack, Color titleBarFore)
        {
            _dark = dark;

            Color actualFormBack = dark ? formBack : Color.FromArgb(226, 226, 226);
            Color actualFooterBack = dark ? formBack : Color.FromArgb(226, 226, 226);
            Color actualEditorBack = dark ? windowBack : Color.FromArgb(240, 240, 240);
            Color actualBorder = dark ? windowFore : Color.FromArgb(136, 136, 136);

            _titleBarBack = dark ? titleBarBack : Color.FromArgb(214, 214, 214);
            _titleBarFore = dark ? titleBarFore : formFore;
            BackColor = actualFormBack;
            ForeColor = formFore;
            _footer.BackColor = actualFooterBack;
            _footer.ForeColor = formFore;
            _definitionFore = windowFore;
            _placeholderFore = dark ? Color.FromArgb(142, 148, 156) : Color.FromArgb(128, 128, 128);
            _tbDefinition.BackColor = actualEditorBack;
            _tbDefinition.ForeColor = _showingPlaceholder ? _placeholderFore : _definitionFore;
            Color acceptBack = Color.FromArgb(actualFooterBack.A,
                Math.Min(255, (int)Math.Round(actualFooterBack.R * 1.20f)),
                Math.Min(255, (int)Math.Round(actualFooterBack.G * 1.20f)),
                Math.Min(255, (int)Math.Round(actualFooterBack.B * 1.20f)));
            _btnAccept.BackColor = acceptBack;
            _btnAccept.ForeColor = formFore;
            _btnAccept.FlatStyle = FlatStyle.Flat;
            _btnAccept.FlatAppearance.BorderColor = actualBorder;
            _btnAccept.FlatAppearance.MouseOverBackColor = Color.FromArgb(acceptBack.A,
                Math.Min(255, (int)Math.Round(acceptBack.R * 1.20f)),
                Math.Min(255, (int)Math.Round(acceptBack.G * 1.20f)),
                Math.Min(255, (int)Math.Round(acceptBack.B * 1.20f)));
            ApplyWindowChromeTheme(forceFrameRefresh: true);
        }

        private void ApplyWindowChromeTheme(bool forceFrameRefresh)
        {
            if (!IsHandleCreated)
                return;

            try
            {
                int darkVal = _dark ? 1 : 0;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
            }
            catch { }

            try
            {
                int captionColor = ColorTranslator.ToWin32(_titleBarBack);
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            }
            catch { }

            try
            {
                int textColor = ColorTranslator.ToWin32(_titleBarFore);
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            }
            catch { }

            try
            {
                string theme = _dark ? "DarkMode_Explorer" : "";
                if (_tbDefinition.IsHandleCreated)
                    NativeMethods.SetWindowTheme(_tbDefinition.Handle, theme, null);
            }
            catch { }

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
            }
        }
    }

    internal sealed class DefineObjectDialog : Form
    {
        private readonly MainForm.CenteredSingleLineTextBox _tbName;
        private readonly Button _btnAdd;
        private readonly Button _btnCancel;
        private bool _dark;
        private Color _formBack = SystemColors.Control;
        private Color _formFore = SystemColors.ControlText;
        private Color _windowFore = SystemColors.ControlText;

        public string ObjectName => _tbName.Text.Trim();

        public DefineObjectDialog(uint selectedAddress)
        {
            Text = $"Define Object - {selectedAddress:X8}";
            ClientSize = new Size(420, 94);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Tahoma", 8.25f);

            var lblName = new Label { Text = "Object Name:", AutoSize = true, Location = new Point(12, 17) };
            _tbName = new MainForm.CenteredSingleLineTextBox
            {
                Location = new Point(100, 13),
                Size = new Size(306, 24),
            };
            _tbName.TextChanged += (_, _) => UpdateButtonState();

            _btnAdd = new Button { Text = "Add", Size = new Size(92, 27), Location = new Point(218, 56) };
            _btnAdd.Click += (_, _) => Accept();
            _btnCancel = new Button { Text = "Cancel", Size = new Size(92, 27), Location = new Point(314, 56) };
            _btnCancel.Click += (_, _) => Close();

            AcceptButton = _btnAdd;
            CancelButton = _btnCancel;

            Controls.AddRange(new Control[] { lblName, _tbName, _btnAdd, _btnCancel });

            UpdateButtonState();

            Shown += (_, _) =>
            {
                ActiveControl = _tbName;
                _tbName.Focus();
                _tbName.SelectAll();
            };
        }

        private static Color BrightenButtonColor(Color color)
        {
            return Color.FromArgb(color.A,
                Math.Min(255, (int)Math.Round(color.R * 1.20f)),
                Math.Min(255, (int)Math.Round(color.G * 1.20f)),
                Math.Min(255, (int)Math.Round(color.B * 1.20f)));
        }

        private void ApplyButtonState(Button button)
        {
            if (button == null)
                return;

            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            Color enabledBack = BrightenButtonColor(_formBack);
            Color hoverBack = BrightenButtonColor(_dark ? Color.FromArgb(58, 66, 78) : Color.FromArgb(210, 220, 235));
            button.FlatAppearance.MouseOverBackColor = hoverBack;
            button.FlatAppearance.MouseDownBackColor = _dark ? Color.FromArgb(70, 80, 94) : Color.FromArgb(190, 205, 225);

            if (button.Enabled)
            {
                button.BackColor = enabledBack;
                button.ForeColor = _formFore;
                button.FlatAppearance.BorderColor = _windowFore;
            }
            else
            {
                button.BackColor = _dark ? Color.FromArgb(58, 62, 68) : Color.FromArgb(236, 236, 236);
                button.ForeColor = _dark ? Color.FromArgb(160, 166, 174) : Color.FromArgb(128, 128, 128);
                button.FlatAppearance.BorderColor = _dark ? Color.FromArgb(96, 102, 110) : Color.FromArgb(170, 170, 170);
            }
        }

        private void UpdateButtonState()
        {
            _btnAdd.Enabled = !string.IsNullOrWhiteSpace(_tbName.Text);
            ApplyButtonState(_btnAdd);
            ApplyButtonState(_btnCancel);
        }

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(_tbName.Text))
                return;

            DialogResult = DialogResult.OK;
            Close();
        }

        public void ApplyThemeColors(Color formBack, Color formFore, Color windowBack, Color windowFore, bool dark)
        {
            _dark = dark;
            _formBack = formBack;
            _formFore = formFore;
            _windowFore = windowFore;

            BackColor = formBack;
            ForeColor = formFore;

            foreach (Control control in Controls)
            {
                if (control is Label label)
                {
                    label.BackColor = formBack;
                    label.ForeColor = formFore;
                }
                else if (control is Button button)
                {
                    button.BackColor = formBack;
                    button.ForeColor = formFore;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = windowFore;
                    ApplyButtonState(button);
                }
            }

            _tbName.BackColor = windowBack;
            _tbName.ForeColor = windowFore;
            _tbName.BorderStyle = BorderStyle.FixedSingle;
            ApplyButtonState(_btnAdd);
            ApplyButtonState(_btnCancel);
        }
    }

    internal sealed class AddObjectFieldLabelDialog : Form
    {
        private readonly MainForm.FlatComboBox _cbObject;
        private readonly MainForm.CenteredSingleLineTextBox _tbLabel;
        private readonly Button _btnUpdate;
        private readonly Button _btnClose;
        private readonly IReadOnlyList<MainForm.AddObjectLabelCandidate> _candidates;
        private bool _dark;
        private Color _formBack = SystemColors.Control;
        private Color _formFore = SystemColors.ControlText;
        private Color _windowFore = SystemColors.ControlText;

        public uint? SelectedStaticAddress { get; private set; }
        public uint SelectedOffset { get; private set; }
        public string EnteredLabel => _tbLabel.Text.Trim();

        public AddObjectFieldLabelDialog(uint selectedAddress, IReadOnlyList<MainForm.AddObjectLabelCandidate> candidates)
        {
            _candidates = candidates ?? Array.Empty<MainForm.AddObjectLabelCandidate>();

            Text = "Add/Edit Object Label";
            ClientSize = new Size(470, 124);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Tahoma", 8.25f);

            var lblObject = new Label { Text = "Object", AutoSize = true, Location = new Point(12, 15) };
            _cbObject = new MainForm.FlatComboBox
            {
                Location = new Point(95, 11),
                Size = new Size(360, 24),
                FormattingEnabled = true,
            };
            _cbObject.SelectedIndexChanged += (_, _) => UpdateSelectionState();

            var lblLabel = new Label { Text = "Enter Label:", AutoSize = true, Location = new Point(12, 50) };
            _tbLabel = new MainForm.CenteredSingleLineTextBox
            {
                Location = new Point(95, 46),
                Size = new Size(360, 24),
            };
            _tbLabel.TextChanged += (_, _) => UpdateButtonState();

            _btnUpdate = new Button { Text = "Update", Size = new Size(92, 27), Location = new Point(267, 86) };
            _btnUpdate.Click += (_, _) => Accept();
            _btnClose = new Button { Text = "Close", Size = new Size(92, 27), Location = new Point(363, 86) };
            _btnClose.Click += (_, _) => Close();
            CancelButton = _btnClose;
            AcceptButton = _btnUpdate;

            Controls.AddRange(new Control[] { lblObject, _cbObject, lblLabel, _tbLabel, _btnUpdate, _btnClose });

            foreach (MainForm.AddObjectLabelCandidate candidate in _candidates)
                _cbObject.Items.Add(candidate);
            if (_cbObject.Items.Count > 0)
                _cbObject.SelectedIndex = 0;

            UpdateSelectionState();

            Shown += (_, _) =>
            {
                ActiveControl = _tbLabel;
                _tbLabel.Focus();
                _tbLabel.SelectAll();
            };
        }

        private void UpdateSelectionState()
        {
            if (_cbObject.SelectedItem is MainForm.AddObjectLabelCandidate candidate)
            {
                SelectedStaticAddress = candidate.Definition.StaticAddress;
                SelectedOffset = candidate.Offset;
                Text = $"Add/Edit Object Label - 0x{candidate.Offset:X}";
                LoadExistingLabelForSelection(candidate);
            }
            else
            {
                SelectedStaticAddress = null;
                SelectedOffset = 0;
                Text = "Add/Edit Object Label";
                LoadExistingLabelForSelection(null);
            }

            UpdateButtonState();
        }

        private void UpdateButtonState()
        {
            // Allow Update with an empty label so an existing object field label can be removed.
            _btnUpdate.Enabled = SelectedStaticAddress.HasValue;
            ApplyButtonState(_btnUpdate);
            ApplyButtonState(_btnClose);
        }

        private void LoadExistingLabelForSelection(MainForm.AddObjectLabelCandidate? candidate)
        {
            string existingLabel = string.Empty;
            if (candidate != null)
            {
                ObjectLabelField? field = candidate.Definition.Fields.FirstOrDefault(x => x.Offset == candidate.Offset);
                if (field != null)
                    existingLabel = field.Label?.Trim() ?? string.Empty;
            }

            _tbLabel.Text = existingLabel;
            _tbLabel.SelectAll();
        }

        private static Color BrightenButtonColor(Color color)
        {
            return Color.FromArgb(color.A,
                Math.Min(255, (int)Math.Round(color.R * 1.20f)),
                Math.Min(255, (int)Math.Round(color.G * 1.20f)),
                Math.Min(255, (int)Math.Round(color.B * 1.20f)));
        }

        private void ApplyButtonState(Button button)
        {
            if (button == null)
                return;

            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            Color enabledBack = BrightenButtonColor(_formBack);
            Color hoverBack = BrightenButtonColor(_dark ? Color.FromArgb(58, 66, 78) : Color.FromArgb(210, 220, 235));
            button.FlatAppearance.MouseOverBackColor = hoverBack;
            button.FlatAppearance.MouseDownBackColor = _dark ? Color.FromArgb(70, 80, 94) : Color.FromArgb(190, 205, 225);

            if (button.Enabled)
            {
                button.BackColor = enabledBack;
                button.ForeColor = _formFore;
                button.FlatAppearance.BorderColor = _windowFore;
            }
            else
            {
                button.BackColor = _dark ? Color.FromArgb(58, 62, 68) : Color.FromArgb(236, 236, 236);
                button.ForeColor = _dark ? Color.FromArgb(160, 166, 174) : Color.FromArgb(128, 128, 128);
                button.FlatAppearance.BorderColor = _dark ? Color.FromArgb(96, 102, 110) : Color.FromArgb(170, 170, 170);
            }
        }

        private void Accept()
        {
            if (!SelectedStaticAddress.HasValue)
                return;

            DialogResult = DialogResult.OK;
            Close();
        }

        public void ApplyThemeColors(Color formBack, Color formFore, Color windowBack, Color windowFore, bool dark)
        {
            _dark = dark;
            _formBack = formBack;
            _formFore = formFore;
            _windowFore = windowFore;

            BackColor = formBack;
            ForeColor = formFore;

            foreach (Control control in Controls)
            {
                if (control is Label label)
                {
                    label.BackColor = formBack;
                    label.ForeColor = formFore;
                }
                else if (control is Button button)
                {
                    button.BackColor = formBack;
                    button.ForeColor = formFore;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = windowFore;
                    ApplyButtonState(button);
                }
            }

            _cbObject.BackColor = windowBack;
            _cbObject.ForeColor = windowFore;
            _tbLabel.BackColor = windowBack;
            _tbLabel.ForeColor = windowFore;
            _tbLabel.BorderStyle = BorderStyle.FixedSingle;
            ApplyButtonState(_btnUpdate);
            ApplyButtonState(_btnClose);
        }
    }

    internal sealed class LabelsWindowDialog : Form
    {
        public uint SelectedAddress { get; private set; }
        public string InitialFilter { get; set; } = string.Empty;
        public bool InitialLabelsOnly { get; set; }
        public uint InitialSelectedAddress { get; set; }
        public int InitialSelectedTabIndex { get; set; }
        public string CurrentFilter => _search.Text;
        public bool LabelsOnly => _cbLabelsOnly.Checked;
        public uint CurrentSelectedAddress => _labelList.SelectedIndices.Count > 0 && _labelList.SelectedIndices[0] >= 0 && _labelList.SelectedIndices[0] < _filtered.Count
            ? _filtered[_labelList.SelectedIndices[0]].Address
            : InitialSelectedAddress;
        public int SelectedTabIndex => _tabs.SelectedIndex;
        public List<ObjectLabelDefinition> ObjectDefinitions => _objects.Select(x => x.Clone()).ToList();
        public Func<IReadOnlyList<ObjectLabelDefinition>, ObjectLabelUpdateResult>? UpdateObjectsCallback { get; set; }
        public Action<uint>? NavigateToAddressCallback { get; set; }
        private bool _showObjectLabels;

        private List<(string Name, uint Address)> _allLabels;
        private List<(string Name, uint Address)> _filtered;
        private List<ObjectLabelDefinition> _objects;

        private readonly MainForm.FlatTabHost _tabs;
        private readonly MainForm.CenteredSingleLineTextBox _search;
        private readonly VirtualDisasmList _labelList;
        private readonly VirtualDisasmList _objectList;
        private readonly Label _countLabel;
        private readonly Label _searchLabel;
        private readonly CheckBox _cbLabelsOnly;
        private readonly Button _btnGo;
        private readonly Button _btnCloseLabels;
        private readonly Button _btnUpdateObject;
        private readonly Button _btnAddObject;
        private readonly Button _btnDeleteObject;
        private readonly Button _btnCloseObjects;
        private readonly Panel _labelsTop;
        private readonly Panel _labelsBottom;
        private readonly Panel _objectBottom;
        private readonly ContextMenuStrip _objectMenu;
        private readonly Font _listFont = new Font("Courier New", 9f);
        private const string ObjectDefinitionExampleText = "0020A588:ExampleObject\r\n0000:Label1\r\n0004:Label2\r\n02F0:Label3";

        private Color _windowBack = Color.White;
        private Color _windowFore = Color.Black;
        private Color _selBack = Color.FromArgb(0, 0, 128);
        private Color _selFore = Color.White;
        private Color _headerBack = SystemColors.Control;
        private Color _headerFore = SystemColors.ControlText;
        private Color _headerBorder = SystemColors.ControlDark;
        private Color _formBack = SystemColors.Control;
        private Color _formFore = SystemColors.ControlText;
        private bool _dark;

        private int _sortCol = 1;
        private bool _sortAsc = true;

        public LabelsWindowDialog(List<(string Name, uint Address)> labels, IEnumerable<ObjectLabelDefinition> objects, bool showObjectLabels)
        {
            _allLabels = labels ?? new List<(string Name, uint Address)>();
            _filtered = new List<(string Name, uint Address)>(_allLabels);
            _objects = objects?.Select(x => x.Clone()).ToList() ?? new List<ObjectLabelDefinition>();
            _showObjectLabels = showObjectLabels;

            Text = "Labels";
            Size = new Size(620, 620);
            MinimumSize = new Size(460, 360);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Font = new Font("Tahoma", 8.25f);

            _tabs = new MainForm.FlatTabHost
            {
                Dock = DockStyle.Fill,
                TabButtonWidth = 118,
            };

            var labelsPage = new MainForm.FlatTabPage("Labels");
            var objectPage = new MainForm.FlatTabPage("Object Labels");
            _tabs.AddPage(labelsPage);
            _tabs.AddPage(objectPage);
            Controls.Add(_tabs);
            _tabs.SetTabVisible(1, _showObjectLabels);
            _tabs.SelectedIndexChanged += (_, _) =>
            {
                if (IsHandleCreated)
                    BeginInvoke((Action)UpdateColumnWidths);
                else
                    UpdateColumnWidths();
            };

            _labelsTop = new Panel { Dock = DockStyle.Top, Height = 34 };
            _labelsBottom = new Panel { Dock = DockStyle.Bottom, Height = 38 };
            _labelList = CreateList();
            _labelList.Dock = DockStyle.Fill;
            _labelList.Columns.Add("Address", 84);
            _labelList.Columns.Add("Label", 420);
            _labelList.DrawCell += OnDrawLabelCell;
            _labelList.DrawHeader += OnDrawHeader;
            _labelList.SelectedIndexChanged += (_, _) => _labelList.Invalidate();
            _labelList.MouseDoubleClick += (_, _) => AcceptLabelSelection();
            _labelList.KeyDown += OnLabelListKeyDown;

            _searchLabel = new Label { Text = "Filter:", AutoSize = true, Location = new Point(8, 10) };
            _search = new MainForm.CenteredSingleLineTextBox
            {
                Location = new Point(48, 6),
                Width = 370,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            _search.TextChanged += OnSearchChanged;
            _search.KeyDown += OnSearchKeyDown;
            _countLabel = new Label { Text = $"{_allLabels.Count} labels", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _labelsTop.Controls.Add(_searchLabel);
            _labelsTop.Controls.Add(_search);
            _labelsTop.Controls.Add(_countLabel);
            _labelsTop.Resize += (_, _) =>
            {
                _countLabel.Location = new Point(_labelsTop.ClientSize.Width - _countLabel.PreferredWidth - 8, 10);
                _search.Width = Math.Max(120, _countLabel.Left - _search.Left - 8);
            };

            _cbLabelsOnly = new MainForm.ThemedCheckBox
            {
                Text = "Labels Only",
                AutoSize = true,
                Location = new Point(8, 11),
                FlatStyle = FlatStyle.Flat,
            };
            _cbLabelsOnly.CheckedChanged += (_, _) => OnSearchChanged(null, EventArgs.Empty);

            _btnGo = new Button
            {
                Text = "Go to Label",
                Size = new Size(100, 26),
            };
            _btnGo.Click += (_, _) => AcceptLabelSelection();

            _btnCloseLabels = new Button
            {
                Text = "Close",
                Size = new Size(90, 26),
            };
            _btnCloseLabels.Click += (_, _) => Close();
            CancelButton = _btnCloseLabels;

            _labelsBottom.Controls.Add(_cbLabelsOnly);
            _labelsBottom.Controls.Add(_btnGo);
            _labelsBottom.Controls.Add(_btnCloseLabels);
            _labelsBottom.Resize += (_, _) =>
            {
                _btnCloseLabels.Location = new Point(_labelsBottom.ClientSize.Width - _btnCloseLabels.Width - 8, 6);
                _btnGo.Location = new Point(_btnCloseLabels.Left - _btnGo.Width - 8, 6);
            };

            labelsPage.Controls.Add(_labelList);
            labelsPage.Controls.Add(_labelsBottom);
            labelsPage.Controls.Add(_labelsTop);

            _objectBottom = new Panel { Dock = DockStyle.Bottom, Height = 38 };
            _objectList = CreateList();
            _objectList.Dock = DockStyle.Fill;
            _objectList.Columns.Add("Address", 84);
            _objectList.Columns.Add("Label", 420);
            _objectList.DrawCell += OnDrawObjectCell;
            _objectList.DrawHeader += OnDrawHeader;
            _objectList.SelectedIndexChanged += (_, _) => { _objectList.Invalidate(); UpdateObjectButtons(); };
            _objectList.MouseDoubleClick += OnObjectListMouseDoubleClick;

            _btnUpdateObject = new Button
            {
                Text = "Update",
                Size = new Size(84, 26),
                Location = new Point(8, 6),
            };
            _btnUpdateObject.Click += (_, _) => UpdateObjects();

            _btnAddObject = new Button
            {
                Text = "Add",
                Size = new Size(84, 26),
                Location = new Point(100, 6),
            };
            _btnAddObject.Click += (_, _) => AddObjectDefinition();

            _btnDeleteObject = new Button
            {
                Text = "Delete",
                Size = new Size(84, 26),
                Location = new Point(192, 6),
            };
            _btnDeleteObject.Click += (_, _) => DeleteSelectedObject();

            _btnCloseObjects = new Button
            {
                Text = "Close",
                Size = new Size(90, 26),
            };
            _btnCloseObjects.Click += (_, _) => Close();

            _objectBottom.Controls.Add(_btnUpdateObject);
            _objectBottom.Controls.Add(_btnAddObject);
            _objectBottom.Controls.Add(_btnDeleteObject);
            _objectBottom.Controls.Add(_btnCloseObjects);
            _objectBottom.Resize += (_, _) =>
            {
                _btnCloseObjects.Location = new Point(_objectBottom.ClientSize.Width - _btnCloseObjects.Width - 8, 6);
            };

            _objectMenu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = false,
            };
            var miEdit = new ToolStripMenuItem("Edit", null, (_, _) => EditSelectedObject());
            var miRemove = new ToolStripMenuItem("Remove", null, (_, _) => DeleteSelectedObject());
            _objectMenu.Items.Add(miEdit);
            _objectMenu.Items.Add(miRemove);
            _objectMenu.Opening += (_, e) =>
            {
                bool hasSelection = _objectList.SelectedIndices.Count > 0;
                miEdit.Enabled = hasSelection;
                miRemove.Enabled = hasSelection;
                if (!hasSelection)
                    e.Cancel = true;
            };
            _objectList.ContextMenuStrip = _objectMenu;

            objectPage.Controls.Add(_objectList);
            objectPage.Controls.Add(_objectBottom);

            Resize += (_, _) => UpdateColumnWidths();
            Shown += (_, _) =>
            {
                _btnCloseLabels.Location = new Point(_labelsBottom.ClientSize.Width - _btnCloseLabels.Width - 8, 6);
                _btnGo.Location = new Point(_btnCloseLabels.Left - _btnGo.Width - 8, 6);
                _btnCloseObjects.Location = new Point(_objectBottom.ClientSize.Width - _btnCloseObjects.Width - 8, 6);
                UpdateColumnWidths();
            };
            PopulateLabelList();
            PopulateObjectList();
            UpdateObjectButtons();
            ActiveControl = _search;
        }

        private VirtualDisasmList CreateList()
        {
            var list = new VirtualDisasmList
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                Font = _listFont,
                MultiSelect = false,
                VirtualMode = true,
                OwnerDraw = true,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                AllowColumnResize = false,
            };
            list.RowHeight = Math.Max(1, _listFont.Height + 4);
            list.HeaderHeight = Math.Max(1, _listFont.Height + 6);
            return list;
        }

        public void ApplyInitialState()
        {
            _search.Text = InitialFilter;
            _cbLabelsOnly.Checked = InitialLabelsOnly;
            int desiredTabIndex = InitialSelectedTabIndex >= 0 && InitialSelectedTabIndex < _tabs.Pages.Count ? InitialSelectedTabIndex : 0;
            if (!_showObjectLabels && desiredTabIndex == 1)
                desiredTabIndex = 0;
            _tabs.SelectedIndex = desiredTabIndex;
            SetShowObjectLabels(_showObjectLabels);
            PopulateLabelList();
            PopulateObjectList();
            RestoreLabelSelection();
        }

        public void ApplyThemeColors(Color formBack, Color formFore, Color windowBack, Color windowFore, bool dark)
        {
            _formBack = formBack;
            _formFore = formFore;
            _windowBack = windowBack;
            _windowFore = windowFore;
            _dark = dark;

            BackColor = formBack;
            ForeColor = formFore;
            _tabs.BackColor = formBack;
            _tabs.TabStripBackColorOverride = formBack;
            _tabs.ContentBackColorOverride = formBack;
            _tabs.ApplyPalette(dark, formBack, formFore);

            foreach (Panel panel in Controls.OfType<Panel>().Concat(FindControls<Panel>(this)))
            {
                panel.BackColor = formBack;
                panel.ForeColor = formFore;
            }

            foreach (Label label in FindControls<Label>(this))
            {
                label.BackColor = formBack;
                label.ForeColor = formFore;
            }

            _search.BackColor = windowBack;
            _search.ForeColor = windowFore;
            _cbLabelsOnly.BackColor = formBack;
            _cbLabelsOnly.ForeColor = formFore;
            _cbLabelsOnly.FlatStyle = FlatStyle.Flat;
            _cbLabelsOnly.FlatAppearance.BorderColor = dark ? Color.FromArgb(112, 120, 132) : Color.FromArgb(120, 120, 120);
            _labelList.BackColor = windowBack;
            _labelList.ForeColor = windowFore;
            _objectList.BackColor = windowBack;
            _objectList.ForeColor = windowFore;
            _countLabel.ForeColor = formFore;
            _searchLabel.ForeColor = formFore;

            _selBack = dark ? Color.FromArgb(64, 72, 84) : Color.FromArgb(0, 0, 128);
            _selFore = Color.White;
            _headerBack = dark ? Color.FromArgb(44, 48, 54) : SystemColors.Control;
            _headerFore = dark ? Color.FromArgb(232, 232, 232) : SystemColors.ControlText;
            _headerBorder = dark ? Color.FromArgb(82, 86, 94) : SystemColors.ControlDark;
            _labelList.HeaderBackColor = _headerBack;
            _labelList.HeaderBorderColor = _headerBorder;
            _objectList.HeaderBackColor = _headerBack;
            _objectList.HeaderBorderColor = _headerBorder;

            foreach (var button in FindButtons(this))
            {
                button.BackColor = formBack;
                button.ForeColor = formFore;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = windowFore;
                ApplyButtonState(button);
            }

            _objectMenu.BackColor = _headerBack;
            _objectMenu.ForeColor = _headerFore;
            _objectMenu.Renderer = new ObjectLabelsMenuRenderer(dark, _headerFore);
            foreach (ToolStripItem item in _objectMenu.Items)
            {
                item.BackColor = _headerBack;
                item.ForeColor = _headerFore;
            }

            UpdateColumnWidths();
            _labelList.Invalidate();
            _objectList.Invalidate();
        }

        private static Color BrightenButtonColor(Color color)
        {
            return Color.FromArgb(color.A,
                Math.Min(255, (int)Math.Round(color.R * 1.20f)),
                Math.Min(255, (int)Math.Round(color.G * 1.20f)),
                Math.Min(255, (int)Math.Round(color.B * 1.20f)));
        }

        private void ApplyButtonState(Button button)
        {
            if (button == null)
                return;

            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            Color enabledBack = BrightenButtonColor(_formBack);
            Color hoverBack = BrightenButtonColor(_dark ? Color.FromArgb(58, 66, 78) : Color.FromArgb(210, 220, 235));
            button.FlatAppearance.MouseOverBackColor = hoverBack;
            button.FlatAppearance.MouseDownBackColor = _dark ? Color.FromArgb(70, 80, 94) : Color.FromArgb(190, 205, 225);

            if (button.Enabled)
            {
                button.BackColor = enabledBack;
                button.ForeColor = _formFore;
                button.FlatAppearance.BorderColor = _windowFore;
            }
            else
            {
                button.BackColor = _dark ? Color.FromArgb(58, 62, 68) : Color.FromArgb(236, 236, 236);
                button.ForeColor = _dark ? Color.FromArgb(160, 166, 174) : Color.FromArgb(128, 128, 128);
                button.FlatAppearance.BorderColor = _dark ? Color.FromArgb(96, 102, 110) : Color.FromArgb(170, 170, 170);
            }
        }

        public void SetShowObjectLabels(bool showObjectLabels)
        {
            _showObjectLabels = showObjectLabels;
            _tabs.SetTabVisible(1, showObjectLabels);
            if (!showObjectLabels && _tabs.SelectedIndex == 1)
                _tabs.SelectedIndex = 0;
        }

        public void SetData(List<(string Name, uint Address)> labels, IEnumerable<ObjectLabelDefinition> objects, bool showObjectLabels)
        {
            _allLabels = labels ?? new List<(string Name, uint Address)>();
            _filtered = new List<(string Name, uint Address)>(_allLabels);
            _objects = objects?.Select(x => x.Clone()).ToList() ?? new List<ObjectLabelDefinition>();
            SetShowObjectLabels(showObjectLabels);
            OnSearchChanged(null, EventArgs.Empty);
            PopulateObjectList();
            RestoreLabelSelection();
        }

        private sealed class ObjectLabelsMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly bool _dark;
            private readonly Color _textColor;
            private readonly Color _menuBack;
            private readonly Color _hoverBack;
            private readonly Color _pressedBack;
            private readonly Color _border;

            public ObjectLabelsMenuRenderer(bool dark, Color textColor) : base(new ObjectLabelsToolStripColorTable(dark))
            {
                _dark = dark;
                _textColor = textColor;
                _menuBack = dark ? Color.FromArgb(44, 48, 54) : SystemColors.Control;
                _hoverBack = dark ? Color.FromArgb(58, 62, 68) : Color.FromArgb(190, 205, 225);
                _pressedBack = dark ? Color.FromArgb(50, 54, 60) : Color.FromArgb(170, 188, 212);
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
                    rect.Width -= 1;
                    rect.Height -= 1;
                    using var p = new Pen(_border);
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

        private sealed class ObjectLabelsToolStripColorTable : ProfessionalColorTable
        {
            private readonly bool _dark;
            public ObjectLabelsToolStripColorTable(bool dark) => _dark = dark;

            private Color Pick(Color darkColor, Color lightColor) => _dark ? darkColor : lightColor;

            public override Color MenuStripGradientBegin => Pick(Color.FromArgb(44, 48, 54), SystemColors.Control);
            public override Color MenuStripGradientEnd => MenuStripGradientBegin;
            public override Color ToolStripDropDownBackground => Pick(Color.FromArgb(36, 39, 45), SystemColors.Control);
            public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
            public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
            public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
            public override Color MenuItemSelected => Pick(Color.FromArgb(58, 62, 68), Color.FromArgb(190, 205, 225));
            public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
            public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
            public override Color ButtonSelectedHighlight => MenuItemSelected;
            public override Color ButtonSelectedHighlightBorder => Pick(Color.FromArgb(88, 96, 108), SystemColors.Highlight);
            public override Color ButtonSelectedGradientBegin => MenuItemSelected;
            public override Color ButtonSelectedGradientMiddle => MenuItemSelected;
            public override Color ButtonSelectedGradientEnd => MenuItemSelected;
            public override Color ButtonPressedHighlight => Pick(Color.FromArgb(50, 54, 60), Color.FromArgb(170, 188, 212));
            public override Color ButtonPressedHighlightBorder => Pick(Color.FromArgb(88, 96, 108), SystemColors.Highlight);
            public override Color ButtonPressedGradientBegin => ButtonPressedHighlight;
            public override Color ButtonPressedGradientMiddle => ButtonPressedHighlight;
            public override Color ButtonPressedGradientEnd => ButtonPressedHighlight;
            public override Color CheckBackground => Pick(Color.FromArgb(70, 76, 86), Color.FromArgb(200, 215, 240));
            public override Color CheckPressedBackground => ButtonPressedHighlight;
            public override Color CheckSelectedBackground => MenuItemSelected;
            public override Color MenuItemBorder => Pick(Color.FromArgb(88, 96, 108), SystemColors.Highlight);
            public override Color MenuBorder => Pick(Color.FromArgb(88, 96, 108), SystemColors.ControlDark);
            public override Color SeparatorDark => Pick(Color.FromArgb(88, 96, 108), SystemColors.ControlDark);
            public override Color SeparatorLight => Pick(Color.FromArgb(60, 66, 74), SystemColors.ControlLight);
            public override Color StatusStripGradientBegin => MenuStripGradientBegin;
            public override Color StatusStripGradientEnd => MenuStripGradientEnd;
            public override Color ToolStripBorder => Pick(Color.FromArgb(88, 96, 108), SystemColors.ControlDark);
            public override Color ToolStripContentPanelGradientBegin => MenuStripGradientBegin;
            public override Color ToolStripContentPanelGradientEnd => MenuStripGradientEnd;
        }

        private static IEnumerable<T> FindControls<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                if (child is T match)
                    yield return match;

                foreach (var nested in FindControls<T>(child))
                    yield return nested;
            }
        }

        private static IEnumerable<Button> FindButtons(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child is Button btn)
                    yield return btn;

                foreach (var nested in FindButtons(child))
                    yield return nested;
            }
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

        private void OnDrawLabelCell(object? s, VirtualDisasmList.VirtualCellPaintEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _filtered.Count) return;
            var (name, addr) = _filtered[e.ItemIndex];
            DrawListCell(e, e.ColumnIndex == 0 ? $"{addr:X8}" : name);
        }

        private void OnDrawObjectCell(object? s, VirtualDisasmList.VirtualCellPaintEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _objects.Count) return;
            ObjectLabelDefinition definition = _objects[e.ItemIndex];
            DrawListCell(e, e.ColumnIndex == 0 ? $"{definition.StaticAddress:X8}" : definition.Label);
        }

        private void DrawListCell(VirtualDisasmList.VirtualCellPaintEventArgs e, string text)
        {
            bool selected = e.Selected;
            Color back = selected ? _selBack : _windowBack;
            Color fore = selected ? _selFore : _windowFore;
            using var br = new SolidBrush(back);
            e.Graphics.FillRectangle(br, e.Bounds);
            Rectangle rect = Rectangle.Inflate(e.Bounds, -2, 0);
            TextRenderer.DrawText(e.Graphics, text, _listFont, rect, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

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

        private void OnSearchChanged(object? s, EventArgs e)
        {
            string q = _search.Text.Trim().ToLowerInvariant();
            IEnumerable<(string Name, uint Address)> query = _allLabels;
            if (_cbLabelsOnly.Checked)
                query = query.Where(l => !(l.Name.Length >= 2 && l.Name[0] == '"' && l.Name[^1] == '"'));
            _filtered = string.IsNullOrEmpty(q)
                ? new List<(string, uint)>(query)
                : query.Where(l => l.Name.ToLowerInvariant().Contains(q)
                                || l.Address.ToString("X8").ToLowerInvariant().Contains(q))
                       .ToList();
            SortFiltered();
            PopulateLabelList();
        }

        private void PopulateLabelList()
        {
            uint keepAddr = CurrentSelectedAddress;
            _labelList.VirtualListSize = 0;
            _labelList.VirtualListSize = _filtered.Count;
            _countLabel.Text = _filtered.Count == _allLabels.Count
                ? $"{_allLabels.Count} labels"
                : $"{_filtered.Count} / {_allLabels.Count}";

            if (_filtered.Count > 0)
            {
                int idx = _filtered.FindIndex(x => x.Address == keepAddr);
                if (idx < 0) idx = _filtered.FindIndex(x => x.Address == InitialSelectedAddress);
                if (idx < 0) idx = 0;
                _labelList.SelectedIndices.Clear();
                _labelList.SelectedIndices.Add(idx);
                _labelList.EnsureVisible(idx);
            }
            UpdateColumnWidths();
        }

        private void PopulateObjectList()
        {
            uint keepAddr = _objectList.SelectedIndices.Count > 0 && _objectList.SelectedIndices[0] >= 0 && _objectList.SelectedIndices[0] < _objects.Count
                ? _objects[_objectList.SelectedIndices[0]].StaticAddress
                : 0u;

            _objectList.VirtualListSize = 0;
            _objectList.VirtualListSize = _objects.Count;
            if (_objects.Count > 0)
            {
                int idx = _objects.FindIndex(x => x.StaticAddress == keepAddr);
                if (idx < 0) idx = 0;
                _objectList.SelectedIndices.Clear();
                _objectList.SelectedIndices.Add(idx);
                _objectList.EnsureVisible(idx);
            }
            UpdateColumnWidths();
            UpdateObjectButtons();
        }

        private void RestoreLabelSelection()
        {
            if (_filtered.Count == 0) return;
            int idx = _filtered.FindIndex(x => x.Address == InitialSelectedAddress);
            if (idx < 0) idx = 0;
            _labelList.SelectedIndices.Clear();
            _labelList.SelectedIndices.Add(idx);
            _labelList.EnsureVisible(idx);
        }

        private void UpdateColumnWidths()
        {
            if (_labelList.Columns.Count >= 2)
            {
                int labelScrollbarWidth = _labelList.HasVerticalScrollbar ? SystemInformation.VerticalScrollBarWidth : 0;
                _labelList.Columns[1].Width = Math.Max(120, _labelList.ClientSize.Width - _labelList.Columns[0].Width - labelScrollbarWidth - 1);
            }

            if (_objectList.Columns.Count >= 2)
            {
                int objectScrollbarWidth = _objectList.HasVerticalScrollbar ? SystemInformation.VerticalScrollBarWidth : 0;
                _objectList.Columns[1].Width = Math.Max(120, _objectList.ClientSize.Width - _objectList.Columns[0].Width - objectScrollbarWidth - 1);
            }
        }

        private void OnSearchKeyDown(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && _labelList.VirtualListSize > 0)
            {
                _labelList.Focus();
                int idx = _labelList.SelectedIndices.Count > 0 ? _labelList.SelectedIndices[0] : 0;
                _labelList.SelectedIndices.Clear();
                _labelList.SelectedIndices.Add(idx);
                _labelList.EnsureVisible(idx);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                AcceptLabelSelection();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void OnLabelListKeyDown(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                AcceptLabelSelection();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void OnObjectListMouseDoubleClick(object? s, MouseEventArgs e)
        {
            var hit = _objectList.HitTest(e.Location);
            if (hit.Item == null)
                return;

            int idx = hit.Item.Index;
            if (idx < 0 || idx >= _objects.Count)
                return;

            _objectList.SelectedIndices.Clear();
            _objectList.SelectedIndices.Add(idx);
            _objectList.EnsureVisible(idx);

            if (hit.ColumnIndex == 0)
                AcceptObjectSelection(idx);
            else if (hit.ColumnIndex == 1)
                EditSelectedObject(idx);
        }

        private void AcceptLabelSelection()
        {
            if (_labelList.SelectedIndices.Count == 0)
                return;

            int idx = _labelList.SelectedIndices[0];
            if (idx < 0 || idx >= _filtered.Count)
                return;

            SelectedAddress = _filtered[idx].Address;
            if (NavigateToAddressCallback != null)
            {
                NavigateToAddressCallback(SelectedAddress);
                Close();
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void AcceptObjectSelection(int index)
        {
            if (index < 0 || index >= _objects.Count)
                return;

            SelectedAddress = _objects[index].StaticAddress;
            if (NavigateToAddressCallback != null)
            {
                NavigateToAddressCallback(SelectedAddress);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void AddObjectDefinition()
        {
            using var dlg = new AddObjectDefinitionDialog("Add Object Definition", "Add Object", string.Empty, ObjectDefinitionExampleText);
            dlg.ApplyThemeColors(_formBack, _formFore, _windowBack, _windowFore, _dark, _formBack, _formFore);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ObjectDefinition == null)
                return;

            ObjectLabelDefinition added = dlg.ObjectDefinition.Clone();
            _objects.Add(added);
            _objects.Sort((a, b) => a.StaticAddress.CompareTo(b.StaticAddress));
            PopulateObjectList();
            SelectObjectByStaticAddress(added.StaticAddress);
            UpdateObjects();
        }

        private void EditSelectedObject()
        {
            if (_objectList.SelectedIndices.Count == 0)
                return;

            EditSelectedObject(_objectList.SelectedIndices[0]);
        }

        private void EditSelectedObject(int index)
        {
            if (index < 0 || index >= _objects.Count)
                return;

            ObjectLabelDefinition existing = _objects[index];
            using var dlg = new AddObjectDefinitionDialog("Edit Object Definition", "Update", BuildDefinitionText(existing));
            dlg.ApplyThemeColors(_formBack, _formFore, _windowBack, _windowFore, _dark, _formBack, _formFore);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ObjectDefinition == null)
                return;

            ObjectLabelDefinition updated = dlg.ObjectDefinition.Clone();
            _objects[index] = updated;
            _objects.Sort((a, b) => a.StaticAddress.CompareTo(b.StaticAddress));
            PopulateObjectList();
            SelectObjectByStaticAddress(updated.StaticAddress);
            UpdateObjects();
        }

        private static string BuildDefinitionText(ObjectLabelDefinition definition)
        {
            return ObjectLabelDefinitionParser.BuildEditorDefinitionText(definition);
        }

        private void SelectObjectByStaticAddress(uint staticAddress)
        {
            int idx = _objects.FindIndex(x => x.StaticAddress == staticAddress);
            if (idx < 0)
                return;

            _objectList.SelectedIndices.Clear();
            _objectList.SelectedIndices.Add(idx);
            _objectList.EnsureVisible(idx);
        }

        private void DeleteSelectedObject()
        {
            if (_objectList.SelectedIndices.Count == 0)
                return;

            int idx = _objectList.SelectedIndices[0];
            if (idx < 0 || idx >= _objects.Count)
                return;

            _objects.RemoveAt(idx);
            PopulateObjectList();
            UpdateObjects();
        }

        private void UpdateObjects()
        {
            if (UpdateObjectsCallback == null)
                return;

            UpdateObjectsCallback(ObjectDefinitions);
        }

        private void UpdateObjectButtons()
        {
            bool hasSelection = _objectList.SelectedIndices.Count > 0;
            _btnDeleteObject.Enabled = hasSelection;
            ApplyButtonState(_btnDeleteObject);
        }
    }
}
