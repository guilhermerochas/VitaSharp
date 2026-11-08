namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The member accessors of libpaf's larger classes, at the offsets libpaf 3.74 keeps the members: image::Image (its
/// decoder state behind +0x2C: mode +4, CLUT mode +8, order +0xC, CLUT size +0x10, format +0x44, pitch alignment
/// +0x50; the cancel callback and its data at +0x40/+0x44 of the image), job::JobItem (cancel flag +0x18), Framework
/// (environment +0x4C, mode +0x54, sound +0x6C), the scroll and list views' block counters and inner widgets, the
/// surfaces' bits-per-pixel shift (+0xC) and the empty answers of the file list's source base classes.
/// </summary>
public sealed partial class Paf
{
    uint ImageInfo(uint me) => U(me + 0x2C);

    [Hle("_ZNK3paf5image5Image9GetFormatEv")] uint ImageFormat(uint me) => U(ImageInfo(me) + 0x44);
    [Hle("_ZNK3paf5image5Image8GetOrderEv")] uint ImageOrder(uint me) => U(ImageInfo(me) + 0xC);
    [Hle("_ZNK3paf5image5Image7GetModeEv")] uint ImageMode(uint me) => U(ImageInfo(me) + 4);
    [Hle("_ZNK3paf5image5Image13GetPitchAlignEv")] uint ImagePitchAlign(uint me) => H(ImageInfo(me) + 0x50);
    [Hle("_ZNK3paf5image5Image11GetCLUTSizeEv")] uint ImageClutSize(uint me) => U(ImageInfo(me) + 0x10);
    [Hle("_ZNK3paf5image5Image11GetCLUTModeEv")] uint ImageClutMode(uint me) => U(ImageInfo(me) + 8);

    /// <summary>(bool (*fn)(void *), void *data): the decoder polls fn(data) to stop early; always accepted.</summary>
    [Hle("_ZN3paf5image5Image19SetDecodeCancelFuncEPFbPvES2_")]
    int ImageSetCancel(uint me, uint fn, uint data) { U(me + 0x40, fn); U(me + 0x44, data); return 1; }

    [Hle("_ZN3paf3job7JobItem6CancelEv")] void JobItemCancel(uint me) => B(me + 0x18, 1);
    [Hle("_ZNK3paf3job7JobItem10IsCanceledEv")] bool JobItemIsCanceled(uint me) => B(me + 0x18) != 0;

    [Hle("_ZN3paf6Plugin17TemplateOpenParamC1Ev")] uint TemplateOpenParamCtor(uint me) { U(me, 0); U(me + 4, 0); return me; }

    [Hle("_ZN3paf9Framework7GetModeEv")] uint FrameworkMode(uint me) => U(me + 0x54);
    [Hle("_ZN3paf9Framework22GetEnvironmentInstanceEv")] uint FrameworkEnvironment(uint me) => U(me + 0x4C);
    [Hle("_ZN3paf9Framework16GetSoundInstanceEv")] uint FrameworkSound(uint me) => U(me + 0x6C);

    /// <summary>(pixels): the byte count of that many pixels at the surface's bits-per-pixel (1 &lt;&lt; shift), rounded up.</summary>
    [Hle("_ZNK3paf5graph11SurfaceBase13PixelsToBytesEi")] int SurfacePixelsToBytes(uint me, int pixels) => ((pixels << (int)U(me + 0xC)) + 7) >> 3;

    /// <summary>Ends a shader object's parameter pass: the bound program's two cursors go back to 0.</summary>
    [Hle("_ZN3paf5graph3arl16VertShaderObject3endEv")]
    [Hle("_ZN3paf5graph3arl16FragShaderObject3endEv")]
    void ShaderObjectEnd(uint me) { uint p = U(me + 8); U(p + 0xC, 0); U(p + 0x10, 0); }

    [Hle("_ZN3paf2ui14ScrollViewBase19BlockUpdateViewPortEv")] void ScrollBlockViewport(uint me) => U(me + 0x39C, U(me + 0x39C) + 1);
    [Hle("_ZN3paf2ui14ScrollViewBase17GetInternalWidgetEv")] uint ScrollInternalWidget(uint me) => U(me + 0x290);
    [Hle("_ZN3paf2ui8ListView15GetGeneratedNumEv")] uint ListGeneratedNum(uint me) => U(U(me + 0x434) + 4);
    [Hle("_ZN3paf2ui8ListView9BlockSyncEv")] void ListBlockSync(uint me) => U(me + 0x420, U(me + 0x420) + 1);
    [Hle("_ZN3paf2ui8ListView13GetSegmentNumEv")] uint ListSegmentNum(uint me) => U(U(me + 0x428) + 4);
    [Hle("_ZNK3paf2ui8listview4Cell15GetSegmentIndexEv")] uint CellSegmentIndex(uint me) => U(U(U(me) + 0x10) + 4);
    [Hle("_ZN3paf2ui11ProgressBar12IsSignalAnimEv")] bool ProgressIsSignalAnim(uint me) => B(me + 0x2EB) != 0;

    [Hle("_ZN3paf2ui8filelist14CellFocusParamC1Ev")] uint CellFocusParamCtor(uint me) { U(me, 0xFFFFFFFF); U(me + 0xC, 0); return me; }

    /// <summary>The base sources have no items: id -1 (64-bit), no visualizer or cell source.</summary>
    [Hle("_ZN3paf2ui8filelist10CellSource5GetIDEi")]
    [Hle("_ZN3paf2ui8filelist13SegmentSource5GetIDEi")]
    long SourceGetId(uint me, int index) => -1;

    [Hle("_ZN3paf2ui8filelist10CellSource13GetVisualizerERNS1_19VisualizerBindParamE")]
    void CellSourceVisualizer(uint ret, uint me, uint param) => U(ret, 0);

    [Hle("_ZN3paf2ui8filelist13SegmentSource13GetCellSourceEix")]
    void SegmentCellSource(uint ret, uint me, int index, long id) => U(ret, 0);
}
