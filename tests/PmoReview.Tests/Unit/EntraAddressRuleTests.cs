using PmoReview.Core.Common;
using PmoReview.Core.Services;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Unit;

/// <summary>
/// 7 Nov 2026 (live simulation): Enterprise ID "fdsafas.@…" was accepted and gave the domain ID "fdsafas.@tenant…", which
/// Entra refuses ("Can't contain a period character '.' immediately preceding the '@' symbol", VERIFIED_FACTS F136). The
/// request form, the API and every mail address now take dots only between characters.
/// </summary>
public class EntraAddressRuleTests
{
    [Theory]
    [InlineData("fdsafas.@dasfasd.com")]
    [InlineData(".jane@contoso.com")]
    [InlineData("jane..doe@contoso.com")]
    public async Task An_enterprise_id_with_a_dot_before_the_at_sign_or_a_stray_dot_is_refused(string enterpriseId)
    {
        var app = TestApp.With();
        var r = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", TestApp.Intake(enterpriseId: enterpriseId));
        Assert.Equal(400, r.Status);
        Assert.Contains("Enterprise ID", r.Body);
    }

    [Fact]
    public async Task A_normal_enterprise_id_is_still_accepted()
    {
        var app = TestApp.With();
        var r = await app.Call(TestApp.Alice, "POST", $"projects/{TestApp.Alpha}/requests", TestApp.Intake(enterpriseId: "sample.jane.o'neil-2@sample-corp.invalid"));
        Assert.Equal(201, r.Status);
    }

    [Theory]
    [InlineData("fdsafas.@tenant.onmicrosoft.com", "dot right before")]
    [InlineData(".a@tenant.onmicrosoft.com", "two dots")]
    [InlineData("a..b@tenant.onmicrosoft.com", "two dots")]
    [InlineData("@tenant.onmicrosoft.com", "empty")]
    public void A_domain_id_entra_refuses_is_named(string domainId, string why) =>
        Assert.Contains(why, DomainIds.EntraProblem(domainId));

    [Fact]
    public void A_domain_id_entra_accepts_has_no_problem()
    {
        Assert.Null(DomainIds.EntraProblem("jane.doe@tenant.onmicrosoft.com"));
        Assert.Contains("64", DomainIds.EntraProblem(new string('a', 65) + "@tenant.onmicrosoft.com"));
        // Stripping a character Entra does not take (here '+') can leave a dot before '@': caught on the derived id too.
        Assert.Equal("jane.@tenant.onmicrosoft.com", DomainIds.Derive("jane.+@x.com", "@tenant.onmicrosoft.com"));
        Assert.NotNull(DomainIds.EntraProblem(DomainIds.Derive("jane.+@x.com", "@tenant.onmicrosoft.com")));
    }

    [Theory]
    [InlineData("fdsafas.@dasfasd.com")]
    [InlineData(".a@b.com")]
    [InlineData("a..b@c.com")]
    public void Mail_addresses_take_dots_only_between_characters(string address) => Assert.Null(MailAddresses.Normalize(address));

    [Fact]
    public void Ordinary_and_guest_sign_in_addresses_still_normalise()
    {
        Assert.Equal("jane.doe@contoso.com", MailAddresses.Normalize("Jane Doe <Jane.Doe@contoso.com>"));
        Assert.Equal("a.b+c@d.com", MailAddresses.Normalize("mailto:a.b+c@d.com"));
    }
}
