using System;
using Silk.NET.OpenGL;

namespace VRMR.Render.Gl
{
    /// <summary>The GPU copy of a <see cref="MeshModel"/> in one GL context: vertex and index buffers, textures, the face shape texture and the bone texture.</summary>
    internal sealed unsafe class GpuMesh
    {
        public readonly WeakReference<MeshModel> Model;
        public readonly uint Vao, BoneTexture, MorphTexture;
        public readonly uint[] Textures;
        readonly uint _vbo, _ibo;

        public GpuMesh(GL gl, MeshModel m)
        {
            Model = new WeakReference<MeshModel>(m);
            Vao = gl.GenVertexArray();
            gl.BindVertexArray(Vao);
            _vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (MeshVertex* p = m.Vertices) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(m.Vertices.Length * MeshVertex.Size), p, BufferUsageARB.StaticDraw);
            _ibo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ibo);
            fixed (uint* p = m.Indices) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(m.Indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

            const uint stride = MeshVertex.Size;
            gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Byte, true, stride, (void*)12);
            gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)16);
            gl.EnableVertexAttribArray(3); gl.VertexAttribIPointer(3, 4, VertexAttribIType.UnsignedShort, stride, (void*)24);
            gl.EnableVertexAttribArray(4); gl.VertexAttribPointer(4, 4, VertexAttribPointerType.UnsignedShort, true, stride, (void*)32);
            gl.EnableVertexAttribArray(5); gl.VertexAttribIPointer(5, 1, VertexAttribIType.Int, stride, (void*)40);
            gl.BindVertexArray(0);

            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            Textures = new uint[m.Textures.Length];
            for (int i = 0; i < Textures.Length; i++)
            {
                var t = m.Textures[i];
                if (t == null) continue;
                Textures[i] = gl.GenTexture();
                gl.BindTexture(TextureTarget.Texture2D, Textures[i]);
                fixed (byte* p = t.Rgba) gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)t.Width, (uint)t.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                gl.GenerateMipmap(TextureTarget.Texture2D);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            }

            BoneTexture = DataTexture(gl);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)Math.Max(1, m.BoneNodes.Length * 3), 1, 0, PixelFormat.Rgba, PixelType.Float, null);

            MorphTexture = DataTexture(gl);
            if (m.MorphHeight > 0)
                fixed (Half* p = m.MorphTexels) gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, MeshModel.MorphTextureWidth, (uint)m.MorphHeight, 0, PixelFormat.Rgba, PixelType.HalfFloat, p);
            else
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, 1, 1, 0, PixelFormat.Rgba, PixelType.HalfFloat, null);
            gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        /// <summary>A texture read with texelFetch: no filtering, no mip levels.</summary>
        static uint DataTexture(GL gl)
        {
            uint t = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, t);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            return t;
        }

        public void Delete(GL gl)
        {
            gl.DeleteVertexArray(Vao);
            gl.DeleteBuffer(_vbo);
            gl.DeleteBuffer(_ibo);
            foreach (uint t in Textures) if (t != 0) gl.DeleteTexture(t);
            gl.DeleteTexture(BoneTexture);
            gl.DeleteTexture(MorphTexture);
        }
    }
}
