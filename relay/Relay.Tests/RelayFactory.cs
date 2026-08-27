using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Relay.Api;

namespace Relay.Tests;

/// <summary>
/// The real relay, booted in process, with its store swapped for one that dies with the test.
/// </summary>
/// <remarks>
/// The endpoints are tested through the pipeline rather than by calling the handlers, because most
/// of what could break in them is the pipeline: the route constraint on the round, the model binding
/// on the lobby request, reading the plan off the raw body, and the header the seat token arrives in.
/// None of that exists when a handler is called as a method.
/// </remarks>
internal sealed class RelayFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName;

    public RelayFactory(string databaseName) => _databaseName = databaseName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Registered after the application's own, so this is the one that gets resolved.
        builder.ConfigureServices(services =>
            services.AddSingleton(MatchStore.InMemory(_databaseName)));
    }
}

/// <summary>Reading the relay's replies without a mirror of every response shape.</summary>
internal static class Replies
{
    private static readonly JsonSerializerOptions Options =
        new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public static async Task<JsonElement> Json(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static async Task<Joined> AsJoined(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Joined>(Options))!;

    /// <summary>Posts a plan as the raw bytes it is, with the seat token that owns it.</summary>
    public static Task<HttpResponseMessage> PostPlan(
        this HttpClient client, string code, int round, string token, byte[] payload)
    {
        ByteArrayContent body = new ByteArrayContent(payload);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        HttpRequestMessage request =
            new HttpRequestMessage(HttpMethod.Post, $"/matches/{code}/rounds/{round}/plan")
            {
                Content = body,
            };
        request.Headers.Add("X-Seat-Token", token);

        return client.SendAsync(request);
    }
}
