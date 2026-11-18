using System;
using Silk.NET.OpenGL;

namespace VRMR.Render.Gl
{
    /// <summary>
    ///     The renderer's own framebuffer: 8-bit colour with a 24-bit depth buffer (the host's has 16 bits, too few for
    ///     clothes lying on skin), optionally multisampled. The finished frame is copied to the host's framebuffer.
    /// </summary>
    sealed class RenderTarget
    {
        readonly GL _gl;
        uint _color, _depth, _resolveFbo, _resolveColor;
        int _samples = -1;

        public RenderTarget(GL gl) { _gl = gl; }

        /// <summary>The framebuffer to draw the scene into.</summary>
        public uint SceneFbo { get; private set; }

        /// <summary>A single-sampled framebuffer holding the finished frame (after <see cref="Resolve" />).</summary>
        public uint ReadFbo => _samples > 0 ? _resolveFbo : SceneFbo;

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>Makes the buffers fit the size and sample count; a no-op when they already do.</summary>
        public void Ensure(int width, int height, int samples)
        {
            if (width == Width && height == Height && samples == _samples) return;
            Delete();
            Width = width;
            Height = height;
            _samples = samples;
            SceneFbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, SceneFbo);
            _color = Renderbuffer(InternalFormat.Rgba8, samples);
            _depth = Renderbuffer(InternalFormat.DepthComponent24, samples);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, _color);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, _depth);
            Check("scene");
            if (samples > 0)
            {
                _resolveFbo = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveFbo);
                _resolveColor = Renderbuffer(InternalFormat.Rgba8, 0);
                _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    RenderbufferTarget.Renderbuffer, _resolveColor);
                Check("resolve");
            }
        }

        uint Renderbuffer(InternalFormat format, int samples)
        {
            uint rb = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
            if (samples > 0)
                _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, format, (uint)Width,
                    (uint)Height);
            else _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, format, (uint)Width, (uint)Height);
            return rb;
        }

        void Check(string what)
        {
            var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
                throw new InvalidOperationException(
                    $"The 3D view's {what} framebuffer is incomplete ({status}, {Width}x{Height}, {_samples} samples).");
        }

        /// <summary>
        ///     With multisampling: averages the samples into the single-sampled copy. The formats match, as OpenGL ES
        ///     requires for this.
        /// </summary>
        public void Resolve()
        {
            if (_samples <= 0) return;
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, SceneFbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
            _gl.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height, ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
        }

        /// <summary>Copies the finished frame into another framebuffer of the same size, optionally upside down.</summary>
        public void CopyTo(uint framebuffer, bool flip)
        {
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, ReadFbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
            _gl.BlitFramebuffer(0, 0, Width, Height, 0, flip ? Height : 0, Width, flip ? 0 : Height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        }

        public void Delete()
        {
            if (SceneFbo != 0) _gl.DeleteFramebuffer(SceneFbo);
            if (_color != 0) _gl.DeleteRenderbuffer(_color);
            if (_depth != 0) _gl.DeleteRenderbuffer(_depth);
            if (_resolveFbo != 0) _gl.DeleteFramebuffer(_resolveFbo);
            if (_resolveColor != 0) _gl.DeleteRenderbuffer(_resolveColor);
            SceneFbo = _color = _depth = _resolveFbo = _resolveColor = 0;
            _samples = -1;
        }
    }
}
