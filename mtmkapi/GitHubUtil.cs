using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json.Nodes;

namespace mtmkapi;

public class GitHubRelease
{
    public string TagName { get; set; }
    public string Name { get; set; }
    public string PublishedAt { get; set; }
    // Add more properties as needed
}

public class PaginatedResponse<T>
{
    public List<T> Items { get; set; }
    public bool HasNextPage { get; set; }
    public int? NextPage { get; set; }
}

public class GitHubClient
{
    private readonly HttpClient _httpClient;
    private const string ApiBaseUrl = "https://api.github.com";

    public GitHubClient(string personalAccessToken)
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitHubReleasesFetcher", "1.0"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);
    }

    public async Task<PaginatedResponse<JsonNode>> GetReleasesAsync(string owner, string repo, int page = 1, int perPage = 30)
    {
        string url = $"{ApiBaseUrl}/repos/{owner}/{repo}/releases?page={page}&per_page={perPage}";
        
        HttpResponseMessage response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        string content = await response.Content.ReadAsStringAsync();
        // var releases = JsonSerializer.Deserialize<List<JsonNode>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var json = JsonNode.Parse(content);
        var jsonArray = json?.AsArray() ?? [];
        List<JsonNode> releases = new();
        foreach (var node in jsonArray)
        {
            releases.Add(node);
        }
        
        var paginatedResponse = new PaginatedResponse<JsonNode>
        {
            Items = releases,
            HasNextPage = false,
            NextPage = null
        };

        // Check for Link header
        if (response.Headers.Contains("Link"))
        {
            var linkHeader = response.Headers.GetValues("Link").FirstOrDefault();
            if (linkHeader != null)
            {
                var links = linkHeader.Split(',');
                foreach (var link in links)
                {
                    if (link.Contains("rel=\"next\""))
                    {
                        paginatedResponse.HasNextPage = true;
                        paginatedResponse.NextPage = page + 1;
                        break;
                    }
                }
            }
        }

        // Alternative method: Check if the number of items returned equals perPage
        if (!paginatedResponse.HasNextPage && releases.Count == perPage)
        {
            paginatedResponse.HasNextPage = true;
            paginatedResponse.NextPage = page + 1;
        }

        return paginatedResponse;
    }
}

// Usage example
public class GitHubReleasesFetcher
{
    public static async IAsyncEnumerable<JsonNode> RunAsync(string token, string owner, string repo)
    {
        var client = new GitHubClient(token); // Or provide a personal access token if needed
        
        int currentPage = 1;
        const int perPage = 50;

        do
        {
            var response = await client.GetReleasesAsync(owner, repo, page: currentPage, perPage: perPage);
                
            foreach (var release in response.Items)
            {
                yield return release;
                // Console.WriteLine($"JSON: {release}");
                //Console.WriteLine($"Tag: {release.TagName}, Name: {release.Name}, Published: {release.PublishedAt}");
            }

            // Console.WriteLine($"Page {currentPage} - Has next page: {response.HasNextPage}");

            if (response.HasNextPage && response.NextPage.HasValue)
            {
                currentPage = response.NextPage.Value;
            }
            else
            {
                break;
            }

        } while (true);
    }
}