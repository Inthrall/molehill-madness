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
        private readonly MatchRng _rng;

        private MoleMatch(TerrainGrid terrain, int playerCount, ulong seed)
        {
            Terrain = terrain;
            PlayerCount = playerCount;
            _rng = new MatchRng(seed);
            _plans = new Plan?[playerCount];

            Vec2[] spawns = MapMaker.SpawnPoints(terrain, playerCount, MatchSettings.MolesPerPlatoon);
            _moles = new Mole[playerCount * MatchSettings.MolesPerPlatoon];

            for (int slot = 0; slot < _moles.Length; slot++)
            {
                // Interleaved rather than clustered, so no platoon starts boxed in.
                int seat = slot % playerCount;
                int index = slot / playerCount;
                _moles[slot] = new Mole(seat, index, spawns[slot]);
            }

            Round = 0;
            LavaLine = Fix64.MaxValue;
            LavaLeftEdge = Fix64.MinValue;
            LavaRightEdge = Fix64.MaxValue;
        }

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

            _plans[plan.Seat] = plan;
        }

        /// <summary>
        /// Runs the round: every plan replays together on one clock, with no initiative.
        /// </summary>
        public RoundResult ResolveRound()
        {
            Round++;
            RoundResult result = new RoundResult(Round);

            Mole?[] actors = new Mole?[PlayerCount];
            Vec2[]?[] routes = new Vec2[]?[PlayerCount];

            foreach (Mole mole in _moles)
            {
                mole.BeginRound();
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
                CheckLava(result);
            }

            Aftermath(actors, result);
            return result;
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
                        actor.AddImpulse(-Vec2.UnitY * HopSpeed);
                    }

                    break;

                case PlanActionKind.Brace:
                    // Bracing is holding still, which is also what a player who plans
                    // nothing does. Nothing else to it.
                    actor.WaypointIndex = int.MaxValue;
                    break;

                case PlanActionKind.Fire:
                    Use(actor, weapon, action, result);
                    break;

                case PlanActionKind.Dynamite:
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
                result.Hits.Add(new BlastHit(mole.Seat, mole.Index, spec.Damage, wentOffDuty));

                if (wentOffDuty)
                {
                    result.Knockouts.Add(new Knockout(mole.Seat, mole.Index, KnockoutCause.Explosion));
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
                         Terrain, _moles, actor.Position, spec, crater: false))
            {
                result.Hits.Add(hit);

                if (hit.WentOffDuty)
                {
                    result.Knockouts.Add(new Knockout(hit.Seat, hit.MoleIndex, KnockoutCause.Explosion));
                }
            }

            TerrainQuery.CollapseCavities(Terrain, actor.Position, spec.BlastRadius);
            Gush(actor.Position);
            result.Detonations++;
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
                        weapon, actor.Seat, actor.Position, Round + MatchSettings.TrapArmDelay, int.MaxValue));
                    break;

                case WeaponId.RootSnare:
                    // Live at once and gone after this round, so it costs its victim
                    // exactly one turn.
                    _placements.Add(new Placement(weapon, actor.Seat, actor.Position, Round, Round));
                    break;

                case WeaponId.GeyserCap:
                    _placements.Add(new Placement(weapon, actor.Seat, actor.Position, Round, int.MaxValue));
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

            foreach (BlastHit hit in Blast.Detonate(Terrain, _moles, at, WeaponTable.Of(weapon)))
            {
                if (hit.Seat == actor.Seat && hit.MoleIndex == actor.Index)
                {
                    continue;
                }

                result.Hits.Add(hit);

                if (hit.WentOffDuty)
                {
                    result.Knockouts.Add(new Knockout(hit.Seat, hit.MoleIndex, KnockoutCause.Explosion));
                }
            }

            result.Detonations++;
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
                                mole.Seat, mole.Index, spec.Damage, wentOffDuty));

                            if (wentOffDuty)
                            {
                                result.Knockouts.Add(new Knockout(
                                    mole.Seat, mole.Index, KnockoutCause.Explosion));
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

                result.Detonations++;
                SplitCluster(shot);

                foreach (BlastHit hit in Blast.Detonate(
                             Terrain, _moles, shot.Position, WeaponTable.Of(shot.Weapon)))
                {
                    result.Hits.Add(hit);

                    if (hit.WentOffDuty)
                    {
                        result.Knockouts.Add(
                            new Knockout(hit.Seat, hit.MoleIndex, KnockoutCause.Explosion));
                    }
                }
            }

            _shots.RemoveAll(shot => shot.HasDetonated);
        }

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
                    result.Knockouts.Add(new Knockout(mole.Seat, mole.Index, KnockoutCause.Lava));
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
                    result.Knockouts.Add(new Knockout(mole.Seat, mole.Index, KnockoutCause.Lava));
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

            RaiseLava();
            RollWind();
            DecideWinner(result);
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
        private static Fix64 HopSpeed => Fix64.FromInt(9);

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
