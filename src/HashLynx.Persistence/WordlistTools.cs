using System.Runtime.CompilerServices;

namespace HashLynx.Persistence;

public sealed record WordlistStatistics(long FileSizeBytes, long LineCount, DateTime LastWriteUtc);
public sealed record WordlistTransformOptions(IReadOnlyList<string> InputPaths, string OutputPath,
    bool RemoveDuplicates = true, int MinimumLength = 0, int MaximumLength = 256);
public sealed record WordlistTransformProgress(long LinesRead, long LinesWritten, string Stage);
public sealed record WordlistTransformResult(string OutputPath, long LinesRead, long LinesWritten);

/// <summary>Byte-preserving wordlist operations. Sorting spills to local disk and merges with bounded fan-in.</summary>
public sealed class WordlistTools
{
    private const int MaximumLineBytes = 1024 * 1024;
    private const int MergeFanIn = 24;
    private readonly string temporaryDirectory;
    private readonly int sortMemoryBytes;
    private static readonly byte[] NewLine = [(byte)'\n'];

    public WordlistTools(AppPaths paths, int sortMemoryBytes = 16 * 1024 * 1024)
    {
        if (sortMemoryBytes < 1024) throw new ArgumentOutOfRangeException(nameof(sortMemoryBytes));
        temporaryDirectory = Path.Combine(paths.CacheDirectory, "wordlist-tools");
        this.sortMemoryBytes = sortMemoryBytes;
    }

    /// <summary>Counts physical LF-delimited lines without decoding or loading candidates into memory.</summary>
    public static Task<WordlistStatistics> InspectAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        var before = Snapshot(path);
        await using var stream = OpenRead(path);
        var buffer = new byte[64 * 1024];
        long lines = 0, bytes = 0;
        byte last = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++) if (buffer[i] == '\n') lines++;
            bytes += read;
            last = buffer[read - 1];
        }
        if (bytes > 0 && last != '\n') lines++;
        EnsureUnchanged(path, before);
        return new WordlistStatistics(bytes, lines, before.LastWriteUtc);
    }, ct);

    public Task<WordlistTransformResult> TransformAsync(WordlistTransformOptions options,
        IProgress<WordlistTransformProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => TransformCoreAsync(options, progress, ct), ct);

    private async Task<WordlistTransformResult> TransformCoreAsync(WordlistTransformOptions options,
        IProgress<WordlistTransformProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.InputPaths is null || options.InputPaths.Count == 0) throw new InvalidOperationException("Select at least one input wordlist.");
        if (options.MinimumLength < 0 || options.MaximumLength < options.MinimumLength || options.MaximumLength > MaximumLineBytes)
            throw new InvalidOperationException("Choose a valid minimum and maximum byte length (up to 1,048,576).");
        var inputs = options.InputPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var output = Path.GetFullPath(options.OutputPath);
        if (inputs.Contains(output, StringComparer.OrdinalIgnoreCase) || File.Exists(output) || Directory.Exists(output))
            throw new IOException("Choose a new output file. Existing files and input wordlists cannot be overwritten.");
        if (!Directory.Exists(Path.GetDirectoryName(output))) throw new IOException("Choose an existing output directory.");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(temporaryDirectory);
        var work = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        // The final staging file is on the destination volume so publishing is an atomic, non-overwriting rename.
        var staging = Path.Combine(Path.GetDirectoryName(output)!, ".hashlynx-wordlist-" + Guid.NewGuid().ToString("N") + ".tmp");
        long readCount = 0, written = 0;
        try
        {
            var runs = new List<string>();
            var chunk = new List<byte[]>();
            long chunkBytes = 0;
            await using (var destination = OpenNew(staging))
            {
                foreach (var input in inputs)
                {
                    var before = Snapshot(input);
                    await foreach (var line in ReadLinesAsync(input, ct).ConfigureAwait(false))
                    {
                        readCount++;
                        if (line.Length >= options.MinimumLength && line.Length <= options.MaximumLength)
                        {
                            if (options.RemoveDuplicates)
                            {
                                chunk.Add(line);
                                chunkBytes += line.Length + 48L;
                                if (chunkBytes >= sortMemoryBytes)
                                {
                                    runs.Add(await FlushRunAsync(chunk, work, ct).ConfigureAwait(false));
                                    chunk.Clear(); chunkBytes = 0;
                                }
                            }
                            else { await WriteLineAsync(destination, line, ct).ConfigureAwait(false); written++; }
                        }
                        if (readCount % 100000 == 0) progress?.Report(new(readCount, written, "Reading input lists"));
                    }
                    EnsureUnchanged(input, before);
                }
                if (options.RemoveDuplicates)
                {
                    if (chunk.Count > 0) runs.Add(await FlushRunAsync(chunk, work, ct).ConfigureAwait(false));
                    chunk.Clear();
                    progress?.Report(new(readCount, 0, "Merging sorted lists"));
                    while (runs.Count > MergeFanIn)
                    {
                        var merged = new List<string>();
                        foreach (var group in runs.Chunk(MergeFanIn))
                        {
                            var path = Path.Combine(work, Guid.NewGuid().ToString("N") + ".run");
                            await using (var stream = OpenNew(path)) await MergeAsync(group, stream, ct).ConfigureAwait(false);
                            merged.Add(path);
                            foreach (var run in group) File.Delete(run);
                        }
                        runs = merged;
                    }
                    written = await MergeAsync(runs, destination, ct).ConfigureAwait(false);
                }
                await destination.FlushAsync(ct).ConfigureAwait(false);
                destination.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(staging, output, overwrite: false);
            progress?.Report(new(readCount, written, "Complete"));
            return new(output, readCount, written);
        }
        finally
        {
            // Both paths are generated by this operation; never clean up input or destination files.
            if (File.Exists(staging)) File.Delete(staging);
            foreach (var file in Directory.EnumerateFiles(work)) File.Delete(file);
            Directory.Delete(work);
        }
    }

    private static async Task<string> FlushRunAsync(List<byte[]> lines, string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lines.Sort(ByteComparer.Instance);
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".run");
        await using var stream = OpenNew(path);
        byte[]? previous = null;
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (previous is null || !line.AsSpan().SequenceEqual(previous)) await WriteLineAsync(stream, line, ct).ConfigureAwait(false);
            previous = line;
        }
        return path;
    }

    private static async Task<long> MergeAsync(IEnumerable<string> paths, Stream destination, CancellationToken ct)
    {
        var readers = new List<IAsyncEnumerator<byte[]>>();
        var queue = new PriorityQueue<(byte[] Line, int Reader), byte[]>(ByteComparer.Instance);
        long written = 0;
        try
        {
            foreach (var path in paths)
            {
                var reader = ReadLinesAsync(path, ct, stripBom: false).GetAsyncEnumerator(ct);
                readers.Add(reader);
                if (await reader.MoveNextAsync().ConfigureAwait(false)) queue.Enqueue((reader.Current, readers.Count - 1), reader.Current);
            }
            byte[]? previous = null;
            while (queue.TryDequeue(out var item, out _))
            {
                ct.ThrowIfCancellationRequested();
                if (previous is null || !item.Line.AsSpan().SequenceEqual(previous))
                {
                    await WriteLineAsync(destination, item.Line, ct).ConfigureAwait(false); written++;
                    previous = item.Line;
                }
                var reader = readers[item.Reader];
                if (await reader.MoveNextAsync().ConfigureAwait(false)) queue.Enqueue((reader.Current, item.Reader), reader.Current);
            }
            return written;
        }
        finally { foreach (var reader in readers) await reader.DisposeAsync().ConfigureAwait(false); }
    }

    private static async IAsyncEnumerable<byte[]> ReadLinesAsync(string path, [EnumeratorCancellation] CancellationToken ct, bool stripBom = true)
    {
        await using var stream = OpenRead(path);
        var buffer = new byte[64 * 1024];
        using var pending = new MemoryStream();
        var firstLine = true;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                AppendBounded(pending, buffer.AsSpan(start, i - start));
                var line = FinishLine(pending, stripBom && firstLine, stripBom);
                firstLine = false;
                yield return line;
                pending.SetLength(0);
                start = i + 1;
            }
            AppendBounded(pending, buffer.AsSpan(start, read - start));
        }
        if (pending.Length > 0) yield return FinishLine(pending, stripBom && firstLine, stripBom);
    }

    private static void AppendBounded(MemoryStream stream, ReadOnlySpan<byte> bytes)
    {
        if (stream.Length + bytes.Length > MaximumLineBytes + 4L)
            throw new InvalidDataException("A wordlist line exceeds 1 MiB. Choose a text wordlist with one candidate per line.");
        stream.Write(bytes);
    }
    private static byte[] FinishLine(MemoryStream stream, bool first, bool stripCr)
    {
        var bytes = stream.GetBuffer().AsSpan(0, (int)stream.Length);
        if (stripCr && bytes.Length > 0 && bytes[^1] == '\r') bytes = bytes[..^1];
        if (first)
        {
            if (bytes.StartsWith(new byte[] { 0xff, 0xfe }) || bytes.StartsWith(new byte[] { 0xfe, 0xff }) || bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
                throw new InvalidDataException("UTF-16/UTF-32 wordlists must be converted to UTF-8 before using wordlist tools.");
            if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        }
        if (bytes.Length > MaximumLineBytes) throw new InvalidDataException("A wordlist line exceeds 1 MiB.");
        return bytes.ToArray();
    }
    private static async ValueTask WriteLineAsync(Stream stream, byte[] line, CancellationToken ct)
    {
        await stream.WriteAsync(line, ct).ConfigureAwait(false);
        await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
    }
    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static FileStream OpenNew(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static (long Length, DateTime LastWriteUtc) Snapshot(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }
    private static void EnsureUnchanged(string path, (long Length, DateTime LastWriteUtc) before)
    {
        if (Snapshot(path) != before) throw new IOException("An input wordlist changed during reading. Retry when it is no longer being edited.");
    }
    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
