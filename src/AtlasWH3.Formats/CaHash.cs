using System.Buffers.Binary;
using System.Text;

namespace AtlasWH3.Formats;

/// <summary>
/// CA's string hashing and hash-map iteration order (calibs.modder.x64.dll), as used by BOB's writers.
/// </summary>
public static class CaHash
{
    /// <summary>CA::murmur_hash: MurmurHash3 x86 32-bit with seed 0x4A545EED.</summary>
    public static uint Murmur(ReadOnlySpan<byte> data)
    {
        const uint c1 = 0xCC9E2D51, c2 = 0x1B873593;
        var h = 0x4A545EEDu;
        var blocks = data.Length / 4;
        for (var i = 0; i < blocks; i++)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]) * c1;
            k = uint.RotateLeft(k, 15) * c2;
            h ^= k;
            h = uint.RotateLeft(h, 13) * 5 + 0xE6546B64;
        }
        var tail = data[(blocks * 4)..];
        if (tail.Length > 0)
        {
            uint k = 0;
            if (tail.Length == 3) k ^= (uint)tail[2] << 16;
            if (tail.Length >= 2) k ^= (uint)tail[1] << 8;
            k ^= tail[0];
            h ^= uint.RotateLeft(k * c1, 15) * c2;
        }
        h ^= (uint)data.Length;
        h ^= h >> 16;
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h;
    }

    public static uint Murmur(string text) => Murmur(Encoding.Latin1.GetBytes(text));

    /// <summary>
    /// Iteration order of a CA_STD hash map keyed by string after inserting <paramref name="keys"/> in order (repeats are
    /// ignored): a list kept grouped by bucket (hash % bucket count), new keys appended to their bucket. It starts with 1
    /// bucket and doubles+1 (1, 3, 7, 15, ...) whenever count + 1 exceeds the bucket count; a rehash re-buckets the keys
    /// in their current list order.
    /// </summary>
    public static List<string> HashMapOrder(IEnumerable<string> keys)
    {
        var bucketCount = 1;
        var buckets = new List<List<string>> { new() };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!seen.Add(key)) continue;
            if ((float)(seen.Count) / bucketCount > 1f)
            {
                var old = buckets.SelectMany(b => b).Where(k => k != key).ToList();
                bucketCount = bucketCount * 2 + 1;
                buckets = Enumerable.Range(0, bucketCount).Select(_ => new List<string>()).ToList();
                foreach (var k in old) buckets[(int)(Murmur(k) % (uint)bucketCount)].Add(k);
            }
            buckets[(int)(Murmur(key) % (uint)bucketCount)].Add(key);
        }
        return buckets.SelectMany(b => b).ToList();
    }
}
