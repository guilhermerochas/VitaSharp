using System.Diagnostics;
using System.Globalization;
using VitaSharp.Core.Host;
using VitaSharp.Desktop.Host;

namespace VitaSharp.Desktop;

/// <summary>"mic-test [--microphone=&lt;name|index|tone[:HZ]&gt;] [--seconds=N]": lists the capture devices, opens the
/// microphone the way a game's VOICE port does (16 kHz, 256-sample grains, through <see cref="MicrophoneFeed"/>) and
/// prints the level of what it hears twice a second. Nothing is recorded or saved.</summary>
public static class MicrophoneTest
{
    public static int Run(string[] args)
    {
        string? setting = Runner.Opt(args, "--microphone");
        double seconds = Runner.Opt(args, "--seconds") is { } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 5;
        OpenAlMicrophone? openAl = OpenAlMicrophone.TryCreate(MicrophoneSpec.Default);
        if (openAl != null)
        {
            List<string> names = openAl.ListDevices(out string? defaultName);
            Console.WriteLine($"capture devices ({names.Count}):");
            for (int i = 0; i < names.Count; i++)
            {
                Console.WriteLine($"{(names[i] == defaultName ? "* " : "  ")}{i}: {names[i]}");
            }
        }

        using IMicrophoneSource? source = Runner.CreateMicrophone(setting);
        if (source == null)
        {
            Console.WriteLine("microphone off (--microphone=none): the Vita microphone is silent");
            return 0;
        }

        const int rate = 16000;
        const int grainSamples = 256;
        using var feed = new MicrophoneFeed(source.Open(rate), rate, grainSamples);
        Console.WriteLine($"VOICE port: {feed.Describe()}");
        if (feed.Stream == null)
        {
            return 1;
        }

        short[] grain = new short[grainSamples];
        var clock = Stopwatch.StartNew();
        double period = (double)grainSamples / rate;
        long grains = 0;
        int peak = 0;
        double sumSquares = 0;
        int samples = 0;
        double nextReport = 0.5;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            double due = (grains + 1) * period;
            double wait = due - clock.Elapsed.TotalSeconds;
            if (wait > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            }

            feed.Fill(grain, muted: false);
            grains++;
            foreach (short sample in grain)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
                sumSquares += (double)sample * sample;
            }

            samples += grain.Length;
            if (due >= nextReport)
            {
                double rms = Math.Sqrt(sumSquares / Math.Max(1, samples));
                Console.WriteLine($"{due,5:F1} s  peak {peak,5}  rms {rms,7:F1}  {new string('#', (int)Math.Min(40, rms / 200))}");
                peak = 0;
                sumSquares = 0;
                samples = 0;
                nextReport += 0.5;
            }
        }

        Console.WriteLine(feed.Describe());
        return 0;
    }
}
