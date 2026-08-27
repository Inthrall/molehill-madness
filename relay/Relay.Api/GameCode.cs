using System.Security.Cryptography;

namespace Relay.Api;

/// <summary>
/// The five-letter code a host reads out and everybody else types in.
/// </summary>
/// <remarks>
/// The design fixes the shape of this: "a short code, five letters, Kahoot-style", with no friends
/// list and no persistent codes. It is the only way into a game other than sitting next to somebody,
/// so two properties matter more than they might elsewhere.
///
/// It has to survive being said out loud down a phone. That rules out letters that sound or look
/// like each other or like digits, so I and O are gone, and it rules out the obvious alternative of
/// using consonants only: a code with no vowels cannot spell anything, which is tempting, but
/// nobody can read BKTQR to a friend either.
///
/// And it has to not spell something regrettable. Keeping vowels means that is possible, so codes
/// are generated and then rejected, which is the only approach that works: you cannot pick an
/// alphabet that is both sayable and incapable of forming words.
/// </remarks>
public static class GameCode
{
    /// <summary>
    /// The letters a code is built from. No I and no O: said aloud or read off a screen they are
    /// one and zero often enough to lose somebody a game.
    /// </summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>How many letters. Five, per the design.</summary>
    public const int Length = 5;

    /// <summary>
    /// Sequences a code may not contain.
    /// </summary>
    /// <remarks>
    /// Deliberately short and deliberately a mechanism rather than an attempt at completeness. An
    /// all-ages game cannot hand somebody a code to read out to a room and hope, and this is the
    /// place that gets extended when something slips through rather than a thing to get right once.
    /// </remarks>
    private static readonly string[] Forbidden =
    {
        "ARSE", "BUM", "CRAP", "DAMN", "FART", "PISS", "POO", "SEX", "TIT", "WEE",
    };

    /// <summary>
    /// Draws a fresh code.
    /// </summary>
    /// <remarks>
    /// From the cryptographic generator rather than a seeded one. A guessable code is a stranger
    /// dropping into a game between children, which is the one thing the age gate exists to stop,
    /// and this costs nothing.
    /// </remarks>
    public static string Draw()
    {
        while (true)
        {
            char[] letters = new char[Length];

            for (int at = 0; at < Length; at++)
            {
                letters[at] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            string code = new string(letters);

            if (IsAllowed(code))
            {
                return code;
            }
        }
    }

    /// <summary>Whether a code is one this would have handed out.</summary>
    public static bool IsAllowed(string? code)
    {
        if (code is null || code.Length != Length)
        {
            return false;
        }

        foreach (char letter in code)
        {
            if (!Alphabet.Contains(letter, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (string word in Forbidden)
        {
            if (code.Contains(word, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a code the way somebody typed it, or null if it is not one.
    /// </summary>
    /// <remarks>
    /// Case and spacing are forgiven because a code arrives via a human ear and a human thumb. A
    /// letter outside the alphabet is not forgiven, because silently mapping it to something would
    /// send a player confidently into the wrong lobby.
    /// </remarks>
    public static string? Parse(string? typed)
    {
        if (typed is null)
        {
            return null;
        }

        string tidied = new string(typed.Where(char.IsLetter).ToArray()).ToUpperInvariant();

        return IsAllowed(tidied) ? tidied : null;
    }
}
