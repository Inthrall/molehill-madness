using System;

namespace MoleSim.Numerics
{
    /// <summary>
    /// A two-component fixed-point vector: every position, velocity and impulse in the
    /// game. Metres and metres per second, never cells.
    /// </summary>
    /// <remarks>
    /// Positions are kept in world metres rather than terrain cells so that changing the
    /// cell size stays a rendering and lookup concern instead of rewriting physics. The
    /// conversion lives in one place, <see cref="ToCell"/>.
    /// </remarks>
    public readonly struct Vec2 : IEquatable<Vec2>
    {
        public Vec2(Fix64 x, Fix64 y)
        {
            X = x;
            Y = y;
        }

        public Fix64 X { get; }

        public Fix64 Y { get; }

        public static Vec2 Zero => new Vec2(Fix64.Zero, Fix64.Zero);

        public static Vec2 UnitX => new Vec2(Fix64.One, Fix64.Zero);

        /// <summary>Straight down, which is where gravity points: Y grows downward.</summary>
        public static Vec2 UnitY => new Vec2(Fix64.Zero, Fix64.One);

        public static Vec2 FromInt(int x, int y) => new Vec2(Fix64.FromInt(x), Fix64.FromInt(y));

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);

        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);

        public static Vec2 operator -(Vec2 value) => new Vec2(-value.X, -value.Y);

        public static Vec2 operator *(Vec2 value, Fix64 scale) =>
            new Vec2(value.X * scale, value.Y * scale);

        public static Vec2 operator *(Fix64 scale, Vec2 value) => value * scale;

        public static Vec2 operator /(Vec2 value, Fix64 divisor) =>
            new Vec2(value.X / divisor, value.Y / divisor);

        public static bool operator ==(Vec2 a, Vec2 b) => a.X == b.X && a.Y == b.Y;

        public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

        /// <summary>Length, computed without an intermediate overflow.</summary>
        public Fix64 Length() => Fix64.Hypot(X, Y);

        /// <summary>
        /// Squared length. Cheaper than <see cref="Length"/> and exact, so range checks
        /// and "which is nearer" comparisons should prefer it.
        /// </summary>
        public Fix64 LengthSquared() => (X * X) + (Y * Y);

        public static Fix64 Dot(Vec2 a, Vec2 b) => (a.X * b.X) + (a.Y * b.Y);

        /// <summary>
        /// Perpendicular dot product. Its sign says which side of <paramref name="a"/>
        /// the vector <paramref name="b"/> falls on, which is how collision decides a
        /// shove direction without any trigonometry.
        /// </summary>
        public static Fix64 Cross(Vec2 a, Vec2 b) => (a.X * b.Y) - (a.Y * b.X);

        public static Fix64 Distance(Vec2 a, Vec2 b) => (a - b).Length();

        public static Fix64 DistanceSquared(Vec2 a, Vec2 b) => (a - b).LengthSquared();

        /// <summary>Rotated a quarter turn, used for surface normals and shove directions.</summary>
        public Vec2 PerpendicularLeft() => new Vec2(Y, -X);

        public Vec2 PerpendicularRight() => new Vec2(-Y, X);

        /// <summary>
        /// Unit vector in the same direction, or <see cref="Zero"/> for a zero vector.
        /// </summary>
        /// <remarks>
        /// Returning zero rather than throwing is deliberate: a mole standing perfectly
        /// still has no direction, and every caller would otherwise need the same guard.
        /// It stays deterministic either way, which is the only thing that must not bend.
        /// </remarks>
        public Vec2 Normalised()
        {
            Fix64 length = Length();

            return length == Fix64.Zero ? Zero : new Vec2(X / length, Y / length);
        }

        /// <summary>Clamps the length, leaving the direction alone. Terminal velocity.</summary>
        public Vec2 WithMaxLength(Fix64 maximum)
        {
            Fix64 lengthSquared = LengthSquared();

            if (lengthSquared <= maximum * maximum)
            {
                return this;
            }

            return Normalised() * maximum;
        }

        public static Vec2 Lerp(Vec2 from, Vec2 to, Fix64 amount) =>
            from + ((to - from) * amount);

        /// <summary>
        /// Rotates this vector by the angle of <paramref name="facing"/>, which must be a
        /// unit vector. A facing of <see cref="UnitX"/> leaves the vector untouched.
        /// </summary>
        /// <remarks>
        /// This is complex multiplication, which is exactly a rotation and needs no
        /// trigonometry at all. Worth knowing, because it means the simulation can turn
        /// things through arbitrary angles without a sine table that every platform would
        /// have to agree on.
        /// </remarks>
        public Vec2 RotatedBy(Vec2 facing) =>
            new Vec2(
                (X * facing.X) - (Y * facing.Y),
                (X * facing.Y) + (Y * facing.X));

        /// <summary>
        /// Cell coordinate containing this position, given the cell size in metres.
        /// Floors rather than truncates, so cells to the left of the origin do not all
        /// collapse onto zero.
        /// </summary>
        public void ToCell(Fix64 cellSize, out int cellX, out int cellY)
        {
            cellX = Fix64.FloorToInt(X / cellSize);
            cellY = Fix64.FloorToInt(Y / cellSize);
        }

        public bool Equals(Vec2 other) => this == other;

        public override bool Equals(object? obj) => obj is Vec2 other && Equals(other);

        public override int GetHashCode() =>
            (X.Raw.GetHashCode() * 397) ^ Y.Raw.GetHashCode();

        public override string ToString() => $"({X}, {Y})";
    }
}
