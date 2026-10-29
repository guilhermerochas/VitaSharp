using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// sce::Json internals: a transliteration of the FW 3.60 libSceJson.suprx, working on guest memory with the same
/// layouts and the same sequence of allocator calls, so games that look at the objects directly (or count on where
/// the allocator puts them) see what the firmware module would leave there.
///
/// Layouts (32-bit guest, all reached through the game's MemAllocator):
/// <list type="bullet">
/// <item>Value, 0x18 bytes: +0 parent Value* (set by the parser and the serializer), +4 root info* (0xC block:
/// null-access callback, its user data, a null Value*), +8 payload (bool byte, int64, uint64, double, or a String*,
/// Array*, Object* allocated with 4 bytes), +0x14 ValueType (0 null, 1 boolean, 2 integer, 3 uinteger, 4 real,
/// 5 string, 6 array, 7 object).</item>
/// <item>String, 4 bytes: rep* (0xC block: +0 char* data, +4 length; data is a 1-byte empty literal in the module's
/// data when empty, else its own length+1 block).</item>
/// <item>Array, 4 bytes: impl* (0xC block: +0 sentinel node*, +4 count). List nodes are 0x20 bytes: +0 prev, +4 next,
/// +8 Value.</item>
/// <item>Object, 4 bytes: impl* (0xC block: +0 head node*, +4 count): a red-black tree (red = 1). Nodes are 0x48
/// bytes: +0 parent, +4 left, +8 right, +0x10 key rep {data, length} inline, +0x20 Pair {String key, 4 pad bytes,
/// Value}, +0x40 colour, +0x41 is-nil. The head is a nil node: parent = root, left = leftmost, right = rightmost.</item>
/// <item>Array/Object iterators, 4 bytes: a pointer to a 4-byte heap cell that holds the node pointer.</item>
/// </list>
/// The module's data block mirrors libSceJson's .data (offsets below); the empty-string literal sits at +0x39 there.
/// </summary>
public sealed unsafe partial class Json
{
    // error codes (values from the firmware module; names follow the PS4 SDK's libSceJson)
    internal const int ERROR_PARSE_INVALID_CHAR = unchecked((int)0x80920101), ERROR_NOMEM = unchecked((int)0x80920102),
        ERROR_NOFILE = unchecked((int)0x80920103), ERROR_NOROOT = unchecked((int)0x80920104),
        ERROR_NOBUF = unchecked((int)0x80920105), ERROR_NOINIT = unchecked((int)0x80920110),
        ERROR_MULTIPLEINIT = unchecked((int)0x80920111);

    const int TNull = 0, TBool = 1, TInt = 2, TUInt = 3, TReal = 4, TString = 5, TArray = 6, TObject = 7;

    // module data (same offsets as libSceJson's .data)
    internal const uint DString = 0x00, DArray = 0x04, DObject = 0x08, DParseCount = 0x0C, DAbort = 0x10, DMutex = 0x14,
        DInfo = 0x18, DNullValue = 0x1C, DInt0 = 0x20, DUInt0 = 0x28, DReal0 = 0x30, DBool0 = 0x38, DEmpty = 0x39;
    // HLE-only: the c_str() of a String without rep (rodata in the module), the lwmutex name, MemAllocator's vtable
    // (offset-to-top, typeinfo, then the 5 slots at the vptr) and the guest stubs the library code calls
    internal const uint DLit = 0x48, DMutexName = 0x50, DVtable = 0x70, DVptr = 0x78, DStubs = 0xA0, DataSize = 0x200;

    enum Stub { AllocDtor, AllocDeletingDtor, PureVirtual, NotifyError, CreateLwMutex, DeleteLwMutex, LockLwMutex, UnlockLwMutex, IoOpen, IoRead, IoClose }

    static readonly (uint Nid, uint LibNid, string Lib)[] StubTargets =
    [
        (0x1666CAFA, 0x0854B69C, "SceLibJson"),   // MemAllocator::~MemAllocator() [D1]
        (0x71AA3947, 0x0854B69C, "SceLibJson"),   // MemAllocator::~MemAllocator() [D0]
        (0x2F2C6046, 0xCAE9ACE6, "SceLibKernel"), // sceClibAbort (allocate/deallocate are pure virtual)
        (0x38C51D0C, 0x0854B69C, "SceLibJson"),   // MemAllocator::notifyError
        (0xDA6EC8EF, 0xCAE9ACE6, "SceLibKernel"), // sceKernelCreateLwMutex
        (0x244E76D2, 0xCAE9ACE6, "SceLibKernel"), // sceKernelDeleteLwMutex
        (0x46E7BE7B, 0xCAE9ACE6, "SceLibKernel"), // sceKernelLockLwMutex
        (0x91FA6614, 0xCAE9ACE6, "SceLibKernel"), // sceKernelUnlockLwMutex
        (0x6C60AC61, 0xCAE9ACE6, "SceLibKernel"), // sceIoOpen
        (0xFDB32293, 0xF2FF276E, "SceIofilemgr"), // sceIoRead
        (0xC70B8886, 0xF2FF276E, "SceIofilemgr"), // sceIoClose
    ];

    uint _data;
    readonly object _dataLock = new();

    /// <summary>The module data block, allocated on first use (not at process start: an ET_SCE_EXEC eboot needs the
    /// bottom of the user region).</summary>
    internal uint Data
    {
        get
        {
            uint d = Volatile.Read(ref _data);
            return d != 0 ? d : CreateData();
        }
    }

    uint CreateData()
    {
        lock (_dataLock)
        {
            if (_data != 0) return _data;
            uint d = Mem.Alloc(GuestMemory.PageSize, "SceLibJson (HLE)");
            GuestMemory.Fill(d, 0, GuestMemory.PageSize);
            GuestMemory.WriteCString(d + DMutexName, "SceJson_internal");
            for (int i = 0; i < StubTargets.Length; i++)
            {
                var (nid, libNid, lib) = StubTargets[i];
                Linker.WriteHleStub(d + DStubs + (uint)i * 16, Emu.Hle.GetHleFunctionIndex(nid, libNid, lib), nid);
            }
            uint vt = d + DVptr;
            W32(vt + 0, StubAddr(d, Stub.AllocDtor));
            W32(vt + 4, StubAddr(d, Stub.AllocDeletingDtor));
            W32(vt + 8, StubAddr(d, Stub.PureVirtual));
            W32(vt + 12, StubAddr(d, Stub.PureVirtual));
            W32(vt + 16, StubAddr(d, Stub.NotifyError));
            Emu.Code.Invalidate(d, GuestMemory.PageSize);
            Volatile.Write(ref _data, d);
            return d;
        }
    }

    static uint StubAddr(uint data, Stub s) => data + DStubs + (uint)s * 16;
    uint StubAddr(Stub s) => StubAddr(Data, s);

    uint Empty => Data + DEmpty;
    uint NullValue => R32(Data + DNullValue);

    // ------------------------------------------------------------------ guest memory and calls

    static uint R32(uint a) => GuestMemory.R32(a);
    static void W32(uint a, uint v) => GuestMemory.W32(a, v);
    static void W32(uint a, int v) => GuestMemory.W32(a, (uint)v);
    static byte R8(uint a) => GuestMemory.Read<byte>(a);
    static void W8(uint a, byte v) => GuestMemory.Write(a, v);
    static ulong R64(uint a) => GuestMemory.Read<ulong>(a);
    static void W64(uint a, ulong v) => GuestMemory.Write(a, v);
    static bool IsNil(uint node) => R8(node + 0x41) != 0;
    static bool Red(uint node) => R8(node + 0x40) != 0;

    /// <summary>Lowest guest stack address this module uses on the current thread (0: none). Temporaries the library
    /// keeps on its stack go below the caller's sp, and guest calls made meanwhile run below them.</summary>
    [ThreadStatic] static uint t_sp;

    static uint StackTop()
    {
        uint sp = GuestThread.Current!.Core.State->R[13] - 64;
        return t_sp != 0 && t_sp < sp ? t_sp : sp;
    }

    /// <summary>Reserves guest stack for a temporary; release by restoring <see cref="t_sp"/>.</summary>
    static uint Alloca(uint size) => t_sp = (StackTop() - size) & ~7u;

    static uint Call(uint fn, params ReadOnlySpan<uint> args) => GuestThread.Current!.CallGuestOnStack(fn, StackTop(), args);

    static uint CallVirt(uint obj, int slot, params ReadOnlySpan<uint> args)
    {
        Span<uint> a = stackalloc uint[args.Length + 1];
        a[0] = obj;
        args.CopyTo(a[1..]);
        return Call(R32(R32(obj) + (uint)slot * 4), a);
    }

    // ------------------------------------------------------------------ allocator (operator new / delete)

    /// <summary>Allocates through the game's MemAllocator (vtable slot 2); a failure calls notifyError (slot 4) and,
    /// while a parse or container insert is running, sets the abort flag that makes it return NOMEM.</summary>
    uint New(uint size)
    {
        uint d = Data;
        uint info = R32(d + DInfo);
        if (info == 0) return 0;
        uint alloc = R32(info);
        if (alloc == 0) return 0;
        uint p = CallVirt(alloc, 2, size, R32(info + 4));
        if (p == 0)
        {
            uint i2 = R32(d + DInfo);
            CallVirt(R32(i2), 4, unchecked((uint)ERROR_NOMEM), size, R32(i2 + 4));
            LockMutex();
            if (R32(d + DParseCount) != 0) W8(d + DAbort, 1);
            UnlockMutex();
        }
        return p;
    }

    void Delete(uint p)
    {
        uint info = R32(Data + DInfo);
        if (info == 0) return;
        uint alloc = R32(info);
        if (alloc == 0) return;
        CallVirt(alloc, 3, p, R32(info + 4));
    }

    void LockMutex() => Call(StubAddr(Stub.LockLwMutex), R32(Data + DMutex), 1, 0);
    void UnlockMutex() => Call(StubAddr(Stub.UnlockLwMutex), R32(Data + DMutex), 1);

    /// <summary>A parse or an insert begins: allocation failures from here on set the abort flag.</summary>
    void ParseEnter()
    {
        LockMutex();
        W32(Data + DParseCount, R32(Data + DParseCount) + 1);
        UnlockMutex();
    }

    /// <summary>Ends what <see cref="ParseEnter"/> began; true if an allocation failed meanwhile.</summary>
    bool ParseLeave()
    {
        uint d = Data;
        LockMutex();
        uint n = R32(d + DParseCount) - 1;
        W32(d + DParseCount, n);
        byte abort = R8(d + DAbort);
        if (n == 0) W8(d + DAbort, 0);
        UnlockMutex();
        return abort != 0;
    }

    bool Aborted => R8(Data + DAbort) != 0;

    // ------------------------------------------------------------------ C string helpers (the module's own)

    static uint StrLen(uint s)
    {
        if (s == 0) return 0;
        uint n = 0;
        while (R8(s + n) != 0) n++;
        return n;
    }

    static int Strcmp(uint a, uint b)
    {
        if (a == 0 || b == 0) return a == b ? 0 : a == 0 ? -1 : 1;
        for (uint i = 0; ; i++)
        {
            byte ca = R8(a + i), cb = R8(b + i);
            if (ca != cb) return ca - cb;
            if (ca == 0) return 0;
        }
    }

    /// <summary>strncmp with the module's signed count (a count above 2^31 compares nothing).</summary>
    static int Strncmp(uint a, uint b, uint count)
    {
        if (a == 0 || b == 0) return a == b ? 0 : a == 0 ? -1 : 1;
        int n = (int)count;
        uint i = 0;
        while (true)
        {
            n--;
            if (n < 0) return 0;
            byte cb = R8(b + i), ca = R8(a + i);
            if (ca != cb) return ca - cb;
            if (ca == 0) return 0;
            i++;
        }
    }

    /// <summary>strchr: the character is compared before the terminator, so 0 finds the terminator.</summary>
    static uint Strchr(uint p, sbyte c)
    {
        while (true)
        {
            sbyte x = (sbyte)R8(p);
            if (x == c) return p;
            if (x == 0) return 0;
            p++;
        }
    }

    // the module's ctype table (bit 2 digit, bit 3 space), indexed by c + 1
    static readonly byte[] CType = Convert.FromHexString(
        "0020202020202020202008080808082020202020202020202020202020202020200810101010101010101010101010101004040404040404" +
        "040404101010101010104141414141410101010101010101010101010101010101010110101010101042424242424202020202020202020202" +
        "020202020202020202021010101020");

    static int CTypeOf(sbyte c) => (uint)c > 0x7F ? 0 : CType[c + 1];

    /// <summary>The module's own strtod: signs, digits, '.', an e/E/d/D exponent applied by repeated *10 or /10.</summary>
    static double Strtod(uint s)
    {
        if (s == 0) return 0;
        bool seen = false;
        int exp = 0;
        while ((CTypeOf((sbyte)R8(s)) & 8) != 0) s++;
        double sign = 1.0;
        sbyte c = (sbyte)R8(s);
        while (c == '-' || c == '+')
        {
            if (c == '-') sign = -sign;
            c = (sbyte)R8(++s);
        }
        double v = 0;
        while ((CTypeOf((sbyte)R8(s)) & 4) != 0) { v = (R8(s) - '0') + v * 10.0; s++; seen = true; }
        if (R8(s) == '.')
        {
            double div = 10.0;
            s++;
            while ((CTypeOf((sbyte)R8(s)) & 4) != 0) { v += (R8(s) - '0') / div; div *= 10.0; s++; seen = true; }
        }
        if (!seen) return 0;
        c = (sbyte)R8(s);
        if (c is (sbyte)'d' or (sbyte)'D' or (sbyte)'e' or (sbyte)'E')
        {
            bool neg = false, digits = false;
            sbyte c1 = (sbyte)R8(s + 1);
            uint p = s + 1;
            if (c1 == '-') { neg = true; p = s + 2; }
            else if (c1 == '+') p = s + 2;
            while ((CTypeOf((sbyte)R8(p)) & 4) != 0) { exp = unchecked((R8(p) - '0') + exp * 10); p++; digits = true; }
            if (!digits) return 0;
            // the module loops exp times; stop once the value can no longer change (0 or infinity)
            for (uint k = (uint)exp; k != 0 && v != 0 && !double.IsInfinity(v); k--) v = neg ? v / 10.0 : v * 10.0;
        }
        return v * sign;
    }

    // ------------------------------------------------------------------ String rep
    //   rep (0xC block): +0 data, +4 length

    /// <summary>rep = copy of [src, src+len): empty when len is 0 or src is null/empty; the old data is freed.</summary>
    void RepAssign(uint rep, uint src, uint len)
    {
        uint data = R32(rep);
        if (data == src) return;
        if (data != Empty) Delete(data);
        if (len != 0 && src != 0 && R8(src) != 0)
        {
            uint p = New(len + 1);
            if (p != 0)
            {
                W32(rep, p);
                W32(rep + 4, len);
                GuestMemory.Copy(p, src, len);
                W8(p + len, 0);
                return;
            }
        }
        W32(rep, Empty);
        W32(rep + 4, 0);
    }

    void RepCopy(uint dst, uint src)
    {
        if (src == dst) return;
        if (R32(dst) != Empty) Delete(R32(dst));
        uint n = R32(src + 4);
        if (n != 0)
        {
            uint p = New(n + 1);
            if (p != 0)
            {
                W32(dst, p);
                uint len = R32(src + 4);
                W32(dst + 4, len);
                GuestMemory.Copy(p, R32(src), len + 1);
                return;
            }
        }
        W32(dst, Empty);
        W32(dst + 4, 0);
    }

    /// <summary>A new empty rep (0 when the allocation fails).</summary>
    uint RepNew()
    {
        uint p = New(0xC);
        if (p == 0) return 0;
        W32(p, Empty);
        W32(p + 4, 0);
        RepAssign(p, 0, 0);
        return p;
    }

    /// <summary>Frees a heap rep the way ~String does (data, then the -1 poison, then the rep).</summary>
    void RepFree(uint rep)
    {
        if (R32(rep) != Empty) Delete(R32(rep));
        W32(rep, uint.MaxValue);
        W32(rep + 4, uint.MaxValue);
        Delete(rep);
    }

    /// <summary>Releases an inline rep (stack temporary or a tree node's key copy).</summary>
    void RepRelease(uint rep)
    {
        if (R32(rep) != Empty) Delete(R32(rep));
        W32(rep, uint.MaxValue);
        W32(rep + 4, uint.MaxValue);
    }

    /// <summary>erase(pos, count) on a rep (String::resize shrinking).</summary>
    void RepErase(uint rep, uint pos, uint count)
    {
        uint size = R32(rep + 4);
        if (pos > size) { OutOfRange("erase"); size = R32(rep + 4); }
        uint avail = size - pos;
        if (count > avail) count = avail;
        uint p = New(size - count + 1);
        if (p == 0) return;
        uint len = R32(rep + 4);
        uint data = R32(rep);
        GuestMemory.Copy(p, data, pos);
        GuestMemory.Copy(p + pos, data + pos + count, len - pos - count);
        W8(p + len - count, 0);
        if (R32(rep) != Empty) Delete(R32(rep));
        W32(rep, p);
        W32(rep + 4, len - count);
    }

    /// <summary>The module asserts (BKPT) on an out-of-range position; this HLE logs and carries on like a debugger
    /// continuing past it.</summary>
    static void OutOfRange(string what) => Log.WarnOnce("json-range-" + what, $"sce::Json::String::{what}: position out of range (the firmware module would hit a BKPT here)");

    uint RFind(uint rep, uint s, uint pos, uint n)
    {
        uint size = R32(rep + 4);
        if (n == 0) return pos < size ? pos : size;
        if (n > size) return uint.MaxValue;
        uint data = R32(rep);
        uint idx = Math.Min(pos, size - n);
        uint p = data + idx;
        while (true)
        {
            if ((sbyte)R8(p) == (sbyte)R8(s) && Strncmp(p, s, n) == 0) return p - R32(rep);
            if (p == R32(rep)) return uint.MaxValue;
            p--;
        }
    }

    /// <summary>The scanning loop shared by the String::find overloads: needle s of n bytes from pos.</summary>
    static uint FindIn(uint rep, uint s, uint n, uint pos)
    {
        uint size = R32(rep + 4);
        if (n == 0 && pos <= size) return pos;
        uint remaining = size - pos;
        if (pos >= size || n > remaining) return uint.MaxValue;
        uint p = R32(rep) + pos;
        while (remaining != 0)
        {
            uint q = Strchr(p, (sbyte)R8(s));
            if (q == 0) return uint.MaxValue;
            if (Strncmp(q, s, n) == 0) return q - R32(rep);
            remaining -= q - p + 1;
            p = q + 1;
        }
        return uint.MaxValue;
    }

    // ------------------------------------------------------------------ String (4 bytes: rep*)

    uint StringCtor(uint self)
    {
        W32(self, 0);
        uint rep = New(0xC);
        if (rep == 0) return self;
        W32(rep, Empty);
        W32(rep + 4, 0);
        W32(self, rep);
        RepAssign(rep, 0, 0);
        return self;
    }

    uint StringCtorCStr(uint self, uint s)
    {
        StringCtor(self);
        uint rep = R32(self);
        if (rep == 0) return self;
        RepAssign(rep, s, s != 0 ? StrLen(s) : 0);
        return self;
    }

    uint StringCtorCopy(uint self, uint other)
    {
        W32(self, 0);
        if (R32(other) == 0) return self;
        StringCtor(self);
        if (R32(self) != 0) RepCopy(R32(self), R32(other));
        return self;
    }

    uint StringDtor(uint self)
    {
        uint rep = R32(self);
        if (rep != 0)
        {
            RepFree(rep);
            W32(self, 0);
        }
        return self;
    }

    /// <summary>Appends n bytes from guest memory (append and operator+= all reallocate to the exact size).</summary>
    uint AppendGuest(uint self, uint src, uint n)
    {
        uint rep = R32(self);
        if (rep == 0) return self;
        uint p = New(R32(rep + 4) + n + 1);
        if (p == 0) return self;
        uint size = R32(rep + 4);
        GuestMemory.Copy(p, R32(rep), size);
        GuestMemory.Copy(p + size, src, n);
        W8(p + size + n, 0);
        if (R32(rep) != Empty) Delete(R32(rep));
        W32(rep, p);
        W32(rep + 4, size + n);
        return self;
    }

    /// <summary>Appends host bytes (the module's literals and snprintf output).</summary>
    uint AppendHost(uint self, ReadOnlySpan<byte> s)
    {
        uint rep = R32(self);
        if (rep == 0) return self;
        uint n = (uint)s.Length;
        uint p = New(R32(rep + 4) + n + 1);
        if (p == 0) return self;
        uint size = R32(rep + 4);
        GuestMemory.Copy(p, R32(rep), size);
        s.CopyTo(GuestMemory.Span(p + size, (int)n));
        W8(p + size + n, 0);
        if (R32(rep) != Empty) Delete(R32(rep));
        W32(rep, p);
        W32(rep + 4, size + n);
        return self;
    }

    uint AppendAscii(uint self, string s)
    {
        Span<byte> b = stackalloc byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return AppendHost(self, b);
    }

    /// <summary>operator+=(unsigned char): the length is strlen of {c, 0}, so appending 0 only reallocates.</summary>
    uint AppendChar(uint self, byte c)
    {
        Span<byte> b = [c];
        return AppendHost(self, c == 0 ? [] : b);
    }

    uint AppendString(uint self, uint other)
    {
        if (R32(self) == 0) return self;
        uint s = R32(other) != 0 ? R32(R32(other)) : Data + DLit;
        return AppendGuest(self, s, StrLen(s));
    }

    uint AppendCStr(uint self, uint s)
    {
        if (R32(self) == 0) return self;
        return AppendGuest(self, s, StrLen(s));
    }

    static uint StringSize(uint self) => R32(self) == 0 ? 0 : R32(R32(self) + 4);

    byte StringAt(uint self, uint i)
    {
        uint rep = R32(self);
        if (rep == 0) return 0;
        if (i >= R32(rep + 4)) OutOfRange("at");
        return R8(R32(rep) + i);
    }

    uint StringAssign(uint self, uint other)
    {
        if (R32(self) == 0 || R32(other) == 0) return self;
        RepCopy(R32(self), R32(other));
        return self;
    }

    // ------------------------------------------------------------------ Value (0x18 bytes)

    static void ValueInit(uint v)
    {
        W32(v, 0);
        W32(v + 4, 0);
        W32(v + 0x14, 0);
    }

    /// <summary>Value::set(ValueType): frees the old String/Array/Object and creates an empty one of the new type
    /// (a failed allocation leaves the value null).</summary>
    void SetType(uint v, int t)
    {
        int cur = (int)R32(v + 0x14);
        if (cur == t) return;
        uint p;
        switch (cur)
        {
            case TString:
                if ((p = R32(v + 8)) != 0) { StringDtor(p); Delete(p); W32(v + 8, 0); }
                break;
            case TArray:
                if ((p = R32(v + 8)) != 0) { ArrayDtor(p); Delete(p); W32(v + 8, 0); }
                break;
            case TObject:
                if ((p = R32(v + 8)) != 0) { ObjectDtor(p); Delete(p); W32(v + 8, 0); }
                break;
        }
        switch (t)
        {
            case TBool: W8(v + 8, 0); break;
            case TInt: case TUInt: case TReal: W64(v + 8, 0); break;
            case TString: case TArray: case TObject:
            {
                uint obj = New(4);
                if (obj != 0)
                {
                    if (t == TString) StringCtor(obj);
                    else if (t == TArray) ArrayCtor(obj);
                    else ObjectCtor(obj);
                }
                W32(v + 8, obj);
                if (obj != 0 && R32(obj) == 0)
                {
                    if (t == TString) StringDtor(obj);
                    else if (t == TArray) ArrayDtor(obj);
                    else ObjectDtor(obj);
                    Delete(obj);
                    W32(v + 8, 0);
                    t = TNull;
                }
                else if (obj == 0) t = TNull;
                break;
            }
        }
        W32(v + 0x14, (uint)t);
    }

    /// <summary>~Value: the payload, then the root info and its null value (recursively).</summary>
    uint ValueDtor(uint v)
    {
        SetType(v, TNull);
        uint ri = R32(v + 4);
        if (ri != 0)
        {
            uint nv = R32(ri + 8);
            if (nv != 0)
            {
                ValueDtor(nv);
                Delete(nv);
                W32(ri + 8, 0);
            }
            Delete(ri);
            W32(v + 4, 0);
        }
        return v;
    }

    /// <summary>Value::set(const Value&amp;): copies type and payload only (parent and root info stay).</summary>
    void ValueSet(uint v, uint o)
    {
        int t = (int)R32(o + 0x14);
        switch (t)
        {
            case TNull: SetType(v, TNull); break;
            case TBool: { byte b = R8(o + 8); SetType(v, TBool); W8(v + 8, b); break; }
            case TInt: case TUInt: case TReal: { ulong x = R64(o + 8); SetType(v, t); W64(v + 8, x); break; }
            case TString:
            {
                uint s = R32(o + 8);
                if (s == 0 || R32(s) == 0) break;
                SetType(v, TString);
                if (R32(v + 8) != 0) StringAssign(R32(v + 8), s);
                break;
            }
            case TArray:
            {
                uint a = R32(o + 8);
                if (a == 0 || R32(a) == 0) break;
                SetType(v, TArray);
                if (R32(v + 8) != 0) ArrayAssign(R32(v + 8), a);
                break;
            }
            case TObject:
            {
                uint a = R32(o + 8);
                if (a == 0 || R32(a) == 0) break;
                SetType(v, TObject);
                if (R32(v + 8) != 0) ObjectAssign(R32(v + 8), a);
                break;
            }
        }
    }

    uint ValueCtorCopy(uint v, uint o)
    {
        ValueInit(v);
        ValueSet(v, o);
        return v;
    }

    uint ValueCtorType(uint v, int t)
    {
        ValueInit(v);
        SetType(v, t);
        return v;
    }

    void ValueSetString(uint v, uint s)
    {
        if (R32(s) == 0) return;
        SetType(v, TString);
        if (R32(v + 8) != 0) StringAssign(R32(v + 8), s);
    }

    void ValueSetArray(uint v, uint a)
    {
        if (R32(a) == 0) return;
        SetType(v, TArray);
        if (R32(v + 8) != 0) ArrayAssign(R32(v + 8), a);
    }

    void ValueSetObject(uint v, uint o)
    {
        if (R32(o) == 0) return;
        SetType(v, TObject);
        if (R32(v + 8) != 0) ObjectAssign(R32(v + 8), o);
    }

    /// <summary>The parser/serializer's parent link: a root (parent 0) gets a root info block with its own null
    /// value, and its null-access callback is cleared; a child loses its root info.</summary>
    void SetParent(uint v, uint parent)
    {
        W32(v, parent);
        uint ri = R32(v + 4);
        if (parent == 0)
        {
            if (ri == 0)
            {
                ri = New(0xC);
                if (ri != 0)
                {
                    W32(ri, 0);
                    W32(ri + 4, 0);
                    uint nv = New(0x18);
                    if (nv != 0)
                    {
                        ValueInit(nv);
                        SetType(nv, TNull);
                    }
                    W32(ri + 8, nv);
                }
                W32(v + 4, ri);
                if (ri == 0) return;
                if (R32(ri + 8) != 0) SetParent(R32(ri + 8), v);
                ri = R32(v + 4);
            }
            if (ri == 0) return;
            W32(ri + 4, 0);
            W32(R32(v + 4), 0);
        }
        else
        {
            if (ri == 0) return;
            uint nv = R32(ri + 8);
            if (nv != 0)
            {
                ValueDtor(nv);
                Delete(nv);
                W32(ri + 8, 0);
            }
            Delete(ri);
            W32(v + 4, 0);
        }
    }

    static uint RootOf(uint v)
    {
        while (R32(v) != 0) v = R32(v);
        return v;
    }

    /// <summary>The getters of a null value ask the root's null-access callback for a substitute
    /// (<c>const Value&amp; cb(ValueType wanted, const Value* parent, void* user)</c>); 0 = no callback.</summary>
    static uint NullAccess(uint v, int wanted)
    {
        uint ri = R32(RootOf(v) + 4);
        if (ri == 0 || R32(ri) == 0) return 0;
        return Call(R32(ri), (uint)wanted, R32(v), R32(ri + 4));
    }

    /// <summary>What getValue/[] return for a missing element: the root's null value, else the module's.</summary>
    uint MissingValue(uint v)
    {
        uint ri = R32(RootOf(v) + 4);
        if (ri != 0 && R32(ri + 8) != 0) return R32(ri + 8);
        return NullValue;
    }

    // ------------------------------------------------------------------ Array (4 bytes: impl*)
    //   impl: +0 sentinel, +4 count; node 0x20: +0 prev, +4 next, +8 Value

    /// <summary>Allocates an impl with its sentinel node (the sentinel may be missing if that allocation fails).</summary>
    uint ListImplNew()
    {
        uint impl = New(0xC);
        if (impl == 0) return 0;
        W32(impl + 4, 0);
        uint s = New(0x20);
        W32(impl, s);
        if (s != 0) { W32(s, s); W32(s + 4, s); }
        return impl;
    }

    /// <summary>Destroys every node and relinks the sentinel to itself.</summary>
    void ListClear(uint impl)
    {
        uint s = R32(impl);
        if (s == 0) return;
        uint n = R32(s + 4);
        while (n != s)
        {
            uint next = R32(n + 4);
            ValueDtor(n + 8);
            Delete(n);
            s = R32(impl);
            n = next;
        }
        W32(s, s);
        W32(s + 4, s);
        W32(impl + 4, 0);
    }

    /// <summary>Destroys every node and the sentinel itself.</summary>
    void ListDestroy(uint s)
    {
        uint n = R32(s + 4);
        while (n != s)
        {
            uint next = R32(n + 4);
            ValueDtor(n + 8);
            Delete(n);
            n = next;
        }
        Delete(s);
    }

    /// <summary>Copies the list of <paramref name="srcImpl"/> into the {sentinel, count} pair at
    /// <paramref name="outPair"/>; the copy stops at the first node that cannot be allocated.</summary>
    void ListCopy(uint outPair, uint srcImpl)
    {
        W32(outPair + 4, 0);
        uint s = New(0x20);
        W32(outPair, s);
        if (s == 0) return;
        uint last = s;
        uint srcSent = R32(srcImpl);
        uint n = srcSent != 0 ? R32(srcSent + 4) : 0;
        while (n != srcSent)
        {
            uint nn = New(0x20);
            if (nn == 0) break;
            ValueCtorCopy(nn + 8, n + 8);
            W32(nn, last);
            W32(outPair + 4, R32(outPair + 4) + 1);
            W32(last + 4, nn);
            last = nn;
            srcSent = R32(srcImpl);
            n = n != 0 ? R32(n + 4) : 0;
        }
        s = R32(outPair);
        W32(last + 4, s);
        W32(s, last);
    }

    uint ArrayCtor(uint self)
    {
        W32(self, 0);
        uint impl = ListImplNew();
        if (impl == 0) return self;
        W32(self, impl);
        ListClear(impl);
        return self;
    }

    uint ArrayCtorCopy(uint self, uint other)
    {
        W32(self, 0);
        if (R32(other) == 0) return self;
        uint impl = ListImplNew();
        if (impl == 0) return self;
        W32(self, impl);
        ListClear(impl);
        ArrayReplace(impl, R32(other));
        return self;
    }

    /// <summary>Copies the source list into a temporary, swaps it in and destroys the old list.</summary>
    void ArrayReplace(uint impl, uint srcImpl)
    {
        uint saved = t_sp;
        try
        {
            uint tmp = Alloca(8);
            ListCopy(tmp, srcImpl);
            uint a = R32(impl), b = R32(tmp);
            W32(tmp, a);
            W32(impl, b);
            a = R32(impl + 4);
            b = R32(tmp + 4);
            W32(tmp + 4, a);
            W32(impl + 4, b);
            if (R32(tmp) != 0) ListDestroy(R32(tmp));
        }
        finally { t_sp = saved; }
    }

    uint ArrayDtor(uint self)
    {
        uint impl = R32(self);
        if (impl == 0) return self;
        if (R32(impl) != 0) ListDestroy(R32(impl));
        W32(impl, uint.MaxValue);
        W32(impl + 4, uint.MaxValue);
        Delete(R32(self));
        W32(self, 0);
        return self;
    }

    uint ArrayAssign(uint self, uint other)
    {
        uint impl = R32(self);
        if (impl == 0 || R32(other) == 0) return self;
        ArrayReplace(impl, R32(other));
        return self;
    }

    /// <summary>Links a copy of <paramref name="value"/> before <paramref name="pos"/>; returns the node, or the
    /// sentinel when the node could not be allocated.</summary>
    uint ListInsert(uint impl, uint pos, uint value)
    {
        uint n = New(0x20);
        if (n != 0) ValueCtorCopy(n + 8, value);
        else n = R32(impl);
        if (n != R32(impl))
        {
            W32(n, R32(pos));
            W32(n + 4, pos);
            W32(R32(pos) + 4, n);
            W32(pos, n);
            W32(impl + 4, R32(impl + 4) + 1);
        }
        return n;
    }

    void ArrayPopBack(uint self)
    {
        uint impl = R32(self);
        if (impl == 0 || R32(impl + 4) == 0 || R32(impl) == 0) return;
        uint last = R32(R32(impl));
        uint prev = R32(last);
        W32(R32(last + 4), prev);
        W32(prev + 4, R32(last + 4));
        ValueDtor(last + 8);
        Delete(last);
        W32(impl + 4, R32(impl + 4) - 1);
    }

    /// <summary>push_back runs inside the parse counter: if copying the value ran out of memory, the element is
    /// removed again.</summary>
    void ArrayPushBack(uint self, uint value)
    {
        uint impl = R32(self);
        if (impl == 0) return;
        ParseEnter();
        impl = R32(self);
        uint s = R32(impl);
        if (s != 0)
        {
            uint n = New(0x20);
            if (n != 0) ValueCtorCopy(n + 8, value);
            else n = R32(impl);
            if (n != R32(impl))
            {
                W32(n, R32(R32(impl)));
                W32(n + 4, R32(impl));
                W32(R32(n) + 4, n);
                W32(R32(impl), n);
                W32(impl + 4, R32(impl + 4) + 1);
            }
        }
        if (ParseLeave()) ArrayPopBack(self);
    }

    uint ArrayBack(uint self)
    {
        uint impl = R32(self);
        return impl == 0 ? NullValue : R32(R32(impl)) + 8;
    }

    // ------------------------------------------------------------------ iterators (4 bytes: cell*)

    uint NewCell()
    {
        uint c = New(4);
        if (c != 0) W32(c, 0);
        return c;
    }

    void FreeCell(uint cell)
    {
        if (cell != 0) Delete(cell);
    }

    /// <summary>begin() of an Array or Object (shared code): a cell holding the first node, or no cell.</summary>
    uint BeginCell(uint container)
    {
        uint c = NewCell();
        uint impl = R32(container);
        if (impl == 0) return c;
        uint s = R32(impl);
        uint first = s != 0 ? R32(s + 4) : 0;
        if (s == 0 || first == 0)
        {
            FreeCell(c);
            return 0;
        }
        if (c != 0) W32(c, first);
        return c;
    }

    uint EndCell(uint container)
    {
        uint c = NewCell();
        uint impl = R32(container);
        if (impl == 0) return c;
        uint s = R32(impl);
        if (s == 0)
        {
            FreeCell(c);
            return 0;
        }
        if (c != 0) W32(c, s);
        return c;
    }

    static bool CellsDiffer(uint a, uint b) => a != 0 && b != 0 && R32(a) != R32(b);
    static bool CellsEqual(uint a, uint b) => a != 0 && b != 0 && R32(a) == R32(b);

    uint ArrayDeref(uint cell) => cell == 0 ? NullValue : R32(cell) + 8;

    static void ArrayAdvance(uint cell, uint n)
    {
        if (cell == 0) return;
        for (uint i = 0; i < n; i++)
            if (R32(cell) != 0) W32(cell, R32(R32(cell) + 4));
    }

    static uint TreeNext(uint node)
    {
        uint r = R32(node + 8);
        if (!IsNil(r))
        {
            while (!IsNil(R32(r + 4))) r = R32(r + 4);
            return r;
        }
        uint p = R32(node);
        while (!IsNil(p) && R32(p + 8) == node)
        {
            node = p;
            p = R32(node);
        }
        return p;
    }

    static void ObjectAdvance(uint cell, uint n)
    {
        if (cell == 0) return;
        for (uint i = 0; i < n; i++)
            if (R32(cell) != 0) W32(cell, TreeNext(R32(cell)));
    }

    /// <summary>Object::iterator operator*/-&gt;: the node's Pair; a cell-less iterator dereferences begin() of the
    /// module's empty Object (the head node's Pair area).</summary>
    uint ObjectDeref(uint cell)
    {
        if (cell != 0) return R32(cell) + 0x20;
        uint tmp = BeginCell(R32(Data + DObject));
        uint r = ObjectDeref(tmp);
        if (tmp != 0) Delete(tmp);
        return r;
    }

    // ------------------------------------------------------------------ Object (4 bytes: impl*), red-black tree

    uint TreeImplNew()
    {
        uint impl = New(0xC);
        if (impl == 0) return 0;
        W32(impl + 4, 0);
        uint h = New(0x48);
        W32(impl, h);
        if (h != 0) HeadInit(h);
        return impl;
    }

    static void HeadInit(uint h)
    {
        W32(h, h);
        W32(h + 4, h);
        W32(h + 8, h);
        W8(h + 0x40, 0);
        W8(h + 0x41, 1);
    }

    uint ObjectCtor(uint self)
    {
        W32(self, 0);
        uint impl = TreeImplNew();
        if (impl != 0)
        {
            uint h = R32(impl);
            if (h == 0 || R32(h + 4) == 0)
            {
                TreeClear(impl);
                if (R32(impl) != 0) Delete(R32(impl));
                W32(impl, uint.MaxValue);
                W32(impl + 4, uint.MaxValue);
                Delete(impl);
                impl = 0;
            }
        }
        W32(self, impl);
        return self;
    }

    uint ObjectCtorCopy(uint self, uint other)
    {
        W32(self, 0);
        if (R32(other) == 0) return self;
        uint impl = TreeImplNew();
        if (impl == 0) return self;
        W32(self, impl);
        TreeReplace(impl, R32(other));
        return self;
    }

    void TreeReplace(uint impl, uint srcImpl)
    {
        uint saved = t_sp;
        try
        {
            uint tmp = Alloca(8);
            TreeCopy(tmp, srcImpl);
            uint a = R32(impl), b = R32(tmp);
            W32(tmp, a);
            W32(impl, b);
            a = R32(impl + 4);
            b = R32(tmp + 4);
            W32(tmp + 4, a);
            W32(impl + 4, b);
            TreeClear(tmp);
            if (R32(tmp) != 0) Delete(R32(tmp));
        }
        finally { t_sp = saved; }
    }

    uint ObjectDtor(uint self)
    {
        uint impl = R32(self);
        if (impl == 0) return self;
        TreeClear(impl);
        impl = R32(self);
        TreeClear(impl);
        if (R32(impl) != 0) Delete(R32(impl));
        W32(impl, uint.MaxValue);
        W32(impl + 4, uint.MaxValue);
        Delete(R32(self));
        W32(self, 0);
        return self;
    }

    uint ObjectAssign(uint self, uint other)
    {
        uint impl = R32(self);
        if (impl == 0 || R32(other) == 0) return self;
        TreeReplace(impl, R32(other));
        return self;
    }

    void DestroyNode(uint n)
    {
        ValueDtor(n + 0x28);
        uint key = R32(n + 0x20);
        if (key != 0)
        {
            RepFree(key);
            W32(n + 0x20, 0);
        }
        RepRelease(n + 0x10);
        Delete(n);
    }

    /// <summary>Destroys every node (bottom-up from the leftmost one) and resets the head.</summary>
    void TreeClear(uint impl)
    {
        uint h = R32(impl);
        if (h == 0) return;
        uint n = R32(h + 4);
        while (!IsNil(n))
        {
            uint l = R32(n + 4);
            if (!IsNil(l)) { n = l; continue; }
            uint r = R32(n + 8);
            if (!IsNil(r)) { n = r; continue; }
            uint p = R32(n);
            if (!IsNil(p))
            {
                if (R32(p + 4) == n) W32(p + 4, R32(impl));
                else W32(p + 8, R32(impl));
            }
            DestroyNode(n);
            n = p;
        }
        h = R32(impl);
        W32(h, h);
        W32(h + 4, h);
        W32(h + 8, h);
        W32(impl + 4, 0);
    }

    void TreeCopy(uint outPair, uint srcImpl)
    {
        W32(outPair + 4, 0);
        uint h = New(0x48);
        W32(outPair, h);
        if (h == 0) return;
        HeadInit(h);
        uint sh = R32(srcImpl);
        if (sh == 0) return;
        uint sroot = R32(sh);
        if (IsNil(sroot)) return;
        uint root = CopySubtree(outPair, R32(outPair), sroot);
        h = R32(outPair);
        if (root == h) return;
        W32(h, root);
        uint lm = root;
        while (!IsNil(R32(lm + 4))) lm = R32(lm + 4);
        W32(h + 4, lm);
        uint rm = root;
        while (!IsNil(R32(rm + 8))) rm = R32(rm + 8);
        W32(h + 8, rm);
        W32(outPair + 4, R32(srcImpl + 4));
    }

    uint CopySubtree(uint outPair, uint parent, uint src)
    {
        uint n = New(0x48);
        if (n == 0) return R32(outPair);
        byte color = R8(src + 0x40);
        W32(n, parent);
        uint h = R32(outPair);
        W32(n + 4, h);
        W32(n + 8, h);
        W32(n + 0x10, Empty);
        W32(n + 0x14, 0);
        RepCopy(n + 0x10, src + 0x10);
        W32(n + 0x20, 0);
        if (R32(src + 0x20) != 0)
        {
            uint rep = RepNew();
            if (rep != 0)
            {
                W32(n + 0x20, rep);
                RepCopy(rep, R32(src + 0x20));
            }
        }
        W32(n + 0x24, R32(src + 0x24));
        ValueCtorCopy(n + 0x28, src + 0x28);
        W8(n + 0x40, color);
        W8(n + 0x41, 0);
        if (!IsNil(R32(src + 4))) W32(n + 4, CopySubtree(outPair, n, R32(src + 4)));
        if (!IsNil(R32(src + 8))) W32(n + 8, CopySubtree(outPair, n, R32(src + 8)));
        return n;
    }

    static bool Less(uint a, uint b) => Strcmp(a, b) < 0;

    /// <summary>The node whose key equals the rep's text, the head (end) when absent, 0 without a head.</summary>
    static uint TreeFind(uint impl, uint keyRep)
    {
        uint h = R32(impl);
        if (h == 0) return 0;
        uint n = R32(h);
        while (!IsNil(n))
        {
            uint k = R32(keyRep), nk = R32(n + 0x10);
            if (Less(k, nk)) n = R32(n + 4);
            else if (Less(nk, k)) n = R32(n + 8);
            else return n;
        }
        return n;
    }

    /// <summary>map::insert of the value_type at <paramref name="vt"/> (+0 key rep, +0x10 Pair): the existing node
    /// for an equal key, the new node, the head if the node could not be allocated, 0 without a head.</summary>
    uint TreeInsert(uint impl, uint vt, uint insertedFlag)
    {
        uint h = R32(impl);
        if (h == 0) return 0;
        if (insertedFlag != 0) W8(insertedFlag, 0);
        uint parent = h, n = R32(h);
        int dir = 0;
        while (!IsNil(n))
        {
            parent = n;
            uint k = R32(vt), nk = R32(n + 0x10);
            if (Less(k, nk)) { n = R32(n + 4); dir = -1; }
            else if (Less(nk, k)) { n = R32(n + 8); dir = 1; }
            else return n;
        }
        uint x = New(0x48);
        if (x == 0) return R32(impl);
        h = R32(impl);
        W32(x, parent);
        W32(x + 4, h);
        W32(x + 8, h);
        W32(x + 0x10, Empty);
        W32(x + 0x14, 0);
        RepCopy(x + 0x10, vt);
        W32(x + 0x20, 0);
        if (R32(vt + 0x10) != 0)
        {
            uint rep = RepNew();
            if (rep != 0)
            {
                W32(x + 0x20, rep);
                RepCopy(rep, R32(vt + 0x10));
            }
        }
        W32(x + 0x24, R32(vt + 0x14));
        ValueCtorCopy(x + 0x28, vt + 0x18);
        W8(x + 0x41, 0);
        W8(x + 0x40, 1);
        h = R32(impl);
        if (dir == 0)
        {
            W32(h, x);
            W32(h + 4, x);
            W32(h + 8, x);
        }
        else if (dir < 0)
        {
            W32(parent + 4, x);
            if (parent == R32(R32(impl) + 4)) W32(R32(impl) + 4, x);
        }
        else
        {
            W32(parent + 8, x);
            if (parent == R32(R32(impl) + 8)) W32(R32(impl) + 8, x);
        }
        W32(impl + 4, R32(impl + 4) + 1);
        InsertFixup(impl, x);
        if (insertedFlag != 0) W8(insertedFlag, 1);
        return x;
    }

    static void RotateLeft(uint h, uint x)
    {
        uint y = R32(x + 8);
        W32(x + 8, R32(y + 4));
        if (!IsNil(R32(y + 4))) W32(R32(y + 4), x);
        uint p = R32(x);
        W32(y, p);
        if (IsNil(p)) W32(h, y);
        else if (x == R32(p + 4)) W32(p + 4, y);
        else W32(p + 8, y);
        W32(y + 4, x);
        W32(x, y);
    }

    static void RotateRight(uint h, uint x)
    {
        uint y = R32(x + 4);
        W32(x + 4, R32(y + 8));
        if (!IsNil(R32(y + 8))) W32(R32(y + 8), x);
        uint p = R32(x);
        W32(y, p);
        if (IsNil(p)) W32(h, y);
        else if (x == R32(p + 8)) W32(p + 8, y);
        else W32(p + 4, y);
        W32(y + 8, x);
        W32(x, y);
    }

    static void InsertFixup(uint impl, uint x)
    {
        uint h = R32(impl);
        if (h == 0) return;
        W8(x + 0x40, 1);
        while (x != R32(h))
        {
            uint p = R32(x);
            if (!Red(p)) break;
            uint g = R32(p);
            if (p == R32(g + 4))
            {
                uint u = R32(g + 8);
                if (Red(u))
                {
                    W8(p + 0x40, 0);
                    W8(u + 0x40, 0);
                    W8(R32(R32(x)) + 0x40, 1);
                    x = R32(R32(x));
                    continue;
                }
                if (x == R32(p + 8))
                {
                    x = p;
                    RotateLeft(h, x);
                }
                W8(R32(x) + 0x40, 0);
                W8(R32(R32(x)) + 0x40, 1);
                RotateRight(h, R32(R32(x)));
            }
            else
            {
                uint u = R32(g + 4);
                if (Red(u))
                {
                    W8(p + 0x40, 0);
                    W8(u + 0x40, 0);
                    W8(R32(R32(x)) + 0x40, 1);
                    x = R32(R32(x));
                    continue;
                }
                if (x == R32(p + 4))
                {
                    x = p;
                    RotateRight(h, x);
                }
                W8(R32(x) + 0x40, 0);
                W8(R32(R32(x)) + 0x40, 1);
                RotateLeft(h, R32(R32(x)));
            }
        }
        W8(R32(h) + 0x40, 0);
    }

    static void DeleteFixup(uint impl, uint x, uint xp)
    {
        uint h = R32(impl);
        if (h == 0) return;
        while (!IsNil(xp) && !Red(x))
        {
            if (x == R32(xp + 4))
            {
                uint w = R32(xp + 8);
                if (Red(w))
                {
                    W8(w + 0x40, 0);
                    W8(xp + 0x40, 1);
                    w = R32(w + 4);
                    RotateLeft(h, xp);
                }
                if (!Red(R32(w + 4)) && !Red(R32(w + 8)))
                {
                    W8(w + 0x40, 1);
                    x = xp;
                    xp = R32(x);
                    continue;
                }
                if (!Red(R32(w + 8)))
                {
                    W8(R32(w + 4) + 0x40, 0);
                    W8(w + 0x40, 1);
                    uint nw = R32(w + 4);
                    RotateRight(h, w);
                    w = nw;
                }
                W8(w + 0x40, R8(xp + 0x40));
                W8(xp + 0x40, 0);
                W8(R32(w + 8) + 0x40, 0);
                RotateLeft(h, xp);
            }
            else
            {
                uint w = R32(xp + 4);
                if (Red(w))
                {
                    W8(w + 0x40, 0);
                    W8(xp + 0x40, 1);
                    w = R32(w + 8);
                    RotateRight(h, xp);
                }
                if (!Red(R32(w + 8)) && !Red(R32(w + 4)))
                {
                    W8(w + 0x40, 1);
                    x = xp;
                    xp = R32(x);
                    continue;
                }
                if (!Red(R32(w + 4)))
                {
                    W8(R32(w + 8) + 0x40, 0);
                    W8(w + 0x40, 1);
                    uint nw = R32(w + 8);
                    RotateLeft(h, w);
                    w = nw;
                }
                W8(w + 0x40, R8(xp + 0x40));
                W8(xp + 0x40, 0);
                W8(R32(w + 4) + 0x40, 0);
                RotateRight(h, xp);
            }
            x = R32(h);
            break;
        }
        W8(x + 0x40, 0);
    }

    void TreeErase(uint impl, uint z)
    {
        uint h = R32(impl);
        if (h == 0) return;
        uint y, xp;
        if (IsNil(R32(z + 4)) || IsNil(R32(z + 8)))
        {
            y = z;
            xp = R32(z);
        }
        else if (IsNil(R32(R32(z + 8) + 4)))
        {
            y = R32(z + 8);
            xp = y;
        }
        else
        {
            y = R32(z + 8);
            while (!IsNil(R32(y + 4))) y = R32(y + 4);
            xp = R32(y);
        }
        uint yp = R32(y);
        uint x = !IsNil(R32(y + 4)) ? R32(y + 4) : R32(y + 8);
        if (!IsNil(x))
        {
            W32(x, yp);
            yp = R32(y);
        }
        if (IsNil(yp)) W32(h, x);
        else if (R32(yp + 4) == y) W32(yp + 4, x);
        else W32(yp + 8, x);
        bool fix = !Red(y);
        if (y != z)
        {
            uint zp = R32(z);
            W32(y, zp);
            if (IsNil(zp)) W32(h, y);
            else if (R32(zp + 4) == z) W32(zp + 4, y);
            else W32(zp + 8, y);
            W32(y + 4, R32(z + 4));
            if (!IsNil(R32(z + 4))) W32(R32(z + 4), y);
            W32(y + 8, R32(z + 8));
            if (!IsNil(R32(z + 8))) W32(R32(z + 8), y);
            W8(y + 0x40, R8(z + 0x40));
        }
        if (fix)
        {
            DeleteFixup(impl, x, xp);
            h = R32(impl);
        }
        if (R32(h + 4) == z)
        {
            uint m = xp;
            if (!IsNil(x))
            {
                m = x;
                while (!IsNil(R32(m + 4))) m = R32(m + 4);
            }
            W32(h + 4, m);
        }
        if (R32(h + 8) == z)
        {
            uint m = xp;
            if (!IsNil(x))
            {
                m = x;
                while (!IsNil(R32(m + 8))) m = R32(m + 8);
            }
            W32(h + 8, m);
        }
        DestroyNode(z);
        W32(impl + 4, R32(impl + 4) - 1);
    }

    /// <summary>Object::operator[]'s lookup: inserts {key, null} unless the key exists; returns the node's Pair.</summary>
    uint LookupOrInsert(uint impl, uint keyRep)
    {
        uint saved = t_sp;
        try
        {
            uint f = Alloca(0x58);
            W32(f, 0);
            uint rep = RepNew();
            if (rep != 0) W32(f, rep);
            ValueCtorType(f + 8, TNull);
            W32(f + 0x20, Empty);
            W32(f + 0x24, 0);
            RepCopy(f + 0x20, keyRep);
            W32(f + 0x30, 0);
            if (R32(f) != 0)
            {
                uint r2 = RepNew();
                if (r2 != 0)
                {
                    W32(f + 0x30, r2);
                    RepCopy(r2, R32(f));
                }
            }
            W32(f + 0x34, R32(f + 4));
            ValueCtorCopy(f + 0x38, f + 8);
            uint node = TreeInsert(impl, f + 0x20, 0);
            ValueDtor(f + 0x38);
            if (R32(f + 0x30) != 0)
            {
                RepFree(R32(f + 0x30));
                W32(f + 0x30, 0);
            }
            RepRelease(f + 0x20);
            ValueDtor(f + 8);
            if (R32(f) != 0) RepFree(R32(f));
            return node + 0x20;
        }
        finally { t_sp = saved; }
    }

    uint ObjectIndex(uint self, uint key)
    {
        uint impl = R32(self);
        if (impl == 0 || R32(key) == 0) return NullValue;
        ParseEnter();
        uint saved = t_sp;
        uint pair;
        try
        {
            uint kd = R32(R32(key));
            uint tmp = Alloca(8);
            W32(tmp, Empty);
            W32(tmp + 4, 0);
            RepAssign(tmp, kd, kd != 0 ? StrLen(kd) : 0);
            pair = LookupOrInsert(R32(self), tmp);
            if (R32(tmp) != Empty) Delete(R32(tmp));
        }
        finally { t_sp = saved; }
        if (ParseLeave()) return NullValue;
        if (R32(pair) != 0 && R32(key) != 0) RepCopy(R32(pair), R32(key));
        return pair + 8;
    }

    uint ObjectFindCell(uint self, uint key)
    {
        uint c = NewCell();
        uint impl = R32(self);
        if (impl == 0) return c;
        uint rep = R32(key);
        if (rep == 0) return c;
        uint node = TreeFind(impl, rep);
        if (node == 0)
        {
            FreeCell(c);
            return 0;
        }
        if (c != 0) W32(c, node);
        return c;
    }

    uint ObjectInsertCell(uint self, uint pair)
    {
        uint c = NewCell();
        uint impl = R32(self);
        if (impl == 0) return c;
        uint saved = t_sp;
        uint node;
        try
        {
            uint kd = R32(pair) != 0 ? R32(R32(pair)) : Data + DLit;
            uint f = Alloca(0x40);
            W32(f + 4, Empty);
            W32(f + 8, 0);
            RepAssign(f + 4, kd, kd != 0 ? StrLen(kd) : 0);
            W32(f + 0x10, Empty);
            W32(f + 0x14, 0);
            RepCopy(f + 0x10, f + 4);
            W32(f + 0x20, 0);
            if (R32(pair) != 0)
            {
                uint rep = RepNew();
                if (rep != 0)
                {
                    W32(f + 0x20, rep);
                    RepCopy(rep, R32(pair));
                }
            }
            W32(f + 0x24, R32(pair + 4));
            ValueCtorCopy(f + 0x28, pair + 8);
            node = TreeInsert(impl, f + 0x10, f);
            ValueDtor(f + 0x28);
            if (R32(f + 0x20) != 0)
            {
                RepFree(R32(f + 0x20));
                W32(f + 0x20, 0);
            }
            RepRelease(f + 0x10);
            RepRelease(f + 4);
        }
        finally { t_sp = saved; }
        if (node == 0)
        {
            FreeCell(c);
            return 0;
        }
        if (c != 0) W32(c, node);
        return c;
    }

    void ObjectEraseKey(uint self, uint key)
    {
        uint impl = R32(self);
        if (impl == 0 || R32(key) == 0) return;
        uint n = TreeFind(impl, R32(key));
        if (n != 0 && !IsNil(n)) TreeErase(impl, n);
    }

    uint PairCtor(uint self)
    {
        W32(self, 0);
        uint rep = New(0xC);
        if (rep != 0)
        {
            W32(rep, Empty);
            W32(rep + 4, 0);
            W32(self, rep);
            RepAssign(rep, 0, 0);
        }
        ValueCtorType(self + 8, TNull);
        return self;
    }

    uint PairCtorKeyValue(uint self, uint key, uint value)
    {
        StringCtorCopy(self, key);
        ValueCtorCopy(self + 8, value);
        return self;
    }

    uint PairDtor(uint self)
    {
        ValueDtor(self + 8);
        StringDtor(self);
        return self;
    }

    // ------------------------------------------------------------------ getters and element access

    uint GetValueIndex(uint v, uint idx)
    {
        int t = (int)R32(v + 0x14);
        if ((t == TArray || t == TObject) && idx < StringSize(R32(v + 8)))
        {
            uint cell = BeginCell(R32(v + 8));
            uint r;
            if (t == TArray)
            {
                ArrayAdvance(cell, idx);
                r = ArrayDeref(cell);
            }
            else
            {
                ObjectAdvance(cell, idx);
                r = ObjectDeref(cell) + 8;
            }
            FreeCell(cell);
            return r;
        }
        return MissingValue(v);
    }

    /// <summary>The Pair's Value for <paramref name="key"/> in an Object value, or 0.</summary>
    uint FindMember(uint v, uint key)
    {
        uint obj = R32(v + 8);
        uint it = ObjectFindCell(obj, key);
        uint e = EndCell(obj);
        bool found = CellsDiffer(it, e);
        FreeCell(e);
        uint r = found ? ObjectDeref(it) + 8 : 0;
        FreeCell(it);
        return r;
    }

    uint GetValueKey(uint v, uint key)
    {
        if (R32(key) == 0) return NullValue;
        if (R32(v + 0x14) == TObject)
        {
            uint r = FindMember(v, key);
            if (r != 0) return r;
        }
        return MissingValue(v);
    }

    uint ReferValueIndex(uint v, uint idx)
    {
        int t = (int)R32(v + 0x14);
        if ((t != TArray && t != TObject) || idx >= StringSize(R32(v + 8))) return 0;
        uint cell = BeginCell(R32(v + 8));
        uint r;
        if (t == TArray)
        {
            ArrayAdvance(cell, idx);
            r = ArrayDeref(cell);
        }
        else
        {
            ObjectAdvance(cell, idx);
            r = ObjectDeref(cell) + 8;
        }
        FreeCell(cell);
        return r;
    }

    // ------------------------------------------------------------------ serializer

    /// <summary>Calls the serializer's flush callback <c>int cb(String&amp; out, void* user)</c> (0: none).</summary>
    static int Flush(uint cb, uint outStr, uint user) => cb == 0 ? 0 : (int)Call(cb, outStr, user);

    void ToStringAppend(uint v, uint outStr)
    {
        switch ((int)R32(v + 0x14))
        {
            case TNull: AppendAscii(outStr, "null"); break;
            case TBool: AppendAscii(outStr, R8(v + 8) != 0 ? "true" : "false"); break;
            case TInt: AppendAscii(outStr, Printf("%lld", R64(v + 8))); break;
            case TUInt: AppendAscii(outStr, Printf("%llu", R64(v + 8))); break;
            case TReal:
            {
                string s = Printf("%f", R64(v + 8));
                int dot = s.IndexOf('.');
                // "%f" output whose fraction is all zeros loses it ("3.000000" -> "3"); any other digit keeps it all
                if (dot >= 0 && s.AsSpan(dot + 1).IndexOfAnyExcept('0') < 0) s = s[..dot];
                AppendAscii(outStr, s);
                break;
            }
            case TString: AppendString(outStr, R32(v + 8)); break;
            case TArray: AppendAscii(outStr, "array"); break;
            case TObject: AppendAscii(outStr, "object"); break;
        }
    }

    /// <summary>The module formats numbers with sceClibSnprintf into a 256-byte buffer (a 300-digit %f is cut to 255
    /// characters); this is the same formatter the HLE snprintf uses.</summary>
    static string Printf(string fmt, ulong arg)
    {
        string s = CFormat.Format(fmt, new VarArgs(new OneArg(arg), 0));
        return s.Length > 255 ? s[..255] : s;
    }

    sealed class OneArg(ulong v) : IVaSource
    {
        int _n;
        public uint Next() => _n++ == 0 ? (uint)v : (uint)(v >> 32);
    }

    void QuoteAppend(uint outStr, uint str)
    {
        AppendChar(outStr, (byte)'"');
        uint n = StringSize(str);
        for (uint i = 0; i < n; i++)
        {
            byte c = StringAt(str, i);
            switch (c)
            {
                case (byte)'"': AppendAscii(outStr, "\\\""); break;
                case (byte)'\\': AppendAscii(outStr, "\\\\"); break;
                case (byte)'/': AppendAscii(outStr, "\\/"); break;
                case 8: AppendAscii(outStr, "\\b"); break;
                case 0xC: AppendAscii(outStr, "\\f"); break;
                case 0xA: AppendAscii(outStr, "\\n"); break;
                case 0xD: AppendAscii(outStr, "\\r"); break;
                case 9: AppendAscii(outStr, "\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7F) AppendAscii(outStr, Printf("\\u%04x", c));
                    else AppendChar(outStr, c);
                    break;
            }
        }
        AppendChar(outStr, (byte)'"');
    }

    int Serialize(uint v, uint outStr, uint cb, uint user, uint parent)
    {
        int ret = 0;
        SetParent(v, parent);
        switch ((int)R32(v + 0x14))
        {
            case TNull: case TBool: case TInt: case TUInt: case TReal:
                ToStringAppend(v, outStr);
                return Flush(cb, outStr, user);
            case TString:
                QuoteAppend(outStr, R32(v + 8));
                return Flush(cb, outStr, user);
            case TArray:
            case TObject:
            {
                bool obj = R32(v + 0x14) == TObject;
                AppendChar(outStr, (byte)(obj ? '{' : '['));
                ret = Flush(cb, outStr, user);
                if (ret < 0) return ret;
                uint c = R32(v + 8);
                uint it = BeginCell(c);
                while (true)
                {
                    uint e = EndCell(c);
                    bool more = CellsDiffer(it, e);
                    FreeCell(e);
                    if (!more) break;
                    uint b = BeginCell(c);
                    bool notFirst = CellsDiffer(it, b);
                    FreeCell(b);
                    if (notFirst) AppendChar(outStr, (byte)',');
                    uint elem;
                    if (obj)
                    {
                        QuoteAppend(outStr, ObjectDeref(it));
                        AppendChar(outStr, (byte)':');
                        elem = ObjectDeref(it) + 8;
                    }
                    else elem = ArrayDeref(it);
                    ret = Serialize(elem, outStr, cb, user, v);
                    if (ret < 0)
                    {
                        FreeCell(it);
                        return ret;
                    }
                    if (obj) { if (it != 0 && R32(it) != 0) W32(it, TreeNext(R32(it))); }
                    else if (it != 0 && R32(it) != 0) W32(it, R32(R32(it) + 4));
                }
                FreeCell(it);
                AppendChar(outStr, (byte)(obj ? '}' : ']'));
                return Flush(cb, outStr, user);
            }
        }
        return ret;
    }

    // ------------------------------------------------------------------ parser

    /// <summary>The parser's character source with one character of push-back: +0 callback, +4 user, +8 current
    /// character, +0xC last read result, +0x10 pushed back.</summary>
    sealed class Reader
    {
        public required Func<Reader, (int Ret, byte Ch)> Read;
        public int Cur = -1, Ret;
        public bool Pushed;
        // buffer source
        public uint Pos, End;
        // guest callback source
        public uint Cb, User, Scratch;
        // file source
        public int Fd = -1;
        public uint Buf, BufSize, Len, BufPos;
    }

    static int Get(Reader r)
    {
        if (r.Pushed)
        {
            r.Pushed = false;
            return r.Cur;
        }
        var (ret, ch) = r.Read(r);
        r.Ret = ret;
        r.Cur = ch;
        return ch;
    }

    static bool IsSpace(int c) => c is ' ' or '\t' or '\n' or '\r';

    static int SkipSpace(Reader r)
    {
        int c;
        do c = Get(r); while (IsSpace(c));
        return c;
    }

    static (int, byte) ReadCallback(Reader r)
    {
        W8(r.Scratch, 0);
        int ret = (int)Call(r.Cb, r.Scratch, r.User);
        return (ret, R8(r.Scratch));
    }

    static (int, byte) ReadBuffer(Reader r)
    {
        if (r.Pos >= r.End) return (ERROR_PARSE_INVALID_CHAR, 0);
        return (0, R8(r.Pos++));
    }

    (int, byte) ReadFile(Reader r)
    {
        if (r.Fd < 0)
        {
            int e = r.Fd == -1 ? ERROR_NOINIT : r.Fd == unchecked((int)0x80010002) ? ERROR_NOFILE : r.Fd;
            return (e, 0);
        }
        if (r.Len == r.BufPos)
        {
            int n = (int)Call(StubAddr(Stub.IoRead), (uint)r.Fd, r.Buf, r.BufSize);
            if (n == 0) return (ERROR_PARSE_INVALID_CHAR, 0);
            if (n < 0) return (n, 0);
            r.Len = (uint)n;
            r.BufPos = 0;
        }
        return (0, R8(r.Buf + r.BufPos++));
    }

    bool ParseValue(uint v, Reader r, uint parent)
    {
        if (Aborted) return false;
        int c = SkipSpace(r);
        r.Pushed = true;
        if (r.Ret < 0) return false;
        r.Pushed = false;
        switch (c)
        {
            case 'n': if (!Literal(r, "ull")) return false; SetType(v, TNull); SetParent(v, parent); return true;
            case 'f': if (!Literal(r, "alse")) return false; SetType(v, TBool); W8(v + 8, 0); SetParent(v, parent); return true;
            case 't': if (!Literal(r, "rue")) return false; SetType(v, TBool); W8(v + 8, 1); SetParent(v, parent); return true;
            case '"':
                if (Aborted) return false;
                SetType(v, TString);
                SetParent(v, parent);
                if (R32(v + 0x14) != TString || R32(v + 8) == 0) return false;
                return ParseString(R32(v + 8), r);
            case '[': return ParseArray(v, r, parent);
            case '{': return ParseObject(v, r, parent);
        }
        r.Pushed = true;
        if (c is >= '0' and <= '9' or '-') return ParseNumber(v, r, parent);
        return false;
    }

    static bool Literal(Reader r, string rest)
    {
        foreach (char k in rest)
        {
            int c = Get(r);
            if (r.Ret < 0) return false;
            if (c != k) { r.Pushed = true; return false; }
        }
        return true;
    }

    int ReadHex4(Reader r)
    {
        int acc = 0;
        for (int i = 0; i < 4; i++)
        {
            int c = Get(r);
            if (r.Ret < 0) return -1;
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'F' ? c - 'A' + 10 : c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
            if (d < 0) { r.Pushed = true; return -1; }
            acc = acc * 16 + d;
        }
        return acc;
    }

    bool ParseUnicode(uint outStr, Reader r)
    {
        int u = ReadHex4(r);
        if (u == -1) return false;
        if (u is >= 0xD800 and <= 0xDFFF)
        {
            if (u >= 0xDC00) return false;
            if (Get(r) != '\\') { r.Pushed = true; return false; }
            if (Get(r) != 'u') { r.Pushed = true; return false; }
            int lo = ReadHex4(r);
            if (lo is < 0xDC00 or > 0xDFFF) return false;
            u = ((lo & 0x3FF) | ((u & 0x3FF) << 10)) + 0x10000;
        }
        if (u < 0x80) AppendChar(outStr, (byte)u);
        else if (u < 0x800)
        {
            AppendChar(outStr, (byte)(0xC0 | ((u >> 6) & 0x1F)));
            AppendChar(outStr, (byte)(0x80 | (u & 0x3F)));
        }
        else if (u < 0x10000)
        {
            AppendChar(outStr, (byte)(0xE0 | ((u >> 12) & 0xF)));
            AppendChar(outStr, (byte)(0x80 | ((u >> 6) & 0x3F)));
            AppendChar(outStr, (byte)(0x80 | (u & 0x3F)));
        }
        else
        {
            AppendChar(outStr, (byte)(0xF0 | ((u >> 18) & 0x7)));
            AppendChar(outStr, (byte)(0x80 | ((u >> 12) & 0x3F)));
            AppendChar(outStr, (byte)(0x80 | ((u >> 6) & 0x3F)));
            AppendChar(outStr, (byte)(0x80 | (u & 0x3F)));
        }
        return true;
    }

    /// <summary>Reads the rest of a string after its opening quote.</summary>
    bool ParseString(uint outStr, Reader r)
    {
        if (Aborted) return false;
        while (true)
        {
            int c = Get(r);
            if (r.Ret < 0) return false;
            if (c < 0x20) { r.Pushed = true; return false; }
            if (c == '"') return true;
            if (c != '\\') { AppendChar(outStr, (byte)c); continue; }
            int e = Get(r);
            if (r.Ret < 0) return false;
            switch (e)
            {
                case '"': AppendChar(outStr, (byte)'"'); break;
                case '\\': AppendChar(outStr, (byte)'\\'); break;
                case '/': AppendChar(outStr, (byte)'/'); break;
                case 'b': AppendChar(outStr, 8); break;
                case 'f': AppendChar(outStr, 0xC); break;
                case 'n': AppendChar(outStr, 0xA); break;
                case 'r': AppendChar(outStr, 0xD); break;
                case 't': AppendChar(outStr, 9); break;
                case 'u': if (!ParseUnicode(outStr, r)) return false; break;
                default: return false;
            }
        }
    }

    /// <summary>Numbers: digits/sign/./e collected into a String; a plain integer becomes UInteger (Integer when
    /// negative); anything else, or overflow, goes through the module's strtod as Real.</summary>
    bool ParseNumber(uint v, Reader r, uint parent)
    {
        if (Aborted) return false;
        uint saved = t_sp;
        try
        {
            uint tmp = Alloca(4);
            StringCtor(tmp);
            bool real = false;
            while (true)
            {
                int c = Get(r);
                if (c is >= '0' and <= '9') { AppendChar(tmp, (byte)c); continue; }
                if (c is '-' or '+' or '.' or 'e' or 'E')
                {
                    if (!StringEmpty(tmp)) real = true;
                    AppendChar(tmp, (byte)c);
                    continue;
                }
                break;
            }
            r.Pushed = true;
            if (!real)
            {
                uint s = StringCStr(tmp);
                uint p = R8(s) == '-' ? s + 1 : s;
                ulong acc = 0;
                bool overflow = false;
                for (byte ch; (ch = R8(p)) != 0; p++)
                {
                    ulong next = unchecked(acc * 10 + (ulong)(long)(sbyte)(ch - '0'));
                    if (acc > next) { overflow = true; break; }
                    acc = next;
                }
                if (!overflow)
                {
                    if (R8(StringCStr(tmp)) == '-')
                    {
                        if (acc < 0x8000000000000000UL)
                        {
                            SetType(v, TInt);
                            W64(v + 8, unchecked(0 - acc));
                            SetParent(v, parent);
                            StringDtor(tmp);
                            return true;
                        }
                    }
                    else
                    {
                        SetType(v, TUInt);
                        W64(v + 8, acc);
                        SetParent(v, parent);
                        StringDtor(tmp);
                        return true;
                    }
                }
            }
            double d = Strtod(StringCStr(tmp));
            SetType(v, TReal);
            W64(v + 8, BitConverter.DoubleToUInt64Bits(d));
            SetParent(v, parent);
            StringDtor(tmp);
            return true;
        }
        finally { t_sp = saved; }
    }

    static bool StringEmpty(uint self) => R32(self) != 0 && R32(R32(self) + 4) == 0;
    uint StringCStr(uint self) => R32(self) == 0 ? Data + DLit : R32(R32(self));

    bool ParseArray(uint v, Reader r, uint parent)
    {
        if (Aborted) return false;
        SetType(v, TArray);
        SetParent(v, parent);
        uint arr;
        if (R32(v + 0x14) != TArray || (arr = R32(v + 8)) == 0) return false;
        int c = SkipSpace(r);
        r.Pushed = true;
        if (r.Ret >= 0)
        {
            r.Pushed = false;
            if (c == ']') return true;
            r.Pushed = true;
        }
        while (true)
        {
            uint saved = t_sp;
            try
            {
                uint tmp = Alloca(0x18);
                ValueInit(tmp);
                SetType(tmp, TNull);
                ArrayPushBack(arr, tmp);
                SetType(tmp, TNull);
            }
            finally { t_sp = saved; }
            if (Aborted) return false;
            if (!ParseValue(ArrayBack(arr), r, v)) return false;
            c = SkipSpace(r);
            r.Pushed = true;
            if (r.Ret < 0) break;
            r.Pushed = false;
            if (c != ',') { r.Pushed = true; break; }
        }
        c = SkipSpace(r);
        r.Pushed = true;
        if (r.Ret < 0) return false;
        r.Pushed = false;
        if (c == ']') return true;
        r.Pushed = true;
        return false;
    }

    bool ParseObject(uint v, Reader r, uint parent)
    {
        if (Aborted) return false;
        SetType(v, TObject);
        SetParent(v, parent);
        uint obj;
        if (R32(v + 0x14) != TObject || (obj = R32(v + 8)) == 0) return false;
        int c = SkipSpace(r);
        r.Pushed = true;
        if (r.Ret >= 0)
        {
            r.Pushed = false;
            if (c == '}') return true;
            r.Pushed = true;
        }
        uint saved = t_sp;
        try
        {
            uint key = Alloca(4);
            StringCtor(key);
            bool ok = Members(obj, key, v, r);
            StringDtor(key);
            return ok;
        }
        finally { t_sp = saved; }
    }

    bool Members(uint obj, uint key, uint v, Reader r)
    {
        while (true)
        {
            int c = SkipSpace(r);
            r.Pushed = true;
            if (r.Ret < 0) return false;
            r.Pushed = false;
            if (c != '"') { r.Pushed = true; return false; }
            if (!ParseString(key, r)) return false;
            c = SkipSpace(r);
            r.Pushed = true;
            if (r.Ret < 0) return false;
            r.Pushed = false;
            if (c != ':') { r.Pushed = true; return false; }
            if (!ParseValue(ObjectIndex(obj, key), r, v)) return false;
            StringClear(key);
            c = SkipSpace(r);
            r.Pushed = true;
            if (r.Ret < 0) break;
            r.Pushed = false;
            if (c != ',') { r.Pushed = true; break; }
        }
        int e = SkipSpace(r);
        r.Pushed = true;
        if (r.Ret < 0) return false;
        r.Pushed = false;
        if (e == '}') return true;
        r.Pushed = true;
        return false;
    }

    void StringClear(uint self)
    {
        if (R32(self) != 0) RepAssign(R32(self), 0, 0);
    }

    /// <summary>Runs one top-level parse: the parse counter around it, PARSE_INVALID_CHAR (or the source's own
    /// negative result) on a syntax error, NOMEM when an allocation failed meanwhile.</summary>
    int RunParse(uint v, Reader r)
    {
        ParseEnter();
        int result = 0;
        if (!ParseValue(v, r, 0))
        {
            result = r.Ret;
            if (result >= 0) result = ERROR_PARSE_INVALID_CHAR;
        }
        return ParseLeave() ? ERROR_NOMEM : result;
    }
}
