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

        /// <summary>Cluster lob. Blankets an area, so standing still is punished.</summary>
        AcornMortar = 3,

        /// <summary>A derrick and a drill. Reaches through soil, which nothing else does.</summary>
        Fracking = 4,

        /// <summary>A fairground mallet. Hardest read in the game, biggest payoff.</summary>
        BigWhack = 5,

        /// <summary>An oversized mousetrap. Placed now, armed next round.</summary>
        SnapTrap = 6,

        /// <summary>A zone that halves movement and stops digging for a round.</summary>
        RootSnare = 7,

        /// <summary>Drill-dash through dirt, and bowl over whatever is where you surface.</summary>
        TunnelTorpedo = 8,

        /// <summary>This turn, dirt costs what open ground costs.</summary>
        PowerClaws = 9,

        /// <summary>A blob of fresh terrain. Bridges, plugs, walls.</summary>
        Sandbag = 10,

        /// <summary>Caps a pocket into a vent that throws things upward.</summary>
        GeyserCap = 11,

        /// <summary>Beetroots wired to a sparkler, planted rather than thrown.</summary>
        BoomBeets = 12,

        /// <summary>A hawk in a postal cap, dropping sacks on a marked strip.</summary>
        SpecialDelivery = 13,

        /// <summary>An ornate golden relic. Three seconds, then a large round hole.</summary>
        MolyHandGrenade = 14,

        /// <summary>Three tonnes of concrete garden gnome, dropped from a great height.</summary>
        GnomeMercy = 15,
    }

    /// <summary>How a weapon behaves, which decides which code path handles it.</summary>
    public enum WeaponKind : byte
    {
        /// <summary>Nothing happens.</summary>
        Nothing = 0,

        /// <summary>Launched from the mole along the aim.</summary>
        Thrown = 1,

        /// <summary>Dropped from above onto a point the player picked.</summary>
        FromTheSky = 2,

        /// <summary>Placed at the mole's feet.</summary>
        Planted = 3,

        /// <summary>A swing, resolved immediately, with no projectile at all.</summary>
        Melee = 4,

        /// <summary>Shakes the ground where the mole stands.</summary>
        Seismic = 5,

        /// <summary>Changes the world or the mole rather than hurting anybody.</summary>
        Tool = 6,
    }

    /// <summary>What a weapon does. One row per weapon, and the balance lives here.</summary>
    public readonly struct WeaponSpec
    {
        public WeaponSpec(
            WeaponKind kind,
            Fix64 launchSpeed,
            int damage,
            Fix64 blastRadius,
            int fuseTicks,
            bool detonatesOnContact,
            bool ridesTheWind,
            Fix64 knockback,
            bool reachesBuried = false,
            int clusterCount = 0,
            int bounceBlasts = 0,
            Fix64 craterRadius = default)
        {
            Kind = kind;
            LaunchSpeed = launchSpeed;
            Damage = damage;
            BlastRadius = blastRadius;
            CraterRadius = craterRadius == Fix64.Zero ? blastRadius : craterRadius;
            FuseTicks = fuseTicks;
            DetonatesOnContact = detonatesOnContact;
            RidesTheWind = ridesTheWind;
            Knockback = knockback;
            ReachesBuried = reachesBuried;
            ClusterCount = clusterCount;
            BounceBlasts = bounceBlasts;
        }

        public WeaponKind Kind { get; }

        /// <summary>Speed at full power. Power scales it.</summary>
        public Fix64 LaunchSpeed { get; }

        /// <summary>Damage at the centre of the blast, falling off to nothing at the edge.</summary>
        public int Damage { get; }

        /// <summary>How far the damage and the shove reach.</summary>
        public Fix64 BlastRadius { get; }

        /// <summary>
        /// How big a hole it leaves, which is not the same as how far it hurts.
        /// </summary>
        /// <remarks>
        /// These started out as one number and the map paid for it: with craters as wide as
        /// the damage, five rounds of ordinary shelling left the field unrecognisable, which
        /// contradicts pacing the design has already fixed. Lava arrives at round eight and
        /// then climbs for several more, so a match is meant to still have ground under it
        /// well past round ten.
        ///
        /// Separating them costs one field and buys the obvious truth that a concussive
        /// blast hurts further than it digs. Damage and knockback are untouched, so how
        /// lethal a weapon is has not changed at all; only how much of the map it eats.
        /// </remarks>
        public Fix64 CraterRadius { get; }

        /// <summary>Ticks before it goes off on its own. Zero means it never does.</summary>
        public int FuseTicks { get; }

        /// <summary>Whether hitting something sets it off.</summary>
        public bool DetonatesOnContact { get; }

        /// <summary>
        /// Whether wind pushes it. Only the Beetle Launcher does, which keeps wind a
        /// flavour of one weapon rather than a tax on the whole arsenal.
        /// </summary>
        public bool RidesTheWind { get; }

        /// <summary>Impulse imparted at the centre of the blast.</summary>
        public Fix64 Knockback { get; }

        /// <summary>
        /// Whether the blast reaches a mole with dirt in the way. False for everything
        /// ballistic, which is what makes being underground worth the stamina.
        /// </summary>
        public bool ReachesBuried { get; }

        /// <summary>How many smaller charges this splits into when it goes off.</summary>
        public int ClusterCount { get; }

        /// <summary>How many times this goes off while bouncing before it stops.</summary>
        public int BounceBlasts { get; }
    }

    /// <summary>The arsenal table. Prototype numbers, as the design document says.</summary>
    public static class WeaponTable
    {
        /// <summary>Sub-charge produced when the Acorn Mortar splits.</summary>
        public const WeaponId AcornShard = WeaponId.ClodLobber;

        /// <summary>
        /// What a platoon starts a match holding, with -1 meaning unlimited.
        /// </summary>
        /// <remarks>
        /// Without this the wheel offered all fifteen every turn, including the two the design
        /// calls the crate rarities, and half of what a crate can contain was thrown away for
        /// want of anywhere to put it. Free access to the Moly Hand Grenade would be the only
        /// thing anybody ever chose, and the crate scramble the design leans on to force
        /// contact would be worth nothing.
        ///
        /// Exactly one weapon is unlimited, so a platoon can never be left unable to act. It is
        /// the Clod Lobber, the weakest thing in the arsenal, which makes everything else a
        /// resource and every crate worth crossing the map for.
        ///
        /// Prototype numbers, as the design says of the arsenal table.
        /// </remarks>
        public static int StartingStock(WeaponId weapon) => weapon switch
        {
            WeaponId.ClodLobber => Unlimited,
            WeaponId.BeetleLauncher => 3,
            WeaponId.AcornMortar => 2,
            WeaponId.Fracking => 2,
            WeaponId.BigWhack => 2,
            WeaponId.SnapTrap => 2,
            WeaponId.RootSnare => 2,
            WeaponId.TunnelTorpedo => 2,
            WeaponId.PowerClaws => 2,
            WeaponId.Sandbag => 3,
            WeaponId.GeyserCap => 1,
            WeaponId.BoomBeets => 2,
            WeaponId.SpecialDelivery => 1,

            // The crate rarities. One per crate, and no other way to get one.
            WeaponId.MolyHandGrenade => 0,
            WeaponId.GnomeMercy => 0,

            _ => 0,
        };

        /// <summary>Stock value meaning there is no limit.</summary>
        public const int Unlimited = -1;

        public static bool IsUnlimited(WeaponId weapon) => StartingStock(weapon) == Unlimited;

        private static readonly WeaponSpec[] Specs = BuildSpecs();

        public static WeaponSpec Of(WeaponId weapon) => Specs[(int)weapon];

        public static WeaponKind KindOf(WeaponId weapon) => Specs[(int)weapon].Kind;

        private static WeaponSpec[] BuildSpecs()
        {
            WeaponSpec[] specs = new WeaponSpec[16];

            specs[(int)WeaponId.None] = default;

            // A three-second fuse, so it can be bounced into a hole or cooked over a head.
            specs[(int)WeaponId.ClodLobber] = new WeaponSpec(
                WeaponKind.Thrown,
                launchSpeed: Fix64.FromInt(26),
                damage: 30,
                blastRadius: Fix64.Ratio(5, 2),
                fuseTicks: MatchSettings.TicksPerSecond * 3,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(15),
                craterRadius: Fix64.Ratio(5, 4));

            // Faster, harder, and goes off the moment it arrives.
            specs[(int)WeaponId.BeetleLauncher] = new WeaponSpec(
                WeaponKind.Thrown,
                launchSpeed: Fix64.FromInt(34),
                damage: 45,
                blastRadius: Fix64.FromInt(2),
                fuseTicks: 0,
                detonatesOnContact: true,
                ridesTheWind: true,
                knockback: Fix64.FromInt(18),
                craterRadius: Fix64.One);

            // Splits into three on the way down, so it covers ground rather than a point.
            specs[(int)WeaponId.AcornMortar] = new WeaponSpec(
                WeaponKind.Thrown,
                launchSpeed: Fix64.FromInt(24),
                damage: 15,
                blastRadius: Fix64.Ratio(3, 2),
                fuseTicks: MatchSettings.TicksPerSecond * 2,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(9),
                clusterCount: 3,
                craterRadius: Fix64.Ratio(3, 4));

            // The only thing in the game that reaches a mole through dirt.
            specs[(int)WeaponId.Fracking] = new WeaponSpec(
                WeaponKind.Seismic,
                launchSpeed: Fix64.Zero,
                damage: 15,
                blastRadius: Fix64.FromInt(6),
                fuseTicks: 0,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(10),
                reachesBuried: true);

            // Enormous, and needs the mole to be standing next to somebody at an exact
            // moment, which is the hardest thing in the game to arrange.
            specs[(int)WeaponId.BigWhack] = new WeaponSpec(
                WeaponKind.Melee,
                launchSpeed: Fix64.Zero,
                damage: 60,
                blastRadius: Fix64.Ratio(3, 2),
                fuseTicks: 0,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(38));

            specs[(int)WeaponId.SnapTrap] = new WeaponSpec(
                WeaponKind.Tool,
                launchSpeed: Fix64.Zero,
                damage: 35,
                blastRadius: Fix64.Ratio(3, 2),
                fuseTicks: 0,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(14));

            specs[(int)WeaponId.RootSnare] = new WeaponSpec(
                WeaponKind.Tool,
                launchSpeed: Fix64.Zero,
                damage: 0,
                blastRadius: Fix64.FromInt(4),
                fuseTicks: 0,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.Zero);

            specs[(int)WeaponId.TunnelTorpedo] = new WeaponSpec(
                WeaponKind.Tool,
                launchSpeed: Fix64.Zero,
                damage: 25,
                blastRadius: Fix64.FromInt(2),
                fuseTicks: 0,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(22),
                craterRadius: Fix64.One);

            specs[(int)WeaponId.PowerClaws] = new WeaponSpec(
                WeaponKind.Tool, Fix64.Zero, 0, Fix64.Zero, 0, false, false, Fix64.Zero);

            specs[(int)WeaponId.Sandbag] = new WeaponSpec(
                WeaponKind.Tool, Fix64.Zero, 0, Fix64.FromInt(2), 0, false, false, Fix64.Zero);

            specs[(int)WeaponId.GeyserCap] = new WeaponSpec(
                WeaponKind.Tool, Fix64.Zero, 0, Fix64.Ratio(3, 2), 0, false, false, Fix64.FromInt(26));

            // Planted at the mole's feet, so it has no launch speed at all. Plant, run,
            // regret.
            specs[(int)WeaponId.BoomBeets] = new WeaponSpec(
                WeaponKind.Planted,
                launchSpeed: Fix64.Zero,
                damage: 50,
                blastRadius: Fix64.FromInt(3),
                fuseTicks: MatchSettings.TicksPerSecond * 3,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(24),
                craterRadius: Fix64.Ratio(3, 2));

            // Three sacks along a strip, falling from above, so anybody underground is
            // simply out of reach. The counterplay is the game's own verb.
            specs[(int)WeaponId.SpecialDelivery] = new WeaponSpec(
                WeaponKind.FromTheSky,
                launchSpeed: Fix64.Zero,
                damage: 25,
                blastRadius: Fix64.FromInt(2),
                fuseTicks: 0,
                detonatesOnContact: true,
                ridesTheWind: false,
                knockback: Fix64.FromInt(14),
                clusterCount: 3,
                craterRadius: Fix64.One);

            // The crate rarity. One per crate, one use, and it ends two platoons' plans.
            specs[(int)WeaponId.MolyHandGrenade] = new WeaponSpec(
                WeaponKind.Thrown,
                launchSpeed: Fix64.FromInt(22),
                damage: 75,
                blastRadius: Fix64.FromInt(5),
                fuseTicks: MatchSettings.TicksPerSecond * 3,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(30),
                craterRadius: Fix64.Ratio(5, 2));

            // Punches through the surface and bounces three times with total indifference
            // to terrain, going off at every landing.
            specs[(int)WeaponId.GnomeMercy] = new WeaponSpec(
                WeaponKind.FromTheSky,
                launchSpeed: Fix64.Zero,
                damage: 50,
                blastRadius: Fix64.Ratio(7, 2),
                fuseTicks: 0,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(26),
                bounceBlasts: 3,
                craterRadius: Fix64.Ratio(7, 4));

            return specs;
        }
    }
}
