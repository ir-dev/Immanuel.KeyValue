using Immanuel.KeyValue.Core;

namespace Immanuel.KeyValue.Tests;

/// <summary>
/// An email address becomes a directory name, so these are the tests that keep a crafted address
/// from escaping the data directory - the same job <see cref="AppKeyTests"/> does for app keys.
/// </summary>
public class UserFolderTests
{
    [Theory]
    [InlineData("raj@immanuel.co", "raj_at_immanuel.co")]
    [InlineData("RAJ@Immanuel.CO", "raj_at_immanuel.co")]      // case is normalised away
    [InlineData("  raj@immanuel.co  ", "raj_at_immanuel.co")]  // as is surrounding whitespace
    [InlineData("first.last+tag@sub.example.co.uk", "first.last+tag_at_sub.example.co.uk")]
    [InlineData("a1_b2@example.com", "a1_b2_at_example.com")]
    public void Maps_an_address_onto_a_folder(string email, string expected) =>
        Assert.Equal(expected, UserFolder.ToFolderName(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("two@at@example.com")]
    [InlineData("user@localhost")]              // no dotted domain, so no sensible folder
    [InlineData("user@example.c")]              // one-letter TLD
    [InlineData("../../etc/passwd@evil.com")]   // path traversal in the local part
    [InlineData("user@../evil.com")]
    [InlineData("us/er@example.com")]
    [InlineData("us\\er@example.com")]
    [InlineData("user@exa mple.com")]
    [InlineData("user\0@example.com")]
    [InlineData(".hidden@example.com")]         // would become a dotfile
    [InlineData("_reserved@example.com")]       // would look like _catalog.db / _users.db
    [InlineData("user..name@example.com")]
    [InlineData("user@example..com")]
    public void Refuses_anything_else(string? email)
    {
        Assert.Null(UserFolder.ToFolderName(email));
        Assert.Null(UserFolder.NormalizeEmail(email));
        Assert.False(UserFolder.IsValidEmail(email));
    }

    [Fact]
    public void Refuses_an_address_longer_than_the_rfc_allows()
    {
        var tooLong = new string('a', 250) + "@example.com";

        Assert.Null(UserFolder.ToFolderName(tooLong));
    }

    [Theory]
    [InlineData("raj@immanuel.co")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("foo_at_bar@example.com")]  // the local part contains the separator token itself
    public void Round_trips_back_to_the_address(string email)
    {
        var folder = UserFolder.ToFolderName(email);

        Assert.NotNull(folder);
        Assert.Equal(email, UserFolder.ToEmail(folder));
    }

    [Theory]
    [InlineData("raj_at_immanuel.co")]
    [InlineData("foo_at_bar_at_example.com")]
    public void Accepts_folders_it_produced(string folder) => Assert.True(UserFolder.IsValidFolderName(folder));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("_catalog")]
    [InlineData("_users")]
    [InlineData("3cg7aby9")]              // an app key is not a user folder
    [InlineData("..")]
    [InlineData("../raj_at_immanuel.co")]
    [InlineData("raj_at_immanuel.co/..")]
    [InlineData("raj@immanuel.co")]       // the unmapped address is not a folder name
    [InlineData("plainfolder")]
    public void Refuses_folders_it_did_not(string? folder) => Assert.False(UserFolder.IsValidFolderName(folder));
}
