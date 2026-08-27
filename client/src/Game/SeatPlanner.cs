using System;
using System.Collections.Generic;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;

/// <summary>
/// One platoon's turn, in progress.
/// </summary>
/// <remarks>
/// The design has everybody planning at once, which is the whole reason split screen exists,
/// so there is no such thing as "the" plan being laid: there are up to four, side by side,
/// each with its own mole, its own aim and its own reset token. This holds one of them.
///
/// A turn is steered rather than drawn. The player pushes, the mole walks, and the route it
/// leaves behind is the plan. The first version drew the route as a line and looped a
/// translucent ghost along it, which put two moles on the screen and left a first-time player
/// unable to say which one was theirs or what the line was for. Steering says the same thing
/// with one mole in it, and it costs the player nothing to learn.
///
/// It knows nothing about input devices or screens. A stick, a keyboard or a thumb all reach the
/// game through the same handful of verbs here, which is what lets the same match run on a phone
/// and in a four-way couch split without the rules caring which.
/// </remarks>
public sealed class SeatPlanner
{
    /// <summary>Drag distance, in metres, that charges a shot fully.</summary>
    public static Fix64 FullPowerDrag => Fix64.FromInt(20);

    private readonly MoleMatch _match;

    private readonly List<PlanAction> _hops = new List<PlanAction>();

    public SeatPlanner(MoleMatch match, int seat)
    {
        _match = match;
        Seat = seat;
        Weapon = FirstAvailable();
    }

    /// <summary>How many hops one turn may schedule. More than this and a plan stops reading.</summary>
    public const int MaxHops = 3;

    public int Seat { get; }

    /// <summary>Which mole is taking the turn. Null when the platoon has nobody eligible.</summary>
    public Mole? Actor { get; private set; }

    /// <summary>The turn being walked out, or null when there is nobody to walk it.</summary>
    public SteeredWalk? Walk { get; private set; }

    public WeaponId Weapon { get; private set; }

    /// <summary>Hops scheduled along the way, in the order they will happen.</summary>
    public IReadOnlyList<PlanAction> Hops => _hops;

    /// <summary>How many of a weapon this platoon has, or -1 for unlimited.</summary>
    public int Stock(WeaponId weapon) => _match.Stock(Seat, weapon);

    /// <summary>Whether the charge button has anything left to plant.</summary>
    public bool HasCharges => _match.CanUse(Seat, WeaponId.BoomBeets);

    public PlanAction? Shot { get; private set; }

    /// <summary>
    /// The Boom Beets, planted at the mole's feet. Does not spend the turn's shot, which is
    /// the whole reason the design gives it a button of its own.
    /// </summary>
    public PlanAction? Charge { get; private set; }

    /// <summary>Where the charge was planted, which is not where the mole ends up.</summary>
    public Vec2 ChargeAt { get; private set; }

    /// <summary>Whether an aim is being dragged out right now.</summary>
    public bool Aiming { get; private set; }

    public Vec2 AimAt { get; private set; }

    /// <summary>How far through the hold-to-reset gesture, from zero to one.</summary>
    public double ResetHeld { get; private set; }

    /// <summary>Whether this seat has locked its plan in for the round.</summary>
    public bool Committed { get; private set; }

    private bool _freeResetSpent;
    private double _tickDebt;

    /// <summary>Resets tokens left: one free a turn, then whatever the crates have given.</summary>
    public int ResetsLeft => (_freeResetSpent ? 0 : 1) + (Actor?.ResetTokens ?? 0);

    /// <summary>Whether this seat still has anything to do this round.</summary>
    public bool IsPlanning => Actor is not null && !Committed;

    /// <summary>
    /// Where the mole has been steered to. This is where it is drawn, where the shot leaves
    /// from, and where anything else in the plan happens.
    /// </summary>
    public Vec2 PlannedPosition => Walk?.Position ?? Actor?.Position ?? Vec2.Zero;

    /// <summary>How much of the round the walk has eaten, from zero to one.</summary>
    public double TimeSpent =>
        Walk is null ? 0 : Math.Min(Walk.TicksUsed / (double)MatchSettings.TicksPerRound, 1);

    /// <summary>How much of the mole's puff the walk has eaten, from zero to one.</summary>
    public double PuffSpent
    {
        get
        {
            if (Walk is null || Actor is null)
            {
                return 0;
            }

            double total = (double)Actor.Stamina.ToDecimal();

            return total <= 0 ? 0 : Math.Min((double)Walk.StaminaSpent.ToDecimal() / total, 1);
        }
    }

    public bool RanOutOfPuff => Walk?.RanOutOfPuff == true;

    /// <summary>Whether there is any of the round left to walk in.</summary>
    public bool HasTimeLeft => Walk?.HasTimeLeft == true;

    public void BeginRound()
    {
        Actor = null;
        Walk = null;
        Shot = null;
        Charge = null;
        _hops.Clear();
        Aiming = false;
        Committed = false;
        ResetHeld = 0;
        _tickDebt = 0;
        _freeResetSpent = false;

        // Whose turn it is, and there is no choosing about it. The simulation rotates through a
        // platoon and skips whoever has gone out: Eligible hands back only moles still standing
        // that have not had a go this cycle, and once they all have, the rotation starts again.
        // Taking the first of them is the rotation. There was briefly a key to step past it, which
        // is a decision nobody needs to be asked to make and one more thing to explain.
        foreach (Mole candidate in _match.Eligible(Seat))
        {
            Actor = candidate;
            break;
        }

        // A platoon with nobody left to move has nothing to commit, and must not hold the
        // round open waiting for a plan that cannot exist.
        Committed = Actor is null;
        StartWalk();
    }

    /// <summary>
    /// Turns the wheel, skipping anything this platoon has none of.
    /// </summary>
    /// <remarks>
    /// Filtered rather than greyed out. A wheel of fifteen where eleven are unavailable is
    /// eleven flicks of nothing, and the simulation would refuse the plan anyway.
    /// </remarks>
    public void CycleWeapon(int direction)
    {
        if (Committed || direction == 0)
        {
            return;
        }

        int step = direction > 0 ? 1 : -1;
        int at = Array.IndexOf(Arsenal.Wheel, Weapon);

        for (int tried = 0; tried < Arsenal.Wheel.Length; tried++)
        {
            at = (at + step + Arsenal.Wheel.Length) % Arsenal.Wheel.Length;

            if (_match.CanUse(Seat, Arsenal.Wheel[at]))
            {
                Weapon = Arsenal.Wheel[at];
                return;
            }
        }
    }

    /// <summary>Everything this platoon could pick right now, in wheel order.</summary>
    public List<WeaponId> Available()
    {
        List<WeaponId> available = new List<WeaponId>(Arsenal.Wheel.Length);

        foreach (WeaponId weapon in Arsenal.Wheel)
        {
            if (_match.CanUse(Seat, weapon))
            {
                available.Add(weapon);
            }
        }

        return available;
    }

    private WeaponId FirstAvailable()
    {
        foreach (WeaponId weapon in Arsenal.Wheel)
        {
            if (_match.CanUse(Seat, weapon))
            {
                return weapon;
            }
        }

        return WeaponId.ClodLobber;
    }

    // ---- Steering ------------------------------------------------------------------

    /// <summary>
    /// Walks the mole for however much of the round the last frame was worth.
    /// </summary>
    /// <remarks>
    /// Real time in, simulation ticks out, which is what makes the movement budget legible: hold
    /// the stick for a second and a second of the round is gone. The eight seconds are eight
    /// seconds of walking rather than of wall clock, so nothing is spent while nobody is pushing.
    ///
    /// The debt is capped rather than carried. A dropped frame is not something the player did,
    /// and paying it back afterwards would jerk the mole forward through ground they were still
    /// deciding about.
    /// </remarks>
    public void Steer(Vec2 direction, double seconds)
    {
        if (!IsPlanning || Walk is null)
        {
            return;
        }

        bool idle = direction.LengthSquared() == Fix64.Zero && !Walk.IsFalling;

        if (idle || !Walk.HasTimeLeft)
        {
            _tickDebt = 0;
            return;
        }

        _tickDebt = Math.Min(
            _tickDebt + (seconds * MatchSettings.TicksPerSecond), MaxTickDebt);

        while (_tickDebt >= 1 && Walk.HasTimeLeft)
        {
            Walk.Advance(direction);
            _tickDebt -= 1;
        }
    }

    /// <summary>Ticks the debt may reach, which is about a fifth of a second's worth of hitch.</summary>
    private const double MaxTickDebt = 6;

    /// <summary>
    /// Walks exactly one tick. What the test driver steers with, so it goes through the same
    /// door a stick does rather than assembling a route behind the game's back.
    /// </summary>
    public void StepToward(Vec2 direction)
    {
        if (IsPlanning)
        {
            Walk?.Advance(direction);
        }
    }

    // ---- Aiming --------------------------------------------------------------------

    public void BeginAim(Vec2 at)
    {
        if (!IsPlanning)
        {
            return;
        }

        Aiming = true;
        AimAt = at;
    }

    public void MoveAim(Vec2 at)
    {
        if (Aiming)
        {
            AimAt = at;
        }
    }

    /// <summary>
    /// Stamps the turn's shot: a direction from the drag, a power from its length, and a
    /// moment from wherever the mole has been steered to.
    /// </summary>
    public void ReleaseAim()
    {
        if (!Aiming || !IsPlanning)
        {
            Aiming = false;
            return;
        }

        Aiming = false;
        Vec2 aim = AimAt - PlannedPosition;

        if (aim.LengthSquared() == Fix64.Zero)
        {
            return;
        }

        Shot = PlanAction.Fire(Now(), aim, PowerFor(aim));
    }

    /// <summary>
    /// How hard a given drag throws, as the byte the plan will carry.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="AimCharge"/> on purpose. The charge gauge is drawn from this rather
    /// than from the raw drag length, so it cannot promise a throw the plan will not contain: the
    /// clamp at either end is part of the answer, and a bar that ignored the floor would read as
    /// empty for a shot that is about to go off at a fifth power anyway.
    /// </remarks>
    private static byte PowerFor(Vec2 aim)
    {
        Fix64 reach = Fix64.Min(aim.Length(), FullPowerDrag);
        int power = Fix64.ToInt(reach / FullPowerDrag * Fix64.FromInt(byte.MaxValue));

        return (byte)(power < WeakestThrow ? WeakestThrow
            : power > byte.MaxValue ? byte.MaxValue : power);
    }

    /// <summary>The softest a shot can be thrown. A dropped clod is still a throw.</summary>
    private const int WeakestThrow = 20;

    /// <summary>Which way the shot points, or nothing when there is no shot to point.</summary>
    public Vec2 AimHeading
    {
        get
        {
            if (Aiming)
            {
                Vec2 aim = AimAt - PlannedPosition;

                return aim.LengthSquared() == Fix64.Zero ? Vec2.Zero : aim.Normalised();
            }

            return Shot?.AimDirection() ?? Vec2.Zero;
        }
    }

    /// <summary>How charged the shot is, from nothing to full.</summary>
    public double AimCharge =>
        (Aiming ? PowerFor(AimAt - PlannedPosition) : Shot?.Power ?? 0) / (double)byte.MaxValue;

    /// <summary>
    /// Plants the charge where the mole is standing, or picks it back up.
    /// </summary>
    /// <remarks>
    /// A toggle rather than a one-way commitment, because it costs nothing to change your mind
    /// while the turn is still being walked and the reset token is far too precious to spend on
    /// a misplaced beet.
    ///
    /// Booked at the moment it is pressed rather than at the end of the route, which steering
    /// makes possible and drawing did not: walk in, drop it, walk out, and the plan holds all
    /// three. Plant, run, regret, in that order.
    /// </remarks>
    public void PlantCharge()
    {
        if (!IsPlanning)
        {
            return;
        }

        if (Charge is not null)
        {
            Charge = null;
            return;
        }

        if (!HasCharges)
        {
            return;
        }

        Charge = PlanAction.Dynamite(Now());
        ChargeAt = PlannedPosition;
    }

    // ---- Hopping ---------------------------------------------------------------------

    /// <summary>
    /// Books a hop for the moment it is pressed.
    /// </summary>
    /// <remarks>
    /// One press, no arming. The drawn version needed two steps, because a single finger laying a
    /// route could not also press a button partway along it, so a hop had to be armed and then
    /// tapped onto the line. Steering has a "now" that a drawing never had, which is exactly what
    /// a hop wants to be booked against.
    /// </remarks>
    /// <returns>Whether one was actually booked.</returns>
    public bool BookHop()
    {
        if (!IsPlanning || _hops.Count >= MaxHops)
        {
            return false;
        }

        int tick = Now();

        // A hop is scheduled at a moment, not at a place, so two of them on the same tick would
        // be one hop and one wasted press.
        foreach (PlanAction existing in _hops)
        {
            if (existing.Tick == tick)
            {
                return false;
            }
        }

        _hops.Add(PlanAction.Hop(tick));
        _hops.Sort((first, second) => first.Tick.CompareTo(second.Tick));
        return true;
    }

    /// <summary>Where a hop was booked, for the client to mark it.</summary>
    public Vec2 HopPosition(PlanAction hop) =>
        Walk?.PositionAt(hop.Tick) ?? Actor?.Position ?? Vec2.Zero;

    /// <summary>Which tick of the round the mole has walked as far as.</summary>
    private int Now()
    {
        int tick = Walk?.TicksUsed ?? 0;

        return tick >= MatchSettings.TicksPerRound ? MatchSettings.TicksPerRound - 1 : tick;
    }

    // ---- Resetting -----------------------------------------------------------------

    /// <summary>Advances the hold-to-reset gesture, and spends a token when it completes.</summary>
    public void HoldReset(double delta)
    {
        if (!IsPlanning)
        {
            return;
        }

        ResetHeld += delta / HoldSeconds;

        if (ResetHeld < 1)
        {
            return;
        }

        ResetHeld = 0;
        SpendReset();
    }

    public void ReleaseReset()
    {
        ResetHeld = 0;
    }

    /// <summary>Long enough that nobody wipes a turn by leaning on a button.</summary>
    private const double HoldSeconds = 0.5;

    /// <summary>
    /// Tears up the whole turn and puts the mole back where it started. One free a turn, then
    /// only what the crates have handed over, and the free one goes first so a hoarded token is
    /// still a token afterwards.
    /// </summary>
    /// <remarks>
    /// This is the only undo. Backing up a few steps needs no machinery now that the turn is
    /// steered: walking back the way you came does it, and costs the puff and the seconds it
    /// would cost a real mole, which is a fairer price than a free rub of the eraser.
    /// </remarks>
    private void SpendReset()
    {
        if (Actor is null || (_freeResetSpent && Actor.ResetTokens <= 0))
        {
            return;
        }

        if (_freeResetSpent)
        {
            Actor.ResetTokens--;
        }
        else
        {
            _freeResetSpent = true;
        }

        Discard();
    }

    private void Discard()
    {
        Shot = null;
        Charge = null;
        _hops.Clear();
        Aiming = false;
        _tickDebt = 0;
        StartWalk();
    }

    private void StartWalk()
    {
        Walk = Actor is null ? null : SteeredWalk.From(Actor, _match.Terrain);
    }

    // ---- Committing ----------------------------------------------------------------

    /// <summary>
    /// Hands the plan to the simulation. Everything after this point is out of the player's
    /// hands, which is the point of the whole design.
    /// </summary>
    public void Commit()
    {
        Plan? plan = Seal();

        if (plan is not null)
        {
            _match.SubmitPlan(plan);
        }
    }

    /// <summary>
    /// Seals the plan and hands it back without submitting it anywhere.
    /// </summary>
    /// <remarks>
    /// Online, the plan goes to the relay and comes back with everybody else's, and the simulation
    /// is fed from those bytes rather than from this object. That is the whole determinism argument:
    /// if this client submitted the object it built while the others submitted what they received,
    /// four simulations would be eating from sources that are only supposed to be identical, and any
    /// imperfection in the codec would surface as a desync in the field instead of a failing test.
    ///
    /// Returns null when this platoon has nothing to plan with, which the caller has to turn into
    /// something anyway: the relay releases a round only when every seat has committed, so a
    /// wiped-out platoon still owes an answer.
    /// </remarks>
    public Plan? Seal()
    {
        if (Actor is null || Committed)
        {
            Committed = true;
            return null;
        }

        Committed = true;

        List<Vec2> waypoints = Walk?.Waypoints() ?? new List<Vec2>();
        RoutePoint[] route = new RoutePoint[waypoints.Count];

        for (int index = 0; index < waypoints.Count; index++)
        {
            route[index] = RoutePoint.FromWorld(waypoints[index]);
        }

        List<PlanAction> actions = new List<PlanAction>(MaxHops + 3);
        actions.AddRange(_hops);

        if (Charge is not null)
        {
            actions.Add(Charge.Value);
        }

        if (Shot is not null)
        {
            actions.Add(Shot.Value);
        }

        return new Plan(Seat, Actor.Index, Weapon, route, actions.ToArray());
    }
}

/// <summary>The order weapons come round on the wheel.</summary>
public static class Arsenal
{
    public static readonly WeaponId[] Wheel =
    {
        WeaponId.ClodLobber,
        WeaponId.BeetleLauncher,
        WeaponId.AcornMortar,
        WeaponId.Fracking,
        WeaponId.BigWhack,
        WeaponId.TunnelTorpedo,
        WeaponId.SnapTrap,
        WeaponId.RootSnare,
        WeaponId.GeyserCap,
        WeaponId.PowerClaws,
        WeaponId.Sandbag,
        WeaponId.SpecialDelivery,
        WeaponId.MolyHandGrenade,
        WeaponId.GnomeMercy,
    };
}
