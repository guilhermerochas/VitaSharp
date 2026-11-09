namespace VitaSharp.Core.Input;

/// <summary>
/// Gives a stick driven by keys the travel time of a real one. A key jumps the stick from the centre to the edge
/// between two samples, which no thumb does (a fast flick takes a few frames). Games that read the stick as a gesture
/// treat that jump differently: the Dead Space port turns the left stick into a touch on its on-screen joystick (a
/// press at the centre, then moves): a stick that jumps to the rim did not walk at all (Isaac stood still with the
/// keyboard), the same push over 6 frames walked, as a controller's stick does. The keyboard's stick positions move
/// toward the keys' target at a bounded rate (the whole travel, centre to edge, in <see cref="TravelMs"/>), both ways.
/// </summary>
public sealed class KeyStickSlew
{
    /// <summary>Time for a full deflection from the centre (and back).</summary>
    public const double TravelMs = 100;

    float _lx, _ly, _rx, _ry;
    long _last = -1;

    /// <summary>Moves the stick positions toward <paramref name="m"/>'s (keyboard input) and writes them back.</summary>
    public void Apply(ref MappedInput m, long nowTicks, long ticksPerSecond)
    {
        float step = _last < 0 ? 0f : (float)((nowTicks - _last) * 1000.0 / ticksPerSecond / TravelMs);
        _last = nowTicks;
        step = Math.Clamp(step, 0f, 2f);
        _lx = Toward(_lx, m.Lx, step); _ly = Toward(_ly, m.Ly, step);
        _rx = Toward(_rx, m.Rx, step); _ry = Toward(_ry, m.Ry, step);
        (m.Lx, m.Ly, m.Rx, m.Ry) = (_lx, _ly, _rx, _ry);
        m.State.Lx = StickMath.ToByte(_lx); m.State.Ly = StickMath.ToByte(_ly);
        m.State.Rx = StickMath.ToByte(_rx); m.State.Ry = StickMath.ToByte(_ry);
    }

    static float Toward(float cur, float target, float step)
    {
        if (Math.Abs(target - cur) <= step) return target;
        return cur + MathF.Sign(target - cur) * step;
    }
}
