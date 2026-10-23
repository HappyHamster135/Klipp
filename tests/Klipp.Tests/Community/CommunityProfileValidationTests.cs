using System.Text;
using Klipp.Core.Community;

namespace Klipp.Tests.Community;

public sealed class CommunityProfileValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("http://community.example.com")]
    [InlineData("https://user:password@community.example.com")]
    [InlineData("https://community.example.com/path")]
    [InlineData("https://community.example.com/?secret=bad")]
    [InlineData("https://project.invalid")]
    public void Incomplete_or_unsafe_endpoints_are_disabled(string url) =>
        new CommunityConfiguration(url, "sb_publishable_testpublickey12345").IsConfigured.ShouldBeFalse();

    [Theory]
    [InlineData("sb_secret_administrativekey12345")]
    [InlineData("YOUR_PUBLISHABLE_KEY")]
    [InlineData("")]
    public void Secret_keys_and_placeholders_are_never_usable_client_configuration(string key) =>
        new CommunityConfiguration("https://project.supabase.co", key).IsConfigured.ShouldBeFalse();

    [Theory]
    [InlineData("anon", true)]
    [InlineData("service_role", false)]
    [InlineData("authenticated", false)]
    public void Only_the_legacy_anon_role_is_accepted_in_distributed_configuration(string role, bool accepted)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"role\":\"{role}\"}}" )).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        new CommunityConfiguration("https://project.supabase.co", $"header.{payload}.signature").IsConfigured.ShouldBe(accepted);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("1player")]
    [InlineData("player-name")]
    [InlineData("player name")]
    [InlineData("player'")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    public void Invalid_usernames_fail_before_network_work(string username) =>
        Should.Throw<CommunityAccountException>(() => CommunityProfileValidation.Validate(username, "Player", "")).Code.ShouldBe("invalid_username");

    [Fact]
    public void Profile_fields_are_normalized_and_length_limits_match_the_database()
    {
        CommunityProfileValidation.Validate(" PLAYER_1 ", " Player ", " Bio ")
            .ShouldBe(("player_1", "Player", "Bio"));
        Should.Throw<CommunityAccountException>(() => CommunityProfileValidation.Validate("player", new string('a', 41), ""));
        Should.Throw<CommunityAccountException>(() => CommunityProfileValidation.Validate("player", "Player", new string('a', 161)));
        Should.Throw<CommunityAccountException>(() => CommunityProfileValidation.Validate("player", "Player\nOther", ""));
    }

    [Theory]
    [InlineData("not-email")]
    [InlineData("Player <player@example.com>")]
    [InlineData("player@localhost")]
    public void Invalid_email_input_is_rejected(string email) =>
        Should.Throw<CommunityAccountException>(() => CommunityProfileValidation.NormalizeEmail(email)).Code.ShouldBe("invalid_email");
}
