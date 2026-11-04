using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the ports are reopened on the host with their saved parameters (the host output starts
/// empty); output pacing continues from the saved deadlines on the restored guest clock.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_ports")]
public sealed partial class Audio
{
    [GenerateSnapshot]
    sealed partial class InPort;

    void SaveExtra(SnapshotWriter w)
    {
        lock (_ports)
        {
            w.Write(_ports.Count);
            foreach (var (id, p) in _ports)
            {
                w.Write(id); w.Write(p.Type); w.Write(p.Grain); w.Write(p.Freq); w.Write(p.Channels);
                w.WriteArray(p.Volume); w.Write(p.NextDeadline);
            }
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_ports)
        {
            foreach (var id in _ports.Keys.ToList()) sceAudioOutReleasePort(id);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                var p = new Port { Type = r.ReadInt32(), Grain = r.ReadInt32(), Freq = r.ReadInt32(), Channels = r.ReadInt32() };
                p.Volume = r.ReadArray<float>() ?? [1, 1];
                p.NextDeadline = r.ReadDouble();
                try { p.Stream = Emu.Host.Audio?.OpenStream(p.Freq, 2, p.Grain); } catch { p.Stream = null; }
                p.Stats = AudioDiagnostics.OpenPort($"port {id} {TypeName(p.Type)}{(id == ExtPortId ? " (ext)" : "")}", p.Grain, p.Freq, p.Channels, p.Stream);
                p.DumpName = $"port{id}_{TypeName(p.Type)}";
                p.Dump = AudioDump.Open(p.DumpName, p.Freq);
                p.DumpGen = AudioDump.Generation;
                _ports[id] = p;
            }
        }
    }
}
