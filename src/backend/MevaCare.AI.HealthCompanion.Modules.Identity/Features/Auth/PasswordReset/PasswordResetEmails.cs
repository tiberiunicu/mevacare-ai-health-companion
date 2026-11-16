using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Features.Auth.PasswordReset;

/// <summary>The two e-mails of the password-reset flow, in English and Romanian (the language the person was using in the app).</summary>
internal static class PasswordResetEmails
{
    private static bool Romanian(string? language) => string.Equals(language, "ro", StringComparison.OrdinalIgnoreCase);

    public static EmailMessage Request(string to, string name, string link, int validMinutes, string? language) =>
        Romanian(language)
            ? new EmailMessage(to, "Resetează-ți parola MevaCare",
                $"Salut, {name}!\n\n" +
                "Am primit o cerere de resetare a parolei pentru contul tău MevaCare. Deschide linkul de mai jos ca să alegi o parolă nouă " +
                $"(este valabil {validMinutes} de minute și poate fi folosit o singură dată):\n\n{link}\n\n" +
                "Dacă nu ai cerut tu resetarea, ignoră acest mesaj: parola ta rămâne neschimbată.\n\nMevaCare")
            : new EmailMessage(to, "Reset your MevaCare password",
                $"Hello {name},\n\n" +
                "We received a request to reset the password of your MevaCare account. Open the link below to choose a new password " +
                $"(it works for {validMinutes} minutes and can be used once):\n\n{link}\n\n" +
                "If you did not ask for this, ignore this message: your password stays the same.\n\nMevaCare");

    public static EmailMessage Changed(string to, string name, string? language) =>
        Romanian(language)
            ? new EmailMessage(to, "Parola ta MevaCare a fost schimbată",
                $"Salut, {name}!\n\nParola contului tău MevaCare tocmai a fost schimbată și ai fost deconectat de pe toate dispozitivele.\n\n" +
                "Dacă nu ai făcut tu asta, resetează-ți din nou parola imediat și scrie-ne.\n\nMevaCare")
            : new EmailMessage(to, "Your MevaCare password was changed",
                $"Hello {name},\n\nThe password of your MevaCare account was just changed and you were signed out everywhere.\n\n" +
                "If this was not you, reset your password again right away and contact us.\n\nMevaCare");
}
