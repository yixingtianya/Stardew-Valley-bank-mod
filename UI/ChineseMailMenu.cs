using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

internal class ChineseMailMenu : IClickableMenu
{
    private const float LineSpacing = 40f;
    private const float TitleMarginTop = 24f;
    private const float SenderMarginTop = 30f;
    private const float BodyMarginTop = 50f;

    private readonly string _title;
    private readonly string _sender;
    private readonly List<string> _bodyLines = new();
    private readonly bool _isJoja;

    public ChineseMailMenu(string mailText, string mailId)
        : base(0, 0, 640, 480)
    {
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

        upperRightCloseButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + 12, 36, 36),
            Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);

        _isJoja = mailId == "BankMod.MorrisThanks";

        string processed = mailText.Replace("@", Game1.player?.Name ?? "玩家");
        string[] allLines = processed.Split('^');

        _title = allLines.Length > 0 ? allLines[0] : "";

        int bodyStart = 1;
        var senderParts = new List<string>();
        for (int i = 1; i < allLines.Length && i <= 2; i++)
        {
            if (!string.IsNullOrEmpty(allLines[i])
                && (allLines[i].StartsWith("发件人") || allLines[i].StartsWith("主题")))
            {
                senderParts.Add(allLines[i]);
                bodyStart = i + 1;
            }
        }
        _sender = string.Join("  ", senderParts);

        for (int i = bodyStart; i < allLines.Length; i++)
            _bodyLines.Add(allLines[i]);
    }

    public override void draw(SpriteBatch b)
    {
        if (_isJoja)
            DrawJojaBackground(b, xPositionOnScreen, yPositionOnScreen, width, height);
        else
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        Color textColor = _isJoja ? new Color(30, 30, 30) : Game1.textColor;

        int textAreaTop = yPositionOnScreen + 32;
        float y = textAreaTop + TitleMarginTop;

        if (!string.IsNullOrEmpty(_title))
        {
            Vector2 titleSize = Game1.dialogueFont.MeasureString(_title);
            float titleX = xPositionOnScreen + (width - titleSize.X) / 2;
            b.DrawString(Game1.dialogueFont, _title, new Vector2(titleX, y), textColor,
                0f, Vector2.Zero, 1f, SpriteEffects.None, 0.86f);
            y += titleSize.Y;
        }

        if (!string.IsNullOrEmpty(_sender))
        {
            y += SenderMarginTop;
            Vector2 senderSize = Game1.smallFont.MeasureString(_sender);
            float senderX = xPositionOnScreen + (width - senderSize.X) / 2;
            b.DrawString(Game1.smallFont, _sender, new Vector2(senderX, y), textColor * 0.7f,
                0f, Vector2.Zero, 1f, SpriteEffects.None, 0.86f);
            y += senderSize.Y;
        }

        y += BodyMarginTop;
        foreach (var line in _bodyLines)
            y = DrawLine(b, line, y, textColor);

        base.draw(b);
    }

    private static void DrawJojaBackground(SpriteBatch b, int x, int y, int w, int h)
    {
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 320, 60, 60),
            x, y, w, h, Color.White, 1f, true);
    }

    private float DrawLine(SpriteBatch b, string line, float y, Color color)
    {
        if (string.IsNullOrEmpty(line))
            return y + LineSpacing;

        Vector2 size = Game1.smallFont.MeasureString(line);
        float x = xPositionOnScreen + (width - size.X) / 2;
        b.DrawString(Game1.smallFont, line, new Vector2(x, y), color,
            0f, Vector2.Zero, 1f, SpriteEffects.None, 0.86f);
        return y + LineSpacing;
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        exitThisMenu();
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        exitThisMenu();
    }

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
    }
}
