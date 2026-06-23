using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice.Direct2D1;
using Polaris.Services;
using Polaris.Services.Gpu;

namespace Polaris.Views;

/// <summary>
/// Shared render-loop / GPU-host lifecycle for the two composition docks
/// (<see cref="MainDockWindowGpu"/> and <see cref="SideDockWindowGpu"/>). Both docks own a
/// <see cref="CompositionHost"/> driven either by a dedicated <see cref="RenderLoop"/> thread
/// (paced to the display refresh rate via the swap chain's frame-latency waitable object) or,
/// when <c>POLARIS_GPU_RENDERTHREAD=0</c>, by the legacy UI-thread <see cref="FrameClock"/>.
/// The plumbing (loop creation, start/stop gating with the GC low-latency scope, the
/// marshalling helpers that move work between the UI and render threads, the icon bitmap
/// caches + upload, and the external drag-in icon preview) was previously duplicated verbatim
/// in both windows; it now lives here so the two docks stay in lock-step and a future dock
/// only implements the dock-specific hooks.
/// </summary>
internal abstract class GpuDockBase
{
    /// <summary>Render on a dedicated thread paced to the refresh rate (60/120/144Hz) and
    /// keep the UI thread free for input. <c>POLARIS_GPU_RENDERTHREAD=0</c> falls back to the
    /// legacy UI-thread <see cref="FrameClock"/> (which caps ~38-53fps on this hardware).</summary>
    protected static readonly bool UseRenderThread =
        Environment.GetEnvironmentVariable("POLARIS_GPU_RENDERTHREAD") != "0";

    /// <summary>Dedicated render thread; null on the UI-thread FrameClock path.</summary>
    protected RenderLoop? _loop;

    /// <summary>The GPU composition host (per-window swap chain + D2D context); owned and
    /// driven on the render thread when <see cref="UseRenderThread"/>.</summary>
    protected CompositionHost? _host;

    /// <summary>UI-thread frame clock used only on the <c>POLARIS_GPU_RENDERTHREAD=0</c> path.</summary>
    protected FrameClock? _timer;

    /// <summary>Balances <see cref="RenderGcScope"/> Enter/Leave across show/hide.</summary>
    protected bool _gcActive;

    /// <summary>WPF BitmapSource icon cache (device-independent; survives host rebuilds).
    /// Populated during slot building; never caches a null (see <see cref="IconExtractor.GetCached"/>).</summary>
    protected readonly Dictionary<string, BitmapSource?> _iconCache = new();

    /// <summary>Direct2D bitmap cache keyed by icon source (device-bound; cleared when the host
    /// is disposed). Caches BOTH hits and misses, so a key is uploaded/probed at most once.</summary>
    protected readonly Dictionary<string, ID2D1Bitmap?> _bmpCache = new();

    /// <summary>Window-local drop point of an in-progress external drag (null when none).</summary>
    protected Vector2? _extDragPt;

    /// <summary>Icon source of the dragged item, previewed at <see cref="_extDragPt"/>.</summary>
    protected string? _dragIconKey;

    /// <summary>Name for this dock's render thread (diagnostics).</summary>
    protected abstract string RenderThreadName { get; }

    /// <summary>The dock's UI-thread dispatcher, used to marshal UI-only work back from the
    /// render thread.</summary>
    protected abstract Dispatcher UiDispatcher { get; }

    /// <summary>Advance animation/layout state and draw one frame. Runs on the render thread
    /// (render-thread path) or the UI thread (FrameClock path).</summary>
    protected abstract void Tick();

    /// <summary>Draw the current state immediately (no state advance).</summary>
    protected abstract void Render();

    /// <summary>Converts a SCREEN-pixel point to this dock's window-local DIP coordinates.</summary>
    protected abstract Vector2 ScreenToLocal(int screenX, int screenY);

    /// <summary>Icon edge length (DIP) used to size the drag-in preview.</summary>
    protected abstract float DragIconSize { get; }

    /// <summary>Lazily create the render thread bound to <see cref="RenderThreadFrame"/>.</summary>
    protected void EnsureLoop() => _loop ??= new RenderLoop(RenderThreadName, RenderThreadFrame);

    /// <summary>Render-thread frame: block until the compositor is ready (refresh-rate paced),
    /// then advance + draw lock-free so UI-thread input never waits for a full render frame.</summary>
    protected void RenderThreadFrame()
    {
        _host?.WaitForVBlank();
        Tick();
    }

    /// <summary>Resume per-frame rendering: the render loop or the UI-thread FrameClock. Also
    /// enters the GC low-latency scope so a busy dock defers gen2 collections.</summary>
    protected void StartDriver()
    {
        if (!_gcActive) { _gcActive = true; RenderGcScope.Enter(); }
        if (UseRenderThread) _loop?.SetActive(true);
        else _timer?.Start();
    }

    /// <summary>Pause per-frame rendering (a hidden/settled dock costs nothing) and leave the
    /// GC low-latency scope.</summary>
    protected void StopDriver()
    {
        if (_gcActive) { _gcActive = false; RenderGcScope.Leave(); }
        if (UseRenderThread) _loop?.SetActive(false);
        else _timer?.Stop();
    }

    /// <summary>Runs <paramref name="a"/> where the GPU device lives: posted to the render
    /// thread (fire-and-forget, FIFO) on the render-thread path, inline otherwise.</summary>
    protected void RunOnRender(Action a)
    {
        if (UseRenderThread && _loop != null) _loop.Post(a);
        else a();
    }

    /// <summary>Runs <paramref name="a"/> on the render thread and waits (ordering-critical
    /// device work, e.g. host teardown before the HWND is destroyed); inline otherwise.</summary>
    protected void InvokeOnRender(Action a)
    {
        if (UseRenderThread && _loop != null) _loop.Invoke(a);
        else a();
    }

    /// <summary>Marshals a UI-thread-only operation (Win32 on UI-owned windows, WPF popups) to
    /// the dispatcher when called from the render thread; inline on the default path.</summary>
    protected void OnUi(Action a)
    {
        if (UseRenderThread) UiDispatcher.BeginInvoke(a);
        else a();
    }

    /// <summary>Requests a repaint after a UI-thread interaction (drag/scroll). Synchronous on
    /// the default path; on the render-thread path the active loop already redraws every frame,
    /// so the state change is picked up on the next vblank (sub-frame latency).</summary>
    protected void RequestRender()
    {
        if (!UseRenderThread) Render();
    }

    /// <summary>Uploads <paramref name="src"/> to a premultiplied-BGRA Direct2D bitmap, cached
    /// by <paramref name="key"/>. Caches BOTH a successful upload and a null (no source / upload
    /// failure), so each key is converted at most once until the host is rebuilt. Runs on the
    /// render thread (it touches the GPU host). The <paramref name="ctx"/> is unused but kept so
    /// call sites read naturally alongside the other ctx-based draw helpers.</summary>
    protected ID2D1Bitmap? GetBitmap(ID2D1DeviceContext ctx, string key, BitmapSource? src)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if (_bmpCache.TryGetValue(key, out var cached))
            return cached;
        ID2D1Bitmap? d2d = null;
        try
        {
            if (src != null)
            {
                if (src.Format != PixelFormats.Pbgra32)
                    src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                var pxbuf = new byte[stride * h];
                src.CopyPixels(pxbuf, stride, 0);
                d2d = _host!.CreateBitmap(w, h, pxbuf, stride);
            }
        }
        catch { d2d = null; }
        _bmpCache[key] = d2d;
        return d2d;
    }

    /// <summary>Called while an external file/shell drag hovers the dock (null on leave/drop)
    /// with the SCREEN point and the first dragged file's icon source. The OLE callback fires
    /// only while the cursor is over the drop shim (== the mouse-solid dock box), so any
    /// reported point is a valid drop spot. Runs on the UI thread (STA OLE callback).</summary>
    protected void OnExternalDragMove((int x, int y)? screenPt, string? iconSrc)
    {
        if (screenPt is { } p)
        {
            _extDragPt = ScreenToLocal(p.x, p.y);
            if (iconSrc != null) _dragIconKey = iconSrc;
        }
        else { _extDragPt = null; _dragIconKey = null; }
        // Repaint so the preview tracks the cursor (synchronous on the default path, next
        // vblank on the render-thread path).
        try { RequestRender(); } catch { }
    }

    /// <summary>Previews the dragged item's icon at the drop point while an external drag hovers
    /// the dock. The OS layered drag image isn't rendered over the topmost composition window,
    /// so without this the user sees nothing being dragged in.</summary>
    protected void DrawDragPreview(ID2D1DeviceContext ctx, Vector2 p)
    {
        var key = _dragIconKey;
        if (key == null)
            return;
        // Resolve at most once per key: GetBitmap caches BOTH hits and misses in _bmpCache, so
        // checking it first means an unresolvable icon doesn't re-probe the shell (synchronous
        // SHGetFileInfo / File.Exists) on every render frame for the whole drag.
        if (!_bmpCache.TryGetValue(key, out var bmp))
            bmp = GetBitmap(ctx, key, IconExtractor.GetCached(key, _iconCache));
        if (bmp == null)
            return;
        float g = DragIconSize, half = g / 2f;
        var bs = bmp.Size;
        var baseTf = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(g / Math.Max(1f, bs.Width), g / Math.Max(1f, bs.Height))
                      * Matrix3x2.CreateTranslation(p.X - half, p.Y - half) * baseTf;
        ctx.DrawBitmap(bmp, 0.85f, InterpolationMode.HighQualityCubic);
        ctx.Transform = baseTf;
    }
}
