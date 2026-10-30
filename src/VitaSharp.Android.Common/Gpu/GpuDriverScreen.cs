using VitaSharp.Core.Gpu;

namespace VitaSharp.Android.Gpu;

/// <summary>One row of the "Driver da GPU" list: the system driver (<see cref="Id"/> "") or an installed package.</summary>
public sealed record GpuDriverOption(string Id, string Title, string Detail, bool Selected, bool Removable);

/// <summary>
/// The model behind the "Driver da GPU" screen (docs/android/mockups/driver-gpu.html; platform-neutral, so it is
/// unit-tested on the desktop): list the system driver and the installed custom driver packages, pick one, install a
/// package the user chose with the Storage Access Framework, remove one, and the libadrenotools options. The choice is
/// stored in &lt;data&gt;/gpu_driver.json and read by AdrenoToolsDriverLoader.Create (src/VitaSharp.Android) before
/// the renderer starts.
/// <para>
/// A process can load one Vulkan driver safely, so a change only applies to the next emulation session:
/// <see cref="Select"/> returns true when the front-end must restart a running game (or tell the user it will).
/// </para>
/// </summary>
public sealed class GpuDriverScreen
{
    public const string SettingsFile = "gpu_driver.json", DriversDir = "gpu_drivers";

    readonly string _settingsPath;

    public CustomDriverStore Store { get; }
    public GpuDriverSettings Settings { get; }

    /// <summary>True on Qualcomm GPUs (stock or Turnip, <see cref="GpuQuirk.CustomDriverCapable"/>): only there does
    /// the screen offer custom drivers; elsewhere it shows the system driver and the GPU information only.</summary>
    public bool CustomDriversSupported { get; }

    public GpuDriverScreen(string dataRoot, GpuCapabilities? caps)
    {
        _settingsPath = Path.Combine(dataRoot, SettingsFile);
        Store = new CustomDriverStore(Path.Combine(dataRoot, DriversDir));
        Settings = GpuDriverSettings.Load(_settingsPath);
        // before the first renderer start there are no capabilities yet: offer the list, the loader checks again
        CustomDriversSupported = caps == null || caps.Has(GpuQuirk.CustomDriverCapable);
    }

    public IReadOnlyList<GpuDriverOption> Options()
    {
        var installed = Store.List();
        bool systemSelected = Settings.Driver.Length == 0 || installed.All(d => d.Id != Settings.Driver);
        var list = new List<GpuDriverOption> { new("", "Driver do sistema", "O que veio no aparelho, aberto pela libadrenotools", systemSelected, false) };
        foreach (var d in installed)
            list.Add(new(d.Id, d.Meta.Name, Detail(d.Meta), !systemSelected && d.Id == Settings.Driver, true));
        return list;
    }

    static string Detail(CustomDriverMeta m)
    {
        var parts = new List<string>();
        if (m.Vendor.Length > 0) parts.Add(m.Vendor);
        if (m.DriverVersion.Length > 0) parts.Add(m.DriverVersion);
        parts.Add(m.LibraryName);
        if (m.MinApi > 0) parts.Add($"API {m.MinApi}+");
        return string.Join(" · ", parts);
    }

    /// <summary>Selects a driver ("" = the system driver). True when the choice changed (applies on the next start).</summary>
    public bool Select(string id)
    {
        if (id.Length > 0 && Store.Find(id) == null) throw new ArgumentException($"GPU driver '{id}' is not installed", nameof(id));
        if (id == Settings.Driver) return false;
        Settings.Driver = id;
        Settings.Save(_settingsPath);
        return true;
    }

    /// <summary>
    /// Installs a driver package from the stream of the file the user picked (ContentResolver.OpenInputStream of the
    /// ACTION_OPEN_DOCUMENT result). The zip is copied next to the store first because the package reader needs a
    /// seekable file. Throws InvalidDataException with the reason (shown in the UI) when the package is not usable.
    /// </summary>
    public InstalledGpuDriver Install(Stream package, int deviceApiLevel)
    {
        Directory.CreateDirectory(Store.Root);
        string tmp = Path.Combine(Store.Root, ".download.zip");
        try
        {
            using (var f = File.Create(tmp)) package.CopyTo(f);
            return Store.Install(tmp, deviceApiLevel);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Removes an installed package; removing the selected one goes back to the system driver.</summary>
    public bool Remove(string id)
    {
        if (!Store.Remove(id)) return false;
        if (Settings.Driver == id)
        {
            Settings.Driver = "";
            Settings.Save(_settingsPath);
        }
        return true;
    }

    /// <summary>The three switches of the screen ("Habilitar texturas BCn", "Modo turbo da GPU", "Redirecionar
    /// arquivos do driver"); null leaves a value unchanged.</summary>
    public void SetOptions(bool? patchBcn = null, bool? turbo = null, bool? fileRedirect = null)
    {
        if (patchBcn is { } b) Settings.PatchBcn = b;
        if (turbo is { } t) Settings.Turbo = t;
        if (fileRedirect is { } r) Settings.FileRedirect = r;
        Settings.Save(_settingsPath);
    }
}

/// <summary>A labelled value, feature or quirk line of the "Informações da GPU" view.</summary>
public sealed record GpuInfoRow(string Label, string Value);
public sealed record GpuFeatureChip(string Name, bool Present, string? Note = null);
public sealed record GpuQuirkLine(GpuQuirk Quirk, string Description, bool Workaround);

/// <summary>
/// The "Informações da GPU" view (docs/android/mockups/gpu-info.html): what <see cref="GpuCapabilities"/> found at
/// device creation, in Portuguese. The share button sends <see cref="GpuCapabilities.Describe"/> (the same block the
/// startup log and info.txt carry), so bug reports stay in the format docs/notes/mobile-gpu.md uses.
/// </summary>
public static class GpuInfoView
{
    public static IReadOnlyList<GpuInfoRow> Rows(GpuCapabilities caps, string? customDriver = null)
    {
        var d = caps.Device;
        string driver = customDriver ?? $"{DriverName(caps.Driver)} {GpuCapabilities.FormatDriverVersion(caps.Vendor, caps.Driver, caps.DriverVersion)}";
        var arch = new List<string>();
        if (caps.Has(GpuQuirk.TileBased)) arch.Add("tile-based");
        if (d.UnifiedMemory) arch.Add("memória unificada");
        return
        [
            new("GPU", d.DeviceName),
            new("Driver", driver),
            new("Vulkan", $"{d.ApiVersion >> 22}.{(d.ApiVersion >> 12) & 0x3FF}.{d.ApiVersion & 0xFFF}"),
            new("Arquitetura", arch.Count > 0 ? string.Join(" · ", arch) : "imediata"),
            new("Orçamento de memória", caps.Has(GpuFeature.MemoryBudget) ? "VK_EXT_memory_budget" : $"{caps.FallbackBudgetFraction:P0} do heap"),
        ];
    }

    static string DriverName(GpuDriver d) => d switch
    {
        GpuDriver.QualcommProprietary => "Qualcomm proprietário",
        GpuDriver.Turnip => "Mesa Turnip",
        GpuDriver.ArmProprietary => "ARM proprietário",
        GpuDriver.Panvk => "Mesa PanVK",
        GpuDriver.ImaginationProprietary => "Imagination proprietário",
        GpuDriver.SamsungProprietary => "Samsung proprietário",
        _ => d.ToString(),
    };

    /// <summary>The features the renderer uses (the "Recursos" chips). <paramref name="bcnPatched"/>: BCn sampled
    /// through libadrenotools' BCeNabler although the driver does not report it.</summary>
    public static IReadOnlyList<GpuFeatureChip> Features(GpuCapabilities caps, bool bcnPatched = false) =>
    [
        bcnPatched && !caps.Has(GpuFeature.TextureCompressionBC) ? new("BCn", true, "via BCeNabler") : new("BCn", caps.Has(GpuFeature.TextureCompressionBC)),
        new("sampleRateShading", caps.Has(GpuFeature.SampleRateShading)),
        new("MSAA 2x/4x", caps.MsaaSamples(2) != 0 && caps.MsaaSamples(4) != 0),
        new("depthClamp", caps.Has(GpuFeature.DepthClamp)),
        new("timestamps", caps.Has(GpuFeature.Timestamps)),
        new("rasterization order", caps.Has(GpuFeature.RasterizationOrderColor)),
        new("wideLines", caps.Has(GpuFeature.WideLines)),
    ];

    /// <summary>The active quirks: real workarounds first ("Contornos ativos"), then the ones that change nothing
    /// ("Observados, sem contorno").</summary>
    public static IReadOnlyList<GpuQuirkLine> Quirks(GpuCapabilities caps) =>
        Enum.GetValues<GpuQuirk>().Where(q => q != GpuQuirk.None && caps.Has(q))
            .Select(q => new GpuQuirkLine(q, Describe(q), IsWorkaround(q)))
            .OrderBy(l => l.Workaround ? 0 : 1).ToArray();

    static bool IsWorkaround(GpuQuirk q) => q is GpuQuirk.TileBased or GpuQuirk.DynamicIndexLowering or GpuQuirk.StaticExtendedDynamicState
        or GpuQuirk.StaticDepthBiasEnable or GpuQuirk.PipelineCachePerDriverBuild or GpuQuirk.Msaa2xAs4x or GpuQuirk.ConservativeMemoryBudget;

    static string Describe(GpuQuirk q) => q switch
    {
        GpuQuirk.TileBased => "load/store escolhidos por cena: a profundidade não volta para a memória quando a cena termina",
        GpuQuirk.DynamicIndexLowering => "índices dinâmicos de registradores viram switch no shader",
        GpuQuirk.StaticExtendedDynamicState => "cull, topologia, profundidade e stencil gravados no pipeline",
        GpuQuirk.StaticDepthBiasEnable => "depth bias sempre ligado no pipeline, zero quando o draw não usa",
        GpuQuirk.PipelineCachePerDriverBuild => "um cache de pipelines por build do driver (drivers customizados)",
        GpuQuirk.Msaa2xAs4x => "cenas MSAA 2x desenhadas com 4 amostras",
        GpuQuirk.ConservativeMemoryBudget => "texturas liberadas a partir de 50% do heap",
        GpuQuirk.DynamicRenderingSuspect => "registrado para relatórios de bug",
        GpuQuirk.TurnipDriver => "driver Mesa: nenhum contorno do driver proprietário",
        GpuQuirk.CustomDriverCapable => "aceita drivers customizados",
        GpuQuirk.AvoidPushDescriptors => "o renderizador já não usa push descriptors",
        GpuQuirk.AvoidClearAttachments => "o renderizador já limpa com load ops",
        _ => q.ToString(),
    };
}
