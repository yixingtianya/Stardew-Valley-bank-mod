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
    private const int WindowWidth = 600;
    private const int WindowHeight = 820;

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
    private Rectangle _tabLeftArrow;
    private Rectangle _tabRightArrow;

    public BankMenu(BankAccountData account, ModConfig config, IModHelper helper, ModServices services, int selectedTab = 0)
        : base(
            (Game1.uiViewport.Width - WindowWidth) / 2,
            (Game1.uiViewport.Height - WindowHeight) / 2,
            WindowWidth,
            WindowHeight
        )
    {
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
            int tabWidth = (int)Game1.smallFont.MeasureString(_companyList[i].Name).X + 24;
            _tabButtons.Add(new ClickableComponent(
                new Rectangle(tabStartX, tabY, tabWidth, tabHeight),
                i.ToString()
            ));
            tabStartX += tabWidth + 8;
        }

        // Buttons: deposit/withdraw on top row, borrow/repay on bottom row
        int centerX = xPositionOnScreen + width / 2;
        int btnY1 = yPositionOnScreen + height - 90;
        int btnY2 = yPositionOnScreen + height - 40;
        int btnW = 130;
        int btnH = 40;
        int gap = 10;

        _depositBtn = new ClickableTextureComponent(
            new Rectangle(centerX - btnW * 2 - gap * 2, btnY1, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
        _withdrawBtn = new ClickableTextureComponent(
            new Rectangle(centerX - btnW - gap, btnY1, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
        _borrow7Btn = new ClickableTextureComponent(
            new Rectangle(centerX + gap, btnY1, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
        _borrow14Btn = new ClickableTextureComponent(
            new Rectangle(centerX + btnW + gap * 2, btnY1, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
        // Bottom row: repay (centered), loan detail toggle
        _repayBtn = new ClickableTextureComponent(
            new Rectangle(centerX - btnW - gap / 2, btnY2, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
        _loanDetailBtn = new ClickableTextureComponent(
            new Rectangle(centerX + gap / 2, btnY2, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
        _rescueBtn = new ClickableTextureComponent(
            new Rectangle(centerX - btnW * 2 - gap * 2, btnY2, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
        _onlineShopBtn = new ClickableTextureComponent(
            new Rectangle(centerX - btnW * 2 - gap * 2, btnY2, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
        _compoundBtn = new ClickableTextureComponent(
            new Rectangle(centerX + btnW + gap, btnY2, btnW, btnH),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
    }

    private CompanyDefinition CurrentCompany => _companyList[_selectedCompanyIndex];

    private CompanyAccount GetOrCreateAccount()
    {
        return _accountService.GetOrCreateAccount(_account, CurrentCompany.Name);
    }

    private LoanRecord? GetCurrentLoan()
    {
        return _loanService.GetLoan(_account, CurrentCompany.Name);
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

        // Stage 8: Warning banner at the top (replaces title area when active)
        string warnText = "";
        if (_account.IsInBankruptcy)
            warnText = "破产保护 - 逾期贷款已冻结(0利息)，借款暂停，出货收入50%强制偿债";
        else if (_account.IsInPrincipalDebt)
            warnText = "贷款逾期 - 宽限期结束后将从存款强制划扣";
        else if (_account.IsInInterestDebt)
            warnText = "利息欠债 - 现金不足支付贷款日息，逾期利息持续累积";

        int titleY = warnText.Length > 0 ? yPositionOnScreen + 18 : yPositionOnScreen + 6;

        if (warnText.Length > 0)
        {
            Vector2 warnSize = Game1.smallFont.MeasureString(warnText);
            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(xPositionOnScreen + 12, yPositionOnScreen + 4, width - 24, (int)warnSize.Y + 8),
                Color.Maroon * 0.4f);
            b.DrawString(Game1.smallFont, warnText,
                new Vector2(xPositionOnScreen + (width - warnSize.X) / 2, yPositionOnScreen + 8),
                Color.Red);
        }

        // Title + author credit on same line (pushed down if warning is active)
        string title = "Dynamic Financial System  ";
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

        // Company tabs (max 4 visible, scrollable, dynamically repositioned)
        int maxVisibleTabs = 4;
        int tabScrollMax = Math.Max(0, _companyList.Count - maxVisibleTabs);
        _tabScrollOffset = Math.Clamp(_tabScrollOffset, 0, tabScrollMax);

        // Left arrow
        if (tabScrollMax > 0)
        {
            _tabLeftArrow = new Rectangle(xPositionOnScreen + 8, yPositionOnScreen + 62, 20, 30);
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

            string name = _companyList[idx].Name;
            int tabWidth = (int)Game1.smallFont.MeasureString(name).X + 24;
            var tabRect = new Rectangle(drawX, yPositionOnScreen + 62, tabWidth, tabHeight);

            bool selected = idx == _selectedCompanyIndex;
            Color bg = selected ? Color.White : Color.LightGray * 0.5f;
            b.Draw(Game1.staminaRect, tabRect, bg);
            b.Draw(Game1.staminaRect, new Rectangle(tabRect.X, tabRect.Y, tabRect.Width, 2), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tabRect.X, tabRect.Y + tabRect.Height - 2, tabRect.Width, 2), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tabRect.X, tabRect.Y, 2, tabRect.Height), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tabRect.X + tabRect.Width - 2, tabRect.Y, 2, tabRect.Height), Color.Gray);

            Vector2 labelSize = Game1.smallFont.MeasureString(name);
            Utility.drawTextWithShadow(b, name, Game1.smallFont,
                new Vector2(drawX + 12, yPositionOnScreen + 62 + (tabHeight - labelSize.Y) / 2),
                selected ? Color.Black : Color.Gray);

            drawX += tabWidth + 8;
        }

        // Right arrow
        if (tabScrollMax > 0)
        {
            _tabRightArrow = new Rectangle(xPositionOnScreen + width - 28, yPositionOnScreen + 62, 20, 30);
            if (_tabScrollOffset < tabScrollMax)
                b.Draw(Game1.mouseCursors, _tabRightArrow, new Rectangle(365, 495, 12, 11), Color.White);
        }

        // === Deposit section ===
        var company = CurrentCompany;
        var acct = GetOrCreateAccount();
        var loan = GetCurrentLoan();
        int infoX = xPositionOnScreen + 40;
        int infoY = dividerY + 48;
        int lineH = 26;

        string titleStr = $"=== {company.Name} ===";
        Utility.drawTextWithShadow(b, titleStr, Game1.smallFont, new Vector2(infoX, infoY), Color.Black);

        int lineOffset = 0;

        // Dynamic company status and fuel display
        if (company.IsDynamic)
        {
            var dyn = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == company.Name);
            if (dyn is not null)
            {
                (string statusText, Color statusColor) = dyn.Status switch
                {
                    CompanyStatus.New => ($"[新公司·保护期剩余 {dyn.ProtectionEndDay - (int)Game1.stats.DaysPlayed} 天]", Color.Cyan),
                    CompanyStatus.Prosperous => ("[繁荣]", Color.Gold),
                    CompanyStatus.Stable => ("[稳定运营]", Color.LimeGreen),
                    CompanyStatus.Hungry => ("[燃料不足·饥饿]", Color.Orange),
                    CompanyStatus.Dying => (dyn.RestructuringDaysRemaining > 0
                        ? $"[重组期·剩 {dyn.RestructuringDaysRemaining} 天]" : "[濒危]", Color.Red),
                    CompanyStatus.Protection => ("[濒死保护期]", Color.Red),
                    _ => ("", Color.Gray)
                };
                // Season transition overlay
                if (dyn.SeasonTransitionDays > 0)
                {
                    statusText = statusText.Length > 0
                        ? statusText.TrimEnd(']') + $" | 季节观望剩{dyn.SeasonTransitionDays}天]"
                        : $"[季节观望·剩 {dyn.SeasonTransitionDays} 天]";
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
                    DrawInfoLine(b, $"燃料库存：{displayFuel:F1} / {smaxDisplay:F0} 个{cropUnit}（够烧 {daysOfFuel:F1} 天）", infoX + 20, infoY + lineH * (lineOffset + 1), fuelColor);
                    lineOffset++;
                    DrawInfoLine(b, $"日消耗：≈{dailyConsumption:F1} 个{cropUnit}/天（基础 {dailyDemand:F1} × {statusCoefficient:F2}）", infoX + 20, infoY + lineH * (lineOffset + 1), Color.Gray);
                    lineOffset++;
                }
            }
        }

        DrawInfoLine(b, $"存款余额：{acct.DepositBalance:N0} g", infoX + 20, infoY + lineH * (lineOffset + 1), Color.DarkGreen);
        DrawInfoLine(b, $"  本金：{acct.BaseAmount:N0} g", infoX + 20, infoY + lineH * (lineOffset + 2), Color.DimGray);
        DrawInfoLine(b, $"存款上限：{company.DepositLimit:N0} g", infoX + 20, infoY + lineH * (lineOffset + 3), Color.DimGray);

        double effectiveDepRate = InterestCalculator.CalculateDepositRate(company, BuildContext());
        string depRateText = $"存款利率（有效）：{effectiveDepRate * 100:F3}%/天";
        Vector2 depRateSize = Game1.smallFont.MeasureString(depRateText);
        _depositRateBounds = new Rectangle(infoX + 20, infoY + lineH * (lineOffset + 4), (int)depRateSize.X, lineH);
        DrawInfoLine(b, depRateText, infoX + 20, infoY + lineH * (lineOffset + 4), Color.DimGray);

        double effectiveLoanRate = InterestCalculator.CalculateLoanRate(company, BuildContext());
        string loanRateText = $"贷款利率（有效）：{effectiveLoanRate * 100:F3}%/天";
        Vector2 loanRateSize = Game1.smallFont.MeasureString(loanRateText);
        _loanRateBounds = new Rectangle(infoX + 20, infoY + lineH * (lineOffset + 5), (int)loanRateSize.X, lineH);
        DrawInfoLine(b, loanRateText, infoX + 20, infoY + lineH * (lineOffset + 5), Color.DimGray);

        DrawInfoLine(b, $"贷款上限：{company.LoanLimit:N0} g", infoX + 20, infoY + lineH * (lineOffset + 6), Color.DimGray);

        if (acct.AccumulatedInterest > 0)
        {
            DrawInfoLine(b, $"累计存款利息：+{acct.AccumulatedInterest:N0} g", infoX + 20, infoY + lineH * (lineOffset + 7), Color.Goldenrod);
        }

        // Hover tooltips for effective rates → show base rate
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();
        if (_depositRateBounds.Contains(mx, my))
            drawHoverText(b, $"基础：{company.DepositInterestRate * 100:F2}%/天", Game1.smallFont);
        if (_loanRateBounds.Contains(mx, my))
            drawHoverText(b, $"基础：{company.LoanInterestRate * 100:F2}%/天", Game1.smallFont);

        // === Loan section ===
        int loanY = infoY + lineH * (lineOffset + 9);
        b.Draw(Game1.staminaRect, new Rectangle(infoX, loanY - 4, width - 80, 2), Color.Gray * 0.5f);

        var companyLoans = _loanService.GetCompanyLoans(_account, CurrentCompany.Name);

        if (!_showLoanDetail)
        {
            // === Summary view: show first loan details (original behavior) ===
            if (loan is not null)
            {
                int today = (int)Game1.stats.DaysPlayed;
                int daysLeft = loan.DueDay - today;

                string summary = companyLoans.Count > 1 ? $"（共 {companyLoans.Count} 笔贷款）" : "";
                DrawInfoLine(b, $"贷款本金：{companyLoans.Sum(l => l.Principal):N0} g {summary}", infoX + 20, loanY, Color.DarkRed);
                DrawInfoLine(b, $"累计未还利息：{companyLoans.Sum(l => l.AccumulatedInterest):N0} g", infoX + 20, loanY + lineH, Color.Red);

                int nextLine = 2;
                int totalOverdue = companyLoans.Sum(l => l.OverdueInterest);
                if (totalOverdue > 0)
                {
                    DrawInfoLine(b, $"逾期欠息：{totalOverdue:N0} g", infoX + 20, loanY + lineH * nextLine, Color.DarkRed);
                    nextLine++;
                }

                DrawInfoLine(b, $"贷款日利率：{loan.InterestRate * 100:F3}%/天", infoX + 20, loanY + lineH * nextLine, Color.DimGray);
                nextLine++;
                DrawInfoLine(b, $"还款周期：{loan.RepaymentPeriodDays} 天", infoX + 20, loanY + lineH * nextLine, Color.DimGray);
                nextLine++;

                Color dueColor;
                string dueStr;
                if (companyLoans.Any(l => l.IsFrozen))
                {
                    dueColor = Color.Red;
                    dueStr = "⚠ 已冻结 — 资产不足，无限期0利息，请尽快还款";
                }
                else if (loan.IsInDefault)
                {
                    dueColor = Color.Red;
                    dueStr = $"逾期中！宽限期剩余 {loan.DefaultDaysRemaining} 天";
                }
                else
                {
                    dueColor = daysLeft <= 2 ? Color.DarkOrange : Color.DimGray;
                    int dueDayOfSeason = ((loan.DueDay - 1) % 28) + 1;
                    dueStr = $"距还款日：{daysLeft} 天 (第 {dueDayOfSeason} 天)";
                }
                DrawInfoLine(b, dueStr, infoX + 20, loanY + lineH * nextLine, dueColor);
                nextLine++;

                int totalOwed = companyLoans.Sum(l => l.Principal + l.AccumulatedInterest + l.OverdueInterest);
                DrawInfoLine(b, $"应还总额：{totalOwed:N0} g", infoX + 20, loanY + lineH * nextLine, Color.Red);
            }
            else
            {
                DrawInfoLine(b, "当前无贷款", infoX + 20, loanY, Color.DimGray);
            }
        }
        else
        {
            // === Detail view: scrollable individual loan list (max 2 visible, scrollbar) ===
            string detailTitle = companyLoans.Count > 0
                ? $"── 贷款明细（共 {companyLoans.Count} 笔）──"
                : "── 贷款明细（无贷款）──";
            DrawInfoLine(b, detailTitle, infoX + 20, loanY, Color.DarkCyan);

            if (companyLoans.Count > 0)
            {
                int listY = loanY + lineH + 4;
                int entryH = 120;
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
                    int btnX = infoX + entryW - 56;
                    int btnY = ey + 4;
                    var repayBtn = new ClickableTextureComponent(
                        new Rectangle(btnX, btnY, 50, 22),
                        Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 0.3f
                    );
                    _loanRepayBtns.Add(repayBtn);
                    b.Draw(Game1.staminaRect, repayBtn.bounds, Color.DarkGoldenrod * 0.5f);
                    b.Draw(Game1.staminaRect, new Rectangle(repayBtn.bounds.X, repayBtn.bounds.Y, repayBtn.bounds.Width, 2), Color.DarkGoldenrod);
                    b.Draw(Game1.staminaRect, new Rectangle(repayBtn.bounds.X, repayBtn.bounds.Y + repayBtn.bounds.Height - 2, repayBtn.bounds.Width, 2), Color.DarkGoldenrod);
                    b.Draw(Game1.staminaRect, new Rectangle(repayBtn.bounds.X, repayBtn.bounds.Y, 2, repayBtn.bounds.Height), Color.DarkGoldenrod);
                    b.Draw(Game1.staminaRect, new Rectangle(repayBtn.bounds.X + repayBtn.bounds.Width - 2, repayBtn.bounds.Y, 2, repayBtn.bounds.Height), Color.DarkGoldenrod);
                    Vector2 rlSize = Game1.smallFont.MeasureString("还款");
                    Utility.drawTextWithShadow(b, "还款", Game1.smallFont,
                        new Vector2(repayBtn.bounds.X + (50 - rlSize.X) / 2, repayBtn.bounds.Y + 3), Color.Gold);

                    // Row 1: loan number + period + due date
                    Color headerColor;
                    string header;
                    if (l.IsFrozen)
                    {
                        header = $"#{idx + 1}  借{l.RepaymentPeriodDays}天  |  ⚠ 已冻结（无限期0利息）";
                        headerColor = Color.Red;
                    }
                    else if (l.IsInDefault)
                    {
                        int dd1 = ((l.DueDay - 1) % 28) + 1;
                        header = $"#{idx + 1}  借{l.RepaymentPeriodDays}天  |  到期第{dd1}天（宽限期剩{l.DefaultDaysRemaining}天）";
                        headerColor = Color.Red;
                    }
                    else
                    {
                        int dd2 = ((l.DueDay - 1) % 28) + 1;
                        header = $"#{idx + 1}  借{l.RepaymentPeriodDays}天  |  到期第{dd2}天（剩{daysLeft}天）";
                        headerColor = daysLeft <= 2 ? Color.Orange : Color.Gold;
                    }
                    DrawInfoLine(b, header, infoX + 8, ey + 6, headerColor);

                    // Row 2: principal + rate
                    DrawInfoLine(b, $"本金 {l.Principal:N0}g  |  日利率 {l.InterestRate * 100:F2}%/天", infoX + 8, ey + 28, Color.White * 0.8f);

                    // Row 3: accumulated interest
                    DrawInfoLine(b, $"累计利息 {l.AccumulatedInterest:N0}g", infoX + 8, ey + 50, Color.OrangeRed);

                    // Row 4: overdue (if any)
                    if (l.OverdueInterest > 0)
                        DrawInfoLine(b, $"逾期欠息 {l.OverdueInterest:N0}g", infoX + 8, ey + 72, Color.Red);

                    // Row 5: total owed
                    int owed = l.Principal + l.AccumulatedInterest + l.OverdueInterest;
                    DrawInfoLine(b, $"应还 {owed:N0}g", infoX + 8, ey + 94, Color.Red);
                }
            }
        }

        // Buttons
        DrawTextButton(b, _depositBtn, "存入", _hoverDeposit, Color.DarkGreen);
        DrawTextButton(b, _withdrawBtn, "取出", _hoverWithdraw, Color.SteelBlue);
        DrawTextButton(b, _borrow7Btn, "借7天", _hoverBorrow7, Color.DarkRed);
        DrawTextButton(b, _borrow14Btn, "借14天", _hoverBorrow14, Color.DarkRed);
        DrawTextButton(b, _repayBtn, "还款", _hoverRepay, Color.OrangeRed);
        DrawTextButton(b, _loanDetailBtn, _showLoanDetail ? "摘要" : "明细", _hoverLoanDetail, Color.DarkCyan);

        // Stage 14: online shopping button
        // Online shopping: only on fixed companies, not dynamic
        bool isDynamic = _account.DynamicCompanies.Any(c => c.CompanyName == CurrentCompany.Name);
        if (_services.OnlineShoppingUnlocked && !isDynamic)
        {
            DrawTextButton(b, _onlineShopBtn, "网购", _hoverOnlineShop, Color.DarkCyan);
        }

        // Stage 15: compound interest button (dynamic companies only)
        if (_config.UseCompoundInterest && isDynamic)
        {
            string compoundLabel;
            Color compoundColor;
            if (!string.IsNullOrEmpty(_account.CompoundActiveCompany) && _account.CompoundActiveCompany == CurrentCompany.Name)
            {
                compoundLabel = $"关闭复利 ({_account.CompoundDaysRemaining}天)";
                compoundColor = Color.Gold;
            }
            else if (_account.CompoundCooldownDays > 0)
            {
                compoundLabel = $"冷却中 ({_account.CompoundCooldownDays}天)";
                compoundColor = Color.Gray;
            }
            else if (string.IsNullOrEmpty(_account.CompoundActiveCompany))
            {
                compoundLabel = "开启复利";
                compoundColor = Color.Gold;
            }
            else
            {
                compoundLabel = "复利已占用";
                compoundColor = Color.DimGray;
            }
            DrawTextButton(b, _compoundBtn, compoundLabel, _hoverCompound, compoundColor);
        }

        // Stage 9: rescue invest button when company is Dying (restructuring)
        var dyn2 = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == CurrentCompany.Name);
        if (dyn2 is not null && dyn2.Status == CompanyStatus.Dying && dyn2.TotalRescueSharesPurchased < 7)
        {
            DrawTextButton(b, _rescueBtn, "入股救市", _hoverRescue, Color.Purple);
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
        float x = btn.bounds.X + (btn.bounds.Width - labelSize.X) / 2;
        float y = btn.bounds.Y + (btn.bounds.Height - labelSize.Y) / 2;
        Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(x, y), hover ? Color.Black : Game1.textColor);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_closeButton.containsPoint(x, y))
        {
            exitThisMenu();
            return;
        }

        // Tab click — use same dynamic positioning as draw
        int tabX = xPositionOnScreen + 30;
        for (int i = _tabScrollOffset; i < Math.Min(_companyList.Count, _tabScrollOffset + 4); i++)
        {
            int tabWidth = (int)Game1.smallFont.MeasureString(_companyList[i].Name).X + 24;
            var rect = new Rectangle(tabX, yPositionOnScreen + 62, tabWidth, 30);
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
        if (_tabRightArrow.Contains(x, y) && _tabScrollOffset < Math.Max(0, _companyList.Count - 4))
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
                Game1.chatBox?.addInfoMessage("多人模式：存款操作已发送至主机处理。");
                return;
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            var company = CurrentCompany;
            var acct = GetOrCreateAccount();
            int maxDeposit = Math.Min(Game1.player.Money, Math.Max(0, company.DepositLimit - acct.BaseAmount));
            Game1.activeClickableMenu = new NumberInputMenu(
                $"请输入存入 {company.Name} 的金额：",
                amount =>
                {
                    var player = Game1.player;
                    var acct2 = GetOrCreateAccount();
                    if (player.Money < amount)
                    {
                        Game1.chatBox?.addErrorMessage("金币不足！");
                        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                        return;
                    }
                    if (acct2.BaseAmount + amount > company.DepositLimit)
                    {
                        Game1.chatBox?.addErrorMessage($"超过存款上限（{company.DepositLimit:N0} g）！");
                        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                        return;
                    }
                    player.Money -= amount;
                    acct2.DepositBalance += amount;
                    acct2.BaseAmount += amount;
                    SaveAccount();
                    Game1.chatBox?.addInfoMessage($"成功存入 {amount:N0} g！");
                    Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                },
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: maxDeposit,
                allButtonLabel: "全部存入"
            );
            return;
        }

        if (_withdrawBtn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer)
            {
                Game1.chatBox?.addErrorMessage("多人模式功能开发中，请由主机操作。");
                return;
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            var company = CurrentCompany;
            var acct = GetOrCreateAccount();
            int maxAllowed = _services.CompanyManager.GetMaxWithdrawal(_account, company);
            int maxWithdraw = Math.Min(acct.DepositBalance, maxAllowed);
            Game1.activeClickableMenu = new NumberInputMenu(
                $"请输入从 {company.Name} 取出的金额：",
                amount =>
                {
                    var player = Game1.player;
                    var acct2 = GetOrCreateAccount();
                    if (acct2.DepositBalance < amount)
                    {
                        Game1.chatBox?.addErrorMessage("存款余额不足！");
                        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                        return;
                    }
                    int maxWd = _services.CompanyManager.GetMaxWithdrawal(_account, company);
                    if (amount > maxWd)
                    {
                        Game1.chatBox?.addErrorMessage($"资产池不足！当前最多可取 {maxWd:N0} g。");
                        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                        return;
                    }
                    acct2.DepositBalance -= amount;
                    acct2.BaseAmount = Math.Max(0, acct2.BaseAmount - amount);
                    // Track asset pool consumption
                    if (company.IsDynamic)
                    {
                        var dyn2 = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == company.Name);
                        if (dyn2 is not null)
                            dyn2.AssetPoolConsumed += amount;
                    }
                    player.Money += amount;
                    _services.ExemptNextMoneyIncrease = true;
                    SaveAccount();
                    Game1.chatBox?.addInfoMessage($"成功取出 {amount:N0} g！");
                    Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
                },
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: maxWithdraw,
                allButtonLabel: "全部取出"
            );
            return;
        }

        if (_borrow7Btn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer)
            {
                Game1.chatBox?.addErrorMessage("多人模式功能开发中，请由主机操作。");
                return;
            }
            if (_account.IsInBankruptcy)
            {
                Game1.chatBox?.addErrorMessage("破产保护期间禁止借款！请先偿还债务。");
                return;
            }
            if (CurrentCompany.IsDynamic)
            {
                var dyn = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == CurrentCompany.Name);
                if (dyn is not null && (dyn.Status == CompanyStatus.Hungry || dyn.Status == CompanyStatus.Dying))
                {
                    string stateName = dyn.Status == CompanyStatus.Hungry ? "饥饿" : "濒死";
                    Game1.chatBox?.addErrorMessage($"{CurrentCompany.Name} 处于{stateName}期，禁止借贷！");
                    return;
                }
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            int maxBorrow7 = CalcMaxBorrow();
            Game1.activeClickableMenu = new NumberInputMenu(
                $"请输入从 {CurrentCompany.Name} 借款金额（7天周期）：",
                amount => ProcessBorrow(amount, 7),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: maxBorrow7,
                allButtonLabel: "最大借款"
            );
            return;
        }

        if (_borrow14Btn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer)
            {
                Game1.chatBox?.addErrorMessage("多人模式功能开发中，请由主机操作。");
                return;
            }
            if (_account.IsInBankruptcy)
            {
                Game1.chatBox?.addErrorMessage("破产保护期间禁止借款！请先偿还债务。");
                return;
            }
            if (CurrentCompany.IsDynamic)
            {
                var dyn14 = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == CurrentCompany.Name);
                if (dyn14 is not null && (dyn14.Status == CompanyStatus.Hungry || dyn14.Status == CompanyStatus.Dying))
                {
                    string stateName14 = dyn14.Status == CompanyStatus.Hungry ? "饥饿" : "濒死";
                    Game1.chatBox?.addErrorMessage($"{CurrentCompany.Name} 处于{stateName14}期，禁止借贷！");
                    return;
                }
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            int maxBorrow14 = CalcMaxBorrow();
            Game1.activeClickableMenu = new NumberInputMenu(
                $"请输入从 {CurrentCompany.Name} 借款金额（14天周期）：",
                amount => ProcessBorrow(amount, 14),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: maxBorrow14,
                allButtonLabel: "最大借款"
            );
            return;
        }

        if (_repayBtn.containsPoint(x, y))
        {
            if (!Context.IsMainPlayer)
            {
                Game1.chatBox?.addErrorMessage("多人模式功能开发中，请由主机操作。");
                return;
            }
            var loan = GetCurrentLoan();
            if (loan is null)
            {
                Game1.chatBox?.addErrorMessage("没有贷款需要还款！");
                return;
            }

            Game1.playSound("bigSelect");
            exitThisMenu();
            int totalOwed = loan.Principal + loan.AccumulatedInterest + loan.OverdueInterest;
            Game1.activeClickableMenu = new NumberInputMenu(
                $"请输入还款金额（应还 {totalOwed:N0} g）：",
                amount => ProcessRepay(amount, loan),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                maxAmount: totalOwed,
                allButtonLabel: "全部还款"
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
                    "SeedShop" => "皮埃尔杂货店", "Blacksmith" => "铁匠铺", "Carpenter" => "木匠铺",
                    "Joja" => "Joja超市", "IslandTrade" => "姜岛商人", "DesertTrade" => "沙漠商人",
                    "Sandy" => "桑迪的绿洲商店", "QiGemShop" => "齐先生商店",
                    _ => sid
                };
                string? closed = GetShopClosedReason(sid);
                if (closed != null)
                    name += $" [{closed}]";
                shopList.Add(new Response(sid, name));
            }
            shopList.Add(new Response("Cancel", "取消"));
            Game1.currentLocation.createQuestionDialogue("选择网购店铺：", shopList.ToArray(), (_, answer) =>
            {
                if (answer != "Cancel")
                {
                    string? closed = GetShopClosedReason(answer);
                    if (closed != null)
                    {
                        Game1.chatBox?.addErrorMessage("该商店未营业" + (closed.Length > 0 ? $"（{closed}）" : ""));
                        return;
                    }
                    if (_config.EnableDeliveryFee)
                    {
                        int fee = _config.DeliveryFeeFlat > 0 ? _config.DeliveryFeeFlat : 0;
                        if (Game1.player.Money < fee)
                        {
                            Game1.chatBox?.addErrorMessage("余额不足，无法支付运费！");
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
                Game1.chatBox?.addInfoMessage($"已关闭 {CurrentCompany.Name} 的复利模式。");
            }
            else if (string.IsNullOrEmpty(_account.CompoundActiveCompany) && _account.CompoundCooldownDays <= 0)
            {
                // Toggle on
                _account.CompoundActiveCompany = CurrentCompany.Name;
                _account.CompoundDaysRemaining = _config.CompoundDurationDays;
                _accountService.Save(_account);
                Game1.chatBox?.addInfoMessage($"已为 {CurrentCompany.Name} 开启复利模式（{_config.CompoundDurationDays} 天）！");
            }
            else
            {
                Game1.chatBox?.addErrorMessage("当前无法开启复利。");
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
                Game1.chatBox?.addErrorMessage("救市基金计算异常。");
                return;
            }
            int remainingDays = 7 - dynForRescue.TotalRescueSharesPurchased;
            int maxAfford = Game1.player.Money / sharePrice;
            int maxShares = Math.Max(1, Math.Min(remainingDays, Math.Max(1, maxAfford)));
            if (maxShares < 1)
            {
                Game1.chatBox?.addErrorMessage($"金币不足！每百股需 {sharePrice:N0} g。");
                return;
            }
            Game1.playSound("bigSelect");
            exitThisMenu();
            Game1.activeClickableMenu = new RescueInvestMenu(
                _account, _config, _services,
                CurrentCompany.Name, sharePrice, maxShares,
                dynForRescue.TotalRescueSharesPurchased,
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex)
            );
            return;
        }

        // Per-loan repay buttons in detail view
        if (_showLoanDetail)
        {
            var companyLoans = _loanService.GetCompanyLoans(_account, CurrentCompany.Name);
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
                        $"请输入还款金额（应还 {owed:N0} g）：",
                        amount => ProcessRepay(amount, targetLoan),
                        () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex),
                        maxAmount: owed,
                        allButtonLabel: "全部还款"
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
            int entryH = 120;
            int maxVisible = 2;
            int listW = width - 80;
            int scrollBarX = infoX + listW - 14;
            int trackH = entryH * maxVisible;
            int maxOffset = Math.Max(0, companyLoans.Count - maxVisible);

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
            Game1.chatBox?.addInfoMessage($"成功借款 {amount:N0} g（{repaymentDays}天周期）！");
        }
        else
        {
            int companyPrincipal = _account.Loans.Where(l => l.CompanyName == company.Name && !l.IsTransferred).Sum(l => l.Principal);

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
                reason = $"超出 {company.Name} 贷款上限（{company.LoanLimit:N0} g，已借 {companyPrincipal:N0} g，公司额度剩余 {companyRemaining:N0} g）";
            else if (totalPrincipal + amount > globalLimit)
                reason = $"超出全局借款上限（{globalLimit:N0} g，净资产 {netWorth:N0} g × {_config.BorrowingLeverageCoefficient:F1}，全局额度剩余 {globalRemaining:N0} g）";
            else
                reason = "借款条件不满足";

            Game1.chatBox?.addErrorMessage($"借款失败！{reason}，当前最多可借 {canBorrow:N0} g。");
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
                Game1.chatBox?.addInfoMessage("所有逾期债务已清偿，破产保护已解除。");
            }

            SaveAccount();

            // Reload to ensure saved state is consistent
            var freshAccount = _accountService.Load();
            bool cleared = loan.Principal <= 0 && loan.AccumulatedInterest <= 0 && loan.OverdueInterest <= 0;
            Game1.chatBox?.addInfoMessage($"成功还款 {repaid:N0} g！{(cleared ? " 该笔贷款已结清。" : "")}");

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
            Game1.chatBox?.addErrorMessage("还款失败，金币不足！");
        }

        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
    }

    private int CalcMaxBorrow()
    {
        var company = CurrentCompany;
        int companyPrincipal = _account.Loans.Where(l => l.CompanyName == company.Name && !l.IsTransferred).Sum(l => l.Principal);
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
            var dyn = _account.DynamicCompanies.FirstOrDefault(c => c.CompanyName == company.Name);
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
            var companyLoans = _loanService.GetCompanyLoans(_account, CurrentCompany.Name);
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
