using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

internal class NumberInputMenu : IClickableMenu
{
    private const int WindowWidth = 500;
    private const int WindowHeight = 250;

    private readonly string _prompt;
    private readonly Action<int> _onConfirm;
    private readonly Action _onCancel;
    private readonly string _confirmLabel;
    private readonly string _cancelLabel;

    private readonly StringBuilder _input = new();
    private readonly ClickableTextureComponent _confirmBtn;
    private readonly ClickableTextureComponent _cancelBtn;
    private readonly ClickableTextureComponent? _allBtn;
    private readonly int _maxAmount;
    private readonly string _allButtonLabel;
    private bool _hoverConfirm;
    private bool _hoverCancel;
    private bool _hoverAll;

    private const int BtnWidth = 100;
    private const int BtnHeight = 44;

    public NumberInputMenu(string prompt, Action<int> onConfirm, Action onCancel,
                           string confirmLabel = "确认", string cancelLabel = "取消",
                           int maxAmount = 0, string allButtonLabel = "")
        : base(
            Game1.uiViewport.Width / 2 - WindowWidth / 2,
            Game1.uiViewport.Height / 2 - WindowHeight / 2,
            WindowWidth,
            WindowHeight,
            true
        )
    {
        _prompt = prompt;
        _onConfirm = onConfirm;
        _onCancel = onCancel;
        _confirmLabel = confirmLabel;
        _cancelLabel = cancelLabel;
        _maxAmount = maxAmount;
        _allButtonLabel = allButtonLabel;

        int centerX = xPositionOnScreen + width / 2;
        int btnY = yPositionOnScreen + height - 80;

        if (!string.IsNullOrEmpty(allButtonLabel))
        {
            _cancelBtn = new ClickableTextureComponent(
                new Rectangle(centerX - BtnWidth / 2 - BtnWidth - 15, btnY, BtnWidth, BtnHeight),
                Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
            );
            _allBtn = new ClickableTextureComponent(
                new Rectangle(centerX - BtnWidth / 2, btnY, BtnWidth, BtnHeight),
                Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
            );
            _confirmBtn = new ClickableTextureComponent(
                new Rectangle(centerX + BtnWidth / 2 + 15, btnY, BtnWidth, BtnHeight),
                Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
            );
        }
        else
        {
            _cancelBtn = new ClickableTextureComponent(
                new Rectangle(centerX - BtnWidth - 15, btnY, BtnWidth, BtnHeight),
                Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
            );
            _confirmBtn = new ClickableTextureComponent(
                new Rectangle(centerX + 15, btnY, BtnWidth, BtnHeight),
                Game1.mouseCursors, new Rectangle(128, 384, 64, 64), 1f
            );
        }
    }

    private static string FormatWithCommas(string numStr)
    {
        if (numStr.Length == 0) return "0";
        var sb = new StringBuilder();
        for (int i = 0; i < numStr.Length; i++)
        {
            if (i > 0 && (numStr.Length - i) % 3 == 0)
                sb.Append(',');
            sb.Append(numStr[i]);
        }
        return sb.ToString();
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        Vector2 promptSize = Game1.smallFont.MeasureString(_prompt);
        Utility.drawTextWithShadow(
            b, _prompt, Game1.smallFont,
            new Vector2(xPositionOnScreen + (width - promptSize.X) / 2, yPositionOnScreen + 30),
            Game1.textColor
        );

        int boxX = xPositionOnScreen + width / 2 - 100;
        int boxY = yPositionOnScreen + 70;
        int boxW = 200;
        int boxH = 40;
        b.Draw(Game1.staminaRect, new Rectangle(boxX, boxY, boxW, boxH), Color.White * 0.2f);
        b.Draw(Game1.staminaRect, new Rectangle(boxX, boxY, boxW, 2), Color.Gray);
        b.Draw(Game1.staminaRect, new Rectangle(boxX, boxY + boxH - 2, boxW, 2), Color.Gray);
        b.Draw(Game1.staminaRect, new Rectangle(boxX, boxY, 2, boxH), Color.Gray);
        b.Draw(Game1.staminaRect, new Rectangle(boxX + boxW - 2, boxY, 2, boxH), Color.Gray);

        string display = FormatWithCommas(_input.Length == 0 ? "0" : _input.ToString());
        Vector2 displaySize = Game1.dialogueFont.MeasureString(display);
        Utility.drawTextWithShadow(
            b, display, Game1.dialogueFont,
            new Vector2(boxX + (boxW - displaySize.X) / 2, boxY + (boxH - displaySize.Y) / 2),
            Game1.textColor
        );

        DrawTextButton(b, _cancelBtn, _cancelLabel, _hoverCancel, Color.DarkGray);
        DrawTextButton(b, _confirmBtn, _confirmLabel, _hoverConfirm, Color.DarkGreen);
        if (_allBtn is not null)
            DrawTextButton(b, _allBtn, _allButtonLabel, _hoverAll, Color.SteelBlue);

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
        if (_confirmBtn.containsPoint(x, y))
            Confirm();
        else if (_cancelBtn.containsPoint(x, y))
            Cancel();
        else if (_allBtn is not null && _allBtn.containsPoint(x, y))
            FillMax();
    }

    private void FillMax()
    {
        if (_maxAmount <= 0)
        {
            Game1.playSound("cancel");
            return;
        }
        _input.Clear();
        _input.Append(_maxAmount.ToString());
        Game1.playSound("smallSelect");
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        Cancel();
    }

    public override void performHoverAction(int x, int y)
    {
        _hoverConfirm = _confirmBtn.containsPoint(x, y);
        _hoverCancel = _cancelBtn.containsPoint(x, y);
        _hoverAll = _allBtn is not null && _allBtn.containsPoint(x, y);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Enter)
            Confirm();
        else if (key == Keys.Escape)
            Cancel();
        else if (key == Keys.Back && _input.Length > 0)
        {
            _input.Remove(_input.Length - 1, 1);
            Game1.playSound("tinyWhip");
        }
        else if (key >= Keys.D0 && key <= Keys.D9 && _input.Length < 9)
        {
            char digit = (char)('0' + (key - Keys.D0));
            if (_input.Length == 0 && digit == '0')
                return;
            _input.Append(digit);
            Game1.playSound("tinyWhip");
        }
        else if (key >= Keys.NumPad0 && key <= Keys.NumPad9 && _input.Length < 9)
        {
            char digit = (char)('0' + (key - Keys.NumPad0));
            if (_input.Length == 0 && digit == '0')
                return;
            _input.Append(digit);
            Game1.playSound("tinyWhip");
        }
    }

    private void Confirm()
    {
        if (_input.Length == 0)
        {
            Game1.playSound("cancel");
            return;
        }
        int amount = int.Parse(_input.ToString());
        if (amount <= 0)
        {
            Game1.playSound("cancel");
            return;
        }
        Game1.playSound("bigSelect");
        exitThisMenu();
        _onConfirm(amount);
    }

    private void Cancel()
    {
        Game1.playSound("backpackIN");
        exitThisMenu();
        _onCancel();
    }
}
