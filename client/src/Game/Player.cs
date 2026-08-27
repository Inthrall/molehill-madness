using System;
using Godot;
using Molehill.Online;

/// <summary>
/// This device's account: an identifier nobody chose and an age band.
/// </summary>
/// <remarks>
/// The design's "anonymous device account". There is no sign-up, no name and no password, because
/// there is nothing for an account to be: "account names never appear anywhere", the clips show
/// platoon colours rather than handles, and a share link resolves to a replay rather than a profile.
/// What is left is an identifier so that a future cross-play link has something to attach to, and the
/// answer to the one question the design does require asking.
///
/// The date of birth is the important part, and the important part of that is what happens to it: it
/// is typed once, turned into a band, and never written down. Only the band is stored, and only the
/// band is ever sent anywhere. Keeping the date would buy a band that corrects itself on a birthday
/// and would cost holding a child's date of birth on disk and, sooner or later, somewhere else too.
/// The birthday case is handled by asking again once the answer could have changed, which needs a
/// year rather than a date.
/// </remarks>
public static class Player
{
    private const string Kept = "user://player.cfg";

    private static string _id = string.Empty;
    private static AgeBand _band = AgeBand.Unknown;
    private static int _reviewAfter;
    private static bool _loaded;

    /// <summary>This device's identifier. Random, and meaningless to anybody including us.</summary>
    public static string Id
    {
        get
        {
            Load();

            return _id;
        }
    }

    /// <summary>Which side of the threshold this account is on.</summary>
    public static AgeBand Band
    {
        get
        {
            Load();

            // A child who has had a birthday since answering might not be one any more, and the
            // account should not be stuck on the wrong side of the line for years. The year the
            // answer could change is kept rather than the date, so this asks again rather than
            // deciding: a year is not a date of birth, and it is enough to know when to ask.
            if (_band == AgeBand.Child && _reviewAfter > 0 && Today().Year >= _reviewAfter)
            {
                return AgeBand.Unknown;
            }

            return _band;
        }
    }

    /// <summary>
    /// Whether the gate still needs putting in front of this player.
    /// </summary>
    /// <remarks>
    /// The design asks at first run. This also asks again when a stored Child answer has aged out,
    /// which is the same question rather than a new one.
    /// </remarks>
    public static bool NeedsGate => Band == AgeBand.Unknown;

    /// <summary>
    /// Records the answer to the gate, as a band and nothing else.
    /// </summary>
    /// <remarks>
    /// Takes the date, keeps none of it. An unreadable date is stored as Unknown rather than guessed
    /// at, so a mistyped answer means being asked again instead of being quietly sorted.
    /// </remarks>
    public static void Answer(DateTime born)
    {
        Load();

        _band = Allowed.From(born, Today());

        // The year this answer could stop being true. Zero for an adult, because an adult does not
        // become a child.
        _reviewAfter = _band == AgeBand.Child ? born.Year + Allowed.Threshold : 0;

        Save();
    }

    /// <summary>Forgets the answer, so the gate is asked again. For settings, and for testing.</summary>
    public static void ForgetAge()
    {
        Load();

        _band = AgeBand.Unknown;
        _reviewAfter = 0;
        Save();
    }

    // ---- Storage ---------------------------------------------------------------------

    private static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        ConfigFile file = new ConfigFile();

        if (file.Load(Kept) == Error.Ok)
        {
            _id = file.GetValue("account", "id", string.Empty).AsString();
            _band = (AgeBand)file.GetValue("account", "band", 0).AsInt32();
            _reviewAfter = file.GetValue("account", "reviewAfter", 0).AsInt32();
        }

        if (_id.Length > 0)
        {
            return;
        }

        // First run on this device. The identifier is made here and never again.
        _id = Guid.NewGuid().ToString("N");
        Save();
    }

    private static void Save()
    {
        ConfigFile file = new ConfigFile();
        file.SetValue("account", "id", _id);
        file.SetValue("account", "band", (int)_band);
        file.SetValue("account", "reviewAfter", _reviewAfter);
        file.Save(Kept);
    }

    /// <summary>
    /// Today, from the device.
    /// </summary>
    /// <remarks>
    /// Which a player could change, and that is accepted. A date-of-birth gate is answered by the
    /// person it is protecting and can be lied to directly with far less effort than changing a
    /// system clock, so defending this particular number would be security theatre. The gate's job is
    /// to ask neutrally and record the answer honestly, not to be unbeatable.
    /// </remarks>
    private static DateTime Today() =>
        DateTimeOffset.FromUnixTimeSeconds((long)Time.GetUnixTimeFromSystem()).UtcDateTime.Date;
}
