using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// Background threads are lowered for real on every host (Core/Util/HostThreads.LowerCurrent): .NET's Thread.Priority
/// does nothing on Linux and Android, where the speculative JIT precompile, the spare shader workers and the texture
/// transcoders then competed as equals with the game's threads on a phone's LITTLE cores. On Linux the thread's nice
/// value changes, and only the calling thread's.
/// </summary>
public sealed class HostThreadsBackgroundTests
{
    [Theory]
    [InlineData(ThreadPriority.Lowest, 10)]
    [InlineData(ThreadPriority.BelowNormal, 5)]
    [InlineData(ThreadPriority.Normal, 0)]
    public void TheNiceValueFollowsThePriority(ThreadPriority priority, int nice) => Assert.Equal(nice, HostThreads.NiceOf(priority));

    [Theory]
    [InlineData(ThreadPriority.Lowest)]
    [InlineData(ThreadPriority.BelowNormal)]
    public void ABackgroundThreadIsLoweredAndOnlyIt(ThreadPriority priority)
    {
        int callerNice = HostThreads.CurrentNice();
        int set = -1, seen = -1;
        ThreadPriority managed = ThreadPriority.Normal;
        var thread = new Thread(() =>
        {
            set = HostThreads.LowerCurrent(priority);
            seen = HostThreads.CurrentNice();
            managed = Thread.CurrentThread.Priority;
        });
        thread.Start();
        thread.Join();
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable(EnvVars.BackgroundNice) is not ("0" or "off")
            && callerNice < HostThreads.NiceOf(priority))
        {
            // (a runner that is itself niced at least as low has nothing to lower)
            Assert.Equal(HostThreads.NiceOf(priority), set);
            Assert.Equal(HostThreads.NiceOf(priority), seen);
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(priority, managed);
        }

        Assert.Equal(callerNice, HostThreads.CurrentNice()); // the caller is untouched
    }
}
