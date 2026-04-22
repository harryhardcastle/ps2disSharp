using System;
using System.Collections.Generic;

namespace PS2Disassembler
{
    /// <summary>
    /// Built-in self-test. Run with: ps2dis --test
    /// Encodes known MIPS instructions and verifies the disassembly output.
    /// </summary>
    public static class DisassemblerTests
    {
        private static int _pass, _fail;

        public static void Run()
        {
            Console.WriteLine("Running disassembler self-tests...\n");
            _pass = _fail = 0;

            var dis = new MipsDisassembler(new DisassemblerOptions());

            // ── SPECIAL ─────────────────────────────────────────────────────
            T(dis, 0x00000000, "nop");
            T(dis, 0x00421020, "add     v0, v0, v0");        // add  rd,rs,rt
            T(dis, 0x00621821, "addu    v1, v1, v0");
            T(dis, 0x00221024, "and     v0, at, v0");
            T(dis, 0x0000000D, "break   0");
            T(dis, 0x0062001A, "div     v1, v0");
            T(dis, 0x0062001B, "divu    v1, v0");
            T(dis, 0x00411008, "jr      v0");                 // jr rs  (rd ignored)
            T(dis, 0x00600008, "jr      v1");
            T(dis, 0x0040F809, "jalr    v0");                 // jalr rs (rd defaults to ra)
            T(dis, 0x0320F809, "jalr    t9");                 // jalr t9
            T(dis, 0x00401009, "jalr    v0, v0");             // jalr rd, rs
            T(dis, 0x00421012, "mflo    v0");
            T(dis, 0x00421010, "mfhi    v0");
            T(dis, 0x00400011, "mthi    v0");
            T(dis, 0x00400013, "mtlo    v0");
            T(dis, 0x00621818, "mult    v1, v1, v0");
            T(dis, 0x00621819, "multu   v1, v1, v0");
            T(dis, 0x01FF0019, "multu   t7, ra");
            T(dis, 0x00221025, "or      v0, at, v0");
            T(dis, 0x00221027, "nor     v0, at, v0");
            T(dis, 0x00021080, "sll     v0, v0, 2");
            T(dis, 0x00221004, "sllv    v0, v0, at");
            T(dis, 0x0002108B, "sra     v0, v0, 2");
            T(dis, 0x00221007, "srav    v0, v0, at");
            T(dis, 0x00021082, "srl     v0, v0, 2");
            T(dis, 0x00221006, "srlv    v0, v0, at");
            T(dis, 0x0000000C, "syscall 0");
            T(dis, 0x00221022, "sub     v0, at, v0");
            T(dis, 0x00221023, "subu    v0, at, v0");
            T(dis, 0x0022102A, "slt     v0, at, v0");
            T(dis, 0x0022102B, "sltu    v0, at, v0");
            T(dis, 0x00221026, "xor     v0, at, v0");

            // 64-bit SPECIAL
            T(dis, 0x0002103C, "dsll    v0, v0, 0");
            T(dis, 0x0002103F, "dsra    v0, v0, 0");
            T(dis, 0x0002103E, "dsrl    v0, v0, 0");
            T(dis, 0x0002107C, "dsll32  v0, v0, 0");
            T(dis, 0x0002107E, "dsrl32  v0, v0, 0");
            T(dis, 0x0002107F, "dsra32  v0, v0, 0");
            T(dis, 0x00221014, "dsllv   v0, v0, at");
            T(dis, 0x00221017, "dsrav   v0, v0, at");
            T(dis, 0x00221016, "dsrlv   v0, v0, at");
            T(dis, 0x0022102C, "dadd    v0, at, v0");
            T(dis, 0x0022102D, "daddu   v0, at, v0");
            T(dis, 0x0022102E, "dsub    v0, at, v0");
            T(dis, 0x0022102F, "dsubu   v0, at, v0");

            // ── I-type ──────────────────────────────────────────────────────
            T(dis, 0x20420001, "addi    v0, v0, 0x1");
            T(dis, 0x24420001, "addiu   v0, v0, 0x1");
            T(dis, 0x2042FFFF, "addi    v0, v0, -0x1");
            T(dis, 0x30420001, "andi    v0, v0, 0x0001");
            T(dis, 0x34420001, "ori     v0, v0, 0x0001");
            T(dis, 0x38420001, "xori    v0, v0, 0x0001");
            T(dis, 0x28420001, "slti    v0, v0, 0x1");
            T(dis, 0x2C420001, "sltiu   v0, v0, 0x1");
            T(dis, 0x3C020001, "lui     v0, 0x0001");

            // ── Loads / Stores ───────────────────────────────────────────────
            T(dis, 0x80430004, "lb      v1, 0x4(v0)");
            T(dis, 0x84430004, "lh      v1, 0x4(v0)");
            T(dis, 0x8C430004, "lw      v1, 0x4(v0)");
            T(dis, 0x90430004, "lbu     v1, 0x4(v0)");
            T(dis, 0x94430004, "lhu     v1, 0x4(v0)");
            T(dis, 0x9C430004, "lwu     v1, 0x4(v0)");
            T(dis, 0xDC430004, "ld      v1, 0x4(v0)");
            T(dis, 0xA0430004, "sb      v1, 0x4(v0)");
            T(dis, 0xA4430004, "sh      v1, 0x4(v0)");
            T(dis, 0xAC430004, "sw      v1, 0x4(v0)");
            T(dis, 0xFC430004, "sd      v1, 0x4(v0)");

            // ── Branches ────────────────────────────────────────────────────
            // BEQ at zero == 0 => offset=0 => target = pc+4+0 = 0x100004
            T(dis, 0x10200000, "beq     at, zero, 0x00100004", 0x00100000);
            T(dis, 0x14200000, "bne     at, zero, 0x00100004", 0x00100000);
            T(dis, 0x18200000, "blez    at, 0x00100004",       0x00100000);
            T(dis, 0x1C200000, "bgtz    at, 0x00100004",       0x00100000);

            // REGIMM
            T(dis, 0x04200000, "bltz    at, 0x00100004", 0x00100000);
            T(dis, 0x04210000, "bgez    at, 0x00100004", 0x00100000);

            // J/JAL
            T(dis, 0x08000400, "j       0x00001000");
            T(dis, 0x0C000400, "jal     0x00001000");

            // ── FPU (COP1) ───────────────────────────────────────────────────
            T(dis, 0x46221000, "add.s   f4, f0, f2");
            T(dis, 0x46221001, "sub.s   f4, f0, f2");
            T(dis, 0x46221002, "mul.s   f4, f0, f2");
            T(dis, 0x46221003, "div.s   f4, f0, f2");
            T(dis, 0x46000005, "abs.s   f0, f0");
            T(dis, 0x46000006, "mov.s   f0, f0");
            T(dis, 0x46000007, "neg.s   f0, f0");
            T(dis, 0x44020000, "mfc1    v0, f0");
            T(dis, 0x44820000, "mtc1    v0, f0");

            // ── COP0 ────────────────────────────────────────────────────────
            T(dis, 0x40024800, "mfc0    v0, Count");
            T(dis, 0x40826800, "mtc0    at, EPC");
            T(dis, 0x42000018, "eret");
            T(dis, 0x42000038, "ei");
            T(dis, 0x42000039, "di");

            // ── MMI ──────────────────────────────────────────────────────────
            T(dis, 0x70620000, "madd    zero, v1, v0");
            T(dis, 0x70220010, "mfhi1   v0");
            T(dis, 0x70620018, "mult1   zero, v1, v0");
            T(dis, 0x70620019, "multu1  v1, v0");

            Console.WriteLine($"\nResults: {_pass} passed, {_fail} failed out of {_pass+_fail} tests.");
        }

        private static void T(MipsDisassembler dis, uint word, string expected, uint pc = 0x00100000)
        {
            string result = dis.DecodeInstruction(word, pc, null).TrimEnd();
            // Normalize whitespace for comparison
            string normResult   = NormWs(result);
            string normExpected = NormWs(expected);

            if (normResult == normExpected)
            {
                _pass++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  PASS  {word:X8}  =>  {result}");
            }
            else
            {
                _fail++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAIL  {word:X8}");
                Console.WriteLine($"        expected: {expected}");
                Console.WriteLine($"        got:      {result}");
            }
            Console.ResetColor();
        }

        private static string NormWs(string s)
        {
            // Collapse all runs of spaces/tabs to a single space
            var sb = new System.Text.StringBuilder();
            bool lastSpace = false;
            foreach (char c in s.Trim())
            {
                if (c == ' ' || c == '\t')
                {
                    if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                }
                else { sb.Append(c); lastSpace = false; }
            }
            return sb.ToString();
        }
    }
}
