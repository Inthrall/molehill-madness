using Molehill.Online;

namespace Molehill.Online.Tests;

/// <summary>
/// The age gate: what a date of birth becomes, and what each answer permits.
/// </summary>
/// <remarks>
/// Every one of these is a pure function, so the whole surface can be covered exhaustively, which is
/// what this kind of code deserves. The failure modes are not crashes: they are a protection silently
/// not applying, or applying to somebody it should not, and neither shows up as anything going wrong.
/// </remarks>
[TestFixture]
public sealed class AgeBandTests
{
    private static readonly DateTime Today = new DateTime(2026, 8, 28);

    // ---- Working out the band -----------------------------------------------------------

    [Test]
    public void SomebodyWellOverTheThresholdIsAnAdult()
    {
        Assert.That(Allowed.From(new DateTime(1990, 3, 4), Today), Is.EqualTo(AgeBand.Adult));
    }

    [Test]
    public void SomebodyWellUnderItIsAChild()
    {
        Assert.That(Allowed.From(new DateTime(2020, 3, 4), Today), Is.EqualTo(AgeBand.Child));
    }

    /// <summary>
    /// The boundary, from both sides and on the day itself. Off-by-one here is a real-world error
    /// about a real person, so it gets its own cases rather than being trusted to the arithmetic.
    /// </summary>
    [TestCase(2013, 8, 28, AgeBand.Adult, TestName = "Thirteen today")]
    [TestCase(2013, 8, 29, AgeBand.Child, TestName = "Thirteen tomorrow")]
    [TestCase(2013, 8, 27, AgeBand.Adult, TestName = "Thirteen yesterday")]
    [TestCase(2013, 12, 31, AgeBand.Child, TestName = "Twelve, birthday later this year")]
    [TestCase(2013, 1, 1, AgeBand.Adult, TestName = "Thirteen, birthday earlier this year")]
    public void TheBoundaryFallsOnTheBirthday(int year, int month, int day, AgeBand expected)
    {
        Assert.That(Allowed.From(new DateTime(year, month, day), Today), Is.EqualTo(expected));
    }

    /// <summary>
    /// A mistyped date is not an answer. Clamping it to something plausible would be deciding on
    /// somebody's behalf, which is the one thing a gate must never do.
    /// </summary>
    [Test]
    public void AnImpossibleDateIsNotAnAnswer()
    {
        Assert.That(
            Allowed.From(Today.AddDays(1), Today),
            Is.EqualTo(AgeBand.Unknown),
            "Born tomorrow.");

        Assert.That(
            Allowed.From(new DateTime(1823, 1, 1), Today),
            Is.EqualTo(AgeBand.Unknown),
            "Two hundred years old.");
    }

    // ---- What each band permits ---------------------------------------------------------

    /// <summary>
    /// The rule the whole gate exists for: strangers are for accounts over the threshold, or younger
    /// ones with a platform's parental approval.
    /// </summary>
    [TestCase(AgeBand.Adult, false, true)]
    [TestCase(AgeBand.Adult, true, true)]
    [TestCase(AgeBand.Child, false, false)]
    [TestCase(AgeBand.Child, true, true)]
    [TestCase(AgeBand.Unknown, false, false)]
    [TestCase(AgeBand.Unknown, true, false)]
    public void MatchmakingFollowsTheThresholdAndParentalApproval(
        AgeBand band, bool approval, bool allowed)
    {
        Assert.That(Allowed.Matchmaking(band, approval), Is.EqualTo(allowed));
    }

    /// <summary>
    /// An account that has not been through the gate has not been cleared for anything. Treating
    /// silence as consent is the whole failure this is guarding against, and parental approval on an
    /// unasked account is not approval of anything.
    /// </summary>
    [Test]
    public void AnUnaskedAccountIsClearedForNothingItNeedsClearingFor()
    {
        Assert.That(Allowed.Matchmaking(AgeBand.Unknown), Is.False);
        Assert.That(Allowed.Matchmaking(AgeBand.Unknown, parentalApproval: true), Is.False);
        Assert.That(Allowed.EmailCollection(AgeBand.Unknown), Is.False);
        Assert.That(Allowed.Analytics(AgeBand.Unknown), Is.False);
        Assert.That(Allowed.Store(AgeBand.Unknown, parentalApproval: true), Is.False);
    }

    [Test]
    public void NoEmailIsCollectedUnderTheThresholdOnAnyTerms()
    {
        Assert.That(Allowed.EmailCollection(AgeBand.Child), Is.False);
        Assert.That(Allowed.EmailCollection(AgeBand.Adult), Is.True);
    }

    [Test]
    public void NothingBeyondCrashReportingIsMeasuredUnderTheThreshold()
    {
        Assert.That(Allowed.Analytics(AgeBand.Child), Is.False);
        Assert.That(Allowed.Analytics(AgeBand.Adult), Is.True);
    }

    /// <summary>Crash reporting is the design's one permitted analytics, for everybody.</summary>
    [TestCase(AgeBand.Unknown)]
    [TestCase(AgeBand.Child)]
    [TestCase(AgeBand.Adult)]
    public void CrashReportingIsAllowedForEverybody(AgeBand band)
    {
        Assert.That(Allowed.CrashReporting(band), Is.True);
    }

    [Test]
    public void TheStoreNeedsEitherAgeOrAPlatformsParentalApproval()
    {
        Assert.That(Allowed.Store(AgeBand.Adult), Is.True);
        Assert.That(Allowed.Store(AgeBand.Child), Is.False);
        Assert.That(Allowed.Store(AgeBand.Child, parentalApproval: true), Is.True);
    }

    /// <summary>
    /// The important negative. A code arrives from somebody you know, so gating it would stop a child
    /// playing with their own family while doing nothing at all about strangers, and local couch play
    /// is open to everyone and needs no account whatsoever.
    /// </summary>
    [TestCase(AgeBand.Unknown)]
    [TestCase(AgeBand.Child)]
    [TestCase(AgeBand.Adult)]
    public void JoiningByCodeIsOpenToEverybody(AgeBand band)
    {
        Assert.That(Allowed.JoiningByCode(band), Is.True);
    }
}
