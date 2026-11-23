using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The GXM calls the Piglet driver (libScePiglet, <see cref="Piglet"/>) makes. Piglet is a client of libgxm on the Vita
/// (its draws, scenes and display flips are GXM work), so its GLES2 state is turned into the same calls a game makes:
/// contexts, render targets, surfaces, the shader patcher, uniform buffers, textures and draws all go through this
/// module's own implementation and reach the renderer the same way.
/// </summary>
public sealed partial class Gxm
{
    internal sealed class PigletApi(Gxm g)
    {
        static Ptr<uint> P(uint a) => new(a);
        public bool Initialized => g._initialized;
        public int Initialize(uint p) => g.sceGxmInitializeThroughEntry(p);
        public int CreateContext(uint p, uint outCtx) => g.sceGxmCreateContextThroughEntry(p, P(outCtx));
        public int DestroyContext(uint ctx) => g.sceGxmDestroyContextThroughEntry(ctx);
        public int CreateRenderTarget(uint p, uint outRt) => g.sceGxmCreateRenderTargetThroughEntry(p, P(outRt));
        public int DestroyRenderTarget(uint rt) => g.sceGxmDestroyRenderTargetThroughEntry(rt);
        public int SyncObjectCreate(uint outSync) => g.sceGxmSyncObjectCreateThroughEntry(P(outSync));
        public int SyncObjectDestroy(uint sync) => g.sceGxmSyncObjectDestroyThroughEntry(sync);
        public int ColorSurfaceInit(uint s, uint fmt, uint w, uint h, uint stride, uint data) => g.sceGxmColorSurfaceInitThroughEntry(s, fmt, 0, 0, 0, w, h, stride, data);
        public int DepthStencilSurfaceInit(uint s, uint fmt, uint type, uint strideSamples, uint depth, uint stencil) => g.sceGxmDepthStencilSurfaceInitThroughEntry(s, fmt, type, strideSamples, depth, stencil);
        public void DepthStencilForce(uint s, bool load, bool store) { g.sceGxmDepthStencilSurfaceSetForceLoadModeThroughEntry(s, load ? 2u : 0); g.sceGxmDepthStencilSurfaceSetForceStoreModeThroughEntry(s, store ? 4u : 0); }
        public void DepthStencilBackground(uint s, float depth, uint stencil) { g.sceGxmDepthStencilSurfaceSetBackgroundDepth(s, depth); g.sceGxmDepthStencilSurfaceSetBackgroundStencilThroughEntry(s, stencil); }
        public int BeginScene(uint ctx, uint rt, uint fragmentSync, uint color, uint depth) => g.sceGxmBeginSceneThroughEntry(ctx, 0, rt, 0, 0, fragmentSync, color, depth);
        public int EndScene(uint ctx) => g.sceGxmEndSceneThroughEntry(ctx, 0, 0);
        public int DisplayQueueAddEntry(uint oldSync, uint newSync, uint data) => g.sceGxmDisplayQueueAddEntryThroughEntry(oldSync, newSync, data);
        public int DisplayQueueFinish() => g.sceGxmDisplayQueueFinishThroughEntry();
        public void Finish(uint ctx) => g.sceGxmFinishThroughEntry(ctx);
        public int ShaderPatcherCreate(uint p, uint outPatcher) => g.sceGxmShaderPatcherCreateThroughEntry(p, P(outPatcher));
        public int RegisterProgram(uint patcher, uint program, uint outId) => g.sceGxmShaderPatcherRegisterProgramThroughEntry(patcher, program, P(outId));
        public int UnregisterProgram(uint patcher, uint id) => g.sceGxmShaderPatcherUnregisterProgramThroughEntry(patcher, id);
        public int CreateVertexProgram(uint patcher, uint id, uint attrs, uint attrCount, uint streams, uint streamCount, uint outVp)
            => g.sceGxmShaderPatcherCreateVertexProgramThroughEntry(patcher, id, attrs, attrCount, streams, streamCount, P(outVp));
        public int CreateFragmentProgram(uint patcher, uint id, uint outputFormat, uint blendInfo, uint vp, uint outFp)
            => g.sceGxmShaderPatcherCreateFragmentProgramThroughEntry(patcher, id, outputFormat, 0, blendInfo, vp, P(outFp));
        public int ReleaseVertexProgram(uint patcher, uint vp) => g.sceGxmShaderPatcherReleaseVertexProgramThroughEntry(patcher, vp);
        public int ReleaseFragmentProgram(uint patcher, uint fp) => g.sceGxmShaderPatcherReleaseFragmentProgramThroughEntry(patcher, fp);
        public void SetVertexProgram(uint ctx, uint vp) => g.sceGxmSetVertexProgramThroughEntry(ctx, vp);
        public void SetFragmentProgram(uint ctx, uint fp) => g.sceGxmSetFragmentProgramThroughEntry(ctx, fp);
        public int SetVertexStream(uint ctx, uint index, uint data) => g.sceGxmSetVertexStreamThroughEntry(ctx, index, data);
        public int SetFragmentTexture(uint ctx, uint index, uint tex) => g.sceGxmSetFragmentTextureThroughEntry(ctx, index, tex);
        public int SetVertexTexture(uint ctx, uint index, uint tex) => g.sceGxmSetVertexTextureThroughEntry(ctx, index, tex);
        public int ReserveVertexDefaultUniformBuffer(uint ctx, uint outBuf) => g.sceGxmReserveVertexDefaultUniformBufferThroughEntry(ctx, P(outBuf));
        public int ReserveFragmentDefaultUniformBuffer(uint ctx, uint outBuf) => g.sceGxmReserveFragmentDefaultUniformBufferThroughEntry(ctx, P(outBuf));
        public int SetUniformDataF(uint buffer, uint param, uint offset, uint count, uint src) => g.sceGxmSetUniformDataFThroughEntry(buffer, param, offset, count, src);
        public int TextureInit(uint t, uint data, uint fmt, uint w, uint h, uint mips, uint type) => type switch
        {
            TexSwizzled => g.sceGxmTextureInitSwizzledThroughEntry(t, data, fmt, w, h, mips),
            TexSwizzledArbitrary => g.sceGxmTextureInitSwizzledArbitraryThroughEntry(t, data, fmt, w, h, mips),
            TexCube => g.sceGxmTextureInitCubeThroughEntry(t, data, fmt, w, h, mips),
            TexCubeArbitrary => g.sceGxmTextureInitCubeArbitraryThroughEntry(t, data, fmt, w, h, mips),
            _ => g.sceGxmTextureInitLinearThroughEntry(t, data, fmt, w, h, mips),
        };
        public void TextureSampling(uint t, uint min, uint mag, uint mip, uint u, uint v, uint lodBias)
        {
            g.sceGxmTextureSetMinFilterThroughEntry(t, min);
            g.sceGxmTextureSetMagFilterThroughEntry(t, mag);
            g.sceGxmTextureSetMipFilterThroughEntry(t, mip);
            g.sceGxmTextureSetUAddrModeThroughEntry(t, u);
            g.sceGxmTextureSetVAddrModeThroughEntry(t, v);
            g.sceGxmTextureSetLodBiasThroughEntry(t, lodBias);
        }
        public void SetCullMode(uint ctx, uint mode) => g.sceGxmSetCullModeThroughEntry(ctx, mode);
        public void SetTwoSided(uint ctx, bool on) => g.sceGxmSetTwoSidedEnableThroughEntry(ctx, on ? 0x800u : 0);
        public void SetDepthFunc(uint ctx, uint f) { g.sceGxmSetFrontDepthFuncThroughEntry(ctx, f); g.sceGxmSetBackDepthFuncThroughEntry(ctx, f); }
        public void SetDepthWrite(uint ctx, bool on) { g.sceGxmSetFrontDepthWriteEnableThroughEntry(ctx, on ? 0 : 0x100000u); g.sceGxmSetBackDepthWriteEnableThroughEntry(ctx, on ? 0 : 0x100000u); }
        public void SetFrontStencil(uint ctx, uint func, uint sf, uint df, uint dp, uint cmp, uint wr, uint reference)
        { g.sceGxmSetFrontStencilFuncThroughEntry(ctx, func, sf, df, dp, cmp, wr); g.sceGxmSetFrontStencilRefThroughEntry(ctx, reference); }
        public void SetBackStencil(uint ctx, uint func, uint sf, uint df, uint dp, uint cmp, uint wr, uint reference)
        { g.sceGxmSetBackStencilFuncThroughEntry(ctx, func, sf, df, dp, cmp, wr); g.sceGxmSetBackStencilRefThroughEntry(ctx, reference); }
        public void SetPolygonMode(uint ctx, uint mode) { g.sceGxmSetFrontPolygonModeThroughEntry(ctx, mode); g.sceGxmSetBackPolygonModeThroughEntry(ctx, mode); }
        public void SetDepthBias(uint ctx, int factor, int units) { g.sceGxmSetFrontDepthBiasThroughEntry(ctx, factor, units); g.sceGxmSetBackDepthBiasThroughEntry(ctx, factor, units); }
        public void SetPointLineWidth(uint ctx, uint w) { g.sceGxmSetFrontPointLineWidthThroughEntry(ctx, w); g.sceGxmSetBackPointLineWidthThroughEntry(ctx, w); }
        public void SetFragmentProgramEnable(uint ctx, bool on) { g.sceGxmSetFrontFragmentProgramEnableThroughEntry(ctx, on ? 0 : 0x200000u); g.sceGxmSetBackFragmentProgramEnableThroughEntry(ctx, on ? 0 : 0x200000u); }
        public void SetViewport(uint ctx, float xo, float xs, float yo, float ys, float zo, float zs) => g.sceGxmSetViewport(ctx, xo, xs, yo, ys, zo, zs);
        public void SetRegionClip(uint ctx, uint mode, uint x0, uint y0, uint x1, uint y1) => g.sceGxmSetRegionClipThroughEntry(ctx, mode, x0, y0, x1, y1);
        public int Draw(uint ctx, uint prim, uint indexType, uint indices, uint count) => g.sceGxmDrawThroughEntry(ctx, prim, indexType, indices, count);
        public int DrawInstanced(uint ctx, uint prim, uint indexType, uint indices, uint count, uint wrap) => g.sceGxmDrawInstancedThroughEntry(ctx, prim, indexType, indices, count, wrap);
        public int TransferFill(uint color, uint fmt, uint dst, uint x, uint y, uint w, uint h, int stride) => g.sceGxmTransferFillThroughEntry(color, fmt, dst, x, y, w, h, stride, 0, 0, 0);
    }

    [SaveState.SnapshotIgnore] PigletApi? _piglet;
    internal PigletApi ForPiglet => _piglet ??= new PigletApi(this);
}
