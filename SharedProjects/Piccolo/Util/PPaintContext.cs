/*
 * Copyright (c) 2003-2006, University of Maryland
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without modification, are permitted provided
 * that the following conditions are met:
 *
 *		Redistributions of source code must retain the above copyright notice, this list of conditions
 *		and the following disclaimer.
 *
 *		Redistributions in binary form must reproduce the above copyright notice, this list of conditions
 *		and the following disclaimer in the documentation and/or other materials provided with the
 *		distribution.
 *
 *		Neither the name of the University of Maryland nor the names of its contributors may be used to
 *		endorse or promote products derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
 * WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
 * PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
 * ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
 * LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR
 * TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
 * ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * Piccolo was written at the Human-Computer Interaction Laboratory www.cs.umd.edu/hcil by Jesse Grosjean
 * and ported to C# by Aaron Clamage under the supervision of Ben Bederson.  The Piccolo website is
 * www.cs.umd.edu/hcil/piccolo.
 */

using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Piccolo.Util {
	#region Enums
	/// <summary>
	/// This enumeration is used by the <see cref="PPaintContext"/> class.  It represents the
	/// quality level with which the piccolo scene graph will be rendered.
	/// </summary>
	/// <remarks>Lower quality rendering is faster.</remarks>
	public enum RenderQuality {
		/// <summary>
		/// The scene graph will be rendered in low quality mode.
		/// </summary>
		LowQuality,

		/// <summary>
		/// The scene graph will be rendered in high quality mode.
		/// </summary>
		HighQuality
	}
	#endregion

	/// <summary>
	/// <b>PPaintContext</b> is used by piccolo nodes to paint themselves off-screen (e.g. ToImage).
	/// </summary>
	/// <remarks>
	/// This class wraps a Graphics object to implement GDI+ based off-screen rendering.
	/// For on-screen rendering, use <see cref="PD2DPaintContext"/> instead.
	/// </remarks>
	public sealed class PPaintContext {
		#region Fields

        /// <summary>
		/// The graphics object used for rendering.
		/// </summary>
        private readonly Graphics graphics;

		/// <summary>
		/// Rendering hints for this paint context.
		/// </summary>
        private RenderQuality renderQuality;

		/// <summary>
		/// A stack of the clip regions that the paint context applies.  These regions are not
		/// affected by the matrices in the transform stack.
		/// </summary>
        private readonly Stack<Region> clipStack;

		/// <summary>
		/// A stack of rectangles representing the local clips.  These values will be affected by
		/// the matrices in the transform stack.
		/// </summary>
        private readonly Stack<RectangleF> localClipStack;

		/// <summary>
		/// A stack of the cameras being painted.
		/// </summary>
        private readonly Stack<PCamera> cameraStack;

		/// <summary>
		/// A stack of the transforms that the paint context applies.
		/// </summary>
        private readonly Stack<Matrix> transformStack;
		#endregion

		#region Constructors
		/// <summary>
		/// Constructs a new PPaintContext for GDI+ off-screen rendering (e.g. ToImage).
		/// </summary>
		/// <param name="graphics">
		/// The graphics context to associate with this paint context.
		/// </param>
		public PPaintContext(Graphics graphics) {
			this.graphics = graphics;
			clipStack = new Stack<Region>();
			localClipStack = new Stack<RectangleF>();
			cameraStack = new Stack<PCamera>();
			transformStack = new Stack<Matrix>();
			RenderQuality = RenderQuality.HighQuality;

            InitializeStacks();
		}

		/// <summary>
		/// Override this method to modify the initial state of the context attribute stacks.
		/// </summary>
        private void InitializeStacks() {
			localClipStack.Push(graphics.ClipBounds);
		}
		#endregion

		#region Context Attributes
		//****************************************************************
		// Context Attributes - Methods for accessing attributes of the
		// graphics context.
		//****************************************************************

		/// <summary>
		/// Gets the graphics context associated with this paint context.
		/// </summary>
		/// <value>The graphics context associated with this paint context.</value>
		public Graphics Graphics => graphics;

        /// <summary>
		/// Gets the current local clip.
		/// </summary>
		/// <value>The current local clip.</value>
		public RectangleF LocalClip => localClipStack.Peek();

        /// <summary>
		/// Gets the scale value applied by the graphics context associated with this paint
		/// context.
		/// </summary>
		public float Scale {
			get {
                Span<PointF> pts = stackalloc PointF[2];
                pts[0] = new PointF(0, 0);
				pts[1] = new PointF(1, 0);
                var elements = graphics.Transform.Elements.AsSpan();
				PMatrix.TransformPoints(MemoryMarshal.Cast<float, Matrix3x2>(elements)[0], pts);
				return PUtil.DistanceBetweenPoints(pts[0], pts[1]);
			}
		}
		#endregion

		#region Context Attribute Stacks
		//****************************************************************
		// Context Attribute Stacks - Attributes that can be pushed and
		// popped.
		//****************************************************************

		/// <summary>
		/// Pushes the given camera onto the camera stack.
		/// </summary>
		/// <param name="camera">The camera to push.</param>
		public void PushCamera(PCamera camera) {
			cameraStack.Push(camera);
		}

        /// <summary>
        /// Pops a camera from the camera stack.
        /// </summary>
        public void PopCamera() {
			cameraStack.Pop();
		}

		/// <summary>
		/// Gets the bottom-most camera on the camera stack (the last camera pushed).
		/// </summary>
		public PCamera Camera => cameraStack.Peek();

        /// <summary>
		/// Pushes the current clip onto the clip stack and sets clip of the graphics context to
		/// the intersection of the current clip and the given clip.
		/// </summary>
		/// <param name="aClip">The clip to push.</param>
		public void PushClip(Region aClip) {
			RectangleF newLocalClip = RectangleF.Intersect(LocalClip, aClip.GetBounds(graphics));
			localClipStack.Push(newLocalClip);

			Region currentClip = Graphics.Clip;
			clipStack.Push(currentClip);
			aClip = aClip.Clone();
			aClip.Intersect(currentClip);
			Graphics.Clip = aClip;
		}

		/// <summary>
		/// Pops a clip from both the clip stack and the local clip stack and sets the clip of the
		/// graphics context to the clip popped from the clip stack.
		/// </summary>
		public void PopClip() {
			Region newClip = clipStack.Pop();
			Graphics.Clip = newClip;
			localClipStack.Pop();
		}

		/// <summary>
		/// Pushes the given matrix onto the transform stack.
		/// </summary>
		/// <param name="matrix">The matrix to push.</param>
		public void PushMatrix(PMatrix matrix) {
			if (matrix == null) return;
			RectangleF newLocalClip = matrix.InverseTransform(LocalClip);
			transformStack.Push(graphics.Transform);
			localClipStack.Push(newLocalClip);
			graphics.MultiplyTransform(matrix.GetGdiMatrix());
		}

		/// <summary>
		/// Pops a matrix from the transform stack.
		/// </summary>
		public void PopMatrix() {
			graphics.Transform = transformStack.Pop();
			localClipStack.Pop();
		}
		#endregion

		#region Render Quality
		//****************************************************************
		// Render Quality - Methods for setting the rendering hints.
		//****************************************************************

		/// <summary>
		/// Sets the rendering hints for this paint context.
		/// </summary>
		public RenderQuality RenderQuality {
			get => renderQuality;
            set {
				renderQuality = value;

				switch (value) {
					case RenderQuality.HighQuality:
						graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
						graphics.SmoothingMode = SmoothingMode.HighQuality;
						graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
						graphics.CompositingQuality = CompositingQuality.HighQuality;
						graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
						break;
					case RenderQuality.LowQuality:
						graphics.InterpolationMode = InterpolationMode.Low;
						graphics.SmoothingMode = SmoothingMode.HighSpeed;
						graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixel;
						graphics.CompositingQuality = CompositingQuality.HighSpeed;
						graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
						break;
				}
			}
		}
        #endregion

		#region Debugging
		/// <summary>
		/// Override this method to change the way the clipping region is painted when the debug
		/// region management flag is set.
		/// </summary>
		/// <param name="brush">The brush to use for painting the clipping region.</param>
		public void PaintClipRegion(Brush brush) {
			graphics.FillRectangle(brush, graphics.ClipBounds);
		}
		#endregion
	}
}
