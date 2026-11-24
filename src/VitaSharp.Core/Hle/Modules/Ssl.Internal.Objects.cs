using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceSslInternal's object helpers of libssl.suprx 3.74 (read in its code, study only) that need no TLS engine: the
// connection's error classification, the MES-to-SceSsl error translation, the BIO and MES record lifetimes on libssl's
// heap and the connection's BIO pair. libssl's error queue is never filled in the HLE (it has no engine), so the
// calls that read it see it empty.
public sealed partial class Ssl
{
    /// <summary>libssl heap blocks the HLE handed out (its malloc), by address.</summary>
    [SnapshotMissing]
    readonly HashSet<uint> _libsslBlocks = new();

    /// <summary>libssl's malloc: the caller's memory function when one was installed (NID_C1299FB6; it may answer
    /// NULL), else a block of the HLE's libssl heap; zeroed.</summary>
    uint LibsslAlloc(uint size)
    {
        uint malloc;
        lock (_libsslBlocks)
        {
            malloc = _libsslMalloc;
        }

        if (malloc != 0)
        {
            uint guestBlock = GuestThread.Current!.CallGuest(malloc, size);
            if (guestBlock != 0)
            {
                GuestMemory.Fill(guestBlock, 0, size);
            }

            return guestBlock;
        }

        uint block = Emu.Memory.Alloc(size, "libssl heap", MemoryRegion.User, 8);
        GuestMemory.Fill(block, 0, size);
        lock (_libsslBlocks)
        {
            _libsslBlocks.Add(block);
        }

        return block;
    }

    /// <summary>libssl's free: a block of its heap goes back; another block goes to the caller's free when one was
    /// installed (NID_C1299FB6), else is left alone.</summary>
    void LibsslFree(uint block)
    {
        uint free;
        lock (_libsslBlocks)
        {
            free = _libsslFree;
            if (_libsslBlocks.Remove(block))
            {
                free = 0;
            }
            else if (free == 0 || block == 0)
            {
                return;
            }
        }

        if (free != 0)
        {
            GuestThread.Current!.CallGuest(free, block);
            return;
        }

        Emu.Memory.Free(block);
    }

    /// <summary>
    /// NID_2F0FC3CD SSL_get_error(SSL *ssl, int result): 0 for a result above 0; an error in libssl's queue is
    /// SSL_ERROR_SSL (1, or SYSCALL 5 for a system one); otherwise, for a failed call, the read BIO (+0xC, rwstate 3)
    /// or the write BIO (+0x10, rwstate 2) says WANT_READ 2 / WANT_WRITE 3 / special (+0x18 reason: connect 7, accept
    /// 9, else 5); rwstate 4 is X509_LOOKUP 4; a TLS 1.x client with a pending session ticket 8; a closed connection
    /// (result 0, +0x2C bit 1, no pending data) ZERO_RETURN 6; else SYSCALL 5.
    /// </summary>
    [Hle("NID_2F0FC3CD", Nid = 0x2F0FC3CD, Library = "SceSslInternal")]
    int SslInternalGetError(uint ssl, int result)
    {
        if (result >= 1)
        {
            return 0;
        }

        int rwState = (int)GuestMemory.R32(ssl + 0x18);
        if (result < 0)
        {
            if (rwState == 3)
            {
                uint readBio = GuestMemory.R32(ssl + 0xC);
                if (BioFlag(readBio, 1) != 0)
                {
                    return 2;
                }

                if (BioFlag(readBio, 2) != 0)
                {
                    return 3;
                }

                if (BioFlag(readBio, 4) != 0)
                {
                    uint reason = GuestMemory.R32(readBio + 0x18);
                    return reason == 2 ? 7 : reason == 3 ? 9 : 5;
                }
            }

            if (rwState == 2)
            {
                uint writeBio = GuestMemory.R32(ssl + 0x10);
                if (BioFlag(writeBio, 2) != 0)
                {
                    return 3;
                }

                if (BioFlag(writeBio, 1) != 0)
                {
                    return 2;
                }

                if (BioFlag(writeBio, 4) != 0)
                {
                    return GuestMemory.R32(writeBio + 0x18) == 2 ? 7 : 5;
                }
            }

            if (rwState == 4)
            {
                return 4;
            }

            int version = (int)GuestMemory.R32(ssl);
            uint ticket = GuestMemory.R32(ssl + 0x158);
            if (version > 0x2FF && version < 0x304 && GuestMemory.R32(ssl + 0x24) != 0 && ticket != 0 && (int)GuestMemory.R32(ticket + 4) >= 0)
            {
                return 8;
            }
        }

        if (result == 0 && (GuestMemory.R32(ssl + 0x2C) & 2) != 0 && GuestMemory.R32(GuestMemory.R32(ssl + 0x54) + 0x16C) == 0)
        {
            return 6;
        }

        return 5;
    }

    /// <summary>A BIO's retry flag (+0x14 bits); a NULL BIO answers all ones, as libssl's helpers do.</summary>
    static uint BioFlag(uint bio, uint mask)
    {
        return bio == 0 ? 0xFFFFFFFF : GuestMemory.R32(bio + 0x14) & mask;
    }

    /// <summary>
    /// NID_461C5990 (int kind, int code): libssl's error as a SceSsl error. Kind 0 translates an MES code: 0 stays 0,
    /// 0x2715 (no memory) is 0x80435022, 0x2711..0x2727 and 0x2729..0x272A, 0x272C..0x2735 map to 0x80432AF0 + code,
    /// anything else 0x804352FF. Kind 1 keeps the code unless libssl's queue holds an out-of-memory error (never here);
    /// another kind is 0x804351FE.
    /// </summary>
    [Hle("NID_461C5990", Nid = 0x461C5990, Library = "SceSslInternal")]
    int SslInternalTranslateError(int kind, int code)
    {
        if (kind == 1)
        {
            return code;
        }

        if (kind != 0)
        {
            return ERR_INVALID_VALUE;
        }

        if (code == 0)
        {
            return 0;
        }

        if (code == 0x2715)
        {
            return unchecked((int)0x80435022);
        }

        bool mapped = code is >= 0x2711 and <= 0x2727 or 0x2729 or 0x272A || (uint)(code - 0x272C) <= 9;
        return mapped ? unchecked((int)(0x80432AF0u + (uint)code)) : unchecked((int)0x804352FF);
    }

    /// <summary>NID_BF99D664 (value, -, record **out): a new 0x28-byte MES record on libssl's heap { value, 1, 0, 0,
    /// -1, ... }; NULL out 0x2721.</summary>
    [Hle("NID_BF99D664", Nid = 0xBF99D664, Library = "SceSslInternal")]
    int SslInternalNewRecord(uint value, uint unused, uint output)
    {
        if (output == 0)
        {
            return (int)MesInvalidArgument;
        }

        uint record = LibsslAlloc(0x28);
        if (record == 0)
        {
            return 0x2715;
        }

        GuestMemory.W32(record, value);
        GuestMemory.W32(record + 4, 1);
        GuestMemory.W32(record + 16, 0xFFFFFFFF);
        GuestMemory.W32(output, record);
        return 0;
    }

    /// <summary>NID_F030A0DE (record *record): frees what the record owns (flag bit 1: the buffer at +0x18, bit 2: the
    /// one at +0x20, then +0x24) and, with flag bit 0, the record itself; 0.</summary>
    [Hle("NID_F030A0DE", Nid = 0xF030A0DE, Library = "SceSslInternal")]
    int SslInternalFreeRecord(uint record)
    {
        if (record == 0)
        {
            return 0;
        }

        uint flags = GuestMemory.R32(record + 4);
        if ((flags & 2) != 0)
        {
            LibsslFree(GuestMemory.R32(record + 0x18));
            GuestMemory.W32(record + 0x18, 0);
            flags &= ~2u;
        }

        if ((flags & 4) != 0)
        {
            LibsslFree(GuestMemory.R32(record + 0x20));
            GuestMemory.W32(record + 0x18, 0); // 3.74 clears +0x18 here too
            flags &= ~4u;
        }

        GuestMemory.W32(record + 4, flags);
        if (GuestMemory.R32(record + 0x24) != 0)
        {
            LibsslFree(GuestMemory.R32(record + 0x24));
            GuestMemory.W32(record + 0x24, 0);
        }

        if ((flags & 1) != 0)
        {
            LibsslFree(record);
        }

        return 0;
    }

    /// <summary>
    /// NID_C4D984C2 BIO_free(BIO *bio): drops a reference (+0x2C); the last one runs the callback (+4, BIO_CB_FREE; a
    /// result below 1 keeps the BIO), frees the BIO's extra data (+0x38) and, when its method has a destroy (+0x20 of
    /// the table), destroys and frees it. NULL 0, else 1.
    /// </summary>
    [Hle("NID_C4D984C2", Nid = 0xC4D984C2, Library = "SceSslInternal")]
    int SslInternalBioFree(uint bio)
    {
        if (bio == 0)
        {
            return 0;
        }

        int references = (int)GuestMemory.R32(bio + 0x2C) - 1;
        GuestMemory.W32(bio + 0x2C, (uint)references);
        if (references >= 1)
        {
            return 1;
        }

        GuestThread thread = GuestThread.Current!;
        uint callback = GuestMemory.R32(bio + 4);
        if (callback != 0)
        {
            int answer = (int)thread.CallGuest(callback, bio, 1, 0, 0, 0, 1);
            if (answer < 1)
            {
                return answer;
            }
        }

        uint extra = GuestMemory.R32(bio + 0x38);
        if (extra != 0)
        {
            LibsslFree(extra);
        }

        uint table = GuestMemory.R32(bio);
        uint destroy = table != 0 ? GuestMemory.R32(table + 0x20) : 0;
        if (destroy != 0)
        {
            thread.CallGuest(destroy, bio);
            LibsslFree(bio);
        }

        return 1;
    }

    /// <summary>NID_4A1557A7 SSL_set_bio(SSL *ssl, BIO *read, BIO *write): the connection's read (+0xC) and write
    /// (+0x10) BIOs. A write BIO that is the buffering BIO (+0x14) is popped first; the old chains the connection no
    /// longer uses are freed (BIO_free_all), one shared by both directions once.</summary>
    [Hle("NID_4A1557A7", Nid = 0x4A1557A7, Library = "SceSslInternal")]
    void SslInternalSetBio(uint ssl, uint read, uint write)
    {
        uint oldWrite = GuestMemory.R32(ssl + 0x10);
        uint buffering = GuestMemory.R32(ssl + 0x14);
        if (buffering != 0 && oldWrite == buffering)
        {
            oldWrite = BioPop(oldWrite);
            GuestMemory.W32(ssl + 0x10, oldWrite);
        }

        uint oldRead = GuestMemory.R32(ssl + 0xC);
        if (oldRead == oldWrite)
        {
            if (oldRead != 0)
            {
                if (read != oldRead && write != oldWrite)
                {
                    BioFreeAll(oldRead);
                }

                GuestMemory.W32(ssl + 0xC, read);
                GuestMemory.W32(ssl + 0x10, write);
                return;
            }
        }
        else if (oldRead != 0 && oldRead != read)
        {
            BioFreeAll(oldRead);
            oldWrite = GuestMemory.R32(ssl + 0x10);
        }

        if (oldWrite != 0 && oldWrite != write)
        {
            BioFreeAll(oldWrite);
        }

        GuestMemory.W32(ssl + 0xC, read);
        GuestMemory.W32(ssl + 0x10, write);
    }

    /// <summary>BIO_pop: unlinks the BIO from its chain (+0x24 next, +0x28 previous) and tells its method (ctrl
    /// BIO_CTRL_POP 7, with the callback around it); the next BIO.</summary>
    static uint BioPop(uint bio)
    {
        if (bio == 0)
        {
            return 0;
        }

        uint next = GuestMemory.R32(bio + 0x24);
        uint previous = GuestMemory.R32(bio + 0x28);
        if (previous != 0)
        {
            GuestMemory.W32(previous + 0x24, next);
        }

        if (next != 0)
        {
            GuestMemory.W32(next + 0x28, previous);
        }

        GuestMemory.W32(bio + 0x24, 0);
        GuestMemory.W32(bio + 0x28, 0);
        uint table = GuestMemory.R32(bio);
        if (table == 0 || GuestMemory.R32(table + 0x18) == 0)
        {
            return next;
        }

        GuestThread thread = GuestThread.Current!;
        uint callback = GuestMemory.R32(bio + 4);
        if (callback != 0)
        {
            if ((int)thread.CallGuest(callback, bio, 6, 0, 7, 0, 1) < 1)
            {
                return next;
            }

            table = GuestMemory.R32(bio);
        }

        uint answer = thread.CallGuest(GuestMemory.R32(table + 0x18), bio, 7, 0, 0);
        if (callback != 0)
        {
            thread.CallGuest(callback, bio, 0x86, 0, 7, 0, answer);
        }

        return next;
    }

    /// <summary>BIO_free_all: frees the chain from the BIO on, stopping after a BIO that still had other references.</summary>
    void BioFreeAll(uint bio)
    {
        while (bio != 0)
        {
            int references = (int)GuestMemory.R32(bio + 0x2C);
            uint next = GuestMemory.R32(bio + 0x24);
            GuestMemory.W32(bio + 0x28, 0);
            SslInternalBioFreeThroughEntry(bio);
            if (references >= 2)
            {
                return;
            }

            bio = next;
        }
    }

    /// <summary>NID_67FB1CD3 (SSL *ssl, a, b): a and b non-zero (else 0x20), then the method +0xBC of the key record
    /// at +0x18 of the object at the connection's +0xC4 (NULL object 0x2721, no record 0x271F, no method 0x271B).</summary>
    [Hle("NID_67FB1CD3", Nid = 0x67FB1CD3, Library = "SceSslInternal")]
    void SslInternalKeyMethodBC(HleContext context)
    {
        if (context.Gpr(1) == 0 || context.Gpr(2) == 0)
        {
            context.Return(0x20);
            return;
        }

        uint keyObject = GuestMemory.R32(context.Gpr(0) + 0xC4);
        if (keyObject == 0)
        {
            context.Return(MesInvalidArgument);
            return;
        }

        uint record = GuestMemory.R32(keyObject + 0x18);
        if (record == 0)
        {
            context.Return(0x271F);
            return;
        }

        uint method = GuestMemory.R32(record + 0xBC);
        context.Return(method == 0 ? MesNotSupported : context.Thread.CallGuest(method, keyObject, context.Gpr(1), context.Gpr(2), context.Gpr(3)));
    }
}
