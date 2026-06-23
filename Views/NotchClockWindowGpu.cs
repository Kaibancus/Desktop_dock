using System;
using System.Globalization;
using System.Numerics;
using System.Windows.Threading;
using Polaris.Interop;
using Polaris.Services;
using Polaris.Services.Gpu;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace Polaris.Views;

/// <summary>GPU-rendered Saturn notch clock: the trapezoid "notch" plate, its soft
/// dark halo and the 3-D pale-gold lettering drawn in Direct2D + DirectWrite under
/// DirectComposition.</summary>
internal sealed class NotchClockWindowGpu : INotchClock
{
    private const float PlateWidth = 240, PlateHeight = 30, Slant = 20, SidePad = 16, FreePad = 14;
    private static readonly int WinW = (int)(PlateWidth + SidePad * 2);
    private static readonly int WinH = (int)(PlateHeight + FreePad);

    private IntPtr _hwnd;
    private CompositionHost? _host;
    private double _dpi = 1.0;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _format;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _releaseTimer;
    private bool _atBottom;
    private bool _built;
    private bool _visible;

    public NotchClockWindowGpu()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Render();
        // Release the GPU device after the notch stays hidden past the delay (the Saturn
        // theme is frequently inactive). A re-summon within the delay cancels it, so quick
        // hide/show cycles don't churn the device; a sustained hide frees it for the trim.
        _releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); if (!_visible) ReleaseGpu(); };
    }

    public void ShowNotch(bool atBottom)
    {
        try
        {
            _releaseTimer.Stop();   // cancel a pending device release — we're showing again
            EnsureBuilt();
            _atBottom = atBottom;

            var mon = MonitorLayout.ActiveBounds;
            int pw = (int)Math.Ceiling(WinW * _dpi), ph = (int)Math.Ceiling(WinH * _dpi);
            int x = (int)Math.Round((mon.Left + (mon.Width - WinW) / 2.0) * _dpi);
            int y = (int)Math.Round((atBottom ? mon.Bottom - WinH : mon.Top) * _dpi);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, pw, ph, Win32.SWP_NOACTIVATE);
            if (!_visible)
            {
                Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
                _visible = true;
            }
            Render();
            _timer.Start();
        }
        catch (Exception ex) { Log.Warn("NotchGpu", "show failed: " + ex.Message); }
    }

    public void HideNotch()
    {
        _timer.Stop();
        if (_visible)
        {
            Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
            _visible = false;
        }
        // Free the GPU device (a full D3D11 device + its driver worker threads) once the
        // notch has stayed hidden past the delay, so a glass session / dismissed dock does
        // not keep a Saturn-only device committed. Recreated lazily on the next ShowNotch.
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    /// <summary>Disposes the GPU device + text resources + window while hidden. The next
    /// ShowNotch rebuilds them lazily via EnsureBuilt. Runs on the UI thread (the notch
    /// renders from a DispatcherTimer, so it has no render thread to coordinate with).</summary>
    private void ReleaseGpu()
    {
        _format?.Dispose(); _format = null;
        _dwrite?.Dispose(); _dwrite = null;
        _host?.Dispose(); _host = null;
        if (_hwnd != IntPtr.Zero) { Win32.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        _built = false;
    }

    private void EnsureBuilt()
    {
        if (_built)
            return;
        _hwnd = CreateWindow(WinW, WinH);
        _dpi = CompositionHost.DpiScale(_hwnd);
        _host = new CompositionHost(_hwnd, (int)Math.Ceiling(WinW * _dpi),
            (int)Math.Ceiling(WinH * _dpi), (float)(96.0 * _dpi));
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _format = _dwrite.CreateTextFormat("华文新魏", null, FontWeight.SemiBold,
            FontStyle.Normal, FontStretch.Normal, 20f, "zh-cn");
        _format.TextAlignment = TextAlignment.Center;
        _format.ParagraphAlignment = ParagraphAlignment.Center;
        _built = true;
    }

    private static Color4 C(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    private void Render()
    {
        if (_host == null || _format == null)
            return;
        var ctx = _host.Context;

        // Plate origin inside the window: the wide edge hugs the screen edge, so the
        // panel sits at the window top for a top notch, FreePad down for a bottom one.
        float ox = SidePad;
        float oy = _atBottom ? FreePad : 0f;

        using var plate = BuildPlate(ctx, ox, oy, _atBottom);

        // Soft dark halo: render the black trapezoid into a command list and blur it
        // with a real D2D GaussianBlur (the WPF version is a 14px blur), so the
        // penumbra around the slant edges is smooth (no banding).
        var glowSrc = ctx.CreateCommandList();
        ctx.Target = glowSrc;
        ctx.BeginDraw();
        using (var black = ctx.CreateSolidColorBrush(C(0xD7, 0x00, 0x00, 0x00)))
            ctx.FillGeometry(plate, black);
        ctx.EndDraw();
        glowSrc.Close();
        _host.SetDefaultTarget();

        using var blur = new Vortice.Direct2D1.Effects.GaussianBlur(ctx);
        blur.SetInput(0, glowSrc, true);
        blur.StandardDeviation = 14f / 3f;

        // Warm tan glow behind the gold text (parity with WPF DropShadowEffect:
        // colour CBBC95, blur 7, depth 0, opacity 0.45) so the clock reads as part
        // of the Saturn ring palette rather than flat gold. Built here (outside the
        // main BeginDraw) as its own command list, then composited under the gold.
        string txt = DateTime.Now.ToString("M月d日 ddd  H:mm", CultureInfo.GetCultureInfo("zh-CN"));
        var rect = new Rect(ox, oy, PlateWidth, PlateHeight);
        var textGlowSrc = ctx.CreateCommandList();
        ctx.Target = textGlowSrc;
        ctx.BeginDraw();
        using (var tan = ctx.CreateSolidColorBrush(C(0x73, 0xCB, 0xBC, 0x95)))   // 0x73 ≈ 0.45 opacity
            ctx.DrawText(txt, _format, rect, tan);
        ctx.EndDraw();
        textGlowSrc.Close();
        _host.SetDefaultTarget();

        using var textGlow = new Vortice.Direct2D1.Effects.GaussianBlur(ctx);
        textGlow.SetInput(0, textGlowSrc, true);
        textGlow.StandardDeviation = 7f / 3f;

        ctx.BeginDraw();
        ctx.Clear(C(0, 0, 0, 0));
        ctx.DrawImage(blur, new Vector2(0, 0), InterpolationMode.Linear, CompositeMode.SourceOver);
        glowSrc.Dispose();

        // Crisp black plate.
        using (var plateBrush = ctx.CreateSolidColorBrush(C(0xEE, 0x07, 0x08, 0x0B)))
            ctx.FillGeometry(plate, plateBrush);

        // 3-D raised lettering: dark offset copy, warm tan glow, then pale-gold copy.
        using (var dark = ctx.CreateSolidColorBrush(C(0xCD, 0x00, 0x00, 0x00)))
            ctx.DrawText(txt, _format, new Rect(rect.X + 1.3f, rect.Y + 1.6f, rect.Width, rect.Height), dark);
        ctx.DrawImage(textGlow, new Vector2(0, 0), InterpolationMode.Linear, CompositeMode.SourceOver);
        textGlowSrc.Dispose();
        using (var gold = ctx.CreateSolidColorBrush(C(0xFF, 0xEC, 0xDF, 0xBE)))
            ctx.DrawText(txt, _format, rect, gold);

        ctx.EndDraw();
        _host.Present();
    }

    private static ID2D1PathGeometry BuildPlate(ID2D1DeviceContext ctx, float ox, float oy, bool atBottom)
    {
        float w = PlateWidth, h = PlateHeight, s = Slant;
        var geo = ctx.Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            if (!atBottom)
            {
                sink.BeginFigure(new Vector2(ox + 0, oy + 0), FigureBegin.Filled);
                sink.AddLine(new Vector2(ox + w, oy + 0));
                sink.AddLine(new Vector2(ox + w - s, oy + h));
                sink.AddLine(new Vector2(ox + s, oy + h));
            }
            else
            {
                sink.BeginFigure(new Vector2(ox + s, oy + 0), FigureBegin.Filled);
                sink.AddLine(new Vector2(ox + w - s, oy + 0));
                sink.AddLine(new Vector2(ox + w, oy + h));
                sink.AddLine(new Vector2(ox + 0, oy + h));
            }
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }

    // ---- Raw Win32 NOREDIRECTIONBITMAP window (shared plumbing in Interop/Win32) ----

    private static readonly Win32.WndProc s_wndProc = Win32.DefWindowProcW;
    private static ushort s_atom;

    private static IntPtr CreateWindow(int w, int h) => Win32.CreateWindow(
        "PolarisNotchGpu",
        Win32.WS_EX_NOREDIRECTIONBITMAP | Win32.WS_EX_TOPMOST | Win32.WS_EX_TRANSPARENT |
        Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
        w, h, s_wndProc, ref s_atom);
}
