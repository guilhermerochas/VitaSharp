// Locals and stackalloc buffers are not zero-initialised (no prolog zeroing in hot paths such as the NEON helpers,
// HLE info structs and per-draw descriptor arrays). Every stackalloc in this assembly is written before it is read
// or cleared explicitly (Span.Clear) where a partially filled buffer is copied out.
[module: System.Runtime.CompilerServices.SkipLocalsInit]
