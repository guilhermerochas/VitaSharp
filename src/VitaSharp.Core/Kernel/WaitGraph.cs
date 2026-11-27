using System.Text;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The wait graph of a process's guest threads, for thread dumps (watchdog, F8 capture, end of run): each waiting thread,
/// the object it waits on and the thread that holds or ends it (a mutex's owner, the thread a thread-end wait waits
/// for), followed along the chain to the first thread that is not waiting on another one, and a cycle when the chain
/// comes back (a deadlock). A thread that waits on an object without an owner (semaphore, event flag) shows the thread
/// that last signalled it.
/// </summary>
public static class WaitGraph
{
    /// <summary>One line per waiting thread: <c>wait 'main' 0x400100F5 -> end of 'pthread' 0x4001022F (Running in
    /// sceIoRead)</c>, <c>wait 'a' 0x.. -> mutex 'm' held by 'b' 0x.. (Waiting) -> ...</c>.</summary>
    public static List<string> Lines(IEnumerable<GuestThread> threads)
    {
        GuestThread[] all = threads.ToArray();
        var lines = new List<string>();
        foreach (GuestThread thread in all)
        {
            if (thread.Status != ThreadStatus.Waiting || thread.CurrentWaitSync == null)
            {
                continue;
            }

            lines.Add("wait " + Chain(thread, all));
        }

        return lines;
    }

    /// <summary>The chain that starts at <paramref name="start"/>, one hop per waited object.</summary>
    internal static string Chain(GuestThread start, IReadOnlyList<GuestThread> threads)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<GuestThread>();
        GuestThread? current = start;
        sb.Append(Named(start));
        while (current != null && seen.Add(current))
        {
            object? sync = current.CurrentWaitSync;
            if (current.Status != ThreadStatus.Waiting || sync == null)
            {
                break;
            }

            (string what, GuestThread? holder) = Hop(sync, threads);
            sb.Append(" -> ").Append(what);
            if (holder == null)
            {
                break;
            }

            sb.Append(' ').Append(Holder(holder));
            if (seen.Contains(holder))
            {
                sb.Append(" (DEADLOCK: cycle)");
                break;
            }

            current = holder;
        }

        return sb.ToString();
    }

    /// <summary>The waited object and the thread the wait depends on (null: none known).</summary>
    private static (string What, GuestThread? Holder) Hop(object sync, IReadOnlyList<GuestThread> threads)
    {
        switch (sync)
        {
            case Mutex mutex:
            {
                GuestThread? owner = mutex.Owner;
                string kind = mutex.IsLight ? "lwmutex" : "mutex";
                return owner == null
                    ? ($"{kind} '{mutex.Name}' (free)", null)
                    : ($"{kind} '{mutex.Name}' held by", owner);
            }
            case Semaphore semaphore:
                return ($"sema '{semaphore.Name}' count {semaphore.Count}{Signalled(semaphore)}", null);
            case KernelObject kernelObject:
                return ($"{kernelObject.WaitDescription}{Signalled(kernelObject)}", null);
        }

        foreach (GuestThread thread in threads)
        {
            if (ReferenceEquals(thread.WaitLock, sync))
            {
                return ("end of", thread);
            }
        }

        return (sync.GetType().Name, null);
    }

    private static string Signalled(KernelObject kernelObject)
        => kernelObject.Signaller is { } signaller ? $", last signalled by {Named(signaller)}" : ", never signalled by a thread";

    private static string Holder(GuestThread thread) => $"{Named(thread)} ({thread.ShortState()})";

    /// <summary>Name and UID: threads often share a name (a port's workers are all 'pthread').</summary>
    internal static string Named(GuestThread thread) => $"'{thread.Name}' 0x{thread.Uid:X}";
}
