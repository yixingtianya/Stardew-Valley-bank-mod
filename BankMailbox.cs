using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace BankMod;

/// <summary>
/// 银行假邮箱物体 - 点击时显示邮箱弹窗
/// </summary>
public class BankMailbox
{
    public const string MailboxType = "BankMailbox";
    
    public Vector2 TileLocation { get; set; }
    
    public BankMailbox()
    {
        TileLocation = Vector2.Zero;
    }

    /// <summary>绘制物体</summary>
    public void draw(SpriteBatch spriteBatch, int x, int y)
    {
        // 使用游戏中的邮箱纹理
        Texture2D texture = Game1.objectSpriteSheet;
        Rectangle sourceRect = Game1.getSourceRectForStandardTileSheet(texture, 33, 16, 16);
        
        Vector2 position = new Vector2(x * 64, y * 64);
        
        spriteBatch.Draw(
            texture,
            position,
            sourceRect,
            Color.White,
            0f,
            Vector2.Zero,
            4f,
            SpriteEffects.None,
            0f
        );

        // 在邮箱上方绘制银行标志
        string iconText = "Bank";
        Vector2 textSize = Game1.smallFont.MeasureString(iconText);
        Vector2 textPos = new Vector2(
            position.X + 32 - textSize.X * 1f / 2,
            position.Y - 10
        );
        
        // 绘制阴影
        spriteBatch.DrawString(
            Game1.smallFont,
            iconText,
            textPos + new Vector2(2, 2),
            Color.Black * 0.5f
        );
        
        // 绘制文字
        spriteBatch.DrawString(
            Game1.smallFont,
            iconText,
            textPos,
            Color.White
        );
    }
}
