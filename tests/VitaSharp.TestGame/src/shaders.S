/*
 * Embeds the precompiled vita2d shader binaries (third_party/vita2d-shaders, MIT licence).
 * The Makefile passes -Wa,-I<that folder> so .incbin finds them.
 */
    .section .rodata

    .macro EMBED_GXP name
    .balign 16
    .global gxp_\name
gxp_\name:
    .incbin "\name\().gxp"
    .endm

    EMBED_GXP clear_v
    EMBED_GXP clear_f
    EMBED_GXP color_v
    EMBED_GXP color_f
    EMBED_GXP texture_v
    EMBED_GXP texture_f
    EMBED_GXP texture_tint_f
