using At9Decoder = VitaSharp.Core.Hle.Audio.Atrac9.Atrac9Decoder;
using VitaSharp.Core.Media;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: decoders are created again from their creation parameters (the info block the game passed)
/// with their stream position; a decoder's inter-frame history is not kept, so the first frame after a load may
/// differ slightly.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_decoders")]
public sealed unsafe partial class Audiodec
{
    sealed partial class Decoder
    {
        /// <summary>The first bytes of the codec info block at creation (save states recreate the host decoder).</summary>
        public byte[] Info = [];
    }

    void SaveExtra(SnapshotWriter w)
    {
        lock (_decoders)
        {
            w.Write(_decoders.Count);
            foreach (var (h, d) in _decoders)
            {
                w.Write(h); w.Write(d.Type); w.Write(d.Channels); w.Write(d.Info); w.Write(d.FrameIndex); w.Write(d.SuperframeUsed); w.Write(d.Adts);
                w.Write(d.Pcm.Length);
            }
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_decoders)
        {
            foreach (var d in _decoders.Values) d.Codec?.Dispose();
            _decoders.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                uint h = r.ReadUInt32();
                var d = new Decoder { Type = r.ReadUInt32(), Channels = r.ReadInt32(), Info = r.ReadBytes() ?? [] };
                d.FrameIndex = r.ReadInt32(); d.SuperframeUsed = r.ReadInt32(); d.Adts = r.ReadBool();
                d.Pcm = new short[r.ReadInt32()];
                uint Word(int o) => d.Info.Length >= o + 4 ? BitConverter.ToUInt32(d.Info, o) : 0;
                switch (d.Type)
                {
                    case TYPE_AT9:
                        d.At9 = new At9Decoder();
                        d.At9.Initialize(BitConverter.GetBytes(Word(4)));
                        var c = d.At9.Config;
                        d.At9Pcm = new short[c.ChannelCount][];
                        for (int k = 0; k < c.ChannelCount; k++) d.At9Pcm[k] = new short[c.FrameSamples];
                        d.Staging = new byte[c.SuperframeBytes + 16];
                        break;
                    case TYPE_MP3:
                        d.Codec = AudioFrameDecoders.CreateMp3(d.Channels);
                        break;
                    case TYPE_AAC:
                    {
                        int rate = (int)Word(12);
                        bool sbr = Word(16) != 0;
                        d.Codec = AudioFrameDecoders.CreateAac(rate, d.Channels, d.Adts ? null : FrameDecoder.AacConfig(sbr ? rate / 2 : rate, d.Channels));
                        break;
                    }
                }
                _decoders[h] = d;
            }
        }
    }
}
