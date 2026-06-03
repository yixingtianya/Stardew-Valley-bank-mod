using BankMod.Data;
using BankMod.Domain;
using BankMod.Services.Abstractions;
using BankMod.Services.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

internal class BankMenu : IClickableMenu
{
    private const int WindowHeight = 820;
    private readonly bool _isChinese;
    private static bool IsChineseLocale =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
    private static int GetWindowWidth() =>
        IsChineseLocale ? 600 : 720;

    private readonly BankAccountData _account;
    private readonly ModConfig _config;
    private readonly IModHelper _helper;
    private readonly IBankAccountService _accountService;
    private readonly ILoanService _loanService;
    private readonly ModServices _services;
    private readonly List<CompanyDefinition> _companyList;
    private int _selectedCompanyIndex;

    private IInterestCalculator InterestCalculator =>
        CurrentCompany.IsDynamic ? _services.DynamicInterestCalculator : _services.FixedInterestCalculator;

    private readonly ClickableTextureComponent _closeButton;
    private readonly List<ClickableComponent> _tabButtons = new();
    private readonly ClickableTextureComponent _depositBtn;
    private readonly ClickableTextureComponent _withdrawBtn;
    private readonly ClickableTextureComponent _borrow7Btn;
    private readonly ClickableTextureComponent _borrow14Btn;
    private readonly ClickableTextureComponent _repayBtn;
    private readonly ClickableTextureComponent _rescueBtn;
    private readonly ClickableTextureComponent _onlineShopBtn;
    private readonly ClickableTextureComponent _compoundBtn;
    private readonly ClickableTextureComponent _loanDetailBtn;
    private readonly List<ClickableTextureComponent> _loanRepayBtns = new();
    private bool _hoverDeposit;
    private bool _hoverWithdraw;
    private bool _hoverBorrow7;
    private bool _hoverBorrow14;
    private bool _hoverRepay;
    private bool _hoverRescue;
    private bool _hoverOnlineShop;
    private bool _hoverCompound;
    private bool _hoverLoanDetail;
    private Rectangle _depositRateBounds;
    private Rectangle _loanRateBounds;
    private bool _showLoanDetail;
    private int _loanScrollOffset;
    private int _tabScrollOffset;
    private int _contentOffsetY;
    private Rectangle _tabLeftArrow;
    private Rectangle _tabRightArrow;

    public BankMenu(BankAccountData account, ModConfig config, IModHelper helper, ModServices services, int selectedTab = 0)
        : base(
            (Game1.uiViewport.Width - GetWindowWidth()) / 2,
            (Game1.uiViewport.Height - WindowHeight) / 2,
            GetWindowWidth(),
            WindowHeight
        )
    {
        _isChinese = IsChineseLocale;
        _selectedCompanyIndex = selectedTab;
        _account = account;
        _config = config;
        _helper = helper;
        _accountService = services.BankAccountService;
        _loanService = services.LoanService;
        _services = services;

        // Build combined company list: fixed from config + active dynamic from save data
        _companyList = services.CompanyManager.GetAllCompanyDefinitions(account);

        _closeButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + 12, 40, 40),
            Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f
        );

        bool hasWarning = account.IsInBankruptcy || account.IsInPrincipalDebt || account.IsInInterestDebt;

        int tabStartX = xPositionOnScreen + 30;
        int tabY = yPositionOnScreen + 55;
        int tabHeight = 30;
        for (int i = 0; i < _companyList.Count; i++)
        {
            int tabWidth = (int)Game1.smallFont.MeasureString(DisplayName(_companyList[i])).X + 24;
            _tabButtons.Add(new ClickableComponent(
                new Rectangle(tabStartX, tabY, tabWidth, tabHeight),
                i.ToString()
            ));
            tabStartX += tabWidth + 8;
        }

        // Buttons: deposit/withdraw/borrow on top row, conditional on bottom row
        int centerX = xPositionOnScreen + width / 2;
        int btnY1 = yPositionOnScreen + height - 90;
        int btnY2 = yPositionOnScreen + height - 40;
        int btnW = _isChinese ? 130 : 150;
        int btnH = 40;
        int gap = 10;

        if (_isChinese)
        {
            // Original layout: hardcoded positions (matching 原版)
            _depositBtn = MakeBtn(centerX - btnW * 2 - gap * 2, btnY1, btnW, btnH);
            _withdrawBtn = MakeBtn(centerX - btnW - gap, btnY1, btnW, btnH);
            _borrow7Btn = MakeBtn(centerX + gap, btnY1, btnW, btnH);
            _borrow14Btn = MakeBtn(centerX + btnW + gap * 2, btnY1, btnW, btnH);
            _repayBtn = MakeBtn(centerX - btnW - gap / 2, btnY2, btnW, btnH);
            _loanDetailBtn = MakeBtn(centerX + gap / 2, btnY2, btnW, btnH);
            _rescueBtn = MakeBtn(centerX - btnW * 2 - gap * 2, btnY2, btnW, btnH);
            _onlineShopBtn = MakeBtn(centerX - btnW * 2 - gap * 2, btnY2, btnW, btnH);
            _compoundBtn = MakeBtn(centerX + btnW + gap, btnY2, btnW, btnH);
        }
        else
        {
            // English layout: Row 1 centered, Row 2 dynamic
            int row1Total = btnW * 4 + gap * 3;
            int row1Start = centerX - row1Total / 2;
            _depositBtn = MakeBtn(row1Start, btnY1, btnW, btnH);
            _withdrawBtn = MakeBtn(row1Start + btnW + gap, btnY1, btnW, btnH);
            _borrow7Btn = MakeBtn(row1Start + (btnW + gap) * 2, btnY1, btnW, btnH);
            _borrow14Btn = MakeBtn(row1Start + (btnW + gap) * 3, btnY1, btnW, btnH);
            _repayBtn = MakeBtn(0, btnY2, btnW, btnH);
            _loanDetailBtn = MakeBtn(0, btnY2, btnW, btnH);
            _rescueBtn = MakeBtn(0, btnY2, btnW, btnH);
            _onlineShopBtn = MakeBtn(0, btnY2, btnW, btnH);
            _compoundBtn = MakeBtn(0, btnY2, btnW, btnH);
        }
    }

    private CompanyDefinition CurrentCompany => _companyList[_selectedCompanyIndex];

    private string DisplayName(CompanyDefinition c) => _services.RouteService.GetDisplayName(c);

    private static ClickableTextureComponent MakeBtn(int x, int y, int w, int h) =>
        new(new Rectangle(x, y, w, h), Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f);

    /// <summary>Cut text into lines that fit within maxWidth pixels using smallFont.</summary>
    private static List<string> WrapText(string text, int maxWidth)
    {
        List<string> lines = new();
        foreach (string segment in text.Split('\n'))
        {
            string[] words = segment.Split(' ');
            string line = "";
            foreach (string word in words)
            {
                string test = line.Length == 0 ? word : line + " " + word;
                if (Game1.smallFont.MeasureString(test).X <= maxWidth)
                    line = test;
                else
                {
                    if (line.Length > 0) lines.Add(line);
                    line = word;
                }
            }
            lines.Add(line.Length > 0 ? line : "");
        }
        return lines;
    }

    private CompanyAccount GetOrCreateAccount()
    {
        return _accountService.GetOrCreateAccount(_account, CurrentCompany.Name, CurrentCompany.OriginalName);
    }

    private LoanRecord? GetCurrentLoan()
    {
        return _loanService.GetLoan(_account, CurrentCompany);
    }

    private void SaveAccount()
    {
        _accountService.Save(_account);
    }

    private InterestCalculationContext BuildContext()
    {
        var tracking = _account.CropShipments.FirstOrDefault(
            s => s.CropCode == (CurrentCompany.CropCode ?? CurrentCompany.Name));
        var suppression = _account.CropSuppressions.FirstOrDefault(
            s => s.CropCode == (CurrentCompany.CropCode ?? CurrentCompany.Name));
        var ca = _account.CompanyAccounts.FirstOrDefault(
            a => a.CompanyName == CurrentCompany.Name);

        return new InterestCalculationContext
        {
            Company = CurrentCompany,
            DailyLuck = Game1.player.DailyLuck,
            IsLightning = Game1.isLightning,
            IsRaining = Game1.isRaining,
            IsSnowing = Game1.isSnowing,
            Config = _config,
            ConsecutiveSellDays = tracking?.ConsecutiveSellDays ?? 0,
            HasSoldHistory = tracking is not null && tracking.CumulativeSellCount > 0,
            DecayDays = tracking?.DecayDays ?? 0,
            SuppressionStacks = suppression?.Stacks ?? 0,
            IsPenaltyPeriod = ca is not null && ca.PenaltyDaysRemaining > 0
        };
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // Self-heal: clear stale debt flags when no loans exist
        if (_account.Loans.Count == 0 && (_account.IsInPrincipalDebt || _account.IsInInterestDebt))
        {
            _account.IsInPrincipalDebt = false;
            _account.IsInInterestDebt = false;
            SaveAccount();
        }

        // Warning text (same logic for both locales)
        string warnText = "";
        if (_account.IsInBankruptcy)
            warnText = I18n.Get("uib.1");
        else if (_account.IsInPrincipalDebt)
            warnText = I18n.Get("uib.2");
        else if (_account.IsInInterestDebt)
            warnText = I18n.Get("uib.3");

        int titleY = yPositionOnScreen + 6;
        _contentOffsetY = 0;

        if (_isChinese)
        {
            // Original layout: single-line warning above title, shifts title down
            if (warnText.Length > 0)
            {
                titleY = yPositionOnScreen + 18;
                Vector2 wtSize = Game1.smallFont.MeasureString(warnText);
                int warnBX = xPositionOnScreen + (width - (int)wtSize.X) / 2 - 12;
                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(warnBX - 4, titleY - 20, (int)wtSize.X + 32, 22),
                    Color.Maroon * 0.6f);
                b.DrawString(Game1.smallFont, warnText,
                    new Vector2(warnBX + 8, titleY - 16), Color.Red);
            }
        }
        else
        {
            // English layout: multi-line warning below divider with _contentOffsetY
            int fakeDividerY = titleY + 32;
            if (warnText.Length > 0)
            {
                int maxWarnWidth = width - 40;
                List<string> warnLines = WrapText(warnText, maxWarnWidth);
                float warnLineH = Game1.smallFont.MeasureString("A").Y;
                int warnBannerH = (int)(warnLines.Count * warnLineH) + 6;
                int warnY = fakeDividerY + 4;
                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(xPositionOnScreen + 12, warnY, width - 24, warnBannerH),
                    Color.Maroon * 0.4f);
                for (int i = 0; i < warnLines.Count; i++)
                {
                    Vector2 lineSz = Game1.smallFont.MeasureString(warnLines[i]);
                    b.DrawString(Game1.smallFont, warnLines[i],
                        new Vector2(xPositionOnScreen + (width - lineSz.X) / 2, warnY + 3 + i * warnLineH),
                        Color.Red);
                }
                _contentOffsetY = warnBannerH + 4;
            }
        }

        // Title + author credit
        string title = I18n.Get("loc.title") + "  ";
        string credit = "by yixingtianya";
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        Vector2 creditSize = Game1.smallFont.MeasureString(credit);
        float combinedW = titleSize.X + creditSize.X;
        float titleX = xPositionOnScreen + (width - combinedW) / 2;
        Utility.drawTextWithShadow(b, title, Game1.dialogueFont,
            new Vector2(titleX, titleY), Game1.textColor);
        Utility.drawTextWithShadow(b, credit, Game1.smallFont,
            new Vector2(titleX + titleSize.X, titleY + 6), Color.DimGray);

        int dividerY = titleY + 32;
        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 30, dividerY, width - 60, 2), Color.Gray);

        // Company tabs
        int maxVisibleTabs = _isChinese ? 4 : 3;
        int tabScrollMax = Math.Max(0, _companyList.Count - maxVisibleTabs);
        _tabScrollOffset = Math.Clamp(_tabScrollOffset, 0, tabScrollMax);

        // Left arrow
        if (tabScrollMax > 0)
        {
            _tabLeftArrow = new Rectangle(xPositionOnScreen + 8, yPositionOnScreen + 62 + _contentOffsetY, 20, 30);
            if (_tabScrollOffset > 0)
                b.Draw(Game1.mouseCursors, _tabLeftArrow, new Rectangle(352, 495, 12, 11), Color.White);
        }

        // Draw visible tabs, repositioned from left
        int drawX = xPositionOnScreen + 30;
        int tabHeight = 30;
        for (int i = 0; i < maxVisibleTabs; i++)
        {
            int idx = _tabScrollOffset + i;
            if (idx >= _companyList.Count) break;

            string name = DisplayName(_companyList[idx]);
            int tabWidth = (int)Game1.smallFont.MeasureString(name).X + 24;
            var tabRect = new Rectangle(drawX, yPositionOnScreen + 62 + _contentOffsetY, tabWidth, tabHeight);

            bool selected = idx == _selectedCompanyIndex;
            Color bg = selected ? Color.White : Color.LightGray * 0.5f;
            b.Draw(Game1.staminaRect, tabRect, bg);
            b.Draw(Game1.staminaRect, new Rectangle(tabRect.X, tabRect.Y, tabRect.Width, 2), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tabRect.X, tabRect.Y + tabRect.Height - 2, tabRect.Width, 2), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tabRect.X, tabRect.Y, 2, tabRect.Height), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tabRect.X + tabRect.Width - 2, tabRect.Y, 2, tabRect.Height), Color.Gray);

            Vector2 labelSize = Game1.smallFont.MeasureString(name);
            Utility.drawTextWithShadow(b, name, Game1.smallFont,
                new Vector2(drawX + 12, yPositionOnScreen + 62 + _contentOffsetY + (tabHeight - labelSize.Y) / 2),
                selected ? Color.Black : Color.Gray);

            drawX += tabWidth + 8;
        }

        // Right arrow
        if (tabScrollMax > 0)
        {
            _tabRightArrow = new Rectangle(xPositionOnScreen + width - 28, yPositionOnScreen + 62 + _contentOffsetY, 20, 30);
            if (_tabScrollOffset < tabScrollMax)
                b.Draw(Game1.mouseCursors, _tabRightArrow, new Rectangle(365, 495, 12, 11), Color.White);
        }

        // === Deposit section ===
        var company = CurrentCompany;
        var acct = GetOrCreateAccount();
        var loan = GetCurrentLoan();
        int infoX = xPositionOnScreen + 40;
        int infoY = dividerY + 48 + _contentOffsetY;
        int lineH = 26;

        string titleStr = $"=== {DisplayName(company)} ===";
        Utility.drawTextWithShadow(b, titleStr, Game1.smallFont, new Vector2(infoX, infoY), Color.Black);

        int lineOffset = 0;

        // Dynamic company status and fuel display
        if (company.IsDynamic)
        {
            var dyn = _account.DynamicCompanies.FirstOrDefault(c =>
                c.CompanyName == company.Name || c.CompanyName == company.OriginalName);
            if (dyn is not null)
            {
                int today = (int)Game1.stats.DaysPlayed;
                (string statusText, Color statusColor) = dyn.Status switch
                {
                    CompanyStatus.New => (I18n.Get("uib.4", new { days = Math.Max(0, dyn.ProtectionEndDay - today) }), Color.Cyan),
                    CompanyStatus.Prosperous => (I18n.Get("uib.5"), Color.Gold),
                    CompanyStatus.Stable => (I18n.Get("uib.6"), Color.LimeGreen),
                    CompanyStatus.Hungry => (I18n.Get("uib.7"), Color.Orange),
                    CompanyStatus.Dying => (dyn.RestructuringDaysRemaining > 0
                        ? I18n.Get("uib.8", new { days = dyn.RestructuringDaysRemaining }) : I18n.Get("uib.9"), Color.Red),
                    CompanyStatus.Protection => (I18n.Get("uib.10"), Color.Red),
                    _ => ("", Color.Gray)
                };
                // Season transition overlay
                if (dyn.SeasonTransitionDays > 0)
                {
                    statusText = statusText.Length > 0
                        ? statusText.TrimEnd(']') + I18n.Get("uib.11", new { days = dyn.SeasonTransitionDays })
                        : I18n.Get("uib.12", new { days = dyn.SeasonTransitionDays });
                    statusColor = Color.DarkCyan;
                }
                if (statusText.Length > 0)
                {
                    DrawInfoLine(b, statusText, infoX + 20, infoY + lineH, statusColor);
                    lineOffset++;
                }

                // Fuel stats
                var cropData = CropDataProvider.GetByCode(company.CropCode ?? company.Name);
                if (cropData is not null)
                {
                    double displayFuel = dyn.FuelStock / 10.0;
                    double smaxDisplay = cropData.Smax * 2;
                    double dailyDemand = cropData.DBase;

                    double statusCoefficient = dyn.Status switch
                    {
                        CompanyStatus.Prosperous => 1.0,
                        CompanyStatus.New => 0.8,
                        CompanyStatus.Stable => 0.8,
                        CompanyStatus.Hungry => 0.5,
                        CompanyStatus.Dying => 0.3,
                        CompanyStatus.Protection => 0.3,
                        _ => 0.8
                    };
                    double dailyConsumption = dailyDemand * statusCoefficient;
                    double daysOfFuel = dailyConsumption > 0 ? displayFuel / dailyConsumption : 999;
                    double satisfactionPct = smaxDisplay > 0 ? displayFuel / smaxDisplay * 100 : 0;

                    Color fuelColor = satisfactionPct > 150 ? Color.LimeGreen : satisfactionPct >= 80 ? Color.Lime : satisfactionPct >= 30 ? Color.Orange : Color.Red;
                    string cropUnit = cropData.DisplayName;
                    DrawInfoLine(b, I18n.Get("uib.13", new { units = displayFuel.ToString("F1"), smax = smaxDisplay.ToString("F0"), cropUnit, days = daysOfFuel.ToString("F1") }), infoX + 20, infoY + lineH * (lineOffset + 1), fuelColor);
                    lineOffset++;
                    DrawInfoLine(b, I18n.Get("uib.14", new { units = dailyConsumption.ToString("F1"), baseDemand = dailyDemand.ToString("F1"), coefficient = statusCoefficient.ToString("F2"), baseUnit = cropUnit }), infoX + 20, infoY + lineH * (lineOffset + 1), Color.Gray);
                    lineOffset++;
                }
            }
        }

        DrawInfoLine(b, I18n.Get("uib.15", new { amount = acct.DepositBalance.ToString("N0") }), infoX + 20, infoY + lineH * (lineOffset + 1), Color.DarkGreen);
        DrawInfoLine(b, I18n.Get("uib.16", new { amount = acct.BaseAmount.ToString("N0") }), infoX + 20, infoY + lineH * (lineOffset + 2), Color.DimGray);
        DrawInfoLine(b, I18n.Get("uib.17", new { amount = company.DepositLimit.ToString("N0") }), infoX + 20, infoY + lineH * (lineOffset + 3), Color.DimGray);

        double effectiveDepRate = InterestCalculator.CalculateDepositRate(company, BuildContext());
        string depRateText = I18n.Get("uib.18", new { rate = $"{effectiveDepRate * 100:F3}%" });
        Vector2 depRateSize = Game1.smallFont.MeasureString(depRateText);
        _depositRateBounds = new Rectangle(infoX + 20, infoY + lineH * (lineOffset + 4), (int)depRateSize.X, lineH);
        DrawInfoLine(b, depRateText, infoX + 20, infoY + lineH * (lineOffset + 4), Color.DimGray);

        double effectiveLoanRate = InterestCalculator.CalculateLoanRate(company, BuildContext());
        string loanRateText = I18n.Get("uib.19", new { rate = $"{effectiveLoanRate * 100:F3}%" });
        Vector2 loanRateSize = Game1.smallFont.MeasureString(loanRateText);
        _loanRateBounds = new Rectangle(infoX + 20, infoY + lineH * (lineOffset + 5), (int)loanRateSize.X, lineH);
        DrawInfoLine(b, loanRateText, infoX + 20, infoY + lineH * (lineOffset + 5), Color.DimGray);

        DrawInfoLine(b, I18n.Get("uib.20", new { amount = company.LoanLimit.ToString("N0") }), infoX + 20, infoY + lineH * (lineOffset + 6), Color.DimGray);

        if (acct.AccumulatedInterest > 0)
        {
            DrawInfoLine(b, I18n.Get("uib.21", new { amount = acct.AccumulatedInterest.ToString("N0") }), infoX + 20, infoY + lineH * (lineOffset + 7), Color.Goldenrod);
        }

        // Hover tooltips for effective rates → show base rate
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();
        if (_depositRateBounds.Contains(mx, my))
            drawHoverText(b, I18n.Get("uib.22", new { rate = $"{company.DepositInterestRate * 100:F2}%" }), Game1.smallFont);
        if (_loanRateBounds.Contains(mx, my))
            drawHoverText(b, I18n.Get("uib.23", new { rate = $"{company.LoanInterestRate * 100:F2}%" }), Game1.smallFont);

        // === Loan section ===
        int loanY = infoY + lineH * (lineOffset + 9);
        b.Draw(Game1.staminaRect, new Rectangle(infoX, loanY - 4, width - 80, 2), Color.Gray * 0.5f);

        var companyLoans = _loanService.GetCompanyLoans(_account, CurrentCompany);

        if (!_showLoanDetail)
        {
            // === Summary view: show first loan details (original behavior) ===
            if (loan is not null)
            {
                int today = (int)Game1.stats.DaysPlayed;
                int daysLeft = loan.DueDay - today;

                string summary = companyLoans.Count > 1 ? I18n.Get("uib.24", new { count = companyLoans.Count }) : "";
                int totalPrincipal = companyLoans.Sum(l => l.Principal);
                int totalInterest = companyLoans.Sum(l => l.AccumulatedInterest);
                DrawInfoLine(b, I18n.Get("uib.25", new { amount = totalPrincipal.ToString("N0") }), infoX + 20, loanY, Color.DarkRed);
                DrawInfoLine(b, I18n.Get("uib.26", new { amount = totalInterest.ToString("N0") }), infoX + 20, loanY + lineH, Color.Red);

                int nextLine = 2;
                int totalOverdue = companyLoans.Sum(l => l.OverdueInterest);
                if (totalOverdue > 0)
                {
                    DrawInfoLine(b, I18n.Get("uib.27", new { amount = totalOverdue.ToString("N0") }), infoX + 20, loanY + lineH * nextLine, Color.DarkRed);
                    nextLine++;
                }

                DrawInfoLine(b, I18n.Get("uib.28", new { rate = $"{loan.InterestRate * 100:F3}%" }), infoX + 20, loanY + lineH * nextLine, Color.DimGray);
                nextLine++;
                DrawInfoLine(b, I18n.Get("uib.29", new { days = loan.RepaymentPeriodDays }), infoX + 20, loanY + lineH * nextLine, Color.DimGray);
                nextLine++;

                Color dueColor;
                string dueStr;
                if (companyLoans.Any(l => l.IsFrozen))
                {
                    dueColor = Color.Red;
                    dueStr = I18n.Get("uib.30");
                }
                else if (loan.IsInDefault)
                {
                    dueColor = Color.Red;
                    dueStr = I18n.Get("uib.31", new { days = loan.DefaultDaysRemaining });
                }
                else
                {
                    // Guard: if daysLeft < 0 but not yet in default, show "overdue" instead of negative number
                    if (daysLeft < 0)
                    {
                        dueColor = Color.Red;
                        dueStr = I18n.Get("uib.31", new { days = 0 });
                    }
                    else
                    {
                        dueColor = daysLeft <= 2 ? Color.DarkOrange : Color.DimGray;
                        dueStr = I18n.Get("uib.32", new { daysLeft, dayOfSeason = ((loan.DueDay - 1) % 28) + 1 });
                    }
                }
                DrawInfoLine(b, dueStr, infoX + 20, loanY + lineH * nextLine, dueColor);
                nextLine++;

                int totalOwed = companyLoans.Sum(l => l.Principal + l.AccumulatedInterest + l.OverdueInterest);
                DrawInfoLine(b, I18n.Get("uib.33", new { amount = totalOwed.ToString("N0") }), infoX + 20, loanY + lineH * nextLine, Color.Red);
            }
            else
            {
                DrawInfoLine(b, I18n.Get("uib.34"), infoX + 20, loanY, Color.DimGray);
            }
        }
        else
        {
            // === Detail view: scrollable individual loan list (max 2 visible, scrollbar) ===
            string detailTitle = companyLoans.Count > 0
                ? I18n.Get("uib.35", new { count = companyLoans.Count })
                : I18n.Get("uib.36");
            DrawInfoLine(b, detailTitle, infoX + 20, loanY, Color.DarkCyan);

            if (companyLoans.Count > 0)
            {
                int listY = loanY + lineH + 4;
                int entryH = _isChinese ? 120 : 145;
                int maxVisible = 2;
                int listW = width - 80;
                int scrollBarX = infoX + listW - 14;
                int trackH = entryH * maxVisible;

                // Clamp scroll
                int maxOffset = Math.Max(0, companyLoans.Count - maxVisible);
                _loanScrollOffset = Math.Clamp(_loanScrollOffset, 0, maxOffset);

                // === Scrollbar track ===
                b.Draw(Game1.staminaRect, new Rectangle(scrollBarX, listY, 10, trackH), Color.Gray * 0.3f);

                // Scrollbar thumb
                if (maxOffset > 0)
                {
                    float thumbRatio = (float)maxVisible / companyLoans.Count;
                    int thumbH = Math.Max(24, (int)(trackH * thumbRatio));
                    int thumbTravel = trackH - thumbH;
                    float scrollRatio = maxOffset > 0 ? (float)_loanScrollOffset / maxOffset : 0;
                    int thumbY = listY + (int)(scrollRatio * thumbTravel);
                    b.Draw(Game1.staminaRect, new Rectangle(scrollBarX, thumbY, 10, thumbH), Color.Gray * 0.6f);
                }
                else
                {
                    b.Draw(Game1.staminaRect, new Rectangle(scrollBarX, listY, 10, trackH), Color.Gray * 0.6f);
                }

                _loanRepayBtns.Clear();

                for (int i = 0; i < maxVisible; i++)
                {
                    int idx = _loanScrollOffset + i;
                    if (idx >= companyLoans.Count) break;

                    var l = companyLoans[idx];
                    int ey = listY + i * entryH;
                    int today = (int)Game1.stats.DaysPlayed;
                    int daysLeft = l.DueDay - today;
                    int entryW = listW - 20;

                    // Entry dark border
                    Color borderColor = l.IsInDefault ? Color.Red : Color.DarkGoldenrod;
                    b.Draw(Game1.staminaRect, new Rectangle(infoX, ey, entryW, entryH - 2), Color.Black * 0.4f);
                    b.Draw(Game1.staminaRect, new Rectangle(infoX, ey, entryW, 2), borderColor);
                    b.Draw(Game1.staminaRect, new Rectangle(infoX, ey + entryH - 4, entryW, 2), borderColor);
                    b.Draw(Game1.staminaRect, new Rectangle(infoX, ey, 2, entryH - 2), borderColor);
                    b.Draw(Game1.staminaRect, new Rectangle(infoX + entryW - 2, ey, 2, entryH - 2), borderColor);

                    // Row 0 (top-right): repay button
                    int btnW = _isChinese ? 50 : 70;
                    int btnH = _isChinese ? 22 : 24;
                    int btnX = infoX + entryW - (_isChinese ? 56 : btnW + 6);
                    int btnY = ey + (_isChinese ? 0 : 4);
                    var repayBtn = new ClickableTextureComponent(
                        new Rectangle(btnX, btnY, btnW, btnH),
                        Game1.mouseCursors, new Rectangle(128, 384, 64, 64), _isChinese ? 0.3f : 0.3f
                    );
                    _loanRepayBtns.Add(repayBtn);
                    b.Draw(Game1.staminaRect, repayBtn.bounds, Color.DarkGoldenrod * 0.5f);
                    b.Draw(Game1.staminaRect, new Rectangle(repayBtn.bounds.X, repayBtn.bounds.Y, repayBtn.bounds.Width, 2), Color.DarkGoldenrod);
                    b.Draw(Game1.staminaRect, new Rectangle(repayBtn.bounds.X, repayBtn.bounds.Y + repayBtn.bounds.Height - 2, repayBtn.bounds.Width, 2), Color.DarkGoldenrod);
                    b.Draw(Game1.staminaRect, new Rectangle(repayBtn.bounds.X, repayBtn.bounds.Y, 2, repayBtn.bounds.Height), Color.DarkGoldenrod);
                    b.Draw(Game1.staminaRect, new Rectangle(repayBtn.bounds.X + repayBtn.bounds.Width - 2, repayBtn.bounds.Y, 2, repayBtn.bounds.Height), Color.DarkGoldenrod);
                    Vector2 rlSize = Game1.smallFont.MeasureString(I18n.Get("uib.37"));
                    Utility.drawTextWithShadow(b, I18n.Get("uib.37"), Game1.smallFont,
                        new Vector2(repayBtn.bounds.X + (btnW - rlSize.X) / 2, repayBtn.bounds.Y + (_isChinese ? 3 : 4)), Color.Gold);

                    // Row 1: loan number + period + due date (below button)
                    Color headerColor;
                    string header;
                    if (l.IsFrozen)
                    {
                        header = I18n.Get("uib.38", new { period = l.RepaymentPeriodDays });
                        headerColor = Color.Red;
                    }
                    else if (l.IsInDefault)
                    {
                        int dd1 = ((l.DueDay - 1) % 28) + 1;
                        header = I18n.Get("uib.39", new { period = l.RepaymentPeriodDays, overdueDays = dd1, graceDays = l.DefaultDaysRemaining });
                        headerColor = Color.Red;
                    }
                    else
                    {
                        int dd2 = ((l.DueDay - 1) % 28) + 1;
                        if (daysLeft < 0)
                        {
                            header = I18n.Get("uib.39", new { period = l.RepaymentPeriodDays, overdueDays = dd2, graceDays = 0 });
                            headerColor = Color.Red;
                        }
                        else
                        {
                            header = I18n.Get("uib.40", new { period = l.RepaymentPeriodDays, overdueDays = dd2, remainingDays = daysLeft });
                            headerColor = daysLeft <= 2 ? Color.Orange : Color.Gold;
                        }
                    }
                    DrawInfoLine(b, header, infoX + 8, ey + (_isChinese ? 6 : btnH + 8), headerColor);

                    // Row 2: principal + rate
                    DrawInfoLine(b, I18n.Get("uib.41", new { principal = l.Principal.ToString("N0"), rate = $"{l.InterestRate * 100:F2}%", days = l.RepaymentPeriodDays }), infoX + 8, ey + (_isChinese ? 28 : btnH + 30), Color.White * 0.8f);

                    // Row 3: accumulated interest
                    DrawInfoLine(b, I18n.Get("uib.42", new { amount = l.AccumulatedInterest.ToString("N0") }), infoX + 8, ey + (_isChinese ? 50 : btnH + 52), Color.OrangeRed);

                    // Row 4: overdue (if any)
                    if (l.OverdueInterest > 0)
                        DrawInfoLine(b, I18n.Get("uib.43", new { amount = l.OverdueInterest.ToString("N0") }), infoX + 8, ey + (_isChinese ? 72 : btnH + 74), Color.Red);

                    // Row 5: total owed
                    int owed = l.Principal + l.AccumulatedInterest + l.OverdueInterest;
                    DrawInfoLine(b, I18n.Get("uib.44", new { amount = owed.ToString("N0") }), infoX + 8, ey + (_isChinese ? 94 : btnH + 96), Color.Red);
                }
            }
        }

        // Row 1: fixed buttons (same for both locales)
        DrawTextButton(b, _depositBtn, I18n.Get("uib.45"), _hoverDeposit, Color.DarkGreen);
        DrawTextButton(b, _withdrawBtn, I18n.Get("uib.46"), _hoverWithdraw, Color.SteelBlue);
        DrawTextButton(b, _borrow7Btn, I18n.Get("uib.47"), _hoverBorrow7, Color.DarkRed);
        DrawTextButton(b, _borrow14Btn, I18n.Get("uib.48"), _hoverBorrow14, Color.DarkRed);

        // Row 2
        bool isDynamic = _account.DynamicCompanies.Any(c => c.CompanyName == CurrentCompany.Name);
        var dyn2 = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == CurrentCompany.Name);

        if (_isChinese)
        {
            // Original layout: unconditional drawing at fixed positions
            DrawTextButton(b, _repayBtn, I18n.Get("uib.37"), _hoverRepay, Color.OrangeRed);
            DrawTextButton(b, _loanDetailBtn, _showLoanDetail ? I18n.Get("uib.49") : I18n.Get("uib.50"), _hoverLoanDetail, Color.DarkCyan);
            if (_services.OnlineShoppingUnlocked && !isDynamic)
                DrawTextButton(b, _onlineShopBtn, I18n.Get("uib.51"), _hoverOnlineShop, Color.DarkCyan);
            if (_config.UseCompoundInterest && isDynamic)
            {
                string cl; Color cc;
                if (!string.IsNullOrEmpty(_account.CompoundActiveCompany) && _account.CompoundActiveCompany == CurrentCompany.Name)
                { cl = I18n.Get("uib.52", new { days = _account.CompoundDaysRemaining }); cc = Color.Gold; }
                else if (_account.CompoundCooldownDays > 0)
                { cl = I18n.Get("uib.53", new { days = _account.CompoundCooldownDays }); cc = Color.Gray; }
                else if (string.IsNullOrEmpty(_account.CompoundActiveCompany))
                { cl = I18n.Get("uib.54"); cc = Color.Gold; }
                else
                { cl = I18n.Get("uib.55"); cc = Color.DimGray; }
                DrawTextButton(b, _compoundBtn, cl, _hoverCompound, cc);
            }
            if (dyn2 is not null && dyn2.Status == CompanyStatus.Dying && dyn2.TotalRescueSharesPurchased < 7)
                DrawTextButton(b, _rescueBtn, I18n.Get("uib.56"), _hoverRescue, Color.Purple);
        }
        else
        {
            // English layout: collect visible conditional buttons, center them
            var row2 = new List<(ClickableTextureComponent btn, string label, Color color)>();
            row2.Add((_repayBtn, I18n.Get("uib.37"), Color.OrangeRed));
            row2.Add((_loanDetailBtn, _showLoanDetail ? I18n.Get("uib.49") : I18n.Get("uib.50"), Color.DarkCyan));
            if (_services.OnlineShoppingUnlocked && !isDynamic)
                row2.Add((_onlineShopBtn, I18n.Get("uib.51"), Color.DarkCyan));
            if (_config.UseCompoundInterest && isDynamic)
            {
                string cl; Color cc;
                if (!string.IsNullOrEmpty(_account.CompoundActiveCompany) && _account.CompoundActiveCompany == CurrentCompany.Name)
                { cl = I18n.Get("uib.52", new { days = _account.CompoundDaysRemaining }); cc = Color.Gold; }
                else if (_account.CompoundCooldownDays > 0)
                { cl = I18n.Get("uib.53", new { days = _account.CompoundCooldownDays }); cc = Color.Gray; }
                else if (string.IsNullOrEmpty(_account.CompoundActiveCompany))
                { cl = I18n.Get("uib.54"); cc = Color.Gold; }
                else
                { cl = I18n.Get("uib.55"); cc = Color.DimGray; }
                row2.Add((_compoundBtn, cl, cc));
            }
            if (dyn2 is not null && dyn2.Status == CompanyStatus.Dying && dyn2.TotalRescueSharesPurchased < 7)
                row2.Add((_rescueBtn, I18n.Get("uib.56"), Color.Purple));

            int btnW2 = _repayBtn.bounds.Width;
            int gap2 = row2.Count > 4 ? 6 : 10;
            int maxRow2Width = width - 20;
            int idealRow2Width = row2.Count * btnW2 + (row2.Count - 1) * gap2;
            if (idealRow2Width > maxRow2Width)
                btnW2 = (maxRow2Width - (row2.Count - 1) * gap2) / row2.Count;
            int row2Total = row2.Count * btnW2 + (row2.Count - 1) * gap2;
            int row2Start = xPositionOnScreen + width / 2 - row2Total / 2;
            for (int i = 0; i < row2.Count; i++)
            {
                var (btn, label, color) = row2[i];
                btn.bounds = new Rectangle(row2Start + i * (btnW2 + gap2), btn.bounds.Y, btnW2, btn.bounds.Height);
            }
            foreach (var (btn, label, color) in row2)
            {
                bool hover = btn == _repayBtn ? _hoverRepay : btn == _loanDetailBtn ? _hoverLoanDetail
                    : btn == _onlineShopBtn ? _hoverOnlineShop : btn == _compoundBtn ? _hoverCompound : _hoverRescue;
                DrawTextButton(b, btn, label, hover, color);
            }
        }

        _closeButton.draw(b);
        drawMouse(b);
    }

    private static void DrawInfoLine(SpriteBatch b, string text, int x, int y, Color color)
    {
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(x, y), color);
    }

    private void DrawTextButton(SpriteBatch b, ClickableTextureComponent btn, string label, bool hover, Color borderColor)
    {
        Color bgColor = hover ? Color.LightGray : Color.White;
        b.Draw(Game1.staminaRect, btn.bounds, bgColor);
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y, btn.bounds.Width, 3), borderColor);
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y + btn.bounds.Height - 3, btn.bounds.Width, 3), borderColor);
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y, 3, btn.bounds.Height), borderColor);
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X + btn.bounds.Width - 3, btn.bounds.Y, 3, btn.bounds.Height), borderColor);

        Vector2 labelSize = Game1.smallFont.MeasureString(label);
        string drawLabel = label;
        if (!_isChinese && labelSize.X > btn.bounds.Width - 8)
        {
            while (drawLabel.Length > 1 && Game1.smallFont.MeasureString(drawLabel + "...").X > btn.bounds.Width - 8)
                drawLabel = drawLabel[..^1];
            drawLabel += "...";
            labelSize = Game1.smallFont.MeasureString(drawLabel);
        }
        float x = btn.bounds.X + (btn.bounds.Width - labelSize.X) / 2;
        float y = btn.bounds.Y + (btn.bounds.Height - labelSize.Y) / 2;
        Utility.drawTextWithShadow(b, drawLabel, Game1.smallFont, new Vector2(x, y), hover ? Color.Black : Game1.textColor);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_closeButton.containsPoint(x, y))
        {
            exitThisMenu();
            return;
        }

        // Tab click — use same dynamic positioning as draw
        int maxVisible = _isChinese ? 4 : 3;
        int tabX = xPositionOnScreen + 30;
        for (int i = _tabScrollOffset; i < Math.Min(_companyList.Count, _tabScrollOffset + maxVisible); i++)
        {
            int tabWidth = (int)Game1.smallFont.MeasureString(DisplayName(_companyList[i])).X + 24;
            var rect = new Rectangle(tabX, yPositionOnScreen + 62 + _contentOffsetY, tabWidth, 30);
            if (rect.Contains(x, y) && i != _selectedCompanyIndex)
            {
                Game1.playSound("smallSelect");
                _selectedCompanyIndex = i;
                return;
            }
            tabX += tabWidth + 8;
        }

        // Tab scroll arrows
        if (_tabLeftArrow.Contains(x, y) && _tabScrollOffset > 0)
        {
            Game1.playSound("smallSelect");
            _tabScrollOffset--;
            return;
        }
        if (_tabRightArrow.Contains(x, y) && _tabScrollOffset < Math.Max(0, _companyList.Count - maxVisible))
        {
            Game1.playSound("smallSelect");
            _tabScrollOffset++;
            return;
        }

        if (_depositBtn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer && !_services.StandaloneMode)
            {
                _services.SendRemoteOperation?.Invoke("Deposit", CurrentCompany.Name, 0, null);
                Game1.chatBox?.addInfoMessage(I18n.Get("uib.57"));
                return;
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            var company = CurrentCompany;
            var acct = GetOrCreateAccount();
            int maxDeposit = Math.Min(Game1.player.Money, Math.Max(0, company.DepositLimit - acct.BaseAmount));
            Game1.activeClickableMenu = new NumberInputMenu(
                I18n.Get("uib.58", new { company = DisplayName(company) }),
                amount =>
                {
                    var player = Game1.player;
                    var acct2 = GetOrCreateAccount();
                    if (player.Money < amount)
                    {
                        Game1.chatBox?.addErrorMessage(I18n.Get("uib.59"));
                        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                        return;
                    }
                    if (acct2.BaseAmount + amount > company.DepositLimit)
                    {
                        Game1.chatBox?.addErrorMessage(I18n.Get("uib.60", new { limit = company.DepositLimit }));
                        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                        return;
                    }
                    player.Money -= amount;
                    acct2.DepositBalance += amount;
                    acct2.BaseAmount += amount;
                    SaveAccount();
                    Game1.chatBox?.addInfoMessage(I18n.Get("uib.61", new { amount = amount.ToString("N0") }));
                    Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                },
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: maxDeposit,
                allButtonLabel: I18n.Get("uib.62")
            );
            return;
        }

        if (_withdrawBtn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.63"));
                return;
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            var company = CurrentCompany;
            var acct = GetOrCreateAccount();
            int maxAllowed = _services.CompanyManager.GetMaxWithdrawal(_account, company);
            int maxWithdraw = Math.Min(acct.DepositBalance, maxAllowed);
            Game1.activeClickableMenu = new NumberInputMenu(
                I18n.Get("uib.64", new { company = DisplayName(company) }),
                amount =>
                {
                    var player = Game1.player;
                    var acct2 = GetOrCreateAccount();
                    if (acct2.DepositBalance < amount)
                    {
                        Game1.chatBox?.addErrorMessage(I18n.Get("uib.65"));
                        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                        return;
                    }
                    int maxWd = _services.CompanyManager.GetMaxWithdrawal(_account, company);
                    if (amount > maxWd)
                    {
                        Game1.chatBox?.addErrorMessage(I18n.Get("uib.66", new { amount = maxWd.ToString("N0") }));
                        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                        return;
                    }
                    acct2.DepositBalance -= amount;
                    acct2.BaseAmount = Math.Max(0, acct2.BaseAmount - amount);
                    // Track asset pool consumption
                    if (company.IsDynamic)
                    {
                        var dyn2 = _account.DynamicCompanies.FirstOrDefault(c =>
                            c.CompanyName == company.Name || c.CompanyName == company.OriginalName);
                        if (dyn2 is not null)
                            dyn2.AssetPoolConsumed += amount;
                    }
                    player.Money += amount;
                    _services.ExemptNextMoneyIncrease = true;
                    SaveAccount();
                    Game1.chatBox?.addInfoMessage(I18n.Get("uib.67", new { amount = amount.ToString("N0") }));
                    Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                },
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: maxWithdraw,
                allButtonLabel: I18n.Get("uib.68")
            );
            return;
        }

        if (_borrow7Btn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.63"));
                return;
            }
            if (_account.IsInBankruptcy)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.69"));
                return;
            }
            if (CurrentCompany.IsDynamic)
            {
                var dyn = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == CurrentCompany.Name);
                if (dyn is not null && (dyn.Status == CompanyStatus.Hungry || dyn.Status == CompanyStatus.Dying))
                {
                    string stateName = dyn.Status == CompanyStatus.Hungry ? I18n.Get("uib.70") : I18n.Get("uib.71");
                    Game1.chatBox?.addErrorMessage(I18n.Get("uib.72", new { status = stateName }));
                    return;
                }
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            int maxBorrow7 = CalcMaxBorrow();
            Game1.activeClickableMenu = new NumberInputMenu(
                I18n.Get("uib.73", new { company = DisplayName(CurrentCompany), days = 7 }),
                amount => ProcessBorrow(amount, 7),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: maxBorrow7,
                allButtonLabel: I18n.Get("uib.74")
            );
            return;
        }

        if (_borrow14Btn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.63"));
                return;
            }
            if (_account.IsInBankruptcy)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.69"));
                return;
            }
            if (CurrentCompany.IsDynamic)
            {
                var dyn14 = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == CurrentCompany.Name);
                if (dyn14 is not null && (dyn14.Status == CompanyStatus.Hungry || dyn14.Status == CompanyStatus.Dying))
                {
                    string stateName14 = dyn14.Status == CompanyStatus.Hungry ? I18n.Get("uib.70") : I18n.Get("uib.71");
                    Game1.chatBox?.addErrorMessage(I18n.Get("uib.75", new { status = stateName14 }));
                    return;
                }
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            int maxBorrow14 = CalcMaxBorrow();
            Game1.activeClickableMenu = new NumberInputMenu(
                I18n.Get("uib.76", new { company = DisplayName(CurrentCompany), days = 14 }),
                amount => ProcessBorrow(amount, 14),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: maxBorrow14,
                allButtonLabel: I18n.Get("uib.74")
            );
            return;
        }

        if (_repayBtn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.63"));
                return;
            }
            var loan = GetCurrentLoan();
            if (loan is null)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.77"));
                return;
            }

            Game1.playSound("bigSelect");
            exitThisMenu();
            int totalOwed = loan.Principal + loan.AccumulatedInterest + loan.OverdueInterest;
            Game1.activeClickableMenu = new NumberInputMenu(
                I18n.Get("uib.78", new { amount = totalOwed }),
                amount => ProcessRepay(amount, loan),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: totalOwed,
                allButtonLabel: I18n.Get("uib.79")
            );
            return;
        }

        if (_loanDetailBtn.containsPoint(x, y))
        {
            Game1.playSound("smallSelect");
            _showLoanDetail = !_showLoanDetail;
            _loanScrollOffset = 0;
            return;
        }

        // Stage 14: online shopping button
        bool isDynamic = _account.DynamicCompanies.Any(c => c.CompanyName == CurrentCompany.Name);
        if (_services.OnlineShoppingUnlocked && !isDynamic && _onlineShopBtn.containsPoint(x, y))
        {
            Game1.playSound("bigSelect");
            var shopList = new List<Response>();
            foreach (var sid in _config.OnlineShopList)
            {
                // CC route: Joja超市已不存在
                if (sid == "Joja" && _services.PierreBoosted)
                    continue;
                string name = sid switch
                {
                    "SeedShop" => I18n.Get("mod.195"), "Blacksmith" => I18n.Get("uib.80"), "Carpenter" => I18n.Get("uib.81"),
                    "Joja" => I18n.Get("mod.191"), "IslandTrade" => I18n.Get("uib.82"), "DesertTrade" => I18n.Get("uib.83"),
                    "Sandy" => I18n.Get("uib.84"), "QiGemShop" => I18n.Get("uib.85"),
                    _ => sid
                };
                string? closed = GetShopClosedReason(sid);
                if (closed != null)
                    name += $" [{closed}]";
                shopList.Add(new Response(sid, name));
            }
            shopList.Add(new Response("Cancel", I18n.Get("uib.86")));
            Game1.currentLocation.createQuestionDialogue(I18n.Get("uib.87"), shopList.ToArray(), (_, answer) =>
            {
                if (answer != "Cancel")
                {
                    string? closed = GetShopClosedReason(answer);
                    if (closed != null)
                    {
                        Game1.chatBox?.addErrorMessage(I18n.Get("uib.88") + (closed.Length > 0 ? $"（{closed}）" : ""));
                        return;
                    }
                    if (_config.EnableDeliveryFee)
                    {
                        int fee = _config.DeliveryFeeFlat > 0 ? _config.DeliveryFeeFlat : 0;
                        if (Game1.player.Money < fee)
                        {
                            Game1.chatBox?.addErrorMessage(I18n.Get("uib.89"));
                            return;
                        }
                        if (fee > 0) Game1.player.Money -= fee;
                    }
                    Utility.TryOpenShopMenu(answer, "BankMod");
                }
            });
            return;
        }

        // Stage 15: compound interest button
        if (_config.UseCompoundInterest && _compoundBtn.containsPoint(x, y))
        {
            if (!string.IsNullOrEmpty(_account.CompoundActiveCompany) && _account.CompoundActiveCompany == CurrentCompany.Name)
            {
                // Toggle off
                _account.CompoundActiveCompany = "";
                _account.CompoundDaysRemaining = 0;
                _accountService.Save(_account);
                Game1.chatBox?.addInfoMessage(I18n.Get("uib.90", new { company = DisplayName(CurrentCompany) }));
            }
            else if (string.IsNullOrEmpty(_account.CompoundActiveCompany) && _account.CompoundCooldownDays <= 0)
            {
                // Toggle on
                _account.CompoundActiveCompany = CurrentCompany.Name;
                _account.CompoundDaysRemaining = _config.CompoundDurationDays;
                _accountService.Save(_account);
                Game1.chatBox?.addInfoMessage(I18n.Get("uib.91", new { company = DisplayName(CurrentCompany), days = _account.CompoundDaysRemaining }));
            }
            else
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.92"));
            }
            return;
        }

        // Stage 9: rescue invest button
        var dynForRescue = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == CurrentCompany.Name);
        if (dynForRescue is not null && dynForRescue.Status == CompanyStatus.Dying
            && dynForRescue.TotalRescueSharesPurchased < 7 && _rescueBtn.containsPoint(x, y))
        {
            int sharePrice = CurrentCompany.LoanLimit / 7;
            if (sharePrice <= 0)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.93"));
                return;
            }
            int remainingDays = 7 - dynForRescue.TotalRescueSharesPurchased;
            int maxAfford = Game1.player.Money / sharePrice;
            int maxShares = Math.Max(1, Math.Min(remainingDays, Math.Max(1, maxAfford)));
            if (maxShares < 1)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.94", new { cost = sharePrice.ToString("N0") }));
                return;
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            Game1.activeClickableMenu = new RescueInvestMenu(
                _account, _config, _services,
                DisplayName(CurrentCompany), sharePrice, maxShares,
                dynForRescue.TotalRescueSharesPurchased,
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex)
            );
            return;
        }

        // Per-loan repay buttons in detail view
        if (_showLoanDetail)
        {
            var companyLoans = _loanService.GetCompanyLoans(_account, CurrentCompany);
            for (int i = 0; i < _loanRepayBtns.Count; i++)
            {
                int loanIdx = _loanScrollOffset + i;
                if (loanIdx >= companyLoans.Count) break;
                if (_loanRepayBtns[i].containsPoint(x, y))
                {
                    var targetLoan = companyLoans[loanIdx];
                    Game1.playSound("bigSelect");
                    exitThisMenu();
                    int owed = targetLoan.Principal + targetLoan.AccumulatedInterest + targetLoan.OverdueInterest;
                    Game1.activeClickableMenu = new NumberInputMenu(
                        I18n.Get("uib.95", new { amount = owed }),
                        amount => ProcessRepay(amount, targetLoan),
                        () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                        maxAmount: owed,
                        allButtonLabel: I18n.Get("uib.79")
                    );
                    return;
                }
            }

            // Scrollbar click in detail view
            int infoX = xPositionOnScreen + 40;
            int infoY = yPositionOnScreen + 90;
            int lineH = 26;
            int lineOffset = GetDepositLineOffset();
            int loanY = infoY + lineH * (lineOffset + 9);
            int listY = loanY + lineH + 4;
            int entryH = _isChinese ? 120 : 145;
            int maxLoanVisible = 2;
            int listW = width - 80;
            int scrollBarX = infoX + listW - 14;
            int trackH = entryH * maxLoanVisible;
            int maxOffset = Math.Max(0, companyLoans.Count - maxLoanVisible);

            if (maxOffset > 0 && x >= scrollBarX && x <= scrollBarX + 10 && y >= listY && y <= listY + trackH)
            {
                // Clicked on scrollbar track — jump toward that position
                float ratio = (float)(y - listY) / trackH;
                int targetOffset = (int)Math.Round(ratio * maxOffset);
                _loanScrollOffset = Math.Clamp(targetOffset, 0, maxOffset);
                Game1.playSound("smallSelect");
                return;
            }
        }
    }

    private void ProcessBorrow(int amount, int repaymentDays)
    {
        var company = CurrentCompany;
        bool success = _loanService.IssueLoan(company, amount, repaymentDays, _account, _config, InterestCalculator);

        if (success)
        {
            _services.ExemptNextMoneyIncrease = true;
            SaveAccount();
            Game1.chatBox?.addInfoMessage(I18n.Get("uib.96", new { amount = amount.ToString("N0"), days = repaymentDays }));
        }
        else
        {
            int companyPrincipal = _account.Loans.Where(l =>
                (l.CompanyName == company.Name || l.CompanyName == company.OriginalName) && !l.IsTransferred).Sum(l => l.Principal);

            // Calculate remaining borrowing capacity
            int companyRemaining = company.LoanLimit - companyPrincipal;

            int totalDeposits = _account.CompanyAccounts.Sum(a => a.DepositBalance);
            int totalPrincipal = _account.Loans.Sum(l => l.Principal);
            int netWorth = totalDeposits + Game1.player.Money - totalPrincipal;
            int leverageLimit = (int)(netWorth * _config.BorrowingLeverageCoefficient);
            int globalLimit = leverageLimit;
            int globalRemaining = globalLimit - totalPrincipal;

            int canBorrow = Math.Min(companyRemaining, globalRemaining);

            string reason;
            if (companyPrincipal + amount > company.LoanLimit)
                reason = I18n.Get("uib.97", new { company = DisplayName(company), loanLimit = company.LoanLimit.ToString("N0"), borrowed = companyPrincipal.ToString("N0"), remaining = companyRemaining.ToString("N0") });
            else if (totalPrincipal + amount > globalLimit)
                reason = I18n.Get("uib.98", new { globalLimit = globalLimit.ToString("N0"), netWorth = netWorth.ToString("N0"), leverage = _config.BorrowingLeverageCoefficient, remaining = globalRemaining.ToString("N0") });
            else
                reason = I18n.Get("uib.99");

            Game1.chatBox?.addErrorMessage(I18n.Get("uib.100", new { reason, canBorrow = canBorrow.ToString("N0") }));
        }

        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
    }

    private void ProcessRepay(int amount, LoanRecord loan)
    {
        int repaid = _loanService.RepayLoan(loan, amount, _account);
        if (repaid > 0)
        {
            // Refresh global debt flags immediately
            _account.IsInInterestDebt = _account.Loans.Any(l => l.IsInInterestDebt);
            _account.IsInPrincipalDebt = _account.Loans.Any(l => l.IsInDefault);

            // Re-check bankruptcy immediately — exit if all overdue debt cleared
            _services.BankruptcyHandler.CheckBankruptcy(_account, _config);

            if (!_account.IsInBankruptcy && _account.BankruptcyWarningShown)
            {
                Game1.chatBox?.addInfoMessage(I18n.Get("uib.101"));
            }

            SaveAccount();

            // Reload to ensure saved state is consistent
            var freshAccount = _accountService.Load();
            bool cleared = loan.Principal <= 0 && loan.AccumulatedInterest <= 0 && loan.OverdueInterest <= 0;
            Game1.chatBox?.addInfoMessage($"{I18n.Get("uib.102", new { amount = repaid.ToString("N0") })}{(cleared ? " " + I18n.Get("uib.103a") : "")}");

            // If no problem loans remain, force-clear all warning flags unconditionally
            bool hasProblems = freshAccount.Loans.Any(l => l.IsFrozen || l.IsInDefault || l.IsInInterestDebt);
            if (!hasProblems)
            {
                freshAccount.IsInBankruptcy = false;
                freshAccount.IsInInterestDebt = false;
                freshAccount.IsInPrincipalDebt = false;
                freshAccount.BankruptcyWarningShown = false;
                _accountService.Save(freshAccount);
            }
            Game1.activeClickableMenu = new BankMenu(freshAccount, _config, _helper, _services, _selectedCompanyIndex);
            return;
        }
        else
        {
            Game1.chatBox?.addErrorMessage(I18n.Get("uib.103"));
        }

        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
    }

    private int CalcMaxBorrow()
    {
        var company = CurrentCompany;
        int companyPrincipal = _account.Loans.Where(l =>
            (l.CompanyName == company.Name || l.CompanyName == company.OriginalName) && !l.IsTransferred).Sum(l => l.Principal);
        int companyRemaining = company.LoanLimit - companyPrincipal;
        int totalDeposits = _account.CompanyAccounts.Sum(a => a.DepositBalance);
        int totalPrincipal = _account.Loans.Sum(l => l.Principal);
        int netWorth = totalDeposits + Game1.player.Money - totalPrincipal;
        int globalLimit = (int)(netWorth * _config.BorrowingLeverageCoefficient);
        int globalRemaining = globalLimit - totalPrincipal;
        return Math.Max(0, Math.Min(companyRemaining, globalRemaining));
    }

    private int GetDepositLineOffset()
    {
        int offset = 0;
        var company = CurrentCompany;
        if (company.IsDynamic)
        {
            var dyn = _account.DynamicCompanies.FirstOrDefault(c =>
                c.CompanyName == company.Name || c.CompanyName == company.OriginalName);
            if (dyn is not null)
            {
                var statusText = dyn.Status switch
                {
                    CompanyStatus.New => "",
                    _ => ""
                };
                if (dyn.Status != CompanyStatus.Bankrupt) offset++; // status line
                var cropData = CropDataProvider.GetByCode(company.CropCode ?? company.Name);
                if (cropData is not null) offset += 2; // fuel + consumption
            }
        }
        return offset;
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (_showLoanDetail)
        {
            var companyLoans = _loanService.GetCompanyLoans(_account, CurrentCompany);
            int maxVisible = 2;
            int maxOffset = Math.Max(0, companyLoans.Count - maxVisible);
            if (direction > 0)
                _loanScrollOffset = Math.Max(0, _loanScrollOffset - 1);
            else if (direction < 0)
                _loanScrollOffset = Math.Min(maxOffset, _loanScrollOffset + 1);
        }
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        exitThisMenu();
    }

    public override void performHoverAction(int x, int y)
    {
        _closeButton.tryHover(x, y);
        _hoverDeposit = _depositBtn.containsPoint(x, y);
        _hoverWithdraw = _withdrawBtn.containsPoint(x, y);
        _hoverBorrow7 = _borrow7Btn.containsPoint(x, y);
        _hoverBorrow14 = _borrow14Btn.containsPoint(x, y);
        _hoverRepay = _repayBtn.containsPoint(x, y);
        _hoverRescue = _rescueBtn.containsPoint(x, y);
        _hoverCompound = _compoundBtn.containsPoint(x, y);
        _hoverOnlineShop = _onlineShopBtn.containsPoint(x, y);
        _hoverLoanDetail = _loanDetailBtn.containsPoint(x, y);
    }

    private string? GetShopClosedReason(string shopId)
    {
        return _services.StoreHoursService.GetClosedReason(shopId);
    }
}
