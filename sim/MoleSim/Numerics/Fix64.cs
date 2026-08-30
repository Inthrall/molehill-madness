using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MoleSim.Numerics
{
    /// <summary>
    /// Q48.16 fixed-point number: a 64-bit value with 16 fractional bits.
    /// Every quantity in the simulation is one of these.
    /// </summary>
    /// <remarks>
    /// IEEE 754 arithmetic is not used anywhere in the simulation because its results
    /// can differ between processors and compilers, which would silently fork a live
    /// match between a phone and a PC. Every operation here is defined purely in terms
    /// of integer arithmetic, so it produces the same bits on every platform.
    ///
    /// The wider intermediates are computed by hand rather than by a platform intrinsic
    /// for the same reason: no hardware-dependent path, no surprises.
    ///
    /// One raw unit is 1/65536, which at the game's 5 cm terrain cell is about 0.015 mm
    /// of precision. Overflow saturates rather than wrapping, because a match that goes
    /// visibly silly is easier to debug than one that quietly inverts a velocity.
    /// </remarks>
    public readonly struct Fix64 : IEquatable<Fix64>, IComparable<Fix64>
    {
        /// <summary>Number of fractional bits.</summary>
        public const int FractionalBits = 16;

        internal const long RawOne = 1L << FractionalBits;
        private const long RawHalf = RawOne >> 1;

        private readonly long _raw;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Fix64(long raw) => _raw = raw;

        /// <summary>The underlying integer. Only serialization, hashing and tests should need this.</summary>
        public long Raw
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _raw;
        }

        public static Fix64 Zero => new Fix64(0);
        public static Fix64 One => new Fix64(RawOne);
        public static Fix64 Half => new Fix64(RawHalf);
        public static Fix64 MinusOne => new Fix64(-RawOne);

        /// <summary>Largest representable value, and the result of overflowing upward.</summary>
        public static Fix64 MaxValue => new Fix64(long.MaxValue);

        /// <summary>
        /// Smallest representable value, and the result of overflowing downward.
        /// This is <c>-long.MaxValue</c> rather than <c>long.MinValue</c> so that negation
        /// is total: every value in the range has a negation inside the range.
        /// </summary>
        public static Fix64 MinValue => new Fix64(-long.MaxValue);

        /// <summary>Smallest positive step: 1/65536.</summary>
        public static Fix64 Epsilon => new Fix64(1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 FromRaw(long raw) => new Fix64(raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 FromInt(int value) => new Fix64((long)value << FractionalBits);

        /// <summary>
        /// Exact ratio <paramref name="numerator"/>/<paramref name="denominator"/>, for
        /// writing readable constants such as <c>Fix64.Ratio(3, 10)</c>.
        /// </summary>
        public static Fix64 Ratio(int numerator, int denominator)
        {
            if (denominator == 0)
            {
                throw new DivideByZeroException("Fix64.Ratio denominator must not be zero.");
            }

            return new Fix64(((long)numerator << FractionalBits) / denominator);
        }

        // ---- Addition and subtraction -------------------------------------------------
        // Saturating, so a runaway impulse pins at the extreme instead of wrapping
        // around into its opposite.

        public static Fix64 operator +(Fix64 a, Fix64 b)
        {
            long sum = unchecked(a._raw + b._raw);

            // Overflow happened if both operands share a sign that the result does not.
            if (((a._raw ^ sum) & (b._raw ^ sum)) < 0)
            {
                return a._raw > 0 ? MaxValue : MinValue;
            }

            return new Fix64(sum);
        }

        public static Fix64 operator -(Fix64 a, Fix64 b)
        {
            long difference = unchecked(a._raw - b._raw);

            if (((a._raw ^ b._raw) & (a._raw ^ difference)) < 0)
            {
                // Which end to saturate at is decided by the direction the subtraction was going,
                // not by the sign of the left operand. Zero minus a negative overflows upward and
                // used to come back as MinValue, because a was not greater than zero: the answer had
                // the wrong sign as well as the wrong magnitude. The overflow only happens when the
                // operands differ in sign, so b's sign is what says which way it went.
                return b._raw < 0 ? MaxValue : MinValue;
            }

            return new Fix64(difference);
        }

        public static Fix64 operator -(Fix64 value) => new Fix64(-value._raw);

        // ---- Multiplication -----------------------------------------------------------

        /// <summary>
        /// Multiplies via a 128-bit intermediate, so the product is correct for the whole
        /// representable range rather than only for conveniently small game values.
        /// </summary>
        public static Fix64 operator *(Fix64 a, Fix64 b)
        {
            long left = a._raw;
            long right = b._raw;

            if (left == 0 || right == 0)
            {
                return Zero;
            }

            bool negative = (left < 0) ^ (right < 0);
            Multiply64(Magnitude(left), Magnitude(right), out ulong high, out ulong low);

            // Shift the 128-bit product right by the fractional bits.
            ulong resultLow = (low >> FractionalBits) | (high << (64 - FractionalBits));
            ulong resultHigh = high >> FractionalBits;

            if (resultHigh != 0 || resultLow > long.MaxValue)
            {
                return negative ? MinValue : MaxValue;
            }

            long result = (long)resultLow;
            return new Fix64(negative ? -result : result);
        }

        // ---- Division -----------------------------------------------------------------

        /// <summary>
        /// Divides via a 128-bit numerator and restoring long division, truncating toward
        /// zero. Deterministic on every platform because it is only shifts and subtracts.
        /// </summary>
        public static Fix64 operator /(Fix64 a, Fix64 b)
        {
            if (b._raw == 0)
            {
                throw new DivideByZeroException("Fix64 division by zero.");
            }

            if (a._raw == 0)
            {
                return Zero;
            }

            bool negative = (a._raw < 0) ^ (b._raw < 0);
            ulong dividend = Magnitude(a._raw);
            ulong divisor = Magnitude(b._raw);

            // Numerator is the dividend scaled up by the fractional bits, which needs
            // 128 bits of room.
            ulong high = dividend >> (64 - FractionalBits);
            ulong low = dividend << FractionalBits;

            ulong quotient = Divide128By64(high, low, divisor);

            if (quotient > long.MaxValue)
            {
                return negative ? MinValue : MaxValue;
            }

            long result = (long)quotient;
            return new Fix64(negative ? -result : result);
        }

        /// <summary>Multiplies by a whole number without the 128-bit path.</summary>
        public static Fix64 operator *(Fix64 a, int b) => a * FromInt(b);

        public static Fix64 operator *(int a, Fix64 b) => FromInt(a) * b;

        // ---- Comparison ---------------------------------------------------------------

        public static bool operator ==(Fix64 a, Fix64 b) => a._raw == b._raw;

        public static bool operator !=(Fix64 a, Fix64 b) => a._raw != b._raw;

        public static bool operator <(Fix64 a, Fix64 b) => a._raw < b._raw;

        public static bool operator >(Fix64 a, Fix64 b) => a._raw > b._raw;

        public static bool operator <=(Fix64 a, Fix64 b) => a._raw <= b._raw;

        public static bool operator >=(Fix64 a, Fix64 b) => a._raw >= b._raw;

        // ---- Rounding and helpers -----------------------------------------------------

        public static Fix64 Abs(Fix64 value) =>
            value._raw < 0 ? new Fix64(-value._raw) : value;

        public static Fix64 Min(Fix64 a, Fix64 b) => a._raw < b._raw ? a : b;

        public static Fix64 Max(Fix64 a, Fix64 b) => a._raw > b._raw ? a : b;

        public static Fix64 Clamp(Fix64 value, Fix64 low, Fix64 high) =>
            value._raw < low._raw ? low : value._raw > high._raw ? high : value;

        public static int Sign(Fix64 value) => value._raw < 0 ? -1 : value._raw > 0 ? 1 : 0;

        /// <summary>Largest whole number not greater than the value.</summary>
        public static Fix64 Floor(Fix64 value) =>
            new Fix64(value._raw & ~(RawOne - 1));

        /// <summary>Smallest whole number not less than the value.</summary>
        public static Fix64 Ceiling(Fix64 value)
        {
            long fraction = value._raw & (RawOne - 1);
            return fraction == 0 ? value : Floor(value) + One;
        }

        /// <summary>Nearest whole number, halves away from zero.</summary>
        public static Fix64 Round(Fix64 value) =>
            value._raw >= 0
                ? Floor(value + Half)
                : -Floor(-value + Half);

        /// <summary>Truncates toward zero and returns the whole part.</summary>
        public static int ToInt(Fix64 value) =>
            (int)(value._raw / RawOne);

        /// <summary>Floor of the value as a whole number, which is what grid lookups want.</summary>
        public static int FloorToInt(Fix64 value) =>
            (int)(value._raw >> FractionalBits);

        // ---- Square root --------------------------------------------------------------

        /// <summary>
        /// Square root, truncated to the nearest representable value below the true root.
        /// </summary>
        /// <remarks>
        /// Computed as the integer square root of the raw value scaled up by the fractional
        /// bits. Values above 2^47 (about 2.1 billion in game units, which no real quantity
        /// approaches) saturate rather than overflowing the intermediate.
        /// </remarks>
        public static Fix64 Sqrt(Fix64 value)
        {
            if (value._raw < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Fix64.Sqrt of a negative value.");
            }

            if (value._raw == 0)
            {
                return Zero;
            }

            if (value._raw >= SqrtSafeLimit)
            {
                return MaxValue;
            }

            return new Fix64((long)IntegerSqrt((ulong)value._raw << FractionalBits));
        }

        /// <summary>Largest raw value whose scaled-up square root still fits in 64 bits.</summary>
        private const long SqrtSafeLimit = 1L << (63 - FractionalBits);

        /// <summary>Length of a two-component vector, without an intermediate overflow.</summary>
        /// <summary>Below this raw value, squaring loses everything, so the maths is scaled up.</summary>
        private const long SmallComponent = 1L << 8;

        /// <summary>How far up. A power of two, so scaling back down is exact.</summary>
        private const int SmallShift = 8;

        public static Fix64 Hypot(Fix64 x, Fix64 y)
        {
            Fix64 absX = Abs(x);
            Fix64 absY = Abs(y);

            if (absX._raw == 0)
            {
                return absY;
            }

            if (absY._raw == 0)
            {
                return absX;
            }

            // Scaled up first when both components are tiny. Squaring a raw value below 256
            // truncates to nothing in Q16, so two points five millimetres apart measured as no
            // distance at all and a small velocity normalised to the zero vector, losing its
            // direction rather than reporting a unit one. Shifting both up by eight bits before
            // squaring and the answer back down afterwards is exact, since it is a power of two,
            // and it moves the floor down by a factor of two hundred and fifty-six.
            if (absX._raw < SmallComponent && absY._raw < SmallComponent)
            {
                Fix64 scaledX = new Fix64(absX._raw << SmallShift);
                Fix64 scaledY = new Fix64(absY._raw << SmallShift);

                return new Fix64(Hypot(scaledX, scaledY)._raw >> SmallShift);
            }

            // Direct route, exact to the last raw unit, and valid for components up to
            // about 46,000 game units. Every real distance, speed and impulse in the game
            // is orders of magnitude inside that.
            Fix64 squaredX = absX * absX;
            Fix64 squaredY = absY * absY;

            if (squaredX != MaxValue && squaredY != MaxValue)
            {
                Fix64 sumOfSquares = squaredX + squaredY;

                if (sumOfSquares != MaxValue && sumOfSquares._raw < SqrtSafeLimit)
                {
                    return Sqrt(sumOfSquares);
                }
            }

            // Absurd magnitudes only: scale by the larger component so the squares stay in
            // range. Cheap insurance, at the cost of amplifying the root's truncation by
            // the scale factor, which is why it is not the everyday path.
            Fix64 larger = Max(absX, absY);
            Fix64 smaller = Min(absX, absY);
            Fix64 ratio = smaller / larger;

            return larger * Sqrt(One + (ratio * ratio));
        }

        // ---- Conversion for debugging and authoring ------------------------------------

        /// <summary>
        /// Approximate decimal value. For logging, tests and content tools only: nothing in
        /// the simulation may branch on this, or determinism is gone.
        /// </summary>
        public decimal ToDecimal() => (decimal)_raw / RawOne;

        public override string ToString() =>
            ToDecimal().ToString("0.####", CultureInfo.InvariantCulture);

        // ---- Equality -----------------------------------------------------------------

        public bool Equals(Fix64 other) => _raw == other._raw;

        public override bool Equals(object? obj) => obj is Fix64 other && Equals(other);

        public override int GetHashCode() => _raw.GetHashCode();

        public int CompareTo(Fix64 other) => _raw.CompareTo(other._raw);

        // ---- Wide integer helpers -----------------------------------------------------

        /// <summary>Absolute value as an unsigned magnitude, safe at the extremes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Magnitude(long value) =>
            value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value;

        /// <summary>Unsigned 64x64 to 128-bit product, built from 32-bit partial products.</summary>
        private static void Multiply64(ulong a, ulong b, out ulong high, out ulong low)
        {
            ulong aLow = a & 0xFFFFFFFFUL;
            ulong aHigh = a >> 32;
            ulong bLow = b & 0xFFFFFFFFUL;
            ulong bHigh = b >> 32;

            ulong lowLow = aLow * bLow;
            ulong lowHigh = aLow * bHigh;
            ulong highLow = aHigh * bLow;
            ulong highHigh = aHigh * bHigh;

            ulong middle = (lowLow >> 32) + (lowHigh & 0xFFFFFFFFUL) + (highLow & 0xFFFFFFFFUL);

            low = (lowLow & 0xFFFFFFFFUL) | (middle << 32);
            high = highHigh + (lowHigh >> 32) + (highLow >> 32) + (middle >> 32);
        }

        /// <summary>
        /// Floor of a 128-bit value divided by a 64-bit one, saturating when the quotient
        /// will not fit. Restoring long division: one shift and one conditional subtract
        /// per bit, identical on every platform.
        /// </summary>
        private static ulong Divide128By64(ulong high, ulong low, ulong divisor)
        {
            if (high >= divisor)
            {
                return ulong.MaxValue;
            }

            ulong quotient = 0;
            ulong remainder = high;

            for (int bit = 63; bit >= 0; bit--)
            {
                // Track the bit shifted off the top: it means the running remainder has
                // exceeded 64 bits and therefore certainly exceeds the divisor.
                ulong carry = remainder >> 63;
                remainder = (remainder << 1) | ((low >> bit) & 1UL);
                quotient <<= 1;

                if (carry != 0 || remainder >= divisor)
                {
                    remainder -= divisor;
                    quotient |= 1UL;
                }
            }

            return quotient;
        }

        /// <summary>Floor of the square root of a 64-bit value, one bit pair at a time.</summary>
        private static ulong IntegerSqrt(ulong value)
        {
            ulong result = 0;
            ulong bit = 1UL << 62;

            while (bit > value)
            {
                bit >>= 2;
            }

            while (bit != 0)
            {
                if (value >= result + bit)
                {
                    value -= result + bit;
                    result = (result >> 1) + bit;
                }
                else
                {
                    result >>= 1;
                }

                bit >>= 2;
            }

            return result;
        }
    }
}
