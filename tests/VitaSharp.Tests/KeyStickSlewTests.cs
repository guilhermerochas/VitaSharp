using VitaSharp.Core.Input;
using Xunit;

namespace VitaSharp.Tests;

public sealed class KeyStickSlewTests
{
    private static MappedInput Held(float ly)
    {
        MappedInput m = MappedInput.Neutral;
        m.Ly = ly;
        m.State.Ly = StickMath.ToByte(ly);
        return m;
    }

    [Fact]
    public void AKeyMovesTheStickOverItsTravelTime()
    {
        var s = new KeyStickSlew();
        const long TicksPerSecond = 1000; // ticks per second = milliseconds
        MappedInput m = Held(-1);
        s.Apply(ref m, 0, TicksPerSecond);
        Assert.Equal(128, m.State.Ly); // the first sample only starts the clock
        m = Held(-1);
        s.Apply(ref m, 50, TicksPerSecond);
        Assert.Equal(-0.5f, m.Ly, 3); // half the travel after half of 100 ms
        Assert.InRange(m.State.Ly, 60, 70);
        m = Held(-1);
        s.Apply(ref m, 100, TicksPerSecond);
        Assert.Equal(-1f, m.Ly);
        Assert.Equal(0, m.State.Ly);
        m = Held(0);
        s.Apply(ref m, 150, TicksPerSecond); // released: back toward the centre at the same rate
        Assert.Equal(-0.5f, m.Ly, 3);
        m = Held(0);
        s.Apply(ref m, 300, TicksPerSecond);
        Assert.Equal(128, m.State.Ly);
    }
}
