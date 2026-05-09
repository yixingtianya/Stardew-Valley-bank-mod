using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;
using StardewModdingAPI;
using StardewValley;

namespace BankMod.Services.Core;

/// <summary>Central service for company lifecycle: daily interest settlement, status queries,
/// and dynamic company generation. Full Stage 6 orchestrator.</summary>
public class CompanyManager : ICompanyManager
{
    private readonly IInterestCalculator _fixedInterestCalculator;
    private readonly IInterestCalculator _dynamicInterestCalculator;
    private readonly IBankAccountService _accountService;
    private readonly ILoanService _loanService;
    private readonly ShipmentTrackingService _shipmentTracking;
    private readonly IFuelService _fuelService;
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;

    private const int MaxDynamicCompanies = 4;
    private const int MaxNewPerSeason = 2;
    private const int NewCompanyProtectionDays = 3;
    public CompanyManager(
        IInterestCalculator fixedInterestCalculator,
        IInterestCalculator dynamicInterestCalculator,
        IBankAccountService accountService,
        ILoanService loanService,
        ShipmentTrackingService shipmentTracking,
        IFuelService fuelService,
        ModConfig config,
        IMonitor monitor)
    {
        _fixedInterestCalculator = fixedInterestCalculator;
        _dynamicInterestCalculator = dynamicInterestCalculator;
        _accountService = accountService;
        _loanService = loanService;
        _shipmentTracking = shipmentTracking;
        _fuelService = fuelService;
        _config = config;
        _monitor = monitor;
    }

    // ============================================================================
    // ICompanyManager — Public API
    // ============================================================================

    public void OnDayStarted()
    {
        var account = _accountService.Load();

        // Step 1: Update consecutive selling days
        _shipmentTracking.UpdateConsecutiveDays(account);

        // Step 2: Reset seasonal spawn counter if season changed
        ResetSeasonalSpawnCounter(account);

        // Step 3: Check for new dynamic company generation
        CheckAndGenerateNewCompanies(account);

        // Step 3.5: Tick suppression countdowns (Stage 7.3)
        TickSuppression(account);

        // Step 4: Update fuel inventory on the shared account → derive company statuses
        _fuelService.UpdateDailyInventory(account);
        UpdateCompanyStatuses(account);

        // Daily status summary
        if (account.DynamicCompanies.Count > 0)
        {
            foreach (var dc in account.DynamicCompanies)
            {
                _monitor.Log(
                    $"[状态] {dc.CompanyName} | {dc.Status} | 燃料={dc.FuelStock}FP",
                    LogLevel.Info);
            }
        }

        // Step 5: Settle daily interest (fixed + dynamic)
        SettleDailyInterest(account);

        // Step 5.5: V3.7 manual compound interest detection
        DetectManualCompoundInterest(account);

        // Step 6: Settle daily loans
        _loanService.DailyTick(account, _config, _fixedInterestCalculator);

        _accountService.Save(account);
    }

    public CompanyStatus GetStatus(CompanyId companyId)
    {
        var account = _accountService.Load();
        var dyn = account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == companyId.Value);
        if (dyn is not null) return dyn.Status;
        return CompanyStatus.Stable;
    }

    public IReadOnlyList<CompanyId> GetActiveCompanies()
    {
        var account = _accountService.Load();
        var all = GetAllCompanyDefinitions(account);
        return all.Select(c => new CompanyId(c.Name)).ToList();
    }

    public List<CompanyDefinition> GetAllCompanyDefinitions(BankAccountData account)
    {
        var result = new List<CompanyDefinition>(_config.Companies.Count + account.DynamicCompanies.Count);

        foreach (var c in _config.Companies)
            result.Add(c);

        foreach (var dc in account.DynamicCompanies)
        {
            if (dc.Status == CompanyStatus.Bankrupt) continue;

            var cropData = CropDataProvider.GetByCode(dc.CropCode);
            double r = cropData?.R ?? 0;
            // V3.5 patch5 3-tier discount rules applied to base rates
            double depRate = r > 0.20 ? r * 0.5 : r > 0 ? r : 0;
            double loanRate = r > 0.20 ? r * 0.75 : r > 0 ? r * 1.5 : 0;

            result.Add(new CompanyDefinition
            {
                Name = dc.CompanyName,
                CropCode = dc.CropCode,
                IsDynamic = true,
                DepositInterestRate = depRate,
                LoanInterestRate = loanRate,
                DepositLimit = dc.DepositLimit,
                LoanLimit = dc.LoanLimit,
                RainBonus = 0,
                SunBonus = 0,
                SnowBonus = 0,
                LightningBonus = 0
            });
        }

        return result;
    }

    public int GetMaxWithdrawal(BankAccountData account, CompanyDefinition company)
    {
        var ca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == company.Name);
        if (ca is null) return 0;

        // Fixed companies: no asset pool restriction
        if (!company.IsDynamic)
            return ca.DepositBalance;

        // Dynamic companies: asset pool only during Hungry/Dying
        var dyn = account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == company.Name);
        if (dyn is null) return ca.DepositBalance;

        double statusMultiplier = dyn.Status switch
        {
            CompanyStatus.Hungry => 0.6,
            CompanyStatus.Dying => 0.3,
            _ => 1.0
        };

        // Statuses other than Hungry/Dying: no asset pool restriction
        if (statusMultiplier >= 1.0)
            return ca.DepositBalance;

        int effectiveLoanLimit = (int)(company.LoanLimit * statusMultiplier);
        int outstandingPrincipal = account.Loans.Where(l => l.CompanyName == company.Name).Sum(l => l.Principal);
        int assetPool = Math.Max(0, effectiveLoanLimit - outstandingPrincipal);

        return Math.Min(ca.DepositBalance, Math.Max(0, assetPool));
    }

    // ============================================================================
    // Daily Lifecycle — Private
    // ============================================================================

    private void ResetSeasonalSpawnCounter(BankAccountData account)
    {
        string currentSeason = Game1.currentSeason;
        if (account.SpawnCounterSeason != currentSeason)
        {
            account.DynamicCompaniesSpawnedThisSeason = 0;
            account.SpawnCounterSeason = currentSeason;
        }
    }

    private void CheckAndGenerateNewCompanies(BankAccountData account)
    {
        int activeCount = account.DynamicCompanies.Count(c => c.Status != CompanyStatus.Bankrupt);
        if (activeCount >= MaxDynamicCompanies) return;
        if (account.DynamicCompaniesSpawnedThisSeason >= MaxNewPerSeason) return;

        foreach (var shipment in account.CropShipments)
        {
            if (shipment.CumulativeSellCount < _config.CompanySpawnRequiredSellCount)
                continue;

            if (account.DynamicCompanies.Any(c =>
                c.CropCode == shipment.CropCode && c.Status != CompanyStatus.Bankrupt))
                continue;

            if (CropDataProvider.IsExcluded(shipment.CropCode))
                continue;

            if (IsCropOnBankruptcyCooldown(account, shipment.CropCode))
                continue;

            var cropData = CropDataProvider.GetByCode(shipment.CropCode);
            if (cropData is null) continue;

            GenerateDynamicCompany(account, shipment.CropCode, cropData);

            activeCount++;
            account.DynamicCompaniesSpawnedThisSeason++;
            if (activeCount >= MaxDynamicCompanies) break;
            if (account.DynamicCompaniesSpawnedThisSeason >= MaxNewPerSeason) break;
        }
    }

    private void GenerateDynamicCompany(BankAccountData account, string cropCode, CropDataRecord cropData)
    {
        int today = (int)Game1.stats.DaysPlayed;

        var company = new DynamicCompanyData
        {
            CompanyName = cropData.DisplayName + "公司",
            CropCode = cropCode,
            GenerationDay = today,
            ProtectionEndDay = today + NewCompanyProtectionDays,
            Status = CompanyStatus.New,
            DepositLimit = (int)(cropData.EquivalentCost * _config.DepositLoanCoefficient),
            LoanLimit = (int)(cropData.EquivalentCost * _config.DepositLoanCoefficient),
            FuelStock = (int)(cropData.Smax * 0.3 * 10) // Smax × 0.3 in FP
        };

        account.DynamicCompanies.Add(company);

        _monitor.Log($"New dynamic company: {company.CompanyName} (crop: {cropCode}, R={cropData.R * 100:F2}%)", LogLevel.Info);
        if (_config.ShowCompanyDangerWarning)
            Game1.chatBox?.addInfoMessage($"新公司诞生：{company.CompanyName}！({cropData.DisplayName}累计出售{_config.CompanySpawnRequiredSellCount}个)");
    }

    private bool IsCropOnBankruptcyCooldown(BankAccountData account, string cropCode)
    {
        var bankruptCompany = account.DynamicCompanies.FirstOrDefault(c =>
            c.CropCode == cropCode && c.Status == CompanyStatus.Bankrupt);
        if (bankruptCompany is null || bankruptCompany.BankruptcySeason is null)
            return false;

        // Block only within the same season instance (one appearance per season)
        return Game1.currentSeason.Equals(bankruptCompany.BankruptcySeason, StringComparison.OrdinalIgnoreCase)
            && Game1.year == bankruptCompany.BankruptcyYear;
    }

    private void UpdateCompanyStatuses(BankAccountData account)
    {
        int today = (int)Game1.stats.DaysPlayed;

        foreach (var company in account.DynamicCompanies)
        {
            if (company.Status == CompanyStatus.Bankrupt) continue;

            var cropData = CropDataProvider.GetByCode(company.CropCode);
            double satisfaction = cropData is not null && cropData.DBase > 0
                ? company.FuelStock / (cropData.DBase * 10.0)
                : 1.0;

            if (company.Status == CompanyStatus.New)
            {
                if (today >= company.ProtectionEndDay)
                    company.Status = DeriveStatusFromSatisfaction(satisfaction);
                continue;
            }

            var newStatus = DeriveStatusFromSatisfaction(satisfaction);
            company.Status = newStatus;

            // Fuel-starved company dies immediately
            if (company.FuelStock <= 0 && newStatus == CompanyStatus.Dying)
            {
                company.Status = CompanyStatus.Bankrupt;
                company.BankruptcySeason = Game1.currentSeason;
                company.BankruptcyYear = Game1.year;

                var ca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == company.CompanyName);
                if (ca is not null)
                {
                    ca.DepositBalance = 0;
                    ca.BaseAmount = 0;
                    ca.AccumulatedInterest = 0;
                }

                _monitor.Log($"公司倒闭：{company.CompanyName} 燃料耗尽，已关闭。存款清零。", LogLevel.Warn);
                if (_config.ShowCompanyDangerWarning)
                    Game1.chatBox?.addInfoMessage($"公司倒闭：{company.CompanyName} 燃料耗尽，已关闭。存款清零。");
            }
        }
    }

    private static CompanyStatus DeriveStatusFromSatisfaction(double satisfaction)
    {
        if (satisfaction > 1.50) return CompanyStatus.Prosperous;
        if (satisfaction >= 0.80) return CompanyStatus.Stable;
        if (satisfaction >= 0.30) return CompanyStatus.Hungry;
        return CompanyStatus.Dying;
    }

    // ============================================================================
    // Interest Settlement
    // ============================================================================

    private void SettleDailyInterest(BankAccountData account)
    {
        if (account.CompanyAccounts.Count == 0) return;

        var allCompanyDefs = GetAllCompanyDefinitions(account);
        int totalInterest = 0;

        foreach (var ca in account.CompanyAccounts)
        {
            var company = allCompanyDefs.FirstOrDefault(c => c.Name == ca.CompanyName);
            if (company is null) continue;

            // When principal has been fully withdrawn (or over-withdrawn), the remaining
            // balance becomes new principal — but capped at DepositLimit so interest
            // money can't be "laundered" into excess principal.
            if (ca.BaseAmount <= 0 && ca.DepositBalance > 0)
            {
                ca.AccumulatedInterest = Math.Max(0, ca.DepositBalance - company.DepositLimit);
                ca.BaseAmount = ca.DepositBalance - ca.AccumulatedInterest;
                ca.PreviousBaseAmount = ca.BaseAmount;
            }

            var ctx = new InterestCalculationContext
            {
                Company = company,
                DailyLuck = Game1.player.DailyLuck,
                IsLightning = Game1.isLightning,
                IsRaining = Game1.isRaining,
                IsSnowing = Game1.isSnowing,
                Config = _config,
                ConsecutiveSellDays = GetConsecutiveDays(account, company),
                HasSoldHistory = HasSoldHistory(account, company),
                DecayDays = GetDecayDays(account, company),
                SuppressionStacks = GetSuppressionStacks(account, company),
                IsPenaltyPeriod = ca.PenaltyDaysRemaining > 0
            };

            var calculator = company.IsDynamic ? _dynamicInterestCalculator : _fixedInterestCalculator;
            double rate = calculator.CalculateDepositRate(company, ctx);
            if (rate == 0) continue;

            int interest = (int)(Math.Max(0, ca.BaseAmount) * rate);

            if (interest == 0) continue;

            ca.DepositBalance += interest;
            ca.AccumulatedInterest += interest;
            totalInterest += interest;

            _monitor.Log($"{company.Name}: 利息 {interest} g (有效利率 {rate * 100:F3}%/天)", LogLevel.Info);
        }

        if (totalInterest != 0 && _config.ShowMorningInterestNotification)
        {
            Game1.chatBox?.addInfoMessage($"银行存款利息结算：共 {totalInterest:N0} g");

            foreach (var ca in account.CompanyAccounts)
            {
                var company = allCompanyDefs.FirstOrDefault(c => c.Name == ca.CompanyName);
                if (company is null || ca.DepositBalance <= 0) continue;

                var ctx = new InterestCalculationContext
                {
                    Company = company,
                    DailyLuck = Game1.player.DailyLuck,
                    IsLightning = Game1.isLightning,
                    IsRaining = Game1.isRaining,
                    IsSnowing = Game1.isSnowing,
                    Config = _config,
                    ConsecutiveSellDays = GetConsecutiveDays(account, company),
                    HasSoldHistory = HasSoldHistory(account, company),
                    DecayDays = GetDecayDays(account, company),
                    SuppressionStacks = GetSuppressionStacks(account, company),
                    IsPenaltyPeriod = ca.PenaltyDaysRemaining > 0
                };
                var calculator = company.IsDynamic ? _dynamicInterestCalculator : _fixedInterestCalculator;
                double effectiveRate = calculator.CalculateDepositRate(company, ctx);
                Game1.chatBox?.addInfoMessage($"  {company.Name} 有效利率 {effectiveRate * 100:F2}%/天（存款 {ca.DepositBalance:N0}g）");
            }
        }
    }

    // ============================================================================
    // Stage 7: Suppression & Consecutive Days
    // ============================================================================

    // ============================================================================
    // V3.7: Manual Compound Interest Detection
    // ============================================================================

    /// <summary>
    /// Detects manual compound interest patterns (player withdrawing interest and
    /// re-depositing as principal to create exponential growth). Uses a 7-day sliding
    /// window with peak-break detection: if the principal delta breaks its peak ≥3 times
    /// within the window, a 7-day penalty is applied (deposit rate × 10%).
    /// </summary>
    private void DetectManualCompoundInterest(BankAccountData account)
    {
        foreach (var ca in account.CompanyAccounts)
        {
            // 1. Decrement penalty days
            if (ca.PenaltyDaysRemaining > 0)
                ca.PenaltyDaysRemaining--;

            // 2. Compute today's principal delta
            int delta = ca.BaseAmount - ca.PreviousBaseAmount;
            ca.PreviousBaseAmount = ca.BaseAmount;

            // 3. Write to sliding window (only positives count; zeros/negatives preserve position)
            if (delta > 0)
                ca.RecentPrincipalDeltas[ca.WindowDayIndex] = delta;
            else
                ca.RecentPrincipalDeltas[ca.WindowDayIndex] = 0;

            // 4. Scan window from oldest to newest for peak-break pattern
            int peak = 0;
            int breakCount = 0;
            int lastPos = 0;
            bool hasFirst = false;

            int oldest = (ca.WindowDayIndex + 1) % 7;
            for (int i = 0; i < 7; i++)
            {
                int idx = (oldest + i) % 7;
                int d = ca.RecentPrincipalDeltas[idx];
                if (d <= 0) continue;

                if (!hasFirst)
                {
                    if (lastPos > 0 && d > lastPos)
                    {
                        hasFirst = true;
                        peak = d;
                        breakCount = 1;
                    }
                    lastPos = d;
                }
                else
                {
                    if (d > peak)
                    {
                        peak = d;
                        breakCount++;
                    }
                    lastPos = d;
                }
            }

            ca.HasFirstIncrease = hasFirst;
            ca.LastPositiveDelta = lastPos;
            ca.WindowPeak = peak;
            ca.WindowBreakCount = breakCount;

            // 5. Trigger penalty
            if (breakCount >= 3 && ca.PenaltyDaysRemaining <= 0)
            {
                ca.PenaltyDaysRemaining = 7;
                _monitor.Log(
                    $"[V3.7] Manual compound interest detected for {ca.CompanyName}! " +
                    $"Peak breaks: {breakCount}, deposit rate reduced to 10% for 7 days.",
                    LogLevel.Warn);
                if (_config.ShowCompanyDangerWarning)
                    Game1.chatBox?.addInfoMessage(
                        $"银行警告：检测到 {ca.CompanyName} 存在手动复利行为！利率已降至10%，持续7天。");
            }

            // 6. Advance window; reset state on wrap
            ca.WindowDayIndex = (ca.WindowDayIndex + 1) % 7;
            if (ca.WindowDayIndex == 0)
            {
                ca.HasFirstIncrease = false;
                ca.LastPositiveDelta = 0;
                ca.WindowPeak = 0;
                ca.WindowBreakCount = 0;
            }
        }
    }

    // ============================================================================
    // Stage 7: Suppression & Consecutive Days Helpers
    // ============================================================================

    public void ApplySuppression(BankAccountData account, string cropCode, int quantity)
    {
        int stacksToAdd = quantity >= 50 ? 3 : quantity >= 20 ? 2 : 1;

        var existing = account.CropSuppressions.FirstOrDefault(s => s.CropCode == cropCode);
        if (existing is null)
        {
            existing = new CropSuppressionData { CropCode = cropCode };
            account.CropSuppressions.Add(existing);
        }

        existing.Stacks = Math.Min(existing.Stacks + stacksToAdd, _config.SuppressMaxStacks);
        existing.RemainingDays = _config.SuppressEffectDurationDays;
    }

    private void TickSuppression(BankAccountData account)
    {
        for (int i = account.CropSuppressions.Count - 1; i >= 0; i--)
        {
            var s = account.CropSuppressions[i];
            s.RemainingDays--;
            if (s.RemainingDays <= 0)
            {
                s.Stacks--;
                if (s.Stacks <= 0)
                    account.CropSuppressions.RemoveAt(i);
                else
                    s.RemainingDays = _config.SuppressEffectDurationDays;
            }
        }
    }

    private int GetConsecutiveDays(BankAccountData account, CompanyDefinition company)
    {
        var tracking = account.CropShipments.FirstOrDefault(
            s => s.CropCode == (company.CropCode ?? company.Name));
        return tracking?.ConsecutiveSellDays ?? 0;
    }

    private bool HasSoldHistory(BankAccountData account, CompanyDefinition company)
    {
        var tracking = account.CropShipments.FirstOrDefault(
            s => s.CropCode == (company.CropCode ?? company.Name));
        return tracking is not null && tracking.CumulativeSellCount > 0;
    }

    private int GetDecayDays(BankAccountData account, CompanyDefinition company)
    {
        var tracking = account.CropShipments.FirstOrDefault(
            s => s.CropCode == (company.CropCode ?? company.Name));
        return tracking?.DecayDays ?? 0;
    }

    private int GetSuppressionStacks(BankAccountData account, CompanyDefinition company)
    {
        var suppression = account.CropSuppressions.FirstOrDefault(
            s => s.CropCode == (company.CropCode ?? company.Name));
        return suppression?.Stacks ?? 0;
    }
}
