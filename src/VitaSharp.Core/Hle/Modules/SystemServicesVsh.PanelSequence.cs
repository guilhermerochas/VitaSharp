using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The command sequences of the display panel drivers (os0:kd/oled.skprx and lcd.skprx 3.74, read in the modules, study
// only). A sequence is a list of entries, each a command byte, a length byte and that many data bytes; the command
// 0x0D is a pause of a number of steps (its length byte), 0xFF ends the list. A driver keeps one pending sequence and a
// pause counter: each step (SceDisplay's vertical blank, or the caller's own 16 ms wait loop in synchronous mode) either
// counts the pause down or sends the next commands up to the next pause or the end; a new sequence while one is pending
// is refused. The sequences built into a module are modelled as one step long: their bytes are the module's data, and
// the emulated panel takes the commands without timing.
public sealed partial class SystemServicesVsh
{
    /// <summary>The pending sequence is one of the module's own (not guest memory).</summary>
    const uint PanelBuiltInSequence = 0xFFFFFFFF;

    /// <summary>The entries a step walks at most before treating the list as ended (a list without its end marker).</summary>
    const int PanelSequenceMaxEntries = 4096;

    /// <summary>The steps a run takes at most (a list of pauses running off the end of memory).</summary>
    const int PanelSequenceMaxSteps = 1 << 16;

    const byte PanelSequencePause = 0x0D, PanelSequenceEnd = 0xFF;

    /// <summary>One step of a pending sequence: counts the pause down, or sends commands up to the next pause (whose
    /// count, plus <paramref name="pauseBias"/>, becomes the counter) or the end (no pending sequence).</summary>
    static void PanelSequenceStep(ref uint sequence, ref int pause, int pauseBias)
    {
        if (sequence == 0)
        {
            return;
        }

        if (pause >= 1)
        {
            pause--;
            return;
        }

        if (sequence == PanelBuiltInSequence)
        {
            sequence = 0;
            return;
        }

        uint entry = sequence;
        for (int walked = 0; walked < PanelSequenceMaxEntries; walked++)
        {
            byte command = GuestMemory.Read<byte>(entry);
            if (command == PanelSequenceEnd)
            {
                break;
            }

            byte length = GuestMemory.Read<byte>(entry + 1);
            if (command == PanelSequencePause)
            {
                sequence = entry + 2;
                pause = length + pauseBias;
                return;
            }

            entry += 2u + length;
        }

        sequence = 0;
    }

    /// <summary>Steps a pending sequence until it ends (a synchronous driver, or one waiting for its sequence).</summary>
    static void PanelSequenceRun(ref uint sequence, ref int pause, int pauseBias)
    {
        for (int step = 0; sequence != 0 && step < PanelSequenceMaxSteps; step++)
        {
            PanelSequenceStep(ref sequence, ref pause, pauseBias);
        }

        sequence = 0;
        pause = 0;
    }
}
