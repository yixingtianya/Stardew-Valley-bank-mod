using BankMod.Data;
using BankMod.Services.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

/// <summary>Shows per-company daily interest log for the current season.
/// First view: company list with total interest + detail button.
/// Second view: per-day rate and interest for a selected company.</summary>
internal class InterestLogMenu : IClickableMenu
{
    private const int WindowWidth = 700;
    private const int WindowHeight = 500;

    private readonly BankAccountData _account;
    private readonly ModConfig _config;
    private readonly IModHelper _helper;
    private readonly ModServices _services;

    // Detail mode state
    private string? _detailCompanyName;
    private List<DailyInterestRecord> _detailRecords = new();

    // Scroll
    private int _scrollOffset;
    private int _maxScroll;

    // Buttons — rebuilt each frame
    private readonly List<ClickableTextureComponent> _detailButtons = new();

    // Hover
    private bool _hoverBack;

    // Dynamic layout cache (recalculated each frame for i18n adaptability)
    private int _colCompany;
    private int _colTotal;
    private int _colDetail;
    private int _colDay;
    private int _colRate;
    private int _colInterest;

    public InterestLogMenu(BankAccountData account, ModConfig config, IModHelper helper, ModServices services)
        : base(
            Game1.uiViewport.Width / 2 - WindowWidth / 2,
            Game1.uiViewport.Height / 2 - WindowHeight / 2,
            WindowWidth,
            WindowHeight,
            true
        )
    {
        _account = account;
        _config = config;
        _helper = helper;
        _services = services;
    }

    private ClickableTextureComponent BackButton => new(
        new Rectangle(xPositionOnScreen + 20, yPositionOnScreen + height - 50, 100, 40),
        Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f);

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        if (_detailCompanyName is null)
            DrawCompanyList(b);
        else
            DrawCompanyDetail(b);

        var backBtn = BackButton;
        DrawTextButton(b, backBtn, I18n.Get("uilog.1"), _hoverBack, Color.DarkSlateGray);
        drawMouse(b);
    }

    /// <summary>Calculate column positions dynamically for the company list view based on text widths.</summary>
    private void CalculateListColumns()
    {
        int pad = 40;
        _colCompany = xPositionOnScreen + pad;

        // Measure header text widths to determine column positions
        string totalHeaderText = I18n.Get("uilog.4");
        Vector2 totalHeaderSize = Game1.smallFont.MeasureString(totalHeaderText);
        string detailText = I18n.Get("uilog.5");
        Vector2 detailSize = Game1.smallFont.MeasureString(detailText);

        int detailBtnWidth = Math.Max(80, (int)detailSize.X + 20);
        _colDetail = xPositionOnScreen + width - pad - detailBtnWidth;

        // Total column: right-aligned before detail button
        _colTotal = _colDetail - (int)totalHeaderSize.X - 30;

        // Ensure minimum spacing
        int minCompanyWidth = (int)Game1.smallFont.MeasureString(I18n.Get("uilog.3")).X + 40;
        if (_colTotal - _colCompany < minCompanyWidth + 40)
            _colTotal = _colCompany + minCompanyWidth + 40;
    }

    /// <summary>Calculate column positions dynamically for the company detail view based on text widths.</summary>
    private void CalculateDetailColumns()
    {
        int pad = 40;
        _colDay = xPositionOnScreen + pad;

        // Measure header widths
        string rateHeader = I18n.Get("uilog.9");
        Vector2 rateHeaderSize = Game1.smallFont.MeasureString(rateHeader);
        string interestHeader = I18n.Get("uilog.10");
        Vector2 interestHeaderSize = Game1.smallFont.MeasureString(interestHeader);

        // Distribute remaining space evenly
        int usedWidth = pad;
        int dayColWidth = 120;
        int rateColWidth = (int)rateHeaderSize.X + 60;
        int interestColWidth = (int)interestHeaderSize.X + 80;
        int availableWidth = width - pad * 2;

        // Scale proportionally if too wide
        int totalNeeded = dayColWidth + rateColWidth + interestColWidth;
        if (totalNeeded > availableWidth)
        {
            float scale = (float)availableWidth / totalNeeded;
            dayColWidth = (int)(dayColWidth * scale);
            rateColWidth = (int)(rateColWidth * scale);
            interestColWidth = (int)(interestColWidth * scale);
        }

        _colDay = xPositionOnScreen + pad;
        _colRate = _colDay + dayColWidth;
        _colInterest = _colRate + rateColWidth;
    }

    private void DrawCompanyList(SpriteBatch b)
    {
        CalculateListColumns();

        // Title
        string title = I18n.Get("uilog.2");
        DrawCenteredText(b, title, Game1.dialogueFont, yPositionOnScreen + 25);

        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 30, yPositionOnScreen + 65, width - 60, 2), Color.Gray);

        // Empty state
        var companies = GetCompanySummaries();
        if (companies.Count == 0)
        {
            string emptyMsg = I18n.Get("uilog.14");
            Vector2 emptySize = Game1.smallFont.MeasureString(emptyMsg);
            Utility.drawTextWithShadow(b, emptyMsg, Game1.smallFont,
                new Vector2(xPositionOnScreen + (width - emptySize.X) / 2, yPositionOnScreen + height / 2 - 10), Color.Gray);
            return;
        }

        // Column headers
        int headerY = yPositionOnScreen + 75;
        Utility.drawTextWithShadow(b, I18n.Get("uilog.3"), Game1.smallFont, new Vector2(_colCompany, headerY), Color.DarkSlateGray);
        Utility.drawTextWithShadow(b, I18n.Get("uilog.4"), Game1.smallFont, new Vector2(_colTotal, headerY), Color.DarkSlateGray);

        // Company rows
        _detailButtons.Clear();
        int rowHeight = 36;
        int listTop = yPositionOnScreen + 100;
        int listBottom = yPositionOnScreen + height - 80;
        int visibleRows = (listBottom - listTop) / rowHeight;
        _maxScroll = Math.Max(0, companies.Count - visibleRows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, _maxScroll);

        // Clip region for scrollable area
        b.End();
        var clipRect = new Rectangle(xPositionOnScreen, listTop, width, listBottom - listTop);
        var scissor = Game1.graphics.GraphicsDevice.ScissorRectangle;
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.Default, new RasterizerState { ScissorTestEnable = true });
        Game1.graphics.GraphicsDevice.ScissorRectangle = clipRect;

        string detailLabel = I18n.Get("uilog.5");
        Vector2 detailLabelSize = Game1.smallFont.MeasureString(detailLabel);
        int detailBtnWidth = Math.Max(80, (int)detailLabelSize.X + 20);

        for (int i = _scrollOffset; i < companies.Count; i++)
        {
            var c = companies[i];
            int rowY = listTop + (i - _scrollOffset) * rowHeight;
            if (rowY + rowHeight > listBottom) break;

            // Alternating row background
            if ((i - _scrollOffset) % 2 == 0)
                b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 30, rowY, width - 60, rowHeight - 2), Color.White * 0.08f);

            Utility.drawTextWithShadow(b, c.DisplayName, Game1.smallFont, new Vector2(_colCompany, rowY + 8), Game1.textColor);
            Utility.drawTextWithShadow(b, c.TotalInterest.ToString("N0"), Game1.smallFont, new Vector2(_colTotal, rowY + 8), Game1.textColor);

            // Detail button
            var detailBtn = new ClickableTextureComponent(
                new Rectangle(_colDetail, rowY + 2, detailBtnWidth, 30),
                Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
            );
            _detailButtons.Add(detailBtn);
            DrawTextButton(b, detailBtn, detailLabel, false, Color.DarkGreen);
        }

        Game1.graphics.GraphicsDevice.ScissorRectangle = scissor;
        b.End();
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);

        // Scroll hint
        if (_maxScroll > 0)
        {
            string scrollHint = I18n.Get("uilog.6");
            Utility.drawTextWithShadow(b, scrollHint, Game1.smallFont,
                new Vector2(xPositionOnScreen + width - 140, listBottom + 5), Color.Gray);
        }
    }

    private void DrawCompanyDetail(SpriteBatch b)
    {
        CalculateDetailColumns();

        // Title: company name + season
        string seasonName = GetLocalizedSeason(_account.InterestLogSeason);
        string title = I18n.Get("uilog.7", new { company = GetDisplayName(_detailCompanyName!), season = seasonName });
        DrawCenteredText(b, title, Game1.dialogueFont, yPositionOnScreen + 25);

        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 30, yPositionOnScreen + 65, width - 60, 2), Color.Gray);

        // Column headers
        int headerY = yPositionOnScreen + 75;
        Utility.drawTextWithShadow(b, I18n.Get("uilog.8"), Game1.smallFont, new Vector2(_colDay, headerY), Color.DarkSlateGray);
        Utility.drawTextWithShadow(b, I18n.Get("uilog.9"), Game1.smallFont, new Vector2(_colRate, headerY), Color.DarkSlateGray);
        Utility.drawTextWithShadow(b, I18n.Get("uilog.10"), Game1.smallFont, new Vector2(_colInterest, headerY), Color.DarkSlateGray);

        // Day rows
        int rowHeight = 30;
        int listTop = yPositionOnScreen + 100;
        int listBottom = yPositionOnScreen + height - 80;
        int visibleRows = (listBottom - listTop) / rowHeight;
        _maxScroll = Math.Max(0, _detailRecords.Count - visibleRows);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, _maxScroll);

        b.End();
        var clipRect = new Rectangle(xPositionOnScreen, listTop, width, listBottom - listTop);
        var scissor = Game1.graphics.GraphicsDevice.ScissorRectangle;
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.Default, new RasterizerState { ScissorTestEnable = true });
        Game1.graphics.GraphicsDevice.ScissorRectangle = clipRect;

        for (int i = _scrollOffset; i < _detailRecords.Count; i++)
        {
            var rec = _detailRecords[i];
            int rowY = listTop + (i - _scrollOffset) * rowHeight;
            if (rowY + rowHeight > listBottom) break;

            if ((i - _scrollOffset) % 2 == 0)
                b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 30, rowY, width - 60, rowHeight - 2), Color.White * 0.08f);

            string dayLabel = I18n.Get("uilog.11", new { day = rec.SeasonDay });
            Utility.drawTextWithShadow(b, dayLabel, Game1.smallFont, new Vector2(_colDay, rowY + 5), Game1.textColor);
            Utility.drawTextWithShadow(b, $"{rec.Rate:P2}", Game1.smallFont, new Vector2(_colRate, rowY + 5), Game1.textColor);
            Utility.drawTextWithShadow(b, rec.Interest.ToString("N0"), Game1.smallFont, new Vector2(_colInterest, rowY + 5), Game1.textColor);
        }

        Game1.graphics.GraphicsDevice.ScissorRectangle = scissor;
        b.End();
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);

        // Total line
        int totalInterest = _detailRecords.Sum(r => r.Interest);
        string totalLine = I18n.Get("uilog.12", new { total = totalInterest.ToString("N0") });
        Utility.drawTextWithShadow(b, totalLine, Game1.smallFont,
            new Vector2(xPositionOnScreen + 40, yPositionOnScreen + height - 80), Game1.textColor);
    }

    private record CompanySummary(string CompanyName, string DisplayName, int TotalInterest);

    private List<CompanySummary> GetCompanySummaries()
    {
        var result = new Dictionary<string, CompanySummary>();
        foreach (var rec in _account.SeasonInterestLog)
        {
            if (!result.TryGetValue(rec.CompanyName, out var existing))
                result[rec.CompanyName] = new CompanySummary(rec.CompanyName, GetDisplayName(rec.CompanyName), rec.Interest);
            else
                result[rec.CompanyName] = existing with { TotalInterest = existing.TotalInterest + rec.Interest };
        }
        return result.Values.OrderBy(c => c.CompanyName).ToList();
    }

    private string GetDisplayName(string companyName)
    {
        // Try dynamic company first
        var dc = _account.DynamicCompanies.FirstOrDefault(d => d.CompanyName == companyName);
        if (dc is not null)
        {
            var cropData = CropDataProvider.GetByCode(dc.CropCode);
            if (cropData is not null)
                return cropData.DisplayName + I18n.Get("cmp.6");
        }
        // Try fixed company
        var fc = _config.Companies.FirstOrDefault(c => c.Name == companyName);
        if (fc is not null)
            return _services.RouteService.GetDisplayName(fc);

        return companyName;
    }

    private string GetLocalizedSeason(string seasonKey)
    {
        if (string.IsNullOrEmpty(seasonKey)) return "";
        // seasonKey format: "spring_Year1"
        var parts = seasonKey.Split("_Year");
        if (parts.Length < 2) return seasonKey;
        string rawSeason = parts[0];
        string year = parts[1];
        string localizedSeason = rawSeason switch
        {
            "spring" => I18n.Get("str.31"),
            "summer" => I18n.Get("str.32"),
            "fall" => I18n.Get("str.33"),
            "winter" => I18n.Get("str.34"),
            _ => rawSeason
        };
        return I18n.Get("uilog.13", new { season = localizedSeason, year });
    }

    private void DrawCenteredText(SpriteBatch b, string text, SpriteFont font, float y)
    {
        Vector2 size = font.MeasureString(text);
        Utility.drawTextWithShadow(b, text, font,
            new Vector2(xPositionOnScreen + (width - size.X) / 2, y), Game1.textColor);
    }

    private void DrawTextButton(SpriteBatch b, ClickableTextureComponent btn, string label, bool hover, Color borderColor)
    {
        Color bgColor = hover ? Color.LightGray : Color.White;
        b.Draw(Game1.staminaRect, btn.bounds, bgColor);
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y, btn.bounds.Width, 2), borderColor);
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y + btn.bounds.Height - 2, btn.bounds.Width, 2), borderColor);
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y, 2, btn.bounds.Height), borderColor);
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X + btn.bounds.Width - 2, btn.bounds.Y, 2, btn.bounds.Height), borderColor);

        Vector2 labelSize = Game1.smallFont.MeasureString(label);
        float x = btn.bounds.X + (btn.bounds.Width - labelSize.X) / 2;
        float y = btn.bounds.Y + (btn.bounds.Height - labelSize.Y) / 2;
        Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(x, y), hover ? Color.Black : Game1.textColor);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        var backBtn = BackButton;
        if (backBtn.containsPoint(x, y))
        {
            Game1.playSound("backpackIN");
            if (_detailCompanyName is not null)
            {
                _detailCompanyName = null;
                _detailRecords.Clear();
                _scrollOffset = 0;
            }
            else
            {
                exitThisMenu();
                Game1.activeClickableMenu = new BankChoiceMenu(_services, _config, _helper);
            }
            return;
        }

        if (_detailCompanyName is null)
        {
            var companies = GetCompanySummaries();
            for (int i = 0; i < _detailButtons.Count && i + _scrollOffset < companies.Count; i++)
            {
                if (_detailButtons[i].containsPoint(x, y))
                {
                    Game1.playSound("bigSelect");
                    var c = companies[i + _scrollOffset];
                    _detailCompanyName = c.CompanyName;
                    _detailRecords = _account.SeasonInterestLog
                        .Where(r => r.CompanyName == c.CompanyName)
                        .OrderBy(r => r.Day)
                        .ToList();
                    _scrollOffset = 0;
                    return;
                }
            }
        }
    }

    public override void performHoverAction(int x, int y)
    {
        _hoverBack = BackButton.containsPoint(x, y);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (direction > 0)
            _scrollOffset = Math.Max(0, _scrollOffset - 3);
        else
            _scrollOffset = Math.Min(_maxScroll, _scrollOffset + 3);
    }
}
