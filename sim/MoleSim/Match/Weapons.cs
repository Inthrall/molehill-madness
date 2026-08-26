using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>
    /// The arsenal. Names here are ours, never the player's: the wheel shows the objects.
    /// </summary>
    public enum WeaponId : byte
    {
        /// <summary>Nothing selected, which is what bracing carries.</summary>
        None = 0,

        /// <summary>The grenade, except it is a clod of earth. Honest arc, infinite ammo.</summary>
        ClodLobber = 1,

        /// <summary>An indignant beetle, fired straight, riding the wind and complaining.</summary>
        BeetleLauncher = 2,

        /// <summary>Beetroots wired to a sparkler, planted rather than thrown.</summary>
        BoomBeets = 3,
    }

    /// <summary>What a weapon does. One row per weapon, and the balance lives here.</summary>
    public readonly struct WeaponSpec
    {
        public WeaponSpec(
            Fix64 launchSpeed,
            int damage,
            Fix64 blastRadius,
            int fuseTicks,
            bool detonatesOnContact,
            bool ridesTheWind,
            Fix64 knockback)
        {
            LaunchSpeed = launchSpeed;
            Damage = damage;
            BlastRadius = blastRadius;
            FuseTicks = fuseTicks;
            DetonatesOnContact = detonatesOnContact;
            RidesTheWind = ridesTheWind;
            Knockback = knockback;
        }

        /// <summary>Speed at full power. Power scales it.</summary>
        public Fix64 LaunchSpeed { get; }

        /// <summary>Damage at the centre of the blast, falling off to nothing at the edge.</summary>
        public int Damage { get; }

        /// <summary>Radius of the crater, and of the damage.</summary>
        public Fix64 BlastRadius { get; }

        /// <summary>Ticks before it goes off on its own. Zero means it never does.</summary>
        public int FuseTicks { get; }

        /// <summary>Whether hitting something sets it off.</summary>
        public bool DetonatesOnContact { get; }

        /// <summary>
        /// Whether wind pushes it. Only the Beetle Launcher does, which is what keeps wind
        /// a flavour of one weapon rather than a tax on the whole arsenal.
        /// </summary>
        public bool RidesTheWind { get; }

        /// <summary>Impulse imparted at the centre of the blast.</summary>
        public Fix64 Knockback { get; }
    }

    /// <summary>The arsenal table. Prototype numbers, as the design document says.</summary>
    public static class WeaponTable
    {
        private static readonly WeaponSpec[] Specs = BuildSpecs();

        public static WeaponSpec Of(WeaponId weapon) => Specs[(int)weapon];

        /// <summary>Whether this weapon produces a projectile when fired.</summary>
        public static bool IsThrown(WeaponId weapon) =>
            weapon is WeaponId.ClodLobber or WeaponId.BeetleLauncher;

        private static WeaponSpec[] BuildSpecs()
        {
            WeaponSpec[] specs = new WeaponSpec[4];

            specs[(int)WeaponId.None] = default;

            // A three-second fuse, so it can be bounced into a hole or cooked over a head.
            specs[(int)WeaponId.ClodLobber] = new WeaponSpec(
                launchSpeed: Fix64.FromInt(26),
                damage: 30,
                blastRadius: Fix64.Ratio(5, 2),
                fuseTicks: MatchSettings.TicksPerSecond * 3,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(15));

            // Faster, harder, and goes off the moment it arrives.
            specs[(int)WeaponId.BeetleLauncher] = new WeaponSpec(
                launchSpeed: Fix64.FromInt(34),
                damage: 45,
                blastRadius: Fix64.FromInt(2),
                fuseTicks: 0,
                detonatesOnContact: true,
                ridesTheWind: true,
                knockback: Fix64.FromInt(18));

            // Planted at the mole's feet, so it has no launch speed at all. Plant, run,
            // regret.
            specs[(int)WeaponId.BoomBeets] = new WeaponSpec(
                launchSpeed: Fix64.Zero,
                damage: 50,
                blastRadius: Fix64.FromInt(3),
                fuseTicks: MatchSettings.TicksPerSecond * 3,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(24));

            return specs;
        }
    }
}
