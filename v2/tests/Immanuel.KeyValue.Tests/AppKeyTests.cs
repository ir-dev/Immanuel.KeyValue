using Immanuel.KeyValue.Core;

namespace Immanuel.KeyValue.Tests;

/// <summary>
/// An app key becomes a file name, so these are the tests that keep a crafted key from
/// escaping the data directory.
/// </summary>
public class AppKeyTests
{
    [Fact]
    public void Generate_produces_a_valid_key()
    {
        for (var i = 0; i < 500; i++)
        {
            var key = AppKey.Generate();
            Assert.Equal(8, key.Length);
            Assert.True(AppKey.IsValid(key), $"generated key '{key}' failed validation");
        }
    }

    [Fact]
    public void Generate_does_not_repeat_itself()
    {
        var keys = new HashSet<string>();
        for (var i = 0; i < 1000; i++) keys.Add(AppKey.Generate());

        // Collisions are possible in principle but astronomically unlikely at this sample size.
        Assert.Equal(1000, keys.Count);
    }

    [Theory]
    [InlineData("3cg7aby9")]
    [InlineData("abcdefgh")]
    [InlineData("00000000")]
    public void Accepts_well_formed_keys(string key) => Assert.True(AppKey.IsValid(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("toolongkey")]
    [InlineData("ABCDEFGH")]       // uppercase would collide with the lowercase file on macOS/Windows
    [InlineData("abcdefg!")]
    [InlineData("abc defg")]
    [InlineData("../etc/p")]       // path traversal
    [InlineData("..\\..\\ab")]
    [InlineData("/tmp/abc")]
    [InlineData("a/b/c/d/")]
    [InlineData("_catalog")]       // must never resolve to the catalog database
    [InlineData("abc\0defg")]
    public void Rejects_anything_else(string? key) => Assert.False(AppKey.IsValid(key));
}
