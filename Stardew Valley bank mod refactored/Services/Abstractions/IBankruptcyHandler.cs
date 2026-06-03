using BankMod.Data;

namespace BankMod.Services.Abstractions;

/// <summary>Detects and manages bankruptcy: eligibility check, state transitions, shipping income garnishment.</summary>
public interface IBankruptcyHandler
{
    /// <summary>
    /// Check bankruptcy conditions after daily interest/loan processing.
    /// Conditions: player cash < total owed AND total deposits < total owed.
    /// If met: enters bankruptcy state (interest frozen, borrow/withdraw blocked).
    /// If cleared: exits bankruptcy state.
    /// </summary>
    void CheckBankruptcy(BankAccountData account, ModConfig config);

    /// <summary>
    /// During bankruptcy, deduct a percentage of daily shipping income toward debt.
    /// Returns the amount deducted.
    /// </summary>
    int ApplyShippingIncomeDeduction(BankAccountData account, ModConfig config, int shippingIncome);
}
