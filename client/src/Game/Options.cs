using Godot;

/// <summary>
/// The handful of things a player can set, and the file they survive in.
/// </summary>
/// <remarks>
/// There was a sound button in the pause menu already, and its own comment admitted what it did:
/// "only decides which speaker is drawn". It drew a crossed-out speaker and left the audio running,
/// which is worse than having no button, because a control that visibly acknowledges a press and
/// then does nothing reads as a broken game rather than a missing feature.
///
/// So the setting is real now, it is one setting rather than one per screen, and it is written down
/// so it survives being closed. Static because there is exactly one of each of these per game and
/// threading them through four screens to prove a point would cost more than it bought.
/// </remarks>
public static class Options
{
    private const string Path = "user://settings.cfg";
    private const string Section = "sound";

    private static bool _loaded;
    private static bool _sound = true;

    /// <summary>Whether the game makes any noise at all.</summary>
    public static bool Sound
    {
        get
        {
            Load();
            return _sound;
        }

        set
        {
            Load();

            if (_sound == value)
            {
                return;
            }

            _sound = value;
            Apply();
            Save();
        }
    }

    /// <summary>
    /// Puts the settings into effect, which has to happen at startup as well as on a change.
    /// </summary>
    public static void Apply()
    {
        Load();

        // The master bus, so it covers everything: the sound effects now and whatever music arrives
        // later, without either having to know this exists.
        AudioServer.SetBusMute(0, !_sound);
    }

    private static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        ConfigFile file = new ConfigFile();

        // Absent is the normal case on a first run, and not a problem: the defaults above stand.
        if (file.Load(Path) != Error.Ok)
        {
            return;
        }

        _sound = (bool)file.GetValue(Section, "on", true);
    }

    private static void Save()
    {
        ConfigFile file = new ConfigFile();

        file.SetValue(Section, "on", _sound);

        // Failure is ignored on purpose. A read-only user directory is somebody else's problem and
        // is not worth losing a game over: the setting still works, it just will not be remembered.
        file.Save(Path);
    }
}
