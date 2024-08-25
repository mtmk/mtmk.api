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
ghApi.MapGet("releases/tag/{owner}/{repo}/{version}", async (ILogger log, INatsConnection nats, string owner, string repo, string version) =>
{
    var kv = new NatsKVContext(new NatsJSContext((NatsConnection)nats));
    var store = await kv.CreateStoreAsync("gh");
    
    var key = $"ver/{owner}/{repo}/{version}";
    var keyTime = $"ver-time/{owner}/{repo}/{version}";
    try
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (await store.GetEntryAsync<long>(keyTime)).Value > 300)
        {
            await store.DeleteAsync(key);
            throw new NatsKVKeyNotFoundException();
        }
        var entry = await store.GetEntryAsync<string>(key);
        return Results.Text(entry.Value);
    }
    catch (Exception e)
    {
        if (e is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
        {
            log.LogInformation($"Not found {owner}/{repo}/{version}");
            
            if (version == "latest")
            {
                var json = JsonNode.Parse(await GetGitHubDataAsync($"repos/{owner}/{repo}/releases/{version}"));

                if (json?["tag_name"]?.GetValue<string>() is { } tagName)
                {
                    await store.PutAsync(key, tagName);
                    await store.PutAsync(keyTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    return Results.Text(tagName);
                }
            }
            else
            {
                var json = JsonNode.Parse(await GetGitHubDataAsync($"repos/{owner}/{repo}/releases"));

                var jsonArray = json?.AsArray() ?? [];
                List<string> tags = new();
                foreach (var node in jsonArray)
                {
                    if (node?["tag_name"]?.GetValue<string>() is { } tagName && tagName.StartsWith(version))
                    {
                        tags.Add(tagName);
                        await store.PutAsync(key, tagName);
                        await store.PutAsync(keyTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        return Results.Text(tagName);
                    }
                }
                tags.Sort();
                var versionTagName = tags.First();
                await store.PutAsync(key, versionTagName);
                return Results.Text(versionTagName);
            }
        }

        throw;
    }
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