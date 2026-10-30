using System.Diagnostics;
using VitaSharp.Core;
using VitaSharp.Core.Input;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// <see cref="EmulationPause"/>: the guest clock stands still while paused and goes on without a jump, waits on it
/// block until the resume, guest threads running guest code park and continue afterwards (except the one finishing a
/// GPU scene), and the pause hotkey's bindings (keyboard default, controller combinations). The pause is global, so
/// these tests run in the "guest" collection (sequentially with the other guest-state tests) and keep it short.
/// </summary>
[Collection("guest")]
public sealed class EmulationPauseTests(GuestMemoryFixture fx)
{
    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    [Fact]
    public void GuestClockStopsWhilePausedAndResumesWithoutAJump()
    {
        Assert.False(EmulationPause.IsPaused);
        try
        {
            Assert.True(EmulationPause.Pause("test"));
            Assert.False(EmulationPause.Pause("test")); // already paused
            Assert.True(GuestClock.Frozen);
            long atPause = GuestClock.Ticks;
            long host0 = EmulationPause.ActiveHostTicks;
            Thread.Sleep(150);
            Assert.Equal(atPause, GuestClock.Ticks);
            // host time spent paused is left out of the "active" host clock (audio gap statistics)
            Assert.True(Ms(EmulationPause.ActiveHostTicks - host0) < 30);

            Assert.True(EmulationPause.Resume("test"));
            Assert.False(GuestClock.Frozen);
            long afterResume = GuestClock.Ticks;
            Assert.True(afterResume >= atPause);
            Assert.True(Ms(afterResume - atPause) < 30, $"the clock jumped {Ms(afterResume - atPause):F1} ms on resume");
            Thread.Sleep(30);
            Assert.True(GuestClock.Ticks > afterResume); // running again
        }
        finally { EmulationPause.Resume("test cleanup"); }
    }

    [Fact]
    public async Task ClockWaitsBlockUntilTheResume()
    {
        try
        {
            EmulationPause.Pause("test");
            long deadline = GuestClock.Ticks + GuestClock.FromSeconds(0.001);
            var wait = Task.Run(() => GuestClock.WaitUntil(deadline));
            Assert.NotSame(wait, await Task.WhenAny(wait, Task.Delay(200))); // a 1 ms guest wait must not end while paused
            EmulationPause.Resume("test");
            Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(2000))); // and ends after the resume
        }
        finally { EmulationPause.Resume("test cleanup"); }
    }

    [Fact]
    public void ToggleAndChangedEvent()
    {
        var seen = new List<bool>();
        void OnChanged(bool p) { lock (seen) seen.Add(p); }
        EmulationPause.Changed += OnChanged;
        try
        {
            Assert.True(EmulationPause.Toggle("test"));
            Assert.False(EmulationPause.Toggle("test"));
            Assert.Equal(new[] { true, false }, seen);
        }
        finally
        {
            EmulationPause.Changed -= OnChanged;
            EmulationPause.Resume("test cleanup");
        }
    }

    // ARM: loop: ldr r1,[r0]; add r1,r1,#1; str r1,[r0]; ldr r2,[r0,#4]; cmp r2,#0; beq loop; bx lr
    static readonly uint[] CountingLoop = [0xE5901000, 0xE2811001, 0xE5801000, 0xE5902004, 0xE3520000, 0x0AFFFFF9, 0xE12FFF1E];

    [Fact]
    public void GuestThreadsParkWhilePausedAndContinueAfterwards()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool jitEnabled = VitaSharp.Core.Cpu.Jit.Enabled;
        string jitMode = VitaSharp.Core.Cpu.Jit.Mode;
        var emu = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
        Exception? fatal = null;
        emu.Fatal += (_, e) => fatal ??= e;
        uint code = fx.Memory.Alloc(0x100, "pause test code"), data = fx.Memory.Alloc(0x10, "pause test counter");
        for (int i = 0; i < CountingLoop.Length; i++) GuestMemory.W32(code + (uint)i * 4, CountingLoop[i]);
        GuestMemory.W32(data, 0);
        GuestMemory.W32(data + 4, 0);
        emu.Code.Invalidate(code, 0x100);
        uint Count() => GuestMemory.R32(data);
        bool WaitFor(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (!cond()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(5); }
            return true;
        }

        var t = emu.Kernel.CreateThread("pause-test", 0, 0x10000100, 0x10000, 0, 0x70000);
        t.HostBody = th => th.CallGuest(code, data);
        try
        {
            t.Start(0, 0);
            Assert.True(WaitFor(() => Count() > 1000, 10_000), "the guest loop did not run");

            EmulationPause.Pause("test");
            Thread.Sleep(100); // the thread leaves its guest code at the next block boundary
            uint parked = Count();
            Thread.Sleep(250);
            Assert.Equal(parked, Count());

            // the thread that has a GPU scene open runs on until it closes it
            emu.OpenSceneThread = t;
            Assert.True(WaitFor(() => Count() > parked + 1000, 5000), "the thread finishing a scene stayed parked");
            emu.OpenSceneThread = null;
            Thread.Sleep(100);
            uint parkedAgain = Count();
            Thread.Sleep(200);
            Assert.Equal(parkedAgain, Count());

            EmulationPause.Resume("test");
            Assert.True(WaitFor(() => Count() > parkedAgain + 1000, 5000), "the thread did not continue after the resume");
        }
        finally
        {
            EmulationPause.Resume("test cleanup");
            GuestMemory.W32(data + 4, 1); // ends the loop
            Assert.True(t.WaitForExit(10_000), "the test thread did not finish");
            emu.Kernel.DeleteThread(t);
            emu.Dispose();
            VitaSharp.Core.Cpu.Jit.Enabled = jitEnabled;
            VitaSharp.Core.Cpu.Jit.Select(jitMode);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Assert.Null(fatal);
    }

    [Fact]
    public void PauseHotkeyDefaultsAndCombinations()
    {
        // keyboard: P pauses; Space stays a Cross key (the usual pause key of emulators is taken here)
        var kb = DefaultMappings.Keyboard();
        Assert.Equal(new[] { InputBinding.OfKey("P") }, kb.Get(VitaControl.PauseEmulation));
        Assert.Contains(InputBinding.OfKey("Space"), kb.Get(VitaControl.Cross));
        foreach (var c in VitaControls.All.Where(c => c != VitaControl.PauseEmulation))
            Assert.DoesNotContain(InputBinding.OfKey("P"), kb.Get(c));
        var cp = new CompiledProfile(kb);
        Assert.True(InputMapper.HotkeyHeld(cp, VitaControl.PauseEmulation, null, new HashSet<string> { "P" }));
        Assert.Equal(VitaSharp.Core.Host.VitaButtons.None, InputMapper.Map(cp, null, new HashSet<string> { "P" }).State.Buttons);

        // controllers: off by default; a combination needs every button
        var pad = DefaultMappings.Gamepad("g", "n", ControllerKind.PlayStation);
        Assert.Empty(pad.Get(VitaControl.PauseEmulation));
        Assert.True(InputCombo.TryParse("pad:back&pad:start", out var combo));
        Assert.Equal("pad:back&pad:start", combo.ToString());
        Assert.False(InputCombo.TryParse("pad:back", out _));
        Assert.False(InputBinding.TryParse("pad:back&pad:start", out _)); // single-binding code skips it
        pad.AddCombo(VitaControl.PauseEmulation, combo);
        var json = InputProfileStore.Serialize(new InputConfig { Controllers = [pad] });
        var back = InputProfileStore.Deserialize(json)!.Controllers[0];
        Assert.Equal("pad:back&pad:start", back.GetCombos(VitaControl.PauseEmulation).Single().ToString());
        var pcp = new CompiledProfile(back);
        var snap = new PadSnapshot { IsGamepad = true };
        snap.Buttons[(int)PadButton.Back] = true;
        Assert.False(InputMapper.HotkeyHeld(pcp, VitaControl.PauseEmulation, snap, null));
        snap.Buttons[(int)PadButton.Start] = true;
        Assert.True(InputMapper.HotkeyHeld(pcp, VitaControl.PauseEmulation, snap, null));

        // recording a combination: the buttons held beyond the baseline
        var held = InputCapture.Held(new PadSnapshot { IsGamepad = true }, snap);
        Assert.Equal(new[] { InputBinding.Of(PadButton.Back), InputBinding.Of(PadButton.Start) }, held);
    }

    [Fact]
    public void OlderKeyboardProfilesGetThePauseKeyWhenFree()
    {
        var old = DefaultMappings.Keyboard();
        old.Set(VitaControl.PauseEmulation, null);
        var store = new InputProfileStore();
        store.Put(old);
        store.Config.Version = 2;
        Assert.Equal(new[] { InputBinding.OfKey("P") }, store.KeyboardProfile().Get(VitaControl.PauseEmulation));
        // P already used by the player for something else: the hotkey stays unbound
        old.Set(VitaControl.Triangle, InputBinding.OfKey("P"));
        store.Put(old);
        store.Config.Version = 2;
        Assert.Empty(store.KeyboardProfile().Get(VitaControl.PauseEmulation));
        // saved at the current version without a pause key: the player's choice is kept
        store.Config.Version = InputConfig.CurrentVersion;
        old.Set(VitaControl.Triangle, InputBinding.OfKey("V"));
        store.Put(old);
        Assert.Empty(store.KeyboardProfile().Get(VitaControl.PauseEmulation));
    }
}
