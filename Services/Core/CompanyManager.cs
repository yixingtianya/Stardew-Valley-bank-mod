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
    private readonly IBankruptcyHandler _bankruptcyHandler;
    private readonly ShipmentTrackingService _shipmentTracking;
    private readonly IFuelService _fuelService;
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;

    // Stage 14 route effects
    public bool PierreSuppressionDisabled { get; set; }
    public bool JojaSuppressionBoosted { get; set; }
    public bool PierreSuppressionBoosted { get; set; }

    private const int MaxDynamicCompanies = 4;
    private const int MaxNewPerSeason = 2;
    private const int NewCompanyProtectionDays = 3;
    public CompanyManager(
        IInterestCalculator fixedInterestCalculator,
        IInterestCalculator dynamicInterestCalculator,
        IBankAccountService accountService,
        ILoanService loanService,
        IBankruptcyHandler bankruptcyHandler,
        ShipmentTrackingService shipmentTracking,
        IFuelService fuelService,
        ModConfig config,
        IMonitor monitor)
    {
        _fixedInterestCalculator = fixedInterestCalculator;
        _dynamicInterestCalculator = dynamicInterestCalculator;
        _accountService = accountService;
        _loanService = loanService;
        _bankruptcyHandler = bankruptcyHandler;
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

        // Step 2.5: Season transition — start or progress the 3-day watch period
        ProcessSeasonTransition(account);

        // Step 3: Check for new dynamic company generation
        CheckAndGenerateNewCompanies(account);

        // Step 3.5: Tick suppression countdowns (Stage 7.3)
        TickSuppression(account);

        // Step 4: Update fuel inventory on the shared account → derive company statuses
        // Stage 9: restore fuel for restructuring companies (fuel doesn't drain during restructuring)
        var restructuringCompanies = account.DynamicCompanies
            .Where(c => c.RestructuringDaysRemaining > 0).ToList();
        var fuelSnapshots = restructuringCompanies
            .ToDictionary(c => c.CompanyName, c => c.FuelStock);

        _fuelService.UpdateDailyInventory(account);

        // Restore fuel for restructuring companies
        foreach (var kvp in fuelSnapshots)
        {
            var rc = account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == kvp.Key);
            if (rc is not null) rc.FuelStock = kvp.Value;
        }

        UpdateCompanyStatuses(account);
        ProgressRestructuring(account);

        // Re-check company generation: bankrupt companies may have freed spawn slots
        CheckAndGenerateNewCompanies(account);

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

        // Step 6: Settle daily loans (Stage 8: interest deduction, debt tracking, bankruptcy on forced-collection failure)
        _loanService.DailyTick(account, _config, _fixedInterestCalculator);

        // Step 7: Show first-time bankruptcy warning
        if (account.IsInBankruptcy && !account.BankruptcyWarningShown)
        {
            account.BankruptcyWarningShown = true;
            Game1.chatBox?.addInfoMessage("⚠ 你已进入破产保护状态！逾期贷款已被冻结（0利息），借款暂停，每日出货收入50%强制偿债。");
            _monitor.Log("[Bankruptcy] Player entered bankruptcy protection", LogLevel.Warn);
        }
        else if (account.IsInPrincipalDebt && !account.IsInBankruptcy)
        {
            Game1.chatBox?.addInfoMessage("⚠ 你有贷款已逾期！宽限期结束后将从存款强制划扣，请尽快还款。");
        }
        else if (account.IsInInterestDebt && !account.IsInBankruptcy && !account.IsInPrincipalDebt)
        {
            Game1.chatBox?.addInfoMessage("⚠ 现金不足以支付贷款日息，已进入利息欠债状态。");
        }

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
            double depRate = r > 0.20 ? r * 0.5 : r > 0 ? r : 0;
            double loanRate = r > 0.20 ? r * 0.75 : r > 0 ? r * 1.5 : 0;

            // Stage 9: Apply status multipliers (value2.txt §3.1)
            double rateMultiplier = dc.Status switch
            {
                CompanyStatus.Prosperous => 1.2,
                CompanyStatus.Stable => 0.9,
                CompanyStatus.Hungry => 0.7,
                CompanyStatus.Dying => 0.4,
                _ => 1.0
            };
            double loanLimitMultiplier = dc.Status switch
            {
                CompanyStatus.Prosperous => _config.ProsperousLoanLimitMult / 10.0,
                CompanyStatus.Stable => _config.StableLoanLimitMult / 10.0,
                CompanyStatus.Hungry => _config.HungryLoanLimitMult / 10.0,
                CompanyStatus.Dying => _config.DyingLoanLimitMult / 10.0,
                _ => 1.0
            };

            result.Add(new CompanyDefinition
            {
                Name = dc.CompanyName,
                CropCode = dc.CropCode,
                IsDynamic = true,
                DepositInterestRate = depRate * rateMultiplier,
                LoanInterestRate = loanRate * rateMultiplier,
                DepositLimit = dc.DepositLimit,
                LoanLimit = (int)(dc.DepositLimit * loanLimitMultiplier),
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

        if (!company.IsDynamic)
            return ca.DepositBalance;

        var dyn = account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == company.Name);
        if (dyn is null) return ca.DepositBalance;

        // Asset pool restriction only during Hungry/Dying (value2.txt §5.1)
        bool restricted = dyn.Status == CompanyStatus.Hungry || dyn.Status == CompanyStatus.Dying;
        if (!restricted)
            return ca.DepositBalance;

        // company.LoanLimit already has the status multiplier applied in GetAllCompanyDefinitions
        int outstandingPrincipal = account.Loans.Where(l => l.CompanyName == company.Name).Sum(l => l.Principal);
        int assetPool = Math.Max(0, company.LoanLimit - outstandingPrincipal);
        int remainingPool = Math.Max(0, assetPool - dyn.AssetPoolConsumed);
        return Math.Min(ca.DepositBalance, remainingPool);
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
            // Remove bankrupt companies — they become eligible for revival next season
            account.DynamicCompanies.RemoveAll(c => c.Status == CompanyStatus.Bankrupt);
            // Reset all crop shipment counters so companies don't immediately respawn
            foreach (var ship in account.CropShipments)
            {
                ship.CumulativeSellCount = 0;
                ship.ConsecutiveSellDays = 0;
                ship.LastSellDay = 0;
            }
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
                c.CropCode == shipment.CropCode))
                continue; // same crop already has a company (active or bankrupt) — wait until next season

            if (CropDataProvider.IsExcluded(shipment.CropCode))
                continue;

            if (IsCropOnBankruptcyCooldown(account, shipment.CropCode))
                continue;

            var cropData = CropDataProvider.GetByCode(shipment.CropCode);
            if (cropData is null) continue;

            GenerateDynamicCompany(account, shipment.CropCode, cropData, shipment.CumulativeSellCount);

            activeCount++;
            account.DynamicCompaniesSpawnedThisSeason++;
            if (activeCount >= MaxDynamicCompanies) break;
            if (account.DynamicCompaniesSpawnedThisSeason >= MaxNewPerSeason) break;
        }
    }

    private void GenerateDynamicCompany(BankAccountData account, string cropCode, CropDataRecord cropData, int cumulativeSells)
    {
        int today = (int)Game1.stats.DaysPlayed;

        // Initial fuel: 30% of Smax + all accumulated crop sells converted to FP
        int baseFP = (int)(cropData.Smax * 0.3 * 10);
        int sellBonusFP = cumulativeSells * 10;
        int maxFP = (int)(cropData.Smax * 2 * 10); // cap at 200% of Smax
        int initialFuel = Math.Min(baseFP + sellBonusFP, maxFP);

        var company = new DynamicCompanyData
        {
            CompanyName = cropData.DisplayName + "公司",
            CropCode = cropCode,
            GenerationDay = today,
            ProtectionEndDay = today + NewCompanyProtectionDays,
            Status = CompanyStatus.New,
            DepositLimit = (int)(cropData.EquivalentCost * _config.DepositLoanCoefficient),
            LoanLimit = (int)(cropData.EquivalentCost * _config.DepositLoanCoefficient),
            FuelStock = initialFuel
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

            // Stage 9: restructuring companies — skip fuel drain, check revival
            if (company.RestructuringDaysRemaining > 0)
            {
                var rcData = CropDataProvider.GetByCode(company.CropCode);
                double rSatisfaction = rcData is not null && rcData.Smax > 0
                    ? company.FuelStock / (rcData.Smax * 10.0)
                    : 1.0;

                // Revival: fuel ≥ 80% (exit Hungry → Stable)
                if (rSatisfaction >= 0.80)
                {
                    if (company.PendingRescueDeposit > 0)
                    {
                        var rca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == company.CompanyName);
                        if (rca is not null)
                        {
                            rca.BaseAmount += company.PendingRescueDeposit * 2;
                            rca.DepositBalance += company.PendingRescueDeposit * 2;
                            Game1.chatBox?.addInfoMessage(
                                $"{company.CompanyName} 复活！入股注资翻倍：{company.PendingRescueDeposit * 2:N0} g 已计入存款本金。");
                        }
                        company.PendingRescueDeposit = 0;
                    }
                    company.RestructuringDaysRemaining = 0;
                    company.TotalRescueSharesPurchased = 0;
                    company.Status = CompanyStatus.Stable;
                    company.AssetPoolConsumed = 0;
                    _monitor.Log($"[Stage9] {company.CompanyName} revived (fuel satisfaction {rSatisfaction*100:F0}%)", LogLevel.Info);
                }
                continue;
            }

            // Season transition: skip normal status updates during the 3-day watch
            if (company.SeasonTransitionDays > 0) continue;

            var cropData = CropDataProvider.GetByCode(company.CropCode);
            double satisfaction = cropData is not null && cropData.Smax > 0
                ? company.FuelStock / (cropData.Smax * 10.0)
                : 1.0;

            if (company.Status == CompanyStatus.New)
            {
                if (today >= company.ProtectionEndDay)
                    company.Status = DeriveStatusFromSatisfaction(satisfaction);
                continue;
            }

            var newStatus = DeriveStatusFromSatisfaction(satisfaction);

            // Reset asset pool consumption when leaving Hungry/Dying
            bool wasRestricted = company.Status == CompanyStatus.Hungry || company.Status == CompanyStatus.Dying;
            bool stillRestricted = newStatus == CompanyStatus.Hungry || newStatus == CompanyStatus.Dying;
            if (wasRestricted && !stillRestricted)
                company.AssetPoolConsumed = 0;

            // Enter restructuring when first hitting Dying
            if (newStatus == CompanyStatus.Dying && company.Status != CompanyStatus.Dying)
            {
                company.Status = CompanyStatus.Dying;
                company.RestructuringDaysRemaining = 0;
                TransferLoansFromDyingCompany(account, company);
                Game1.chatBox?.addInfoMessage(
                    $"⚠ {company.CompanyName} 进入濒死重组期！请入股救市购买重组天数（每百股 {company.LoanLimit/7:N0} g），上限7天。");
                _monitor.Log($"[Stage9] {company.CompanyName} entered Dying restructuring", LogLevel.Warn);
                continue;
            }

            company.Status = newStatus;

            // Fuel-starved but NOT in restructuring → immediate death
            if (company.FuelStock <= 0 && newStatus == CompanyStatus.Dying && company.RestructuringDaysRemaining <= 0)
            {
                TransferLoansFromDyingCompany(account, company);
                LiquidateCompany(account, company);
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

            // Rebalance principal/interest when:
            // 1. Principal withdrawn past zero (BaseAmount ≤ 0), or
            // 2. Total deposit ≤ accumulated interest (AccInt has consumed the balance)
            // AccInt = max(0, DepositBalance - DepositLimit); excess→interest, rest→principal
            if (ca.DepositBalance > 0 &&
                (ca.BaseAmount <= 0 || ca.DepositBalance <= ca.AccumulatedInterest))
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
        int effectiveMaxStacks = (PierreSuppressionBoosted || JojaSuppressionBoosted) ? _config.SuppressMaxStacks * 2 : _config.SuppressMaxStacks;

        var existing = account.CropSuppressions.FirstOrDefault(s => s.CropCode == cropCode);
        if (existing is null)
        {
            existing = new CropSuppressionData { CropCode = cropCode };
            account.CropSuppressions.Add(existing);
        }

        existing.Stacks = Math.Min(existing.Stacks + stacksToAdd, effectiveMaxStacks);
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

    /// <summary>Decrement restructuring days; liquidate if expired without revival.</summary>
    private void ProgressRestructuring(BankAccountData account)
    {
        foreach (var company in account.DynamicCompanies)
        {
            if (company.RestructuringDaysRemaining <= 0) continue;
            if (company.Status == CompanyStatus.Bankrupt) continue;

            company.RestructuringDaysRemaining--;

            if (company.RestructuringDaysRemaining <= 0)
            {
                // Restructuring expired → liquidate
                TransferLoansFromDyingCompany(account, company);
                LiquidateCompany(account, company);
            }
        }
    }

    // ============================================================================
    // Season Transition (TV.txt 季节过渡机制)
    // ============================================================================

    private void ProcessSeasonTransition(BankAccountData account)
    {
        int today = (int)Game1.stats.DaysPlayed;
        int dayOfMonth = Game1.dayOfMonth;
        bool isFirstDay = dayOfMonth == 1;

        // Day 1: start transition for all active dynamic companies (skip new/protected)
        if (isFirstDay)
        {
            foreach (var dc in account.DynamicCompanies)
            {
                if (dc.Status == CompanyStatus.Bankrupt) continue;
                if (dc.Status == CompanyStatus.New) continue;
                dc.SeasonTransitionDays = 3;
            }
            return;
        }

        // Day 2-3: just count down, skip in UpdateCompanyStatuses
        if (dayOfMonth <= 3)
        {
            foreach (var dc in account.DynamicCompanies)
                if (dc.SeasonTransitionDays > 0) dc.SeasonTransitionDays--;
            return;
        }

        // Day 4: process outcomes
        if (dayOfMonth != 4) return;

        var toProcess = account.DynamicCompanies
            .Where(c => c.SeasonTransitionDays <= 0 && c.Status != CompanyStatus.Bankrupt && c.SeasonTransitionDays == 0)
            .ToList();

        // Actually, we need the ones that were IN transition (had SeasonTransitionDays > 0 at day 3)
        // Let me just check all non-bankrupt companies
        var transitioning = account.DynamicCompanies
            .Where(c => c.Status != CompanyStatus.Bankrupt)
            .ToList();

        foreach (var dc in transitioning)
        {
            // If still in rescue restructuring, evaluate by fuel status first
            if (dc.RestructuringDaysRemaining > 0)
            {
                var rcData = CropDataProvider.GetByCode(dc.CropCode);
                if (rcData is not null && rcData.Smax > 0)
                {
                    double sat = dc.FuelStock / (rcData.Smax * 10.0);
                    if (sat >= 0.80) dc.Status = CompanyStatus.Stable;
                    else if (sat >= 0.30) dc.Status = CompanyStatus.Hungry;
                    else dc.Status = CompanyStatus.Dying;
                }
            }

            dc.SeasonTransitionDays = 0;

            // Outcome 1: 光荣退休 (Prosperous, low shipments, 20%)
            if (dc.Status == CompanyStatus.Prosperous)
            {
                var shipment = account.CropShipments.FirstOrDefault(s => s.CropCode == dc.CropCode);
                int recentSells = shipment?.CumulativeSellCount ?? 0;
                if (recentSells < _config.CompanySpawnRequiredSellCount && _rng.Next(100) < 20)
                {
                    // Return deposits
                    var ca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == dc.CompanyName);
                    if (ca is not null && ca.BaseAmount > 0)
                    {
                        Game1.player.Money += ca.BaseAmount;
                        Game1.chatBox?.addInfoMessage($"{dc.CompanyName} 光荣退市！存款 {ca.BaseAmount:N0} g 已退还。");
                    }
                    TransferLoansFromDyingCompany(account, dc);
                    LiquidateCompany(account, dc);
                    _monitor.Log($"[Season] {dc.CompanyName} retired honorably", LogLevel.Info);
                    continue;
                }
            }

            // Outcome 2: 成功转型 (Prosperous/Stable, another crop has fuel > threshold, slots full, 50%)
            if (dc.Status == CompanyStatus.Prosperous || dc.Status == CompanyStatus.Stable)
            {
                var candidates = account.CropShipments
                    .Where(s => s.CumulativeSellCount >= _config.CompanySpawnRequiredSellCount
                        && !account.DynamicCompanies.Any(c => c.CropCode == s.CropCode && c.Status != CompanyStatus.Bankrupt))
                    .ToList();
                int activeCount = account.DynamicCompanies.Count(c => c.Status != CompanyStatus.Bankrupt);
                if (candidates.Count > 0 && activeCount >= MaxDynamicCompanies && _rng.Next(100) < 50)
                {
                    var target = candidates[_rng.Next(candidates.Count)];
                    string oldName = dc.CompanyName;
                    var newCrop = CropDataProvider.GetByCode(target.CropCode);
                    string newName = (newCrop?.DisplayName ?? target.CropCode) + "公司";
                    dc.CropCode = target.CropCode;
                    dc.CompanyName = newName;
                    dc.Status = CompanyStatus.New;
                    dc.ProtectionEndDay = (int)Game1.stats.DaysPlayed + NewCompanyProtectionDays;
                    dc.FuelStock = (int)((newCrop?.Smax ?? 0) * 0.3 * 10);
                    dc.PendingRescueDeposit = 0;
                    dc.RestructuringDaysRemaining = 0;
                    dc.AssetPoolConsumed = 0;
                    Game1.chatBox?.addInfoMessage($"{oldName} 成功转型为 {newName}！存款和贷款已保留。");
                    _monitor.Log($"[Season] {oldName} transformed to {newName}", LogLevel.Info);
                    continue;
                }
            }

            // Outcome 3: 倒闭撤退 (Hungry 60%, Dying 80%)
            if (dc.Status == CompanyStatus.Hungry && _rng.Next(100) < 60
                || dc.Status == CompanyStatus.Dying && _rng.Next(100) < 80)
            {
                Game1.chatBox?.addInfoMessage($"{dc.CompanyName} 在季节交替中倒闭撤退。");
                TransferLoansFromDyingCompany(account, dc);
                LiquidateCompany(account, dc);
                _monitor.Log($"[Season] {dc.CompanyName} closed in transition", LogLevel.Info);
                continue;
            }

            // Outcome 4: 硬撑存续
            _monitor.Log($"[Season] {dc.CompanyName} survived season transition", LogLevel.Debug);
        }
    }

    private static readonly Random _rng = new();

    // ============================================================================
    // Stage 9: Debt Transfer, Tiered Liquidation, Rescue Investment
    // ============================================================================

    /// <summary>Transfer all loans from the dying company to the fixed company with the highest current loan rate.</summary>
    private void TransferLoansFromDyingCompany(BankAccountData account, DynamicCompanyData company)
    {
        var companyLoans = account.Loans.Where(l => l.CompanyName == company.CompanyName && !l.IsFrozen).ToList();
        if (companyLoans.Count == 0) return;

        var bestFixed = _config.Companies
            .OrderByDescending(c => c.LoanInterestRate)
            .First();

        int today = (int)Game1.stats.DaysPlayed;
        double transferRate = bestFixed.LoanInterestRate * _config.DebtTransferRateDiscount;

        foreach (var loan in companyLoans)
        {
            loan.CompanyName = bestFixed.Name;
            loan.InterestRate = transferRate;
            loan.RepaymentPeriodDays = _config.DebtTransferNewRepaymentDays;
            loan.DueDay = today + _config.DebtTransferNewRepaymentDays;
            loan.IsInDefault = false;
            loan.DefaultDaysRemaining = 0;
            loan.IsTransferred = true;
        }

        Game1.chatBox?.addInfoMessage($"债券转移：{company.CompanyName} 的 {companyLoans.Count} 笔贷款已转移至 {bestFixed.Name}（利率 {transferRate*100:F2}%/天）。");
        _monitor.Log($"[Stage9] {companyLoans.Count} loans transferred from {company.CompanyName} to {bestFixed.Name}", LogLevel.Info);
    }

    /// <summary>Formal liquidation: return deposit by status tier, clear company, reset shipment counter.</summary>
    private void LiquidateCompany(BankAccountData account, DynamicCompanyData company)
    {
        double returnRate = company.Status switch
        {
            CompanyStatus.Prosperous => _config.ProsperousReturnRate,
            CompanyStatus.Stable => _config.StableReturnRate,
            CompanyStatus.Hungry => _config.HungryReturnRate,
            CompanyStatus.Dying => _config.DyingReturnRate,
            _ => 0
        };

        if (company.Status == CompanyStatus.Stable)
            returnRate = Math.Min(returnRate, _config.ProsperousReturnRate);

        var ca = account.CompanyAccounts.FirstOrDefault(a => a.CompanyName == company.CompanyName);
        int returnAmount = ca is not null ? (int)(ca.BaseAmount * returnRate) : 0;

        int rescueLost = company.PendingRescueDeposit;
        company.PendingRescueDeposit = 0;
        company.RestructuringDaysRemaining = 0;
        company.TotalRescueSharesPurchased = 0;

        if (returnAmount > 0)
            Game1.player.Money += returnAmount;

        string msg = $"公司倒闭：{company.CompanyName} 燃料耗尽";
        if (returnAmount > 0)
            msg += $"，返还存款 {returnAmount:N0} g（{returnRate*100:F0}%）";
        else
            msg += "，存款归零";
        if (rescueLost > 0)
            msg += $"，入股注资 {rescueLost:N0} g 已清零";
        Game1.chatBox?.addInfoMessage(msg);

        if (ca is not null)
        {
            ca.DepositBalance = 0;
            ca.BaseAmount = 0;
            ca.AccumulatedInterest = 0;
        }

        company.Status = CompanyStatus.Bankrupt;
        company.BankruptcySeason = Game1.currentSeason;
        company.BankruptcyYear = Game1.year;

        // Free up a spawn slot so another company can replace this one
        if (account.DynamicCompaniesSpawnedThisSeason > 0)
            account.DynamicCompaniesSpawnedThisSeason--;

        var shipment = account.CropShipments.FirstOrDefault(s => s.CropCode == company.CropCode);
        if (shipment is not null)
        {
            shipment.CumulativeSellCount = 0;
            shipment.ConsecutiveSellDays = 0;
            shipment.LastSellDay = 0;
        }

        _monitor.Log($"[Stage9] {company.CompanyName} liquidated, returned {returnAmount}g ({returnRate*100:F0}%)", LogLevel.Warn);
    }

    public bool RescueInvest(BankAccountData account, string companyName, int amount)
    {
        if (amount <= 0) return false;

        var company = account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == companyName);
        if (company is null) return false;
        if (company.Status != CompanyStatus.Dying && company.RestructuringDaysRemaining <= 0) return false;

        // 每百股价格 = 有效贷款上限 / 7（应用状态乘数后）
        double loanLimitMult = company.Status switch
        {
            CompanyStatus.Dying => _config.DyingLoanLimitMult / 10.0,
            _ => 1.0
        };
        int effectiveLoanLimit = (int)(company.LoanLimit * loanLimitMult);
        int sharePrice = effectiveLoanLimit / 7;
        if (sharePrice <= 0) return false;

        // amount buys N 百股 → N restructuring days, capped at 7 total
        int newDays = amount / sharePrice;
        if (newDays <= 0) return false;

        int newTotalDays = Math.Min(7, company.RestructuringDaysRemaining + newDays);
        int actualDaysAdded = newTotalDays - company.RestructuringDaysRemaining;
        if (actualDaysAdded <= 0) return false;

        int actualCost = actualDaysAdded * sharePrice;
        if (Game1.player.Money < actualCost) return false;

        Game1.player.Money -= actualCost;
        company.RestructuringDaysRemaining = newTotalDays;
        company.TotalRescueSharesPurchased += actualDaysAdded;
        company.PendingRescueDeposit += actualCost;

        // Start restructuring if not already
        if (company.Status != CompanyStatus.Dying && company.RestructuringDaysRemaining > 0)
        {
            // Company was about to die but got saved by restructuring
        }

        Game1.chatBox?.addInfoMessage(
            $"入股救市：向 {companyName} 购买 {actualDaysAdded} 百股（{actualCost:N0} g），" +
            $"重组期延长至 {newTotalDays} 天。复活后注资翻倍计入存款。");
        return true;
    }
}
