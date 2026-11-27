using System.IO.Compression;
using System.Text;

namespace GameSubTR.Core.Insomniac;

// Insomniac Games PC port'ları (Marvel's Spider-Man Remastered vb.) için dosya formatları.
// Format bilgisi topluluk araştırmalarına (Overstrike, Tkachov) dayanır; bu dosya bağımsız bir yeniden yazımdır.

/// <summary>LZ4 blok (çerçevesiz) çözücü.</summary>
public static class Lz4
{
    public static byte[] DecodeBlock(ReadOnlySpan<byte> src, int outputSize)
    {
        var dst = new byte[outputSize];
        int s = 0, d = 0;
        while (s < src.Length)
        {
            int token = src[s++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { b = src[s++]; lit += b; } while (b == 255); }
            src.Slice(s, lit).CopyTo(dst.AsSpan(d));
            s += lit; d += lit;
            if (s >= src.Length) break; // son dizi yalnızca literal
            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            int match = token & 15;
            if (match == 15) { int b; do { b = src[s++]; match += b; } while (b == 255); }
            match += 4;
            int from = d - offset;
            if (offset <= 0 || from < 0) throw new InvalidDataException("LZ4: geçersiz offset");
            for (int i = 0; i < match; i++) dst[d++] = dst[from + i]; // örtüşen kopya
        }
        if (d != outputSize) throw new InvalidDataException($"LZ4: beklenen {outputSize}, çıkan {d}");
        return dst;
    }
}

/// <summary>DSAR sıkıştırılmış arşiv (veya düz arşiv) içinden varlık okur.</summary>
public static class Dsar
{
    public const uint Magic = 0x52415344; // "DSAR"

    private readonly record struct Block(long RealOffset, long CompOffset, int RealSize, int CompSize, byte Type);

    public static byte[] ExtractAsset(Stream archive, long offset, int size)
    {
        var result = new byte[size];
        var magic = new byte[4];
        archive.Seek(0, SeekOrigin.Begin);
        if (archive.Length < 32 || archive.Read(magic, 0, 4) != 4 || BitConverter.ToUInt32(magic, 0) != Magic)
        {
            archive.Seek(offset, SeekOrigin.Begin);
            ReadExactly(archive, result);
            return result;
        }

        var head = new byte[32];
        archive.Seek(0, SeekOrigin.Begin);
        ReadExactly(archive, head);
        int headerEnd = (int)BitConverter.ToUInt32(head, 12);
        var table = new byte[headerEnd - 32];
        ReadExactly(archive, table);
        var blocks = new List<Block>(table.Length / 32);
        for (int o = 0; o + 32 <= table.Length; o += 32)
            blocks.Add(new Block((long)BitConverter.ToUInt64(table, o), (long)BitConverter.ToUInt64(table, o + 8),
                (int)BitConverter.ToUInt32(table, o + 16), (int)BitConverter.ToUInt32(table, o + 20), table[o + 24]));

        long end = offset + size;
        int written = 0;
        foreach (var b in blocks)
        {
            long bEnd = b.RealOffset + b.RealSize;
            if (bEnd <= offset || b.RealOffset >= end) continue;
            var comp = new byte[b.CompSize];
            archive.Seek(b.CompOffset, SeekOrigin.Begin);
            ReadExactly(archive, comp);
            byte[] plain = b.Type switch
            {
                3 => Lz4.DecodeBlock(comp, b.RealSize),
                0 when b.CompSize == b.RealSize => comp,
                _ => throw new NotSupportedException($"DSAR: desteklenmeyen sıkıştırma türü {b.Type}")
            };
            long from = Math.Max(offset, b.RealOffset), to = Math.Min(end, bEnd);
            plain.AsSpan((int)(from - b.RealOffset), (int)(to - from)).CopyTo(result.AsSpan((int)(from - offset)));
            written += (int)(to - from);
        }
        if (written != size) throw new InvalidDataException("DSAR: varlık tam okunamadı");
        return result;
    }

    private static void ReadExactly(Stream s, byte[] buf)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf, n, buf.Length - n);
            if (r <= 0) throw new EndOfStreamException();
            n += r;
        }
    }
}

/// <summary>
/// DAT1 kapsayıcı: başlık + bölüm tablosu + (bilinmeyenler, dizeler) + 16 bayt hizalı bölümler.
/// Bölümler ham bayt olarak tutulur; değiştirilmeyen her şey birebir korunur.
/// </summary>
public sealed class Dat1
{
    public const uint Magic = 0x44415431; // "1TAD"

    public uint TypeMagic { get; private set; }
    private ushort _unknownsCount;
    private byte[] _preamble = Array.Empty<byte>(); // bilinmeyenler + dize bloğu (tablo ile ilk bölüm arası)
    private readonly List<uint> _tags = new();       // tablodaki sıra
    private readonly List<uint> _byOffset = new();   // dosyadaki sıra
    public Dictionary<uint, byte[]> Sections { get; } = new();

    public static Dat1 Parse(ReadOnlySpan<byte> b)
    {
        if (BitConverter.ToUInt32(b) != Magic) throw new InvalidDataException("DAT1 değil");
        var d = new Dat1 { TypeMagic = BitConverter.ToUInt32(b[4..]) };
        int count = BitConverter.ToUInt16(b[12..]);
        d._unknownsCount = BitConverter.ToUInt16(b[14..]);
        var offsets = new Dictionary<uint, (int Off, int Size)>();
        int min = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int p = 16 + 12 * i;
            uint tag = BitConverter.ToUInt32(b[p..]);
            int off = (int)BitConverter.ToUInt32(b[(p + 4)..]);
            int size = (int)BitConverter.ToUInt32(b[(p + 8)..]);
            d._tags.Add(tag);
            offsets[tag] = (off, size);
            min = Math.Min(min, off);
        }
        int tableEnd = 16 + 12 * count;
        d._preamble = b[tableEnd..min].ToArray();
        d._byOffset.AddRange(d._tags.OrderBy(t => offsets[t].Off));
        foreach (var t in d._tags) d.Sections[t] = b.Slice(offsets[t].Off, offsets[t].Size).ToArray();
        return d;
    }

    public byte[] Save()
    {
        int count = _tags.Count;
        long pos = 16 + 12 * count + _preamble.Length;
        var offsets = new Dictionary<uint, long>();
        foreach (var t in _byOffset)
        {
            pos = Align(pos);
            offsets[t] = pos;
            pos += Sections[t].Length;
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic); w.Write(TypeMagic); w.Write((uint)pos);
        w.Write((ushort)count); w.Write(_unknownsCount);
        foreach (var t in _tags) { w.Write(t); w.Write((uint)offsets[t]); w.Write((uint)Sections[t].Length); }
        w.Write(_preamble);
        foreach (var t in _byOffset)
        {
            while (ms.Position < offsets[t]) w.Write((byte)0);
            w.Write(Sections[t]);
        }
        return ms.ToArray();
    }

    private static long Align(long p) => (p + 15) & ~15L;
}

/// <summary>Arşiv içindekiler dosyası (Spider-Man Remastered / Miles Morales biçimi, "I20").</summary>
public sealed class Toc
{
    public const uint FileMagic = 0x77AF12AF;
    public const uint TagArchives = 0x398ABFF0, TagAssetIds = 0x506D7B8A, TagSizes = 0x65BCF461, TagOffsets = 0xDCD720B5, TagSpans = 0xEDE8ADA9;

    public Dat1 Dat1 { get; }
    private Toc(Dat1 d) => Dat1 = d;

    public static bool IsToc(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[4];
            return f.Read(b, 0, 4) == 4 && BitConverter.ToUInt32(b) == FileMagic;
        }
        catch { return false; }
    }

    public static Toc Load(string path)
    {
        var all = File.ReadAllBytes(path);
        if (BitConverter.ToUInt32(all, 0) != FileMagic) throw new InvalidDataException("toc dosyası değil");
        int len = (int)BitConverter.ToUInt32(all, 4);
        var raw = new byte[len];
        using (var z = new ZLibStream(new MemoryStream(all, 8, all.Length - 8), CompressionMode.Decompress))
        {
            int n = 0, r;
            while (n < len && (r = z.Read(raw, n, len - n)) > 0) n += r;
            if (n != len) throw new InvalidDataException("toc açılamadı");
        }
        return new Toc(Dat1.Parse(raw));
    }

    public void Save(string path)
    {
        var raw = Dat1.Save();
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(FileMagic));
        ms.Write(BitConverter.GetBytes((uint)raw.Length));
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var tmp = path + ".gstr.tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        File.Move(tmp, path, overwrite: true);
    }

    public int AssetCount => Dat1.Sections[TagAssetIds].Length / 8;
    public ulong AssetId(int i) => BitConverter.ToUInt64(Dat1.Sections[TagAssetIds], i * 8);
    public uint ArchiveIndex(int i) => BitConverter.ToUInt32(Dat1.Sections[TagOffsets], i * 8);
    public uint Offset(int i) => BitConverter.ToUInt32(Dat1.Sections[TagOffsets], i * 8 + 4);
    public uint Size(int i) => BitConverter.ToUInt32(Dat1.Sections[TagSizes], i * 12 + 4);

    public void SetLocation(int i, uint archive, uint offset, uint size)
    {
        BitConverter.TryWriteBytes(Dat1.Sections[TagOffsets].AsSpan(i * 8), archive);
        BitConverter.TryWriteBytes(Dat1.Sections[TagOffsets].AsSpan(i * 8 + 4), offset);
        BitConverter.TryWriteBytes(Dat1.Sections[TagSizes].AsSpan(i * 12 + 4), size);
    }

    public IReadOnlyList<(uint Start, uint Count)> Spans
    {
        get
        {
            var s = Dat1.Sections[TagSpans];
            var list = new List<(uint, uint)>(s.Length / 8);
            for (int o = 0; o < s.Length; o += 8) list.Add((BitConverter.ToUInt32(s, o), BitConverter.ToUInt32(s, o + 4)));
            return list;
        }
    }

    public int ArchiveCount => Dat1.Sections[TagArchives].Length / 72;

    public string ArchiveName(int i)
    {
        var s = Dat1.Sections[TagArchives].AsSpan(i * 72 + 8, 64);
        int z = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(z < 0 ? s : s[..z]);
    }

    /// <summary>Arşiv listesinin sonuna yeni bir arşiv ekler (ya da varsa indeksini döndürür).</summary>
    public uint FindOrAddArchive(string name)
    {
        for (int i = 0; i < ArchiveCount; i++) if (ArchiveName(i) == name) return (uint)i;
        if (Encoding.ASCII.GetByteCount(name) > 63) throw new ArgumentException("Arşiv adı çok uzun", nameof(name));
        int index = ArchiveCount;
        var entry = new byte[72];
        BitConverter.TryWriteBytes(entry.AsSpan(0), 0u);                  // install bucket
        BitConverter.TryWriteBytes(entry.AsSpan(4), (uint)(10000 + index)); // chunkmap
        Encoding.ASCII.GetBytes(name).CopyTo(entry, 8);
        Dat1.Sections[TagArchives] = Dat1.Sections[TagArchives].Concat(entry).ToArray();
        return (uint)index;
    }

    /// <summary>Verilen varlık kimliğinin her span'deki indeksi.</summary>
    public List<(int Span, int Index)> FindAsset(ulong assetId)
    {
        var result = new List<(int, int)>();
        var ids = Dat1.Sections[TagAssetIds];
        var spans = Spans;
        for (int sp = 0; sp < spans.Count; sp++)
        {
            int lo = (int)spans[sp].Start, hi = (int)(spans[sp].Start + spans[sp].Count) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                ulong v = BitConverter.ToUInt64(ids, mid * 8);
                if (v == assetId) { result.Add((sp, mid)); break; }
                if (v < assetId) lo = mid + 1; else hi = mid - 1;
            }
        }
        return result;
    }

    public byte[] ReadAsset(string assetArchiveDir, int index)
    {
        var path = Path.Combine(assetArchiveDir, ArchiveName((int)ArchiveIndex(index)).Replace('\\', Path.DirectorySeparatorChar));
        using var f = File.OpenRead(path);
        return Dsar.ExtractAsset(f, Offset(index), (int)Size(index));
    }
}

/// <summary>localization_all.localization varlığı: anahtar → metin tablosu.</summary>
public sealed class LocalizationAsset
{
    public const uint AssetMagic = 0x122BB0AB;
    public const ulong AssetId = 0xBE55D94F171BF8DE; // localization/localization_all.localization
    public const uint TagCount = 0xD540A903, TagKeys = 0x4D73CEBD, TagKeyOffsets = 0xA4EA55B2, TagValues = 0x70A382B8, TagValueOffsets = 0xF80DEEB4;

    private readonly byte[] _header; // magic + boyut + 28 bilinmeyen bayt
    public Dat1 Dat1 { get; }
    public List<string> Keys { get; } = new();
    public List<string> Values { get; } = new();

    private LocalizationAsset(byte[] header, Dat1 dat1) { _header = header; Dat1 = dat1; }

    public static LocalizationAsset Parse(byte[] bytes)
    {
        if (BitConverter.ToUInt32(bytes, 0) != AssetMagic) throw new InvalidDataException("Localization varlığı değil");
        var l = new LocalizationAsset(bytes[..36], Dat1.Parse(bytes.AsSpan(36)));
        var s = l.Dat1.Sections;
        int n = (int)BitConverter.ToUInt32(s[TagCount], 0);
        for (int i = 0; i < n; i++)
        {
            l.Keys.Add(CStr(s[TagKeys], (int)BitConverter.ToUInt32(s[TagKeyOffsets], i * 4)));
            l.Values.Add(CStr(s[TagValues], (int)BitConverter.ToUInt32(s[TagValueOffsets], i * 4)));
        }
        return l;
    }

    private static string CStr(byte[] b, int o)
    {
        int e = Array.IndexOf(b, (byte)0, o);
        return Encoding.UTF8.GetString(b, o, (e < 0 ? b.Length : e) - o);
    }

    /// <summary>
    /// Yalnızca metinleri değiştirir: değer bloğu ve değer offsetleri yeniden yazılır;
    /// anahtarlar, hash'ler, sıralama tabloları ve bayraklar olduğu gibi kalır.
    /// </summary>
    public byte[] SaveWithValues(IReadOnlyDictionary<string, string> replacements)
    {
        var blob = new MemoryStream();
        var offsets = new byte[Values.Count * 4];
        var seen = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int i = 0; i < Values.Count; i++)
        {
            var v = replacements.TryGetValue(Keys[i], out var r) ? r : Values[i];
            if (!seen.TryGetValue(v, out var off))
            {
                off = (uint)blob.Position;
                seen[v] = off;
                var bytes = Encoding.UTF8.GetBytes(v);
                blob.Write(bytes);
                blob.WriteByte(0);
            }
            BitConverter.TryWriteBytes(offsets.AsSpan(i * 4), off);
        }
        Dat1.Sections[TagValues] = blob.ToArray();
        Dat1.Sections[TagValueOffsets] = offsets;
        var body = Dat1.Save();
        var header = (byte[])_header.Clone();
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)body.Length);
        return header.Concat(body).ToArray();
    }
}
