# Plan: Convert HexBox to WPF Control with DirectX (D2D1) Rendering

## Context & Current State

### What Exists Today

**`Be.Windows.Forms.HexBox`** (WinForms, GDI+):
- 4,404-line WinForms `Control` subclass
- Renders via `System.Drawing.Graphics` in `OnPaint` / `OnPaintBackground`
- GDI+ calls: `DrawString`, `FillRectangle`, `DrawLine`, `SolidBrush`, `Pen`
- Win32 caret: `NativeMethods.CreateCaret`, `SetCaretPos`, `ShowCaret`, `DestroyCaret`
- Hosted in WPF via `WindowsFormsHost` / `WindowsFormsHostEx` throughout the solution
- Used by: `BinaryInterpreterWPF`, `InterpreterExportLoader`, `BytecodeEditor`,
  `EntryMetadataExportLoader`, `ConditionalsEditorWindow`, `FileHexViewer`, and more

**`HexEditorControl`** (`UserControls/SharedToolControls/HexEditor/`, existing WPF prototype):
- Partial WPF replacement already written using WPF `Canvas` + `TextBlock`/`Rectangle`
- Renders by clearing and rebuilding Canvas children on every invalidation — O(n) element
  creation/destruction per frame, very slow for large files
- Has a local `IByteProvider` interface that diverges from `LegendaryExplorerCore.Misc.IByteProvider`
- Incomplete public API (missing `Highlight`, `UnhighlightAll`, `Refresh`, `Select`,
  `ScrollByteIntoView`, etc.)
- Only used by the test harness `HexEditorTestWindow`; callers still use WinForms HexBox

**SharpDX infrastructure already in `LegendaryExplorer.csproj`**:
- `SharpDX`, `SharpDX.Direct3D11`, `SharpDX.Direct2D1` (includes DirectWrite),
  `SharpDX.DXGI`, `SharpDX.Mathematics`, `SharpDX.D3DCompiler`
- `Microsoft.Wpf.Interop.DirectX-x64` (the `D3D11Image` WPF↔DirectX bridge)

**Established rendering pattern** (`LegacySceneRenderControl` / `SceneRenderControl`):
- `D3D11Image` (`Microsoft.Wpf.Interop.DirectX`) hosted inside a WPF `ContentControl`
- `D3D11Image.OnRender` callback receives a `DXGI Surface` pointer
- `D3D11` device + backbuffer created from shared DXGI resource handle
- `D2D.RenderTarget` created from `newBackBuffer.QueryInterface<Surface>()` (see
  `MeshRenderContext.CreateSizeDependentResources`)
- `DW.Factory` + `DW.TextFormat` for DirectWrite text
- `CompositionTarget.Rendering` triggers `D3DImage.RequestRender()` each WPF frame

---

## Goal

Replace the WPF Canvas rendering inside `HexEditorControl` with a D2D1+DirectWrite render
path using `D3D11Image`, while:
1. Keeping the same XAML host structure (UserControl with ScrollBar, layout panels)
2. Exposing an API compatible with all current `Be.Windows.Forms.HexBox` call-sites so
   callers can be migrated away from `WindowsFormsHost`
3. Using `LegendaryExplorerCore.Misc.IByteProvider` as the data interface

---

## Architecture Overview

```
HexEditorControl (UserControl)
├── XAML: Grid with ScrollBar + Image (hosts D3D11Image)
│          (line info panel, column header, hex area, string area are all
│           drawn in D2D1 — no child WPF elements for text)
├── D3D11Image  ← Microsoft.Wpf.Interop.DirectX
├── HexD2DContext (new class, analogous to LegacyRenderContext)
│   ├── D3D11.Device
│   ├── D2D1.RenderTarget  (created from DXGI Surface of D3D11 backbuffer)
│   ├── DW.Factory  (shared, singleton or per-context)
│   ├── DW.TextFormat  (monospace font, measured once)
│   └── D2D1.SolidColorBrush cache  (background, foreground, selection, highlight)
└── Input / layout logic (existing keyboard + mouse handlers, adapted)
```

---

## Implementation Phases

### Phase 1 — `HexD2DContext`: DirectX Resource Management

**New file**: `HexEditor/HexD2DContext.cs`

Mirrors the `LegacyRenderContext` pattern:

```csharp
internal sealed class HexD2DContext : IDisposable
{
    // D3D11
    public D3D11.Device Device { get; private set; }

    // D2D1
    public D2D.RenderTarget RenderTarget { get; private set; }
    public float CharWidth { get; private set; }
    public float CharHeight { get; private set; }

    // DirectWrite
    private DW.Factory _dwFactory;
    private DW.TextFormat _textFormat;

    // Brushes (recreated with RenderTarget)
    public D2D.SolidColorBrush BrushBackground { get; private set; }
    public D2D.SolidColorBrush BrushForeground { get; private set; }
    public D2D.SolidColorBrush BrushInfoForeground { get; private set; }
    public D2D.SolidColorBrush BrushSelection { get; private set; }
    public D2D.SolidColorBrush BrushSelectionFg { get; private set; }
    public D2D.SolidColorBrush BrushShadow { get; private set; }

    // Lifecycle (matches LegacyRenderContext)
    public void CreateDeviceResources() { ... }              // D3D11 device, DW factory
    public void CreateSizeDependentResources(D3D11.Texture2D backbuffer) { ... }  // D2D RT, brushes
    public void DisposeSizeDependentResources() { ... }
    public void DisposeAll() { ... }
}
```

**Font metrics**: After creating the `DW.TextFormat`, measure character size using a
`DW.TextLayout` for a single `"0"` character. This gives exact pixel-aligned
`CharWidth`/`CharHeight` without needing a WPF `FormattedText`.

**Device creation flags**: `BgraSupport | SingleThreaded` (same as `LegacyRenderContext`).

**D2D factory**: `D2D.FactoryType.SingleThreaded` (all rendering on the WPF UI thread via
`CompositionTarget.Rendering`).

---

### Phase 2 — `HexEditorControl`: Replace Canvas Rendering with D3D11Image

**Modify** `HexEditorControl.xaml` and `HexEditorControl.xaml.cs`.

#### XAML changes

Replace the Canvas-per-region layout with a single `Image` that hosts `D3D11Image` plus
the native WPF `ScrollBar`. Keep the overall Grid structure but remove `LineInfoCanvas`,
`ColumnInfoCanvas`, `HexCanvas`, `StringCanvas` — all regions are drawn in D2D1.

```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>  <!-- ScrollBar -->
    </Grid.ColumnDefinitions>

    <!-- D3D11Image lives here -->
    <Image x:Name="RenderImage"
           Grid.Column="0"
           HorizontalAlignment="Stretch"
           VerticalAlignment="Stretch"
           Focusable="True"/>

    <!-- WPF caret overlay (thin blinking rectangle) -->
    <Rectangle x:Name="Caret"
               Width="1" Height="14"
               Fill="{DynamicResource TextBrush}"
               Visibility="Collapsed"
               IsHitTestVisible="False"
               Panel.ZIndex="100"/>

    <ScrollBar x:Name="VerticalScrollBar"
               Grid.Column="1"
               Orientation="Vertical" .../>
</Grid>
```

#### Code-behind initialization (matches `LegacySceneRenderControl`)

```csharp
private Microsoft.Wpf.Interop.DirectX.D3D11Image _d3dImage;
private HexD2DContext _d2dContext;

private void OnLoaded(...)
{
    _d2dContext = new HexD2DContext();
    _d2dContext.CreateDeviceResources();  // D3D11 device + DW factory + text metrics

    _d3dImage = new D3D11Image { OnRender = D3DImage_OnRender };
    RenderImage.Source = _d3dImage;
    _d3dImage.WindowOwner = new WindowInteropHelper(Window.GetWindow(this)).Handle;

    CompositionTarget.Rendering += CompositionTarget_Rendering;
    _d3dImage.SetPixelSize((int)RenderSize.Width, (int)RenderSize.Height);

    CalculateLayout();  // uses _d2dContext.CharWidth / CharHeight
}

private void CompositionTarget_Rendering(object sender, EventArgs e)
{
    if (_dirty && _d2dContext.IsReady)
    {
        _d3dImage.RequestRender();
        _dirty = false;
    }
}

private void D3DImage_OnRender(IntPtr surface, bool isNewSurface)
{
    if (isNewSurface)
    {
        _d2dContext.DisposeSizeDependentResources();
        // Unwrap DXGI surface → D3D11 texture → D2D RenderTarget
        var comObj = CppObject.FromPointer<ComObject>(surface);
        var dxgiRes = comObj.QueryInterface<SharpDX.DXGI.Resource>();
        var sharedHandle = dxgiRes.SharedHandle;
        dxgiRes.Dispose();
        var d3dRes = _d2dContext.Device.OpenSharedResource<D3D11.Resource>(sharedHandle);
        var backbuffer = d3dRes.QueryInterface<D3D11.Texture2D>();
        d3dRes.Dispose();
        _d2dContext.CreateSizeDependentResources(backbuffer);
    }
    RenderFrame();
}

private void OnSizeChanged(...)
{
    _d3dImage?.SetPixelSize((int)RenderSize.Width, (int)RenderSize.Height);
    CalculateLayout();
    Invalidate();
}
```

**`_dirty` flag**: Set instead of calling `_d3dImage.RequestRender()` directly; the
`CompositionTarget.Rendering` loop drains it once per frame. This prevents redundant
redraws within the same frame when multiple state changes occur (e.g., selection + scroll).

**Disposal**: On window closing, call `_d2dContext.DisposeAll()`, null `_d3dImage`,
unsubscribe from `CompositionTarget.Rendering`. Use the same window-closing hook as
`LegacySceneRenderControl`.

---

### Phase 3 — D2D1 Rendering Logic (`RenderFrame`)

All layout calculations stay as-is (they produce pixel coordinates). The render path
replaces Canvas element creation with D2D1 draw calls.

#### Layout regions (same as HexBox / existing HexEditorControl)

```
_recLineInfo      — left column: hex address labels
_recColumnInfo    — top row: column header (00 01 02 … 0F)
_recHex           — main hex area
_recStringView    — right column: ASCII view
```

These are computed in `CalculateLayout()` using `_d2dContext.CharWidth/CharHeight` instead
of `FormattedText`.

#### `RenderFrame()` structure

```csharp
private void RenderFrame()
{
    var rt = _d2dContext.RenderTarget;
    rt.BeginDraw();

    rt.Clear(/* BackgroundColor */);

    if (LineInfoVisible)    RenderLineInfo(rt);
    if (ColumnInfoVisible)  RenderColumnHeader(rt);
    RenderHexArea(rt);
    if (StringViewVisible)  RenderStringArea(rt);
    if (GroupSeparatorVisible) RenderGroupSeparators(rt);

    rt.EndDraw();
}
```

#### Text rendering

Use `DW.TextLayout` or `rt.DrawText(string, textFormat, layoutRect, brush)`.

For the monospace hex grid, `DrawText` with a pre-built `DW.TextFormat` (Consolas or
Courier New, fixed size) is sufficient. Each cell is two characters (`"XX"`), positioned
at `(col * charWidth * 3, row * charHeight)`.

For maximum throughput, batch all non-highlighted bytes by color, then issue one
`DrawText` call per cell. Optionally, build a single string per row using `Span<char>`
(matching the existing `ConvertByteToHex` approach) and issue one `DrawText` call per row.

**Per-row batching** (recommended):

```csharp
// For each visible row:
Span<char> rowBuf = stackalloc char[bytesPerLine * 3];
// fill with hex chars
rt.DrawText(rowBuf, textFormat,
    new RawRectangleF(hexX, rowY, hexX + rowWidth, rowY + charHeight),
    defaultBrush);
// Then overdraw selected/highlighted cells with background + text
```

#### Selection and highlight rendering

For selected or highlighted bytes, draw a filled `RawRectangleF` first, then overdraw text:

```csharp
rt.FillRectangle(new RawRectangleF(x, y, x + cellWidth, y + charHeight), selectionBrush);
rt.DrawText(hexChars, textFormat, cellRect, selectionFgBrush);
```

The `_highlightRegions` list is iterated once per frame to determine per-byte colors,
same logic as the existing `PaintHex`/`PaintHexAndStringView` methods.

#### Shadow selection (cross-pane highlight)

When the cursor is in the hex pane, draw a translucent overlay rectangle in the string
pane at the same byte position (and vice versa). Implemented with a semi-transparent
brush (`D2D.SolidColorBrush` with alpha < 1), matching `PaintCurrentBytesSign`.

#### Column separators

`rt.DrawLine(new RawVector2(x, y0), new RawVector2(x, y1), infoFgBrush, 1f)`

#### Caret

Keep the WPF `Rectangle` element for the caret (it's a thin 1px–2px element that blinks,
and using a WPF overlay is simpler and correct for accessibility). Position it using
`Canvas.SetLeft/Top` based on `_bytePos` and `_byteCharacterPos`, same as the existing
`UpdateCaret()`. The caret remains a WPF element overlaid on top of the `Image`.

---

### Phase 4 — Complete the Public API (HexBox Compatibility)

Add the missing members to `HexEditorControl` so callers can drop in the new control:

| HexBox member | HexEditorControl equivalent |
|---|---|
| `ByteProvider` | Already exists; switch to `LegendaryExplorerCore.Misc.IByteProvider` |
| `SelectionStart` / `SelectionLength` | Already exist |
| `Select(long start, long length)` | Add method |
| `ScrollByteIntoView()` / `(long index)` | Add methods (call existing `EnsureVisible`) |
| `Highlight(long, long, Color, Color, string)` | Add method (existing `_highlightRegions`) |
| `Highlight(long, long)` (default colors) | Add overload |
| `UnhighlightAll()` | Already exists as `ClearHighlights()` — add alias |
| `Refresh()` | Add method (calls `Invalidate()`) |
| `VScrollBarVisible` | Already exists as DependencyProperty |
| `LineInfoVisible` | Already exists |
| `ColumnInfoVisible` | Already exists |
| `StringViewVisible` | Already exists |
| `GroupSeparatorVisible` | Already exists |
| `ReadOnly` | Already exists |
| `UseFixedBytesPerLine` / `BytesPerLine` | Already exists (`BytesPerLine`) |
| `MinBytesPerLine` / `MaxBytesPerLine` | Already exist as constants; expose as properties |
| `MinWidth` / `MaxWidth` | Map to WPF `MinWidth`/`MaxWidth` on the control |
| `InsertActive` | Add bool property (affects caret width; 1px vs charWidth) |
| `SelectionChanged` event | Add event, fire from `SetPosition` |
| `HighlightRegionAdded` event | Add event, fire from `Highlight()` |
| `InfoForeColor` / `SelectionForeColor` / `SelectionBackColor` | Add DP with brush cache invalidation |
| `GetByteProvider()` extension method | Already exists in `LegendaryExplorerCore` |

**`IByteProvider` alignment**: The current `HexEditorControl` defines its own local
`IByteProvider`. Replace it with `LegendaryExplorerCore.Misc.IByteProvider` to be
compatible with `ReadOptimizedByteProvider` and `DynamicByteProvider`.

**`HighlightRegion`**: Replace the local struct with the one from `HexBox.HighlightRegion`
or define an equivalent in a shared location reachable by both callers and the new control.

---

### Phase 5 — Migrate Callers from WindowsFormsHost

For each caller that currently does:

```xaml
<sharedUi:WindowsFormsHostEx x:Name="hexbox_Host">
    <forms:HexBox MinBytesPerLine="4" MaxBytesPerLine="16" .../>
</sharedUi:WindowsFormsHostEx>
```

Replace with:

```xaml
<hexEditor:HexEditorControl x:Name="hexbox_Host"
    MinBytesPerLine="4" MaxBytesPerLine="16" .../>
```

And in code-behind, remove:

```csharp
_hexBox = (HexBox)hexbox_Host.Child;
```

Since `HexEditorControl` is the control itself (not a host), all `_hexBox.X` calls become
`hexbox_Host.X`.

**`SubstituteImageForHexBox`**: Currently captures a bitmap of the WinForms host to avoid
airspace issues during animations. With a pure WPF control this workaround is unnecessary
— simply remove the property or make it a no-op.

**`DrawToBitmapSource()`**: Remove; no longer needed without WinForms host.

**`WindowsFormsHost.EnableWindowsFormsInterop()`** in `AppBoot.cs`: Can be removed once
all HexBox usages are migrated.

**Callers to update**:
- `BinaryInterpreterWPF` (xaml.cs + xaml)
- `InterpreterExportLoader` (xaml.cs + xaml)
- `BytecodeEditor` (xaml.cs + xaml)
- `EntryMetadataExportLoader` (xaml.cs + xaml)
- `ConditionalsEditorWindow` (xaml.cs + xaml)
- `FileHexViewer` (xaml.cs + xaml)
- `Soundpanel` (if used)

---

## File Inventory

| Action | File |
|---|---|
| **New** | `HexEditor/HexD2DContext.cs` |
| **Modify** | `HexEditor/HexEditorControl.xaml` |
| **Modify** | `HexEditor/HexEditorControl.xaml.cs` |
| **Modify (each caller)** | `BinaryInterpreterWPF.xaml[.cs]`, `InterpreterExportLoader.xaml[.cs]`, `BytecodeEditor.xaml[.cs]`, `EntryMetadataExportLoader.xaml[.cs]`, `ConditionalsEditorWindow.xaml[.cs]`, `FileHexViewer.xaml[.cs]` |
| **Eventually remove** | `SharedProjects/Be.Windows.Forms.HexBox/` project (after all callers migrated) |

---

## Key Technical Decisions & Rationale

### D2D1 via D3D11Image (not HwndRenderTarget or WriteableBitmap)

`D3D11Image` is already proven in this codebase (`LegacySceneRenderControl`,
`SceneRenderControl`). It provides GPU-composited output into the WPF visual tree with no
airspace issues and no WinForms interop. `HwndRenderTarget` would require another HWND and
re-introduce airspace problems. `WriteableBitmap` with software D2D would work but gives
up GPU acceleration and requires CPU readback.

### Pure D2D1 (no 3D geometry)

The hex editor only needs 2D primitives: filled rectangles and text. A D2D1 `RenderTarget`
on the D3D11 backbuffer DXGI surface covers all required operations. No vertex buffers,
shaders, or D3D11 draw calls are needed beyond device initialization.

### `_dirty` flag + `CompositionTarget.Rendering` (not `InvalidateVisual`)

`InvalidateVisual` would invoke WPF's own layout/render cycle; we need to trigger
`D3DImage.RequestRender()` instead. The dirty-flag approach coalesces multiple state
changes (e.g., scroll + selection) into one render per frame.

### Monospaced font (Consolas / Courier New)

Required for correct column alignment. Measured once at initialization via
`DW.TextLayout` to get exact pixel-accurate `CharWidth`/`CharHeight`. All layout
rectangle math uses these fixed values, identical to the existing `_charSize` in `HexBox`.

### Per-row batching for text rendering

Issuing one `DrawText` call per row (rather than per byte) reduces the D2D1 call count
from `O(bytesPerLine × visibleRows)` to `O(visibleRows)` for unselected text. Selected
and highlighted bytes are then drawn individually on top (cell by cell), which is a
smaller set.

### Keep WPF caret overlay

The caret is a 1×charHeight rectangle that blinks at 500ms. Implementing this in D2D1
would require scheduling a timer-driven dirty flag. Since WPF already has `DispatcherTimer`
and the caret is a trivial WPF `Rectangle`, keeping it as a WPF overlay is simpler and
does not introduce any measurable overhead.

### `InsertActive` property

The original HexBox widens the caret to `charWidth` when in overwrite mode (default) and
narrows it to 1px in insert mode. The same logic applies to the WPF caret `Rectangle.Width`.

---

## Non-Goals / Out of Scope

- Horizontal scrollbar (the existing control and HexBox do not use one at this abstraction
  level; width is constrained by `MinBytesPerLine`/`MaxBytesPerLine`)
- Find/Replace UI (callers use their own; the control exposes `SelectionStart`/`Length`)
- Context menu (existing callers provide their own; the control does not need one)
- Accessibility / narrator support (not present in WinForms HexBox either)
- DPI-awareness beyond what `D3D11Image.SetPixelSize` provides (matches existing controls)
