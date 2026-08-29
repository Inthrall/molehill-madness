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
    private static string _relayId = string.Empty;
    private static string _relaySecret = string.Empty;
    private static AgeBand _band = AgeBand.Unknown;
    private static int _reviewAfter;
    private static int _planned;
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

    /// <summary>
    /// The account the relay knows this device by, if it has ever needed one.
    /// </summary>
    /// <remarks>
    /// A different thing from <see cref="Id"/>, which this device made up for itself. This one is
    /// issued by the relay, and the secret beside it is handed over exactly once and cannot be
    /// reissued, so it is written down the moment it arrives rather than when something is finished
    /// with it. There is nothing in an account to recover it by: no email for an under-threshold one,
    /// and the design says there must not be.
    ///
    /// Null until the first time somebody asks to be put among strangers. Couch play needs no
    /// account and a game code needs none either, so most devices never have one.
    /// </remarks>
    public static AccountKey? RelayAccount
    {
        get
        {
            Load();

            return _relayId.Length > 0 && _relaySecret.Length > 0
                ? new AccountKey(_relayId, _relaySecret)
                : null;
        }
    }

    /// <summary>Keeps the account the relay just issued, since it will not issue it again.</summary>
    public static void RememberRelay(AccountKey account)
    {
        if (account is null)
        {
            return;
        }

        Load();

        _relayId = account.Id;
        _relaySecret = account.Secret;
        Save();
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

    /// <summary>
    /// Whether this device has ever finished a planning phase.
    /// </summary>
    /// <remarks>
    /// The design's beginner flag. It had two jobs and has one left, which is worth saying rather
    /// than leaving the reader to find out.
    ///
    /// It used to decide whether the drawn paw appeared, and that was the whole of the tutorial. The
    /// paw is gone: it demonstrated the drag that laid a route, and that gesture stopped existing when
    /// steering moved to the keys, so what it was left teaching was how to pan a camera. Nothing
    /// teaches a first round now.
    ///
    /// What remains is the half the design calls "first matches are seeded together where the
    /// population allows", so a beginner's first game is against other beginners rather than against
    /// somebody who has been reading dirt for six months. There is matchmaking to seed now, and this
    /// flag is still not sent anywhere, because nothing on the pool side asks for it yet.
    /// </remarks>
    public static bool Beginner
    {
        get
        {
            Load();

            return _planned == 0;
        }
    }

    /// <summary>How many turns this device has ever planned. Small numbers are the interesting ones.</summary>
    public static int Planned
    {
        get
        {
            Load();

            return _planned;
        }
    }

    /// <summary>
    /// Notes that a turn was planned.
    /// </summary>
    /// <remarks>
    /// Counted rather than flagged, because "has this player seen the game before" and "is this their
    /// very first turn" are different questions and the second one is the one the paw asks. A count
    /// answers both and costs the same.
    /// </remarks>
    public static void Planned1More()
    {
        Load();

        if (_planned >= int.MaxValue)
        {
            return;
        }

        _planned++;
        Save();
    }

    /// <summary>Forgets everything about experience, so the paw appears again. For testing.</summary>
    public static void ForgetExperience()
    {
        Load();

        _planned = 0;
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
            _relayId = file.GetValue("account", "relayId", string.Empty).AsString();
            _relaySecret = file.GetValue("account", "relaySecret", string.Empty).AsString();
            _band = (AgeBand)file.GetValue("account", "band", 0).AsInt32();
            _reviewAfter = file.GetValue("account", "reviewAfter", 0).AsInt32();
            _planned = file.GetValue("account", "planned", 0).AsInt32();
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
        file.SetValue("account", "relayId", _relayId);
        file.SetValue("account", "relaySecret", _relaySecret);
        file.SetValue("account", "band", (int)_band);
        file.SetValue("account", "reviewAfter", _reviewAfter);
        file.SetValue("account", "planned", _planned);
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
