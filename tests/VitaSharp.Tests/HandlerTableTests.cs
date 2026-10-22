using System.Reflection;
using VitaSharp.Core.Cpu;

namespace VitaSharp.Tests;

/// <summary>The generated CPU handler table resolves every Op exactly like the reflection lookup by name it replaced.</summary>
public sealed unsafe class HandlerTableTests
{
    [Fact]
    public void EveryOpResolvesToItsNamedHandler()
    {
        var asm = typeof(HandlerTable).Assembly;
        var sig = new[] { typeof(CpuState*), typeof(uint), typeof(uint) };
        for (int i = 1; i < (int)Op.Count; i++)
        {
            var name = ((Op)i).ToString();
            int us = name.IndexOf('_');
            var mi = asm.GetType($"VitaSharp.Core.Cpu.{name[..us]}")!
                .GetMethod(name[(us + 1)..], BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, sig);
            Assert.NotNull(mi);
            Assert.Equal(mi, HandlerTable.Methods[i]);
            Assert.True(HandlerTable.Pointers[i] != null, name);
            Assert.Equal((nint)mi!.MethodHandle.GetFunctionPointer(), (nint)HandlerTable.Pointers[i]);
        }
    }
}
