using System.Text.RegularExpressions;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// paf::Url (0x50 bytes: { vtable, parsed URI element +4, flags +0x30, common::String text +0x38 }): the text is kept
/// and its parts (scheme, user, password, host, port, path, query, anchor) are read from it on demand. The port is
/// the URL's, else the scheme's default (80 http, 443 https, 21 ftp), else 0.
/// </summary>
public sealed partial class Paf
{
    static readonly Regex UrlParts = new(@"^(?:([A-Za-z][A-Za-z0-9+.\-]*):)?(?://(?:([^:@/]*)(?::([^@/]*))?@)?(\[[^\]]*\]|[^:/?#]*)(?::(\d*))?)?([^?#]*)(?:\?([^#]*))?(?:#(.*))?$", RegexOptions.CultureInvariant);

    uint UrlVtable => VTable("Url", 0x269A36D7, 0xD19653CB);

    [Hle("_ZN3paf3UrlC1Ev")]
    [Hle("_ZN3paf3UrlC2Ev")]
    uint UrlCtor(uint me)
    {
        Memory.GuestMemory.Fill(me + 4, 0, 0x34);
        U(me, UrlVtable);
        CStrCtor(me + 0x38);
        return me;
    }

    [Hle("_ZN3paf3UrlC1ERKS0_")]
    [Hle("_ZN3paf3UrlC2ERKS0_")]
    uint UrlCopy(uint me, uint other) { UrlCtor(me); StrAssign(me + 0x38, UrlText(other)); return me; }

    [Hle("_ZN3paf3UrlC1ERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEE")]
    [Hle("_ZN3paf3UrlC2ERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEE")]
    uint UrlFromString(uint me, uint s) { UrlCtor(me); StrAssign(me + 0x38, StrRead(s)); return me; }

    [Hle("_ZN3paf3UrlC1ERKNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEE")]
    [Hle("_ZN3paf3UrlC2ERKNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEE")]
    uint UrlFromWString(uint me, uint s) { UrlCtor(me); StrAssign(me + 0x38, WStrRead(s)); return me; }

    [Hle("_ZN3paf3UrlD1Ev")]
    [Hle("_ZN3paf3UrlD2Ev")]
    uint UrlDtor(uint me) { CStrDtor(me + 0x38); return me; }

    [Hle("_ZN3paf3UrlD0Ev")] void UrlDelete(uint me) { UrlDtor(me); Free(me); }

    [Hle("_ZN3paf3UrlaSERKS0_")] uint UrlAssign(uint me, uint other) { StrAssign(me + 0x38, UrlText(other)); WStrAssign(me + 0x44, ""); return me; }
    [Hle("_ZN3paf3UrlaSERKNS_12basic_stringIcNS_11char_traitsIcEENS_9allocatorIcEEEE")] uint UrlAssignS(uint me, uint s) { StrAssign(me + 0x38, StrRead(s)); WStrAssign(me + 0x44, ""); return me; }
    [Hle("_ZN3paf3UrlaSERKNS_12basic_stringIwNS_11char_traitsIwEENS_9allocatorIwEEEE")] uint UrlAssignW(uint me, uint s) { StrAssign(me + 0x38, WStrRead(s)); WStrAssign(me + 0x44, ""); return me; }

    string UrlText(uint me) => CStrText(me + 0x38);
    Match UrlMatch(uint me) => UrlParts.Match(UrlText(me));

    string UrlPart(uint me, int group) => UrlMatch(me) is { Success: true } m ? m.Groups[group].Value : "";

    void UrlOut(uint ret, string s, bool wide) { StrInit(ret, wide); if (wide) WStrAssign(ret, s); else StrAssign(ret, s); }

    static int DefaultPort(string scheme) => scheme.ToLowerInvariant() switch { "http" => 80, "https" => 443, "ftp" => 21, _ => 0 };

    string UrlBase(uint me)
    {
        var m = UrlMatch(me);
        if (!m.Success) return "";
        string path = m.Groups[6].Value;
        int slash = path.LastIndexOf('/');
        string dir = slash >= 0 ? path[..(slash + 1)] : "/";
        string auth = m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? "//" + m.Groups[4].Value + (m.Groups[5].Value.Length > 0 ? ":" + m.Groups[5].Value : "") : "";
        return (m.Groups[1].Value.Length > 0 ? m.Groups[1].Value + ":" : "") + auth + dir;
    }

    string UrlRelative(uint me)
    {
        var m = UrlMatch(me);
        if (!m.Success) return "";
        return m.Groups[6].Value + (m.Groups[7].Success ? "?" + m.Groups[7].Value : "") + (m.Groups[8].Success ? "#" + m.Groups[8].Value : "");
    }

    [Hle("_ZNK3paf3Url12GetUrlStringEv")] void UrlGetUrl(uint ret, uint me) => UrlOut(ret, UrlText(me), false);
    [Hle("_ZNK3paf3Url13GetUrlWStringEv")] void UrlGetUrlW(uint ret, uint me) => UrlOut(ret, UrlText(me), true);
    [Hle("_ZNK3paf3Url16GetBaseUrlStringEv")] void UrlGetBase(uint ret, uint me) => UrlOut(ret, UrlBase(me), false);
    [Hle("_ZNK3paf3Url17GetBaseUrlWStringEv")] void UrlGetBaseW(uint ret, uint me) => UrlOut(ret, UrlBase(me), true);
    [Hle("_ZNK3paf3Url15GetSchemeStringEv")] void UrlGetScheme(uint ret, uint me) => UrlOut(ret, UrlPart(me, 1), false);
    [Hle("_ZNK3paf3Url16GetSchemeWStringEv")] void UrlGetSchemeW(uint ret, uint me) => UrlOut(ret, UrlPart(me, 1), true);
    [Hle("_ZNK3paf3Url13GetUserStringEv")] void UrlGetUser(uint ret, uint me) => UrlOut(ret, UrlPart(me, 2), false);
    [Hle("_ZNK3paf3Url14GetUserWStringEv")] void UrlGetUserW(uint ret, uint me) => UrlOut(ret, UrlPart(me, 2), true);
    [Hle("_ZNK3paf3Url17GetPasswordStringEv")] void UrlGetPassword(uint ret, uint me) => UrlOut(ret, UrlPart(me, 3), false);
    [Hle("_ZNK3paf3Url18GetPasswordWStringEv")] void UrlGetPasswordW(uint ret, uint me) => UrlOut(ret, UrlPart(me, 3), true);
    [Hle("_ZNK3paf3Url13GetHostStringEv")] void UrlGetHost(uint ret, uint me) => UrlOut(ret, UrlPart(me, 4), false);
    [Hle("_ZNK3paf3Url14GetHostWStringEv")] void UrlGetHostW(uint ret, uint me) => UrlOut(ret, UrlPart(me, 4), true);
    [Hle("_ZNK3paf3Url13GetPathStringEv")] void UrlGetPath(uint ret, uint me) => UrlOut(ret, UrlPart(me, 6), false);
    [Hle("_ZNK3paf3Url14GetPathWStringEv")] void UrlGetPathW(uint ret, uint me) => UrlOut(ret, UrlPart(me, 6), true);
    [Hle("_ZNK3paf3Url14GetQueryStringEv")] void UrlGetQuery(uint ret, uint me) => UrlOut(ret, UrlPart(me, 7), false);
    [Hle("_ZNK3paf3Url15GetQueryWStringEv")] void UrlGetQueryW(uint ret, uint me) => UrlOut(ret, UrlPart(me, 7), true);
    [Hle("_ZNK3paf3Url15GetAnchorStringEv")] void UrlGetAnchor(uint ret, uint me) => UrlOut(ret, UrlPart(me, 8), false);
    [Hle("_ZNK3paf3Url16GetAnchorWStringEv")] void UrlGetAnchorW(uint ret, uint me) => UrlOut(ret, UrlPart(me, 8), true);
    [Hle("_ZNK3paf3Url21GetRelativePathStringEv")] void UrlGetRelative(uint ret, uint me) => UrlOut(ret, UrlRelative(me), false);
    [Hle("_ZNK3paf3Url22GetRelativePathWStringEv")] void UrlGetRelativeW(uint ret, uint me) => UrlOut(ret, UrlRelative(me), true);

    /// <summary>The ";"-parameters of the last path segment.</summary>
    string UrlParams(uint me) { string p = UrlPart(me, 6); int i = p.IndexOf(';', Math.Max(0, p.LastIndexOf('/'))); return i >= 0 ? p[(i + 1)..] : ""; }
    [Hle("_ZNK3paf3Url18GetParamaterStringEv")] void UrlGetParams(uint ret, uint me) => UrlOut(ret, UrlParams(me), false);
    [Hle("_ZNK3paf3Url19GetParamaterWStringEv")] void UrlGetParamsW(uint ret, uint me) => UrlOut(ret, UrlParams(me), true);

    [Hle("_ZNK3paf3Url7GetPortEv")]
    int UrlGetPort(uint me)
    {
        var m = UrlMatch(me);
        if (!m.Success) return 0;
        return int.TryParse(m.Groups[5].Value, out int p) ? p : DefaultPort(m.Groups[1].Value);
    }
}
