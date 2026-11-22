using System;
using System.Numerics;

namespace VRMR.Render
{
    /// <summary>
    ///     The 3D view's orbit camera: it looks at <see cref="Target" /> from <see cref="Distance" /> metres away. World space
    ///     is Unity's (metres, +Y up, +Z forward); screen positions are in device-independent pixels from the view's top-left.
    /// </summary>
    public sealed class ViewCamera
    {
        /// <summary>Nearest distance in front of the eye that is drawn, in metres.</summary>
        public const float Near = 0.03f;

        /// <summary>Farthest distance that is drawn, in metres.</summary>
        public const float Far = 200f;

        public const float MinDistance = 0.3f, MaxDistance = 40f;

        /// <summary>How far behind the target an orthographic eye sits, so nothing in the scene is cut by the near plane.</summary>
        const float OrthoBack = 60f;

        bool _dirty = true, _ortho;
        Vec3 _eye, _right, _up, _forward;
        float _focal, _orthoScale;
        Vec3 _target = new(0, 0.75f, 0);

        float _yaw = 22, _pitch = 12, _distance = 3.0f, _fov = 45;

        /// <summary>Heading of the eye around the target, in degrees.</summary>
        public float Yaw
        {
            get => _yaw;
            set
            {
                _yaw = value;
                _dirty = true;
            }
        }

        /// <summary>Elevation of the eye above the target, in degrees (-85 to 85).</summary>
        public float Pitch
        {
            get => _pitch;
            set
            {
                _pitch = MathX.Clamp(value, -85, 85);
                _dirty = true;
            }
        }

        /// <summary>
        ///     Parallel projection: sizes don't shrink with depth. <see cref="Distance" /> then only sets the zoom (the target
        ///     looks as big as it would in perspective).
        /// </summary>
        public bool Orthographic
        {
            get => _ortho;
            set
            {
                _ortho = value;
                _dirty = true;
            }
        }

        /// <summary>Looks along an axis: heading and elevation in degrees, elevation up to straight down (90).</summary>
        public void LookFrom(float yaw, float pitch)
        {
            _yaw = yaw;
            _pitch = MathX.Clamp(pitch, -89.9f, 89.9f);
            _dirty = true;
        }

        /// <summary>Distance from the eye to the target, in metres.</summary>
        public float Distance
        {
            get => _distance;
            set
            {
                _distance = MathX.Clamp(value, MinDistance, MaxDistance);
                _dirty = true;
            }
        }

        /// <summary>Vertical field of view, in degrees.</summary>
        public float Fov
        {
            get => _fov;
            set
            {
                _fov = MathX.Clamp(value, 5, 120);
                _dirty = true;
            }
        }

        /// <summary>The point the camera orbits.</summary>
        public Vec3 Target
        {
            get => _target;
            set
            {
                _target = value;
                _dirty = true;
            }
        }

        /// <summary>Size of the view in device-independent pixels (the control keeps it up to date).</summary>
        public float Width { get; private set; } = 1;

        public float Height { get; private set; } = 1;

        public Vec3 Eye
        {
            get
            {
                Update();
                return _eye;
            }
        }

        public Vec3 Right
        {
            get
            {
                Update();
                return _right;
            }
        }

        public Vec3 Up
        {
            get
            {
                Update();
                return _up;
            }
        }

        public Vec3 Forward
        {
            get
            {
                Update();
                return _forward;
            }
        }

        /// <summary>Pixels per unit of tangent: a point one metre off axis at one metre depth is this far from the centre.</summary>
        public float Focal
        {
            get
            {
                Update();
                return _focal;
            }
        }

        /// <summary>Sets the size of the view the camera projects into.</summary>
        public void SetViewport(float width, float height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == Width && height == Height) return;
            Width = width;
            Height = height;
            _dirty = true;
        }

        void Update()
        {
            if (!_dirty) return;
            _dirty = false;
            float yr = _yaw * MathX.Deg2Rad, pr = _pitch * MathX.Deg2Rad;
            var dir = new Vec3((float)(Math.Sin(yr) * Math.Cos(pr)), (float)Math.Sin(pr),
                (float)(Math.Cos(yr) * Math.Cos(pr)));
            _eye = _target + dir * (_ortho ? OrthoBack : _distance);
            _forward = (_target - _eye).Normalized;
            _right = Vec3.Cross(Vec3.Up, _forward).Normalized;
            _up = Vec3.Cross(_forward, _right);
            _focal = Height / 2f / (float)Math.Tan(_fov * 0.5f * MathX.Deg2Rad);
            _orthoScale = _focal / _distance;
        }

        /// <summary>Pixels per metre across the view at a depth (the same at every depth when orthographic).</summary>
        public float Scale(float depth)
        {
            Update();
            return _ortho ? _orthoScale : _focal / depth;
        }

        /// <summary>A world point in camera space: x right, y up, z depth along the view direction.</summary>
        public Vec3 ToCamera(Vec3 world)
        {
            Update();
            var d = world - _eye;
            return new Vec3(Vec3.Dot(d, _right), Vec3.Dot(d, _up), Vec3.Dot(d, _forward));
        }

        /// <summary>Screen position of a camera-space point (its depth must be positive).</summary>
        public Vector2 FromCamera(Vec3 c)
        {
            float k = Scale(c.z);
            return new Vector2(Width / 2f + c.x * k, Height / 2f - c.y * k);
        }

        /// <summary>Depth of a world point along the view direction, in metres (negative behind the eye).</summary>
        public float Depth(Vec3 world)
        {
            Update();
            return Vec3.Dot(world - _eye, _forward);
        }

        /// <summary>Screen position of a world point; false when it is behind the camera.</summary>
        public bool Project(Vec3 world, out Vector2 screen)
        {
            var c = ToCamera(world);
            if (c.z < Near)
            {
                screen = default;
                return false;
            }

            screen = FromCamera(c);
            return true;
        }

        /// <summary>The world-space ray through a screen point.</summary>
        public void Ray(Vector2 screen, out Vec3 origin, out Vec3 dir)
        {
            Update();
            if (_ortho)
            {
                origin = _eye + _right * ((screen.X - Width / 2f) / _orthoScale) -
                         _up * ((screen.Y - Height / 2f) / _orthoScale);
                dir = _forward;
                return;
            }

            origin = _eye;
            dir = (_forward * _focal + _right * (screen.X - Width / 2f) - _up * (screen.Y - Height / 2f)).Normalized;
        }

        /// <summary>
        ///     A world-space segment on screen, cut at the near plane; false when all of it is behind the camera.
        ///     depth is the mean depth of its ends (for sorting and fading).
        /// </summary>
        public bool Segment(Vec3 a, Vec3 b, out Vector2 pa, out Vector2 pb, out float depth)
        {
            var ca = ToCamera(a);
            var cb = ToCamera(b);
            depth = (ca.z + cb.z) * 0.5f;
            pa = pb = default;
            if (ca.z < Near && cb.z < Near) return false;
            if (ca.z < Near) ca = Vec3.Lerp(ca, cb, (Near - ca.z) / (cb.z - ca.z));
            if (cb.z < Near) cb = Vec3.Lerp(cb, ca, (Near - cb.z) / (ca.z - cb.z));
            pa = FromCamera(ca);
            pb = FromCamera(cb);
            return true;
        }

        /// <summary>
        ///     World to clip space for a view of pixelWidth x pixelHeight device pixels, column-major, 16 floats. It maps
        ///     exactly as <see cref="Project" /> does, so GPU geometry and screen-space overlays line up.
        /// </summary>
        internal void ViewProjection(Span<float> m, float pixelWidth, float pixelHeight, float pixelsPerDip)
        {
            Update();
            // the pixel size is the scaled size rounded down: the same sub-pixel shift the screen-space overlays get
            float ox = Width * pixelsPerDip / pixelWidth - 1f, oy = 1f - Height * pixelsPerDip / pixelHeight;
            Vec3 r = _right, u = _up, f = _forward, e = _eye;
            if (_ortho)
            {
                // clip = (sx * dot(d, r) + ox, sy * dot(d, u) + oy, depth mapped Near..Far to -1..1, w = 1)
                float sx = _orthoScale * pixelsPerDip / (pixelWidth / 2f),
                    sy = _orthoScale * pixelsPerDip / (pixelHeight / 2f);
                float dz = 2f / (Far - Near), oz = -(Far + Near) / (Far - Near);
                m[0] = sx * r.x;
                m[4] = sx * r.y;
                m[8] = sx * r.z;
                m[12] = -sx * Vec3.Dot(e, r) + ox;
                m[1] = sy * u.x;
                m[5] = sy * u.y;
                m[9] = sy * u.z;
                m[13] = -sy * Vec3.Dot(e, u) + oy;
                m[2] = dz * f.x;
                m[6] = dz * f.y;
                m[10] = dz * f.z;
                m[14] = -dz * Vec3.Dot(e, f) + oz;
                m[3] = m[7] = m[11] = 0;
                m[15] = 1;
                return;
            }

            float fx = _focal * pixelsPerDip / (pixelWidth / 2f), fy = _focal * pixelsPerDip / (pixelHeight / 2f);
            float a = (Far + Near) / (Far - Near), b = -2f * Far * Near / (Far - Near);
            // clip = (fx * dot(d, r) + ox * w, fy * dot(d, u) + oy * w, a * dot(d, f) + b, w = dot(d, f)) with d = p - eye
            float ef = Vec3.Dot(e, f);
            m[0] = fx * r.x + ox * f.x;
            m[4] = fx * r.y + ox * f.y;
            m[8] = fx * r.z + ox * f.z;
            m[12] = -fx * Vec3.Dot(e, r) - ox * ef;
            m[1] = fy * u.x + oy * f.x;
            m[5] = fy * u.y + oy * f.y;
            m[9] = fy * u.z + oy * f.z;
            m[13] = -fy * Vec3.Dot(e, u) - oy * ef;
            m[2] = a * f.x;
            m[6] = a * f.y;
            m[10] = a * f.z;
            m[14] = -a * Vec3.Dot(e, f) + b;
            m[3] = f.x;
            m[7] = f.y;
            m[11] = f.z;
            m[15] = -Vec3.Dot(e, f);
        }
    }
}
