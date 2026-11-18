using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;

namespace VRMR.Render.Gl
{
    /// <summary>What one frame draws, prepared by <see cref="SceneBuilder" />.</summary>
    sealed class SceneDraw
    {
        public readonly GlyphAtlas Atlas = new();

        /// <summary>Every 2D shape of the frame; the first <see cref="UnderMeshVertices" /> vertices are drawn before the meshes.</summary>
        public readonly VectorBatch Batch = new();

        /// <summary>The actors whose mesh is drawn.</summary>
        public readonly List<ActorView> Meshes = new();

        public Rgba Background, Untextured;
        public ViewCamera Camera;
        public int Msaa;

        /// <summary>Device pixels per device-independent pixel.</summary>
        public float Scale = 1f;

        public int UnderMeshVertices;
    }

    /// <summary>
    ///     Draws a prepared frame with OpenGL ES 3.0: the floor shapes, the skinned avatar meshes with depth, then every
    ///     other shape on top. One instance per GL context; all calls need that context current.
    /// </summary>
    sealed unsafe class SceneRenderer
    {
        public readonly int MaxSamples;

        /// <summary>The GL implementation and the graphics adapter it runs on.</summary>
        public readonly string Version, Adapter;

        readonly ConditionalWeakTable<MeshModel, GpuMesh> _byModel = new();
        readonly GL _gl;
        readonly List<GpuMesh> _gpuMeshes = new();
        readonly ShaderProgram _mesh, _vector;
        readonly int[] _morphBase = new int[MeshModel.MaxActiveShapes];
        readonly float[] _morphWeight = new float[MeshModel.MaxActiveShapes];
        readonly RenderTarget _target;
        readonly int _uViewProj, _uScale, _uLight, _uMorphCount, _uMorphBase, _uMorphWeight, _uMode, _uColor;
        readonly int _uViewport, _uAtlasTexel;
        readonly uint _vectorVao, _vectorVbo, _atlasTexture;
        float[] _bones = new float[128 * 12];
        bool _errorsChecked;
        int _vectorCapacity;

        public SceneRenderer(GL gl)
        {
            _gl = gl;
            Version = gl.GetStringS(StringName.Version) ?? "";
            Adapter = gl.GetStringS(StringName.Renderer) ?? "";
            gl.GetInteger((GetPName)0x8D57 /* GL_MAX_SAMPLES */, out int maxSamples);
            MaxSamples = maxSamples;

            _mesh = new ShaderProgram(gl, "mesh", Shaders.MeshVertex, Shaders.MeshFragment);
            _uViewProj = _mesh.Uniform("u_viewProj");
            _uScale = _mesh.Uniform("u_scale");
            _uLight = _mesh.Uniform("u_light");
            _uMorphCount = _mesh.Uniform("u_morphCount");
            _uMorphBase = _mesh.Uniform("u_morphBase");
            _uMorphWeight = _mesh.Uniform("u_morphWeight");
            _uMode = _mesh.Uniform("u_mode");
            _uColor = _mesh.Uniform("u_color");
            gl.UseProgram(_mesh.Handle);
            gl.Uniform1(_mesh.Uniform("u_tex"), 0);
            gl.Uniform1(_mesh.Uniform("u_bones"), 1);
            gl.Uniform1(_mesh.Uniform("u_morphs"), 2);

            _vector = new ShaderProgram(gl, "vector", Shaders.VectorVertex, Shaders.VectorFragment);
            _uViewport = _vector.Uniform("u_viewport");
            _uAtlasTexel = _vector.Uniform("u_atlasTexel");
            gl.UseProgram(_vector.Handle);
            gl.Uniform1(_vector.Uniform("u_atlas"), 0);
            gl.Uniform2(_uAtlasTexel, 1f / GlyphAtlas.Size, 1f / GlyphAtlas.Size);
            gl.UseProgram(0);

            _vectorVao = gl.GenVertexArray();
            gl.BindVertexArray(_vectorVao);
            _vectorVbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vectorVbo);
            const uint stride = VectorVertex.Size;
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)16);
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)32);
            gl.EnableVertexAttribArray(4);
            gl.VertexAttribPointer(4, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)36);
            gl.BindVertexArray(0);

            _atlasTexture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _atlasTexture);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, GlyphAtlas.Size, GlyphAtlas.Size, 0,
                PixelFormat.Red, PixelType.UnsignedByte, null);
            gl.BindTexture(TextureTarget.Texture2D, 0);

            _target = new RenderTarget(gl);
        }

        /// <summary>Triangles of avatar meshes in the last frame.</summary>
        public int LastTriangles { get; private set; }

        public int Width => _target.Width;
        public int Height => _target.Height;

        /// <summary>
        ///     Draws the frame into the renderer's own buffers and, when target is given, copies it there
        ///     (flip: upside down, for hosts whose first row is the top one).
        /// </summary>
        public void Render(SceneDraw scene, int width, int height, uint? target, bool flip)
        {
            var gl = _gl;
            SweepMeshes();
            _target.Ensure(width, height, Math.Min(Math.Max(0, scene.Msaa), MaxSamples));
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _target.SceneFbo);
            gl.Viewport(0, 0, (uint)width, (uint)height);
            gl.Disable(EnableCap.ScissorTest);
            gl.Disable(EnableCap.CullFace);
            gl.DepthMask(true);
            var bg = scene.Background;
            gl.ClearColor(bg.R / 255f, bg.G / 255f, bg.B / 255f, 1f);
            gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

            UploadVectors(scene);
            DrawVectors(0, scene.UnderMeshVertices, width, height);
            DrawMeshes(scene, width, height);
            DrawVectors(scene.UnderMeshVertices, scene.Batch.Count - scene.UnderMeshVertices, width, height);

            _target.Resolve();
            if (target.HasValue)
            {
                _target.CopyTo(target.Value, flip);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.Value);
            }

            if (!_errorsChecked)
            {
                _errorsChecked = true;
                for (GLEnum e; (e = gl.GetError()) != GLEnum.NoError;)
                    AppLog.Warn("3D view: OpenGL error " + e + " in the first frame");
            }
        }

        void UploadVectors(SceneDraw scene)
        {
            var gl = _gl;
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            if (scene.Atlas.Dirty)
            {
                scene.Atlas.Dirty = false;
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, _atlasTexture);
                fixed (byte* p = scene.Atlas.Pixels)
                    gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, GlyphAtlas.Size, GlyphAtlas.Size,
                        PixelFormat.Red, PixelType.UnsignedByte, p);
            }

            var vertices = scene.Batch.Vertices;
            if (vertices.Length == 0) return;
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vectorVbo);
            if (vertices.Length > _vectorCapacity)
            {
                _vectorCapacity = Math.Max(vertices.Length, _vectorCapacity * 2);
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vectorCapacity * VectorVertex.Size), null,
                    BufferUsageARB.DynamicDraw);
            }

            fixed (VectorVertex* p =
                       vertices)
                gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(vertices.Length * VectorVertex.Size), p);
        }

        void DrawVectors(int first, int count, int width, int height)
        {
            if (count <= 0) return;
            var gl = _gl;
            gl.Disable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero,
                BlendingFactor.One); // the frame stays opaque
            gl.UseProgram(_vector.Handle);
            gl.Uniform2(_uViewport, width, (float)height);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _atlasTexture);
            gl.BindVertexArray(_vectorVao);
            gl.DrawArrays(PrimitiveType.Triangles, first, (uint)count);
            gl.BindVertexArray(0);
        }

        void DrawMeshes(SceneDraw scene, int width, int height)
        {
            LastTriangles = 0;
            if (scene.Meshes.Count == 0) return;
            var gl = _gl;
            var cam = scene.Camera;
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.Disable(EnableCap.Blend);
            gl.UseProgram(_mesh.Handle);
            Span<float> vp = stackalloc float[16];
            cam.ViewProjection(vp, width, height, scene.Scale);
            fixed (float* p = vp) gl.UniformMatrix4(_uViewProj, 1, false, p);
            var light = (cam.Up * 0.75f - cam.Forward * 0.6f - cam.Right * 0.35f)
                .Normalized; // a soft key light from the upper left of the camera
            gl.Uniform3(_uLight, light.x, light.y, light.z);
            var plain = scene.Untextured;
            gl.Uniform3(_uColor, plain.R / 255f, plain.G / 255f, plain.B / 255f);

            foreach (var actor in scene.Meshes)
            {
                var model = actor.Model;
                var gpu = GpuFor(model);
                UploadBones(model, gpu, actor);
                gl.ActiveTexture(TextureUnit.Texture2);
                gl.BindTexture(TextureTarget.Texture2D, gpu.MorphTexture);
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.Uniform1(_uScale, actor.Scale);
                gl.BindVertexArray(gpu.Vao);
                int mode = -1, shapes = -1;
                uint bound = uint.MaxValue;
                bool[] hidden = actor.HiddenParts;
                for (int part = 0; part < model.PartFirstSub.Length; part++)
                {
                    if (hidden != null && part < hidden.Length && hidden[part]) continue;
                    int active = ActiveShapes(model.PartMorphs[part], actor.Face);
                    if (active > 0)
                    {
                        fixed (int* b = _morphBase) gl.Uniform1(_uMorphBase, (uint)active, b);
                        fixed (float* w = _morphWeight) gl.Uniform1(_uMorphWeight, (uint)active, w);
                    }

                    if (active != shapes)
                    {
                        shapes = active;
                        gl.Uniform1(_uMorphCount, active);
                    }

                    for (int s = model.PartFirstSub[part], end = s + model.PartSubCount[part]; s < end; s++)
                    {
                        ref readonly var sub = ref model.SubMeshes[s];
                        uint texture = sub.Texture >= 0 ? gpu.Textures[sub.Texture] : 0;
                        int m = texture == 0 ? 0 : model.Textures[sub.Texture].Cutout ? 2 : 1;
                        if (m != mode)
                        {
                            mode = m;
                            gl.Uniform1(_uMode, m);
                        }

                        if (texture != 0 && texture != bound)
                        {
                            bound = texture;
                            gl.BindTexture(TextureTarget.Texture2D, texture);
                        }

                        gl.DrawElements(PrimitiveType.Triangles, (uint)sub.IndexCount, DrawElementsType.UnsignedInt,
                            (void*)(sub.IndexStart * sizeof(uint)));
                        LastTriangles += sub.IndexCount / 3;
                    }
                }
            }

            gl.BindVertexArray(0);
        }

        /// <summary>
        ///     One 3x4 matrix per bone of the model's palette: the bone's rotation and position now, relative to its bind
        ///     pose, with the avatar scale folded into the translation. The same numbers <see cref="SkinnedAvatar.Skin" /> uses.
        /// </summary>
        void UploadBones(MeshModel model, GpuMesh gpu, ActorView actor)
        {
            int n = model.BoneNodes.Length;
            if (n == 0) return;
            if (_bones.Length < n * 12) _bones = new float[n * 12];
            float scale = actor.Scale;
            var nodePos = actor.NodePos;
            var nodeRot = actor.NodeRot;
            for (int i = 0; i < n; i++)
            {
                int node = model.BoneNodes[i];
                var q = (nodeRot[node] * model.BoneBindRot[i].Conjugate).Normalized;
                float x = q.x, y = q.y, z = q.z, w = q.w;
                float r00 = 1 - 2 * (y * y + z * z), r01 = 2 * (x * y - z * w), r02 = 2 * (x * z + y * w);
                float r10 = 2 * (x * y + z * w), r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - x * w);
                float r20 = 2 * (x * z - y * w), r21 = 2 * (y * z + x * w), r22 = 1 - 2 * (x * x + y * y);
                var b = model.BoneBindPos[i];
                var c = nodePos[node];
                int o = i * 12;
                _bones[o] = r00;
                _bones[o + 1] = r01;
                _bones[o + 2] = r02;
                _bones[o + 3] = c.x - scale * (r00 * b.x + r01 * b.y + r02 * b.z);
                _bones[o + 4] = r10;
                _bones[o + 5] = r11;
                _bones[o + 6] = r12;
                _bones[o + 7] = c.y - scale * (r10 * b.x + r11 * b.y + r12 * b.z);
                _bones[o + 8] = r20;
                _bones[o + 9] = r21;
                _bones[o + 10] = r22;
                _bones[o + 11] = c.z - scale * (r20 * b.x + r21 * b.y + r22 * b.z);
            }

            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, gpu.BoneTexture);
            fixed (float* p = _bones)
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)(n * 3), 1, PixelFormat.Rgba, PixelType.Float,
                    p);
        }

        /// <summary>
        ///     Fills the shape uniforms with the part's face shapes that are on (0.01 or more either way: add-ons drive some
        ///     below zero); returns how many.
        /// </summary>
        int ActiveShapes(MorphRow[] rows, FaceWeights face)
        {
            int n = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                float w = face.Resolve(rows[i].Channel);
                if (Math.Abs(w) < 0.01f) continue;
                w = Math.Min(1f, w);
                int slot = n;
                if (n < _morphBase.Length) n++;
                else
                {
                    // more shapes on than the shader takes: the weakest gives way
                    slot = 0;
                    for (int k = 1; k < n; k++)
                        if (Math.Abs(_morphWeight[k]) < Math.Abs(_morphWeight[slot]))
                            slot = k;
                    if (Math.Abs(_morphWeight[slot]) >= Math.Abs(w)) continue;
                }

                _morphBase[slot] = rows[i].BaseTexel;
                _morphWeight[slot] = w;
            }

            return n;
        }

        GpuMesh GpuFor(MeshModel model)
        {
            if (_byModel.TryGetValue(model, out var gpu)) return gpu;
            gpu = new GpuMesh(_gl, model);
            _byModel.Add(model, gpu);
            _gpuMeshes.Add(gpu);
            return gpu;
        }

        /// <summary>Frees the GPU copies of models that were disposed or collected.</summary>
        void SweepMeshes()
        {
            for (int i = _gpuMeshes.Count - 1; i >= 0; i--)
            {
                var gpu = _gpuMeshes[i];
                bool alive = gpu.Model.TryGetTarget(out var model);
                if (alive && !model.IsDisposed) continue;
                if (alive) _byModel.Remove(model);
                gpu.Delete(_gl);
                _gpuMeshes.RemoveAt(i);
            }
        }

        /// <summary>The finished frame as RGBA bytes, top row first. rgba must hold width * height * 4 bytes.</summary>
        public void ReadPixels(Span<byte> rgba)
        {
            int w = _target.Width, h = _target.Height, stride = w * 4;
            if (rgba.Length < stride * h)
                throw new ArgumentException("The buffer is smaller than the frame.", nameof(rgba));
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _target.ReadFbo);
            _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            fixed (byte* p = rgba) _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            var row = stride <= 16384 ? stackalloc byte[stride] : new byte[stride];
            for (int y = 0; y < h / 2; y++) // GL's first row is the bottom one
            {
                var a = rgba.Slice(y * stride, stride);
                var b = rgba.Slice((h - 1 - y) * stride, stride);
                a.CopyTo(row);
                b.CopyTo(a);
                row.CopyTo(b);
            }
        }

        /// <summary>Deletes every GL object. The context must be current and alive.</summary>
        public void Dispose()
        {
            foreach (var gpu in _gpuMeshes) gpu.Delete(_gl);
            _gpuMeshes.Clear();
            _byModel.Clear();
            _target.Delete();
            _gl.DeleteVertexArray(_vectorVao);
            _gl.DeleteBuffer(_vectorVbo);
            _gl.DeleteTexture(_atlasTexture);
            _mesh.Delete();
            _vector.Delete();
        }
    }
}
