using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Molehill.Online
{
    /// <summary>
    /// Listens for the relay saying something has happened, so the client can stop guessing.
    /// </summary>
    /// <remarks>
    /// This rings; it does not deliver. Nothing that matters arrives over the socket, so a notice
    /// means only "go and ask", and asking is the same call the client would have made on its own a
    /// moment later. That is what makes the whole thing safe to lose: a phone with no socket, or one
    /// whose socket has just dropped in a tunnel, plays exactly as it did before this existed, a beat
    /// slower. The socket is never the way a round arrives.
    ///
    /// Any notice rings it, including one this build has never heard of. The alternative is a client
    /// that ignores a future notice kind and waits for its slow poll instead, and since the only cost
    /// of ringing is one HTTP call that finds nothing new, the generous direction is the cheap one.
    ///
    /// Reconnects for ever while it is running. A dropped socket is the normal case rather than the
    /// exception on a phone, so there is no attempt count and no giving up: the poll underneath is
    /// still going, so a doorbell that is out of action costs latency and nothing at all else.
    /// </remarks>
    public sealed class LiveDoorbell : IDisposable
    {
        /// <summary>How to open a socket, so a test can hand over one that goes somewhere else.</summary>
        public delegate Task<WebSocket> Opener(Uri where, string token, CancellationToken cancel);

        /// <summary>How long to wait before trying again after a socket falls over.</summary>
        private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(3);

        private readonly Uri _where;
        private readonly string _token;
        private readonly Opener _open;
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

        private Task? _listening;
        private int _rang;
        private int _connected;

        public LiveDoorbell(Uri relay, string code, string token, Opener? open = null)
        {
            ArgumentNullException.ThrowIfNull(relay);

            _where = Address(relay, code);
            _token = token;
            _open = open ?? Dial;
        }

        /// <summary>Whether there is a socket up right now.</summary>
        public bool Listening => Volatile.Read(ref _connected) == 1;

        /// <summary>What went wrong last time, for a log rather than for a player.</summary>
        public string Trouble { get; private set; } = string.Empty;

        /// <summary>Where the socket for a match lives, given where the relay does.</summary>
        /// <remarks>
        /// The scheme is swapped rather than configured, since a socket to a relay reached over
        /// HTTPS has to be wss and one to a relay reached over plain HTTP has to be ws. Getting that
        /// wrong is a connection refused on a device and nothing at all on a desktop, which is the
        /// worst way round for finding out.
        /// </remarks>
        public static Uri Address(Uri relay, string code)
        {
            ArgumentNullException.ThrowIfNull(relay);

            UriBuilder built = new UriBuilder(new Uri(relay, $"matches/{code}/live"))
            {
                Scheme = relay.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            };

            return built.Uri;
        }

        /// <summary>Starts listening. Calling it twice does nothing the first call did not.</summary>
        public void Start()
        {
            if (_listening is not null)
            {
                return;
            }

            _listening = Task.Run(() => Listen(_stopping.Token));
        }

        /// <summary>
        /// Whether anything has happened since the last time this was asked.
        /// </summary>
        /// <remarks>
        /// Consumed on read, and interlocked because the answer is written by a socket on the thread
        /// pool and read by a game loop on the main thread. Several notices between two frames ring
        /// once: the client's answer to any of them is to ask the relay, and asking twice for the
        /// same reason is a wasted call.
        /// </remarks>
        public bool Rang() => Interlocked.Exchange(ref _rang, 0) == 1;

        public void Dispose()
        {
            if (!_stopping.IsCancellationRequested)
            {
                _stopping.Cancel();
            }

            _stopping.Dispose();
        }

        private async Task Listen(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                WebSocket? socket = null;

                try
                {
                    socket = await _open(_where, _token, cancel).ConfigureAwait(false);

                    Volatile.Write(ref _connected, 1);

                    await Hear(socket, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (WebSocketException trouble)
                {
                    Trouble = trouble.Message;
                }
                catch (InvalidOperationException trouble)
                {
                    // What the handshake throws when the relay answers with anything but an upgrade,
                    // which on this endpoint means the seat token was not accepted.
                    Trouble = trouble.Message;
                }
                catch (System.Net.Http.HttpRequestException trouble)
                {
                    Trouble = trouble.Message;
                }
                finally
                {
                    Volatile.Write(ref _connected, 0);
                    socket?.Dispose();
                }

                try
                {
                    await Task.Delay(Backoff, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private async Task Hear(WebSocket socket, CancellationToken cancel)
        {
            byte[] buffer = new byte[512];

            while (socket.State == WebSocketState.Open && !cancel.IsCancellationRequested)
            {
                WebSocketReceiveResult heard = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancel)
                    .ConfigureAwait(false);

                if (heard.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                // Not read. What was said does not change what to do about it, and a client that
                // parsed the notice would be a second place that has to know the relay's vocabulary.
                Interlocked.Exchange(ref _rang, 1);
            }
        }

        /// <summary>The real socket, for a real relay.</summary>
        private static async Task<WebSocket> Dial(Uri where, string token, CancellationToken cancel)
        {
            ClientWebSocket socket = new ClientWebSocket();

            socket.Options.SetRequestHeader("X-Seat-Token", token);

            try
            {
                await socket.ConnectAsync(where, cancel).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            return socket;
        }
    }
}
