using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Runtime;
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
        // ── State ─────────────────────────────────────────────────────────
        private byte[]?              _fileData;
        private ElfInfo?             _elfInfo;
        private List<SlimRow>            _rows       = new(); // instruction-only rows for display
        private Dictionary<uint, string> _autoLabels   = new(); // address → label from disassembler
        private Dictionary<uint, string> _userLabels   = new(); // address → user-defined label
        private Dictionary<uint, string> _userComments = new(); // address → user-defined comment
        private Dictionary<uint, string> _stringLabels = new(); // address → ASCII string from binary
        private Dictionary<uint, uint> _originalOpCode = new(); // address → original word before a user edit/NOP in the disassembler
        private List<(string Name, uint Address)> _cachedLabels = new(); // cached label browser data
        private Dictionary<uint, int>?   _addrToRow  = new(); // fast address → row index lookup (disabled for huge regions)

        // Jump-range highlight (rows visually skipped by the selected branch/jump)
        private int _jumpSkipStart = -1;
        private int _jumpSkipEnd   = -1;
        private int _highlightDestIdx = -1;
        private string _lastGotoAddress = "";

        // Cross-reference (xref) state
        private Dictionary<uint, uint[]> _xrefs            = new(); // target addr → compact caller array
        private CancellationTokenSource? _xrefCts;
        private bool                         _xrefAnalysisRunning;
        private bool                         _disassemblyRunning;
        private bool                         _queuedXrefAnalyze;
        private bool                         _queuedXrefAnalyzeQuiet = true;
        private bool                         _queuedXrefAnalyzeExtendedCleanup;
        private uint                         _xrefTarget    = 0;    // address currently being watched
        private int                          _xrefIdx       = 0;    // which caller we'll jump to next
        private uint                         _pendingNavAddr = 0;   // navigate here after next ReloadRows
        private int                          _pendingNavVisibleOffset = 0; // keep target on same visible row during navigation
        private bool                         _pendingNavCenter = false;
        private uint                 _baseAddr   = 0x00000000;
        private uint                 _disasmBase = 0x00000000; // start of disassembly region
        private uint                 _disasmLen  = 0x02000000; // 32 MB default window
        private string               _fileName   = "";
        private int                  _selRow     = -1;
        private readonly Stack<uint>  _navBack    = new(); // navigation history for Left-arrow GoBack (stores addresses)
        private bool _suppressHistoryPush;

        // ASCII byte bar above disassembler
        private Panel? _asciiBytesBar;

        private bool _showHex   = true;
        private bool _showBytes = false;
        private bool _useAbi    = true;
        private string? _currentProjectPath;

        // Hex panel is fully virtual — we never build ListViewItems upfront
        private int _hexRowCount  = 0;
        private int _hexViewOffset = 0; // byte offset into _fileData for the first visible hex row
        private DarkVScrollBar? _hexVScroll; // custom scrollbar for Memory View

        // Hex byte-level selection
        private enum HexSelMode { None, Hex, Ascii }
        private HexSelMode _hexSelMode = HexSelMode.None;
        private int _hexSelAnchor = -1;   // byte offset in _fileData where selection started
        private int _hexSelCurrent = -1;  // byte offset in _fileData where selection ends
        private bool _hexSelecting;       // true while mouse is held down

        // Background disassembly cancellation
        private CancellationTokenSource? _disCts;

        // Live PCSX2 refresh
        private System.Windows.Forms.Timer? _liveTimer;
        private bool _liveReading; // true while a background read is in flight
        private int _reinterpretNesting; // skip live-row refresh while hotkey reinterpretation is active
        private uint _liveProcId;
        private long _eeHostAddr;
        private System.Windows.Forms.Timer? _reinterpretTimer;
        private DisplayReinterpret? _heldReinterpretMode;
        private readonly PineIpcClient _pine = new();
        private bool _pineAvailable;
        private DateTime _nextPineRetryUtc = DateTime.MinValue;
        private PineDebugWindow? _pineDebugWindow;
        private readonly Queue<string> _pineLogBacklog = new();
        private const int PineLogBacklogMax = 512;

        // Optional PCSX2 debug server support (required for breakpoints/step/watchpoints).
        private readonly DebugServerClient _debugServer = new();
        private bool _debugServerAvailable;
        private DateTime _nextDebugServerRetryUtc = DateTime.MinValue;
        private DateTime _nextDebuggerPollUtc = DateTime.MinValue;
        private const int DebuggerPollIntervalMs = 150;
        private const uint WatchpointSizeBytes = 1u;
        private const uint EeCurrentThreadIdAddress = 0x000125ECu;
        private const uint EeThreadControlBlockBaseAddress = 0x00017400u;
        private const int EeThreadControlBlockSize = 0x50;
        private const int EeMaxThreadCount = 256;
        private readonly HashSet<uint> _userBreakpoints = new();
        private uint? _activeBreakpointAddress;
        private bool _activeBreakpointIsWatchpoint;
        private bool _lastDebuggerPaused;
        private uint _lastDebuggerPc;
        private long _lastDebuggerCycles;
        private bool _pausedBreakpointUiLatched;
        private uint? _pausedBreakpointUiAddress;
        private bool _pausedBreakpointUiIsWatchpoint;
        private int _pausedBreakpointUiRunningPolls;
        private bool _breakpointUiFrozen;
        private uint? _breakpointUiFrozenAddress;
        private bool _breakpointUiFrozenIsWatchpoint;
        private uint? _readMemcheckAddress;
        private uint? _writeMemcheckAddress;
        private long _readMemcheckHits = -1;
        private long _writeMemcheckHits = -1;
        private bool _watchpointsSuspended;
        // ── Access Monitor (non-breaking memory access logger) ──
        private bool _accessMonitorActive;
        private bool _accessMonitorPassiveMode;
        private bool _accessMonitorMemcheckInstalled;
        private bool _accessMonitorNeedsRearm;
        private uint _accessMonitorAddress;
        private uint _accessMonitorSizeBytes = 4u;
        private string _accessMonitorType = "readwrite"; // "read", "write", or "readwrite"
        private readonly Dictionary<uint, long> _accessMonitorHits = new(); // PC → hit count
        private readonly Dictionary<uint, uint> _accessMonitorParents = new(); // PC → parent (return address)
        private readonly List<uint> _accessMonitorPcOrder = new(); // stable first-seen order
        private readonly List<AccessMonitorRow> _accessMonitorRows = new();
        private DateTime _accessMonitorNextUiRefresh = DateTime.MinValue;
        private DateTime _accessMonitorNextServerPollUtc = DateTime.MinValue;
        private DateTime _accessMonitorRearmUtc = DateTime.MinValue;
        private DateTime _accessMonitorBurstWindowUtc = DateTime.MinValue;
        private long _accessMonitorLastObservedHits = -1;
        private uint _accessMonitorLastObservedPc;
        private uint _accessMonitorBurstPc;
        private int _accessMonitorBurstHitCount;
        private int _accessMonitorPassiveMissingPolls;
        private int _accessMonitorPassiveFailurePolls;
        private Form? _accessMonitorForm;
        private FindDialog? _findDialog;
        private VirtualDisasmList? _accessMonitorList;
        private Label? _accessMonitorStatusLabel;
        private sealed class AccessMonitorRow
        {
            public uint Pc;
            public long Count;
            public uint Parent;
            public string Instruction = string.Empty;
        }
        private int _lastVisibleDisasmChangeCount;
        private sealed class VisibleLiveSnapshot
        {
            public int DisasmStartRow;
            public int DisasmEndRow;
            public int DisasmReadStartOffset;
            public int DisasmReadLength;
            public int HexStartOffset;
            public int HexLength;
        }
        private DateTime _nextPineVisibleReadLogUtc = DateTime.MinValue;
        private string _lastStatusText = string.Empty;
        private const int DefaultLiveRefreshIntervalMs = 1000 / AppSettings.DefaultRefreshRate;

        // Inline editing
        private TextBox? _inlineEdit;
        private int      _inlineRow = -1;
        private int      _inlineCol = -1;

        // ── Colours / Theme ───────────────────────────────────────────────
        private enum AppTheme { Light, Dark }

        private AppTheme _currentTheme = AppTheme.Dark;
        private ToolStripMenuItem? _miThemeLight;
        private ToolStripMenuItem? _miThemeDark;

        private Color ColBg       = Color.FromArgb(200, 216, 240);
        private Color ColSkip     = Color.FromArgb(130, 170, 215);
        private Color ColXref     = Color.FromArgb(180, 180, 180);
        private Color ColComment  = Color.FromArgb(80, 130, 80);
        private Color ColHexBg    = Color.White;
        private Color ColSel      = Color.FromArgb(0, 0, 128);
        private Color ColSelFg    = Color.White;
        private Color ColAddr     = Color.FromArgb(128, 0, 0);
        private Color ColHexFg    = Color.FromArgb(128, 0, 0);
        private Color ColLabel    = Color.FromArgb(128, 0, 0);
        private Color ColNop      = Color.Gray;
        private Color ColBranch   = Color.FromArgb(0, 110, 0);
        private Color ColJump     = Color.FromArgb(170, 0, 0);
        private Color ColCall     = Color.FromArgb(170, 0, 0);
        private Color ColFpu      = Color.FromArgb(0, 0, 180);
        private Color ColMmi      = Color.FromArgb(90, 0, 130);
        private Color ColSys      = Color.FromArgb(130, 80, 0);
        private Color ColData     = Color.FromArgb(100, 100, 100);
        private Color ColMem      = Color.FromArgb(0, 80, 160);
        private Color ColAscii    = Color.FromArgb(0, 100, 0);
        private Color ColZeroByte = Color.FromArgb(200, 200, 200);

        private Color _headerBack = SystemColors.Control;
        private Color _headerFore = SystemColors.ControlText;
        private Color _headerBorder = SystemColors.ControlDark;
        private Color _themeFormBack = SystemColors.Control;
        private Color _themeFormFore = SystemColors.ControlText;
        private Color _themeWindowBack = Color.White;
        private Color _themeWindowFore = Color.Black;
        private Color _themeEditValidBack = Color.FromArgb(255, 255, 200);
        private Color _themeEditInvalidBack = Color.FromArgb(255, 160, 160);
        private Color _themeHighlightDest = Color.FromArgb(205, 225, 255);
        private Color _themeBreakpointBack = Color.FromArgb(0, 180, 0);
        private Color _themeBreakpointActiveBack = Color.FromArgb(220, 0, 0);
        private Color _themeTitleBarBack = SystemColors.ControlDarkDark;
        private Color _themeTitleBarText = Color.White;
        private Color _themeCodeManagerRtbBack = Color.White; // lighter bg for RichTextBoxes inside Code Manager so they stand out
        private Color _themeCodeManagerBack = Color.White; // Code Manager panel background (light grey in light theme)

        private Color tokenRegisterAColor;
        private Color tokenRegisterTColor;
        private Color tokenRegisterVColor;
        private Color tokenRegisterSColor;
        private Color tokenRegisterSPColor;
        private Color tokenRegisterRAColor;
        private Color tokenRegisterFColor;
        private Color tokenRegisterGPColor;
        private Color tokenRegisterKColor;
        private Color tokenRegisterATColor;
        private Color tokenRegisterZeroColor;
        private Color tokenRegisterFPColor;
        private Color tokenRegisterOtherColor;

        // ── Fonts ─────────────────────────────────────────────────────────
        private const string DisasmFontFamily = "Liberation Mono"; //Fira Code //Courier New //Cascadia Code
        private const float DisasmFontSize = 9f;
        private Font _mono       = new(DisasmFontFamily, DisasmFontSize, FontStyle.Regular);
        private const int DisasmTextVerticalOffset = 0;
        private int _disasmRowHeight = 16; // desired native ListView row height; note Windows will not shrink below the font's own minimum
        private AppSettings _appSettings = new();

        private int CurrentDisasmRowHeight => _disasmRowHeight > 0 ? _disasmRowHeight : _mono.Height;

        private const int PreservedKernelWindowLength = 0x00010000;
        private byte[]? _preservedKernelWindow;

        // ── Controls ──────────────────────────────────────────────────────
        private readonly VirtualDisasmList     _disasmList;
        private LabelsWindowDialog?            _labelsWindow;
        private string _labelBrowserFilter = string.Empty;
        private bool _labelBrowserLabelsOnly;
        private uint _labelBrowserSelectedAddress;
        private readonly VirtualDisasmList    _hexList;
        private readonly FlatTabHost          _mainTabs;   // Disassembler | Memory View | Code Manager
        private readonly FlatTabPage          _memoryViewPage;
        private readonly StatusStrip          _statusStrip;
        private readonly ToolStripStatusLabel _sbInfo;
        private readonly ToolStripStatusLabel _sbAddr;
        private readonly ToolStripStatusLabel _sbSize;
        private readonly ToolStripStatusLabel _sbMode;
        private readonly ToolStripStatusLabel  _sbProgress;
        private readonly ToolStripProgressBar _progressBar;
        private readonly ToolStripMenuItem    _miSave;
        private readonly MenuStrip            _menuBar;
        private readonly ToolStripLabel       _menuStatusSpring;
        private readonly ToolStripLabel       _menuStatusLabel;
        private ToolStripMenuItem?            _miBreakpointsSidebar;
        private ToolStripMenuItem?            _miSetBreakpoint;
        private ToolStripMenuItem?            _miClearBreakpoints;
        private ToolStripMenuItem?            _miBreakpointsMenu;
        private ToolStripMenuItem?            _miCtxSetPcBreakpoint;
        private ToolStripMenuItem?            _miCtxSetReadBreakpoint;
        private ToolStripMenuItem?            _miCtxSetWriteBreakpoint;
        private ToolStripMenuItem?            _miCtxClearBreakpoints;
        private ToolStripMenuItem?            _miCtxMonitorAccess;
        private ToolStripMenuItem?            _miCtxNopOpcode;
        private ToolStripMenuItem?            _miCtxRestoreOriginalOpcode;
        private ToolStripSeparator?           _miCtxEditSeparator;
        private ToolStripSeparator?           _miCtxBreakpointSeparator;
        private ToolStripSeparator?           _miCtxMonitorSeparator;
        private SplitContainer?               _disasmBreakpointSplit;
        private Panel?                        _breakpointsPanel;
        private int                           _breakpointSidebarPreferredWidth = BreakpointSidebarFixedWidth;
        private bool                          _updatingBreakpointSidebarSplitter;
        private Button?                       _btnContinueEmu;
        private Button?                       _btnStep;
        private Button?                       _btnStepOver;
        private CheckBox?                     _chkReadBreakpoint;
        private TextBox?                      _txtReadBreakpoint;
        private CheckBox?                     _chkWriteBreakpoint;
        private TextBox?                      _txtWriteBreakpoint;
        private ThemedCallStackTextBox?      _txtCallStack;
        private VirtualDisasmList?            _fprList;
        private readonly List<(string Reg, string Value, bool IsFloat)> _fprRows = new();
        private readonly System.Windows.Forms.Timer _activityStatusResetTimer;
        private Font?                         _menuStatusBoldFont;
        private bool                          _menuPauseStatusActive;
        private string                        _menuPauseStatusSavedText = "Ready";
        private Color                         _menuPauseStatusSavedColor = SystemColors.ControlText;
        private Font?                         _menuPauseStatusSavedFont;
        private bool _addrIndexDirty = false;
        private const int AddrIndexMaxRows = 250000;
        private const int BreakpointSidebarMainMinWidth = 260; //260
        private const int BreakpointSidebarPanelMinWidth = 160; //220
        private const int BreakpointSidebarFixedWidth = 173;
        private ToolStripMenuItem? _miAttach;
        private ToolStripMenuItem? _miDetach;

        // Column index helpers
        private int LblCol => 1 + (_showHex ? 1 : 0) + (_showBytes ? 1 : 0);
        private int CmdCol => LblCol + 1;

        // ── Constructor ───────────────────────────────────────────────────

        private bool _managedTrimQueued;
        private int _managedTrimGeneration;

        private static void ForceManagedMemoryTrim()
        {
            try
            {
                // Trim genuinely dead managed objects, but do not force the process working
                // set down with EmptyWorkingSet(). That API makes Task Manager numbers swing
                // wildly and hides the app's real steady-state footprint behind artificial
                // page-outs.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        private void QueueManagedMemoryTrim(int delayMs = 0, int generation = -1)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            if (delayMs > 0)
            {
                int delayedExpectedGeneration = generation >= 0 ? generation : _managedTrimGeneration;
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delayMs);
                    }
                    catch
                    {
                        return;
                    }

                    if (IsDisposed || !IsHandleCreated || delayedExpectedGeneration != _managedTrimGeneration)
                        return;

                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (!IsDisposed && delayedExpectedGeneration == _managedTrimGeneration)
                                ForceManagedMemoryTrim();
                        }));
                    }
                    catch
                    {
                    }
                });

                return;
            }

            if (_managedTrimQueued)
                return;

            int expectedGeneration = generation >= 0 ? generation : _managedTrimGeneration;
            _managedTrimQueued = true;
            BeginInvoke((Action)(() =>
            {
                _managedTrimQueued = false;
                if (!IsDisposed && expectedGeneration == _managedTrimGeneration)
                    ForceManagedMemoryTrim();
            }));
        }

        private void QueueManagedMemoryTrimBurst(bool extended = false)
        {
            int generation = ++_managedTrimGeneration;
            QueueManagedMemoryTrim(generation: generation);
            QueueManagedMemoryTrim(delayMs: 750, generation: generation);
            if (extended)
                QueueManagedMemoryTrim(delayMs: 1500, generation: generation);
        }

        public MainForm(string? startFile = null)
        {
            SuspendLayout();
            Text          = "ps2dis#";
            Size          = new Size(900, 700); //1120, 740
            MinimumSize   = new Size(600, 400); //700, 500
            StartPosition = FormStartPosition.CenterScreen;
            Font          = new Font("Tahoma", 8.25f);
            AllowDrop     = true;

            // Set application icon
            try
            {
                string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (System.IO.File.Exists(iconPath))
                    Icon = new Icon(iconPath);
            }
            catch { /* non-fatal */ }

            // ── Menu ──────────────────────────────────────────────────────
            _menuBar = new ClickThroughMenuStrip();

            var fileMenu = new ToolStripMenuItem("File");
            fileMenu.DropDownItems.Add("Open Binary",             null, (_, _) => OpenBinary());
            _miSave = new ToolStripMenuItem("Save Disassembly",        null, (_, _) => SaveDisasm()) { Enabled = false };
            fileMenu.DropDownItems.Add(_miSave);
            fileMenu.DropDownItems.Add("Import Labels",       null, (_, _) => ImportLabelsFromElf());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            _miAttach = new ToolStripMenuItem("Attach to PCSX2",       null, (_, _) => AttachToPcsx2());
            _miDetach = new ToolStripMenuItem("Detach from PCSX2",     null, (_, _) => DetachFromPcsx2()) { Enabled = false };
            fileMenu.DropDownItems.Add(_miAttach);
            fileMenu.DropDownItems.Add(_miDetach);
            fileMenu.DropDownItems.Add("Open PCSX2dis Project",          null, (_, _) => OpenPcsx2DisProject());
            fileMenu.DropDownItems.Add("Save PCSX2dis Project",          null, (_, _) => QuickSaveProject());
            fileMenu.DropDownItems.Add("Save PCSX2dis Project as..",     null, (_, _) => SavePcsx2DisProjectAs());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("Exit",                               null, (_, _) => Close());

            var editMenu = new ToolStripMenuItem("Edit");
            editMenu.DropDownItems.Add("Labels",               null, (_, _) => ShowLabelBrowser());
            editMenu.DropDownItems.Add("Go to Address",  null, (_, _) => ShowGoto());
            editMenu.DropDownItems.Add("Find",                 null, (_, _) => ShowFind());
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add("Add / Edit Label",          null, (_, _) => AddOrEditLabel());
            editMenu.DropDownItems.Add("Set Disasm Region",             null, (_, _) => ShowSetRegion());
            editMenu.DropDownItems.Add(new ToolStripSeparator());
            editMenu.DropDownItems.Add("Options",                    null, (_, _) => ShowOptionsDialog());

            var viewMenu = new ToolStripMenuItem("View");
            viewMenu.DropDownItems.Add("Go to Start",       null, (_, _) => SelectRow(0));
            viewMenu.DropDownItems.Add("Go to End",         null, (_, _) => SelectRow(_rows.Count - 1));
            viewMenu.DropDownItems.Add("Go to Entry Point", null, (_, _) => GotoEntry());

            var anaMenu = new ToolStripMenuItem("Analyzer");
            anaMenu.DropDownItems.Add("Invoke Analyzer", null, (_, _) => RunXrefAnalyzer());
            anaMenu.DropDownItems.Add("Debug Window", null, (_, _) => ShowPineDebugWindow());

            var breakpointsMenu = new ToolStripMenuItem("Breakpoints");
            _miBreakpointsMenu = breakpointsMenu;
            breakpointsMenu.Enabled = false; // disabled until attached to PCSX2
            breakpointsMenu.Visible = false; // invisible until attached to PCSX2
            _miBreakpointsSidebar = new ToolStripMenuItem("Breakpoints") { CheckOnClick = true };
            _miBreakpointsSidebar.CheckedChanged += (_, _) => SetBreakpointSidebarVisible(_miBreakpointsSidebar.Checked);
            _miSetBreakpoint = new ToolStripMenuItem("Set Breakpoint", null, (_, _) => SetBreakpointOnSelectedRow());
            _miClearBreakpoints = new ToolStripMenuItem("Clear All Breakpoints", null, (_, _) => ClearAllBreakpoints());
            breakpointsMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                _miBreakpointsSidebar,
                _miSetBreakpoint,
                _miClearBreakpoints,
            });

            _menuStatusSpring = new ToolStripLabel
            {
                AutoSize = false,
                Overflow = ToolStripItemOverflow.Never,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Enabled = false
            };
            _menuStatusLabel = new ToolStripLabel("Ready")
            {
                Alignment = ToolStripItemAlignment.Right,
                AutoSize = false,
                Width = 420,
                Overflow = ToolStripItemOverflow.Never,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0, 1, 6, 2)
            };
            _menuStatusBoldFont = new Font(_menuStatusLabel.Font ?? Font, FontStyle.Bold);
            _menuPauseStatusSavedFont = _menuStatusLabel.Font;
            _menuPauseStatusSavedColor = _menuStatusLabel.ForeColor;
            _activityStatusResetTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _activityStatusResetTimer.Tick += (_, _) =>
            {
                _activityStatusResetTimer.Stop();
                if (!IsDisposed && _menuStatusLabel.Text != "Ready")
                    SetActivityStatus("Ready");
            };

            _menuBar.Items.AddRange(new ToolStripItem[]
                { fileMenu, editMenu, viewMenu, anaMenu, breakpointsMenu, _menuStatusSpring, _menuStatusLabel });
            MainMenuStrip = _menuBar;
            _menuBar.MouseDown += OnMenuBarMouseDown;
            _menuBar.SizeChanged += (_, _) => UpdateMenuStatusLayout();
            UpdateMenuStatusLayout();


            // ── Hex panel (fully virtual) ──────────────────────────────────
            _hexList = new VirtualDisasmList
            {
                View          = View.Details,
                FullRowSelect = false,
                GridLines     = false,
                Font          = _mono,
                BackColor     = ColHexBg,
                ForeColor     = Color.Black,
                HeaderStyle   = ColumnHeaderStyle.None,
                HeaderHeight  = 0,
                MultiSelect   = false,
                Dock          = DockStyle.Fill,
                BorderStyle   = BorderStyle.None,
                OwnerDraw     = true,
                VirtualMode   = true,
                Scrollable    = true,
                SuppressDefaultSelection = true,
            };
            _hexList.Columns.Add("Address", 88);
            for (int i = 0; i < 16; i++)
                _hexList.Columns.Add("", 20);
            _hexList.Columns.Add("", 150);
            _hexList.DrawCell             += DrawHexCell;
            _hexList.SelectedIndexChanged += OnHexSelChanged;
            _hexList.MouseDown            += OnHexMouseDown;
            _hexList.MouseMove            += OnHexMouseMove;
            _hexList.MouseUp              += OnHexMouseUp;
            _hexList.KeyDown              += OnHexKeyDown;
            var hexCtx = new ContextMenuStrip();
            hexCtx.Items.Add("Copy", null, (_, _) => CopyHexSelection());
            hexCtx.Items.Add("Go to in Disassembler", null, (_, _) => GoToHexSelectionInDisassembler());
            _hexList.ContextMenuStrip = hexCtx;
            ApplyHexViewMetrics();

            // ── Disasm panel (virtual + owner-draw) ────────────────────────
            _disasmList = new VirtualDisasmList
            {
                View          = View.Details,
                FullRowSelect = true,
                GridLines     = false,
                Font          = _mono,
                BackColor     = ColBg,
                ForeColor     = Color.Black,
                HeaderStyle   = ColumnHeaderStyle.Nonclickable,
                MultiSelect   = false,
                Dock          = DockStyle.Fill,
                VirtualMode   = true,
                BorderStyle   = BorderStyle.None,
                OwnerDraw     = true,
            };
            RebuildColumns();
            _disasmList.RetrieveVirtualItem   += OnRetrieveVirtualItem;
            _disasmList.DrawHeader           += DrawDisasmHeader;
            _disasmList.DrawCell             += DrawDisasmCell;
            _disasmList.SelectedIndexChanged += OnDisasmSelChanged;
            _disasmList.MouseDown            += OnDisasmMouseDown;
            _disasmList.MouseDoubleClick     += OnDisasmMouseDoubleClick;
            _disasmList.KeyDown              += OnListKeyDown;
            _disasmList.KeyUp                += OnListKeyUp;
            _disasmList.ColumnWidthChanged   += (_, _) => UpdateDisassemblyColumnWidths();

            _reinterpretTimer = new System.Windows.Forms.Timer { Interval = 20 };
            _reinterpretTimer.Tick += (_, _) =>
            {
                if (_heldReinterpretMode.HasValue)
                    ConvertSelection(_heldReinterpretMode.Value, advanceSelection: true);
            };

            var ctx = new ContextMenuStrip();
            var miCtxFreeze = new ToolStripMenuItem("Freeze", null, (_, _) => FreezeSelectedRowToCodes());
            ctx.Items.Add(miCtxFreeze);
            ctx.Items.Add("Copy Line", null, (_, _) => CopyLineForSelectedRow());
            ctx.Items.Add("Go to in Memory View", null, (_, _) => GoToSelectedRowInMemoryView());
            _miCtxEditSeparator = new ToolStripSeparator();
            ctx.Items.Add(_miCtxEditSeparator);
            _miCtxNopOpcode = new ToolStripMenuItem("NOP Op-Code", null, (_, _) => NopSelectedOpcode());
            _miCtxRestoreOriginalOpcode = new ToolStripMenuItem("Restore original Op-Code", null, (_, _) => RestoreOriginalOpcodeForSelectedRow());
            ctx.Items.Add(_miCtxNopOpcode);
            ctx.Items.Add(_miCtxRestoreOriginalOpcode);
            _miCtxBreakpointSeparator = new ToolStripSeparator();
            ctx.Items.Add(_miCtxBreakpointSeparator);
            _miCtxSetPcBreakpoint = new ToolStripMenuItem("Set PC Breakpoint", null, (_, _) => SetBreakpointOnSelectedRow());
            _miCtxSetReadBreakpoint = new ToolStripMenuItem("Set Read Breakpoint", null, (_, _) => SetMemoryBreakpointOnSelectedRow(isRead: true));
            _miCtxSetWriteBreakpoint = new ToolStripMenuItem("Set Write Breakpoint", null, (_, _) => SetMemoryBreakpointOnSelectedRow(isRead: false));
            _miCtxClearBreakpoints = new ToolStripMenuItem("Clear All Breakpoints", null, (_, _) => ClearAllBreakpoints());
            ctx.Items.Add(_miCtxSetPcBreakpoint);
            ctx.Items.Add(_miCtxSetReadBreakpoint);
            ctx.Items.Add(_miCtxSetWriteBreakpoint);
            ctx.Items.Add(_miCtxClearBreakpoints);
            _miCtxMonitorSeparator = new ToolStripSeparator();
            ctx.Items.Add(_miCtxMonitorSeparator);
            _miCtxMonitorAccess = new ToolStripMenuItem("Monitor Access", null, (_, _) => StartAccessMonitorOnSelectedRow());
            ctx.Items.Add(_miCtxMonitorAccess);
            ctx.Opening += (_, _) =>
            {
                bool attached = IsLiveAttached();
                bool hasSelection = _selRow >= 0 && _selRow < _rows.Count;
                miCtxFreeze.Visible = attached;
                if (_miCtxEditSeparator != null) _miCtxEditSeparator.Visible = hasSelection;
                if (_miCtxNopOpcode != null) _miCtxNopOpcode.Enabled = hasSelection;
                if (_miCtxRestoreOriginalOpcode != null)
                {
                    _miCtxRestoreOriginalOpcode.Enabled = hasSelection && HasOriginalOpcodeForAddress(_rows[_selRow].Address);
                }
                if (_miCtxBreakpointSeparator != null) _miCtxBreakpointSeparator.Visible = attached;
                if (_miCtxSetPcBreakpoint != null) _miCtxSetPcBreakpoint.Visible = attached;
                if (_miCtxSetReadBreakpoint != null) _miCtxSetReadBreakpoint.Visible = attached;
                if (_miCtxSetWriteBreakpoint != null) _miCtxSetWriteBreakpoint.Visible = attached;
                if (_miCtxClearBreakpoints != null) _miCtxClearBreakpoints.Visible = attached;
                if (_miCtxMonitorSeparator != null) _miCtxMonitorSeparator.Visible = attached;
                if (_miCtxMonitorAccess != null) _miCtxMonitorAccess.Visible = attached;
            };
            _disasmList.ContextMenuStrip = ctx;

            // ── Main Tabs: Disassembler | Memory View | Code Manager ─
            _mainTabs = new FlatTabHost
            {
                Dock = DockStyle.Fill,
            };
            _mainTabs.SelectedIndexChanged += (_, _) =>
            {
                int idx = _mainTabs.SelectedIndex;
                // Recalculate hex rows when switching to Memory View
                if (idx == 1)
                {
                    BeginInvoke((Action)(() =>
                    {
                        AdjustHexSplitter();
                        // Read only the visible hex range from live memory on tab switch
                        if (_fileData != null && _pineAvailable && EnsurePineConnected())
                        {
                            int startOffset = _hexList.TopIndex * 16;
                            int length = Math.Max(16, _hexRowCount * 16);
                            if (startOffset >= 0 && startOffset + length <= _fileData.Length)
                            {
                                try
                                {
                                    byte[] data = _pine.ReadMemory(OffsetToPineAddress(startOffset), length);
                                    if (data != null && data.Length > 0)
                                    {
                                        int copyLen = Math.Min(data.Length, _fileData.Length - startOffset);
                                        if (!IsSuspiciousAllZeroPineRead(data, _fileData, startOffset, copyLen))
                                            Buffer.BlockCopy(data, 0, _fileData, startOffset, copyLen);
                                    }
                                }
                                catch { /* non-fatal */ }
                            }
                            UpdateHexScrollBar();
                            _hexList?.Invalidate();
                        }
                    }));
                }
                // Lazy-init Code Manager on first visit — avoids allocating the dialog at startup
                if (idx == 2)
                    BeginInvoke((Action)(() => EnsureCodeToolsDialog()));
            };

            var tpDisasm  = new FlatTabPage("Disassembler") { Padding = new Padding(0), Margin = new Padding(0) };
            _memoryViewPage = new FlatTabPage("Memory View") { Padding = new Padding(0), Margin = new Padding(0) };
            var tpCodes   = new FlatTabPage("Code Manager") { Padding = new Padding(0), Margin = new Padding(0), Tag = "CodeManagerHostPage" };
            _mainTabs.AddPage(tpDisasm);
            _mainTabs.AddPage(_memoryViewPage);
            _mainTabs.AddPage(tpCodes);
            _mainTabs.SetTabEnabled(2, false); // Code Manager disabled until attached to PCSX2
            _mainTabs.SetTabVisible(2, false); // and invisible until attached

            // Disassembler tab: ASCII byte bar + disassembly view fills the page.
            _asciiBytesBar = new DoubleBufferedPanel
            {
                Dock = DockStyle.Top,
                Height = CurrentDisasmRowHeight,
                BackColor = ColHexBg,
            };
            _asciiBytesBar.Paint += PaintAsciiBytesBar;
            _disasmList.Dock = DockStyle.Fill;
            tpDisasm.Controls.Add(_disasmList);
            tpDisasm.Controls.Add(_asciiBytesBar);

            // Memory View tab: hex list fills the tab and uses the same custom virtual control model
            // as the disassembler, which avoids native ListView hover/scroll repaint churn.
            _hexList.Dock = DockStyle.Fill;
            _memoryViewPage.Controls.Add(_hexList);

            // Code Manager tab: panel that will host CodeToolsDialog's inner TabControl
            var codeManagerPanel = new Panel { Dock = DockStyle.Fill, Tag = "CodeManagerPanel", Margin = new Padding(0), Padding = new Padding(1) };
            tpCodes.Controls.Add(codeManagerPanel);

            // ── Status bar ────────────────────────────────────────────────
            _statusStrip = new StatusStrip();
            _sbInfo = new ToolStripStatusLabel("No file loaded \u2014 drag & drop a binary/ELF/dump, or use File > Open")
                { Spring = true, TextAlign = ContentAlignment.MiddleLeft, BorderSides = ToolStripStatusLabelBorderSides.Right };
            _sbAddr = new ToolStripStatusLabel("00000000")
                { AutoSize = false, Width = 95, BorderSides = ToolStripStatusLabelBorderSides.Right };
            _sbSize = new ToolStripStatusLabel("0 bytes")
                { AutoSize = false, Width = 80, BorderSides = ToolStripStatusLabelBorderSides.Right };
            _sbMode = new ToolStripStatusLabel("EE / R5900")
                { AutoSize = false, Width = 90, BorderSides = ToolStripStatusLabelBorderSides.Right };
            _sbProgress = new ToolStripStatusLabel("")
                { AutoSize = false, Width = 120 };
            _progressBar = new ToolStripProgressBar
                { AutoSize = false, Width = 120, Visible = false, Minimum = 0, Maximum = 100 };
            _statusStrip.Items.AddRange(new ToolStripItem[]
                { _sbInfo, _sbAddr, _sbSize, _sbMode, _sbProgress, _progressBar });

            _statusStrip.Visible = false;

            _disasmBreakpointSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BorderStyle = BorderStyle.None,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel2,
            };
            _disasmBreakpointSplit.Resize += (_, _) => MaintainBreakpointSidebarWidth();
            _disasmBreakpointSplit.SplitterMoved += (_, _) => CaptureBreakpointSidebarWidthFromSplitter();
            _breakpointsPanel = CreateBreakpointsSidebar();
            _breakpointsPanel.Dock = DockStyle.Fill;
            _disasmBreakpointSplit.Panel1.Controls.Add(_mainTabs);
            _disasmBreakpointSplit.Panel2.Controls.Add(_breakpointsPanel);
            _disasmBreakpointSplit.Panel2Collapsed = true;

            Controls.Add(_disasmBreakpointSplit);
            Controls.Add(_statusStrip);
            Controls.Add(_menuBar);

            SetReadyStatus();

            KeyPreview = true;
            KeyDown   += OnFormKeyDown;
            KeyUp     += OnFormKeyUp;
            Shown     += (_, _) =>
            {
                InitializeHexPanelForFourRows();
                AdjustHexSplitter();
                // Re-apply scrollbar theme now that all control handles are created
                ApplyScrollbarTheme(this, _currentTheme == AppTheme.Dark);
                RefreshTitleBarTheme(forceFrameRefresh: true);
            };
            Resize    += (_, _) => AdjustHexSplitter();
            _disasmList.Resize += (_, _) => UpdateDisassemblyColumnWidths();
            _hexList.Resize    += (_, _) => AdjustHexSplitter();

            DragEnter += (_, e) => e.Effect = e.Data!.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop  += (_, e) =>
            {
                if (e.Data!.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0)
                    LoadFile(f[0], isRawDump: false);
            };

            // Load persisted settings before applying theme
            _appSettings = AppSettings.Load();
            ApplyFontFromSettings(_appSettings.FontFamily, _appSettings.FontSize);
            var startupTheme = _appSettings.Theme == "Light" ? AppTheme.Light : AppTheme.Dark;
            ApplyTheme(startupTheme);
            ResumeLayout();

            // Apply theme again once handles exist so SetWindowTheme takes effect on all controls
            Load += (_, _) => { ApplyTheme(_currentTheme, forceFrameRefresh: true); };

            if (startFile != null)
                Load += (_, _) => LoadFile(startFile, isRawDump: false);
        }



        // MainForm is intentionally split across partial files:
        // - MainForm.Debugger.cs
        // - MainForm.Theme.cs
        // - MainForm.FileAndLive.cs
        // - MainForm.Disassembly.cs
        // - MainForm.CodeTools.cs
    }
}
