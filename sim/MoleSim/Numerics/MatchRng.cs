using System;

namespace MoleSim.Numerics
{
    /// <summary>
    /// The one source of randomness in a match: xoshiro256** with a fixed, published
    /// algorithm and no platform dependencies.
    /// </summary>
    /// <remarks>
    /// Every draw in a match comes from here, in a defined order, which is what makes a
    /// recorded match replay bit-exact down to which knockout animation plays. The
    /// platform generators are unusable for this: their algorithms are unspecified and
    /// free to change between runtime versions.
    ///
    /// Nothing outside the simulation may draw from a match's generator. Presentation
    /// randomness (dust, idle blinks, camera jitter) belongs to the client and must never
    /// touch this.
    /// </remarks>
    public sealed class MatchRng
    {
        private ulong _state0;
        private ulong _state1;
        private ulong _state2;
        private ulong _state3;

        /// <summary>Creates a generator for a match seed.</summary>
        public MatchRng(ulong seed)
        {
            // SplitMix64 spreads a single seed into four words of state. Seeding the state
            // directly from a small number would leave xoshiro correlated for its first
            // few draws.
            ulong mixer = seed;
            _state0 = SplitMix64(ref mixer);
            _state1 = SplitMix64(ref mixer);
            _state2 = SplitMix64(ref mixer);
            _state3 = SplitMix64(ref mixer);
        }

        private MatchRng(ulong state0, ulong state1, ulong state2, ulong state3)
        {
            _state0 = state0;
            _state1 = state1;
            _state2 = state2;
            _state3 = state3;
        }

        /// <summary>
        /// Creates a generator from explicit state words, for conformance tests against the
        /// published algorithm and for restoring a snapshot.
        /// </summary>
        public static MatchRng FromState(ulong state0, ulong state1, ulong state2, ulong state3)
        {
            if ((state0 | state1 | state2 | state3) == 0)
            {
                throw new ArgumentException("xoshiro256** state must not be all zero.", nameof(state0));
            }

            return new MatchRng(state0, state1, state2, state3);
        }

        /// <summary>The four state words, for writing into a replay or a save.</summary>
        public void Snapshot(out ulong state0, out ulong state1, out ulong state2, out ulong state3)
        {
            state0 = _state0;
            state1 = _state1;
            state2 = _state2;
            state3 = _state3;
        }

        /// <summary>Next raw draw. Every other method here is built on this one.</summary>
        public ulong NextUInt64()
        {
            ulong result = RotateLeft(_state1 * 5UL, 7) * 9UL;
            ulong shifted = _state1 << 17;

            _state2 ^= _state0;
            _state3 ^= _state1;
            _state1 ^= _state2;
            _state0 ^= _state3;
            _state2 ^= shifted;
            _state3 = RotateLeft(_state3, 45);

            return result;
        }

        /// <summary>
        /// Whole number in <c>[0, exclusiveUpperBound)</c>, with the bias that plain
        /// remainder arithmetic would introduce rejected rather than tolerated.
        /// </summary>
        public int NextInt(int exclusiveUpperBound)
        {
            if (exclusiveUpperBound <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(exclusiveUpperBound), "Upper bound must be positive.");
            }

            ulong range = (ulong)exclusiveUpperBound;

            // Draws below this threshold would land in the short final block of the 64-bit
            // space and over-represent the low results, so they are redrawn. The loop
            // terminates in one iteration overwhelmingly often, and its iteration count is
            // itself deterministic, so replays are unaffected.
            ulong threshold = (0UL - range) % range;

            ulong value;
            do
            {
                value = NextUInt64();
            }
            while (value < threshold);

            return (int)(value % range);
        }

        /// <summary>Whole number in <c>[inclusiveLower, exclusiveUpper)</c>.</summary>
        public int NextInt(int inclusiveLower, int exclusiveUpper)
        {
            if (exclusiveUpper <= inclusiveLower)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(exclusiveUpper), "Upper bound must exceed lower bound.");
            }

            return inclusiveLower + NextInt(exclusiveUpper - inclusiveLower);
        }

        public bool NextBool() => (NextUInt64() >> 63) != 0;

        /// <summary>Value in <c>[0, 1)</c>, taken from the strongest bits of a draw.</summary>
        public Fix64 NextFraction() =>
            Fix64.FromRaw((long)(NextUInt64() >> (64 - Fix64.FractionalBits)));

        /// <summary>Value in <c>[inclusiveLower, exclusiveUpper)</c>.</summary>
        public Fix64 NextFix64(Fix64 inclusiveLower, Fix64 exclusiveUpper) =>
            inclusiveLower + ((exclusiveUpper - inclusiveLower) * NextFraction());

        /// <summary>Picks an index into a collection of the given length.</summary>
        public int NextIndex(int count) => NextInt(count);

        private static ulong RotateLeft(ulong value, int bits) =>
            (value << bits) | (value >> (64 - bits));

        private static ulong SplitMix64(ref ulong state)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
