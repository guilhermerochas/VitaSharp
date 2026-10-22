using System.Text.Json;
using System.Text.Json.Serialization;
using VitaSharp.Core.Hle.Modules;

namespace VitaSharp.Core.Util;

/// <summary>
/// Source-generated System.Text.Json metadata for the small JSON stores the HLE keeps on the host (trophies, registry,
/// trigger events): reflection-based serialization is not available under Native AOT / trimming.
/// </summary>
[JsonSerializable(typeof(Dictionary<int, ulong>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SortedDictionary<string, string>))]
[JsonSerializable(typeof(SortedDictionary<int, TriggerUtil.Event>))]
internal sealed partial class CoreJson : JsonSerializerContext
{
    /// <summary>Same output as JsonSerializer with WriteIndented = true.</summary>
    public static CoreJson Indented { get; } = new(new JsonSerializerOptions { WriteIndented = true });
}
