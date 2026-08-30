using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Relay.Api;

/// <summary>
/// What stops somebody reading the whole game out of the code space.
/// </summary>
/// <remarks>
/// A game code is five letters from a twenty-four letter alphabet, which is a shade under eight
/// million codes, and knowing one is the whole of what it takes to join a lobby that has a seat
/// free. That is the design working as intended: a code is read out to a friend, and gating it
/// behind an account would stop a child playing with their own family while doing nothing about the
/// risk the age gate exists for.
///
/// It only works while codes have to be given rather than found. Measured against this relay before
/// this file existed, one keep-alive connection swept three thousand codes a second, which is the
/// entire space in about three quarters of an hour from a laptop. At that price a stranger can
/// enumerate every live lobby in the game and drop into any with a seat free, and since platoons
/// have generated names and no profiles, the host cannot tell them from the friend they were
/// expecting. The design's argument that "the person who gave them the code is accountable for who
/// is in it" only holds if nobody can find a code without being given one.
///
/// So the surface that answers questions about codes is metered per caller. The numbers are set
/// against what a real client does rather than against a feeling: one waiting in a lobby asks about
/// it once a second, so a sustained two a second with a burst of thirty leaves ordinary play
/// untouched and turns a sweep of the space into something that takes a month and a half from one
/// address rather than an afternoon.
///
/// It is not the whole answer and does not pretend to be. Somebody with a thousand addresses is back
/// to a day, and the real ceiling on that is a lobby not lasting for ever, which is a separate piece
/// of work. What this removes is the case where one laptop and no cleverness is enough.
/// </remarks>
public static class Guessing
{
    /// <summary>The policy name, on the endpoints that answer questions about a code.</summary>
    public const string Policy = "codes";

    /// <summary>How many requests a caller may make before it has to wait.</summary>
    public const int Burst = 30;

    /// <summary>How many it gets back, and how often.</summary>
    public const int PerPeriod = 30;

    /// <summary>The period those are replenished over.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    /// <summary>Adds the limiter. Nothing is metered until an endpoint asks for the policy.</summary>
    public static IServiceCollection AddCodeGuessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(
                Policy,
                http => RateLimitPartition.GetTokenBucketLimiter(
                    Caller(http),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = Burst,
                        TokensPerPeriod = PerPeriod,
                        ReplenishmentPeriod = Period,
                        AutoReplenishment = true,

                        // Refused rather than held. A queue would turn a sweep into a slow sweep and
                        // spend the relay's memory holding somebody else's requests for them.
                        QueueLimit = 0,
                    }));
        });
    }

    /// <summary>
    /// Who to meter, which is the connection rather than anything it claims about itself.
    /// </summary>
    /// <remarks>
    /// Deliberately not a header. X-Forwarded-For is set by whoever is asking unless something
    /// trusted overwrites it, so metering on one would be metering a field the sweeper controls,
    /// which is worse than not metering at all because it looks like a limit. Behind an ingress the
    /// address seen here is the ingress, and the fix is for the host to be configured with forwarded
    /// headers it actually trusts, not for this to believe a string.
    ///
    /// A caller with no remote address at all is metered on its connection instead. That only
    /// happens in process, where there is no socket to have an address, and one shared bucket would
    /// mean one component's requests metering another's.
    /// </remarks>
    private static string Caller(HttpContext http)
    {
        IPAddress? address = http.Connection.RemoteIpAddress;

        // The connection when there is no address, rather than one shared bucket for everybody
        // without one. Over a socket there is always an address, so this only ever fires in process,
        // where lumping every caller together would mean one component's requests metering another's.
        return address is null ? http.Connection.Id : address.ToString();
    }
}
