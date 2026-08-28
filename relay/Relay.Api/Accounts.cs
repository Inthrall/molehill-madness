namespace Relay.Api;

/// <summary>
/// Which side of the age threshold an account is on. The only thing about age the relay keeps.
/// </summary>
/// <remarks>
/// A band and not a date, and not a number of years either. The date of birth is typed once on the
/// device, used once to work this out, and never leaves it: what arrives here is one of these three
/// values. There is nothing to be gained by the relay knowing how old anybody is and a great deal to
/// be lost by storing it.
///
/// Three values rather than two, because "we have not asked yet" is a real state and must not be
/// confused with either answer. Defaulting an unasked account to the safe side would be tempting and
/// wrong: it would silently apply child protections to adults and, worse, would make a bug that
/// skipped the gate look like a working gate.
///
/// The same three values as the client's own enum, deliberately, so the two ends of the wire cannot
/// disagree about what a band is. They are two declarations rather than one because the relay has no
/// reference to the game and must not acquire one, which is the same rule that keeps it from ever
/// seeing a plan.
/// </remarks>
public enum AgeBand
{
    /// <summary>Nobody has been asked yet. Not an answer, and not a default.</summary>
    Unknown = 0,

    /// <summary>Under the threshold. Every protection on, and no way to turn them off.</summary>
    Child = 1,

    /// <summary>Over the threshold.</summary>
    Adult = 2,
}

/// <summary>
/// One player, as little of them as the game can get away with knowing.
/// </summary>
/// <remarks>
/// An id, a band and two timestamps. No name, because names are generated and belong to a platoon
/// rather than a person; no email, because the design gives under-threshold accounts none and this
/// has no way to tell which it is talking to until the band arrives; no display handle, no friends,
/// nothing that could make anybody findable. The design calls for "no discoverable social graph", and
/// the cheapest way to have none is to store nothing one could be built from.
///
/// The secret is not on this record on purpose. It is handed over once, when the account is made, and
/// after that it only ever travels inward: the relay checks it and never repeats it, so an account
/// read cannot leak the credential that owns it. The same shape a seat token has.
/// </remarks>
public sealed record Account(
    string Id,
    AgeBand Band,
    DateTimeOffset CreatedAt,
    DateTimeOffset SeenAt,
    bool Approved = false,
    string? Email = null);

/// <summary>
/// What an account of a given band is allowed to do here.
/// </summary>
/// <remarks>
/// The client has a copy of this rule and the copy is not the enforcement. A guard that runs on the
/// device protects nobody: it is there so the button is not offered, and it can be edited out by
/// anybody who cares to. This one is the gate.
///
/// It is a rule stated twice, which is worth being uncomfortable about, and the discomfort is smaller
/// than the alternatives. Sharing a library would mean the relay referencing the game, which is the
/// thing that keeps it from ever learning what a plan is. Trusting the client would mean not having
/// a gate. So it is written down in both places, in the same words, with each saying what the other
/// is for.
/// </remarks>
public static class Allowed
{
    /// <summary>
    /// Whether this account may be dropped into a match with strangers.
    /// </summary>
    /// <remarks>
    /// The design: "random matchmaking with strangers is gated to accounts over the threshold, or to
    /// younger ones with platform-level parental approval". This is the one rule the whole age gate
    /// exists to enforce, and everything that pairs a player with somebody they did not invite comes
    /// through here.
    ///
    /// The approval is a parameter and not a claim. It is only ever true because a platform signed a
    /// statement saying so and this relay held the public half of the key that checked it: a player
    /// cannot set it, a client cannot send it, and with no platform keys configured nothing can be
    /// approved at all. See <see cref="Approvals"/>, where the argument for that shape lives.
    ///
    /// Never on an unasked account, approved or not. One that has not been through the gate has not
    /// been cleared for anything, an approval about a band nobody has established is an approval of
    /// nothing, and treating silence as consent is the whole failure.
    /// </remarks>
    public static bool Matchmaking(AgeBand band, bool approved = false) =>
        band switch
        {
            AgeBand.Adult => true,
            AgeBand.Child => approved,
            _ => false,
        };

    /// <summary>
    /// Whether an email address may be asked for or kept.
    /// </summary>
    /// <remarks>
    /// The design: under-threshold accounts get "no email collection". Not collection with a consent
    /// box, not collection we delete later, and not collection with parental approval either. None.
    /// The approval the design describes buys a child the stranger pool and the store; it says
    /// nothing about handing us their email address, and reading it as though it did would be
    /// inventing consent from an adjacent sentence.
    /// </remarks>
    public static bool EmailCollection(AgeBand band) => band == AgeBand.Adult;

    /// <summary>
    /// Whether a match can be joined by code.
    /// </summary>
    /// <remarks>
    /// Everybody, including a child and including an account that has never been asked, which is why
    /// the code endpoints take no account at all. A code arrives from somebody you know, and the
    /// person who read it out is accountable for who else is in the lobby. Gating this would stop a
    /// child playing with their own family while doing nothing about the risk the gate exists for.
    ///
    /// Here as a statement rather than as a call site. Nothing asks it, and nothing should have to:
    /// the absence of a check on those endpoints is the implementation. It is written down so that
    /// the absence reads as a decision rather than as an oversight somebody helpfully corrects.
    /// </remarks>
    public static bool JoiningByCode(AgeBand band) => true;
}
