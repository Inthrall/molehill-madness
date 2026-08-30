using System;
using System.Collections.Generic;
using Godot;

/// <summary>The noises the game makes.</summary>
public enum Sound
{
    /// <summary>Something went off.</summary>
    Boom = 0,

    /// <summary>Something left a barrel.</summary>
    Launch = 1,

    /// <summary>Claws in soil.</summary>
    Dig = 2,

    /// <summary>A mole leaving in a puff.</summary>
    Poof = 3,

    /// <summary>That hurt, but comically.</summary>
    Ouch = 4,

    /// <summary>A button, a notch on the wheel, a plan locked in.</summary>
    Click = 5,

    /// <summary>A crate arriving.</summary>
    Thunk = 6,

    // Everything below arrives as a recording rather than a waveform, so there is no generator
    // for any of it. A sound with neither a sample nor a generator is silent and harmless, which
    // is what lets this list run ahead of the audio folder.

    /// <summary>Walking on turf.</summary>
    Walk = 7,

    /// <summary>Walking underground, which is the same act muffled.</summary>
    Burrow = 8,

    /// <summary>Arriving back on the ground.</summary>
    Land = 9,

    /// <summary>A bag of soil landing in a heap.</summary>
    Sandbag = 10,

    /// <summary>The Big Whack connecting.</summary>
    Whack = 11,

    /// <summary>A snap trap closing.</summary>
    Snap = 12,

    /// <summary>An acorn mortar leaving, and the split when it clusters.</summary>
    Mortar = 13,

    /// <summary>A helmet spinning to a stop.</summary>
    Helmet = 14,

    /// <summary>The stretcher squad's wheels.</summary>
    Stretcher = 15,

    /// <summary>One notch of the weapon wheel, quieter than a button.</summary>
    Notch = 16,

    /// <summary>A plan locked in.</summary>
    Commit = 17,

    /// <summary>A turn torn up.</summary>
    Reset = 18,

    /// <summary>The last few seconds of the planning clock.</summary>
    Warn = 19,

    /// <summary>A crate claimed.</summary>
    Collect = 20,

    /// <summary>A mole leaving the ground. A spring, because this is a cartoon.</summary>
    Hop = 21,

    /// <summary>A Tunnel Torpedo cutting. The one sound that runs rather than hits.</summary>
    Drill = 22,

    /// <summary>A crate breaking open.</summary>
    Crate = 23,
}

/// <summary>
/// Synthesised sound, built at startup, with no audio assets at all.
/// </summary>
/// <remarks>
/// The gate this build exists for asks whether the slapstick is funny, and silent slapstick is
/// not the same thing being tested. A mole spinning into a puff of dust reads as a bug without
/// a sound and as a joke with one, so playing the fun gate in silence would answer a question
/// nobody asked.
///
/// Generated rather than recorded because the alternative is an asset pipeline, a licence trail
/// and a folder of files, none of which the prototype needs to answer that question. Seven
/// waveforms built from sine, square and noise cost a few milliseconds at startup and can be
/// thrown away wholesale when a sound designer arrives.
///
/// Nothing here is deterministic and nothing here needs to be. Sound is drawn from the same
/// recording the damage numbers are, so it lands on the right tick, but a wobble in the pitch
/// cannot change a match.
/// </remarks>
public partial class Sfx : Node
{
    private const int SampleRate = 22050;
    private const int Voices = 10;

    private readonly Dictionary<Sound, AudioStreamWav> _streams =
        new Dictionary<Sound, AudioStreamWav>();

    /// <summary>
    /// Recorded variants per sound, which take precedence over anything synthesised.
    /// </summary>
    /// <remarks>
    /// Several per sound wherever the library had them. The wobble below stops a repeat sounding
    /// like a stuck record, but a pitch-shifted copy of one recording is still one recording, and
    /// digging fires often enough for that to wear through in a single turn.
    /// </remarks>
    private readonly Dictionary<Sound, AudioStream[]> _samples =
        new Dictionary<Sound, AudioStream[]>();

    private readonly List<AudioStreamPlayer> _players = new List<AudioStreamPlayer>();
    private readonly Random _wobble = new Random(20260827);
    private int _next;

    public override void _Ready()
    {
        _streams[Sound.Boom] = Boom();
        _streams[Sound.Launch] = Launch();
        _streams[Sound.Dig] = Dig();
        _streams[Sound.Poof] = Poof();
        _streams[Sound.Ouch] = Ouch();
        _streams[Sound.Click] = Click();
        _streams[Sound.Thunk] = Thunk();

        // After the waveforms, because a recording wins wherever one exists. The generators stay as
        // the fallback for everything the audio folder has not reached yet: explosions, the fuse,
        // steam, wind and the rest are still synthesised, and a mixed set is a great deal better
        // than either a silent game or waiting for the whole list to be recorded.
        LoadSamples();

        // A pool, so a busy tick can stack four explosions without any of them cutting
        // another one off.
        for (int voice = 0; voice < Voices; voice++)
        {
            AudioStreamPlayer player = new AudioStreamPlayer();
            AddChild(player);
            _players.Add(player);
        }
    }

    public void Play(Sound sound, float volumeDb = 0f, float pitchSpread = 0.12f)
    {
        if (_players.Count == 0)
        {
            return;
        }

        AudioStream? stream = Pick(sound);

        if (stream is null)
        {
            return;
        }

        AudioStreamPlayer player = _players[_next];
        _next = (_next + 1) % _players.Count;

        player.Stream = stream;
        player.VolumeDb = volumeDb;
        player.PitchScale = 1f + (float)((_wobble.NextDouble() - 0.5) * 2 * pitchSpread);
        player.Play();
    }

    /// <summary>
    /// Whichever recording or waveform this sound should use, or nothing if it has neither.
    /// </summary>
    private AudioStream? Pick(Sound sound)
    {
        if (_samples.TryGetValue(sound, out AudioStream[]? variants) && variants.Length > 0)
        {
            return variants[_wobble.Next(variants.Length)];
        }

        return _streams.TryGetValue(sound, out AudioStreamWav? built) ? built : null;
    }

    /// <summary>
    /// Loads whatever is in the audio folder, by name.
    /// </summary>
    /// <remarks>
    /// Convention rather than a table: a sound called Dig looks for dig_0.ogg, dig_1.ogg and so on
    /// until one is missing. That means a new recording is added by dropping a file in and running
    /// the import, with no code change at all, which matters because two thirds of the list is still
    /// missing and will arrive a few files at a time.
    ///
    /// Missing is not an error. The folder is expected to be incomplete for a long while, and a
    /// sound with no recording falls back to its waveform or stays silent.
    /// </remarks>
    private void LoadSamples()
    {
        foreach (Sound sound in Enum.GetValues<Sound>())
        {
            string name = sound.ToString().ToLowerInvariant();
            List<AudioStream> found = new List<AudioStream>();

            for (int variant = 0; ; variant++)
            {
                string path = $"res://audio/{name}_{variant}.ogg";

                if (!ResourceLoader.Exists(path))
                {
                    break;
                }

                if (ResourceLoader.Load(path) is AudioStream stream)
                {
                    found.Add(stream);
                }
            }

            if (found.Count > 0)
            {
                _samples[sound] = found.ToArray();
            }
        }

        // One line, because the folder is meant to be incomplete and the interesting question on
        // any given build is which half of the list is real yet. Cheap to read and cheaper to grep.
        GD.Print(
            $"sfx: {_samples.Count} of {Enum.GetValues<Sound>().Length} sounds recorded, "
            + $"{CountVariants()} files");
    }

    private int CountVariants()
    {
        int total = 0;

        foreach (AudioStream[] variants in _samples.Values)
        {
            total += variants.Length;
        }

        return total;
    }

    // ---- The waveforms ---------------------------------------------------------------

    /// <summary>A low thump under a burst of grit.</summary>
    private static AudioStreamWav Boom() => Build(0.45f, (time, life, noise) =>
    {
        float thump = Sine(time, Lerp(115f, 45f, life)) * Decay(life, 5f);
        float grit = noise * Decay(life, 9f) * 0.7f;

        return Soft((thump * 0.8f) + (grit * 0.5f));
    });

    /// <summary>A rising whistle, short enough to be a departure rather than a flight.</summary>
    private static AudioStreamWav Launch() => Build(0.14f, (time, life, noise) =>
        Square(time, Lerp(260f, 820f, life)) * Decay(life, 14f) * 0.32f);

    /// <summary>Claws in soil: dull, brief, and used a great deal.</summary>
    private static AudioStreamWav Dig() => Build(0.08f, (time, life, noise) =>
        noise * (1f - life) * 0.3f);

    /// <summary>
    /// The signature exit: a descending pop with a cloud of dust behind it.
    /// </summary>
    private static AudioStreamWav Poof() => Build(0.32f, (time, life, noise) =>
    {
        float pop = Sine(time, Lerp(1150f, 260f, life)) * Decay(life, 6f) * 0.45f;
        float dust = noise * Decay(life, 8f) * 0.35f;

        return Soft(pop + dust);
    });

    /// <summary>
    /// A squeak that rises then falls. Kid-friendly on purpose: the design is chasing the
    /// lowest age rating it can get, and a cry of pain is exactly the wrong noise for that.
    /// </summary>
    private static AudioStreamWav Ouch() => Build(0.2f, (time, life, noise) =>
    {
        float pitch = life < 0.35f
            ? Lerp(380f, 760f, life / 0.35f)
            : Lerp(760f, 430f, (life - 0.35f) / 0.65f);

        float vibrato = 1f + (Sine(time, 22f) * 0.05f);
        float bell = MathF.Sin(life * MathF.PI);

        return Sine(time, pitch * vibrato) * bell * 0.35f;
    });

    /// <summary>A tick. Deliberately tiny: it fires on every notch of the wheel.</summary>
    private static AudioStreamWav Click() => Build(0.03f, (time, life, noise) =>
        Square(time, 900f) * Decay(life, 60f) * 0.18f);

    /// <summary>Wood on soil.</summary>
    private static AudioStreamWav Thunk() => Build(0.16f, (time, life, noise) =>
    {
        float body = Sine(time, 150f) * Decay(life, 16f);
        float knock = noise * Decay(life, 60f) * 0.5f;

        return Soft((body * 0.6f) + (knock * 0.4f));
    });

    // ---- Synthesis ------------------------------------------------------------------

    /// <summary>
    /// Fills a mono sixteen-bit buffer from a voice function.
    /// </summary>
    /// <remarks>
    /// The voice is handed the absolute time, how far through the sound it is, and a noise
    /// sample, which between them cover everything these seven need. Noise comes from a fixed
    /// generator so a given sound is byte-identical every run: not required, but it makes a
    /// recorded capture reproducible.
    /// </remarks>
    private static AudioStreamWav Build(float seconds, Func<float, float, float, float> voice)
    {
        int samples = (int)(seconds * SampleRate);
        byte[] data = new byte[samples * 2];
        Random noise = new Random(1979);
        float smoothed = 0f;

        for (int index = 0; index < samples; index++)
        {
            float time = index / (float)SampleRate;
            float life = index / (float)samples;

            // Averaged with the previous sample, which takes the fizz off white noise and
            // leaves something closer to soil than to static.
            float raw = (float)((noise.NextDouble() * 2) - 1);
            smoothed = (smoothed * 0.6f) + (raw * 0.4f);

            short sample = (short)(Math.Clamp(voice(time, life, smoothed), -1f, 1f) * 30000);
            data[index * 2] = (byte)(sample & 0xFF);
            data[(index * 2) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = SampleRate,
            Stereo = false,
            Data = data,
        };
    }

    private static float Sine(float time, float hertz) =>
        MathF.Sin(time * hertz * MathF.Tau);

    private static float Square(float time, float hertz) =>
        MathF.Sin(time * hertz * MathF.Tau) >= 0 ? 1f : -1f;

    private static float Decay(float life, float rate) =>
        MathF.Exp(-life * rate);

    private static float Lerp(float from, float to, float amount) =>
        from + ((to - from) * amount);

    /// <summary>Rounds off a peak instead of clipping it flat.</summary>
    private static float Soft(float value) =>
        MathF.Tanh(value * 1.4f);
}
