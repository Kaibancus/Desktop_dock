using System;
using System.Windows.Threading;
using Polaris.Services.Gpu;

namespace Polaris.Views;

/// <summary>
/// Shared render-loop / GPU-host lifecycle for the two composition docks
/// (<see cref="MainDockWindowGpu"/> and <see cref="SideDockWindowGpu"/>). Both docks own a
/// <see cref="CompositionHost"/> driven either by a dedicated <see cref="RenderLoop"/> thread
/// (paced to the display refresh rate via the swap chain's frame-latency waitable object) or,
/// when <c>POLARIS_GPU_RENDERTHREAD=0</c>, by the legacy UI-thread <see cref="FrameClock"/>.
/// The plumbing (loop creation, start/stop gating with the GC low-latency scope, and the
/// marshalling helpers that move work between the UI and render threads) was previously
/// duplicated verbatim in both windows; it now lives here so the two docks stay in lock-step
/// and a future dock only implements the dock-specific hooks.
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
}
