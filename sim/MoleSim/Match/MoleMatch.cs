using System;
using System.Collections.Generic;
using MoleSim.Numerics;
using MoleSim.Terrain;

namespace MoleSim.Match
{
    /// <summary>
    /// A match. Takes plans in, resolves rounds, and can be hashed.
    /// </summary>
    /// <remarks>
    /// This is the whole public surface of the game. A client renders what it sees here, a
    /// relay ferries the plans that go in, and the tools replay them. Nothing else may
    /// change any of it.
    ///
    /// Player count is a parameter rather than a pair of named seats, and every solver it
    /// calls treats seats symmetrically, so there is no initiative at any player count.
    /// </remarks>
    public sealed class MoleMatch
    {
        private readonly Mole[] _moles;
        private readonly Plan?[] _plans;
        private readonly List<Projectile> _shots = new List<Projectile>();
        private readonly List<Placement> _placements = new List<Placement>();
        private readonly List<Crate> _crates = new List<Crate>();
        private readonly int[][] _stock;
        private int _quietRounds;
        private Fix64 _staminaScale = Fix64.One;
        private readonly MatchRng _rng;

        private MoleMatch(TerrainGrid terrain, int playerCount, ulong seed)
        {
            Terrain = terrain;
            PlayerCount = playerCount;
            _rng = new MatchRng(seed);
            _plans = new Plan?[playerCount];

            Vec2[] spawns = MapMaker.SpawnPoints(terrain, playerCount, MatchSettings.MolesPerPlatoon);
            Vec2[] facings = MapMaker.SpawnFacings(terrain, playerCount, MatchSettings.MolesPerPlatoon);
            _moles = new Mole[playerCount * MatchSettings.MolesPerPlatoon];

            for (int slot = 0; slot < _moles.Length; slot++)
            {
                // Interleaved rather than clustered, so no platoon starts boxed in.
                int seat = slot % playerCount;
                int index = slot / playerCount;
                _moles[slot] = new Mole(seat, index, spawns[slot]) { Facing = facings[slot] };
            }

            _stock = new int[playerCount][];

            for (int seat = 0; seat < playerCount; seat++)
            {
                _stock[seat] = new int[WeaponCount];

                foreach (WeaponId weapon in AllWeapons)
                {
                    _stock[seat][(int)weapon] = WeaponTable.StartingStock(weapon);
                }
            }

            Round = 0;
            LavaLine = Fix64.MaxValue;
            LavaLeftEdge = Fix64.MinValue;
            LavaRightEdge = Fix64.MaxValue;
        }

        /// <summary>Enough room for every id in <see cref="WeaponId"/>.</summary>
        private const int WeaponCount = 16;

        private static readonly WeaponId[] AllWeapons =
            (WeaponId[])Enum.GetValues(typeof(WeaponId));

        /// <summary>Starts a match on generated ground.</summary>
        public static MoleMatch Create(int playerCount, ulong seed, int widthCells, int heightCells)
        {
            if (playerCount is < 2 or > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(playerCount), "A match is two to four players.");
            }

            return new MoleMatch(MapMaker.Field(widthCells, heightCells, seed), playerCount, seed);
        }

        /// <summary>Starts a match on ground somebody else built.</summary>
        public static MoleMatch Create(TerrainGrid terrain, int playerCount, ulong seed)
        {
            if (terrain is null)
            {
                throw new ArgumentNullException(nameof(terrain));
            }

            if (playerCount is < 2 or > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(playerCount), "A match is two to four players.");
            }

            return new MoleMatch(terrain, playerCount, seed);
        }

        public TerrainGrid Terrain { get; }

        public int PlayerCount { get; }

        /// <summary>Rounds resolved so far. The first round resolved is round one.</summary>
        public int Round { get; private set; }

        /// <summary>Wind for the round about to be planned, in metres per second.</summary>
        public Fix64 Wind { get; private set; }

        /// <summary>World height of the lava surface. Everything below it is gone.</summary>
        public Fix64 LavaLine { get; private set; }

        /// <summary>How far in the lava has crept from the left. Grows past halfway.</summary>
        public Fix64 LavaLeftEdge { get; private set; }

        public Fix64 LavaRightEdge { get; private set; }

        /// <summary>Every mole, in a fixed order: interleaved by seat, then by index.</summary>
        public IReadOnlyList<Mole> Moles => _moles;

        /// <summary>Projectiles currently in the air. Empty between rounds.</summary>
        public IReadOnlyList<Projectile> Shots => _shots;

        /// <summary>Traps, snares and vents lying about the map.</summary>
        public IReadOnlyList<Placement> Placements => _placements;

        /// <summary>Crates on their way down, or waiting to be claimed.</summary>
        public IReadOnlyList<Crate> Crates => _crates;

        /// <summary>
        /// What a round's stamina is multiplied by. Shrinks when nobody will fight.
        /// </summary>
        public Fix64 StaminaScale => _staminaScale;

        /// <summary>
        /// How many of a weapon a platoon has left, or <see cref="WeaponTable.Unlimited"/>.
        /// </summary>
        /// <remarks>
        /// Part of the match state and hashed with the rest of it, because what a platoon is
        /// holding decides what plans are legal, and two machines that disagree about that
        /// would disagree about whether a round even happened.
        /// </remarks>
        public int Stock(int seat, WeaponId weapon)
        {
            if (seat < 0 || seat >= PlayerCount)
            {
                return 0;
            }

            return _stock[seat][(int)weapon];
        }

        /// <summary>Whether a platoon could pick a weapon this turn.</summary>
        public bool CanUse(int seat, WeaponId weapon) =>
            weapon != WeaponId.None && Stock(seat, weapon) != 0;

        /// <summary>
        /// Adds to a platoon's holdings. What a crate does, and how a scenario is set up.
        /// </summary>
        public void Restock(int seat, WeaponId weapon, int count)
        {
            if (seat < 0 || seat >= PlayerCount || weapon == WeaponId.None || count <= 0)
            {
                return;
            }

            if (_stock[seat][(int)weapon] == WeaponTable.Unlimited)
            {
                return;
            }

            _stock[seat][(int)weapon] += count;
        }

        /// <summary>
        /// Takes one out of a platoon's holdings, at the moment the thing is actually used.
        /// </summary>
        /// <remarks>
        /// At use rather than at commit, and that distinction is the whole reason it is here
        /// and not in <see cref="SubmitPlan"/>. Damage ends a mole's input and deletes its
        /// unfired shot; charging it for ammunition it never got to throw would punish the
        /// same hit twice.
        /// </remarks>
        private void Spend(int seat, WeaponId weapon)
        {
            if (weapon == WeaponId.None || _stock[seat][(int)weapon] == WeaponTable.Unlimited)
            {
                return;
            }

            if (_stock[seat][(int)weapon] > 0)
            {
                _stock[seat][(int)weapon]--;
            }
        }

        /// <summary>
        /// Records a mole leaving, and picks the pratfall it leaves on.
        /// </summary>
        /// <remarks>
        /// Every knockout goes through here so the reel is chosen once, in the simulation,
        /// from information the client would not have. Doing it at each call site would
        /// guarantee that two of them eventually disagreed.
        /// </remarks>
        private void RecordKnockout(
            RoundResult result, int seat, int moleIndex, KnockoutCause cause, int damage, Fix64 shove)
        {
            Mole? mole = Find(seat, moleIndex);

            bool underground = mole is not null
                && MaterialTable.IsSolid(TerrainQuery.MaterialAt(
                    Terrain, mole.Position - (Vec2.UnitY * MatchSettings.Radius)));

            result.Knockouts.Add(new Knockout(
                seat,
                moleIndex,
                cause,
                KnockoutReel.Choose(cause, damage, shove, underground, _rng)));
        }

        /// <summary>Whether a seat still has anybody left.</summary>
        public bool SeatIsAlive(int seat)
        {
            foreach (Mole mole in _moles)
            {
                if (mole.Seat == seat && !mole.IsOffDuty)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The moles a seat may choose from this round.</summary>
        public IEnumerable<Mole> Eligible(int seat)
        {
            foreach (Mole mole in _moles)
            {
                if (mole.Seat == seat && !mole.IsOffDuty && !mole.HasActedThisCycle)
                {
                    yield return mole;
                }
            }
        }

        /// <summary>
        /// Accepts a plan for the coming round. Throws on anything illegal, which is the
        /// whole anti-cheat story for v1: a client can submit bad inputs, never bad state,
        /// and every client rejects the same bad inputs identically.
        /// </summary>
        public void SubmitPlan(Plan plan)
        {
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (plan.Seat < 0 || plan.Seat >= PlayerCount)
            {
                throw new InvalidPlanException($"Seat {plan.Seat} is not in this match.");
            }

            Mole? chosen = Find(plan.Seat, plan.MoleIndex);

            if (chosen is null)
            {
                throw new InvalidPlanException(
                    $"Seat {plan.Seat} has no mole {plan.MoleIndex}.");
            }

            if (chosen.IsOffDuty)
            {
                throw new InvalidPlanException("That mole is off duty.");
            }

            if (chosen.HasActedThisCycle)
            {
                throw new InvalidPlanException("That mole has already had its turn this cycle.");
            }

            foreach (PlanAction action in plan.Actions)
            {
                if (action.Tick >= MatchSettings.TicksPerRound)
                {
                    throw new InvalidPlanException(
                        $"An action is scheduled at tick {action.Tick}, past the end of the round.");
                }
            }

            if (CountOf(plan, PlanActionKind.Fire) > 1)
            {
                throw new InvalidPlanException("One shot per turn.");
            }

            // A plan naming something the platoon does not hold is an illegal input, not a
            // world that changed underneath it, so it is refused rather than degraded. This is
            // the whole anti-cheat story for v1: every client rejects the same bad inputs
            // identically, so a client can submit rubbish but never a state nobody else has.
            if (CountOf(plan, PlanActionKind.Fire) > 0 && !CanUse(plan.Seat, plan.Weapon))
            {
                throw new InvalidPlanException(
                    $"Seat {plan.Seat} has no {plan.Weapon} left.");
            }

            if (CountOf(plan, PlanActionKind.Dynamite) > 0
                && !CanUse(plan.Seat, WeaponId.BoomBeets))
            {
                throw new InvalidPlanException($"Seat {plan.Seat} has no Boom Beets left.");
            }

            _plans[plan.Seat] = plan;
        }

        /// <summary>
        /// Runs the round: every plan replays together on one clock, with no initiative.
        /// </summary>
        /// <param name="record">
        /// Whether to keep every tick so the round can be watched, replayed or turned into
        /// a clip. Off by default, because the headless runners and the corpus resolve
        /// thousands of rounds and have no use for it.
        /// </param>
        public RoundResult ResolveRound(bool record = false, ITickWatcher? watching = null)
        {
            Round++;
            RoundResult result = new RoundResult(Round);

            if (record)
            {
                result.Recording = new RoundRecording(
                    Round, _moles.Length, MatchSettings.TicksPerRound);

                Terrain.StartJournal(result.Recording.Journal);
            }

            Mole?[] actors = new Mole?[PlayerCount];
            Vec2[]?[] routes = new Vec2[]?[PlayerCount];

            foreach (Mole mole in _moles)
            {
                mole.BeginRound();
                mole.Stamina *= _staminaScale;
            }

            for (int seat = 0; seat < PlayerCount; seat++)
            {
                Plan? plan = _plans[seat];

                if (plan is null)
                {
                    continue;
                }

                Mole? actor = Find(seat, plan.MoleIndex);

                if (actor is null || actor.IsOffDuty)
                {
                    continue;
                }

                actors[seat] = actor;
                routes[seat] = plan.ToWorldRoute();
            }

            for (int tick = 0; tick < MatchSettings.TicksPerRound; tick++)
            {
                FireScheduledActions(tick, actors, result);
                MoveEverybody(actors, routes);
                AdvanceShots(result);
                CheckPlacements(result);
                LandAndClaimCrates(tick, result);
                CheckLava(result);

                result.Recording?.Capture(
                    tick, _moles, _shots, result.Hits.Count, result.Knockouts.Count,
                    result.Detonations);

                // After everything has moved, so a tick's hash is the state at the end of it.
                watching?.Ticked(Round, tick, this);
            }

            Terrain.StopJournal();
            Aftermath(actors, result);
            return result;
        }

        /// <summary>
        /// Something that wants to see every tick as it happens.
        /// </summary>
        /// <remarks>
        /// Only the divergence bisector uses this, and it exists because a round is atomic from the
        /// outside: ResolveRound goes in and a result comes out, so the finest grain anything could
        /// compare was a whole round. When two platforms disagree, a round is 240 ticks of somewhere
        /// to look, and the plan is blunt about what that costs, calling determinism debugging
        /// without a bisector "despair".
        ///
        /// An interface rather than a delegate so that MoleSim keeps its own company: nothing here
        /// needs System, and the shape of what an observer wants to record is the observer's problem.
        ///
        /// Handed the match rather than a snapshot, deliberately. A hash says two machines disagree
        /// and nothing about what they disagree over, so a bisector has to be able to read the state
        /// itself, and copying it out first would mean deciding here what is worth copying.
        /// </remarks>
        public interface ITickWatcher
        {
            void Ticked(int round, int tick, MoleMatch match);
        }

        /// <summary>
        /// A hash of everything that matters. Two machines that disagree here have
        /// diverged, and online play between them is finished.
        /// </summary>
        public ulong StateHash()
        {
            unchecked
            {
                ulong hash = 0xCBF29CE484222325UL;
                hash = Fold(hash, Terrain.Hash);
                hash = Fold(hash, (ulong)Round);
                hash = Fold(hash, (ulong)Wind.Raw);
                hash = Fold(hash, (ulong)LavaLine.Raw);

                foreach (Mole mole in _moles)
                {
                    hash = Fold(hash, (ulong)mole.Position.X.Raw);
                    hash = Fold(hash, (ulong)mole.Position.Y.Raw);
                    hash = Fold(hash, (ulong)mole.Velocity.X.Raw);
                    hash = Fold(hash, (ulong)mole.Velocity.Y.Raw);
                    hash = Fold(hash, (ulong)mole.Pluck);
                    hash = Fold(hash, (ulong)mole.Stamina.Raw);
                    hash = Fold(hash, (ulong)mole.LavaStrikes);
                    hash = Fold(hash, mole.IsOffDuty ? 1UL : 0UL);
                }

                // What each platoon is holding decides which plans are legal, so two machines
                // that disagree about it would disagree about whether a round happened at all.
                for (int seat = 0; seat < PlayerCount; seat++)
                {
                    for (int weapon = 0; weapon < WeaponCount; weapon++)
                    {
                        hash = Fold(hash, (ulong)(long)_stock[seat][weapon]);
                    }
                }

                return hash;
            }
        }

        // ---- Round internals ----------------------------------------------------------

        private void FireScheduledActions(int tick, Mole?[] actors, RoundResult result)
        {
            for (int seat = 0; seat < PlayerCount; seat++)
            {
                Mole? actor = actors[seat];
                Plan? plan = _plans[seat];

                if (actor is null || plan is null)
                {
                    continue;
                }

                // Taking damage tears up the rest of the recording, unfired shot included.
                // Hitting somebody before their firing tick deletes their shot outright,
                // which is the deepest read in the game and falls out of two rules rather
                // than a system.
                if (!actor.AcceptsInput)
                {
                    continue;
                }

                foreach (PlanAction action in plan.Actions)
                {
                    if (action.Tick != tick)
                    {
                        continue;
                    }

                    Perform(action, actor, plan.Weapon, result);
                }
            }
        }

        private void Perform(PlanAction action, Mole actor, WeaponId weapon, RoundResult result)
        {
            switch (action.Kind)
            {
                case PlanActionKind.Hop:
                    if (!actor.IsAirborne)
                    {
                        actor.AddImpulse(-Vec2.UnitY * MatchSettings.HopSpeed);
                    }

                    break;

                case PlanActionKind.Fire:
                    if (!CanUse(actor.Seat, weapon))
                    {
                        break;
                    }

                    Spend(actor.Seat, weapon);
                    Use(actor, weapon, action, result);
                    break;

                case PlanActionKind.Dynamite:
                    if (!CanUse(actor.Seat, WeaponId.BoomBeets))
                    {
                        break;
                    }

                    Spend(actor.Seat, WeaponId.BoomBeets);
                    Plant(actor, WeaponId.BoomBeets);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// The aim the shot actually leaves on, which is not always the one that was
        /// committed.
        /// </summary>
        /// <remarks>
        /// A mole with its feet on the ground fires exactly where it was pointed. A mole
        /// in the air fires relative to the way it is tumbling, because it is holding the
        /// thing rather than the thing holding itself level. Getting punted off a ledge
        /// before your firing tick no longer merely moves the shot, it turns it, which is
        /// a great deal funnier and gives knockback a second use.
        /// </remarks>
        private static Vec2 AimOf(Mole actor, PlanAction action)
        {
            Vec2 aim = action.AimDirection();

            return actor.IsAirborne ? aim.RotatedBy(actor.Facing) : aim;
        }

        private void Use(Mole actor, WeaponId weapon, PlanAction action, RoundResult result)
        {
            WeaponSpec spec = WeaponTable.Of(weapon);

            switch (spec.Kind)
            {
                case WeaponKind.Thrown:
                    Launch(actor, weapon, action);
                    break;

                case WeaponKind.FromTheSky:
                    CallItIn(actor, weapon, action);
                    break;

                case WeaponKind.Planted:
                    Plant(actor, weapon);
                    break;

                case WeaponKind.Melee:
                    Swing(actor, weapon, action, result);
                    break;

                case WeaponKind.Seismic:
                    Frack(actor, weapon, result);
                    break;

                case WeaponKind.Tool:
                    UseTool(actor, weapon, action, result);
                    break;

                default:
                    break;
            }
        }

        private void Launch(Mole actor, WeaponId weapon, PlanAction action)
        {
            WeaponSpec spec = WeaponTable.Of(weapon);
            Vec2 aim = AimOf(actor, action);

            if (aim.LengthSquared() == Fix64.Zero)
            {
                return;
            }

            // Started clear of the body, so a shot cannot detonate inside the mole that
            // fired it on the way out.
            Vec2 muzzle = actor.Position + (aim * (MatchSettings.Radius + Projectile.Radius + WorldScale.CellSize));
            Vec2 velocity = aim * (spec.LaunchSpeed * action.PowerFraction());

            _shots.Add(new Projectile(weapon, actor.Seat, actor.Index, muzzle, velocity));
        }

        private void Plant(Mole actor, WeaponId weapon)
        {
            _shots.Add(new Projectile(weapon, actor.Seat, actor.Index, actor.Position, Vec2.Zero));
        }

        /// <summary>
        /// Drops something on a spot rather than throwing it there. Aim and power name the
        /// spot; the thing itself arrives from above, which is why anybody underground is
        /// simply out of reach.
        /// </summary>
        private void CallItIn(Mole actor, WeaponId weapon, PlanAction action)
        {
            Vec2 aim = AimOf(actor, action);

            if (aim.LengthSquared() == Fix64.Zero)
            {
                return;
            }

            Vec2 target = actor.Position
                + (aim * (MatchSettings.SkyTargetRange * action.PowerFraction()));

            WeaponSpec spec = WeaponTable.Of(weapon);
            int count = spec.ClusterCount > 0 ? spec.ClusterCount : 1;

            for (int index = 0; index < count; index++)
            {
                Fix64 offset = MatchSettings.SkySpread
                    * Fix64.FromInt(index - ((count - 1) / 2));

                _shots.Add(new Projectile(
                    weapon,
                    actor.Seat,
                    actor.Index,
                    new Vec2(target.X + offset, target.Y - MatchSettings.SkyDropHeight),
                    Vec2.Zero));
            }
        }

        /// <summary>
        /// The Big Whack. No projectile at all: either somebody is standing there at the
        /// moment of the swing or the mallet meets thin air.
        /// </summary>
        private void Swing(Mole actor, WeaponId weapon, PlanAction action, RoundResult result)
        {
            WeaponSpec spec = WeaponTable.Of(weapon);
            Vec2 aim = AimOf(actor, action);
            Vec2 struckAt = actor.Position + (aim * MatchSettings.MeleeReach);

            foreach (Mole mole in _moles)
            {
                if (mole.IsOffDuty || mole == actor)
                {
                    continue;
                }

                if (Vec2.Distance(mole.Position, struckAt) > MatchSettings.MeleeReach)
                {
                    continue;
                }

                bool wentOffDuty = mole.TakeDamage(spec.Damage);
                mole.AddImpulse(aim * spec.Knockback);
                result.Hits.Add(new BlastHit(
                    mole.Seat, mole.Index, spec.Damage, wentOffDuty, actor.Seat, actor.Index));

                if (wentOffDuty)
                {
                    RecordKnockout(
                        result, mole.Seat, mole.Index, KnockoutCause.Melee, spec.Damage, spec.Knockback);
                }
            }
        }

        /// <summary>
        /// Fracking: a shock through the soil, tunnels caving in, and a gusher that throws
        /// whatever is above the bore straight up into the open.
        /// </summary>
        private void Frack(Mole actor, WeaponId weapon, RoundResult result)
        {
            WeaponSpec spec = WeaponTable.Of(weapon);

            // The shock reaches through dirt, which nothing else in the arsenal does, and
            // is the whole reason this exists. It does not crater: a seismic weapon shakes
            // the ground rather than removing it, and cratering here would blow the roof
            // off the very tunnels it is supposed to bring down.
            foreach (BlastHit hit in Blast.Detonate(
                         Terrain, _moles, actor.Position, spec, crater: false,
                         bySeat: actor.Seat, byMoleIndex: actor.Index))
            {
                result.Hits.Add(hit);

                if (hit.WentOffDuty)
                {
                    RecordKnockout(
                        result, hit.Seat, hit.MoleIndex, KnockoutCause.Seismic, hit.Damage, spec.Knockback);
                }
            }

            TerrainQuery.CollapseCavities(Terrain, actor.Position, spec.BlastRadius);
            Gush(actor.Position);
            result.Blasts.Add(new Detonation(actor.Position, spec.BlastRadius));
        }

        /// <summary>Throws everything in a narrow column straight up.</summary>
        private void Gush(Vec2 origin)
        {
            foreach (Mole mole in _moles)
            {
                if (mole.IsOffDuty)
                {
                    continue;
                }

                if (Fix64.Abs(mole.Position.X - origin.X) > MatchSettings.GusherHalfWidth)
                {
                    continue;
                }

                // Only what is above the bore, and only within reach of the column.
                if (mole.Position.Y > origin.Y
                    || origin.Y - mole.Position.Y > MatchSettings.SkyDropHeight)
                {
                    continue;
                }

                mole.Velocity = Vec2.Zero;
                mole.AddImpulse(-Vec2.UnitY * MatchSettings.GusherSpeed);
            }
        }

        private void UseTool(Mole actor, WeaponId weapon, PlanAction action, RoundResult result)
        {
            switch (weapon)
            {
                case WeaponId.PowerClaws:
                    actor.DiggingIsCheap = true;
                    break;

                case WeaponId.Sandbag:
                    // Counts as loose soil, so it is cheap for anybody to dig back out.
                    Terrain.DepositCircle(
                        WorldScale.ToCell(actor.Position.X),
                        WorldScale.ToCell(actor.Position.Y) + 2,
                        Fix64.FloorToInt(WeaponTable.Of(weapon).BlastRadius / WorldScale.CellSize),
                        Material.LooseSoil);
                    break;

                case WeaponId.SnapTrap:
                    _placements.Add(new Placement(
                        weapon, actor.Seat, actor.Position, Round, action.Tick,
                        Round + MatchSettings.TrapArmDelay, int.MaxValue));
                    break;

                case WeaponId.RootSnare:
                    // Live at once and gone after this round, so it costs its victim
                    // exactly one turn.
                    _placements.Add(new Placement(
                        weapon, actor.Seat, actor.Position, Round, action.Tick, Round, Round));
                    break;

                case WeaponId.GeyserCap:
                    _placements.Add(new Placement(
                        weapon, actor.Seat, actor.Position, Round, action.Tick, Round, int.MaxValue));
                    break;

                case WeaponId.TunnelTorpedo:
                    Drill(actor, weapon, action, result);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Drills a straight line through dirt and bowls over whatever is where it
        /// surfaces. The mole-est move in the game.
        /// </summary>
        private void Drill(Mole actor, WeaponId weapon, PlanAction action, RoundResult result)
        {
            Vec2 aim = AimOf(actor, action);

            if (aim.LengthSquared() == Fix64.Zero)
            {
                return;
            }

            Fix64 stride = WorldScale.CellSize * Fix64.FromInt(2);
            Fix64 travelled = Fix64.Zero;
            Vec2 at = actor.Position;

            while (travelled < MatchSettings.TorpedoRange)
            {
                Vec2 next = at + (aim * stride);

                // Carve first, then see whether anything is still in the way. Asking
                // whether the material at the body's centre is diggable would stop the
                // drill before it started, because a mole standing on the surface has open
                // air at its centre: the same mistake the walking solver made once already.
                TerrainQuery.CarveBody(Terrain, next, MatchSettings.Radius);

                if (TerrainQuery.IsBlocked(Terrain, next, MatchSettings.Radius))
                {
                    // Bedrock, which is the one thing that stops a torpedo.
                    break;
                }

                at = next;
                travelled += stride;
            }

            actor.Position = at;
            actor.Facing = aim;

            foreach (BlastHit hit in Blast.Detonate(
                         Terrain, _moles, at, WeaponTable.Of(weapon),
                         bySeat: actor.Seat, byMoleIndex: actor.Index))
            {
                if (hit.Seat == actor.Seat && hit.MoleIndex == actor.Index)
                {
                    continue;
                }

                result.Hits.Add(hit);

                if (hit.WentOffDuty)
                {
                    RecordKnockout(
                        result, hit.Seat, hit.MoleIndex, KnockoutCause.Explosion,
                        hit.Damage, WeaponTable.Of(weapon).Knockback);
                }
            }

            result.Blasts.Add(new Detonation(at, WeaponTable.Of(weapon).BlastRadius));
        }

        /// <summary>
        /// Runs the traps, snares and vents lying about the map.
        /// </summary>
        private void CheckPlacements(RoundResult result)
        {
            foreach (Placement placement in _placements)
            {
                if (!placement.IsArmed(Round))
                {
                    continue;
                }

                WeaponSpec spec = WeaponTable.Of(placement.Weapon);

                foreach (Mole mole in _moles)
                {
                    if (mole.IsOffDuty)
                    {
                        continue;
                    }

                    if (Vec2.Distance(mole.Position, placement.Position) > spec.BlastRadius)
                    {
                        continue;
                    }

                    switch (placement.Weapon)
                    {
                        case WeaponId.SnapTrap:
                            placement.Spent = true;
                            bool wentOffDuty = mole.TakeDamage(spec.Damage);
                            mole.AddImpulse(-Vec2.UnitY * spec.Knockback);
                            result.Hits.Add(new BlastHit(
                                mole.Seat, mole.Index, spec.Damage, wentOffDuty,
                                placement.OwnerSeat, -1));

                            if (wentOffDuty)
                            {
                                RecordKnockout(
                                    result, mole.Seat, mole.Index, KnockoutCause.Trap,
                                    spec.Damage, spec.Knockback);
                            }

                            break;

                        case WeaponId.RootSnare:
                            mole.IsSnared = true;
                            break;

                        case WeaponId.GeyserCap:
                            if (!mole.IsAirborne)
                            {
                                mole.AddImpulse(-Vec2.UnitY * spec.Knockback);
                            }

                            break;

                        default:
                            break;
                    }
                }
            }
        }

        private void MoveEverybody(Mole?[] actors, Vec2[]?[] routes)
        {
            foreach (Mole mole in _moles)
            {
                Vec2[]? route = null;

                if (actors[mole.Seat] == mole)
                {
                    route = routes[mole.Seat];
                }

                MoleMotion.Step(mole, Terrain, route);
            }
        }

        private void AdvanceShots(RoundResult result)
        {
            // Iterated by index because detonations add to the list.
            for (int index = 0; index < _shots.Count; index++)
            {
                Projectile shot = _shots[index];

                if (shot.HasDetonated)
                {
                    continue;
                }

                if (!ProjectileMotion.Step(shot, Terrain, _moles, Wind))
                {
                    continue;
                }

                result.Blasts.Add(
                    new Detonation(shot.Position, WeaponTable.Of(shot.Weapon).BlastRadius));
                SplitCluster(shot);

                foreach (BlastHit hit in Blast.Detonate(
                             Terrain, _moles, shot.Position, WeaponTable.Of(shot.Weapon),
                             bySeat: shot.OwnerSeat, byMoleIndex: shot.OwnerMole))
                {
                    result.Hits.Add(hit);

                    if (hit.WentOffDuty)
                    {
                        RecordKnockout(
                            result, hit.Seat, hit.MoleIndex, KnockoutCause.Explosion,
                            hit.Damage, WeaponTable.Of(shot.Weapon).Knockback);
                    }
                }
            }

            _shots.RemoveAll(shot => shot.HasDetonated);
        }

        /// <summary>
        /// Drops the telegraphed crates in mid-round, and hands them out.
        /// </summary>
        /// <remarks>
        /// They arrive halfway through the round rather than at the start, so the scramble
        /// happens with everybody already committed to a plan drawn before they knew who
        /// else was going for it.
        /// </remarks>
        private void LandAndClaimCrates(int tick, RoundResult result)
        {
            foreach (Crate crate in _crates)
            {
                if (crate.Gone)
                {
                    continue;
                }

                if (!crate.HasLanded)
                {
                    if (tick < CrateLandingTick)
                    {
                        continue;
                    }

                    // Nothing is carved. A crate used to punch a small hole on the way in, so
                    // the last stretch had to be dug rather than strolled up to, and that was
                    // right while a crate buried itself a metre down. It is actively wrong now
                    // that one rests on a ledge: the hole is fourteen cells in the radius, centred
                    // on the crate, whose floor is only six cells under it, so a landing crate
                    // removed eight cells of the ground it was sitting on and fourteen either
                    // side. It blew away its own ledge, dropped whoever was waiting there out of
                    // reach, and left the crate hanging over a fresh crater.
                    //
                    // There is nothing left for a carve to do. The landing spot is already a ledge
                    // with sixteen cells of headroom over it, so the crate arrives in open air.
                    crate.HasLanded = true;
                }

                Claim(crate, result);
            }
        }

        /// <summary>
        /// Works out who, if anybody, gets a crate this tick.
        /// </summary>
        /// <remarks>
        /// One arrival takes it. Two arriving on the same tick split it. Three or four
        /// arriving at once tear it apart and nobody gets anything, which is deterministic
        /// and, as the design puts it, correct.
        /// </remarks>
        private void Claim(Crate crate, RoundResult result)
        {
            List<Mole> arrivals = new List<Mole>();

            foreach (Mole mole in _moles)
            {
                if (mole.IsOffDuty)
                {
                    continue;
                }

                if (Vec2.Distance(mole.Position, crate.Position) <= Crate.ReachRadius)
                {
                    arrivals.Add(mole);
                }
            }

            if (arrivals.Count == 0)
            {
                return;
            }

            crate.Gone = true;

            if (arrivals.Count >= 3)
            {
                result.CrateClaims.Add(new CrateClaim(-1, -1, crate.Contents, shattered: true));
                return;
            }

            CrateContents share = arrivals.Count == 2 ? crate.Contents.Halved() : crate.Contents;

            foreach (Mole winner in arrivals)
            {
                Award(winner, share);
                result.CrateClaims.Add(
                    new CrateClaim(winner.Seat, winner.Index, share, shattered: false));
            }
        }

        private void Award(Mole winner, CrateContents contents)
        {
            switch (contents.Kind)
            {
                case CrateKind.Grub:
                    winner.Pluck = Math.Min(
                        MatchSettings.StartingPluck, winner.Pluck + contents.Amount);
                    break;

                case CrateKind.ResetToken:
                    winner.ResetTokens += contents.Amount;
                    break;

                default:
                    // Weapons and dynamite, straight into the platoon's holdings. This is the
                    // other half of the crate loop: everything but the Clod Lobber runs out,
                    // so a telegraphed crate is worth crossing the map for.
                    Restock(winner.Seat, contents.Weapon, contents.Amount);
                    break;
            }
        }

        /// <summary>Halfway through the round, at four seconds.</summary>
        private const int CrateLandingTick = MatchSettings.TicksPerRound / 2;

        /// <summary>
        /// Splits a cluster charge into its pieces as it goes off, thrown outward and
        /// upward so they scatter rather than landing on the same spot.
        /// </summary>
        private void SplitCluster(Projectile shot)
        {
            WeaponSpec spec = WeaponTable.Of(shot.Weapon);

            // Only the ones launched from a mole split on the way down. Anything called in
            // from the sky arrived as a group already.
            if (spec.ClusterCount <= 0 || spec.Kind != WeaponKind.Thrown)
            {
                return;
            }

            for (int index = 0; index < spec.ClusterCount; index++)
            {
                Fix64 sideways = MatchSettings.ClusterSpread
                    * Fix64.FromInt(index - ((spec.ClusterCount - 1) / 2));

                _shots.Add(new Projectile(
                    WeaponTable.AcornShard,
                    shot.OwnerSeat,
                    shot.OwnerMole,
                    shot.Position,
                    new Vec2(sideways, -MatchSettings.ClusterSpread)));
            }
        }

        private void CheckLava(RoundResult result)
        {
            if (LavaLine == Fix64.MaxValue)
            {
                return;
            }

            foreach (Mole mole in _moles)
            {
                if (mole.IsOffDuty)
                {
                    continue;
                }

                bool touching =
                    mole.Position.Y + MatchSettings.Radius >= LavaLine
                    || mole.Position.X - MatchSettings.Radius <= LavaLeftEdge
                    || mole.Position.X + MatchSettings.Radius >= LavaRightEdge;

                if (!touching)
                {
                    continue;
                }

                mole.LavaStrikes++;

                if (mole.LavaStrikes > MatchSettings.LavaStrikesAllowed)
                {
                    // The third landing is the knockout, whatever pluck is left.
                    mole.TakeDamage(mole.Pluck);
                    RecordKnockout(
                        result, mole.Seat, mole.Index, KnockoutCause.Lava,
                        MatchSettings.LavaBounceDamage, Fix64.Zero);
                    continue;
                }

                // Trampolined back out on a burst of steam, yelping. The damage ends its
                // recording, so being knocked toward the lava also ends the turn.
                bool wentOffDuty = mole.TakeDamage(MatchSettings.LavaBounceDamage);
                mole.Velocity = Vec2.Zero;
                mole.AddImpulse(-Vec2.UnitY * MatchSettings.LavaBounceSpeed);
                mole.Position = new Vec2(mole.Position.X, LavaLine - MatchSettings.Radius - WorldScale.CellSize);

                result.Hits.Add(new BlastHit(
                    mole.Seat, mole.Index, MatchSettings.LavaBounceDamage, wentOffDuty));

                if (wentOffDuty)
                {
                    RecordKnockout(
                        result, mole.Seat, mole.Index, KnockoutCause.Lava,
                        MatchSettings.LavaBounceDamage, Fix64.Zero);
                }
            }
        }

        private void Aftermath(Mole?[] actors, RoundResult result)
        {
            foreach (Mole? actor in actors)
            {
                if (actor is not null)
                {
                    actor.HasActedThisCycle = true;
                }
            }

            // Once everybody still standing has had a go, the rotation starts again.
            for (int seat = 0; seat < PlayerCount; seat++)
            {
                bool anyLeft = false;

                foreach (Mole mole in _moles)
                {
                    if (mole.Seat == seat && !mole.IsOffDuty && !mole.HasActedThisCycle)
                    {
                        anyLeft = true;
                        break;
                    }
                }

                if (anyLeft)
                {
                    continue;
                }

                foreach (Mole mole in _moles)
                {
                    if (mole.Seat == seat)
                    {
                        mole.HasActedThisCycle = false;
                    }
                }
            }

            Array.Clear(_plans, 0, _plans.Length);
            _shots.Clear();
            _placements.RemoveAll(
                placement => placement.Spent || Round > placement.ExpiresAfterRound);

            NudgeIfNobodyWillFight(result);
            RaiseLava();
            RollWind();
            TelegraphNextCrates(result);
            DecideWinner(result);
        }

        /// <summary>
        /// Shrinks everybody's legs when nobody will fight.
        /// </summary>
        /// <remarks>
        /// Simultaneous turns have an obvious failure mode: four players in four bunkers,
        /// each waiting for somebody else to blink. Three rounds in a row with nothing
        /// happening anywhere on the field costs every platoon a tenth of its stamina, so
        /// bunkers gradually become too expensive to maintain.
        ///
        /// Gentle, thematic, and it only bites a table that is collectively refusing to
        /// play: one shot fired anywhere resets the count. It does not recover, which is
        /// deliberate. A stalemate that has already cost you something is one you have a
        /// reason to break.
        /// </remarks>
        private void NudgeIfNobodyWillFight(RoundResult result)
        {
            if (result.TotalDamage > 0)
            {
                _quietRounds = 0;
                return;
            }

            _quietRounds++;

            if (_quietRounds < QuietRoundsTolerated)
            {
                return;
            }

            _quietRounds = 0;
            _staminaScale *= Fix64.Ratio(9, 10);
            result.StalemateNudged = true;
        }

        /// <summary>Rounds of nothing happening before the nudge lands.</summary>
        private const int QuietRoundsTolerated = 3;

        /// <summary>
        /// Announces where the next crates will come down, so the fight over them is
        /// something everybody scheduled in advance rather than a surprise.
        /// </summary>
        private void TelegraphNextCrates(RoundResult result)
        {
            _crates.Clear();

            foreach (Crate crate in CrateSpawner.Telegraph(Terrain, _moles, PlayerCount, _rng))
            {
                _crates.Add(crate);
                result.NextCrates.Add(new CrateTelegraph(crate.Position));
            }
        }

        /// <summary>
        /// Lava arrives at a fixed round and climbs a step every round after, then closes
        /// in from both sides once it is past halfway. A height and two insets: no flow to
        /// simulate, no cooling state, nothing to tune but the step.
        /// </summary>
        private void RaiseLava()
        {
            if (Round < MatchSettings.BoilingPointRound)
            {
                return;
            }

            Fix64 mapBottom = WorldScale.ToMetres(Terrain.Height);
            int steps = Round - MatchSettings.BoilingPointRound;
            LavaLine = mapBottom - (MatchSettings.LavaRisePerRound * Fix64.FromInt(steps));

            Fix64 halfway = mapBottom - (WorldScale.ToMetres(Terrain.Height) / Fix64.FromInt(2));

            if (LavaLine > halfway)
            {
                return;
            }

            int closingSteps = Fix64.ToInt(
                (halfway - LavaLine) / MatchSettings.LavaRisePerRound) + 1;
            Fix64 inset = MatchSettings.LavaClosePerRound * Fix64.FromInt(closingSteps);

            LavaLeftEdge = inset;
            LavaRightEdge = WorldScale.ToMetres(Terrain.Width) - inset;
        }

        private void RollWind()
        {
            Wind = _rng.NextFix64(-MatchSettings.MaxWindSpeed, MatchSettings.MaxWindSpeed);
        }

        private void DecideWinner(RoundResult result)
        {
            int alive = 0;
            int lastAlive = -1;

            for (int seat = 0; seat < PlayerCount; seat++)
            {
                if (!SeatIsAlive(seat))
                {
                    continue;
                }

                alive++;
                lastAlive = seat;
            }

            if (alive > 1)
            {
                return;
            }

            result.MatchOver = true;
            result.WinningSeat = alive == 1 ? lastAlive : -1;
        }

        // ---- Helpers ------------------------------------------------------------------

        private Mole? Find(int seat, int index)
        {
            foreach (Mole mole in _moles)
            {
                if (mole.Seat == seat && mole.Index == index)
                {
                    return mole;
                }
            }

            return null;
        }

        private static int CountOf(Plan plan, PlanActionKind kind)
        {
            int count = 0;

            foreach (PlanAction action in plan.Actions)
            {
                if (action.Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Upward kick a hop gives.</summary>


        private static ulong Fold(ulong hash, ulong value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 0x100000001B3UL;
                return hash ^ (hash >> 29);
            }
        }
    }

    /// <summary>Thrown when a plan asks for something the rules do not allow.</summary>
    public sealed class InvalidPlanException : Exception
    {
        public InvalidPlanException(string message)
            : base(message)
        {
        }

        public InvalidPlanException()
        {
        }

        public InvalidPlanException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
