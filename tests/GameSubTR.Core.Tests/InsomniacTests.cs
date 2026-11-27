using System.Text;
using GameSubTR.Core.Insomniac;
using Xunit;

namespace GameSubTR.Core.Tests;

public class InsomniacTests
{
    [Fact]
    public void Lz4_DecodesReferenceBlock()
    {
        // python: lz4.block.compress(data, store_size=False)
        var comp = Convert.FromBase64String("/xVTcGlkZXItTWFuIHN3aW5ncyB0aHJvdWdoIE5ldyBZb3JrLiAkAP//////bvD5AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0BBQkNERUZHSElKS0xNTk9QUVJTVFVWV1hZWltcXV5fYGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn+AgYKDhIWGh4iJiouMjY6PkJGSk5SVlpeYmZqbnJ2en6ChoqOkpaanqKmqq6ytrq+wsbKztLW2t7i5uru8vb6/wMHCw8TFxsfIycrLzM3Oz9DR0tPU1dbX2Nna29zd3t/g4eLj5OXm5+jp6uvs7e7v8PHy8/T19vf4+fr7/P3+/1RoZSBFbmQu");
        var expected = Enumerable.Repeat("Spider-Man swings through New York. ", 40).SelectMany(Encoding.ASCII.GetBytes)
            .Concat(Enumerable.Range(0, 256).Select(i => (byte)i)).Concat(Encoding.ASCII.GetBytes("The End.")).ToArray();
        var outp = Lz4.DecodeBlock(comp, expected.Length);
        Assert.True(outp.AsSpan().SequenceEqual(expected));
    }

    /// <summary>Elle kurulmuş küçük bir localization varlığı (anahtar, değer, bayrak bölümleri).</summary>
    private static byte[] BuildLocalization(string[] keys, string[] values)
    {
        byte[] Strings(string[] a, out uint[] offs)
        {
            var ms = new MemoryStream(); offs = new uint[a.Length];
            for (int i = 0; i < a.Length; i++) { offs[i] = (uint)ms.Position; ms.Write(Encoding.UTF8.GetBytes(a[i])); ms.WriteByte(0); }
            return ms.ToArray();
        }
        byte[] U32(uint[] a) => a.SelectMany(BitConverter.GetBytes).ToArray();
        var kd = Strings(keys, out var ko);
        var vd = Strings(values, out var vo);
        var sections = new (uint Tag, byte[] Data)[]
        {
            (0x06A58050, U32(keys.Select(k => (uint)k.GetHashCode()).ToArray())),
            (LocalizationAsset.TagKeys, kd),
            (LocalizationAsset.TagKeyOffsets, U32(ko)),
            (LocalizationAsset.TagValues, vd),
            (LocalizationAsset.TagCount, BitConverter.GetBytes((uint)keys.Length)),
            (LocalizationAsset.TagValueOffsets, U32(vo)),
        };
        var preamble = Encoding.ASCII.GetBytes("Localization Built File\0\0\0\0\0\0\0\0");
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        long pos = 16 + 12 * sections.Length + preamble.Length;
        var offs = new List<long>();
        foreach (var s in sections) { pos = (pos + 15) & ~15L; offs.Add(pos); pos += s.Data.Length; }
        w.Write(Dat1.Magic); w.Write(0x122BB0ABu); w.Write((uint)pos); w.Write((ushort)sections.Length); w.Write((ushort)0);
        for (int i = 0; i < sections.Length; i++) { w.Write(sections[i].Tag); w.Write((uint)offs[i]); w.Write((uint)sections[i].Data.Length); }
        w.Write(preamble);
        for (int i = 0; i < sections.Length; i++) { while (ms.Position < offs[i]) w.Write((byte)0); w.Write(sections[i].Data); }
        var body = ms.ToArray();
        var header = new byte[36];
        BitConverter.TryWriteBytes(header.AsSpan(0), LocalizationAsset.AssetMagic);
        BitConverter.TryWriteBytes(header.AsSpan(4), (uint)body.Length);
        return header.Concat(body).ToArray();
    }

    [Fact]
    public void Dat1_RoundTripIsByteIdentical()
    {
        var asset = BuildLocalization(new[] { "INVALID", "MENU_START", "MENU_QUIT" }, new[] { "", "Start", "Quit" });
        var d = Dat1.Parse(asset.AsSpan(36));
        Assert.True(d.Save().AsSpan().SequenceEqual(asset.AsSpan(36)));
    }

    [Fact]
    public void Localization_ReplacesOnlyValues()
    {
        var asset = BuildLocalization(new[] { "INVALID", "MENU_START", "MENU_QUIT", "HINT" }, new[] { "", "Start", "Quit", "Press <b>E</b>" });
        var l = LocalizationAsset.Parse(asset);
        Assert.Equal(new[] { "INVALID", "MENU_START", "MENU_QUIT", "HINT" }, l.Keys.ToArray());
        Assert.Equal("Quit", l.Values[2]);

        Assert.True(l.SaveWithValues(new Dictionary<string, string>()).AsSpan().SequenceEqual(asset)); // değişiklik yoksa aynı

        var changed = LocalizationAsset.Parse(l.SaveWithValues(new Dictionary<string, string> { ["MENU_QUIT"] = "Çıkış", ["HINT"] = "<b>E</b> tuşuna bas" }));
        Assert.Equal(new[] { "", "Start", "Çıkış", "<b>E</b> tuşuna bas" }, changed.Values.ToArray());
        Assert.Equal(l.Keys, changed.Keys);
        // Anahtar/hash bölümleri değişmez.
        Assert.True(l.Dat1.Sections[0x06A58050].AsSpan().SequenceEqual(changed.Dat1.Sections[0x06A58050]));
        Assert.True(l.Dat1.Sections[LocalizationAsset.TagKeys].AsSpan().SequenceEqual(changed.Dat1.Sections[LocalizationAsset.TagKeys]));
    }

    [Fact]
    public void Dsar_ReadsRawArchive()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("xxxxHELLOyyyy"));
        using var f = File.OpenRead(path);
        Assert.Equal("HELLO", Encoding.ASCII.GetString(Dsar.ExtractAsset(f, 4, 5)));
    }

    [Fact]
    public void Transliterate_MapsMissingGlyphs()
    {
        Assert.Equal("Isik gibi, sogus, EMIN MISIN", InsomniacPatcher.Transliterate("Işık gibi, soğuş, EMİN MİSİN"));
        Assert.Equal("çöü ÇÖÜ", InsomniacPatcher.Transliterate("çöü ÇÖÜ"));
    }
}
