using System;
using System.Drawing;
using System.Windows.Forms;

namespace PS2Disassembler
{
    internal sealed class PineDebugWindow : Form
    {
        private readonly TextBox _logBox;
        private readonly Label _lblPineStatus;
        private readonly Label _lblMcpStatus;

        public PineDebugWindow()
        {
            Text = "Debug";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 360);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(520, 260);
            Padding = new Padding(0);

            // ── Top toolbar panel ────────────────────────────────────
            var toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(8, 6, 8, 6),
            };

            var btnClear = CreateToolButton("Clear", 0);
            btnClear.Click += (_, _) => _logBox.Clear();

            var btnCopy = CreateToolButton("Copy", 1);
            btnCopy.Click += (_, _) =>
            {
                if (!string.IsNullOrEmpty(_logBox.Text))
                    Clipboard.SetText(_logBox.Text);
            };

            // ── Status labels (right-aligned, stacked vertically) ────
            _lblPineStatus = new Label
            {
                Text = "PINE Server: Disconnected",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
            };

            _lblMcpStatus = new Label
            {
                Text = "MCP Server: Disconnected",
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
            };

            // Use a TableLayoutPanel on the right for the two labels
            var statusPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(0),
                Margin = new Padding(0),
            };
            statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusPanel.Controls.Add(_lblPineStatus, 0, 0);
            statusPanel.Controls.Add(_lblMcpStatus, 0, 1);

            toolbar.Controls.Add(btnClear);
            toolbar.Controls.Add(btnCopy);
            toolbar.Controls.Add(statusPanel);

            // ── Log text box ─────────────────────────────────────────
            _logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = false,
                Font = new Font("Tahoma", 8.25f),
                BorderStyle = BorderStyle.None,
            };

            // Add a 1px separator line between toolbar and log
            var separator = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
            };

            Controls.Add(_logBox);
            Controls.Add(separator);
            Controls.Add(toolbar);
        }

        private static Button CreateToolButton(string text, int index)
        {
            const int btnWidth = 72;
            const int btnHeight = 28;
            const int btnSpacing = 6;

            var btn = new Button
            {
                Text = text,
                Size = new Size(btnWidth, btnHeight),
                Location = new Point(8 + index * (btnWidth + btnSpacing), 6),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Regular),
            };
            btn.FlatAppearance.BorderSize = 1;
            return btn;
        }

        public void AppendLine(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLine), line);
                return;
            }
            _logBox.AppendText(line + Environment.NewLine);
        }

        /// <summary>
        /// Updates the PINE Server connection status label.
        /// </summary>
        public void SetPineStatus(bool connected)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool>(SetPineStatus), connected);
                return;
            }
            _lblPineStatus.Text = connected
                ? "PINE Server: Connected"
                : "PINE Server: Disconnected";
            _lblPineStatus.ForeColor = connected
                ? Color.FromArgb(80, 200, 120)
                : Color.FromArgb(220, 100, 100);
        }

        /// <summary>
        /// Updates the MCP Server connection status label.
        /// </summary>
        public void SetMcpStatus(bool connected)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool>(SetMcpStatus), connected);
                return;
            }
            _lblMcpStatus.Text = connected
                ? "MCP Server: Connected"
                : "MCP Server: Disconnected";
            _lblMcpStatus.ForeColor = connected
                ? Color.FromArgb(80, 200, 120)
                : Color.FromArgb(220, 100, 100);
        }
    }
}
