using MoleSim.Numerics;

namespace MoleSim.Match
{
    /// <summary>One mole, and everything true about it right now.</summary>
    public sealed class Mole
    {
        public Mole(int seat, int index, Vec2 position)
        {
            Seat = seat;
            Index = index;
            Position = position;
            Pluck = MatchSettings.StartingPluck;
            Stamina = Fix64.FromInt(MatchSettings.StartingStamina);
        }

        /// <summary>Which platoon it belongs to.</summary>
        public int Seat { get; }

        /// <summary>Its place in that platoon, which is also its name for the whole match.</summary>
        public int Index { get; }

        public Vec2 Position { get; set; }

        public Vec2 Velocity { get; set; }

        /// <summary>Health. At zero the mole goes off duty; nobody dies.</summary>
        public int Pluck { get; set; }

        /// <summary>Movement budget for this round, refilled at the start of every one.</summary>
        public Fix64 Stamina { get; set; }

        /// <summary>Out of the match, comically and harmlessly.</summary>
        public bool IsOffDuty { get; set; }

        /// <summary>
        /// Falling or flying rather than in contact with the ground. Route following is
        /// suspended while this is true, and resumes on landing, which is how a mole that
        /// walks into a fresh crater falls in, climbs out and carries on.
        /// </summary>
        public bool IsAirborne { get; set; }

        /// <summary>
        /// Lava touches survived. The third landing is the knockout rather than a bounce.
        /// </summary>
        public int LavaStrikes { get; set; }

        /// <summary>
        /// Set when this mole takes damage of any kind, which tears up the rest of its
        /// recording exactly as in the original: movement stops, an unfired shot is
        /// cancelled, and physics decides the remainder of its eight seconds.
        /// </summary>
        public bool InputCancelled { get; set; }

        /// <summary>Whether it has taken its turn in the current rotation.</summary>
        public bool HasActedThisCycle { get; set; }

        /// <summary>Next waypoint on the route being walked. Round-scoped.</summary>
        public int WaypointIndex { get; set; }

        /// <summary>
        /// Consecutive ticks spent getting no closer to the current waypoint. Round-scoped.
        /// </summary>
        public int StalledTicks { get; set; }

        /// <summary>Power Claws: this turn, dirt costs what open ground costs. Round-scoped.</summary>
        public bool DiggingIsCheap { get; set; }

        /// <summary>
        /// Caught in a Root Snare: half speed, and no digging at all. Round-scoped, so a
        /// snare costs its victim exactly one turn.
        /// </summary>
        public bool IsSnared { get; set; }

        /// <summary>
        /// Which way the mole is pointing. On the ground that is the way it last walked;
        /// in the air it is the way it is travelling, which is what makes a shot fired
        /// mid-tumble go somewhere its owner did not choose.
        /// </summary>
        public Vec2 Facing { get; set; } = Vec2.UnitX;

        /// <summary>True while the mole can still be steered by its plan.</summary>
        public bool AcceptsInput => !IsOffDuty && !InputCancelled;

        /// <summary>Called at the start of every round, before plans are applied.</summary>
        public void BeginRound()
        {
            Stamina = Fix64.FromInt(MatchSettings.StartingStamina);
            InputCancelled = false;
            WaypointIndex = 0;
            StalledTicks = 0;
            DiggingIsCheap = false;
            IsSnared = false;
        }

        /// <summary>
        /// Applies damage and ends this mole's go. Returns true if it went off duty.
        /// </summary>
        /// <remarks>
        /// Every route into damage comes through here, so the rule that being hit ends
        /// your turn cannot be forgotten at one call site and honoured at another.
        /// </remarks>
        public bool TakeDamage(int amount)
        {
            if (IsOffDuty)
            {
                return false;
            }

            InputCancelled = true;
            Pluck -= amount;

            if (Pluck > 0)
            {
                return false;
            }

            Pluck = 0;
            IsOffDuty = true;
            return true;
        }

        /// <summary>Adds an impulse, capped so a chain of blasts cannot fling a mole off the map.</summary>
        public void AddImpulse(Vec2 impulse)
        {
            Velocity = (Velocity + impulse).WithMaxLength(MatchSettings.TerminalSpeed);
            IsAirborne = true;
        }
    }
}
