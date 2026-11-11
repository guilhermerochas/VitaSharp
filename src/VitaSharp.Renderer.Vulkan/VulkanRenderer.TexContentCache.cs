using Silk.NET.Vulkan;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// Images of texture contents a texture no longer shows, kept by content: a texture whose memory goes back to a content
/// it had (Killzone's streamed textures switch between a few contents: ~1.6 of its ~2.1 uploads per frame in gameplay
/// re-upload a content uploaded before) takes that image back instead of decoding, transcoding and uploading it again.
/// A texture starts keeping images from its second content change on (one that changed once may never change again), the
/// cache holds at most <see cref="ContentCacheBudget"/> bytes (least recently kept go first) and is emptied when GPU
/// memory runs out. The key is the content hash plus everything else the image depends on except the address (format,
/// size, type, mips and LOD min, stride, palette, gamma), so another texture with the same layout and content may take
/// it too. VITASHARP_TEX_CONTENT_CACHE=0 turns it off (A/B).
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    static readonly bool ContentCacheOn = Environment.GetEnvironmentVariable(EnvVars.TexContentCache) != "0";
    const ulong ContentCacheBudget = 48ul << 20;
    /// <summary>Larger textures are uploaded in place as before (one image would take much of the budget).</summary>
    const uint ContentCacheMaxTexture = 4u << 20;

    readonly record struct ContentKey(ulong Hash, uint Format, uint Width, uint Height, uint Type, uint Mips, uint Stride, uint Palette, bool Gamma);

    sealed class KeptImage
    {
        public Image Image;
        public ImageView View;
        public ImageMemoryPool.Allocation Mem;
        public long Order;
    }

    readonly Dictionary<ContentKey, KeptImage> _contentCache = new();
    ulong _contentCacheBytes;
    long _contentOrder, _texContentHits;

    static ContentKey ContentKeyOf(TexKey k, ulong hash) => new(hash, k.Format, k.Width, k.Height, k.Type, k.Mips, k.Stride, k.Palette, k.Gamma);

    /// <summary>The kept image of <paramref name="hash"/> for this key's layout, removed from the cache (now the caller's).</summary>
    (Image, ImageView, ImageMemoryPool.Allocation)? TakeCachedContent(TexKey key, ulong hash)
    {
        if (_contentCache.Count == 0 || !_contentCache.Remove(ContentKeyOf(key, hash), out var k))
        {
            return null;
        }

        _contentCacheBytes -= k.Mem.Size;
        return (k.Image, k.View, k.Mem);
    }

    /// <summary>Keeps an image the texture stops showing under its content <paramref name="hash"/> (0: unknown content,
    /// the image goes); evicts the oldest kept images over the budget.</summary>
    void KeepContent(TexKey key, ulong hash, Image image, ImageView view, ImageMemoryPool.Allocation mem, uint bytes)
    {
        var ck = ContentKeyOf(key, hash);
        if (hash == 0 || bytes > ContentCacheMaxTexture || _contentCache.ContainsKey(ck))
        {
            ReleaseImage(image, view, mem, now: false);
            return;
        }
        _contentCache[ck] = new KeptImage { Image = image, View = view, Mem = mem, Order = ++_contentOrder };
        _contentCacheBytes += mem.Size;
        while (_contentCacheBytes > ContentCacheBudget && _contentCache.Count > 0)
        {
            var oldest = default(KeyValuePair<ContentKey, KeptImage>);
            foreach (var kv in _contentCache)
            {
                if (oldest.Value == null || kv.Value.Order < oldest.Value.Order)
                {
                    oldest = kv;
                }
            }

            _contentCache.Remove(oldest.Key);
            _contentCacheBytes -= oldest.Value.Mem.Size;
            ReleaseImage(oldest.Value.Image, oldest.Value.View, oldest.Value.Mem, now: false);
        }
    }

    /// <summary>Releases every kept image (out of GPU memory, renderer shutdown); returns the bytes freed.</summary>
    ulong FlushContentCache(bool now)
    {
        ulong freed = _contentCacheBytes;
        foreach (var k in _contentCache.Values)
        {
            ReleaseImage(k.Image, k.View, k.Mem, now);
        }

        _contentCache.Clear();
        _contentCacheBytes = 0;
        return freed;
    }
}
