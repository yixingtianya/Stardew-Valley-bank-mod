using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

internal class ChineseMailMenu : IClickableMenu
{
    private const float LineSpacing = 32f;
    private const float TextLeftMargin = 72f;
    private const float TextTopMargin = 64f;

    private static Texture2D? _letterBg;

    private readonly string _title;
    private readonly string _sender;
    private readonly List<string> _bodyLines = new();
    private readonly bool _isJoja;

    public ChineseMailMenu(string mailText, string mailId)
        : base(0, 0, 640, 360)
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
        // 1. Dark overlay — vanilla LetterViewerMenu style
        b.Draw(Game1.fadeToBlackRect,
            Game1.graphics.GraphicsDevice.Viewport.Bounds,
            Color.Black * 0.75f);

        // 2. Background — Joja or vanilla letter
        if (_isJoja)
        {
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 320, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, true);
        }
        else
        {
            // Vanilla letter: letterBG texture at scale 2 (320*2=640, 180*2=360)
            _letterBg ??= Game1.temporaryContent.Load<Texture2D>("LooseSprites\\letterBG");
            b.Draw(_letterBg,
                new Vector2(xPositionOnScreen, yPositionOnScreen),
                new Rectangle(0, 0, 320, 180),
                Color.White, 0f, Vector2.Zero, 2f, SpriteEffects.None, 0.86f);

            // Wax seal decoration at top center — vanilla letter style
            b.Draw(Game1.mouseCursors,
                new Vector2(xPositionOnScreen + width / 2 - 40, yPositionOnScreen - 40),
                new Rectangle(578, 1782, 46, 44),
                Color.White, 0f, Vector2.Zero, 2f, SpriteEffects.None, 0.88f);
        }

        // 3. Draw text
        Color textColor = _isJoja ? new Color(30, 30, 30) : Game1.textColor;
        float y = yPositionOnScreen + TextTopMargin;

        if (!string.IsNullOrEmpty(_title))
        {
            Vector2 titleSize = Game1.dialogueFont.MeasureString(_title);
            float titleX = xPositionOnScreen + (width - titleSize.X) / 2;
            b.DrawString(Game1.dialogueFont, _title, new Vector2(titleX, y), textColor,
                0f, Vector2.Zero, 1f, SpriteEffects.None, 0.86f);
            y += titleSize.Y + 8f;
        }

        if (!string.IsNullOrEmpty(_sender))
        {
            Vector2 senderSize = Game1.smallFont.MeasureString(_sender);
            float senderX = xPositionOnScreen + (width - senderSize.X) / 2;
            b.DrawString(Game1.smallFont, _sender, new Vector2(senderX, y), textColor * 0.7f,
                0f, Vector2.Zero, 1f, SpriteEffects.None, 0.86f);
            y += senderSize.Y + 12f;
        }

        // Body text — left-aligned (vanilla style)
        foreach (var line in _bodyLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                y += LineSpacing;
                continue;
            }
            b.DrawString(Game1.smallFont, line,
                new Vector2(xPositionOnScreen + TextLeftMargin, y), textColor,
                0f, Vector2.Zero, 1f, SpriteEffects.None, 0.86f);
            y += LineSpacing;
        }

        base.draw(b);
        drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        exitThisMenu();
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        exitThisMenu();
    }
}
