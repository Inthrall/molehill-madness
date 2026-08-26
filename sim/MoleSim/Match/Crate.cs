using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>What is inside a crate.</summary>
    public enum CrateKind : byte
    {
        /// <summary>One of the limited weapons.</summary>
        Weapon = 0,

        /// <summary>A grub. Restores pluck.</summary>
        Grub = 1,

        /// <summary>A spare reset token, so a fumbled plan is forgivable twice.</summary>
        ResetToken = 2,

        /// <summary>More Boom Beets.</summary>
        Dynamite = 3,
    }

    /// <summary>The contents of one crate, decided when it is telegraphed.</summary>
    public readonly struct CrateContents
    {
        internal CrateContents(CrateKind kind, WeaponId weapon, int amount)
        {
            Kind = kind;
            Weapon = weapon;
            Amount = amount;
        }

        public CrateKind Kind { get; }

        /// <summary>Meaningful when <see cref="Kind"/> is a weapon.</summary>
        public WeaponId Weapon { get; }

        /// <summary>Pluck restored, or how many of the thing there are.</summary>
        public int Amount { get; }

        /// <summary>Splits in two, for the case where two moles arrive at once.</summary>
        public CrateContents Halved() =>
            new CrateContents(Kind, Weapon, Amount > 1 ? Amount / 2 : 1);
    }

    /// <summary>A supply crate, told about a round before it lands.</summary>
    /// <remarks>
    /// The design has crates as the game's engine of contact. Every aftermath announces
    /// exactly where the next ones will come down, so a crate is a fight both sides
    /// scheduled in advance rather than a surprise, and they land in the middle of the map
    /// as near equidistant from everybody as the ground allows. Nothing scolds a player
    /// for turtling; the crates simply keep appearing somewhere they have to come out to
    /// reach.
    /// </remarks>
    public sealed class Crate
    {
        public Crate(Vec2 position, CrateContents contents)
        {
            Position = position;
            Contents = contents;
        }

        public Vec2 Position { get; }

        public CrateContents Contents { get; }

        /// <summary>False until it has actually come down, part way through the round.</summary>
        public bool HasLanded { get; set; }

        /// <summary>Set once somebody has had it, or once it has been torn apart.</summary>
        public bool Gone { get; set; }

        /// <summary>How close a mole must be to claim it.</summary>
        public static Fix64 ReachRadius => MatchSettings.Radius + Fix64.Ratio(1, 2);
    }
}
