using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Console;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using NATS.Extensions.Microsoft.DependencyInjection;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.FormatterName = "SystemdLogFormatter";
});

#pragma warning disable IL3050
#pragma warning disable IL2026
builder.Logging.AddConsoleFormatter<SystemdLogFormatter, ConsoleFormatterOptions>();
#pragma warning restore IL2026
#pragma warning restore IL3050

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

var gitHubApiKey = File.ReadLines("gh.key").First();

builder.Services.AddNatsClient();

var app = builder.Build();

var sampleTodos = new Todo[]
{
    new(1, "Walk the dog"),
    new(2, "Do the dishes", DateOnly.FromDateTime(DateTime.Now)),
    new(3, "Do the laundry", DateOnly.FromDateTime(DateTime.Now.AddDays(1))),
    new(4, "Clean the bathroom"),
    new(5, "Clean the car", DateOnly.FromDateTime(DateTime.Now.AddDays(2)))
};

var todosApi = app.MapGroup("/todos");
todosApi.MapGet("/", () => sampleTodos);
todosApi.MapGet("/{id}", (int id) =>
    sampleTodos.FirstOrDefault(a => a.Id == id) is { } todo
        ? Results.Ok(todo)
        : Results.NotFound());

var ghApi = app.MapGroup("/gh/v1");
ghApi.MapGet("releases/tag/{owner}/{repo}/{version}", async (INatsConnection nats, string owner, string repo, string version) =>
{
    var kv = new NatsKVContext(new NatsJSContext((NatsConnection)nats));
    var store = await kv.CreateStoreAsync("gh");
    NatsKVEntry<string> entry;
    try
    {
        entry = await store.GetEntryAsync<string>($"{owner}/{repo}/{version}");
    }
    catch (NatsKVKeyNotFoundException)
    {
        var json = JsonNode.Parse(await GetGitHubDataAsync($"repos/{owner}/{repo}/releases/{version}"));
        await store.PutAsync($"{owner}/{repo}/{version}", json.ToString());
        return Results.Text(json.ToString());
    }

    return Results.Text(entry.Value);
});

app.Run();

return;

async Task<string> GetGitHubDataAsync(string endpoint)
{
    using var client = new HttpClient();
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gitHubApiKey);
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YourApp", "1.0"));

    var response = await client.GetAsync($"https://api.github.com/{endpoint}");
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
}

public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);

[JsonSerializable(typeof(Todo[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}