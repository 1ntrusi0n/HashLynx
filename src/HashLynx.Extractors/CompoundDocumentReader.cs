using System.Text;
using static HashLynx.Extractors.ArchiveData;

namespace HashLynx.Extractors;

/// <summary>Read-only MS-CFB metadata reader. Never opens embedded objects or follows external links.</summary>
internal sealed class CompoundDocumentReader(FileStream stream, CancellationToken ct)
{
    private const uint End = 0xfffffffe, Free = 0xffffffff;
    private int sectorSize, budget = 16 * 1024 * 1024;
    private uint sectorCount;
    private uint[] fat = [];
    private byte[] directory = [];
    private readonly HashSet<uint> structuralSectors = [];

    public async Task<byte[]> ReadEncryptionInfoAsync()
    {
        Require(stream.Length is >= 512 and <= 512L * 1024 * 1024, "Office extraction supports compound documents up to 512 MiB.");
        var h = await ReadAsync(stream, 0, 512, ct);
        Require(h.AsSpan(0, 8).SequenceEqual(Convert.FromHexString("d0cf11e0a1b11ae1")), "This is not an encrypted Office compound document.");
        var major = U16(h.AsSpan(26)); var shift = U16(h.AsSpan(30));
        Require((major == 3 && shift == 9 || major == 4 && shift == 12) && U16(h.AsSpan(28)) == 0xfffe
            && U16(h.AsSpan(32)) == 6 && U32(h.AsSpan(56)) == 4096, "The Office compound container has an unsupported or invalid header.");
        sectorSize = 1 << shift;
        Require(stream.Length % sectorSize == 0);
        sectorCount = checked((uint)(stream.Length / sectorSize - 1));
        var fatCount = U32(h.AsSpan(44)); var difatCount = U32(h.AsSpan(72));
        Require(fatCount > 0 && (ulong)fatCount * (uint)sectorSize <= 4 * 1024 * 1024 && difatCount <= 1024,
            "The Office allocation metadata exceeds the built-in limits.");
        var fatSectors = new List<uint>();
        void Add(uint sid)
        {
            if (sid == Free) return;
            Require(fatSectors.Count < fatCount && sid < sectorCount && structuralSectors.Add(sid)); fatSectors.Add(sid);
        }
        for (var i = 0; i < 109; i++) Add(U32(h.AsSpan(76 + i * 4)));
        var next = U32(h.AsSpan(68));
        for (var i = 0u; i < difatCount; i++)
        {
            Require(next < sectorCount && structuralSectors.Add(next), "The Office DIFAT chain is cyclic or invalid.");
            var d = await Sector(next);
            for (var j = 0; j < sectorSize - 4; j += 4) Add(U32(d.AsSpan(j)));
            next = U32(d.AsSpan(sectorSize - 4));
        }
        Require((difatCount == 0 && next is End or Free || difatCount > 0 && next == End) && fatSectors.Count == fatCount);
        fat = new uint[checked((int)fatCount * sectorSize / 4)];
        for (var i = 0; i < fatSectors.Count; i++)
        {
            var sector = await Sector(fatSectors[i]);
            for (var j = 0; j < sectorSize; j += 4) fat[i * sectorSize / 4 + j / 4] = U32(sector.AsSpan(j));
        }
        Require(fat.Length >= sectorCount);
        var fatSet = fatSectors.ToHashSet();
        foreach (var sid in structuralSectors)
            Require(fat[sid] == (fatSet.Contains(sid) ? 0xfffffffd : 0xfffffffc), "The Office structural sector markers are invalid.");
        directory = await Chain(U32(h.AsSpan(48)), null, 2 * 1024 * 1024);
        Require(major == 3 ? U32(h.AsSpan(40)) == 0 : U32(h.AsSpan(40)) * (ulong)sectorSize == (ulong)directory.Length);
        Require(directory.Length >= 128 && directory[66] == 5, "The Office root directory is missing.");
        var entries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<uint>(); var visited = new HashSet<uint>(); pending.Push(U32(directory.AsSpan(76)));
        while (pending.TryPop(out var index))
        {
            ct.ThrowIfCancellationRequested();
            if (index == Free) continue;
            Require(index < directory.Length / 128 && index != 0 && visited.Add(index), "The Office directory links are cyclic or invalid.");
            var offset = checked((int)index * 128); var length = U16(directory.AsSpan(offset + 64));
            Require(length is >= 2 and <= 64 && length % 2 == 0 && directory[offset + length - 2] == 0 && directory[offset + length - 1] == 0);
            var name = Encoding.Unicode.GetString(directory, offset, length - 2);
            Require(entries.TryAdd(name, offset), "The Office directory contains duplicate stream names.");
            pending.Push(U32(directory.AsSpan(offset + 68))); pending.Push(U32(directory.AsSpan(offset + 72)));
        }
        Require(entries.TryGetValue("EncryptionInfo", out var info) && entries.ContainsKey("EncryptedPackage"),
            "No encrypted Office package was found. Legacy DOC/XLS/PPT encryption and document editing restrictions are not supported.");
        Require(directory[info + 66] == 2 && directory[entries["EncryptedPackage"] + 66] == 2);
        var size = Size(info, major); var start = U32(directory.AsSpan(info + 116));
        Require(size is >= 8 and <= 1024 * 1024, "The Office encryption metadata is missing or too large.");
        if (size >= 4096) return await Chain(start, checked((int)size), 1024 * 1024);

        var miniCount = U32(h.AsSpan(64));
        Require(miniCount > 0 && (ulong)miniCount * (uint)sectorSize <= 2 * 1024 * 1024);
        var miniFat = await Chain(U32(h.AsSpan(60)), checked((int)miniCount * sectorSize), 2 * 1024 * 1024);
        var rootSize = Size(0, major); Require(rootSize is > 0 and <= 8 * 1024 * 1024);
        var miniStream = await Chain(U32(directory.AsSpan(116)), (int)rootSize, 8 * 1024 * 1024);
        var output = new byte[(int)size]; var seen = new HashSet<uint>(); var pos = 0;
        while (pos < output.Length)
        {
            ct.ThrowIfCancellationRequested();
            Require(start < miniFat.Length / 4 && (ulong)start * 64 + 64 <= (ulong)miniStream.Length && seen.Add(start),
                "The Office mini-stream chain is cyclic or invalid.");
            var take = Math.Min(64, output.Length - pos);
            miniStream.AsSpan((int)start * 64, take).CopyTo(output.AsSpan(pos)); pos += take;
            start = U32(miniFat.AsSpan((int)start * 4));
        }
        Require(start == End, "The Office mini-stream size disagrees with its allocation chain.");
        return output;
    }

    private ulong Size(int entry, int major) => major == 3 ? U32(directory.AsSpan(entry + 120)) : U64(directory.AsSpan(entry + 120));
    private async Task<byte[]> Sector(uint sid)
    {
        Require(sid < sectorCount && (budget -= sectorSize) >= 0, "The Office metadata exceeds the bounded read limit.");
        return await ReadAsync(stream, ((ulong)sid + 1) * (uint)sectorSize, sectorSize, ct);
    }
    private async Task<byte[]> Chain(uint sid, int? length, int maximum)
    {
        using var output = new MemoryStream(); var seen = new HashSet<uint>();
        while (sid != End)
        {
            Require(sid < fat.Length && structuralSectors.Add(sid) && seen.Add(sid), "The Office allocation chain is cyclic, overlapping or invalid.");
            Require(output.Length < (length ?? maximum), "The Office allocation chain exceeds its declared size or limit.");
            var sector = await Sector(sid); var take = length is { } n ? Math.Min(sectorSize, n - (int)output.Length) : sectorSize;
            output.Write(sector, 0, take); sid = fat[sid];
        }
        Require(length is null || output.Length == length, "The Office allocation chain is truncated.");
        return output.ToArray();
    }
}
