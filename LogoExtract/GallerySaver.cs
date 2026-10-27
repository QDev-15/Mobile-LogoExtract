using Android.Content;
using Android.OS;
using Android.Provider;

namespace LogoExtract;

/// <summary>Saves a PNG directly into the device's public Pictures/LogoExtract folder via MediaStore, so it
/// shows up in Gallery/Files immediately -- a real "save to device" path, independent of whichever app (if
/// any) the user picks from the system share sheet. Added 2026-10-27: ExportAsync's Share.Default.RequestAsync
/// only ever offered the share sheet, and the owner found no reliable way to actually save a copy on-device
/// from there (some share targets just preview the file, a few require sign-in/network, none guarantee a local
/// copy) -- this gives a direct, always-available save that needs no other app installed.</summary>
public static class GallerySaver
{
    /// <summary>Returns the saved content Uri's string form on success, or null if the write failed. Safe to
    /// call on any thread; does the actual I/O synchronously since callers already run it inside a
    /// <c>Task.Run</c>.</summary>
    public static string? SaveImagePng(byte[] pngBytes, string displayName)
    {
        ContentResolver? resolver = Android.App.Application.Context.ContentResolver;
        if (resolver == null) return null;

        var values = new ContentValues();
        values.Put(MediaStore.MediaColumns.DisplayName, displayName);
        values.Put(MediaStore.MediaColumns.MimeType, "image/png");
        // RelativePath only exists from API 29 (scoped storage); below that, MediaStore.Images.Media's legacy
        // insert still lands in the shared Pictures directory without it, just not in our own subfolder.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            values.Put(MediaStore.MediaColumns.RelativePath, "Pictures/LogoExtract");

        global::Android.Net.Uri? collection = MediaStore.Images.Media.ExternalContentUri;
        global::Android.Net.Uri? uri = resolver.Insert(collection!, values);
        if (uri == null) return null;

        using System.IO.Stream? outStream = resolver.OpenOutputStream(uri);
        if (outStream == null) return null;
        outStream.Write(pngBytes, 0, pngBytes.Length);
        return uri.ToString();
    }
}
