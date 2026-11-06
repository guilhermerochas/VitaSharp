using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The NGS amplitude envelope module (SceNgsEnvelopeParams): up to 4 points, each an amplitude and the time to the next
/// point, held at the last point after key on, and a release from the current height to 0 over uReleaseMsecs after
/// sceNgsVoiceKeyOff ("processing continues until the voice finishes"); the voice ends when the release does. It used
/// to be a passthrough and a key off stopped the voice on the next update, so every keyed-off sound was cut mid-wave:
/// Jak 1's Scream voices release over 23 ms to 12 s and were cut at full level (a click per sound, 150 in 40 s).
/// Curved segments are approximated as linear. State (SceNgsEnvelopeStates, 20 bytes) lives in the module's GuestState:
/// +0 current height, +4 position in the segment (ms), +8 release scale (height when the release started), +12
/// current point, +16 releasing. <c>VITASHARP_NGS_ENVELOPE=0</c> restores the passthrough (A/B).
/// </summary>
public sealed partial class Ngs
{
    const uint EnvelopeParamsId = 0x01015CE3;
    static readonly bool EnvelopeOff = Environment.GetEnvironmentVariable(EnvVars.NgsEnvelope) == "0";

    /// <summary>The voice's envelope module when it shapes the sound (present, not bypassed, valid parameters).</summary>
    static NgsModuleData? ActiveEnvelope(NgsVoice v)
    {
        if (EnvelopeOff) return null;
        foreach (var m in v.Modules)
            if (m.Kind == NgsModuleKind.Envelope && !m.Bypassed && m.ParamAddr != 0 && m.GuestState.Length >= 20 && m.Param32(0) == EnvelopeParamsId)
                return m;
        return null;
    }

    static float EnvF(NgsModuleData m, int off) => BitConverter.ToSingle(m.GuestState, off);
    static void EnvF(NgsModuleData m, int off, float v) => BitConverter.TryWriteBytes(m.GuestState.AsSpan(off), v);

    /// <summary>Key on: the envelope starts at its first point.</summary>
    static void ResetEnvelope(NgsModuleData m)
    {
        if (m.GuestState.Length < 20) return;
        Array.Clear(m.GuestState, 0, 20);
        int n = m.ParamAddr == 0 ? 0 : (int)Math.Min(m.Param32(8 + 48 + 4), 4u);
        EnvF(m, 0, n > 0 ? m.ParamF(8 + 4) : 1f);
        EnvF(m, 8, 1f);
    }

    /// <summary>Starts the release; false when the envelope has no release time (the voice then stops at once).</summary>
    static bool StartRelease(NgsModuleData m)
    {
        if (m.Param32(8 + 48) == 0) return false;
        if (GetState(m, 16) != 0) return true;
        EnvF(m, 8, EnvF(m, 0)); // release from the current height
        SetState(m, 16, 1);
        EnvF(m, 4, 0);
        return true;
    }

    /// <summary>Applies one granule of the envelope to the voice's product; true when the release has ended.</summary>
    bool ProcessEnvelope(NgsVoice v, NgsModuleData m, int g)
    {
        if (EnvelopeOff || m.Bypassed || m.ParamAddr == 0 || m.GuestState.Length < 20 || m.Param32(0) != EnvelopeParamsId) return false;
        int points = (int)Math.Min(m.Param32(8 + 48 + 4), 4u);
        uint releaseMs = m.Param32(8 + 48);
        bool releasing = GetState(m, 16) != 0;
        if (points == 0 && !releasing) return false;
        float msPerSample = 1000f / v.Rack.System.SampleRate;
        float height = EnvF(m, 0), pos = EnvF(m, 4), relScale = EnvF(m, 8);
        int point = GetState(m, 12);
        // the points once per granule (a guest memory read per sample was most of the module's cost)
        Span<uint> dur = stackalloc uint[4];
        Span<float> amp = stackalloc float[4];
        for (int k = 0; k < points; k++) { dur[k] = m.Param32(8 + k * 12); amp[k] = m.ParamF(8 + k * 12 + 4); }
        int loopStart = (int)m.Param32(8 + 48 + 8), loopEnd = (int)m.Param32(8 + 48 + 12);
        var buf = v.Product;
        bool done = false;
        int steps = 0; // point changes in this granule (zero-length segments in a loop must not spin)
        for (int i = 0; i < g; i++)
        {
            if (releasing)
            {
                height = releaseMs == 0 ? 0 : relScale * (1f - pos / releaseMs);
                if (height <= 0) { height = 0; done = true; }
            }
            else if (point >= 0 && point < points - 1)
            {
                // segment point -> point + 1 over the point's uMsecsToNextPoint (0 = a step)
                uint d = dur[point];
                float a0 = amp[point], a1 = amp[point + 1];
                if (pos >= d && steps++ < 16) { point++; pos -= d; height = a1; i--; continue; }
                height = a0 + (a1 - a0) * (d == 0 ? 1f : Math.Min(pos / d, 1f));
            }
            else
            {
                // at the last point: an envelope loop (uLoopStart..nLoopEnd) restarts its segment, else it holds
                height = points > 0 ? amp[points - 1] : 1f;
                if (loopEnd >= 0 && loopEnd == point && loopStart >= 0 && loopStart < loopEnd && steps++ < 16) { point = loopStart; pos = 0; i--; continue; }
            }
            if (buf != null) { buf[2 * i] *= height; buf[2 * i + 1] *= height; }
            pos += msPerSample;
        }
        EnvF(m, 0, height); EnvF(m, 4, pos); SetState(m, 12, point);
        return done;
    }
}
