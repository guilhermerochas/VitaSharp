using System.Runtime.InteropServices;
using VitaSharp.Core.Host;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// The media keys on macOS. They never reach the GLFW window as key events: AppKit delivers them as NSSystemDefined
/// events (subtype 8, the key and its state in data1). A local event monitor of the application
/// (<c>+[NSEvent addLocalMonitorForEventsMatchingMask:handler:]</c>, installed once on the main thread through the
/// Objective-C runtime) sees them and hands each key to <see cref="Target"/> (<see cref="HidUsages.ConsumerFromMacSystemEvent"/>);
/// the event goes on to the application unchanged.
/// </summary>
internal static unsafe partial class MacMediaKeys
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    /// <summary>NSEventMaskSystemDefined (1 &lt;&lt; NSEventTypeSystemDefined, 14).</summary>
    private const ulong SystemDefinedMask = 1UL << 14;

    /// <summary>BLOCK_IS_GLOBAL: a block with no captured state, never copied or freed.</summary>
    private const int BlockIsGlobal = 1 << 28;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public BlockDescriptor* Descriptor;
    }

    private static BlockLiteral* s_block;
    private static nint s_monitor;
    private static nint s_selSubtype, s_selData1;

    /// <summary>Where a media key goes (usage, down); null = nowhere.</summary>
    public static Action<ushort, bool>? Target { get; set; }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendMonitor(nint receiver, nint selector, ulong mask, nint handler);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial short SendShort(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendInteger(nint receiver, nint selector);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen(string path, int mode);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlsym(nint handle, string symbol);

    /// <summary>Installs the monitor (once, on the main thread, after the window exists); does nothing elsewhere.</summary>
    public static void Install()
    {
        if (!OperatingSystem.IsMacOS() || s_monitor != 0)
        {
            return;
        }

        try
        {
            nint libSystem = dlopen(LibSystem, 1);
            nint globalBlock = libSystem == 0 ? 0 : dlsym(libSystem, "_NSConcreteGlobalBlock");
            nint eventClass = objc_getClass("NSEvent");
            if (globalBlock == 0 || eventClass == 0)
            {
                return;
            }

            s_selSubtype = sel_registerName("subtype");
            s_selData1 = sel_registerName("data1");
            var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            descriptor->Size = (nuint)sizeof(BlockLiteral);
            s_block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
            s_block->Isa = globalBlock;
            s_block->Flags = BlockIsGlobal;
            s_block->Invoke = (nint)(delegate* unmanaged<BlockLiteral*, nint, nint>)&OnEvent;
            s_block->Descriptor = descriptor;
            s_monitor = SendMonitor(eventClass, sel_registerName("addLocalMonitorForEventsMatchingMask:handler:"), SystemDefinedMask, (nint)s_block);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Core.Util.Log.Warn($"macOS media keys: no event monitor ({e.Message})");
        }
    }

    /// <summary>The monitor's handler: the event's media key, if any, to <see cref="Target"/>; the event is returned
    /// so the application still gets it.</summary>
    [UnmanagedCallersOnly]
    private static nint OnEvent(BlockLiteral* block, nint @event)
    {
        if (@event == 0 || Target is not { } target)
        {
            return @event;
        }

        try
        {
            short subtype = SendShort(@event, s_selSubtype);
            long data1 = subtype == 8 ? SendInteger(@event, s_selData1) : 0;
            if (HidUsages.ConsumerFromMacSystemEvent(subtype, data1, out bool down) is var usage and not 0)
            {
                target(usage, down);
            }
        }
        catch (Exception e)
        {
            // nothing may leave a native callback
            Core.Util.Log.WarnOnce("mac-media-keys", $"macOS media keys: {e.Message}");
        }

        return @event;
    }
}
