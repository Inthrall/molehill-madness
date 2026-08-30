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
    /// <summary>
    /// How far out from the mole the aim point sits, in metres.
    /// </summary>
    /// <remarks>
    /// Only a distance to put a point at now. It used to be the full-power drag, and the length of
    /// the drag was the power: pointing further threw harder. That is gone, and the name went with
    /// it, because leaving it called FullPowerDrag would have every reader of the pad and touch
    /// paths believe a reach still means a power.
    /// </remarks>
    public static Fix64 AimReach => Fix64.FromInt(20);

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

    /// <summary>
    /// Every weapon use booked this turn, in the order they will happen.
    /// </summary>
    /// <remarks>
    /// A list rather than the single Shot this replaced, because a turn now has two allowances
    /// rather than one shot: an attack and a movement ability, and the things you build with may be
    /// used more than once inside their own allowance.
    ///
    /// The Boom Beets used to live outside this entirely, as a separate action with its own button
    /// and its own field, because firing meant aiming and winding up and neither means anything for
    /// something dropped at your feet. Planted weapons now ask for a press and nothing else, so the
    /// beets are an ordinary use of an ordinary weapon and the button and the field are both gone.
    /// </remarks>
    public IReadOnlyList<PlanAction> Uses => _uses;

    private readonly List<PlanAction> _uses = new List<PlanAction>(4);

    /// <summary>The turn's attack, if one has been booked.</summary>
    public PlanAction? Shot => Booked(UseSlot.Attack);

    /// <summary>Which weapon is booked into a slot, or None if the slot is still free.</summary>
    public WeaponId BookedIn(UseSlot slot)
    {
        foreach (PlanAction use in _uses)
        {
            if (WeaponTable.SlotOf(WeaponOf(use)) == slot)
            {
                return WeaponOf(use);
            }
        }

        return WeaponId.None;
    }

    /// <summary>How many times a slot has been spent this turn.</summary>
    public int SpentIn(UseSlot slot)
    {
        int spent = 0;

        foreach (PlanAction use in _uses)
        {
            if (WeaponTable.SlotOf(WeaponOf(use)) == slot)
            {
                spent++;
            }
        }

        return spent;
    }

    /// <summary>
    /// Whether the selected weapon could be used once more this turn.
    /// </summary>
    /// <remarks>
    /// The same three questions the simulation asks when it validates the plan, asked here so the
    /// fire button can go grey instead of the plan being refused after the fact. Stock, the slot not
    /// already holding a different weapon, and the weapon's own per-turn allowance.
    /// </remarks>
    public bool CanUseAgain
    {
        get
        {
            if (!IsPlanning || !_match.CanUse(Seat, Weapon))
            {
                return false;
            }

            // The slot is not already holding something else. A full allowance is still usable,
            // because pressing again replaces rather than refuses, so this is about which weapon
            // owns the slot and not about how many times it has been used.
            WeaponId booked = BookedIn(WeaponTable.SlotOf(Weapon));

            return booked == WeaponId.None || booked == Weapon;
        }
    }

    /// <summary>How many more times the selected weapon could be used before it starts replacing.</summary>
    public int UsesLeft
    {
        get
        {
            UseSlot slot = WeaponTable.SlotOf(Weapon);
            int allowance = WeaponTable.UsesPerTurn(Weapon);
            int stock = Stock(Weapon);

            if (stock >= 0 && stock < allowance)
            {
                allowance = stock;
            }

            int left = allowance - SpentIn(slot);

            return left < 0 ? 0 : left;
        }
    }

    private PlanAction? Booked(UseSlot slot)
    {
        foreach (PlanAction use in _uses)
        {
            if (WeaponTable.SlotOf(WeaponOf(use)) == slot)
            {
                return use;
            }
        }

        return null;
    }

    /// <summary>
    /// Which weapon a booked use is of.
    /// </summary>
    /// <remarks>
    /// Every use this class books names its own weapon, so the fallback is only ever reached by a
    /// plan built somewhere else. It has to be that way round: resolving None against whatever is
    /// currently on the wheel would re-label a booked shot every time the player turned the wheel to
    /// look at something, and a Clod Lobber already thrown would become a use of the Power Claws.
    /// </remarks>
    private WeaponId WeaponOf(PlanAction use) =>
        use.Weapon == WeaponId.None ? Weapon : use.Weapon;

    /// <summary>Whether an aim is being dragged out right now.</summary>
    public bool Aiming { get; private set; }

    public Vec2 AimAt { get; private set; }

    /// <summary>How long the aim has been held, as a fraction of a full charge.</summary>
    public double AimHeld { get; private set; }

    /// <summary>How far through the hold-to-reset gesture, from zero to one.</summary>
    public double ResetHeld { get; private set; }

    /// <summary>
    /// How far through the hold-to-end-turn gesture this seat is, from zero to one.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="ResetHeld"/> and for the same reason. Ending a turn cannot be
    /// taken back: the plan goes to the simulation and the round resolves around it. A tap that does
    /// something irreversible is a tap somebody makes by accident, and this one used to sit on the
    /// space bar, which is the key a hand rests on.
    /// </remarks>
    public double CommitHeld { get; private set; }

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
        _uses.Clear();
        _hops.Clear();
        Aiming = false;
        Committed = false;
        ResetHeld = 0;
        CommitHeld = 0;
        AimHeld = 0;
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

    /// <summary>
    /// Loads a weapon outright, if the platoon holds it.
    /// </summary>
    /// <remarks>
    /// The wheel steps one place at a time, which is right for a thumb and useless to a driver that
    /// knows what it wants. Refused rather than forced when the platoon has none, so a caller cannot
    /// arm something the plan would then be rejected for naming.
    /// </remarks>
    public bool Select(WeaponId weapon)
    {
        if (Committed || !_match.CanUse(Seat, weapon))
        {
            return false;
        }

        Weapon = weapon;
        return true;
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

    /// <summary>What the selected weapon needs to be told before it can be used.</summary>
    public AimStyle Style => WeaponTable.AimingFor(Weapon);

    public void BeginAim(Vec2 at)
    {
        // Refused up front rather than at the release. Letting an aim be dragged out and then
        // dropped on the floor is the silent failure this class goes out of its way to avoid
        // everywhere else, and the only case left is a slot already holding a different weapon.
        if (!IsPlanning || !CanUseAgain)
        {
            return;
        }

        // A weapon with nothing to point and nothing to wind up is used by the press itself. Going
        // through the aim states for it would ask for a drag and a hold and then discard both, and
        // the Sandbag would appear to be a weapon you could throw further by leaning on the button.
        if (Style == AimStyle.Press)
        {
            Book(PlanAction.Fire(Now(), Heading(), byte.MaxValue, Weapon));
            return;
        }

        Aiming = true;
        AimAt = at;
        AimHeld = 0;
    }

    /// <summary>
    /// Which way the mole is facing, as the aim a press-only weapon carries.
    /// </summary>
    /// <remarks>
    /// The resolver ignores it for every weapon that gets here, so any direction would do, and a
    /// zero vector would be the honest one. The facing is used instead because the plan is encoded
    /// as an angle: zero does not survive the trip, and a direction that arrives at resolution
    /// meaning something other than what was sent is a trap for whoever next gives one of these
    /// weapons a use for its aim.
    /// </remarks>
    private Vec2 Heading()
    {
        Vec2 facing = Actor?.Facing ?? Vec2.Zero;

        return facing.LengthSquared() == Fix64.Zero ? new Vec2(Fix64.One, Fix64.Zero) : facing;
    }

    /// <summary>
    /// Winds the shot up for as long as the button is held.
    /// </summary>
    /// <remarks>
    /// Power used to be the length of the drag: point near the mole for a lob, point a long way off
    /// to throw hard. Two things were wrong with that. It spent the aim gesture twice, so choosing a
    /// direction and choosing a power were the same movement and neither could be adjusted without
    /// disturbing the other; and it does not survive contact with a pad or a thumb, where the stick
    /// has a fixed throw and the distance available is whatever the deadzone left over.
    ///
    /// Time is the honest axis for a wind-up, and every artillery game in the genre uses it. Point
    /// where you want it to go, hold to decide how hard, let go to stamp the shot.
    ///
    /// Polled rather than counted from key events, like the reset and the commit, because a held
    /// button is one press and then silence. Clamped at full rather than cycling: overshooting a
    /// charge you cannot see the end of is a worse deal than waiting a moment with it full.
    /// </remarks>
    public void HoldAim(double delta)
    {
        if (!Aiming || !IsPlanning || Style != AimStyle.DirectionAndPower)
        {
            return;
        }

        AimHeld += delta / ChargeSeconds;

        if (AimHeld > 1)
        {
            AimHeld = 1;
        }
    }

    /// <summary>How long a full wind-up takes.</summary>
    public const double ChargeSeconds = 1.2;

    public void MoveAim(Vec2 at)
    {
        if (Aiming)
        {
            AimAt = at;
        }
    }

    /// <summary>
    /// Stamps the turn's shot: a direction from where it is pointed, a power from how long it was
    /// held, and a moment from wherever the mole has been steered to.
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

        // A swing and a drill have exactly one strength, so they leave at full rather than at
        // whatever the thumb happened to have accumulated on the way to choosing a direction.
        Book(PlanAction.Fire(
            Now(), aim,
            Style == AimStyle.DirectionAndPower ? PowerFor(AimHeld) : byte.MaxValue,
            Weapon));

        AimHeld = 0;
    }

    /// <summary>
    /// How hard a given wind-up throws, as the byte the plan will carry.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="AimCharge"/> on purpose. The charge gauge is drawn from this rather
    /// than from the raw hold, so it cannot promise a throw the plan will not contain: the clamp at
    /// either end is part of the answer, and a bar that ignored the floor would read as empty for a
    /// shot that is about to go off at a fifth power anyway.
    /// </remarks>
    private static byte PowerFor(double charged)
    {
        int power = (int)(charged * byte.MaxValue);

        return (byte)(power < WeakestThrow ? WeakestThrow
            : power > byte.MaxValue ? byte.MaxValue : power);
    }

    /// <summary>The softest a shot can be thrown. A dropped clod is still a throw.</summary>
    private const int WeakestThrow = 20;

    /// <summary>
    /// Books a weapon use, if the turn has an allowance left for it.
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to the simulation. A plan that breaks an allowance is refused
    /// outright when it is submitted, which from the player's side is the turn silently failing at
    /// the moment they can no longer do anything about it. The button greys out instead.
    /// </remarks>
    private void Book(PlanAction use)
    {
        if (!CanUseAgain)
        {
            return;
        }

        if (UsesLeft > 0)
        {
            _uses.Add(use);
            return;
        }

        // The allowance is full, so this replaces the most recent use of the slot rather than being
        // dropped. Changing your mind while the clock is still running has always been free here:
        // aiming again used to simply overwrite the turn's one shot, and losing that to the new list
        // would mean a misjudged throw could only be undone by spending a reset token on the whole
        // turn. Replacing the last one covers both shapes, the single shot and the third sandbag.
        for (int index = _uses.Count - 1; index >= 0; index--)
        {
            if (WeaponTable.SlotOf(WeaponOf(_uses[index])) == WeaponTable.SlotOf(use.Weapon))
            {
                _uses[index] = use;
                return;
            }
        }
    }

    /// <summary>Which way the shot points, or nothing when there is no shot to point.</summary>
    public Vec2 AimHeading
    {
        get
        {
            // Nothing to point means nothing to draw. The facing a press-only weapon carries is
            // only there to survive the plan codec, and drawing an arrow off it would tell a player
            // the Sandbag has a direction they can choose.
            if (Style == AimStyle.Press)
            {
                return Vec2.Zero;
            }

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
        Style != AimStyle.DirectionAndPower
            ? 0
            : (Aiming ? PowerFor(AimHeld) : Shot?.Power ?? 0) / (double)byte.MaxValue;

    /// <summary>
    /// Plants the charge where the mole is standing, or picks it back up.
    /// </summary>
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

        // The preview leaves the ground here, which is the whole of what makes the key feel like
        // it did something. Asked first: a mole already in the air cannot hop, resolution will
        // ignore the action, and booking one anyway would spend a hop on nothing.
        if (Walk?.Hop() == false)
        {
            return false;
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

    /// <summary>Advances the hold-to-end-turn gesture, and commits when it completes.</summary>
    public void HoldCommit(double delta)
    {
        if (!IsPlanning)
        {
            return;
        }

        CommitHeld += delta / HoldSeconds;

        if (CommitHeld < 1)
        {
            return;
        }

        CommitHeld = 0;
        Commit();
    }

    public void ReleaseCommit()
    {
        CommitHeld = 0;
    }

    /// <summary>
    /// Long enough that nobody wipes a turn, or ends one, by leaning on a button.
    /// </summary>
    /// <remarks>
    /// One hold length for both gestures rather than one each. They are the two irreversible
    /// presses in the game and they want to feel like the same kind of press, so a player who has
    /// learned the weight of one has learned the other.
    /// </remarks>
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
        _uses.Clear();
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

        actions.AddRange(_uses);

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
        WeaponId.BoomBeets,
        WeaponId.PowerClaws,
        WeaponId.Sandbag,
        WeaponId.SpecialDelivery,
        WeaponId.MolyHandGrenade,
        WeaponId.GnomeMercy,
    };
}
