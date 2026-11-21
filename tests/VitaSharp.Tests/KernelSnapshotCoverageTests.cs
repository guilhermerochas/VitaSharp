using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// Every kind of kernel object the object table can hold has a save state record: a type missing from KernelSnapshot
/// makes every save refused while one such object lives (the SceUlt fallback's objects were missing, and a title
/// using it could not be saved). The sweep covers every concrete KernelObject of the core assembly, nested and private
/// ones included; only the types marked [NeverInObjectTable] (never registered) are left out.
/// </summary>
public sealed class KernelSnapshotCoverageTests
{
    [Fact]
    public void EveryKernelObjectTypeHasASaveStateRecord()
    {
        var missing = new List<string>();
        int covered = 0;
        foreach (Type type in typeof(KernelObject).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(KernelObject).IsAssignableFrom(type)
                || type.IsDefined(typeof(NeverInObjectTableAttribute), inherit: false))
            {
                continue;
            }

            if (KernelSnapshot.KindOf(type) == null)
            {
                missing.Add(type.FullName!);
            }
            else
            {
                covered++;
            }
        }

        Assert.True(missing.Count == 0, "kernel objects without a save state record: " + string.Join(", ", missing));
        Assert.True(covered >= 20, $"only {covered} kernel object types found");
    }
}
