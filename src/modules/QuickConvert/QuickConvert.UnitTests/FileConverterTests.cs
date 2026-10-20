// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace QuickConvert.UnitTests;

[TestClass]
public sealed class FileConverterTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void Setup() => _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "QuickConvertTests", Guid.NewGuid().ToString())).FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void GetTargetsExcludesTheSourceFormat()
    {
        CollectionAssert.DoesNotContain(FileConverter.GetTargets("photo.png"), "png");
        CollectionAssert.DoesNotContain(FileConverter.GetTargets("photo.JPEG"), "jpg");
        CollectionAssert.Contains(FileConverter.GetTargets("clip.mp4"), "mp3");
        CollectionAssert.DoesNotContain(FileConverter.GetTargets("clip.mp4"), "mp4");
        Assert.AreEqual(0, FileConverter.GetTargets("notes.docx").Length);
    }

    [TestMethod]
    public async Task ConvertingTransparentPngToJpgFlattensOntoWhite()
    {
        var source = Path.Combine(_directory, "dot.png");
        await WriteTransparentPngAsync(source);

        var output = await FileConverter.ConvertAsync(source, "jpg");

        Assert.AreEqual(Path.Combine(_directory, "dot.jpg"), output);
        var file = await StorageFile.GetFileFromPathAsync(output);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.AreEqual(BitmapDecoder.JpegDecoderId, decoder.DecoderInformation.CodecId);
        var pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();
        Assert.IsTrue(pixels[0] > 245 && pixels[1] > 245 && pixels[2] > 245, "Transparent pixels should become white, not black.");
    }

    [TestMethod]
    public async Task ConvertingNeverOverwritesAnExistingFile()
    {
        var source = Path.Combine(_directory, "dot.png");
        await WriteTransparentPngAsync(source);
        File.WriteAllText(Path.Combine(_directory, "dot.bmp"), "keep me");

        var output = await FileConverter.ConvertAsync(source, "bmp");

        Assert.AreNotEqual(Path.Combine(_directory, "dot.bmp"), output);
        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(_directory, "dot.bmp")));
    }

    private static async Task WriteTransparentPngAsync(string path)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
        var file = await folder.CreateFileAsync(Path.GetFileName(path));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 2, 2, 96, 96, new byte[16]);
        await encoder.FlushAsync();
    }
}
