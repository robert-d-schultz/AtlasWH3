namespace AtlasWH3.Formats.Maps;

/// <summary>
/// A port of zlib 1.2.x's deflate for levels 4–9 (deflate_slow, trees.c), windowBits 15, memLevel 8, so the output
/// is byte-identical to the zlib that BOB's libpng links (camera_heightmap.png: level 6, Z_FILTERED). .NET's own
/// ZLibStream (zlib-ng) produces a different, equally valid stream. One-shot: all input, then Z_FINISH.
/// </summary>
public sealed class ZlibDeflate
{
    private const int MaxBits = 15, LengthCodes = 29, Literals = 256, LCodes = Literals + 1 + LengthCodes, DCodes = 30,
        BlCodes = 19, HeapSize = 2 * LCodes + 1, MaxBlBits = 7, EndBlock = 256, Rep3_6 = 16, Repz3_10 = 17, Repz11_138 = 18,
        MinMatch = 3, MaxMatch = 258, MinLookahead = MaxMatch + MinMatch + 1, TooFar = 4096;
    private const int WBits = 15, WSize = 1 << WBits, WMask = WSize - 1, HashBits = 8 + 7, HashSize = 1 << HashBits,
        HashMask = HashSize - 1, HashShift = (HashBits + MinMatch - 1) / MinMatch, LitBufSize = 1 << (8 + 6),
        WindowSize = 2 * WSize, MaxDist = WSize - MinLookahead;

    private static readonly int[] ExtraLBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] ExtraDBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly int[] ExtraBlBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 7];
    private static readonly int[] BlOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly byte[] LengthCode = new byte[256], DistCode = new byte[512];
    private static readonly int[] BaseLength = new int[LengthCodes], BaseDist = new int[DCodes];
    private static readonly ushort[] StaticLCode = new ushort[LCodes + 2], StaticLLen = new ushort[LCodes + 2];
    private static readonly ushort[] StaticDCode = new ushort[DCodes], StaticDLen = new ushort[DCodes];

    static ZlibDeflate()
    {
        int length = 0, code;
        for (code = 0; code < LengthCodes - 1; code++)
        {
            BaseLength[code] = length;
            for (var n = 0; n < 1 << ExtraLBits[code]; n++) LengthCode[length++] = (byte)code;
        }
        LengthCode[length - 1] = (byte)code;
        var dist = 0;
        for (code = 0; code < 16; code++)
        {
            BaseDist[code] = dist;
            for (var n = 0; n < 1 << ExtraDBits[code]; n++) DistCode[dist++] = (byte)code;
        }
        dist >>= 7;
        for (; code < DCodes; code++)
        {
            BaseDist[code] = dist << 7;
            for (var n = 0; n < 1 << (ExtraDBits[code] - 7); n++) DistCode[256 + dist++] = (byte)code;
        }
        var blCount = new int[MaxBits + 1];
        var i = 0;
        while (i <= 143) { StaticLLen[i++] = 8; blCount[8]++; }
        while (i <= 255) { StaticLLen[i++] = 9; blCount[9]++; }
        while (i <= 279) { StaticLLen[i++] = 7; blCount[7]++; }
        while (i <= 287) { StaticLLen[i++] = 8; blCount[8]++; }
        GenCodes(StaticLCode, StaticLLen, LCodes + 1, blCount);
        for (var n = 0; n < DCodes; n++) { StaticDLen[n] = 5; StaticDCode[n] = (ushort)BiReverse(n, 5); }
    }

    private sealed class Tree(int size, ushort[]? staticLen, int[] extra, int extraBase, int elems, int maxLength)
    {
        public readonly ushort[] Fc = new ushort[size];   // Freq / Code (a union in zlib)
        public readonly ushort[] Dl = new ushort[size];   // Dad / Len (a union in zlib)
        public readonly ushort[]? StaticLen = staticLen;
        public readonly int[] Extra = extra;
        public readonly int ExtraBase = extraBase, Elems = elems, MaxLength = maxLength;
        public int MaxCode;
    }

    // configuration (level 4..9 use deflate_slow)
    private readonly int _good, _lazy, _nice, _chain;
    private readonly bool _filtered;

    private readonly byte[] _input;
    private int _next;
    private readonly byte[] _window = new byte[WindowSize + MaxMatch];
    private readonly ushort[] _prev = new ushort[WSize];
    private readonly ushort[] _head = new ushort[HashSize];
    private int _insH, _blockStart, _matchLength = MinMatch - 1, _prevMatch, _matchAvailable, _strStart, _matchStart,
        _lookahead, _prevLength = MinMatch - 1, _insert;

    private readonly Tree _l = new(HeapSize, StaticLLen, ExtraLBits, Literals + 1, LCodes, MaxBits);
    private readonly Tree _d = new(2 * DCodes + 1, StaticDLen, ExtraDBits, 0, DCodes, MaxBits);
    private readonly Tree _bl = new(2 * BlCodes + 1, null, ExtraBlBits, 0, BlCodes, MaxBlBits);
    private readonly int[] _blCount = new int[MaxBits + 1];
    private readonly int[] _heap = new int[2 * LCodes + 1];
    private int _heapLen, _heapMax;
    private readonly byte[] _depth = new byte[2 * LCodes + 1];
    private readonly byte[] _symBuf = new byte[LitBufSize * 3];
    private int _symNext;
    private const int SymEnd = (LitBufSize - 1) * 3;
    private long _optLen, _staticLen;

    private readonly MemoryStream _out = new();
    private uint _biBuf;
    private int _biValid;

    private ZlibDeflate(byte[] input, int level, bool filtered)
    {
        (_good, _lazy, _nice, _chain) = level switch
        {
            4 => (4, 4, 16, 16),
            5 => (8, 16, 32, 32),
            6 => (8, 16, 128, 128),
            7 => (8, 32, 128, 256),
            8 => (32, 128, 258, 1024),
            9 => (32, 258, 258, 4096),
            _ => throw new ArgumentOutOfRangeException(nameof(level), "levels 4-9 (deflate_slow) only"),
        };
        _filtered = filtered;
        _input = input;
        InitBlock();
    }

    /// <summary>zlib stream (header, deflate data, Adler-32) of <paramref name="input"/>.</summary>
    public static byte[] Compress(byte[] input, int level = 6, bool filteredStrategy = false)
    {
        var z = new ZlibDeflate(input, level, filteredStrategy);
        var levelFlags = filteredStrategy || level >= 2 ? (level < 6 ? 1 : level == 6 ? 2 : 3) : 0;
        var header = (8 + ((WBits - 8) << 4)) << 8 | levelFlags << 6;
        header += 31 - header % 31;
        z._out.WriteByte((byte)(header >> 8));
        z._out.WriteByte((byte)header);
        z.DeflateSlow();
        var adler = Adler32(input);
        z._out.WriteByte((byte)(adler >> 24));
        z._out.WriteByte((byte)(adler >> 16));
        z._out.WriteByte((byte)(adler >> 8));
        z._out.WriteByte((byte)adler);
        return z._out.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        var i = 0;
        while (i < data.Length)
        {
            var n = Math.Min(5552, data.Length - i);
            for (var k = 0; k < n; k++) { a += data[i++]; b += a; }
            a %= 65521;
            b %= 65521;
        }
        return b << 16 | a;
    }

    // ---- deflate.c ----

    private void UpdateHash(byte c) => _insH = ((_insH << HashShift) ^ c) & HashMask;

    private int InsertString(int str)
    {
        UpdateHash(_window[str + MinMatch - 1]);
        int matchHead = _prev[str & WMask] = _head[_insH];
        _head[_insH] = (ushort)str;
        return matchHead;
    }

    private void FillWindow()
    {
        do
        {
            var more = WindowSize - _lookahead - _strStart;
            if (_strStart >= WSize + MaxDist)
            {
                Array.Copy(_window, WSize, _window, 0, WSize - more);
                _matchStart -= WSize;
                _strStart -= WSize;
                _blockStart -= WSize;
                if (_insert > _strStart) _insert = _strStart;
                for (var i = 0; i < HashSize; i++) _head[i] = (ushort)(_head[i] >= WSize ? _head[i] - WSize : 0);
                for (var i = 0; i < WSize; i++) _prev[i] = (ushort)(_prev[i] >= WSize ? _prev[i] - WSize : 0);
                more += WSize;
            }
            if (_next >= _input.Length) break;
            var n = Math.Min(more, _input.Length - _next);
            Array.Copy(_input, _next, _window, _strStart + _lookahead, n);
            _next += n;
            _lookahead += n;
            if (_lookahead + _insert >= MinMatch)
            {
                var str = _strStart - _insert;
                _insH = _window[str];
                UpdateHash(_window[str + 1]);
                while (_insert != 0)
                {
                    UpdateHash(_window[str + MinMatch - 1]);
                    _prev[str & WMask] = _head[_insH];
                    _head[_insH] = (ushort)str;
                    str++;
                    _insert--;
                    if (_lookahead + _insert < MinMatch) break;
                }
            }
        } while (_lookahead < MinLookahead && _next < _input.Length);
    }

    private int LongestMatch(int curMatch)
    {
        var chainLength = _chain;
        var scan = _strStart;
        var bestLen = _prevLength;
        var niceMatch = _nice;
        var limit = _strStart > MaxDist ? _strStart - MaxDist : 0;
        var strend = _strStart + MaxMatch;
        var scanEnd1 = _window[scan + bestLen - 1];
        var scanEnd = _window[scan + bestLen];
        if (_prevLength >= _good) chainLength >>= 2;
        if (niceMatch > _lookahead) niceMatch = _lookahead;
        var w = _window;
        do
        {
            var match = curMatch;
            if (w[match + bestLen] != scanEnd || w[match + bestLen - 1] != scanEnd1 || w[match] != w[scan] || w[match + 1] != w[scan + 1])
                continue;
            int s = scan + 2, m = match + 2;
            while (s < strend && w[s] == w[m]) { s++; m++; }
            var len = MaxMatch - (strend - s);
            if (len > bestLen)
            {
                _matchStart = curMatch;
                bestLen = len;
                if (len >= niceMatch) break;
                scanEnd1 = w[scan + bestLen - 1];
                scanEnd = w[scan + bestLen];
            }
        } while ((curMatch = _prev[curMatch & WMask]) > limit && --chainLength != 0);
        return bestLen <= _lookahead ? bestLen : _lookahead;
    }

    private void FlushBlock(bool last)
    {
        TrFlushBlock(_blockStart >= 0 ? _blockStart : -1, _strStart - _blockStart, last);
        _blockStart = _strStart;
    }

    private void DeflateSlow()
    {
        for (;;)
        {
            if (_lookahead < MinLookahead)
            {
                FillWindow();
                if (_lookahead == 0) break;
            }
            var hashHead = 0;
            if (_lookahead >= MinMatch) hashHead = InsertString(_strStart);
            _prevLength = _matchLength;
            _prevMatch = _matchStart;
            _matchLength = MinMatch - 1;
            if (hashHead != 0 && _prevLength < _lazy && _strStart - hashHead <= MaxDist)
            {
                _matchLength = LongestMatch(hashHead);
                if (_matchLength <= 5 && (_filtered || (_matchLength == MinMatch && _strStart - _matchStart > TooFar)))
                    _matchLength = MinMatch - 1;
            }
            if (_prevLength >= MinMatch && _matchLength <= _prevLength)
            {
                var maxInsert = _strStart + _lookahead - MinMatch;
                var flush = TallyDist(_strStart - 1 - _prevMatch, _prevLength - MinMatch);
                _lookahead -= _prevLength - 1;
                _prevLength -= 2;
                do
                {
                    if (++_strStart <= maxInsert) InsertString(_strStart);
                } while (--_prevLength != 0);
                _matchAvailable = 0;
                _matchLength = MinMatch - 1;
                _strStart++;
                if (flush) FlushBlock(false);
            }
            else if (_matchAvailable != 0)
            {
                if (TallyLit(_window[_strStart - 1])) FlushBlock(false);
                _strStart++;
                _lookahead--;
            }
            else
            {
                _matchAvailable = 1;
                _strStart++;
                _lookahead--;
            }
        }
        if (_matchAvailable != 0)
        {
            TallyLit(_window[_strStart - 1]);
            _matchAvailable = 0;
        }
        _insert = _strStart < MinMatch - 1 ? _strStart : MinMatch - 1;
        FlushBlock(true);
    }

    // ---- trees.c ----

    private void InitBlock()
    {
        Array.Clear(_l.Fc, 0, LCodes);
        Array.Clear(_d.Fc, 0, DCodes);
        Array.Clear(_bl.Fc, 0, BlCodes);
        _l.Fc[EndBlock] = 1;
        _optLen = _staticLen = 0;
        _symNext = 0;
    }

    private static int DCode(int dist) => dist < 256 ? DistCode[dist] : DistCode[256 + (dist >> 7)];

    private bool TallyLit(byte c)
    {
        _symBuf[_symNext++] = 0;
        _symBuf[_symNext++] = 0;
        _symBuf[_symNext++] = c;
        _l.Fc[c]++;
        return _symNext == SymEnd;
    }

    private bool TallyDist(int dist, int len)
    {
        _symBuf[_symNext++] = (byte)dist;
        _symBuf[_symNext++] = (byte)(dist >> 8);
        _symBuf[_symNext++] = (byte)len;
        dist--;
        _l.Fc[LengthCode[len] + Literals + 1]++;
        _d.Fc[DCode(dist)]++;
        return _symNext == SymEnd;
    }

    private bool Smaller(Tree t, int n, int m) => t.Fc[n] < t.Fc[m] || (t.Fc[n] == t.Fc[m] && _depth[n] <= _depth[m]);

    private void PqDownHeap(Tree t, int k)
    {
        var v = _heap[k];
        var j = k << 1;
        while (j <= _heapLen)
        {
            if (j < _heapLen && Smaller(t, _heap[j + 1], _heap[j])) j++;
            if (Smaller(t, v, _heap[j])) break;
            _heap[k] = _heap[j];
            k = j;
            j <<= 1;
        }
        _heap[k] = v;
    }

    private void GenBitLen(Tree t)
    {
        var tree = t;
        var maxCode = t.MaxCode;
        var overflow = 0;
        int h, n, bits;
        for (bits = 0; bits <= MaxBits; bits++) _blCount[bits] = 0;
        tree.Dl[_heap[_heapMax]] = 0;
        for (h = _heapMax + 1; h < HeapSize; h++)
        {
            n = _heap[h];
            bits = tree.Dl[tree.Dl[n]] + 1;
            if (bits > t.MaxLength) { bits = t.MaxLength; overflow++; }
            tree.Dl[n] = (ushort)bits;
            if (n > maxCode) continue;
            _blCount[bits]++;
            var xbits = n >= t.ExtraBase ? t.Extra[n - t.ExtraBase] : 0;
            long f = tree.Fc[n];
            _optLen += f * (bits + xbits);
            if (t.StaticLen is { } st) _staticLen += f * (st[n] + xbits);
        }
        if (overflow == 0) return;
        do
        {
            bits = t.MaxLength - 1;
            while (_blCount[bits] == 0) bits--;
            _blCount[bits]--;
            _blCount[bits + 1] += 2;
            _blCount[t.MaxLength]--;
            overflow -= 2;
        } while (overflow > 0);
        for (bits = t.MaxLength; bits != 0; bits--)
        {
            n = _blCount[bits];
            while (n != 0)
            {
                var m = _heap[--h];
                if (m > maxCode) continue;
                if (tree.Dl[m] != bits)
                {
                    _optLen += ((long)bits - tree.Dl[m]) * tree.Fc[m];
                    tree.Dl[m] = (ushort)bits;
                }
                n--;
            }
        }
    }

    private static void GenCodes(ushort[] code, ushort[] len, int maxCode, int[] blCount)
    {
        var nextCode = new int[MaxBits + 1];
        var c = 0;
        for (var bits = 1; bits <= MaxBits; bits++)
        {
            c = (c + blCount[bits - 1]) << 1;
            nextCode[bits] = c;
        }
        for (var n = 0; n <= maxCode; n++)
        {
            int l = len[n];
            if (l == 0) continue;
            code[n] = (ushort)BiReverse(nextCode[l]++, l);
        }
    }

    private static int BiReverse(int code, int len)
    {
        var res = 0;
        do
        {
            res |= code & 1;
            code >>= 1;
            res <<= 1;
        } while (--len > 0);
        return res >> 1;
    }

    private void BuildTree(Tree t)
    {
        var maxCode = -1;
        int n, m, node;
        _heapLen = 0;
        _heapMax = HeapSize;
        for (n = 0; n < t.Elems; n++)
        {
            if (t.Fc[n] != 0) { _heap[++_heapLen] = maxCode = n; _depth[n] = 0; }
            else t.Dl[n] = 0;
        }
        while (_heapLen < 2)
        {
            node = _heap[++_heapLen] = maxCode < 2 ? ++maxCode : 0;
            t.Fc[node] = 1;
            _depth[node] = 0;
            _optLen--;
            if (t.StaticLen is { } st) _staticLen -= st[node];
        }
        t.MaxCode = maxCode;
        for (n = _heapLen / 2; n >= 1; n--) PqDownHeap(t, n);
        node = t.Elems;
        do
        {
            n = _heap[1];
            _heap[1] = _heap[_heapLen--];
            PqDownHeap(t, 1);
            m = _heap[1];
            _heap[--_heapMax] = n;
            _heap[--_heapMax] = m;
            t.Fc[node] = (ushort)(t.Fc[n] + t.Fc[m]);
            _depth[node] = (byte)((_depth[n] >= _depth[m] ? _depth[n] : _depth[m]) + 1);
            t.Dl[n] = t.Dl[m] = (ushort)node;
            _heap[1] = node++;
            PqDownHeap(t, 1);
        } while (_heapLen >= 2);
        _heap[--_heapMax] = _heap[1];
        GenBitLen(t);
        GenCodes(t.Fc, t.Dl, maxCode, _blCount);
    }

    private void ScanTree(Tree t, int maxCode)
    {
        int prevLen = -1, nextLen = t.Dl[0], count = 0, maxCount = 7, minCount = 4;
        if (nextLen == 0) { maxCount = 138; minCount = 3; }
        t.Dl[maxCode + 1] = 0xffff;
        for (var n = 0; n <= maxCode; n++)
        {
            var curLen = nextLen;
            nextLen = t.Dl[n + 1];
            if (++count < maxCount && curLen == nextLen) continue;
            if (count < minCount) _bl.Fc[curLen] += (ushort)count;
            else if (curLen != 0)
            {
                if (curLen != prevLen) _bl.Fc[curLen]++;
                _bl.Fc[Rep3_6]++;
            }
            else if (count <= 10) _bl.Fc[Repz3_10]++;
            else _bl.Fc[Repz11_138]++;
            count = 0;
            prevLen = curLen;
            if (nextLen == 0) { maxCount = 138; minCount = 3; }
            else if (curLen == nextLen) { maxCount = 6; minCount = 3; }
            else { maxCount = 7; minCount = 4; }
        }
    }

    private void SendTree(Tree t, int maxCode)
    {
        int prevLen = -1, nextLen = t.Dl[0], count = 0, maxCount = 7, minCount = 4;
        if (nextLen == 0) { maxCount = 138; minCount = 3; }
        for (var n = 0; n <= maxCode; n++)
        {
            var curLen = nextLen;
            nextLen = t.Dl[n + 1];
            if (++count < maxCount && curLen == nextLen) continue;
            if (count < minCount)
            {
                do SendCode(curLen, _bl); while (--count != 0);
            }
            else if (curLen != 0)
            {
                if (curLen != prevLen) { SendCode(curLen, _bl); count--; }
                SendCode(Rep3_6, _bl);
                SendBits(count - 3, 2);
            }
            else if (count <= 10) { SendCode(Repz3_10, _bl); SendBits(count - 3, 3); }
            else { SendCode(Repz11_138, _bl); SendBits(count - 11, 7); }
            count = 0;
            prevLen = curLen;
            if (nextLen == 0) { maxCount = 138; minCount = 3; }
            else if (curLen == nextLen) { maxCount = 6; minCount = 3; }
            else { maxCount = 7; minCount = 4; }
        }
    }

    private int BuildBlTree()
    {
        ScanTree(_l, _l.MaxCode);
        ScanTree(_d, _d.MaxCode);
        BuildTree(_bl);
        int maxBlIndex;
        for (maxBlIndex = BlCodes - 1; maxBlIndex >= 3; maxBlIndex--)
            if (_bl.Dl[BlOrder[maxBlIndex]] != 0) break;
        _optLen += 3 * ((long)maxBlIndex + 1) + 5 + 5 + 4;
        return maxBlIndex;
    }

    private void SendAllTrees(int lcodes, int dcodes, int blcodes)
    {
        SendBits(lcodes - 257, 5);
        SendBits(dcodes - 1, 5);
        SendBits(blcodes - 4, 4);
        for (var rank = 0; rank < blcodes; rank++) SendBits(_bl.Dl[BlOrder[rank]], 3);
        SendTree(_l, lcodes - 1);
        SendTree(_d, dcodes - 1);
    }

    private void TrFlushBlock(int buf, int storedLen, bool last)
    {
        BuildTree(_l);
        BuildTree(_d);
        var maxBlIndex = BuildBlTree();
        var optLenB = (_optLen + 3 + 7) >> 3;
        var staticLenB = (_staticLen + 3 + 7) >> 3;
        if (staticLenB <= optLenB) optLenB = staticLenB;
        var lastBit = last ? 1 : 0;
        if (storedLen + 4 <= optLenB && buf != -1)
        {
            SendBits((0 << 1) + lastBit, 3);
            BiWindup();
            _out.WriteByte((byte)storedLen);
            _out.WriteByte((byte)(storedLen >> 8));
            _out.WriteByte((byte)~storedLen);
            _out.WriteByte((byte)(~storedLen >> 8));
            _out.Write(_window, buf, storedLen);
        }
        else if (staticLenB == optLenB)
        {
            SendBits((1 << 1) + lastBit, 3);
            CompressBlock(StaticLCode, StaticLLen, StaticDCode, StaticDLen);
        }
        else
        {
            SendBits((2 << 1) + lastBit, 3);
            SendAllTrees(_l.MaxCode + 1, _d.MaxCode + 1, maxBlIndex + 1);
            CompressBlock(_l.Fc, _l.Dl, _d.Fc, _d.Dl);
        }
        InitBlock();
        if (last) BiWindup();
    }

    private void CompressBlock(ushort[] lCode, ushort[] lLen, ushort[] dCode, ushort[] dLen)
    {
        var sx = 0;
        while (sx < _symNext)
        {
            var dist = _symBuf[sx++] | _symBuf[sx++] << 8;
            int lc = _symBuf[sx++];
            if (dist == 0) SendBits(lCode[lc], lLen[lc]);
            else
            {
                int code = LengthCode[lc];
                SendBits(lCode[code + Literals + 1], lLen[code + Literals + 1]);
                var extra = ExtraLBits[code];
                if (extra != 0) SendBits(lc - BaseLength[code], extra);
                dist--;
                code = DCode(dist);
                SendBits(dCode[code], dLen[code]);
                extra = ExtraDBits[code];
                if (extra != 0) SendBits(dist - BaseDist[code], extra);
            }
        }
        SendBits(lCode[EndBlock], lLen[EndBlock]);
    }

    private void SendCode(int c, Tree t) => SendBits(t.Fc[c], t.Dl[c]);

    private void SendBits(int value, int length)
    {
        _biBuf |= (uint)(value & ((1 << length) - 1)) << _biValid;
        _biValid += length;
        while (_biValid >= 8)
        {
            _out.WriteByte((byte)_biBuf);
            _biBuf >>= 8;
            _biValid -= 8;
        }
    }

    private void BiWindup()
    {
        if (_biValid > 0) _out.WriteByte((byte)_biBuf);
        _biBuf = 0;
        _biValid = 0;
    }
}
