using System.Diagnostics;
using Molehill.Clip;

namespace Molehill.Clip.Tests;

/// <summary>
/// Turning frames into a file, and picking which kind of file that is.
/// </summary>
/// <remarks>
/// The video half of this cannot be fully verified everywhere, and the tests say so rather than
/// pretending. Where ffmpeg exists, which is every Linux CI runner and any development machine that
/// has it installed, the encoder is run for real and the bytes that come out are checked: that is
/// the one test that can say the command line is right, because ffmpeg is the only thing that knows.
/// Where it does not, those tests are skipped by name and the rest still cover the parts that are
/// pure, which is the command line itself, the lookup, the fallback and the buffering encoder.
/// </remarks>
[TestFixture]
public sealed class EncoderTests
{
    private string? _pathWas;
    private string? _namedWas;

    [SetUp]
    public void SetUp()
    {
        _pathWas = Environment.GetEnvironmentVariable("PATH");
        _namedWas = Environment.GetEnvironmentVariable(FfmpegEncoder.PathVariable);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("PATH", _pathWas);
        Environment.SetEnvironmentVariable(FfmpegEncoder.PathVariable, _namedWas);
        Clips.Reconsider();
    }

    // ---- The command line ---------------------------------------------------------------

    /// <summary>
    /// Nearly every way this can be wrong is in this one string, and ffmpeg will not tell you about
    /// most of them: a pixel format that does not match what is being pushed in produces a video of
    /// colourful noise, and a size that does not match produces one that slowly shears sideways.
    /// </summary>
    [Test]
    public void TheCommandLineDescribesExactlyWhatIsBeingPushedIn()
    {
        string line = FfmpegEncoder.Arguments(1080, 1920, 15, "/tmp/out.mp4");

        Assert.That(line, Does.Contain("-f rawvideo"));
        Assert.That(line, Does.Contain("-pixel_format rgba"));
        Assert.That(line, Does.Contain("-video_size 1080x1920"));
        Assert.That(line, Does.Contain("-framerate 15"));
        Assert.That(line, Does.Contain("-i -"), "The frames arrive on standard input.");
        Assert.That(line, Does.EndWith("/tmp/out.mp4"));
    }

    /// <summary>
    /// 4:2:0 is not a preference. H.264 in anything else is refused outright by most phone decoders
    /// and by every social platform, so a clip that skipped it would encode perfectly and then not
    /// play anywhere it was sent.
    /// </summary>
    [Test]
    public void TheVideoIsSomethingAPhoneWillActuallyPlay()
    {
        string line = FfmpegEncoder.Arguments(1080, 1920, 15, "out.mp4");

        Assert.That(line, Does.Contain("-c:v libx264"));
        Assert.That(line, Does.Contain("-pix_fmt yuv420p"));
        Assert.That(line, Does.Contain("-movflags +faststart"));
    }

    [Test]
    public void AnOutputPathWithASpaceInItIsQuoted()
    {
        string line = FfmpegEncoder.Arguments(16, 16, 15, @"C:\Users\Someone Else\out.mp4");

        Assert.That(line, Does.EndWith("\"C:\\Users\\Someone Else\\out.mp4\""));
    }

    // ---- Finding one ---------------------------------------------------------------------

    [Test]
    public void TheEnvironmentVariableBeatsEverythingElse()
    {
        string pretend = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pretend-ffmpeg");
        File.WriteAllText(pretend, "not really");

        try
        {
            Environment.SetEnvironmentVariable(FfmpegEncoder.PathVariable, pretend);

            Assert.That(FfmpegEncoder.Find(), Is.EqualTo(pretend));
        }
        finally
        {
            File.Delete(pretend);
        }
    }

    [Test]
    public void NoFfmpegAnywhereIsNotAnError()
    {
        Environment.SetEnvironmentVariable(FfmpegEncoder.PathVariable, null);
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        Assert.That(FfmpegEncoder.Find(), Is.Null);
    }

    /// <summary>
    /// The fallback is the whole reason the design can promise clips at all, so the choice has to
    /// come back with something whatever the machine has. Nothing here is allowed to return null.
    /// </summary>
    [Test]
    public void WithoutAVideoEncoderTheChoiceIsTheAnimatedPng()
    {
        Environment.SetEnvironmentVariable(FfmpegEncoder.PathVariable, null);
        Environment.SetEnvironmentVariable("PATH", string.Empty);
        Clips.Reconsider();

        using IClipEncoder chosen = Clips.Choose();

        Assert.That(chosen.Format, Is.EqualTo(ClipFormat.AnimatedPng));
        Assert.That(chosen.Streams, Is.False);
    }

    /// <summary>
    /// A binary that is not ffmpeg fails the probe rather than the clip. The probe exists because a
    /// streaming encoder does not keep its frames: one that broke halfway would have nothing left to
    /// fall back to but the whole re-simulation again.
    /// </summary>
    [Test]
    public void SomethingThatIsNotAnEncoderDoesNotPassTheProbe()
    {
        Assert.That(FfmpegEncoder.Probe("no-such-binary-anywhere"), Is.False);
    }

    [Test]
    public void AnEncoderWithNoBinaryBehindItFinishesWithNothing()
    {
        using FfmpegEncoder encoder = new FfmpegEncoder("no-such-binary-anywhere");

        encoder.Begin(16, 16, 15);
        encoder.Add(new byte[16 * 16 * 4]);

        Assert.That(encoder.Finish(), Is.Null);
    }

    // ---- The fallback ---------------------------------------------------------------------

    [Test]
    public void TheAnimatedPngEncoderWritesWhatTheWriterWrites()
    {
        byte[] first = Frame(4, 4, 0x11);
        byte[] second = Frame(4, 4, 0x22);

        using ApngEncoder encoder = new ApngEncoder();

        encoder.Begin(4, 4, 15);
        encoder.Add(first);
        encoder.Add(second);

        ClipFile? made = encoder.Finish();

        Assert.That(made, Is.Not.Null);
        Assert.That(made!.Format, Is.EqualTo(ClipFormat.AnimatedPng));
        Assert.That(made.Frames, Is.EqualTo(2));
        Assert.That(
            made.Bytes,
            Is.EqualTo(Apng.Write(4, 4, new[] { first, second }, 15)));
    }

    [Test]
    public void AnEncoderGivenNoFramesMakesNothing()
    {
        using ApngEncoder encoder = new ApngEncoder();

        encoder.Begin(4, 4, 15);

        Assert.That(encoder.Finish(), Is.Null);
    }

    // ---- The real thing ---------------------------------------------------------------------

    /// <summary>
    /// The only test that can say the command line is right, because ffmpeg is the only thing that
    /// knows. Skipped where there is no ffmpeg, which on this project means it runs on the Linux CI
    /// runner and on any machine with one installed.
    /// </summary>
    [Test]
    public void FfmpegTurnsRawFramesIntoAnMp4()
    {
        string binary = Available();

        using FfmpegEncoder encoder = new FfmpegEncoder(binary);

        encoder.Begin(64, 64, 15);

        // Something that actually changes between frames, so the encoder has motion to compress and
        // cannot quietly produce a one-frame file that happens to pass every other assertion.
        for (int frame = 0; frame < 15; frame++)
        {
            encoder.Add(Frame(64, 64, (byte)(frame * 17)));
        }

        ClipFile? made = encoder.Finish();

        Assert.That(made, Is.Not.Null, "ffmpeg produced nothing.");
        Assert.That(made!.Format, Is.EqualTo(ClipFormat.Mp4));
        Assert.That(made.Frames, Is.EqualTo(15));
        Assert.That(made.Bytes, Has.Length.GreaterThan(0));

        // The first box of an MP4 is ftyp, four bytes of length then the four characters, and
        // faststart is what guarantees it is first rather than somewhere after the video data.
        Assert.That(
            System.Text.Encoding.ASCII.GetString(made.Bytes, 4, 4),
            Is.EqualTo("ftyp"),
            "That is not an MP4.");

        TestContext.Out.WriteLine(
            $"ffmpeg: {made.Bytes.Length} bytes in {made.Took.TotalMilliseconds:F0} ms for 15 frames at 64x64.");
    }

    /// <summary>
    /// The probe has to agree with reality, since it is what decides whether a player gets a video at
    /// all. A machine with a working ffmpeg that failed the probe would silently fall back for ever.
    /// </summary>
    [Test]
    public void ARealFfmpegPassesTheProbeAndIsChosen()
    {
        string binary = Available();

        Assert.That(FfmpegEncoder.Probe(binary), Is.True);

        Environment.SetEnvironmentVariable(FfmpegEncoder.PathVariable, binary);
        Clips.Reconsider();

        using IClipEncoder chosen = Clips.Choose();

        Assert.That(chosen.Format, Is.EqualTo(ClipFormat.Mp4));
        Assert.That(chosen.Streams, Is.True, "A video encoder must not be asked to buffer.");
    }

    /// <summary>
    /// A rough sighting of the number the whole feature hangs on. This is a desktop and the plan's
    /// budget is a mid-range phone, so it proves nothing about the target: what it can catch is an
    /// answer that is an order of magnitude out, which would mean the approach is wrong rather than
    /// the hardware is slow.
    /// </summary>
    [Test]
    public void ThreeSecondsOfPortraitVideoEncodesInSomethingLikeTheBudget()
    {
        string binary = Available();

        using FfmpegEncoder encoder = new FfmpegEncoder(binary);

        byte[] frame = Frame(1080, 1920, 0x40);

        encoder.Begin(1080, 1920, 15);

        for (int count = 0; count < 45; count++)
        {
            encoder.Add(frame);
        }

        ClipFile? made = encoder.Finish();

        Assert.That(made, Is.Not.Null);

        TestContext.Out.WriteLine(
            $"clip budget: 45 frames of 1080x1920 in {made!.Took.TotalMilliseconds:F0} ms "
            + $"({made.Bytes.Length} bytes) on {Environment.OSVersion}.");

        Assert.That(
            made.Took, Is.LessThan(TimeSpan.FromSeconds(30)),
            "A desktop taking half a minute means the approach is wrong, not that the machine is slow.");
    }

    // ---- Helpers ------------------------------------------------------------------------

    /// <summary>An ffmpeg to test against, or a skipped test that says why there is not one.</summary>
    private static string Available()
    {
        string? binary = FfmpegEncoder.Find();

        if (binary is null)
        {
            Assert.Ignore(
                "No ffmpeg on this machine. The video encoder is covered on any runner that has one; "
                + $"point {FfmpegEncoder.PathVariable} at a binary to cover it here.");
        }

        return binary!;
    }

    /// <summary>A frame of one flat colour, which is all these need to be.</summary>
    private static byte[] Frame(int width, int height, byte shade)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int at = 0; at < pixels.Length; at += 4)
        {
            pixels[at] = shade;
            pixels[at + 1] = (byte)(255 - shade);
            pixels[at + 2] = shade;
            pixels[at + 3] = 255;
        }

        return pixels;
    }
}
