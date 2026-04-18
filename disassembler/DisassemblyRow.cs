using System;

namespace PS2Disassembler
{
    /// <summary>Type of instruction for colour-coding in the GUI.</summary>
    public enum InstructionType : byte
    {
        Normal, Nop, Branch, Jump, Call, Return,
        Load, Store, Alu, Fpu, Mmi, System, Data, Label
    }

    /// <summary>
    /// Sub-type of a Data row.  Stored in <see cref="SlimRow.DataSub"/>.
    /// Replaces the stored Mnemonic/Operands strings that used to eat 16 bytes per row.
    /// </summary>
    public enum DataKind : byte
    {
        None  = 0,   // instruction row  — mnemonic decoded on demand, never stored
        Word  = 1,   // .word
        Half  = 2,   // .half
        Byte  = 3,   // .byte
        Float = 4,   // .float
    }

    /// <summary>
    /// Compact 16-byte storage row used in the main <c>_rows</c> list.
    /// Halves the footprint of the 8-million-row EE RAM listing compared with
    /// <see cref="DisassemblyRow"/> (32 bytes).  Mnemonic and Operands are computed
    /// on demand by <c>ResolveRowForDisplay</c>; they are never stored here.
    /// </summary>
    internal readonly struct SlimRow
    {
        public uint            Address { get; init; }   // 4
        public uint            Word    { get; init; }   // 4
        public InstructionType Kind    { get; init; }   // 1
        public DataKind        DataSub { get; init; }   // 1   (only when Kind == Data)
        // 2 bytes implicit padding                    //  2
        public uint            Target  { get; init; }   // 4
        // ──────────────────────────────────────────────────────── total: 16 bytes

        /// <summary>Mnemonic as a computed string — zero heap allocation.</summary>
        public string Mnemonic => DataSub switch
        {
            DataKind.Word  => ".word",
            DataKind.Half  => ".half",
            DataKind.Byte  => ".byte",
            DataKind.Float => ".float",
            _              => string.Empty,
        };

        public string HexWord
        {
            get
            {
                if (DataSub == DataKind.Byte)
                {
                    int    pos  = (int)(Address & 3);
                    int    col  = (3 - pos) * 2;
                    char[] buf  = new char[8]; buf.AsSpan().Fill('-');
                    string hex  = (Word & 0xFFu).ToString("X2");
                    buf[col] = hex[0]; buf[col + 1] = hex[1];
                    return new string(buf);
                }
                if (DataSub == DataKind.Half)
                {
                    string hex = (Word & 0xFFFFu).ToString("X4");
                    return (Address & 2) == 0 ? "----" + hex : hex + "----";
                }
                return Word.ToString("X8");
            }
        }

        public string BytesStr
        {
            get
            {
                byte b0 = (byte)(Word);
                byte b1 = (byte)(Word >> 8);
                byte b2 = (byte)(Word >> 16);
                byte b3 = (byte)(Word >> 24);
                return $"{b0:X2} {b1:X2} {b2:X2} {b3:X2}";
            }
        }

        // ── Factory helpers ─────────────────────────────────────────────

        /// <summary>Creates a Data SlimRow with the given sub-type.</summary>
        public static SlimRow DataRow(uint addr, uint word, DataKind sub, uint target = 0)
            => new() { Address = addr, Word = word, Kind = InstructionType.Data, DataSub = sub, Target = target };

        /// <summary>Maps a ".word"/".half"/".byte"/".float" mnemonic to a DataKind.</summary>
        public static DataKind DataKindFromMnemonic(string? mnemonic) => mnemonic switch
        {
            ".word"  => DataKind.Word,
            ".half"  => DataKind.Half,
            ".byte"  => DataKind.Byte,
            ".float" => DataKind.Float,
            _        => DataKind.None,
        };
    }

    /// <summary>
    /// Full display / decode row returned by <see cref="MipsDisassemblerEx"/> and
    /// <c>ResolveRowForDisplay</c>.  Still carries Mnemonic and Operands strings because
    /// display code needs them, but instances are short-lived and are NOT stored in the
    /// main <c>_rows</c> list (which uses the compact <see cref="SlimRow"/> instead).
    /// </summary>
    public readonly struct DisassemblyRow
    {
        public DisassemblyRow()
        {
            Address  = 0;
            Word     = 0;
            Mnemonic = string.Empty;
            Operands = string.Empty;
            Kind     = InstructionType.Normal;
            Target   = 0;
        }

        public uint   Address   { get; init; }
        public uint   Word      { get; init; }
        public string Mnemonic  { get; init; } = string.Empty;
        public string Operands  { get; init; } = string.Empty;
        public InstructionType Kind { get; init; }
        public uint   Target    { get; init; }

        public string HexWord
        {
            get
            {
                if (Mnemonic == ".byte")
                {
                    int    pos  = (int)(Address & 3);
                    int    col  = (3 - pos) * 2;
                    char[] buf  = new char[8]; buf.AsSpan().Fill('-');
                    string hex  = (Word & 0xFFu).ToString("X2");
                    buf[col] = hex[0]; buf[col + 1] = hex[1];
                    return new string(buf);
                }
                if (Mnemonic == ".half")
                {
                    string hex = (Word & 0xFFFFu).ToString("X4");
                    return (Address & 2) == 0 ? "----" + hex : hex + "----";
                }
                return Word.ToString("X8");
            }
        }

        public string BytesStr
        {
            get
            {
                byte b0 = (byte)(Word);
                byte b1 = (byte)(Word >> 8);
                byte b2 = (byte)(Word >> 16);
                byte b3 = (byte)(Word >> 24);
                return $"{b0:X2} {b1:X2} {b2:X2} {b3:X2}";
            }
        }
    }
}
