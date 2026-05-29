using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

/// <summary>Non-interactive overlay showing bankruptcy slogan in bottom-right corner.
/// Managed via Game1.onScreenMenus — rendered properly by the game's UI system without cross-target leakage.</summary>
internal class BankruptBanner : IClickableMenu
{
    private static BankruptBanner? _instance;

    private BankruptBanner() : base(0, 0, 1, 1) { }

    public static void Show()
    {
        if (_instance != null) return;
        _instance = new BankruptBanner();
        Game1.onScreenMenus.Add(_instance);
    }

    public static void Hide()
    {
        if (_instance == null) return;
        Game1.onScreenMenus.Remove(_instance);
        _instance = null;
    }

    public override void draw(SpriteBatch b)
    {
        string text = I18n.Get("uibnr.1");
        Vector2 size = Game1.dialogueFont.MeasureString(text);
        float x = Game1.uiViewport.Width - size.X - 20;
        float y = Game1.uiViewport.Height - size.Y - 12;

        var bg = new Rectangle((int)x - 12, (int)y - 6, (int)size.X + 24, (int)size.Y + 12);
        b.Draw(Game1.staminaRect, bg, Color.Black * 0.7f);
        b.DrawString(Game1.dialogueFont, text, new Vector2(x, y), Color.Gold);

        drawMouse(b);
    }
}
