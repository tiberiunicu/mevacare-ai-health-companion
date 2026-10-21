using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;
using MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth;
using MevaCare.AI.HealthCompanion.Modules.Identity.Options;
using MevaCare.AI.HealthCompanion.UnitTests.Support;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MevaCare.AI.HealthCompanion.UnitTests.Identity;

public class UserTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Normalize_ignores_case_and_surrounding_whitespace()
    {
        Assert.Equal(User.Normalize("  Jane.Doe@Example.test "), User.Normalize("jane.doe@example.TEST"));
    }

    [Fact]
    public void Register_assigns_the_user_role_only()
    {
        var user = User.Register("a@example.test", "A", "hash");
        Assert.Equal([Roles.User], user.Roles.Select(r => r.Role));
    }

    [Fact]
    public void Account_locks_after_the_maximum_number_of_failed_logins()
    {
        var user = User.Register("a@example.test", "A", "hash");
        for (var i = 0; i < 4; i++)
        {
            user.RegisterFailedLogin(Now, 5, TimeSpan.FromMinutes(15));
        }

        Assert.False(user.IsLockedOut(Now));

        user.RegisterFailedLogin(Now, 5, TimeSpan.FromMinutes(15));

        Assert.True(user.IsLockedOut(Now));
        Assert.True(user.IsLockedOut(Now.AddMinutes(14)));
        Assert.False(user.IsLockedOut(Now.AddMinutes(16)));
    }

    [Fact]
    public void Successful_login_clears_failures_and_lockout()
    {
        var user = User.Register("a@example.test", "A", "hash");
        user.RegisterFailedLogin(Now, 1, TimeSpan.FromMinutes(15));

        user.RegisterSuccessfulLogin(Now.AddMinutes(20));

        Assert.False(user.IsLockedOut(Now));
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Equal(Now.AddMinutes(20), user.LastLoginAtUtc);
    }
}

public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _validator = new();

    private static RegisterRequest Valid() => new("jane@example.test", "correct horse battery", "Jane", true);

    [Fact]
    public void A_complete_request_is_valid() => Assert.True(_validator.Validate(Valid()).IsValid);

    [Theory]
    [InlineData("short")]
    [InlineData("          ")]
    [InlineData("123456789")]
    public void Weak_passwords_are_rejected(string password) =>
        Assert.False(_validator.Validate(Valid() with { Password = password }).IsValid);

    [Fact]
    public void Password_equal_to_the_email_is_rejected() =>
        Assert.False(_validator.Validate(Valid() with { Email = "jane.doe@example.test", Password = "JANE.DOE@example.test" }).IsValid);

    [Fact]
    public void Terms_must_be_accepted() =>
        Assert.False(_validator.Validate(Valid() with { AcceptTerms = false }).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Invalid_emails_are_rejected(string email) =>
        Assert.False(_validator.Validate(Valid() with { Email = email }).IsValid);
}

public class TokenServiceTests
{
    private static TokenService Create(out JwtOptions jwt)
    {
        jwt = new JwtOptions { SigningKey = new string('k', 48), AccessTokenMinutes = 15 };
        return new TokenService(Options.Create(jwt), TestDb.Clock());
    }

    [Fact]
    public void Access_token_carries_subject_and_roles_but_no_personal_data()
    {
        var service = Create(out var jwt);
        var user = User.Register("jane@example.test", "Jane Doe", "hash");

        var token = service.CreateAccessToken(user);

        var parsed = new JsonWebToken(token.Value);
        Assert.Equal(user.Id.ToString(), parsed.Subject);
        Assert.Equal(jwt.Issuer, parsed.Issuer);
        Assert.Contains(parsed.Claims, c => c.Type == "role" && c.Value == Roles.User);
        Assert.DoesNotContain("jane", token.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(parsed.Claims, c => c.Value.Contains("example.test", StringComparison.Ordinal));
        Assert.Equal(TestDb.Clock().GetUtcNow().UtcDateTime.AddMinutes(15), token.ExpiresAtUtc);
    }

    [Fact]
    public void Refresh_tokens_are_random_and_only_their_hash_is_derivable()
    {
        var service = Create(out _);

        var (raw1, hash1) = service.CreateRefreshToken();
        var (raw2, _) = service.CreateRefreshToken();

        Assert.NotEqual(raw1, raw2);
        Assert.NotEqual(raw1, hash1);
        Assert.Equal(hash1, service.HashRefreshToken(raw1));
        Assert.True(raw1.Length >= 43);
    }
}
