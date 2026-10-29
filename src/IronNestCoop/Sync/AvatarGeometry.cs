using System;
using System.Collections.Generic;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// The crew figure's shapes as plain numbers (no Unity types), so the same geometry feeds the game
    /// (<see cref="AvatarModel"/>) and the offline preview in docs/avatar. The figure is made of low-poly lathes and
    /// boxes, flat shaded: every triangle has its own three vertices and its own face normal.
    /// Metres, +y up, +z forward (the way the figure faces). Built once and shared by every figure.
    /// Every shape is a closed shell (no open edges), so no part can be seen through from any angle; the goggle
    /// lenses are the one exception, flat discs set into the closed rim cups.
    /// </summary>
    public static class AvatarGeometry
    {
        // ------------------------------------------------------------------ materials (submesh slots)

        public const int Coat = 0, Leather = 1, Brass = 2, Glow = 3, Player = 4;
        public static readonly string[] MaterialNames = { "coat", "leather", "brass", "glow", "player" };

        /// <summary>Approximately linear RGB per material. Player is only the default; each figure tints its own.</summary>
        public static readonly float[][] Colors =
        {
            new[] { 0.29f, 0.32f, 0.36f },   // coat: slate grey
            new[] { 0.42f, 0.29f, 0.19f },   // leather: mask, belt, gloves, boots
            new[] { 0.72f, 0.55f, 0.28f },   // brass: goggle rims, buckle, buttons
            new[] { 1.00f, 0.86f, 0.55f },   // glow: goggle lenses (also the emission colour)
            new[] { 0.92f, 0.70f, 0.20f },   // player: helmet and scarf
        };
        public static readonly float[] Smoothness = { 0.15f, 0.35f, 0.6f, 0.9f, 0.45f };
        public static readonly float[] Metallic = { 0f, 0f, 0.6f, 0f, 0.1f };

        // ------------------------------------------------------------------ rig (pivots of the moving parts)

        /// <summary>Hip pivot height above the feet, standing and crouched. The legs hang from here.</summary>
        public const float HipY = 0.48f, HipYCrouch = 0.34f, HipX = 0.11f;
        /// <summary>Height above the hips, before the crouch squash is applied.</summary>
        public const float NeckY = 1.00f, ShoulderX = 0.29f, ShoulderY = 0.86f, TailY = 0.93f, TailZ = -0.17f;
        /// <summary>Where the head centre sits on the neck. The helmet and goggle lenses are placed relative to the head.</summary>
        public static readonly V3 HeadOffset = new(0, 0.15f, 0.01f), HelmetOffset = new(0, 0.075f, -0.005f), EyesOffset = new(0, 0.025f, 0.155f);
        /// <summary>The head, with its helmet and goggles, is drawn this much bigger, which gives a chunky, friendly proportion.</summary>
        public const float HeadScale = 1.2f;
        /// <summary>Arm length from the shoulder to the centre of the mitten.</summary>
        public const float ArmLength = 0.50f;

        public enum Piece { Coat, Head, Eyes, Helmet, Arm, Leg, Tail }

        // ------------------------------------------------------------------ data

        public struct V3
        {
            public float X, Y, Z;
            public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static V3 operator *(V3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
            public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        public sealed class Sub
        {
            public int Material;
            public readonly List<int> Tris = new();
        }

        public sealed class Part
        {
            public readonly string Name;
            public readonly List<V3> Pos = new(), Nrm = new();
            public readonly List<Sub> Subs = new();
            public Part(string name) { Name = name; }
            public int Triangles => Pos.Count / 3;
        }

        // ------------------------------------------------------------------ the figure

        public static Part[] BuildAll()
        {
            var parts = new Part[7];
            parts[(int)Piece.Coat] = CoatPart();
            parts[(int)Piece.Head] = HeadPart();
            parts[(int)Piece.Eyes] = EyesPart();
            parts[(int)Piece.Helmet] = HelmetPart();
            parts[(int)Piece.Arm] = ArmPart();
            parts[(int)Piece.Leg] = LegPart();
            parts[(int)Piece.Tail] = TailPart();
            return parts;
        }

        /// <summary>Builds the greatcoat from hem to collar, with a belt and buckle, two buttons and the scarf wrapped round
        /// the neck. The origin is at the hip pivot.</summary>
        static Part CoatPart()
        {
            var b = new Builder("coat");
            b.Lathe(Coat, 8, 0.82f, (-0.22f, 0), (-0.22f, 0.30f), (0.30f, 0.24f), (0.78f, 0.27f), (0.94f, 0.20f), (1.00f, 0.10f), (1.00f, 0));
            b.Lathe(Leather, 8, 0.82f, (0.27f, 0), (0.27f, 0.252f), (0.37f, 0.252f), (0.37f, 0));   // closed shape; the end caps hide inside the coat
            b.Box(Brass, new V3(-0.035f, 0.285f, 0.180f), new V3(0.035f, 0.355f, 0.200f));
            b.Box(Brass, new V3(-0.016f, 0.520f, 0.185f), new V3(0.016f, 0.552f, 0.203f));
            b.Box(Brass, new V3(-0.016f, 0.660f, 0.190f), new V3(0.016f, 0.692f, 0.208f));
            b.Lathe(Player, 8, 0.86f, (0.90f, 0), (0.90f, 0.226f), (0.965f, 0.19f), (1.02f, 0.12f), (1.02f, 0));
            return b.Part;
        }

        /// <summary>Builds the leather gas-mask hood: a round head, two brass goggle rims and a stubby filter snout.
        /// The origin is at the head centre.</summary>
        static Part HeadPart()
        {
            var b = new Builder("head");
            b.Lathe(Leather, 8, 0.95f, (-0.16f, 0), (-0.11f, 0.115f), (-0.03f, 0.158f), (0.06f, 0.15f), (0.13f, 0.09f), (0.16f, 0));
            foreach (float x in new[] { -0.064f, 0.064f })
            {
                // a closed cup made of: a back deep in the hood, an outer wall, a front lip, an inner wall, and a floor 1 cm behind the lens
                b.Xf(90, new V3(x, EyesOffset.Y, 0.12f));   // turns the lathe axis (+y) to face forward
                b.Lathe(Brass, 8, 1f, (-0.04f, 0), (-0.04f, 0.052f), (0.045f, 0.052f), (0.045f, 0.036f), (0.025f, 0.036f), (0.025f, 0));
            }
            b.Xf(115, new V3(0, -0.055f, 0.125f));   // the snout points forward and a little downward
            b.Lathe(Coat, 8, 1f, (-0.04f, 0), (-0.04f, 0.058f), (0.075f, 0.05f), (0.075f, 0));   // the base is sunk into the hood
            return b.Part;
        }

        /// <summary>Builds the two glowing lenses as their own piece, so they can blink and squint. The origin is between them.</summary>
        static Part EyesPart()
        {
            var b = new Builder("eyes");
            foreach (float x in new[] { -0.064f, 0.064f })
            {
                b.Xf(90, new V3(x, 0, 0));
                b.Lathe(Glow, 8, 1f, (0, 0.034f), (0, 0));   // sits inside the rim's opening (0.036), so the two never overlap
            }
            return b.Part;
        }

        /// <summary>Builds a Brodie-style helmet: a low dome on a wide brim, closed underneath. The origin is at the brim.</summary>
        static Part HelmetPart()
        {
            var b = new Builder("helmet");
            b.Lathe(Player, 8, 1f, (-0.012f, 0), (-0.012f, 0.29f), (0.012f, 0.29f), (0.012f, 0.2025f), (0.06f, 0.195f), (0.115f, 0.15f), (0.15f, 0.08f), (0.165f, 0));
            return b.Part;
        }

        /// <summary>Builds the sleeve and mitten hanging down from the shoulder pivot. Both arms share this shape.</summary>
        static Part ArmPart()
        {
            var b = new Builder("arm");
            b.Lathe(Coat, 6, 1f, (-0.44f, 0), (-0.44f, 0.07f), (-0.02f, 0.078f), (0.05f, 0.05f), (0.07f, 0));
            b.Lathe(Leather, 6, 1f, (-ArmLength - 0.065f, 0), (-ArmLength - 0.035f, 0.05f), (-ArmLength + 0.01f, 0.066f), (-ArmLength + 0.05f, 0.05f), (-ArmLength + 0.07f, 0));
            b.Box(Leather, new V3(-0.02f, -ArmLength - 0.025f, 0.035f), new V3(0.02f, -ArmLength + 0.03f, 0.088f));   // thumb
            return b.Part;
        }

        /// <summary>Builds the trouser leg and boot hanging from the hip pivot down to the floor. Both legs share this shape.</summary>
        static Part LegPart()
        {
            var b = new Builder("leg");
            b.Box(Coat, new V3(-0.065f, -0.40f, -0.065f), new V3(0.065f, -0.05f, 0.065f));
            b.Box(Leather, new V3(-0.07f, -HipY, -0.08f), new V3(0.07f, -HipY + 0.10f, 0.16f));
            return b.Part;
        }

        /// <summary>Builds the scarf's loose end, hanging from the back of the neck. It trails when the figure moves.</summary>
        static Part TailPart()
        {
            var b = new Builder("tail");
            b.Strip(Player, Section(0f, 0.05f, 0f, 0.012f), Section(-0.17f, 0.045f, -0.01f, 0.012f), Section(-0.33f, 0.04f, -0.035f, 0.012f));
            return b.Part;
        }

        /// <summary>A flat rectangle across the strip at height y, with half-width w, centred at depth z, and half-thickness t.
        /// The corners are in the order (-x,-z) (+x,-z) (+x,+z) (-x,+z).</summary>
        static V3[] Section(float y, float w, float z, float t) => new[]
        {
            new V3(-w, y, z - t), new V3(w, y, z - t), new V3(w, y, z + t), new V3(-w, y, z + t),
        };

        // ------------------------------------------------------------------ builder

        sealed class Builder
        {
            public readonly Part Part;
            float _cos = 1, _sin = 0;
            V3 _off;

            public Builder(string name) { Part = new Part(name); }

            /// <summary>Sets a transform for the shapes that follow: they are turned about X by <paramref name="degrees"/>, then moved by <paramref name="offset"/>.</summary>
            public void Xf(float degrees, V3 offset)
            {
                float a = degrees * MathF.PI / 180f;
                _cos = MathF.Cos(a); _sin = MathF.Sin(a); _off = offset;
            }

            V3 Rot(V3 p) => new(p.X, p.Y * _cos - p.Z * _sin, p.Y * _sin + p.Z * _cos);

            /// <summary>Adds one triangle facing along <paramref name="outward"/>. Unity winds front faces clockwise, which is
            /// the order whose cross product points at the viewer.</summary>
            void Tri(int mat, V3 a, V3 b, V3 c, V3 outward)
            {
                a = Rot(a) + _off; b = Rot(b) + _off; c = Rot(c) + _off; outward = Rot(outward);
                var n = V3.Cross(b - a, c - a);
                float len = MathF.Sqrt(V3.Dot(n, n));
                if (len < 1e-9f) return;   // degenerate triangle (a pole of a lathe)
                if (V3.Dot(n, outward) < 0) { (b, c) = (c, b); n = n * -1; }
                n = n * (1f / len);
                var sub = Part.Subs.Find(s => s.Material == mat);
                if (sub == null) { sub = new Sub { Material = mat }; Part.Subs.Add(sub); }
                foreach (var v in new[] { a, b, c }) { sub.Tris.Add(Part.Pos.Count); Part.Pos.Add(v); Part.Nrm.Add(n); }
            }

            void Quad(int mat, V3 a, V3 b, V3 c, V3 d, V3 outward) { Tri(mat, a, b, c, outward); Tri(mat, a, c, d, outward); }

            /// <summary>Builds a surface of revolution about +y through the profile rings (y, radius). The profile is walked so
            /// the outside is on the right (bottom to top for a wall), and a radius of 0 closes the shape. Faces are
            /// centred front and back.</summary>
            public void Lathe(int mat, int sides, float zScale, params (float Y, float R)[] rings)
            {
                float step = 2 * MathF.PI / sides;
                V3 P(int j, (float Y, float R) ring)
                {
                    float t = (j + 0.5f) * step;
                    return new V3(ring.R * MathF.Sin(t), ring.Y, ring.R * MathF.Cos(t) * zScale);
                }
                for (int i = 0; i + 1 < rings.Length; i++)
                {
                    var lo = rings[i]; var hi = rings[i + 1];
                    float nr = hi.Y - lo.Y, ny = -(hi.R - lo.R);   // the profile normal, pointing outward
                    for (int j = 0; j < sides; j++)
                    {
                        float tm = (j + 1) * step;
                        var outward = new V3(nr * MathF.Sin(tm), ny, nr * MathF.Cos(tm));
                        Quad(mat, P(j, lo), P(j + 1, lo), P(j + 1, hi), P(j, hi), outward);
                    }
                }
            }

            /// <summary>Builds a six-faced solid from eight corners: the bottom four, then the top four, both in the order
            /// (-x,-z) (+x,-z) (+x,+z) (-x,+z).</summary>
            public void Hex(int mat, V3[] c)
            {
                var centre = new V3(0, 0, 0);
                foreach (var v in c) centre = centre + v * 0.125f;
                void Face(int a, int b, int d, int e) => Quad(mat, c[a], c[b], c[d], c[e], (c[a] + c[b] + c[d] + c[e]) * 0.25f - centre);
                Face(0, 1, 2, 3); Face(4, 5, 6, 7);   // bottom, top
                Face(0, 1, 5, 4); Face(1, 2, 6, 5); Face(2, 3, 7, 6); Face(3, 0, 4, 7);
            }

            /// <summary>Builds a closed tube through rectangular cross-sections (see <see cref="Section"/>), capped at both ends.</summary>
            public void Strip(int mat, params V3[][] sections)
            {
                for (int i = 0; i + 1 < sections.Length; i++)
                {
                    var a = sections[i]; var c = sections[i + 1];
                    var mid = new V3(0, 0, 0);
                    foreach (var v in a) mid = mid + v * 0.125f;
                    foreach (var v in c) mid = mid + v * 0.125f;
                    for (int j = 0; j < 4; j++)
                    {
                        int n = (j + 1) % 4;
                        Quad(mat, a[j], a[n], c[n], c[j], (a[j] + a[n] + c[n] + c[j]) * 0.25f - mid);
                    }
                }
                var first = sections[0]; var last = sections[sections.Length - 1];
                var along = (last[0] + last[2]) * 0.5f - (first[0] + first[2]) * 0.5f;
                Quad(mat, first[0], first[1], first[2], first[3], along * -1);
                Quad(mat, last[0], last[1], last[2], last[3], along);
            }

            public void Box(int mat, V3 min, V3 max) => Hex(mat, new[]
            {
                new V3(min.X, min.Y, min.Z), new V3(max.X, min.Y, min.Z), new V3(max.X, min.Y, max.Z), new V3(min.X, min.Y, max.Z),
                new V3(min.X, max.Y, min.Z), new V3(max.X, max.Y, min.Z), new V3(max.X, max.Y, max.Z), new V3(min.X, max.Y, max.Z),
            });
        }
    }
}
