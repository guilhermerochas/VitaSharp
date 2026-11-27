using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VitaSharp.Mobile.Core.Library;

/// <summary>
/// The phases of a screen's opening, from the tap that opened it (Stopwatch timestamps): each <see cref="Mark"/> keeps
/// the first time a phase was reached, and <see cref="Format"/> writes them in order as milliseconds from the tap
/// ("mount 180 ms, data 182 ms, drawn 410 ms"), for the app log line the F8 capture carries. Thread-safe.
/// </summary>
public sealed class OpenPhases(long startTimestamp)
{
    private readonly List<(string Name, long Timestamp)> _phases = new();

    public long StartTimestamp { get; } = startTimestamp;

    /// <summary>Records <paramref name="name"/> at <paramref name="timestamp"/> unless it was already reached.</summary>
    public void Mark(string name, long timestamp)
    {
        lock (_phases)
        {
            foreach ((string Name, long Timestamp) phase in _phases)
            {
                if (phase.Name == name)
                {
                    return;
                }
            }

            _phases.Add((name, timestamp));
        }
    }

    /// <summary>Records <paramref name="name"/> now.</summary>
    public void Mark(string name) => Mark(name, Stopwatch.GetTimestamp());

    public bool Reached(string name)
    {
        lock (_phases)
        {
            return _phases.Exists(phase => phase.Name == name);
        }
    }

    /// <summary>Milliseconds from the start to <paramref name="name"/>, or -1 when it was not reached.</summary>
    public long MillisecondsTo(string name)
    {
        lock (_phases)
        {
            foreach ((string Name, long Timestamp) phase in _phases)
            {
                if (phase.Name == name)
                {
                    return (long)Stopwatch.GetElapsedTime(StartTimestamp, phase.Timestamp).TotalMilliseconds;
                }
            }
        }

        return -1;
    }

    /// <summary>"name N ms, ..." in the order the phases were reached.</summary>
    public string Format()
    {
        var text = new StringBuilder();
        lock (_phases)
        {
            foreach ((string Name, long Timestamp) phase in _phases)
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                long ms = (long)Stopwatch.GetElapsedTime(StartTimestamp, phase.Timestamp).TotalMilliseconds;
                text.Append(phase.Name).Append(' ').Append(ms.ToString(CultureInfo.InvariantCulture)).Append(" ms");
            }
        }

        return text.ToString();
    }
}
