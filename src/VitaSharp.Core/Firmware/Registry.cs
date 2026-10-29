using System.Collections.Concurrent;
using System.Text;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The system registry (SceRegMgr): every key the firmware defines, with its type, size and factory default, read from
/// the installed firmware's template (os0:kd/registry.db0, XOR-obfuscated text: Vita3K regmgr.cpp documents the
/// layout), plus the values the system software wrote, kept in &lt;FsRoot&gt;/vd0/registry/vitasharp-registry.txt
/// (one "path TAB type TAB value" line per written key). One instance per file-system root, shared by every
/// process of the system (the shell and the applications see the same registry).
/// Keys are addressed as category + name ("/CONFIG/SYSTEM/", "language"); categories are matched with or without
/// their trailing '/'.
/// </summary>
public sealed class Registry
{
    public enum KeyType { Int = 0, Str = 1, Bin = 2 }

    public sealed record Key(string Category, string Name, KeyType Type, int Size, byte[] Default)
    {
        public string Path => Category + Name;
    }

    static readonly byte[] XorKey = [0x89, 0xFA, 0x95, 0x48, 0xCB, 0x6D, 0x77, 0x9D, 0xA2, 0x25, 0x34, 0xFD, 0xA9, 0x35, 0x59, 0x6E];
    const int TemplateHeaderSize = 138;

    static readonly ConcurrentDictionary<string, Registry> Instances = new(StringComparer.OrdinalIgnoreCase);

    public static Registry For(string fsRoot) => Instances.GetOrAdd(System.IO.Path.GetFullPath(fsRoot), r => new Registry(r));

    /// <summary>A fresh, uncached instance (tests: reading back what an earlier instance saved).</summary>
    internal static Registry Open(string fsRoot) => new(System.IO.Path.GetFullPath(fsRoot));

    readonly object _lock = new();
    readonly Dictionary<string, Key> _keys = new(StringComparer.Ordinal);
    readonly Dictionary<string, byte[]> _written = new(StringComparer.Ordinal);
    /// <summary>Defaults that follow the emulator's options (language, button assignment...) instead of the template.</summary>
    readonly Dictionary<string, Func<int>> _dynamicDefaults = new(StringComparer.Ordinal);
    public string StorePath { get; }

    /// <summary>Called after a value changes (path), e.g. to notify registry callbacks.</summary>
    public event Action<string>? Changed;

    public IReadOnlyCollection<Key> Keys => _keys.Values;

    Registry(string fsRoot)
    {
        StorePath = System.IO.Path.Combine(fsRoot, "vd0", "registry", "vitasharp-registry.txt");
        string template = System.IO.Path.Combine(fsRoot, "os0", "kd", "registry.db0");
        try
        {
            if (File.Exists(template)) ParseTemplate(Decrypt(File.ReadAllBytes(template)));
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            Log.Warn($"registry: cannot read {template}: {e.Message}");
        }
        if (_keys.Count == 0) Log.Info("registry: no firmware template (os0:kd/registry.db0); only the keys VitaSharp knows exist");
        Load();
    }

    internal static string Decrypt(byte[] data)
    {
        if (data.Length <= TemplateHeaderSize) return "";
        var sb = new StringBuilder(data.Length - TemplateHeaderSize);
        for (int i = 0; i < data.Length - TemplateHeaderSize; i++) sb.Append((char)(data[i + TemplateHeaderSize] ^ XorKey[i & 0xF]));
        return sb.ToString();
    }

    /// <summary>[BASE...] maps numbers to path components; [REG-BAS] lists "n/n/n=type:size:...:default" entries
    /// (a component "NN-MM" expands to one category per number). The other sections are per-model overrides.</summary>
    void ParseTemplate(string text)
    {
        var names = new Dictionary<int, string>();
        string section = "";
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith('[')) { section = line; continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string left = line[..eq], right = line[(eq + 1)..];
            if (section.StartsWith("[BASE", StringComparison.Ordinal))
            {
                if (int.TryParse(left, out int n)) names[n] = right;
            }
            else if (section == "[REG-BAS")
            {
                var path = new StringBuilder("/");
                foreach (var part in left.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    path.Append(int.TryParse(part, out int n) && names.TryGetValue(n, out var s) ? s : part);
                string full = path.ToString();
                int slash = full.LastIndexOf('/') + 1;
                string category = full[..slash], name = full[slash..];
                var values = right.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (values.Length < 2 || !int.TryParse(values[0], out int type) || !int.TryParse(values[1], out int size)) continue;
                string def = values[^1];
                byte[] init = new byte[Math.Max(size, 0)];
                if (type == (int)KeyType.Int) BitConverter.GetBytes(int.TryParse(def, out int iv) ? iv : 0).AsSpan(0, Math.Min(4, init.Length)).CopyTo(init);
                else if (def != "0") Encoding.UTF8.GetBytes(def).AsSpan(0, Math.Min(def.Length, init.Length)).CopyTo(init);
                foreach (var cat in ExpandRange(category))
                    _keys.TryAdd(cat + name, new Key(cat, name, (KeyType)type, size, init));
            }
        }
    }

    static IEnumerable<string> ExpandRange(string category)
    {
        var m = System.Text.RegularExpressions.Regex.Match(category, @"^(.*/)(\d{2})-(\d{2})(/.*)$");
        if (!m.Success) { yield return category; yield break; }
        int a = int.Parse(m.Groups[2].Value), b = int.Parse(m.Groups[3].Value);
        for (int i = a; i <= b; i++) yield return $"{m.Groups[1].Value}{i:D2}{m.Groups[4].Value}";
    }

    static string Normalize(string category, string name)
    {
        if (!category.StartsWith('/')) category = "/" + category;
        if (!category.EndsWith('/')) category += "/";
        return category + name;
    }

    /// <summary>Adds a key the template lacks (no firmware installed) or makes its default follow the emulator.</summary>
    public void Define(string path, KeyType type, int size, Func<int>? dynamicDefault = null, byte[]? def = null)
    {
        int slash = path.LastIndexOf('/') + 1;
        lock (_lock)
        {
            _keys.TryAdd(path, new Key(path[..slash], path[slash..], type, size, def ?? new byte[size]));
            if (dynamicDefault != null) _dynamicDefaults[path] = dynamicDefault;
        }
    }

    public Key? Find(string category, string name) { lock (_lock) return _keys.GetValueOrDefault(Normalize(category, name)); }

    /// <summary>The current value of a key (written value, else its default), or null for an unknown key.</summary>
    public byte[]? Get(string path)
    {
        lock (_lock)
        {
            if (_written.TryGetValue(path, out var w)) return w;
            if (_dynamicDefaults.TryGetValue(path, out var d)) return BitConverter.GetBytes(d());
            return _keys.TryGetValue(path, out var k) ? k.Default : null;
        }
    }

    public byte[]? Get(string category, string name) => Get(Normalize(category, name));

    public bool TryGetInt(string category, string name, out int value)
    {
        value = 0;
        if (Get(category, name) is not { } v) return false;
        Span<byte> b = stackalloc byte[4];
        b.Clear();
        v.AsSpan(0, Math.Min(4, v.Length)).CopyTo(b);
        value = BitConverter.ToInt32(b);
        return true;
    }

    /// <summary>Stores a value; false for a key the firmware does not define.</summary>
    public bool Set(string category, string name, ReadOnlySpan<byte> value)
    {
        string path = Normalize(category, name);
        lock (_lock)
        {
            if (!_keys.TryGetValue(path, out var k)) return false;
            var stored = new byte[k.Type == KeyType.Int ? 4 : Math.Max(k.Size, value.Length)];
            value[..Math.Min(value.Length, stored.Length)].CopyTo(stored);
            _written[path] = stored;
            Save();
        }
        Changed?.Invoke(path);
        return true;
    }

    public bool SetInt(string category, string name, int value) => Set(category, name, BitConverter.GetBytes(value));

    void Load()
    {
        if (!File.Exists(StorePath)) return;
        try
        {
            foreach (var line in File.ReadAllLines(StorePath))
            {
                var f = line.Split('\t');
                if (f.Length != 3 || !_keys.TryGetValue(f[0], out var k)) continue;
                _written[f[0]] = k.Type == KeyType.Int ? BitConverter.GetBytes(int.Parse(f[2])) : Convert.FromHexString(f[2]);
            }
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            Log.Warn($"registry: cannot read {StorePath}: {e.Message}");
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
            File.WriteAllLines(StorePath, _written.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            {
                var k = _keys[kv.Key];
                return $"{kv.Key}\t{k.Type}\t{(k.Type == KeyType.Int ? BitConverter.ToInt32(kv.Value).ToString() : Convert.ToHexString(kv.Value))}";
            }), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"registry: cannot write {StorePath}: {e.Message}");
        }
    }
}
