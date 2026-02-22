# Piccolo Direct2D (WPF) Implementation Plan

## Context and Constraints

### What already exists
- `SharedProjects/Piccolo/` — the Piccolo scene-graph library. Pure GDI+ (System.Drawing). No DirectX dependency.
- `LegendaryExplorer/LegendaryExplorer/LegendaryExplorer.csproj` — already references:
  - `Microsoft.Wpf.Interop.DirectX-x64` v0.9.0-beta-22856
  - `SharpDX` v4.2.0
  - `SharpDX.Direct2D1` v4.2.0
  - `SharpDX.Direct3D11` v4.2.0
  - `SharpDX.DXGI` v4.2.0
  - `SharpDX.Mathematics` v4.2.0
  - `SharpDX.Desktop` v4.2.0
  - `SharpDX.D3DCompiler` v4.2.0
- `SceneRenderControl.cs` — a fully working WPF control that hosts `D3D11Image`, manages device lifetime, forwards input. This is the template the new canvas must follow exactly.

### Key facts about Piccolo's current rendering
- `PCanvas` is a WinForms `Control`. It is the only WinForms type that consumers touch.
- `PPaintContext` wraps a GDI+ `Graphics` object. All nodes call `paintContext.Graphics` directly (no helper wrappers).
- The four nodes that paint:
  - `PNode.Paint` — `g.FillRectangle(Brush, Bounds)`
  - `PPath.Paint` — `g.FillPath(b, path)` / `g.DrawPath(pen, path)`
  - `PText.Paint` — `g.DrawString(text, font, brush, bounds, format)` and `g.FillRectangle` for greek text
  - `PImage.Paint` — `g.DrawImage(image, bounds)`
- `PText` also has a static `GRAPHICS = Graphics.FromImage(new Bitmap(1,1))` used in `RecomputeBounds()` for text measurement — independent of the paint path.
- `PPaintContext.PushMatrix` calls `graphics.MultiplyTransform(matrix.GetGdiMatrix())` and saves/restores `graphics.Transform`.
- `PPaintContext.PushClip` calls `graphics.Clip = intersectedRegion`.
- `PInputManager.ProcessEventFromCamera(EventArgs, PInputType, PCamera, PCanvas)` — the 4th parameter (`PCanvas`) is only used in one place: `nextWindowsSource.PointToClient(pt)` during drag-drop events.
- `PCanvas.CreatePaintContext(PaintEventArgs)` is a `protected virtual` factory method — the designed extension point for plugging in a different renderer.

---

## Goal

Create `PCanvasWpf`: a WPF `ContentControl` that hosts the Piccolo scene graph and renders it through Direct2D, using the same `D3D11Image` interop pattern as `SceneRenderControl`. The existing WinForms `PCanvas` must remain untouched and fully functional.

---

## Files to Create

| File | Purpose |
|---|---|
| `SharedProjects/Piccolo/PCanvasWpf.cs` | WPF host control (replaces PCanvas for WPF callers) |
| `SharedProjects/Piccolo/Util/PD2DPaintContext.cs` | D2D implementation of the paint context |

## Files to Modify

| File | Change |
|---|---|
| `SharedProjects/Piccolo/Piccolo.csproj` | Add SharpDX NuGet package references |
| `SharedProjects/Piccolo/PNode.cs` | D2D branch in `Paint()` |
| `SharedProjects/Piccolo/Nodes/PPath.cs` | D2D branch in `Paint()`, geometry cache |
| `SharedProjects/Piccolo/Nodes/PText.cs` | D2D branch in `Paint()`, DirectWrite measurement |
| `SharedProjects/Piccolo/Nodes/PImage.cs` | D2D branch in `Paint()`, bitmap cache |
| `SharedProjects/Piccolo/PInputManager.cs` | Widen `PCanvas` parameter to an interface |

---

## Step 1 — Add NuGet packages to Piccolo.csproj

Open `SharedProjects/Piccolo/Piccolo.csproj`. The project currently targets `net6.0-windows`. Add the same SharpDX references that `LegendaryExplorer.csproj` uses, all with `NoWarn="NU1701"` because SharpDX targets .NET Framework and triggers compatibility warnings.

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Wpf.Interop.DirectX-x64" Version="0.9.0-beta-22856" />
  <PackageReference Include="SharpDX" Version="4.2.0" NoWarn="NU1701" />
  <PackageReference Include="SharpDX.Direct2D1" Version="4.2.0" NoWarn="NU1701" />
  <PackageReference Include="SharpDX.Direct3D11" Version="4.2.0" NoWarn="NU1701" />
  <PackageReference Include="SharpDX.DXGI" Version="4.2.0" NoWarn="NU1701" />
  <PackageReference Include="SharpDX.Mathematics" Version="4.2.0" NoWarn="NU1701" />
</ItemGroup>
```

Also ensure the project has a WPF property group so that WPF types are available:

```xml
<PropertyGroup>
  <UseWPF>true</UseWPF>
</PropertyGroup>
```

---

## Step 2 — Extract `IPiccoloCanvas` interface from `PCanvas`

`PInputManager.ProcessEventFromCamera` currently takes a `PCanvas` as its 4th parameter. The only use of that parameter is:

```csharp
// PInputManager.cs line 164
currentCanvasPosition = nextWindowsSource.PointToClient(pt);  // DragDrop only
```

### 2a. Define the interface

Add a new file `SharedProjects/Piccolo/IPiccoloCanvas.cs`:

```csharp
namespace Piccolo
{
    /// <summary>
    /// Minimal surface PInputManager needs from either PCanvas (WinForms) or PCanvasWpf (WPF).
    /// </summary>
    public interface IPiccoloCanvas
    {
        /// <summary>Converts screen coordinates to canvas-local coordinates.</summary>
        System.Drawing.PointF PointToClient(System.Drawing.Point pt);
    }
}
```

### 2b. Make `PCanvas` implement it

`PCanvas` already has `PointToClient` inherited from `Control`; just add the interface declaration:

```csharp
public class PCanvas : Control, IPiccoloCanvas {
```

Because `Control.PointToClient` returns `System.Drawing.Point` rather than `PointF`, add an explicit implementation:

```csharp
PointF IPiccoloCanvas.PointToClient(System.Drawing.Point pt) =>
    (PointF)(System.Drawing.Point)PointToClient(pt);
```

### 2c. Update `PInputManager`

Change the signature of `ProcessEventFromCamera`:

```csharp
// Before:
public void ProcessEventFromCamera(EventArgs e, PInputType type, PCamera source, PCanvas canvas)
// After:
public void ProcessEventFromCamera(EventArgs e, PInputType type, PCamera source, IPiccoloCanvas canvas)
```

Update the field:

```csharp
private IPiccoloCanvas nextWindowsSource;
```

This is a non-breaking change because `PCanvas` now implements `IPiccoloCanvas`, so all existing call sites compile unchanged.

---

## Step 3 — Create `PD2DPaintContext`

**File:** `SharedProjects/Piccolo/Util/PD2DPaintContext.cs`

This class mirrors `PPaintContext` but wraps a `SharpDX.Direct2D1.DeviceContext` instead of `Graphics`. It must satisfy the same stack contract so the unchanged `PCamera`, `PLayer`, and `PNode` traversal code continues to work.

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using SharpDX.Mathematics.Interop;
using DwFactory = SharpDX.DirectWrite.Factory;
using D2dFactory = SharpDX.Direct2D1.Factory1;

namespace Piccolo.Util
{
    public sealed class PD2DPaintContext : PPaintContext
    {
        // ── Core D2D objects ──────────────────────────────────────────
        public DeviceContext D2DContext { get; }
        public DwFactory DWriteFactory { get; }

        // ── Stacks (parallel to PPaintContext's stacks) ────────────────
        private readonly Stack<RawMatrix3x2> _transformStack = new();
        private readonly Stack<RectangleF>   _localClipStack  = new();
        private readonly Stack<Layer>        _layerStack      = new();  // clip layers
        private readonly Stack<PCamera>      _cameraStack     = new();

        // ── Resource caches (owned by context, disposed with it) ───────
        //    Key: ARGB int  Value: SolidColorBrush
        private readonly Dictionary<int, SolidColorBrush> _brushCache = new();

        public PD2DPaintContext(DeviceContext d2dContext, DwFactory dwFactory, PCanvasWpf canvas)
            : base(canvas)   // see §3a below
        {
            D2DContext   = d2dContext;
            DWriteFactory = dwFactory;
            _localClipStack.Push(new RectangleF(0, 0,
                d2dContext.Size.Width, d2dContext.Size.Height));
        }

        // ── Graphics property — throws to catch accidental GDI+ usage ──
        // (PPaintContext.Graphics must be overridable; see §3a)
        public override System.Drawing.Graphics Graphics =>
            throw new InvalidOperationException(
                "GDI+ Graphics is not available in the Direct2D paint context. " +
                "Check for paintContext is PD2DPaintContext before calling paint code.");

        // ── Scale (matches PPaintContext.Scale semantics) ──────────────
        public override float Scale
        {
            get
            {
                var t = D2DContext.Transform;
                // magnitude of the X basis vector
                return MathF.Sqrt(t.M11 * t.M11 + t.M12 * t.M12);
            }
        }

        // ── LocalClip ──────────────────────────────────────────────────
        public override RectangleF LocalClip => _localClipStack.Peek();

        // ── Camera stack ───────────────────────────────────────────────
        public override void PushCamera(PCamera camera) => _cameraStack.Push(camera);
        public override void PopCamera()                => _cameraStack.Pop();
        public override PCamera Camera                 => _cameraStack.Peek();

        // ── Transform stack ────────────────────────────────────────────
        public override void PushMatrix(PMatrix matrix)
        {
            if (matrix == null) return;
            _transformStack.Push(D2DContext.Transform);
            _localClipStack.Push(matrix.InverseTransform(LocalClip));

            var m = matrix.MatrixReference;  // System.Numerics.Matrix3x2
            // Right-multiply: D2D uses row-vector convention same as GDI+
            var current = D2DContext.Transform;
            D2DContext.Transform = Multiply(ToRaw(m), current);
        }

        public override void PopMatrix()
        {
            D2DContext.Transform = _transformStack.Pop();
            _localClipStack.Pop();
        }

        // ── Clip stack (D2D uses axis-aligned rectangular layers) ──────
        public override void PushClip(System.Drawing.Region aClip)
        {
            // Convert the GDI+ region bounds to an axis-aligned rect in
            // D2D coordinates.  PushAxisAlignedClip is sufficient because
            // Piccolo clips are always axis-aligned rectangles.
            RectangleF gdiRect  = aClip.GetBounds(DummyGraphics);
            RectangleF newLocal = RectangleF.Intersect(LocalClip, gdiRect);
            _localClipStack.Push(newLocal);

            D2DContext.PushAxisAlignedClip(
                ToRawRect(newLocal),
                AntialiasMode.PerPrimitive);
        }

        public override void PopClip()
        {
            D2DContext.PopAxisAlignedClip();
            _localClipStack.Pop();
        }

        // ── Brush helper (cached) ──────────────────────────────────────
        public SolidColorBrush GetBrush(Color color)
        {
            int key = color.ToArgb();
            if (!_brushCache.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(D2DContext, ToRawColor(color));
                _brushCache[key] = brush;
            }
            return brush;
        }

        // ── Conversion helpers ─────────────────────────────────────────
        public static RawColor4 ToRawColor(Color c) =>
            new RawColor4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        public static RawMatrix3x2 ToRaw(System.Numerics.Matrix3x2 m) =>
            new RawMatrix3x2(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);

        public static RawRectangleF ToRawRect(RectangleF r) =>
            new RawRectangleF(r.Left, r.Top, r.Right, r.Bottom);

        private static RawMatrix3x2 Multiply(RawMatrix3x2 a, RawMatrix3x2 b)
        {
            return new RawMatrix3x2(
                a.M11*b.M11 + a.M12*b.M21,
                a.M11*b.M12 + a.M12*b.M22,
                a.M21*b.M11 + a.M22*b.M21,
                a.M21*b.M12 + a.M22*b.M22,
                a.M31*b.M11 + a.M32*b.M21 + b.M31,
                a.M31*b.M12 + a.M32*b.M22 + b.M32);
        }

        // Lazily-created dummy GDI Graphics for Region.GetBounds()
        private static System.Drawing.Graphics _dummyGfx;
        private static System.Drawing.Graphics DummyGraphics =>
            _dummyGfx ??= System.Drawing.Graphics.FromImage(new System.Drawing.Bitmap(1, 1));

        // ── Dispose ────────────────────────────────────────────────────
        public void DisposeResources()
        {
            foreach (var b in _brushCache.Values) b.Dispose();
            _brushCache.Clear();
            foreach (var l in _layerStack) l.Dispose();
            _layerStack.Clear();
        }
    }
}
```

### 3a. Make `PPaintContext` extensible

`PPaintContext` is currently `sealed` with `private readonly` members. To allow `PD2DPaintContext` to share its type token (so `paintContext is PD2DPaintContext` works) while keeping the base class simple, do **one** of the following:

**Option A (recommended — minimal change):** Keep `PPaintContext` sealed. Make `PD2DPaintContext` a completely independent class that inherits only from `object`. Change `PNode.FullPaint`, `PNode.Paint`, and `PCamera.FullPaint` signatures from `PPaintContext` to a new interface `IPaintContext` that both classes implement.

**Option B:** Unseal `PPaintContext` and make `Graphics`, `Scale`, `LocalClip`, and the push/pop methods `virtual`. `PD2DPaintContext` inherits and overrides them.

Option A is safer because it doesn't touch the existing class at all. Option B is less code. The plan uses Option B for conciseness, but Option A is preferred if there is any concern about destabilising the GDI+ path.

For Option B, the minimal changes to `PPaintContext.cs`:
- Remove `sealed`
- Change `private readonly Graphics graphics` → `protected readonly Graphics graphics` (or keep private and expose via a virtual property)
- Add `virtual` to `Graphics`, `Scale`, `LocalClip`, `PushCamera`, `PopCamera`, `Camera`, `PushClip`, `PopClip`, `PushMatrix`, `PopMatrix`
- Change constructor to `protected` (so subclass can call `base(...)` — but `PD2DPaintContext` passes a dummy `Graphics` object and its own `PCanvasWpf`)

For simplicity, keep the constructor signature `PPaintContext(Graphics graphics, PCanvas canvas)` and add a second `protected` constructor `PPaintContext(IPiccoloCanvas canvas)` that skips GDI+ initialisation. `PD2DPaintContext` calls the second constructor.

---

## Step 4 — Create `PCanvasWpf`

**File:** `SharedProjects/Piccolo/PCanvasWpf.cs`

This mirrors `SceneRenderControl` closely. Key differences from `SceneRenderControl`:
- Instead of an abstract `RenderContext`, it owns the Piccolo scene graph directly (like `PCanvas`).
- The `D3DImage_OnRender` callback creates D2D resources from the shared D3D11 texture and calls `camera.FullPaint(new PD2DPaintContext(...))`.
- Mouse/keyboard events are forwarded to `Root.DefaultInputManager.ProcessEventFromCamera`.

```csharp
using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Wpf.Interop.DirectX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.DirectWrite;
using Piccolo.Event;
using Piccolo.Util;
using WpfCursor = System.Windows.Input.Cursor;
using DrawingPoint = System.Drawing.Point;
using DrawingPointF = System.Drawing.PointF;
using D2dFactory = SharpDX.Direct2D1.Factory1;
using D3d11Device = SharpDX.Direct3D11.Device;
using D2dDevice = SharpDX.Direct2D1.Device;
using DwFactory = SharpDX.DirectWrite.Factory;
using DxgiDevice = SharpDX.DXGI.Device;
using D2dDeviceContext = SharpDX.Direct2D1.DeviceContext;

namespace Piccolo
{
    public class PCanvasWpf : ContentControl, IPiccoloCanvas, IDisposable
    {
        // ── D3D/D2D devices (long-lived) ──────────────────────────────
        private D3d11Device   _d3dDevice;
        private D2dDevice     _d2dDevice;
        private D2dFactory    _d2dFactory;
        private DwFactory     _dwFactory;

        // ── Size-dependent resources ───────────────────────────────────
        private D2dDeviceContext _d2dContext;   // recreated on resize

        // ── WPF interop ────────────────────────────────────────────────
        private D3D11Image _d3dImage;
        private System.Windows.Controls.Image _image;
        private bool _initiallyLoaded;

        // ── Piccolo scene graph ────────────────────────────────────────
        private PCamera _camera;
        private Stack<WpfCursor> _cursorStack = new();

        // ── Render quality (mirrors PCanvas) ──────────────────────────
        private RenderQuality _defaultRenderQuality     = RenderQuality.HighQuality;
        private RenderQuality _animatingRenderQuality   = RenderQuality.LowQuality;
        private RenderQuality _interactingRenderQuality = RenderQuality.LowQuality;
        private int _interacting;
        public bool GridFitText { get; set; }

        // ── Delegates (mirrors PCanvas) ───────────────────────────────
        public LowRenderQualityDelegate  LowRenderQuality;
        public HighRenderQualityDelegate HighRenderQuality;

        // ── Constructor ────────────────────────────────────────────────
        public PCanvasWpf()
        {
            if (DesignerProperties.GetIsInDesignMode(this)) return;
            Loaded += OnLoaded;
            Focusable = true;
        }

        // ── Scene graph accessors (mirrors PCanvas) ────────────────────
        public PCamera Camera
        {
            get => _camera;
            set
            {
                if (_camera != null) _camera.Canvas = null;
                _camera = value;
                if (_camera != null)
                {
                    _camera.Canvas = this;  // requires IPiccoloCanvas on PNode.Canvas
                    _camera.Bounds = new RectangleF(0, 0,
                        (float)RenderSize.Width, (float)RenderSize.Height);
                }
            }
        }

        public PRoot Root    => _camera?.Root;
        public PLayer Layer  => _camera?.GetLayer(0);

        public virtual PPanEventHandler PanEventHandler
        {
            get => _panHandler;
            set { /* swap handlers on camera, same as PCanvas */ }
        }

        public virtual PZoomEventHandler ZoomEventHandler
        {
            get => _zoomHandler;
            set { /* swap handlers on camera, same as PCanvas */ }
        }

        public void AddInputEventListener(PInputEventListener l)    => Camera.AddInputEventListener(l);
        public void RemoveInputEventListener(PInputEventListener l) => Camera.RemoveInputEventListener(l);

        // ── IPiccoloCanvas ─────────────────────────────────────────────
        DrawingPointF IPiccoloCanvas.PointToClient(DrawingPoint screenPt)
        {
            // Convert Win32 screen coords to WPF device-independent,
            // then to physical pixels for Piccolo.
            var wpfPt = PointFromScreen(new System.Windows.Point(screenPt.X, screenPt.Y));
            double dpiScale = GetDpiScale();
            return new DrawingPointF((float)(wpfPt.X * dpiScale), (float)(wpfPt.Y * dpiScale));
        }

        // ── InvalidateBounds (called by Piccolo when scene changes) ────
        public virtual void InvalidateBounds(RectangleF bounds)
        {
            _d3dImage?.RequestRender();
        }

        // ── Loaded / Unloaded ──────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_initiallyLoaded)
            {
                var window = Window.GetWindow(this);
                window?.Closing += (_, ce) => { if (!ce.Cancel) Dispose(); };

                _d3dImage = new D3D11Image { OnRender = D3DImage_OnRender };
                _image    = new System.Windows.Controls.Image
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    Source              = _d3dImage
                };
                Content = _image;

                _d3dImage.WindowOwner =
                    new System.Windows.Interop.WindowInteropHelper(window!).Handle;

                CreateDevices();

                Camera   = PUtil.CreateBasicScenegraph();
                PanEventHandler  = new PPanEventHandler();
                ZoomEventHandler = new PZoomEventHandler();

                CompositionTarget.Rendering += OnCompositionTargetRendering;
                SizeChanged += OnSizeChanged;
                _initiallyLoaded = true;
                _d3dImage.SetPixelSize((int)RenderSize.Width, (int)RenderSize.Height);
            }

            Unloaded += OnUnloaded;
            AttachInputHandlers();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DetachInputHandlers();
            Unloaded -= OnUnloaded;
        }

        // ── Device creation ────────────────────────────────────────────
        private void CreateDevices()
        {
            // D3D11 device with BGRA support (required for D2D interop)
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.SingleThreaded;
#if DEBUG
            flags |= DeviceCreationFlags.Debug;
#endif
            _d3dDevice  = new D3d11Device(DriverType.Hardware, flags);

            // D2D factory
            _d2dFactory = new D2dFactory(FactoryType.SingleThreaded);

            // D2D device via DXGI device
            using var dxgiDevice = _d3dDevice.QueryInterface<DxgiDevice>();
            _d2dDevice  = new D2dDevice(_d2dFactory, dxgiDevice);

            // DirectWrite factory (shared singleton is fine)
            _dwFactory  = new DwFactory(SharpDX.DirectWrite.FactoryType.Shared);
        }

        // ── Size-dependent resource creation ──────────────────────────
        private void CreateSizeDependentResources(IntPtr surface, int width, int height)
        {
            DisposeSizeDependentResources();

            // Unwrap the D3D11 texture from the shared surface handle
            var comObj   = SharpDX.CppObject.FromPointer<SharpDX.ComObject>(surface);
            var resource = comObj.QueryInterface<SharpDX.DXGI.Resource>();
            IntPtr sharedHandle = resource.SharedHandle;
            resource.Dispose();

            var d3dResource = _d3dDevice.OpenSharedResource<SharpDX.Direct3D11.Resource>(sharedHandle);
            var texture     = d3dResource.QueryInterface<Texture2D>();
            d3dResource.Dispose();

            // Create D2D render target on the texture
            using var dxgiSurface = texture.QueryInterface<Surface>();
            texture.Dispose();

            var bitmapProps = new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied),
                96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw);

            _d2dContext = new D2dDeviceContext(_d2dDevice, DeviceContextOptions.None);

            using var targetBitmap = new Bitmap1(_d2dContext, dxgiSurface, bitmapProps);
            _d2dContext.Target = targetBitmap;

            // Update camera bounds
            if (_camera != null)
                _camera.Bounds = new RectangleF(0, 0, width, height);
        }

        private void DisposeSizeDependentResources()
        {
            _d2dContext?.Target?.Dispose();
            _d2dContext?.Dispose();
            _d2dContext = null;
        }

        // ── Render callback ────────────────────────────────────────────
        private void D3DImage_OnRender(IntPtr surface, bool isNewSurface)
        {
            int width  = (int)RenderSize.Width;
            int height = (int)RenderSize.Height;

            if (isNewSurface)
                CreateSizeDependentResources(surface, width, height);

            if (_d2dContext == null || _camera == null) return;

            _d2dContext.BeginDraw();
            _d2dContext.Clear(ToRawColor(BackgroundColor));

            // Build paint context and walk the scene graph
            var paintContext = new PD2DPaintContext(_d2dContext, _dwFactory, this);
            SetRenderQuality(paintContext);
            _camera.FullPaint(paintContext);
            paintContext.DisposeResources();

            _d2dContext.EndDraw();
        }

        private void SetRenderQuality(PD2DPaintContext ctx)
        {
            bool animating   = Root?.ActivityScheduler.Animating ?? false;
            bool interacting = _interacting > 0;

            RenderQuality q = _defaultRenderQuality;
            if (animating)   q = _animatingRenderQuality;
            if (interacting) q = _interactingRenderQuality;
            ctx.RenderQuality = q;
        }

        // ── Background colour (replaces WinForms BackColor) ───────────
        public Color BackgroundColor { get; set; } = Color.White;

        private static SharpDX.Mathematics.Interop.RawColor4 ToRawColor(Color c) =>
            new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        // ── Composition target rendering loop ─────────────────────────
        private void OnCompositionTargetRendering(object sender, EventArgs e)
        {
            if (_d2dContext != null)
                _d3dImage?.RequestRender();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _d3dImage?.SetPixelSize((int)RenderSize.Width, (int)RenderSize.Height);
            if (_camera != null)
                _camera.Bounds = new RectangleF(0, 0,
                    (float)RenderSize.Width, (float)RenderSize.Height);
        }

        // ── Cursor management (mirrors PCanvas) ───────────────────────
        public void PushCursor(WpfCursor cursor)
        {
            _cursorStack.Push(Cursor);
            Cursor = cursor;
        }

        public void PopCursor() => Cursor = _cursorStack.Pop();

        // ── Dispose ────────────────────────────────────────────────────
        public void Dispose()
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            DisposeSizeDependentResources();
            _dwFactory?.Dispose();
            _d2dDevice?.Dispose();
            _d2dFactory?.Dispose();
            _d3dDevice?.Dispose();
            _d3dImage?.Dispose();
            GC.SuppressFinalize(this);
        }

        // ── DPI helper ─────────────────────────────────────────────────
        private double GetDpiScale()
        {
            var source = PresentationSource.FromVisual(this);
            return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }

        // ── Input forwarding ──────────────────────────────────────────
        // See Step 6 below for the full input handler block.
    }
}
```

---

## Step 5 — Port `Paint()` methods to Direct2D

Each `Paint()` method keeps its GDI+ path untouched and adds a D2D branch at the top:

### 5a. `PNode.Paint` (fill rectangle with `Brush`)

```csharp
protected virtual void Paint(PPaintContext paintContext)
{
    if (paintContext is PD2DPaintContext d2d)
    {
        if (Brush is SolidBrush sb)
        {
            using var b = new SharpDX.Direct2D1.SolidColorBrush(
                d2d.D2DContext, PD2DPaintContext.ToRawColor(sb.Color));
            d2d.D2DContext.FillRectangle(
                PD2DPaintContext.ToRawRect(Bounds), b);
        }
        return;
    }
    // existing GDI+ path unchanged
    if (Brush != null)
    {
        Graphics g = paintContext.Graphics;
        g.FillRectangle(Brush, Bounds);
    }
}
```

**Note:** For performance, once the basic pipeline is verified, replace `new SolidColorBrush(...)` inside Paint with `d2d.GetBrush(sb.Color)` from the context's brush cache.

### 5b. `PPath.Paint` (fill/stroke a `GraphicsPath`)

This requires converting `GraphicsPath` to a D2D `PathGeometry`. Add a cached geometry field to `PPath`:

```csharp
// In PPath fields:
private SharpDX.Direct2D1.PathGeometry _d2dGeometry;
private bool _d2dGeometryDirty = true;
```

Invalidate `_d2dGeometry` whenever the path changes (in `UpdateBoundsFromPath` and any setter that calls `InvalidatePaint`):

```csharp
_d2dGeometryDirty = true;
_d2dGeometry?.Dispose();
_d2dGeometry = null;
```

Geometry conversion helper (add as a private method on `PPath`):

```csharp
private SharpDX.Direct2D1.PathGeometry GetOrCreateD2DGeometry(
    SharpDX.Direct2D1.Factory1 factory)
{
    if (_d2dGeometry != null && !_d2dGeometryDirty)
        return _d2dGeometry;

    _d2dGeometry?.Dispose();
    _d2dGeometry = new SharpDX.Direct2D1.PathGeometry(factory);
    using var sink = _d2dGeometry.Open();

    var pts   = path.PathData.Points;
    var types = path.PathData.Types;
    bool figureOpen = false;

    for (int i = 0; i < pts.Length; i++)
    {
        byte type = (byte)(types[i] & 0x07);  // mask off flags
        bool close = (types[i] & 0x80) != 0;

        if (type == 0)  // MoveTo
        {
            if (figureOpen) sink.EndFigure(FigureEnd.Open);
            sink.BeginFigure(new RawVector2(pts[i].X, pts[i].Y),
                FigureBegin.Filled);
            figureOpen = true;
        }
        else if (type == 1)  // LineTo
        {
            sink.AddLine(new RawVector2(pts[i].X, pts[i].Y));
        }
        else if (type == 3)  // Bezier (cubic)
        {
            sink.AddBezier(new BezierSegment
            {
                Point1 = new RawVector2(pts[i].X,   pts[i].Y),
                Point2 = new RawVector2(pts[i+1].X, pts[i+1].Y),
                Point3 = new RawVector2(pts[i+2].X, pts[i+2].Y),
            });
            i += 2;
        }

        if (close && figureOpen)
        {
            sink.EndFigure(FigureEnd.Closed);
            figureOpen = false;
        }
    }
    if (figureOpen) sink.EndFigure(FigureEnd.Open);
    sink.Close();
    _d2dGeometryDirty = false;
    return _d2dGeometry;
}
```

Paint method:

```csharp
protected override void Paint(PPaintContext paintContext)
{
    if (paintContext is PD2DPaintContext d2d)
    {
        var geom = GetOrCreateD2DGeometry(d2d.D2DContext.Factory.QueryInterface<Factory1>());
        if (Brush is SolidBrush sb)
        {
            var b = d2d.GetBrush(sb.Color);
            d2d.D2DContext.FillGeometry(geom, b);
        }
        if (pen != null && pen.Brush is SolidBrush pb)
        {
            var b = d2d.GetBrush(pb.Color);
            d2d.D2DContext.DrawGeometry(geom, b, pen.Width);
        }
        return;
    }
    // original GDI+ path unchanged
    Brush b2 = Brush;
    Graphics g = paintContext.Graphics;
    if (b2 != null) g.FillPath(b2, path);
    if (pen != null) g.DrawPath(pen, path);
}
```

### 5c. `PText.Paint` (text rendering via DirectWrite)

```csharp
protected override void Paint(PPaintContext paintContext)
{
    base.Paint(paintContext);  // fills background if Brush set

    if (text == null || textBrush == null || font == null) return;

    if (paintContext is PD2DPaintContext d2d)
    {
        float renderedFontSize = FontSizeInPoints * paintContext.Scale;
        if (renderedFontSize < PUtil.GreekThreshold)
        {
            if (textBrush is SolidBrush sb)
                d2d.D2DContext.FillRectangle(
                    PD2DPaintContext.ToRawRect(Bounds), d2d.GetBrush(sb.Color));
        }
        else if (renderedFontSize < PUtil.MaxFontSize)
        {
            var fmt = GetOrCreateTextFormat(d2d.DWriteFactory);
            using var layout = new TextLayout(d2d.DWriteFactory,
                text, fmt, Bounds.Width, Bounds.Height);
            if (textBrush is SolidBrush sb)
                d2d.D2DContext.DrawTextLayout(
                    new RawVector2(Bounds.X, Bounds.Y), layout,
                    d2d.GetBrush(sb.Color));
        }
        return;
    }
    // original GDI+ path unchanged
    ...
}
```

Add a cached `TextFormat` field to `PText`:

```csharp
private TextFormat _dwTextFormat;

private TextFormat GetOrCreateTextFormat(SharpDX.DirectWrite.Factory factory)
{
    if (_dwTextFormat != null) return _dwTextFormat;
    var weight = font.Bold ? FontWeight.Bold : FontWeight.Regular;
    var style  = font.Italic ? SharpDX.DirectWrite.FontStyle.Italic
                             : SharpDX.DirectWrite.FontStyle.Normal;
    _dwTextFormat = new TextFormat(factory, font.Name, weight,
        style, FontStretch.Normal, FontSizeInPoints);
    // Map StringFormat alignment
    _dwTextFormat.TextAlignment    = MapHAlign(stringFormat);
    _dwTextFormat.ParagraphAlignment = MapVAlign(stringFormat);
    return _dwTextFormat;
}
```

Invalidate `_dwTextFormat` (dispose and null) whenever `Font`, `StringFormat`, or `TextBrush` properties change.

**Text measurement for `RecomputeBounds`:**

`PText.RecomputeBounds` currently calls `GRAPHICS.MeasureString(...)`. This static GDI+ object still works correctly for layout purposes regardless of which renderer is active, so **no change is required** here initially. If pixel-perfect agreement with DirectWrite metrics becomes important later, `RecomputeBounds` can be overridden or given an alternate path that uses a `TextLayout.Metrics` measurement via a long-lived shared `DWriteFactory`.

### 5d. `PImage.Paint` (bitmap rendering)

```csharp
protected override void Paint(PPaintContext paintContext)
{
    if (Image == null) return;
    RectangleF b = Bounds;

    if (paintContext is PD2DPaintContext d2d)
    {
        var bmp = GetOrCreateD2DBitmap(d2d.D2DContext);
        if (bmp != null)
            d2d.D2DContext.DrawBitmap(bmp,
                PD2DPaintContext.ToRawRect(b),
                1.0f,
                BitmapInterpolationMode.Linear);
        return;
    }
    // original
    Graphics g = paintContext.Graphics;
    g.DrawImage(image, b);
}
```

Add a cached D2D bitmap field to `PImage`:

```csharp
private SharpDX.Direct2D1.Bitmap _d2dBitmap;

private SharpDX.Direct2D1.Bitmap GetOrCreateD2DBitmap(DeviceContext ctx)
{
    if (_d2dBitmap != null) return _d2dBitmap;
    if (image is not System.Drawing.Bitmap bmp) return null;

    var data = bmp.LockBits(
        new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
        System.Drawing.Imaging.ImageLockMode.ReadOnly,
        System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    try
    {
        var props = new BitmapProperties(
            new PixelFormat(Format.B8G8R8A8_UNorm,
                            SharpDX.Direct2D1.AlphaMode.Premultiplied));
        _d2dBitmap = new SharpDX.Direct2D1.Bitmap(ctx,
            new SharpDX.Size2(bmp.Width, bmp.Height),
            new SharpDX.DataPointer(data.Scan0, data.Stride * bmp.Height),
            data.Stride, props);
    }
    finally
    {
        bmp.UnlockBits(data);
    }
    return _d2dBitmap;
}
```

Invalidate `_d2dBitmap` (dispose + null) in the `Image` property setter so a new bitmap is created next frame if the image changes.

---

## Step 6 — Input handling in `PCanvasWpf`

The goal is to map WPF mouse/keyboard events into Piccolo `PInputType` events, in the same way `PCanvas` does with WinForms events. Piccolo's `PInputManager` consumes `System.Windows.Forms.MouseEventArgs` and `System.Windows.Forms.KeyEventArgs` — WinForms types. Because those types are thin value structs, the cleanest approach is to **re-use them** by constructing synthetic WinForms event args from WPF event data.

```csharp
private void AttachInputHandlers()
{
    PreviewMouseDown  += Canvas_MouseDown;
    PreviewMouseMove  += Canvas_MouseMove;
    PreviewMouseUp    += Canvas_MouseUp;
    PreviewMouseWheel += Canvas_MouseWheel;
    KeyDown           += Canvas_KeyDown;
    KeyUp             += Canvas_KeyUp;
}

private void DetachInputHandlers() { /* mirror of attach */ }

private void Canvas_MouseDown(object s, MouseButtonEventArgs e)
{
    Focus();
    var pos = GetPixelPosition(e);
    var wfArgs = new System.Windows.Forms.MouseEventArgs(
        ToWfButtons(e.ChangedButton), 1, pos.X, pos.Y, 0);
    Root.DefaultInputManager.ProcessEventFromCamera(
        wfArgs, PInputType.MouseDown, Camera, this);
}

private void Canvas_MouseMove(object s, MouseEventArgs e)
{
    var pos    = GetPixelPosition(e);
    var wfArgs = new System.Windows.Forms.MouseEventArgs(
        ToWfMouseButtons(e), 0, pos.X, pos.Y, 0);
    bool isDrag = e.LeftButton  == MouseButtonState.Pressed
               || e.RightButton == MouseButtonState.Pressed
               || e.MiddleButton == MouseButtonState.Pressed;
    Root.DefaultInputManager.ProcessEventFromCamera(
        wfArgs, isDrag ? PInputType.MouseDrag : PInputType.MouseMove, Camera, this);
}

// MouseUp and MouseWheel follow the same pattern.

private void Canvas_KeyDown(object s, KeyEventArgs e)
{
    var wfArgs = new System.Windows.Forms.KeyEventArgs(ToWfKey(e.Key));
    Root.DefaultInputManager.ProcessEventFromCamera(
        wfArgs, PInputType.KeyDown, Camera, this);
}

// Helper: WPF position → integer pixel coords
private (int X, int Y) GetPixelPosition(MouseEventArgs e)
{
    var wpfPt   = e.GetPosition(this);
    double scale = GetDpiScale();
    return ((int)(wpfPt.X * scale), (int)(wpfPt.Y * scale));
}
```

Key mapping (`ToWfKey`) maps `System.Windows.Input.Key` to `System.Windows.Forms.Keys` — a straightforward `switch` or cast because both enums share the same underlying Win32 virtual key values.

DragDrop: If the consumers of `PCanvasWpf` do not need drag-drop support, omit it for now. If needed, handle `Drop` / `DragOver` WPF events and convert `DragEventArgs` similarly, noting that `IPiccoloCanvas.PointToClient` handles the coordinate translation.

---

## Step 7 — DPI / coordinate consistency

WPF logical pixels are 1/96 inch. When the display runs at a scale factor (e.g., 150%), `RenderSize` reports logical pixels but `D3D11Image.SetPixelSize` and all D2D drawing must use physical pixels.

- `D3D11Image.SetPixelSize(physicalWidth, physicalHeight)` — always pass the physical size:
  ```csharp
  double scale = GetDpiScale();
  _d3dImage.SetPixelSize(
      (int)(RenderSize.Width  * scale),
      (int)(RenderSize.Height * scale));
  ```

- Mouse positions passed to Piccolo must be in the same coordinate space (physical pixels). `GetPixelPosition` in Step 6 already does this.

- `_camera.Bounds` must be set in physical pixels so that Piccolo's layout and hit-testing match the rendered output:
  ```csharp
  _camera.Bounds = new RectangleF(0, 0,
      (float)(RenderSize.Width  * dpiScale),
      (float)(RenderSize.Height * dpiScale));
  ```

- Callers that previously set `PCanvas.Width`/`Height` directly will now need to set `PCanvasWpf.Width`/`Height` in WPF logical units; the above scaling handles the conversion internally.

---

## Step 8 — `PCamera.Canvas` typing

`PCamera.Canvas` is currently typed `PCanvas`. It is used to call `canvas.InvalidateBounds(rect)` when the scene changes. Since `PCanvasWpf` is not a `PCanvas`, this must be widened.

Change `PCamera.Canvas` from `PCanvas` to `IPiccoloCanvas` and add `InvalidateBounds(RectangleF)` to `IPiccoloCanvas`:

```csharp
public interface IPiccoloCanvas
{
    System.Drawing.PointF PointToClient(System.Drawing.Point pt);
    void InvalidateBounds(System.Drawing.RectangleF bounds);
}
```

`PCanvas` already has `InvalidateBounds`; add it to the interface declaration. `PCanvasWpf` calls `_d3dImage?.RequestRender()` in its implementation.

Search the codebase for any other places `PCanvas` is used as a type rather than through its API (e.g., casts, `is PCanvas` checks) and widen each one.

---

## Step 9 — `PNode.Canvas` typing

`PNode` has a `Canvas` property (used by some nodes and event handlers to access the canvas). Verify its type and update to `IPiccoloCanvas` if needed:

```bash
grep -n "PCanvas" SharedProjects/Piccolo/PNode.cs
grep -n "PCanvas" SharedProjects/Piccolo/PCamera.cs
grep -n "PCanvas" SharedProjects/Piccolo/PLayer.cs
grep -n "PCanvas" SharedProjects/Piccolo/PRoot.cs
grep -n "\.Canvas" SharedProjects/Piccolo/PNode.cs | head -30
```

Everywhere the type is `PCanvas`, change to `IPiccoloCanvas`.

---

## Step 10 — Wire up in a consumer window

Find the first consumer of `PCanvas` (likely a graph editor tool in `LegendaryExplorer`) and replace it with `PCanvasWpf` to validate the pipeline end to end. Good first candidates:

```bash
grep -rn "PCanvas" LegendaryExplorer/LegendaryExplorer/Tools/ --include="*.cs" -l
grep -rn "PCanvas" LegendaryExplorer/LegendaryExplorer/Tools/ --include="*.xaml" -l
```

Steps to replace one consumer:
1. Remove the `WindowsFormsHost` wrapper (if any).
2. Replace `PCanvas canvas = new PCanvas()` with `PCanvasWpf canvas = new PCanvasWpf()`.
3. Move any `canvas.Camera = ...` / event handler wiring — the API is intentionally identical.
4. Build and run. Confirm the scene renders. Confirm mouse pan/zoom work.

---

## Step 11 — Debug rendering mode

Add a compile-time flag `PICCOLO_D2D_DEBUG` that inserts a bright red 1px border around every node's bounds when rendering in D2D. This makes it easy to verify that the transform stack and clip stack are working correctly. Remove before merging to main.

---

## Known risks and mitigations

| Risk | Mitigation |
|---|---|
| `PPaintContext` is `sealed` — inheritance blocked | Use Option A (interface) or unseal (Option B) as described in §3a |
| `GraphicsPath.PathData` bezier segments: GDI+ emits cubic beziers as 3 consecutive points with type `3`; correct parsing is required | Verify with `Debug.Assert(types[i+1] & 0x07 == 3)` during development |
| `System.Drawing.Pen.Brush` may not be `SolidBrush` (e.g. `HatchBrush`, `TextureBrush`) | Log a warning and fall back to black stroke; extend later |
| `System.Drawing.Brush` may not be `SolidBrush` | Same fallback; Piccolo uses `SolidBrush` almost exclusively in practice |
| D2D device lost (GPU driver reset) | Catch `SharpDX.SharpDXException` with HRESULT `D2DERR_RECREATE_TARGET` / `DXGI_ERROR_DEVICE_REMOVED` in `D3DImage_OnRender`, set size-dependent resources to null, let `isNewSurface = true` on the next callback trigger recreation |
| DPI change at runtime (user moves window to different monitor) | Subscribe to `Window.DpiChanged` (WPF 4.6.2+) and call `SetPixelSize` + reset camera bounds |
| PText bounds measured with GDI+ but rendered with DirectWrite — slight size mismatch | Acceptable for now; document as a known discrepancy. Full fix: replace `GRAPHICS.MeasureString` with a `TextLayout.Metrics` measurement |

---

## File checklist for the implementing agent

- [ ] `SharedProjects/Piccolo/Piccolo.csproj` — add NuGet refs + `<UseWPF>true</UseWPF>`
- [ ] `SharedProjects/Piccolo/IPiccoloCanvas.cs` — new interface
- [ ] `SharedProjects/Piccolo/PCanvas.cs` — implement `IPiccoloCanvas`
- [ ] `SharedProjects/Piccolo/PInputManager.cs` — widen `PCanvas` param to `IPiccoloCanvas`
- [ ] `SharedProjects/Piccolo/PCamera.cs` — widen `Canvas` property to `IPiccoloCanvas`; check all other `PCanvas` usages in this file
- [ ] `SharedProjects/Piccolo/PNode.cs` — widen `Canvas` property if present; D2D branch in `Paint()`
- [ ] `SharedProjects/Piccolo/Util/PPaintContext.cs` — unseal + add virtual members (Option B) OR leave unchanged (Option A)
- [ ] `SharedProjects/Piccolo/Util/PD2DPaintContext.cs` — new file
- [ ] `SharedProjects/Piccolo/PCanvasWpf.cs` — new file
- [ ] `SharedProjects/Piccolo/Nodes/PPath.cs` — D2D branch in `Paint()`, geometry cache
- [ ] `SharedProjects/Piccolo/Nodes/PText.cs` — D2D branch in `Paint()`, TextFormat cache
- [ ] `SharedProjects/Piccolo/Nodes/PImage.cs` — D2D branch in `Paint()`, bitmap cache
- [ ] One consumer window — replace `PCanvas` with `PCanvasWpf` to validate end-to-end
