using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Pairrot.IntegrationTests;

internal static class TestHttp
{
    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!;

    public static async Task<JsonNode> ExpectAsync(this Task<HttpResponseMessage> request, HttpStatusCode status)
    {
        var response = await request;
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {content}");
        return content.Length == 0 ? new JsonObject() : JsonNode.Parse(content)!;
    }

    public static async Task ExpectErrorAsync(this Task<HttpResponseMessage> request, HttpStatusCode status, string code)
    {
        var body = await request.ExpectAsync(status);
        Assert.Equal(code, body["error"]?["code"]?.GetValue<string>());
    }

    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient client, string url, object body) =>
        client.PatchAsJsonAsync(url, body);

    public static Task<HttpResponseMessage> PatchRawAsync(this HttpClient client, string url, string json) =>
        client.PatchAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    public static Task<HttpResponseMessage> PostRawAsync(this HttpClient client, string url, string json) =>
        client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    public static string Str(this JsonNode node, string property) => node[property]!.GetValue<string>();
}
