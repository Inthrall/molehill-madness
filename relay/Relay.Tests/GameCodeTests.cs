using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// The code is the only way into a game, so the tests here are about the two things that can go
/// wrong out in the world rather than about the code path: a code that cannot survive being said
/// down a phone, and a code that spells something a child should not read out to a room.
/// </summary>
[TestFixture]
public sealed class GameCodeTests
{
    /// <summary>
    /// Enough draws that a letter appearing zero times means the generator cannot produce it, rather
    /// than that it happened not to this time. With 24 letters, the chance of missing any one across
    /// five thousand letters drawn is somewhere past astronomical.
    /// </summary>
    private const int ManyDraws = 1000;

    [Test]
    public void TheAlphabetHasNoLettersThatSoundOrLookLikeDigits()
    {
        Assert.That(GameCode.Alphabet, Does.Not.Contain("I"), "I reads as one.");
        Assert.That(GameCode.Alphabet, Does.Not.Contain("O"), "O reads as zero.");
    }

    [Test]
    public void EveryDrawnCodeIsOneItWouldAccept()
    {
        for (int drawn = 0; drawn < ManyDraws; drawn++)
        {
            string code = GameCode.Draw();

            Assert.That(code, Has.Length.EqualTo(GameCode.Length));
            Assert.That(GameCode.IsAllowed(code), Is.True, $"Drew {code} but would refuse it.");
        }
    }

    /// <summary>
    /// The generator is only as good as its reach: an alphabet it cannot cover would quietly shrink
    /// the code space, and a rejection rule with a bug in it could exclude a whole letter without
    /// ever failing.
    /// </summary>
    [Test]
    public void DrawsReachEveryLetterOfTheAlphabet()
    {
        HashSet<char> seen = new HashSet<char>();

        for (int drawn = 0; drawn < ManyDraws; drawn++)
        {
            seen.UnionWith(GameCode.Draw());
        }

        Assert.That(seen, Is.EquivalentTo(GameCode.Alphabet.ToCharArray()));
    }

    [Test]
    public void ACodeSpellingSomethingRegrettableIsRefused()
    {
        Assert.That(GameCode.IsAllowed("FARTS"), Is.False);
        Assert.That(GameCode.IsAllowed("BUMPY"), Is.False);
        Assert.That(GameCode.IsAllowed("CRAPS"), Is.False);
    }

    /// <summary>
    /// A word buried in the middle counts. Checking only the start would let most of the list
    /// through, and a code is read aloud whole.
    /// </summary>
    [Test]
    public void TheCheckLooksAnywhereInTheCodeNotJustTheStart()
    {
        Assert.That(GameCode.IsAllowed("ZBUMZ"), Is.False);
        Assert.That(GameCode.IsAllowed("ZZPOO"), Is.False);
        Assert.That(GameCode.IsAllowed("MUBBA"), Is.True, "The same letters, spelling nothing.");
        Assert.That(GameCode.IsAllowed("FRATS"), Is.True);
    }

    [TestCase("abcde", "ABCDE")]
    [TestCase("  ABCDE  ", "ABCDE")]
    [TestCase("ab-cd e", "ABCDE")]
    [TestCase("AbCdE", "ABCDE")]
    public void ParseForgivesCaseAndSpacing(string typed, string expected)
    {
        Assert.That(GameCode.Parse(typed), Is.EqualTo(expected));
    }

    /// <summary>
    /// The one thing not to forgive. Mapping a mistyped letter to a nearby one would send a player
    /// confidently into somebody else's game, which is worse than telling them the code was wrong.
    /// </summary>
    [TestCase("ABCDI")]
    [TestCase("ABCDO")]
    [TestCase("ABCD")]
    [TestCase("ABCDEF")]
    [TestCase("")]
    [TestCase(null)]
    public void ParseRefusesAnythingItDidNotIssue(string? typed)
    {
        Assert.That(GameCode.Parse(typed), Is.Null);
    }

    /// <summary>
    /// Digits are stripped rather than refused, because a five-letter code with a stray keypress in
    /// it is still readable, and what survives is checked against the alphabet anyway.
    /// </summary>
    [Test]
    public void DigitsAreStrippedBeforeTheCodeIsJudged()
    {
        Assert.That(GameCode.Parse("AB1CD2E"), Is.EqualTo("ABCDE"));
        Assert.That(GameCode.Parse("12345"), Is.Null);
    }
}
