using Android.App;
using Android.Runtime;
using VitaSharp.Android.Services;

namespace VitaSharp.Android;

[Application]
public sealed class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        // the game process (":game") initialises from GameActivity with its own log; the UI process here
        if (!IsGameProcess()) AppServices.Initialize(this, gameProcess: false);
        base.OnCreate();
    }

    static bool IsGameProcess()
    {
        try { return File.ReadAllText("/proc/self/cmdline").TrimEnd('\0').EndsWith(":game", StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }
}
