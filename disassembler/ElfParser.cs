using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PS2Disassembler
{
    public class ElfInfo
    {
        public uint EntryPoint  { get; set; }
        public uint LoadAddress { get; set; }
        public List<ElfSection> Sections { get; set; } = new();
        public List<ElfSegment> Segments { get; set; } = new();
        public List<ElfSymbol>  Symbols  { get; set; } = new();
        public bool Is64Bit     { get; set; }
        public bool IsBigEndian { get; set; }
    }

    public class ElfSection
    {
        public string Name    { get; set; }
        public uint   Type    { get; set; }
        public uint   Flags   { get; set; }
        public uint   Address { get; set; }
        public uint   Offset  { get; set; }
        public uint   Size    { get; set; }
    }

    public class ElfSegment
    {
        public uint Type    { get; set; }
        public uint Offset  { get; set; }
        public uint VAddr   { get; set; }
        public uint PAddr   { get; set; }
        public uint FileSz  { get; set; }
        public uint MemSz   { get; set; }
        public uint Flags   { get; set; }
        public uint Align   { get; set; }
    }

    public class ElfSymbol
    {
        public string Name    { get; set; }
        public uint   Value   { get; set; }
        public uint   Size    { get; set; }
        public byte   Info    { get; set; }
        public byte   Other   { get; set; }
        public ushort Shndx   { get; set; }
        public byte   Type    => (byte)(Info & 0x0F);
        public byte   Bind    => (byte)(Info >> 4);
    }

    /// <summary>
    /// Parses PS2 ELF32 executables (little-endian MIPS)
    /// </summary>
    public static class ElfParser
    {
        public static bool IsElf(byte[] data)
        {
            if (data.Length < 4) return false;
            return data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F';
        }

        public static ElfInfo Parse(byte[] data)
        {
            if (!IsElf(data))
                throw new InvalidOperationException("Not an ELF file");

            var info = new ElfInfo();
            bool be = data[5] == 2; // EI_DATA: 1=LE, 2=BE
            info.IsBigEndian = be;
            info.Is64Bit     = data[4] == 2; // EI_CLASS

            if (info.Is64Bit)
                throw new NotSupportedException("64-bit ELF not supported for PS2");

            // ELF32 header
            info.EntryPoint = R32(data, 0x18, be);

            uint phOff   = R32(data, 0x1C, be);
            uint shOff   = R32(data, 0x20, be);
            ushort phNum = R16(data, 0x2C, be);
            ushort shNum = R16(data, 0x30, be);
            ushort shStrIdx = R16(data, 0x32, be);
            ushort phEntSz = R16(data, 0x2A, be);
            ushort shEntSz = R16(data, 0x2E, be);

            // Parse program headers (segments)
            info.LoadAddress = uint.MaxValue;
            for (int i = 0; i < phNum; i++)
            {
                uint off = phOff + (uint)(i * phEntSz);
                if (off + 32 > data.Length) break;

                var seg = new ElfSegment
                {
                    Type   = R32(data, off + 0x00, be),
                    Offset = R32(data, off + 0x04, be),
                    VAddr  = R32(data, off + 0x08, be),
                    PAddr  = R32(data, off + 0x0C, be),
                    FileSz = R32(data, off + 0x10, be),
                    MemSz  = R32(data, off + 0x14, be),
                    Flags  = R32(data, off + 0x18, be),
                    Align  = R32(data, off + 0x1C, be)
                };
                info.Segments.Add(seg);

                // PT_LOAD = 1
                if (seg.Type == 1 && seg.VAddr < info.LoadAddress && seg.FileSz > 0)
                    info.LoadAddress = seg.VAddr;
            }

            if (info.LoadAddress == uint.MaxValue)
                info.LoadAddress = 0x00100000;

            // Parse section headers
            if (shOff == 0 || shNum == 0) return info;

            // Get section name string table (byte offset of the .shstrtab data)
            uint shStrDataOff = 0;
            if (shStrIdx < shNum)
            {
                uint snOff = shOff + (uint)(shStrIdx * shEntSz);
                if (snOff + 24 <= data.Length)
                    shStrDataOff = R32(data, snOff + 0x10, be);
            }

            uint symTabOff = 0, symTabSize = 0;
            uint strTabOff = 0, strTabSize = 0;
            uint symTabLink = 0;

            for (int i = 0; i < shNum; i++)
            {
                uint off = shOff + (uint)(i * shEntSz);
                if (off + 40 > data.Length) break;

                uint nameIdx = R32(data, off + 0x00, be);
                uint type    = R32(data, off + 0x04, be);
                uint flags   = R32(data, off + 0x08, be);
                uint addr    = R32(data, off + 0x0C, be);
                uint dataOff = R32(data, off + 0x10, be);
                uint size    = R32(data, off + 0x14, be);
                uint link    = R32(data, off + 0x18, be);

                // nameIdx is a byte offset into .shstrtab, not an array index
                string secName = shStrDataOff > 0 ? ReadElfString(data, shStrDataOff, nameIdx) : $"sec_{i}";

                var section = new ElfSection
                {
                    Name    = secName,
                    Type    = type,
                    Flags   = flags,
                    Address = addr,
                    Offset  = dataOff,
                    Size    = size
                };
                info.Sections.Add(section);

                // SHT_SYMTAB = 2
                if (type == 2) { symTabOff = dataOff; symTabSize = size; symTabLink = link; }
            }

            // Resolve the string table linked to the symbol table via sh_link
            if (symTabOff > 0 && symTabLink < shNum)
            {
                uint strSecOff = shOff + symTabLink * shEntSz;
                if (strSecOff + 24 <= data.Length)
                {
                    strTabOff  = R32(data, strSecOff + 0x10, be);
                    strTabSize = R32(data, strSecOff + 0x14, be);
                }
            }

            // Parse symbols — nameIdx is a byte offset into strTab
            if (symTabOff > 0 && strTabOff > 0)
            {
                for (uint off = symTabOff; off + 16 <= symTabOff + symTabSize; off += 16)
                {
                    if (off + 16 > data.Length) break;
                    uint nameIdx = R32(data, off + 0x00, be);
                    uint value   = R32(data, off + 0x04, be);
                    uint size    = R32(data, off + 0x08, be);
                    byte symInfo = data[off + 0x0C];
                    byte other   = data[off + 0x0D];
                    ushort shndx = R16(data, off + 0x0E, be);

                    string name = ReadElfString(data, strTabOff, nameIdx);
                    info.Symbols.Add(new ElfSymbol { Name = name, Value = value, Size = size, Info = symInfo, Other = other, Shndx = shndx });
                }
            }

            return info;
        }

        // Reads a null-terminated ASCII string from data at (tableOffset + nameOffset).
        // ELF string tables use byte offsets, not array indices.
        private static string ReadElfString(byte[] data, uint tableOffset, uint nameOffset)
        {
            uint pos = tableOffset + nameOffset;
            if (pos >= (uint)data.Length) return "";
            int start = (int)pos;
            int end   = start;
            while (end < data.Length && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, start, end - start);
        }

        public static bool TryBuildLoadImage(byte[] data, ElfInfo info, out byte[] image, out uint baseAddress)
        {
            image = Array.Empty<byte>();
            baseAddress = 0;

            var loadSegments = new List<ElfSegment>();
            ulong minVAddr = ulong.MaxValue;
            ulong maxVAddr = 0;

            foreach (var seg in info.Segments)
            {
                if (seg.Type != 1 || seg.MemSz == 0) // PT_LOAD
                    continue;

                loadSegments.Add(seg);
                ulong start = seg.VAddr;
                ulong end = start + Math.Max((ulong)seg.MemSz, (ulong)seg.FileSz);
                if (start < minVAddr) minVAddr = start;
                if (end > maxVAddr) maxVAddr = end;
            }

            if (loadSegments.Count == 0 || minVAddr == ulong.MaxValue || maxVAddr <= minVAddr)
                return false;

            ulong imageSize = maxVAddr - minVAddr;
            if (imageSize > int.MaxValue)
                throw new IOException("ELF load image is too large to map into memory.");

            image = new byte[(int)imageSize];
            baseAddress = (uint)minVAddr;

            foreach (var seg in loadSegments)
            {
                if (seg.Offset >= data.Length)
                    continue;

                ulong destOffset = (ulong)seg.VAddr - minVAddr;
                if (destOffset >= (ulong)image.Length)
                    continue;

                ulong available = (ulong)data.Length - seg.Offset;
                ulong copyLength = Math.Min((ulong)seg.FileSz, available);
                if (destOffset + copyLength > (ulong)image.Length)
                    copyLength = (ulong)image.Length - destOffset;

                if (copyLength == 0)
                    continue;

                Buffer.BlockCopy(data, (int)seg.Offset, image, (int)destOffset, (int)copyLength);
            }

            return true;
        }

        private static uint   R32(byte[] d, uint o, bool be) => be
            ? (uint)((d[o] << 24) | (d[o+1] << 16) | (d[o+2] << 8) | d[o+3])
            : (uint)(d[o] | (d[o+1] << 8) | (d[o+2] << 16) | (d[o+3] << 24));

        private static ushort R16(byte[] d, uint o, bool be) => be
            ? (ushort)((d[o] << 8) | d[o+1])
            : (ushort)(d[o] | (d[o+1] << 8));
    }
}
