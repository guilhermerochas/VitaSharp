using System.Collections.Concurrent;

namespace VitaSharp.Core.Kernel;

public abstract class KernelObject
{
    public int Uid { get; internal set; }
    public string Name { get; init; } = "";

    string? _waitDescription;
    /// <summary>"kind name" shown in thread states; built once so blocking waits do not allocate a string each time.</summary>
    public string WaitDescription => _waitDescription ??= $"{GetType().Name.ToLowerInvariant()} {Name}";
}

/// <summary>Common SCE error codes.</summary>
public static class SceError
{
    public const int OK = 0;
    public const int ERROR = unchecked((int)0x80020001);
    public const int NOT_IMPLEMENTED = unchecked((int)0x80020002);
    public const int INVALID_ARGUMENT = unchecked((int)0x80020005);
    public const int ILLEGAL_ADDR = unchecked((int)0x80020006);
    public const int ILLEGAL_SIZE = unchecked((int)0x8002000B);
    public const int NO_MEMORY = unchecked((int)0x8002710B);
    public const int ILLEGAL_TYPE = unchecked((int)0x8002000C);
    public const int UNKNOWN_UID = unchecked((int)0x80028001);
    public const int ILLEGAL_THREAD_ID = unchecked((int)0x80028022);
    public const int UNKNOWN_THREAD_ID = unchecked((int)0x80028021);
    public const int ILLEGAL_PRIORITY = unchecked((int)0x80028023);
    public const int NOT_DORMANT = unchecked((int)0x80028028);
    public const int DORMANT = unchecked((int)0x80028027);
    public const int WAIT_TIMEOUT = unchecked((int)0x80028005);
    public const int WAIT_CANCEL = unchecked((int)0x80028007);
    public const int WAIT_DELETE = unchecked((int)0x80028006);
    public const int UNKNOWN_SEMA_ID = unchecked((int)0x80028101);
    public const int SEMA_ZERO = unchecked((int)0x80028102);
    public const int SEMA_OVF = unchecked((int)0x80028103);
    public const int UNKNOWN_EVF_ID = unchecked((int)0x800280E1);
    public const int EVF_COND = unchecked((int)0x800280E3);
    public const int EVF_MULTI = unchecked((int)0x800280E2);
    public const int UNKNOWN_MUTEX_ID = unchecked((int)0x80028141);
    public const int MUTEX_RECURSIVE = unchecked((int)0x80028142);
    public const int MUTEX_LOCK_OVF = unchecked((int)0x80028143);
    public const int MUTEX_UNLOCK_UDF = unchecked((int)0x80028144);
    public const int MUTEX_FAILED_TO_OWN = unchecked((int)0x80028145);
    public const int MUTEX_NOT_OWNED = unchecked((int)0x80028146);
    public const int LW_MUTEX_FAILED_TO_OWN = unchecked((int)0x80028185);
    public const int LW_MUTEX_NOT_OWNED = unchecked((int)0x80028186);
    public const int LW_MUTEX_RECURSIVE = unchecked((int)0x80028182);
    public const int LW_MUTEX_UNLOCK_UDF = unchecked((int)0x80028184);
    public const int UNKNOWN_COND_ID = unchecked((int)0x800281A1);
    public const int UNKNOWN_CALLBACK_ID = unchecked((int)0x800280A1);
    public const int UNKNOWN_MSG_PIPE_ID = unchecked((int)0x80028221);
    public const int MPP_EMPTY = unchecked((int)0x80028223);
    public const int MPP_FULL = unchecked((int)0x80028222);
    public const int UNKNOWN_RW_LOCK_ID = unchecked((int)0x800281E1);
    public const int UNKNOWN_TIMER_ID = unchecked((int)0x80028241);
    /// <summary>Lookup by name failed (sceKernelFind*/Open* by name); same value as UID_CANNOT_FIND_BY_NAME.</summary>
    public const int NOT_FOUND = unchecked((int)0x8002450A);
    public const int UID_CANNOT_FIND_BY_NAME = unchecked((int)0x8002450A);
    public const int NOSYS = unchecked((int)0x80020003);
    public const int ILLEGAL_PERMISSION = unchecked((int)0x80020008);
    public const int INVALID_FLAGS = unchecked((int)0x8002000A);
    public const int ILLEGAL_PATTERN = unchecked((int)0x8002000D);
    public const int ILLEGAL_ATTR = unchecked((int)0x8002000E);
    public const int ILLEGAL_COUNT = unchecked((int)0x8002000F);
    public const int ILLEGAL_MODE = unchecked((int)0x80020010);
    public const int UID_NAME_TOO_LONG = unchecked((int)0x80024002);
    public const int INVALID_UID = unchecked((int)0x80024501);
    public const int BLOCK_ERROR = unchecked((int)0x80024700);
    /// <summary>Vita3K only (not in the SDK header).</summary>
    public const int ILLEGAL_BLOCK_ID = unchecked((int)0x80024701);
    public const int ILLEGAL_MEMBLOCK_SIZE = unchecked((int)0x80024B05);
    public const int MEMBLOCK_RANGE_ERROR = unchecked((int)0x80024B09);
    public const int MEMBLOCK_OVERFLOW = unchecked((int)0x80024B0B);
    public const int DIFFERENT_UID_CLASS = unchecked((int)0x80028002);
    public const int CAN_NOT_WAIT = unchecked((int)0x80028004);
    public const int ILLEGAL_STACK_SIZE = unchecked((int)0x80028024);
    public const int ILLEGAL_CPU_AFFINITY_MASK = unchecked((int)0x80028025);
    public const int NOT_SUSPENDED = unchecked((int)0x8002802E);
    public const int UNKNOWN_THREAD_EVENT_ID = unchecked((int)0x80028061);
    public const int KERNEL_TLS_ERROR = unchecked((int)0x80028080);
    public const int ALREADY_SENT = unchecked((int)0x80028121);
    public const int UNKNOWN_LW_MUTEX_ID = unchecked((int)0x80028181);
    public const int LW_MUTEX_LOCK_OVF = unchecked((int)0x80028183);
    public const int UNKNOWN_LW_COND_ID = unchecked((int)0x800281C1);
    public const int RW_LOCK_RECURSIVE = unchecked((int)0x800281E2);
    public const int RW_LOCK_LOCK_OVF = unchecked((int)0x800281E3);
    public const int RW_LOCK_NOT_OWNED = unchecked((int)0x800281E4);
    public const int RW_LOCK_UNLOCK_UDF = unchecked((int)0x800281E5);
    public const int RW_LOCK_FAILED_TO_LOCK = unchecked((int)0x800281E6);
    public const int RW_LOCK_FAILED_TO_UNLOCK = unchecked((int)0x800281E7);
    public const int UNKNOWN_EVENT_ID = unchecked((int)0x80028201);
    public const int EVENT_COND = unchecked((int)0x80028202);
    public const int MODULEMGR_NO_MOD = unchecked((int)0x8002D011);
    public const int MODULEMGR_NOENT = unchecked((int)0x8002D01C);
    public const int LIBRARYDB_NO_MOD = unchecked((int)0x8002D082);
    public const int ERRNO_ENOSYS = unchecked((int)0x80010058);

    // IO
    public const int IO_ENOENT = unchecked((int)0x80010002);
    public const int IO_EBADF = unchecked((int)0x80010009);
    public const int IO_EEXIST = unchecked((int)0x80010011);
    public const int IO_ENOTDIR = unchecked((int)0x80010014);
    public const int IO_EISDIR = unchecked((int)0x80010015);
    public const int IO_EINVAL = unchecked((int)0x80010016);
    public const int IO_EMFILE = unchecked((int)0x80010018);
    public const int IO_ENOSPC = unchecked((int)0x8001001C);
    public const int IO_EACCES = unchecked((int)0x8001000D);
    public const int IO_ENOTEMPTY = unchecked((int)0x8001005A);
}

/// <summary>UID table shared by every kernel object kind.</summary>
public sealed class ObjectTable
{
    readonly ConcurrentDictionary<int, KernelObject> _objects = new();
    int _next = 0x4001_0001;

    public int Register(KernelObject o)
    {
        int uid = Interlocked.Add(ref _next, 2);
        o.Uid = uid;
        _objects[uid] = o;
        return uid;
    }

    public T? Get<T>(int uid) where T : KernelObject => _objects.TryGetValue(uid, out var o) ? o as T : null;
    public bool Remove(int uid) => _objects.TryRemove(uid, out _);
    public IEnumerable<T> All<T>() where T : KernelObject => _objects.Values.OfType<T>();

    public T? FindByName<T>(string name) where T : KernelObject => _objects.Values.OfType<T>().FirstOrDefault(o => o.Name == name);
}
