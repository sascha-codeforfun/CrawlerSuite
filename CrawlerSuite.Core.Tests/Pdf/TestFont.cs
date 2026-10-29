using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace CrawlerSuite.Core.Tests.Pdf;

/// <summary>Builds tiny synthetic TrueType files (head, maxp, loca, glyf, and any extra tables) for tests.</summary>
internal static class TestFont
{
    /// <param name="glyphLengths">Outline byte length per glyph ID; 0 means an empty glyph (like a space).</param>
    public static byte[] Build(int[] glyphLengths, int unitsPerEm = 2048, string sfnt = "\0\u0001\0\0",
        IReadOnlyList<(string Tag, byte[] Data)>? extra = null)
    {
        var glyf = new byte[Sum(glyphLengths)];
        var loca = new byte[(glyphLengths.Length + 1) * 4];
        int pos = 0;
        for (int i = 0; i <= glyphLengths.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4), (uint)pos);
            if (i < glyphLengths.Length) pos += glyphLengths[i];
        }

        var head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), (ushort)unitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(36), -100);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), -400);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), 2000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), 1900);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // long loca

        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00005000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphLengths.Length);

        var tables = new List<(string Tag, byte[] Data)> { ("glyf", glyf), ("head", head), ("loca", loca), ("maxp", maxp) };
        if (extra is not null) tables.AddRange(extra);
        int offset = 12 + tables.Count * 16;
        var file = new List<byte>(Encoding.Latin1.GetBytes(sfnt));
        var dir = new byte[8 + tables.Count * 16];
        BinaryPrimitives.WriteUInt16BigEndian(dir, (ushort)tables.Count);
        var body = new List<byte>();
        for (int i = 0; i < tables.Count; i++)
        {
            var rec = dir.AsSpan(8 + i * 16);
            Encoding.ASCII.GetBytes(tables[i].Tag).CopyTo(rec);
            BinaryPrimitives.WriteUInt32BigEndian(rec[8..], (uint)(offset + body.Count));
            BinaryPrimitives.WriteUInt32BigEndian(rec[12..], (uint)tables[i].Data.Length);
            body.AddRange(tables[i].Data);
        }
        file.AddRange(dir);
        file.AddRange(body);
        return file.ToArray();
    }

    private static int Sum(int[] values)
    {
        int s = 0;
        foreach (int v in values) s += v;
        return s;
    }
}
