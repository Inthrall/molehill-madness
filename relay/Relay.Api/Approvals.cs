using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Relay.Api;

/// <summary>
/// A platform saying that a grown-up has approved this account.
/// </summary>
/// <remarks>
/// The design allows an under-threshold account into the stranger pool "with platform-level parental
/// approval", and the two words that matter are platform-level. This is not a setting, not a box a
/// player ticks and not a field a client sends: it is a statement made by a store, about one account,
/// signed with a key the store holds and the relay only has the public half of.
///
/// That shape is the whole point. An approval the relay accepted on a client's word would be a hole
/// in the age gate wearing the name of a safeguard, and it would be worse than having no approval at
/// all, because it would look like protection to anybody reading the feature list. A signature makes
/// the claim as good as the platform that made it and useless to anybody else, including the player
/// it is about.
///
/// With no platform keys configured, nothing can be approved. That is the correct default and it is
/// the state this repository ships in, because no store has been integrated with yet: the mechanism
/// is real and there is nobody entitled to use it.
/// </remarks>
public sealed record Grant(string Account, string Platform, DateTimeOffset IssuedAt);

/// <summary>Reading and checking the grants a platform issues.</summary>
public static class Approvals
{
    /// <summary>Where the public halves of the platforms' keys are configured.</summary>
    public const string SettingSection = "Relay:Approvals";

    /// <summary>
    /// How old a grant may be and still be worth honouring.
    /// </summary>
    /// <remarks>
    /// An hour. A grant is minted when a parent approves something and presented immediately after,
    /// so an hour is generous for the round trip and short enough that one captured off a wire is
    /// worthless by the time anybody could reuse it. It is also why the issue time is inside the
    /// signature rather than beside it.
    /// </remarks>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(1);

    /// <summary>
    /// The platforms this relay will believe, read once at startup.
    /// </summary>
    /// <remarks>
    /// Empty unless somebody has configured one, and an empty set means no account can ever be
    /// approved. A relay that fell back to trusting the client when it had no keys would be a relay
    /// whose safeguard switched itself off exactly when it was least supervised.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Configured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (IConfigurationSection platform in
            configuration.GetSection(SettingSection).GetChildren())
        {
            if (platform.Value is not string pem || pem.Length == 0)
            {
                continue;
            }

            // Read as a key here rather than at the point of use, so a mistyped one stops the
            // process while somebody is watching rather than failing the first time a child tries
            // to play.
            // Checked before it is imported, because ImportFromPem is happy to take either half
            // of a key pair and this service must never hold the half that signs. A private key here
            // would work perfectly and quietly turn "the relay only holds public keys" into a
            // sentence in a comment: anything that later wanted to mint an approval could, and the
            // one property that makes a platform's word worth taking is that we cannot forge it.
            if (!pem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The approval key for '{platform.Key}' has to be a public key. "
                    + "This relay must never be given the half that signs.");
            }

            using ECDsa checking = ECDsa.Create();

            try
            {
                checking.ImportFromPem(pem);
            }
            catch (ArgumentException trouble)
            {
                throw new InvalidOperationException(
                    $"The approval key for '{platform.Key}' is not a public key.", trouble);
            }

            keys[platform.Key] = pem;
        }

        return keys;
    }

    /// <summary>
    /// Checks a grant and says who it is about, or null if it is worth nothing.
    /// </summary>
    /// <remarks>
    /// Everything that could make a grant worthless is checked here and each check is a way somebody
    /// would try to use one: a signature that does not verify is a forgery, a platform nobody
    /// configured is a stranger, a grant about a different account is one lifted from somewhere else,
    /// and an old one is a replay. The account is compared inside rather than being taken from the
    /// request, so a grant issued for one child cannot be presented by another.
    ///
    /// Null rather than an exception for every one of them, and deliberately without saying which.
    /// The caller's answer is the same in all cases and a reply that distinguished them would be a
    /// way to ask the relay questions about grants it has never seen.
    /// </remarks>
    public static Grant? Read(
        string token,
        IReadOnlyDictionary<string, string> keys,
        string account,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (token is null || keys.Count == 0)
        {
            return null;
        }

        string[] halves = token.Split('.');

        if (halves.Length != 2)
        {
            return null;
        }

        byte[] payload;
        byte[] signature;

        try
        {
            payload = Base64Url.DecodeFromChars(halves[0]);
            signature = Base64Url.DecodeFromChars(halves[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (Parse(payload) is not Grant grant)
        {
            return null;
        }

        if (!string.Equals(grant.Account, account, StringComparison.Ordinal))
        {
            return null;
        }

        if (now - grant.IssuedAt > Fresh || grant.IssuedAt - now > Fresh)
        {
            // Both directions. A grant from the future is either a clock nobody has set or somebody
            // buying themselves a year of validity, and neither is a thing to accept.
            return null;
        }

        if (!keys.TryGetValue(grant.Platform, out string? pem))
        {
            return null;
        }

        using ECDsa key = ECDsa.Create();

        try
        {
            key.ImportFromPem(pem);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return key.VerifyData(payload, signature, HashAlgorithmName.SHA256) ? grant : null;
    }

    /// <summary>
    /// Signs a grant, which is what a platform would do and what the tests do.
    /// </summary>
    /// <remarks>
    /// Here rather than in the tests so that the two halves cannot drift apart, and because the
    /// shape of a grant is something a platform integrating with this needs written down somewhere
    /// it can be read. Nothing in the running relay calls it: the relay only ever holds public keys,
    /// and a service that could mint its own approvals would be back to ticking a box.
    /// </remarks>
    public static string Sign(ECDsa key, Grant grant)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(grant);

        byte[] payload = Wire.Json(writer =>
        {
            writer.WriteString("account", grant.Account);
            writer.WriteString("platform", grant.Platform);
            writer.WriteString(
                "issuedAt", grant.IssuedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        });

        byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256);

        return $"{Wire.Url(payload)}.{Wire.Url(signature)}";
    }

    private static Grant? Parse(byte[] payload)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(payload);

            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (Text(parsed.RootElement, "account") is not string account
                || Text(parsed.RootElement, "platform") is not string platform
                || Text(parsed.RootElement, "issuedAt") is not string issued
                || !DateTimeOffset.TryParse(
                    issued,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset at))
            {
                return null;
            }

            return new Grant(account, platform, at);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement found)
            && found.ValueKind == JsonValueKind.String
            && found.GetString() is string value
            && value.Length > 0
                ? value
                : null;
}
