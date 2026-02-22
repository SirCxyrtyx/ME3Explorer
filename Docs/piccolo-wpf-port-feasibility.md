# Piccolo WPF Port Feasibility Assessment

## 1. Executive Summary

Porting Piccolo from a WinForms `Control` to a WPF `FrameworkElement` is **feasible** but represents a **significant engineering effort**. The codebase has deep, pervasive dependencies on both `System.Windows.Forms` and `System.Drawing` (GDI+), spread across ~35 files. No trivial "rename the base class" path exists.

Two viable approaches are described below. The **WriteableBitmap bridge** (Option A) is lower-risk and preserves almost all existing rendering and scene-graph logic. The **full WPF rendering port** (Option B) is a cleaner long-term outcome but requires touching every file that does drawing.

The primary motivation for a port is the **WinForms/WPF airspace problem**: the current `WindowsFormsHost` approach makes it impossible to render WPF UI elements (tooltips, adorners, popups, overlays) on top of the graph editors. The project already acknowledges pain with the interop layer - the custom `WindowsFormsHostEx` class exists solely to work around `WindowsFormsHost` memory leaks.

---

## 2. Current Architecture

### 2.1 Piccolo Library (`SharedProjects/Piccolo/`)

35 C# files implementing a zoomable scene graph framework:

| Component | File(s) | Role |
|---|---|---|
| `PCanvas` | `PCanvas.cs` | The WinForms `Control` - entry point for all I/O |
| `PPaintContext` | `Util/PPaintContext.cs` | Wraps GDI+ `Graphics`; owns transform/clip stacks |
| `PNode` | `PNode.cs` | Base scene-graph node; all drawing delegated here |
| `PInputManager` | `PInputManager.cs` | Dispatches WinForms events into the scene graph |
| `PInputEventArgs` | `Event/PInputEventArgs.cs` | Piccolo event wrapper; stores raw WinForms `EventArgs` |
| `PActivityScheduler` | `Activities/PActivityScheduler.cs` | Animation loop; uses WinForms `Timer` |
| `PMatrix` | `Util/PMatrix.cs` | 2D affine transform; wraps `System.Numerics.Matrix3x2` |
| Nodes | `Nodes/PText.cs`, `PPath.cs`, `PImage.cs` | Concrete GDI+ drawing implementations |

### 2.2 Consumers

Five WPF tools embed PCanvas subclasses via `WindowsFormsHostEx` (a `WindowsFormsHost` subclass with memory-leak workarounds):

| Tool | Graph Editor Class | XAML Host |
|---|---|---|
| Sequence Editor | `SequenceGraphEditor : PCanvas` | `GraphHost` (WindowsFormsHostEx) |
| Pathfinding Editor | `PathingGraphEditor : PCanvas` | `GraphHost` (WindowsFormsHostEx) |
| Dialogue Editor | `ConvGraphEditor : PCanvas` | `GraphHost` (WindowsFormsHostEx) |
| Wwise Editor | `WwiseGraphEditor : PCanvas` | `GraphHost` (WindowsFormsHostEx) |
| Conditionals Editor | (direct PCanvas) | `GraphHost` (WindowsFormsHostEx) |

---

## 3. Complete Dependency Inventory

### 3.1 `System.Windows.Forms` Dependencies (9 files)

| API | Used In | Notes |
|---|---|---|
| `Control` (base class) | `PCanvas.cs:72` | The root coupling; everything else flows from this |
| `Timer` | `PActivityScheduler.cs:56,196-203` | Animation loop tick driver |
| `MouseEventArgs` | `PCanvas.cs:551,560,578,626`, `PInputEventArgs.cs:427`, `PInputManager.cs:161` | Mouse position, button, delta |
| `MouseButtons` (enum) | `PCanvas.cs:570,614` | Button-state checks |
| `KeyEventArgs` | `PCanvas.cs:477,486,524`, `PInputEventArgs.cs:279,301,319,339,356,373,390` | Keyboard input |
| `KeyPressEventArgs` | `PCanvas.cs:486`, `PInputEventArgs.cs:410` | Character input |
| `Keys` (enum) | `PCanvas.cs:501-518`, `PInputEventArgs.cs:276,298,316,335,358,375,393` | Key codes and modifier flags |
| `Control.ModifierKeys` (static) | `PInputEventArgs.cs:341` | Modifier state for non-key events |
| `DragEventArgs` | `PCanvas.cs:635,644,653,661`, `PInputEventArgs.cs:482,500,517,523,546`, `PInputManager.cs:163-164` | Drag-and-drop events |
| `DragDropEffects` (enum) | `PInputEventArgs.cs:479,514` | Drag-and-drop effect flags |
| `IDataObject` | `PInputEventArgs.cs:497` | Drag-and-drop data payload |
| `Cursor` | `PCanvas.cs:88,124,421,429`, `PInputEventArgs.cs:193,200` | Cursor stack |
| `ControlStyles` | `PCanvas.cs:137-140` | Double-buffering, paint mode flags |
| `PaintEventArgs` | `PCanvas.cs:700` | OnPaint signature |
| `Container` | `PCanvas.cs:111` | Designer boilerplate |
| `Control.PointToClient` | `PInputManager.cs:164` | Drag-drop screen→client coordinate conversion |
| `Control.MouseButtons` (static) | `PCanvas.cs:614` | Polled button state |
| `Control.MousePosition` (static) | `PCanvas.cs:611` | Polled cursor screen position |

### 3.2 `System.Drawing` / GDI+ Dependencies (20 files)

| API | Key Usages | Notes |
|---|---|---|
| `Graphics` | `PPaintContext.cs` (throughout) | The rendering context; the central GDI+ object |
| `Matrix` (GDI+) | `PPaintContext.cs:108,246,256` | Transform stack storage |
| `Region` | `PPaintContext.cs:91,214-232` | Clip stack storage |
| `Bitmap` | `PText.cs:89`, `PNode.cs:3638` | Off-screen rendering surface |
| `GraphicsPath` | `PPath.cs` | Vector path rendering |
| `Brush`, `Pen` | `PNode.cs`, `PCamera.cs`, `PPath.cs`, `PText.cs` | Fill and stroke |
| `Font`, `StringFormat` | `PText.cs` | Text layout and rendering |
| `Color` | `PCanvas.cs:131`, throughout | Color values |
| `RectangleF`, `PointF`, `SizeF` | Everywhere (~20 files) | Core geometric types |
| `InterpolationMode`, `SmoothingMode`, `TextRenderingHint`, `CompositingQuality`, `PixelOffsetMode` | `PPaintContext.cs:302-332` | Render quality settings |
| `Graphics.ClipBounds` | `PPaintContext.cs:135,356` | Initial clip setup |
| `Graphics.Transform` | `PPaintContext.cs:172,246,256` | Read/write of current GDI+ CTM |
| `Graphics.MultiplyTransform` | `PPaintContext.cs:248` | Applies a transform |
| `Graphics.Clip` | `PPaintContext.cs:218,222,231` | Sets clip region |

**Notable: `PMatrix` is already decoupled from GDI+.** It uses `System.Numerics.Matrix3x2` internally and only creates GDI+ `Matrix` objects as an output adapter (`GetGdiMatrix()`). This is a significant advantage for a port.

---

## 4. Porting Approaches

### Option A: WriteableBitmap Bridge (Recommended — Lower Risk)

**Strategy:** Replace `PCanvas : Control` with `PCanvas : FrameworkElement`. Override `OnRender(DrawingContext)` to blit a GDI+ `Bitmap` into a WPF `WriteableBitmap`. Adapt WPF input events to WinForms-compatible structs at the `PCanvas` boundary. Keep all existing rendering and scene-graph logic intact.

```
WPF Input Events
     │
     ▼
PCanvas : FrameworkElement
  │  adapts WPF event args → WinForms-compatible structs
  │  renders via WriteableBitmap ← GDI+ Bitmap
  ▼
PInputManager / PPaintContext / PNode / ... (unchanged)
```

**Files requiring significant changes:**

| File | Change Required |
|---|---|
| `PCanvas.cs` | Change base class; rewrite all event overrides; replace `Invalidate` / `Update` / `Size` / `Bounds` / `Cursor` / `ControlStyles` / `AllowDrop`; blit logic |
| `PActivityScheduler.cs` | `Timer` → `DispatcherTimer` |
| `PInputEventArgs.cs` | Optionally keep WinForms types if fake WinForms event args are constructed in `PCanvas`; or replace with WPF types and update all casts |
| `PInputManager.cs` | `PointToClient` → WPF `GetPosition`; `DragEventArgs` namespace change |
| `PPaintContext.cs` | Add method to get `Graphics` from `Bitmap`; Scale property needs alternate transform-read strategy |
| Consumer graph editors | Remove `WindowsFormsHost`; embed directly in WPF layout; update `System.Drawing.Size`, `BackColor`, etc. |

**Files that would not need to change:**
- All `PNode`, `PCamera`, `PLayer`, `PRoot` logic
- All `Activities` (except `PActivityScheduler` Timer replacement)
- All `Nodes/PPath`, `Nodes/PText`, `Nodes/PImage` rendering
- All `Event/` handlers (pan, zoom, drag, etc.)
- `PMatrix`, `PPickPath`, `PUtil`, `PDebug`

**Key implementation details:**

*Rendering loop:*
```csharp
// In PCanvas : FrameworkElement
private WriteableBitmap _wpfBitmap;
private System.Drawing.Bitmap _gdiBitmap;

protected override void OnRender(DrawingContext dc) {
    // Resize GDI bitmap if needed
    EnsureBitmap((int)ActualWidth, (int)ActualHeight);
    using var g = System.Drawing.Graphics.FromImage(_gdiBitmap);
    var paintContext = new PPaintContext(g, this);
    paintContext.RenderQuality = currentRenderQuality;
    camera.FullPaint(paintContext);
    // Copy pixels to WriteableBitmap
    BlitToWriteable(_gdiBitmap, _wpfBitmap);
    dc.DrawImage(_wpfBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
}
```

*Event translation (mouse):*
WPF `MouseButtonEventArgs` exposes `GetPosition(element)` returning `System.Windows.Point`. Create a minimal adapter:
```csharp
protected override void OnMouseDown(MouseButtonEventArgs e) {
    base.OnMouseDown(e);
    var pos = e.GetPosition(this);
    var adapted = new System.Windows.Forms.MouseEventArgs(
        ToWinFormsButton(e.ChangedButton), e.ClickCount, (int)pos.X, (int)pos.Y, 0);
    Root.DefaultInputManager.ProcessEventFromCamera(adapted, PInputType.MouseDown, Camera, null);
}
```

*Timer replacement:*
```csharp
// PActivityScheduler.cs
// Before:
private Timer activityTimer;
activityTimer = new System.Windows.Forms.Timer();
activityTimer.Tick += StepActivities;

// After:
private DispatcherTimer activityTimer;
activityTimer = new DispatcherTimer();
activityTimer.Tick += StepActivities;
// Interval, Start(), Stop() API is identical
```

**Partial invalidation:** WPF `FrameworkElement.InvalidateVisual()` invalidates the whole element (no `Invalidate(Rectangle)` equivalent). For Option A, the `regionManagement` code path in `InvalidateBounds` must be simplified to always call `InvalidateVisual()`. Given that GDI+ still does the actual drawing, you can still clip the GDI+ paint to the invalidated region if needed by tracking it manually.

**Advantages:**
- ~80% of the codebase is unchanged
- All GDI+ rendering code preserved as-is
- Relatively low risk of behavioral regressions
- Existing node subclasses (`PText`, `PPath`, `PImage`) work without modification

**Disadvantages:**
- Pixel copy on every frame (WriteableBitmap blitting) adds overhead
- GDI+ rendering is CPU-only; misses WPF GPU compositing
- `System.Drawing` dependency remains in the project
- HiDPI/DPI scaling requires extra attention (GDI+ vs WPF DPI models differ)

---

### Option B: Full WPF Rendering Port (Higher Effort — Clean Long-Term)

**Strategy:** Replace `PPaintContext`'s GDI+ `Graphics` with a WPF `DrawingContext`. Replace all `System.Drawing` geometric types with WPF equivalents throughout the library.

**API mapping:**

| GDI+ | WPF Equivalent | Notes |
|---|---|---|
| `Graphics` | `DrawingContext` | Push/pop model vs mutable state |
| `RectangleF` | `System.Windows.Rect` | Rename/replace throughout |
| `PointF` | `System.Windows.Point` | Rename/replace throughout |
| `SizeF` | `System.Windows.Size` | Rename/replace throughout |
| `Color` | `System.Windows.Media.Color` | Minor API difference |
| `Brush` | `System.Windows.Media.Brush` | Fundamentally different class hierarchy |
| `Pen` | `System.Windows.Media.Pen` | Different construction |
| `Font` | `System.Windows.Media.GlyphTypeface` / `Typeface` | Significantly more complex |
| `StringFormat` | `System.Windows.Media.FormattedText` | Different text layout model |
| `GraphicsPath` | `System.Windows.Media.PathGeometry` | Different API |
| `Matrix` (GDI+) | `System.Windows.Media.Matrix` | Transform stack entries |
| `Region` | `System.Windows.Media.Geometry` | Clip regions |
| `g.FillRectangle(b, r)` | `dc.DrawRectangle(b, null, r)` | Simple |
| `g.DrawRectangle(p, r)` | `dc.DrawRectangle(null, p, r)` | Simple |
| `g.FillPath(b, path)` | `dc.DrawGeometry(b, null, geom)` | Need PathGeometry |
| `g.DrawString(...)` | `dc.DrawText(FormattedText, origin)` | Major API difference |
| `g.DrawImage(img, r)` | `dc.DrawImage(imgSrc, r)` | Need ImageSource |
| `g.MultiplyTransform(m)` | `dc.PushTransform(new MatrixTransform(m))` | Push/pop model |
| `graphics.Transform = saved` | `dc.Pop()` | Model change |
| `graphics.Clip = region` | `dc.PushClip(geometry)` / `dc.Pop()` | Model change |
| `InterpolationMode` | `RenderOptions.SetBitmapScalingMode(...)` | Attached property |
| `SmoothingMode` | `RenderOptions.SetEdgeMode(...)` | Attached property |
| `TextRenderingHint` | `TextOptions.SetTextFormattingMode(...)` | Attached property |

**Transform stack in WPF DrawingContext:**
The current code saves/restores the GDI+ transform by storing `Matrix` objects on a stack, then reassigning `graphics.Transform`. WPF `DrawingContext` is a forward-only push/pop API - you cannot read back the current transform. This requires tracking the composed transform separately to maintain the `PPaintContext.Scale` property:

```csharp
// Before (GDI+):
public float Scale {
    get {
        var elements = graphics.Transform.Elements.AsSpan();
        // Read transform directly from Graphics object
    }
}

// After (WPF): must maintain a parallel matrix stack
private Stack<Matrix3x2> _transformTracker = new();
public float Scale {
    get {
        var current = _transformTracker.Peek();
        // Compute scale from tracked matrix
    }
}
```

**Text rendering is the hardest part.** WPF `FormattedText` is stateful and expensive to construct; GDI+ `Graphics.DrawString` is simpler. `PText` currently measures text size using a shared static `Graphics.FromImage(new Bitmap(1,1))`, which has no WPF equivalent. Text measurement in WPF requires constructing `FormattedText` objects or using `GlyphTypeface.AdvanceWidths`.

**Advantages:**
- Fully native WPF; GPU-accelerated compositing
- Eliminates `System.Drawing` dependency entirely
- Better HiDPI support out of the box
- Enables WPF visual effects (transforms, opacity, animations at the compositing layer)

**Disadvantages:**
- All ~20 files using `System.Drawing` require rewriting
- `PPaintContext` transform/clip stack needs architectural rethinking (forward-only `DrawingContext`)
- Text measurement and layout is significantly more complex in WPF
- High regression risk; requires thorough testing of all node types

---

## 5. Detailed Risk Assessment by Component

### 5.1 `PCanvas` — High Complexity

This is the central integration point and requires the most work regardless of approach. Key issues:

**`IsInputKey` override:** WinForms uses `IsInputKey(Keys keyData)` to prevent arrow keys from being consumed by the window. WPF handles this differently: override `OnPreviewKeyDown` and set `e.Handled = true`, or check `e.Key` in `OnKeyDown`. Straightforward to implement, but the mechanism is different.

**`SimulateMouseMoveOrDrag`:** Uses `Control.MousePosition` (global screen position) and `PointToClient` (screen→client conversion). WPF equivalent: `Mouse.GetPosition(this)` returns position relative to any element directly, which is actually cleaner.

**`InvalidateBounds(RectangleF)`:** Currently calls `Invalidate(new Rectangle(...))` for dirty-region tracking. WPF `FrameworkElement` only exposes `InvalidateVisual()` (full element repaint). The `regionManagement` feature would need to be disabled or emulated differently.

**`PaintImmediately()` / `Update()`:** Forces synchronous paint. WPF equivalent: `Dispatcher.Invoke(DispatcherPriority.Render, () => {})` or `UpdateLayout()`. The semantics are not identical but functionally close.

**`Size` / `Bounds`:** `PCanvas.OnResize` sets `camera.Bounds = new RectangleF(...)` using `Bounds.Width/Height`. WPF: `ActualWidth`/`ActualHeight` provide element size. `SizeChanged` event replaces `OnResize`.

### 5.2 `PInputEventArgs` — High Complexity

This class stores a raw `EventArgs e` field and downcasts it to specific WinForms types throughout:

```csharp
private EventArgs e;
// ...
KeyEventArgs ke = (KeyEventArgs)e;       // WinForms type
MouseEventArgs me = (MouseEventArgs)e;   // WinForms type
DragEventArgs de = (DragEventArgs)e;     // WinForms type
```

For **Option A**, the cleanest approach is to have `PCanvas` construct fake WinForms event args from WPF events (adapters), so the internal machinery is unchanged. For **Option B**, all these casts must be replaced with WPF types.

Additionally, `Modifiers` falls back to `System.Windows.Forms.Control.ModifierKeys` (a static property) for non-key events. WPF equivalent: `System.Windows.Input.Keyboard.Modifiers`, with a mapping from `System.Windows.Input.ModifierKeys` to `System.Windows.Forms.Keys` flags.

### 5.3 `PPaintContext` — High Complexity (Option B only)

`PPaintContext` maintains stacks of GDI+ `Matrix` and `Region` objects for transform and clip management. The GDI+ model is mutable-state: set `graphics.Transform`, draw, restore. WPF `DrawingContext` is push/pop only.

The current push implementation:
```csharp
public void PushMatrix(PMatrix matrix) {
    transformStack.Push(graphics.Transform); // save current transform
    graphics.MultiplyTransform(matrix.GetGdiMatrix()); // apply
}
public void PopMatrix() {
    graphics.Transform = transformStack.Pop(); // restore
}
```

WPF equivalent (DrawingContext):
```csharp
public void PushMatrix(PMatrix matrix) {
    _transformTracker.Push(_transformTracker.Peek() * matrix.Matrix);
    _dc.PushTransform(new MatrixTransform(
        matrix.Matrix.M11, matrix.Matrix.M12,
        matrix.Matrix.M21, matrix.Matrix.M22,
        matrix.Matrix.M31, matrix.Matrix.M32));
}
public void PopMatrix() {
    _transformTracker.Pop();
    _dc.Pop();
}
```

The `Scale` property reads back `graphics.Transform.Elements` to compute the current accumulated scale. With WPF, the tracked `_transformTracker` stack provides this without reading from the drawing context.

Clip regions work similarly — `graphics.Clip = region` becomes `dc.PushClip(geometry)` + `dc.Pop()`.

### 5.4 `PActivityScheduler` — Low Complexity

```csharp
// Change only these lines:
private System.Windows.Forms.Timer activityTimer;
// →
private System.Windows.Threading.DispatcherTimer activityTimer;

activityTimer = new System.Windows.Forms.Timer();
activityTimer.Interval = PUtil.ACTIVITY_SCHEDULER_FRAME_INTERVAL;
activityTimer.Tick += StepActivities;
// →
activityTimer = new DispatcherTimer();
activityTimer.Interval = TimeSpan.FromMilliseconds(PUtil.ACTIVITY_SCHEDULER_FRAME_INTERVAL);
activityTimer.Tick += StepActivities; // same signature (object, EventArgs)
```

`DispatcherTimer` has the same `Start()`, `Stop()`, `Interval`, and `Tick` API. The only difference is `Interval` is `TimeSpan` instead of `int` milliseconds. The `Tick` event signature (`EventHandler`) is identical.

### 5.5 `PMatrix` — No Change Needed

`PMatrix` already uses `System.Numerics.Matrix3x2` internally. The only GDI+-specific method is `GetGdiMatrix()` which is only called from `PPaintContext.PushMatrix`. In Option A it stays; in Option B it is removed.

### 5.6 Drag-and-Drop — Medium Complexity

The WinForms and WPF drag-and-drop models both implement the COM-based OLE drag-and-drop protocol. `IDataObject` exists in both namespaces but they are different interfaces. Key differences:

| Aspect | WinForms | WPF |
|---|---|---|
| Event type | `DragEventArgs` from `System.Windows.Forms` | `DragEventArgs` from `System.Windows` |
| Coordinate system | Screen coordinates; `PointToClient` needed | `GetPosition(element)` gives element-relative coords |
| Data object | `System.Windows.Forms.IDataObject` | `System.Windows.IDataObject` |
| Drop effect | `DragDropEffects` (same enum values) | `DragDropEffects` (same enum values, same namespace) |
| AllowDrop | `Control.AllowDrop = true` | `UIElement.AllowDrop = true` (same name) |

`DragDropEffects` is defined in `System.Windows` in WPF and has the same values, so the enum values are wire-compatible even though the types differ.

### 5.7 `Cursor` — Low Complexity

`System.Windows.Forms.Cursor` → `System.Windows.Input.Cursor`. The `PCanvas.Cursor` property maps directly to `UIElement.Cursor`. System cursors (`Cursors.Arrow`, `Cursors.SizeAll`, etc.) exist in both namespaces with the same names. The cursor push/pop stack logic in `PCanvas.PushCursor`/`PopCursor` is unchanged.

### 5.8 Consumer Graph Editors — Medium Complexity

Each of the four `PCanvas` subclasses (`SequenceGraphEditor`, `PathingGraphEditor`, `ConvGraphEditor`, `WwiseGraphEditor`) uses:
- `this.Size = new System.Drawing.Size(width, height)` → removed (WPF uses layout system)
- `BackColor = Color.White` → `Background = Brushes.White` (WPF property)
- `graphEditor.BackColor = GraphEditorBackColor` (caller sets this) → same `Background` change
- `graphEditor.Camera.MouseDown += handler` → unchanged (Piccolo internal event, not WinForms)

The XAML changes are straightforward: remove `<sharedUi:WindowsFormsHostEx>` and embed the WPF canvas directly. The `GraphHost.Child = null` disposal pattern disappears.

---

## 6. HiDPI / DPI Scaling Considerations

GDI+ and WPF have fundamentally different DPI models:
- **GDI+:** Works in physical pixels by default; DPI scaling is opt-in per-process
- **WPF:** Works in logical units (1/96-inch DIPs); hardware pixels are abstracted away

If the application is DPI-aware (which most modern WPF apps are), a `WriteableBitmap` bridge must account for the DPI scale factor when creating the GDI+ `Bitmap` and the blit geometry. Specifically:
- The backing `Bitmap` should be `ActualWidth * dpiScale` × `ActualHeight * dpiScale` physical pixels
- The `WriteableBitmap` must be declared at the screen DPI
- Mouse coordinates arriving from WPF are in logical pixels and must be scaled

For Option B (full WPF rendering), WPF handles DPI automatically and this is not a concern.

---

## 7. Known Issues with the Current WindowsFormsHost Approach

These are the pain points that a WPF port would resolve:

1. **Airspace problem:** WinForms controls (via `WindowsFormsHost`) always render above WPF elements in the same HWND. WPF tooltips, context menus, adorners, and overlays cannot appear on top of the graph editors.

2. **Memory leaks:** The custom `WindowsFormsHostEx` class was created specifically to work around memory management problems with `WindowsFormsHost`. The comment "WindowsFormsHost will sometimes stubbornly stick around in memory" and the force-removal from the visual tree in `Dispose` indicate this is an active problem.

3. **Keyboard focus:** WinForms and WPF have separate focus systems. Input routing between them requires explicit focus management and can produce subtle bugs (e.g., keyboard shortcuts not working when focus is in the WinForms control).

4. **Accessibility:** WPF's built-in accessibility (UI Automation) does not see into `WindowsFormsHost` content.

5. **Theme/styling:** WinForms controls do not respect WPF themes or system DPI awareness settings in the same way.

---

## 8. Effort Estimate by File

### Option A (WriteableBitmap Bridge)

| File | Effort | Primary Changes |
|---|---|---|
| `PCanvas.cs` | Large | New base class, all event overrides, blit logic, size/cursor/drop |
| `PActivityScheduler.cs` | Trivial | `Timer` → `DispatcherTimer` |
| `PInputManager.cs` | Small | DragDrop coordinate conversion, namespace |
| `PInputEventArgs.cs` | Small–Medium | `ModifierKeys` fallback; DragDrop namespaces; or adapter-based (no change if adapters built in PCanvas) |
| `PPaintContext.cs` | Small | Scale property fix for no direct transform read-back |
| Consumer graph editors (×4) | Small each | Remove WFH, update `Size`/`BackColor` |
| XAML files (×5) | Trivial each | Remove `WindowsFormsHostEx` wrapper |
| All other Piccolo files (~25) | None | Unchanged |

### Option B (Full WPF Rendering)

| File Group | Effort |
|---|---|
| `PCanvas.cs` | Large (same as Option A) |
| `PPaintContext.cs` | Very Large (complete rewrite) |
| `PText.cs` | Large (text measurement and rendering completely different) |
| `PPath.cs` | Medium (GraphicsPath → PathGeometry) |
| `PImage.cs` | Small (Image → ImageSource) |
| `PNode.cs` | Medium (all `Paint`/`FullPaint` methods, brush/pen usage) |
| `PCamera.cs` | Medium (debug drawing code) |
| `PMatrix.cs` | Small (remove `GetGdiMatrix()`) |
| `PActivityScheduler.cs` | Trivial |
| `PInputEventArgs.cs` | Medium (all WinForms type casts) |
| `PInputManager.cs` | Small |
| `PCanvas.cs` + consumers | Same as Option A |
| All event handlers | Small (mostly unchanged) |

---

## 9. Recommendation

**Pursue Option A (WriteableBitmap Bridge)** as a first step. It:
- Eliminates the `WindowsFormsHost` and its memory and airspace problems immediately
- Preserves the entire rendering engine and scene-graph logic with minimal risk
- Can be executed incrementally by swapping one graph editor at a time
- Leaves open a future Option B migration once the WinForms coupling is mostly gone

**Option B** becomes attractive after Option A is stable, as a separate effort focused purely on the rendering layer without any input/event system changes.

### Implementation Order for Option A

1. Replace `Timer` with `DispatcherTimer` in `PActivityScheduler` (isolated, zero risk)
2. Create `PCanvas : FrameworkElement` with WriteableBitmap rendering and WPF event adapters
3. Port one graph editor (Sequence Editor is a good candidate) to validate the approach
4. Port remaining three graph editors
5. Remove `WindowsFormsHostEx` and related XAML plumbing from all five tool windows

---

## 10. Files That Do Not Require Changes (Option A)

The following files are completely decoupled from WinForms/GDI+ and require no changes:

- `PNode.cs` — Scene graph node base class
- `PCamera.cs` — Camera logic (excluding debug drawing helper)
- `PLayer.cs` — Layer management
- `PRoot.cs` — Root node and input processing
- `PMatrix.cs` — Transform math (already uses `System.Numerics`)
- `PPickPath.cs` — Hit testing
- `PUtil.cs` — Utility methods
- `PCameraList.cs` — Camera list
- `PNodeFilter.cs` — Node filtering
- `PDebug.cs` — Debug statistics
- All `Activities/` files except `PActivityScheduler.cs`
- All `Event/` handler files (`PPanEventHandler`, `PZoomEventHandler`, `PDragEventHandler`, etc.)
- `Nodes/PPath.cs`, `Nodes/PText.cs`, `Nodes/PImage.cs`
- All `NamespaceDoc.cs` files
