using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Molehill.Online;
using MoleSim.Match;
using Relay.Api;

namespace Molehill.Online.Tests;

/// <summary>
/// One relay, in process, with a store that dies with the test.
/// </summary>
internal sealed class TestRelay : WebApplicationFactory<Program>
{
    private readonly string _databaseName;

    public TestRelay(string databaseName) => _databaseName = databaseName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
            services.AddSingleton(MatchStore.InMemory(_databaseName)));
    }

    /// <summary>A relay client wired to this relay, over the in-process pipeline.</summary>
    public RelayClient Client() => new RelayClient(CreateClient());
}

/// <summary>
/// One player, as the game would run them: an online session and a simulation of their own.
/// </summary>
/// <remarks>
/// This is the part that makes these tests worth having. Each seat gets a completely separate
/// MoleMatch built from the same seed, exactly as four phones would, and the only thing they share
/// is the relay. If the online flow loses a plan, reorders the seats, or feeds a simulation from
/// anything other than the bytes that went over the wire, these two worlds come apart and the state
/// hash says so.
/// </remarks>
internal sealed class Player
{
    private const int MapWidthCells = 400;
    private const int MapHeightCells = 240;

    private Player(OnlineMatch online) => Online = online;

    public OnlineMatch Online { get; }

    /// <summary>This player's own simulation, built once the seed is known.</summary>
    public MoleMatch? Match { get; private set; }

    public static Player Hosting(RelayClient relay, int playerCount, MatchPace pace) =>
        new Player(OnlineMatch.Hosting(relay, playerCount, pace));

    public static Player Joining(RelayClient relay, string code) =>
        new Player(OnlineMatch.Joining(relay, code));

    public static Player Resuming(RelayClient relay, string code, string token) =>
        new Player(OnlineMatch.Resuming(relay, code, token));

    /// <summary>Builds the world from the seed the relay handed out.</summary>
    public void BuildWorld()
    {
        Match ??= MoleMatch.Create(
            Online.PlayerCount, Online.Seed, MapWidthCells, MapHeightCells);
    }

    /// <summary>
    /// Plans for whichever mole is up, the way the game's own rotation does, and commits it.
    /// </summary>
    /// <remarks>
    /// Deliberately plain: the point of these tests is the transport and the round loop, not the
    /// planning. What matters is that each seat produces a plan of its own that the others could not
    /// have guessed, so a bug that fed one seat's plan to another would show up.
    /// </remarks>
    public void PlanAndCommit(WeaponId weapon, int power)
    {
        BuildWorld();

        Mole? actor = null;

        foreach (Mole mole in Match!.Eligible(Online.Seat))
        {
            actor = mole;
            break;
        }

        if (actor is null)
        {
            // Every mole in this platoon is out, so there is nothing to plan. The relay still needs
            // something, because a round is only released when every seat has committed.
            Online.Commit(PlanCodec.Write(new Plan(
                Online.Seat, 0, WeaponId.None, Array.Empty<RoutePoint>(), Array.Empty<PlanAction>())));

            return;
        }

        // Aim differs by seat, so no two platoons ever submit the same bytes and a bug that fed one
        // seat's plan to another would show up rather than pass.
        PlanAction shot = PlanAction.Fire(
            tick: 10 + (Online.Seat * 3),
            aim: new MoleSim.Numerics.Vec2(
                MoleSim.Numerics.Fix64.FromInt(3 - Online.Seat),
                MoleSim.Numerics.Fix64.FromInt(-2 - Online.Seat)),
            power: (byte)power);

        Online.Commit(PlanCodec.Write(new Plan(
            Online.Seat, actor.Index, weapon, Array.Empty<RoutePoint>(), new[] { shot })));
    }

    /// <summary>
    /// Feeds the released plans into this player's own simulation and resolves the round.
    /// </summary>
    public ulong TakeRound()
    {
        BuildWorld();

        foreach (Plan plan in Online.Plans)
        {
            Match!.SubmitPlan(plan);
        }

        int round = Online.Round;

        Match!.ResolveRound();
        ulong hash = Match.StateHash();

        Online.ReportHash(round, hash);
        Online.RoundTaken();

        return hash;
    }
}
