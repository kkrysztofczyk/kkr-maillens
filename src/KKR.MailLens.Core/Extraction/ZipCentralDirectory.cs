using System.Buffers.Binary;
using System.Text;

namespace KKR.MailLens;

// Ograniczony odczyt katalogu centralnego ZIP: pozwala policzyc i nazwac wpisy
// bez materializowania calego katalogu przez ZipArchive (wrogi plik moze
// zadeklarowac lub zawierac miliony wpisow w kilkudziesieciu MB).
static class ZipCentralDirectory
{
    const uint EndOfCentralDirectorySignature = 0x06054B50;   // PK\x05\x06
    const uint CentralDirectoryHeaderSignature = 0x02014B50;  // PK\x01\x02
    const int EndRecordLength = 22;
    const int CentralHeaderLength = 46;
    const int MaxCommentLength = ushort.MaxValue;

    /// <summary>
    /// Zwraca nazwy wpisow katalogu centralnego, czytajac najwyzej <paramref name="maxEntries"/>.
    /// Zwraca null, gdy struktura ZIP jest niepoprawna; <paramref name="exceedsLimit"/> ustawia,
    /// gdy zadeklarowana lub rzeczywista liczba wpisow przekracza limit.
    /// </summary>
    public static IReadOnlyList<string>? TryReadEntryNames(byte[] content, int maxEntries, out bool exceedsLimit)
    {
        exceedsLimit = false;
        int endRecord = FindEndRecord(content);
        if (endRecord < 0) return null;

        int declaredCount = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(endRecord + 10));
        long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(endRecord + 16));
        if (declaredCount > maxEntries) { exceedsLimit = true; return Array.Empty<string>(); }
        if (directoryOffset >= content.Length) return null; // obejmuje tez znacznik ZIP64 (0xFFFFFFFF)

        var names = new List<string>(Math.Min(declaredCount, 64));
        long position = directoryOffset;
        while (position + 4 <= content.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan((int)position)) == CentralDirectoryHeaderSignature)
        {
            if (names.Count >= maxEntries) { exceedsLimit = true; return names; }
            if (position + CentralHeaderLength > content.Length) return null;
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan((int)position + 28));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan((int)position + 30));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan((int)position + 32));
            long nameStart = position + CentralHeaderLength;
            if (nameStart + nameLength > content.Length) return null;
            names.Add(Encoding.UTF8.GetString(content, (int)nameStart, nameLength));
            position = nameStart + nameLength + extraLength + commentLength;
        }
        return names;
    }

    static int FindEndRecord(byte[] content)
    {
        if (content.Length < EndRecordLength) return -1;
        int lowest = Math.Max(0, content.Length - EndRecordLength - MaxCommentLength);
        for (int offset = content.Length - EndRecordLength; offset >= lowest; offset--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(offset)) == EndOfCentralDirectorySignature)
                return offset;
        return -1;
    }
}
