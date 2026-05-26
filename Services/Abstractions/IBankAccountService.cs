using BankMod.Data;

namespace BankMod.Services.Abstractions;

/// <summary>Manages per-company bank account persistence and operations.</summary>
public interface IBankAccountService
{
    BankAccountData Load();
    void Save(BankAccountData data);
    void InvalidateCache();
    CompanyAccount GetOrCreateAccount(BankAccountData data, string companyName);
}
