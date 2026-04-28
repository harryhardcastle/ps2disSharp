using System;
using System.Globalization;

namespace PS2Disassembler
{
    /// <summary>
    /// MIPS R5900 assembler — parses the same operand format the disassembler produces.
    /// Returns null when an instruction cannot be assembled.
    /// </summary>
    internal static class MipsAssembler
    {
        private static readonly string[] GprAbi =
        {
            "zero","at","v0","v1","a0","a1","a2","a3",
            "t0","t1","t2","t3","t4","t5","t6","t7",
            "s0","s1","s2","s3","s4","s5","s6","s7",
            "t8","t9","k0","k1","gp","sp","fp","ra"
        };

        private static readonly string[] Cop0Abi =
        {
            "Index","Random","EntryLo0","EntryLo1","Context","PageMask","Wired","C0r7",
            "BadVAddr","Count","EntryHi","Compare","Status","Cause","EPC","PRId",
            "Config","C0r17","C0r18","C0r19","C0r20","C0r21","C0r22","Debug",
            "DEPC","Perf","C0r26","C0r27","TagLo","TagHi","ErrorEPC","C0r31"
        };

        /// <summary>
        /// Assemble a single "mnemonic operands" line at the given PC.
        /// Returns the 32-bit machine word, or null on failure.
        /// </summary>
        public static uint? Assemble(string text, uint pc)
        {
            text = text.Trim();
            if (text.Length == 0) return null;

            int sep = text.IndexOfAny(new[] { ' ', '\t' });
            string mnem = sep < 0 ? text          : text[..sep];
            string ops  = sep < 0 ? string.Empty  : text[(sep + 1)..].Trim();

            mnem = mnem.ToLowerInvariant();
            string[] p = ops.Length == 0
                ? Array.Empty<string>()
                : ops.Split(',', StringSplitOptions.TrimEntries);

            try
            {
                return mnem switch
                {
                    // ── Special ───────────────────────────────────────────
                    "nop"    => 0u,
                    "syscall"=> CodeFn(p, 0x0C),
                    "break"  => CodeFn(p, 0x0D),
                    "sync"   => CodeFn(p, 0x0F),
                    "eret"   => (0x10u << 26) | (1u << 25) | 0x18u,

                    // ── J-type ────────────────────────────────────────────
                    "j"      => JType(0x02, p, pc),
                    "jal"    => JType(0x03, p, pc),

                    // ── SPECIAL R-type: rd, rs, rt ────────────────────────
                    "add"    => RType(p, 0x20),
                    "addu"   => RType(p, 0x21),
                    "sub"    => RType(p, 0x22),
                    "subu"   => RType(p, 0x23),
                    "and"    => RType(p, 0x24),
                    "or"     => RType(p, 0x25),
                    "xor"    => RType(p, 0x26),
                    "nor"    => RType(p, 0x27),
                    "slt"    => RType(p, 0x2A),
                    "sltu"   => RType(p, 0x2B),
                    "movz"   => RType(p, 0x0A),
                    "movn"   => RType(p, 0x0B),
                    "dadd"   => RType(p, 0x2C),
                    "daddu"  => RType(p, 0x2D),
                    "dsub"   => RType(p, 0x2E),
                    "dsubu"  => RType(p, 0x2F),

                    // ── SPECIAL R-type: rd, rs, rt (mult has rd first on EE) ──
                    "mult"   => MultDiv(p, 0x18),
                    "multu"  => MultDiv(p, 0x19),

                    // ── SPECIAL R-type: rs, rt (div) ─────────────────────
                    "div"    => MultDiv(p, 0x1A),
                    "divu"   => MultDiv(p, 0x1B),
                    "dmult"  => MultDiv(p, 0x1C),
                    "dmultu" => MultDiv(p, 0x1D),
                    "ddiv"   => MultDiv(p, 0x1E),
                    "ddivu"  => MultDiv(p, 0x1F),

                    // ── SPECIAL R-type: rd, rt, sa (immediate shift) ──────
                    "sll"    => ShiftImm(p, 0x00),
                    "srl"    => ShiftImm(p, 0x02),
                    "sra"    => ShiftImm(p, 0x03),
                    "dsll"   => ShiftImm(p, 0x38),
                    "dsrl"   => ShiftImm(p, 0x3A),
                    "dsra"   => ShiftImm(p, 0x3B),
                    "dsll32" => ShiftImm(p, 0x3C),
                    "dsrl32" => ShiftImm(p, 0x3E),
                    "dsra32" => ShiftImm(p, 0x3F),

                    // ── SPECIAL R-type: rd, rt, rs (variable shift) ───────
                    "sllv"   => ShiftVar(p, 0x04),
                    "srlv"   => ShiftVar(p, 0x06),
                    "srav"   => ShiftVar(p, 0x07),
                    "dsllv"  => ShiftVar(p, 0x14),
                    "dsrlv"  => ShiftVar(p, 0x16),
                    "dsrav"  => ShiftVar(p, 0x17),

                    // ── SPECIAL R-type: rd ────────────────────────────────
                    "mfhi"   => Rd(p, 0x10),
                    "mflo"   => Rd(p, 0x12),
                    "mfsa"   => Rd(p, 0x28),

                    // ── SPECIAL R-type: rs ────────────────────────────────
                    "mthi"   => Rs(p, 0x11),
                    "mtlo"   => Rs(p, 0x13),
                    "mtsa"   => Rs(p, 0x29),

                    // ── SPECIAL: jr, jalr ─────────────────────────────────
                    "jr"     => (Reg(p[0]) << 21) | 0x08u,
                    "jalr"   => p.Length >= 2
                                    ? (Reg(p[1]) << 21) | (Reg(p[0]) << 11) | 0x09u
                                    : (Reg(p[0]) << 21) | (31u << 11) | 0x09u,

                    // ── SPECIAL: trap ─────────────────────────────────────
                    "teq"    => (Reg(p[0]) << 21) | (Reg(p[1]) << 16) | 0x34u,
                    "tne"    => (Reg(p[0]) << 21) | (Reg(p[1]) << 16) | 0x36u,

                    // ── REGIMM branches: rs, addr ──────────────────────────
                    "bltz"    => Regimm(0x00, p, pc),
                    "bgez"    => Regimm(0x01, p, pc),
                    "bltzl"   => Regimm(0x02, p, pc),
                    "bgezl"   => Regimm(0x03, p, pc),
                    "bltzal"  => Regimm(0x10, p, pc),
                    "bgezal"  => Regimm(0x11, p, pc),
                    "bltzall" => Regimm(0x12, p, pc),
                    "bgezall" => Regimm(0x13, p, pc),

                    // ── I-type: rt, rs, imm ───────────────────────────────
                    "addi"   => IType(0x08, p),
                    "addiu"  => IType(0x09, p),
                    "slti"   => IType(0x0A, p),
                    "sltiu"  => IType(0x0B, p),
                    "andi"   => IType(0x0C, p),
                    "ori"    => IType(0x0D, p),
                    "xori"   => IType(0x0E, p),
                    "daddi"  => IType(0x18, p),
                    "daddiu" => IType(0x19, p),

                    // ── I-type: rt, imm (lui) ─────────────────────────────
                    "lui"    => (0x0Fu << 26) | (Reg(p[0]) << 16) | (ushort)ParseImm(p[1]),

                    // ── I-type branches: rs, rt, addr ─────────────────────
                    "beq"    => Branch2(0x04, p, pc),
                    "bne"    => Branch2(0x05, p, pc),
                    "beql"   => Branch2(0x14, p, pc),
                    "bnel"   => Branch2(0x15, p, pc),

                    // ── I-type branches: rs, addr ─────────────────────────
                    "blez"   => Branch1(0x06, p, pc),
                    "bgtz"   => Branch1(0x07, p, pc),
                    "blezl"  => Branch1(0x16, p, pc),
                    "bgtzl"  => Branch1(0x17, p, pc),

                    // ── Loads / Stores: rt, offset(base) ──────────────────
                    "lb"     => MemOp(0x20, p),
                    "lh"     => MemOp(0x21, p),
                    "lwl"    => MemOp(0x22, p),
                    "lw"     => MemOp(0x23, p),
                    "lbu"    => MemOp(0x24, p),
                    "lhu"    => MemOp(0x25, p),
                    "lwr"    => MemOp(0x26, p),
                    "lwu"    => MemOp(0x27, p),
                    "sb"     => MemOp(0x28, p),
                    "sh"     => MemOp(0x29, p),
                    "swl"    => MemOp(0x2A, p),
                    "sw"     => MemOp(0x2B, p),
                    "sdl"    => MemOp(0x2C, p),
                    "sdr"    => MemOp(0x2D, p),
                    "swr"    => MemOp(0x2E, p),
                    "ldl"    => MemOp(0x1A, p),
                    "ldr"    => MemOp(0x1B, p),
                    "lq"     => MemOp(0x1E, p),
                    "sq"     => MemOp(0x1F, p),
                    "ld"     => MemOp(0x37, p),
                    "sd"     => MemOp(0x3F, p),

                    // ── COP0 moves: rt, c0reg ─────────────────────────────
                    "mfc0"   => (0x10u << 26) | (0u  << 21) | (Reg(p[0]) << 16) | (C0Reg(p[1]) << 11),
                    "dmfc0"  => (0x10u << 26) | (1u  << 21) | (Reg(p[0]) << 16) | (C0Reg(p[1]) << 11),
                    "mtc0"   => (0x10u << 26) | (4u  << 21) | (Reg(p[0]) << 16) | (C0Reg(p[1]) << 11),
                    "dmtc0"  => (0x10u << 26) | (5u  << 21) | (Reg(p[0]) << 16) | (C0Reg(p[1]) << 11),

                    // ── COP1 moves: rt, fs ────────────────────────────────
                    "mfc1"   => (0x11u << 26) | (0u  << 21) | (Reg(p[0]) << 16) | (FReg(p[1]) << 11),
                    "mtc1"   => (0x11u << 26) | (4u  << 21) | (Reg(p[0]) << 16) | (FReg(p[1]) << 11),
                    "cfc1"   => (0x11u << 26) | (2u  << 21) | (Reg(p[0]) << 16) | (FReg(p[1]) << 11),
                    "ctc1"   => (0x11u << 26) | (6u  << 21) | (Reg(p[0]) << 16) | (FReg(p[1]) << 11),

                    // ── COP1.S 3-operand: fd, fs, ft ──────────────────────
                    "add.s"   => Cop1S3(0x00, p),
                    "sub.s"   => Cop1S3(0x01, p),
                    "mul.s"   => Cop1S3(0x02, p),
                    "div.s"   => Cop1S3(0x03, p),
                    "rsqrt.s" => Cop1S3(0x16, p),
                    "madd.s"  => Cop1S3(0x1C, p),
                    "msub.s"  => Cop1S3(0x1D, p),
                    "max.s"   => Cop1S3(0x28, p),
                    "min.s"   => Cop1S3(0x29, p),

                    // ── COP1.S 2-operand dest+src: fd, fs ─────────────────
                    "sqrt.s"  => Cop1SqrtS(p),
                    "abs.s"   => Cop1S2d(0x05, p),
                    "mov.s"   => Cop1S2d(0x06, p),
                    "neg.s"   => Cop1S2d(0x07, p),
                    "cvt.w.s" => Cop1S2d(0x24, p),

                    // ── COP1.S 2-operand source: fs, ft (accumulator) ─────
                    "adda.s"  => Cop1S2s(0x18, p),
                    "suba.s"  => Cop1S2s(0x19, p),
                    "mula.s"  => Cop1S2s(0x1A, p),
                    "madda.s" => Cop1S2s(0x1E, p),
                    "msuba.s" => Cop1S2s(0x1F, p),

                    // ── COP1.S comparisons: fs, ft ────────────────────────
                    "c.f.s"   => Cop1S2s(0x30, p),
                    "c.eq.s"  => Cop1S2s(0x32, p),
                    "c.lt.s"  => Cop1S2s(0x34, p),
                    "c.le.s"  => Cop1S2s(0x36, p),

                    // ── COP1.W: fd, fs ────────────────────────────────────
                    "cvt.s.w" => Cop1W2d(0x20, p),

                    // ── COP1.D / conversions ──────────────────────────────
                    "add.d"   => Cop1D3(0x00, p),
                    "sub.d"   => Cop1D3(0x01, p),
                    "mul.d"   => Cop1D3(0x02, p),
                    "div.d"   => Cop1D3(0x03, p),
                    "abs.d"   => Cop1D2d(0x05, p),
                    "neg.d"   => Cop1D2d(0x07, p),
                    "cvt.s.d" => Cop1D2d(0x20, p),
                    "cvt.w.d" => Cop1D2d(0x24, p),
                    "cvt.d.s" => Cop1S2dWithFmt(0x11, 0x21, p),
                    "cvt.d.w" => Cop1S2dWithFmt(0x14, 0x21, p),
                    "cvt.l.s" => Cop1S2dWithFmt(0x10, 0x25, p),
                    "cvt.s.l" => Cop1S2dWithFmt(0x15, 0x20, p),
                    "cvt.l.d" => Cop1S2dWithFmt(0x11, 0x25, p),
                    "cvt.d.l" => Cop1S2dWithFmt(0x15, 0x21, p),

                    // ── COP1 branches ─────────────────────────────────────
                    "bc1f"    => Cop1Branch(false, p, pc),
                    "bc1t"    => Cop1Branch(true, p, pc),

                    // ── COP1 loads/stores: ft, offset(base) ───────────────
                    "lwc1"    => MemOpFpu(0x31, p),
                    "swc1"    => MemOpFpu(0x39, p),
                    "ldc1"    => MemOpFpu(0x35, p),
                    "sdc1"    => MemOpFpu(0x3D, p),

                    // ── MMI main: rd, rs, rt ──────────────────────────────
                    "madd"    => MmiOps3(0x00, 0x00, p),
                    "maddu"   => MmiOps3(0x01, 0x00, p),
                    "madd1"   => MmiOps3(0x20, 0x00, p),
                    "maddu1"  => MmiOps3(0x21, 0x00, p),
                    "mult1"   => MmiOps3(0x18, 0x00, p),
                    "multu1"  => MmiOps3(0x19, 0x00, p),

                    // ── MMI main: rd only ─────────────────────────────────
                    "plzcw"   => MmiRd(0x04, 0x00, p),
                    "mfhi1"   => MmiRd(0x10, 0x00, p),
                    "mflo1"   => MmiRd(0x12, 0x00, p),

                    // ── MMI main: rs only ─────────────────────────────────
                    "mthi1"   => MmiRs(0x11, 0x00, p),
                    "mtlo1"   => MmiRs(0x13, 0x00, p),

                    // ── MMI main: rs, rt ──────────────────────────────────
                    "div1"    => (0x1Cu << 26) | (Reg(p[0]) << 21) | (Reg(p[1]) << 16) | 0x1Au,
                    "divu1"   => (0x1Cu << 26) | (Reg(p[0]) << 21) | (Reg(p[1]) << 16) | 0x1Bu,

                    // ── MMI main: rd, sa (pmfhl/pmthl) ───────────────────
                    "pmfhl"   => (0x1Cu << 26) | (Reg(p[0]) << 11) | ((uint)ParseImm(p[1]) << 6) | 0x30u,
                    "pmthl"   => (0x1Cu << 26) | (Reg(p[0]) << 21) | ((uint)ParseImm(p[1]) << 6) | 0x31u,

                    // ── MMI main: rd, rt, sa (shifts) ────────────────────
                    "psllh"   => MmiShift(0x34, p),
                    "psrlh"   => MmiShift(0x36, p),
                    "psrah"   => MmiShift(0x37, p),
                    "psllw"   => MmiShift(0x3C, p),
                    "psrlw"   => MmiShift(0x3E, p),
                    "psraw"   => MmiShift(0x3F, p),

                    // ── MMI0 (fn=0x08): rd, rs, rt ───────────────────────
                    "paddw"   => MmiOps3(0x08, 0x00, p),
                    "psubw"   => MmiOps3(0x08, 0x01, p),
                    "pcgtw"   => MmiOps3(0x08, 0x02, p),
                    "pmaxw"   => MmiOps3(0x08, 0x03, p),
                    "paddh"   => MmiOps3(0x08, 0x04, p),
                    "psubh"   => MmiOps3(0x08, 0x05, p),
                    "pcgth"   => MmiOps3(0x08, 0x06, p),
                    "pmaxh"   => MmiOps3(0x08, 0x07, p),
                    "paddb"   => MmiOps3(0x08, 0x08, p),
                    "psubb"   => MmiOps3(0x08, 0x09, p),
                    "pcgtb"   => MmiOps3(0x08, 0x0A, p),
                    "paddsw"  => MmiOps3(0x08, 0x10, p),
                    "psubsw"  => MmiOps3(0x08, 0x11, p),
                    "pextlw"  => MmiOps3(0x08, 0x12, p),
                    "ppacw"   => MmiOps3(0x08, 0x13, p),
                    "paddsh"  => MmiOps3(0x08, 0x14, p),
                    "psubsh"  => MmiOps3(0x08, 0x15, p),
                    "pextlh"  => MmiOps3(0x08, 0x16, p),
                    "ppach"   => MmiOps3(0x08, 0x17, p),
                    "paddsb"  => MmiOps3(0x08, 0x18, p),
                    "psubsb"  => MmiOps3(0x08, 0x19, p),
                    "pextlb"  => MmiOps3(0x08, 0x1A, p),
                    "ppacb"   => MmiOps3(0x08, 0x1B, p),
                    "pext5"   => MmiOps2(0x08, 0x1E, p),
                    "ppac5"   => MmiOps2(0x08, 0x1F, p),

                    // ── MMI1 (fn=0x28): mostly rd, rs, rt ────────────────
                    "pabsw"   => MmiOps2(0x28, 0x01, p),
                    "pceqw"   => MmiOps3(0x28, 0x02, p),
                    "pminw"   => MmiOps3(0x28, 0x03, p),
                    "padsbh"  => MmiOps3(0x28, 0x04, p),
                    "pabsh"   => MmiOps2(0x28, 0x05, p),
                    "pceqh"   => MmiOps3(0x28, 0x06, p),
                    "pminh"   => MmiOps3(0x28, 0x07, p),
                    "pceqb"   => MmiOps3(0x28, 0x0A, p),
                    "padduw"  => MmiOps3(0x28, 0x10, p),
                    "psubuw"  => MmiOps3(0x28, 0x11, p),
                    "pextuw"  => MmiOps3(0x28, 0x12, p),
                    "padduh"  => MmiOps3(0x28, 0x14, p),
                    "psubuh"  => MmiOps3(0x28, 0x15, p),
                    "pextuh"  => MmiOps3(0x28, 0x16, p),
                    "paddub"  => MmiOps3(0x28, 0x18, p),
                    "psubub"  => MmiOps3(0x28, 0x19, p),
                    "pextub"  => MmiOps3(0x28, 0x1A, p),
                    "qfsrv"   => MmiOps3(0x28, 0x1B, p),

                    // ── MMI2 (fn=0x09): mostly rd, rs, rt ────────────────
                    "pmaddw"  => MmiOps3(0x09, 0x00, p),
                    "psllvw"  => MmiOps3(0x09, 0x02, p),
                    "psrlvw"  => MmiOps3(0x09, 0x03, p),
                    "pmsubw"  => MmiOps3(0x09, 0x04, p),
                    "pmfhi"   => MmiRd(0x09, 0x08, p),
                    "pmflo"   => MmiRd(0x09, 0x09, p),
                    "pinth"   => MmiOps3(0x09, 0x0A, p),
                    "pmultw"  => MmiOps3(0x09, 0x0C, p),
                    "pdivw"   => MmiOps3(0x09, 0x0D, p),
                    "pcpyld"  => MmiOps3(0x09, 0x0E, p),
                    "pmaddh"  => MmiOps3(0x09, 0x10, p),
                    "phmadh"  => MmiOps3(0x09, 0x11, p),
                    "pand"    => MmiOps3(0x09, 0x12, p),
                    "pxor"    => MmiOps3(0x09, 0x13, p),
                    "pmsubh"  => MmiOps3(0x09, 0x14, p),
                    "phmsbh"  => MmiOps3(0x09, 0x15, p),
                    "pexeh"   => MmiOps2(0x09, 0x1A, p),
                    "prevh"   => MmiOps2(0x09, 0x1B, p),
                    "pmulth"  => MmiOps3(0x09, 0x1C, p),
                    "pdivbw"  => MmiOps3(0x09, 0x1D, p),
                    "pexew"   => MmiOps2(0x09, 0x1E, p),
                    "prot3w"  => MmiOps2(0x09, 0x1F, p),

                    // ── MMI3 (fn=0x29): mostly rd, rs, rt ────────────────
                    "psravw"  => MmiOps3(0x29, 0x02, p),
                    "pmthi"   => MmiRs(0x29, 0x08, p),
                    "pmtlo"   => MmiRs(0x29, 0x09, p),
                    "pinteh"  => MmiOps3(0x29, 0x0A, p),
                    "pmultuw" => MmiOps3(0x29, 0x0C, p),
                    "pdivuw"  => MmiOps3(0x29, 0x0D, p),
                    "pcpyud"  => MmiOps2(0x29, 0x0E, p),
                    "por"     => MmiOps3(0x29, 0x12, p),
                    "pnor"    => MmiOps3(0x29, 0x13, p),
                    "pexch"   => MmiOps2(0x29, 0x1A, p),
                    "pcpyh"   => MmiOps2(0x29, 0x1B, p),
                    "pexcw"   => MmiOps2(0x29, 0x1E, p),

                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        // ── Encoders ──────────────────────────────────────────────────────

        // SPECIAL: syscall/break/sync code field followed by function.
        private static uint CodeFn(string[] p, uint fn)
        {
            uint code = p.Length > 0 && !string.IsNullOrWhiteSpace(p[0])
                ? ((uint)ParseImm(p[0]) & 0x03FFFFFu)
                : 0u;
            return (code << 6) | fn;
        }

        // R-type: rd, rs, rt  (fn only — opcode=0)
        private static uint RType(string[] p, uint fn)
        {
            uint rd = Reg(p[0]), rs = Reg(p[1]), rt = Reg(p[2]);
            return (rs << 21) | (rt << 16) | (rd << 11) | fn;
        }

        // R-type: rs, rt for standard mult/div, or rd, rs, rt for EE three-operand mult.
        private static uint MultDiv(string[] p, uint fn)
        {
            if (p.Length == 2)
            {
                uint rs = Reg(p[0]), rt = Reg(p[1]);
                return (rs << 21) | (rt << 16) | fn;
            }
            if (p.Length >= 3)
            {
                uint rd = Reg(p[0]), rs = Reg(p[1]), rt = Reg(p[2]);
                return (rs << 21) | (rt << 16) | (rd << 11) | fn;
            }
            throw new FormatException();
        }

        // R-type: rs, rt  (div/divu)
        private static uint RsRt(string[] p, uint fn)
        {
            uint rs = Reg(p[0]), rt = Reg(p[1]);
            return (rs << 21) | (rt << 16) | fn;
        }

        // Shift immediate: rd, rt, sa
        private static uint ShiftImm(string[] p, uint fn)
        {
            uint rd = Reg(p[0]), rt = Reg(p[1]), sa = (uint)ParseImm(p[2]) & 0x1F;
            return (rt << 16) | (rd << 11) | (sa << 6) | fn;
        }

        // Shift variable: rd, rt, rs
        private static uint ShiftVar(string[] p, uint fn)
        {
            uint rd = Reg(p[0]), rt = Reg(p[1]), rs = Reg(p[2]);
            return (rs << 21) | (rt << 16) | (rd << 11) | fn;
        }

        // R-type single rd
        private static uint Rd(string[] p, uint fn) => (Reg(p[0]) << 11) | fn;

        // R-type single rs
        private static uint Rs(string[] p, uint fn) => (Reg(p[0]) << 21) | fn;

        // J-type
        private static uint JType(uint op, string[] p, uint pc)
        {
            uint target = ParseAddr(p[0]);
            return (op << 26) | ((target >> 2) & 0x03FFFFFFu);
        }

        // REGIMM: op=1, rt=sub-op, rs, offset
        private static uint Regimm(uint rt, string[] p, uint pc)
        {
            uint rs = Reg(p[0]);
            short offset = BranchOffset(ParseAddr(p[1]), pc);
            return (0x01u << 26) | (rs << 21) | (rt << 16) | (ushort)offset;
        }

        // I-type: rt, rs, imm
        private static uint IType(uint op, string[] p)
        {
            uint rt = Reg(p[0]), rs = Reg(p[1]);
            ushort imm = (ushort)ParseImm(p[2]);
            return (op << 26) | (rs << 21) | (rt << 16) | imm;
        }

        // Branch with two registers: rs, rt, addr
        private static uint Branch2(uint op, string[] p, uint pc)
        {
            uint rs = Reg(p[0]), rt = Reg(p[1]);
            short offset = BranchOffset(ParseAddr(p[2]), pc);
            return (op << 26) | (rs << 21) | (rt << 16) | (ushort)offset;
        }

        // Branch with one register: rs, addr
        private static uint Branch1(uint op, string[] p, uint pc)
        {
            uint rs = Reg(p[0]);
            short offset = BranchOffset(ParseAddr(p[1]), pc);
            return (op << 26) | (rs << 21) | (ushort)offset;
        }

        // Memory: rt, offset(base)
        private static uint MemOp(uint op, string[] p)
        {
            uint rt = Reg(p[0]);
            string mem = p[1].Trim();
            int lp = mem.IndexOf('('), rp = mem.IndexOf(')');
            if (lp < 0 || rp < 0) throw new FormatException();
            ushort off = (ushort)ParseImm(mem[..lp]);
            uint rs = Reg(mem[(lp + 1)..rp]);
            return (op << 26) | (rs << 21) | (rt << 16) | off;
        }

        // Memory with FPU register: ft, offset(base)
        private static uint MemOpFpu(uint op, string[] p)
        {
            uint ft = FReg(p[0]);
            string mem = p[1].Trim();
            int lp = mem.IndexOf('('), rp = mem.IndexOf(')');
            if (lp < 0 || rp < 0) throw new FormatException();
            ushort off = (ushort)ParseImm(mem[..lp]);
            uint rs = Reg(mem[(lp + 1)..rp]);
            return (op << 26) | (rs << 21) | (ft << 16) | off;
        }

        // COP1.S 3-operand: fd, fs, ft
        private static uint Cop1S3(uint fn, string[] p)
        {
            uint fd = FReg(p[0]), fs = FReg(p[1]), ft = FReg(p[2]);
            return (0x11u << 26) | (0x10u << 21) | (ft << 16) | (fs << 11) | (fd << 6) | fn;
        }

        // COP1.S 2-operand dest+src: fd, fs
        private static uint Cop1S2d(uint fn, string[] p)
        {
            uint fd = FReg(p[0]), fs = FReg(p[1]);
            return (0x11u << 26) | (0x10u << 21) | (0u << 16) | (fs << 11) | (fd << 6) | fn;
        }

        // Code Designer Lite preserves this EE/R5900 form for sqrt.s when fd == fs.
        // Example: sqrt.s $f3, $f3 => 460300C4, not the generic 460018C4 form.
        private static uint Cop1SqrtS(string[] p)
        {
            uint fd = FReg(p[0]), fs = FReg(p[1]);
            if (fd == fs)
                return (0x11u << 26) | (0x10u << 21) | (fd << 16) | (0u << 11) | (fd << 6) | 0x04u;
            return (0x11u << 26) | (0x10u << 21) | (0u << 16) | (fs << 11) | (fd << 6) | 0x04u;
        }

        // COP1.S 2-operand source: fs, ft (accumulator ops and comparisons)
        private static uint Cop1S2s(uint fn, string[] p)
        {
            uint fs = FReg(p[0]), ft = FReg(p[1]);
            return (0x11u << 26) | (0x10u << 21) | (ft << 16) | (fs << 11) | (0u << 6) | fn;
        }

        // COP1.W 2-operand: fd, fs  (cvt.s.w uses fmt=W=0x14)
        private static uint Cop1W2d(uint fn, string[] p)
        {
            uint fd = FReg(p[0]), fs = FReg(p[1]);
            return (0x11u << 26) | (0x14u << 21) | (0u << 16) | (fs << 11) | (fd << 6) | fn;
        }

        // COP1.D 3-operand: fd, fs, ft
        private static uint Cop1D3(uint fn, string[] p)
        {
            uint fd = FReg(p[0]), fs = FReg(p[1]), ft = FReg(p[2]);
            return (0x11u << 26) | (0x11u << 21) | (ft << 16) | (fs << 11) | (fd << 6) | fn;
        }

        // COP1.D 2-operand dest+src: fd, fs
        private static uint Cop1D2d(uint fn, string[] p)
            => Cop1S2dWithFmt(0x11, fn, p);

        private static uint Cop1S2dWithFmt(uint fmt, uint fn, string[] p)
        {
            uint fd = FReg(p[0]), fs = FReg(p[1]);
            return (0x11u << 26) | (fmt << 21) | (0u << 16) | (fs << 11) | (fd << 6) | fn;
        }

        private static uint Cop1Branch(bool taken, string[] p, uint pc)
        {
            uint target = ParseAddr(p[0]);
            ushort offset = (ushort)BranchOffset(target, pc);
            return (0x11u << 26) | (0x08u << 21) | ((taken ? 1u : 0u) << 16) | offset;
        }

        // MMI 3-operand: rd, rs, rt
        private static uint MmiOps3(uint fn, uint sub, string[] p)
        {
            uint rd = Reg(p[0]), rs = Reg(p[1]), rt = Reg(p[2]);
            return (0x1Cu << 26) | (rs << 21) | (rt << 16) | (rd << 11) | (sub << 6) | fn;
        }

        // MMI 2-operand: rd, rt
        private static uint MmiOps2(uint fn, uint sub, string[] p)
        {
            uint rd = Reg(p[0]), rt = Reg(p[1]);
            return (0x1Cu << 26) | (0u << 21) | (rt << 16) | (rd << 11) | (sub << 6) | fn;
        }

        // MMI rd-only
        private static uint MmiRd(uint fn, uint sub, string[] p)
        {
            uint rd = Reg(p[0]);
            return (0x1Cu << 26) | (0u << 21) | (0u << 16) | (rd << 11) | (sub << 6) | fn;
        }

        // MMI rs-only
        private static uint MmiRs(uint fn, uint sub, string[] p)
        {
            uint rs = Reg(p[0]);
            return (0x1Cu << 26) | (rs << 21) | (0u << 16) | (0u << 11) | (sub << 6) | fn;
        }

        // MMI shift: rd, rt, sa  (psllh, psrlh, etc.)
        private static uint MmiShift(uint fn, string[] p)
        {
            uint rd = Reg(p[0]), rt = Reg(p[1]), sa = (uint)ParseImm(p[2]) & 0x1F;
            return (0x1Cu << 26) | (0u << 21) | (rt << 16) | (rd << 11) | (sa << 6) | fn;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static uint Reg(string s)
        {
            s = s.Trim().ToLowerInvariant();
            if (s.StartsWith('$')) s = s[1..];

            int idx = Array.IndexOf(GprAbi, s);
            if (idx >= 0) return (uint)idx;
            if (uint.TryParse(s, out uint n) && n < 32) return n;
            throw new FormatException($"Unknown register: {s}");
        }

        private static uint FReg(string s)
        {
            s = s.Trim().ToLowerInvariant();
            if (s.StartsWith('$')) s = s[1..];
            if (s.StartsWith('f') && uint.TryParse(s[1..], out uint n) && n < 32) return n;
            if (uint.TryParse(s, out uint m) && m < 32) return m;
            throw new FormatException($"Unknown FPU register: {s}");
        }

        private static uint C0Reg(string s)
        {
            s = s.Trim();
            for (int i = 0; i < Cop0Abi.Length; i++)
                if (string.Equals(Cop0Abi[i], s, StringComparison.OrdinalIgnoreCase)) return (uint)i;
            if (s.StartsWith('$') && uint.TryParse(s[1..], out uint n) && n < 32) return n;
            if (uint.TryParse(s, out uint m) && m < 32) return m;
            throw new FormatException($"Unknown COP0 register: {s}");
        }

        private static int ParseImm(string s)
        {
            s = s.Trim();
            bool neg = s.StartsWith('-');
            if (neg) s = s[1..].TrimStart();
            uint v;
            if (s.StartsWith('$'))
                v = Convert.ToUInt32(s[1..], 16);
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                v = Convert.ToUInt32(s[2..], 16);
            else
                v = uint.Parse(s, CultureInfo.InvariantCulture);
            return neg ? -(int)v : (int)v;
        }

        private static uint ParseAddr(string s)
        {
            s = s.Trim();
            // Strip dynamic suffix like " (+66▼)" appended by the GUI
            int sp = s.IndexOf(" (", StringComparison.Ordinal);
            if (sp >= 0) s = s[..sp].Trim();
            if (s.StartsWith('$'))
                return Convert.ToUInt32(s[1..], 16);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(s[2..], 16);
            if (uint.TryParse(s, out uint v)) return v;
            return 0; // unresolved label
        }

        private static short BranchOffset(uint target, uint pc)
            => (short)(((int)target - (int)(pc + 4)) / 4);
    }
}
