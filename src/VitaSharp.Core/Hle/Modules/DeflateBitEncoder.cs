using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The deflate encoder (RFC 1951) of the kernel's zlib 1.2.5 (SceZlibForDriver, in SceSblPostSsMgr): a bit-level
/// writer with zlib's 16-bit bit buffer (bi_buf / bi_valid, what deflatePrime sets), LZ77 matching over a 32 KB
/// window kept across flushes (zlib's level table: greedy for levels 1-3, lazy for 4-9, the TOO_FAR rule, the
/// strategies) and each block written stored, with the fixed codes or with dynamic codes, whichever is shortest (as
/// zlib's _tr_flush_block chooses). The blocks start where the last one ended, on any bit: primed bits, a Z_BLOCK or
/// a Z_PARTIAL_FLUSH leave the stream off a byte boundary and the next block follows those bits directly.
/// </summary>
internal sealed class DeflateBitEncoder
{
    public const int NoFlush = 0, PartialFlush = 1, SyncFlush = 2, FullFlush = 3, Finish = 4, Block = 5;
    public const int StrategyFiltered = 1, StrategyHuffmanOnly = 2, StrategyRle = 3, StrategyFixed = 4;

    const int WindowSize = 32768;
    const int MinMatch = 3;
    const int MaxMatch = 258;
    const int TooFar = 4096;
    const int HashBits = 15;
    const int EndOfBlock = 256;
    const int LengthCodes = 29;
    const int LiteralLengthSymbols = 286;
    const int DistanceSymbols = 30;
    const int CodeLengthSymbols = 19;

    /// <summary>The input a block takes before it is written without a flush (zlib writes a block when its symbol
    /// buffer fills; this bounds the memory the same way).</summary>
    const int BlockInput = 0xFFFF;

    /// <summary>zlib's MAX_DIST: the farthest match (the window less MIN_LOOKAHEAD).</summary>
    const int MaxDistance = WindowSize - (MaxMatch + MinMatch + 1);

    static ReadOnlySpan<ushort> LengthBase => [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    static ReadOnlySpan<byte> LengthExtra => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    static ReadOnlySpan<ushort> DistanceBase => [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    static ReadOnlySpan<byte> DistanceExtra => [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    static ReadOnlySpan<byte> CodeLengthOrder => [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    /// <summary>zlib's configuration_table: good_length, max_lazy, nice_length, max_chain by level.</summary>
    static ReadOnlySpan<ushort> LevelTable =>
    [
        0, 0, 0, 0,
        4, 4, 8, 4,
        4, 5, 16, 8,
        4, 6, 32, 32,
        4, 4, 16, 16,
        8, 16, 32, 32,
        8, 16, 128, 128,
        8, 32, 128, 256,
        32, 128, 258, 1024,
        32, 258, 258, 4096,
    ];

    static readonly byte[] FixedLiteralLengths = MakeFixedLiteralLengths();
    static readonly byte[] FixedDistanceLengths = MakeFixedDistanceLengths();

    /// <summary>The window (up to 32 KB of input already written) followed by the input of the block being built.</summary>
    byte[] _history = new byte[WindowSize + BlockInput];
    int _historyLength;
    int _blockStart;
    ushort _bitBuffer;
    int _bitCount;
    int _level;
    int _strategy;
    int _goodLength;
    int _maxLazy;
    int _niceLength;
    int _maxChain;

    public DeflateBitEncoder(int level, int strategy)
    {
        SetParameters(level, strategy);
    }

    /// <summary>Data type of the first block's literals (z_stream.data_type): 0 binary, 1 text, 2 not known yet.</summary>
    public int DataType { get; private set; } = 2;

    /// <summary>The bits held in the bit buffer, not written as a byte yet.</summary>
    public int PendingBits => _bitCount;

    /// <summary>The level and strategy, with the level's match search parameters.</summary>
    public void SetParameters(int level, int strategy)
    {
        _level = level;
        _strategy = strategy;
        _goodLength = LevelTable[level * 4];
        _maxLazy = LevelTable[(level * 4) + 1];
        _niceLength = LevelTable[(level * 4) + 2];
        _maxChain = LevelTable[(level * 4) + 3];
    }

    /// <summary>deflateTune: the match search parameters.</summary>
    public void Tune(int goodLength, int maxLazy, int niceLength, int maxChain)
    {
        _goodLength = goodLength;
        _maxLazy = maxLazy;
        _niceLength = niceLength;
        _maxChain = maxChain;
    }

    /// <summary>
    /// deflatePrime, as zlib 1.2.5 does it: the bit buffer becomes the low <paramref name="bits"/> bits of
    /// <paramref name="value"/> (any bits it held are dropped), written before the next block. zlib 1.2.5 does not
    /// check the count and its bit buffer is 16 bits wide; a count past 16 (undefined in zlib) is kept as 16.
    /// </summary>
    public void Prime(int bits, int value)
    {
        int count = Math.Clamp(bits, 0, 16);
        _bitCount = count;
        _bitBuffer = (ushort)(value & (int)((1u << count) - 1));
    }

    /// <summary>deflateSetDictionary: the window starts with the last 32 KB of the dictionary.</summary>
    public void SetDictionary(ReadOnlySpan<byte> dictionary)
    {
        if (dictionary.Length < MinMatch)
        {
            return;
        }

        if (dictionary.Length > WindowSize)
        {
            dictionary = dictionary[^WindowSize..];
        }

        // the window is replaced; input given but not written yet stays, after the dictionary
        byte[] waiting = _history.AsSpan(_blockStart, _historyLength - _blockStart).ToArray();
        dictionary.CopyTo(_history);
        waiting.CopyTo(_history, dictionary.Length);
        _blockStart = dictionary.Length;
        _historyLength = _blockStart + waiting.Length;
    }

    /// <summary>Takes input; a block is written to <paramref name="output"/> each time 64 KB are waiting.</summary>
    public void AddInput(ReadOnlySpan<byte> input, List<byte> output)
    {
        while (!input.IsEmpty)
        {
            int room = _blockStart + BlockInput - _historyLength;
            if (room == 0)
            {
                WriteBlock(output, last: false);
                continue;
            }

            int count = Math.Min(room, input.Length);
            input[..count].CopyTo(_history.AsSpan(_historyLength));
            _historyLength += count;
            input = input[count..];
        }
    }

    /// <summary>A flush point: the waiting input as a block, then what the flush asks for (an empty fixed block for
    /// Z_PARTIAL_FLUSH, an empty stored block and a byte boundary for Z_SYNC/FULL_FLUSH, the last block and a byte
    /// boundary for Z_FINISH, nothing more for Z_BLOCK); complete bytes go to <paramref name="output"/>.</summary>
    public void Flush(int flush, List<byte> output)
    {
        if (flush == NoFlush)
        {
            return;
        }

        if (flush == Finish)
        {
            WriteBlock(output, last: true);
            AlignToByte(output);
            return;
        }

        if (_historyLength > _blockStart)
        {
            WriteBlock(output, last: false);
        }

        switch (flush)
        {
            case PartialFlush:
                WriteBits(1 << 1, 3);
                WriteBits(0, 7);
                break;
            case SyncFlush:
            case FullFlush:
                WriteStoredBlock(output, ReadOnlySpan<byte>.Empty, last: false);
                if (flush == FullFlush)
                {
                    // zlib clears its hash: nothing after a full flush refers to what came before it
                    _historyLength = 0;
                    _blockStart = 0;
                }

                break;
        }

        FlushBits(output);
    }

    // ------------------------------------------------------------------ bit writer

    /// <summary>zlib's send_bits: the bits go into the 16-bit buffer, which is written as two bytes when it fills.</summary>
    void WriteBits(int value, int length)
    {
        if (_bitCount > 16 - length)
        {
            ushort full = (ushort)(_bitBuffer | (value << _bitCount));
            _pendingBytes.Add((byte)full);
            _pendingBytes.Add((byte)(full >> 8));
            _bitBuffer = (ushort)(value >> (16 - _bitCount));
            _bitCount += length - 16;
        }
        else
        {
            _bitBuffer = (ushort)(_bitBuffer | (value << _bitCount));
            _bitCount += length;
        }
    }

    /// <summary>The bytes the bit writer completed since the last time they were moved to an output.</summary>
    readonly List<byte> _pendingBytes = [];

    void MovePending(List<byte> output)
    {
        output.AddRange(_pendingBytes);
        _pendingBytes.Clear();
    }

    /// <summary>zlib's bi_flush: the complete bytes of the buffer are written, at most 7 bits stay.</summary>
    void FlushBits(List<byte> output)
    {
        if (_bitCount == 16)
        {
            _pendingBytes.Add((byte)_bitBuffer);
            _pendingBytes.Add((byte)(_bitBuffer >> 8));
            _bitBuffer = 0;
            _bitCount = 0;
        }
        else if (_bitCount >= 8)
        {
            _pendingBytes.Add((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }

        MovePending(output);
    }

    /// <summary>zlib's bi_windup: the buffer written out to a byte boundary.</summary>
    void AlignToByte(List<byte> output)
    {
        if (_bitCount > 8)
        {
            _pendingBytes.Add((byte)_bitBuffer);
            _pendingBytes.Add((byte)(_bitBuffer >> 8));
        }
        else if (_bitCount > 0)
        {
            _pendingBytes.Add((byte)_bitBuffer);
        }

        _bitBuffer = 0;
        _bitCount = 0;
        MovePending(output);
    }

    void WriteCode(ushort[] codes, byte[] lengths, int symbol)
    {
        WriteBits(codes[symbol], lengths[symbol]);
    }

    // ------------------------------------------------------------------ blocks

    /// <summary>One block of the waiting input (possibly empty), in the shortest of the three forms.</summary>
    void WriteBlock(List<byte> output, bool last)
    {
        ReadOnlySpan<byte> blockInput = _history.AsSpan(_blockStart, _historyLength - _blockStart);
        if (_level == 0)
        {
            WriteStoredBlocks(output, blockInput, last);
            SlideWindow();
            return;
        }

        List<int> symbols = FindMatches();
        var literalFrequencies = new int[LiteralLengthSymbols];
        var distanceFrequencies = new int[DistanceSymbols];
        foreach (int symbol in symbols)
        {
            int distance = symbol >> 9;
            if (distance == 0)
            {
                literalFrequencies[symbol & 0x1FF]++;
            }
            else
            {
                literalFrequencies[257 + LengthCode((symbol & 0x1FF) + MinMatch)]++;
                distanceFrequencies[DistanceCode(distance)]++;
            }
        }

        literalFrequencies[EndOfBlock] = 1;
        if (DataType == 2)
        {
            DataType = DetectDataType(literalFrequencies);
        }

        byte[] literalLengths = HuffmanLengths(literalFrequencies, 15);
        byte[] distanceLengths = HuffmanLengths(distanceFrequencies, 15);
        int literalCount = Trim(literalLengths, 257);
        int distanceCount = Trim(distanceLengths, 1);
        List<int> lengthRuns = RunLengths(literalLengths, literalCount, distanceLengths, distanceCount);
        var codeLengthFrequencies = new int[CodeLengthSymbols];
        foreach (int run in lengthRuns)
        {
            codeLengthFrequencies[run & 0x1F]++;
        }

        byte[] codeLengthLengths = HuffmanLengths(codeLengthFrequencies, 7);
        int codeLengthCount = CodeLengthSymbols;
        while (codeLengthCount > 4 && codeLengthLengths[CodeLengthOrder[codeLengthCount - 1]] == 0)
        {
            codeLengthCount--;
        }

        long dynamicBits = 3 + 5 + 5 + 4 + (3L * codeLengthCount);
        foreach (int run in lengthRuns)
        {
            int symbol = run & 0x1F;
            dynamicBits += codeLengthLengths[symbol] + (symbol == 16 ? 2 : symbol == 17 ? 3 : symbol == 18 ? 7 : 0);
        }

        dynamicBits += DataBits(literalFrequencies, distanceFrequencies, literalLengths, distanceLengths);
        long fixedBits = 3 + DataBits(literalFrequencies, distanceFrequencies, FixedLiteralLengths, FixedDistanceLengths);
        long optimalBytes = (Math.Min(dynamicBits, fixedBits) + 7) >> 3;
        long storedBytes = blockInput.Length + 4;
        if (storedBytes <= optimalBytes && blockInput.Length <= 0xFFFF)
        {
            WriteStoredBlock(output, blockInput, last);
        }
        else if (_strategy == StrategyFixed || fixedBits <= dynamicBits)
        {
            WriteBits((1 << 1) | (last ? 1 : 0), 3);
            WriteSymbols(symbols, FixedLiteralLengths, FixedDistanceLengths);
        }
        else
        {
            WriteBits((2 << 1) | (last ? 1 : 0), 3);
            WriteBits(literalCount - 257, 5);
            WriteBits(distanceCount - 1, 5);
            WriteBits(codeLengthCount - 4, 4);
            for (int i = 0; i < codeLengthCount; i++)
            {
                WriteBits(codeLengthLengths[CodeLengthOrder[i]], 3);
            }

            ushort[] codeLengthCodes = CanonicalCodes(codeLengthLengths);
            foreach (int run in lengthRuns)
            {
                int symbol = run & 0x1F;
                WriteCode(codeLengthCodes, codeLengthLengths, symbol);
                if (symbol == 16)
                {
                    WriteBits(run >> 5, 2);
                }
                else if (symbol == 17)
                {
                    WriteBits(run >> 5, 3);
                }
                else if (symbol == 18)
                {
                    WriteBits(run >> 5, 7);
                }
            }

            WriteSymbols(symbols, literalLengths, distanceLengths);
        }

        MovePending(output);
        SlideWindow();
    }

    void WriteStoredBlocks(List<byte> output, ReadOnlySpan<byte> data, bool last)
    {
        do
        {
            int count = Math.Min(data.Length, 0xFFFF);
            WriteStoredBlock(output, data[..count], last && count == data.Length);
            data = data[count..];
        }
        while (!data.IsEmpty);
    }

    /// <summary>zlib's _tr_stored_block: the block type, a byte boundary, LEN and NLEN, the bytes.</summary>
    void WriteStoredBlock(List<byte> output, ReadOnlySpan<byte> data, bool last)
    {
        WriteBits(last ? 1 : 0, 3);
        AlignToByte(output);
        output.Add((byte)data.Length);
        output.Add((byte)(data.Length >> 8));
        output.Add((byte)~data.Length);
        output.Add((byte)(~data.Length >> 8));
        output.AddRange(data);
    }

    void WriteSymbols(List<int> symbols, byte[] literalLengths, byte[] distanceLengths)
    {
        ushort[] literalCodes = CanonicalCodes(literalLengths);
        ushort[] distanceCodes = CanonicalCodes(distanceLengths);
        foreach (int symbol in symbols)
        {
            int distance = symbol >> 9;
            if (distance == 0)
            {
                WriteCode(literalCodes, literalLengths, symbol & 0x1FF);
                continue;
            }

            int length = (symbol & 0x1FF) + MinMatch;
            int lengthCode = LengthCode(length);
            WriteCode(literalCodes, literalLengths, 257 + lengthCode);
            if (LengthExtra[lengthCode] != 0)
            {
                WriteBits(length - LengthBase[lengthCode], LengthExtra[lengthCode]);
            }

            int distanceCode = DistanceCode(distance);
            WriteCode(distanceCodes, distanceLengths, distanceCode);
            if (DistanceExtra[distanceCode] != 0)
            {
                WriteBits(distance - DistanceBase[distanceCode], DistanceExtra[distanceCode]);
            }
        }

        WriteCode(literalCodes, literalLengths, EndOfBlock);
    }

    /// <summary>After a block: the window keeps the last 32 KB, the next block starts after it.</summary>
    void SlideWindow()
    {
        if (_historyLength > WindowSize)
        {
            int drop = _historyLength - WindowSize;
            _history.AsSpan(drop, WindowSize).CopyTo(_history);
            _historyLength = WindowSize;
        }

        _blockStart = _historyLength;
    }

    static long DataBits(int[] literalFrequencies, int[] distanceFrequencies, byte[] literalLengths, byte[] distanceLengths)
    {
        long bits = 0;
        for (int i = 0; i < LiteralLengthSymbols; i++)
        {
            bits += (long)literalFrequencies[i] * (literalLengths[i] + (i > 256 ? LengthExtra[i - 257] : 0));
        }

        for (int i = 0; i < DistanceSymbols; i++)
        {
            bits += (long)distanceFrequencies[i] * (distanceLengths[i] + DistanceExtra[i]);
        }

        return bits;
    }

    /// <summary>zlib 1.2.5's detect_data_type: binary when a control byte outside tab/newline/return... occurs, text
    /// when a tab, newline, return or a byte from 32 up occurs, else binary.</summary>
    static int DetectDataType(int[] literalFrequencies)
    {
        uint blackMask = 0xF3FFC07F;
        for (int n = 0; n <= 31; n++, blackMask >>= 1)
        {
            if ((blackMask & 1) != 0 && literalFrequencies[n] != 0)
            {
                return 0;
            }
        }

        if (literalFrequencies[9] != 0 || literalFrequencies[10] != 0 || literalFrequencies[13] != 0)
        {
            return 1;
        }

        for (int n = 32; n < 256; n++)
        {
            if (literalFrequencies[n] != 0)
            {
                return 1;
            }
        }

        return 0;
    }

    static int LengthCode(int length)
    {
        int code = 0;
        while (code < LengthCodes - 1 && LengthBase[code + 1] <= length)
        {
            code++;
        }

        return code;
    }

    static int DistanceCode(int distance)
    {
        int code = 0;
        while (code < DistanceSymbols - 1 && DistanceBase[code + 1] <= distance)
        {
            code++;
        }

        return code;
    }

    // ------------------------------------------------------------------ LZ77

    /// <summary>The block's symbols: a literal (the byte) or a match ((distance &lt;&lt; 9) | (length - 3)).</summary>
    List<int> FindMatches()
    {
        var symbols = new List<int>();
        int end = _historyLength;
        var head = new int[1 << HashBits];
        Array.Fill(head, -1);
        var previous = new int[end];
        for (int i = 0; i < _blockStart; i++)
        {
            Insert(head, previous, i, end);
        }

        bool lazy = _level >= 4;
        int position = _blockStart;
        int heldLength = 0;
        int heldDistance = 0;
        bool holding = false;
        while (position < end)
        {
            (int length, int distance) = BestMatch(head, previous, position, end, holding ? heldLength : 0);
            Insert(head, previous, position, end);
            if (!lazy)
            {
                if (length >= MinMatch)
                {
                    symbols.Add((distance << 9) | (length - MinMatch));
                    for (int i = 1; i < length; i++)
                    {
                        Insert(head, previous, position + i, end);
                    }

                    position += length;
                }
                else
                {
                    symbols.Add(_history[position]);
                    position++;
                }

                continue;
            }

            if (holding && heldLength >= MinMatch && length <= heldLength)
            {
                // the match held at the previous byte is at least as long: it is written, from there
                symbols.Add((heldDistance << 9) | (heldLength - MinMatch));
                for (int i = 1; i < heldLength - 1; i++)
                {
                    Insert(head, previous, position + i, end);
                }

                position += heldLength - 1;
                holding = false;
                heldLength = 0;
                continue;
            }

            if (holding)
            {
                symbols.Add(_history[position - 1]);
            }

            holding = true;
            heldLength = length;
            heldDistance = distance;
            position++;
        }

        if (holding)
        {
            symbols.Add(_history[position - 1]);
        }

        return symbols;
    }

    void Insert(int[] head, int[] previous, int position, int end)
    {
        if (position + MinMatch > end)
        {
            previous[position] = -1;
            return;
        }

        int hash = Hash(position);
        previous[position] = head[hash];
        head[hash] = position;
    }

    int Hash(int position)
    {
        return ((_history[position] << 10) ^ (_history[position + 1] << 5) ^ _history[position + 2]) & ((1 << HashBits) - 1);
    }

    /// <summary>The longest match for <paramref name="position"/> in the window (zlib's longest_match: max_chain
    /// candidates, a quarter of them once the held match reaches good_length, done at nice_length); lengths under 3,
    /// a 3-byte match farther than 4096 (TOO_FAR), short matches of Z_FILTERED and every match of Z_HUFFMAN_ONLY are
    /// no match; Z_RLE looks one byte back only.</summary>
    (int Length, int Distance) BestMatch(int[] head, int[] previous, int position, int end, int heldLength)
    {
        if (_strategy == StrategyHuffmanOnly || position + MinMatch > end)
        {
            return (0, 0);
        }

        int limit = Math.Min(MaxMatch, end - position);
        if (_strategy == StrategyRle)
        {
            if (position == 0)
            {
                return (0, 0);
            }

            int run = 0;
            while (run < limit && _history[position + run] == _history[position - 1 + run])
            {
                run++;
            }

            return run >= MinMatch ? (run, 1) : (0, 0);
        }

        if (_level >= 4 && heldLength >= _maxLazy)
        {
            return (0, 0);
        }

        int chain = _maxChain;
        if (heldLength >= _goodLength)
        {
            chain >>= 2;
        }

        int bestLength = Math.Max(heldLength, MinMatch - 1);
        if (bestLength >= limit)
        {
            // nothing here can be longer than the match already held
            return (0, 0);
        }

        int bestDistance = 0;
        int candidate = head[Hash(position)];
        while (candidate >= 0 && chain-- > 0)
        {
            int distance = position - candidate;
            if (distance > MaxDistance)
            {
                break;
            }

            if (_history[candidate + bestLength] == _history[position + bestLength])
            {
                int length = 0;
                while (length < limit && _history[candidate + length] == _history[position + length])
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLength = length;
                    bestDistance = distance;
                    if (length >= _niceLength || length >= limit)
                    {
                        break;
                    }
                }
            }

            candidate = previous[candidate];
        }

        if (bestDistance == 0 || bestLength < MinMatch)
        {
            return (0, 0);
        }

        if (bestLength <= 5 && (_strategy == StrategyFiltered || (bestLength == MinMatch && bestDistance > TooFar)))
        {
            return (0, 0);
        }

        return (bestLength, bestDistance);
    }

    // ------------------------------------------------------------------ Huffman codes

    /// <summary>The code lengths of a Huffman code for the frequencies, none longer than <paramref name="maxLength"/>
    /// (frequencies are halved until the tree fits); a code with fewer than two used symbols gets codes 0 and 1 of
    /// length 1, as zlib's build_tree makes at least two codes.</summary>
    static byte[] HuffmanLengths(int[] frequencies, int maxLength)
    {
        var lengths = new byte[frequencies.Length];
        var weights = (int[])frequencies.Clone();
        while (true)
        {
            var used = new List<int>();
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] != 0)
                {
                    used.Add(i);
                }
            }

            if (used.Count < 2)
            {
                int only = used.Count == 1 ? used[0] : 0;
                lengths[only] = 1;
                lengths[only == 0 ? 1 : 0] = 1;
                return lengths;
            }

            // a heap of (weight, node); nodes past the symbols are the merged ones
            var parent = new int[weights.Length * 2];
            var queue = new PriorityQueue<int, long>();
            foreach (int symbol in used)
            {
                queue.Enqueue(symbol, ((long)weights[symbol] << 20) | (uint)symbol);
            }

            int next = weights.Length;
            var nodeWeight = new long[weights.Length * 2];
            foreach (int symbol in used)
            {
                nodeWeight[symbol] = weights[symbol];
            }

            while (queue.Count > 1)
            {
                int first = queue.Dequeue();
                int second = queue.Dequeue();
                nodeWeight[next] = nodeWeight[first] + nodeWeight[second];
                parent[first] = next;
                parent[second] = next;
                queue.Enqueue(next, (nodeWeight[next] << 20) | (uint)next);
                next++;
            }

            int root = queue.Dequeue();
            bool fits = true;
            foreach (int symbol in used)
            {
                int depth = 0;
                for (int node = symbol; node != root; node = parent[node])
                {
                    depth++;
                }

                if (depth > maxLength)
                {
                    fits = false;
                    break;
                }

                lengths[symbol] = (byte)depth;
            }

            if (fits)
            {
                return lengths;
            }

            Array.Clear(lengths);
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] != 0)
                {
                    weights[i] = Math.Max(1, weights[i] >> 1);
                }
            }
        }
    }

    /// <summary>The count of lengths written (past the last nonzero one, at least <paramref name="minimum"/>).</summary>
    static int Trim(byte[] lengths, int minimum)
    {
        int count = lengths.Length;
        while (count > minimum && lengths[count - 1] == 0)
        {
            count--;
        }

        return count;
    }

    /// <summary>The canonical codes of the lengths (RFC 1951 3.2.2), bit-reversed for the LSB-first writer.</summary>
    static ushort[] CanonicalCodes(byte[] lengths)
    {
        var lengthCounts = new int[16];
        foreach (byte length in lengths)
        {
            lengthCounts[length]++;
        }

        lengthCounts[0] = 0;
        var nextCode = new int[16];
        int code = 0;
        for (int bits = 1; bits < 16; bits++)
        {
            code = (code + lengthCounts[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        var codes = new ushort[lengths.Length];
        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            if (length == 0)
            {
                continue;
            }

            int value = nextCode[length]++;
            int reversed = 0;
            for (int bit = 0; bit < length; bit++)
            {
                reversed = (reversed << 1) | ((value >> bit) & 1);
            }

            codes[i] = (ushort)reversed;
        }

        return codes;
    }

    /// <summary>The literal/length and distance lengths as code-length symbols: (extra &lt;&lt; 5) | symbol, with
    /// 16 (repeat the last 3-6 times), 17 (3-10 zeros) and 18 (11-138 zeros).</summary>
    static List<int> RunLengths(byte[] literalLengths, int literalCount, byte[] distanceLengths, int distanceCount)
    {
        var all = new byte[literalCount + distanceCount];
        literalLengths.AsSpan(0, literalCount).CopyTo(all);
        distanceLengths.AsSpan(0, distanceCount).CopyTo(all.AsSpan(literalCount));
        var runs = new List<int>();
        int i = 0;
        while (i < all.Length)
        {
            byte length = all[i];
            int run = 1;
            while (i + run < all.Length && all[i + run] == length)
            {
                run++;
            }

            if (length == 0 && run >= 3)
            {
                int count = Math.Min(run, 138);
                runs.Add(count >= 11 ? ((count - 11) << 5) | 18 : ((count - 3) << 5) | 17);
                i += count;
                continue;
            }

            if (length != 0 && run >= 4)
            {
                runs.Add(length);
                int count = Math.Min(run - 1, 6);
                runs.Add(((count - 3) << 5) | 16);
                i += count + 1;
                continue;
            }

            runs.Add(length);
            i++;
        }

        return runs;
    }

    static byte[] MakeFixedLiteralLengths()
    {
        var lengths = new byte[288];
        for (int i = 0; i < 288; i++)
        {
            lengths[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
        }

        return lengths[..LiteralLengthSymbols];
    }

    static byte[] MakeFixedDistanceLengths()
    {
        var lengths = new byte[DistanceSymbols];
        Array.Fill(lengths, (byte)5);
        return lengths;
    }

    // ------------------------------------------------------------------ save states

    public DeflateBitEncoder Clone()
    {
        var copy = (DeflateBitEncoder)MemberwiseClone();
        copy._history = (byte[])_history.Clone();
        return copy;
    }

    public void SaveState(SnapshotWriter writer)
    {
        writer.Write(_history.AsSpan(0, _historyLength).ToArray());
        writer.Write(_blockStart);
        writer.Write(_bitBuffer);
        writer.Write(_bitCount);
        writer.Write(_level);
        writer.Write(_strategy);
        writer.Write(_goodLength);
        writer.Write(_maxLazy);
        writer.Write(_niceLength);
        writer.Write(_maxChain);
        writer.Write(DataType);
    }

    public void LoadState(SnapshotReader reader)
    {
        byte[] history = reader.ReadBytes() ?? [];
        _history = new byte[WindowSize + BlockInput];
        history.CopyTo(_history, 0);
        _historyLength = history.Length;
        _blockStart = reader.ReadInt32();
        _bitBuffer = reader.ReadUInt16();
        _bitCount = reader.ReadInt32();
        _level = reader.ReadInt32();
        _strategy = reader.ReadInt32();
        _goodLength = reader.ReadInt32();
        _maxLazy = reader.ReadInt32();
        _niceLength = reader.ReadInt32();
        _maxChain = reader.ReadInt32();
        DataType = reader.ReadInt32();
    }
}
