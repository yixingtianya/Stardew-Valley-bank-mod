using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

/// <summary>
/// 自定义邮箱弹窗 - 显示欢迎邮件和手机附件
/// </summary>
public class MailboxDialog : IClickableMenu
{
    private const int WindowWidth = 600;
    private const int WindowHeight = 500;

    private readonly ClickableTextureComponent _receiveButton;
    private readonly ClickableTextureComponent _closeButton;
    private bool _hoverReceive;
    private bool _hoverClose;
    
    // 按钮样式
    private const int BtnWidth = 140;
    private const int BtnHeight = 44;

    public bool PhoneReceived { get; private set; } = false;

    public MailboxDialog()
        : base(
            Game1.uiViewport.Width / 2 - WindowWidth / 2,
            Game1.uiViewport.Height / 2 - WindowHeight / 2,
            WindowWidth,
            WindowHeight,
            true
        )
    {
        int centerX = xPositionOnScreen + width / 2;
        int btnY = yPositionOnScreen + height - 80;

        // 领取按钮 - 和BankChoiceMenu一致的样式
        _receiveButton = new ClickableTextureComponent(
            new Rectangle(centerX - BtnWidth / 2, btnY, BtnWidth, BtnHeight),
            Game1.mouseCursors,
            new Rectangle(0, 0, 1, 1),
            1f
        )
        {
            hoverText = ""
        };

        // 关闭按钮
        _closeButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 10, 40, 40),
            Game1.mouseCursors,
            new Rectangle(337, 494, 12, 12),
            3f
        )
        {
            hoverText = I18n.Get("uimb.1")
        };
    }

    /// <inheritdoc />
    public override void draw(SpriteBatch b)
    {
        // 背景遮罩
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

        // 对话框面板
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // 标题
        string title = I18n.Get("uimb.2");
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        Utility.drawTextWithShadow(
            b, title, Game1.dialogueFont,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 25),
            Game1.textColor
        );

        // 发件人
        string from = I18n.Get("uimb.3");
        Utility.drawTextWithShadow(
            b, from, Game1.smallFont,
            new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 75),
            Game1.textColor
        );

        // 主题
        string subject = I18n.Get("uimb.4");
        Utility.drawTextWithShadow(
            b, subject, Game1.smallFont,
            new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 100),
            Game1.textColor
        );

        // 分隔线
        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 30, yPositionOnScreen + 130, width - 60, 2), Color.Gray);

        // 正文
        string[] bodyLines = new[]
        {
            I18n.Get("uimb.5"),
            "",
            I18n.Get("uimb.6"),
            "",
            I18n.Get("uimb.7"),
            I18n.Get("uimb.8"),
            I18n.Get("uimb.9"),
            I18n.Get("uimb.10"),
            "",
            I18n.Get("uimb.11"),
            "",
            I18n.Get("uimb.12")
        };

        for (int i = 0; i < bodyLines.Length; i++)
        {
            Utility.drawTextWithShadow(
                b, bodyLines[i], Game1.smallFont,
                new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 145 + i * 22),
                Game1.textColor
            );
        }

        // 手机图标区域
        int phoneX = xPositionOnScreen + width - 150;
        int phoneY = yPositionOnScreen + 300;
        
        // 绘制手机物品
        DrawPhoneItem(b, phoneX, phoneY);

        // 附件说明
        string attachText = I18n.Get("uimb.13");
        Vector2 attachSize = Game1.smallFont.MeasureString(attachText);
        Utility.drawTextWithShadow(
            b, attachText, Game1.smallFont,
            new Vector2(phoneX + 50 - attachSize.X / 2, phoneY + 80),
            Color.DarkGray
        );

        // 领取按钮 - 使用文本按钮样式
        DrawTextButton(b, _receiveButton, I18n.Get("uimb.14"), _hoverReceive, Color.DarkGreen);

        // 关闭按钮
        _closeButton.draw(b);

        drawMouse(b);
    }
    
    /// <summary>绘制文本按钮</summary>
    private void DrawTextButton(SpriteBatch b, ClickableTextureComponent btn, string label, bool hover, Color borderColor)
    {
        // 按钮背景
        Color bgColor = hover ? Color.LightGray : Color.White;
        b.Draw(Game1.staminaRect, btn.bounds, bgColor);
        
        // 按钮边框
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y, btn.bounds.Width, 3), borderColor); // 顶部
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y + btn.bounds.Height - 3, btn.bounds.Width, 3), borderColor); // 底部
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X, btn.bounds.Y, 3, btn.bounds.Height), borderColor); // 左边
        b.Draw(Game1.staminaRect, new Rectangle(btn.bounds.X + btn.bounds.Width - 3, btn.bounds.Y, 3, btn.bounds.Height), borderColor); // 右边

        // 按钮文字
        Vector2 labelSize = Game1.smallFont.MeasureString(label);
        float x = btn.bounds.X + (btn.bounds.Width - labelSize.X) / 2;
        float y = btn.bounds.Y + (btn.bounds.Height - labelSize.Y) / 2;
        
        Utility.drawTextWithShadow(
            b, label, Game1.smallFont,
            new Vector2(x, y),
            hover ? Color.Black : Game1.textColor
        );
    }

    /// <summary>绘制手机物品图标</summary>
    private void DrawPhoneItem(SpriteBatch b, int x, int y)
    {
        // 绘制手机背景框
        b.Draw(Game1.staminaRect, new Rectangle(x, y, 100, 80), Color.White * 0.3f);
        b.Draw(Game1.staminaRect, new Rectangle(x, y, 100, 80), null, Color.Black, 0f, Vector2.Zero, SpriteEffects.None, 0.01f);

        // 绘制手机图标（使用一个简单的矩形表示）
        Rectangle phoneRect = new Rectangle(x + 25, y + 10, 50, 60);
        b.Draw(Game1.staminaRect, phoneRect, Color.DarkSlateGray);
        
        // 手机屏幕
        Rectangle screenRect = new Rectangle(x + 30, y + 15, 40, 40);
        b.Draw(Game1.staminaRect, screenRect, Color.LightBlue * 0.5f);

        // 手机按钮
        Rectangle btnRect = new Rectangle(x + 40, y + 60, 20, 8);
        b.Draw(Game1.staminaRect, btnRect, Color.Gray);
    }

    /// <summary>绘制按钮标签</summary>
    private void DrawButtonLabel(SpriteBatch b, ClickableTextureComponent btn, string label, bool hover)
    {
        Vector2 labelSize = Game1.smallFont.MeasureString(label);
        float scale = 1f;
        
        float x = btn.bounds.X + (btn.bounds.Width - labelSize.X * scale) / 2;
        float y = btn.bounds.Y + (btn.bounds.Height - labelSize.Y * scale) / 2;
        
        Utility.drawTextWithShadow(
            b, label, Game1.smallFont,
            new Vector2(x, y),
            hover ? Color.Yellow : Game1.textColor
        );
    }

    /// <inheritdoc />
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_receiveButton.containsPoint(x, y))
        {
            Game1.playSound("bigSelect");
            PhoneReceived = true;
            exitThisMenu();
        }
        else if (_closeButton.containsPoint(x, y))
        {
            Game1.playSound("backpackIN");
            exitThisMenu();
        }
    }

    /// <inheritdoc />
    public override void performHoverAction(int x, int y)
    {
        _receiveButton.tryHover(x, y);
        _closeButton.tryHover(x, y);
        _hoverReceive = _receiveButton.containsPoint(x, y);
        _hoverClose = _closeButton.containsPoint(x, y);
    }

    /// <inheritdoc />
    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        exitThisMenu();
    }
}
