using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// Linking an address to an account, and the several ways that would be misused.
/// </summary>
/// <remarks>
/// A verification endpoint is one of the classic ways to turn a service into somebody else's
/// problem: anybody who can call it can cause mail to be sent to an address they do not own. So most
/// of what is tested here is refusal, and each refusal is a thing the endpoint would otherwise be
/// used for rather than a hypothetical.
///
/// The sending itself is tested against a real SMTP server stood up in this process, which is the
/// difference between this and the push notifications. Firebase could only ever be tested up to the
/// socket because there is no Firebase project; SMTP is a protocol rather than a vendor, so the
/// bytes can be read as they arrive and there is nothing left to hedge about.
/// </remarks>
[TestFixture]
public sealed class EmailTests
{
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp() => _now = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

    // ---- What counts as an address ----------------------------------------------------------

    [TestCase("somebody@example.com")]
    [TestCase("some.body+molehill@example.co.nz")]
    public void AnAddressThatCouldExistIsWorthTrying(string address)
    {
        Assert.That(Emails.Plausible(address), Is.True);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("nobody")]
    [TestCase("@example.com")]
    [TestCase("somebody@")]
    [TestCase("two@at@example.com")]
    [TestCase("somebody@localhost")]
    [TestCase("some body@example.com")]
    public void SomethingThatIsNotAnAddressIsNotSentTo(string address)
    {
        Assert.That(Emails.Plausible(address), Is.False);
    }

    /// <summary>
    /// One address, one account. Compared in a tidied form, or the uniqueness rule is one anybody
    /// can walk around by holding down shift.
    /// </summary>
    [Test]
    public void CapitalisingItDifferentlyIsTheSameAddress()
    {
        Assert.That(Emails.Tidy("  SomeBody@Example.COM "), Is.EqualTo("somebody@example.com"));
    }

    [Test]
    public void ACodeIsSixCharactersAPhoneCanTypeWithoutGuessing()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            string code = Emails.Code();

            Assert.That(code, Has.Length.EqualTo(Emails.CodeLength));
            Assert.That(code, Does.Not.Contain("I"));
            Assert.That(code, Does.Not.Contain("O"));
        }
    }

    // ---- The claim ---------------------------------------------------------------------------

    [Test]
    public void AClaimRunsOutOfTimeAndOfGuesses()
    {
        EmailClaim claim = new EmailClaim(
            "account", "somebody@example.com", "ABCDEF", _now, _now + Emails.Lasts, 0);

        Assert.That(Emails.Live(claim, _now), Is.True);
        Assert.That(Emails.Live(claim, _now + Emails.Lasts), Is.False);
        Assert.That(Emails.Live(claim with { Tries = Emails.Guesses }, _now), Is.False);
        Assert.That(Emails.Live(null, _now), Is.False);
    }

    [Test]
    public void TheCodeIsComparedWithoutCaringAboutCaseOrSpaces()
    {
        EmailClaim claim = new EmailClaim(
            "account", "somebody@example.com", "ABCDEF", _now, _now + Emails.Lasts, 0);

        Assert.That(Emails.Matches(claim, "abcdef"), Is.True);
        Assert.That(Emails.Matches(claim, " ABCDEF "), Is.True);
        Assert.That(Emails.Matches(claim, "ABCDEG"), Is.False);
        Assert.That(Emails.Matches(claim, "ABCDE"), Is.False);
        Assert.That(Emails.Matches(claim, null), Is.False);
    }

    // ---- Who may be asked ---------------------------------------------------------------------

    /// <summary>
    /// The design gives under-threshold accounts "no email collection". Not collection with a
    /// consent box, not collection deleted later, and not collection with a parental approval
    /// attached: an approval about playing with strangers says nothing about handing us an address.
    /// Refused by the relay, before anything is sent.
    /// </summary>
    [TestCase(AgeBand.Child)]
    [TestCase(AgeBand.Unknown)]
    public async Task NobodyUnderTheThresholdIsAskedForAnAddress(AgeBand band)
    {
        using Posted post = new Posted();
        using RelayFactory relay = Relay($"email-young-{Guid.NewGuid():N}", post);
        using HttpClient client = relay.CreateClient();

        Opened account = await Account(client, band);

        HttpResponseMessage refused = await Ask(client, account, "somebody@example.com");

        Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(post.Sent, Is.Empty, "Nothing should have left the building.");
    }

    [Test]
    public async Task AnAdultCanLinkAnAddressAndProveIt()
    {
        using Posted post = new Posted();
        using RelayFactory relay = Relay($"email-ok-{Guid.NewGuid():N}", post);
        using HttpClient client = relay.CreateClient();

        Opened account = await Account(client, AgeBand.Adult);

        Assert.That(
            (await Ask(client, account, "Somebody@Example.com")).StatusCode,
            Is.EqualTo(HttpStatusCode.Accepted));

        Assert.That(post.Sent, Has.Count.EqualTo(1));
        Assert.That(post.Sent[0].Address, Is.EqualTo("somebody@example.com"));

        Assert.That(
            (await Prove(client, account, post.Sent[0].Code)).StatusCode,
            Is.EqualTo(HttpStatusCode.NoContent));

        // And it stays proved, so the code cannot be replayed to relink something else later.
        Assert.That(
            (await Prove(client, account, post.Sent[0].Code)).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task AWrongCodeRunsOutOfGuesses()
    {
        using Posted post = new Posted();
        using RelayFactory relay = Relay($"email-guess-{Guid.NewGuid():N}", post);
        using HttpClient client = relay.CreateClient();

        Opened account = await Account(client, AgeBand.Adult);

        await Ask(client, account, "somebody@example.com");

        for (int attempt = 0; attempt < Emails.Guesses; attempt++)
        {
            Assert.That(
                (await Prove(client, account, "ZZZZZZ")).StatusCode,
                Is.EqualTo(HttpStatusCode.Forbidden));
        }

        // Out of guesses, so the claim is gone and even the right code is no use.
        Assert.That(
            (await Prove(client, account, post.Sent[0].Code)).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// Without this, one account is a button that posts mail to any address as fast as it can be
    /// called, and this relay is the tool rather than the target.
    /// </summary>
    [Test]
    public async Task AskingAgainStraightAwayIsRefused()
    {
        using Posted post = new Posted();
        using RelayFactory relay = Relay($"email-fast-{Guid.NewGuid():N}", post);
        using HttpClient client = relay.CreateClient();

        Opened account = await Account(client, AgeBand.Adult);

        await Ask(client, account, "somebody@example.com");

        Assert.That(
            (await Ask(client, account, "somebody-else@example.com")).StatusCode,
            Is.EqualTo(HttpStatusCode.TooManyRequests));

        Assert.That(post.Sent, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// An address is how an account is recovered, so letting a second account claim one would be
    /// letting somebody take the first account over.
    /// </summary>
    [Test]
    public async Task AnAddressAlreadyLinkedIsNotMoved()
    {
        using Posted post = new Posted();
        using RelayFactory relay = Relay($"email-taken-{Guid.NewGuid():N}", post);
        using HttpClient client = relay.CreateClient();

        Opened first = await Account(client, AgeBand.Adult);
        Opened second = await Account(client, AgeBand.Adult);

        await Ask(client, first, "somebody@example.com");
        await Prove(client, first, post.Sent[0].Code);

        Assert.That(
            (await Ask(client, second, "SOMEBODY@example.com")).StatusCode,
            Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task AnAddressThatIsNotOneIsRefusedBeforeAnythingIsSent()
    {
        using Posted post = new Posted();
        using RelayFactory relay = Relay($"email-junk-{Guid.NewGuid():N}", post);
        using HttpClient client = relay.CreateClient();

        Opened account = await Account(client, AgeBand.Adult);

        Assert.That(
            (await Ask(client, account, "not an address")).StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(post.Sent, Is.Empty);
    }

    // ---- Actually sending ----------------------------------------------------------------------

    /// <summary>
    /// The real sender against a real SMTP server, standing in this process, reading the bytes as
    /// they arrive. Nothing here is stubbed except the far end being somebody's mail provider.
    /// </summary>
    [Test]
    public async Task TheSenderPostsACodeAnSmtpServerCanRead()
    {
        using FakeSmtp server = new FakeSmtp();

        server.Start();

        using SmtpEmailSender sender = new SmtpEmailSender(
            new SmtpSettings(
                "127.0.0.1", server.Port, Tls: false, string.Empty, string.Empty,
                "moles@molehill-madness.test"));

        Assert.That(await sender.Send("somebody@example.com", "BADGER"), Is.True);

        string message = await server.Received();

        Assert.That(message, Does.Contain("MAIL FROM:<moles@molehill-madness.test>"));
        Assert.That(message, Does.Contain("RCPT TO:<somebody@example.com>"));
        Assert.That(message, Does.Contain("BADGER"), "The code has to be in the message.");
        Assert.That(message, Does.Contain("Molehill Madness"));

        // Plain text, because a game that promises children no email collection should not be
        // sending adults tracking pixels either.
        Assert.That(message, Does.Not.Contain("text/html"));
    }

    [Test]
    public async Task ASenderWithNothingListeningSaysSoRatherThanThrowing()
    {
        using SmtpEmailSender sender = new SmtpEmailSender(
            new SmtpSettings(
                "127.0.0.1", 1, Tls: false, string.Empty, string.Empty, "moles@example.com"));

        Assert.That(await sender.Send("somebody@example.com", "BADGER"), Is.False);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static RelayFactory Relay(string name, IEmailSender post) =>
        new RelayFactory(name, services: services => services.AddSingleton(post));

    private static async Task<Opened> Account(HttpClient client, AgeBand band)
    {
        HttpResponseMessage made = await client.PostAsJsonAsync("/accounts", new OpenAccount(band));

        return (await made.Content.ReadFromJsonAsync<Opened>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static Task<HttpResponseMessage> Ask(
        HttpClient client, Opened account, string address) =>
        client.SendAsync(Owned(
            new HttpRequestMessage(HttpMethod.Post, "/accounts/email")
            {
                Content = JsonContent.Create(new ClaimEmail(address)),
            },
            account));

    private static Task<HttpResponseMessage> Prove(HttpClient client, Opened account, string code) =>
        client.SendAsync(Owned(
            new HttpRequestMessage(HttpMethod.Put, "/accounts/email")
            {
                Content = JsonContent.Create(new ProveEmail(code)),
            },
            account));

    private static HttpRequestMessage Owned(HttpRequestMessage request, Opened account)
    {
        request.Headers.Add("X-Account", account.Id);
        request.Headers.Add("X-Account-Secret", account.Secret);

        return request;
    }

    /// <summary>A sender that keeps what it was asked to post, so a test can read the code.</summary>
    private sealed class Posted : IEmailSender, IDisposable
    {
        public List<(string Address, string Code)> Sent { get; } =
            new List<(string Address, string Code)>();

        public Task<bool> Send(string address, string code, CancellationToken cancel = default)
        {
            Sent.Add((address, code));

            return Task.FromResult(true);
        }

        public void Dispose() => Sent.Clear();
    }

    /// <summary>
    /// Enough of an SMTP server to receive one message and hand back what arrived.
    /// </summary>
    /// <remarks>
    /// Forty lines, because SMTP for one plain unauthenticated message is a handful of numbered
    /// replies and a full stop on its own line. It advertises nothing, so the client does not try to
    /// negotiate TLS or authentication, which keeps this about whether the message is right rather
    /// than about reimplementing a mail server.
    /// </remarks>
    private sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);

        /// <summary>
        /// Completed when a whole message has arrived, rather than when the client hangs up.
        /// </summary>
        /// <remarks>
        /// SmtpClient keeps its connection open after a send and only says QUIT when it is disposed,
        /// so a test that waited for the conversation to end would be waiting for something that has
        /// not happened yet. A message is finished at the full stop on its own line, which is the
        /// only framing SMTP has, and that is the moment worth waiting for.
        /// </remarks>
        private readonly TaskCompletionSource<string> _message =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(Talk);
        }

        public async Task<string> Received() =>
            await _message.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Dispose() => _listener.Dispose();

        private async Task Talk()
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();
            using StreamReader reading = new StreamReader(stream, Encoding.ASCII);
            using StreamWriter writing = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

            StringBuilder heard = new StringBuilder();

            await writing.WriteAsync("220 localhost molehill test\r\n");

            bool body = false;

            while (await reading.ReadLineAsync() is string line)
            {
                heard.Append(line).Append('\n');

                if (body)
                {
                    // A full stop on its own line is the end of the message, and it is the only
                    // framing SMTP has.
                    if (line == ".")
                    {
                        body = false;
                        await writing.WriteAsync("250 OK\r\n");
                        _message.TrySetResult(heard.ToString());
                    }

                    continue;
                }

                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    // Nothing advertised, so nothing is negotiated.
                    await writing.WriteAsync("250 localhost\r\n");
                }
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    body = true;
                    await writing.WriteAsync("354 Go ahead\r\n");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writing.WriteAsync("221 Bye\r\n");
                    break;
                }
                else
                {
                    await writing.WriteAsync("250 OK\r\n");
                }
            }

            // In case the client hung up without ever finishing a message, so a test waiting on one
            // fails with its own assertion rather than with a timeout that explains nothing.
            _message.TrySetResult(heard.ToString());
        }
    }
}
