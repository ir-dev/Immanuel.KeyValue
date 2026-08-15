using System.Net;
using System.Text.Json;

namespace Immanuel.KeyValue.Tests;

/// <summary>
/// The contract with everyone who wrote against keyvalue.immanuel.co before this rewrite.
/// These assert on the raw response text, not on deserialised objects, because the exact
/// bytes are the thing that must not change.
/// </summary>
public class LegacyApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private HttpClient Client => fixture.CreateClient();

    private async Task<string> GetAppKeyAsync()
    {
        var body = await Client.GetStringAsync("/api/KeyVal/GetAppKey");
        return JsonSerializer.Deserialize<string>(body)!;
    }

    [Fact]
    public async Task GetAppKey_returns_a_quoted_json_string()
    {
        var response = await Client.GetAsync("/api/KeyVal/GetAppKey");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();

        // Web API 2 serialised string returns as JSON. ASP.NET Core would default to
        // text/plain, so the quotes here are the regression test for that.
        Assert.StartsWith("\"", body);
        Assert.EndsWith("\"", body);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var appKey = JsonSerializer.Deserialize<string>(body)!;
        Assert.Equal(8, appKey.Length);
    }

    [Fact]
    public async Task Round_trips_a_value_through_the_v1_endpoints()
    {
        var appKey = await GetAppKeyAsync();

        var update = await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/yourkey/yourvalue", null);
        update.EnsureSuccessStatusCode();
        Assert.Equal("true", await update.Content.ReadAsStringAsync());

        var read = await Client.GetStringAsync($"/api/KeyVal/GetValue/{appKey}/yourkey");
        Assert.Equal("\"yourvalue\"", read);
    }

    [Fact]
    public async Task GetValue_returns_an_empty_string_for_a_key_that_was_never_set()
    {
        var appKey = await GetAppKeyAsync();

        Assert.Equal("\"\"", await Client.GetStringAsync($"/api/KeyVal/GetValue/{appKey}/nothing-here"));
    }

    [Fact]
    public async Task UpdateValue_without_a_value_stores_nothing_and_still_succeeds()
    {
        var appKey = await GetAppKeyAsync();

        var update = await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/blank", null);
        update.EnsureSuccessStatusCode();

        Assert.Equal("\"\"", await Client.GetStringAsync($"/api/KeyVal/GetValue/{appKey}/blank"));
    }

    [Fact]
    public async Task ActOnValue_increment_returns_v1s_exact_message()
    {
        var appKey = await GetAppKeyAsync();
        await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/counter/5", null);

        var response = await Client.PostAsync($"/api/KeyVal/ActOnValue/{appKey}/counter/increment", null);
        response.EnsureSuccessStatusCode();

        Assert.Equal("\"Increment Successful\"", await response.Content.ReadAsStringAsync());
        Assert.Equal("\"6\"", await Client.GetStringAsync($"/api/KeyVal/GetValue/{appKey}/counter"));
    }

    [Fact]
    public async Task ActOnValue_on_text_returns_v1s_exact_failure_message()
    {
        var appKey = await GetAppKeyAsync();
        await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/word/hello", null);

        var response = await Client.PostAsync($"/api/KeyVal/ActOnValue/{appKey}/word/increment", null);
        response.EnsureSuccessStatusCode();

        // Including the original typo, in case anyone is matching on the string.
        Assert.Equal(
            "\"Increment Failed, increment applied on string charecters\"",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ActOnValue_is_case_insensitive_like_v1()
    {
        var appKey = await GetAppKeyAsync();
        await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/c/1", null);

        var response = await Client.PostAsync($"/api/KeyVal/ActOnValue/{appKey}/c/INCREMENT", null);
        response.EnsureSuccessStatusCode();

        Assert.Equal("\"2\"", await Client.GetStringAsync($"/api/KeyVal/GetValue/{appKey}/c"));
    }

    [Fact]
    public async Task ActOnValue_now_understands_decrement()
    {
        var appKey = await GetAppKeyAsync();
        await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/c/10", null);

        var response = await Client.PostAsync($"/api/KeyVal/ActOnValue/{appKey}/c/decrement", null);
        response.EnsureSuccessStatusCode();

        Assert.Equal("\"9\"", await Client.GetStringAsync($"/api/KeyVal/GetValue/{appKey}/c"));
    }

    [Fact]
    public async Task Routes_are_case_insensitive()
    {
        var response = await Client.GetAsync("/api/keyval/getappkey");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetCount_returns_a_bare_number()
    {
        var appKey = await GetAppKeyAsync();
        await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/counted/1", null);

        var body = await Client.GetStringAsync("/api/KeyVal/GetCount");

        Assert.True(long.TryParse(body, out var count), $"expected a bare number, got '{body}'");
        Assert.True(count >= 1);
    }

    [Fact]
    public async Task Writing_to_an_app_key_that_was_never_issued_is_a_404()
    {
        var response = await Client.PostAsync("/api/KeyVal/UpdateValue/zzzzzzzz/k/v", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_app_keys_are_rejected()
    {
        var response = await Client.PostAsync("/api/KeyVal/UpdateValue/TOOLONGKEY/k/v", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_actions_are_rejected_rather_than_silently_ignored()
    {
        var appKey = await GetAppKeyAsync();
        await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/c/1", null);

        var response = await Client.PostAsync($"/api/KeyVal/ActOnValue/{appKey}/c/multiply", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIp_answers()
    {
        var response = await Client.GetAsync("/api/KeyVal/GetIp");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Cors_is_open_to_every_origin()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/KeyVal/GetCount");
        request.Headers.Add("Origin", "https://somebody-elses-site.example");

        var response = await Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
