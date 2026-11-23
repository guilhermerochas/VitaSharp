using VitaSharp.Core;
using VitaSharp.Core.Boot;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>
/// The boot steps <see cref="Emulator.LoadTitle"/> reports for the loading screen (<see cref="BootProgress"/>): "game"
/// when the title starts loading and, with a renderer, "shader-cache" before its caches are read, both on the thread
/// that loads it and before the executable is loaded; a title that cannot be loaded has reported "game" and nothing
/// after it (the launcher then shows the failure on the loading screen). Synthetic title, no renderer of a real GPU.
/// </summary>
[Collection("guest")]
public sealed class LoadTitleBootStepTests : IDisposable
{
    private readonly GuestMemoryFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-bootstep-" + Guid.NewGuid().ToString("N"));
    private readonly List<Emulator> _emulators = [];
    private readonly List<string> _events = [];
    private readonly int _thread = Environment.CurrentManagedThreadId;

    public LoadTitleBootStepTests(GuestMemoryFixture fixture)
    {
        _fixture = fixture;
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        Directory.CreateDirectory(_root);
        BootProgress.Reported += OnStep;
    }

    public void Dispose()
    {
        BootProgress.Reported -= OnStep;
        foreach (Emulator emu in _emulators)
        {
            emu.Stop();
            emu.Dispose();
        }

        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Only the steps of this test's thread (another test may load a title meanwhile).</summary>
    private void OnStep(BootStep step)
    {
        if (Environment.CurrentManagedThreadId == _thread)
        {
            _events.Add(BootProgress.Name(step));
        }
    }

    /// <summary>A renderer that records when the title's caches are opened.</summary>
    private sealed class RecordingBackend(List<string> events) : IGpuBackend
    {
        public void Execute(GpuCommand command)
        {
        }

        public void BeginTitle(string titleId) => events.Add("BeginTitle " + titleId);

        public void Dispose()
        {
        }
    }

    private Emulator NewEmulator(string fs)
    {
        var emu = new Emulator(new EmulatorOptions { FsRoot = fs, Memory = _fixture.Memory, EnableJit = false, CpuBackend = "interp" });
        _emulators.Add(emu);
        return emu;
    }

    [Fact]
    public void LoadingATitleReportsTheGameThenTheShaderCacheBeforeItsCachesOpen()
    {
        string fs = Path.Combine(_root, "fs");
        string app = SyntheticTitle.Install(fs, "VSTS00002");
        Emulator emu = NewEmulator(fs);
        emu.Gpu.Backend = new RecordingBackend(_events);
        emu.LoadTitle(app);
        Assert.Equal(["game", "shader-cache", "BeginTitle VSTS00002"], _events);
    }

    [Fact]
    public void WithoutARendererThereIsNoShaderCacheStep()
    {
        string fs = Path.Combine(_root, "fs");
        string app = SyntheticTitle.Install(fs, "VSTS00003");
        NewEmulator(fs).LoadTitle(app);
        Assert.Equal(["game"], _events);
    }

    [Fact]
    public void ATitleThatCannotBeLoadedStopsAfterTheGameStep()
    {
        string fs = Path.Combine(_root, "fs");
        string app = SyntheticTitle.Install(fs, "VSTS00004");
        File.WriteAllBytes(Path.Combine(app, "eboot.bin"), new byte[64]); // no SELF/ELF
        Emulator emu = NewEmulator(fs);
        Assert.ThrowsAny<Exception>(() => emu.LoadTitle(app));
        Assert.Equal(["game"], _events);
    }
}
