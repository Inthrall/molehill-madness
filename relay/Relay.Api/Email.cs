using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Relay.Api;

/// <summary>An address somebody has claimed but not yet proved.</summary>
public sealed record EmailClaim(
    string Account,
    string Email,
    string Code,
    DateTimeOffset AskedAt,
    DateTimeOffset Expires,
    int Tries);

/// <summary>
/// Linking an address to an account, and the several ways that can be misused.
/// </summary>
/// <remarks>
/// The design gives the account "email or platform sign-in" so that a player is the same player on a
/// phone and a desktop, and gives under-threshold accounts "no email collection" at all. Both halves
/// live here, and the second is the important one: an address is refused for anybody who is not an
/// adult, by the relay, before anything is sent. Not collection with a consent box, not collection
/// deleted later, and not collection with a parental approval attached either, because an approval
/// about playing with strangers says nothing about handing us an address.
///
/// The rest of this exists because a verification endpoint is one of the classic ways to make a
/// service into somebody else's problem. Anybody who can call it can cause mail to be sent to an
/// address they do not own, so it is rate limited per account, the code expires, wrong guesses are
/// counted and run out, and an address already attached to an account is refused rather than moved.
/// None of those is theoretical: all four are what the endpoint would be used for.
/// </remarks>
public static class Emails
{
    /// <summary>How long a code is worth typing in.</summary>
    public static readonly TimeSpan Lasts = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long before the same account may ask for another one.
    /// </summary>
    /// <remarks>
    /// A minute. Without it, one account is a button that posts mail to any address as fast as it
    /// can be called, which makes this relay the tool rather than the target.
    /// </remarks>
    public static readonly TimeSpan Between = TimeSpan.FromMinutes(1);

    /// <summary>How many wrong codes a claim survives.</summary>
    public const int Guesses = 5;

    /// <summary>How many characters the code is.</summary>
    public const int CodeLength = 6;

    /// <summary>
    /// Whether that address is worth trying to send to.
    /// </summary>
    /// <remarks>
    /// Deliberately not a validator. The rules for what is a legal address are famously baroque, a
    /// regular expression that implements them is famously wrong, and the only thing that actually
    /// establishes an address exists is sending to it, which is exactly what this whole flow does.
    /// So this catches what is obviously not an address and lets the mail server be the judge of
    /// everything else.
    /// </remarks>
    public static bool Plausible(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 254)
        {
            return false;
        }

        int at = address.IndexOf('@', StringComparison.Ordinal);

        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1)
        {
            return false;
        }

        foreach (char character in address)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        // A domain with no dot in it is a local hostname, which is either a mistake or somebody
        // aiming this at something inside the network it runs in.
        return address.IndexOf('.', at) > at + 1;
    }

    /// <summary>
    /// The form an address is stored and compared in.
    /// </summary>
    /// <remarks>
    /// Lowercased and trimmed, so that one address cannot be linked to two accounts by capitalising
    /// it differently. The local part is technically case-sensitive and in practice never is; the
    /// alternative here is a uniqueness rule anybody can walk around, which is worse.
    /// </remarks>
    public static string Tidy(string address) =>
        address.Trim().ToLowerInvariant();

    /// <summary>
    /// A code to type back in.
    /// </summary>
    /// <remarks>
    /// From the same alphabet the game codes use, and for the same reason: it is read off a screen
    /// and typed on a phone, so I and O are out. From the cryptographic generator because a
    /// guessable one would let somebody link an address they never proved.
    /// </remarks>
    public static string Code()
    {
        char[] letters = new char[CodeLength];

        for (int at = 0; at < CodeLength; at++)
        {
            letters[at] = GameCode.Alphabet[
                RandomNumberGenerator.GetInt32(GameCode.Alphabet.Length)];
        }

        return new string(letters);
    }

    /// <summary>Whether an account is asking again too soon after the last time.</summary>
    public static bool TooSoon(EmailClaim? claim, DateTimeOffset now) =>
        claim is not null && now - claim.AskedAt < Between;

    /// <summary>
    /// Whether that address has been written to too recently, whoever did the asking.
    /// </summary>
    /// <remarks>
    /// The limit that actually protects anybody. Metering the account is metering the sender, and a
    /// sender can have as many accounts as it likes for nothing, so the per-account rule alone let
    /// one machine aim tens of thousands of codes a minute at one inbox. The address is the thing
    /// being spent here, so the address is what has to be metered.
    /// </remarks>
    public static bool WrittenToRecently(DateTimeOffset? lastAsked, DateTimeOffset now) =>
        lastAsked is DateTimeOffset when && now - when < Between;

    /// <summary>Whether a claim is still worth checking a code against.</summary>
    public static bool Live(EmailClaim? claim, DateTimeOffset now) =>
        claim is not null && now < claim.Expires && claim.Tries < Guesses;

    /// <summary>
    /// Whether a typed code matches, compared without leaking how long it took to find out.
    /// </summary>
    /// <remarks>
    /// A fixed-time comparison for six characters is close to superstition, and it costs one call.
    /// The habit is worth more than the instance: the next comparison somebody writes in this
    /// codebase will be against something that matters.
    /// </remarks>
    public static bool Matches(EmailClaim claim, string? typed)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (typed is null)
        {
            return false;
        }

        // Tidied before the lengths are compared, not after. A code read off a screen and pasted
        // back arrives with a space on the end often enough that comparing the raw length first
        // rejects the right answer, which is a maddening thing to be on the wrong side of.
        string tidy = typed.Trim().ToUpperInvariant();

        if (tidy.Length != claim.Code.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(tidy),
            System.Text.Encoding.ASCII.GetBytes(claim.Code));
    }
}

/// <summary>
/// Where a verification code actually goes.
/// </summary>
/// <remarks>
/// The same split the notifications use, and for the same reasons. Deciding who may be sent one and
/// what it says is the half with rules in it and is worth being certain about; handing bytes to a
/// mail server is somebody else's service. A development run wants the log, because the question
/// there is whether the right code reaches the right claim.
/// </remarks>
public interface IEmailSender
{
    Task<bool> Send(string address, string code, CancellationToken cancel = default);
}

/// <summary>The sender used until there is a mail server: it writes the code down.</summary>
/// <remarks>
/// Not a stub that throws and not one that silently does nothing. During development the interesting
/// question is whether a code was minted for the right claim, and a log line answers it exactly as
/// well as an inbox would.
/// </remarks>
public sealed partial class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _log;

    public LoggingEmailSender(ILogger<LoggingEmailSender> log) => _log = log;

    public Task<bool> Send(string address, string code, CancellationToken cancel = default)
    {
        Posted(address, code);

        return Task.FromResult(true);
    }

    [LoggerMessage(
        EventId = 40,
        Level = LogLevel.Information,
        Message = "Would send {Address} the code {Code}.")]
    private partial void Posted(string address, string code);
}

/// <summary>
/// Sends over SMTP, which every mail provider speaks.
/// </summary>
/// <remarks>
/// SMTP rather than one provider's HTTP API, deliberately. Every provider hands out SMTP credentials,
/// so this picks no vendor and needs no package, and unlike the notification sender it can be tested
/// against a real server: the tests stand one up in the process and read the bytes that arrive. That
/// is the difference between this and Firebase, and it is why this one is not hedged.
///
/// The message is as short as a message can be. No HTML, no images, no tracking pixel, no marketing:
/// a code, what it is for, and how long it lasts. A game that promises "no email collection" for
/// children should not be sending adults anything they did not ask for either.
/// </remarks>
public sealed class SmtpEmailSender : IEmailSender, IDisposable
{
    private readonly SmtpClient _smtp;
    private readonly string _from;

    public SmtpEmailSender(SmtpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _from = settings.From;
        _smtp = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.Tls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = settings.User.Length > 0
                ? new NetworkCredential(settings.User, settings.Password)
                : null,
            Timeout = (int)TimeSpan.FromSeconds(20).TotalMilliseconds,
        };
    }

    public async Task<bool> Send(string address, string code, CancellationToken cancel = default)
    {
        string minutes = Emails.Lasts.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture);

        using MailMessage message = new MailMessage(_from, address)
        {
            Subject = "Your Molehill Madness code",
            Body = $"Type {code} into the game to link this address.\r\n\r\n"
                + $"It works for {minutes} minutes. "
                + "If you did not ask for this, nothing has happened and you can ignore it.\r\n",
            IsBodyHtml = false,
        };

        try
        {
            await _smtp.SendMailAsync(message, cancel).ConfigureAwait(false);

            return true;
        }
        catch (SmtpException)
        {
            // The server refused it or could not be reached. The claim stays live, so the player can
            // ask again in a minute, and nothing here is worth throwing over.
            return false;
        }
        catch (InvalidOperationException)
        {
            // A misconfigured client, which is a deployment problem rather than a request one.
            return false;
        }
    }

    public void Dispose() => _smtp.Dispose();
}

/// <summary>Where the mail goes out through, if it goes out at all.</summary>
public sealed record SmtpSettings(
    string Host, int Port, bool Tls, string User, string Password, string From)
{
    /// <summary>Where the relay looks for these.</summary>
    public const string SettingSection = "Relay:Smtp";

    /// <summary>
    /// The configured mail server, or null if there is not one.
    /// </summary>
    /// <remarks>
    /// Null means the codes go to the log, which is right for a development run and wrong for a
    /// deployment. A half-configured one throws rather than falling back, on the same argument the
    /// Firebase key uses: an operator who set a host and got silence has no way to tell a broken
    /// setting from a quiet day.
    /// </remarks>
    public static SmtpSettings? Configured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(SettingSection);

        if (section["Host"] is not string host || host.Length == 0)
        {
            return null;
        }

        if (section["From"] is not string from || !Emails.Plausible(from))
        {
            throw new InvalidOperationException(
                $"{SettingSection}:From has to be an address the mail server will send as.");
        }

        int port = int.TryParse(
            section["Port"], CultureInfo.InvariantCulture, out int configured) ? configured : 587;

        return new SmtpSettings(
            host,
            port,
            !string.Equals(section["Tls"], "false", StringComparison.OrdinalIgnoreCase),
            section["User"] ?? string.Empty,
            section["Password"] ?? string.Empty,
            from);
    }
}
