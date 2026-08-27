using System.Collections.Generic;

namespace Molehill.Online
{
    /// <summary>
    /// Everything anybody can say.
    /// </summary>
    /// <remarks>
    /// The whole of communication in this game, and a fixed list on purpose. The design is blunt about
    /// it: "no free text chat, anywhere, ever", because "a fixed wheel of emotes and canned phrases
    /// cannot be used to harass, groom or leak personal information". A closed set of eight is a safety
    /// property you get for free and cannot lose to a clever workaround.
    ///
    /// Pictures rather than phrases, because there are no words in the game. The design lists the
    /// emote wheel among the load-bearing glyph work alongside the weapon objects and the four
    /// helmets, and it names two of these outright: a pointed "nice shot" and a very sarcastic
    /// "after you". Sarcasm is wanted. What is excluded is anything that could carry cruelty a fixed
    /// picture cannot, which is a different thing from excluding edge.
    ///
    /// Eight because that is a radial menu somebody can hit with a thumb without looking. Adding a
    /// ninth is a wire change, since the relay refuses an index that is not on the wheel, and that is
    /// deliberate: an emote nothing can draw is worse than one nobody can send.
    /// </remarks>
    public enum Emote
    {
        /// <summary>Applause. Genuine, or pointed after a spectacular miss.</summary>
        NiceShot = 0,

        /// <summary>A sweeping bow. The design's "very sarcastic 'after you'".</summary>
        AfterYou = 1,

        /// <summary>A wince. Owning a mistake, which is most of this game.</summary>
        Oops = 2,

        /// <summary>An hourglass. Give me a moment, I am thinking.</summary>
        Thinking = 3,

        /// <summary>A warning. Look behind you, or look at the lava.</summary>
        WatchOut = 4,

        /// <summary>A raised paw. Well played, without the edge.</summary>
        WellPlayed = 5,

        /// <summary>A laughing mole. The one a comedy game cannot do without.</summary>
        Laughing = 6,

        /// <summary>
        /// An offered paw. Truce?
        /// </summary>
        /// <remarks>
        /// This one exists to buy back something the design says cutting chat costs: "no coordinating
        /// a temporary alliance in a four-way match". A truce cannot be agreed without words, but it
        /// can be proposed with a picture and accepted by simply not shooting somebody, which is a
        /// negotiation the game can carry. Kingmaking in a free-for-all is on the risk register, and a
        /// table that can gesture at each other has one more tool for handling it than one that
        /// cannot.
        /// </remarks>
        Truce = 7,
    }

    /// <summary>The wheel, in the order it is laid out.</summary>
    public static class Wheel
    {
        /// <summary>
        /// Clockwise from the top.
        /// </summary>
        /// <remarks>
        /// Ordered so the two that get used most under pressure are easiest to hit. Watch out is at
        /// the top because it is the only one that is ever urgent, and Nice shot sits next to it
        /// because it is the one sent most.
        /// </remarks>
        public static readonly Emote[] Order =
        {
            Emote.WatchOut,
            Emote.NiceShot,
            Emote.WellPlayed,
            Emote.Laughing,
            Emote.AfterYou,
            Emote.Oops,
            Emote.Thinking,
            Emote.Truce,
        };

        /// <summary>How many are on it. The relay refuses anything else.</summary>
        public static int Count => Order.Length;
    }

    /// <summary>Something a platoon said, and when it arrived.</summary>
    /// <remarks>
    /// Deliberately carrying no simulation state and deliberately not going anywhere near a plan. An
    /// emote is presentation: it appears above a mole for a few seconds and changes nothing, which is
    /// what lets it travel out of band at all. The moment an emote could affect an outcome it would be
    /// an input, and inputs live in plans and go through the codec so that every client agrees on them.
    /// </remarks>
    public sealed class Said
    {
        public Said(int seat, Emote emote, double arrivedAt)
        {
            Seat = seat;
            Emote = emote;
            ArrivedAt = arrivedAt;
        }

        public int Seat { get; }

        public Emote Emote { get; }

        /// <summary>
        /// When this client heard it, on its own clock, in seconds since the session started.
        /// </summary>
        /// <remarks>
        /// This client's clock rather than the relay's, and not the simulation's tick either. How long
        /// a picture stays on screen is a local presentation decision, and hanging it off a shared
        /// clock would mean an emote sent while somebody's phone was in a tunnel arriving already
        /// expired.
        /// </remarks>
        public double ArrivedAt { get; }
    }

    /// <summary>What has been said lately, and by whom.</summary>
    /// <remarks>
    /// Keeps only the last thing each seat said, because that is all a screen can show: a platoon has
    /// one mole to hang a bubble over and a second bubble would land on top of the first. It also
    /// means a burst that got past the relay's limit costs one drawn picture rather than a stack.
    /// </remarks>
    public sealed class Conversation
    {
        private readonly Dictionary<int, Said> _latest = new Dictionary<int, Said>();

        /// <summary>How long a picture stays up.</summary>
        public const double Lingers = 3.5;

        public void Heard(int seat, Emote emote, double at)
        {
            _latest[seat] = new Said(seat, emote, at);
        }

        /// <summary>What this seat is currently saying, or null if it has gone quiet.</summary>
        public Said? From(int seat, double now) =>
            _latest.TryGetValue(seat, out Said? said) && now - said.ArrivedAt < Lingers
                ? said
                : null;

        public void Clear() => _latest.Clear();
    }
}
