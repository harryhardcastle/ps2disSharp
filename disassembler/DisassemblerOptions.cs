namespace PS2Disassembler
{
    public class DisassemblerOptions
    {
        public uint BaseAddress  { get; set; } = 0x00100000;
        public bool ShowAddress  { get; set; } = true;
        public bool ShowHex      { get; set; } = true;
        public bool UseAbiNames  { get; set; } = true;
        public ElfInfo? ElfInfo  { get; set; }
    }
}
