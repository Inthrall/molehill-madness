using System.Collections.Generic;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>What one mole took from one blast.</summary>
    public readonly struct BlastHit
    {
        public BlastHit(int seat, int moleIndex, int damage, bool wentOffDuty)
        {
            Seat = seat;
            MoleIndex = moleIndex;
            Damage = damage;
            WentOffDuty = wentOffDuty;
        }

        public int Seat { get; }

        public int MoleIndex { get; }

        public int Damage { get; }

        public bool WentOffDuty { get; }
    }

    /// <summary>Craters the ground and knocks moles about.</summary>
    /// <remarks>
    /// Friendly fire is always on and there is no toggle, so nothing here asks who fired.
    /// Every mole in reach takes the same treatment, including the one that pulled the
    /// trigger and the rest of its own platoon standing admiringly close.
    /// </remarks>
    public static class Blast
    {
        /// <summary>
        /// Applies a blast at a point: removes terrain, damages and shoves everything in
        /// reach, and reports what it did.
        /// </summary>
        public static List<BlastHit> Detonate(
            TerrainGrid terrain, Mole[] moles, Vec2 centre, WeaponSpec spec)
        {
            int cellRadius = Fix64.FloorToInt(spec.BlastRadius / WorldScale.CellSize);
            terrain.CarveCircle(
                WorldScale.ToCell(centre.X), WorldScale.ToCell(centre.Y), cellRadius);

            List<BlastHit> hits = new List<BlastHit>();

            // Moles are visited in seat then index order, which is fixed, and each is
            // treated independently, so the outcome cannot depend on the order anyway.
            foreach (Mole mole in moles)
            {
                if (mole.IsOffDuty)
                {
                    continue;
                }

                Vec2 offset = mole.Position - centre;
                Fix64 distance = offset.Length();

                if (distance >= spec.BlastRadius)
                {
                    continue;
                }

                // Linear falloff: everything at the centre, nothing at the rim.
                Fix64 closeness = Fix64.One - (distance / spec.BlastRadius);
                int damage = Fix64.ToInt(Fix64.FromInt(spec.Damage) * closeness);

                if (damage < 1)
                {
                    damage = 1;
                }

                Vec2 push = distance == Fix64.Zero
                    ? -Vec2.UnitY
                    : offset / distance;

                // A little upward bias, so a blast underfoot launches rather than merely
                // shoving sideways.
                push = (push + (-Vec2.UnitY * Fix64.Ratio(1, 3))).Normalised();

                bool wentOffDuty = mole.TakeDamage(damage);
                mole.AddImpulse(push * (spec.Knockback * closeness));

                hits.Add(new BlastHit(mole.Seat, mole.Index, damage, wentOffDuty));
            }

            return hits;
        }
    }
}
