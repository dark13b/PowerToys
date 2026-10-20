// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace QuickConvert;

/// <summary>
/// Converts files using only the codecs that ship with Windows:
/// WIC for images, Media Foundation for audio/video and Windows.Data.Pdf for PDF pages.
/// </summary>
public static class FileConverter
{
    public enum Kind
    {
        None,
        Image,
        Video,
        Audio,
        Pdf,
    }

    private static readonly string[] ImageInputs = ["png", "jpg", "bmp", "gif", "tiff", "ico", "heic", "heif", "webp", "jxr", "avif", "dng"];
    private static readonly string[] VideoInputs = ["mp4", "mov", "m4v", "wmv", "avi", "mkv", "3gp", "webm"];
    private static readonly string[] AudioInputs = ["mp3", "wav", "m4a", "wma", "flac", "aac"];

    public static Kind GetKind(string path)
    {
        var ext = Normalize(Path.GetExtension(path));
        return ImageInputs.Contains(ext) ? Kind.Image
            : VideoInputs.Contains(ext) ? Kind.Video
            : AudioInputs.Contains(ext) ? Kind.Audio
            : ext == "pdf" ? Kind.Pdf
            : Kind.None;
    }

    /// <summary>Gets the formats (lower-case extensions) a file can be converted to, excluding its own.</summary>
    public static string[] GetTargets(string path)
    {
        string[] targets = GetKind(path) switch
        {
            Kind.Image => ["png", "jpg", "bmp", "tiff", "gif"],
            Kind.Video => ["mp4", "wmv", "avi", "mp3", "m4a", "wav"],
            Kind.Audio => ["mp3", "m4a", "wav", "wma", "flac"],
            Kind.Pdf => ["png", "jpg"],
            _ => [],
        };

        var own = Normalize(Path.GetExtension(path));
        return targets.Where(t => t != own).ToArray();
    }

    /// <summary>Writes the converted file next to the source and returns its path.</summary>
    public static async Task<string> ConvertAsync(string path, string target)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
        var name = Path.GetFileNameWithoutExtension(path);

        return GetKind(path) switch
        {
            Kind.Image => await ConvertImageAsync(path, await NewFileAsync(folder, name, target), target),
            Kind.Video or Kind.Audio => await TranscodeAsync(path, await NewFileAsync(folder, name, target), target),
            Kind.Pdf => await RenderPdfAsync(path, folder, name, target),
            _ => throw new NotSupportedException(path),
        };
    }

    private static string Normalize(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "jpeg" or "jpe" or "jfif" => "jpg",
        "tif" => "tiff",
        var other => other,
    };

    private static Task<StorageFile> NewFileAsync(StorageFolder folder, string name, string extension)
        => folder.CreateFileAsync($"{name}.{extension}", CreationCollisionOption.GenerateUniqueName).AsTask();

    private static Guid GetEncoderId(string extension) => extension switch
    {
        "png" => BitmapEncoder.PngEncoderId,
        "jpg" => BitmapEncoder.JpegEncoderId,
        "bmp" => BitmapEncoder.BmpEncoderId,
        "tiff" => BitmapEncoder.TiffEncoderId,
        "gif" => BitmapEncoder.GifEncoderId,
        _ => throw new NotSupportedException(extension),
    };

    private static async Task<string> ConvertImageAsync(string path, StorageFile output, string target)
    {
        try
        {
            var source = await StorageFile.GetFileFromPathAsync(path);
            using var input = await source.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(input);
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            var pixels = pixelData.DetachPixelData();

            if (target is "jpg" or "bmp")
            {
                // No alpha channel in these formats: flatten onto white instead of black.
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte inverseAlpha = (byte)(255 - pixels[i + 3]);
                    pixels[i] += inverseAlpha;
                    pixels[i + 1] += inverseAlpha;
                    pixels[i + 2] += inverseAlpha;
                    pixels[i + 3] = 255;
                }
            }

            using var stream = await output.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(GetEncoderId(target), stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                decoder.OrientedPixelWidth,
                decoder.OrientedPixelHeight,
                decoder.DpiX,
                decoder.DpiY,
                pixels);
            await encoder.FlushAsync();
            return output.Path;
        }
        catch
        {
            await output.DeleteAsync();
            throw;
        }
    }

    private static async Task<string> TranscodeAsync(string path, StorageFile output, string target)
    {
        var profile = target switch
        {
            "mp4" => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto),
            "wmv" => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto),
            "avi" => MediaEncodingProfile.CreateAvi(VideoEncodingQuality.Auto),
            "mp3" => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
            "m4a" => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High),
            "wav" => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High),
            "wma" => MediaEncodingProfile.CreateWma(AudioEncodingQuality.High),
            "flac" => MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High),
            _ => throw new NotSupportedException(target),
        };

        try
        {
            var source = await StorageFile.GetFileFromPathAsync(path);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareFileTranscodeAsync(source, output, profile);
            if (!prepared.CanTranscode)
            {
                throw new InvalidOperationException($"Cannot convert {path} to {target}: {prepared.FailureReason}");
            }

            await prepared.TranscodeAsync();
            return output.Path;
        }
        catch
        {
            await output.DeleteAsync();
            throw;
        }
    }

    private static async Task<string> RenderPdfAsync(string path, StorageFolder folder, string name, string target)
    {
        var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        string? first = null;
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            // One image per page: "name.png" for single-page documents, "name-1.png", "name-2.png"... otherwise.
            var output = await NewFileAsync(folder, pdf.PageCount == 1 ? name : $"{name}-{i + 1}", target);
            using var page = pdf.GetPage(i);
            using var stream = await output.OpenAsync(FileAccessMode.ReadWrite);
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                // 2x the page's 96-DPI size keeps text crisp.
                DestinationWidth = (uint)(page.Size.Width * 2),
                BitmapEncoderId = GetEncoderId(target),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            });
            first ??= output.Path;
        }

        return first ?? throw new InvalidOperationException($"{path} has no pages");
    }
}
