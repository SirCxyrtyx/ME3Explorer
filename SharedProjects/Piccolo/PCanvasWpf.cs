using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Wpf.Interop.DirectX;
using Piccolo.Event;
using Piccolo.Util;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using WpfCursor    = System.Windows.Input.Cursor;
using WfMouseArgs  = System.Windows.Forms.MouseEventArgs;
using WfMouseBtns  = System.Windows.Forms.MouseButtons;
using WfKeyArgs    = System.Windows.Forms.KeyEventArgs;
using WfKeys       = System.Windows.Forms.Keys;
using WfKeyPressArgs = System.Windows.Forms.KeyPressEventArgs;
using WfDragArgs   = System.Windows.Forms.DragEventArgs;
using D3d11Device  = SharpDX.Direct3D11.Device;
using D2dDevice    = SharpDX.Direct2D1.Device;
using D2dFactory   = SharpDX.Direct2D1.Factory1;
using DwFactory    = SharpDX.DirectWrite.Factory;
using DxgiDevice   = SharpDX.DXGI.Device;
using D2dCtx       = SharpDX.Direct2D1.DeviceContext;

namespace Piccolo {
    /// <summary>
    /// <b>PCanvasWpf</b> is a WPF control that renders the Piccolo scene graph via Direct2D.
    /// It replaces the WinForms <c>PCanvas</c>.
    /// </summary>
    public class PCanvasWpf : ContentControl, IDisposable {
        #region D3D/D2D devices (long-lived)
        private D3d11Device  _d3dDevice;
        private D2dDevice    _d2dDevice;
        private D2dFactory   _d2dFactory;
        private DwFactory    _dwFactory;
        #endregion

        #region Size-dependent resources
        private D2dCtx _d2dContext;
        #endregion

        #region WPF interop
        private D3D11Image _d3dImage;
        private System.Windows.Controls.Image _hostImage;
        private bool _initiallyLoaded;
        #endregion

        #region Scene graph
        private PCamera _camera;
        private PPanEventHandler  _panEventHandler;
        private PZoomEventHandler _zoomEventHandler;
        private readonly Stack<WpfCursor> _cursorStack = new();
        private bool _isInvalidated;
        #endregion

        #region Constructor
        public PCanvasWpf() {
            Loaded   += OnLoaded;
            Focusable = true;
        }
        #endregion

        #region Scene graph API (mirrors PCanvas)
        public virtual PCamera Camera {
            get => _camera;
            set {
                if (_camera != null) _camera.Canvas = null;
                _camera = value;
                if (_camera != null) {
                    _camera.Canvas = this;
                    _camera.Bounds = PhysicalBounds();
                }
            }
        }

        public virtual PRoot  Root  => _camera?.Root;
        public PLayer Layer => _camera?.GetLayer(0);

        public virtual PPanEventHandler PanEventHandler {
            get => _panEventHandler;
            set {
                if (_panEventHandler != null) RemoveInputEventListener(_panEventHandler);
                _panEventHandler = value;
                if (_panEventHandler != null) AddInputEventListener(_panEventHandler);
            }
        }

        public virtual PZoomEventHandler ZoomEventHandler {
            get => _zoomEventHandler;
            set {
                if (_zoomEventHandler != null) RemoveInputEventListener(_zoomEventHandler);
                _zoomEventHandler = value;
                if (_zoomEventHandler != null) AddInputEventListener(_zoomEventHandler);
            }
        }

        public virtual void AddInputEventListener(PInputEventListener listener)    => _camera?.AddInputEventListener(listener);
        public virtual void RemoveInputEventListener(PInputEventListener listener) => _camera?.RemoveInputEventListener(listener);

        public virtual bool Animating => Root?.ActivityScheduler?.Animating ?? false;
        #endregion

        #region Canvas state (used by PRoot)
        public bool IsInvalidated {
            get => _isInvalidated;
            set => _isInvalidated = value;
        }

        /// <summary>Called by PRoot.ProcessInputs to force a redraw when paint is invalid.</summary>
        public virtual void Update() => RequestRedraw();

        /// <summary>Called by PCamera.RepaintFrom.</summary>
        public virtual void InvalidateBounds(RectangleF bounds) {
            _isInvalidated = true;
            RequestRedraw();
        }

        public void RequestRedraw() => _d3dImage?.RequestRender();

        public virtual void PaintImmediately() => RequestRedraw();
        #endregion

        #region Cursor
        public virtual void PushCursor(WpfCursor cursor) {
            _cursorStack.Push(Cursor);
            Cursor = cursor;
        }

        public virtual void PopCursor() {
            if (_cursorStack.Count > 0) Cursor = _cursorStack.Pop();
        }
        #endregion

        #region Coordinate conversion
        /// <summary>Converts screen coordinates to physical canvas pixels.</summary>
        public System.Drawing.PointF PointToClient(System.Drawing.Point screenPt) {
            var wpfPt = PointFromScreen(new Point(screenPt.X, screenPt.Y));
            double s  = DpiScale();
            return new System.Drawing.PointF((float)(wpfPt.X * s), (float)(wpfPt.Y * s));
        }
        #endregion

        #region Lifecycle
        private void OnLoaded(object sender, RoutedEventArgs e) {
            if (!_initiallyLoaded) {
                var window = Window.GetWindow(this);
                if (window != null)
                    window.Closing += (_, ce) => { if (!ce.Cancel) Dispose(); };

                _d3dImage  = new D3D11Image { OnRender = D3DImage_OnRender };
                _hostImage = new System.Windows.Controls.Image {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                    Source              = _d3dImage,
                    Stretch             = System.Windows.Media.Stretch.Fill,
                };
                Content = _hostImage;

                var interopHelper = new System.Windows.Interop.WindowInteropHelper(window ?? new Window());
                _d3dImage.WindowOwner = interopHelper.Handle;

                CreateDevices();

                Camera            = PUtil.CreateBasicScenegraph();
                PanEventHandler   = new PPanEventHandler();
                ZoomEventHandler  = new PZoomEventHandler();

                CompositionTarget.Rendering += OnCompositionTargetRendering;
                SizeChanged += OnSizeChanged;
                _initiallyLoaded = true;
                SetPixelSize();
            }

            Unloaded += OnUnloaded;
            AttachInputHandlers();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) {
            DetachInputHandlers();
            Unloaded -= OnUnloaded;
        }
        #endregion

        #region Device creation
        private void CreateDevices() {
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.SingleThreaded;
#if DEBUG
            flags |= DeviceCreationFlags.Debug;
#endif
            _d3dDevice  = new D3d11Device(DriverType.Hardware, flags);
            _d2dFactory = new D2dFactory(FactoryType.SingleThreaded);

            using var dxgi = _d3dDevice.QueryInterface<DxgiDevice>();
            _d2dDevice     = new D2dDevice(_d2dFactory, dxgi);
            _dwFactory     = new DwFactory(SharpDX.DirectWrite.FactoryType.Shared);
        }
        #endregion

        #region Size-dependent resources
        private void CreateSizeDependentResources(IntPtr surface, int w, int h) {
            DisposeSizeDependentResources();

            var comObj   = SharpDX.CppObject.FromPointer<SharpDX.ComObject>(surface);
            var resource = comObj.QueryInterface<SharpDX.DXGI.Resource>();
            var shared   = resource.SharedHandle;
            resource.Dispose();
            comObj.Dispose();

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
                _camera.Bounds = new System.Drawing.RectangleF(0, 0, w, h);
        }

        private void DisposeSizeDependentResources() {
            _d2dContext?.Target?.Dispose();
            _d2dContext?.Dispose();
            _d2dContext = null;
        }
        #endregion

        #region Render callback
        private void D3DImage_OnRender(IntPtr surface, bool isNewSurface) {
            int w = (int)(RenderSize.Width  * DpiScale());
            int h = (int)(RenderSize.Height * DpiScale());
            if (w <= 0 || h <= 0) return;

            if (isNewSurface)
                CreateSizeDependentResources(surface, w, h);

            if (_d2dContext == null || _camera == null) return;

            _d2dContext.BeginDraw();
            _d2dContext.Clear(PD2DPaintContext.ToRawColor(System.Drawing.Color.White));

            var ctx = new PD2DPaintContext(_d2dContext, _dwFactory);
            _camera.FullPaint(ctx);
            ctx.DisposeResources();

            _d2dContext.EndDraw();
            _isInvalidated = false;
        }

        private void OnCompositionTargetRendering(object sender, EventArgs e) =>
            _d3dImage?.RequestRender();

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) {
            SetPixelSize();
            if (_camera != null) _camera.Bounds = PhysicalBounds();
        }

        private void SetPixelSize() {
            double s = DpiScale();
            _d3dImage?.SetPixelSize(
                Math.Max(1, (int)(RenderSize.Width  * s)),
                Math.Max(1, (int)(RenderSize.Height * s)));
        }

        private System.Drawing.RectangleF PhysicalBounds() {
            double s = DpiScale();
            return new System.Drawing.RectangleF(0, 0,
                (float)(RenderSize.Width  * s),
                (float)(RenderSize.Height * s));
        }

        private double DpiScale() =>
            PresentationSource.FromVisual(this)
                ?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        #endregion

        #region Input handling
        private void AttachInputHandlers() {
            PreviewMouseDown  += Canvas_MouseDown;
            PreviewMouseMove  += Canvas_MouseMove;
            PreviewMouseUp    += Canvas_MouseUp;
            PreviewMouseWheel += Canvas_MouseWheel;
            MouseEnter        += Canvas_MouseEnter;
            MouseLeave        += Canvas_MouseLeave;
            PreviewKeyDown    += Canvas_KeyDown;
            PreviewKeyUp      += Canvas_KeyUp;
            PreviewKeyUp      += Canvas_KeyPress;
        }

        private void DetachInputHandlers() {
            PreviewMouseDown  -= Canvas_MouseDown;
            PreviewMouseMove  -= Canvas_MouseMove;
            PreviewMouseUp    -= Canvas_MouseUp;
            PreviewMouseWheel -= Canvas_MouseWheel;
            MouseEnter        -= Canvas_MouseEnter;
            MouseLeave        -= Canvas_MouseLeave;
            PreviewKeyDown    -= Canvas_KeyDown;
            PreviewKeyUp      -= Canvas_KeyUp;
            PreviewKeyUp      -= Canvas_KeyPress;
        }

        private void Canvas_MouseDown(object s, MouseButtonEventArgs e) {
            Focus();
            var (x, y) = PixelPos(e);
            var args   = new WfMouseArgs(ToWfButton(e.ChangedButton), 1, x, y, 0);
            Root?.DefaultInputManager.ProcessEventFromCamera(args, PInputType.MouseDown, Camera, this);
            Root?.DefaultInputManager.ProcessEventFromCamera(args, PInputType.Click, Camera, this);
        }

        private void Canvas_MouseMove(object s, MouseEventArgs e) {
            var (x, y) = PixelPos(e);
            bool drag  = e.LeftButton  == MouseButtonState.Pressed
                      || e.RightButton == MouseButtonState.Pressed
                      || e.MiddleButton == MouseButtonState.Pressed;
            var args = new WfMouseArgs(ToWfButtons(e), 0, x, y, 0);
            Root?.DefaultInputManager.ProcessEventFromCamera(
                args, drag ? PInputType.MouseDrag : PInputType.MouseMove, Camera, this);
        }

        private void Canvas_MouseUp(object s, MouseButtonEventArgs e) {
            var (x, y) = PixelPos(e);
            var args   = new WfMouseArgs(ToWfButton(e.ChangedButton), 1, x, y, 0);
            Root?.DefaultInputManager.ProcessEventFromCamera(args, PInputType.MouseUp, Camera, this);
        }

        private void Canvas_MouseWheel(object s, MouseWheelEventArgs e) {
            var (x, y) = PixelPos(e);
            var args   = new WfMouseArgs(WfMouseBtns.None, 0, x, y, e.Delta);
            Root?.DefaultInputManager.ProcessEventFromCamera(args, PInputType.MouseWheel, Camera, this);
        }

        private void Canvas_MouseEnter(object s, MouseEventArgs e) => SimulateMouseMoveOrDrag(e);
        private void Canvas_MouseLeave(object s, MouseEventArgs e) => SimulateMouseMoveOrDrag(e);

        private void SimulateMouseMoveOrDrag(MouseEventArgs e) {
            var (x, y) = PixelPos(e);
            bool drag  = e.LeftButton  == MouseButtonState.Pressed
                      || e.RightButton == MouseButtonState.Pressed
                      || e.MiddleButton == MouseButtonState.Pressed;
            var args = new WfMouseArgs(ToWfButtons(e), 0, x, y, 0);
            Root?.DefaultInputManager.ProcessEventFromCamera(
                args, drag ? PInputType.MouseDrag : PInputType.MouseMove, Camera, this);
        }

        private void Canvas_KeyDown(object s, System.Windows.Input.KeyEventArgs e) {
            var args = new WfKeyArgs(ToWfKey(e.Key));
            Root?.DefaultInputManager.ProcessEventFromCamera(args, PInputType.KeyDown, Camera, null);
        }

        private void Canvas_KeyUp(object s, System.Windows.Input.KeyEventArgs e) {
            var args = new WfKeyArgs(ToWfKey(e.Key));
            Root?.DefaultInputManager.ProcessEventFromCamera(args, PInputType.KeyUp, Camera, null);
        }

        private void Canvas_KeyPress(object s, System.Windows.Input.KeyEventArgs e) {
            // WPF doesn't have a direct KeyPress; approximate with KeyUp for printable keys.
            char c = GetChar(e.Key);
            if (c != '\0') {
                var args = new WfKeyPressArgs(c);
                Root?.DefaultInputManager.ProcessEventFromCamera(args, PInputType.KeyPress, Camera, null);
            }
        }

        private (int x, int y) PixelPos(MouseEventArgs e) {
            var p = e.GetPosition(this);
            double s = DpiScale();
            return ((int)(p.X * s), (int)(p.Y * s));
        }

        private static WfMouseBtns ToWfButton(MouseButton btn) => btn switch {
            MouseButton.Left   => WfMouseBtns.Left,
            MouseButton.Right  => WfMouseBtns.Right,
            MouseButton.Middle => WfMouseBtns.Middle,
            MouseButton.XButton1 => WfMouseBtns.XButton1,
            MouseButton.XButton2 => WfMouseBtns.XButton2,
            _ => WfMouseBtns.None,
        };

        private static WfMouseBtns ToWfButtons(MouseEventArgs e) {
            var result = WfMouseBtns.None;
            if (e.LeftButton   == MouseButtonState.Pressed) result |= WfMouseBtns.Left;
            if (e.RightButton  == MouseButtonState.Pressed) result |= WfMouseBtns.Right;
            if (e.MiddleButton == MouseButtonState.Pressed) result |= WfMouseBtns.Middle;
            return result;
        }

        // WPF Key and WinForms Keys share the same underlying Win32 VK values for most keys.
        private static WfKeys ToWfKey(Key key) => (WfKeys)(int)KeyInterop.VirtualKeyFromKey(key);

        private static char GetChar(Key key) {
            int vk = KeyInterop.VirtualKeyFromKey(key);
            // Simple ASCII printable range
            if (vk >= 0x20 && vk < 0x7F) return (char)vk;
            return '\0';
        }
        #endregion

        #region Dispose
        public virtual void Dispose() {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            DisposeSizeDependentResources();
            _dwFactory?.Dispose();
            _d2dDevice?.Dispose();
            _d2dFactory?.Dispose();
            _d3dDevice?.Dispose();
            _d3dImage?.Dispose();
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
