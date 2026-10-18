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
    public const int INVALID_ARGUMENT = unchecked((int)0x80020003);
    public const int ILLEGAL_ADDR = unchecked((int)0x8002000C);
    public const int ILLEGAL_SIZE = unchecked((int)0x80020015);
    public const int NO_MEMORY = unchecked((int)0x80028004);
    public const int ILLEGAL_TYPE = unchecked((int)0x8002000A);
    public const int UNKNOWN_UID = unchecked((int)0x80028002);
    public const int ILLEGAL_THREAD_ID = unchecked((int)0x80028040);
    public const int UNKNOWN_THREAD_ID = unchecked((int)0x80028041);
    public const int ILLEGAL_PRIORITY = unchecked((int)0x8002801B);
    public const int NOT_DORMANT = unchecked((int)0x80028045);
    public const int DORMANT = unchecked((int)0x80028044);
    public const int WAIT_TIMEOUT = unchecked((int)0x80028005);
    public const int WAIT_CANCEL = unchecked((int)0x80028007);
    public const int WAIT_DELETE = unchecked((int)0x80028006);
    public const int UNKNOWN_SEMA_ID = unchecked((int)0x800281C1);
    public const int SEMA_ZERO = unchecked((int)0x800281C3);
    public const int SEMA_OVF = unchecked((int)0x800281C4);
    public const int UNKNOWN_EVF_ID = unchecked((int)0x80028181);
    public const int EVF_COND = unchecked((int)0x80028183);
    public const int EVF_MULTI = unchecked((int)0x80028184);
    public const int UNKNOWN_MUTEX_ID = unchecked((int)0x800281D1);
    public const int MUTEX_RECURSIVE = unchecked((int)0x800281D4);
    public const int MUTEX_LOCK_OVF = unchecked((int)0x800281D5);
    public const int MUTEX_UNLOCK_UDF = unchecked((int)0x800281D6);
    public const int MUTEX_FAILED_TO_OWN = unchecked((int)0x800281D7);
    public const int MUTEX_NOT_OWNED = unchecked((int)0x800281D8);
    public const int LW_MUTEX_FAILED_TO_OWN = unchecked((int)0x80028227);
    public const int LW_MUTEX_NOT_OWNED = unchecked((int)0x80028228);
    public const int LW_MUTEX_RECURSIVE = unchecked((int)0x80028224);
    public const int LW_MUTEX_UNLOCK_UDF = unchecked((int)0x80028226);
    public const int UNKNOWN_COND_ID = unchecked((int)0x800281E1);
    public const int UNKNOWN_CALLBACK_ID = unchecked((int)0x800280B1);
    public const int UNKNOWN_MSG_PIPE_ID = unchecked((int)0x80028261);
    public const int MPP_EMPTY = unchecked((int)0x80028263);
    public const int MPP_FULL = unchecked((int)0x80028262);
    public const int UNKNOWN_RW_LOCK_ID = unchecked((int)0x80028281);
    public const int UNKNOWN_TIMER_ID = unchecked((int)0x80028241);
    public const int NOT_FOUND = unchecked((int)0x80028080);

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
