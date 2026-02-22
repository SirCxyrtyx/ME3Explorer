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
- `PCanvas` is a WinForms `Control`. It is the root of all WinForms coupling.
- `PPaintContext` wraps a GDI+ `Graphics` object. All nodes call `paintContext.Graphics` directly.
- The four nodes that paint:
  - `PNode.Paint` — `g.FillRectangle(Brush, Bounds)`
  - `PPath.Paint` — `g.FillPath(b, path)` / `g.DrawPath(pen, path)`
  - `PText.Paint` — `g.DrawString(text, font, brush, bounds, format)` and `g.FillRectangle` for greek text
  - `PImage.Paint` — `g.DrawImage(image, bounds)`
- `PText` has a static `GRAPHICS = Graphics.FromImage(new Bitmap(1,1))` used in `RecomputeBounds()` for text measurement.
- `PPaintContext.PushMatrix` calls `graphics.MultiplyTransform` and saves/restores `graphics.Transform`.
- `PPaintContext.PushClip` calls `graphics.Clip = intersectedRegion`.
- `PCanvas` is referenced inside the framework in five distinct places beyond the rendering path:

| Location | What it uses from `PCanvas` |
|---|---|
| `PInputManager` | `nextWindowsSource.PointToClient(pt)` (drag-drop only) |
| `PCamera` | `private PCanvas canvas` field / `Canvas` property |
| `PRoot.InvokeCanvas` | `canvas.BeginInvoke(delegate)`, `canvas.IsHandleCreated` |
| `PPaintContext` constructor | Stored and returned via `Canvas` property |
| `PInputEventArgs` | `TopCamera.Canvas.PushCursor()` / `PopCursor()` |

### Goal

Replace the WinForms `PCanvas` and GDI+ rendering entirely. The outcome is:

- `PCanvasWpf` is the **only** canvas. `PCanvas` is deleted.
- `PD2DPaintContext` is the **only** paint context. `PPaintContext` is deleted.
- All `Paint()` methods render via Direct2D only — no conditional branches, no GDI+ fallback.
- All internal framework references to `PCanvas` or `PPaintContext` are changed to the new types directly. No abstraction interface is introduced, since there will be only one implementation of each.

---

## Files to Delete

| File | Reason |
|---|---|
| `SharedProjects/Piccolo/PCanvas.cs` | Replaced by `PCanvasWpf` |
| `SharedProjects/Piccolo/PCanvas.resx` | Resource file for the deleted control |
| `SharedProjects/Piccolo/Util/PPaintContext.cs` | Replaced by `PD2DPaintContext` |

## Files to Create

| File | Purpose |
|---|---|
| `SharedProjects/Piccolo/PCanvasWpf.cs` | WPF host control; the sole canvas implementation |
| `SharedProjects/Piccolo/Util/PD2DPaintContext.cs` | D2D paint context; the sole paint context implementation |

## Files to Modify

| File | Change |
|---|---|
| `SharedProjects/Piccolo/Piccolo.csproj` | Add SharpDX NuGet refs, `<UseWPF>true</UseWPF>`, remove WinForms refs |
| `SharedProjects/Piccolo/PCamera.cs` | `PCanvas canvas` → `PCanvasWpf canvas` |
| `SharedProjects/Piccolo/PRoot.cs` | `InvokeCanvas` returns `PCanvasWpf`; use `Dispatcher.BeginInvoke` / `IsLoaded` |
| `SharedProjects/Piccolo/PInputManager.cs` | `PCanvas` param → `PCanvasWpf` |
| `SharedProjects/Piccolo/Event/PInputEventArgs.cs` | `Canvas` property → `PCanvasWpf`; cursor type → WPF `Cursor` |
| `SharedProjects/Piccolo/PNode.cs` | `Paint(PPaintContext)` → `Paint(PD2DPaintContext)`; replace GDI+ fill |
| `SharedProjects/Piccolo/Nodes/PPath.cs` | `Paint` → D2D; geometry cache |
| `SharedProjects/Piccolo/Nodes/PText.cs` | `Paint` → D2D; DirectWrite measurement |
| `SharedProjects/Piccolo/Nodes/PImage.cs` | `Paint` → D2D; bitmap cache |
| `SharedProjects/Piccolo/Activities/PActivity.cs` | `PCanvas` refs → `PCanvasWpf` |
| `SharedProjects/Piccolo/Activities/PColorActivity.cs` | `PCanvas` refs → `PCanvasWpf` |
| `SharedProjects/Piccolo/Activities/PTransformActivity.cs` | `PCanvas` refs → `PCanvasWpf` |
| Four graph editor files | Subclass `PCanvasWpf` instead of `PCanvas` |

---

## Step 1 — Update `Piccolo.csproj`

The project currently targets `net6.0-windows` with WinForms enabled. Replace WinForms with WPF and add SharpDX references.

```xml
<PropertyGroup>
  <UseWPF>true</UseWPF>
  <!-- Remove <UseWindowsForms>true</UseWindowsForms> if present -->
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.Wpf.Interop.DirectX-x64" Version="0.9.0-beta-22856" />
  <PackageReference Include="SharpDX" Version="4.2.0" NoWarn="NU1701" />
  <PackageReference Include="SharpDX.Direct2D1" Version="4.2.0" NoWarn="NU1701" />
  <PackageReference Include="SharpDX.Direct3D11" Version="4.2.0" NoWarn="NU1701" />
  <PackageReference Include="SharpDX.DXGI" Version="4.2.0" NoWarn="NU1701" />
  <PackageReference Include="SharpDX.Mathematics" Version="4.2.0" NoWarn="NU1701" />
</ItemGroup>
```

`SharpDX` targets .NET Framework; `NoWarn="NU1701"` suppresses the compatibility warning. The `Microsoft.Wpf.Interop.DirectX-x64` package provides `D3D11Image`.

---

## Step 2 — Create `PD2DPaintContext`

**File:** `SharedProjects/Piccolo/Util/PD2DPaintContext.cs`

This is a standalone class — not a subclass of `PPaintContext`. It owns the same conceptual stacks (transform, clip, camera) that `PPaintContext` maintained, but implemented against Direct2D types.

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using SharpDX.Mathematics.Interop;
using DwFactory = SharpDX.DirectWrite.Factory;

namespace Piccolo.Util
{
    public sealed class PD2DPaintContext
    {
        // ── Core D2D objects ──────────────────────────────────────────
        public DeviceContext D2DContext  { get; }
        public DwFactory DWriteFactory  { get; }

        // ── Stacks ────────────────────────────────────────────────────
        private readonly Stack<RawMatrix3x2> _transformStack = new();
        private readonly Stack<RectangleF>   _clipStack      = new();
        private readonly Stack<PCamera>      _cameraStack    = new();

        // ── Resource cache ────────────────────────────────────────────
        private readonly Dictionary<int, SolidColorBrush> _brushCache = new();

        // ── Render quality ────────────────────────────────────────────
        public RenderQuality RenderQuality { get; set; }

        public PD2DPaintContext(DeviceContext d2dContext, DwFactory dwFactory)
        {
            D2DContext    = d2dContext;
            DWriteFactory = dwFactory;
            _clipStack.Push(new RectangleF(0, 0,
                d2dContext.Size.Width, d2dContext.Size.Height));
        }

        // ── Scale ─────────────────────────────────────────────────────
        public float Scale
        {
            get
            {
                var t = D2DContext.Transform;
                return MathF.Sqrt(t.M11 * t.M11 + t.M12 * t.M12);
            }
        }

        // ── Local clip ────────────────────────────────────────────────
        public RectangleF LocalClip => _clipStack.Peek();

        // ── Camera stack ──────────────────────────────────────────────
        public void PushCamera(PCamera camera) => _cameraStack.Push(camera);
        public void PopCamera()                => _cameraStack.Pop();
        public PCamera Camera                  => _cameraStack.Peek();

        // ── Transform stack ───────────────────────────────────────────
        public void PushMatrix(PMatrix matrix)
        {
            if (matrix == null) return;
            _transformStack.Push(D2DContext.Transform);
            _clipStack.Push(matrix.InverseTransform(LocalClip));

            var m       = matrix.MatrixReference;
            var current = D2DContext.Transform;
            D2DContext.Transform = Multiply(ToRaw(m), current);
        }

        public void PopMatrix()
        {
            D2DContext.Transform = _transformStack.Pop();
            _clipStack.Pop();
        }

        // ── Clip stack ────────────────────────────────────────────────
        // Piccolo clips are always axis-aligned rectangles, so
        // PushAxisAlignedClip is sufficient.
        public void PushClip(System.Drawing.Region aClip)
        {
            RectangleF gdiRect  = aClip.GetBounds(DummyGraphics);
            RectangleF newLocal = RectangleF.Intersect(LocalClip, gdiRect);
            _clipStack.Push(newLocal);
            D2DContext.PushAxisAlignedClip(ToRawRect(newLocal),
                AntialiasMode.PerPrimitive);
        }

        public void PopClip()
        {
            D2DContext.PopAxisAlignedClip();
            _clipStack.Pop();
        }

        // ── Brush cache ───────────────────────────────────────────────
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

        // ── Conversion helpers ────────────────────────────────────────
        public static RawColor4 ToRawColor(Color c) =>
            new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        public static RawMatrix3x2 ToRaw(System.Numerics.Matrix3x2 m) =>
            new(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);

        public static RawRectangleF ToRawRect(RectangleF r) =>
            new(r.Left, r.Top, r.Right, r.Bottom);

        private static RawMatrix3x2 Multiply(RawMatrix3x2 a, RawMatrix3x2 b) =>
            new(a.M11*b.M11 + a.M12*b.M21,
                a.M11*b.M12 + a.M12*b.M22,
                a.M21*b.M11 + a.M22*b.M21,
                a.M21*b.M12 + a.M22*b.M22,
                a.M31*b.M11 + a.M32*b.M21 + b.M31,
                a.M31*b.M12 + a.M32*b.M22 + b.M32);

        // Lazily-created dummy GDI Graphics for Region.GetBounds()
        private static System.Drawing.Graphics _dummyGfx;
        private static System.Drawing.Graphics DummyGraphics =>
            _dummyGfx ??= System.Drawing.Graphics.FromImage(
                new System.Drawing.Bitmap(1, 1));

        // ── Dispose ───────────────────────────────────────────────────
        public void DisposeResources()
        {
            foreach (var b in _brushCache.Values) b.Dispose();
            _brushCache.Clear();
        }
    }
}
```

**Note on `DummyGraphics`:** The only remaining GDI+ dependency is `Region.GetBounds()`, which requires a `Graphics` object as a measurement context. This is a one-pixel scratch bitmap that is never rendered. If eliminating the `System.Drawing` dependency entirely becomes a goal, `PushClip` can be changed to accept `RectangleF` directly and all call sites updated.

---

## Step 3 — Update traversal signatures in the scene-graph core

`PPaintContext` appears in the signatures of `FullPaint`, `FullPaintBounds`, `Paint`, `PaintBounds` across `PNode`, `PCamera`, `PLayer`, and `PPickPath`. Replace every occurrence of `PPaintContext` with `PD2DPaintContext`. Because there is now only one paint context type, no casts or branches are needed anywhere.

Files to update:
- `SharedProjects/Piccolo/PNode.cs`
- `SharedProjects/Piccolo/PCamera.cs`
- `SharedProjects/Piccolo/PLayer.cs`
- `SharedProjects/Piccolo/Util/PPickPath.cs`

---

## Step 4 — Rewrite `Paint()` methods for Direct2D

Each node's `Paint` method is replaced outright — no GDI+ fallback branch.

### 4a. `PNode.Paint`

```csharp
protected virtual void Paint(PD2DPaintContext ctx)
{
    if (Brush is SolidBrush sb)
        ctx.D2DContext.FillRectangle(
            PD2DPaintContext.ToRawRect(Bounds),
            ctx.GetBrush(sb.Color));
}
```

### 4b. `PPath.Paint`

Requires converting `GraphicsPath` → D2D `PathGeometry`. Cache the geometry; invalidate (dispose + null) whenever the path changes (in `UpdateBoundsFromPath` and any setter that calls `InvalidatePaint`).

```csharp
// Field on PPath:
private SharpDX.Direct2D1.PathGeometry _d2dGeometry;

private SharpDX.Direct2D1.PathGeometry GetOrCreateGeometry(Factory1 factory)
{
    if (_d2dGeometry != null) return _d2dGeometry;

    _d2dGeometry = new PathGeometry(factory);
    using var sink = _d2dGeometry.Open();

    var pts   = path.PathData.Points;
    var types = path.PathData.Types;
    bool figureOpen = false;

    for (int i = 0; i < pts.Length; i++)
    {
        byte type  = (byte)(types[i] & 0x07);
        bool close = (types[i] & 0x80) != 0;

        if (type == 0)        // MoveTo
        {
            if (figureOpen) sink.EndFigure(FigureEnd.Open);
            sink.BeginFigure(new RawVector2(pts[i].X, pts[i].Y), FigureBegin.Filled);
            figureOpen = true;
        }
        else if (type == 1)   // LineTo
        {
            sink.AddLine(new RawVector2(pts[i].X, pts[i].Y));
        }
        else if (type == 3)   // Cubic bezier — GDI+ emits 3 consecutive points
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
    return _d2dGeometry;
}

protected override void Paint(PD2DPaintContext ctx)
{
    var factory = ctx.D2DContext.Factory.QueryInterface<Factory1>();
    var geom    = GetOrCreateGeometry(factory);

    if (Brush is SolidBrush sb)
        ctx.D2DContext.FillGeometry(geom, ctx.GetBrush(sb.Color));

    if (pen?.Brush is SolidBrush pb)
        ctx.D2DContext.DrawGeometry(geom, ctx.GetBrush(pb.Color), pen.Width);
}
```

**Risk:** `System.Drawing.Pen.Brush` or `System.Drawing.Brush` may not be `SolidBrush` (e.g. `HatchBrush`, `TextureBrush`). Log a warning and fall back to black; extend later. Piccolo uses `SolidBrush` almost exclusively in practice.

### 4c. `PText.Paint`

```csharp
// Field on PText:
private TextFormat _dwTextFormat;

private TextFormat GetOrCreateTextFormat(DwFactory factory)
{
    if (_dwTextFormat != null) return _dwTextFormat;
    _dwTextFormat = new TextFormat(factory,
        font.Name,
        font.Bold   ? FontWeight.Bold    : FontWeight.Regular,
        font.Italic ? FontStyle.Italic   : FontStyle.Normal,
        FontStretch.Normal,
        FontSizeInPoints);
    _dwTextFormat.TextAlignment      = MapHAlign(stringFormat);
    _dwTextFormat.ParagraphAlignment = MapVAlign(stringFormat);
    return _dwTextFormat;
}

protected override void Paint(PD2DPaintContext ctx)
{
    base.Paint(ctx);  // fills background if Brush set

    if (text == null || textBrush == null || font == null) return;
    if (textBrush is not SolidBrush sb) return;

    float renderedFontSize = FontSizeInPoints * ctx.Scale;

    if (renderedFontSize < PUtil.GreekThreshold)
    {
        ctx.D2DContext.FillRectangle(
            PD2DPaintContext.ToRawRect(Bounds), ctx.GetBrush(sb.Color));
    }
    else if (renderedFontSize < PUtil.MaxFontSize)
    {
        var fmt    = GetOrCreateTextFormat(ctx.DWriteFactory);
        using var layout = new TextLayout(ctx.DWriteFactory,
            text, fmt, Bounds.Width, Bounds.Height);
        ctx.D2DContext.DrawTextLayout(
            new RawVector2(Bounds.X, Bounds.Y),
            layout,
            ctx.GetBrush(sb.Color));
    }
}
```

Invalidate `_dwTextFormat` (dispose + null) whenever `Font`, `StringFormat`, or `TextBrush` change.

**Text measurement:** `PText.RecomputeBounds` currently calls the static `GRAPHICS.MeasureString(...)`. This keeps working unchanged and can be replaced with `TextLayout.Metrics` later if pixel-perfect agreement with DirectWrite becomes necessary.

### 4d. `PImage.Paint`

```csharp
// Field on PImage:
private SharpDX.Direct2D1.Bitmap _d2dBitmap;

private SharpDX.Direct2D1.Bitmap GetOrCreateBitmap(DeviceContext ctx)
{
    if (_d2dBitmap != null) return _d2dBitmap;
    if (image is not System.Drawing.Bitmap bmp) return null;

    var data = bmp.LockBits(
        new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
        System.Drawing.Imaging.ImageLockMode.ReadOnly,
        System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    try
    {
        _d2dBitmap = new SharpDX.Direct2D1.Bitmap(ctx,
            new SharpDX.Size2(bmp.Width, bmp.Height),
            new SharpDX.DataPointer(data.Scan0, data.Stride * bmp.Height),
            data.Stride,
            new BitmapProperties(new PixelFormat(
                Format.B8G8R8A8_UNorm,
                SharpDX.Direct2D1.AlphaMode.Premultiplied)));
    }
    finally { bmp.UnlockBits(data); }

    return _d2dBitmap;
}

protected override void Paint(PD2DPaintContext ctx)
{
    if (Image == null) return;
    var bmp = GetOrCreateBitmap(ctx.D2DContext);
    if (bmp != null)
        ctx.D2DContext.DrawBitmap(bmp,
            PD2DPaintContext.ToRawRect(Bounds),
            1.0f,
            BitmapInterpolationMode.Linear);
}
```

Invalidate `_d2dBitmap` (dispose + null) in the `Image` property setter.

---

## Step 5 — Update internal framework `PCanvas` references

### 5a. `PCamera`

```csharp
// Before:
private PCanvas canvas;
public PCanvas Canvas { get => canvas; set { ... } }

// After:
private PCanvasWpf canvas;
public PCanvasWpf Canvas { get => canvas; set { ... } }
```

The setter calls `canvas.InvalidateBounds(rect)` — this becomes `canvas.RequestRedraw()` (or equivalent; see `PCanvasWpf` definition in Step 6).

### 5b. `PRoot`

`InvokeCanvas` currently uses `canvas.IsHandleCreated` (WinForms) and `canvas.BeginInvoke(delegate)` (WinForms). Replace with WPF equivalents:

```csharp
// Before:
private PCanvas InvokeCanvas { get { ... return camera.Canvas; } }

if (canvas is { IsHandleCreated: true } && ...)
    canvas.BeginInvoke(processScheduledInputsDelegate);

// After:
private PCanvasWpf InvokeCanvas { get { ... return camera.Canvas; } }

if (canvas is { IsLoaded: true } && ...)
    canvas.Dispatcher.BeginInvoke(processScheduledInputsDelegate);
```

### 5c. `PInputManager`

```csharp
// Before:
private PCanvas nextWindowsSource;
public void ProcessEventFromCamera(EventArgs e, PInputType type, PCamera camera, PCanvas canvas)

// After:
private PCanvasWpf nextWindowsSource;
public void ProcessEventFromCamera(EventArgs e, PInputType type, PCamera camera, PCanvasWpf canvas)
```

The one use of `nextWindowsSource` is `nextWindowsSource.PointToClient(pt)` during drag-drop. `PCanvasWpf` implements this method (see Step 6).

### 5d. `PInputEventArgs`

```csharp
// Before:
public PCanvas Canvas => TopCamera.Canvas;
public void PushCursor(Cursor cursor) => TopCamera.Canvas.PushCursor(cursor);
public void PopCursor()               => TopCamera.Canvas.PopCursor();

// After:
public PCanvasWpf Canvas => TopCamera.Canvas;
public void PushCursor(System.Windows.Input.Cursor cursor) => TopCamera.Canvas.PushCursor(cursor);
public void PopCursor()                                    => TopCamera.Canvas.PopCursor();
```

**Note:** `Cursor` changes from `System.Windows.Forms.Cursor` to `System.Windows.Input.Cursor`. Any event handlers in consumer code that call `PushCursor`/`PopCursor` with WinForms cursors will need updating.

### 5e. Activity files

Check `PActivity.cs`, `PColorActivity.cs`, `PTransformActivity.cs` for `PCanvas` references and update to `PCanvasWpf`. These are likely only using the canvas to call `Invalidate()` / `Update()` — replace with `RequestRedraw()`.

---

## Step 6 — Create `PCanvasWpf`

**File:** `SharedProjects/Piccolo/PCanvasWpf.cs`

Mirrors `SceneRenderControl` for the D3D11/D2D device lifecycle, and mirrors `PCanvas` for the Piccolo scene-graph API surface.

```csharp
using System;
using System.Collections.Generic;
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
using WpfCursor    = System.Windows.Input.Cursor;
using DrawingPoint = System.Drawing.Point;
using DrawingPointF = System.Drawing.PointF;
using D2dFactory   = SharpDX.Direct2D1.Factory1;
using D3d11Device  = SharpDX.Direct3D11.Device;
using D2dDevice    = SharpDX.Direct2D1.Device;
using DwFactory    = SharpDX.DirectWrite.Factory;
using DxgiDevice   = SharpDX.DXGI.Device;
using D2dCtx       = SharpDX.Direct2D1.DeviceContext;

namespace Piccolo
{
    public class PCanvasWpf : ContentControl, IDisposable
    {
        // ── D3D/D2D devices (long-lived) ──────────────────────────────
        private D3d11Device _d3dDevice;
        private D2dDevice   _d2dDevice;
        private D2dFactory  _d2dFactory;
        private DwFactory   _dwFactory;

        // ── Size-dependent resources ───────────────────────────────────
        private D2dCtx _d2dContext;

        // ── WPF interop ────────────────────────────────────────────────
        private D3D11Image _d3dImage;
        private System.Windows.Controls.Image _image;
        private bool _initiallyLoaded;

        // ── Piccolo scene graph ────────────────────────────────────────
        private PCamera _camera;
        private readonly Stack<WpfCursor> _cursorStack = new();

        // ── Render quality ─────────────────────────────────────────────
        public Color BackgroundColor { get; set; } = Color.White;

        // ── Constructor ────────────────────────────────────────────────
        public PCanvasWpf()
        {
            Loaded   += OnLoaded;
            Focusable = true;
        }

        // ── Scene-graph accessors (mirrors PCanvas) ────────────────────
        public PCamera Camera
        {
            get => _camera;
            set
            {
                if (_camera != null) _camera.Canvas = null;
                _camera = value;
                if (_camera != null)
                {
                    _camera.Canvas = this;
                    _camera.Bounds = PhysicalBounds();
                }
            }
        }

        public PRoot  Root  => _camera?.Root;
        public PLayer Layer => _camera?.GetLayer(0);

        public void AddInputEventListener(PInputEventListener l)    => Camera.AddInputEventListener(l);
        public void RemoveInputEventListener(PInputEventListener l) => Camera.RemoveInputEventListener(l);

        // ── Methods called by framework internals ──────────────────────
        public DrawingPointF PointToClient(DrawingPoint screenPt)
        {
            var wpfPt    = PointFromScreen(new Point(screenPt.X, screenPt.Y));
            double scale = DpiScale();
            return new DrawingPointF((float)(wpfPt.X * scale), (float)(wpfPt.Y * scale));
        }

        public void RequestRedraw() => _d3dImage?.RequestRender();

        public void PushCursor(WpfCursor cursor) { _cursorStack.Push(Cursor); Cursor = cursor; }
        public void PopCursor()                  { Cursor = _cursorStack.Pop(); }

        // ── Loaded / Unloaded ──────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_initiallyLoaded)
            {
                var window = Window.GetWindow(this)!;
                window.Closing += (_, ce) => { if (!ce.Cancel) Dispose(); };

                _d3dImage = new D3D11Image { OnRender = D3DImage_OnRender };
                _image    = new System.Windows.Controls.Image
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    Source              = _d3dImage,
                };
                Content = _image;

                _d3dImage.WindowOwner =
                    new System.Windows.Interop.WindowInteropHelper(window).Handle;

                CreateDevices();
                Camera           = PUtil.CreateBasicScenegraph();
                Camera.Canvas    = this;

                CompositionTarget.Rendering += OnCompositionTargetRendering;
                SizeChanged += OnSizeChanged;
                _initiallyLoaded = true;
                SetPixelSize();
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
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.SingleThreaded;
#if DEBUG
            flags |= DeviceCreationFlags.Debug;
#endif
            _d3dDevice  = new D3d11Device(DriverType.Hardware, flags);
            _d2dFactory = new D2dFactory(FactoryType.SingleThreaded);

            using var dxgi = _d3dDevice.QueryInterface<DxgiDevice>();
            _d2dDevice  = new D2dDevice(_d2dFactory, dxgi);
            _dwFactory  = new DwFactory(SharpDX.DirectWrite.FactoryType.Shared);
        }

        // ── Size-dependent resources ───────────────────────────────────
        private void CreateSizeDependentResources(IntPtr surface, int w, int h)
        {
            DisposeSizeDependentResources();

            var comObj   = SharpDX.CppObject.FromPointer<SharpDX.ComObject>(surface);
            var resource = comObj.QueryInterface<SharpDX.DXGI.Resource>();
            var shared   = resource.SharedHandle;
            resource.Dispose();

            var d3dRes  = _d3dDevice.OpenSharedResource<SharpDX.Direct3D11.Resource>(shared);
            var texture = d3dRes.QueryInterface<Texture2D>();
            d3dRes.Dispose();

            using var dxgiSurface = texture.QueryInterface<Surface>();
            texture.Dispose();

            _d2dContext = new D2dCtx(_d2dDevice, DeviceContextOptions.None);

            var props = new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm,
                                SharpDX.Direct2D1.AlphaMode.Premultiplied),
                96, 96,
                BitmapOptions.Target | BitmapOptions.CannotDraw);
            using var target = new Bitmap1(_d2dContext, dxgiSurface, props);
            _d2dContext.Target = target;

            if (_camera != null)
                _camera.Bounds = new RectangleF(0, 0, w, h);
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
            int w = (int)(RenderSize.Width  * DpiScale());
            int h = (int)(RenderSize.Height * DpiScale());

            if (isNewSurface)
                CreateSizeDependentResources(surface, w, h);

            if (_d2dContext == null || _camera == null) return;

            _d2dContext.BeginDraw();
            _d2dContext.Clear(PD2DPaintContext.ToRawColor(BackgroundColor));

            var ctx = new PD2DPaintContext(_d2dContext, _dwFactory);
            _camera.FullPaint(ctx);
            ctx.DisposeResources();

            _d2dContext.EndDraw();
        }

        private void OnCompositionTargetRendering(object sender, EventArgs e) =>
            _d3dImage?.RequestRender();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SetPixelSize();
            if (_camera != null) _camera.Bounds = PhysicalBounds();
        }

        private void SetPixelSize()
        {
            double s = DpiScale();
            _d3dImage?.SetPixelSize(
                (int)(RenderSize.Width  * s),
                (int)(RenderSize.Height * s));
        }

        private RectangleF PhysicalBounds()
        {
            double s = DpiScale();
            return new RectangleF(0, 0,
                (float)(RenderSize.Width  * s),
                (float)(RenderSize.Height * s));
        }

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

        // ── DPI ────────────────────────────────────────────────────────
        private double DpiScale() =>
            PresentationSource.FromVisual(this)
                ?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

        // ── Input handling — see Step 7 ────────────────────────────────
    }
}
```

---

## Step 7 — Input handling in `PCanvasWpf`

`PInputManager` consumes `System.Windows.Forms.MouseEventArgs` and `System.Windows.Forms.KeyEventArgs`. Construct synthetic WinForms event-args from WPF event data. This requires keeping a reference to `System.Windows.Forms` in `Piccolo.csproj` (add `<UseWindowsForms>true</UseWindowsForms>` alongside `<UseWPF>true</UseWPF>`) **only** for event-arg construction in this class — all other WinForms dependencies are eliminated.

Alternatively, `PInputManager` can be updated to accept a discriminated union or plain struct carrying position + button state, eliminating the WinForms event-arg types entirely. That is a larger refactor; use the synthetic-args approach first to keep scope contained.

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

private void DetachInputHandlers()
{
    PreviewMouseDown  -= Canvas_MouseDown;
    PreviewMouseMove  -= Canvas_MouseMove;
    PreviewMouseUp    -= Canvas_MouseUp;
    PreviewMouseWheel -= Canvas_MouseWheel;
    KeyDown           -= Canvas_KeyDown;
    KeyUp             -= Canvas_KeyUp;
}

private void Canvas_MouseDown(object s, MouseButtonEventArgs e)
{
    Focus();
    var (x, y) = PixelPos(e);
    var args = new System.Windows.Forms.MouseEventArgs(
        ToWfButton(e.ChangedButton), 1, x, y, 0);
    Root.DefaultInputManager.ProcessEventFromCamera(
        args, PInputType.MouseDown, Camera, this);
}

private void Canvas_MouseMove(object s, MouseEventArgs e)
{
    var (x, y) = PixelPos(e);
    bool drag  = e.LeftButton  == MouseButtonState.Pressed
              || e.RightButton == MouseButtonState.Pressed;
    var args = new System.Windows.Forms.MouseEventArgs(
        ToWfButtons(e), 0, x, y, 0);
    Root.DefaultInputManager.ProcessEventFromCamera(
        args, drag ? PInputType.MouseDrag : PInputType.MouseMove, Camera, this);
}

// MouseUp and MouseWheel follow the same pattern.

private void Canvas_KeyDown(object s, KeyEventArgs e)
{
    var args = new System.Windows.Forms.KeyEventArgs(ToWfKey(e.Key));
    Root.DefaultInputManager.ProcessEventFromCamera(
        args, PInputType.KeyDown, Camera, this);
}

private (int x, int y) PixelPos(MouseEventArgs e)
{
    var p = e.GetPosition(this);
    double s = DpiScale();
    return ((int)(p.X * s), (int)(p.Y * s));
}
```

`ToWfKey` maps `System.Windows.Input.Key` → `System.Windows.Forms.Keys`. Both enums share the same underlying Win32 virtual key values, so a direct cast (`(System.Windows.Forms.Keys)(int)key`) works for the common keys; add a `switch` for the handful that differ.

---

## Step 8 — DPI / coordinate consistency

WPF logical pixels are 1/96 inch. All D2D drawing and Piccolo layout must use physical pixels.

- `D3D11Image.SetPixelSize` — always physical: `(int)(RenderSize.Width * DpiScale())`.
- Mouse positions passed to `PInputManager` — physical (handled by `PixelPos()` above).
- `_camera.Bounds` — physical (set via `PhysicalBounds()` above).
- Subscribe to `Window.DpiChanged` (WPF 4.6.2+) in `OnLoaded` and call `SetPixelSize()` + update camera bounds.

---

## Step 9 — Port the four graph editors

The four subclasses of `PCanvas` in `LegendaryExplorer`:

| Class | File |
|---|---|
| `SequenceGraphEditor` | `Tools/Sequence Editor/SequenceGraphEditor.cs` |
| `ConvGraphEditor` | `Tools/Dialogue Editor/ConvGraphEditor.cs` |
| `PathingGraphEditor` | `Tools/PathfindingEditor/PathingGraphEditor.cs` |
| `WwiseGraphEditor` | `Tools/WwiseEditor/WwiseGraphEditor.cs` |

For each:
1. Change `: PCanvas` → `: PCanvasWpf`.
2. Remove any `WindowsFormsHost` wrapper in the corresponding XAML.
3. Update the host WinForms `Form` to a WPF `Window` (or embed in an existing WPF window via XAML), since `PCanvasWpf` is a WPF `ContentControl`.
4. Update cursor references from `System.Windows.Forms.Cursor` to `System.Windows.Input.Cursor`.
5. Build and confirm the scene renders; confirm mouse pan/zoom.

Start with `SequenceGraphEditor` as the validation target before touching the others.

---

## Step 10 — Delete `PCanvas` and `PPaintContext`

Once all four graph editors compile and run against `PCanvasWpf`:

1. Delete `SharedProjects/Piccolo/PCanvas.cs` and `PCanvas.resx`.
2. Delete `SharedProjects/Piccolo/Util/PPaintContext.cs`.
3. Verify the build is clean. Remove any now-dead `using System.Windows.Forms` directives from the remaining Piccolo files.

---

## Known risks and mitigations

| Risk | Mitigation |
|---|---|
| `Pen.Brush` / `Brush` is not `SolidBrush` | Log warning, fall back to black; extend later. Piccolo uses `SolidBrush` almost exclusively. |
| `GraphicsPath.PathData` bezier format | GDI+ emits cubic beziers as 3 consecutive points with type `3`. Assert during development: `Debug.Assert((types[i+1] & 0x07) == 3)`. |
| D2D device lost (GPU driver reset) | Catch `SharpDXException` with `D2DERR_RECREATE_TARGET` / `DXGI_ERROR_DEVICE_REMOVED` in `D3DImage_OnRender`; null out size-dependent resources; let `isNewSurface = true` trigger recreation on next callback. |
| DPI change at runtime | Subscribe to `Window.DpiChanged` in `OnLoaded`; call `SetPixelSize()` and reset camera bounds. |
| `PText` bounds measured with GDI+, rendered with DirectWrite | Slight size discrepancy is acceptable initially. Fix later by replacing `GRAPHICS.MeasureString` with `TextLayout.Metrics`. |
| WinForms event-arg construction in `PCanvasWpf` | Requires `<UseWindowsForms>true</UseWindowsForms>` in `Piccolo.csproj` until `PInputManager` is refactored to use a plain struct for input data. |

---

## File checklist for the implementing agent

**Delete:**
- [ ] `SharedProjects/Piccolo/PCanvas.cs`
- [ ] `SharedProjects/Piccolo/PCanvas.resx`
- [ ] `SharedProjects/Piccolo/Util/PPaintContext.cs`

**Create:**
- [ ] `SharedProjects/Piccolo/PCanvasWpf.cs`
- [ ] `SharedProjects/Piccolo/Util/PD2DPaintContext.cs`

**Modify:**
- [ ] `SharedProjects/Piccolo/Piccolo.csproj` — SharpDX refs + `<UseWPF>true</UseWPF>` (+ `<UseWindowsForms>` if needed for Step 7)
- [ ] `SharedProjects/Piccolo/PCamera.cs` — `PCanvas` → `PCanvasWpf`
- [ ] `SharedProjects/Piccolo/PRoot.cs` — `PCanvas` → `PCanvasWpf`; `IsHandleCreated` → `IsLoaded`; `BeginInvoke` → `Dispatcher.BeginInvoke`
- [ ] `SharedProjects/Piccolo/PInputManager.cs` — `PCanvas` → `PCanvasWpf`
- [ ] `SharedProjects/Piccolo/Event/PInputEventArgs.cs` — `PCanvas` → `PCanvasWpf`; cursor type → WPF
- [ ] `SharedProjects/Piccolo/PNode.cs` — `PPaintContext` → `PD2DPaintContext`; rewrite `Paint()`
- [ ] `SharedProjects/Piccolo/PCamera.cs` — `PPaintContext` → `PD2DPaintContext` in traversal signatures
- [ ] `SharedProjects/Piccolo/PLayer.cs` — `PPaintContext` → `PD2DPaintContext` in traversal signatures
- [ ] `SharedProjects/Piccolo/Util/PPickPath.cs` — `PPaintContext` → `PD2DPaintContext` if referenced
- [ ] `SharedProjects/Piccolo/Nodes/PPath.cs` — rewrite `Paint()`, add geometry cache
- [ ] `SharedProjects/Piccolo/Nodes/PText.cs` — rewrite `Paint()`, add `TextFormat` cache
- [ ] `SharedProjects/Piccolo/Nodes/PImage.cs` — rewrite `Paint()`, add bitmap cache
- [ ] `SharedProjects/Piccolo/Activities/PActivity.cs` — `PCanvas` → `PCanvasWpf`
- [ ] `SharedProjects/Piccolo/Activities/PColorActivity.cs` — `PCanvas` → `PCanvasWpf`
- [ ] `SharedProjects/Piccolo/Activities/PTransformActivity.cs` — `PCanvas` → `PCanvasWpf`
- [ ] `Tools/Sequence Editor/SequenceGraphEditor.cs` — `: PCanvas` → `: PCanvasWpf` (validate first)
- [ ] `Tools/Dialogue Editor/ConvGraphEditor.cs` — `: PCanvas` → `: PCanvasWpf`
- [ ] `Tools/PathfindingEditor/PathingGraphEditor.cs` — `: PCanvas` → `: PCanvasWpf`
- [ ] `Tools/WwiseEditor/WwiseGraphEditor.cs` — `: PCanvas` → `: PCanvasWpf`
