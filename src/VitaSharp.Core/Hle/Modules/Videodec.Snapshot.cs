using VitaSharp.Core.Media;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: a host video decoder cannot be saved; each one is created again with its saved size and
/// starts empty (the restored movie restarts at a key frame, see Mp4.Snapshot.cs; pictures already decoded but not
/// yet taken are dropped). It is given the stream's SPS and PPS again before its first unit.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_decoders", "_carryOver")]
public sealed partial class Videodec
{
    void SaveExtra(SnapshotWriter w)
    {
        lock (_decoders)
        {
            w.Write(_decoders.Count);
            foreach (var (h, d) in _decoders) { w.Write(h); w.Write(d.MaxWidth); w.Write(d.MaxHeight); w.Write(d.Started); w.Write(d.ParamSets); }
        }
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
                _decoders[h] = new Decoder { Codec = VideoDecoders.CreateH264(new VideoDecoderOptions((int)w, (int)ht)), MaxWidth = w, MaxHeight = ht, Started = started, ParamSets = ps, ResendParamSets = ps != null };
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
