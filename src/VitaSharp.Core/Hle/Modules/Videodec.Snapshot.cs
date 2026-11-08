using VitaSharp.Core.Media;
using VitaSharp.Core.Util;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: a host video decoder cannot be saved; each one is created again with its saved size, given
/// the stream's SPS and PPS again and the access units since the last IDR picture (decoded again, their pictures
/// dropped), so it holds the same reference pictures as before the save. Pictures already decoded but not yet taken
/// are dropped; a GOP too long to keep restarts at the next IDR as before.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_decoders", "_carryOver")]
public sealed partial class Videodec
{
    void SaveExtra(SnapshotWriter w)
    {
        lock (_decoders)
        {
            w.Write(_decoders.Count);
            foreach (var (h, d) in _decoders)
            {
                w.Write(h); w.Write(d.MaxWidth); w.Write(d.MaxHeight); w.Write(d.Started); w.Write(d.ParamSets);
                // the units since the last IDR (none when that GOP was too long to keep)
                int units = d.ReplayValid ? d.ReplayUnits.Count : 0;
                w.Write(units);
                for (int i = 0; i < units; i++)
                {
                    var (off, len, pts) = d.ReplayUnits[i];
                    w.Write(pts);
                    w.Write(d.Replay.AsSpan(off, len).ToArray());
                }
                w.Write(d.LastTakenPts);
            }
        }
    }

    /// <summary>Feeds a decoder created by a load the units since the last IDR the saved one had decoded, so the units the
    /// game sends next find their reference pictures; the pictures this produces again are dropped (the game already
    /// had them before the save).</summary>
    static void Replay(Decoder d, List<(long Pts, byte[] Data)> units, long lastTakenPts)
    {
        if (d.Codec == null || units.Count == 0) return;
        if (d.ParamSets != null && ParameterSets(units[0].Data) == null) d.Codec.Send(d.ParamSets, units[0].Pts);
        d.ResendParamSets = false;
        foreach (var (pts, data) in units)
        {
            d.Remember(data, pts);
            d.Codec.Send(data, pts);
        }
        d.SkipThroughPts = lastTakenPts;
        Log.Info($"sceAvcdec: save state load replayed {units.Count} units since the last IDR into a new decoder (its pictures up to pts {lastTakenPts} were shown before the save and are dropped)");
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_decoders)
        {
            foreach (var d in _decoders.Values) d.Codec?.Dispose();
            _decoders.Clear();
            _carryOver = null;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                uint h = r.ReadUInt32(), w = r.ReadUInt32(), ht = r.ReadUInt32();
                bool started = r.ReadBool();
                var ps = r.ReadBytes();
                int count = r.ReadInt32();
                var units = new List<(long Pts, byte[] Data)>(count);
                for (int u = 0; u < count; u++) { long pts = r.ReadInt64(); units.Add((pts, r.ReadBytes() ?? [])); }
                long lastTaken = r.ReadInt64();
                var d = new Decoder { Codec = VideoDecoders.CreateH264(new VideoDecoderOptions((int)w, (int)ht)), MaxWidth = w, MaxHeight = ht, Started = started, ParamSets = ps, ResendParamSets = ps != null };
                _decoders[h] = d;
                d.LastTakenPts = lastTaken;
                Replay(d, units, lastTaken);
            }
        }
    }
}

[GenerateSnapshot]
public sealed partial class CodecEngine
{
    [GenerateSnapshot]
    sealed partial class Block;
}
