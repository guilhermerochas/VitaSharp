using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Xunit.Abstractions;

namespace VitaSharp.Tests;

/// <summary>
/// SceLibJson HLE (Json.cs) against the firmware's libSceJson.suprx. Every scenario is a driver that calls the library
/// through guest calls on a guest thread, exactly as a game would; it runs twice in one emulator, once against the
/// firmware module's exports and once against HLE stubs, with a bump allocator behind the game's MemAllocator that
/// starts from the same address each time. Both runs must make the same allocator calls, return the same results and
/// leave the same bytes in the allocator's arena and in the "game" memory (module data pointers are compared by
/// offset; the lwmutex work area and the tree nodes' uninitialised padding are masked).
/// Without a firmware module only the HLE run happens (with a few fixed expectations).
/// </summary>
[Collection("guest")]
public sealed unsafe class JsonHleTests(GuestMemoryFixture fx, ITestOutputHelper output)
{
    [Fact]
    public void Hle_ParsesSerializesAndReportsErrors()
    {
        using var h = new JsonHarness(fx, null);
        var log = h.Run(useLle: false, d =>
        {
            d.Init();
            uint v = d.NewValue();
            Assert.Equal(0, (int)d.Parse(v, "{\"b\":[1,-2,3.5,true,null],\"a\":\"x\\u00e9\"}"));
            Assert.Equal(7u, d.Call(JsonDriver.GetTypeFn, v));
            Assert.Equal("{\"a\":\"x\u00c3\u00a9\",\"b\":[1,-2,3.500000,true,null]}", Latin1(d.Serialize(v)));
            Assert.Equal(Json.ERROR_PARSE_INVALID_CHAR, (int)d.Parse(v, "[1,"));
            Assert.Equal(Json.ERROR_PARSE_INVALID_CHAR, (int)d.Parse(v, "{\"a\" 1}"));
            d.DestroyValue(v);
            Assert.Equal(Json.ERROR_MULTIPLEINIT, (int)d.Call(JsonDriver.Initialize, d.Initializer, d.InitParam));
            Assert.Equal(0u, d.Call(JsonDriver.TerminateFn, d.Initializer));
            Assert.Equal(Json.ERROR_NOINIT, (int)d.Call(JsonDriver.TerminateFn, d.Initializer));
        });
        Assert.Equal(0, log.Allocator.Count(e => e.StartsWith("A")) - log.Allocator.Count(e => e.StartsWith("F")));
    }

    [Fact]
    public void Hle_AllocationFailureAbortsTheParseWithNoMem()
    {
        using var h = new JsonHarness(fx, null);
        h.Run(useLle: false, d =>
        {
            d.Init();
            uint v = d.NewValue();
            h.FailAfter = 3;
            Assert.Equal(Json.ERROR_NOMEM, (int)d.Parse(v, "{\"key\":[1,2,3,\"long string value\"]}"));
            h.FailAfter = -1;
            d.DestroyValue(v);
            d.Call(JsonDriver.TerminateFn, d.Initializer);
        });
    }

    // ------------------------------------------------------------------ differential (firmware module)

    public static IEnumerable<object[]> Scenarios => JsonScenarios.All.Keys.Select(k => new object[] { k });

    [JsonFirmwareTheory]
    [MemberData(nameof(Scenarios))]
    public void MatchesFirmwareModule(string scenario)
    {
        using var h = new JsonHarness(fx, JsonFirmware.ModulePath);
        var body = JsonScenarios.All[scenario];
        var lle = h.Run(useLle: true, body);
        var hle = h.Run(useLle: false, body);
        output.WriteLine($"{scenario}: {lle.Allocator.Count} allocator calls, {lle.Results.Count} results, {lle.ArenaUsed} arena bytes");
        Compare("allocator calls", lle.Allocator, hle.Allocator);
        Compare("results", lle.Results, hle.Results);
        Compare("callbacks", lle.Callbacks, hle.Callbacks);
        Assert.Equal(lle.ArenaUsed, hle.ArenaUsed);
        CompareMemory("arena", lle.Arena, hle.Arena, lle.Mask);
        CompareMemory("game memory", lle.Game, hle.Game, null);
    }

    void Compare(string what, List<string> lle, List<string> hle)
    {
        int n = Math.Min(lle.Count, hle.Count);
        for (int i = 0; i < n; i++)
        {
            if (lle[i] == hle[i]) continue;
            int from = Math.Max(0, i - 6), to = Math.Min(n - 1, i + 2);
            var ctx = string.Join("\n", Enumerable.Range(from, to - from + 1).Select(k => $"  [{k}] lle={lle[k]}  hle={hle[k]}"));
            Assert.Fail($"{what} differ at {i}:\n{ctx}");
        }
        if (lle.Count != hle.Count)
            Assert.Fail($"{what}: {lle.Count} (firmware) vs {hle.Count} (HLE); first extra: {(lle.Count > n ? lle[n] : hle[n])}");
    }

    static void CompareMemory(string what, uint[] lle, uint[] hle, bool[]? mask)
    {
        Assert.Equal(lle.Length, hle.Length);
        for (int i = 0; i < lle.Length; i++)
        {
            if (mask != null && mask[i]) continue;
            if (lle[i] != hle[i]) Assert.Fail($"{what} differs at +0x{i * 4:X}: firmware {lle[i]:X8}, HLE {hle[i]:X8}");
        }
    }

    static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);
}

/// <summary>The scenarios run against both implementations (each is a driver body).</summary>
static class JsonScenarios
{
    const string Doc = "{ \"name\" : \"Devious\\tDungeon\", \"level\": 12, \"neg\": -42, \"big\": 18446744073709551615, " +
                       "\"over\": 18446744073709551616, \"min\": -9223372036854775808, \"pi\": 3.14159, \"e\": 1e3, \"e2\": 2.5E-2, " +
                       "\"z\": -0, \"dot\": .5, \"t\": true, \"f\": false, \"n\": null, \"esc\": \"q\\\"b\\\\s\\/\\b\\f\\n\\r\\t\\u0041\\u00e9\\u20ac\\ud83d\\ude00\", " +
                       "\"ctl\": \"\\u0001\\u001f\\u007f\", \"arr\": [ [], {}, [1,[2,[3]]], {\"x\":{\"y\":{}}} ], \"empty\": \"\", " +
                       "\"dup\": 1, \"dup\": 2, \"\u00e9\": \"utf8\" }";

    public static readonly Dictionary<string, Action<JsonDriver>> All = new()
    {
        ["init-terminate"] = d =>
        {
            uint v = d.NewValue();
            uint s = d.NewString("x");
            d.Rec(d.Call(JsonDriver.Serialize1, v, s));                 // not initialized
            d.Rec(d.Parse(v, "1"));
            d.Rec(d.Call(JsonDriver.SetAllocInfoCb, d.Initializer, 0, 0));
            d.Init();
            d.Rec(d.Call(JsonDriver.SetAllocInfoCb, d.Initializer, 0, 0));
            d.Rec(d.Call(JsonDriver.Initialize, d.Initializer, d.InitParam));
            d.Rec(d.Call(JsonDriver.TerminateFn, d.Initializer));
            d.Rec(d.Call(JsonDriver.TerminateFn, d.Initializer));
            d.Rec(d.Call(JsonDriver.Initialize, d.Initializer, d.InitParam));
            d.Call(JsonDriver.InitializerDtor, d.Initializer);
            d.Rec(d.Call(JsonDriver.Initialize, d.Initializer, d.InitParam));
            d.Rec(d.Call(JsonDriver.TerminateFn, d.Initializer));
        },
        ["parse-serialize"] = d =>
        {
            d.Init();
            uint v = d.NewValue();
            d.Rec(d.Parse(v, Doc));
            d.RecText(d.Serialize(v));
            d.Rec(d.Call(JsonDriver.Count, v));
            foreach (var key in new[] { "name", "level", "neg", "big", "over", "min", "pi", "e", "e2", "z", "dot", "t", "f", "n", "esc", "nope" })
            {
                uint e = d.Call(JsonDriver.IndexCStr, v, d.CStr(key));
                d.Rec(d.Call(JsonDriver.GetTypeFn, e));
                uint str = d.NewString("");
                d.Call(JsonDriver.ToStringFn, e, str);
                d.RecText(d.StringBytes(str));
                d.Rec(d.Call(JsonDriver.ToBool, e));
                d.RecMem(d.Call(JsonDriver.GetInteger, e), 8);
                d.RecMem(d.Call(JsonDriver.GetUInteger, e), 8);
                d.RecMem(d.Call(JsonDriver.GetReal, e), 8);
                d.RecMem(d.Call(JsonDriver.GetBoolean, e), 1);
                d.Rec(d.Call(JsonDriver.GetString, e));
                d.Rec(d.Call(JsonDriver.GetRoot, e));
                d.DestroyString(str);
            }
            uint arr = d.Call(JsonDriver.IndexCStr, v, d.CStr("arr"));
            for (uint i = 0; i < 5; i++) d.Rec(d.Call(JsonDriver.GetTypeFn, d.Call(JsonDriver.GetValueIdx, arr, i)));
            d.Rec(d.Call(JsonDriver.GetValueIdx, v, 3));
            d.Rec(d.Call(JsonDriver.GetValueIdx, v, 300));
            d.RecText(d.Serialize(arr));
            d.DestroyValue(v);
            d.Terminate();
        },
        ["parse-errors"] = d =>
        {
            d.Init();
            uint v = d.NewValue();
            foreach (var text in new[]
                     {
                         "", " ", "{", "}", "[", "]", "[1,]", "[,1]", "{\"a\" 1}", "{\"a\":}", "{a:1}", "{\"a\":1,}", "tru", "nul", "falsy",
                         "\"abc", "\"\\x\"", "\"\\u12\"", "\"\\ud800\"", "\"\\ud800\\u0041\"", "\"\\udc00\"", "\"a\u0001b\"", "01", "-", "--1", "1.2.3",
                         "1e", "1e+", "+1", "[1 2]", "{\"a\":1 \"b\":2}", "[true false]", "  42  ", "42 garbage", "[[[[[[]]]]]]", "{\"\":0}",
                     })
            {
                d.Rec(d.Parse(v, text));
                d.Rec(d.Call(JsonDriver.GetTypeFn, v));
                d.RecText(d.Serialize(v));
            }
            d.Rec(d.ParseN(v, "[1,2]xyz", 5));
            d.Rec(d.ParseN(v, "[1,2]", 3));
            d.Rec(d.ParseN(v, "123", 0));
            d.DestroyValue(v);
            d.Terminate();
        },
        ["parse-callback"] = d =>
        {
            d.Init();
            uint v = d.NewValue();
            d.Rec(d.ParseCb(v, "{\"cb\":[1,2,{\"k\":\"v\"}]}", -1));
            d.RecText(d.Serialize(v));
            d.Rec(d.ParseCb(v, "[1,2,3,4,5]", 4));         // the reader fails after 4 characters
            d.RecText(d.Serialize(v));
            d.Rec(d.ParseCb(v, "\"text\"", -1));
            d.RecText(d.Serialize(v));
            d.DestroyValue(v);
            d.Terminate();
        },
        ["parse-file"] = d =>
        {
            d.Init(bufSize: 16);
            uint v = d.NewValue();
            d.Rec(d.Call(JsonDriver.ParseFile, v, d.CStr("ux0:data/json/test.json")));
            d.RecText(d.Serialize(v));
            d.Rec(d.Call(JsonDriver.ParseFile, v, d.CStr("ux0:data/json/missing.json")));
            d.Rec(d.Call(JsonDriver.ParseFile, v, d.CStr("ux0:data/json/bad.json")));
            d.DestroyValue(v);
            d.Terminate();
            d.Init(bufSize: 0);
            uint w = d.NewValue();
            d.Rec(d.Call(JsonDriver.ParseFile, w, d.CStr("ux0:data/json/test.json")));
            d.DestroyValue(w);
            d.Terminate();
        },
        ["value-api"] = d =>
        {
            d.Init();
            uint a = d.Game(0x18), b = d.Game(0x18), c = d.Game(0x18);
            d.Call(JsonDriver.ValueCtorBool, a, 1);
            d.RecValue(a);
            d.Call(JsonDriver.ValueCtorInt, b, 0, unchecked((uint)-5), 0xFFFFFFFF);
            d.RecValue(b);
            d.CallD(JsonDriver.ValueCtorReal, 2.75, c);
            d.RecValue(c);
            d.Call(JsonDriver.ValueDtor, a);
            d.Call(JsonDriver.ValueCtorUInt, a, 0, 0x12345678, 0x9ABCDEF0);
            d.RecValue(a);
            for (uint t = 0; t <= 8; t++)
            {
                d.Call(JsonDriver.ValueDtor, a);
                d.Call(JsonDriver.ValueCtorType, a, t);
                d.RecValue(a);
                d.Rec(d.Call(JsonDriver.ToBool, a));
                d.Rec(d.Call(JsonDriver.Count, a));
            }
            uint s = d.NewString("hello");
            d.Call(JsonDriver.ValueDtor, a);
            d.Call(JsonDriver.ValueCtorString, a, s);
            d.RecValue(a);
            d.RecText(d.Serialize(a));
            uint nullStr = d.Game(4);                                     // a String without rep
            d.Call(JsonDriver.ValueSetString, a, nullStr);
            d.RecValue(a);
            d.Call(JsonDriver.ValueSetBool, a, 0);
            d.Call(JsonDriver.ValueSetInt, a, 0, 7, 0);
            d.Call(JsonDriver.ValueSetUInt, a, 0, 8, 0);
            d.CallD(JsonDriver.ValueSetReal, 1e300, a);
            d.RecValue(a);
            d.RecText(d.Serialize(a));
            d.CallD(JsonDriver.ValueSetReal, -1.5e-7, a);
            d.RecText(d.Serialize(a));
            d.Call(JsonDriver.ValueSetType, a, 6);
            uint arr = d.Call(JsonDriver.ReferArray, a);
            d.Call(JsonDriver.PushBack, arr, b);
            d.Call(JsonDriver.PushBack, arr, c);
            d.Call(JsonDriver.ValueCopyCtor, d.Game(0x18), a);
            uint copy = d.Game(0x18);
            d.Call(JsonDriver.ValueCtor, copy);
            d.Call(JsonDriver.ValueAssign, copy, a);
            d.RecText(d.Serialize(copy));
            d.Call(JsonDriver.ValueSwap, copy, b);
            d.RecValue(copy);
            d.RecValue(b);
            d.Call(JsonDriver.ValueSetValue, c, b);
            d.RecValue(c);
            d.Call(JsonDriver.ValueSetArray, c, d.Call(JsonDriver.GetArray, b));
            d.Call(JsonDriver.ValueClear, b);
            d.RecValue(b);
            foreach (var f in new[] { JsonDriver.ReferString, JsonDriver.ReferArray, JsonDriver.ReferObject, JsonDriver.ReferInteger,
                         JsonDriver.ReferUInteger, JsonDriver.ReferReal, JsonDriver.ReferBoolean })
            {
                d.Rec(d.Call(f, a));
                d.Rec(d.Call(f, copy));
                d.Rec(d.Call(f, c));
            }
            d.Rec(d.Call(JsonDriver.ReferValueIdx, c, 1));
            d.Rec(d.Call(JsonDriver.ReferValueIdx, c, 5));
            uint o = d.Game(0x18);
            d.Call(JsonDriver.ValueCtorType, o, 7);
            uint obj = d.Call(JsonDriver.ReferObject, o);
            uint k = d.NewString("key");
            d.Call(JsonDriver.ValueAssign, d.Call(JsonDriver.ObjIndex, obj, k), c);
            d.Rec(d.Call(JsonDriver.ReferValueKey, o, k));
            d.Rec(d.Call(JsonDriver.ReferValueKey, o, d.NewString("other")));
            d.Rec(d.Call(JsonDriver.GetValueKey, o, k));
            d.Rec(d.Call(JsonDriver.IndexString, o, d.NewString("other")));
            d.Rec(d.Call(JsonDriver.IndexIdx, o, 0));
            d.RecText(d.Serialize(o));
            d.Call(JsonDriver.ValueSetObject, a, obj);
            d.RecText(d.Serialize(a));
            d.Call(JsonDriver.ValueCtorObject, d.Game(0x18), obj);
            d.Call(JsonDriver.ValueCtorArray, d.Game(0x18), arr);
            foreach (uint v in new[] { a, b, c, copy, o }) d.Call(JsonDriver.ValueDtor, v);
            d.DestroyString(s);
            d.DestroyString(k);
            d.Terminate();
        },
        ["null-access"] = d =>
        {
            d.Init();
            uint v = d.NewValue();
            uint sub = d.NewValue();
            d.Call(JsonDriver.ValueSetInt, sub, 0, 99, 0);
            d.Rec(d.Call(JsonDriver.SetNullCb, v, d.NullCallback, 0x55));     // no root info yet
            d.Rec(d.Parse(v, "{\"a\":null,\"b\":[null]}"));
            d.Rec(d.Call(JsonDriver.SetNullCb, v, d.NullCallback, 0x55));
            d.NullAnswer = sub;
            uint a = d.Call(JsonDriver.IndexCStr, v, d.CStr("a"));
            d.RecMem(d.Call(JsonDriver.GetInteger, a), 8);
            d.Rec(d.Call(JsonDriver.GetString, a));
            uint missing = d.Call(JsonDriver.IndexCStr, v, d.CStr("zz"));
            d.Rec(missing);
            d.RecMem(d.Call(JsonDriver.GetReal, missing), 8);
            d.Rec(d.Call(JsonDriver.SetNullCb, a, d.NullCallback, 0x55));     // not a root
            d.RecText(d.Serialize(v));                                          // clears the callback
            d.Rec(d.Call(JsonDriver.GetString, a));
            d.DestroyValue(sub);
            d.DestroyValue(v);
            d.Terminate();
        },
        ["string-api"] = d =>
        {
            d.Init();
            uint s = d.NewString("hello world, hello json");
            uint t = d.Game(4);
            d.Call(JsonDriver.StringCopyCtor, t, s);
            uint u = d.Game(4);
            d.Call(JsonDriver.StringCtor, u);
            d.Call(JsonDriver.StringAppendStr, u, s);
            d.Call(JsonDriver.StringAppendC, u, d.CStr("!?"));
            d.Call(JsonDriver.StringAppendCN, u, d.CStr("abcdef"), 3);
            d.Call(JsonDriver.StringPlusC, u, d.CStr("++"));
            d.Call(JsonDriver.StringPlusCh, u, (uint)'#');
            d.Call(JsonDriver.StringPlusCh, u, 0);
            d.RecText(d.StringBytes(u));
            d.Rec(d.Call(JsonDriver.StringSize, u));
            d.Rec(d.Call(JsonDriver.StringLength, u));
            d.Rec(d.Call(JsonDriver.StringEmpty, u));
            uint hello = d.NewString("hello");
            foreach (uint pos in new uint[] { 0, 1, 13, 22, 23, 100, uint.MaxValue })
            {
                d.Rec(d.Call(JsonDriver.FindStr, s, hello, pos));
                d.Rec(d.Call(JsonDriver.FindC, s, d.CStr("o"), pos));
                d.Rec(d.Call(JsonDriver.FindCN, s, d.CStr("json!"), pos, 4));
                d.Rec(d.Call(JsonDriver.FindCN, s, d.CStr(""), pos, 0));
                d.Rec(d.Call(JsonDriver.FindCh, s, (uint)'l', pos));
                d.Rec(d.Call(JsonDriver.FindCh, s, 0, pos));
                d.Rec(d.Call(JsonDriver.RFindStr, s, hello, pos));
                d.Rec(d.Call(JsonDriver.RFindC, s, d.CStr("l"), pos));
                d.Rec(d.Call(JsonDriver.RFindCN, s, d.CStr("worldX"), pos, 5));
                d.Rec(d.Call(JsonDriver.RFindCN, s, d.CStr(""), pos, 0));
                d.Rec(d.Call(JsonDriver.RFindCh, s, (uint)'h', pos));
            }
            foreach (var (pos, len) in new (uint, uint)[] { (0, 5), (6, uint.MaxValue), (20, 10), (23, 1), (2, 0) })
            {
                uint sub = d.Game(4);
                d.Call(JsonDriver.Substr, sub, s, pos, len);
                d.RecText(d.StringBytes(sub));
                d.DestroyString(sub);
            }
            uint none = d.Game(4);
            uint sub2 = d.Game(4);
            d.Call(JsonDriver.Substr, sub2, none, 0, 3);
            d.Rec(d.StringBytes(sub2).Length);
            d.Rec(d.Call(JsonDriver.StringCompare, s, t));
            d.Rec(d.Call(JsonDriver.StringCompare, s, hello));
            d.Rec(d.Call(JsonDriver.StringCompareC, s, d.CStr("hello")));
            d.Rec(d.Call(JsonDriver.StringCompareC, hello, d.CStr("hello")));
            d.Rec(d.Call(JsonDriver.StringEq, s, t));
            d.Rec(d.Call(JsonDriver.StringEq, s, hello));
            d.Rec(d.Call(JsonDriver.StringEqC, hello, d.CStr("hello")));
            d.Rec(d.Call(JsonDriver.StringEqC, hello, d.CStr("help")));
            d.Rec(d.Call(JsonDriver.StringAt, s, 4));
            d.Rec(d.Call(JsonDriver.StringCStr, none));
            d.Rec(d.Call(JsonDriver.StringEmpty, none));
            d.Call(JsonDriver.StringResize, t, 40);
            d.RecMem(d.Call(JsonDriver.StringCStr, t), 41);
            d.Call(JsonDriver.StringResize, t, 3);
            d.RecText(d.StringBytes(t));
            d.Call(JsonDriver.StringAssign, t, hello);
            d.RecText(d.StringBytes(t));
            d.Call(JsonDriver.StringClear, t);
            d.Rec(d.Call(JsonDriver.StringEmpty, t));
            d.Call(JsonDriver.StringAssign, hello, none);
            d.RecText(d.StringBytes(hello));
            foreach (uint x in new[] { s, t, u, hello, sub2 }) d.DestroyString(x);
            d.Terminate();
        },
        ["array-api"] = d =>
        {
            d.Init();
            uint arr = d.Game(4);
            d.Call(JsonDriver.ArrayCtor, arr);
            uint v = d.NewValue();
            for (uint i = 0; i < 6; i++)
            {
                d.Call(JsonDriver.ValueSetUInt, v, 0, i * 10, 0);
                d.Call(JsonDriver.PushBack, arr, v);
            }
            d.Rec(d.Call(JsonDriver.ArraySize, arr));
            d.Rec(d.Call(JsonDriver.ArrayEmpty, arr));
            uint it = d.Game(4), end = d.Game(4), tmp = d.Game(4);
            d.Call(JsonDriver.ArrayBegin, it, arr);
            d.Call(JsonDriver.ArrayEnd, end, arr);
            while (d.Call(JsonDriver.ArrayItNe, it, end) != 0)
            {
                d.RecMem(d.Call(JsonDriver.GetUInteger, d.Call(JsonDriver.ArrayItDeref, it)), 8);
                d.Call(JsonDriver.ArrayItInc, it);
            }
            d.Call(JsonDriver.ArrayItDtor, it);
            d.Call(JsonDriver.ArrayBegin, it, arr);
            d.Call(JsonDriver.ArrayItAdvance, it, 2);
            d.Call(JsonDriver.ValueSetBool, v, 1);
            d.Call(JsonDriver.ArrayInsert, tmp, arr, it, v);
            d.RecMem(d.Call(JsonDriver.ArrayItDeref, tmp), 0x18);
            d.Call(JsonDriver.ArrayItDtor, tmp);
            d.Call(JsonDriver.ArrayInsert, tmp, arr, end, v);
            d.Call(JsonDriver.ArrayItDtor, tmp);
            d.Call(JsonDriver.ArrayErase, tmp, arr, it);
            d.RecMem(d.Call(JsonDriver.ArrayItDeref, tmp), 0x18);
            uint it2 = d.Game(4);
            d.Call(JsonDriver.ArrayItCopy, it2, tmp);
            d.Call(JsonDriver.ArrayItInc, it2);
            d.Call(JsonDriver.ArrayItAssign, tmp, it2);
            d.Rec(d.Call(JsonDriver.ArrayItNe, tmp, it2));
            uint blank = d.Game(4);
            d.Call(JsonDriver.ArrayItCtor, blank);
            d.Call(JsonDriver.ArrayItAssign, it2, blank);
            d.Rec(d.Call(JsonDriver.ArrayItNe, it2, blank));
            d.Rec(d.Call(JsonDriver.ArrayItDeref, blank));
            foreach (uint x in new[] { it, it2, tmp, blank, end }) d.Call(JsonDriver.ArrayItDtor, x);
            d.Call(JsonDriver.ArrayPopBack, arr);
            d.RecMem(d.Call(JsonDriver.ArrayBack, arr), 0x18);
            uint copy = d.Game(4);
            d.Call(JsonDriver.ArrayCopyCtor, copy, arr);
            d.Call(JsonDriver.ArrayClear, arr);
            d.Rec(d.Call(JsonDriver.ArraySize, arr));
            d.Rec(d.Call(JsonDriver.ArrayEmpty, arr));
            d.Call(JsonDriver.ArrayAssign, arr, copy);
            d.Rec(d.Call(JsonDriver.ArraySize, arr));
            uint wrap = d.NewValue();
            d.Call(JsonDriver.ValueSetArray, wrap, arr);
            d.RecText(d.Serialize(wrap));
            d.Call(JsonDriver.ArrayDtor, arr);
            d.Call(JsonDriver.ArrayDtor, copy);
            d.Call(JsonDriver.ArrayDtor, d.Game(4));                     // never constructed
            d.DestroyValue(v);
            d.DestroyValue(wrap);
            d.Terminate();
        },
        ["object-tree"] = d =>
        {
            d.Init();
            uint obj = d.Game(4);
            d.Call(JsonDriver.ObjectCtor, obj);
            var rng = new Random(1234);
            var keys = Enumerable.Range(0, 48).Select(i => $"k{rng.Next(1000):D3}_{i}").ToList();
            uint val = d.NewValue();
            foreach (var key in keys)
            {
                uint ks = d.NewString(key);
                d.Call(JsonDriver.ValueSetUInt, val, 0, (uint)key.Length, 0);
                d.Call(JsonDriver.ValueAssign, d.Call(JsonDriver.ObjIndex, obj, ks), val);
                d.DestroyString(ks);
            }
            d.Rec(d.Call(JsonDriver.ObjectSize, obj));
            uint pair = d.Game(0x20);
            d.Call(JsonDriver.PairCtorKV, pair, d.NewString("k500_new"), val);
            uint res = d.Game(4);
            d.Call(JsonDriver.ObjectInsert, res, obj, pair);
            d.Rec(d.Call(JsonDriver.ObjectItDeref, res));
            d.Call(JsonDriver.ObjectItDtor, res);
            d.Call(JsonDriver.ObjectInsert, res, obj, pair);                // duplicate
            d.Rec(d.Call(JsonDriver.ObjectItDeref, res));
            d.Call(JsonDriver.ObjectItDtor, res);
            d.Call(JsonDriver.PairDtor, pair);
            uint emptyPair = d.Game(0x20);
            d.Call(JsonDriver.PairCtor, emptyPair);
            d.Call(JsonDriver.ObjectInsert, res, obj, emptyPair);
            d.Call(JsonDriver.ObjectItDtor, res);
            d.Call(JsonDriver.PairDtor, emptyPair);
            foreach (var key in new[] { keys[5], "nope", keys[40] })
            {
                uint ks = d.NewString(key);
                d.Call(JsonDriver.ObjectFind, res, obj, ks);
                d.Rec(d.Call(JsonDriver.ObjectItDeref, res));
                d.Call(JsonDriver.ObjectItDtor, res);
                d.DestroyString(ks);
            }
            uint wrap = d.NewValue();
            d.Call(JsonDriver.ValueSetObject, wrap, obj);
            d.RecText(d.Serialize(wrap));
            foreach (var key in keys.OrderBy(_ => rng.Next()).Take(40).Append("absent"))
            {
                uint ks = d.NewString(key);
                d.Call(JsonDriver.ObjectErase, obj, ks);
                d.DestroyString(ks);
            }
            d.Rec(d.Call(JsonDriver.ObjectSize, obj));
            uint it = d.Game(4), end = d.Game(4), it2 = d.Game(4);
            d.Call(JsonDriver.ObjectBegin, it, obj);
            d.Call(JsonDriver.ObjectEnd, end, obj);
            d.Call(JsonDriver.ObjectItCopy, it2, it);
            d.Call(JsonDriver.ObjectItAdvance, it2, 3);
            d.Rec(d.Call(JsonDriver.ObjectItEq, it, it2));
            while (d.Call(JsonDriver.ObjectItNe, it, end) != 0)
            {
                d.RecText(d.StringBytes(d.Call(JsonDriver.ObjectItArrow, it)));
                d.Rec(d.Call(JsonDriver.ObjectItEq, it, it2));
                d.Call(JsonDriver.ObjectItInc, it);
            }
            d.Call(JsonDriver.ObjectItAssign, it2, end);
            d.Rec(d.Call(JsonDriver.ObjectItEq, it, it2));
            uint blank = d.Game(4);
            d.Call(JsonDriver.ObjectItCtor, blank);
            d.Rec(d.Call(JsonDriver.ObjectItDeref, blank));
            foreach (uint x in new[] { it, end, it2, blank }) d.Call(JsonDriver.ObjectItDtor, x);
            uint copy = d.Game(4);
            d.Call(JsonDriver.ObjectCopyCtor, copy, obj);
            uint other = d.Game(4);
            d.Call(JsonDriver.ObjectCtor, other);
            d.Call(JsonDriver.ObjectAssign, other, copy);
            d.Call(JsonDriver.ValueSetObject, wrap, other);
            d.RecText(d.Serialize(wrap));
            d.Call(JsonDriver.ObjectClear, obj);
            d.Rec(d.Call(JsonDriver.ObjectSize, obj));
            d.Rec(d.Call(JsonDriver.ObjectEmpty, obj));
            foreach (uint x in new[] { obj, copy, other }) d.Call(JsonDriver.ObjectDtor, x);
            d.DestroyValue(val);
            d.DestroyValue(wrap);
            d.Terminate();
        },
        ["serialize-callback"] = d =>
        {
            d.Init();
            uint v = d.NewValue();
            d.Rec(d.Parse(v, "{\"list\":[1,2,3,\"four\"],\"o\":{\"p\":true}}"));
            uint s = d.NewString("");
            d.SerializeFlushes = -1;
            d.Rec(d.Call(JsonDriver.Serialize2, v, s, d.SerializeCallback, 0x77));
            d.SerializeFlushes = 5;                                          // the callback fails on its 5th call
            d.Rec(d.Call(JsonDriver.Serialize2, v, s, d.SerializeCallback, 0x77));
            d.RecText(d.StringBytes(s));
            d.DestroyString(s);
            d.DestroyValue(v);
            d.Terminate();
        },
        ["out-of-memory"] = d =>
        {
            foreach (int n in new[] { 0, 1, 2, 3, 4, 5, 6, 8 })
            {
                d.FailAfter(n);
                d.Rec(d.Call(JsonDriver.Initialize, d.Initializer, d.InitParam));
                d.FailAfter(-1);
                d.Call(JsonDriver.TerminateFn, d.Initializer);
            }
            d.Init();
            uint v = d.NewValue();
            foreach (int n in new[] { 0, 1, 2, 5, 9, 14, 20, 30, 45, 70 })
            {
                d.FailAfter(n);
                d.Rec(d.Parse(v, "{\"alpha\":[1,2,{\"beta\":\"gamma\"}],\"delta\":\"epsilon\"}"));
                d.FailAfter(-1);
                d.RecText(d.Serialize(v));
            }
            uint arr = d.Game(4);
            d.Call(JsonDriver.ArrayCtor, arr);
            d.FailAfter(1);
            d.Call(JsonDriver.PushBack, arr, v);
            d.FailAfter(-1);
            d.Rec(d.Call(JsonDriver.ArraySize, arr));
            uint obj = d.Game(4);
            d.Call(JsonDriver.ObjectCtor, obj);
            d.FailAfter(2);
            d.Rec(d.Call(JsonDriver.ObjIndex, obj, d.NewString("k")));
            d.FailAfter(-1);
            d.Call(JsonDriver.ArrayDtor, arr);
            d.Call(JsonDriver.ObjectDtor, obj);
            d.DestroyValue(v);
            d.Terminate();
        },
        // the C2/D2 (and other shared-code) entry points not used above, so every one of the 179 exports runs
        ["aliases"] = d =>
        {
            d.Init();
            uint init2 = d.Game(8);
            d.Call("_ZN3sce4Json11InitializerC2Ev", init2);
            d.Rec(GuestMemory.Read<byte>(init2));
            d.Call("_ZN3sce4Json11InitializerD2Ev", init2);
            uint alloc = d.Game(8);
            d.Call("_ZN3sce4Json12MemAllocatorC1Ev", alloc);
            d.Rec(GuestMemory.R32(alloc));
            d.Call("_ZN3sce4Json12MemAllocatorD1Ev", alloc);
            uint s = d.Game(4), s2 = d.Game(4), s3 = d.Game(4);
            d.Call("_ZN3sce4Json6StringC2EPKc", s, d.CStr("alias"));
            d.Call("_ZN3sce4Json6StringC1ERKS1_", s2, s);
            d.Call("_ZN3sce4Json6StringC2Ev", s3);
            d.RecText(d.StringBytes(s2));
            uint vals = d.Game(0x18 * 12);
            uint v(int i) => vals + (uint)i * 0x18;
            d.Call("_ZN3sce4Json5ValueC1Ey", v(0), 0, 1, 2);
            d.Call("_ZN3sce4Json5ValueC2ENS0_9ValueTypeE", v(1), 6);
            d.Call("_ZN3sce4Json5ValueC2ERKNS0_6StringE", v(2), s);
            d.Call("_ZN3sce4Json5ValueC2Eb", v(3), 1);
            d.CallD("_ZN3sce4Json5ValueC2Ed", -0.25, v(4));
            d.Call("_ZN3sce4Json5ValueC2Ev", v(5));
            d.Call("_ZN3sce4Json5ValueC2Ex", v(6), 0, 3, 0x80000000);
            d.Call("_ZN3sce4Json5ValueC2ERKS1_", v(7), v(2));
            uint arr = d.Game(4), arr2 = d.Game(4), arr3 = d.Game(4);
            d.Call("_ZN3sce4Json5ArrayC2Ev", arr);
            d.Call(JsonDriver.PushBack, arr, v(0));
            d.Call(JsonDriver.PushBack, arr, v(2));
            d.Call("_ZN3sce4Json5ArrayC1ERKS1_", arr2, arr);
            d.Call("_ZN3sce4Json5ValueC2ERKNS0_5ArrayE", v(8), arr2);
            uint obj = d.Game(4), obj2 = d.Game(4);
            d.Call("_ZN3sce4Json6ObjectC2Ev", obj);
            d.Call(JsonDriver.ValueAssign, d.Call(JsonDriver.ObjIndex, obj, s), v(8));
            d.Call("_ZN3sce4Json6ObjectC2ERKS1_", obj2, obj);
            d.Call("_ZN3sce4Json5ValueC2ERKNS0_6ObjectE", v(9), obj2);
            for (int i = 0; i < 10; i++) d.RecText(d.Serialize(v(i)));
            d.Rec(d.Call("_ZNK3sce4Json5Value9getObjectEv", v(9)));
            d.Rec(d.Call("_ZNK3sce4Json5Value9getObjectEv", v(0)));
            uint ai = d.Game(4), ai2 = d.Game(4), ae = d.Game(4);
            d.Call("_ZN3sce4Json5Array8iteratorC2Ev", ai);
            d.Call(JsonDriver.ArrayItDtor, ai);
            d.Call(JsonDriver.ArrayBegin, ai, arr);
            d.Call("_ZN3sce4Json5Array8iteratorC2ERKS2_", ai2, ai);
            d.Call("_ZN3sce4Json5Array8iteratorppEi", ai2, 0);
            d.Rec(d.Call("_ZNK3sce4Json5Array8iteratorptEv", ai2));
            d.Call(JsonDriver.ArrayEnd, ae, arr);
            d.Rec(d.Call(JsonDriver.ArrayItNe, ai2, ae));
            foreach (uint x in new[] { ai, ai2, ae }) d.Call("_ZN3sce4Json5Array8iteratorD2Ev", x);
            uint oi = d.Game(4), oi2 = d.Game(4);
            d.Call("_ZN3sce4Json6Object8iteratorC1Ev", oi);
            d.Call(JsonDriver.ObjectItDtor, oi);
            d.Call(JsonDriver.ObjectBegin, oi, obj);
            d.Call("_ZN3sce4Json6Object8iteratorC2ERKS2_", oi2, oi);
            d.Call("_ZN3sce4Json6Object8iteratorppEv", oi2);
            d.Rec(d.Call(JsonDriver.ObjectItEq, oi, oi2));
            foreach (uint x in new[] { oi, oi2 }) d.Call("_ZN3sce4Json6Object8iteratorD2Ev", x);
            uint pair = d.Game(0x20), pair2 = d.Game(0x20);
            d.Call("_ZN3sce4Json6Object4PairC2ERKNS0_6StringERKNS0_5ValueE", pair, s, v(3));
            d.Call("_ZN3sce4Json6Object4PairC2Ev", pair2);
            d.RecMem(pair + 8, 0x18);
            foreach (uint x in new[] { pair, pair2 }) d.Call("_ZN3sce4Json6Object4PairD2Ev", x);
            foreach (uint x in new[] { arr, arr2, arr3 }) d.Call("_ZN3sce4Json5ArrayD2Ev", x);
            foreach (uint x in new[] { obj, obj2 }) d.Call("_ZN3sce4Json6ObjectD2Ev", x);
            for (int i = 0; i < 10; i++) d.Call("_ZN3sce4Json5ValueD2Ev", v(i));
            foreach (uint x in new[] { s, s2, s3 }) d.Call("_ZN3sce4Json6StringD2Ev", x);
            // the deleting destructor frees the object through the library's allocator
            uint boxed = d.Game(8);
            d.Call(JsonDriver.MemAllocatorCtor, boxed);
            d.Call("_ZN3sce4Json12MemAllocatorD0Ev", boxed);
            d.Rec(GuestMemory.R32(boxed));
            d.Terminate();
        },
        ["allocator-base"] = d =>
        {
            uint alloc = d.Game(8);
            d.Call(JsonDriver.MemAllocatorCtor, alloc);
            d.Rec(GuestMemory.R32(alloc));
            d.Call(JsonDriver.MemAllocatorNotify, alloc, 0x80920102, 64, 0);
            d.Call(JsonDriver.MemAllocatorNotify, alloc, 0x80920999, 64, 0);
            d.Call(JsonDriver.MemAllocatorDtor, alloc);
            d.Rec(GuestMemory.R32(alloc));
        },
    };
}

/// <summary>What one run of a scenario produced.</summary>
sealed class JsonRunLog
{
    public List<string> Allocator = [], Results = [], Callbacks = [];
    public uint[] Arena = [], Game = [];
    public bool[] Mask = [];
    public uint ArenaUsed;
}

/// <summary>Calls the library from a guest thread through one implementation (firmware exports or HLE stubs).</summary>
sealed unsafe class JsonDriver(JsonHarness h, GuestThread thread, Func<string, uint> resolve)
{
    // mangled names used by the scenarios
    public const string Initialize = "_ZN3sce4Json11Initializer10initializeEPKNS0_13InitParameterE",
        TerminateFn = "_ZN3sce4Json11Initializer9terminateEv", InitializerCtor = "_ZN3sce4Json11InitializerC1Ev",
        InitializerDtor = "_ZN3sce4Json11InitializerD1Ev",
        SetAllocInfoCb = "_ZN3sce4Json11Initializer24setAllocatorInfoCallBackEPFviNS0_9ValueTypeEPvES3_",
        MemAllocatorCtor = "_ZN3sce4Json12MemAllocatorC2Ev", MemAllocatorDtor = "_ZN3sce4Json12MemAllocatorD2Ev",
        MemAllocatorNotify = "_ZN3sce4Json12MemAllocator11notifyErrorEijPv",
        ValueCtor = "_ZN3sce4Json5ValueC1Ev", ValueDtor = "_ZN3sce4Json5ValueD1Ev", ValueCtorType = "_ZN3sce4Json5ValueC1ENS0_9ValueTypeE",
        ValueCtorBool = "_ZN3sce4Json5ValueC1Eb", ValueCtorInt = "_ZN3sce4Json5ValueC1Ex", ValueCtorUInt = "_ZN3sce4Json5ValueC2Ey",
        ValueCtorReal = "_ZN3sce4Json5ValueC1Ed", ValueCtorString = "_ZN3sce4Json5ValueC1ERKNS0_6StringE",
        ValueCtorArray = "_ZN3sce4Json5ValueC1ERKNS0_5ArrayE", ValueCtorObject = "_ZN3sce4Json5ValueC1ERKNS0_6ObjectE",
        ValueCopyCtor = "_ZN3sce4Json5ValueC1ERKS1_", ValueAssign = "_ZN3sce4Json5ValueaSERKS1_", ValueSwap = "_ZN3sce4Json5Value4swapERS1_",
        ValueClear = "_ZN3sce4Json5Value5clearEv", ValueSetType = "_ZN3sce4Json5Value3setENS0_9ValueTypeE",
        ValueSetBool = "_ZN3sce4Json5Value3setEb", ValueSetInt = "_ZN3sce4Json5Value3setEx", ValueSetUInt = "_ZN3sce4Json5Value3setEy",
        ValueSetReal = "_ZN3sce4Json5Value3setEd", ValueSetString = "_ZN3sce4Json5Value3setERKNS0_6StringE",
        ValueSetArray = "_ZN3sce4Json5Value3setERKNS0_5ArrayE", ValueSetObject = "_ZN3sce4Json5Value3setERKNS0_6ObjectE",
        ValueSetValue = "_ZN3sce4Json5Value3setERKS1_",
        GetTypeFn = "_ZNK3sce4Json5Value7getTypeEv", GetRoot = "_ZNK3sce4Json5Value7getRootEv", Count = "_ZNK3sce4Json5Value5countEv",
        ToBool = "_ZNK3sce4Json5ValuecvbEv", ToStringFn = "_ZNK3sce4Json5Value8toStringERNS0_6StringE",
        GetString = "_ZNK3sce4Json5Value9getStringEv", GetArray = "_ZNK3sce4Json5Value8getArrayEv",
        GetInteger = "_ZNK3sce4Json5Value10getIntegerEv", GetUInteger = "_ZNK3sce4Json5Value11getUIntegerEv",
        GetReal = "_ZNK3sce4Json5Value7getRealEv", GetBoolean = "_ZNK3sce4Json5Value10getBooleanEv",
        GetValueIdx = "_ZNK3sce4Json5Value8getValueEj", GetValueKey = "_ZNK3sce4Json5Value8getValueERKNS0_6StringE",
        IndexIdx = "_ZNK3sce4Json5ValueixEj", IndexString = "_ZNK3sce4Json5ValueixERKNS0_6StringE", IndexCStr = "_ZNK3sce4Json5ValueixEPKc",
        ReferString = "_ZN3sce4Json5Value11referStringEv", ReferArray = "_ZN3sce4Json5Value10referArrayEv",
        ReferObject = "_ZN3sce4Json5Value11referObjectEv", ReferInteger = "_ZN3sce4Json5Value12referIntegerEv",
        ReferUInteger = "_ZN3sce4Json5Value13referUIntegerEv", ReferReal = "_ZN3sce4Json5Value9referRealEv",
        ReferBoolean = "_ZN3sce4Json5Value12referBooleanEv", ReferValueIdx = "_ZN3sce4Json5Value10referValueEj",
        ReferValueKey = "_ZN3sce4Json5Value10referValueERKNS0_6StringE",
        SetNullCb = "_ZN3sce4Json5Value21setNullAccessCallbackEPFRKS1_NS0_9ValueTypeEPS2_PvES6_",
        Serialize1 = "_ZN3sce4Json5Value9serializeERNS0_6StringE", Serialize2 = "_ZN3sce4Json5Value9serializeERNS0_6StringEPFiS3_PvES4_",
        ParseCbFn = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPFiRcPvES5_", ParseBuf = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPKcj",
        ParseFile = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPKc",
        StringCtor = "_ZN3sce4Json6StringC1Ev", StringCtorC = "_ZN3sce4Json6StringC1EPKc", StringCopyCtor = "_ZN3sce4Json6StringC2ERKS1_",
        StringDtor = "_ZN3sce4Json6StringD1Ev", StringAssign = "_ZN3sce4Json6StringaSERKS1_",
        StringAppendStr = "_ZN3sce4Json6String6appendERKS1_", StringAppendC = "_ZN3sce4Json6String6appendEPKc",
        StringAppendCN = "_ZN3sce4Json6String6appendEPKcj", StringPlusC = "_ZN3sce4Json6StringpLEPKc", StringPlusCh = "_ZN3sce4Json6StringpLEh",
        StringClear = "_ZN3sce4Json6String5clearEv", StringResize = "_ZN3sce4Json6String6resizeEj",
        StringSize = "_ZNK3sce4Json6String4sizeEv", StringLength = "_ZNK3sce4Json6String6lengthEv", StringEmpty = "_ZNK3sce4Json6String5emptyEv",
        StringCStr = "_ZNK3sce4Json6String5c_strEv", StringAt = "_ZNK3sce4Json6String2atEj",
        StringCompare = "_ZNK3sce4Json6String7compareERKS1_", StringCompareC = "_ZNK3sce4Json6String7compareEPKc",
        StringEq = "_ZNK3sce4Json6StringeqERKS1_", StringEqC = "_ZNK3sce4Json6StringeqEPKc",
        FindStr = "_ZNK3sce4Json6String4findERKS1_j", FindC = "_ZNK3sce4Json6String4findEPKcj", FindCN = "_ZNK3sce4Json6String4findEPKcjj",
        FindCh = "_ZNK3sce4Json6String4findEcj", RFindStr = "_ZNK3sce4Json6String5rfindERKS1_j", RFindC = "_ZNK3sce4Json6String5rfindEPKcj",
        RFindCN = "_ZNK3sce4Json6String5rfindEPKcjj", RFindCh = "_ZNK3sce4Json6String5rfindEcj", Substr = "_ZNK3sce4Json6String6substrEjj",
        ArrayCtor = "_ZN3sce4Json5ArrayC1Ev", ArrayCopyCtor = "_ZN3sce4Json5ArrayC2ERKS1_", ArrayDtor = "_ZN3sce4Json5ArrayD1Ev",
        ArrayAssign = "_ZN3sce4Json5ArrayaSERKS1_", PushBack = "_ZN3sce4Json5Array9push_backERKNS0_5ValueE",
        ArrayPopBack = "_ZN3sce4Json5Array8pop_backEv", ArrayClear = "_ZN3sce4Json5Array5clearEv", ArrayBack = "_ZNK3sce4Json5Array4backEv",
        ArraySize = "_ZNK3sce4Json5Array4sizeEv", ArrayEmpty = "_ZNK3sce4Json5Array5emptyEv",
        ArrayBegin = "_ZNK3sce4Json5Array5beginEv", ArrayEnd = "_ZNK3sce4Json5Array3endEv",
        ArrayInsert = "_ZN3sce4Json5Array6insertERKNS1_8iteratorERKNS0_5ValueE", ArrayErase = "_ZN3sce4Json5Array5eraseERKNS1_8iteratorE",
        ArrayItCtor = "_ZN3sce4Json5Array8iteratorC1Ev", ArrayItCopy = "_ZN3sce4Json5Array8iteratorC1ERKS2_",
        ArrayItDtor = "_ZN3sce4Json5Array8iteratorD1Ev", ArrayItAssign = "_ZN3sce4Json5Array8iteratoraSERKS2_",
        ArrayItInc = "_ZN3sce4Json5Array8iteratorppEv", ArrayItAdvance = "_ZN3sce4Json5Array8iterator7advanceEj",
        ArrayItDeref = "_ZNK3sce4Json5Array8iteratordeEv", ArrayItNe = "_ZNK3sce4Json5Array8iteratorneES2_",
        ObjectCtor = "_ZN3sce4Json6ObjectC1Ev", ObjectCopyCtor = "_ZN3sce4Json6ObjectC1ERKS1_", ObjectDtor = "_ZN3sce4Json6ObjectD1Ev",
        ObjectAssign = "_ZN3sce4Json6ObjectaSERKS1_", ObjIndex = "_ZN3sce4Json6ObjectixERKNS0_6StringE",
        ObjectInsert = "_ZN3sce4Json6Object6insertERKNS1_4PairE", ObjectFind = "_ZNK3sce4Json6Object4findERKNS0_6StringE",
        ObjectErase = "_ZN3sce4Json6Object5eraseERKNS0_6StringE", ObjectClear = "_ZN3sce4Json6Object5clearEv",
        ObjectSize = "_ZNK3sce4Json6Object4sizeEv", ObjectEmpty = "_ZNK3sce4Json6Object5emptyEv",
        ObjectBegin = "_ZNK3sce4Json6Object5beginEv", ObjectEnd = "_ZNK3sce4Json6Object3endEv",
        ObjectItCtor = "_ZN3sce4Json6Object8iteratorC2Ev", ObjectItCopy = "_ZN3sce4Json6Object8iteratorC1ERKS2_",
        ObjectItDtor = "_ZN3sce4Json6Object8iteratorD1Ev", ObjectItAssign = "_ZN3sce4Json6Object8iteratoraSERKS2_",
        ObjectItInc = "_ZN3sce4Json6Object8iteratorppEi", ObjectItAdvance = "_ZN3sce4Json6Object8iterator7advanceEj",
        ObjectItDeref = "_ZNK3sce4Json6Object8iteratordeEv", ObjectItArrow = "_ZNK3sce4Json6Object8iteratorptEv",
        ObjectItEq = "_ZNK3sce4Json6Object8iteratoreqES2_", ObjectItNe = "_ZNK3sce4Json6Object8iteratorneES2_",
        PairCtor = "_ZN3sce4Json6Object4PairC1Ev", PairCtorKV = "_ZN3sce4Json6Object4PairC1ERKNS0_6StringERKNS0_5ValueE",
        PairDtor = "_ZN3sce4Json6Object4PairD1Ev";

    public JsonRunLog Log => h.Current!;
    public uint Initializer, InitParam;
    public uint NullCallback => h.NullCallbackStub;
    public uint SerializeCallback => h.SerializeCallbackStub;
    public uint NullAnswer { get => h.NullAnswer; set => h.NullAnswer = value; }
    public int SerializeFlushes { get => h.SerializeFlushes; set => h.SerializeFlushes = value; }
    public JsonDriver Self => this;

    public uint Call(string name, params uint[] args)
    {
        uint r = thread.CallGuest(resolve(name), args);
        if (name == Initialize && GuestMemory.R32(h.DataBase + Json.DMutex) is var m and not 0) h.LwMutexBlocks.Add(m);
        return r;
    }

    public uint CallD(string name, double d, params uint[] args)
    {
        ((double*)thread.Core.State->D)[0] = d;
        return Call(name, args);
    }

    public void FailAfter(int n) => h.FailAfter = n;
    public uint Game(uint size) => h.Game(size);
    public uint CStr(string s) => h.GameCString(Encoding.UTF8.GetBytes(s));

    public void Rec(uint v) => Log.Results.Add(h.Normalize(v));
    public void Rec(int v) => Log.Results.Add(v.ToString());
    public void RecText(byte[] b) => Log.Results.Add("\"" + Encoding.Latin1.GetString(b) + "\"");
    public void RecMem(uint addr, int len) => Log.Results.Add(addr == 0 ? "null" : Convert.ToHexString(GuestMemory.ReadSpan(addr, len)));
    public void RecValue(uint v) => Log.Results.Add($"type={GuestMemory.R32(v + 0x14)} payload={GuestMemory.Read<ulong>(v + 8):X16} parent={h.Normalize(GuestMemory.R32(v))} ri={h.Normalize(GuestMemory.R32(v + 4))}");

    /// <summary>Initializer + InitParameter (allocator, user data 0x1234, file buffer size) and initialize().</summary>
    public void Init(uint bufSize = 0x100)
    {
        if (Initializer == 0)
        {
            Initializer = Game(8);
            Call(InitializerCtor, Initializer);
            InitParam = Game(12);
        }
        GuestMemory.W32(InitParam, h.Allocator);
        GuestMemory.W32(InitParam + 4, 0x1234);
        GuestMemory.W32(InitParam + 8, bufSize);
        Rec(Call(Initialize, Initializer, InitParam));
    }

    public void Terminate() => Rec(Call(TerminateFn, Initializer));

    public uint NewValue()
    {
        uint v = Game(0x18);
        Call(ValueCtor, v);
        return v;
    }

    public void DestroyValue(uint v) => Call(ValueDtor, v);

    public uint NewString(string s)
    {
        uint str = Game(4);
        Call(StringCtorC, str, CStr(s));
        return str;
    }

    public void DestroyString(uint s) => Call(StringDtor, s);

    public byte[] StringBytes(uint str)
    {
        uint rep = GuestMemory.R32(str);
        if (rep == 0) return [];
        return GuestMemory.ReadSpan(GuestMemory.R32(rep), (int)GuestMemory.R32(rep + 4)).ToArray();
    }

    public uint Parse(uint v, string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        return Call(ParseBuf, v, h.GameCString(b), (uint)b.Length);
    }

    public uint ParseN(uint v, string text, uint n) => Call(ParseBuf, v, h.GameCString(Encoding.UTF8.GetBytes(text)), n);

    /// <summary>Parses through the character callback; <paramref name="failAt"/> &gt;= 0 makes the callback return an
    /// error after that many characters (-1: it returns 0x80920101 at the end of the text).</summary>
    public uint ParseCb(uint v, string text, int failAt)
    {
        h.ReaderText = Encoding.UTF8.GetBytes(text);
        h.ReaderPos = 0;
        h.ReaderFailAt = failAt;
        return Call(ParseCbFn, v, h.ReaderStub, 0x99);
    }

    public byte[] Serialize(uint v)
    {
        uint s = NewString("");
        uint r = Call(Serialize1, v, s);
        Rec(r);
        var b = StringBytes(s);
        DestroyString(s);
        return b;
    }
}

/// <summary>One emulator in the shared test address space, with the firmware module (optional) loaded, HLE stubs for
/// every SceLibJson NID, a guest MemAllocator whose methods are host code, and the arenas both runs share.</summary>
sealed unsafe class JsonHarness : IDisposable
{
    const uint ArenaSize = 0x80_0000, GameSize = 0x4_0000;
    readonly Emulator _emu;
    readonly bool _jitEnabled;
    readonly string _jitMode;
    readonly string _root;
    readonly uint _arena, _gameBase, _stubs;
    readonly Dictionary<string, uint> _hleStubs = [];
    readonly LoadedModule? _lle;
    readonly Json _hle;
    uint _bump, _gameBump;
    int _allocCount;

    public JsonRunLog? Current;
    public uint Allocator, NullCallbackStub, SerializeCallbackStub, ReaderStub;
    int _failAt = -1;
    /// <summary>Makes the allocator fail from the n-th next allocation on (-1: never).</summary>
    public int FailAfter { set => _failAt = value < 0 ? -1 : _allocCount + value; }
    public uint NullAnswer;
    public int SerializeFlushes = -1;
    public byte[] ReaderText = [];
    public int ReaderPos, ReaderFailAt = -1;
    Exception? _fatal;

    public JsonHarness(GuestMemoryFixture fx, string? firmwareModule)
    {
        _jitEnabled = Jit.Enabled;
        _jitMode = Jit.Mode;
        _root = Path.Combine(Path.GetTempPath(), "vitasharp-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "ux0", "data", "json"));
        File.WriteAllText(Path.Combine(_root, "ux0", "data", "json", "test.json"), "{\"file\": [10, 20, {\"deep\": \"text that spans several 16-byte reads\"}], \"ok\": true}");
        File.WriteAllText(Path.Combine(_root, "ux0", "data", "json", "bad.json"), "{\"file\": [10, 20");
        if (firmwareModule != null)
        {
            Directory.CreateDirectory(Path.Combine(_root, "vs0", "sys", "external"));
            File.Copy(firmwareModule, Path.Combine(_root, "vs0", "sys", "external", "libSceJson.suprx"));
        }
        _emu = new Emulator(new EmulatorOptions { FsRoot = _root, Memory = fx.Memory, EnableJit = false, CpuBackend = "interp" });
        _emu.Fatal += (_, e) => _fatal ??= e;
        _hle = _emu.Hle.Modules.OfType<Json>().Single();
        if (firmwareModule != null) _lle = _emu.LoadModule("vs0:sys/external/libSceJson.suprx");

        var mem = _emu.Memory;
        _arena = mem.Alloc(ArenaSize, "json test arena");
        _gameBase = mem.Alloc(GameSize, "json test game");
        _stubs = mem.Alloc(0x4000, "json test stubs");
        uint next = _stubs;
        uint Stub(uint svc, uint nid)
        {
            uint a = next;
            Linker.WriteHleStub(a, svc, nid);
            next += 16;
            return a;
        }
        // the game's MemAllocator subclass: vptr -> [D1, D0, allocate, deallocate, notifyError]
        uint vt = mem.Alloc(0x1000, "json test vtable");
        uint dtor = Stub(_emu.Hle.RegisterHostFunction("test:~Allocator", c => { }), 0);
        GuestMemory.W32(vt, dtor);
        GuestMemory.W32(vt + 4, dtor);
        GuestMemory.W32(vt + 8, Stub(_emu.Hle.RegisterHostFunction("test:allocate", c => c.Return(Allocate(c.Gpr(1), c.Gpr(2)))), 0));
        GuestMemory.W32(vt + 12, Stub(_emu.Hle.RegisterHostFunction("test:deallocate", c => Current!.Allocator.Add($"F {Normalize(c.Gpr(1))} u={c.Gpr(2):X}")), 0));
        GuestMemory.W32(vt + 16, Stub(_emu.Hle.RegisterHostFunction("test:notifyError", c => Current!.Allocator.Add($"E {c.Gpr(1):X8} size={c.Gpr(2)} u={c.Gpr(3):X}")), 0));
        Allocator = vt + 0x100;
        GuestMemory.W32(Allocator, vt);
        NullCallbackStub = Stub(_emu.Hle.RegisterHostFunction("test:nullAccess", c =>
        {
            Current!.Callbacks.Add($"null type={c.Gpr(0)} parent={Normalize(c.Gpr(1))} u={c.Gpr(2):X}");
            c.Return(NullAnswer);
        }), 0);
        SerializeCallbackStub = Stub(_emu.Hle.RegisterHostFunction("test:serialize", c =>
        {
            uint rep = GuestMemory.R32(c.Gpr(0));
            string text = rep == 0 ? "<null>" : Encoding.Latin1.GetString(GuestMemory.ReadSpan(GuestMemory.R32(rep), (int)GuestMemory.R32(rep + 4)));
            Current!.Callbacks.Add($"flush \"{text}\" u={c.Gpr(1):X}");
            int ret = SerializeFlushes == 0 ? unchecked((int)0x80001234) : 0;
            if (SerializeFlushes > 0) SerializeFlushes--;
            c.Return(ret);
        }), 0);
        ReaderStub = Stub(_emu.Hle.RegisterHostFunction("test:reader", c =>
        {
            if (ReaderFailAt >= 0 && ReaderPos >= ReaderFailAt) { c.Return(unchecked((int)0x80005678)); return; }
            if (ReaderPos >= ReaderText.Length) { c.Return(unchecked((int)0x80920101)); return; }
            GuestMemory.Write(c.Gpr(0), ReaderText[ReaderPos++]);
            c.Return(0);
        }), 0);
        _nextStub = next;
        _emu.Code.Invalidate(0, uint.MaxValue);
    }

    uint _nextStub;

    /// <summary>An HLE stub for a SceLibJson export (made on first use).</summary>
    uint HleStub(string name)
    {
        if (_hleStubs.TryGetValue(name, out uint a)) return a;
        var info = NidDatabase.LookupName(name).Single(n => n.Library == "SceLibJson");
        a = _nextStub;
        _nextStub += 16;
        Linker.WriteHleStub(a, _emu.Hle.GetHleFunctionIndex(info.Nid, 0x0854B69C, "SceLibJson"), info.Nid);
        _emu.Code.Invalidate(a, 16);
        return _hleStubs[name] = a;
    }

    uint Allocate(uint size, uint user)
    {
        int n = _allocCount++;
        if (_failAt >= 0 && n >= _failAt)
        {
            Current!.Allocator.Add($"A {size} u={user:X} -> fail");
            return 0;
        }
        uint p = _arena + _bump;
        _bump = (_bump + size + 7) & ~7u;
        if (_bump > ArenaSize) throw new InvalidOperationException("json test arena exhausted");
        Current!.Allocator.Add($"A {size} u={user:X} -> +{p - _arena:X}");
        _sizes[p] = size;
        return p;
    }

    readonly Dictionary<uint, uint> _sizes = [];

    public uint Game(uint size)
    {
        uint p = _gameBase + _gameBump;
        _gameBump = (_gameBump + size + 7) & ~7u;
        if (_gameBump > GameSize) throw new InvalidOperationException("json test game memory exhausted");
        return p;
    }

    public uint GameCString(byte[] s)
    {
        uint p = Game((uint)s.Length + 1);
        s.CopyTo(GuestMemory.Span(p, s.Length));
        GuestMemory.Write(p + (uint)s.Length, (byte)0);
        return p;
    }

    /// <summary>Addresses of the module's data, its empty literal and MemAllocator's vtable, as offsets (the firmware
    /// module and the HLE keep them in different places).</summary>
    public string Normalize(uint v)
    {
        if (_runLle && _lle != null)
        {
            uint b0 = _lle.Segments[0].Address, b1 = _lle.Segments[1].Address;
            if (v == b0 + 0x6210) return "LIT";
            if (v == b0 + 0x621C) return "VPTR";
            if (v >= b1 && v < b1 + 0x48) return $"D+{v - b1:X}";
        }
        else
        {
            uint d = _hle.Data;
            if (v == d + Json.DLit) return "LIT";
            if (v == d + Json.DVptr) return "VPTR";
            if (v >= d && v < d + 0x48) return $"D+{v - d:X}";
        }
        if (v >= _arena && v < _arena + ArenaSize) return $"A+{v - _arena:X}";
        if (v >= _gameBase && v < _gameBase + GameSize) return $"G+{v - _gameBase:X}";
        return $"{v:X8}";
    }

    uint NormalizeWord(uint v)
    {
        string n = Normalize(v);
        if (n == "LIT") return 0xEEEE0000;
        if (n == "VPTR") return 0xEEEE0001;
        if (n.StartsWith("D+")) return 0xEEEF0000 + Convert.ToUInt32(n[2..], 16);
        return v;
    }

    bool _runLle;

    public JsonRunLog Run(bool useLle, Action<JsonDriver> body)
    {
        if (useLle && _lle == null) throw new InvalidOperationException("no firmware module");
        _runLle = useLle;
        GuestMemory.Fill(_arena, 0, ArenaSize);
        GuestMemory.Fill(_gameBase, 0, GameSize);
        _bump = _gameBump = 0;
        _allocCount = 0;
        _sizes.Clear();
        _failAt = -1;
        SerializeFlushes = -1;
        NullAnswer = 0;
        _fatal = null;
        var log = Current = new JsonRunLog();
        Exception? failure = null;
        var t = _emu.Kernel.CreateThread("json-test", 0, 0x10000100, 0x10000, 0, 0x70000);
        t.HostBody = th =>
        {
            try
            {
                var d = new JsonDriver(this, th, name => useLle
                    ? (_emu.Linker.TryGetFunctionExport(NidDatabase.LookupName(name).Single(n => n.Library == "SceLibJson").Nid, out uint a) ? a : throw new InvalidOperationException(name))
                    : HleStub(name));
                body(d);
                log.ArenaUsed = _bump;
                log.Arena = new uint[_bump / 4];
                log.Mask = new bool[log.Arena.Length];
                for (int i = 0; i < log.Arena.Length; i++) log.Arena[i] = NormalizeWord(GuestMemory.R32(_arena + (uint)i * 4));
                log.Game = new uint[_gameBump / 4];
                for (int i = 0; i < log.Game.Length; i++) log.Game[i] = NormalizeWord(GuestMemory.R32(_gameBase + (uint)i * 4));
                foreach (var (p, size) in _sizes)
                {
                    int w = (int)((p - _arena) / 4);
                    if (size == 0x48) log.Mask[w + 9] = true; // node padding (uninitialised stack bytes in operator[])
                    if (size == 0x20 && LwMutexBlocks.Contains(p)) for (int k = 0; k < 8; k++) log.Mask[w + k] = true;
                }
            }
            catch (Exception e) { failure = e; }
        };
        LwMutexBlocks.Clear();
        t.Start(0, 0);
        Assert.True(t.WaitForExit(120_000), "json test thread did not finish");
        _emu.Kernel.DeleteThread(t);
        if (failure != null) throw new Xunit.Sdk.XunitException($"driver failed ({(useLle ? "firmware" : "HLE")}): {failure}");
        if (_fatal != null) throw new Xunit.Sdk.XunitException($"guest fault ({(useLle ? "firmware" : "HLE")}): {_fatal}");
        return log;
    }

    /// <summary>The lwmutex work areas (the module's 0x20 allocation in initialize): their contents hold kernel UIDs.</summary>
    public readonly HashSet<uint> LwMutexBlocks = [];

    /// <summary>The module data of the implementation the current run uses.</summary>
    public uint DataBase => _runLle ? _lle!.Segments[1].Address : _hle.Data;

    public void Dispose()
    {
        _emu.Dispose();
        Jit.Enabled = _jitEnabled;
        Jit.Select(_jitMode);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}

/// <summary>Where the firmware's libSceJson.suprx comes from: <see cref="EnvVars.FirmwareDir"/> (a Vita3K data folder
/// or a vs0 folder), else %APPDATA%\Vita3K\Vita3K, else installed from the official PSVUPDAT.PUP
/// (<see cref="EnvVars.FirmwarePup"/> or the repository root) into a temporary folder.</summary>
static class JsonFirmware
{
    static readonly Lazy<string?> _path = new(Find);
    public static string? ModulePath => _path.Value;

    static string? InVs0(string dir)
    {
        foreach (var p in new[] { Path.Combine(dir, "vs0", "sys", "external", "libSceJson.suprx"), Path.Combine(dir, "sys", "external", "libSceJson.suprx") })
            if (File.Exists(p)) return p;
        return null;
    }

    static string? Find()
    {
        var env = Environment.GetEnvironmentVariable(EnvVars.FirmwareDir);
        if (!string.IsNullOrEmpty(env)) return InVs0(env);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData) && InVs0(Path.Combine(appData, "Vita3K", "Vita3K")) is { } v3k) return v3k;
        if (RealFirmware.PupPath is not { } pup) return null;
        var tmp = Path.Combine(Path.GetTempPath(), "vitasharp-json-fw-" + Environment.ProcessId);
        FirmwareInstaller.Install(pup, tmp);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { FirmwareInstaller.DeleteTree(tmp); } catch (IOException) { } };
        return InVs0(tmp);
    }
}

public sealed class JsonFirmwareTheoryAttribute : TheoryAttribute
{
    public JsonFirmwareTheoryAttribute()
    {
        if (JsonFirmware.ModulePath == null)
            Skip = $"libSceJson.suprx not found (set {EnvVars.FirmwareDir} to a Vita3K data folder or vs0, or {EnvVars.FirmwarePup})";
    }
}
