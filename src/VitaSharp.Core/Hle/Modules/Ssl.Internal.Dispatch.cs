using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceSslInternal's dispatchers of libssl.suprx 3.74 (read in its code, study only): calls that look a function up in
// an object's method table (the object's first word, or the table behind its +4) and run it with the caller's
// arguments. A NULL object is 0x2721 (the library's invalid-argument code), a missing method 0x271B; the BIO calls
// keep their callback protocol (the object's +4 runs before and after the method). libssl's error queue is not kept.
public sealed partial class Ssl
{
    /// <summary>The method at +offset of the object's table (its first word); 0x2721 for a NULL object or a required
    /// argument 0, 0x271B for no method.</summary>
    static void DispatchTable(HleContext context, int offset, bool argumentsValid)
    {
        uint table = context.Gpr(0) != 0 && argumentsValid ? GuestMemory.R32(context.Gpr(0)) : 0;
        if (context.Gpr(0) == 0 || !argumentsValid)
        {
            context.Return(MesInvalidArgument);
            return;
        }

        uint method = GuestMemory.R32(table + (uint)offset);
        context.Return(method == 0 ? MesNotSupported : CallMethod(context, method));
    }

    /// <summary>The method at +offset of the table behind the object's +4 (and, with <paramref name="twice"/>, that
    /// table's +4); NULL object 0x2721. These calls do not check the method.</summary>
    static void DispatchInner(HleContext context, int offset, bool twice)
    {
        uint objectAddress = context.Gpr(0);
        if (objectAddress == 0)
        {
            context.Return(MesInvalidArgument);
            return;
        }

        uint table = GuestMemory.R32(objectAddress + 4);
        if (twice)
        {
            table = GuestMemory.R32(table + 4);
        }

        context.Return(CallMethod(context, GuestMemory.R32(table + (uint)offset)));
    }

    [Hle("NID_CE37B69E", Nid = 0xCE37B69E, Library = "SceSslInternal")]
    void SslInternalDispatch04(HleContext context) => DispatchTable(context, 0x04, true);

    [Hle("NID_144FA354", Nid = 0x144FA354, Library = "SceSslInternal")]
    void SslInternalDispatch14(HleContext context) => DispatchTable(context, 0x14, true);

    /// <summary>NID_29AAC768 (object, a, b, c, d, e, f): also needs the fifth and seventh arguments non-zero.</summary>
    [Hle("NID_29AAC768", Nid = 0x29AAC768, Library = "SceSslInternal")]
    void SslInternalDispatch1C(HleContext context) => DispatchTable(context, 0x1C, context.Gpr(4) != 0 && context.Gpr(6) != 0);

    [Hle("NID_91C2531C", Nid = 0x91C2531C, Library = "SceSslInternal")]
    void SslInternalDispatch20(HleContext context) => DispatchTable(context, 0x20, context.Gpr(2) != 0);

    [Hle("NID_64BDF1BB", Nid = 0x64BDF1BB, Library = "SceSslInternal")]
    void SslInternalDispatch24(HleContext context) => DispatchTable(context, 0x24, context.Gpr(2) != 0);

    [Hle("NID_17BF2367", Nid = 0x17BF2367, Library = "SceSslInternal")]
    void SslInternalDispatch2C(HleContext context) => DispatchTable(context, 0x2C, true);

    [Hle("NID_B5A099D2", Nid = 0xB5A099D2, Library = "SceSslInternal")]
    void SslInternalDispatch30(HleContext context) => DispatchTable(context, 0x30, context.Gpr(2) != 0);

    /// <summary>NID_306E2A22 (object, a, b) / NID_170D595D (object, a, b): the second-level table at +0x4C of the
    /// object's table, its +0 / +8 method; both / the last argument must be non-zero.</summary>
    [Hle("NID_306E2A22", Nid = 0x306E2A22, Library = "SceSslInternal")]
    void SslInternalDispatchSub0(HleContext context) => DispatchSubTable(context, 0, context.Gpr(1) != 0 && context.Gpr(2) != 0);

    [Hle("NID_170D595D", Nid = 0x170D595D, Library = "SceSslInternal")]
    void SslInternalDispatchSub8(HleContext context) => DispatchSubTable(context, 8, context.Gpr(2) != 0);

    static void DispatchSubTable(HleContext context, int offset, bool argumentsValid)
    {
        if (context.Gpr(0) == 0 || !argumentsValid)
        {
            context.Return(MesInvalidArgument);
            return;
        }

        uint table = GuestMemory.R32(GuestMemory.R32(context.Gpr(0)) + 0x4C);
        uint method = table != 0 ? GuestMemory.R32(table + (uint)offset) : 0;
        context.Return(method == 0 ? MesNotSupported : CallMethod(context, method));
    }

    /// <summary>NID_46358909 (object **holder): the release through the table of the object the holder points at
    /// (+0x20); NULL holder or object 0, no method 0x271B.</summary>
    [Hle("NID_46358909", Nid = 0x46358909, Library = "SceSslInternal")]
    void SslInternalRelease(HleContext context)
    {
        uint holder = context.Gpr(0);
        uint table = holder != 0 ? GuestMemory.R32(GuestMemory.R32(holder)) : 0;
        if (table == 0)
        {
            context.Return(0);
            return;
        }

        uint method = GuestMemory.R32(table + 0x20);
        context.Return(method == 0 ? MesNotSupported : CallMethod(context, method));
    }

    [Hle("NID_D4ADF069", Nid = 0xD4ADF069, Library = "SceSslInternal")]
    void SslInternalInner04(HleContext context) => DispatchInner(context, 0x04, false);

    [Hle("NID_5E50CB55", Nid = 0x5E50CB55, Library = "SceSslInternal")]
    void SslInternalInner08(HleContext context) => DispatchInner(context, 0x08, false);

    [Hle("NID_6CDB4EE8", Nid = 0x6CDB4EE8, Library = "SceSslInternal")]
    void SslInternalInnerInner0C(HleContext context) => DispatchInner(context, 0x0C, true);

    [Hle("NID_04BAED82", Nid = 0x04BAED82, Library = "SceSslInternal")]
    void SslInternalInnerInner14(HleContext context) => DispatchInner(context, 0x14, true);

    [Hle("NID_D1A82B1A", Nid = 0xD1A82B1A, Library = "SceSslInternal")]
    void SslInternalInnerInner1C(HleContext context) => DispatchInner(context, 0x1C, true);

    [Hle("NID_88391E03", Nid = 0x88391E03, Library = "SceSslInternal")]
    void SslInternalInnerInner20(HleContext context) => DispatchInner(context, 0x20, true);

    /// <summary>NID_6D2A86F0 / NID_4AAF0D45 (...): run the two hooks libssl's own engine installs (globals its
    /// initialisation points at internal functions of the module) with the caller's arguments; with no hook 0. The
    /// HLE has no libssl engine behind sceSslInit, so no hook is ever installed: 0 (the default).</summary>
    [Hle("NID_6D2A86F0", Nid = 0x6D2A86F0, Library = "SceSslInternal", Default = true)]
    int SslInternalHookA()
    {
        Log.InfoOnce("ssl-hook-a", "SceSslInternal: NID_6D2A86F0 hook not installed (no libssl engine): 0, default");
        return 0;
    }

    [Hle("NID_4AAF0D45", Nid = 0x4AAF0D45, Library = "SceSslInternal", Default = true)]
    int SslInternalHookB()
    {
        Log.InfoOnce("ssl-hook-b", "SceSslInternal: NID_4AAF0D45 hook not installed (no libssl engine): 0, default");
        return 0;
    }

    /// <summary>
    /// NID_16D7C7CA BIO_ctrl(BIO *bio, int command, long value, void *pointer): the BIO's callback (+4) is asked first
    /// (BIO_CB_CTRL, a result below 1 ends the call), then the method's ctrl (+0x18 of the table at +0), then the
    /// callback again with the result (BIO_CB_CTRL | BIO_CB_RETURN). NULL BIO 0; no method -2.
    /// </summary>
    [Hle("NID_16D7C7CA", Nid = 0x16D7C7CA, Library = "SceSslInternal")]
    int SslInternalBioControl(uint bio, uint command, uint value, uint pointer)
    {
        if (bio == 0)
        {
            return 0;
        }

        uint table = GuestMemory.R32(bio);
        if (table == 0 || GuestMemory.R32(table + 0x18) == 0)
        {
            return -2;
        }

        GuestThread thread = GuestThread.Current!;
        uint callback = GuestMemory.R32(bio + 4);
        if (callback != 0)
        {
            int before = (int)thread.CallGuest(callback, bio, 6, pointer, command, value, 1);
            if (before < 1)
            {
                return before;
            }

            table = GuestMemory.R32(bio);
        }

        int result = (int)thread.CallGuest(GuestMemory.R32(table + 0x18), bio, command, value, pointer);
        if (callback != 0)
        {
            result = (int)thread.CallGuest(callback, bio, 0x86, pointer, command, value, (uint)result);
        }

        return result;
    }

    /// <summary>
    /// NID_33AC7A16 BIO_write(BIO *bio, const void *data, int length): NULL BIO 0; no write method (+8) or an
    /// uninitialised BIO (+0xC 0) -2. The callback (+4) runs before (BIO_CB_WRITE) and after (| BIO_CB_RETURN); the
    /// bytes written count into +0x34; with +0x14 bit 4 the BIO is flushed (ctrl 0xB, with its own callback calls). A
    /// BIO without references (+0x2C below 1) answers 0.
    /// </summary>
    [Hle("NID_33AC7A16", Nid = 0x33AC7A16, Library = "SceSslInternal")]
    int SslInternalBioWrite(uint bio, uint data, uint length)
    {
        if (bio == 0)
        {
            return 0;
        }

        uint table = GuestMemory.R32(bio);
        if (table == 0 || GuestMemory.R32(table + 8) == 0)
        {
            return -2;
        }

        GuestThread thread = GuestThread.Current!;
        uint callback = GuestMemory.R32(bio + 4);
        if (callback != 0)
        {
            int before = (int)thread.CallGuest(callback, bio, 3, data, length, 0, 1);
            if (before < 1)
            {
                return before;
            }

            table = GuestMemory.R32(bio);
        }

        if (GuestMemory.R32(bio + 0xC) == 0)
        {
            return -2;
        }

        int written = (int)thread.CallGuest(GuestMemory.R32(table + 8), bio, data, length);
        if (written > 0)
        {
            GuestMemory.W32(bio + 0x34, GuestMemory.R32(bio + 0x34) + (uint)written);
        }

        if ((GuestMemory.R32(bio + 0x14) & 0x10) != 0)
        {
            FlushBio(thread, bio);
        }

        if ((int)GuestMemory.R32(bio + 0x2C) < 1)
        {
            return 0;
        }

        return callback != 0 ? (int)thread.CallGuest(callback, bio, 0x83, data, length, 0, (uint)written) : written;
    }

    static void FlushBio(GuestThread thread, uint bio)
    {
        uint table = GuestMemory.R32(bio);
        if (table == 0 || GuestMemory.R32(table + 0x18) == 0)
        {
            return;
        }

        uint callback = GuestMemory.R32(bio + 4);
        if (callback != 0)
        {
            if ((int)thread.CallGuest(callback, bio, 6, 0, 0xB, 0, 1) < 1)
            {
                return;
            }

            table = GuestMemory.R32(bio);
        }

        uint flushed = thread.CallGuest(GuestMemory.R32(table + 0x18), bio, 0xB, 0, 0);
        if (callback != 0)
        {
            thread.CallGuest(callback, bio, 0x86, 0, 0xB, 0, flushed);
        }
    }

    /// <summary>NID_DABB61EE (object, value, required): NULL object or a required argument 0 is 0x2721; the method at
    /// +0x10 of the object's table runs as (object, 1, value); no method 0x271B.</summary>
    [Hle("NID_DABB61EE", Nid = 0xDABB61EE, Library = "SceSslInternal")]
    uint SslInternalDispatch10(uint objectAddress, uint value, uint required)
    {
        if (objectAddress == 0 || required == 0)
        {
            return MesInvalidArgument;
        }

        uint method = GuestMemory.R32(GuestMemory.R32(objectAddress) + 0x10);
        return method == 0 ? MesNotSupported : GuestThread.Current!.CallGuest(method, objectAddress, 1, value);
    }

    /// <summary>NID_FEFE761C (object, ..., the seventh argument): that argument 0, a NULL object or one without a table
    /// is 0x2721; the method at +0x30 of its table runs with the caller's arguments; no method 0x271B.</summary>
    [Hle("NID_FEFE761C", Nid = 0xFEFE761C, Library = "SceSslInternal")]
    void SslInternalDispatch30Checked(HleContext context)
    {
        uint objectAddress = context.Gpr(0);
        uint table = objectAddress != 0 ? GuestMemory.R32(objectAddress) : 0;
        if (context.Gpr(6) == 0 || table == 0)
        {
            context.Return(MesInvalidArgument);
            return;
        }

        uint method = GuestMemory.R32(table + 0x30);
        context.Return(method == 0 ? MesNotSupported : CallMethod(context, method));
    }

    /// <summary>
    /// NID_CB83EA07 BIO_printf(BIO *bio, const char *format, ...): NULL BIO 0; the text is formatted into libssl's
    /// 1024-byte buffer (cut to 1023 bytes) and written with BIO_write (NID_33AC7A16), whose result it returns.
    /// </summary>
    [Hle("NID_CB83EA07", Nid = 0xCB83EA07, Library = "SceSslInternal")]
    int SslInternalBioPrintf(uint bio, string format, VarArgs arguments)
    {
        if (bio == 0)
        {
            return 0;
        }

        const int BufferSize = 0x400;
        byte[] text = System.Text.Encoding.UTF8.GetBytes(CFormat.Format(format, arguments));
        int length = Math.Min(text.Length, BufferSize - 1);
        uint buffer = Emu.Memory.Alloc(BufferSize, "libssl BIO_printf", MemoryRegion.User, 4);
        try
        {
            text.AsSpan(0, length).CopyTo(GuestMemory.Span(buffer, length));
            GuestMemory.Write(buffer + (uint)length, (byte)0);
            return SslInternalBioWriteThroughEntry(bio, buffer, (uint)length);
        }
        finally
        {
            Emu.Memory.Free(buffer);
        }
    }

    /// <summary>
    /// NID_92560B8D SSL_connect(SSL *ssl): libssl first resets a connection that is not in its handshake (SSL_clear,
    /// engine state) and then runs the connect method (+0x14 of the method table at +8). The HLE's method tables are
    /// NULL (no libssl engine), so a connection without a table fails the handshake (-1, never connected); a table
    /// the caller made has its method run.
    /// </summary>
    [Hle("NID_92560B8D", Nid = 0x92560B8D, Library = "SceSslInternal", Default = true)]
    int SslInternalConnect(uint ssl)
    {
        uint table = GuestMemory.R32(ssl + 8);
        uint method = table != 0 ? GuestMemory.R32(table + 0x14) : 0;
        if (method == 0)
        {
            EngineUnavailable("SSL_connect");
            return -1;
        }

        return (int)GuestThread.Current!.CallGuest(method, ssl);
    }
}
