using System.Diagnostics;
using VitaSharp.Mobile.Core.Library;

namespace VitaSharp.Tests;

/// <summary>The phases of the Android Trofeus screen's opening, written to its "Trophies:" log line.</summary>
public sealed class OpenPhasesTests
{
    [Fact]
    public void PhasesAreWrittenInOrderFromTheStartAndKeepTheirFirstTime()
    {
        long start = 1_000_000;
        long perMs = Stopwatch.Frequency / 1000;
        var phases = new OpenPhases(start);
        phases.Mark("mount", start + 120 * perMs);
        phases.Mark("data", start + 125 * perMs);
        phases.Mark("mount", start + 900 * perMs);
        phases.Mark("drawn", start + 410 * perMs);
        Assert.Equal("mount 120 ms, data 125 ms, drawn 410 ms", phases.Format());
        Assert.True(phases.Reached("data"));
        Assert.False(phases.Reached("render"));
        Assert.Equal(410, phases.MillisecondsTo("drawn"));
        Assert.Equal(-1, phases.MillisecondsTo("render"));
    }
}
