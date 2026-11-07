using Microsoft.AspNetCore.DataProtection;
namespace Ledgerra.Infrastructure.Security;

public sealed class BankSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Ledgerra.BankConnections.v1");
    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string value) => _protector.Unprotect(value);
}
