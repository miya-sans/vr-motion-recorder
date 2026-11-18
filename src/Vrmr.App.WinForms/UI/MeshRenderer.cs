using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VRMR.UI
{
    /// <summary>
    ///     Small multi-threaded software rasterizer for the avatar preview: z-buffer, perspective-correct textures with
    ///     cutout alpha, soft two-sided lighting. Renders into a premultiplied ARGB bitmap with a transparent background,
    ///     so the viewport's grid shows through and its GDI+ overlays draw on top.
    /// </summary>
    public sealed class MeshRenderer : IDisposable
    {
        /// <summary>Half the cores: the preview runs next to VRChat and SteamVR.</summary>
        static readonly ParallelOptions Par = new()
            { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) };

        public double LastMs;
        Bitmap _bmp;

        int[] _px;

        // per-part, per-triangle: first and last pixel row the triangle can touch (first > last: not drawn), so a band
        // skips the triangles outside it with one comparison instead of setting each one up
        int[][] _row0, _row1;

        // per-part projected vertices (reused)
        float[][] _sx, _sy, _iz, _lt;
        int _w, _h;
        float[] _z;

        public void Dispose()
        {
            _bmp?.Dispose();
            _bmp = null;
        }

        public Bitmap Render(SkinnedAvatar m, Vec3 eye, Vec3 r, Vec3 u, Vec3 f, float focal, float cx, float cy, int w,
            int h, float near)
        {
            var sw = Stopwatch.StartNew();
            if (w < 1 || h < 1) return null;
            if (w != _w || h != _h || _bmp == null)
            {
                _w = w;
                _h = h;
                _px = new int[w * h];
                _z = new float[w * h];
                _bmp?.Dispose();
                _bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }

            Array.Clear(_px, 0, _px.Length);
            Array.Clear(_z, 0, _z.Length);
            int np = m.Parts.Count;
            if (_sx == null || _sx.Length != np)
            {
                _sx = new float[np][];
                _sy = new float[np][];
                _iz = new float[np][];
                _lt = new float[np][];
                _row0 = new int[np][];
                _row1 = new int[np][];
            }

            var light = (u * 0.75f - f * 0.6f - r * 0.35f).Normalized;
            Parallel.For(0, np, Par, pi =>
            {
                var part = m.Parts[pi];
                if (!part.Visible) return;
                int n = part.Pos.Length;
                if (_sx[pi] == null || _sx[pi].Length != n)
                {
                    _sx[pi] = new float[n];
                    _sy[pi] = new float[n];
                    _iz[pi] = new float[n];
                    _lt[pi] = new float[n];
                }

                float[] sx = _sx[pi];
                float[] sy = _sy[pi];
                float[] iz = _iz[pi];
                float[] lt = _lt[pi];
                for (int v = 0; v < n; v++)
                {
                    var d = part.Pos[v] - eye;
                    float z = Vec3.Dot(d, f);
                    if (z < near)
                    {
                        iz[v] = -1;
                        continue;
                    }

                    float inv = 1f / z;
                    sx[v] = cx + Vec3.Dot(d, r) * inv * focal;
                    sy[v] = cy - Vec3.Dot(d, u) * inv * focal;
                    iz[v] = inv;
                    var nn = part.Nrm[v];
                    float len = nn.Length;
                    float ndl = len > 1e-6f ? Vec3.Dot(nn, light) / len : 0;
                    lt[v] = 0.58f + 0.42f * Math.Abs(ndl);
                }

                // the rows each triangle covers (the same rounding as the fill loop), or none when it is clipped or off screen
                int[] tri = part.Tri;
                int nt = tri.Length / 3;
                if (_row0[pi] == null || _row0[pi].Length != nt)
                {
                    _row0[pi] = new int[nt];
                    _row1[pi] = new int[nt];
                }

                int[] r0 = _row0[pi];
                int[] r1 = _row1[pi];
                for (int t = 0; t < nt; t++)
                {
                    int a = tri[t * 3], b = tri[t * 3 + 1], c = tri[t * 3 + 2];
                    r0[t] = 1;
                    r1[t] = 0;
                    if (iz[a] <= 0 || iz[b] <= 0 || iz[c] <= 0) continue;
                    float minX = Math.Min(sx[a], Math.Min(sx[b], sx[c])),
                        maxX = Math.Max(sx[a], Math.Max(sx[b], sx[c]));
                    if (maxX < 0 || minX > w) continue;
                    float minY = Math.Min(sy[a], Math.Min(sy[b], sy[c])),
                        maxY = Math.Max(sy[a], Math.Max(sy[b], sy[c]));
                    if (maxY < 0 || minY > h) continue;
                    int y0 = Math.Max(0, (int)Math.Ceiling(minY - 0.5f)),
                        y1 = Math.Min(h - 1, (int)Math.Floor(maxY - 0.5f));
                    if (y0 <= y1)
                    {
                        r0[t] = y0;
                        r1[t] = y1;
                    }
                }
            });
            int bands = Math.Max(1, Math.Min(h / 8, Environment.ProcessorCount * 3));
            Parallel.For(0, bands, Par, b =>
            {
                int y0 = h * b / bands, y1 = h * (b + 1) / bands;
                for (int pi = 0; pi < np; pi++)
                {
                    var part = m.Parts[pi];
                    if (part.Visible)
                        DrawPart(m, part, _sx[pi], _sy[pi], _iz[pi], _lt[pi], _row0[pi], _row1[pi], y0, y1);
                }
            });
            var bd = _bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(_px, y * w, bd.Scan0 + y * bd.Stride, w);
            _bmp.UnlockBits(bd);
            LastMs = sw.Elapsed.TotalMilliseconds;
            return _bmp;
        }

        void DrawPart(SkinnedAvatar m, SkinnedAvatar.Part part, float[] sx, float[] sy, float[] iz, float[] lt,
            int[] row0, int[] row1, int y0, int y1)
        {
            int[] tri = part.Tri;
            float[] uv = part.UV;
            int[] mat = part.Mat;
            int w = _w;
            int[] px = _px;
            float[] zb = _z;
            int nt = tri.Length / 3;
            for (int t = 0; t < nt; t++)
            {
                if (row1[t] < y0 || row0[t] >= y1) continue; // not in this band (or not drawn at all)
                int a = tri[t * 3], b = tri[t * 3 + 1], c = tri[t * 3 + 2];
                float za = iz[a], zbv = iz[b], zc = iz[c];
                if (za <= 0 || zbv <= 0 || zc <= 0) continue;
                float xa = sx[a], ya = sy[a], xb = sx[b], yb = sy[b], xc = sx[c], yc = sy[c];
                float minY = Math.Min(ya, Math.Min(yb, yc)), maxY = Math.Max(ya, Math.Max(yb, yc));
                int iy0 = Math.Max(y0, (int)Math.Ceiling(minY - 0.5f)),
                    iy1 = Math.Min(y1 - 1, (int)Math.Floor(maxY - 0.5f));
                if (iy0 > iy1) continue;
                float minX = Math.Min(xa, Math.Min(xb, xc)), maxX = Math.Max(xa, Math.Max(xb, xc));
                int ix0 = Math.Max(0, (int)Math.Ceiling(minX - 0.5f)),
                    ix1 = Math.Min(w - 1, (int)Math.Floor(maxX - 0.5f));
                if (ix0 > ix1) continue;
                if (ix1 - ix0 > 4 * w || iy1 - iy0 > 4 * _h) continue;
                float area = (xb - xa) * (yc - ya) - (xc - xa) * (yb - ya);
                if (Math.Abs(area) < 1e-6f) continue;
                float ia = 1f / area;
                // barycentric gradients
                float d0x = (yb - yc) * ia, d1x = (yc - ya) * ia;
                float d0y = (xc - xb) * ia, d1y = (xa - xc) * ia;
                int ti = mat[t];
                var tex = ti >= 0 && ti < m.Textures.Count ? m.Textures[ti] : null;
                int[] tp = tex?.Px;
                int tw = tex?.W ?? 0, th = tex?.H ?? 0;
                bool cut = tex != null && tex.Cutout;
                float ua = uv[t * 6] * za,
                    va = uv[t * 6 + 1] * za,
                    ub = uv[t * 6 + 2] * zbv,
                    vb = uv[t * 6 + 3] * zbv,
                    uc = uv[t * 6 + 4] * zc,
                    vc = uv[t * 6 + 5] * zc;
                float la = lt[a], lb = lt[b], lc = lt[c];
                for (int y = iy0; y <= iy1; y++)
                {
                    float py = y + 0.5f, pxs = ix0 + 0.5f;
                    float b0 = ((xb - pxs) * (yc - py) - (xc - pxs) * (yb - py)) * ia;
                    float b1 = ((xc - pxs) * (ya - py) - (xa - pxs) * (yc - py)) * ia;
                    int row = y * w;
                    for (int x = ix0; x <= ix1; x++, b0 += d0x, b1 += d1x)
                    {
                        float b2 = 1 - b0 - b1;
                        if (b0 < 0 || b1 < 0 || b2 < 0) continue;
                        float z = b0 * za + b1 * zbv + b2 * zc;
                        int idx = row + x;
                        if (z <= zb[idx]) continue;
                        int col;
                        if (tp != null)
                        {
                            float iz1 = 1f / z;
                            float tu = (b0 * ua + b1 * ub + b2 * uc) * iz1, tv = (b0 * va + b1 * vb + b2 * vc) * iz1;
                            tu -= (float)Math.Floor(tu);
                            tv -= (float)Math.Floor(tv);
                            int tx = (int)(tu * tw), ty = (int)((1 - tv) * th);
                            if (tx >= tw) tx = tw - 1;
                            if (ty >= th) ty = th - 1;
                            if (ty < 0) ty = 0;
                            col = tp[ty * tw + tx];
                            if (cut && (uint)col >> 24 < 128) continue;
                        }
                        else col = unchecked((int)0xFFB8BCC8);

                        float l = b0 * la + b1 * lb + b2 * lc;
                        int rr = (int)(((col >> 16) & 255) * l),
                            gg = (int)(((col >> 8) & 255) * l),
                            bb = (int)((col & 255) * l);
                        if (rr > 255) rr = 255;
                        if (gg > 255) gg = 255;
                        if (bb > 255) bb = 255;
                        px[idx] = unchecked((int)0xFF000000) | (rr << 16) | (gg << 8) | bb;
                        zb[idx] = z;
                    }
                }
            }
        }
    }
}
