namespace VRMR.Render.Gl
{
    /// <summary>GLSL ES 3.00 sources (ANGLE on Windows, native OpenGL ES 3 elsewhere).</summary>
    internal static class Shaders
    {
        /// <summary>
        /// The avatar mesh: face shapes, then linear blend skinning, as <see cref="SkinnedAvatar.Skin"/> does on the CPU.
        /// Each bone is three texels (the rows of a 3x4 matrix); a face shape is a row of deltas in the shape texture.
        /// The light is worked out per vertex and is two-sided.
        /// </summary>
        public const string MeshVertex = @"#version 300 es
precision highp float;
precision highp int;
precision highp sampler2D;
layout(location = 0) in vec3 a_pos;
layout(location = 1) in vec4 a_normal;
layout(location = 2) in vec2 a_uv;
layout(location = 3) in uvec4 a_bones;
layout(location = 4) in vec4 a_weights;
layout(location = 5) in int a_morph;
uniform mat4 u_viewProj;
uniform float u_scale;
uniform vec3 u_light;
uniform sampler2D u_bones;
uniform sampler2D u_morphs;
uniform int u_morphCount;
uniform int u_morphBase[64];
uniform float u_morphWeight[64];
out vec2 v_uv;
out float v_light;
void main() {
    vec3 p = a_pos;
    if (a_morph >= 0) {
        for (int i = 0; i < u_morphCount; i++) {
            int t = u_morphBase[i] + a_morph;
            p += texelFetch(u_morphs, ivec2(t % 2048, t / 2048), 0).xyz * u_morphWeight[i];
        }
    }
    vec3 n = a_normal.xyz;
    vec3 pos = p;
    vec3 nrm = n;
    if (a_weights.x + a_weights.y + a_weights.z + a_weights.w > 0.5) {
        vec4 sp = vec4(p * u_scale, 1.0);
        pos = vec3(0.0);
        nrm = vec3(0.0);
        for (int k = 0; k < 4; k++) {
            float w = a_weights[k];
            int b = int(a_bones[k]) * 3;
            vec4 r0 = texelFetch(u_bones, ivec2(b, 0), 0);
            vec4 r1 = texelFetch(u_bones, ivec2(b + 1, 0), 0);
            vec4 r2 = texelFetch(u_bones, ivec2(b + 2, 0), 0);
            pos += w * vec3(dot(r0, sp), dot(r1, sp), dot(r2, sp));
            nrm += w * vec3(dot(r0.xyz, n), dot(r1.xyz, n), dot(r2.xyz, n));
        }
    }
    float len = length(nrm);
    v_light = 0.58 + 0.42 * (len > 1e-6 ? abs(dot(nrm, u_light)) / len : 0.0);
    v_uv = a_uv;
    gl_Position = u_viewProj * vec4(pos, 1.0);
}";

        /// <summary>u_mode: 0 plain colour, 1 texture, 2 texture with cutout alpha (uploaded premultiplied, so edges do not fringe).</summary>
        public const string MeshFragment = @"#version 300 es
precision highp float;
uniform sampler2D u_tex;
uniform int u_mode;
uniform vec3 u_color;
in vec2 v_uv;
in float v_light;
out vec4 o_color;
void main() {
    vec3 c = u_color;
    if (u_mode != 0) {
        vec4 t = texture(u_tex, v_uv);
        if (u_mode == 2) {
            if (t.a < 0.5) discard;
            c = t.rgb / t.a;
        } else {
            c = t.rgb;
        }
    }
    o_color = vec4(min(c * v_light, vec3(1.0)), 1.0);
}";

        /// <summary>2D shapes in device pixels from the top-left; see <see cref="VectorBatch"/> for the kinds.</summary>
        public const string VectorVertex = @"#version 300 es
precision highp float;
layout(location = 0) in vec2 a_pos;
layout(location = 1) in vec2 a_local;
layout(location = 2) in vec4 a_params;
layout(location = 3) in float a_kind;
layout(location = 4) in vec4 a_color;
uniform vec2 u_viewport;
out vec2 v_local;
flat out vec4 v_params;
flat out int v_kind;
out vec4 v_color;
void main() {
    v_local = a_local;
    v_params = a_params;
    v_kind = int(a_kind + 0.5);
    v_color = a_color;
    gl_Position = vec4(a_pos.x / u_viewport.x * 2.0 - 1.0, 1.0 - a_pos.y / u_viewport.y * 2.0, 0.0, 1.0);
}";

        /// <summary>Coverage from the shape's distance function, so every edge is anti-aliased without multisampling.</summary>
        public const string VectorFragment = @"#version 300 es
precision highp float;
uniform sampler2D u_atlas;
uniform vec2 u_atlasTexel;
in vec2 v_local;
flat in vec4 v_params;
flat in int v_kind;
in vec4 v_color;
out vec4 o_color;
float dashes(float along) {
    if (v_params.w <= 0.0) return 1.0;
    float t = mod(along, v_params.w);
    return clamp(min(t, v_params.z - t) + 0.5, 0.0, 1.0);
}
void main() {
    float cov = 1.0;
    if (v_kind == 0) {
        float dx = v_local.x - clamp(v_local.x, 0.0, v_params.x);
        cov = clamp(v_params.y + 0.5 - length(vec2(dx, v_local.y)), 0.0, 1.0) * dashes(v_local.x);
    } else if (v_kind == 1) {
        cov = clamp(v_params.y + 0.5 - abs(length(v_local) - v_params.x), 0.0, 1.0)
            * dashes((atan(v_local.y, v_local.x) + 3.14159265) * v_params.x);
    } else if (v_kind == 2) {
        cov = clamp(v_params.x + 0.5 - length(v_local), 0.0, 1.0);
    } else if (v_kind == 4) {
        cov = texture(u_atlas, v_local * u_atlasTexel).r;
    } else if (v_kind == 5) {
        vec2 q = abs(v_local) - (v_params.xy - vec2(v_params.z));
        float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - v_params.z;
        cov = v_params.w > 0.0 ? clamp(v_params.w + 0.5 - abs(d), 0.0, 1.0) : clamp(0.5 - d, 0.0, 1.0);
    }
    o_color = vec4(v_color.rgb, v_color.a * cov);
}";
    }
}
