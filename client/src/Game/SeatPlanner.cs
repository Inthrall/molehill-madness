using System.Collections.Generic;
using MoleSim;
using MoleSim.Match;
using MoleSim.Numerics;
using MoleSim.Terrain;

/// <summary>
/// One platoon's turn, in progress.
/// </summary>
/// <remarks>
/// The design has everybody planning at once, which is the whole reason split screen exists,
/// so there is no such thing as "the" plan being laid: there are up to four, side by side,
/// each with its own ink, its own aim and its own reset token. This holds one of them.
///
/// It knows nothing about input devices or screens. A mouse, a gamepad or a thumb all reach
/// the game through the same handful of verbs here, which is what lets the same match run on
/// a phone and in a four-way couch split without the rules caring which.
/// </remarks>
public sealed class SeatPlanner
{
    /// <summary>How far the pen travels before another waypoint drops.</summary>
    private static Fix64 PenStep => Fix64.Ratio(3, 4);

    /// <summary>Drag distance, in metres, that charges a shot fully.</summary>
    public static Fix64 FullPowerDrag => Fix64.FromInt(20);

    private readonly MoleMatch _match;

    public SeatPlanner(MoleMatch match, int seat)
    {
        _match = match;
        Seat = seat;
        Route = new List<Vec2>();
    }

    public int Seat { get; }

    /// <summary>Which mole is taking the turn. Null when the platoon has nobody eligible.</summary>
    public Mole? Actor { get; private set; }

    /// <summary>The waypoints as laid, in world metres.</summary>
    public List<Vec2> Route { get; }

    /// <summary>What the route would actually do, or null while nothing is laid.</summary>
    public GhostPreview? Preview { get; private set; }

    public WeaponId Weapon { get; private set; } = WeaponId.ClodLobber;

    public PlanAction? Shot { get; private set; }

    /// <summary>
    /// The Boom Beets, planted at the mole's feet. Does not spend the turn's shot, which is
    /// the whole reason the design gives it a button of its own.
    /// </summary>
    public PlanAction? Charge { get; private set; }

    /// <summary>Whether an aim is being dragged out right now.</summary>
    public bool Aiming { get; private set; }

    public Vec2 AimAt { get; private set; }

    /// <summary>How far through the hold-to-reset gesture, from zero to one.</summary>
    public double ResetHeld { get; private set; }

    /// <summary>Whether this seat has locked its plan in for the round.</summary>
    public bool Committed { get; private set; }

    /// <summary>Drives the looping ghost. Presentation only.</summary>
    public double GhostClock { get; private set; }

    private bool _penDown;
    private bool _freeResetSpent;

    /// <summary>Resets tokens left: one free a turn, then whatever the crates have given.</summary>
    public int ResetsLeft => (_freeResetSpent ? 0 : 1) + (Actor?.ResetTokens ?? 0);

    /// <summary>Whether this seat still has anything to do this round.</summary>
    public bool IsPlanning => Actor is not null && !Committed;

    /// <summary>Where a stamped shot would leave from: the pen's tip, not the mole's feet.</summary>
    public Vec2 Muzzle => Preview?.End ?? Actor?.Position ?? Vec2.Zero;

    public void BeginRound()
    {
        Actor = null;
        Route.Clear();
        Preview = null;
        Shot = null;
        Charge = null;
        Aiming = false;
        Committed = false;
        ResetHeld = 0;
        GhostClock = 0;
        _penDown = false;
        _freeResetSpent = false;

        foreach (Mole candidate in _match.Eligible(Seat))
        {
            Actor = candidate;
            break;
        }

        // A platoon with nobody left to move has nothing to commit, and must not hold the
        // round open waiting for a plan that cannot exist.
        Committed = Actor is null;
    }

    public void Tick(double delta)
    {
        GhostClock += delta;
    }

    /// <summary>Steps to the next mole that has not had its turn this cycle.</summary>
    public void CycleActor()
    {
        if (Actor is null || Committed)
        {
            return;
        }

        List<Mole> choices = new List<Mole>();

        foreach (Mole candidate in _match.Eligible(Seat))
        {
            choices.Add(candidate);
        }

        if (choices.Count <= 1)
        {
            return;
        }

        Actor = choices[(choices.IndexOf(Actor) + 1) % choices.Count];
        Route.Clear();
        Preview = null;
        Shot = null;
        Charge = null;
    }

    public void CycleWeapon(int direction)
    {
        if (Committed)
        {
            return;
        }

        int at = System.Array.IndexOf(Arsenal.Wheel, Weapon);
        Weapon = Arsenal.Wheel[(at + direction + Arsenal.Wheel.Length) % Arsenal.Wheel.Length];
    }

    // ---- Laying ink ----------------------------------------------------------------

    public void PenDown(Vec2 at)
    {
        if (!IsPlanning)
        {
            return;
        }

        // A fresh stroke replaces the old one. Ink only dries when the pen lifts, which is
        // what lets it be backed up mid-stroke.
        _penDown = true;
        Route.Clear();
        Extend(at);
    }

    public void PenUp()
    {
        _penDown = false;
        RebuildPreview();
    }

    public bool PenIsDown => _penDown;

    /// <summary>
    /// Lays or retracts ink. The pen may be backed up along its own stroke while it is still
    /// down, which is the design's one concession to a wobbly hand.
    /// </summary>
    public void Extend(Vec2 at)
    {
        if (!IsPlanning || !_penDown)
        {
            return;
        }

        if (Route.Count == 0)
        {
            Route.Add(at);
            RebuildPreview();
            return;
        }

        if (Route.Count >= 2
            && Vec2.Distance(at, Route[Route.Count - 2]) < PenStep / Fix64.FromInt(2))
        {
            Route.RemoveAt(Route.Count - 1);
            RebuildPreview();
            return;
        }

        if (Vec2.Distance(at, Route[Route.Count - 1]) > PenStep)
        {
            Route.Add(at);
            RebuildPreview();
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
    /// moment from wherever the ghost had got to.
    /// </summary>
    public void ReleaseAim()
    {
        if (!Aiming || !IsPlanning)
        {
            Aiming = false;
            return;
        }

        Aiming = false;
        Vec2 aim = AimAt - Muzzle;

        if (aim.LengthSquared() == Fix64.Zero)
        {
            return;
        }

        Fix64 reach = Fix64.Min(aim.Length(), FullPowerDrag);
        int power = Fix64.ToInt(reach / FullPowerDrag * Fix64.FromInt(byte.MaxValue));
        int tick = Preview?.TicksUsed ?? 0;

        if (tick >= MatchSettings.TicksPerRound)
        {
            tick = MatchSettings.TicksPerRound - 1;
        }

        Shot = PlanAction.Fire(
            tick, aim, (byte)(power < 20 ? 20 : power > byte.MaxValue ? byte.MaxValue : power));
    }

    /// <summary>
    /// Plants the charge wherever the ghost has got to, or picks it back up.
    /// </summary>
    /// <remarks>
    /// A toggle rather than a one-way commitment, because it costs nothing to change your mind
    /// about it while the ink is still wet and the reset token is far too precious to spend on
    /// a misplaced beet.
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

        int tick = Preview?.TicksUsed ?? 0;

        Charge = PlanAction.Dynamite(
            tick >= MatchSettings.TicksPerRound ? MatchSettings.TicksPerRound - 1 : tick);
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
    /// Tears up the whole turn. One free a turn, then only what the crates have handed over,
    /// and the free one goes first so a hoarded token is still a token afterwards.
    /// </summary>
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

        Route.Clear();
        Preview = null;
        Shot = null;
        Charge = null;
    }

    // ---- Committing ----------------------------------------------------------------

    /// <summary>
    /// Hands the plan to the simulation. Everything after this point is out of the player's
    /// hands, which is the point of the whole design.
    /// </summary>
    public void Commit()
    {
        if (Actor is null || Committed)
        {
            Committed = true;
            return;
        }

        RoutePoint[] route = new RoutePoint[Route.Count];

        for (int index = 0; index < Route.Count; index++)
        {
            route[index] = RoutePoint.FromWorld(Route[index]);
        }

        List<PlanAction> actions = new List<PlanAction>(2);

        if (Charge is not null)
        {
            actions.Add(Charge.Value);
        }

        if (Shot is not null)
        {
            actions.Add(Shot.Value);
        }

        _match.SubmitPlan(new Plan(Seat, Actor.Index, Weapon, route, actions.ToArray()));
        Committed = true;
    }

    private void RebuildPreview()
    {
        if (Actor is null || Route.Count == 0)
        {
            Preview = null;
            return;
        }

        Preview = GhostPreview.Walk(Actor, _match.Terrain, Route);
        GhostClock = 0;
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
