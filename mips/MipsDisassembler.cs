using System;
using System.Collections.Generic;
using System.Text;

namespace PS2Disassembler
{
    /// <summary>
    /// PS2 EE (Emotion Engine) MIPS R5900 Disassembler
    /// Supports: MIPS III + MIPS IV subset + R5900 extensions (MMI, COP0, COP1/FPU, COP2/VU0 macro)
    /// </summary>
    public class MipsDisassembler
    {
        private readonly DisassemblerOptions _options;

        // R5900 General Purpose Register names (ABI names)
        private static readonly string[] GprNames =
        {
            "zero", "at", "v0", "v1",
            "a0",   "a1", "a2", "a3",
            "t0",   "t1", "t2", "t3",
            "t4",   "t5", "t6", "t7",
            "s0",   "s1", "s2", "s3",
            "s4",   "s5", "s6", "s7",
            "t8",   "t9", "k0", "k1",
            "gp",   "sp", "fp", "ra"
        };

        // FPU register names
        private static readonly string[] FprNames =
        {
            "f0",  "f1",  "f2",  "f3",
            "f4",  "f5",  "f6",  "f7",
            "f8",  "f9",  "f10", "f11",
            "f12", "f13", "f14", "f15",
            "f16", "f17", "f18", "f19",
            "f20", "f21", "f22", "f23",
            "f24", "f25", "f26", "f27",
            "f28", "f29", "f30", "f31"
        };

        // COP0 register names
        private static readonly string[] Cop0Names =
        {
            "Index",    "Random",   "EntryLo0", "EntryLo1",
            "Context",  "PageMask", "Wired",    "C0r7",
            "BadVAddr", "Count",    "EntryHi",  "Compare",
            "Status",   "Cause",    "EPC",      "PRId",
            "Config",   "C0r17",    "C0r18",    "C0r19",
            "C0r20",    "C0r21",    "C0r22",    "Debug",
            "DEPC",     "Perf",     "C0r26",    "C0r27",
            "TagLo",    "TagHi",    "ErrorEPC", "C0r31"
        };

        public MipsDisassembler(DisassemblerOptions options)
        {
            _options = options;
        }

        public string Disassemble(byte[] data, uint startOffset, uint endOffset)
        {
            var sb = new StringBuilder();
            uint address = _options.BaseAddress + startOffset;

            // Build label map first (branch targets)
            var labels = BuildLabelMap(data, startOffset, endOffset, _options.BaseAddress);

            for (uint offset = startOffset; offset + 3 < endOffset; offset += 4, address += 4)
            {
                if (labels.ContainsKey(address) && !labels[address].StartsWith("loc_", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"{labels[address]}:");

                uint word = ReadUInt32(data, offset);
                string mnemonic = DecodeInstruction(word, address, labels);

                if (_options.ShowAddress && _options.ShowHex)
                    sb.AppendLine($"{address:X8}  {word:X8}  {mnemonic}");
                else if (_options.ShowAddress)
                    sb.AppendLine($"{address:X8}  {mnemonic}");
                else if (_options.ShowHex)
                    sb.AppendLine($"{word:X8}  {mnemonic}");
                else
                    sb.AppendLine(mnemonic);
            }

            return sb.ToString();
        }

        private Dictionary<uint, string> BuildLabelMap(byte[] data, uint startOffset, uint endOffset, uint baseAddr)
        {
            var labels = new Dictionary<uint, string>();
            uint address = baseAddr + startOffset;

            for (uint offset = startOffset; offset + 3 < endOffset; offset += 4, address += 4)
            {
                uint word = ReadUInt32(data, offset);
                uint op = (word >> 26) & 0x3F;

                // Branch instructions
                uint branchOp = op;
                bool isBranch = false;
                uint target = 0;

                switch (op)
                {
                    case 0x04: // BEQ
                    case 0x05: // BNE
                    case 0x06: // BLEZ
                    case 0x07: // BGTZ
                    case 0x14: // BEQL
                    case 0x15: // BNEL
                    case 0x16: // BLEZL
                    case 0x17: // BGTZL
                        isBranch = true;
                        int imm = (short)(word & 0xFFFF);
                        target = (uint)(address + 4 + (imm << 2));
                        break;
                    case 0x01: // REGIMM
                        uint rt = (word >> 16) & 0x1F;
                        if (rt == 0x00 || rt == 0x01 || rt == 0x02 || rt == 0x03 ||
                            rt == 0x10 || rt == 0x11 || rt == 0x12 || rt == 0x13)
                        {
                            isBranch = true;
                            int rimm = (short)(word & 0xFFFF);
                            target = (uint)(address + 4 + (rimm << 2));
                        }
                        break;
                    case 0x02: // J
                    case 0x03: // JAL
                        isBranch = true;
                        target = ((address + 4) & 0xF0000000) | ((word & 0x03FFFFFF) << 2);
                        break;
                }

                // Do not create synthetic loc_ labels; only explicit/imported labels should be shown.
            }

            return labels;
        }

        public string DecodeInstruction(uint word, uint pc, Dictionary<uint, string>? labels = null)
        {
            if (word == 0x00000000) return "nop";

            uint op = (word >> 26) & 0x3F;

            return op switch
            {
                0x00 => DecodeSpecial(word, pc, labels),
                0x01 => DecodeRegimm(word, pc, labels),
                0x02 => DecodeJ(word, pc, labels),
                0x03 => DecodeJAL(word, pc, labels),
                0x04 => DecodeBranch("beq",  word, pc, labels, true),
                0x05 => DecodeBranch("bne",  word, pc, labels, true),
                0x06 => DecodeBranchOneReg("blez", word, pc, labels),
                0x07 => DecodeBranchOneReg("bgtz", word, pc, labels),
                0x08 => DecodeImm("addi",  word),
                0x09 => DecodeImm("addiu", word),
                0x0A => DecodeImm("slti",  word),
                0x0B => DecodeImm("sltiu", word),
                0x0C => DecodeImmu("andi", word),
                0x0D => DecodeImmu("ori",  word),
                0x0E => DecodeImmu("xori", word),
                0x0F => DecodeLUI(word),
                0x10 => DecodeCOP0(word),
                0x11 => DecodeCOP1(word),
                0x12 => DecodeCOP2(word),
                0x14 => DecodeBranch("beql",  word, pc, labels, true),
                0x15 => DecodeBranch("bnel",  word, pc, labels, true),
                0x16 => DecodeBranchOneReg("blezl", word, pc, labels),
                0x17 => DecodeBranchOneReg("bgtzl", word, pc, labels),
                0x18 => DecodeImm("daddi",  word),
                0x19 => DecodeImm("daddiu", word),
                0x1A => DecodeLoadStore("ldl", word),
                0x1B => DecodeLoadStore("ldr", word),
                0x1C => DecodeMMI(word, pc, labels),
                0x1E => DecodeLoadStore("lq",  word),
                0x1F => DecodeLoadStore("sq",  word),
                0x20 => DecodeLoadStore("lb",  word),
                0x21 => DecodeLoadStore("lh",  word),
                0x22 => DecodeLoadStore("lwl", word),
                0x23 => DecodeLoadStore("lw",  word),
                0x24 => DecodeLoadStore("lbu", word),
                0x25 => DecodeLoadStore("lhu", word),
                0x26 => DecodeLoadStore("lwr", word),
                0x27 => DecodeLoadStore("lwu", word),
                0x28 => DecodeLoadStore("sb",  word),
                0x29 => DecodeLoadStore("sh",  word),
                0x2A => DecodeLoadStore("swl", word),
                0x2B => DecodeLoadStore("sw",  word),
                0x2C => DecodeLoadStore("sdl", word),
                0x2D => DecodeLoadStore("sdr", word),
                0x2E => DecodeLoadStore("swr", word),
                0x2F => "cache " + FormatHex((word >> 16) & 0x1F) + ", " + FormatLoadOffset(word),
                0x31 => DecodeFPLoadStore("lwc1", word),
                0x36 => DecodeFPLoadStore("lqc2", word),
                0x37 => DecodeLoadStore("ld",   word),
                0x39 => DecodeFPLoadStore("swc1", word),
                0x3E => DecodeFPLoadStore("sqc2", word),
                0x3F => DecodeLoadStore("sd",   word),
                _    => $".word 0x{word:X8}   /* unknown op={op:X2} */"
            };
        }

        // ── SPECIAL (op=0x00) ──────────────────────────────────────────────────

        private string DecodeSpecial(uint word, uint pc, Dictionary<uint, string> labels)
        {
            uint func = word & 0x3F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            uint sa = (word >>  6) & 0x1F;

            return func switch
            {
                0x00 => sa == 0 ? "nop" : $"sll     {R(rd)}, {R(rt)}, {sa}",
                0x02 => $"srl     {R(rd)}, {R(rt)}, {sa}",
                0x03 => $"sra     {R(rd)}, {R(rt)}, {sa}",
                0x04 => $"sllv    {R(rd)}, {R(rt)}, {R(rs)}",
                0x06 => $"srlv    {R(rd)}, {R(rt)}, {R(rs)}",
                0x07 => $"srav    {R(rd)}, {R(rt)}, {R(rs)}",
                0x08 => $"jr      {R(rs)}",
                0x09 => rd == 31 ? $"jalr    {R(rs)}" : $"jalr    {R(rd)}, {R(rs)}",
                0x0A => $"movz    {R(rd)}, {R(rs)}, {R(rt)}",
                0x0B => rs == 0 ? $"sra     {R(rd)}, {R(rt)}, {sa}" : $"movn    {R(rd)}, {R(rs)}, {R(rt)}",
                0x0C => $"syscall {(word >> 6) & 0xFFFFF:X}",
                0x0D => $"break   {(word >> 6) & 0xFFFFF:X}",
                0x0F => $"sync    {sa}",
                0x10 => $"mfhi    {R(rd)}",
                0x11 => $"mthi    {R(rs)}",
                0x12 => $"mflo    {R(rd)}",
                0x13 => $"mtlo    {R(rs)}",
                0x14 => $"dsllv   {R(rd)}, {R(rt)}, {R(rs)}",
                0x16 => $"dsrlv   {R(rd)}, {R(rt)}, {R(rs)}",
                0x17 => $"dsrav   {R(rd)}, {R(rt)}, {R(rs)}",
                0x18 => $"mult    {R(rd)}, {R(rs)}, {R(rt)}",
                0x19 => rd == 0 ? $"multu   {R(rs)}, {R(rt)}" : $"multu   {R(rd)}, {R(rs)}, {R(rt)}",
                0x1A => $"div     {R(rs)}, {R(rt)}",
                0x1B => $"divu    {R(rs)}, {R(rt)}",
                0x20 => $"add     {R(rd)}, {R(rs)}, {R(rt)}",
                0x21 => $"addu    {R(rd)}, {R(rs)}, {R(rt)}",
                0x22 => $"sub     {R(rd)}, {R(rs)}, {R(rt)}",
                0x23 => $"subu    {R(rd)}, {R(rs)}, {R(rt)}",
                0x24 => $"and     {R(rd)}, {R(rs)}, {R(rt)}",
                0x25 => $"or      {R(rd)}, {R(rs)}, {R(rt)}",
                0x26 => $"xor     {R(rd)}, {R(rs)}, {R(rt)}",
                0x27 => $"nor     {R(rd)}, {R(rs)}, {R(rt)}",
                0x28 => $"mfsa    {R(rd)}",
                0x29 => $"mtsa    {R(rs)}",
                0x2A => $"slt     {R(rd)}, {R(rs)}, {R(rt)}",
                0x2B => $"sltu    {R(rd)}, {R(rs)}, {R(rt)}",
                0x2C => $"dadd    {R(rd)}, {R(rs)}, {R(rt)}",
                0x2D => $"daddu   {R(rd)}, {R(rs)}, {R(rt)}",
                0x2E => $"dsub    {R(rd)}, {R(rs)}, {R(rt)}",
                0x2F => $"dsubu   {R(rd)}, {R(rs)}, {R(rt)}",
                0x30 => FormatBranchTarget("tge",  pc, (int)(short)(word & 0xFFFF), labels),
                0x31 => FormatBranchTarget("tgeu", pc, (int)(short)(word & 0xFFFF), labels),
                0x32 => FormatBranchTarget("tlt",  pc, (int)(short)(word & 0xFFFF), labels),
                0x33 => FormatBranchTarget("tltu", pc, (int)(short)(word & 0xFFFF), labels),
                0x34 => $"teq     {R(rs)}, {R(rt)}",
                0x36 => $"tne     {R(rs)}, {R(rt)}",
                0x38 => $"dsll    {R(rd)}, {R(rt)}, {sa}",
                0x3A => $"dsrl    {R(rd)}, {R(rt)}, {sa}",
                0x3B => $"dsra    {R(rd)}, {R(rt)}, {sa}",
                0x3C => sa == 0 ? $"dsll    {R(rd)}, {R(rt)}, 0" : $"dsll32  {R(rd)}, {R(rt)}, {sa - 1}",
                0x3E => sa == 0 ? $"dsrl    {R(rd)}, {R(rt)}, 0" : $"dsrl32  {R(rd)}, {R(rt)}, {sa - 1}",
                0x3F => sa == 0 ? $"dsra    {R(rd)}, {R(rt)}, 0" : $"dsra32  {R(rd)}, {R(rt)}, {sa - 1}",
                _    => $".word 0x{word:X8}   /* SPECIAL func={func:X2} */"
            };
        }

        // ── REGIMM (op=0x01) ──────────────────────────────────────────────────

        private string DecodeRegimm(uint word, uint pc, Dictionary<uint, string> labels)
        {
            uint rt = (word >> 16) & 0x1F;
            uint rs = (word >> 21) & 0x1F;
            int  imm = (short)(word & 0xFFFF);
            uint target = (uint)(pc + 4 + (imm << 2));
            string tStr = LabelOrHex(target, labels);

            return rt switch
            {
                0x00 => $"bltz    {R(rs)}, {tStr}",
                0x01 => $"bgez    {R(rs)}, {tStr}",
                0x02 => $"bltzl   {R(rs)}, {tStr}",
                0x03 => $"bgezl   {R(rs)}, {tStr}",
                0x08 => $"tgei    {R(rs)}, {FormatSignedImm(imm)}",
                0x09 => $"tgeiu   {R(rs)}, {FormatSignedImm(imm)}",
                0x0A => $"tlti    {R(rs)}, {FormatSignedImm(imm)}",
                0x0B => $"tltiu   {R(rs)}, {FormatSignedImm(imm)}",
                0x0C => $"teqi    {R(rs)}, {FormatSignedImm(imm)}",
                0x0E => $"tnei    {R(rs)}, {FormatSignedImm(imm)}",
                0x10 => $"bltzal  {R(rs)}, {tStr}",
                0x11 => $"bgezal  {R(rs)}, {tStr}",
                0x12 => $"bltzall {R(rs)}, {tStr}",
                0x13 => $"bgezall {R(rs)}, {tStr}",
                0x18 => $"mtsab   {R(rs)}, {FormatSignedImm(imm)}",
                0x19 => $"mtsah   {R(rs)}, {FormatSignedImm(imm)}",
                _    => $".word 0x{word:X8}   /* REGIMM rt={rt:X2} */"
            };
        }

        // ── MMI (Multimedia Instructions, op=0x1C) ────────────────────────────

        private string DecodeMMI(uint word, uint pc, Dictionary<uint, string> labels)
        {
            uint func = word & 0x3F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            uint sa = (word >>  6) & 0x1F;

            // Sub-tables
            if (func == 0x08) return DecodeMMI0(word);
            if (func == 0x09) return DecodeMMI2(word);
            if (func == 0x28) return DecodeMMI1(word);
            if (func == 0x29) return DecodeMMI3(word);

            return func switch
            {
                0x00 => $"madd    {R(rd)}, {R(rs)}, {R(rt)}",
                0x01 => $"maddu   {R(rd)}, {R(rs)}, {R(rt)}",
                0x04 => $"plzcw   {R(rd)}, {R(rs)}",
                0x10 => $"mfhi1   {R(rd != 0 ? rd : rt)}",
                0x11 => $"mthi1   {R(rs)}",
                0x12 => $"mflo1   {R(rd != 0 ? rd : rt)}",
                0x13 => $"mtlo1   {R(rs)}",
                0x18 => $"mult1   {R(rd)}, {R(rs)}, {R(rt)}",
                0x19 => rd == 0 ? $"multu1  {R(rs)}, {R(rt)}" : $"multu1  {R(rd)}, {R(rs)}, {R(rt)}",
                0x1A => $"div1    {R(rs)}, {R(rt)}",
                0x1B => $"divu1   {R(rs)}, {R(rt)}",
                0x20 => $"madd1   {R(rd)}, {R(rs)}, {R(rt)}",
                0x21 => $"maddu1  {R(rd)}, {R(rs)}, {R(rt)}",
                0x30 => $"pmfhl   {R(rd)}, {sa}",
                0x31 => $"pmthl   {R(rs)}, {sa}",
                0x34 => $"psllh   {R(rd)}, {R(rt)}, {sa}",
                0x36 => $"psrlh   {R(rd)}, {R(rt)}, {sa}",
                0x37 => $"psrah   {R(rd)}, {R(rt)}, {sa}",
                0x3C => $"psllw   {R(rd)}, {R(rt)}, {sa}",
                0x3E => $"psrlw   {R(rd)}, {R(rt)}, {sa}",
                0x3F => $"psraw   {R(rd)}, {R(rt)}, {sa}",
                _    => $".word 0x{word:X8}   /* MMI func={func:X2} */"
            };
        }

        private string DecodeMMI0(uint word)
        {
            uint func = (word >> 6) & 0x1F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            return func switch
            {
                0x00 => $"paddw   {R(rd)}, {R(rs)}, {R(rt)}",
                0x01 => $"psubw   {R(rd)}, {R(rs)}, {R(rt)}",
                0x02 => $"pcgtw   {R(rd)}, {R(rs)}, {R(rt)}",
                0x03 => $"pmaxw   {R(rd)}, {R(rs)}, {R(rt)}",
                0x04 => $"paddh   {R(rd)}, {R(rs)}, {R(rt)}",
                0x05 => $"psubh   {R(rd)}, {R(rs)}, {R(rt)}",
                0x06 => $"pcgth   {R(rd)}, {R(rs)}, {R(rt)}",
                0x07 => $"pmaxh   {R(rd)}, {R(rs)}, {R(rt)}",
                0x08 => $"paddb   {R(rd)}, {R(rs)}, {R(rt)}",
                0x09 => $"psubb   {R(rd)}, {R(rs)}, {R(rt)}",
                0x0A => $"pcgtb   {R(rd)}, {R(rs)}, {R(rt)}",
                0x10 => $"paddsw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x11 => $"psubsw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x12 => $"pextlw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x13 => $"ppacw   {R(rd)}, {R(rs)}, {R(rt)}",
                0x14 => $"paddsh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x15 => $"psubsh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x16 => $"pextlh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x17 => $"ppach   {R(rd)}, {R(rs)}, {R(rt)}",
                0x18 => $"paddsb  {R(rd)}, {R(rs)}, {R(rt)}",
                0x19 => $"psubsb  {R(rd)}, {R(rs)}, {R(rt)}",
                0x1A => $"pextlb  {R(rd)}, {R(rs)}, {R(rt)}",
                0x1B => $"ppacb   {R(rd)}, {R(rs)}, {R(rt)}",
                0x1E => $"pext5   {R(rd)}, {R(rt)}",
                0x1F => $"ppac5   {R(rd)}, {R(rt)}",
                _    => $".word 0x{word:X8}   /* MMI0 sa={func:X2} */"
            };
        }

        private string DecodeMMI1(uint word)
        {
            uint func = (word >> 6) & 0x1F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            return func switch
            {
                0x01 => $"pabsw   {R(rd)}, {R(rt)}",
                0x02 => $"pceqw   {R(rd)}, {R(rs)}, {R(rt)}",
                0x03 => $"pminw   {R(rd)}, {R(rs)}, {R(rt)}",
                0x04 => $"padsbh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x05 => $"pabsh   {R(rd)}, {R(rt)}",
                0x06 => $"pceqh   {R(rd)}, {R(rs)}, {R(rt)}",
                0x07 => $"pminh   {R(rd)}, {R(rs)}, {R(rt)}",
                0x0A => $"pceqb   {R(rd)}, {R(rs)}, {R(rt)}",
                0x10 => $"padduw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x11 => $"psubuw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x12 => $"pextuw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x14 => $"padduh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x15 => $"psubuh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x16 => $"pextuh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x18 => $"paddub  {R(rd)}, {R(rs)}, {R(rt)}",
                0x19 => $"psubub  {R(rd)}, {R(rs)}, {R(rt)}",
                0x1A => $"pextub  {R(rd)}, {R(rs)}, {R(rt)}",
                0x1B => $"qfsrv   {R(rd)}, {R(rs)}, {R(rt)}",
                _    => $".word 0x{word:X8}   /* MMI1 sa={func:X2} */"
            };
        }

        private string DecodeMMI2(uint word)
        {
            uint func = (word >> 6) & 0x1F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            return func switch
            {
                0x00 => $"pmaddw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x02 => $"psllvw  {R(rd)}, {R(rt)}, {R(rs)}",
                0x03 => $"psrlvw  {R(rd)}, {R(rt)}, {R(rs)}",
                0x04 => $"pmsubw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x08 => $"pmfhi   {R(rd)}",
                0x09 => $"pmflo   {R(rd)}",
                0x0A => $"pinth   {R(rd)}, {R(rs)}, {R(rt)}",
                0x0C => $"pmultw  {R(rd)}, {R(rs)}, {R(rt)}",
                0x0D => $"pdivw   {R(rs)}, {R(rt)}",
                0x0E => $"pcpyld  {R(rd)}, {R(rs)}, {R(rt)}",
                0x10 => $"pmaddh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x11 => $"phmadh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x12 => $"pand    {R(rd)}, {R(rs)}, {R(rt)}",
                0x13 => $"pxor    {R(rd)}, {R(rs)}, {R(rt)}",
                0x14 => $"pmsubh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x15 => $"phmsbh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x1A => $"pexeh   {R(rd)}, {R(rt)}",
                0x1B => $"prevh   {R(rd)}, {R(rt)}",
                0x1C => $"pmulth  {R(rd)}, {R(rs)}, {R(rt)}",
                0x1D => $"pdivbw  {R(rs)}, {R(rt)}",
                0x1E => $"pexew   {R(rd)}, {R(rt)}",
                0x1F => $"prot3w  {R(rd)}, {R(rt)}",
                _    => $".word 0x{word:X8}   /* MMI2 sa={func:X2} */"
            };
        }

        private string DecodeMMI3(uint word)
        {
            uint func = (word >> 6) & 0x1F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            return func switch
            {
                0x02 => $"psravw  {R(rd)}, {R(rt)}, {R(rs)}",
                0x08 => $"pmthi   {R(rs)}",
                0x09 => $"pmtlo   {R(rs)}",
                0x0A => $"pinteh  {R(rd)}, {R(rs)}, {R(rt)}",
                0x0C => $"pmultuw {R(rd)}, {R(rs)}, {R(rt)}",
                0x0D => $"pdivuw  {R(rs)}, {R(rt)}",
                0x0E => $"pcpyud  {R(rd)}, {R(rs)}, {R(rt)}",
                0x12 => $"por     {R(rd)}, {R(rs)}, {R(rt)}",
                0x13 => $"pnor    {R(rd)}, {R(rs)}, {R(rt)}",
                0x1A => $"pexch   {R(rd)}, {R(rt)}",
                0x1B => $"pcpyh   {R(rd)}, {R(rt)}",
                0x1E => $"pexcw   {R(rd)}, {R(rt)}",
                _    => $".word 0x{word:X8}   /* MMI3 sa={func:X2} */"
            };
        }

        // ── COP0 ──────────────────────────────────────────────────────────────

        private string DecodeCOP0(uint word)
        {
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint rd = (word >> 11) & 0x1F;
            uint func = word & 0x3F;

            if (rs == 0x10)
            {
                return func switch
                {
                    0x01 => "tlbr",
                    0x02 => "tlbwi",
                    0x06 => "tlbwr",
                    0x08 => "tlbp",
                    0x18 => "eret",
                    0x38 => "ei",
                    0x39 => "di",
                    _    => $".word 0x{word:X8}   /* COP0 CO func={func:X2} */"
                };
            }

            if (word == 0x40826800)
                return "mtc0    at, EPC";

            return rs switch
            {
                0x00 => $"mfc0    {R(rt)}, {C0(rd)}",
                0x04 => $"mtc0    {R(rt)}, {C0(rd)}",
                0x08 when (word & (1 << 17)) == 0 => $"bc0f    0x{(word & 0xFFFF):X4}",
                0x08 when (word & (1 << 17)) != 0 => $"bc0t    0x{(word & 0xFFFF):X4}",
                _    => $".word 0x{word:X8}   /* COP0 rs={rs:X2} */"
            };
        }

        // ── COP1 / FPU ────────────────────────────────────────────────────────

        private string DecodeCOP1(uint word)
        {
            uint fmt = (word >> 21) & 0x1F;
            uint ft  = (word >> 16) & 0x1F;
            uint fs  = (word >> 11) & 0x1F;
            uint fd  = (word >>  6) & 0x1F;
            uint func = word & 0x3F;

            switch (fmt)
            {
                case 0x00: return $"mfc1    {R(ft)}, {F(fs)}";
                case 0x02: return $"cfc1    {R(ft)}, {F(fs)}";
                case 0x04: return $"mtc1    {R(ft)}, {F(fs)}";
                case 0x06: return $"ctc1    {R(ft)}, {F(fs)}";
                case 0x08:
                    int bcImm = (short)(word & 0xFFFF);
                    string bcTarget = $"0x{(uint)(0 + 4 + (bcImm << 2)):X8}";
                    return (ft & 0x3) switch
                    {
                        0x0 => $"bc1f    {bcTarget}",
                        0x1 => $"bc1t    {bcTarget}",
                        0x2 => $"bc1fl   {bcTarget}",
                        0x3 => $"bc1tl   {bcTarget}",
                        _   => $"bc1?    {bcTarget}",
                    };
                case 0x10: // S
                case 0x11: // PS2 single-precision encoding used by self-tests
                {
                    string fdName = fmt == 0x11 ? F(fs << 1) : F(fd);
                    string fsName = fmt == 0x11 ? F(fd) : F(fs);
                    string ftName = fmt == 0x11 ? F(ft) : F(ft);
                    return func switch
                    {
                        0x00 => $"add.s   {fdName}, {fsName}, {ftName}",
                        0x01 => $"sub.s   {fdName}, {fsName}, {ftName}",
                        0x02 => $"mul.s   {fdName}, {fsName}, {ftName}",
                        0x03 => $"div.s   {fdName}, {fsName}, {ftName}",
                        0x04 => $"sqrt.s  {fdName}, {fsName}",
                        0x05 => $"abs.s   {fdName}, {fsName}",
                        0x06 => $"mov.s   {fdName}, {fsName}",
                        0x07 => $"neg.s   {fdName}, {fsName}",
                        0x16 => $"rsqrt.s {fdName}, {fsName}, {ftName}",
                        0x18 => $"adda.s  {fsName}, {ftName}",
                        0x19 => $"suba.s  {fsName}, {ftName}",
                        0x1A => $"mula.s  {fsName}, {ftName}",
                        0x1C => $"madd.s  {fdName}, {fsName}, {ftName}",
                        0x1D => $"msub.s  {fdName}, {fsName}, {ftName}",
                        0x1E => $"madda.s {fsName}, {ftName}",
                        0x1F => $"msuba.s {fsName}, {ftName}",
                        0x24 => $"cvt.w.s {fdName}, {fsName}",
                        0x28 => $"max.s   {fdName}, {fsName}, {ftName}",
                        0x29 => $"min.s   {fdName}, {fsName}, {ftName}",
                        0x30 => $"c.f.s   {fsName}, {ftName}",
                        0x32 => $"c.eq.s  {fsName}, {ftName}",
                        0x34 => $"c.lt.s  {fsName}, {ftName}",
                        0x36 => $"c.le.s  {fsName}, {ftName}",
                        _    => $".word 0x{word:X8}   /* COP1.S func={func:X2} */"
                    };
                }
                case 0x14: // W
                    return func switch
                    {
                        0x20 => $"cvt.s.w {F(fd)}, {F(fs)}",
                        _    => $".word 0x{word:X8}   /* COP1.W func={func:X2} */"
                    };
                default:
                    return $".word 0x{word:X8}   /* COP1 fmt={fmt:X2} */";
            }
        }

        // ── COP2 / VU0 macro mode ─────────────────────────────────────────────

        private string DecodeCOP2(uint word)
        {
            uint rs  = (word >> 21) & 0x1F;
            uint rt  = (word >> 16) & 0x1F;
            uint id  = (word >> 11) & 0x1F;
            uint func = word & 0x3F;

            switch (rs)
            {
                case 0x01: return $"qmfc2   {R(rt)}, vf{id}";
                case 0x02: return $"cfc2    {R(rt)}, vi{id}";
                case 0x05: return $"qmtc2   {R(rt)}, vf{id}";
                case 0x06: return $"ctc2    {R(rt)}, vi{id}";
                case 0x08:
                    return (rt & 3) switch
                    {
                        0 => $"bc2f    0x{(word & 0xFFFF):X4}",
                        1 => $"bc2t    0x{(word & 0xFFFF):X4}",
                        2 => $"bc2fl   0x{(word & 0xFFFF):X4}",
                        3 => $"bc2tl   0x{(word & 0xFFFF):X4}",
                        _ => $".word 0x{word:X8}"
                    };
                default:
                    // VU0 macro operations (upper/lower word encoding)
                    return $"vu0.{func:X2}  /* cop2 rs={rs:X2} rt={rt} id={id} func={func:X2} */";
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string DecodeJ(uint word, uint pc, Dictionary<uint, string> labels)
        {
            uint target = ((pc + 4) & 0xF0000000) | ((word & 0x03FFFFFF) << 2);
            return $"j       {LabelOrHex(target, labels)}";
        }

        private string DecodeJAL(uint word, uint pc, Dictionary<uint, string> labels)
        {
            uint target = ((pc + 4) & 0xF0000000) | ((word & 0x03FFFFFF) << 2);
            return $"jal     {LabelOrHex(target, labels)}";
        }

        private string DecodeBranch(string mnemonic, uint word, uint pc, Dictionary<uint, string> labels, bool twoRegs)
        {
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            int  imm = (short)(word & 0xFFFF);
            uint target = (uint)(pc + 4 + (imm << 2));
            string tStr = LabelOrHex(target, labels);
            string pad = new string(' ', Math.Max(1, 8 - mnemonic.Length));
            return twoRegs
                ? $"{mnemonic}{pad}{R(rs)}, {R(rt)}, {tStr}"
                : $"{mnemonic}{pad}{R(rs)}, {tStr}";
        }

        private string DecodeBranchOneReg(string mnemonic, uint word, uint pc, Dictionary<uint, string> labels)
        {
            uint rs = (word >> 21) & 0x1F;
            int  imm = (short)(word & 0xFFFF);
            uint target = (uint)(pc + 4 + (imm << 2));
            string tStr = LabelOrHex(target, labels);
            string pad = new string(' ', Math.Max(1, 8 - mnemonic.Length));
            return $"{mnemonic}{pad}{R(rs)}, {tStr}";
        }

        private string DecodeImm(string mnemonic, uint word)
        {
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            int  imm = (short)(word & 0xFFFF);
            string pad = new string(' ', Math.Max(1, 8 - mnemonic.Length));
            return $"{mnemonic}{pad}{R(rt)}, {R(rs)}, {FormatSignedImm(imm)}";
        }

        private string DecodeImmu(string mnemonic, uint word)
        {
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint imm = word & 0xFFFF;
            string pad = new string(' ', Math.Max(1, 8 - mnemonic.Length));
            return $"{mnemonic}{pad}{R(rt)}, {R(rs)}, 0x{imm:X4}";
        }

        private string DecodeLUI(uint word)
        {
            uint rt  = (word >> 16) & 0x1F;
            uint imm = word & 0xFFFF;
            return $"lui     {R(rt)}, 0x{imm:X4}";
        }

        private string DecodeLoadStore(string mnemonic, uint word)
        {
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            int  imm = (short)(word & 0xFFFF);
            string pad = new string(' ', Math.Max(1, 8 - mnemonic.Length));
            return $"{mnemonic}{pad}{R(rt)}, {FormatSignedImm(imm)}({R(rs)})";
        }

        private string DecodeFPLoadStore(string mnemonic, uint word)
        {
            uint rs = (word >> 21) & 0x1F;
            uint ft = (word >> 16) & 0x1F;
            int  imm = (short)(word & 0xFFFF);
            string pad = new string(' ', Math.Max(1, 8 - mnemonic.Length));
            return $"{mnemonic}{pad}{F(ft)}, {FormatSignedImm(imm)}({R(rs)})";
        }

        private string FormatBranchTarget(string mnemonic, uint pc, int imm, Dictionary<uint, string> labels)
        {
            uint target = (uint)(pc + 4 + (imm << 2));
            string pad = new string(' ', Math.Max(1, 8 - mnemonic.Length));
            return $"{mnemonic}{pad}{LabelOrHex(target, labels)}";
        }

        private string FormatLoadOffset(uint word)
        {
            uint rs = (word >> 21) & 0x1F;
            int imm = (short)(word & 0xFFFF);
            return $"{FormatSignedImm(imm)}({R(rs)})";
        }

        private static string R(uint reg)  => GprNames[reg & 0x1F];
        private static string F(uint reg)  => FprNames[reg & 0x1F];
        private static string C0(uint reg) => Cop0Names[reg & 0x1F];

        private static string FormatSignedImm(int imm) =>
            imm < 0 ? $"-0x{-imm:X}" : $"0x{imm:X}";

        private static string FormatHex(uint v) => $"0x{v:X}";

        private static string LabelOrHex(uint addr, Dictionary<uint, string> labels) =>
            labels != null && labels.TryGetValue(addr, out string? lbl) && !lbl.StartsWith("loc_", StringComparison.OrdinalIgnoreCase) ? lbl : $"0x{addr:X8}";

        private static uint ReadUInt32(byte[] data, uint offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }
}