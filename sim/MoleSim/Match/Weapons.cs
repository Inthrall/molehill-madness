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

        /// <summary>
        /// A plank, laid straight out from the mole along the aim. Bridges a gap or roofs a hole.
        /// </summary>
        Girder = 16,
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

    /// <summary>
    /// How much a player has to be asked before a weapon can be used.
    /// </summary>
    /// <remarks>
    /// Every weapon used to be aimed and wound up, because the throwing ones were built first and
    /// the rest were added to the same table. It made nonsense of most of the arsenal: winding up a
    /// wind-up on the Power Claws, which sharpens the mole's own claws, or pointing the Sandbag,
    /// which drops soil where the mole is standing. Nine of the fifteen weapons discarded both
    /// numbers the moment they reached the simulation, so the gesture asked for them was theatre.
    ///
    /// Derived from the kind rather than stored per row, because the kind already decides it: what
    /// the resolver reads off the order is exactly what the player has to supply. The one exception
    /// is named, and it is named because a Tool can be either.
    /// </remarks>
    public enum AimStyle : byte
    {
        /// <summary>
        /// Nothing to point and nothing to wind up. It happens where the mole is, so pressing is
        /// the whole gesture.
        /// </summary>
        Press = 0,

        /// <summary>
        /// A direction and no more. A swing reaches as far as an arm and a drill as far as a drill,
        /// and neither has a harder or softer version of itself.
        /// </summary>
        Direction = 1,

        /// <summary>
        /// A direction and a wind-up: the artillery case, and the only one the aim gauge means
        /// anything for.
        /// </summary>
        DirectionAndPower = 2,
    }

    /// <summary>
    /// Which of a turn's two allowances a weapon spends.
    /// </summary>
    /// <remarks>
    /// A turn is one attack and one movement ability, which is the shape the genre settled on
    /// decades ago and for a good reason: a mole that must choose between getting somewhere and
    /// hurting somebody spends every turn on the same dull sum, and one that may do both has a
    /// reason to think about the order.
    ///
    /// Only the two that exist for getting about are here. The Power Claws sharpen the mole's own
    /// claws and do nothing to anybody, and the Tunnel Torpedo is the game's own verb with a motor
    /// on it. The Sandbag and the Geyser Cap were both considered and left as attacks: a bag of soil
    /// is terrain you can drop on a head, and a capped vent is a trap whenever it is not under you.
    /// </remarks>
    public enum UseSlot : byte
    {
        /// <summary>The turn's shot.</summary>
        Attack = 0,

        /// <summary>The turn's way of getting somewhere.</summary>
        Movement = 1,
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
            WeaponId.Girder => 2,
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

        /// <summary>
        /// Which of the turn's two allowances this weapon spends.
        /// </summary>
        /// <remarks>
        /// Named rather than derived. Nothing in the spec table says whether a thing is for getting
        /// about, and a rule guessed from the numbers would put the Tunnel Torpedo, which does
        /// twenty-five damage, in with the grenades.
        /// </remarks>
        /// <summary>
        /// The highest weapon there is. The enum is contiguous from None, so this is its ceiling.
        /// </summary>
        public const WeaponId Last = WeaponId.Girder;

        /// <summary>
        /// Whether that value names a weapon at all.
        /// </summary>
        /// <remarks>
        /// A plan arrives as bytes from another player's device, and a byte is any of two hundred
        /// and fifty-six things while a weapon is one of seventeen. Nothing in the transport can
        /// check that, and nothing in it should: the relay stores plans as opaque bytes precisely so
        /// that it can never grow an opinion about one. The check belongs here, where bytes stop
        /// being bytes.
        ///
        /// It matters more than a tidy-up. Every weapon lookup indexes an array by this value, so an
        /// unchecked byte is an IndexOutOfRangeException raised inside the simulation, on every
        /// honest client, from a payload one dishonest client sent. The design's anti-cheat argument
        /// is that a cheat can only submit illegal inputs and that every client rejects those
        /// identically; an exception nobody catches is not a rejection, it is a crash.
        /// </remarks>
        public static bool Exists(WeaponId weapon) =>
            weapon >= WeaponId.None && weapon <= Last;

        public static UseSlot SlotOf(WeaponId weapon) => weapon switch
        {
            WeaponId.PowerClaws => UseSlot.Movement,
            WeaponId.TunnelTorpedo => UseSlot.Movement,
            WeaponId.Girder => UseSlot.Movement,
            _ => UseSlot.Attack,
        };

        /// <summary>
        /// How many times one mole may use this weapon in one turn.
        /// </summary>
        /// <remarks>
        /// One, for everything that goes off. Two exceptions, and both are things you build with
        /// rather than fire: a single sandbag is a bump in the ground and three are a step or a wall
        /// worth crossing the map for, and a pair of beets is a corner denied rather than one
        /// unlucky tile.
        ///
        /// Set to the starting stock in both cases, so a fresh platoon can spend a whole allowance
        /// in one turn and no allowance promises more uses than the arsenal ever hands out. Stock is
        /// a separate limit and is checked separately: using the allowance up costs the same as
        /// spreading it over three turns, and what it buys is doing it before somebody walks past
        /// rather than after.
        /// </remarks>
        public static int UsesPerTurn(WeaponId weapon) => weapon switch
        {
            WeaponId.Sandbag => 3,
            WeaponId.BoomBeets => 2,
            _ => 1,
        };

        /// <summary>
        /// What the player has to supply before this weapon can be used.
        /// </summary>
        /// <remarks>
        /// Read straight off what the resolver does with the order. Thrown scales its launch speed
        /// by the power and FromTheSky scales how far away it lands, so both want the wind-up.
        /// Melee uses the direction and ignores the power. Planted and Seismic use neither: one
        /// drops at the mole's feet and the other shakes the ground the mole is standing on.
        ///
        /// Tool is the one kind that splits, so the split is written out rather than guessed. The
        /// Girder is laid along an aim; the other four happen at the mole and would be asking for a
        /// direction they then throw away.
        ///
        /// The Tunnel Torpedo is the one Tool that wants the wind-up as well. It used to take a
        /// direction only, on the grounds that a drill has one strength, which was true and beside
        /// the point: a drill has a length, and spending the whole twelve metres to go through a
        /// wall two metres thick is the same waste as throwing a clod off the map. The wind-up buys
        /// distance rather than force, which is what <see cref="MatchSettings.TorpedoRangeFor"/>
        /// says and the only thing about a torpedo a player would want to choose.
        /// </remarks>
        public static AimStyle AimingFor(WeaponId weapon)
        {
            if (weapon == WeaponId.TunnelTorpedo)
            {
                return AimStyle.DirectionAndPower;
            }

            if (weapon == WeaponId.Girder)
            {
                return AimStyle.Direction;
            }

            return KindOf(weapon) switch
            {
                WeaponKind.Thrown => AimStyle.DirectionAndPower,
                WeaponKind.FromTheSky => AimStyle.DirectionAndPower,
                WeaponKind.Melee => AimStyle.Direction,
                _ => AimStyle.Press,
            };
        }

        private static WeaponSpec[] BuildSpecs()
        {
            WeaponSpec[] specs = new WeaponSpec[17];

            specs[(int)WeaponId.None] = default;

            // A three-second fuse, so it can be bounced into a hole or cooked over a head.
            //
            // Launch speeds across the arsenal came down by about a third after play testing said
            // throwing was too hard to control. At the old numbers a full charge left the map, so
            // the useful part of the wind-up was its first third and the rest was a way to miss;
            // reducing the top end makes the whole sweep worth something, which matters more now
            // that the charge oscillates and a player is choosing a moment rather than a duration.
            specs[(int)WeaponId.ClodLobber] = new WeaponSpec(
                WeaponKind.Thrown,
                launchSpeed: Fix64.FromInt(18),
                damage: 30,
                blastRadius: Fix64.Ratio(5, 2),
                fuseTicks: MatchSettings.TicksPerSecond * 3,
                detonatesOnContact: false,
                ridesTheWind: false,
                knockback: Fix64.FromInt(15),
                craterRadius: Fix64.Ratio(5, 4));

            // Faster, harder, and goes off the moment it arrives. Still the flattest thing in
            // the arsenal, which is its whole character, so it keeps the largest share.
            specs[(int)WeaponId.BeetleLauncher] = new WeaponSpec(
                WeaponKind.Thrown,
                launchSpeed: Fix64.FromInt(24),
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
                launchSpeed: Fix64.FromInt(17),
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

            // Radius down from two metres. A two metre blob is five times the mole's own height,
            // and because a deposit skips ground that is already solid, all of it landed in the air
            // above and around the mole rather than under it: the bag buried whoever dropped it and
            // built nothing worth standing on. Nine tenths of a metre is about two mole widths,
            // which is a step.
            // No damage and no blast. The radius is the plank's half-thickness, which is the only
            // number a deposit needs, and it is the thinnest thing that a mole can stand on without
            // falling through between ticks.
            specs[(int)WeaponId.Girder] = new WeaponSpec(
                WeaponKind.Tool, Fix64.Zero, 0, Fix64.Ratio(1, 8), 0, false, false, Fix64.Zero);

            specs[(int)WeaponId.Sandbag] = new WeaponSpec(
                WeaponKind.Tool, Fix64.Zero, 0, Fix64.Ratio(9, 10), 0, false, false, Fix64.Zero);

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
                launchSpeed: Fix64.FromInt(16),
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
