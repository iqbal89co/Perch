namespace Perch.Pages.AiUsage;

/// <summary>Turns one line (without its newline) into an item, or null to skip it.</summary>
public delegate T? LineParser<T>(ReadOnlySpan<byte> line) where T : class;

/// <summary>
/// Incremental reader for an append-only JSON Lines file. It remembers how far it has consumed, always at a
/// line boundary, and on the next refresh reads only the bytes appended since. A partial last line is left
/// for later. A file that became shorter is read again from the start.
/// The file is opened with FileShare.ReadWrite | Delete because the AI tools keep writing to it.
/// </summary>
public sealed class JsonlFileIndex<T> where T : class
{
    private const int ChunkSize = 256 * 1024;
    private readonly List<T> _items = new();

    public IReadOnlyList<T> Items => _items;

    /// <summary>Bytes consumed so far (the start of the first unread line).</summary>
    public long Offset { get; private set; }

    /// <summary>Reads what was appended since the last refresh. Returns the number of bytes read.</summary>
    public long Refresh(string path, LineParser<T> parse)
    {
        var length = new FileInfo(path).Length;
        if (length < Offset)
        {
            _items.Clear();
            Offset = 0;
        }
        if (length == Offset) return 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        stream.Seek(Offset, SeekOrigin.Begin);

        // The chunk is rented: a fresh 256 KB array per file per refresh landed on the large object heap and
        // forced a full collection every few files.
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            return ReadFrom(stream, chunk, parse);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private long ReadFrom(Stream stream, byte[] chunk, LineParser<T> parse)
    {
        var pending = new byte[4096];
        var pendingLength = 0;
        long consumed = Offset;
        long read = 0;

        int n;
        while ((n = stream.Read(chunk, 0, ChunkSize)) > 0)
        {
            read += n;
            var data = chunk.AsSpan(0, n);
            while (true)
            {
                var newline = data.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    Append(ref pending, ref pendingLength, data);
                    break;
                }

                var lineBytes = data[..newline];
                consumed += pendingLength + newline + 1;
                if (pendingLength == 0)
                {
                    Process(lineBytes, parse);
                }
                else
                {
                    Append(ref pending, ref pendingLength, lineBytes);
                    Process(pending.AsSpan(0, pendingLength), parse);
                    pendingLength = 0;
                }
                data = data[(newline + 1)..];
            }
        }

        Offset = consumed;
        return read;
    }

    /// <summary>Forgets everything; the next refresh reads the whole file.</summary>
    public void Reset()
    {
        _items.Clear();
        Offset = 0;
    }

    private void Process(ReadOnlySpan<byte> line, LineParser<T> parse)
    {
        if (line.IsEmpty) return;
        var item = parse(line);
        if (item != null) _items.Add(item);
    }

    private static void Append(ref byte[] buffer, ref int length, ReadOnlySpan<byte> source)
    {
        if (length + source.Length > buffer.Length)
        {
            var bigger = new byte[Math.Max(buffer.Length * 2, length + source.Length)];
            Buffer.BlockCopy(buffer, 0, bigger, 0, length);
            buffer = bigger;
        }
        source.CopyTo(buffer.AsSpan(length));
        length += source.Length;
    }
}
