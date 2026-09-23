using System.Buffers.Binary;

namespace HashLynx.Extractors.Tests;

// Synthetic partition metadata around Hashcat's public mode-22100 known-answer
// vector (password: hashcat). No user volume, filesystem or plaintext is included.
// https://github.com/hashcat/hashcat/blob/master/src/modules/module_22100.c
internal static class BitLockerFixture
{
    public const string Hash = "$bitlocker$1$16$6f972989ddc209f1eccf07313a7266a2$1048576$12$3a33a8eaff5e6f81d907b591$60$316b0f6d4cb445fb056f0e3e0633c413526ff4481bbf588917b70a4e8f8075f5ceb45958a800b42cb7ff9b7f5e17c6145bf8561ea86f52d3592059fb";
    public static byte[] Salt => Convert.FromHexString(Hash.Split('$')[4]);
    public static byte[] Encrypted => Convert.FromHexString(Hash.Split('$')[7] + Hash.Split('$')[9]);
    public static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    public static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    public static void U64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
    public static byte[] Entry(ushort kind, ushort value, byte[] data, ushort version = 1)
    {
        var bytes = new byte[8 + data.Length]; U16(bytes, 0, checked((ushort)bytes.Length));
        U16(bytes, 2, kind); U16(bytes, 4, value); U16(bytes, 6, version); data.CopyTo(bytes, 8); return bytes;
    }
    public static byte[] Protector(ushort protection = 0x2000, bool reverse = false, byte[]? salt = null, byte[]? encrypted = null)
    {
        var body = new byte[28]; U16(body, 26, protection);
        var stretch = new byte[20]; U32(stretch, 0, 0x1000); (salt ?? Salt).CopyTo(stretch, 4);
        // Include a separate nested stretch key; the VMK ciphertext is its sibling.
        var nested = Entry(0, 5, new byte[72]);
        var s = Entry(0, 3, [.. stretch, .. nested]); var e = Entry(0, 5, encrypted ?? Encrypted);
        return Entry(2, 8, reverse ? [.. body, .. e, .. s] : [.. body, .. s, .. e]);
    }
    public static byte[] Image(bool toGo = false, bool usedSpace = false, byte[]? entries = null)
    {
        var data = new byte[16384];
        (toGo ? "MSWIN4.1"u8 : "-FVE-FS-"u8).CopyTo(data.AsSpan(3)); U16(data, 11, 512); U16(data, 510, 0xaa55);
        var guidOffset = toGo ? 0x1a8 : 0xa0;
        new Guid(usedSpace ? "92a84d3b-dd80-4d0e-9e4e-b1e3284eaed8" : "4967d63b-2e29-4ad8-8399-f6a339e3d001").ToByteArray().CopyTo(data, guidOffset);
        entries ??= Protector();
        for (var copy = 0; copy < 3; copy++)
        {
            var offset = 4096 * (copy + 1); U64(data, guidOffset + 16 + copy * 8, (ulong)offset);
            "-FVE-FS-"u8.CopyTo(data.AsSpan(offset)); U16(data, offset + 8, (ushort)((112 + entries.Length + 15) / 16)); U16(data, offset + 10, 2);
            U32(data, offset + 64, (uint)(48 + entries.Length)); U32(data, offset + 68, 1); U32(data, offset + 72, 48); U32(data, offset + 76, (uint)(48 + entries.Length));
            entries.CopyTo(data, offset + 112);
        }
        return data;
    }
}
