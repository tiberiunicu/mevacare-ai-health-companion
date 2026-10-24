using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Security;

/// <summary>Encrypts provider tokens before they are stored. Tokens never reach the database, the API responses, the export or the logs in clear text.</summary>
public interface ITokenProtector
{
    string Protect(string value);

    /// <summary>False when the value cannot be decrypted (for example the key ring was lost): the account then has to be reconnected.</summary>
    bool TryUnprotect(string protectedValue, out string value);
}

public sealed class DataProtectionTokenProtector(IDataProtectionProvider provider) : ITokenProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("MevaCare.AI.HealthCompanion.Integrations.Tokens.v1");

    public string Protect(string value) => _protector.Protect(value);

    public bool TryUnprotect(string protectedValue, out string value)
    {
        try
        {
            value = _protector.Unprotect(protectedValue);
            return true;
        }
        catch (CryptographicException)
        {
            value = "";
            return false;
        }
    }
}
