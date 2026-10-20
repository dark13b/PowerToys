// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

using ManagedCommon;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Color = Windows.UI.Color;

namespace QuickConvert;

/// <summary>
/// Draws the "Liquid Glass" wheel: the screen behind the wheel seen through a glass lens
/// (refraction with chromatic fringes at the bevel), a specular rim, and a glass droplet
/// that magnifies what is under it and slides between formats on a spring.
/// </summary>
/// <remarks>
/// The backdrop is captured once, just before the wheel appears, so the wheel never has to be
/// excluded from screen capture (screenshots and recordings keep showing it).
/// All geometry is in DIPs on the 400 DIP window; the glass disk matches the 336 DIP surface.
/// </remarks>
internal sealed partial class LiquidGlass : IDisposable
{
    private const float Size = 400;
    private const float GlassRadius = 168;
    private const float Bevel = 30;
    private const float HubRadius = 58;
    private const float HubBevel = 16;
    private const float DropletInner = 66;
    private const float DropletOuter = 163;
    private const float MaxShift = 28; // Largest refraction offset, in DIPs.
    private const float LightDegrees = -45; // Light comes from the top-left.

    private static readonly Vector2 Center = new(Size / 2, Size / 2);
    private static readonly byte[] LensMap = BuildLensMap();

    private readonly CanvasControl _canvas;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Bitmap? _capture;
    private CanvasBitmap? _screen;
    private CanvasRenderTarget? _backdrop;
    private CanvasBitmap? _lens;
    private ICanvasImage? _glass;
    private bool _rendering;
    private double _lastTick;

    // Live capture of the window under the wheel (see StartWindowCapture).
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private nint _capturedWindow;
    private RectInt32 _rect;
    private float _dpi;

    // Materialize animation: 0 = gone, 1 = fully formed.
    private float _presence;
    private float _presenceVelocity;
    private float _presenceTarget;
    private Action? _onHidden;

    // Droplet spring, in degrees clockwise from 12 o'clock.
    private float _angle;
    private float _angleVelocity;
    private float _targetAngle;
    private float _span = 90;
    private float _dropletOpacity;
    private bool _hasHover;

    public LiquidGlass(CanvasControl canvas)
    {
        _canvas = canvas;
        _canvas.Draw += OnDraw;
    }

    /// <summary>
    /// Prepares the backdrop for <paramref name="windowRect"/> (physical pixels) while the wheel is
    /// still hidden: a snapshot of the screen, overlaid with a live capture of <paramref name="windowUnder"/>.
    /// </summary>
    /// <remarks>
    /// The screen snapshot also contains the drag thumbnail, which DWM draws over everything, so on
    /// its own the glass would freeze a copy of it. A window capture holds only that window's content
    /// (no drag thumbnail, no wheel) and keeps updating, so the glass refracts what is really behind it.
    /// The snapshot only fills the parts of the wheel outside that window.
    /// </remarks>
    public void Capture(RectInt32 windowRect, double scale, nint windowUnder)
    {
        StopWindowCapture();
        _rect = windowRect;
        _dpi = (float)(96 * scale);
        try
        {
            if (_capture is null || _capture.Width != windowRect.Width || _capture.Height != windowRect.Height)
            {
                _capture?.Dispose();
                _capture = new Bitmap(windowRect.Width, windowRect.Height, PixelFormat.Format32bppArgb);
            }

            using (var graphics = Graphics.FromImage(_capture))
            {
                graphics.CopyFromScreen(windowRect.X, windowRect.Y, 0, 0, _capture.Size, CopyPixelOperation.SourceCopy);
            }

            var data = _capture.LockBits(new Rectangle(0, 0, _capture.Width, _capture.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            _capture.UnlockBits(data);

            var device = CanvasDevice.GetSharedDevice();
            _screen?.Dispose();
            _screen = CanvasBitmap.CreateFromBytes(device, bytes, _capture.Width, _capture.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, _dpi);
            _lens ??= CanvasBitmap.CreateFromBytes(device, LensMap, (int)Size, (int)Size, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
            _backdrop?.Dispose();
            _backdrop = new CanvasRenderTarget(device, Size, Size, _dpi);
            using (var ds = _backdrop.CreateDrawingSession())
            {
                ds.DrawImage(_screen);
            }

            _glass = BuildGlass(_backdrop, _lens);
            StartWindowCapture(windowUnder);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or COMException)
        {
            // No desktop to copy from (locked or remote session) or the GPU went away:
            // draw the rim and droplet over a plain frosted fill instead.
            Logger.LogWarning($"Liquid Glass backdrop capture failed: {ex.Message}");
            _glass = null;
        }

        _hasHover = false;
        _dropletOpacity = 0;
    }

    public void Show()
    {
        _presence = 0;
        _presenceVelocity = 0;
        _presenceTarget = 1;
        _onHidden = null;
        StartRendering();
    }

    /// <summary>Plays the dissolve, then calls <paramref name="onHidden"/>.</summary>
    public void Hide(Action onHidden)
    {
        _presenceTarget = 0;
        _onHidden = onHidden;
        StartRendering();
    }

    /// <summary>Moves the droplet to <paramref name="index"/> of <paramref name="count"/> sectors, or hides it with -1.</summary>
    public void SetHover(int index, int count)
    {
        if (index < 0 || count == 0)
        {
            _hasHover = false;
            StartRendering();
            return;
        }

        float span = 360f / count;
        _targetAngle = index * span;
        if (!_hasHover && _dropletOpacity < 0.05f)
        {
            // Appear in place rather than sweeping in from the last position.
            _angle = _targetAngle;
            _angleVelocity = 0;
        }

        _span = span;
        _hasHover = true;
        StartRendering();
    }

    public void Dispose()
    {
        StopRendering();
        StopWindowCapture();
        _canvas.Draw -= OnDraw;
        _capture?.Dispose();
        _screen?.Dispose();
        _backdrop?.Dispose();
        _lens?.Dispose();
    }

    private void StartWindowCapture(nint window)
    {
        if (window == 0 || !GraphicsCaptureSession.IsSupported())
        {
            return;
        }

        try
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            nint itemPointer = interop.CreateForWindow(window, ref itemIid);
            var item = GraphicsCaptureItem.FromAbi(itemPointer);
            Marshal.Release(itemPointer);

            _capturedWindow = window;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(CanvasDevice.GetSharedDevice(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            _framePool.FrameArrived += (_, _) => _canvas.DispatcherQueue.TryEnqueue(TakeFrame);
            _session = _framePool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            try
            {
                // No yellow capture border: the capture is an implementation detail of the effect.
                _session.IsBorderRequired = false;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or COMException)
            {
                Logger.LogInfo($"Liquid Glass: borderless capture unavailable ({ex.Message})");
            }

            _session.StartCapture();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            // Some windows (the desktop, elevated or protected windows) can't be captured;
            // the screen snapshot alone is used then.
            Logger.LogInfo($"Liquid Glass: window capture unavailable ({ex.Message})");
            StopWindowCapture();
        }
    }

    // Runs on the UI thread: the newest frame of the captured window is drawn over the screen
    // snapshot, at the window's position relative to the wheel.
    private void TakeFrame()
    {
        if (_framePool is null || _backdrop is null || _screen is null)
        {
            return;
        }

        using var frame = _framePool.TryGetNextFrame();
        if (frame is null || DwmGetWindowAttribute(_capturedWindow, DwmwaExtendedFrameBounds, out var bounds, Marshal.SizeOf<NativeRect>()) != 0)
        {
            return;
        }

        float scale = _dpi / 96;
        using var content = CanvasBitmap.CreateFromDirect3D11Surface(_backdrop, frame.Surface, _dpi);
        using (var ds = _backdrop.CreateDrawingSession())
        {
            ds.DrawImage(_screen);
            ds.DrawImage(content, new Vector2((bounds.Left - _rect.X) / scale, (bounds.Top - _rect.Y) / scale));
        }

        _canvas.Invalidate();
    }

    private void StopWindowCapture()
    {
        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
        _capturedWindow = 0;
    }

    private void StartRendering()
    {
        if (!_rendering)
        {
            _rendering = true;
            _lastTick = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void StopRendering()
    {
        if (_rendering)
        {
            _rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(now - _lastTick, 1.0 / 30);
        _lastTick = now;

        // Springs: slightly underdamped so the glass settles with a small, liquid overshoot.
        Spring(ref _presence, ref _presenceVelocity, _presenceTarget, 210, 0.62f, dt);

        float delta = ((_targetAngle - _angle + 540) % 360) - 180;
        _angleVelocity += ((300 * delta) - (2 * MathF.Sqrt(300) * 0.62f * _angleVelocity)) * dt;
        _angle = (_angle + (_angleVelocity * dt) + 360) % 360;

        // Fade out slowly, so crossing the hub to another format slides the droplet instead of re-creating it.
        _dropletOpacity += ((_hasHover ? 1 : 0) - _dropletOpacity) * Math.Min(1, dt * (_hasHover ? 18 : 5));

        _canvas.Invalidate();

        bool settled = MathF.Abs(_presence - _presenceTarget) < 0.002f && MathF.Abs(_presenceVelocity) < 0.01f
            && MathF.Abs(delta) < 0.05f && MathF.Abs(_angleVelocity) < 0.5f
            && MathF.Abs(_dropletOpacity - (_hasHover ? 1 : 0)) < 0.01f;
        if (settled)
        {
            StopRendering();
            if (_presenceTarget == 0 && _onHidden is { } done)
            {
                _onHidden = null;
                StopWindowCapture();
                done();
            }
        }
    }

    private static void Spring(ref float value, ref float velocity, float target, float stiffness, float dampingRatio, float dt)
    {
        velocity += ((stiffness * (target - value)) - (2 * MathF.Sqrt(stiffness) * dampingRatio * velocity)) * dt;
        value += velocity * dt;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float presence = Math.Clamp(_presence, 0, 1.2f);
        if (presence <= 0.001f)
        {
            return;
        }

        // Materialize: grow from 88% with the spring's overshoot, fading in with it.
        ds.Transform = Matrix3x2.CreateScale(0.88f + (0.12f * presence), Center);
        float opacity = Math.Clamp(presence, 0, 1);

        DrawShadow(sender, ds, opacity);

        using (var disk = CanvasGeometry.CreateCircle(sender, Center, GlassRadius))
        using (ds.CreateLayer(opacity, disk))
        {
            if (_glass is not null)
            {
                ds.DrawImage(_glass);
            }
            else
            {
                ds.FillCircle(Center, GlassRadius, Color.FromArgb(150, 40, 40, 44));
            }

            // A breath of frost so labels stay legible over busy content.
            ds.FillCircle(Center, GlassRadius, Color.FromArgb(18, 255, 255, 255));

            // Thickness: the bevel catches light just inside the rim and darkens at the very edge.
            using (var bevel = new CanvasRadialGradientBrush(sender, [
                new CanvasGradientStop { Position = (GlassRadius - Bevel) / GlassRadius, Color = Color.FromArgb(0, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.95f, Color = Color.FromArgb(34, 255, 255, 255) },
                new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(40, 0, 0, 0) },
            ]) { Center = Center, RadiusX = GlassRadius, RadiusY = GlassRadius })
            {
                ds.FillCircle(Center, GlassRadius, bevel);
            }

            DrawDroplet(sender, ds);

            // The hub is a separate, thicker drop of glass.
            using (var hubShade = new CanvasRadialGradientBrush(sender, [
                new CanvasGradientStop { Position = 0.7f, Color = Color.FromArgb(0, 255, 255, 255) },
                new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(46, 255, 255, 255) },
            ]) { Center = Center, RadiusX = HubRadius, RadiusY = HubRadius })
            {
                ds.FillCircle(Center, HubRadius, hubShade);
            }
        }

        DrawRim(ds, GlassRadius - 0.75f, 1.5f, opacity, 1f);
        DrawRim(ds, GlassRadius - 3.5f, 1f, opacity * 0.45f, -1f);
        DrawRim(ds, HubRadius, 1.25f, opacity * 0.9f, 1f);
    }

    private static void DrawShadow(CanvasControl sender, CanvasDrawingSession ds, float opacity)
    {
        using var disk = new CanvasCommandList(sender);
        using (var cl = disk.CreateDrawingSession())
        {
            cl.FillCircle(Center, GlassRadius - 4, Color.FromArgb(255, 0, 0, 0));
        }

        ds.DrawImage(
            new ShadowEffect { Source = disk, BlurAmount = 16, ShadowColor = Color.FromArgb((byte)(110 * opacity), 0, 0, 0) },
            new Vector2(0, 10));
    }

    private void DrawDroplet(CanvasControl sender, CanvasDrawingSession ds)
    {
        if (_dropletOpacity < 0.01f)
        {
            return;
        }

        // Liquid: the droplet stretches along its path while it is moving.
        float stretch = Math.Min(MathF.Abs(_angleVelocity) * 0.035f, _span * 0.55f);
        using var droplet = BuildDroplet(sender, _angle, _span - 6 + stretch);
        using (ds.CreateLayer(_dropletOpacity, droplet))
        {
            var mid = PointAt((DropletInner + DropletOuter) / 2, _angle);
            if (_glass is not null)
            {
                // The droplet is a magnifying lens over the glass beneath it.
                ds.DrawImage(new Transform2DEffect { Source = _glass, TransformMatrix = Matrix3x2.CreateScale(1.16f, mid) });
            }

            using var sheen = new CanvasLinearGradientBrush(sender, Color.FromArgb(92, 255, 255, 255), Color.FromArgb(26, 255, 255, 255))
            {
                StartPoint = PointAt(DropletOuter, _angle),
                EndPoint = PointAt(DropletInner, _angle),
            };
            ds.FillGeometry(droplet, sheen);
        }

        ds.DrawGeometry(droplet, Color.FromArgb((byte)(200 * _dropletOpacity), 255, 255, 255), 1.25f);
    }

    /// <summary>A ring sector with rounded corners, like a drop of glass resting on the wheel.</summary>
    private static CanvasGeometry BuildDroplet(ICanvasResourceCreator resources, float centerDegrees, float spanDegrees)
    {
        const float Corner = 12;
        float inner = DropletInner + Corner;
        float outer = DropletOuter - Corner;
        float half = (spanDegrees / 2) - (Corner / ((inner + outer) / 2) * 180 / MathF.PI);
        half = Math.Max(half, 1);

        using var path = new CanvasPathBuilder(resources);
        path.BeginFigure(PointAt(outer, centerDegrees - half));
        path.AddArc(PointAt(outer, centerDegrees + half), outer, outer, 0, CanvasSweepDirection.Clockwise, half * 2 > 180 ? CanvasArcSize.Large : CanvasArcSize.Small);
        path.AddLine(PointAt(inner, centerDegrees + half));
        path.AddArc(PointAt(inner, centerDegrees - half), inner, inner, 0, CanvasSweepDirection.CounterClockwise, half * 2 > 180 ? CanvasArcSize.Large : CanvasArcSize.Small);
        path.EndFigure(CanvasFigureLoop.Closed);

        using var core = CanvasGeometry.CreatePath(path);
        using var rounding = core.Stroke(Corner * 2, new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round });
        return core.CombineWith(rounding, Matrix3x2.Identity, CanvasGeometryCombine.Union);
    }

    /// <summary>
    /// Specular rim: brightest where it faces the light, with a softer echo on the opposite side.
    /// <paramref name="side"/> = -1 flips it for the inner reflection of the bevel.
    /// </summary>
    private static void DrawRim(CanvasDrawingSession ds, float radius, float width, float strength, float side)
    {
        const int Segments = 180;
        const float Step = 360f / Segments;
        float light = side > 0 ? LightDegrees : LightDegrees + 180;
        for (int i = 0; i < Segments; i++)
        {
            float mid = (i + 0.5f) * Step;
            float facing = MathF.Max(0, MathF.Cos((mid - light) * MathF.PI / 180));
            float opposite = MathF.Max(0, MathF.Cos((mid - light - 180) * MathF.PI / 180));
            float alpha = strength * (0.16f + (0.84f * MathF.Pow(facing, 5)) + (0.4f * MathF.Pow(opposite, 5)));
            ds.DrawLine(PointAt(radius, i * Step), PointAt(radius, (i + 1) * Step), Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), 255, 255, 255), width);
        }
    }

    /// <summary>
    /// Refraction: the backdrop bent through the lens map, each color channel by a slightly
    /// different amount (dispersion), lightly blurred and brightened like real glass.
    /// </summary>
    private static ICanvasImage BuildGlass(CanvasBitmap backdrop, CanvasBitmap lens)
    {
        ICanvasImage Channel(float amount, Matrix5x4 keep) => new ColorMatrixEffect
        {
            ColorMatrix = keep,
            Source = new DisplacementMapEffect
            {
                Source = backdrop,
                Displacement = lens,
                Amount = amount,
                XChannelSelect = EffectChannelSelect.Red,
                YChannelSelect = EffectChannelSelect.Green,
            },
        };

        var refracted = new CompositeEffect
        {
            Mode = CanvasComposite.Add,
            Sources =
            {
                Channel(MaxShift * 2 * 0.94f, new Matrix5x4 { M11 = 1, M44 = 1 }),
                Channel(MaxShift * 2, new Matrix5x4 { M22 = 1, M44 = 1 }),
                Channel(MaxShift * 2 * 1.08f, new Matrix5x4 { M33 = 1, M44 = 1 }),
            },
        };

        return new SaturationEffect
        {
            Saturation = 1.35f,
            Source = new ExposureEffect
            {
                Exposure = 0.12f,
                Source = new GaussianBlurEffect { Source = refracted, BlurAmount = 2.5f, BorderMode = EffectBorderMode.Hard },
            },
        };
    }

    /// <summary>
    /// Displacement map for the wheel's glass, encoded as B8G8R8A8 with R/G = 0.5 + offset / (2 * MaxShift).
    /// Flat in the middle of the ring; the bevel and the hub bend the backdrop toward the center,
    /// which magnifies it near the edges the way a thick, rounded pane of glass does.
    /// </summary>
    private static byte[] BuildLensMap()
    {
        int size = (int)Size;
        var map = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - Center.X;
                float dy = y + 0.5f - Center.Y;
                float distance = MathF.Sqrt((dx * dx) + (dy * dy));
                float shiftX = 0;
                float shiftY = 0;

                if (distance > 0.001f)
                {
                    float nx = dx / distance;
                    float ny = dy / distance;
                    float pull = 0;
                    if (distance < HubRadius)
                    {
                        float t = Math.Clamp((distance - (HubRadius - HubBevel)) / HubBevel, 0, 1);
                        pull = (distance * 0.14f) + (12 * t * t);
                    }
                    else if (distance < GlassRadius)
                    {
                        float t = Math.Clamp((distance - (GlassRadius - Bevel)) / Bevel, 0, 1);
                        pull = MaxShift * MathF.Pow(t, 2.2f);
                    }

                    shiftX = -nx * pull;
                    shiftY = -ny * pull;
                }

                int i = ((y * size) + x) * 4;
                map[i] = 0; // B
                map[i + 1] = Encode(shiftY); // G
                map[i + 2] = Encode(shiftX); // R
                map[i + 3] = 255;
            }
        }

        return map;

        static byte Encode(float shift) => (byte)Math.Clamp(MathF.Round(127.5f + (shift / (2 * MaxShift) * 255)), 0, 255);
    }

    private static Vector2 PointAt(float radius, float degreesFromTop)
    {
        float radians = (degreesFromTop - 90) * MathF.PI / 180;
        return new Vector2(Center.X + (radius * MathF.Cos(radians)), Center.Y + (radius * MathF.Sin(radians)));
    }

    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out NativeRect value, int size);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);

        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }
}
