using Immanuel.KeyValue.Core;

namespace Immanuel.KeyValue.Tests;

public class KeyValueStoreTests
{
    [Fact]
    public async Task Stores_and_reads_back_a_value()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync("1.2.3.4", "tests");

        var result = await fixture.Store.SetValueAsync(appKey, "greeting", "hello", null, null);

        Assert.True(result.IsOk);
        Assert.True(result.Created);
        Assert.Equal("hello", await fixture.Store.GetValueAsync(appKey, "greeting"));
    }

    [Fact]
    public async Task Overwriting_reports_that_nothing_was_created()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        await fixture.Store.SetValueAsync(appKey, "k", "first", null, null);
        var second = await fixture.Store.SetValueAsync(appKey, "k", "second", null, null);

        Assert.True(second.IsOk);
        Assert.False(second.Created);
        Assert.Equal("second", await fixture.Store.GetValueAsync(appKey, "k"));
    }

    [Fact]
    public async Task Each_app_key_gets_its_own_database_file()
    {
        using var fixture = new StoreFixture();
        var first = await fixture.Store.CreateAppKeyAsync(null, null);
        var second = await fixture.Store.CreateAppKeyAsync(null, null);

        await fixture.Store.SetValueAsync(first, "shared", "one", null, null);
        await fixture.Store.SetValueAsync(second, "shared", "two", null, null);

        Assert.Equal("one", await fixture.Store.GetValueAsync(first, "shared"));
        Assert.Equal("two", await fixture.Store.GetValueAsync(second, "shared"));

        Assert.True(File.Exists(Path.Combine(fixture.DataDirectory, $"{first}.db")));
        Assert.True(File.Exists(Path.Combine(fixture.DataDirectory, $"{second}.db")));
    }

    [Fact]
    public async Task Unknown_app_key_is_rejected_rather_than_conjured_into_existence()
    {
        using var fixture = new StoreFixture();

        var result = await fixture.Store.SetValueAsync("zzzzzzzz", "k", "v", null, null);

        Assert.Equal(StoreStatus.AppKeyNotFound, result.Status);
        Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, "zzzzzzzz.db")));
    }

    [Fact]
    public async Task Auto_create_restores_the_v1_permissive_behaviour()
    {
        using var fixture = new StoreFixture(o => o.AutoCreateUnknownAppKeys = true);

        var result = await fixture.Store.SetValueAsync("zzzzzzzz", "k", "v", null, null);

        Assert.True(result.IsOk);
        Assert.Equal("v", await fixture.Store.GetValueAsync("zzzzzzzz", "k"));
    }

    [Fact]
    public async Task Missing_key_reads_as_null()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        Assert.Null(await fixture.Store.GetValueAsync(appKey, "never-set"));
    }

    // ---------- injection ----------

    [Theory]
    [InlineData("it's a key")]
    [InlineData("'; DROP TABLE KeyVal; --")]
    [InlineData("key\" OR 1=1")]
    public async Task Quotes_and_sql_in_key_names_are_data_not_code(string key)
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        var result = await fixture.Store.SetValueAsync(appKey, key, "safe", null, null);

        Assert.True(result.IsOk);
        Assert.Equal("safe", await fixture.Store.GetValueAsync(appKey, key));

        // The table is still there, and holds exactly the one row we wrote.
        var all = await fixture.Store.ListAsync(appKey);
        Assert.NotNull(all);
        Assert.Single(all!);
    }

    [Fact]
    public async Task Quotes_and_sql_in_values_are_data_not_code()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        const string nasty = "it's '; DELETE FROM KeyVal; -- {\"json\": \"too\"}";
        await fixture.Store.SetValueAsync(appKey, "k", nasty, null, null);

        Assert.Equal(nasty, await fixture.Store.GetValueAsync(appKey, "k"));
    }

    // ---------- limits ----------

    [Fact]
    public async Task Rejects_a_key_longer_than_the_limit()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        var result = await fixture.Store.SetValueAsync(appKey, new string('k', 65), "v", null, null);

        Assert.Equal(StoreStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Rejects_a_value_longer_than_the_limit()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        var result = await fixture.Store.SetValueAsync(appKey, "k", new string('v', 1025), null, null);

        Assert.Equal(StoreStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Stops_at_the_key_quota()
    {
        using var fixture = new StoreFixture(o => o.MaxKeysPerAppKey = 3);
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await fixture.Store.SetValueAsync(appKey, $"k{i}", "v", null, null)).IsOk);
        }

        var overflow = await fixture.Store.SetValueAsync(appKey, "k3", "v", null, null);
        Assert.Equal(StoreStatus.KeyLimitReached, overflow.Status);

        // Overwriting an existing key still works - the quota counts distinct keys.
        Assert.True((await fixture.Store.SetValueAsync(appKey, "k0", "changed", null, null)).IsOk);
    }

    // ---------- increment ----------

    [Fact]
    public async Task Increment_creates_the_counter_when_it_does_not_exist()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        var result = await fixture.Store.AdjustAsync(appKey, "visits", 1);

        Assert.True(result.IsOk);
        Assert.Equal("1", result.Value);
    }

    [Fact]
    public async Task Increment_adds_to_an_existing_number()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);
        await fixture.Store.SetValueAsync(appKey, "visits", "41", null, null);

        Assert.Equal("42", (await fixture.Store.AdjustAsync(appKey, "visits", 1)).Value);
    }

    [Fact]
    public async Task Decrement_and_larger_steps_work()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);
        await fixture.Store.SetValueAsync(appKey, "n", "10", null, null);

        Assert.Equal("7", (await fixture.Store.AdjustAsync(appKey, "n", -3)).Value);
        Assert.Equal("107", (await fixture.Store.AdjustAsync(appKey, "n", 100)).Value);
    }

    [Fact]
    public async Task Increment_can_cross_zero_into_negatives()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);
        await fixture.Store.SetValueAsync(appKey, "n", "1", null, null);

        Assert.Equal("-1", (await fixture.Store.AdjustAsync(appKey, "n", -2)).Value);

        // ... and back up again, which only works if the numeric guard accepts a leading '-'.
        Assert.Equal("4", (await fixture.Store.AdjustAsync(appKey, "n", 5)).Value);
    }

    [Fact]
    public async Task Null_and_empty_values_count_as_zero()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        await fixture.Store.SetValueAsync(appKey, "empty", "", null, null);
        await fixture.Store.SetValueAsync(appKey, "nothing", null, null, null);

        Assert.Equal("1", (await fixture.Store.AdjustAsync(appKey, "empty", 1)).Value);
        Assert.Equal("1", (await fixture.Store.AdjustAsync(appKey, "nothing", 1)).Value);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("12abc")]
    [InlineData("1.5")]
    [InlineData("1-2")]
    [InlineData("- 5")]
    [InlineData("-")]
    public async Task Increment_refuses_values_that_are_not_whole_numbers(string value)
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);
        await fixture.Store.SetValueAsync(appKey, "k", value, null, null);

        var result = await fixture.Store.AdjustAsync(appKey, "k", 1);

        Assert.Equal(StoreStatus.NotNumeric, result.Status);
        Assert.Equal(value, await fixture.Store.GetValueAsync(appKey, "k"));
    }

    [Fact]
    public async Task Concurrent_increments_do_not_lose_updates()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);
        await fixture.Store.SetValueAsync(appKey, "hits", "0", null, null);

        // The whole reason increment is one UPDATE statement rather than read-then-write.
        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => fixture.Store.AdjustAsync(appKey, "hits", 1)));

        Assert.Equal("50", await fixture.Store.GetValueAsync(appKey, "hits"));
    }

    // ---------- delete & list ----------

    [Fact]
    public async Task Deletes_a_key()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);
        await fixture.Store.SetValueAsync(appKey, "k", "v", null, null);

        Assert.Equal(StoreStatus.Ok, await fixture.Store.DeleteAsync(appKey, "k"));
        Assert.Null(await fixture.Store.GetValueAsync(appKey, "k"));
        Assert.Equal(StoreStatus.KeyNotFound, await fixture.Store.DeleteAsync(appKey, "k"));
    }

    [Fact]
    public async Task Lists_keys_in_order()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        await fixture.Store.SetValueAsync(appKey, "charlie", "3", null, null);
        await fixture.Store.SetValueAsync(appKey, "alpha", "1", null, null);
        await fixture.Store.SetValueAsync(appKey, "bravo", "2", null, null);

        var entries = await fixture.Store.ListAsync(appKey);

        Assert.NotNull(entries);
        Assert.Equal(["alpha", "bravo", "charlie"], entries!.Select(e => e.KeyName));
    }

    [Fact]
    public async Task Listing_an_unknown_app_key_returns_null()
    {
        using var fixture = new StoreFixture();
        Assert.Null(await fixture.Store.ListAsync("zzzzzzzz"));
    }

    // ---------- catalog ----------

    [Fact]
    public async Task Stats_track_app_keys_and_total_keys()
    {
        using var fixture = new StoreFixture();
        var first = await fixture.Store.CreateAppKeyAsync(null, null);
        var second = await fixture.Store.CreateAppKeyAsync(null, null);

        await fixture.Store.SetValueAsync(first, "a", "1", null, null);
        await fixture.Store.SetValueAsync(first, "b", "2", null, null);
        await fixture.Store.SetValueAsync(second, "c", "3", null, null);

        var stats = await fixture.Store.GetStatsAsync();

        Assert.Equal(2, stats.AppKeys);
        Assert.Equal(3, stats.Keys);
    }

    [Fact]
    public async Task Deleting_a_key_lowers_the_total()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        await fixture.Store.SetValueAsync(appKey, "a", "1", null, null);
        await fixture.Store.SetValueAsync(appKey, "b", "2", null, null);
        await fixture.Store.DeleteAsync(appKey, "a");

        Assert.Equal(1, (await fixture.Store.GetStatsAsync()).Keys);
    }

    [Fact]
    public async Task Overwriting_does_not_inflate_the_total()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync(null, null);

        for (var i = 0; i < 5; i++) await fixture.Store.SetValueAsync(appKey, "same", $"v{i}", null, null);

        Assert.Equal(1, (await fixture.Store.GetStatsAsync()).Keys);
    }

    [Fact]
    public async Task App_key_info_reports_the_key_count()
    {
        using var fixture = new StoreFixture();
        var appKey = await fixture.Store.CreateAppKeyAsync("9.9.9.9", "agent");

        await fixture.Store.SetValueAsync(appKey, "a", "1", null, null);
        await fixture.Store.SetValueAsync(appKey, "b", "2", null, null);

        var info = await fixture.Store.GetAppKeyInfoAsync(appKey);

        Assert.NotNull(info);
        Assert.Equal(appKey, info!.ClientKey);
        Assert.Equal(2, info.KeyCount);
        Assert.False(string.IsNullOrWhiteSpace(info.CreatedAt));
    }

    // ---------- app key validation at the store boundary ----------

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEFGH")]
    [InlineData("short")]
    public async Task Malformed_app_keys_never_touch_the_file_system(string appKey)
    {
        using var fixture = new StoreFixture(o => o.AutoCreateUnknownAppKeys = true);

        var result = await fixture.Store.SetValueAsync(appKey, "k", "v", null, null);

        Assert.Equal(StoreStatus.Invalid, result.Status);

        // Only the catalog should exist - no tenant database was created anywhere.
        Assert.DoesNotContain(
            Directory.GetFiles(fixture.DataDirectory, "*.db"),
            file => !Path.GetFileName(file).StartsWith('_'));
    }
}
