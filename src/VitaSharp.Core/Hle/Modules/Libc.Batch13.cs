using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// libc.suprx 3.74 (SceLibstdcxx) helpers of HLE batch 13, read in the module.
public sealed partial class Libc
{
    static string RegexErrorText(int code) => code switch
    {
        0 => "regex_error(error_collate): The expression contained an invalid collating element name.",
        1 => "regex_error(error_ctype): The expression contained an invalid character class name.",
        2 => "regex_error(error_escape): The expression contained an invalid escaped character, or a trailing escape.",
        3 => "regex_error(error_backref): The expression contained an invalid back reference.",
        4 => "regex_error(error_brack): The expression contained mismatched [ and ].",
        5 => "regex_error(error_paren): The expression contained mismatched ( and ).",
        6 => "regex_error(error_brace): The expression contained mismatched { and }.",
        7 => "regex_error(error_badbrace): The expression contained an invalid range in a { expression }.",
        8 => "regex_error(error_range): The expression contained an invalid character range, such as [b-a] in most encodings.",
        9 => "regex_error(error_space): There was insufficient memory to convert the expression into a finite state machine.",
        10 => "regex_error(error_badrepeat): One of *?+{ was not preceded by a valid regular expression.",
        11 => "regex_error(error_complexity): The complexity of an attempted match against a regular expression exceeded a pre-set level.",
        12 => "regex_error(error_stack): There was insufficient memory to determine whether the regular expression could match the specified character sequence.",
        13 => "regex_error(error_parse)",
        14 => "regex_error(error_syntax)",
        _ => "regex_error",
    };

    /// <summary>std::_Xregex_error(regex_constants::error_type code): throws a regex_error (0x24 bytes: runtime_error
    /// with the module's text for the code, the code at +0x20).</summary>
    [Hle]
    void _ZSt13_Xregex_errorNSt15regex_constants10error_typeE(HleContext ctx, int code)
    {
        uint obj = AllocateException(ExcRegexError.Size);
        Construct(obj, ExcRegexError, cstr: ConstString(RegexErrorText(code)));
        GuestMemory.W32(obj + 0x20, (uint)code);
        Throw(ctx, obj, TiOf(ExcRegexError), ExportStubOfSlot(ExcRegexError, 0));
    }
}

public sealed partial class Libc
{
    readonly Dictionary<Loader.LoadedModule, (uint Top, uint End)> _exidxOverride = new();

    /// <summary>__set_exidx_main(const void *exidx_start, const void *exidx_end): the ARM EHABI exception index table
    /// (.ARM.exidx, 8-byte entries sorted by function) the unwinder uses for the calling module's code, set by the
    /// program's start-up code instead of the one in its module info. A NULL or empty range goes back to the module
    /// info's table.</summary>
    [Hle]
    unsafe void __set_exidx_main(HleContext ctx, uint start, uint end)
    {
        uint lr = ctx.Cpu->R[14] & ~1u;
        var m = K.Modules.Values.FirstOrDefault(x => x.Segments.Values.Any(s => lr - s.Address < s.Size));
        if (m == null) return;
        lock (_exidxOverride)
        {
            if (start == 0 || end <= start) _exidxOverride.Remove(m);
            else _exidxOverride[m] = (start, end);
        }
    }
}

public sealed partial class Libc
{
    /// <summary>std::_Rethrow_future_exception(exception_ptr p) (by value: the pointer word *p), libc.suprx 3.74's
    /// sequence: __cxa_rethrow_primary_exception on the object (the by-value copy's extra reference is dropped by
    /// the frame's cleanup as the exception leaves it, so the count ends where it started), std::terminate for a
    /// NULL pointer.</summary>
    [Hle("_ZSt25_Rethrow_future_exceptionSt13exception_ptr")]
    void RethrowFutureException(HleContext ctx, uint p)
    {
        uint obj = p != 0 ? GuestMemory.R32(p) : 0;
        if (obj == 0) { TerminateNow(); return; }
        __cxa_rethrow_primary_exception(ctx, obj);
    }
}
