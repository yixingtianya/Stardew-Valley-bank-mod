using BankMod.Data;
using BankMod.Services.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

internal class BankChoiceMenu : IClickableMenu
{
    private const int WindowWidth = 600;
    private const int WindowHeight = 380;

    private readonly ModServices _services;
    private readonly ModConfig _config;
    private readonly IModHelper _helper;
    private readonly ClickableTextureComponent _enterButton;
    private readonly ClickableTextureComponent _interestLogButton;
    private readonly ClickableTextureComponent _laterButton;
    private bool _hoverEnter;
    private bool _hoverInterestLog;
    private bool _hoverLater;

    public BankChoiceMenu(ModServices services, ModConfig config, IModHelper helper)
        : base(
            Game1.uiViewport.Width / 2 - WindowWidth / 2,
            Game1.uiViewport.Height / 2 - WindowHeight / 2,
            WindowWidth,
            WindowHeight,
            true
        )
    {
        _services = services;
        _config = config;
        _helper = helper;

        // Calculate button widths dynamically based on label text
        string interestLogLabel = I18n.Get("uic.6");
        string enterLabel = I18n_Phone_Choice_Enter();
        string laterLabel = I18n_Phone_Choice_Later();

        Vector2 interestLogSize = Game1.smallFont.MeasureString(interestLogLabel);
        Vector2 enterSize = Game1.smallFont.MeasureString(enterLabel);
        Vector2 laterSize = Game1.smallFont.MeasureString(laterLabel);

        int minWidth = 120;
        int padding = 30; // horizontal padding inside button
        int btnWidth = Math.Max(minWidth, (int)Math.Max(interestLogSize.X + padding, Math.Max(enterSize.X + padding, laterSize.X + padding)));
        int btnHeight = 44;
        int gap = 15;

        int centerX = xPositionOnScreen + width / 2;
        int btnY = yPositionOnScreen + height - 90;

        // Three buttons: Interest Log | Enter Bank | Maybe Later
        int totalWidth = btnWidth * 3 + gap * 2;
        int startX = centerX - totalWidth / 2;

        _interestLogButton = new ClickableTextureComponent(
            new Rectangle(startX, btnY, btnWidth, btnHeight),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );

        _enterButton = new ClickableTextureComponent(
            new Rectangle(startX + btnWidth + gap, btnY, btnWidth, btnHeight),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );

        _laterButton = new ClickableTextureComponent(
            new Rectangle(startX + (btnWidth + gap) * 2, btnY, btnWidth, btnHeight),
            Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
        );
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        string title = I18n.Get("uic.1");
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        Utility.drawTextWithShadow(
            b, title, Game1.dialogueFont,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 25),
            Game1.textColor
        );

        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 40, yPositionOnScreen + 70, width - 80, 2), Color.Gray);

        string welcome = I18n_Phone_Dialogue_Welcome();
        Vector2 welcomeSize = Game1.smallFont.MeasureString(welcome);
        Utility.drawTextWithShadow(
            b, welcome, Game1.smallFont,
            new Vector2(xPositionOnScreen + (width - welcomeSize.X) / 2, yPositionOnScreen + 85),
            Game1.textColor
        );

        string prompt = I18n_Phone_Dialogue_Choice();
        Vector2 promptSize = Game1.smallFont.MeasureString(prompt);
        Utility.drawTextWithShadow(
            b, prompt, Game1.smallFont,
            new Vector2(xPositionOnScreen + (width - promptSize.X) / 2, yPositionOnScreen + 130),
            Game1.textColor
        );

        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 80, yPositionOnScreen + 165, width - 160, 100), Color.White * 0.1f);

        string[] services = { I18n.Get("uic.2"), I18n.Get("uic.3"), I18n.Get("uic.4"), I18n.Get("uic.5") };
        for (int i = 0; i < services.Length; i++)
        {
            Utility.drawTextWithShadow(
                b, $"• {services[i]}", Game1.smallFont,
                new Vector2(xPositionOnScreen + 120, yPositionOnScreen + 175 + i * 24),
                Color.DarkSlateGray
            );
        }

        DrawTextButton(b, _interestLogButton, I18n.Get("uic.6"), _hoverInterestLog, Color.DarkBlue);
        DrawTextButton(b, _enterButton, I18n_Phone_Choice_Enter(), _hoverEnter, Color.DarkGreen);
        DrawTextButton(b, _laterButton, I18n_Phone_Choice_Later(), _hoverLater, Color.DarkGray);

        drawMouse(b);
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
        if (_interestLogButton.containsPoint(x, y))
        {
            Game1.playSound("bigSelect");
            exitThisMenu();
            var account = _services.BankAccountService.Load();
            Game1.activeClickableMenu = new InterestLogMenu(account, _config, _helper, _services);
        }
        else if (_enterButton.containsPoint(x, y))
        {
            Game1.playSound("bigSelect");
            exitThisMenu();
            var account = _services.BankAccountService.Load();
            Game1.activeClickableMenu = new BankMenu(account, _config, _helper, _services);
        }
        else if (_laterButton.containsPoint(x, y))
        {
            Game1.playSound("backpackIN");
            exitThisMenu();
        }
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        exitThisMenu();
    }

    public override void performHoverAction(int x, int y)
    {
        _hoverInterestLog = _interestLogButton.containsPoint(x, y);
        _hoverEnter = _enterButton.containsPoint(x, y);
        _hoverLater = _laterButton.containsPoint(x, y);
    }

    private static string I18n_Phone_Dialogue_Welcome() => I18n.Phone_Dialogue_Welcome();
    private static string I18n_Phone_Dialogue_Choice() => I18n.Phone_Dialogue_Choice();
    private static string I18n_Phone_Choice_Enter() => I18n.Phone_Choice_Enter();
    private static string I18n_Phone_Choice_Later() => I18n.Phone_Choice_Later();
}
