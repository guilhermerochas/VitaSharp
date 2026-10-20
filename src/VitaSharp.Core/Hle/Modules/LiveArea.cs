using System.Xml;
using VitaSharp.Core.Io;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceLiveAreaUtil: replaces / updates the title's LiveArea (drawn by SceShell on hardware). There is no shell here:
/// requests are validated like FW 3.60 (psp2/livearea.h), complete immediately and only update a frame table (frame id
/// → revision and user data per layer) seeded from the title's sce_sys LiveArea template, so the revision and user-data
/// queries answer coherently. Only one asynchronous request may be pending per process; its result is returned once by
/// sceLiveAreaGetStatus.
/// </summary>
public sealed class LiveArea : HleModule
{
    internal const int ERR_PENDING = unchecked((int)0x80104001), ERR_INVALID_ARG = unchecked((int)0x80104002),
        ERR_PATH_NOT_RESOLVED = unchecked((int)0x80104003), ERR_PARSE = unchecked((int)0x80104004),
        ERR_XML_TOO_LARGE = unchecked((int)0x80104009), ERR_UNAVAILABLE = unchecked((int)0x8010400A),
        ERR_PATH_NOT_PERMITTED = unchecked((int)0x8010400B), ERR_IPMI_TOO_LARGE = unchecked((int)0x80020588);
    internal const int MaxPathLen = 255, MaxXmlSize = 10239, MaxIpmiRequest = 0x3F00, MaxUserData = 1024;
    const string FormatVersion = "01.00";

    internal sealed class Frame
    {
        public ulong Revision;
        public string UserData = "";
    }

    readonly object _lock = new();
    int? _pending;
    ulong _revision;
    /// <summary>Frames per layer (0 normal, 1 retail), by id; null until seeded from the template.</summary>
    Dictionary<string, Frame>[]? _frames;

    /// <summary>Test hooks: VFS and title id used instead of the emulator's.</summary>
    internal Vfs? VfsOverride { get; set; }
    internal string? TitleIdOverride { get; set; }
    Vfs Vfs => VfsOverride ?? Emu.Vfs;
    string TitleId => TitleIdOverride ?? Emu.TitleId;

    // ------------------------------------------------------------------ frame table

    Dictionary<string, Frame>[] Frames()
    {
        if (_frames != null) return _frames;
        _frames = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
        LoadTemplate("app0:sce_sys/livearea/contents", 0, replace: false);
        LoadTemplate("app0:sce_sys/retail/livearea/contents", 1, replace: false);
        return _frames;
    }

    /// <summary>Reads template.xml of a LiveArea contents directory: content-rev of &lt;livearea&gt; and every
    /// &lt;frame id&gt;. Malformed or missing templates leave the table as it is.</summary>
    void LoadTemplate(string contentsPath, int layer, bool replace)
    {
        var host = Vfs.Resolve(contentsPath.TrimEnd('/') + "/template.xml");
        if (host == null || !File.Exists(host)) return;
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(File.ReadAllText(host));
            if (replace) _frames![layer].Clear();
            if (doc.DocumentElement?.GetAttribute("content-rev") is { Length: > 0 } rev && ulong.TryParse(rev, out var r))
                _revision = Math.Max(_revision, r);
            foreach (XmlElement f in doc.GetElementsByTagName("frame"))
                if (f.GetAttribute("id") is { Length: > 0 } id) _frames![layer][id] = ParseFrame(f, null);
        }
        catch (XmlException e) { Log.Debug($"LiveArea: {contentsPath}/template.xml: {e.Message}"); }
    }

    static Frame ParseFrame(XmlElement f, Frame? previous)
    {
        var frame = new Frame
        {
            Revision = ulong.TryParse(f.GetAttribute("rev"), out var r) ? r : previous != null ? previous.Revision + 1 : 0,
            UserData = previous?.UserData ?? "",
        };
        if (f.GetElementsByTagName("user-data") is { Count: > 0 } ud) frame.UserData = ud[0]!.InnerText;
        return frame;
    }

    /// <summary>Parses a frame document (root or first &lt;frame&gt; element with an id). Null when unparsable.</summary>
    internal static (string Id, XmlElement Element)? ParseFrameXml(string xml)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);
            var f = doc.DocumentElement?.Name == "frame" ? doc.DocumentElement : doc.GetElementsByTagName("frame").OfType<XmlElement>().FirstOrDefault();
            if (f == null || f.GetAttribute("id") is not { Length: > 0 } id) return null;
            return (id, f);
        }
        catch (XmlException) { return null; }
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Path policy of the implicit-title exports: the source must be under app0: or savedata0:, and an app0:
    /// path below /sce_* only the LiveArea contents directories.</summary>
    internal static bool IsPermittedImplicitPath(string path)
    {
        var (dev, rel) = Vfs.Split(path);
        if (dev.Equals("savedata0", StringComparison.OrdinalIgnoreCase)) return true;
        if (!dev.Equals("app0", StringComparison.OrdinalIgnoreCase)) return false;
        rel = rel.TrimEnd('/');
        if (!rel.StartsWith("sce_", StringComparison.OrdinalIgnoreCase)) return true;
        return rel.Equals("sce_sys/livearea/contents", StringComparison.OrdinalIgnoreCase)
            || rel.Equals("sce_sys/retail/livearea/contents", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads contents_path: null when NULL or longer than 255 characters.</summary>
    static string? ReadPath(uint ptr)
    {
        if (ptr == 0) return null;
        var s = GuestMemory.ReadCString(ptr, MaxPathLen + 2);
        return s.Length > MaxPathLen ? null : s;
    }

    /// <summary>Applies the source-path policy (implicit title only) and resolves the directory.</summary>
    int CheckSource(string path, bool implicitTitle, bool mustExist)
    {
        if (implicitTitle && !IsPermittedImplicitPath(path)) return ERR_PATH_NOT_PERMITTED;
        if (!mustExist) return 0;
        string resolved = Emu is not null ? Emu.Hle.Modules.OfType<Fios2User>().FirstOrDefault()?.Resolve(path) ?? path : path;
        var host = Vfs.Resolve(resolved);
        return host != null && Directory.Exists(host) ? 0 : ERR_PATH_NOT_RESOLVED;
    }

    bool IsCurrentTitle(string? titleId) => string.IsNullOrEmpty(titleId) || titleId.Equals(TitleId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Submits an asynchronous request: BUSY if one is pending, else stores the completed result.</summary>
    int Submit(Func<int> op)
    {
        lock (_lock)
        {
            if (_pending != null) return ERR_PENDING;
            _pending = op();
            return 0;
        }
    }

    // ------------------------------------------------------------------ status

    [Hle]
    int sceLiveAreaGetStatus()
    {
        lock (_lock)
        {
            if (_pending is not int r) return ERR_PENDING;
            _pending = null;
            return r;
        }
    }

    // ------------------------------------------------------------------ replace all

    int ReplaceAll(string path, bool implicitTitle, string? titleId)
    {
        int r = CheckSource(path, implicitTitle, mustExist: true);
        if (r < 0) { Log.Debug($"sceLiveAreaReplaceAll('{path}') = 0x{r:X8}"); return r; }
        Log.Info($"sceLiveAreaReplaceAll('{path}'{(titleId is { Length: > 0 } ? $", {titleId}" : "")})");
        if (!IsCurrentTitle(titleId)) return 0;
        lock (_lock)
        {
            Frames();
            _revision++;
            // the replacement data is one layer; it carries the retail frames when it comes from the retail directory
            int layer = path.Contains("retail", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            LoadTemplate(path, layer, replace: true);
        }
        return 0;
    }

    [Hle]
    int sceLiveAreaReplaceAllSync(uint contentsPath) =>
        ReadPath(contentsPath) is { } p ? ReplaceAll(p, true, null) : ERR_INVALID_ARG;

    [Hle]
    int sceLiveAreaReplaceAllAsync(uint contentsPath) =>
        ReadPath(contentsPath) is { } p ? Submit(() => ReplaceAll(p, true, null)) : ERR_INVALID_ARG;

    [Hle(Nid = 0xB0571F88, Library = "SceLiveAreaUtil")]
    int sceLiveAreaReplaceAllForTitleSync(uint contentsPath, uint titleId, uint lastModified) =>
        ReadPath(contentsPath) is { } p ? ReplaceAll(p, false, GuestMemory.ReadCString(titleId, 16)) : ERR_INVALID_ARG;

    [Hle(Nid = 0xEF2A3C3D, Library = "SceLiveAreaUtil")]
    int sceLiveAreaReplaceAllForTitleAsync(uint contentsPath, uint titleId, uint lastModified)
    {
        if (ReadPath(contentsPath) is not { } p) return ERR_INVALID_ARG;
        string title = GuestMemory.ReadCString(titleId, 16);
        return Submit(() => ReplaceAll(p, false, title));
    }

    // ------------------------------------------------------------------ frame updates

    /// <summary>Argument checks done before the request is sent (returned directly also by the async variants).</summary>
    static int CheckFrameArgs(uint formatVersion, uint contentsPath, int targetType, out string? path)
    {
        path = ReadPath(contentsPath);
        if (formatVersion == 0 || GuestMemory.ReadCString(formatVersion, 16) != FormatVersion || path == null
            || targetType is not (0 or 1)) return ERR_INVALID_ARG;
        return 0;
    }

    /// <summary>Reads one frame document: length &lt; 0 means NUL-terminated.</summary>
    static int ReadFrameXml(uint xmlPtr, int length, out string xml)
    {
        xml = "";
        if (xmlPtr == 0) return ERR_INVALID_ARG;
        if (length < 0)
        {
            xml = GuestMemory.ReadCString(xmlPtr, MaxXmlSize + 1);
            return System.Text.Encoding.UTF8.GetByteCount(xml) > MaxXmlSize ? ERR_XML_TOO_LARGE : 0;
        }
        if (length > MaxXmlSize) return ERR_XML_TOO_LARGE;
        xml = System.Text.Encoding.UTF8.GetString(GuestMemory.Span(xmlPtr, length)).TrimEnd('\0');
        return 0;
    }

    /// <summary>Parses and applies the frame documents (the part SceShell does).</summary>
    int ApplyFrames(IReadOnlyList<string> xmls, string path, int targetType, bool implicitTitle, string? titleId)
    {
        int r = CheckSource(path, implicitTitle, mustExist: false);
        if (r < 0) return r;
        var parsed = new List<(string Id, XmlElement Element)>();
        foreach (var x in xmls)
        {
            if (ParseFrameXml(x) is not { } f) { Log.Debug("sceLiveAreaUpdateFrame: unparsable frame XML"); return ERR_PARSE; }
            parsed.Add(f);
        }
        Log.Info($"sceLiveAreaUpdateFrame: {string.Join(", ", parsed.Select(p => p.Id))} ({(targetType == 1 ? "retail" : "normal")}, '{path}')");
        if (!IsCurrentTitle(titleId)) return 0;
        lock (_lock)
        {
            var layer = Frames()[targetType];
            foreach (var (id, el) in parsed) layer[id] = ParseFrame(el, layer.GetValueOrDefault(id));
            _revision++;
        }
        return 0;
    }

    int UpdateFrame(uint formatVersion, uint frameXml, int frameXmlLength, uint contentsPath, int targetType, bool implicitTitle,
        string? titleId, bool async)
    {
        int r = CheckFrameArgs(formatVersion, contentsPath, targetType, out var path);
        if (r < 0) return r;
        r = ReadFrameXml(frameXml, frameXmlLength, out var xml);
        if (r < 0) return r;
        return async ? Submit(() => ApplyFrames([xml], path!, targetType, implicitTitle, titleId))
            : ApplyFrames([xml], path!, targetType, implicitTitle, titleId);
    }

    [Hle]
    int sceLiveAreaUpdateFrameSync(uint formatVersion, uint frameXml, int frameXmlLength, uint contentsPath, int targetType) =>
        UpdateFrame(formatVersion, frameXml, frameXmlLength, contentsPath, targetType, true, null, false);

    [Hle]
    int sceLiveAreaUpdateFrameAsync(uint formatVersion, uint frameXml, int frameXmlLength, uint contentsPath, int targetType) =>
        UpdateFrame(formatVersion, frameXml, frameXmlLength, contentsPath, targetType, true, null, true);

    [Hle(Nid = 0x009CD8E2, Library = "SceLiveAreaUtil")]
    int sceLiveAreaUpdateFrameForTitleSync(uint formatVersion, uint frameXml, int frameXmlLength, uint contentsPath, int targetType, uint titleId) =>
        UpdateFrame(formatVersion, frameXml, frameXmlLength, contentsPath, targetType, false, GuestMemory.ReadCString(titleId, 16), false);

    [Hle(Nid = 0xC1F7A931, Library = "SceLiveAreaUtil")]
    int sceLiveAreaUpdateFrameForTitleAsync(uint formatVersion, uint frameXml, int frameXmlLength, uint contentsPath, int targetType, uint titleId) =>
        UpdateFrame(formatVersion, frameXml, frameXmlLength, contentsPath, targetType, false, GuestMemory.ReadCString(titleId, 16), true);

    /// <summary>Size of the serialized multi-frame request: each NUL-terminated payload rounded to 16 bytes plus an
    /// 8-byte descriptor per entry (approximation of the FW 3.60 IPMI layout).</summary>
    internal static int FramesRequestSize(IEnumerable<string> xmls) =>
        xmls.Sum(x => ((System.Text.Encoding.UTF8.GetByteCount(x) + 1 + 15) & ~15) + 8);

    int UpdateFrames(uint formatVersion, uint frameXmlArray, uint frameCount, uint contentsPath, int targetType, uint titleIdPtr, bool async)
    {
        int r = CheckFrameArgs(formatVersion, contentsPath, targetType, out var path);
        if (r < 0) return r;
        if (frameXmlArray == 0 || frameCount == 0 || frameCount > MaxIpmiRequest / 8) return ERR_INVALID_ARG;
        var xmls = new List<string>();
        long total = 0;
        for (uint i = 0; i < frameCount; i++)
        {
            uint p = GuestMemory.R32(frameXmlArray + i * 4);
            if (p == 0) return ERR_INVALID_ARG;
            var x = GuestMemory.ReadCString(p, MaxIpmiRequest + 1);
            total += System.Text.Encoding.UTF8.GetByteCount(x);
            if (total > MaxIpmiRequest) return ERR_IPMI_TOO_LARGE;
            xmls.Add(x);
        }
        if (FramesRequestSize(xmls) > MaxIpmiRequest) return ERR_IPMI_TOO_LARGE;
        string titleId = GuestMemory.ReadCString(titleIdPtr, 16);
        // always the ForTitle source-path policy
        return async ? Submit(() => ApplyFrames(xmls, path!, targetType, false, titleId)) : ApplyFrames(xmls, path!, targetType, false, titleId);
    }

    [Hle(Nid = 0x31A5E6AB, Library = "SceLiveAreaUtil")]
    int sceLiveAreaUpdateFramesForTitleSync(uint formatVersion, uint frameXmlArray, uint frameCount, uint contentsPath, int targetType, uint titleId) =>
        UpdateFrames(formatVersion, frameXmlArray, frameCount, contentsPath, targetType, titleId, false);

    [Hle(Nid = 0x876A649B, Library = "SceLiveAreaUtil")]
    int sceLiveAreaUpdateFramesForTitleAsync(uint formatVersion, uint frameXmlArray, uint frameCount, uint contentsPath, int targetType, uint titleId) =>
        UpdateFrames(formatVersion, frameXmlArray, frameCount, contentsPath, targetType, titleId, true);

    // ------------------------------------------------------------------ queries

    [Hle]
    int sceLiveAreaGetRevision(uint revision)
    {
        if (revision == 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            Frames();
            GuestMemory.Write(revision, _revision);
        }
        return 0;
    }

    /// <summary>Retail frames take precedence over normal ones.</summary>
    Frame? FindFrame(string id)
    {
        var f = Frames();
        return f[1].GetValueOrDefault(id) ?? f[0].GetValueOrDefault(id);
    }

    [Hle]
    int sceLiveAreaGetFrameRevision(uint frameId, uint revision)
    {
        if (frameId == 0 || revision == 0) return ERR_INVALID_ARG;
        string id = GuestMemory.ReadCString(frameId, 256);
        if (id.Length == 0) return ERR_INVALID_ARG;
        lock (_lock)
        {
            if (FindFrame(id) is not { } frame) return ERR_UNAVAILABLE;
            GuestMemory.Write(revision, frame.Revision);
        }
        return 0;
    }

    [Hle]
    int sceLiveAreaGetFrameUserData(uint frameId, uint buffer, uint bufferSize)
    {
        if (frameId == 0 || buffer == 0 || bufferSize == 0) return ERR_INVALID_ARG;
        string id = GuestMemory.ReadCString(frameId, 256);
        lock (_lock)
        {
            if (id.Length == 0 || FindFrame(id) is not { } frame) return ERR_UNAVAILABLE;
            GuestMemory.WriteCString(buffer, frame.UserData, (int)Math.Min(bufferSize, (uint)MaxUserData));
        }
        return 0;
    }
}
