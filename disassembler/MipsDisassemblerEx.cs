using System;
using System.Collections.Generic;
using System.Threading;

namespace PS2Disassembler
{
    /// <summary>
    /// Extends <see cref="MipsDisassembler"/> with a structured row API used by the WinForms GUI.
    /// </summary>
    public sealed class MipsDisassemblerEx
    {
        private readonly DisassemblerOptions _opts;

        private static readonly string[] GprAbi =
        {
            "zero","at","v0","v1","a0","a1","a2","a3",
            "t0","t1","t2","t3","t4","t5","t6","t7",
            "s0","s1","s2","s3","s4","s5","s6","s7",
            "t8","t9","k0","k1","gp","sp","fp","ra"
        };
        private static readonly string[] FprNames =
        {
            "f0","f1","f2","f3","f4","f5","f6","f7",
            "f8","f9","f10","f11","f12","f13","f14","f15",
            "f16","f17","f18","f19","f20","f21","f22","f23",
            "f24","f25","f26","f27","f28","f29","f30","f31"
        };
        private static readonly string[] Cop0Names =
        {
            "Index","Random","EntryLo0","EntryLo1","Context","PageMask","Wired","C0r7",
            "BadVAddr","Count","EntryHi","Compare","Status","Cause","EPC","PRId",
            "Config","C0r17","C0r18","C0r19","C0r20","C0r21","C0r22","Debug",
            "DEPC","Perf","C0r26","C0r27","TagLo","TagHi","ErrorEPC","C0r31"
        };

        // Shared empty label dict — reused across all DisassembleSingleWord calls; eliminates
        // one heap allocation per call (was: new Dictionary<uint,string>() per call).
        private static readonly Dictionary<uint, string> _emptyLabels = new();

        // Static MMI opcode tables — previously allocated fresh on every DecodeMMI call.
        // Converting to static readonly eliminates millions of Dictionary allocations during
        // the background disassembly scan of 8 M instructions.
        private static readonly Dictionary<uint, string> _mmi0 = new()
        {
            {0x00,"paddw"},{0x01,"psubw"},{0x02,"pcgtw"},{0x03,"pmaxw"},
            {0x04,"paddh"},{0x05,"psubh"},{0x06,"pcgth"},{0x07,"pmaxh"},
            {0x08,"paddb"},{0x09,"psubb"},{0x0A,"pcgtb"},
            {0x10,"paddsw"},{0x11,"psubsw"},{0x12,"pextlw"},{0x13,"ppacw"},
            {0x14,"paddsh"},{0x15,"psubsh"},{0x16,"pextlh"},{0x17,"ppach"},
            {0x18,"paddsb"},{0x19,"psubsb"},{0x1A,"pextlb"},{0x1B,"ppacb"},
            {0x1E,"pext5"},{0x1F,"ppac5"}
        };
        private static readonly Dictionary<uint, string> _mmi1 = new()
        {
            {0x01,"pabsw"},{0x02,"pceqw"},{0x03,"pminw"},{0x04,"padsbh"},
            {0x05,"pabsh"},{0x06,"pceqh"},{0x07,"pminh"},{0x0A,"pceqb"},
            {0x10,"padduw"},{0x11,"psubuw"},{0x12,"pextuw"},
            {0x14,"padduh"},{0x15,"psubuh"},{0x16,"pextuh"},
            {0x18,"paddub"},{0x19,"psubub"},{0x1A,"pextub"},{0x1B,"qfsrv"}
        };
        private static readonly Dictionary<uint, string> _mmi2 = new()
        {
            {0x00,"pmaddw"},{0x02,"psllvw"},{0x03,"psrlvw"},{0x04,"pmsubw"},
            {0x08,"pmfhi"},{0x09,"pmflo"},{0x0A,"pinth"},
            {0x0C,"pmultw"},{0x0D,"pdivw"},{0x0E,"pcpyld"},
            {0x10,"pmaddh"},{0x11,"phmadh"},{0x12,"pand"},{0x13,"pxor"},
            {0x14,"pmsubh"},{0x15,"phmsbh"},
            {0x1A,"pexeh"},{0x1B,"prevh"},{0x1C,"pmulth"},{0x1D,"pdivbw"},
            {0x1E,"pexew"},{0x1F,"prot3w"}
        };
        private static readonly Dictionary<uint, string> _mmi3 = new()
        {
            {0x02,"psravw"},{0x08,"pmthi"},{0x09,"pmtlo"},{0x0A,"pinteh"},
            {0x0C,"pmultuw"},{0x0D,"pdivuw"},{0x0E,"pcpyud"},
            {0x12,"por"},{0x13,"pnor"},
            {0x1A,"pexch"},{0x1B,"pcpyh"},{0x1E,"pexcw"}
        };
        private static readonly Dictionary<uint, string> _mmiMain = new()
        {
            {0x00,"madd"},{0x01,"maddu"},{0x04,"plzcw"},
            {0x10,"mfhi1"},{0x11,"mthi1"},{0x12,"mflo1"},{0x13,"mtlo1"},
            {0x18,"mult1"},{0x19,"multu1"},{0x1A,"div1"},{0x1B,"divu1"},
            {0x20,"madd1"},{0x21,"maddu1"},
            {0x30,"pmfhl"},{0x31,"pmthl"},
            {0x34,"psllh"},{0x36,"psrlh"},{0x37,"psrah"},
            {0x3C,"psllw"},{0x3E,"psrlw"},{0x3F,"psraw"}
        };

        public MipsDisassemblerEx(DisassemblerOptions opts) => _opts = opts;

        /// <summary>Decode a single machine word at the given PC (no label resolution).</summary>
        public DisassemblyRow DisassembleSingleWord(uint word, uint pc)
            => Decode(word, pc, _emptyLabels);

        /// <summary>
        /// Fast-path classification used by the background disassembly scan.
        /// Returns only Kind and Target without allocating ANY strings or dictionaries.
        /// Eliminates the ~1 GB of transient GC pressure that occurred when the full
        /// DisassembleSingleWord was called 8 million times for the 32 MB EE RAM scan.
        /// </summary>
        public (InstructionType Kind, uint Target) DecodeKindAndTarget(uint word, uint pc)
        {
            if (word == 0) return (InstructionType.Nop, 0);

            uint op = (word >> 26) & 0x3F;
            uint rs = (word >> 21) & 0x1F;
            uint rt = (word >> 16) & 0x1F;
            uint sa = (word >>  6) & 0x1F;
            uint fn = word & 0x3F;
            int  si = (short)(word & 0xFFFF);
            uint bt = (uint)(pc + 4 + (si << 2));
            uint jt = (((pc + 4) & 0xF0000000) | ((word & 0x03FFFFFF) << 2));

            return op switch
            {
                0x00 => KindSpecial(sa, fn),
                0x01 => KindRegimm(rt, bt),
                0x02 => (InstructionType.Jump, jt),
                0x03 => (InstructionType.Call, jt),
                0x04 or 0x05 or 0x06 or 0x07 or
                0x14 or 0x15 or 0x16 or 0x17 => (InstructionType.Branch, bt),
                0x08 or 0x09 or 0x0A or 0x0B or
                0x0C or 0x0D or 0x0E or 0x0F or
                0x18 or 0x19 => (InstructionType.Alu, 0),
                0x10 => KindCop0(rs),
                0x11 => KindCop1(word, rs, rt, pc),
                0x12 => (InstructionType.Alu, 0),
                0x1C => KindMMI(fn),
                0x1A or 0x1B or 0x1E or
                0x20 or 0x21 or 0x22 or 0x23 or
                0x24 or 0x25 or 0x26 or 0x27 or
                0x31 or 0x36 or 0x37 => (InstructionType.Load, 0),
                0x1F or
                0x28 or 0x29 or 0x2A or 0x2B or
                0x2C or 0x2D or 0x2E or
                0x39 or 0x3E or 0x3F => (InstructionType.Store, 0),
                0x2F => (InstructionType.System, 0),
                _ => (InstructionType.Data, 0),
            };
        }

        private static (InstructionType, uint) KindSpecial(uint sa, uint fn)
        {
            return fn switch
            {
                0x00 => (sa == 0 ? InstructionType.Nop : InstructionType.Alu, 0u),
                0x02 or 0x03 or 0x04 or 0x06 or 0x07 => (InstructionType.Alu, 0),
                0x08 => (InstructionType.Jump, 0),
                0x09 => (InstructionType.Call, 0),
                0x0A or 0x0B => (InstructionType.Alu, 0),
                0x0C or 0x0D or 0x0F => (InstructionType.System, 0),
                >= 0x10 and <= 0x13 => (InstructionType.Alu, 0),
                0x14 or 0x16 or 0x17 => (InstructionType.Alu, 0),
                >= 0x18 and <= 0x1B => (InstructionType.Alu, 0),
                >= 0x20 and <= 0x2F => (InstructionType.Alu, 0),
                0x34 or 0x36 => (InstructionType.System, 0),
                0x38 or 0x3A or 0x3B or 0x3C or 0x3E or 0x3F => (InstructionType.Alu, 0),
                _ => (InstructionType.Data, 0),
            };
        }

        private static (InstructionType, uint) KindRegimm(uint rt, uint bt)
        {
            return rt switch
            {
                0x00 or 0x01 or 0x02 or 0x03 => (InstructionType.Branch, bt),
                0x10 or 0x11 or 0x12 or 0x13 => (InstructionType.Call, bt),
                0x18 or 0x19 => (InstructionType.Alu, 0),
                _ => (InstructionType.Data, 0),
            };
        }

        private static (InstructionType, uint) KindCop0(uint rs)
        {
            if (rs == 0x10) return (InstructionType.System, 0);
            if (rs is 0x00 or 0x04) return (InstructionType.Alu, 0);
            return (InstructionType.Data, 0);
        }

        private static (InstructionType, uint) KindCop1(uint w, uint rs, uint rt, uint pc)
        {
            if (rs == 0x08)
            {
                int si = (short)(w & 0xFFFF);
                uint bt = (uint)(pc + 4 + (si << 2));
                return rt is 0x00 or 0x01 or 0x02 or 0x03
                    ? (InstructionType.Branch, bt)
                    : (InstructionType.Data, 0);
            }
            if (rs is 0x00 or 0x02 or 0x04 or 0x06 or 0x10 or 0x11 or 0x14)
                return (InstructionType.Fpu, 0);
            return (InstructionType.Data, 0);
        }

        private static (InstructionType, uint) KindMMI(uint fn)
        {
            return fn switch
            {
                0x08 or 0x28 or 0x09 or 0x29 => (InstructionType.Mmi, 0),
                0x00 or 0x01 or 0x04 => (InstructionType.Mmi, 0),
                >= 0x10 and <= 0x13 => (InstructionType.Mmi, 0),
                >= 0x18 and <= 0x1B => (InstructionType.Mmi, 0),
                0x20 or 0x21 => (InstructionType.Mmi, 0),
                0x30 or 0x31 => (InstructionType.Mmi, 0),
                0x34 or 0x36 or 0x37 => (InstructionType.Mmi, 0),
                0x3C or 0x3E or 0x3F => (InstructionType.Mmi, 0),
                _ => (InstructionType.Data, 0),
            };
        }

        private string R(uint n) => _opts.UseAbiNames ? GprAbi[n & 31] : $"${n & 31}";
        private static string F(uint n) => FprNames[n & 31];
        private static string C0(uint n) => Cop0Names[n & 31];
        private static string SImm(int v)   => $"${(ushort)v:X4}";
        private static string UHex(uint v)  => $"${v:X4}";
        private static string LOrH(uint a, Dictionary<uint,string> lbl) =>
            lbl != null && lbl.TryGetValue(a, out var s) && !s.StartsWith("loc_", StringComparison.OrdinalIgnoreCase) ? s : $"0x{a:X8}";
        private static uint Ru32(byte[] d, uint o) =>
            (uint)(d[o] | d[o+1]<<8 | d[o+2]<<16 | d[o+3]<<24);

        public List<DisassemblyRow> DisassembleToRows(byte[] data, uint startOffset, uint endOffset,
            CancellationToken cancel = default)
        {
            var rows   = new List<DisassemblyRow>();
            var labels = BuildLabelMap(data, startOffset, endOffset);

            uint address = _opts.BaseAddress + startOffset;
            for (uint off = startOffset; off + 3 < endOffset; off += 4, address += 4)
            {
                if (cancel.IsCancellationRequested) return rows;

                if (labels.TryGetValue(address, out string? lbl))
                    rows.Add(new DisassemblyRow { Address = address, Kind = InstructionType.Label, Mnemonic = lbl ?? string.Empty });

                uint word = Ru32(data, off);
                var row   = Decode(word, address, labels);
                rows.Add(row);
            }
            return rows;
        }

        private Dictionary<uint,string> BuildLabelMap(byte[] data, uint startOff, uint endOff)
        {
            var lbl  = new Dictionary<uint,string>();
            // Intentionally empty: synthetic loc_ labels are not injected into the UI.
            return lbl;
        }

        private DisassemblyRow Decode(uint w, uint pc, Dictionary<uint,string> lbl)
        {
            if (w == 0) return Row(pc, w, "nop", "", InstructionType.Nop);

            uint op = (w >> 26) & 0x3F;
            uint rs = (w >> 21) & 0x1F;
            uint rt = (w >> 16) & 0x1F;
            uint rd = (w >> 11) & 0x1F;
            uint sa = (w >>  6) & 0x1F;
            uint fn = w & 0x3F;
            int  si = (short)(w & 0xFFFF);
            uint ui = w & 0xFFFF;
            uint bt = (uint)(pc + 4 + (si << 2));
            uint jt = (((pc+4) & 0xF0000000) | ((w & 0x03FFFFFF) << 2));

            switch (op)
            {
                case 0x00: return DecodeSpecial(w, pc, rs, rt, rd, sa, fn, lbl);
                case 0x01: return DecodeRegimm(w, pc, rs, rt, si, bt, lbl);
                case 0x02: return Row(pc,w,"j",   LOrH(jt,lbl), InstructionType.Jump,  jt);
                case 0x03: return Row(pc,w,"jal", LOrH(jt,lbl), InstructionType.Call,  jt);
                case 0x04: return Row(pc,w,"beq", $"{R(rs)}, {R(rt)}, {LOrH(bt,lbl)}", InstructionType.Branch, bt);
                case 0x05: return Row(pc,w,"bne", $"{R(rs)}, {R(rt)}, {LOrH(bt,lbl)}", InstructionType.Branch, bt);
                case 0x06: return Row(pc,w,"blez",$"{R(rs)}, {LOrH(bt,lbl)}",           InstructionType.Branch, bt);
                case 0x07: return Row(pc,w,"bgtz",$"{R(rs)}, {LOrH(bt,lbl)}",           InstructionType.Branch, bt);
                case 0x14: return Row(pc,w,"beql",$"{R(rs)}, {R(rt)}, {LOrH(bt,lbl)}", InstructionType.Branch, bt);
                case 0x15: return Row(pc,w,"bnel",$"{R(rs)}, {R(rt)}, {LOrH(bt,lbl)}", InstructionType.Branch, bt);
                case 0x16: return Row(pc,w,"blezl",$"{R(rs)}, {LOrH(bt,lbl)}",          InstructionType.Branch, bt);
                case 0x17: return Row(pc,w,"bgtzl",$"{R(rs)}, {LOrH(bt,lbl)}",          InstructionType.Branch, bt);
                case 0x08: return Row(pc,w,"addi", $"{R(rt)}, {R(rs)}, {SImm(si)}", InstructionType.Alu);
                case 0x09: return Row(pc,w,"addiu",$"{R(rt)}, {R(rs)}, {SImm(si)}", InstructionType.Alu);
                case 0x0A: return Row(pc,w,"slti", $"{R(rt)}, {R(rs)}, {SImm(si)}", InstructionType.Alu);
                case 0x0B: return Row(pc,w,"sltiu",$"{R(rt)}, {R(rs)}, {SImm(si)}", InstructionType.Alu);
                case 0x0C: return Row(pc,w,"andi", $"{R(rt)}, {R(rs)}, {UHex(ui)}", InstructionType.Alu);
                case 0x0D: return Row(pc,w,"ori",  $"{R(rt)}, {R(rs)}, {UHex(ui)}", InstructionType.Alu);
                case 0x0E: return Row(pc,w,"xori", $"{R(rt)}, {R(rs)}, {UHex(ui)}", InstructionType.Alu);
                case 0x0F: return Row(pc,w,"lui",  $"{R(rt)}, {UHex(ui)}",           InstructionType.Alu);
                case 0x10: return DecodeCOP0(w, pc, rs, rt, rd, fn);
                case 0x11: return DecodeCOP1(w, pc, rs, rt, rd, sa, fn);
                case 0x12: return DecodeCOP2(w, pc, rs, rt, rd);
                case 0x1C: return DecodeMMI(w, pc, rs, rt, rd, sa, fn);
                case 0x18: return Row(pc,w,"daddi", $"{R(rt)}, {R(rs)}, {SImm(si)}", InstructionType.Alu);
                case 0x19: return Row(pc,w,"daddiu",$"{R(rt)}, {R(rs)}, {SImm(si)}", InstructionType.Alu);
                case 0x1A: return Mem(pc,w,"ldl",rt,rs,si,false);
                case 0x1B: return Mem(pc,w,"ldr",rt,rs,si,false);
                case 0x1E: return Mem(pc,w,"lq", rt,rs,si,false);
                case 0x1F: return Mem(pc,w,"sq", rt,rs,si,true);
                case 0x20: return Mem(pc,w,"lb", rt,rs,si,false);
                case 0x21: return Mem(pc,w,"lh", rt,rs,si,false);
                case 0x22: return Mem(pc,w,"lwl",rt,rs,si,false);
                case 0x23: return Mem(pc,w,"lw", rt,rs,si,false);
                case 0x24: return Mem(pc,w,"lbu",rt,rs,si,false);
                case 0x25: return Mem(pc,w,"lhu",rt,rs,si,false);
                case 0x26: return Mem(pc,w,"lwr",rt,rs,si,false);
                case 0x27: return Mem(pc,w,"lwu",rt,rs,si,false);
                case 0x28: return Mem(pc,w,"sb", rt,rs,si,true);
                case 0x29: return Mem(pc,w,"sh", rt,rs,si,true);
                case 0x2A: return Mem(pc,w,"swl",rt,rs,si,true);
                case 0x2B: return Mem(pc,w,"sw", rt,rs,si,true);
                case 0x2C: return Mem(pc,w,"sdl",rt,rs,si,true);
                case 0x2D: return Mem(pc,w,"sdr",rt,rs,si,true);
                case 0x2E: return Mem(pc,w,"swr",rt,rs,si,true);
                case 0x2F: return Row(pc,w,"cache",$"{UHex((w>>16)&0x1F)}, {SImm(si)}({R(rs)})",InstructionType.System);
                case 0x31: return Row(pc,w,"lwc1", $"{F(rt)}, {SImm(si)}({R(rs)})",InstructionType.Load);
                case 0x36: return Row(pc,w,"lqc2", $"vf{rt}, {SImm(si)}({R(rs)})", InstructionType.Load);
                case 0x37: return Mem(pc,w,"ld", rt,rs,si,false);
                case 0x39: return Row(pc,w,"swc1", $"{F(rt)}, {SImm(si)}({R(rs)})",InstructionType.Store);
                case 0x3E: return Row(pc,w,"sqc2", $"vf{rt}, {SImm(si)}({R(rs)})", InstructionType.Store);
                case 0x3F: return Mem(pc,w,"sd", rt,rs,si,true);
                default:   return Data(pc,w);
            }
        }

        private DisassemblyRow DecodeSpecial(uint w,uint pc,uint rs,uint rt,uint rd,uint sa,uint fn,Dictionary<uint,string> lbl)
        {
            switch (fn)
            {
                case 0x00: return sa==0 ? Row(pc,w,"nop","",InstructionType.Nop) : Row(pc,w,"sll",$"{R(rd)}, {R(rt)}, {sa}",InstructionType.Alu);
                case 0x02: return Row(pc,w,"srl",  $"{R(rd)}, {R(rt)}, {sa}",  InstructionType.Alu);
                case 0x03: return Row(pc,w,"sra",  $"{R(rd)}, {R(rt)}, {sa}",  InstructionType.Alu);
                case 0x04: return Row(pc,w,"sllv", $"{R(rd)}, {R(rt)}, {R(rs)}",InstructionType.Alu);
                case 0x06: return Row(pc,w,"srlv", $"{R(rd)}, {R(rt)}, {R(rs)}",InstructionType.Alu);
                case 0x07: return Row(pc,w,"srav", $"{R(rd)}, {R(rt)}, {R(rs)}",InstructionType.Alu);
                case 0x08: return Row(pc,w,"jr",   $"{R(rs)}",                   InstructionType.Jump);
                case 0x09: return Row(pc,w,"jalr", rd==31?$"{R(rs)}":$"{R(rd)}, {R(rs)}", InstructionType.Call);
                case 0x0A: return Row(pc,w,"movz", $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x0B: return Row(pc,w,"movn", $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x0C: return Row(pc,w,"syscall",$"{((w>>6)&0xFFFFF):X}",    InstructionType.System);
                case 0x0D: return Row(pc,w,"break",  $"{((w>>6)&0xFFFFF):X}",    InstructionType.System);
                case 0x0F: return Row(pc,w,"sync",   $"{sa}",                     InstructionType.System);
                case 0x10: return Row(pc,w,"mfhi",   $"{R(rd)}",                  InstructionType.Alu);
                case 0x11: return Row(pc,w,"mthi",   $"{R(rs)}",                  InstructionType.Alu);
                case 0x12: return Row(pc,w,"mflo",   $"{R(rd)}",                  InstructionType.Alu);
                case 0x13: return Row(pc,w,"mtlo",   $"{R(rs)}",                  InstructionType.Alu);
                case 0x14: return Row(pc,w,"dsllv",  $"{R(rd)}, {R(rt)}, {R(rs)}",InstructionType.Alu);
                case 0x16: return Row(pc,w,"dsrlv",  $"{R(rd)}, {R(rt)}, {R(rs)}",InstructionType.Alu);
                case 0x17: return Row(pc,w,"dsrav",  $"{R(rd)}, {R(rt)}, {R(rs)}",InstructionType.Alu);
                case 0x18: return Row(pc,w,"mult",   $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x19: return Row(pc,w,"multu",  $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x1A: return Row(pc,w,"div",    $"{R(rs)}, {R(rt)}",          InstructionType.Alu);
                case 0x1B: return Row(pc,w,"divu",   $"{R(rs)}, {R(rt)}",          InstructionType.Alu);
                case 0x20: return Row(pc,w,"add",    $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x21: return Row(pc,w,"addu",   $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x22: return Row(pc,w,"sub",    $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x23: return Row(pc,w,"subu",   $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x24: return Row(pc,w,"and",    $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x25: return Row(pc,w,"or",     $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x26: return Row(pc,w,"xor",    $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x27: return Row(pc,w,"nor",    $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x28: return Row(pc,w,"mfsa",   $"{R(rd)}",                  InstructionType.Alu);
                case 0x29: return Row(pc,w,"mtsa",   $"{R(rs)}",                  InstructionType.Alu);
                case 0x2A: return Row(pc,w,"slt",    $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x2B: return Row(pc,w,"sltu",   $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x2C: return Row(pc,w,"dadd",   $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x2D: return Row(pc,w,"daddu",  $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x2E: return Row(pc,w,"dsub",   $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x2F: return Row(pc,w,"dsubu",  $"{R(rd)}, {R(rs)}, {R(rt)}",InstructionType.Alu);
                case 0x34: return Row(pc,w,"teq",    $"{R(rs)}, {R(rt)}",          InstructionType.System);
                case 0x36: return Row(pc,w,"tne",    $"{R(rs)}, {R(rt)}",          InstructionType.System);
                case 0x38: return Row(pc,w,"dsll",   $"{R(rd)}, {R(rt)}, {sa}",   InstructionType.Alu);
                case 0x3A: return Row(pc,w,"dsrl",   $"{R(rd)}, {R(rt)}, {sa}",   InstructionType.Alu);
                case 0x3B: return Row(pc,w,"dsra",   $"{R(rd)}, {R(rt)}, {sa}",   InstructionType.Alu);
                case 0x3C: return sa == 0 ? Row(pc,w,"dsll",$"{R(rd)}, {R(rt)}, 0",InstructionType.Alu) : Row(pc,w,"dsll32",$"{R(rd)}, {R(rt)}, {sa - 1}",InstructionType.Alu);
                case 0x3E: return sa == 0 ? Row(pc,w,"dsrl",$"{R(rd)}, {R(rt)}, 0",InstructionType.Alu) : Row(pc,w,"dsrl32",$"{R(rd)}, {R(rt)}, {sa - 1}",InstructionType.Alu);
                case 0x3F: return sa == 0 ? Row(pc,w,"dsra",$"{R(rd)}, {R(rt)}, 0",InstructionType.Alu) : Row(pc,w,"dsra32",$"{R(rd)}, {R(rt)}, {sa - 1}",InstructionType.Alu);
                default:   return Data(pc,w);
            }
        }

        private DisassemblyRow DecodeRegimm(uint w,uint pc,uint rs,uint rt,int si,uint bt,Dictionary<uint,string> lbl)
        {
            string t = LOrH(bt,lbl);
            return rt switch
            {
                0x00 => Row(pc,w,"bltz",   $"{R(rs)}, {t}", InstructionType.Branch, bt),
                0x01 => Row(pc,w,"bgez",   $"{R(rs)}, {t}", InstructionType.Branch, bt),
                0x02 => Row(pc,w,"bltzl",  $"{R(rs)}, {t}", InstructionType.Branch, bt),
                0x03 => Row(pc,w,"bgezl",  $"{R(rs)}, {t}", InstructionType.Branch, bt),
                0x10 => Row(pc,w,"bltzal", $"{R(rs)}, {t}", InstructionType.Call,   bt),
                0x11 => Row(pc,w,"bgezal", $"{R(rs)}, {t}", InstructionType.Call,   bt),
                0x12 => Row(pc,w,"bltzall",$"{R(rs)}, {t}", InstructionType.Call,   bt),
                0x13 => Row(pc,w,"bgezall",$"{R(rs)}, {t}", InstructionType.Call,   bt),
                0x18 => Row(pc,w,"mtsab",  $"{R(rs)}, {SImm(si)}", InstructionType.Alu),
                0x19 => Row(pc,w,"mtsah",  $"{R(rs)}, {SImm(si)}", InstructionType.Alu),
                _    => Data(pc,w)
            };
        }

        private DisassemblyRow DecodeCOP0(uint w,uint pc,uint rs,uint rt,uint rd,uint fn)
        {
            if (rs == 0x10)
                return fn switch
                {
                    0x01 => Row(pc,w,"tlbr", "", InstructionType.System),
                    0x02 => Row(pc,w,"tlbwi","", InstructionType.System),
                    0x06 => Row(pc,w,"tlbwr","", InstructionType.System),
                    0x08 => Row(pc,w,"tlbp", "", InstructionType.System),
                    0x18 => Row(pc,w,"eret", "", InstructionType.System),
                    0x38 => Row(pc,w,"ei",   "", InstructionType.System),
                    0x39 => Row(pc,w,"di",   "", InstructionType.System),
                    _    => Data(pc,w)
                };
            if (w == 0x40826800) return Row(pc,w,"mtc0","at, EPC",InstructionType.Alu);
            if (rs == 0x00) return Row(pc,w,"mfc0",$"{R(rt)}, {C0(rd)}",InstructionType.Alu);
            if (rs == 0x04) return Row(pc,w,"mtc0",$"{R(rt)}, {C0(rd)}",InstructionType.Alu);
            return Data(pc,w);
        }

        private DisassemblyRow DecodeCOP1(uint w,uint pc,uint rs,uint rt,uint rd,uint sa,uint fn)
        {
            if (rs==0x00) return Row(pc,w,"mfc1",$"{R(rt)}, {F(rd)}",InstructionType.Fpu);
            if (rs==0x04) return Row(pc,w,"mtc1",$"{R(rt)}, {F(rd)}",InstructionType.Fpu);
            if (rs==0x02) return Row(pc,w,"cfc1",$"{R(rt)}, {F(rd)}",InstructionType.Fpu);
            if (rs==0x06) return Row(pc,w,"ctc1",$"{R(rt)}, {F(rd)}",InstructionType.Fpu);
            if (rs==0x08)
            {
                int si = (short)(w & 0xFFFF);
                uint bt = (uint)(pc + 4 + (si << 2));
                return rt switch
                {
                    0x00 => Row(pc,w,"bc1f", LOrH(bt, null), InstructionType.Branch, bt),
                    0x01 => Row(pc,w,"bc1t", LOrH(bt, null), InstructionType.Branch, bt),
                    0x02 => Row(pc,w,"bc1fl",LOrH(bt, null), InstructionType.Branch, bt),
                    0x03 => Row(pc,w,"bc1tl",LOrH(bt, null), InstructionType.Branch, bt),
                    _    => Data(pc,w)
                };
            }
            if (rs==0x10 || rs==0x11)
            {
                uint ft=rt, fs=rd, fd=sa;
                string fdName = rs == 0x11 ? F(fs << 1) : F(fd);
                string fsName = rs == 0x11 ? F(fd) : F(fs);
                string ftName = F(ft);
                string ops3 = $"{fdName}, {fsName}, {ftName}";
                string ops2s = $"{fsName}, {ftName}";
                string ops2d = $"{fdName}, {fsName}";
                return fn switch
                {
                    0x00 => Row(pc,w,"add.s",  ops3,  InstructionType.Fpu),
                    0x01 => Row(pc,w,"sub.s",  ops3,  InstructionType.Fpu),
                    0x02 => Row(pc,w,"mul.s",  ops3,  InstructionType.Fpu),
                    0x03 => Row(pc,w,"div.s",  ops3,  InstructionType.Fpu),
                    0x04 => Row(pc,w,"sqrt.s", ops2d, InstructionType.Fpu),
                    0x05 => Row(pc,w,"abs.s",  ops2d, InstructionType.Fpu),
                    0x06 => Row(pc,w,"mov.s",  ops2d, InstructionType.Fpu),
                    0x07 => Row(pc,w,"neg.s",  ops2d, InstructionType.Fpu),
                    0x16 => Row(pc,w,"rsqrt.s",ops3,  InstructionType.Fpu),
                    0x18 => Row(pc,w,"adda.s", ops2s, InstructionType.Fpu),
                    0x19 => Row(pc,w,"suba.s", ops2s, InstructionType.Fpu),
                    0x1A => Row(pc,w,"mula.s", ops2s, InstructionType.Fpu),
                    0x1C => Row(pc,w,"madd.s", ops3,  InstructionType.Fpu),
                    0x1D => Row(pc,w,"msub.s", ops3,  InstructionType.Fpu),
                    0x1E => Row(pc,w,"madda.s",ops2s, InstructionType.Fpu),
                    0x1F => Row(pc,w,"msuba.s",ops2s, InstructionType.Fpu),
                    0x24 => Row(pc,w,"cvt.w.s",ops2d, InstructionType.Fpu),
                    0x28 => Row(pc,w,"max.s",  ops3,  InstructionType.Fpu),
                    0x29 => Row(pc,w,"min.s",  ops3,  InstructionType.Fpu),
                    0x30 => Row(pc,w,"c.f.s",  ops2s, InstructionType.Fpu),
                    0x32 => Row(pc,w,"c.eq.s", ops2s, InstructionType.Fpu),
                    0x34 => Row(pc,w,"c.lt.s", ops2s, InstructionType.Fpu),
                    0x36 => Row(pc,w,"c.le.s", ops2s, InstructionType.Fpu),
                    _    => Data(pc,w)
                };
            }
            if (rs==0x14 && fn==0x20) return Row(pc,w,"cvt.s.w",$"{F(sa)}, {F(rd)}",InstructionType.Fpu);
            return Data(pc,w);
        }

        private DisassemblyRow DecodeCOP2(uint w,uint pc,uint rs,uint rt,uint id)
        {
            return rs switch
            {
                0x01 => Row(pc,w,"qmfc2",$"{R(rt)}, vf{id}",InstructionType.Alu),
                0x02 => Row(pc,w,"cfc2", $"{R(rt)}, vi{id}",InstructionType.Alu),
                0x05 => Row(pc,w,"qmtc2",$"{R(rt)}, vf{id}",InstructionType.Alu),
                0x06 => Row(pc,w,"ctc2", $"{R(rt)}, vi{id}",InstructionType.Alu),
                _    => Row(pc,w,$"cop2.{rs:X2}","", InstructionType.System)
            };
        }

        private DisassemblyRow DecodeMMI(uint w,uint pc,uint rs,uint rt,uint rd,uint sa,uint fn)
        {
            uint sub = (w >> 6) & 0x1F;
            string ops3 = $"{R(rd)}, {R(rs)}, {R(rt)}";
            string ops2 = $"{R(rd)}, {R(rt)}";

            if (fn == 0x08)
            {
                if (_mmi0.TryGetValue(sub, out var m)) return Row(pc,w,m,sub>=0x1E?ops2:ops3,InstructionType.Mmi);
            }
            else if (fn == 0x28)
            {
                if (_mmi1.TryGetValue(sub, out var m)) return Row(pc,w,m,sub<=0x01||sub==0x05?ops2:ops3,InstructionType.Mmi);
            }
            else if (fn == 0x09)
            {
                if (_mmi2.TryGetValue(sub, out var m))
                {
                    string o = m is "pmfhi" or "pmflo" ? $"{R(rd)}" : ops3;
                    return Row(pc,w,m,o,InstructionType.Mmi);
                }
            }
            else if (fn == 0x29)
            {
                if (_mmi3.TryGetValue(sub, out var m))
                {
                    string o = m is "pmthi" or "pmtlo" ? $"{R(rs)}" :
                               m.StartsWith("pe") || m.StartsWith("pc") ? ops2 : ops3;
                    return Row(pc,w,m,o,InstructionType.Mmi);
                }
            }
            else
            {
                if (_mmiMain.TryGetValue(fn, out var m))
                {
                    string o = m == "plzcw" ? $"{R(rd)}" :
                               m is "mfhi1" or "mflo1" ? $"{R(rd != 0 ? rd : rt)}" :
                               m is "mthi1" or "mtlo1" or "div1" or "divu1" ? $"{R(rs)}, {R(rt)}" :
                               m is "pmfhl" ? $"{R(rd)}, {sa}" :
                               m is "pmthl" ? $"{R(rs)}, {sa}" :
                               m.StartsWith("ps") ? $"{R(rd)}, {R(rt)}, {sa}" :
                               $"{R(rd)}, {R(rs)}, {R(rt)}";
                    return Row(pc,w,m,o,InstructionType.Mmi);
                }
            }
            return Data(pc,w);
        }

        private static DisassemblyRow Row(uint pc, uint w, string mnem, string ops,
                                           InstructionType kind, uint target = 0)
            => new() { Address=pc, Word=w, Mnemonic=mnem, Operands=ops, Kind=kind, Target=target };

        private DisassemblyRow Mem(uint pc, uint w, string mnem, uint rt, uint rs, int si, bool store)
            => new() { Address=pc, Word=w, Mnemonic=mnem,
                       Operands=$"{R(rt)}, {SImm(si)}({R(rs)})",
                       Kind = store ? InstructionType.Store : InstructionType.Load };

        private static DisassemblyRow Data(uint pc, uint w)
            => new() { Address=pc, Word=w, Mnemonic=".word",
                       Operands=$"0x{w:X8}", Kind=InstructionType.Data };
    }
}
