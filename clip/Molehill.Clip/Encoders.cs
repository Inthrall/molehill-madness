using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Molehill.Clip
{
    /// <summary>What a finished clip turned out to be, which decides where it can be shared.</summary>
    public enum ClipFormat
    {
        /// <summary>The fallback. Plays anywhere that shows a picture, and is enormous.</summary>
        AnimatedPng = 0,

        /// <summary>What every share sheet actually wants.</summary>
        Mp4 = 1,
    }

    /// <summary>
    /// A finished clip, and what it cost to make.
    /// </summary>
    /// <remarks>
    /// The cost is part of the result rather than something a caller times from outside, because the
    /// plan's open question about this whole pipeline is a number: five seconds on a mid-range phone.
    /// A clip that cannot say how long it took cannot answer it, and the answer decides whether the
    /// video path ships at all.
    /// </remarks>
    public sealed class ClipFile
    {
        public ClipFile(byte[] bytes, ClipFormat format, int frames, TimeSpan took)
        {
            Bytes = bytes;
            Format = format;
            Frames = frames;
            Took = took;
        }

        public byte[] Bytes { get; }

        public ClipFormat Format { get; }

        public int Frames { get; }

        /// <summary>How long the encoding took, not counting the re-simulation that fed it.</summary>
        public TimeSpan Took { get; }

        /// <summary>What to call the file, which is the only thing a share sheet reads.</summary>
        public string Extension => Format == ClipFormat.Mp4 ? "mp4" : "png";
    }

    /// <summary>
    /// Something that turns a run of frames into a file.
    /// </summary>
    /// <remarks>
    /// Three calls rather than one, and the shape is the whole point of this interface existing. The
    /// pipeline used to collect every frame into a list and hand the list over, which is what an
    /// animated PNG needs and what a hardware encoder makes unnecessary: an encoder consumes frames
    /// as they arrive and never holds more than one or two. A full portrait frame is eight megabytes
    /// of RGBA, so the difference between handing frames over one at a time and handing over a list
    /// of forty-five is the difference between two frames in memory and three hundred and seventy
    /// megabytes of them. No encoder that streams could be written against a signature that takes a
    /// list, so the signature had to change before any of them could exist.
    ///
    /// <see cref="Streams"/> is how a caller knows which kind it has, and it is not a curiosity: the
    /// buffering one needs its frames shrunk before they are handed over and the streaming one does
    /// not, so the quality of the clip depends on the answer.
    /// </remarks>
    public interface IClipEncoder : IDisposable
    {
        /// <summary>What comes out of it.</summary>
        ClipFormat Format { get; }

        /// <summary>Whether it consumes frames as they arrive rather than holding them all.</summary>
        bool Streams { get; }

        /// <summary>Starts a clip. Called once, before any frame.</summary>
        void Begin(int width, int height, int fps);

        /// <summary>One frame, as RGBA rows top to bottom, exactly width * height * 4 bytes.</summary>
        void Add(byte[] rgba);

        /// <summary>The finished clip, or null if it could not be made.</summary>
        ClipFile? Finish();
    }

    /// <summary>
    /// The fallback encoder: holds every frame and writes an animated PNG at the end.
    /// </summary>
    /// <remarks>
    /// Holding them all is not a shortcoming of this class, it is what the format is. An APNG's
    /// frames are compressed independently but the file cannot be started until the count is known,
    /// since the animation control chunk carries it and comes before the first frame. So this one
    /// buffers, says so, and is fed shrunken frames by whoever is driving it.
    /// </remarks>
    public sealed class ApngEncoder : IClipEncoder
    {
        private readonly List<byte[]> _frames = new List<byte[]>();

        private int _width;
        private int _height;
        private int _fps;

        public ClipFormat Format => ClipFormat.AnimatedPng;

        public bool Streams => false;

        public void Begin(int width, int height, int fps)
        {
            _width = width;
            _height = height;
            _fps = fps;
            _frames.Clear();
        }

        public void Add(byte[] rgba) => _frames.Add(rgba);

        public ClipFile? Finish()
        {
            if (_frames.Count == 0)
            {
                return null;
            }

            long started = Stopwatch.GetTimestamp();
            byte[] written = Apng.Write(_width, _height, _frames, _fps);

            return new ClipFile(
                written, ClipFormat.AnimatedPng, _frames.Count, Stopwatch.GetElapsedTime(started));
        }

        public void Dispose() => _frames.Clear();
    }

    /// <summary>
    /// The desktop video encoder: raw frames into ffmpeg's mouth, an MP4 out the other end.
    /// </summary>
    /// <remarks>
    /// The plan names three encoders, one per platform, and this is the one that can be written and
    /// run from a development machine. Android's MediaCodec needs a Godot plugin in Java, which needs
    /// a device to mean anything, and iOS is out of scope entirely. So this is the encoder that
    /// proves the seam works and gives the rest of the pipeline something real to push frames into.
    ///
    /// Raw RGBA down a pipe rather than files on disk, because the frames exist in memory already and
    /// writing forty-five PNGs to a temp directory to have ffmpeg read them back is work nobody
    /// needs. The output is a file rather than the other end of the pipe, because an MP4 is muxed
    /// with an index that gets written last and then moved to the front, and neither of those is
    /// something a stream can do.
    ///
    /// It is worth being clear about what "bundled ffmpeg" means for the licence, because it decides
    /// how this ships rather than how it works: a build of ffmpeg without any GPL component is LGPL,
    /// which a shipped game can carry as long as it says so and the binary stays replaceable, and it
    /// stays replaceable here because this looks it up by path rather than linking it. Nothing in
    /// this class needs to change if the answer comes back as "ship a build we made ourselves".
    /// </remarks>
    public sealed class FfmpegEncoder : IClipEncoder
    {
        /// <summary>Set this to point the encoder at a binary that is not on the path.</summary>
        public const string PathVariable = "MOLEHILL_FFMPEG";

        /// <summary>
        /// How long to wait for the encoder to finish after the last frame has gone in.
        /// </summary>
        /// <remarks>
        /// Thirty seconds for three seconds of video, which is six times the budget the whole feature
        /// is allowed on a phone. It is a deadlock guard rather than a performance limit: if ffmpeg
        /// has not finished by then it is not going to, and a share button that never comes back is
        /// worse than one that says no.
        /// </remarks>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        private readonly string _binary;

        private Process? _ffmpeg;
        private System.Threading.Tasks.Task<string>? _complaints;
        private System.Threading.Tasks.Task<string>? _chatter;
        private string? _output;
        private long _started;
        private int _frames;
        private bool _broken;

        public FfmpegEncoder(string binary) => _binary = binary;

        /// <summary>
        /// Whatever the encoder said on its way past, kept for whoever is debugging it.
        /// </summary>
        /// <remarks>
        /// This project has no logger below the client, so the alternative to a property is throwing
        /// the only explanation away. An encoder that refuses a clip and cannot say why is the worst
        /// version of this feature to be holding at three in the morning.
        /// </remarks>
        public string? Trouble { get; private set; }

        public ClipFormat Format => ClipFormat.Mp4;

        public bool Streams => true;

        /// <summary>
        /// Where ffmpeg is, or null if this machine has not got one.
        /// </summary>
        /// <remarks>
        /// Three places, in the order that lets a decision override a default: the environment
        /// variable, so a developer can point at a particular build without changing anything; the
        /// directory beside the game, which is where a shipped copy would sit; and the path, which is
        /// what a development machine with ffmpeg installed already has.
        /// </remarks>
        public static string? Find(string? beside = null)
        {
            string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

            string? named = Environment.GetEnvironmentVariable(PathVariable);

            if (!string.IsNullOrWhiteSpace(named) && File.Exists(named))
            {
                return named;
            }

            if (beside is not null)
            {
                string bundled = Path.Combine(beside, name);

                if (File.Exists(bundled))
                {
                    return bundled;
                }
            }

            string? paths = Environment.GetEnvironmentVariable("PATH");

            if (paths is null)
            {
                return null;
            }

            foreach (string directory in paths.Split(Path.PathSeparator))
            {
                if (directory.Length == 0)
                {
                    continue;
                }

                string candidate;

                try
                {
                    candidate = Path.Combine(directory, name);
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters no path can hold. Not this machine's problem to
                    // fix, and certainly not a reason to fail to find ffmpeg somewhere else on it.
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// The command line, built where it can be read back.
        /// </summary>
        /// <remarks>
        /// A static that returns the arguments, rather than a private that builds them on the way
        /// into a process, so a test can assert the exact line without launching anything. Nearly
        /// every mistake available here is in this string: a pixel format that does not match what is
        /// being pushed in, a size the frames are not, a profile a phone will not play.
        ///
        /// yuv420p and the baseline-friendly settings are not decoration. H.264 in anything other
        /// than 4:2:0 is refused outright by most phone decoders and by every social platform, and a
        /// clip that will not play is worse than no clip. faststart moves the index to the front,
        /// which is what lets a viewer start playing before the whole file has arrived.
        /// </remarks>
        public static string Arguments(int width, int height, int fps, string output)
        {
            string size = string.Create(
                CultureInfo.InvariantCulture, $"{width}x{height}");
            string rate = fps.ToString(CultureInfo.InvariantCulture);

            return string.Join(
                ' ',
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "rgba",
                "-video_size", size,
                "-framerate", rate,
                "-i", "-",
                "-c:v", "libx264",
                "-preset", "veryfast",
                "-crf", "23",
                "-pix_fmt", "yuv420p",
                "-movflags", "+faststart",
                Quoted(output));
        }

        /// <summary>
        /// Whether this binary can actually encode, asked with a clip too small to cost anything.
        /// </summary>
        /// <remarks>
        /// Asked once, before the real clip, and that ordering is the point. Frames go into a
        /// streaming encoder and are not kept, so an encoder that fails halfway through has lost
        /// them: there is nothing left to fall back to except re-simulating the round again. Two
        /// sixteen-pixel frames answer the same question for a millisecond, and every way this can be
        /// broken shows up in them. A binary that is not ffmpeg at all, one built without libx264,
        /// one that cannot write to the temp directory: all of them fail the probe rather than the
        /// clip somebody was waiting for.
        /// </remarks>
        public static bool Probe(string binary)
        {
            using FfmpegEncoder encoder = new FfmpegEncoder(binary);

            try
            {
                // Sixteen by sixteen because H.264 wants even dimensions and a macroblock is sixteen.
                encoder.Begin(16, 16, 15);
                encoder.Add(new byte[16 * 16 * 4]);
                encoder.Add(new byte[16 * 16 * 4]);

                return encoder.Finish() is not null;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public void Begin(int width, int height, int fps)
        {
            _output = Path.Combine(
                Path.GetTempPath(),
                string.Create(CultureInfo.InvariantCulture, $"molehill-{Guid.NewGuid():N}.mp4"));

            ProcessStartInfo start = new ProcessStartInfo(_binary, Arguments(width, height, fps, _output))
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            _started = Stopwatch.GetTimestamp();
            _frames = 0;
            _broken = false;

            try
            {
                _ffmpeg = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No such binary, or one this machine will not run. Not an exception worth throwing
                // on: the caller's answer to every failure here is the same, which is the fallback.
                _ffmpeg = null;
                _broken = true;
            }

            if (_ffmpeg is null)
            {
                _broken = true;

                return;
            }

            // Drained from here rather than from Finish, and this ordering is the whole of the fix.
            // A child process writing into a pipe nobody is reading blocks once the pipe buffer
            // fills, which on this machine is four kilobytes; blocked, it stops reading its own
            // standard input, and the next frame pushed into it never returns. Since the frames are
            // pushed on the game's main thread, that is not a slow share button, it is the whole
            // game stopped for ever.
            //
            // Four kilobytes is not much. This encoder's own notes point out that some builds of
            // ffmpeg print a deprecation notice whatever the log level says, and any per-frame
            // warning clears it long before the last frame. Reading both from the start costs two
            // tasks that spend their lives asleep.
            _complaints = _ffmpeg.StandardError.ReadToEndAsync();
            _chatter = _ffmpeg.StandardOutput.ReadToEndAsync();
        }

        public void Add(byte[] rgba)
        {
            if (_broken || _ffmpeg is null || rgba is null)
            {
                return;
            }

            try
            {
                _ffmpeg.StandardInput.BaseStream.Write(rgba, 0, rgba.Length);
                _frames++;
            }
            catch (IOException)
            {
                // ffmpeg closed the pipe, which means it has already given up on something. Stop
                // pushing and let Finish report it: writing into a broken pipe frame after frame
                // would turn one failure into forty-five.
                _broken = true;
            }
        }

        public ClipFile? Finish()
        {
            if (_ffmpeg is null)
            {
                Clean();

                return null;
            }

            try
            {
                _ffmpeg.StandardInput.BaseStream.Flush();
                _ffmpeg.StandardInput.Close();
            }
            catch (IOException)
            {
                _broken = true;
            }

            // Both streams have been draining since Begin, so nothing here can be waiting on a
            // pipe that filled up during the frames. All that is left is to collect what they said.
            if (!_ffmpeg.WaitForExit(Patience))
            {
                Kill();
                Clean();

                return null;
            }

            // Both finish the moment the process does, since its pipes close with it.
            Trouble = _complaints?.GetAwaiter().GetResult();
            _ = _chatter?.GetAwaiter().GetResult();

            // Not gated on what came out of standard error, deliberately. Some builds of ffmpeg
            // print a deprecation notice whatever the log level is set to, and refusing a perfectly
            // good MP4 because the encoder grumbled on the way past would make the video path fail
            // on exactly the machines that have an old ffmpeg installed. The exit code and a file
            // with bytes in it are what say it worked.
            bool worked = _ffmpeg.ExitCode == 0 && !_broken;
            ClipFile? made = null;

            if (worked && _output is not null && File.Exists(_output))
            {
                byte[] bytes = File.ReadAllBytes(_output);

                if (bytes.Length > 0 && _frames > 0)
                {
                    made = new ClipFile(
                        bytes, ClipFormat.Mp4, _frames, Stopwatch.GetElapsedTime(_started));
                }
            }

            Clean();

            return made;
        }

        public void Dispose()
        {
            Kill();
            Clean();
        }

        private void Kill()
        {
            if (_ffmpeg is null)
            {
                return;
            }

            try
            {
                if (!_ffmpeg.HasExited)
                {
                    _ffmpeg.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone between the question and the answer, which is the outcome wanted.
            }
        }

        private void Clean()
        {
            _ffmpeg?.Dispose();
            _ffmpeg = null;

            if (_output is null)
            {
                return;
            }

            try
            {
                File.Delete(_output);
            }
            catch (IOException)
            {
                // A temp file left behind is untidy rather than wrong, and the operating system
                // clears them out. Failing a clip that was made because its scratch file would not
                // delete would be the wrong way round.
            }

            _output = null;
        }

        private static string Quoted(string path) =>
            path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
    }

    /// <summary>
    /// Which encoder this machine can use.
    /// </summary>
    /// <remarks>
    /// Answered once and remembered, because the answer involves launching a process and it cannot
    /// change while the game is running. The order is the plan's: a video encoder if there is one,
    /// the animated PNG if there is not, and the second of those is guaranteed, so this never returns
    /// nothing. A player never finds out which they got except by the size of the file.
    /// </remarks>
    public static class Clips
    {
        private static bool _looked;
        private static string? _ffmpeg;

        /// <summary>
        /// The best encoder available, having checked that it works before handing it over.
        /// </summary>
        /// <param name="beside">
        /// Where a bundled ffmpeg would sit, which is the directory the game is running from. Null
        /// when the caller has no opinion, which is every caller except the client.
        /// </param>
        public static IClipEncoder Choose(string? beside = null)
        {
            if (!_looked)
            {
                _looked = true;

                string? found = FfmpegEncoder.Find(beside);

                _ffmpeg = found is not null && FfmpegEncoder.Probe(found) ? found : null;
            }

            return _ffmpeg is null ? new ApngEncoder() : new FfmpegEncoder(_ffmpeg);
        }

        /// <summary>Makes the next <see cref="Choose"/> look again. For tests.</summary>
        public static void Reconsider()
        {
            _looked = false;
            _ffmpeg = null;
        }
    }
}
