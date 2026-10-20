// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

using ManagedCommon;
using Microsoft.PowerToys.Common.UI.Controls.Window;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.ApplicationModel.Resources;
using PowerToys.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;

namespace QuickConvert;

/// <summary>
/// The format wheel. Pressing Shift while dragging files (for example out of File Explorer or the
/// desktop) opens it under the cursor; dropping the files on a format converts them next to the originals.
/// </summary>
public sealed partial class WheelWindow : TransparentWindow, IDisposable
{
    private const double WindowSize = 400; // DIP; leaves room around the 336 DIP surface for its shadow.
    private const double Center = 168;
    private const double OuterRadius = 160;
    private const double InnerRadius = 64;
    private const double GapDegrees = 1.5;

    private const ulong WmKeyDown = 0x0100;
    private const ulong WmSysKeyDown = 0x0104;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkShift = 0x10;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int SmSwapButton = 23;

    private static readonly ResourceLoader Strings = new("PowerToys.QuickConvert.pri");
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys", "QuickConvert", "settings.json");

    private readonly KeyboardHook _hook;
    private readonly DispatcherQueueTimer _buttonWatch;
    private readonly List<(Path Shape, TextBlock Label)> _sectors = [];

    private List<string> _files = [];
    private string[] _targets = [];
    private int _hovered = -1;
    private int _releasedTicks;
    private bool _visible;
    private bool _busy;
    private bool _liquidGlass;

    public WheelWindow()
    {
        InitializeComponent();
        AppWindow.Title = "Quick Convert";
        IsAlwaysOnTop = true;
        Surface.SubscribeTo(this);

        // The drag ends with a button release that only the drop source sees. Watch the button
        // while the wheel is open so it closes when the files are dropped somewhere else.
        _buttonWatch = DispatcherQueue.CreateTimer();
        _buttonWatch.Interval = TimeSpan.FromMilliseconds(50);
        _buttonWatch.Tick += (_, _) => WatchButton();

        _hook = new KeyboardHook(_ => DispatcherQueue.TryEnqueue(ShowAtCursor), () => true, IsTrigger);
        _hook.Start();
    }

    public void Dispose() => _hook.Dispose();

    // Runs inside the low-level keyboard hook, so it must stay cheap. Returning true swallows
    // the Shift press, which also stops Explorer from switching the drag to "move".
    private bool IsTrigger(KeyboardEvent ev) =>
        ev.message is WmKeyDown or WmSysKeyDown
        && ev.key is VkShift or VkLShift or VkRShift
        && !_visible
        && IsDragButtonDown()
        && IsShellDragInProgress();

    private static bool IsDragButtonDown() =>
        (GetAsyncKeyState(GetSystemMetrics(SmSwapButton) != 0 ? VkRButton : VkLButton) & 0x8000) != 0;

    // The shell shows its drag image window only while something is being dragged, so a Shift
    // press during a box selection or a text selection doesn't open the wheel.
    private static bool IsShellDragInProgress()
    {
        nint dragImage = FindWindow("SysDragImage", null);
        return dragImage != 0 && IsWindowVisible(dragImage);
    }

    private void ShowAtCursor()
    {
        if (_visible || !FlyoutWindowHelper.TryGetDisplayAreaAtCursor(out var display) || display is null || !GetCursorPos(out var cursor))
        {
            return;
        }

        int size = (int)Math.Round(WindowSize * FlyoutWindowHelper.GetDpiScale(display));
        FlyoutWindowHelper.MoveAndResizeOnDisplay(this, display, new RectInt32(cursor.X - (size / 2), cursor.Y - (size / 2), size, size));

        ApplyStyle(IsLiquidGlassEnabled());
        _files = [];
        SetTargets([]);
        SetHub("\uE8B7", Strings.GetString("DropHere"));
        _visible = true;
        _releasedTicks = 0;
        _buttonWatch.Start();
        Show();
    }

    private void Dismiss()
    {
        _buttonWatch.Stop();
        _visible = false;
        Hide();
    }

    private void WatchButton()
    {
        if (_busy || IsDragButtonDown())
        {
            _releasedTicks = 0;
            return;
        }

        // A drop on the wheel arrives right after the release; give it a moment before closing.
        if (++_releasedTicks >= 3)
        {
            Dismiss();
        }
    }

    private async void Root_DragEnter(object sender, DragEventArgs e)
    {
        if (_files.Count > 0 || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var paths = (await e.DataView.GetStorageItemsAsync()).OfType<IStorageFile>().Select(f => f.Path).ToList();
            var kind = paths.Count > 0 ? FileConverter.GetKind(paths[0]) : FileConverter.Kind.None;

            // A mixed selection converts only the files of the first file's kind.
            _files = paths.Where(p => FileConverter.GetKind(p) == kind).ToList();
            SetTargets(kind == FileConverter.Kind.None ? [] : FileConverter.GetTargets(_files[0]));

            var glyph = kind switch
            {
                FileConverter.Kind.Image => "\uE91B",
                FileConverter.Kind.Video => "\uE714",
                FileConverter.Kind.Audio => "\uE8D6",
                FileConverter.Kind.Pdf => "\uE8A5",
                _ => "\uE783",
            };
            var text = kind == FileConverter.Kind.None ? Strings.GetString("Unsupported")
                : _files.Count == 1 ? System.IO.Path.GetFileName(_files[0])
                : string.Format(CultureInfo.CurrentCulture, Strings.GetString("FileCount"), _files.Count);
            SetHub(glyph, text);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        int index = HitTest(e.GetPosition(Ring));
        Highlight(index);
        e.AcceptedOperation = index >= 0 ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (index >= 0)
        {
            e.DragUIOverride.Caption = string.Format(CultureInfo.CurrentCulture, Strings.GetString("ConvertTo"), _targets[index].ToUpperInvariant());
        }
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => Highlight(-1);

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        int index = HitTest(e.GetPosition(Ring));
        if (index < 0 || _files.Count == 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            Dismiss();
            return;
        }

        // Copy, never move: the originals must stay where they are.
        e.AcceptedOperation = DataPackageOperation.Copy;
        var target = _targets[index];
        var files = _files;
        _busy = true;
        Busy.IsActive = true;
        SetHub("\uE895", target.ToUpperInvariant());

        bool failed = false;
        foreach (var file in files)
        {
            try
            {
                await FileConverter.ConvertAsync(file, target);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Converting {file} to {target} failed", ex);
                failed = true;
            }
        }

        Busy.IsActive = false;
        SetHub(failed ? "\uE783" : "\uE73E", Strings.GetString(failed ? "Failed" : "Done"));
        await Task.Delay(700);
        _busy = false;
        Dismiss();
    }

    private void SetHub(string glyph, string text)
    {
        HubIcon.Glyph = glyph;
        HubText.Text = text;
    }

    private int HitTest(Point point)
    {
        double dx = point.X - Center;
        double dy = point.Y - Center;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (_targets.Length == 0 || distance < InnerRadius || distance > OuterRadius + 8)
        {
            return -1;
        }

        // Degrees clockwise from 12 o'clock, shifted so sector 0 is centered at the top.
        double span = 360.0 / _targets.Length;
        double angle = ((Math.Atan2(dy, dx) * 180 / Math.PI) + 90 + (span / 2) + 360) % 360;
        return Math.Min((int)(angle / span), _targets.Length - 1);
    }

    private void SetTargets(string[] targets)
    {
        _targets = targets;
        _hovered = -1;
        _sectors.Clear();
        Ring.Children.Clear();

        double span = 360.0 / Math.Max(targets.Length, 1);
        for (int i = 0; i < targets.Length; i++)
        {
            double start = (i * span) - (span / 2) + (GapDegrees / 2);
            double end = start + span - GapDegrees;

            var figure = new PathFigure { StartPoint = PointAt(OuterRadius, start), IsClosed = true };
            figure.Segments.Add(new ArcSegment { Point = PointAt(OuterRadius, end), Size = new Size(OuterRadius, OuterRadius), SweepDirection = SweepDirection.Clockwise, IsLargeArc = span > 180 });
            figure.Segments.Add(new LineSegment { Point = PointAt(InnerRadius, end) });
            figure.Segments.Add(new ArcSegment { Point = PointAt(InnerRadius, start), Size = new Size(InnerRadius, InnerRadius), SweepDirection = SweepDirection.Counterclockwise, IsLargeArc = span > 180 });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            var label = targets[i].ToUpperInvariant();
            var shape = new Path { Data = geometry };
            AutomationProperties.SetName(shape, string.Format(CultureInfo.CurrentCulture, Strings.GetString("ConvertTo"), label));

            var labelPosition = PointAt((OuterRadius + InnerRadius) / 2, i * span);
            var text = new TextBlock { Text = label, Width = 72, TextAlignment = TextAlignment.Center, FontSize = 15, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
            Canvas.SetLeft(text, labelPosition.X - 36);
            Canvas.SetTop(text, labelPosition.Y - 10);

            // Hovered sectors grow slightly out of the ring; ScaleTransition animates the change.
            // Both scale around the ring's center, expressed in each element's own coordinates.
            shape.CenterPoint = new Vector3((float)Center, (float)Center, 0);
            text.CenterPoint = new Vector3((float)(Center - labelPosition.X + 36), (float)(Center - labelPosition.Y + 10), 0);
            shape.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
            text.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
            Ring.Children.Add(shape);
            Ring.Children.Add(text);
            _sectors.Add((shape, text));
            Paint(i, false);
        }
    }

    private void Highlight(int index)
    {
        if (index == _hovered)
        {
            return;
        }

        if (_hovered >= 0)
        {
            Paint(_hovered, false);
        }

        _hovered = index;
        if (index >= 0)
        {
            Paint(index, true);
        }
    }

    private void Paint(int index, bool hovered)
    {
        var (shape, label) = _sectors[index];
        if (_liquidGlass)
        {
            // Clear glass with hairline separators; the hovered sector becomes a brighter glass droplet.
            shape.Fill = hovered ? Brush("GlassDropletBrush") : null;
            shape.Stroke = Brush(hovered ? "GlassDropletStrokeBrush" : "GlassSeparatorBrush");
            label.Foreground = Brush("TextFillColorPrimaryBrush");
        }
        else
        {
            shape.Fill = Brush(hovered ? "AccentFillColorDefaultBrush" : "SubtleFillColorSecondaryBrush");
            shape.Stroke = null;
            label.Foreground = Brush(hovered ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        }

        var scale = hovered ? new Vector3(1.05f, 1.05f, 1) : Vector3.One;
        shape.Scale = scale;
        label.Scale = scale;
    }

    private void ApplyStyle(bool liquidGlass)
    {
        if (liquidGlass == _liquidGlass)
        {
            return;
        }

        _liquidGlass = liquidGlass;

        // A null Style falls back to the standard Fluent TransientSurface look.
        Surface.Style = liquidGlass ? (Style)Root.Resources["LiquidGlassSurfaceStyle"] : null;
        HubCircle.Fill = Brush(liquidGlass ? "GlassHubBrush" : "CardBackgroundFillColorDefaultBrush");
        HubCircle.Stroke = Brush(liquidGlass ? "GlassRimBrush" : "CardStrokeColorDefaultBrush");
    }

    // ponytail: read straight from settings.json until the module has a Settings page that writes it.
    private static bool IsLiquidGlassEnabled()
    {
        try
        {
            using var settings = JsonDocument.Parse(System.IO.File.ReadAllText(SettingsPath));
            return settings.RootElement.GetProperty("properties").GetProperty("wheel_style").GetProperty("value").GetString() == "liquid-glass";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private Brush Brush(string key) =>
        (Brush)(Root.Resources.TryGetValue(key, out var local) ? local : Application.Current.Resources[key]);

    private static Point PointAt(double radius, double degreesFromTop)
    {
        double radians = (degreesFromTop - 90) * Math.PI / 180;
        return new Point(Center + (radius * Math.Cos(radians)), Center + (radius * Math.Sin(radians)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint lpPoint);
}
