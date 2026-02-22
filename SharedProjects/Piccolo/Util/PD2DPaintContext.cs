using System;
using System.Collections.Generic;
using System.Drawing;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using SharpDX.Mathematics.Interop;

namespace Piccolo.Util {
    /// <summary>
    /// Paint context used by <see cref="PCanvasWpf"/> to paint the scene graph via Direct2D.
    /// This is used for on-screen rendering only. For off-screen (ToImage) rendering,
    /// use <see cref="PPaintContext"/>.
    /// </summary>
    public sealed class PD2DPaintContext {
        // ── Core D2D objects ──────────────────────────────────────────
        public DeviceContext D2DContext { get; }
        public Factory DWriteFactory { get; }

        // ── Stacks ────────────────────────────────────────────────────
        private readonly Stack<RawMatrix3x2> _transformStack = new();
        private readonly Stack<RectangleF>   _clipStack      = new();
        private readonly Stack<PCamera>      _cameraStack    = new();

        // ── Resource cache ────────────────────────────────────────────
        private readonly Dictionary<int, SolidColorBrush> _brushCache = new();

        // ── Render quality ────────────────────────────────────────────
        public RenderQuality RenderQuality { get; set; }

        public PD2DPaintContext(DeviceContext d2dContext, Factory dwFactory) {
            D2DContext    = d2dContext;
            DWriteFactory = dwFactory;
            _clipStack.Push(new RectangleF(0, 0,
                d2dContext.Size.Width, d2dContext.Size.Height));
        }

        // ── Scale ─────────────────────────────────────────────────────
        public float Scale {
            get {
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
        public void PushMatrix(PMatrix matrix) {
            if (matrix == null) return;
            _transformStack.Push(D2DContext.Transform);
            _clipStack.Push(matrix.InverseTransform(LocalClip));

            var m       = matrix.MatrixReference;
            var current = D2DContext.Transform;
            D2DContext.Transform = Multiply(ToRaw(m), current);
        }

        public void PopMatrix() {
            D2DContext.Transform = _transformStack.Pop();
            _clipStack.Pop();
        }

        // ── Clip stack ────────────────────────────────────────────────
        // Piccolo clips are always axis-aligned rectangles.
        public void PushClip(Region aClip) {
            RectangleF gdiRect  = aClip.GetBounds(DummyGraphics);
            RectangleF newLocal = RectangleF.Intersect(LocalClip, gdiRect);
            _clipStack.Push(newLocal);
            D2DContext.PushAxisAlignedClip(ToRawRect(newLocal),
                AntialiasMode.PerPrimitive);
        }

        public void PopClip() {
            D2DContext.PopAxisAlignedClip();
            _clipStack.Pop();
        }

        // ── Brush cache ───────────────────────────────────────────────
        public SolidColorBrush GetBrush(Color color) {
            int key = color.ToArgb();
            if (!_brushCache.TryGetValue(key, out var brush)) {
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
            new(a.M11 * b.M11 + a.M12 * b.M21,
                a.M11 * b.M12 + a.M12 * b.M22,
                a.M21 * b.M11 + a.M22 * b.M21,
                a.M21 * b.M12 + a.M22 * b.M22,
                a.M31 * b.M11 + a.M32 * b.M21 + b.M31,
                a.M31 * b.M12 + a.M32 * b.M22 + b.M32);

        // Lazily-created dummy GDI Graphics used only for Region.GetBounds().
        private static System.Drawing.Graphics _dummyGfx;
        private static System.Drawing.Graphics DummyGraphics =>
            _dummyGfx ??= System.Drawing.Graphics.FromImage(
                new System.Drawing.Bitmap(1, 1));

        // ── Dispose ───────────────────────────────────────────────────
        public void DisposeResources() {
            foreach (var b in _brushCache.Values) b.Dispose();
            _brushCache.Clear();
        }
    }
}
