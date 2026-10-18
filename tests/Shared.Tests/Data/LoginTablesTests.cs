using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Protocol.Auth;

namespace Shared.Tests.Data;

/// <summary>Rules of the login client tables (spec 9.10) and the ReasonCodeStrings coverage rule (spec 9.1).</summary>
public class LoginTablesTests
{
    [Theory]
    [InlineData("https://auth.example.com", true)]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://[::1]:5080", true)]
    [InlineData("http://192.168.1.10:5080", false)]
    [InlineData("http://auth.example.com", false)]
    [InlineData("ftp://127.0.0.1", false)]
    [InlineData("not a url", false)]
    public void Auth_endpoint_allows_https_or_loopback_http_only(string url, bool allowed)
    {
        Assert.Equal(allowed, AuthEndpoint.IsAllowed(url, out var problem));
        Assert.Equal(allowed, problem.Length == 0);
    }

    [Fact]
    public void Layout_id_must_be_screen_dot_element()
    {
        var errors = new RowErrors();

        new LoginLayout { Id = "title_logo", Screen = "title", Offset = new[] { 0, 0 }, Size = new[] { 1, 1 } }.Validate(errors);

        Assert.Equal(nameof(LoginLayout.Id), Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void Layout_offset_and_size_must_be_pairs()
    {
        var errors = new RowErrors();

        new LoginLayout { Id = "title.title_logo", Screen = "title", Offset = new[] { 0 }, Size = new[] { 1, 2, 3 } }.Validate(errors);

        Assert.Equal(new[] { nameof(LoginLayout.Offset), nameof(LoginLayout.Size) }, errors.Items.Select(e => e.Field));
    }

    [Fact]
    public void Layout_element_id_is_the_part_after_the_screen()
    {
        Assert.Equal("steam_badge", new LoginLayout { Id = "serverSelect.steam_badge", Screen = "serverSelect" }.ElementId());
    }

    [Fact]
    public void Unknown_login_screen_is_refused()
    {
        var errors = new RowErrors();

        new LoginScreens { Id = "inventory" }.Validate(errors);

        Assert.Single(errors.Items);
    }

    [Fact]
    public void Login_settings_refuse_plain_http_off_loopback()
    {
        var errors = new RowErrors();

        new LoginSettings { AuthApiUrl = "http://10.0.0.5:5080", WorldScene = "Assets/World.unity" }.Validate(errors);

        Assert.Equal(nameof(LoginSettings.AuthApiUrl), Assert.Single(errors.Items).Field);
    }

    [Fact]
    public void Coverage_reports_every_code_without_a_row()
    {
        var set = SetWith("UnknownReason", "NameTaken");

        var errors = ReasonCodeCoverage.Check(set);

        Assert.All(errors, e => Assert.Equal(ValidationCodes.RuleBroken, e.Code));
        Assert.Contains(errors, e => e.Message.Contains("AuthReasonCode.NoFreeSlot"));
        Assert.Contains(errors, e => e.Message.Contains("ConnectRejectReason.VersionMismatch"));
        Assert.Contains(errors, e => e.Message.Contains("ClientReasonCode.SteamUnavailable"));
        Assert.DoesNotContain(errors, e => e.Message.Contains(".NameTaken") || e.Message.Contains(".None"));
    }

    [Fact]
    public void Coverage_requires_the_unknown_reason_row()
    {
        var all = ReasonCodeCoverage.Enums.SelectMany(Enum.GetNames).Where(n => n != "None").Distinct().ToArray();

        var errors = ReasonCodeCoverage.Check(SetWith(all));

        Assert.Contains(ReasonCodeStrings.UnknownCode, Assert.Single(errors).Message);
    }

    [Fact]
    public void Coverage_lists_the_login_and_client_enums()
    {
        Assert.Contains(typeof(AuthReasonCode), ReasonCodeCoverage.Enums);
        Assert.Contains(typeof(ConnectRejectReason), ReasonCodeCoverage.Enums);
        Assert.Contains(typeof(ClientReasonCode), ReasonCodeCoverage.Enums);
    }

    [Fact]
    public void Client_reason_codes_do_not_reuse_server_code_names()
    {
        var server = Enum.GetNames<AuthReasonCode>().Concat(Enum.GetNames<ConnectRejectReason>()).ToHashSet();

        Assert.Equal(new[] { "None" }, Enum.GetNames<ClientReasonCode>().Where(server.Contains));
    }

    private static ConfigSet SetWith(params string[] codes)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ReasonCodeStrings), 1,
            codes.Select(c => (object)new ReasonCodeStrings { Code = c, Text = "x" }).ToArray()));
        return set;
    }
}
