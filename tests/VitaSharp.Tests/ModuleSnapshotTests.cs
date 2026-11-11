using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// Save states restore every HLE module exactly: each module's state (the generated <see cref="ISnapshotState"/> of
/// its [GenerateSnapshot] fields plus its hand-written parts) is saved, loaded into the same module of a second
/// emulator and saved again, and the two must be byte for byte the same; first as the module starts, then with every
/// saved field filled with values of its type (collections with two entries, nested states filled the same way).
/// </summary>
[Collection("guest")]
public sealed class ModuleSnapshotTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    static byte[] Save(ISnapshotState s)
    {
        var w = new SnapshotWriter();
        s.SaveState(w);
        return w.Data.ToArray();
    }

    static void Load(ISnapshotState s, byte[] data, ObjectTable objects)
    {
        var r = new SnapshotReader(data) { Objects = objects };
        s.LoadState(r);
        Assert.True(r.AtEnd, $"{s.GetType().Name}: {r.Remaining} bytes of its state were not read back");
    }

    [Fact]
    public void EveryModuleStateRoundTrips()
    {
        string root = Path.Combine(Path.GetTempPath(), "vitasharp-modsnap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var a = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            using var b = new Emulator(new EmulatorOptions { FsRoot = root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
            var src = a.Hle.Modules.ToList();
            var dst = b.Hle.Modules.ToList();
            Assert.Equal(src.Select(m => m.GetType()), dst.Select(m => m.GetType()));
            var bad = new List<string>();
            var notFilled = new List<string>();
            int states = 0, filled = 0;
            var fill = new Filler(new Random(20261111));
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i] is not ISnapshotState s || dst[i] is not ISnapshotState d)
                {
                    continue;
                }

                string name = src[i].GetType().Name;
                states++;
                byte[] first = Save(s);
                Load(d, first, b.Kernel.Objects);
                byte[] again = Save(d);
                if (!first.AsSpan().SequenceEqual(again))
                {
                    bad.Add($"{name} (as it starts): {first.Length} bytes saved, {again.Length} after a load");
                    continue;
                }

                if (!fill.Fill(src[i], 0))
                {
                    notFilled.Add($"{name}: no generated fields");
                    continue;
                }
                byte[] full;
                try
                {
                    full = Save(s);
                }
                catch (Exception e)
                {
                    notFilled.Add($"{name}: filled state not savable ({e.GetType().Name}: {e.Message})");
                    continue;
                }
                try
                {
                    Load(d, full, b.Kernel.Objects);
                }
                catch (Exception e)
                {
                    notFilled.Add($"{name}: filled state not loadable ({e.GetType().Name}: {e.Message})");
                    continue;
                }
                byte[] full2 = Save(d);
                filled++;
                if (!full.AsSpan().SequenceEqual(full2))
                {
                    int at = 0;
                    while (at < full.Length && at < full2.Length && full[at] == full2[at])
                    {
                        at++;
                    }

                    bad.Add($"{name} (filled): {full.Length} bytes saved, {full2.Length} after a load, first difference at {at}");
                }
            }
            output.WriteLine($"{states} module states, {filled} filled and round-tripped; not filled:\n  " + string.Join("\n  ", notFilled));
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.True(states > 80, $"only {states} modules with a state");
            Assert.True(filled * 10 >= states * 8, $"only {filled} of {states} module states could be filled:\n" + string.Join("\n", notFilled));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException) { }
        }
    }

    /// <summary>Fills the fields a [GenerateSnapshot] type saves (the generator's rules: instance fields declared in
    /// the type, without [SnapshotIgnore] or a [SnapshotIgnoreFields] name; readonly scalars are configuration).</summary>
    sealed class Filler(Random rng)
    {
        const int MaxDepth = 4;

        /// <summary>Fields that only carry a value across a load (the hand-written LoadExtra sets them from other saved
        /// data and AfterSnapshotLoad applies them): they keep their resting value.</summary>
        /// AvPlayer's player clock offset is set the same way from the saved playback time.
        static readonly HashSet<string> LoadOnly = ["AvPlayer+Player.RestoreUs", "AvPlayer+Player.RestoreRunning", "AvPlayer+Player.ClockOffsetUs"];

        static bool Generated(Type t) => t.GetCustomAttribute<GenerateSnapshotAttribute>() != null;

        /// <summary>Fills <paramref name="o"/>'s saved fields; false when its type has no generated snapshot.</summary>
        public bool Fill(object o, int depth)
        {
            bool any = false;
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                if (!Generated(t))
                {
                    continue;
                }

                any = true;
                var ignored = new HashSet<string>(t.GetCustomAttributes<SnapshotIgnoreFieldsAttribute>().SelectMany(x => x.Names));
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    string name = f.Name;
                    PropertyInfo? prop = null;
                    if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    {
                        name = name[1..name.IndexOf('>')];
                        prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (prop?.GetCustomAttribute<SnapshotIgnoreAttribute>() != null)
                        {
                            continue;
                        }
                    }
                    else if (f.GetCustomAttribute<CompilerGeneratedAttribute>() != null)
                    {
                        continue;
                    }

                    if (ignored.Contains(name) || f.GetCustomAttribute<SnapshotIgnoreAttribute>() != null)
                    {
                        continue;
                    }

                    if (LoadOnly.Contains($"{t.DeclaringType?.Name}+{t.Name}.{name}"))
                    {
                        continue;
                    }

                    bool readOnly = prop != null ? prop.SetMethod == null || IsInitOnly(prop) : f.IsInitOnly;
                    var cur = f.GetValue(o);
                    if (readOnly)
                    {
                        // filled in place: collections and nested states
                        if (cur != null && depth < MaxDepth)
                        {
                            FillInPlace(cur, f.FieldType, depth + 1);
                        }

                        continue;
                    }
                    if (Value(f.FieldType, depth + 1, cur, out var v))
                    {
                        f.SetValue(o, v);
                    }
                }
            }
            return any;
        }

        static bool IsInitOnly(PropertyInfo p) =>
            p.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

        void FillInPlace(object cur, Type t, int depth)
        {
            if (cur is IDictionary || IsCollection(t))
            {
                FillCollection(cur, t, depth);
                return;
            }
            if (cur is Array arr)
            {
                var et = t.GetElementType()!;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (Value(et, depth, arr.GetValue(i), out var v))
                    {
                        arr.SetValue(v, i);
                    }
                }

                return;
            }
            if (Generated(cur.GetType()))
            {
                Fill(cur, depth);
            }
        }

        static bool IsCollection(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() is var g &&
            (g == typeof(List<>) || g == typeof(HashSet<>) || g == typeof(Queue<>) || g == typeof(Dictionary<,>) ||
             g == typeof(SortedDictionary<,>) || g == typeof(System.Collections.Concurrent.ConcurrentDictionary<,>));

        void FillCollection(object c, Type t, int depth)
        {
            var g = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments();
            t.GetMethod("Clear")!.Invoke(c, null);
            if (depth >= MaxDepth)
            {
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                if (!Value(args[0], depth + 1, null, out var e))
                {
                    return;
                }

                if (args.Length == 2)
                {
                    if (e == null || !Value(args[1], depth + 1, null, out var val))
                    {
                        return;
                    }

                    var dict = (IDictionary)c;
                    if (!dict.Contains(e))
                    {
                        dict[e] = val;
                    }
                }
                else if (g == typeof(Queue<>))
                {
                    t.GetMethod("Enqueue")!.Invoke(c, [e]);
                }
                else
                {
                    t.GetMethod("Add")!.Invoke(c, [e]);
                }
            }
        }

        /// <summary>A value of <paramref name="t"/> (false: leave the field as it is).</summary>
        public bool Value(Type t, int depth, object? cur, out object? v)
        {
            v = null;
            if (t == typeof(bool))
            {
                v = true;
                return true;
            }
            if (t == typeof(byte))
            {
                v = (byte)rng.Next(1, 256);
                return true;
            }
            if (t == typeof(sbyte))
            {
                v = (sbyte)rng.Next(-128, 128);
                return true;
            }
            if (t == typeof(short))
            {
                v = (short)rng.Next(short.MinValue, short.MaxValue);
                return true;
            }
            if (t == typeof(ushort))
            {
                v = (ushort)rng.Next(1, 65536);
                return true;
            }
            if (t == typeof(char))
            {
                v = (char)rng.Next('a', 'z');
                return true;
            }
            if (t == typeof(int))
            {
                v = rng.Next(1, 1000);
                return true;
            }
            if (t == typeof(uint))
            {
                v = (uint)rng.Next(1, int.MaxValue);
                return true;
            }
            if (t == typeof(long))
            {
                v = rng.NextInt64(1, long.MaxValue);
                return true;
            }
            if (t == typeof(ulong))
            {
                v = (ulong)rng.NextInt64(1, long.MaxValue);
                return true;
            }
            if (t == typeof(float))
            {
                v = (float)rng.NextDouble() * 100;
                return true;
            }
            if (t == typeof(double))
            {
                v = rng.NextDouble() * 100;
                return true;
            }
            if (t == typeof(nint))
            {
                v = (nint)rng.Next(1, 1000);
                return true;
            }
            if (t == typeof(string))
            {
                v = "s" + rng.Next(1000, 9999);
                return true;
            }
            if (t.IsEnum)
            {
                var vals = Enum.GetValues(t);
                if (vals.Length == 0)
                {
                    return false;
                }

                v = vals.GetValue(rng.Next(vals.Length));
                return true;
            }
            if (Nullable.GetUnderlyingType(t) is { } inner)
            {
                return Value(inner, depth, null, out v);
            }

            if (typeof(KernelObject).IsAssignableFrom(t))
            {
                return false; // saved by UID: null stays null
            }

            if (depth > MaxDepth)
            {
                return false;
            }

            if (t.IsArray && t.GetArrayRank() == 1)
            {
                var et = t.GetElementType()!;
                var arr = Array.CreateInstance(et, 2);
                for (int i = 0; i < 2; i++)
                {
                    if (!Value(et, depth + 1, null, out var e))
                    {
                        return false;
                    }
                    arr.SetValue(e, i);
                }
                v = arr;
                return true;
            }
            if (t.IsGenericType && t.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
            {
                var parts = new List<object?>();
                foreach (var p in t.GetGenericArguments())
                {
                    if (!Value(p, depth + 1, null, out var e))
                    {
                        return false;
                    }
                    parts.Add(e);
                }
                v = Activator.CreateInstance(t, [.. parts]);
                return true;
            }
            if (IsCollection(t))
            {
                var c = cur ?? Activator.CreateInstance(t)!;
                FillCollection(c, t, depth);
                v = c;
                return true;
            }
            if (t.IsClass && typeof(ISnapshotState).IsAssignableFrom(t))
            {
                if (t.IsAbstract)
                {
                    return false;
                }

                object? o = cur;
                if (o == null)
                {
                    try
                    {
                        o = Activator.CreateInstance(t, nonPublic: true);
                    }
                    catch (MissingMethodException)
                    {
                        return false;
                    }
                    catch (TargetInvocationException)
                    {
                        return false;
                    }
                }
                if (o != null && Generated(t))
                {
                    Fill(o, depth);
                }

                v = o;
                return o != null;
            }
            return false; // blittable structs and anything else keep their value
        }
    }
}
