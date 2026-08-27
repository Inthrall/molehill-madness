using System;
using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>What a player committed to for one round.</summary>
    /// <remarks>
    /// This is the only thing that travels between players, and the whole online
    /// architecture rests on it being a recording of <em>inputs</em> rather than outcomes.
    /// A plan says "walk toward here, then fire that way at this moment"; it never says
    /// where anybody ends up. That is why a plan degrades comically instead of breaking
    /// when the world changes underneath it, and why the relay can be a courier that never
    /// simulates anything.
    ///
    /// Route points are cell coordinates rather than world positions. Cells are 6.25 cm,
    /// which is finer than anybody can aim on a phone, and it keeps a plan small enough
    /// that a four-player round is a few kilobytes on the wire.
    /// </remarks>
    public sealed class Plan
    {
        /// <summary>Bumped whenever the wire format changes. Old clients reject newer plans loudly.</summary>
        public const byte FormatVersion = 1;

        public Plan(int seat, int moleIndex, WeaponId weapon, RoutePoint[] route, PlanAction[] actions)
        {
            Seat = seat;
            MoleIndex = moleIndex;
            Weapon = weapon;
            Route = route ?? throw new ArgumentNullException(nameof(route));
            Actions = actions ?? throw new ArgumentNullException(nameof(actions));
        }

        /// <summary>Which platoon this plan belongs to.</summary>
        public int Seat { get; }

        /// <summary>
        /// Which of that platoon's moles is acting. Hidden until resolution online, because
        /// the choice is itself part of the plan.
        /// </summary>
        public int MoleIndex { get; }

        /// <summary>What is on the wheel. One shot per turn, so one weapon per plan.</summary>
        public WeaponId Weapon { get; }

        /// <summary>The route, laid forward only, in cell coordinates.</summary>
        public RoutePoint[] Route { get; }

        /// <summary>Everything scheduled along it, in tick order.</summary>
        public PlanAction[] Actions { get; }

        /// <summary>
        /// A plan that does nothing, which the design calls bracing in place and is the safe
        /// default for a dropped connection or a distracted friend.
        /// </summary>
        public static Plan Idle(int seat, int moleIndex) =>
            new Plan(seat, moleIndex, WeaponId.None, Array.Empty<RoutePoint>(), Array.Empty<PlanAction>());

        /// <summary>Converts the route to world positions for the solver.</summary>
        public Vec2[] ToWorldRoute()
        {
            Vec2[] world = new Vec2[Route.Length];

            for (int index = 0; index < Route.Length; index++)
            {
                world[index] = Route[index].ToWorld();
            }

            return world;
        }
    }

    /// <summary>A point on a route, in terrain cells.</summary>
    public readonly struct RoutePoint : IEquatable<RoutePoint>
    {
        public RoutePoint(int cellX, int cellY)
        {
            CellX = (short)cellX;
            CellY = (short)cellY;
        }

        public short CellX { get; }

        public short CellY { get; }

        /// <summary>The centre of the cell, which is where the ghost stood.</summary>
        public Vec2 ToWorld() =>
            new Vec2(WorldScale.ToCentreMetres(CellX), WorldScale.ToCentreMetres(CellY));

        public static RoutePoint FromWorld(Vec2 position) =>
            new RoutePoint(WorldScale.ToCell(position.X), WorldScale.ToCell(position.Y));

        public static bool operator ==(RoutePoint left, RoutePoint right) => left.Equals(right);

        public static bool operator !=(RoutePoint left, RoutePoint right) => !left.Equals(right);

        public bool Equals(RoutePoint other) => CellX == other.CellX && CellY == other.CellY;

        public override bool Equals(object? obj) => obj is RoutePoint other && Equals(other);

        public override int GetHashCode() => (CellX << 16) ^ CellY;

        public override string ToString() => $"({CellX}, {CellY})";
    }

    /// <summary>The things a player can schedule along a route.</summary>
    public enum PlanActionKind : byte
    {
        /// <summary>Jump a gap or a lip.</summary>
        Hop = 0,

        // 1 was Brace: dig in where you stand for a third off the next blast. Removed, because
        // holding still is what a player who plans nothing already does, so the action's only
        // content was the damage bonus, and a bonus for staying put pulls against a design that
        // fights bunkering with the stalemate nudge. Planning nothing still braces in place; there
        // is simply nothing to press for it. The number is left vacant rather than reused so the
        // wire encoding of everything else is untouched.

        /// <summary>The turn's single shot, stamped where the mole has been steered to.</summary>
        Fire = 2,

        /// <summary>Plant the Boom Beets, which does not spend the turn's shot.</summary>
        Dynamite = 3,
    }

    /// <summary>One scheduled action.</summary>
    /// <remarks>
    /// Aim is stored as a direction rather than an angle, which sidesteps fixed-point
    /// trigonometry entirely: no lookup tables, no polynomial approximation, and no
    /// question about whether two platforms agree on the sine of anything.
    /// </remarks>
    public readonly struct PlanAction : IEquatable<PlanAction>
    {
        /// <summary>Fixed-point scale for the stored aim components.</summary>
        public const int AimScale = 4096;

        private PlanAction(int tick, PlanActionKind kind, short aimX, short aimY, byte power)
        {
            Tick = (ushort)tick;
            Kind = kind;
            AimX = aimX;
            AimY = aimY;
            Power = power;
        }

        /// <summary>When in the round it happens, in ticks from the start.</summary>
        public ushort Tick { get; }

        public PlanActionKind Kind { get; }

        /// <summary>Aim direction, scaled by <see cref="AimScale"/>. Meaningful for Fire only.</summary>
        public short AimX { get; }

        public short AimY { get; }

        /// <summary>Launch power, 0 to 255. Meaningful for Fire only.</summary>
        public byte Power { get; }

        /// <summary>
        /// Rebuilds an action from its stored fields. For the codec only: everything else
        /// should go through the named factories so an aim is always normalised.
        /// </summary>
        internal static PlanAction FromWire(
            ushort tick, PlanActionKind kind, short aimX, short aimY, byte power) =>
            new PlanAction(tick, kind, aimX, aimY, power);

        public static PlanAction Hop(int tick) =>
            new PlanAction(tick, PlanActionKind.Hop, 0, 0, 0);

        public static PlanAction Dynamite(int tick) =>
            new PlanAction(tick, PlanActionKind.Dynamite, 0, 0, 0);

        /// <summary>Stamps the turn's shot: a direction, a power and a moment.</summary>
        public static PlanAction Fire(int tick, Vec2 aim, byte power)
        {
            Vec2 unit = aim.Normalised();

            return new PlanAction(
                tick,
                PlanActionKind.Fire,
                (short)Fix64.ToInt(unit.X * Fix64.FromInt(AimScale)),
                (short)Fix64.ToInt(unit.Y * Fix64.FromInt(AimScale)),
                power);
        }

        /// <summary>Aim as a direction, normalised so a rounded encoding still launches true.</summary>
        public Vec2 AimDirection()
        {
            Vec2 raw = new Vec2(
                Fix64.FromInt(AimX) / Fix64.FromInt(AimScale),
                Fix64.FromInt(AimY) / Fix64.FromInt(AimScale));

            return raw.Normalised();
        }

        /// <summary>Power as a fraction of the weapon's full charge.</summary>
        public Fix64 PowerFraction() => Fix64.FromInt(Power) / Fix64.FromInt(byte.MaxValue);

        public static bool operator ==(PlanAction left, PlanAction right) => left.Equals(right);

        public static bool operator !=(PlanAction left, PlanAction right) => !left.Equals(right);

        public bool Equals(PlanAction other) =>
            Tick == other.Tick
            && Kind == other.Kind
            && AimX == other.AimX
            && AimY == other.AimY
            && Power == other.Power;

        public override bool Equals(object? obj) => obj is PlanAction other && Equals(other);

        public override int GetHashCode() =>
            (Tick << 16) ^ ((int)Kind << 8) ^ (AimX * 31) ^ (AimY * 17) ^ Power;

        public override string ToString() => $"{Kind}@{Tick}";
    }
}
