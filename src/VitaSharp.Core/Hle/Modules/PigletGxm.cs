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
        public int Initialize(uint p) => g.sceGxmInitialize(p);
        public int CreateContext(uint p, uint outCtx) => g.sceGxmCreateContext(p, P(outCtx));
        public int DestroyContext(uint ctx) => g.sceGxmDestroyContext(ctx);
        public int CreateRenderTarget(uint p, uint outRt) => g.sceGxmCreateRenderTarget(p, P(outRt));
        public int DestroyRenderTarget(uint rt) => g.sceGxmDestroyRenderTarget(rt);
        public int SyncObjectCreate(uint outSync) => g.sceGxmSyncObjectCreate(P(outSync));
        public int SyncObjectDestroy(uint sync) => g.sceGxmSyncObjectDestroy(sync);
        public int ColorSurfaceInit(uint s, uint fmt, uint w, uint h, uint stride, uint data) => g.sceGxmColorSurfaceInit(s, fmt, 0, 0, 0, w, h, stride, data);
        public int DepthStencilSurfaceInit(uint s, uint fmt, uint type, uint strideSamples, uint depth, uint stencil) => g.sceGxmDepthStencilSurfaceInit(s, fmt, type, strideSamples, depth, stencil);
        public void DepthStencilForce(uint s, bool load, bool store) { g.sceGxmDepthStencilSurfaceSetForceLoadMode(s, load ? 2u : 0); g.sceGxmDepthStencilSurfaceSetForceStoreMode(s, store ? 4u : 0); }
        public void DepthStencilBackground(uint s, float depth, uint stencil) { g.sceGxmDepthStencilSurfaceSetBackgroundDepth(s, depth); g.sceGxmDepthStencilSurfaceSetBackgroundStencil(s, stencil); }
        public int BeginScene(uint ctx, uint rt, uint fragmentSync, uint color, uint depth) => g.sceGxmBeginScene(ctx, 0, rt, 0, 0, fragmentSync, color, depth);
        public int EndScene(uint ctx) => g.sceGxmEndScene(ctx, 0, 0);
        public int DisplayQueueAddEntry(uint oldSync, uint newSync, uint data) => g.sceGxmDisplayQueueAddEntry(oldSync, newSync, data);
        public int DisplayQueueFinish() => g.sceGxmDisplayQueueFinish();
        public void Finish(uint ctx) => g.sceGxmFinish(ctx);
        public int ShaderPatcherCreate(uint p, uint outPatcher) => g.sceGxmShaderPatcherCreate(p, P(outPatcher));
        public int RegisterProgram(uint patcher, uint program, uint outId) => g.sceGxmShaderPatcherRegisterProgram(patcher, program, P(outId));
        public int UnregisterProgram(uint patcher, uint id) => g.sceGxmShaderPatcherUnregisterProgram(patcher, id);
        public int CreateVertexProgram(uint patcher, uint id, uint attrs, uint attrCount, uint streams, uint streamCount, uint outVp)
            => g.sceGxmShaderPatcherCreateVertexProgram(patcher, id, attrs, attrCount, streams, streamCount, P(outVp));
        public int CreateFragmentProgram(uint patcher, uint id, uint outputFormat, uint blendInfo, uint vp, uint outFp)
            => g.sceGxmShaderPatcherCreateFragmentProgram(patcher, id, outputFormat, 0, blendInfo, vp, P(outFp));
        public int ReleaseVertexProgram(uint patcher, uint vp) => g.sceGxmShaderPatcherReleaseVertexProgram(patcher, vp);
        public int ReleaseFragmentProgram(uint patcher, uint fp) => g.sceGxmShaderPatcherReleaseFragmentProgram(patcher, fp);
        public void SetVertexProgram(uint ctx, uint vp) => g.sceGxmSetVertexProgram(ctx, vp);
        public void SetFragmentProgram(uint ctx, uint fp) => g.sceGxmSetFragmentProgram(ctx, fp);
        public int SetVertexStream(uint ctx, uint index, uint data) => g.sceGxmSetVertexStream(ctx, index, data);
        public int SetFragmentTexture(uint ctx, uint index, uint tex) => g.sceGxmSetFragmentTexture(ctx, index, tex);
        public int SetVertexTexture(uint ctx, uint index, uint tex) => g.sceGxmSetVertexTexture(ctx, index, tex);
        public int ReserveVertexDefaultUniformBuffer(uint ctx, uint outBuf) => g.sceGxmReserveVertexDefaultUniformBuffer(ctx, P(outBuf));
        public int ReserveFragmentDefaultUniformBuffer(uint ctx, uint outBuf) => g.sceGxmReserveFragmentDefaultUniformBuffer(ctx, P(outBuf));
        public int SetUniformDataF(uint buffer, uint param, uint offset, uint count, uint src) => g.sceGxmSetUniformDataF(buffer, param, offset, count, src);
        public int TextureInit(uint t, uint data, uint fmt, uint w, uint h, uint mips, uint type) => type switch
        {
            TexSwizzled => g.sceGxmTextureInitSwizzled(t, data, fmt, w, h, mips),
            TexSwizzledArbitrary => g.sceGxmTextureInitSwizzledArbitrary(t, data, fmt, w, h, mips),
            TexCube => g.sceGxmTextureInitCube(t, data, fmt, w, h, mips),
            TexCubeArbitrary => g.sceGxmTextureInitCubeArbitrary(t, data, fmt, w, h, mips),
            _ => g.sceGxmTextureInitLinear(t, data, fmt, w, h, mips),
        };
        public void TextureSampling(uint t, uint min, uint mag, uint mip, uint u, uint v, uint lodBias)
        {
            g.sceGxmTextureSetMinFilter(t, min);
            g.sceGxmTextureSetMagFilter(t, mag);
            g.sceGxmTextureSetMipFilter(t, mip);
            g.sceGxmTextureSetUAddrMode(t, u);
            g.sceGxmTextureSetVAddrMode(t, v);
            g.sceGxmTextureSetLodBias(t, lodBias);
        }
        public void SetCullMode(uint ctx, uint mode) => g.sceGxmSetCullMode(ctx, mode);
        public void SetTwoSided(uint ctx, bool on) => g.sceGxmSetTwoSidedEnable(ctx, on ? 0x800u : 0);
        public void SetDepthFunc(uint ctx, uint f) { g.sceGxmSetFrontDepthFunc(ctx, f); g.sceGxmSetBackDepthFunc(ctx, f); }
        public void SetDepthWrite(uint ctx, bool on) { g.sceGxmSetFrontDepthWriteEnable(ctx, on ? 0 : 0x100000u); g.sceGxmSetBackDepthWriteEnable(ctx, on ? 0 : 0x100000u); }
        public void SetFrontStencil(uint ctx, uint func, uint sf, uint df, uint dp, uint cmp, uint wr, uint reference)
        { g.sceGxmSetFrontStencilFunc(ctx, func, sf, df, dp, cmp, wr); g.sceGxmSetFrontStencilRef(ctx, reference); }
        public void SetBackStencil(uint ctx, uint func, uint sf, uint df, uint dp, uint cmp, uint wr, uint reference)
        { g.sceGxmSetBackStencilFunc(ctx, func, sf, df, dp, cmp, wr); g.sceGxmSetBackStencilRef(ctx, reference); }
        public void SetPolygonMode(uint ctx, uint mode) { g.sceGxmSetFrontPolygonMode(ctx, mode); g.sceGxmSetBackPolygonMode(ctx, mode); }
        public void SetDepthBias(uint ctx, int factor, int units) { g.sceGxmSetFrontDepthBias(ctx, factor, units); g.sceGxmSetBackDepthBias(ctx, factor, units); }
        public void SetPointLineWidth(uint ctx, uint w) { g.sceGxmSetFrontPointLineWidth(ctx, w); g.sceGxmSetBackPointLineWidth(ctx, w); }
        public void SetFragmentProgramEnable(uint ctx, bool on) { g.sceGxmSetFrontFragmentProgramEnable(ctx, on ? 0 : 0x200000u); g.sceGxmSetBackFragmentProgramEnable(ctx, on ? 0 : 0x200000u); }
        public void SetViewport(uint ctx, float xo, float xs, float yo, float ys, float zo, float zs) => g.sceGxmSetViewport(ctx, xo, xs, yo, ys, zo, zs);
        public void SetRegionClip(uint ctx, uint mode, uint x0, uint y0, uint x1, uint y1) => g.sceGxmSetRegionClip(ctx, mode, x0, y0, x1, y1);
        public int Draw(uint ctx, uint prim, uint indexType, uint indices, uint count) => g.sceGxmDraw(ctx, prim, indexType, indices, count);
        public int DrawInstanced(uint ctx, uint prim, uint indexType, uint indices, uint count, uint wrap) => g.sceGxmDrawInstanced(ctx, prim, indexType, indices, count, wrap);
        public int TransferFill(uint color, uint fmt, uint dst, uint x, uint y, uint w, uint h, int stride) => g.sceGxmTransferFill(color, fmt, dst, x, y, w, h, stride, 0, 0, 0);
    }

    [SaveState.SnapshotIgnore] PigletApi? _piglet;
    internal PigletApi ForPiglet => _piglet ??= new PigletApi(this);
}
