using System;

namespace MoleSim.Match
{
    /// <summary>
    /// Turns a plan into bytes and back. This is the only thing that goes over the wire.
    /// </summary>
    /// <remarks>
    /// Hand-packed little-endian rather than any general-purpose serializer, for three
    /// reasons: the layout is then something a person can read off the page and reason
    /// about; it is identical on every platform without depending on a library's opinions;
    /// and it is small. A forty-point route with four actions comes out around two hundred
    /// bytes, comfortably inside the design's one-kilobyte budget, which is what lets a
    /// whole four-player match be stored as a seed and a list of these.
    ///
    /// The version byte leads, and a newer version is rejected loudly rather than
    /// misinterpreted. A relay never reads any of this; it stores opaque blobs.
    /// </remarks>
    public static class PlanCodec
    {
        /// <summary>Bytes in the fixed header.</summary>
        private const int HeaderBytes = 8;

        private const int RoutePointBytes = 4;

        private const int ActionBytes = 9;

        /// <summary>Sanity ceiling, so a corrupt length cannot ask for a huge allocation.</summary>
        private const int MaxRoutePoints = 4096;

        private const int MaxActions = 256;

        public static byte[] Write(Plan plan)
        {
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            byte[] buffer = new byte[
                HeaderBytes
                + (plan.Route.Length * RoutePointBytes)
                + (plan.Actions.Length * ActionBytes)];

            int at = 0;
            buffer[at++] = Plan.FormatVersion;
            buffer[at++] = (byte)plan.Seat;
            buffer[at++] = (byte)plan.MoleIndex;
            buffer[at++] = (byte)plan.Weapon;
            WriteUInt16(buffer, ref at, (ushort)plan.Route.Length);
            WriteUInt16(buffer, ref at, (ushort)plan.Actions.Length);

            foreach (RoutePoint point in plan.Route)
            {
                WriteInt16(buffer, ref at, point.CellX);
                WriteInt16(buffer, ref at, point.CellY);
            }

            foreach (PlanAction action in plan.Actions)
            {
                WriteUInt16(buffer, ref at, action.Tick);
                buffer[at++] = (byte)action.Kind;
                buffer[at++] = (byte)action.Weapon;
                buffer[at++] = action.Power;
                WriteInt16(buffer, ref at, action.AimX);
                WriteInt16(buffer, ref at, action.AimY);
            }

            return buffer;
        }

        public static Plan Read(byte[] bytes)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length < HeaderBytes)
            {
                throw new PlanFormatException("Plan is shorter than its own header.");
            }

            int at = 0;
            byte version = bytes[at++];

            if (version != Plan.FormatVersion)
            {
                throw new PlanFormatException(
                    $"Plan is format version {version}; this build speaks {Plan.FormatVersion}.");
            }

            int seat = bytes[at++];
            int moleIndex = bytes[at++];
            WeaponId weapon = (WeaponId)bytes[at++];
            int routeCount = ReadUInt16(bytes, ref at);
            int actionCount = ReadUInt16(bytes, ref at);

            if (routeCount > MaxRoutePoints || actionCount > MaxActions)
            {
                throw new PlanFormatException("Plan claims more route or actions than is possible.");
            }

            int expected = HeaderBytes + (routeCount * RoutePointBytes) + (actionCount * ActionBytes);

            if (bytes.Length != expected)
            {
                throw new PlanFormatException(
                    $"Plan is {bytes.Length} bytes but its header describes {expected}.");
            }

            RoutePoint[] route = new RoutePoint[routeCount];
            for (int index = 0; index < routeCount; index++)
            {
                short cellX = ReadInt16(bytes, ref at);
                short cellY = ReadInt16(bytes, ref at);
                route[index] = new RoutePoint(cellX, cellY);
            }

            PlanAction[] actions = new PlanAction[actionCount];
            for (int index = 0; index < actionCount; index++)
            {
                ushort tick = ReadUInt16(bytes, ref at);
                PlanActionKind kind = (PlanActionKind)bytes[at++];
                WeaponId actionWeapon = (WeaponId)bytes[at++];
                byte power = bytes[at++];
                short aimX = ReadInt16(bytes, ref at);
                short aimY = ReadInt16(bytes, ref at);

                actions[index] = PlanAction.FromWire(
                    tick, kind, aimX, aimY, power, actionWeapon);
            }

            return new Plan(seat, moleIndex, weapon, route, actions);
        }

        private static void WriteUInt16(byte[] buffer, ref int at, ushort value)
        {
            buffer[at++] = (byte)(value & 0xFF);
            buffer[at++] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteInt16(byte[] buffer, ref int at, short value) =>
            WriteUInt16(buffer, ref at, unchecked((ushort)value));

        private static ushort ReadUInt16(byte[] buffer, ref int at)
        {
            ushort value = (ushort)(buffer[at] | (buffer[at + 1] << 8));
            at += 2;
            return value;
        }

        private static short ReadInt16(byte[] buffer, ref int at) =>
            unchecked((short)ReadUInt16(buffer, ref at));
    }

    /// <summary>Thrown when a plan cannot be read, rather than being guessed at.</summary>
    public sealed class PlanFormatException : Exception
    {
        public PlanFormatException(string message)
            : base(message)
        {
        }

        public PlanFormatException()
        {
        }

        public PlanFormatException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
