using Android.App;
using Android.Content;
using Android.Provider;
using Microsoft.Win32.SafeHandles;
using VitaSharp.Core.Util;
using AUri = Android.Net.Uri;

namespace VitaSharp.Android.Services;

/// <summary>A document the user picked with the Storage Access Framework.</summary>
public sealed record PickedDocument(AUri Uri, string DisplayName, long Size)
{
    /// <summary>
    /// The document as a seekable read-only stream: ContentResolver.OpenFileDescriptor, its fd wrapped in a FileStream.
    /// Local documents (the downloads, the SD card) are real files behind the descriptor, so a .PUP or .VPK of several
    /// gigabytes is read in place, never copied. A provider that only streams (cloud) gives a pipe: then the document is
    /// copied into the app's cache first (<paramref name="cacheDir"/>).
    /// </summary>
    public Stream OpenSeekable(string cacheDir)
    {
        var resolver = AppServices.AppContext.ContentResolver!;
        var pfd = resolver.OpenFileDescriptor(Uri, "r") ?? throw new IOException($"cannot open {DisplayName}");
        var fs = new FileStream(new SafeFileHandle(pfd.DetachFd(), ownsHandle: true), FileAccess.Read, 1 << 16);
        pfd.Close();
        if (fs.CanSeek) return fs;
        Directory.CreateDirectory(cacheDir);
        string tmp = Path.Combine(cacheDir, "import-" + Guid.NewGuid().ToString("N"));
        Log.Info($"SAF: {DisplayName} is not seekable: copying it to {tmp}");
        // the copy deletes itself when the caller closes it
        var copy = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        using (fs) fs.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }
}

/// <summary>
/// The Storage Access Framework picker (ACTION_OPEN_DOCUMENT) for the firmware (.PUP), VPKs and GPU driver packages.
/// No storage permission is needed: the app reads only what the user picks. The result arrives in
/// MainActivity.OnActivityResult.
/// </summary>
public static class Saf
{
    const int RequestBase = 0x5600;
    static int _next;
    static readonly Dictionary<int, TaskCompletionSource<PickedDocument?>> Pending = new();

    /// <summary>Opens the picker. <paramref name="mimeTypes"/>: e.g. "application/octet-stream" (a .PUP has no
    /// registered type, so the picker shows every file), "application/zip".</summary>
    public static Task<PickedDocument?> PickAsync(params string[] mimeTypes)
    {
        var activity = MainActivity.Current ?? throw new InvalidOperationException("no activity");
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeTypes.Length == 1 ? mimeTypes[0] : "*/*");
        if (mimeTypes.Length > 1) intent.PutExtra(Intent.ExtraMimeTypes, mimeTypes);
        int code;
        var tcs = new TaskCompletionSource<PickedDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Pending)
        {
            code = RequestBase + (_next++ & 0xFF);
            Pending[code] = tcs;
        }
        activity.StartActivityForResult(intent, code);
        return tcs.Task;
    }

    public static void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        TaskCompletionSource<PickedDocument?>? tcs;
        lock (Pending)
        {
            if (!Pending.Remove(requestCode, out tcs)) return;
        }
        if (resultCode != Result.Ok || data?.Data is not { } uri) { tcs.TrySetResult(null); return; }
        try { tcs.TrySetResult(Describe(uri)); }
        catch (Exception e)
        {
            Log.Warn($"SAF: {uri}: {e.Message}");
            tcs.TrySetResult(new PickedDocument(uri, uri.LastPathSegment ?? "arquivo", -1));
        }
    }

    static PickedDocument Describe(AUri uri)
    {
        string name = uri.LastPathSegment ?? "arquivo";
        long size = -1;
        using var c = AppServices.AppContext.ContentResolver!.Query(uri, [IOpenableColumns.DisplayName, IOpenableColumns.Size], null, null, null);
        if (c != null && c.MoveToFirst())
        {
            if (!c.IsNull(0)) name = c.GetString(0) ?? name;
            if (!c.IsNull(1)) size = c.GetLong(1);
        }
        return new PickedDocument(uri, name, size);
    }
}
