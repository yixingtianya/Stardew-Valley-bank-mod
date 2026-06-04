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

    // Set by ModServices after construction to resolve localized display names
    internal Func<CompanyDefinition, string>? DisplayNameResolver { get; set; }

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

    /// <summary>Resolve display name for a CompanyDefinition — always returns Chinese for dynamic companies.</summary>
    private string ResolveDisplayName(CompanyDefinition def)
    {
        if (def.IsDynamic && !string.IsNullOrEmpty(def.CropCode))
        {
            var cropData = CropDataProvider.GetByCode(def.CropCode);
            if (cropData != null)
                return cropData.DisplayName + I18n.Get("cmp.6");
        }
        return DisplayNameResolver?.Invoke(def) ?? def.Name;
    }

    /// <summary>Resolve display name for a DynamicCompanyData by crop code.</summary>
    private string ResolveDynamicDisplayName(DynamicCompanyData dc)
    {
        var cropData = CropDataProvider.GetByCode(dc.CropCode);
        return cropData != null ? cropData.DisplayName + I18n.Get("cmp.6") : dc.CompanyName;
    }

    /// <summary>Find the CompanyDefinition matching a company account name and resolve its display name.</summary>
    private string ResolveAccountDisplayName(string companyName, IEnumerable<CompanyDefinition> allDefs)
    {
        var def = allDefs.FirstOrDefault(c => c.Name == companyName || c.OriginalName == companyName);
        return def != null ? ResolveDisplayName(def) : companyName;
    }

    // ============================================================================
    // ICompanyManager — Public API
    // ============================================================================

    public void OnDayStarted()
    {
        var account = _accountService.Load();

        // Step 0: Rotate pre-generated values (tomorrow → today, generate new tomorrow)
        GenerateTodayValues(account);

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

        // Stage 15: Snapshot compound state BEFORE countdown.
        // The countdown is moved to AFTER DetectManualCompoundInterest so that:
        //   1. The last day of compound interest still has isCompound=true in SettleDailyInterest
        //   2. The `continue` guard in DetectManualCompoundInterest still works on the last day
        // Previously the countdown ran before interest settlement, causing CompoundActiveCompany
        // to be cleared prematurely, and the anti-compound `continue` guard to fail on expiry day.
        bool compoundExpiringToday = account.CompoundDaysRemaining == 1
            && !string.IsNullOrEmpty(account.CompoundActiveCompany);
        string? expiringCompanyName = compoundExpiringToday ? account.CompoundActiveCompany : null;

        // Stage 15: FBN true/false event detection
        TryTriggerFbnEvent(account);

        // Reset temporary bankruptcy risk boost from previous day
        account.FbnTempBoostCompany = "";

        // Daily status summary
        if (account.DynamicCompanies.Count > 0)
        {
            foreach (var dc in account.DynamicCompanies)
            {
                double displayFuel = dc.FuelStock / 10.0;
                var cropData = CropDataProvider.GetByCode(dc.CropCode);
                string displayName = cropData != null ? cropData.DisplayName + I18n.Get("cmp.6") : dc.CompanyName;
                _monitor.Log(
                    I18n.Get("cmp.2", new { name = displayName, status = dc.Status.ToString(), fuel = displayFuel.ToString("F1"), units = dc.FuelStock }),
                    LogLevel.Info);
            }
        }

        // Step 5: Settle daily interest (fixed + dynamic)
        SettleDailyInterest(account);

        // Step 5.5: V3.7 manual compound interest detection
        DetectManualCompoundInterest(account);

        // Stage 15: compound interest daily countdown — AFTER DetectManualCompoundInterest
        // so that the `continue` guard is still effective on the last day of compound interest.
        if (account.CompoundDaysRemaining > 0)
        {
            account.CompoundDaysRemaining--;
            if (account.CompoundDaysRemaining <= 0)
            {
                // Reset anti-compound sliding window for the expiring company to prevent
                // false positives from accumulated PreviousBaseAmount drift during compound period
                if (!string.IsNullOrEmpty(expiringCompanyName))
                {
                    var expiringCa = account.CompanyAccounts.FirstOrDefault(ca => ca.CompanyName == expiringCompanyName);
                    if (expiringCa != null)
                    {
                        expiringCa.RecentPrincipalDeltas = new int[7];
                        expiringCa.WindowDayIndex = 0;
                        expiringCa.HasFirstIncrease = false;
                        expiringCa.LastPositiveDelta = 0;
                        expiringCa.WindowPeak = 0;
                        expiringCa.WindowBreakCount = 0;
                        expiringCa.PreviousBaseAmount = expiringCa.BaseAmount;
                        expiringCa.PreviousAccumulatedInterest = expiringCa.AccumulatedInterest;
                        expiringCa.AccIntDecreasedInWindow = false;
                        _monitor.Log($"[Compound] Reset anti-compound sliding window for {expiringCompanyName}", LogLevel.Info);
                    }
                }
                account.CompoundActiveCompany = "";
                account.CompoundCooldownDays = 112;
                Game1.chatBox?.addInfoMessage(I18n.Get("cmp.1"));
                _monitor.Log("[Compound] Period ended, 112-day cooldown started", LogLevel.Info);
            }
        }
        if (account.CompoundCooldownDays > 0)
            account.CompoundCooldownDays--;

        // Step 6: Settle daily loans (Stage 8: interest deduction, debt tracking, bankruptcy on forced-collection failure)
        _loanService.DailyTick(account, _config, _fixedInterestCalculator);

        // Step 7: Show first-time bankruptcy warning
        if (account.IsInBankruptcy && !account.BankruptcyWarningShown)
        {
            account.BankruptcyWarningShown = true;
            Game1.chatBox?.addInfoMessage(I18n.Get("cmp.3"));
            _monitor.Log("[Bankruptcy] Player entered bankruptcy protection", LogLevel.Warn);
        }
        else if (account.IsInPrincipalDebt && !account.IsInBankruptcy)
        {
            Game1.chatBox?.addInfoMessage(I18n.Get("cmp.4"));
        }
        else if (account.IsInInterestDebt && !account.IsInBankruptcy && !account.IsInPrincipalDebt)
        {
            Game1.chatBox?.addInfoMessage(I18n.Get("cmp.5"));
        }

        // Step 8: Pre-generate tomorrow's luck and random values for FBN forecast accuracy
        // (called from DayEnding in BankMod.cs)

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
                OriginalName = dc.CompanyName,
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
        var ca = account.CompanyAccounts.FirstOrDefault(a =>
            a.CompanyName == company.Name || a.CompanyName == company.OriginalName);
        if (ca is null) return 0;

        if (!company.IsDynamic)
            return ca.DepositBalance;

        var dyn = account.DynamicCompanies.FirstOrDefault(c =>
            c.CompanyName == company.Name || c.CompanyName == company.OriginalName);
        if (dyn is null) return ca.DepositBalance;

        // Asset pool restriction only during Hungry/Dying (value2.txt §5.1)
        bool restricted = dyn.Status == CompanyStatus.Hungry || dyn.Status == CompanyStatus.Dying;
        if (!restricted)
            return ca.DepositBalance;

        // company.LoanLimit already has the status multiplier applied in GetAllCompanyDefinitions
        int outstandingPrincipal = account.Loans.Where(l =>
            l.CompanyName == company.Name || l.CompanyName == company.OriginalName).Sum(l => l.Principal);
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
            CompanyName = cropCode,
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
            Game1.chatBox?.addInfoMessage(I18n.Get("cmp.7", new { count = cumulativeSells }));
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
                                I18n.Get("cmp.8", new { amount = company.PendingRescueDeposit * 2 }));
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
                    I18n.Get("cmp.9", new { daysPerShare = 1, maxDays = 7 }));
                _monitor.Log($"[Stage9] {company.CompanyName} entered Dying restructuring", LogLevel.Warn);
                continue;
            }

            company.Status = newStatus;

            // Stage 15: daily natural bankruptcy check (value2.txt risk table)
            if (!_config.DisableNaturalBankruptcy && company.RestructuringDaysRemaining <= 0
                && company.Status != CompanyStatus.Protection && company.Status != CompanyStatus.New)
            {
                double dailyProb = company.Status switch
                {
                    CompanyStatus.Prosperous => 1.0 - Math.Pow(1.0 - _config.ProsperousAnnualRisk, 1.0 / 112),
                    CompanyStatus.Stable => (1.0 - Math.Pow(1.0 - _config.ProsperousAnnualRisk, 1.0 / 112)) * 0.3,
                    CompanyStatus.Hungry => 0.05,
                    CompanyStatus.Dying => 0.15,
                    _ => 0
                };
                // FBN true event: annual risk becomes today's daily probability
                if (!string.IsNullOrEmpty(account.FbnTempBoostCompany)
                    && account.FbnTempBoostCompany == company.CompanyName)
                    dailyProb = _config.ProsperousAnnualRisk;

                double bankruptRoll = account.TomorrowBankruptRandoms.TryGetValue(company.CompanyName, out double br)
                    ? br : _rng.NextDouble();
                if (dailyProb > 0 && bankruptRoll < dailyProb)
                {
                    _monitor.Log($"[Bankrupt] {company.CompanyName} went bankrupt (status={company.Status}, dailyProb={dailyProb:F4})", LogLevel.Warn);
                    TransferLoansFromDyingCompany(account, company);
                    LiquidateCompany(account, company, I18n.Get("cmp.26"));
                    continue;
                }
            }

            // Fuel-starved but NOT in restructuring → immediate death
            if (company.FuelStock <= 0 && newStatus == CompanyStatus.Dying && company.RestructuringDaysRemaining <= 0)
            {
                TransferLoansFromDyingCompany(account, company);
                LiquidateCompany(account, company, I18n.Get("cmp.27"));
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

        // Season interest log: reset when season changes
        string currentSeason = Game1.currentSeason;
        int currentYear = Game1.year;
        string seasonKey = $"{currentSeason}_Year{currentYear}";
        if (account.InterestLogSeason != seasonKey)
        {
            _monitor.Log($"[InterestLog] Season changed to {seasonKey}, clearing log (was {account.InterestLogSeason}, {account.SeasonInterestLog.Count} records)", LogLevel.Info);
            account.InterestLogSeason = seasonKey;
            account.SeasonInterestLog.Clear();
        }
        int daysPlayed = (int)Game1.stats.DaysPlayed;
        int seasonDay = Game1.dayOfMonth;

        var allCompanyDefs = GetAllCompanyDefinitions(account);
        int totalInterest = 0;
        // Collect per-company interest for notification (use pre-increment values)
        var companyInterestMap = new Dictionary<string, (string DisplayName, int Interest, double Rate, int Deposit)>();

        foreach (var ca in account.CompanyAccounts)
        {
            var company = allCompanyDefs.FirstOrDefault(c => c.Name == ca.CompanyName);
            if (company is null) continue;

            // Rebalance principal/interest when:
            // 1. Principal withdrawn past zero (BaseAmount ≤ 0), or
            // 2. Total deposit ≤ accumulated interest (AccInt has consumed the balance)
            if (ca.DepositBalance > 0 &&
                (ca.BaseAmount <= 0 || ca.DepositBalance <= ca.AccumulatedInterest))
            {
                ca.AccumulatedInterest = Math.Max(0, ca.DepositBalance - company.DepositLimit);
                ca.BaseAmount = ca.DepositBalance - ca.AccumulatedInterest;
                ca.PreviousBaseAmount = ca.BaseAmount;
                // Reset anti-compound tracking to avoid false detection after rebalance.
                // Without this, DetectManualCompoundInterest sees the AccInt drop as
                // "interest withdrawn and re-deposited as principal" → false penalty.
                ca.PreviousAccumulatedInterest = ca.AccumulatedInterest;
                ca.AccIntDecreasedInWindow = false;
                _monitor.Log($"[Rebalance] {ca.CompanyName}: AccInt reset to {ca.AccumulatedInterest}, BaseAmount={ca.BaseAmount} (anti-compound tracking cleared)", LogLevel.Info);
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
                IsPenaltyPeriod = ca.PenaltyDaysRemaining > 0,
                PreGeneratedRandom = account.TodayRandoms.TryGetValue(ca.CompanyName, out double rnd) ? rnd : null
            };

            var calculator = company.IsDynamic ? _dynamicInterestCalculator : _fixedInterestCalculator;
            double rate = calculator.CalculateDepositRate(company, ctx);

            bool isCompound = account.UseCompoundInterest
                && !string.IsNullOrEmpty(account.CompoundActiveCompany)
                && account.CompoundActiveCompany == ca.CompanyName
                && account.CompoundDaysRemaining > 0;
            int principal = isCompound ? Math.Max(0, ca.DepositBalance) : Math.Max(0, ca.BaseAmount);
            int interest = (int)(principal * rate);

            // Record daily interest log (even if interest is 0, to show rate)
            account.SeasonInterestLog.Add(new DailyInterestRecord
            {
                Day = daysPlayed,
                SeasonDay = seasonDay,
                CompanyName = ca.CompanyName,
                Rate = rate,
                Interest = interest
            });

            if (rate == 0) continue;

            if (interest == 0) continue;

            ca.DepositBalance += interest;
            ca.AccumulatedInterest += interest;
            totalInterest += interest;

            string displayName = ResolveDisplayName(company);
            companyInterestMap[ca.CompanyName] = (displayName, interest, rate, ca.DepositBalance);

            _monitor.Log(I18n.Get("cmp.10", new { interest, rate = $"{rate:P2}", days = isCompound ? account.CompoundDurationDays : 1 }), LogLevel.Info);
        }

        if (totalInterest != 0 && _config.ShowMorningInterestNotification)
        {
            Game1.chatBox?.addInfoMessage(I18n.Get("cmp.11", new { total = totalInterest }));

            foreach (var entry in companyInterestMap.Values)
            {
                Game1.chatBox?.addInfoMessage(I18n.Get("cmp.12", new { company = entry.DisplayName, interest = entry.Interest.ToString("N0"), rate = $"{entry.Rate:P2}", deposit = entry.Deposit.ToString("N0") }));
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
            // Stage 15: skip anti-compound detection when official compound is active,
            // but still update PreviousBaseAmount/PreviousAccumulatedInterest so there's
            // no accumulated drift when compound expires
            if (!string.IsNullOrEmpty(account.CompoundActiveCompany)
                && account.CompoundActiveCompany == ca.CompanyName
                && account.CompoundDaysRemaining > 0)
            {
                ca.PreviousBaseAmount = ca.BaseAmount;
                ca.PreviousAccumulatedInterest = ca.AccumulatedInterest;
                continue;
            }

            // 1. Decrement penalty days
            if (ca.PenaltyDaysRemaining > 0)
                ca.PenaltyDaysRemaining--;

            // 2. Check if AccumulatedInterest decreased compared to yesterday.
            //    AccInt decreasing means interest was withdrawn (and potentially re-deposited
            //    as principal). This is a necessary condition for manual compound interest —
            //    pure farming deposits never decrease AccInt.
            if (ca.AccumulatedInterest < ca.PreviousAccumulatedInterest)
            {
                ca.AccIntDecreasedInWindow = true;
                _monitor.Log($"[V3.7] AccInt decreased for {ca.CompanyName}: {ca.PreviousAccumulatedInterest}->{ca.AccumulatedInterest} (delta={ca.AccumulatedInterest - ca.PreviousAccumulatedInterest})", LogLevel.Info);
            }
            ca.PreviousAccumulatedInterest = ca.AccumulatedInterest;

            // 3. Compute today's principal delta
            int delta = ca.BaseAmount - ca.PreviousBaseAmount;
            ca.PreviousBaseAmount = ca.BaseAmount;

            // 4. Write to sliding window (only positives count; zeros/negatives preserve position)
            if (delta > 0)
                ca.RecentPrincipalDeltas[ca.WindowDayIndex] = delta;
            else
                ca.RecentPrincipalDeltas[ca.WindowDayIndex] = 0;

            // 5. Scan window from oldest to newest for peak-break pattern
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

            // 6. Trigger penalty — requires BOTH:
            //    a) Peak-break pattern (breakCount >= 3): principal deposits are escalating
            //    b) AccInt decreased in window: interest was withdrawn and converted to principal
            //    Without (b), escalating deposits are just normal farming income (selling crops).
            if (breakCount >= 3 && ca.AccIntDecreasedInWindow && ca.PenaltyDaysRemaining <= 0)
            {
                ca.PenaltyDaysRemaining = 7;
                _monitor.Log(
                    $"[V3.7] Manual compound interest detected for {ca.CompanyName}! " +
                    $"Peak breaks: {breakCount}, AccInt decreased in window, deposit rate reduced to 10% for 7 days.",
                    LogLevel.Warn);
                if (_config.ShowCompanyDangerWarning)
                {
                    var dcMatch = account.DynamicCompanies.FirstOrDefault(d => d.CompanyName == ca.CompanyName);
                    string displayName = dcMatch != null
                        ? ResolveDynamicDisplayName(dcMatch)
                        : ResolveAccountDisplayName(ca.CompanyName, _config.Companies);
                    Game1.chatBox?.addInfoMessage(
                        I18n.Get("cmp.13", new { company = displayName, rate = "10%", days = 7 }));
                }
            }

            // 7. Advance window; reset state on wrap
            ca.WindowDayIndex = (ca.WindowDayIndex + 1) % 7;
            if (ca.WindowDayIndex == 0)
            {
                ca.HasFirstIncrease = false;
                ca.LastPositiveDelta = 0;
                ca.WindowPeak = 0;
                ca.WindowBreakCount = 0;
                ca.AccIntDecreasedInWindow = false;
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

    /// <summary>Pre-generate tomorrow's per-company random values for FBN forecast.
    /// Called at DayEnding. Only dynamic companies get random volatility.</summary>
    public void PreGenerateTomorrowValues(BankAccountData account)
    {
        account.TomorrowRandoms.Clear();
        account.TomorrowBankruptRandoms.Clear();
        var activeCompanies = account.DynamicCompanies
            .Where(c => c.Status != CompanyStatus.Bankrupt)
            .ToList();
        var dynamicNames = activeCompanies.Select(c => c.CompanyName).ToHashSet();
        foreach (var ca in account.CompanyAccounts)
        {
            if (dynamicNames.Contains(ca.CompanyName))
                account.TomorrowRandoms[ca.CompanyName] = _rng.NextDouble() * 2 - 1; // [-1, +1]
        }
        // Pre-generate bankruptcy random check for each active company
        foreach (var dc in activeCompanies)
        {
            account.TomorrowBankruptRandoms[dc.CompanyName] = _rng.NextDouble(); // [0, 1)
        }
        // Pre-generate tomorrow's luck (mirrors SDV: Math.Min(0.1, random.Next(-100, 101) / 1000.0))
        account.TomorrowLuck = Math.Min(0.1, _rng.Next(-100, 101) / 1000.0);
    }

    /// <summary>Generate today's per-company random values and rotate tomorrow's into today's.
    /// Called at DayStarted. Ensures reload stability (today's values are saved to disk).</summary>
    public void GenerateTodayValues(BankAccountData account)
    {
        // Rotate: tomorrow's values become today's
        account.TodayRandoms = new Dictionary<string, double>(account.TomorrowRandoms);
        // Tomorrow's bankruptcy randoms are consumed at UpdateCompanyStatuses — do NOT clear here.
        // They were pre-generated at DayEnding and saved to disk; clearing here would regenerate
        // them with a different RNG sequence, defeating the determinism guarantee.
        // Regenerate only the non-bankruptcy randoms (company volatility + luck)
        account.TomorrowRandoms.Clear();
        var activeCompanies = account.DynamicCompanies
            .Where(c => c.Status != CompanyStatus.Bankrupt)
            .ToList();
        var dynamicNames = activeCompanies.Select(c => c.CompanyName).ToHashSet();
        foreach (var ca in account.CompanyAccounts)
        {
            if (dynamicNames.Contains(ca.CompanyName))
                account.TomorrowRandoms[ca.CompanyName] = _rng.NextDouble() * 2 - 1;
        }
        account.TomorrowLuck = Math.Min(0.1, _rng.Next(-100, 101) / 1000.0);
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
                        Game1.chatBox?.addInfoMessage(I18n.Get("cmp.14", new { deposit = ca.BaseAmount }));
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
                    string newDisplayName = (newCrop?.DisplayName ?? target.CropCode) + I18n.Get("cmp.6");
                    dc.CropCode = target.CropCode;
                    dc.CompanyName = target.CropCode;
                    dc.Status = CompanyStatus.New;
                    dc.ProtectionEndDay = (int)Game1.stats.DaysPlayed + NewCompanyProtectionDays;
                    dc.FuelStock = (int)((newCrop?.Smax ?? 0) * 0.3 * 10);
                    dc.PendingRescueDeposit = 0;
                    dc.RestructuringDaysRemaining = 0;
                    dc.AssetPoolConsumed = 0;
                    Game1.chatBox?.addInfoMessage(I18n.Get("cmp.15", new { company = newDisplayName }));
                    _monitor.Log($"[Season] {oldName} transformed to {newDisplayName} (crop: {target.CropCode})", LogLevel.Info);
                    continue;
                }
            }

            // Outcome 3: 倒闭撤退 (Hungry 60%, Dying 80%)
            if (dc.Status == CompanyStatus.Hungry && _rng.Next(100) < 60
                || dc.Status == CompanyStatus.Dying && _rng.Next(100) < 80)
            {
                Game1.chatBox?.addInfoMessage(I18n.Get("cmp.16"));
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
    private static readonly Random _fbnRng = new();

    private void TryTriggerFbnEvent(BankAccountData account)
    {
        var companies = account.DynamicCompanies.Where(c => c.Status != CompanyStatus.Bankrupt).ToList();
        if (companies.Count == 0) return;

        int day = Game1.dayOfMonth;
        string season = Game1.currentSeason;

        // Season change: reset quotas
        if (account.FbnSeason != season)
        {
            account.FbnSeason = season;
            account.FbnTrueUsed = false;
            account.FbnFalseUsed = 0;
            account.FbnFalseQuota = _fbnRng.Next(2, 4); // 2-3 false
            account.FbnLastEventDay = 0;
        }

        // Cannot be adjacent to yesterday's event
        if (account.FbnLastEventDay > 0 && day == account.FbnLastEventDay + 1) return;

        // Random trigger: ~15% chance per day if quota remains
        int remaining = (account.FbnTrueUsed ? 0 : 1) + (account.FbnFalseQuota - account.FbnFalseUsed);
        if (remaining <= 0) return;
        if (_fbnRng.Next(100) >= 15 * remaining) return;

        // Pick true or false (true prioritized if not used)
        bool isReal = !account.FbnTrueUsed && _fbnRng.Next(remaining) == 0;
        if (isReal)
            account.FbnTrueUsed = true;
        else
            account.FbnFalseUsed++;

        var company = companies[_fbnRng.Next(companies.Count)];
        account.FbnLastEventDay = day;

        if (isReal)
        {
            account.FbnTempBoostCompany = company.CompanyName;
            account.FbnEventCompany = company.CompanyName;
            account.FbnEventIsReal = true;
            account.FbnEventDay = day;
            account.FbnShowOutcome = false;
            _monitor.Log(I18n.Get("cmp.17", new { day, name = company.CompanyName }), LogLevel.Info);
        }
        else
        {
            account.FbnEventCompany = company.CompanyName;
            account.FbnEventIsReal = false;
            account.FbnEventDay = day;
            account.FbnShowOutcome = false;
            _monitor.Log(I18n.Get("cmp.18", new { day, name = company.CompanyName }), LogLevel.Info);
        }
        // TODO: show FBN news via TV system or chat
    }

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
            loan.CompanyName = bestFixed.OriginalName;
            loan.InterestRate = transferRate;
            loan.RepaymentPeriodDays = _config.DebtTransferNewRepaymentDays;
            loan.DueDay = today + _config.DebtTransferNewRepaymentDays;
            loan.IsInDefault = false;
            loan.DefaultDaysRemaining = 0;
            loan.IsTransferred = true;
        }

        string oldDisplayName = ResolveDynamicDisplayName(company);
        string newDisplayName = ResolveAccountDisplayName(bestFixed.Name, _config.Companies);
        Game1.chatBox?.addInfoMessage(I18n.Get("cmp.19", new { count = companyLoans.Count, oldCompany = oldDisplayName, newCompany = newDisplayName, rate = transferRate.ToString("P2"), days = _config.DebtTransferNewRepaymentDays }));
        _monitor.Log($"[Stage9] {companyLoans.Count} loans transferred from {company.CompanyName} to {bestFixed.Name}", LogLevel.Info);
    }

    /// <summary>Formal liquidation: return deposit by status tier, clear company, reset shipment counter.</summary>
    private void LiquidateCompany(BankAccountData account, DynamicCompanyData company, string? reason = null)
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

        string msg = I18n.Get("cmp.20");
        if (!string.IsNullOrEmpty(reason))
            msg += $" {reason}";
        if (returnAmount > 0)
            msg += I18n.Get("cmp.21", new { amount = returnAmount });
        else
            msg += I18n.Get("cmp.22");
        if (rescueLost > 0)
            msg += I18n.Get("cmp.23");
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
            I18n.Get("cmp.24", new { shares = actualDaysAdded, company = companyName }) +
            I18n.Get("cmp.25", new { days = company.RestructuringDaysRemaining }));
        return true;
    }
}
