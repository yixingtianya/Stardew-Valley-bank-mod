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
    private const int WindowHeight = 680;

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
    private bool _hoverDeposit;
    private bool _hoverWithdraw;
    private bool _hoverBorrow7;
    private bool _hoverBorrow14;
    private bool _hoverRepay;
    private Rectangle _depositRateBounds;
    private Rectangle _loanRateBounds;

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

        int tabStartX = xPositionOnScreen + 30;
        int tabY = yPositionOnScreen + 62;
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
        int btnY1 = yPositionOnScreen + height - 100;
        int btnY2 = yPositionOnScreen + height - 50;
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
        // Bottom row: repay (centered)
        _repayBtn = new ClickableTextureComponent(
            new Rectangle(centerX - btnW / 2, btnY2, btnW, btnH),
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

        string title = "Dynamic Finance Company System";
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        Utility.drawTextWithShadow(
            b, title, Game1.dialogueFont,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 22),
            Game1.textColor
        );

        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 30, yPositionOnScreen + 52, width - 60, 2), Color.Gray);

        // Company tabs
        int tabIndex = 0;
        foreach (var tab in _tabButtons)
        {
            bool selected = tabIndex == _selectedCompanyIndex;
            Color bg = selected ? Color.White : Color.LightGray * 0.5f;
            b.Draw(Game1.staminaRect, tab.bounds, bg);
            b.Draw(Game1.staminaRect, new Rectangle(tab.bounds.X, tab.bounds.Y, tab.bounds.Width, 2), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tab.bounds.X, tab.bounds.Y + tab.bounds.Height - 2, tab.bounds.Width, 2), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tab.bounds.X, tab.bounds.Y, 2, tab.bounds.Height), Color.Gray);
            b.Draw(Game1.staminaRect, new Rectangle(tab.bounds.X + tab.bounds.Width - 2, tab.bounds.Y, 2, tab.bounds.Height), Color.Gray);

            Vector2 labelSize = Game1.smallFont.MeasureString(_companyList[tabIndex].Name);
            float lx = tab.bounds.X + (tab.bounds.Width - labelSize.X) / 2;
            float ly = tab.bounds.Y + (tab.bounds.Height - labelSize.Y) / 2;
            Utility.drawTextWithShadow(b, _companyList[tabIndex].Name, Game1.smallFont,
                new Vector2(lx, ly), selected ? Color.Black : Color.Gray);
            tabIndex++;
        }

        // === Deposit section ===
        var company = CurrentCompany;
        var acct = GetOrCreateAccount();
        var loan = GetCurrentLoan();
        int infoX = xPositionOnScreen + 40;
        int infoY = yPositionOnScreen + 105;
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
                    CompanyStatus.New => ($"[新公司·保护期剩余 {dyn.ProtectionEndDay - (int)Game1.stats.DaysPlayed} 天]", Color.DarkCyan),
                    CompanyStatus.Prosperous => ("[繁荣]", Color.Gold),
                    CompanyStatus.Stable => ("[稳定运营]", Color.DarkGreen),
                    CompanyStatus.Hungry => ("[燃料不足·饥饿]", Color.DarkOrange),
                    CompanyStatus.Dying => ("[濒危]", Color.DarkRed),
                    CompanyStatus.Protection => ("[濒死保护期]", Color.DarkRed),
                    _ => ("", Color.Gray)
                };
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
                    double smaxDisplay = cropData.Smax;
                    double fuelRatio = smaxDisplay > 0 ? displayFuel / smaxDisplay * 100 : 0;
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

                    Color fuelColor = fuelRatio > 60 ? Color.DarkGreen : fuelRatio > 30 ? Color.DarkOrange : Color.DarkRed;
                    DrawInfoLine(b, $"燃料库存：{displayFuel:F1} / {smaxDisplay:F0} ({fuelRatio:F0}%)", infoX + 20, infoY + lineH * (lineOffset + 1), fuelColor);
                    lineOffset++;
                    string cropUnit = cropData.DisplayName;
                    DrawInfoLine(b, $"日消耗：≈{dailyConsumption:F1} 个{cropUnit}/天", infoX + 20, infoY + lineH * (lineOffset + 1), Color.DimGray);
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

        if (loan is not null)
        {
            int today = (int)Game1.stats.DaysPlayed;
            int daysLeft = loan.DueDay - today;

            DrawInfoLine(b, $"贷款本金：{loan.Principal:N0} g", infoX + 20, loanY, Color.DarkRed);
            DrawInfoLine(b, $"累计未还利息：{loan.AccumulatedInterest:N0} g", infoX + 20, loanY + lineH, Color.Red);
            DrawInfoLine(b, $"贷款日利率：{loan.InterestRate * 100:F3}%/天", infoX + 20, loanY + lineH * 2, Color.DimGray);
            DrawInfoLine(b, $"还款周期：{loan.RepaymentPeriodDays} 天", infoX + 20, loanY + lineH * 3, Color.DimGray);

            Color dueColor = loan.IsInDefault ? Color.Red : (daysLeft <= 2 ? Color.DarkOrange : Color.DimGray);
            string dueStr = loan.IsInDefault
                ? $"逾期中！宽限期剩余 {loan.DefaultDaysRemaining} 天"
                : $"距还款日：{daysLeft} 天 (第 {loan.DueDay} 天)";
            DrawInfoLine(b, dueStr, infoX + 20, loanY + lineH * 4, dueColor);

            int totalOwed = loan.Principal + loan.AccumulatedInterest;
            DrawInfoLine(b, $"应还总额：{totalOwed:N0} g", infoX + 20, loanY + lineH * 5, Color.OrangeRed);
        }
        else
        {
            DrawInfoLine(b, "当前无贷款", infoX + 20, loanY, Color.DimGray);
        }

        // Buttons
        DrawTextButton(b, _depositBtn, "存入", _hoverDeposit, Color.DarkGreen);
        DrawTextButton(b, _withdrawBtn, "取出", _hoverWithdraw, Color.SteelBlue);
        DrawTextButton(b, _borrow7Btn, "借7天", _hoverBorrow7, Color.DarkRed);
        DrawTextButton(b, _borrow14Btn, "借14天", _hoverBorrow14, Color.DarkRed);
        DrawTextButton(b, _repayBtn, "还款", _hoverRepay, Color.OrangeRed);

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

        for (int i = 0; i < _tabButtons.Count; i++)
        {
            if (_tabButtons[i].containsPoint(x, y) && i != _selectedCompanyIndex)
            {
                Game1.playSound("smallSelect");
                _selectedCompanyIndex = i;
                return;
            }
        }

        if (_depositBtn.containsPoint(x, y))
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
                    player.Money += amount;
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
            Game1.playSound("bigSelect");
            exitThisMenu();
            Game1.activeClickableMenu = new NumberInputMenu(
                $"请输入从 {CurrentCompany.Name} 借款金额（7天周期）：",
                amount => ProcessBorrow(amount, 7),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex)
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
            Game1.playSound("bigSelect");
            exitThisMenu();
            Game1.activeClickableMenu = new NumberInputMenu(
                $"请输入从 {CurrentCompany.Name} 借款金额（14天周期）：",
                amount => ProcessBorrow(amount, 14),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex)
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
            int totalOwed = loan.Principal + loan.AccumulatedInterest;
            Game1.activeClickableMenu = new NumberInputMenu(
                $"请输入还款金额（应还 {totalOwed:N0} g）：",
                amount => ProcessRepay(amount),
                () => Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex)
            );
            return;
        }
    }

    private void ProcessBorrow(int amount, int repaymentDays)
    {
        var company = CurrentCompany;
        bool success = _loanService.IssueLoan(company, amount, repaymentDays, _account, _config, InterestCalculator);

        if (success)
        {
            SaveAccount();
            Game1.chatBox?.addInfoMessage($"成功借款 {amount:N0} g（{repaymentDays}天周期）！");
        }
        else
        {
            var existing = GetCurrentLoan();
            int currentPrincipal = existing?.Principal ?? 0;

            // Calculate remaining borrowing capacity
            int companyRemaining = company.LoanLimit - currentPrincipal;

            int totalDeposits = _account.CompanyAccounts.Sum(a => a.DepositBalance);
            int totalLoans = _account.Loans.Sum(l => l.Principal);
            int netWorth = totalDeposits + Game1.player.Money - totalLoans;
            int leverageLimit = (int)(netWorth * _config.BorrowingLeverageCoefficient);
            int globalLimit = Math.Min(leverageLimit, _config.BorrowingHardCap);
            int globalRemaining = globalLimit - totalLoans;

            int canBorrow = Math.Min(companyRemaining, globalRemaining);

            string reason;
            if (currentPrincipal + amount > company.LoanLimit)
                reason = $"超出 {company.Name} 贷款上限（{company.LoanLimit:N0} g，已借 {currentPrincipal:N0} g，公司额度剩余 {companyRemaining:N0} g）";
            else if (totalLoans + amount > globalLimit)
                reason = $"超出全局借款上限（{globalLimit:N0} g，净资产 {netWorth:N0} g × {_config.BorrowingLeverageCoefficient:F1}，全局额度剩余 {globalRemaining:N0} g）";
            else
                reason = "借款条件不满足";

            Game1.chatBox?.addErrorMessage($"借款失败！{reason}，当前最多可借 {canBorrow:N0} g。");
        }

        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
    }

    private void ProcessRepay(int amount)
    {
        var loan = GetCurrentLoan();
        if (loan is null)
        {
            Game1.chatBox?.addErrorMessage("没有贷款需要还款！");
            Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
            return;
        }

        int repaid = _loanService.RepayLoan(loan, amount, _account);
        if (repaid > 0)
        {
            SaveAccount();
            Game1.chatBox?.addInfoMessage($"成功还款 {repaid:N0} g！{(loan.Principal <= 0 && loan.AccumulatedInterest <= 0 ? " 贷款已结清。" : "")}");
        }
        else
        {
            Game1.chatBox?.addErrorMessage("还款失败，金币不足！");
        }

        Game1.activeClickableMenu = new BankMenu(_account, _config, _helper, _services, _selectedCompanyIndex);
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
    }
}
