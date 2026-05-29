using BankMod.Data;
using BankMod.Services.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

internal class RescueInvestMenu : IClickableMenu
{
    private const int WindowWidth = 480;
    private const int WindowHeight = 320;

    private readonly BankAccountData _account;
    private readonly ModConfig _config;
    private readonly ModServices _services;
    private readonly string _companyName;
    private readonly int _sharePrice;
    private readonly int _maxShares;
    private readonly int _alreadyPurchased;
    private int _sharesToBuy = 1;
    private readonly Action _onClose;

    private Rectangle _minusRect;
    private Rectangle _plusRect;
    private Rectangle _confirmRect;
    private Rectangle _cancelRect;

    public RescueInvestMenu(BankAccountData account, ModConfig config, ModServices services,
        string companyName, int sharePrice, int maxShares, int alreadyPurchased, Action onClose)
        : base(
            (Game1.uiViewport.Width - WindowWidth) / 2,
            (Game1.uiViewport.Height - WindowHeight) / 2,
            WindowWidth, WindowHeight)
    {
        _account = account;
        _config = config;
        _services = services;
        _companyName = companyName;
        _sharePrice = sharePrice;
        _maxShares = maxShares;
        _alreadyPurchased = alreadyPurchased;
        _onClose = onClose;
        _sharesToBuy = 1;
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        int centerX = xPositionOnScreen + width / 2;

        string title = I18n.Get("uir.1");
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        Utility.drawTextWithShadow(b, title, Game1.dialogueFont,
            new Vector2(centerX - titleSize.X / 2, yPositionOnScreen + 20), Game1.textColor);

        string sub = I18n.Get("uir.2", new { price = _sharePrice, days = 7, shares = _alreadyPurchased });
        Vector2 subSize = Game1.smallFont.MeasureString(sub);
        Utility.drawTextWithShadow(b, sub, Game1.smallFont,
            new Vector2(centerX - subSize.X / 2, yPositionOnScreen + 56), Color.DarkCyan);

        // +/- controls
        int rowY = yPositionOnScreen + 90;
        int btnW = 48;
        int btnH = 42;

        _minusRect = new Rectangle(centerX - 110, rowY, btnW, btnH);
        _plusRect = new Rectangle(centerX + 62, rowY, btnW, btnH);

        b.Draw(Game1.staminaRect, _minusRect, Color.DarkRed * 0.7f);
        Utility.drawTextWithShadow(b, "−", Game1.dialogueFont,
            new Vector2(_minusRect.X + 18, _minusRect.Y + 6), Color.White);

        b.Draw(Game1.staminaRect, _plusRect, Color.DarkGreen * 0.7f);
        Utility.drawTextWithShadow(b, "+", Game1.dialogueFont,
            new Vector2(_plusRect.X + 16, _plusRect.Y + 6), Color.White);

        string countStr = I18n.Get("uir.3", new { shares = _sharesToBuy });
        Vector2 countSize = Game1.dialogueFont.MeasureString(countStr);
        Utility.drawTextWithShadow(b, countStr, Game1.dialogueFont,
            new Vector2(centerX - countSize.X / 2, rowY + 6), Color.Gold);

        // Total cost
        int totalCost = _sharesToBuy * _sharePrice;
        string costStr = I18n.Get("uir.4", new { cost = totalCost });
        Vector2 costSize = Game1.smallFont.MeasureString(costStr);
        Utility.drawTextWithShadow(b, costStr, Game1.smallFont,
            new Vector2(centerX - costSize.X / 2, rowY + btnH + 14), Color.Yellow);

        // Confirm / Cancel
        int btnY2 = yPositionOnScreen + height - 62;
        _confirmRect = new Rectangle(centerX - 130, btnY2, 120, 40);
        _cancelRect = new Rectangle(centerX + 10, btnY2, 120, 40);

        b.Draw(Game1.staminaRect, _confirmRect, Color.DarkGoldenrod * 0.6f);
        Utility.drawTextWithShadow(b, I18n.Get("uir.5"), Game1.smallFont,
            new Vector2(_confirmRect.X + 44, _confirmRect.Y + 12), Color.White);

        b.Draw(Game1.staminaRect, _cancelRect, Color.Gray * 0.6f);
        Utility.drawTextWithShadow(b, I18n.Get("uib.86"), Game1.smallFont,
            new Vector2(_cancelRect.X + 44, _cancelRect.Y + 12), Color.White);

        drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_cancelRect.Contains(x, y))
        {
            Game1.playSound("smallSelect");
            exitThisMenu();
            _onClose();
            return;
        }
        if (_minusRect.Contains(x, y))
        {
            Game1.playSound("smallSelect");
            _sharesToBuy = Math.Max(1, _sharesToBuy - 1);
            return;
        }
        if (_plusRect.Contains(x, y))
        {
            Game1.playSound("smallSelect");
            _sharesToBuy = Math.Min(_maxShares, _sharesToBuy + 1);
            return;
        }
        if (_confirmRect.Contains(x, y))
        {
            int cost = _sharesToBuy * _sharePrice;
            if (Game1.player.Money < cost)
            {
                Game1.chatBox?.addErrorMessage(I18n.Get("uib.59"));
                return;
            }
            Game1.playSound("bigSelect");
            _services.CompanyManager.RescueInvest(_account, _companyName, cost);
            _services.BankAccountService.Save(_account);
            exitThisMenu();
            _onClose();
        }
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        exitThisMenu();
        _onClose();
    }
}
