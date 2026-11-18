using System;
using Silk.NET.OpenGL;

namespace VRMR.Render.Gl
{
    /// <summary>A linked vertex + fragment shader pair.</summary>
    internal sealed class ShaderProgram
    {
        public readonly uint Handle;
        readonly GL _gl;

        public ShaderProgram(GL gl, string name, string vertexSource, string fragmentSource)
        {
            _gl = gl;
            uint vs = Compile(ShaderType.VertexShader, name, vertexSource), fs = Compile(ShaderType.FragmentShader, name, fragmentSource);
            Handle = gl.CreateProgram();
            gl.AttachShader(Handle, vs);
            gl.AttachShader(Handle, fs);
            gl.LinkProgram(Handle);
            gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int ok);
            string log = ok == 0 ? gl.GetProgramInfoLog(Handle) : null;
            gl.DetachShader(Handle, vs); gl.DetachShader(Handle, fs);
            gl.DeleteShader(vs); gl.DeleteShader(fs);
            if (ok == 0) { gl.DeleteProgram(Handle); throw new InvalidOperationException("The '" + name + "' shader did not link: " + log); }
        }

        uint Compile(ShaderType type, string name, string source)
        {
            uint s = _gl.CreateShader(type);
            _gl.ShaderSource(s, source);
            _gl.CompileShader(s);
            _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok != 0) return s;
            string log = _gl.GetShaderInfoLog(s);
            _gl.DeleteShader(s);
            throw new InvalidOperationException("The '" + name + "' " + type + " did not compile: " + log);
        }

        public int Uniform(string name) => _gl.GetUniformLocation(Handle, name);

        public void Delete() => _gl.DeleteProgram(Handle);
    }
}
