using System.IO.Compression;
using System.Text.Json;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// meta.json of a custom GPU driver package: the zip format of the Adreno/Turnip driver packages used by yuzu,
/// Skyline, Vita3K and the community driver builds (K11MCH1/AdrenoToolsDrivers):
/// <c>{"schemaVersion":1,"name":"Turnip","description":"...","author":"...","packageVersion":"1","vendor":"Mesa",
/// "driverVersion":"Vulkan 1.3.274","minApi":27,"libraryName":"vulkan.ad07XX.so"}</c>.
/// </summary>
public sealed record CustomDriverMeta(int SchemaVersion, string Name, string Description, string Author, string PackageVersion,
    string Vendor, string DriverVersion, int MinApi, string LibraryName)
{
    /// <summary>Parses meta.json; null (with the reason) when a required field is missing or unsafe.</summary>
    public static CustomDriverMeta? Parse(string json, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int I(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
            var m = new CustomDriverMeta(I("schemaVersion"), S("name"), S("description"), S("author"), S("packageVersion"),
                S("vendor"), S("driverVersion"), I("minApi"), S("libraryName"));
            if (m.SchemaVersion != 1) { error = $"unsupported schemaVersion {m.SchemaVersion}"; return null; }
            if (m.Name.Length == 0) { error = "no name"; return null; }
            if (!IsSafeFileName(m.LibraryName) || !m.LibraryName.EndsWith(".so", StringComparison.Ordinal)) { error = $"bad libraryName '{m.LibraryName}'"; return null; }
            return m;
        }
        catch (JsonException e) { error = $"invalid JSON ({e.Message})"; return null; }
    }

    /// <summary>A plain file name: no directories, no "..", no control characters.</summary>
    public static bool IsSafeFileName(string s) =>
        s.Length is > 0 and < 128 && s.IndexOfAny(['/', '\\', ':']) < 0 && s != "." && s != ".." && !s.Any(char.IsControl);
}

/// <summary>An installed driver: its folder (ends with a separator, as libadrenotools expects) and metadata.</summary>
public sealed record InstalledGpuDriver(string Id, string Directory, CustomDriverMeta Meta)
{
    public string LibraryPath => Path.Combine(Directory, Meta.LibraryName);
}

/// <summary>
/// User-supplied GPU drivers (Android: loaded with libadrenotools instead of the system Vulkan driver). Each
/// package is extracted into &lt;root&gt;/&lt;id&gt;/ where id is a file-name-safe form of its name. Platform-neutral
/// (zip + JSON), so installing and listing are unit-tested on the desktop; loading is Android-only
/// (VitaSharp.Android/Gpu/AdrenoToolsDriverLoader.cs).
/// </summary>
public sealed class CustomDriverStore(string root)
{
    public string Root { get; } = root;

    public static string IdOf(string name)
    {
        var id = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray()).Trim('.');
        return id.Length == 0 ? "driver" : id;
    }

    /// <summary>Installs a driver package (zip with meta.json and the library). Throws InvalidDataException with the
    /// reason when the package is not usable; an installed driver of the same name is replaced.</summary>
    public InstalledGpuDriver Install(string zipPath, int deviceApiLevel = int.MaxValue)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var metaEntry = zip.GetEntry("meta.json") ?? throw new InvalidDataException("no meta.json in the package");
        string json;
        using (var s = new StreamReader(metaEntry.Open())) json = s.ReadToEnd();
        var meta = CustomDriverMeta.Parse(json, out var err) ?? throw new InvalidDataException($"meta.json: {err}");
        if (meta.MinApi > deviceApiLevel) throw new InvalidDataException($"needs Android API {meta.MinApi}, the device has {deviceApiLevel}");
        if (zip.GetEntry(meta.LibraryName) == null) throw new InvalidDataException($"{meta.LibraryName} (libraryName) is not in the package");
        string id = IdOf(meta.Name);
        string dir = Path.Combine(Root, id);
        string tmp = dir + ".installing";
        if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
        System.IO.Directory.CreateDirectory(tmp);
        string fullTmp = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            // flat packages only: the library and its companions live next to meta.json
            if (!CustomDriverMeta.IsSafeFileName(e.FullName)) continue;
            string dest = Path.GetFullPath(Path.Combine(tmp, e.FullName));
            if (!dest.StartsWith(fullTmp, StringComparison.Ordinal)) continue;
            e.ExtractToFile(dest, true);
        }
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.Move(tmp, dir);
        return new InstalledGpuDriver(id, dir + Path.DirectorySeparatorChar, meta);
    }

    public IReadOnlyList<InstalledGpuDriver> List()
    {
        var list = new List<InstalledGpuDriver>();
        if (!System.IO.Directory.Exists(Root)) return list;
        foreach (var dir in System.IO.Directory.GetDirectories(Root).Order(StringComparer.Ordinal))
        {
            if (dir.EndsWith(".installing", StringComparison.Ordinal)) continue;
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) continue;
            var meta = CustomDriverMeta.Parse(File.ReadAllText(metaPath), out _);
            if (meta != null && File.Exists(Path.Combine(dir, meta.LibraryName)))
                list.Add(new InstalledGpuDriver(Path.GetFileName(dir), dir + Path.DirectorySeparatorChar, meta));
        }
        return list;
    }

    public InstalledGpuDriver? Find(string id) => id.Length == 0 ? null : List().FirstOrDefault(d => d.Id == id);

    public bool Remove(string id)
    {
        if (!CustomDriverMeta.IsSafeFileName(id)) return false;
        string dir = Path.Combine(Root, id);
        if (!System.IO.Directory.Exists(dir)) return false;
        System.IO.Directory.Delete(dir, true);
        return true;
    }
}

/// <summary>GPU driver settings (Android "Driver da GPU"): which driver to load and libadrenotools options.</summary>
public sealed class GpuDriverSettings
{
    /// <summary>Installed driver id (<see cref="CustomDriverStore"/>), "" = the system driver.</summary>
    public string Driver { get; set; } = "";
    /// <summary>ADRENOTOOLS_DRIVER_FILE_REDIRECT: redirect the driver's file accesses (shader dumps, configs) to the app's folder.</summary>
    public bool FileRedirect { get; set; }
    /// <summary>adrenotools_set_turbo: keep the GPU at maximum clocks while a game runs (more heat and battery).</summary>
    public bool Turbo { get; set; }
    /// <summary>Patch BCn texture support into Qualcomm drivers that hide it (libadrenotools BCeNabler).</summary>
    public bool PatchBcn { get; set; } = true;

    public static GpuDriverSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            bool B(string k, bool d) => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
            return new GpuDriverSettings
            {
                Driver = r.TryGetProperty("driver", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() ?? "" : "",
                FileRedirect = B("fileRedirect", false), Turbo = B("turbo", false), PatchBcn = B("patchBcn", true),
            };
        }
        catch (Exception e) when (e is JsonException or IOException) { return new(); }
    }

    public void Save(string path)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        using var w = new Utf8JsonWriter(f, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("driver", Driver);
        w.WriteBoolean("fileRedirect", FileRedirect);
        w.WriteBoolean("turbo", Turbo);
        w.WriteBoolean("patchBcn", PatchBcn);
        w.WriteEndObject();
    }
}
