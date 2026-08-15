using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Immanuel.KeyValue.Tests;

/// <summary>
/// The account flow end to end: sign up, take the code, get a session, issue app keys into your
/// own folder, and reach them with the custom header you chose.
///
/// The fixture configures no SMTP relay, so every code check accepts
/// <see cref="ApiFixture.MasterOtp"/> - the same fallback a fresh checkout runs with.
/// </summary>
public class AccountApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client => fixture.CreateClient();

    // Each test gets its own address, so one signing up cannot make another's sign-up a conflict.
    private static string NewEmail() => $"user{Guid.NewGuid():n}@example.com";

    /// <summary>Signs a fresh account up and returns its address and session token.</summary>
    private async Task<(string Email, string Token)> SignUpAsync()
    {
        var email = NewEmail();

        var requested = await Client.PostAsJsonAsync("/api/v2/auth/signup", new { email }, Json);
        requested.EnsureSuccessStatusCode();

        var verified = await Client.PostAsJsonAsync(
            "/api/v2/auth/verify", new { email, code = ApiFixture.MasterOtp }, Json);
        verified.EnsureSuccessStatusCode();

        var body = await verified.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (email, body.GetProperty("token").GetString()!);
    }

    private HttpClient SignedIn(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpClient WithHeader(HttpClient client, string name, string value)
    {
        client.DefaultRequestHeaders.Add(name, value);
        return client;
    }

    private async Task<string> NewAppKeyAsync(string token)
    {
        var response = await SignedIn(token).PostAsync("/api/v2/me/appkeys", null);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("appKey").GetString()!;
    }

    private async Task<(string Name, string Value)> SetHeaderAsync(string token, string? value = null)
    {
        var header = (Name: "x-test-token", Value: value ?? $"kv_{Guid.NewGuid():n}");

        var response = await SignedIn(token).PutAsJsonAsync(
            "/api/v2/me/header", new { name = header.Name, value = header.Value }, Json);

        response.EnsureSuccessStatusCode();
        return header;
    }

    // ---------- sign-up and sign-in ----------

    [Fact]
    public async Task Signing_up_reports_that_the_master_code_is_in_play()
    {
        var response = await Client.PostAsJsonAsync("/api/v2/auth/signup", new { email = NewEmail() }, Json);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal("master", body.GetProperty("delivery").GetString());

        // RevealMasterOtp is off, so the code itself must not come back over the API.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("code").ValueKind);
    }

    [Fact]
    public async Task Signing_up_twice_is_a_409()
    {
        var email = NewEmail();

        (await Client.PostAsJsonAsync("/api/v2/auth/signup", new { email }, Json)).EnsureSuccessStatusCode();

        var again = await Client.PostAsJsonAsync("/api/v2/auth/signup", new { email }, Json);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Signing_in_without_an_account_is_a_404()
    {
        var response = await Client.PostAsJsonAsync("/api/v2/auth/signin", new { email = NewEmail() }, Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("../../etc/passwd@evil.com")]
    public async Task An_address_that_cannot_become_a_folder_is_a_400(string email)
    {
        var response = await Client.PostAsJsonAsync("/api/v2/auth/signup", new { email }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_is_rejected_and_the_limit_discards_it()
    {
        var email = NewEmail();
        (await Client.PostAsJsonAsync("/api/v2/auth/signup", new { email }, Json)).EnsureSuccessStatusCode();

        // The fixture allows three attempts.
        for (var attempt = 1; attempt < 3; attempt++)
        {
            var wrong = await Client.PostAsJsonAsync("/api/v2/auth/verify", new { email, code = "111111" }, Json);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var spent = await Client.PostAsJsonAsync("/api/v2/auth/verify", new { email, code = "111111" }, Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);

        // The code is gone, so even the right one no longer works.
        var afterwards = await Client.PostAsJsonAsync(
            "/api/v2/auth/verify", new { email, code = ApiFixture.MasterOtp }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    [Fact]
    public async Task Verifying_returns_a_session_and_the_account()
    {
        var (email, token) = await SignUpAsync();

        Assert.NotEmpty(token);

        var me = await SignedIn(token).GetFromJsonAsync<JsonElement>("/api/v2/me", Json);

        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal(email.Replace("@", "_at_"), me.GetProperty("folder").GetString());
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var (_, token) = await SignUpAsync();

        (await SignedIn(token).PostAsync("/api/v2/auth/signout", null)).EnsureSuccessStatusCode();

        var afterwards = await SignedIn(token).GetAsync("/api/v2/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    // ---------- folders on disk ----------

    [Fact]
    public async Task An_app_key_lands_in_the_folder_named_after_the_address()
    {
        var (email, token) = await SignUpAsync();
        var appKey = await NewAppKeyAsync(token);

        var expected = Path.Combine(fixture.DataDirectory, email.Replace("@", "_at_"), $"{appKey}.db");

        Assert.True(File.Exists(expected), $"expected a database at {expected}");
    }

    [Fact]
    public async Task An_anonymous_app_key_stays_in_the_data_directory()
    {
        var response = await Client.PostAsync("/api/v2/appkeys", null);
        response.EnsureSuccessStatusCode();

        var appKey = (await response.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("appKey").GetString()!;

        Assert.True(File.Exists(Path.Combine(fixture.DataDirectory, $"{appKey}.db")));
    }

    // ---------- the custom API header ----------

    [Fact]
    public async Task The_header_authenticates_calls_to_an_owned_app_key()
    {
        var (_, token) = await SignUpAsync();
        var appKey = await NewAppKeyAsync(token);
        var header = await SetHeaderAsync(token);

        var client = WithHeader(fixture.CreateClient(), header.Name, header.Value);

        var written = await client.PutAsJsonAsync(
            $"/api/v2/appkeys/{appKey}/keys/greeting", new { value = "hello" }, Json);
        written.EnsureSuccessStatusCode();

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/v2/appkeys/{appKey}/keys/greeting", Json);
        Assert.Equal("hello", read.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Header_names_are_matched_case_insensitively()
    {
        var (_, token) = await SignUpAsync();
        var appKey = await NewAppKeyAsync(token);
        var header = await SetHeaderAsync(token);

        // HTTP field names are case-insensitive, so the credential has to be too.
        var client = WithHeader(fixture.CreateClient(), header.Name.ToUpperInvariant(), header.Value);

        (await client.GetAsync($"/api/v2/appkeys/{appKey}/keys")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task An_owned_app_key_is_a_403_without_the_header()
    {
        var (_, token) = await SignUpAsync();
        var appKey = await NewAppKeyAsync(token);
        await SetHeaderAsync(token);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client.GetAsync($"/api/v2/appkeys/{appKey}/keys")).StatusCode);

        // ... including through the v1 API, which is the same store underneath.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client.GetAsync($"/api/KeyVal/GetValue/{appKey}/greeting")).StatusCode);
    }

    [Fact]
    public async Task One_account_cannot_reach_another_account_s_app_key()
    {
        var (_, mine) = await SignUpAsync();
        var appKey = await NewAppKeyAsync(mine);

        var (_, theirs) = await SignUpAsync();
        var theirHeader = await SetHeaderAsync(theirs);

        var client = WithHeader(fixture.CreateClient(), theirHeader.Name, theirHeader.Value);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v2/appkeys/{appKey}/keys")).StatusCode);
    }

    [Fact]
    public async Task Two_accounts_cannot_claim_the_same_header()
    {
        var (_, first) = await SignUpAsync();
        var header = await SetHeaderAsync(first);

        var (_, second) = await SignUpAsync();

        var response = await SignedIn(second).PutAsJsonAsync(
            "/api/v2/me/header", new { name = header.Name, value = header.Value }, Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("authorization", "kv_a_perfectly_fine_value")]   // reserved
    [InlineData("x bad name", "kv_a_perfectly_fine_value")]      // not an HTTP token
    [InlineData("x-test-token", "short")]                        // too short to be a secret
    public async Task A_header_the_server_cannot_accept_is_a_400(string name, string value)
    {
        var (_, token) = await SignUpAsync();

        var response = await SignedIn(token).PutAsJsonAsync("/api/v2/me/header", new { name, value }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Removing_the_header_closes_the_app_key_off()
    {
        var (_, token) = await SignUpAsync();
        var appKey = await NewAppKeyAsync(token);
        var header = await SetHeaderAsync(token);

        (await SignedIn(token).DeleteAsync("/api/v2/me/header")).EnsureSuccessStatusCode();

        var client = WithHeader(fixture.CreateClient(), header.Name, header.Value);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v2/appkeys/{appKey}/keys")).StatusCode);
    }

    // ---------- account management needs a session ----------

    [Fact]
    public async Task The_api_header_cannot_manage_the_account()
    {
        var (_, token) = await SignUpAsync();
        var header = await SetHeaderAsync(token);

        var client = WithHeader(fixture.CreateClient(), header.Name, header.Value);

        // A leaked API header must not be able to mint app keys or rewrite the credential itself.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v2/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v2/me/appkeys", null)).StatusCode);

        var rewrite = await client.PutAsJsonAsync(
            "/api/v2/me/header", new { name = "x-attacker", value = "kv_attacker_value_here" }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, rewrite.StatusCode);
    }

    [Fact]
    public async Task Account_routes_need_a_session_at_all()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/v2/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/api/v2/me/appkeys")).StatusCode);
    }

    // ---------- app keys ----------

    [Fact]
    public async Task Issuing_with_the_header_present_issues_to_that_account()
    {
        var (_, token) = await SignUpAsync();
        var header = await SetHeaderAsync(token);

        var client = WithHeader(fixture.CreateClient(), header.Name, header.Value);

        var response = await client.PostAsync("/api/v2/appkeys", null);
        response.EnsureSuccessStatusCode();

        var appKey = (await response.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("appKey").GetString()!;

        var keys = await SignedIn(token).GetFromJsonAsync<JsonElement>("/api/v2/me/appkeys", Json);

        Assert.Contains(appKey, keys.EnumerateArray().Select(k => k.GetProperty("appKey").GetString()));
    }

    [Fact]
    public async Task Deleting_an_app_key_removes_its_database()
    {
        var (email, token) = await SignUpAsync();
        var appKey = await NewAppKeyAsync(token);
        var header = await SetHeaderAsync(token);

        var client = WithHeader(fixture.CreateClient(), header.Name, header.Value);
        (await client.PutAsJsonAsync($"/api/v2/appkeys/{appKey}/keys/k", new { value = "v" }, Json))
            .EnsureSuccessStatusCode();

        (await SignedIn(token).DeleteAsync($"/api/v2/me/appkeys/{appKey}")).EnsureSuccessStatusCode();

        var path = Path.Combine(fixture.DataDirectory, email.Replace("@", "_at_"), $"{appKey}.db");
        Assert.False(File.Exists(path), $"{path} should be gone");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/appkeys/{appKey}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_somebody_else_s_app_key_is_a_404()
    {
        var (_, mine) = await SignUpAsync();
        var appKey = await NewAppKeyAsync(mine);

        var (_, theirs) = await SignUpAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await SignedIn(theirs).DeleteAsync($"/api/v2/me/appkeys/{appKey}")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_app_keys_are_unaffected_by_any_of_this()
    {
        var appKey = JsonSerializer.Deserialize<string>(
            await Client.GetStringAsync("/api/KeyVal/GetAppKey"))!;

        // No credential of any kind, exactly as a caller written against v1 in 2017 would do it.
        (await Client.PostAsync($"/api/KeyVal/UpdateValue/{appKey}/visits/41", null)).EnsureSuccessStatusCode();
        (await Client.PostAsync($"/api/KeyVal/ActOnValue/{appKey}/visits/increment", null)).EnsureSuccessStatusCode();

        Assert.Equal("\"42\"", await Client.GetStringAsync($"/api/KeyVal/GetValue/{appKey}/visits"));
    }

    [Fact]
    public async Task The_state_endpoint_describes_the_deployment()
    {
        var state = await Client.GetFromJsonAsync<JsonElement>("/api/v2/auth/state", Json);

        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.Equal("master", state.GetProperty("delivery").GetString());
    }
}

/// <summary>
/// The local-development shortcut: with <c>Auth:RevealMasterOtp</c> on, the code comes back in
/// the response so the console can fill it in and a fresh checkout needs no mailbox at all.
/// </summary>
public class RevealedOtpTests(RevealingApiFixture fixture) : IClassFixture<RevealingApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task The_master_code_comes_back_and_signs_the_account_in()
    {
        var client = fixture.CreateClient();
        var email = $"user{Guid.NewGuid():n}@example.com";

        var requested = await client.PostAsJsonAsync("/api/v2/auth/signup", new { email }, Json);
        requested.EnsureSuccessStatusCode();

        var code = (await requested.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("code").GetString();

        Assert.Equal(ApiFixture.MasterOtp, code);

        var verified = await client.PostAsJsonAsync("/api/v2/auth/verify", new { email, code }, Json);
        verified.EnsureSuccessStatusCode();
    }
}
