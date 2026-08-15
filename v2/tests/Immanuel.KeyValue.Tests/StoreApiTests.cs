using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Immanuel.KeyValue.Tests;

public class StoreApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client => fixture.CreateClient();

    private async Task<string> NewAppKeyAsync()
    {
        var response = await Client.PostAsync("/api/v2/appkeys", null);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("appKey").GetString()!;
    }

    private Task<HttpResponseMessage> SetAsync(string appKey, string key, string? value) =>
        Client.PutAsJsonAsync($"/api/v2/appkeys/{appKey}/keys/{key}", new { value }, Json);

    [Fact]
    public async Task Creating_an_app_key_returns_201_and_a_location()
    {
        var response = await Client.PostAsync("/api/v2/appkeys", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(8, body.GetProperty("appKey").GetString()!.Length);
    }

    [Fact]
    public async Task Put_creates_then_updates()
    {
        var appKey = await NewAppKeyAsync();

        var created = await SetAsync(appKey, "colour", "green");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var updated = await SetAsync(appKey, "colour", "blue");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("blue", body.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Values_in_the_body_may_contain_anything_a_url_cannot_carry()
    {
        var appKey = await NewAppKeyAsync();

        // The reason the v2 API exists: none of this survives a URL path segment.
        const string awkward = "a/b/c\nline two\ttabbed \"quoted\" 'single' {\"json\":true} 100% ünïcode";

        var response = await SetAsync(appKey, "awkward", awkward);
        response.EnsureSuccessStatusCode();

        var read = await Client.GetFromJsonAsync<JsonElement>($"/api/v2/appkeys/{appKey}/keys/awkward", Json);
        Assert.Equal(awkward, read.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Lists_every_key()
    {
        var appKey = await NewAppKeyAsync();
        await SetAsync(appKey, "one", "1");
        await SetAsync(appKey, "two", "2");

        var list = await Client.GetFromJsonAsync<JsonElement>($"/api/v2/appkeys/{appKey}/keys", Json);

        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(["one", "two"], list.EnumerateArray().Select(e => e.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Delete_removes_a_key_and_is_404_the_second_time()
    {
        var appKey = await NewAppKeyAsync();
        await SetAsync(appKey, "temp", "value");

        Assert.Equal(HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/api/v2/appkeys/{appKey}/keys/temp")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.DeleteAsync($"/api/v2/appkeys/{appKey}/keys/temp")).StatusCode);
    }

    [Fact]
    public async Task Increment_starts_a_counter_from_zero()
    {
        var appKey = await NewAppKeyAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/v2/appkeys/{appKey}/keys/visits/increment", new { by = 1 }, Json);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("1", body.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Increment_steps_by_an_arbitrary_amount()
    {
        var appKey = await NewAppKeyAsync();
        await SetAsync(appKey, "n", "100");

        var response = await Client.PostAsJsonAsync(
            $"/api/v2/appkeys/{appKey}/keys/n/increment", new { by = -30 }, Json);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("70", body.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Incrementing_text_is_a_409()
    {
        var appKey = await NewAppKeyAsync();
        await SetAsync(appKey, "word", "hello");

        var response = await Client.PostAsJsonAsync(
            $"/api/v2/appkeys/{appKey}/keys/word/increment", new { by = 1 }, Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_app_key_is_a_404_everywhere()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v2/appkeys/zzzzzzzz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v2/appkeys/zzzzzzzz/keys")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v2/appkeys/zzzzzzzz/keys/k")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync("zzzzzzzz", "k", "v")).StatusCode);
    }

    [Fact]
    public async Task App_key_info_reports_counts_and_timestamps()
    {
        var appKey = await NewAppKeyAsync();
        await SetAsync(appKey, "a", "1");

        var info = await Client.GetFromJsonAsync<JsonElement>($"/api/v2/appkeys/{appKey}", Json);

        Assert.Equal(appKey, info.GetProperty("appKey").GetString());
        Assert.Equal(1, info.GetProperty("keyCount").GetInt64());
        Assert.EndsWith("Z", info.GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task Stats_are_available()
    {
        var appKey = await NewAppKeyAsync();
        await SetAsync(appKey, "a", "1");

        var stats = await Client.GetFromJsonAsync<JsonElement>("/api/v2/stats", Json);

        Assert.True(stats.GetProperty("appKeys").GetInt64() >= 1);
        Assert.True(stats.GetProperty("keys").GetInt64() >= 1);
    }

    [Fact]
    public async Task Both_apis_see_the_same_data()
    {
        var appKey = await NewAppKeyAsync();

        // Written with v2 ...
        await SetAsync(appKey, "shared", "written-by-v2");
        Assert.Equal("\"written-by-v2\"", await Client.GetStringAsync($"/api/KeyVal/GetValue/{appKey}/shared"));

        // ... overwritten with v1.
        await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/shared/written-by-v1", null);

        var read = await Client.GetFromJsonAsync<JsonElement>($"/api/v2/appkeys/{appKey}/keys/shared", Json);
        Assert.Equal("written-by-v1", read.GetProperty("value").GetString());
    }

    [Fact]
    public async Task An_app_key_issued_by_v1_works_in_v2()
    {
        var body = await Client.GetStringAsync("/api/KeyVal/GetAppKey");
        var appKey = JsonSerializer.Deserialize<string>(body)!;

        var response = await SetAsync(appKey, "k", "v");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Health_and_openapi_are_served()
    {
        (await Client.GetAsync("/health")).EnsureSuccessStatusCode();
        (await Client.GetAsync("/openapi/v1.json")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_landing_page_is_served_at_the_root()
    {
        var response = await Client.GetAsync("/");
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Free Online Key-Value Store", html);
    }
}
