using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLibJson (sce::Json, 179 C++ methods): HLE replacement for vs0:sys/external/libSceJson.suprx, used when the
/// firmware module is not installed (or with --hle-modules=json). The implementation (<c>Json.Core.cs</c>) follows the
/// FW 3.60 module instruction by instruction: same object layouts, same allocator calls through the game's
/// MemAllocator subclass, same error codes. ARM C++ ABI: <c>this</c> in r0, constructors and destructors return it,
/// class objects returned by value (iterators, String) go to the hidden pointer in r0 with <c>this</c> in r1, and
/// iterators passed by value arrive by reference.
/// </summary>
public sealed unsafe partial class Json : HleModule
{
    // ------------------------------------------------------------------ Initializer (1 byte: initialized flag)

    [Hle("_ZN3sce4Json11InitializerC1Ev")]
    [Hle("_ZN3sce4Json11InitializerC2Ev")]
    uint InitializerCtor(uint thisPtr)
    {
        W8(thisPtr, 0);
        return thisPtr;
    }

    [Hle("_ZN3sce4Json11InitializerD1Ev")]
    [Hle("_ZN3sce4Json11InitializerD2Ev")]
    uint InitializerDtor(uint thisPtr)
    {
        InitializerTerminate(thisPtr);
        return thisPtr;
    }

    /// <summary>InitParameter: +0 MemAllocator*, +4 user data (passed to every allocator call), +8 file read buffer
    /// size (Parser::parse from a file). Creates the module's shared empty String/Array/Object, null Value and
    /// lwmutex, all from the allocator.</summary>
    [Hle("_ZN3sce4Json11Initializer10initializeEPKNS0_13InitParameterE")]
    int InitializerInitialize(uint thisPtr, uint param)
    {
        uint d = Data;
        if (R32(d + DInfo) != 0 || R8(thisPtr) != 0) return ERROR_MULTIPLEINIT;
        uint alloc = R32(param);
        if (alloc == 0) return ERROR_NOMEM;
        uint info = CallVirt(alloc, 2, 0xC, R32(param + 4));
        if (info == 0) return ERROR_NOMEM;
        W32(d + DInfo, info);
        W32(info, R32(param));
        W32(info + 4, R32(param + 4));
        W32(info + 8, R32(param + 8));
        W32(d + DParseCount, 0);
        W8(d + DAbort, 0);
        uint s = New(4);
        if (s != 0) StringCtor(s);
        W32(d + DString, s);
        uint a = New(4);
        if (a != 0) ArrayCtor(a);
        W32(d + DArray, a);
        uint o = New(4);
        if (o != 0) ObjectCtor(o);
        W32(d + DObject, o);
        uint nv = New(0x18);
        if (nv != 0) ValueCtorType(nv, TNull);
        W32(d + DNullValue, nv);
        uint m = New(0x20);
        if (m != 0) Call(StubAddr(Stub.CreateLwMutex), m, d + DMutexName, 0x2002, 0, 0);
        W32(d + DMutex, m);
        W8(thisPtr, 1);
        if (R32(d + DString) == 0 || R32(d + DArray) == 0 || R32(d + DObject) == 0 || R32(d + DNullValue) == 0 || m == 0)
        {
            InitializerTerminate(thisPtr);
            return ERROR_NOMEM;
        }
        return 0;
    }

    [Hle("_ZN3sce4Json11Initializer9terminateEv")]
    int InitializerTerminate(uint thisPtr)
    {
        uint d = Data;
        if (R32(d + DInfo) == 0 || R8(thisPtr) == 0) return ERROR_NOINIT;
        uint nv = R32(d + DNullValue);
        if (nv != 0)
        {
            ValueDtor(nv);
            Delete(nv);
        }
        W32(d + DNullValue, 0);
        uint m = R32(d + DMutex);
        if (m != 0)
        {
            Call(StubAddr(Stub.DeleteLwMutex), m);
            Delete(m);
        }
        W32(d + DMutex, 0);
        uint s = R32(d + DString);
        if (s != 0) { StringDtor(s); Delete(s); }
        W32(d + DString, 0);
        uint a = R32(d + DArray);
        if (a != 0) { ArrayDtor(a); Delete(a); }
        W32(d + DArray, 0);
        uint o = R32(d + DObject);
        if (o != 0) { ObjectDtor(o); Delete(o); }
        W32(d + DObject, 0);
        Delete(R32(d + DInfo));
        W32(d + DInfo, 0);
        W32(d + DParseCount, 0);
        W8(thisPtr, 0);
        W8(d + DAbort, 0);
        return 0;
    }

    /// <summary>Accepted and ignored by the firmware module (only checks that the library is initialized).</summary>
    [Hle("_ZN3sce4Json11Initializer24setAllocatorInfoCallBackEPFviNS0_9ValueTypeEPvES3_")]
    int InitializerSetAllocatorInfoCallBack(uint thisPtr, uint cb, uint user) => R32(Data + DInfo) == 0 ? ERROR_NOINIT : 0;

    // ------------------------------------------------------------------ MemAllocator (games subclass it)

    /// <summary>vtable: ~MemAllocator, deleting ~MemAllocator, allocate(size, user) and deallocate(ptr, user)
    /// (pure: abort), notifyError(code, size, user).</summary>
    [Hle("_ZN3sce4Json12MemAllocatorC1Ev")]
    [Hle("_ZN3sce4Json12MemAllocatorC2Ev")]
    [Hle("_ZN3sce4Json12MemAllocatorD1Ev")]
    [Hle("_ZN3sce4Json12MemAllocatorD2Ev")]
    uint MemAllocatorCtor(uint thisPtr)
    {
        W32(thisPtr, Data + DVptr);
        return thisPtr;
    }

    [Hle("_ZN3sce4Json12MemAllocatorD0Ev")]
    uint MemAllocatorDeletingDtor(uint thisPtr)
    {
        W32(thisPtr, Data + DVptr);
        Delete(thisPtr);
        return thisPtr;
    }

    [Hle("_ZN3sce4Json12MemAllocator11notifyErrorEijPv")]
    void MemAllocatorNotifyError(uint thisPtr, int code, uint size, uint user)
    {
        if (code == ERROR_NOMEM) Log.Info($"[libSceJson] Allocation Fail. size = {(int)size}");
        else Log.Info($"[libSceJson] unknown notification[0x{code:x}]");
    }

    // ------------------------------------------------------------------ Value

    [Hle("_ZN3sce4Json5ValueC1Ev")]
    [Hle("_ZN3sce4Json5ValueC2Ev")]
    uint ValueCtor(uint thisPtr) => ValueCtorType(thisPtr, TNull);

    [Hle("_ZN3sce4Json5ValueC1ENS0_9ValueTypeE")]
    [Hle("_ZN3sce4Json5ValueC2ENS0_9ValueTypeE")]
    uint ValueCtorWithType(uint thisPtr, int type) => ValueCtorType(thisPtr, type);

    [Hle("_ZN3sce4Json5ValueC1Eb")]
    [Hle("_ZN3sce4Json5ValueC2Eb")]
    uint ValueCtorBool(uint thisPtr, uint b)
    {
        ValueCtorType(thisPtr, TBool);
        W8(thisPtr + 8, (byte)b);
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5ValueC1Ex")]
    [Hle("_ZN3sce4Json5ValueC2Ex")]
    uint ValueCtorInt(uint thisPtr, long v)
    {
        ValueCtorType(thisPtr, TInt);
        W64(thisPtr + 8, (ulong)v);
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5ValueC1Ey")]
    [Hle("_ZN3sce4Json5ValueC2Ey")]
    uint ValueCtorUInt(uint thisPtr, ulong v)
    {
        ValueCtorType(thisPtr, TUInt);
        W64(thisPtr + 8, v);
        return thisPtr;
    }

    /// <summary>The double argument is in d0 (hard-float VFP ABI).</summary>
    [Hle("_ZN3sce4Json5ValueC1Ed")]
    [Hle("_ZN3sce4Json5ValueC2Ed")]
    uint ValueCtorReal(HleContext ctx, uint thisPtr)
    {
        double v = HleContext.ArgF64(ctx, 0);
        ValueCtorType(thisPtr, TReal);
        W64(thisPtr + 8, BitConverter.DoubleToUInt64Bits(v));
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5ValueC1ERKNS0_6StringE")]
    [Hle("_ZN3sce4Json5ValueC2ERKNS0_6StringE")]
    uint ValueCtorString(uint thisPtr, uint s)
    {
        ValueInit(thisPtr);
        ValueSetString(thisPtr, s);
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5ValueC1ERKNS0_5ArrayE")]
    [Hle("_ZN3sce4Json5ValueC2ERKNS0_5ArrayE")]
    uint ValueCtorArray(uint thisPtr, uint a)
    {
        ValueInit(thisPtr);
        ValueSetArray(thisPtr, a);
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5ValueC1ERKNS0_6ObjectE")]
    [Hle("_ZN3sce4Json5ValueC2ERKNS0_6ObjectE")]
    uint ValueCtorObject(uint thisPtr, uint o)
    {
        ValueInit(thisPtr);
        ValueSetObject(thisPtr, o);
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5ValueC1ERKS1_")]
    [Hle("_ZN3sce4Json5ValueC2ERKS1_")]
    uint ValueCtorValue(uint thisPtr, uint other) => ValueCtorCopy(thisPtr, other);

    [Hle("_ZN3sce4Json5ValueD1Ev")]
    [Hle("_ZN3sce4Json5ValueD2Ev")]
    uint ValueDestructor(uint thisPtr) => ValueDtor(thisPtr);

    [Hle("_ZN3sce4Json5Value3setENS0_9ValueTypeE")]
    void ValueSetType(uint thisPtr, int type) => SetType(thisPtr, type);

    [Hle("_ZN3sce4Json5Value3setEb")]
    void ValueSetBool(uint thisPtr, uint b)
    {
        SetType(thisPtr, TBool);
        W8(thisPtr + 8, (byte)b);
    }

    [Hle("_ZN3sce4Json5Value3setEx")]
    void ValueSetInt(uint thisPtr, long v)
    {
        SetType(thisPtr, TInt);
        W64(thisPtr + 8, (ulong)v);
    }

    [Hle("_ZN3sce4Json5Value3setEy")]
    void ValueSetUInt(uint thisPtr, ulong v)
    {
        SetType(thisPtr, TUInt);
        W64(thisPtr + 8, v);
    }

    [Hle("_ZN3sce4Json5Value3setEd")]
    void ValueSetReal(HleContext ctx, uint thisPtr)
    {
        double v = HleContext.ArgF64(ctx, 0);
        SetType(thisPtr, TReal);
        W64(thisPtr + 8, BitConverter.DoubleToUInt64Bits(v));
    }

    [Hle("_ZN3sce4Json5Value3setERKNS0_6StringE")]
    void ValueSetStringRef(uint thisPtr, uint s) => ValueSetString(thisPtr, s);

    [Hle("_ZN3sce4Json5Value3setERKNS0_5ArrayE")]
    void ValueSetArrayRef(uint thisPtr, uint a) => ValueSetArray(thisPtr, a);

    [Hle("_ZN3sce4Json5Value3setERKNS0_6ObjectE")]
    void ValueSetObjectRef(uint thisPtr, uint o) => ValueSetObject(thisPtr, o);

    [Hle("_ZN3sce4Json5Value3setERKS1_")]
    void ValueSetValue(uint thisPtr, uint other) => ValueSet(thisPtr, other);

    [Hle("_ZN3sce4Json5ValueaSERKS1_")]
    uint ValueAssign(uint thisPtr, uint other)
    {
        ValueSet(thisPtr, other);
        return thisPtr;
    }

    /// <summary>Three value copies through a temporary; parent and root info stay with each object.</summary>
    [Hle("_ZN3sce4Json5Value4swapERS1_")]
    void ValueSwap(uint thisPtr, uint other)
    {
        uint saved = t_sp;
        try
        {
            uint tmp = Alloca(0x18);
            ValueInit(tmp);
            ValueSet(tmp, thisPtr);
            ValueSet(thisPtr, other);
            ValueSet(other, tmp);
            ValueDtor(tmp);
        }
        finally { t_sp = saved; }
    }

    [Hle("_ZN3sce4Json5Value5clearEv")]
    void ValueClear(uint thisPtr) => SetType(thisPtr, TNull);

    [Hle("_ZNK3sce4Json5Value7getTypeEv")]
    int ValueGetType(uint thisPtr) => (int)R32(thisPtr + 0x14);

    [Hle("_ZNK3sce4Json5Value7getRootEv")]
    uint ValueGetRoot(uint thisPtr) => RootOf(thisPtr);

    [Hle("_ZNK3sce4Json5Value5countEv")]
    uint ValueCount(uint thisPtr)
    {
        int t = (int)R32(thisPtr + 0x14);
        return t is TArray or TObject ? StringSize(R32(thisPtr + 8)) : 0;
    }

    [Hle("_ZNK3sce4Json5ValuecvbEv")]
    bool ValueToBool(uint thisPtr)
    {
        switch ((int)R32(thisPtr + 0x14))
        {
            case TNull: return false;
            case TBool: return R8(thisPtr + 8) != 0;
            case TInt: case TUInt: return R64(thisPtr + 8) != 0;
            case TReal: return BitConverter.UInt64BitsToDouble(R64(thisPtr + 8)) != 0.0;
            case TString: return !StringEmpty(R32(thisPtr + 8));
            default: return true;
        }
    }

    [Hle("_ZNK3sce4Json5Value8toStringERNS0_6StringE")]
    void ValueToString(uint thisPtr, uint outStr) => ToStringAppend(thisPtr, outStr);

    [Hle("_ZN3sce4Json5Value9serializeERNS0_6StringE")]
    int ValueSerialize(uint thisPtr, uint outStr) => R32(Data + DInfo) == 0 ? ERROR_NOINIT : Serialize(thisPtr, outStr, 0, 0, 0);

    /// <summary>serialize with a flush callback <c>int cb(String&amp; out, void* user)</c>, called after every token;
    /// a negative result stops the serialization and is returned.</summary>
    [Hle("_ZN3sce4Json5Value9serializeERNS0_6StringEPFiS3_PvES4_")]
    int ValueSerializeCb(uint thisPtr, uint outStr, uint cb, uint user) =>
        R32(Data + DInfo) == 0 ? ERROR_NOINIT : Serialize(thisPtr, outStr, cb, user, 0);

    /// <summary>Only on a root that went through the parser or serializer (it then has its root info).</summary>
    [Hle("_ZN3sce4Json5Value21setNullAccessCallbackEPFRKS1_NS0_9ValueTypeEPS2_PvES6_")]
    int ValueSetNullAccessCallback(uint thisPtr, uint cb, uint user)
    {
        if (R32(thisPtr) != 0) return ERROR_NOROOT;
        uint ri = R32(thisPtr + 4);
        if (ri == 0) return ERROR_NOROOT;
        W32(ri, cb);
        W32(ri + 4, user);
        return 0;
    }

    // typed getters: a null value consults the null-access callback; a mismatch returns the module's default

    uint TypedGet(uint thisPtr, int type, uint defaultAddr, bool payloadIsPointer, int alt = -1)
    {
        while (true)
        {
            int t = (int)R32(thisPtr + 0x14);
            if (t == TNull)
            {
                uint sub = NullAccess(thisPtr, type);
                if (sub != 0) { thisPtr = sub; continue; }
            }
            if (t == type || t == alt) return payloadIsPointer ? R32(thisPtr + 8) : thisPtr + 8;
            return payloadIsPointer ? R32(defaultAddr) : defaultAddr;
        }
    }

    [Hle("_ZNK3sce4Json5Value9getStringEv")]
    uint ValueGetString(uint thisPtr) => TypedGet(thisPtr, TString, Data + DString, true);

    [Hle("_ZNK3sce4Json5Value8getArrayEv")]
    uint ValueGetArray(uint thisPtr) => TypedGet(thisPtr, TArray, Data + DArray, true);

    [Hle("_ZNK3sce4Json5Value9getObjectEv")]
    uint ValueGetObject(uint thisPtr) => TypedGet(thisPtr, TObject, Data + DObject, true);

    [Hle("_ZNK3sce4Json5Value10getIntegerEv")]
    uint ValueGetInteger(uint thisPtr) => TypedGet(thisPtr, TInt, Data + DInt0, false, TUInt);

    [Hle("_ZNK3sce4Json5Value11getUIntegerEv")]
    uint ValueGetUInteger(uint thisPtr) => TypedGet(thisPtr, TUInt, Data + DUInt0, false, TInt);

    [Hle("_ZNK3sce4Json5Value7getRealEv")]
    uint ValueGetReal(uint thisPtr) => TypedGet(thisPtr, TReal, Data + DReal0, false);

    [Hle("_ZNK3sce4Json5Value10getBooleanEv")]
    uint ValueGetBoolean(uint thisPtr) => TypedGet(thisPtr, TBool, Data + DBool0, false);

    [Hle("_ZNK3sce4Json5Value8getValueEj")]
    [Hle("_ZNK3sce4Json5ValueixEj")]
    uint ValueGetValueIndex(uint thisPtr, uint idx) => GetValueIndex(thisPtr, idx);

    [Hle("_ZNK3sce4Json5Value8getValueERKNS0_6StringE")]
    [Hle("_ZNK3sce4Json5ValueixERKNS0_6StringE")]
    uint ValueGetValueKey(uint thisPtr, uint key) => GetValueKey(thisPtr, key);

    [Hle("_ZNK3sce4Json5ValueixEPKc")]
    uint ValueIndexCStr(uint thisPtr, uint key)
    {
        uint saved = t_sp;
        try
        {
            uint tmp = Alloca(4);
            StringCtorCStr(tmp, key);
            uint r = GetValueKey(thisPtr, tmp);
            StringDtor(tmp);
            return r;
        }
        finally { t_sp = saved; }
    }

    // mutable accessors: the payload when the type matches, else 0

    [Hle("_ZN3sce4Json5Value11referStringEv")]
    uint ValueReferString(uint thisPtr) => R32(thisPtr + 0x14) == TString ? R32(thisPtr + 8) : 0;

    [Hle("_ZN3sce4Json5Value10referArrayEv")]
    uint ValueReferArray(uint thisPtr) => R32(thisPtr + 0x14) == TArray ? R32(thisPtr + 8) : 0;

    [Hle("_ZN3sce4Json5Value11referObjectEv")]
    uint ValueReferObject(uint thisPtr) => R32(thisPtr + 0x14) == TObject ? R32(thisPtr + 8) : 0;

    [Hle("_ZN3sce4Json5Value12referIntegerEv")]
    [Hle("_ZN3sce4Json5Value13referUIntegerEv")]
    uint ValueReferInteger(uint thisPtr) => R32(thisPtr + 0x14) is TInt or TUInt ? thisPtr + 8 : 0;

    [Hle("_ZN3sce4Json5Value9referRealEv")]
    uint ValueReferReal(uint thisPtr) => R32(thisPtr + 0x14) == TReal ? thisPtr + 8 : 0;

    [Hle("_ZN3sce4Json5Value12referBooleanEv")]
    uint ValueReferBoolean(uint thisPtr) => R32(thisPtr + 0x14) == TBool ? thisPtr + 8 : 0;

    [Hle("_ZN3sce4Json5Value10referValueEj")]
    uint ValueReferValueIndex(uint thisPtr, uint idx) => ReferValueIndex(thisPtr, idx);

    [Hle("_ZN3sce4Json5Value10referValueERKNS0_6StringE")]
    uint ValueReferValueKey(uint thisPtr, uint key) => R32(thisPtr + 0x14) == TObject ? FindMember(thisPtr, key) : 0;

    // ------------------------------------------------------------------ Parser (static members)

    [Hle("_ZN3sce4Json6Parser5parseERNS0_5ValueEPFiRcPvES5_")]
    int ParserParseCallback(uint value, uint cb, uint user)
    {
        if (R32(Data + DInfo) == 0) return ERROR_NOINIT;
        uint saved = t_sp;
        try
        {
            var r = new Reader { Read = ReadCallback, Cb = cb, User = user, Scratch = Alloca(8) };
            return RunParse(value, r);
        }
        finally { t_sp = saved; }
    }

    [Hle("_ZN3sce4Json6Parser5parseERNS0_5ValueEPKcj")]
    int ParserParseBuffer(uint value, uint buf, uint size)
    {
        if (R32(Data + DInfo) == 0) return ERROR_NOINIT;
        return RunParse(value, new Reader { Read = ReadBuffer, Pos = buf, End = buf + size });
    }

    /// <summary>Reads the file with sceIo* through a buffer of InitParameter's size taken from the allocator.</summary>
    [Hle("_ZN3sce4Json6Parser5parseERNS0_5ValueEPKc")]
    int ParserParseFile(uint value, uint path)
    {
        uint d = Data;
        uint info = R32(d + DInfo);
        if (info == 0) return ERROR_NOINIT;
        uint bufSize = R32(info + 8);
        if (bufSize == 0) return ERROR_NOBUF;
        var r = new Reader { Read = ReadFile };
        uint buf = 0;
        uint alloc = R32(info);
        if (alloc != 0)
        {
            buf = CallVirt(alloc, 2, bufSize, R32(info + 4));
            if (buf == 0)
            {
                uint i2 = R32(d + DInfo);
                CallVirt(R32(i2), 4, unchecked((uint)ERROR_NOMEM), bufSize, R32(i2 + 4));
                LockMutex();
                if (R32(d + DParseCount) != 0) W8(d + DAbort, 1);
                UnlockMutex();
            }
        }
        if (buf != 0)
        {
            r.Buf = buf;
            r.BufSize = bufSize;
            r.Fd = (int)Call(StubAddr(Stub.IoOpen), path, 1, 0x100);
        }
        int result = R32(d + DInfo) == 0 ? ERROR_NOINIT : RunParse(value, r);
        if (r.Fd >= 0) Call(StubAddr(Stub.IoClose), (uint)r.Fd);
        info = R32(d + DInfo);
        if (info != 0 && R32(info) != 0) CallVirt(R32(info), 3, buf, R32(info + 4));
        return result;
    }

    // ------------------------------------------------------------------ String

    [Hle("_ZN3sce4Json6StringC1Ev")]
    [Hle("_ZN3sce4Json6StringC2Ev")]
    uint StringConstruct(uint thisPtr) => StringCtor(thisPtr);

    [Hle("_ZN3sce4Json6StringC1EPKc")]
    [Hle("_ZN3sce4Json6StringC2EPKc")]
    uint StringConstructCStr(uint thisPtr, uint s) => StringCtorCStr(thisPtr, s);

    [Hle("_ZN3sce4Json6StringC1ERKS1_")]
    [Hle("_ZN3sce4Json6StringC2ERKS1_")]
    uint StringConstructCopy(uint thisPtr, uint other) => StringCtorCopy(thisPtr, other);

    [Hle("_ZN3sce4Json6StringD1Ev")]
    [Hle("_ZN3sce4Json6StringD2Ev")]
    uint StringDestruct(uint thisPtr) => StringDtor(thisPtr);

    [Hle("_ZN3sce4Json6StringaSERKS1_")]
    uint StringOperatorAssign(uint thisPtr, uint other) => StringAssign(thisPtr, other);

    [Hle("_ZN3sce4Json6String6appendERKS1_")]
    uint StringAppendString(uint thisPtr, uint other) => AppendString(thisPtr, other);

    [Hle("_ZN3sce4Json6String6appendEPKc")]
    [Hle("_ZN3sce4Json6StringpLEPKc")]
    uint StringAppendCStr(uint thisPtr, uint s) => AppendCStr(thisPtr, s);

    [Hle("_ZN3sce4Json6String6appendEPKcj")]
    uint StringAppendN(uint thisPtr, uint s, uint n) => R32(thisPtr) == 0 ? thisPtr : AppendGuest(thisPtr, s, n);

    [Hle("_ZN3sce4Json6StringpLEh")]
    uint StringAppendByte(uint thisPtr, uint c) => AppendChar(thisPtr, (byte)c);

    [Hle("_ZN3sce4Json6String5clearEv")]
    void StringClearExport(uint thisPtr) => StringClear(thisPtr);

    [Hle("_ZN3sce4Json6String6resizeEj")]
    void StringResize(uint thisPtr, uint n)
    {
        uint rep = R32(thisPtr);
        if (rep == 0) return;
        uint size = R32(rep + 4);
        if (n < size) { RepErase(rep, n, uint.MaxValue); return; }
        if (n <= size) return;
        uint grow = n - size;
        uint p = New(n + 1);
        if (p == 0) return;
        size = R32(rep + 4);
        GuestMemory.Copy(p, R32(rep), size);
        for (uint i = 0; i < grow; i++) W8(p + size + i, 0);
        W8(p + size + grow, 0);
        if (R32(rep) != Empty) Delete(R32(rep));
        W32(rep, p);
        W32(rep + 4, size + grow);
    }

    [Hle("_ZNK3sce4Json6String4sizeEv")]
    [Hle("_ZNK3sce4Json6String6lengthEv")]
    [Hle("_ZNK3sce4Json5Array4sizeEv")]
    [Hle("_ZNK3sce4Json6Object4sizeEv")]
    uint SizeOf(uint thisPtr) => StringSize(thisPtr);

    [Hle("_ZNK3sce4Json6String5emptyEv")]
    bool StringIsEmpty(uint thisPtr) => StringEmpty(thisPtr);

    [Hle("_ZNK3sce4Json6String5c_strEv")]
    uint StringGetCStr(uint thisPtr) => StringCStr(thisPtr);

    [Hle("_ZNK3sce4Json6String2atEj")]
    uint StringGetAt(uint thisPtr, uint i) => StringAt(thisPtr, i);

    [Hle("_ZNK3sce4Json6String7compareERKS1_")]
    int StringCompare(uint thisPtr, uint other)
    {
        if (R32(thisPtr) == 0 || R32(other) == 0) return 0;
        return Strcmp(R32(R32(thisPtr)), R32(R32(other)));
    }

    /// <summary>Copies the argument into a temporary rep first (one allocation), like the firmware module.</summary>
    [Hle("_ZNK3sce4Json6String7compareEPKc")]
    int StringCompareCStr(uint thisPtr, uint s)
    {
        uint rep = R32(thisPtr);
        if (rep == 0) return 0;
        uint saved = t_sp;
        try
        {
            uint tmp = Alloca(8);
            W32(tmp, Empty);
            W32(tmp + 4, 0);
            RepAssign(tmp, s, s != 0 ? StrLen(s) : 0);
            int r = Strcmp(R32(rep), R32(tmp));
            if (R32(tmp) != Empty) Delete(R32(tmp));
            return r;
        }
        finally { t_sp = saved; }
    }

    [Hle("_ZNK3sce4Json6StringeqERKS1_")]
    bool StringEquals(uint thisPtr, uint other)
    {
        if (R32(thisPtr) == 0 || R32(other) == 0) return false;
        uint a = R32(R32(thisPtr)), b = R32(R32(other));
        return R8(a) == R8(b) && Strcmp(a, b) == 0;
    }

    [Hle("_ZNK3sce4Json6StringeqEPKc")]
    bool StringEqualsCStr(uint thisPtr, uint s)
    {
        if (R32(thisPtr) == 0) return false;
        uint a = R32(R32(thisPtr));
        return R8(a) == R8(s) && Strcmp(a, s) == 0;
    }

    [Hle("_ZNK3sce4Json6String4findERKS1_j")]
    uint StringFindString(uint thisPtr, uint other, uint pos)
    {
        uint rep = R32(thisPtr);
        if (rep == 0) return uint.MaxValue;
        uint s = R32(other) != 0 ? R32(R32(other)) : Data + DLit;
        return FindIn(rep, s, StrLen(s), pos);
    }

    [Hle("_ZNK3sce4Json6String4findEPKcj")]
    uint StringFindCStr(uint thisPtr, uint s, uint pos)
    {
        uint rep = R32(thisPtr);
        return rep == 0 ? uint.MaxValue : FindIn(rep, s, StrLen(s), pos);
    }

    [Hle("_ZNK3sce4Json6String4findEPKcjj")]
    uint StringFindN(uint thisPtr, uint s, uint pos, uint n)
    {
        uint rep = R32(thisPtr);
        return rep == 0 ? uint.MaxValue : FindIn(rep, s, n, pos);
    }

    [Hle("_ZNK3sce4Json6String4findEcj")]
    uint StringFindChar(uint thisPtr, uint c, uint pos)
    {
        uint rep = R32(thisPtr);
        if (rep == 0) return uint.MaxValue;
        uint size = R32(rep + 4);
        if (pos >= size) return uint.MaxValue;
        uint q = Strchr(R32(rep) + pos, (sbyte)c);
        return q == 0 ? uint.MaxValue : q - R32(rep);
    }

    [Hle("_ZNK3sce4Json6String5rfindERKS1_j")]
    uint StringRFindString(uint thisPtr, uint other, uint pos)
    {
        uint rep = R32(thisPtr);
        if (rep == 0) return uint.MaxValue;
        uint s = R32(other) != 0 ? R32(R32(other)) : Data + DLit;
        return RFind(rep, s, pos, StrLen(s));
    }

    [Hle("_ZNK3sce4Json6String5rfindEPKcj")]
    uint StringRFindCStr(uint thisPtr, uint s, uint pos)
    {
        uint rep = R32(thisPtr);
        return rep == 0 ? uint.MaxValue : RFind(rep, s, pos, StrLen(s));
    }

    [Hle("_ZNK3sce4Json6String5rfindEPKcjj")]
    uint StringRFindN(uint thisPtr, uint s, uint pos, uint n)
    {
        uint rep = R32(thisPtr);
        return rep == 0 ? uint.MaxValue : RFind(rep, s, pos, n);
    }

    [Hle("_ZNK3sce4Json6String5rfindEcj")]
    uint StringRFindChar(uint thisPtr, uint c, uint pos)
    {
        uint rep = R32(thisPtr);
        if (rep == 0) return uint.MaxValue;
        uint size = R32(rep + 4), data = R32(rep);
        if (size == 0) return uint.MaxValue;
        for (uint p = data + Math.Min(pos, size - 1); ; p--)
        {
            if (R8(p) == (byte)c) return p - data;
            if (p == data) return uint.MaxValue;
        }
    }

    /// <summary>Returns a new String (hidden result pointer in r0, this in r1). Without a rep the result copies the
    /// module's empty String.</summary>
    [Hle("_ZNK3sce4Json6String6substrEjj")]
    uint StringSubstr(uint ret, uint thisPtr, uint pos, uint len)
    {
        uint rep = R32(thisPtr);
        if (rep == 0)
        {
            uint g = R32(Data + DString);
            W32(ret, 0);
            if (R32(g) == 0) return ret;
            StringCtor(ret);
            if (R32(ret) != 0) RepCopy(R32(ret), R32(g));
            return ret;
        }
        uint saved = t_sp;
        try
        {
            uint tmp = Alloca(8);
            uint size = R32(rep + 4);
            if (pos > size) { OutOfRange("substr"); size = R32(rep + 4); }
            W32(tmp, Empty);
            W32(tmp + 4, 0);
            if (pos < size)
            {
                uint avail = size - pos;
                if (len == uint.MaxValue || pos + len > size) len = avail;
                RepAssign(tmp, R32(rep) + pos, len);
            }
            uint text = R32(tmp);
            // a String(const char*) of the substring, copied into the result
            uint t2 = RepNew();
            if (t2 != 0) RepAssign(t2, text, text != 0 ? StrLen(text) : 0);
            W32(ret, 0);
            if (t2 != 0)
            {
                uint r3 = RepNew();
                if (r3 != 0)
                {
                    W32(ret, r3);
                    RepCopy(r3, t2);
                }
                RepFree(t2);
            }
            if (R32(tmp) != Empty) Delete(R32(tmp));
        }
        finally { t_sp = saved; }
        return ret;
    }

    // ------------------------------------------------------------------ Array

    [Hle("_ZN3sce4Json5ArrayC1Ev")]
    [Hle("_ZN3sce4Json5ArrayC2Ev")]
    uint ArrayConstruct(uint thisPtr) => ArrayCtor(thisPtr);

    [Hle("_ZN3sce4Json5ArrayC1ERKS1_")]
    [Hle("_ZN3sce4Json5ArrayC2ERKS1_")]
    uint ArrayConstructCopy(uint thisPtr, uint other) => ArrayCtorCopy(thisPtr, other);

    [Hle("_ZN3sce4Json5ArrayD1Ev")]
    [Hle("_ZN3sce4Json5ArrayD2Ev")]
    uint ArrayDestruct(uint thisPtr) => ArrayDtor(thisPtr);

    [Hle("_ZN3sce4Json5ArrayaSERKS1_")]
    uint ArrayOperatorAssign(uint thisPtr, uint other) => ArrayAssign(thisPtr, other);

    [Hle("_ZN3sce4Json5Array9push_backERKNS0_5ValueE")]
    void ArrayPushBackExport(uint thisPtr, uint value) => ArrayPushBack(thisPtr, value);

    [Hle("_ZN3sce4Json5Array8pop_backEv")]
    void ArrayPopBackExport(uint thisPtr) => ArrayPopBack(thisPtr);

    [Hle("_ZN3sce4Json5Array5clearEv")]
    void ArrayClear(uint thisPtr)
    {
        uint impl = R32(thisPtr);
        if (impl != 0) ListClear(impl);
    }

    [Hle("_ZNK3sce4Json5Array4backEv")]
    uint ArrayBackExport(uint thisPtr) => ArrayBack(thisPtr);

    [Hle("_ZNK3sce4Json5Array5emptyEv")]
    [Hle("_ZNK3sce4Json6Object5emptyEv")]
    bool ContainerEmpty(uint thisPtr)
    {
        uint impl = R32(thisPtr);
        return impl == 0 || R32(impl + 4) == 0;
    }

    [Hle("_ZNK3sce4Json5Array5beginEv")]
    [Hle("_ZNK3sce4Json6Object5beginEv")]
    uint ContainerBegin(uint ret, uint thisPtr)
    {
        W32(ret, 0);
        W32(ret, BeginCell(thisPtr));
        return ret;
    }

    [Hle("_ZNK3sce4Json5Array3endEv")]
    [Hle("_ZNK3sce4Json6Object3endEv")]
    uint ContainerEnd(uint ret, uint thisPtr)
    {
        W32(ret, 0);
        W32(ret, EndCell(thisPtr));
        return ret;
    }

    [Hle("_ZN3sce4Json5Array6insertERKNS1_8iteratorERKNS0_5ValueE")]
    uint ArrayInsert(uint ret, uint thisPtr, uint pos, uint value)
    {
        W32(ret, 0);
        uint c = NewCell();
        W32(ret, c);
        uint impl = R32(thisPtr);
        if (impl == 0) return ret;
        uint n = ListInsert(impl, R32(R32(pos)), value);
        if (n == 0) { FreeCell(c); W32(ret, 0); }
        else if (c != 0) W32(c, n);
        return ret;
    }

    /// <summary>Returns an iterator to the following element (no check that the position is not end()).</summary>
    [Hle("_ZN3sce4Json5Array5eraseERKNS1_8iteratorE")]
    uint ArrayErase(uint ret, uint thisPtr, uint it)
    {
        W32(ret, 0);
        uint c = NewCell();
        W32(ret, c);
        uint impl = R32(thisPtr);
        if (impl == 0) return ret;
        uint node = R32(R32(it));
        uint prev = R32(node), next = R32(node + 4);
        W32(next, prev);
        W32(prev + 4, R32(node + 4));
        ValueDtor(node + 8);
        Delete(node);
        W32(impl + 4, R32(impl + 4) - 1);
        if (next == 0) { FreeCell(c); W32(ret, 0); }
        else if (c != 0) W32(c, next);
        return ret;
    }

    // ------------------------------------------------------------------ iterators (Array and Object share most code)

    [Hle("_ZN3sce4Json5Array8iteratorC1Ev")]
    [Hle("_ZN3sce4Json5Array8iteratorC2Ev")]
    [Hle("_ZN3sce4Json6Object8iteratorC1Ev")]
    [Hle("_ZN3sce4Json6Object8iteratorC2Ev")]
    uint IteratorCtor(uint thisPtr)
    {
        W32(thisPtr, 0);
        W32(thisPtr, NewCell());
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5Array8iteratorC1ERKS2_")]
    [Hle("_ZN3sce4Json5Array8iteratorC2ERKS2_")]
    [Hle("_ZN3sce4Json6Object8iteratorC1ERKS2_")]
    [Hle("_ZN3sce4Json6Object8iteratorC2ERKS2_")]
    uint IteratorCtorCopy(uint thisPtr, uint other)
    {
        W32(thisPtr, 0);
        if (R32(other) == 0) return thisPtr;
        uint c = NewCell();
        if (c == 0) return thisPtr;
        W32(thisPtr, c);
        W32(c, R32(R32(other)));
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5Array8iteratorD1Ev")]
    [Hle("_ZN3sce4Json5Array8iteratorD2Ev")]
    [Hle("_ZN3sce4Json6Object8iteratorD1Ev")]
    [Hle("_ZN3sce4Json6Object8iteratorD2Ev")]
    uint IteratorDtor(uint thisPtr)
    {
        uint c = R32(thisPtr);
        if (c != 0)
        {
            Delete(c);
            W32(thisPtr, 0);
        }
        return thisPtr;
    }

    /// <summary>Copies the position into the existing cell; a cell-less iterator stays cell-less, and copying an
    /// empty position frees the cell.</summary>
    [Hle("_ZN3sce4Json5Array8iteratoraSERKS2_")]
    [Hle("_ZN3sce4Json6Object8iteratoraSERKS2_")]
    uint IteratorAssign(uint thisPtr, uint other)
    {
        uint c = R32(thisPtr);
        if (c == 0) return thisPtr;
        uint oc = R32(other);
        uint node = oc != 0 ? R32(oc) : 0;
        if (node == 0)
        {
            Delete(c);
            W32(thisPtr, 0);
        }
        else W32(c, node);
        return thisPtr;
    }

    [Hle("_ZNK3sce4Json5Array8iteratorneES2_")]
    [Hle("_ZNK3sce4Json6Object8iteratorneES2_")]
    bool IteratorNotEqual(uint thisPtr, uint other) => CellsDiffer(R32(thisPtr), R32(other));

    [Hle("_ZNK3sce4Json6Object8iteratoreqES2_")]
    bool IteratorEqual(uint thisPtr, uint other) => CellsEqual(R32(thisPtr), R32(other));

    /// <summary>Both increments return this (one shared function in the firmware module).</summary>
    [Hle("_ZN3sce4Json5Array8iteratorppEv")]
    [Hle("_ZN3sce4Json5Array8iteratorppEi")]
    uint ArrayIteratorIncrement(uint thisPtr)
    {
        uint c = R32(thisPtr);
        if (c != 0 && R32(c) != 0) W32(c, R32(R32(c) + 4));
        return thisPtr;
    }

    [Hle("_ZN3sce4Json5Array8iterator7advanceEj")]
    void ArrayIteratorAdvance(uint thisPtr, uint n) => ArrayAdvance(R32(thisPtr), n);

    [Hle("_ZNK3sce4Json5Array8iteratordeEv")]
    [Hle("_ZNK3sce4Json5Array8iteratorptEv")]
    uint ArrayIteratorDeref(uint thisPtr) => ArrayDeref(R32(thisPtr));

    [Hle("_ZN3sce4Json6Object8iteratorppEv")]
    [Hle("_ZN3sce4Json6Object8iteratorppEi")]
    uint ObjectIteratorIncrement(uint thisPtr)
    {
        uint c = R32(thisPtr);
        if (c != 0 && R32(c) != 0) W32(c, TreeNext(R32(c)));
        return thisPtr;
    }

    [Hle("_ZN3sce4Json6Object8iterator7advanceEj")]
    void ObjectIteratorAdvance(uint thisPtr, uint n) => ObjectAdvance(R32(thisPtr), n);

    [Hle("_ZNK3sce4Json6Object8iteratordeEv")]
    [Hle("_ZNK3sce4Json6Object8iteratorptEv")]
    uint ObjectIteratorDeref(uint thisPtr) => ObjectDeref(R32(thisPtr));

    // ------------------------------------------------------------------ Object

    [Hle("_ZN3sce4Json6ObjectC1Ev")]
    [Hle("_ZN3sce4Json6ObjectC2Ev")]
    uint ObjectConstruct(uint thisPtr) => ObjectCtor(thisPtr);

    [Hle("_ZN3sce4Json6ObjectC1ERKS1_")]
    [Hle("_ZN3sce4Json6ObjectC2ERKS1_")]
    uint ObjectConstructCopy(uint thisPtr, uint other) => ObjectCtorCopy(thisPtr, other);

    [Hle("_ZN3sce4Json6ObjectD1Ev")]
    [Hle("_ZN3sce4Json6ObjectD2Ev")]
    uint ObjectDestruct(uint thisPtr) => ObjectDtor(thisPtr);

    [Hle("_ZN3sce4Json6ObjectaSERKS1_")]
    uint ObjectOperatorAssign(uint thisPtr, uint other) => ObjectAssign(thisPtr, other);

    /// <summary>The member's Value, inserted as null when absent; the module's null Value when out of memory.</summary>
    [Hle("_ZN3sce4Json6ObjectixERKNS0_6StringE")]
    uint ObjectOperatorIndex(uint thisPtr, uint key) => ObjectIndex(thisPtr, key);

    [Hle("_ZN3sce4Json6Object6insertERKNS1_4PairE")]
    uint ObjectInsert(uint ret, uint thisPtr, uint pair)
    {
        W32(ret, 0);
        W32(ret, ObjectInsertCell(thisPtr, pair));
        return ret;
    }

    [Hle("_ZNK3sce4Json6Object4findERKNS0_6StringE")]
    uint ObjectFind(uint ret, uint thisPtr, uint key)
    {
        W32(ret, 0);
        W32(ret, ObjectFindCell(thisPtr, key));
        return ret;
    }

    [Hle("_ZN3sce4Json6Object5eraseERKNS0_6StringE")]
    void ObjectErase(uint thisPtr, uint key) => ObjectEraseKey(thisPtr, key);

    [Hle("_ZN3sce4Json6Object5clearEv")]
    void ObjectClear(uint thisPtr)
    {
        uint impl = R32(thisPtr);
        if (impl != 0) TreeClear(impl);
    }

    [Hle("_ZN3sce4Json6Object4PairC1Ev")]
    [Hle("_ZN3sce4Json6Object4PairC2Ev")]
    uint PairConstruct(uint thisPtr) => PairCtor(thisPtr);

    [Hle("_ZN3sce4Json6Object4PairC1ERKNS0_6StringERKNS0_5ValueE")]
    [Hle("_ZN3sce4Json6Object4PairC2ERKNS0_6StringERKNS0_5ValueE")]
    uint PairConstructKeyValue(uint thisPtr, uint key, uint value) => PairCtorKeyValue(thisPtr, key, value);

    [Hle("_ZN3sce4Json6Object4PairD1Ev")]
    [Hle("_ZN3sce4Json6Object4PairD2Ev")]
    uint PairDestruct(uint thisPtr) => PairDtor(thisPtr);
}
