using BankMod.Services.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace BankMod.UI;

internal class JojaSupplyMenu : IClickableMenu
{
    private const int WindowWidth = 700;
    private const int WindowHeight = 520;
    private const int ItemRowHeight = 48;
    private const int MaxVisibleRows = 6;

    private readonly ModServices _services;
    private readonly IModHelper _helper;
    private readonly List<SupplyItemRow> _rows = new();
    private readonly ClickableTextureComponent _closeButton;
    private readonly ClickableTextureComponent _sellAllBtn;
    private readonly ClickableTextureComponent _upArrow;
    private readonly ClickableTextureComponent _downArrow;
    private int _scrollIndex;

    public JojaSupplyMenu(ModServices services, IModHelper helper)
        : base(
            (Game1.uiViewport.Width - WindowWidth) / 2,
            (Game1.uiViewport.Height - WindowHeight) / 2,
            WindowWidth,
            WindowHeight
        )
    {
        _services = services;
        _helper = helper;

        _closeButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + WindowWidth - 52, yPositionOnScreen + 8, 44, 44),
            Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);

        _sellAllBtn = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + 40, yPositionOnScreen + WindowHeight - 72, 140, 44),
            Game1.mouseCursors, new Rectangle(320, 432, 16, 16), 2f);

        _upArrow = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + WindowWidth - 80, yPositionOnScreen + 60, 40, 40),
            Game1.mouseCursors, new Rectangle(76, 72, 40, 40), 1f);

        _downArrow = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + WindowWidth - 80, yPositionOnScreen + 60 + ItemRowHeight * MaxVisibleRows, 40, 40),
            Game1.mouseCursors, new Rectangle(12, 72, 40, 40), 1f);

        RebuildItemList();
    }

    private void RebuildItemList()
    {
        _rows.Clear();
        if (Game1.player is null) return;

        foreach (var item in Game1.player.Items)
        {
            if (item is not StardewValley.Object obj || obj.QualifiedItemId is null)
                continue;

            string? cropCode = ShipmentTrackingService.NormalizeItemId(obj.QualifiedItemId);
            if (cropCode is null) continue;

            var cropData = CropDataProvider.GetByCode(cropCode);
            if (cropData is null) continue;

            int existingIdx = _rows.FindIndex(r => r.CropCode == cropCode);
            if (existingIdx >= 0)
            {
                var r = _rows[existingIdx];
                r.Quantity += obj.Stack;
                _rows[existingIdx] = r;
            }
            else
            {
                int price = obj.Price > 0 ? obj.Price : (int)(cropData.EquivalentCost * 2);
                _rows.Add(new SupplyItemRow
                {
                    CropCode = cropCode,
                    DisplayName = cropData.DisplayName,
                    Quantity = obj.Stack,
                    PricePerItem = price,
                    SellBtn = new ClickableTextureComponent(
                        Rectangle.Empty,
                        Game1.mouseCursors, new Rectangle(320, 432, 16, 16), 1f)
                });
            }
        }
    }

    public override void draw(SpriteBatch b)
    {
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, WindowWidth, WindowHeight, Color.White);

        Utility.drawTextWithShadow(b, "Joja超市 — 提供进货",
            Game1.smallFont, new Vector2(xPositionOnScreen + 30, yPositionOnScreen + 16), Color.Black);

        // Item list
        int listStartY = yPositionOnScreen + 68;
        if (_rows.Count == 0)
        {
            Utility.drawTextWithShadow(b, "背包中没有可提供的农产品",
                Game1.smallFont, new Vector2(xPositionOnScreen + 40, listStartY + 20), Color.DimGray);
        }
        else
        {
            for (int i = 0; i < MaxVisibleRows; i++)
            {
                int idx = _scrollIndex + i;
                if (idx >= _rows.Count) break;

                var row = _rows[idx];
                int rowY = listStartY + i * ItemRowHeight;

                // Background stripe
                b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 30, rowY, WindowWidth - 100, ItemRowHeight - 4),
                    i % 2 == 0 ? Color.White * 0.3f : Color.Gray * 0.2f);

                // Crop name + quantity
                Utility.drawTextWithShadow(b, $"{row.DisplayName} ×{row.Quantity}",
                    Game1.smallFont, new Vector2(xPositionOnScreen + 40, rowY + 14), Color.Black);

                // Price
                int totalPrice = row.PricePerItem * row.Quantity;
                Vector2 priceSize = Game1.smallFont.MeasureString($"{row.PricePerItem} g/个");
                Utility.drawTextWithShadow(b, $"{row.PricePerItem} g/个",
                    Game1.smallFont, new Vector2(xPositionOnScreen + 320, rowY + 14), Color.DimGray);

                // Sell button
                row.SellBtn.bounds = new Rectangle(xPositionOnScreen + 520, rowY + 8, 60, 30);
                b.Draw(Game1.staminaRect, row.SellBtn.bounds, row.SellBtn.containsPoint(Game1.getMouseX(), Game1.getMouseY()) ? Color.Goldenrod : Color.DarkGoldenrod);
                Utility.drawTextWithShadow(b, "卖出", Game1.smallFont,
                    new Vector2(row.SellBtn.bounds.X + 12, rowY + 16), Color.Black);
            }

            // Scroll arrows
            if (_scrollIndex > 0)
                _upArrow.draw(b);
            if (_scrollIndex + MaxVisibleRows < _rows.Count)
                _downArrow.draw(b);
        }

        // Buttons
        _closeButton.draw(b);
        int sellAllX = xPositionOnScreen + 40;
        int sellAllY = yPositionOnScreen + WindowHeight - 72;
        _sellAllBtn.bounds = new Rectangle(sellAllX, sellAllY, 160, 40);
        bool hoverSellAll = _sellAllBtn.containsPoint(Game1.getMouseX(), Game1.getMouseY());
        b.Draw(Game1.staminaRect, _sellAllBtn.bounds, hoverSellAll ? Color.DarkGreen : Color.Green);
        Utility.drawTextWithShadow(b, "全部卖出", Game1.smallFont,
            new Vector2(sellAllX + 44, sellAllY + 12), Color.Black);

        drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_closeButton.containsPoint(x, y))
        {
            exitThisMenu();
            return;
        }

        if (_sellAllBtn.containsPoint(x, y) && _rows.Count > 0)
        {
            SellAll();
            return;
        }

        if (_upArrow.containsPoint(x, y) && _scrollIndex > 0)
        {
            _scrollIndex--;
            Game1.playSound("shwip");
            return;
        }

        if (_downArrow.containsPoint(x, y) && _scrollIndex + MaxVisibleRows < _rows.Count)
        {
            _scrollIndex++;
            Game1.playSound("shwip");
            return;
        }

        foreach (var row in _rows)
        {
            if (row.SellBtn.containsPoint(x, y))
            {
                StartSellItem(row);
                return;
            }
        }
    }

    private void StartSellItem(SupplyItemRow row)
    {
        int maxQty = row.Quantity;
        Game1.activeClickableMenu = new NumberInputMenu(
            $"卖出 {row.DisplayName}（{row.PricePerItem} g/个）：",
            amount =>
            {
                if (amount > maxQty) amount = maxQty;
                ProcessSale(row.CropCode, amount, row.PricePerItem);
            },
            () => Game1.activeClickableMenu = new JojaSupplyMenu(_services, _helper),
            maxAmount: maxQty,
            allButtonLabel: "全部卖出"
        );
    }

    private void SellAll()
    {
        string summary = "";
        foreach (var row in _rows)
        {
            if (row.Quantity <= 0) continue;
            ProcessSale(row.CropCode, row.Quantity, row.PricePerItem);
            summary += $"{row.DisplayName}×{row.Quantity} ";
        }

        if (summary.Length > 0)
            Game1.chatBox?.addInfoMessage($"已向Joja超市供应: {summary.TrimEnd()}");
    }

    private void ProcessSale(string cropCode, int quantity, int pricePerItem)
    {
        var player = Game1.player;
        if (player is null) return;

        // Remove items from inventory
        int remaining = quantity;
        for (int i = player.Items.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var item = player.Items[i];
            if (item is not StardewValley.Object obj) continue;

            string? itemCrop = ShipmentTrackingService.NormalizeItemId(obj.QualifiedItemId);
            if (itemCrop != cropCode) continue;

            int take = Math.Min(remaining, obj.Stack);
            obj.Stack -= take;
            remaining -= take;
            if (obj.Stack <= 0)
                player.Items[i] = null;
        }

        // Give gold
        int revenue = quantity * pricePerItem;
        player.Money += revenue;
        Game1.playSound("sell");

        // Stage 7.3: competitor suppression + fuel deduction
        try
        {
            var account = _services.BankAccountService.Load();
            _services.CompanyManager.ApplySuppression(account, cropCode, quantity);
            _services.FuelService.RecordExternalSale(cropCode, quantity);
            _services.BankAccountService.Save(account);
        }
        catch (Exception ex)
        {
            _services.Monitor.LogOnce($"[JojaSupply] Suppression/fuel error: {ex.Message}");
        }

        _services.Monitor.Log($"[JojaSupply] Sold {cropCode} x{quantity} = {revenue}g", LogLevel.Debug);

        // Refresh list
        RebuildItemList();
        if (_rows.Count == 0)
            exitThisMenu();
    }

    public override void performHoverAction(int x, int y)
    {
        _closeButton.tryHover(x, y);
    }

    /// Minimal NumberInputMenu embedded here to avoid circular dependency.

    private struct SupplyItemRow
    {
        public string CropCode;
        public string DisplayName;
        public int Quantity;
        public int PricePerItem;
        public ClickableTextureComponent SellBtn;
    }
}
