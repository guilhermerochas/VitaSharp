using System.Numerics;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Hle.Motion;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceMotion's magnetometer (psp2/motion.h; driver_us 3.74 passes sceMotionMagnetometerOn/Off to the sensor driver and
/// copies its field stability into SceMotionState +0xF4): with the magnetometer on and a host source whose
/// magnetometer reads a steady Earth field, sceMotionGetState reports magFieldStability STABLE and the NED matrix (the
/// device axes in North-East-Down) from gravity and the field; off, without a field, or with a disturbed one, UNSTABLE
/// and the NED matrix = the rotation matrix, as before. VitaSharp only kept the switch: the compass never had a north.
/// Synthetic: a source the test feeds with <see cref="MotionAxes.EarthField"/> readings.
/// </summary>
[Collection("guest")]
public sealed class MotionMagnetometerTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "magnetometer");

    public void Dispose() => _harness.Dispose();

    /// <summary>A source the test fills; every read hands over what was queued.</summary>
    private sealed class TestMotion : IMotionSource
    {
        private readonly List<MotionSample> _queue = [];
        private ulong _us = 1000;

        public bool HasSensors => true;

        /// <summary>Queues <paramref name="count"/> samples of a Vita lying still with these readings.</summary>
        public void Hold(Vector3 gravity, Func<int, Vector3> field, int count = 12)
        {
            lock (_queue)
            {
                for (int i = 0; i < count; i++)
                {
                    _us += 5000;
                    _queue.Add(new MotionSample(_us, gravity, Vector3.Zero, field(i)));
                }
            }
        }

        public int ReadSamples(Span<MotionSample> dest)
        {
            lock (_queue)
            {
                int n = Math.Min(dest.Length, _queue.Count);
                for (int i = 0; i < n; i++)
                {
                    dest[i] = _queue[i];
                }

                _queue.RemoveRange(0, n);
                return n;
            }
        }
    }

    private Sensors Module => _harness.Module<Sensors>();

    private (TestMotion Source, uint State) Start()
    {
        var source = new TestMotion();
        _harness.Emu.Host.Motion = source;
        Assert.Equal(0, Call(Module, "sceMotionStartSampling"));
        return (source, _harness.Alloc(MotionDevice.StateSize));
    }

    private static byte Stability(uint state) => GuestMemory.Read<byte>(state + 244);

    /// <summary>Row <paramref name="axis"/> of the NED matrix: that device axis in (north, east, down).</summary>
    private static Vector3 NedRow(uint state, int axis) => new(
        GuestMemory.Read<float>(state + 120 + (uint)axis * 16),
        GuestMemory.Read<float>(state + 124 + (uint)axis * 16),
        GuestMemory.Read<float>(state + 128 + (uint)axis * 16));

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void ASteadyFieldGivesTheNedMatrixAndAStableField()
    {
        (TestMotion source, uint state) = Start();
        Assert.Equal(0, Call(Module, "sceMotionMagnetometerOn"));
        Assert.Equal(1, Call(Module, "sceMotionGetMagnetometerState"));
        Vector3 flat = MotionAxes.TiltGravity(0, 0);

        // lying flat, the top edge towards magnetic north: device y = north, x = east, z = up
        source.Hold(flat, _ => MotionAxes.EarthField(0, 0, 0));
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldStable, Stability(state));
        AssertNear(new Vector3(0, 1, 0), NedRow(state, 0));
        AssertNear(new Vector3(1, 0, 0), NedRow(state, 1));
        AssertNear(new Vector3(0, 0, -1), NedRow(state, 2));

        // turned so the top edge points east: device y = east, x = south
        source.Hold(flat, _ => MotionAxes.EarthField(90, 0, 0));
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldStable, Stability(state));
        AssertNear(new Vector3(-1, 0, 0), NedRow(state, 0));
        AssertNear(new Vector3(0, 1, 0), NedRow(state, 1));

        // standing up (top edge raised 90 degrees) facing north: the screen faces south, the top edge is up
        Vector3 standing = MotionAxes.TiltGravity(MathF.PI / 2, 0);
        source.Hold(standing, _ => MotionAxes.EarthField(0, MathF.PI / 2, 0));
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldStable, Stability(state));
        AssertNear(new Vector3(0, 0, -1), NedRow(state, 1)); // device y (top edge) points up
        AssertNear(new Vector3(-1, 0, 0), NedRow(state, 2)); // device z (out of the screen) points south
    }

    [Fact]
    public void WithTheMagnetometerOffOrWithoutAFieldItIsUnstableAndTheNedMatrixIsTheRotation()
    {
        (TestMotion source, uint state) = Start();
        Vector3 flat = MotionAxes.TiltGravity(0, 0);

        // off (the default): the field is not used
        source.Hold(flat, _ => MotionAxes.EarthField(90, 0, 0));
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldUnstable, Stability(state));
        for (uint i = 0; i < 16; i++)
        {
            Assert.Equal(GuestMemory.Read<float>(state + 56 + i * 4), GuestMemory.Read<float>(state + 120 + i * 4));
        }

        // on, but the source has no magnetometer
        Assert.Equal(0, Call(Module, "sceMotionMagnetometerOn"));
        source.Hold(flat, _ => Vector3.Zero);
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldUnstable, Stability(state));
    }

    [Fact]
    public void ADisturbedFieldIsUnstable()
    {
        (TestMotion source, uint state) = Start();
        Assert.Equal(0, Call(Module, "sceMotionMagnetometerOn"));
        Vector3 flat = MotionAxes.TiltGravity(0, 0);

        // a magnet passing by: the readings jump
        source.Hold(flat, i => MotionAxes.EarthField(0, 0, 0) + new Vector3(i % 2 == 0 ? 15 : -15, 0, 0));
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldUnstable, Stability(state));

        // far stronger than the Earth's field (a speaker magnet on the unit)
        source.Hold(flat, _ => MotionAxes.EarthField(0, 0, 0) * 5);
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldUnstable, Stability(state));

        // steady again
        source.Hold(flat, _ => MotionAxes.EarthField(0, 0, 0));
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldStable, Stability(state));

        // switched off: unstable again
        Assert.Equal(0, Call(Module, "sceMotionMagnetometerOff"));
        Assert.Equal(0, Call(Module, "sceMotionGetMagnetometerState"));
        source.Hold(flat, _ => MotionAxes.EarthField(0, 0, 0));
        Assert.Equal(0, Call(Module, "sceMotionGetState", state));
        Assert.Equal(MotionDevice.FieldUnstable, Stability(state));
    }

    [Fact]
    public void TheEarthFieldOfAHeadingPointsNorthAndDown()
    {
        AssertNear(new Vector3(0, MotionAxes.EarthFieldHorizontal, -MotionAxes.EarthFieldVertical), MotionAxes.EarthField(0, 0, 0));
        AssertNear(new Vector3(-MotionAxes.EarthFieldHorizontal, 0, -MotionAxes.EarthFieldVertical), MotionAxes.EarthField(90, 0, 0));
        Span<float> m = stackalloc float[16];
        Assert.False(MotionDevice.NedMatrix(new Vector3(0, 0, -1), new Vector3(0, 0, -40), m)); // field along gravity: no heading
        Assert.False(MotionDevice.NedMatrix(Vector3.Zero, new Vector3(0, 20, -40), m));
    }
}
