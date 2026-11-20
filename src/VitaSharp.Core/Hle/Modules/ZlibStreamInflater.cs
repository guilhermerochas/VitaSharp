namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// A resumable inflater (RFC 1950 zlib, RFC 1951 deflate, RFC 1952 gzip) for the kernel's zlib (SceZlibForDriver):
/// input arrives in pieces, decoding stops where the input runs out and goes on when more comes, as zlib's inflate
/// does. Each step is a transaction: a block header with its code tables, a stored block's header, one literal or
/// one length/distance pair, a wrapper header or trailer is decoded whole or not at all, so running out of input never
/// leaves a half-read symbol. The output keeps the last 32 KB as the window; what the caller has not taken yet stays
/// pending.
/// </summary>
internal sealed class ZlibStreamInflater
{
    public enum Wrapper
    {
        Raw,
        Zlib,
        Gzip,
        Auto,
    }

    public enum Status
    {
        /// <summary>All the input was used and the stream has not ended.</summary>
        NeedInput,

        /// <summary>The stream (and its trailer) ended.</summary>
        Done,

        /// <summary>A zlib header asks for a preset dictionary (its adler32 in <see cref="DictionaryId"/>).</summary>
        NeedDictionary,

        /// <summary>The data is not a valid stream.</summary>
        BadData,
    }

    const int MaxBits = 15;
    const int WindowSize = 32768;

    static ReadOnlySpan<ushort> LengthBase => [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    static ReadOnlySpan<byte> LengthExtra => [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    static ReadOnlySpan<ushort> DistanceBase => [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    static ReadOnlySpan<byte> DistanceExtra => [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    static ReadOnlySpan<byte> CodeLengthOrder => [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    static readonly uint[] CrcTable = MakeCrcTable();

    enum Mode
    {
        Header,
        BlockHeader,
        Stored,
        Codes,
        Trailer,
        End,
        Bad,
    }

    /// <summary>A canonical Huffman code: the count of codes of each length and the symbols in code order.</summary>
    sealed class Huffman(int symbols)
    {
        public readonly short[] Counts = new short[MaxBits + 1];
        public readonly short[] Symbols = new short[symbols];
    }

    Wrapper _wrapper;
    Mode _mode = Mode.Header;
    byte[] _input = new byte[256];
    int _inputLength;
    int _inputPosition;
    ulong _bitBuffer;
    int _bitCount;
    bool _lastBlock;
    uint _storedLeft;
    Huffman? _lengths;
    Huffman? _distances;
    byte[] _output = new byte[WindowSize * 2];
    int _outputLength;
    int _delivered;
    uint _adler = 1;
    uint _crc;
    uint _outputSize;

    public ZlibStreamInflater(Wrapper wrapper)
    {
        _wrapper = wrapper;
        _mode = wrapper == Wrapper.Raw ? Mode.BlockHeader : Mode.Header;
    }

    /// <summary>The adler32 of a zlib header's preset dictionary.</summary>
    public uint DictionaryId { get; private set; }

    /// <summary>The checksum of the output so far: adler32 (zlib, raw) or crc32 (gzip).</summary>
    public uint Checksum => _wrapper == Wrapper.Gzip ? _crc : _adler;

    /// <summary>The output not taken yet.</summary>
    public int Pending => _outputLength - _delivered;

    /// <summary>The input bytes not used (left over after the end, or not reached yet).</summary>
    public int UnusedInput => _inputLength - (_inputPosition - _bitCount / 8);

    /// <summary>True between two blocks of the deflate data (a point inflateSync can resume at).</summary>
    public bool AtBlockBoundary => _mode == Mode.BlockHeader;

    public bool Ended => _mode == Mode.End;

    /// <summary>A copy with the same state (inflateCopy).</summary>
    public ZlibStreamInflater Clone()
    {
        var copy = (ZlibStreamInflater)MemberwiseClone();
        copy._input = (byte[])_input.Clone();
        copy._output = (byte[])_output.Clone();
        return copy;
    }

    /// <summary>Adds input (the bytes already used are dropped first).</summary>
    public void AddInput(ReadOnlySpan<byte> data)
    {
        int used = _inputPosition - _bitCount / 8;
        if (used > 0)
        {
            Array.Copy(_input, used, _input, 0, _inputLength - used);
            _inputLength -= used;
            _inputPosition -= used;
        }

        if (_inputLength + data.Length > _input.Length)
        {
            Array.Resize(ref _input, Math.Max(_input.Length * 2, _inputLength + data.Length));
        }

        data.CopyTo(_input.AsSpan(_inputLength));
        _inputLength += data.Length;
    }

    /// <summary>Gives the pending output (as much as <paramref name="destination"/> holds); the bytes given.</summary>
    public int TakeOutput(Span<byte> destination)
    {
        int count = Math.Min(destination.Length, Pending);
        _output.AsSpan(_delivered, count).CopyTo(destination);
        _delivered += count;
        return count;
    }

    /// <summary>A preset dictionary: the window starts with it (it is not output and not in the checksum). For a zlib
    /// stream it must match the header's dictionary id (false otherwise).</summary>
    public bool SetDictionary(ReadOnlySpan<byte> dictionary)
    {
        if (_wrapper == Wrapper.Zlib && Adler32(1, dictionary) != DictionaryId)
        {
            return false;
        }

        ReadOnlySpan<byte> window = dictionary.Length > WindowSize ? dictionary[^WindowSize..] : dictionary;
        EnsureOutputRoom(window.Length);
        window.CopyTo(_output.AsSpan(_outputLength));
        _outputLength += window.Length;
        _delivered = _outputLength;
        if (_mode == Mode.Header)
        {
            _mode = Mode.BlockHeader;
        }

        return true;
    }

    /// <summary>Skips input up to the next 00 00 FF FF (an empty stored block's length): true and back at a block
    /// boundary when found, else the input is used up and false (inflateSync).</summary>
    public bool Sync()
    {
        int start = _inputPosition - _bitCount / 8;
        _bitBuffer = 0;
        _bitCount = 0;
        for (int i = start; i + 4 <= _inputLength; i++)
        {
            if (_input[i] == 0 && _input[i + 1] == 0 && _input[i + 2] == 0xFF && _input[i + 3] == 0xFF)
            {
                _inputPosition = i + 4;
                _mode = Mode.BlockHeader;
                _lastBlock = false;
                return true;
            }
        }

        _inputPosition = Math.Max(start, _inputLength - 3);
        return false;
    }

    /// <summary>Decodes as far as the input goes.</summary>
    public Status Run()
    {
        while (true)
        {
            switch (_mode)
            {
                case Mode.Header:
                    {
                        Status? header = ReadHeader();
                        if (header is { } stop)
                        {
                            return stop;
                        }

                        break;
                    }

                case Mode.BlockHeader:
                    if (_lastBlock)
                    {
                        _mode = Mode.Trailer;
                        break;
                    }

                    if (!ReadBlockHeader())
                    {
                        return _mode == Mode.Bad ? Status.BadData : Status.NeedInput;
                    }

                    break;
                case Mode.Stored:
                    if (!CopyStored())
                    {
                        return Status.NeedInput;
                    }

                    break;
                case Mode.Codes:
                    {
                        int codes = DecodeCodes();
                        if (codes <= 0)
                        {
                            return codes < 0 ? Status.BadData : Status.NeedInput;
                        }

                        break;
                    }

                case Mode.Trailer:
                    {
                        Status? trailer = ReadTrailer();
                        if (trailer is { } stop)
                        {
                            return stop;
                        }

                        break;
                    }

                case Mode.End:
                    return Status.Done;
                default:
                    return Status.BadData;
            }
        }
    }

    // ------------------------------------------------------------------ bits

    (int Position, ulong Bits, int Count) Checkpoint() => (_inputPosition, _bitBuffer, _bitCount);

    void Restore((int Position, ulong Bits, int Count) checkpoint)
    {
        (_inputPosition, _bitBuffer, _bitCount) = checkpoint;
    }

    bool Need(int count)
    {
        while (_bitCount < count)
        {
            if (_inputPosition >= _inputLength)
            {
                return false;
            }

            _bitBuffer |= (ulong)_input[_inputPosition] << _bitCount;
            _inputPosition++;
            _bitCount += 8;
        }

        return true;
    }

    bool Bits(int count, out uint value)
    {
        if (!Need(count))
        {
            value = 0;
            return false;
        }

        value = (uint)(_bitBuffer & ((1UL << count) - 1));
        _bitBuffer >>= count;
        _bitCount -= count;
        return true;
    }

    /// <summary>Drops the bits up to the next byte boundary.</summary>
    void AlignToByte()
    {
        int drop = _bitCount % 8;
        _bitBuffer >>= drop;
        _bitCount -= drop;
    }

    bool Byte(out byte value)
    {
        bool ok = Bits(8, out uint bits);
        value = (byte)bits;
        return ok;
    }

    // ------------------------------------------------------------------ wrappers

    Status? ReadHeader()
    {
        var checkpoint = Checkpoint();
        if (!Need(16))
        {
            Restore(checkpoint);
            return Status.NeedInput;
        }

        if (_wrapper == Wrapper.Auto)
        {
            _wrapper = (_bitBuffer & 0xFFFF) == 0x8B1F ? Wrapper.Gzip : Wrapper.Zlib;
        }

        return _wrapper == Wrapper.Gzip ? ReadGzipHeader(checkpoint) : ReadZlibHeader(checkpoint);
    }

    Status? ReadZlibHeader((int Position, ulong Bits, int Count) checkpoint)
    {
        Byte(out byte cmf);
        Byte(out byte flags);
        if (((cmf << 8) | flags) % 31 != 0 || (cmf & 0xF) != 8 || (cmf >> 4) > 7)
        {
            _mode = Mode.Bad;
            return Status.BadData;
        }

        if ((flags & 0x20) != 0)
        {
            uint id = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!Byte(out byte part))
                {
                    Restore(checkpoint);
                    return Status.NeedInput;
                }

                id = (id << 8) | part;
            }

            DictionaryId = id;
            _mode = Mode.BlockHeader;
            return Status.NeedDictionary;
        }

        _mode = Mode.BlockHeader;
        return null;
    }

    Status? ReadGzipHeader((int Position, ulong Bits, int Count) checkpoint)
    {
        Byte(out byte id1);
        Byte(out byte id2);
        if (id1 != 0x1F || id2 != 0x8B)
        {
            _mode = Mode.Bad;
            return Status.BadData;
        }

        if (!Byte(out byte method) || !Byte(out byte flags))
        {
            Restore(checkpoint);
            return Status.NeedInput;
        }

        if (method != 8)
        {
            _mode = Mode.Bad;
            return Status.BadData;
        }

        // MTIME, XFL, OS
        for (int i = 0; i < 6; i++)
        {
            if (!Byte(out _))
            {
                Restore(checkpoint);
                return Status.NeedInput;
            }
        }

        if ((flags & 4) != 0)
        {
            if (!Bits(16, out uint extra))
            {
                Restore(checkpoint);
                return Status.NeedInput;
            }

            for (uint i = 0; i < extra; i++)
            {
                if (!Byte(out _))
                {
                    Restore(checkpoint);
                    return Status.NeedInput;
                }
            }
        }

        foreach (int text in (ReadOnlySpan<int>)[8, 0x10])
        {
            if ((flags & text) == 0)
            {
                continue;
            }

            byte character;
            do
            {
                if (!Byte(out character))
                {
                    Restore(checkpoint);
                    return Status.NeedInput;
                }
            }
            while (character != 0);
        }

        if ((flags & 2) != 0 && !Bits(16, out _))
        {
            Restore(checkpoint);
            return Status.NeedInput;
        }

        _mode = Mode.BlockHeader;
        return null;
    }

    Status? ReadTrailer()
    {
        AlignToByte();
        if (_wrapper == Wrapper.Raw)
        {
            _mode = Mode.End;
            return null;
        }

        var checkpoint = Checkpoint();
        int size = _wrapper == Wrapper.Gzip ? 8 : 4;
        Span<byte> trailer = stackalloc byte[8];
        for (int i = 0; i < size; i++)
        {
            if (!Byte(out trailer[i]))
            {
                Restore(checkpoint);
                return Status.NeedInput;
            }
        }

        bool valid = _wrapper == Wrapper.Gzip
            ? BitConverter.ToUInt32(trailer[..4]) == _crc && BitConverter.ToUInt32(trailer[4..8]) == _outputSize
            : ((uint)trailer[0] << 24 | (uint)trailer[1] << 16 | (uint)trailer[2] << 8 | trailer[3]) == _adler;
        if (!valid)
        {
            _mode = Mode.Bad;
            return Status.BadData;
        }

        _mode = Mode.End;
        return null;
    }

    // ------------------------------------------------------------------ deflate blocks

    bool ReadBlockHeader()
    {
        var checkpoint = Checkpoint();
        if (!Bits(1, out uint last) || !Bits(2, out uint type))
        {
            Restore(checkpoint);
            return false;
        }

        switch (type)
        {
            case 0:
                AlignToByte();
                if (!Bits(16, out uint length) || !Bits(16, out uint complement))
                {
                    Restore(checkpoint);
                    return false;
                }

                if ((length ^ complement) != 0xFFFF)
                {
                    _mode = Mode.Bad;
                    return false;
                }

                _storedLeft = length;
                _mode = Mode.Stored;
                break;
            case 1:
                (_lengths, _distances) = FixedCodes();
                _mode = Mode.Codes;
                break;
            case 2:
                {
                    int tables = ReadDynamicTables();
                    if (tables <= 0)
                    {
                        Restore(checkpoint);
                        if (tables < 0)
                        {
                            _mode = Mode.Bad;
                        }

                        return false;
                    }

                    _mode = Mode.Codes;
                    break;
                }

            default:
                _mode = Mode.Bad;
                return false;
        }

        _lastBlock = last != 0;
        return true;
    }

    bool CopyStored()
    {
        while (_storedLeft > 0)
        {
            if (!Byte(out byte value))
            {
                return false;
            }

            Emit(value);
            _storedLeft--;
        }

        _mode = Mode.BlockHeader;
        return true;
    }

    /// <summary>1 at the end of the block, 0 when the input ran out, -1 for bad data.</summary>
    int DecodeCodes()
    {
        while (true)
        {
            var checkpoint = Checkpoint();
            int symbol = Decode(_lengths!);
            if (symbol == -1)
            {
                Restore(checkpoint);
                return 0;
            }

            if (symbol < 0 || symbol > 285)
            {
                _mode = Mode.Bad;
                return -1;
            }

            if (symbol < 256)
            {
                Emit((byte)symbol);
                continue;
            }

            if (symbol == 256)
            {
                _mode = Mode.BlockHeader;
                return 1;
            }

            symbol -= 257;
            if (!Bits(LengthExtra[symbol], out uint lengthExtra))
            {
                Restore(checkpoint);
                return 0;
            }

            int length = LengthBase[symbol] + (int)lengthExtra;
            int distanceSymbol = Decode(_distances!);
            if (distanceSymbol == -1)
            {
                Restore(checkpoint);
                return 0;
            }

            if (distanceSymbol < 0 || distanceSymbol > 29 || !Bits(DistanceExtra[distanceSymbol], out uint distanceExtra))
            {
                if (distanceSymbol is >= 0 and <= 29)
                {
                    Restore(checkpoint);
                    return 0;
                }

                _mode = Mode.Bad;
                return -1;
            }

            int distance = DistanceBase[distanceSymbol] + (int)distanceExtra;
            if (distance > _outputLength)
            {
                _mode = Mode.Bad;
                return -1;
            }

            EnsureOutputRoom(length);
            for (int i = 0; i < length; i++)
            {
                Emit(_output[_outputLength - distance]);
            }
        }
    }

    /// <summary>The next symbol of the code; -1 when the input runs out, -2 for an incomplete code.</summary>
    int Decode(Huffman code)
    {
        int value = 0;
        int first = 0;
        int index = 0;
        for (int length = 1; length <= MaxBits; length++)
        {
            if (!Bits(1, out uint bit))
            {
                return -1;
            }

            value |= (int)bit;
            int count = code.Counts[length];
            if (value - count < first)
            {
                return code.Symbols[index + (value - first)];
            }

            index += count;
            first += count;
            first <<= 1;
            value <<= 1;
        }

        return -2;
    }

    /// <summary>1 when the tables were read, 0 when the input ran out, -1 for bad tables.</summary>
    int ReadDynamicTables()
    {
        if (!Bits(5, out uint literalCount) || !Bits(5, out uint distanceCount) || !Bits(4, out uint codeCount))
        {
            return 0;
        }

        int literals = (int)literalCount + 257;
        int distances = (int)distanceCount + 1;
        if (literals > 286 || distances > 30)
        {
            return -1;
        }

        Span<short> lengths = stackalloc short[320];
        lengths.Clear();
        for (int i = 0; i < codeCount + 4; i++)
        {
            if (!Bits(3, out uint codeLength))
            {
                return 0;
            }

            lengths[CodeLengthOrder[i]] = (short)codeLength;
        }

        var lengthCode = new Huffman(19);
        if (Build(lengthCode, lengths[..19]) != 0)
        {
            return -1;
        }

        int filled = 0;
        while (filled < literals + distances)
        {
            int symbol = Decode(lengthCode);
            if (symbol == -1)
            {
                return 0;
            }

            if (symbol < 0)
            {
                return -1;
            }

            if (symbol < 16)
            {
                lengths[filled++] = (short)symbol;
                continue;
            }

            short repeated = 0;
            uint repeat;
            if (symbol == 16)
            {
                if (filled == 0)
                {
                    return -1;
                }

                repeated = lengths[filled - 1];
                if (!Bits(2, out repeat))
                {
                    return 0;
                }

                repeat += 3;
            }
            else if (symbol == 17)
            {
                if (!Bits(3, out repeat))
                {
                    return 0;
                }

                repeat += 3;
            }
            else
            {
                if (!Bits(7, out repeat))
                {
                    return 0;
                }

                repeat += 11;
            }

            if (filled + repeat > literals + distances)
            {
                return -1;
            }

            for (uint i = 0; i < repeat; i++)
            {
                lengths[filled++] = repeated;
            }
        }

        if (lengths[256] == 0)
        {
            return -1;
        }

        var lengthTable = new Huffman(286);
        var distanceTable = new Huffman(30);
        int built = Build(lengthTable, lengths[..literals]);
        if (built < 0 || (built > 0 && literals - lengthTable.Counts[0] != 1))
        {
            return -1;
        }

        built = Build(distanceTable, lengths.Slice(literals, distances));
        if (built < 0 || (built > 0 && distances - distanceTable.Counts[0] != 1))
        {
            return -1;
        }

        _lengths = lengthTable;
        _distances = distanceTable;
        return 1;
    }

    /// <summary>Builds a canonical code; 0 complete, above 0 incomplete, below 0 over-subscribed.</summary>
    static int Build(Huffman code, ReadOnlySpan<short> lengths)
    {
        Array.Clear(code.Counts);
        foreach (short length in lengths)
        {
            code.Counts[length]++;
        }

        if (code.Counts[0] == lengths.Length)
        {
            return 0;
        }

        int left = 1;
        for (int length = 1; length <= MaxBits; length++)
        {
            left <<= 1;
            left -= code.Counts[length];
            if (left < 0)
            {
                return left;
            }
        }

        Span<short> offsets = stackalloc short[MaxBits + 1];
        offsets[1] = 0;
        for (int length = 1; length < MaxBits; length++)
        {
            offsets[length + 1] = (short)(offsets[length] + code.Counts[length]);
        }

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
            {
                code.Symbols[offsets[lengths[symbol]]++] = (short)symbol;
            }
        }

        return left;
    }

    static (Huffman Lengths, Huffman Distances) FixedCodes()
    {
        Span<short> lengths = stackalloc short[288];
        for (int symbol = 0; symbol < 288; symbol++)
        {
            lengths[symbol] = (short)(symbol < 144 ? 8 : symbol < 256 ? 9 : symbol < 280 ? 7 : 8);
        }

        var lengthTable = new Huffman(288);
        Build(lengthTable, lengths);
        Span<short> distanceLengths = stackalloc short[30];
        distanceLengths.Fill(5);
        var distanceTable = new Huffman(30);
        Build(distanceTable, distanceLengths);
        return (lengthTable, distanceTable);
    }

    // ------------------------------------------------------------------ output

    void EnsureOutputRoom(int count)
    {
        if (_outputLength + count <= _output.Length)
        {
            return;
        }

        // keep the window before the first byte not taken yet
        int keepFrom = Math.Max(0, Math.Min(_delivered, _outputLength - WindowSize));
        if (keepFrom > 0)
        {
            Array.Copy(_output, keepFrom, _output, 0, _outputLength - keepFrom);
            _outputLength -= keepFrom;
            _delivered -= keepFrom;
        }

        if (_outputLength + count > _output.Length)
        {
            Array.Resize(ref _output, Math.Max(_output.Length * 2, _outputLength + count));
        }
    }

    void Emit(byte value)
    {
        EnsureOutputRoom(1);
        _output[_outputLength++] = value;
        _outputSize++;
        uint low = ((_adler & 0xFFFF) + value) % 65521;
        uint high = ((_adler >> 16) + low) % 65521;
        _adler = (high << 16) | low;
        _crc = ~(CrcTable[(~_crc ^ value) & 0xFF] ^ (~_crc >> 8));
    }

    // ------------------------------------------------------------------ checksums

    public static uint Adler32(uint adler, ReadOnlySpan<byte> data)
    {
        uint a = adler & 0xFFFF;
        uint b = adler >> 16;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    public static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
