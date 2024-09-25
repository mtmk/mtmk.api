using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Console;
using mtmkapi;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using NATS.Extensions.Microsoft.DependencyInjection;

// await foreach (var node in GitHubReleasesFetcher.RunAsync(
//                    File.ReadLines("c:/users/mtmk/.keys/gh.key").First(),
//                    "nats-io",
//                    "nats-server"))
// {
//     var value = node["tag_name"]!.GetValue<string>();
//     var compversion = ComparedVersion(value);
//
//     Console.WriteLine(compversion);
// }
// return;

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

var versionComparer = new VersionComparer();
var gitHubApiKey = File.ReadLines("gh.key").First();
var reposAllowed = File.ReadLines("repos.txt").ToHashSet();

builder.Services.AddNatsClient();

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

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
    if (!reposAllowed.Contains($"{owner}/{repo}"))
    {
        logger.LogWarning($"Unauthorized {owner}/{repo}");
        return Results.NotFound();
    }
    
    var kv = new NatsKVContext(new NatsJSContext((NatsConnection)nats));
    var store = await kv.CreateStoreAsync("gh");
    
    var key = $"ver/{owner}/{repo}/{version}";
    var keyTime = $"ver-time/{owner}/{repo}/{version}";
    try
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (await store.GetEntryAsync<long>(keyTime)).Value > 3600)
        {
            await store.DeleteAsync(key);
            throw new NatsKVKeyNotFoundException();
        }
        var entry = await store.GetEntryAsync<string>(key);
        
        if (entry.Value == "__not_found__")
        {
            return Results.NotFound();
        }
        
        return Results.Text(entry.Value);
    }
    catch (Exception e)
    {
        if (e is NatsKVKeyNotFoundException or NatsKVKeyDeletedException)
        {
            logger.LogInformation($"Not found {owner}/{repo}/{version}");
            
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
            else if (version == "main")
            {
                return Results.Text("main");
            }
            else
            {
                List<string> tags = new();
                await foreach (var node in GitHubReleasesFetcher.RunAsync(gitHubApiKey, owner, repo))
                {
                    if (node?["tag_name"]?.GetValue<string>() is { } tagName && tagName.StartsWith(version))
                    {
                        tags.Add(tagName);
                    }
                }
                
                var versionTagName = tags
                    .OrderDescending(versionComparer)
                    .FirstOrDefault();
                
                if (versionTagName == null)
                {
                    logger.LogWarning($"Not found version '{version}' not found in repo {owner}/{repo}");
                    await store.PutAsync(key, "__not_found__");
                    await store.PutAsync(keyTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    return Results.NotFound();
                }
                
                await store.PutAsync(key, versionTagName);
                await store.PutAsync(keyTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                logger.LogInformation($"Found version '{version}' as '{versionTagName}' in repo {owner}/{repo}");
                
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
    logger.LogInformation($"GitHub API call {endpoint}");
    
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

class VersionComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        return string.Compare(ComparedVersion(x), ComparedVersion(x), StringComparison.Ordinal);
    }
    
    string ComparedVersion(string? version)
    {
        if (version == null)
        {
            return "";
        }
        
        version = version.Replace("v", "");
        var strings = version.Split('.');
        var v = new StringBuilder();
        foreach (var n in strings)
        {
            if (Regex.IsMatch(n, @"^\d+$"))
            {
                v.Append($"{n.PadLeft(4, '0')}");
                continue;
            }

            if (n.Contains("-"))
            {
                foreach (var s in n.Split("-"))
                {
                    if (Regex.IsMatch(s, @"^\d+$"))
                    {
                        v.Append($"{s.PadLeft(4, '0')}");
                    }
                    else
                    {
                        v.Append($"-{s}");
                    }
                }
            }
        }

        return v.ToString();
    }
}