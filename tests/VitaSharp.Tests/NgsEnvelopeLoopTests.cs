using System.Reflection;
using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The NGS envelope's sustain loop (SceNgsEnvelopeParams uLoopStart / nLoopEnd): reaching the loop's end point goes
/// back to its start wherever the loop sits, so the voice holds there until key off or until the game moves the loop.
/// Assassin's Creed III Liberation's voices use 4 points with the loop on points 1..2 (amplitude 1) and a point 3 at
/// 0: looping only at the last point ran every voice into that 0 (sound effects cut after 105 ms, streams silent).
/// </summary>
[Collection("guest")]
public sealed class NgsEnvelopeLoopTests(GuestMemoryFixture fixture)
{
    private const int Granularity = 512;
    private const int SampleRate = 48000;

    private static object? Invoke(Ngs module, string name, params object[] args) =>
        typeof(Ngs).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, args);

    private static object? Static(string name, params object[] args) =>
        typeof(Ngs).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    /// <summary>The parameters as the game writes them: 4 points (ms to next, amplitude), release, loop.</summary>
    private uint Params(uint[] msToNext, float[] amplitude, uint releaseMs, uint loopStart, int loopEnd)
    {
        uint prm = fixture.Memory.Alloc(0x48, "ngs envelope params");
        GuestMemory.Fill(prm, 0, 0x48);
        GuestMemory.W32(prm, 0x01015CE3);
        GuestMemory.W32(prm + 4, 0x48);
        for (uint i = 0; i < 4; i++)
        {
            GuestMemory.W32(prm + 8 + i * 12, msToNext[i]);
            GuestMemory.W32(prm + 12 + i * 12, BitConverter.SingleToUInt32Bits(amplitude[i]));
        }
        GuestMemory.W32(prm + 56, releaseMs);
        GuestMemory.W32(prm + 60, 4);
        GuestMemory.W32(prm + 64, loopStart);
        GuestMemory.W32(prm + 68, unchecked((uint)loopEnd));
        return prm;
    }

    private static (NgsVoice Voice, NgsModuleData Module) Envelope(uint prm)
    {
        var sys = new NgsSystem { Handle = 0x1000, Granularity = Granularity, SampleRate = SampleRate };
        var rack = new NgsRack { System = sys, ChannelsPerVoice = 2 };
        var voice = new NgsVoice { Rack = rack };
        var m = new NgsModuleData
        {
            Voice = voice,
            Kind = NgsModuleKind.Envelope,
            ParamAddr = prm,
            ParamSize = 0x48,
            GuestState = new byte[20],
        };
        voice.Modules = [m];
        Static("ResetEnvelope", m);
        return (voice, m);
    }

    /// <summary>Runs one granule over a constant 1.0 signal; returns the envelope heights it applied.</summary>
    private static float[] Granule(Ngs ngs, NgsVoice voice, NgsModuleData m, out bool done)
    {
        float[] product = new float[Granularity * 2];
        Array.Fill(product, 1f);
        voice.Product = product;
        done = (bool)Invoke(ngs, "ProcessEnvelope", voice, m, Granularity)!;
        return product;
    }

    private static int GranulesIn(double seconds) => (int)(seconds * SampleRate / Granularity);

    [Fact]
    public void SustainLoopBeforeTheLastPointHoldsTheVoiceUntilKeyOff()
    {
        // AC3L's streamed voices: 0 ms to 0, 5 ms to 1, 0 ms to 1, then a point at 0; loop 1..2, no release
        var ngs = new Ngs();
        (NgsVoice voice, NgsModuleData m) = Envelope(Params([0, 5, 0, 0], [0f, 1f, 1f, 0f], 0, 1, 2));
        for (int g = 0; g < GranulesIn(10); g++)
        {
            float[] heights = Granule(ngs, voice, m, out bool done);
            Assert.False(done);
            Assert.All(heights, h => Assert.Equal(1f, h));
        }
    }

    [Fact]
    public void FadeInThenSustainHasNoHoleInsideTheSound()
    {
        // AC3L's sound effects: 100 ms fade-in from 0, then the same sustain loop; the sound must not stop at 105 ms
        var ngs = new Ngs();
        (NgsVoice voice, NgsModuleData m) = Envelope(Params([100, 5, 0, 0], [0f, 1f, 1f, 0f], 0, 1, 2));
        float last = -1f;
        int samples = 0;
        for (int g = 0; g < GranulesIn(2); g++)
        {
            float[] heights = Granule(ngs, voice, m, out bool done);
            Assert.False(done);
            for (int i = 0; i < heights.Length; i += 2)
            {
                float h = heights[i];
                Assert.True(h >= last - 1e-6f, $"the envelope went down at sample {samples}: {last} -> {h}");
                if (samples >= SampleRate / 10)
                {
                    Assert.Equal(1f, h, 4);
                }
                last = h;
                samples++;
            }
        }
    }

    [Fact]
    public void LoopMovedAwayLetsTheEnvelopeRunToItsLaterPoints()
    {
        // AC3L ends a sound by rewriting the parameters: point 2 now fades to point 3 (0) over 100 ms and the loop
        // end is negative (no loop): the envelope leaves the sustain and fades out, then holds 0
        var ngs = new Ngs();
        (NgsVoice voice, NgsModuleData m) = Envelope(Params([0, 5, 0, 0], [0f, 1f, 1f, 0f], 0, 1, 2));
        for (int g = 0; g < 4; g++)
        {
            Granule(ngs, voice, m, out _);
        }
        GuestMemory.W32(m.ParamAddr + 8 + 2 * 12, 100);
        GuestMemory.W32(m.ParamAddr + 68, unchecked((uint)-3));
        float[] heights = [];
        for (int g = 0; g < GranulesIn(0.3); g++)
        {
            heights = Granule(ngs, voice, m, out bool done);
            Assert.False(done);
        }
        Assert.Equal(0f, heights[^1]);
    }

    [Fact]
    public void SustainLoopEndsWithTheReleaseAfterKeyOff()
    {
        // the same sustain with a 10 ms release: key off fades from the held height to 0 and ends the voice
        var ngs = new Ngs();
        (NgsVoice voice, NgsModuleData m) = Envelope(Params([0, 5, 0, 0], [0f, 1f, 1f, 0f], 10, 1, 2));
        for (int g = 0; g < GranulesIn(1); g++)
        {
            Granule(ngs, voice, m, out _);
        }
        Assert.True((bool)Static("StartRelease", m)!);
        // 10 ms = 480 samples, inside one 512-sample granule
        float[] heights = Granule(ngs, voice, m, out bool done);
        Assert.True(done);
        Assert.True(heights[0] > 0.99f);
        Assert.True(heights[240 * 2] > 0.45f && heights[240 * 2] < 0.55f);
        Assert.Equal(0f, heights[^1]);
    }

    [Fact]
    public void LoopEndingOnTheLastPointStillRepeats()
    {
        // the loop on the last segment (points 2..3, 1 -> 0.5 over 10 ms): the height cycles and never holds at 0.5
        var ngs = new Ngs();
        (NgsVoice voice, NgsModuleData m) = Envelope(Params([0, 0, 10, 0], [1f, 1f, 1f, 0.5f], 0, 2, 3));
        int risesBackToOne = 0;
        float last = 1f;
        for (int g = 0; g < GranulesIn(0.2); g++)
        {
            float[] heights = Granule(ngs, voice, m, out _);
            for (int i = 0; i < heights.Length; i += 2)
            {
                if (heights[i] > last + 0.25f)
                {
                    risesBackToOne++;
                }
                last = heights[i];
            }
        }
        Assert.True(risesBackToOne >= 15, $"the loop repeated {risesBackToOne} times in 200 ms (10 ms each)");
    }
}
