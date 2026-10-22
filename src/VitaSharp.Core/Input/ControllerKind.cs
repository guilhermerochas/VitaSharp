using System.Globalization;

namespace VitaSharp.Core.Input;

public enum ControllerKind { Generic, Xbox, PlayStation, Nintendo, Keyboard }

/// <summary>
/// Recognises the family of a game controller from its SDL-format GUID (what GLFW reports: bus, vendor and product
/// ids as little-endian 16-bit fields, or "xinput" for XInput devices on Windows) and falls back to its name.
/// </summary>
public static class ControllerClassifier
{
    public const ushort VendorSony = 0x054C, VendorMicrosoft = 0x045E, VendorNintendo = 0x057E;

    /// <summary>Vendor and product ids encoded in an SDL GUID, or null when it has none (XInput, name hashes).</summary>
    public static (ushort Vendor, ushort Product)? VendorProduct(string? guid)
    {
        if (guid is not { Length: 32 } || IsXInput(guid)) return null;
        // SDL 2 layout: bus16 crc16 vendor16 zero16 product16 zero16 version16 driver8 info8 (little-endian fields)
        if (!TryHex16(guid, 8, out var vendor) || !TryHex16(guid, 16, out var product)) return null;
        if (!TryHex16(guid, 12, out var z1) || z1 != 0 || !TryHex16(guid, 20, out var z2) || z2 != 0) return null;
        if (vendor == 0) return null;
        return (vendor, product);
    }

    static bool IsXInput(string guid) => guid.StartsWith("78696e707574", StringComparison.OrdinalIgnoreCase); // "xinput"

    static bool TryHex16(string s, int pos, out ushort value)
    {
        value = 0;
        if (!byte.TryParse(s.AsSpan(pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lo)
            || !byte.TryParse(s.AsSpan(pos + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hi)) return false;
        value = (ushort)(lo | hi << 8);
        return true;
    }

    public static ControllerKind Classify(string? guid, string? name)
    {
        if (guid != null && IsXInput(guid)) return ControllerKind.Xbox;
        if (VendorProduct(guid) is { } vp)
        {
            switch (vp.Vendor)
            {
                case VendorSony: return ControllerKind.PlayStation;
                case VendorMicrosoft: return ControllerKind.Xbox;
                case VendorNintendo: return ControllerKind.Nintendo;
            }
        }
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("xbox") || n.Contains("xinput") || n.Contains("x-box")) return ControllerKind.Xbox;
        if (n.Contains("dualshock") || n.Contains("dualsense") || n.Contains("playstation") || n.Contains("ps3") || n.Contains("ps4")
            || n.Contains("ps5") || n.Contains("sony") || n == "wireless controller") return ControllerKind.PlayStation;
        if (n.Contains("nintendo") || n.Contains("switch") || n.Contains("joy-con") || n.Contains("pro controller")) return ControllerKind.Nintendo;
        return ControllerKind.Generic;
    }

    public static string KindLabel(ControllerKind k) => k switch
    {
        ControllerKind.Xbox => "Xbox",
        ControllerKind.PlayStation => "PlayStation (DualShock / DualSense)",
        ControllerKind.Nintendo => "Nintendo (Switch Pro / Joy-Con)",
        ControllerKind.Keyboard => "Keyboard",
        _ => "Generic controller",
    };
}
