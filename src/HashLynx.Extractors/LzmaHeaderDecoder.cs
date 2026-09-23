namespace HashLynx.Extractors;

/// <summary>Only decodes bounded, unencrypted archive metadata; never writes extracted files.</summary>
internal static class LzmaHeaderDecoder
{
    public static byte[] Decode(byte[] packed, byte[] properties, int outputSize, CancellationToken ct)
    {
        ArchiveData.Require(properties.Length == 5 && properties[0] < 225 && properties[0] % 9 + properties[0] / 9 % 5 <= 4 && ArchiveData.U32(properties.AsSpan(1)) <= 8 * 1024 * 1024,
            "The 7-Zip metadata decoder requires LZMA properties with a dictionary at most 8 MiB.");
        ArchiveData.Require(outputSize is > 0 and <= 8 * 1024 * 1024);
        ct.ThrowIfCancellationRequested();
        var output = new byte[outputSize];
        using var input = new CheckedInput(packed, ct);
        using var destination = new MemoryStream(output, 0, output.Length, true, true);
        try
        {
            var decoder = new global::SevenZip.Compression.LZMA.Decoder();
            decoder.SetDecoderProperties(properties);
            decoder.Code(input, destination, packed.Length, outputSize, null!);
            ArchiveData.Require(destination.Position == outputSize, "The compressed 7-Zip metadata ended early.");
        }
        catch (Exception ex) when (ex is global::SevenZip.DataErrorException or global::SevenZip.InvalidParamException or NotSupportedException)
        { throw new InvalidDataException("The compressed 7-Zip metadata is invalid."); }
        ct.ThrowIfCancellationRequested();
        return output;
    }

    private sealed class CheckedInput(byte[] data, CancellationToken ct) : MemoryStream(data, false)
    {
        public override int ReadByte()
        {
            ct.ThrowIfCancellationRequested();
            var value = base.ReadByte();
            if (value < 0) throw new InvalidDataException("The LZMA metadata stream is truncated.");
            return value;
        }
    }
}
