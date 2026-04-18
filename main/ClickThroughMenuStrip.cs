using System;
using System.Windows.Forms;

namespace PS2Disassembler
{
    internal sealed class ClickThroughMenuStrip : MenuStrip
    {
        private const int WM_MOUSEACTIVATE = 0x0021;
        private static readonly IntPtr MA_ACTIVATE = new IntPtr(1);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = MA_ACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }
    }
}
