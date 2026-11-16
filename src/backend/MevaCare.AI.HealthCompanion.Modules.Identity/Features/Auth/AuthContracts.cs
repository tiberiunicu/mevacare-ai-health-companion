using FluentValidation;
using MevaCare.AI.HealthCompanion.Modules.Identity.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth;

public sealed record RegisterRequest(
    string Email,
    string Password,
    string DisplayName,
    bool AcceptTerms,
    bool HealthDataConsent = false,
    bool AiProcessingConsent = false);

public sealed record LoginRequest(string Email, string Password);

/// <param name="HasPassword">False for an account created with Google: it can sign in only with Google.</param>
/// <param name="LinkedProviders">Outside sign-in providers connected to the account, e.g. "Google".</param>
public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    bool HasPassword,
    IReadOnlyCollection<string> LinkedProviders);

public sealed record AuthResponse(string AccessToken, DateTime ExpiresAtUtc, UserDto User);

/// <summary>Result of a sign-in/refresh. The refresh token travels only in an httpOnly cookie, never in the JSON body.</summary>
public sealed record AuthSession(AuthResponse Response, string RefreshToken, DateTime RefreshTokenExpiresAtUtc);

internal static class UserMapping
{
    public static UserDto ToDto(this User user) =>
        new(user.Id, user.Email, user.DisplayName, user.Roles.Select(r => r.Role).Order().ToArray(),
            user.HasPassword, user.ExternalLogins.Select(l => l.Provider).Order().ToArray());
}

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256).EmailAddress();
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).SetValidator(new PasswordValidator());
        RuleFor(x => x.Password).NotEqual(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .WithMessage("The password must not be the same as the e-mail address.");
        RuleFor(x => x.AcceptTerms).Equal(true).WithMessage("You must accept the Terms of Service and Privacy Policy.");
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

/// <summary>Length-based policy (NIST SP 800-63B): long passphrases are allowed, composition rules are not imposed.</summary>
public sealed class PasswordValidator : AbstractValidator<string>
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    public PasswordValidator()
    {
        RuleFor(x => x)
            .NotEmpty()
            .MinimumLength(MinLength).WithMessage($"The password must be at least {MinLength} characters long.")
            .MaximumLength(MaxLength).WithMessage($"The password must be at most {MaxLength} characters long.")
            .Must(p => !string.IsNullOrWhiteSpace(p)).WithMessage("The password must not be blank.");
    }
}
