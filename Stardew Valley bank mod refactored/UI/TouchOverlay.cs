using System.Runtime.InteropServices;
using BankMod.Data;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace BankMod.UI;

/// <summary>Virtual touch input overlay providing numeric keypad and action buttons for Android.</summary>
internal class TouchOverlay
{
    private static readonly bool IsAndroid = RuntimeInformation.IsOSPlatform(OSPlatform.Create("Android"));

    public enum OverlayMode
    {
        None,
        Numeric,
        Operation
    }

    private struct TouchButton
    {
        public Rectangle Bounds;
        public string Label;
        public Color BorderColor;
        public char KeyChar;
        public bool IsSpecial;
        public string SpecialAction;
        public Action? OnClick;
    }

    private readonly List<TouchButton> _buttons = new();
    private OverlayMode _mode = OverlayMode.None;
    private bool _isVisible;
    private bool _wasTouched;
    private readonly ModConfig _config;
    private readonly IMonitor _monitor;

    public event Action<char>? OnKeyPressed;
    public event Action? OnConfirm;
    public event Action? OnCancel;
    public event Action? OnTabLeft;
    public event Action? OnTabRight;
    public event Action<int>? OnScroll;

    public TouchOverlay(ModConfig config, IMonitor monitor)
    {
        _config = config;
        _monitor = monitor;
    }

    public static bool ShouldShow(ModConfig config) => IsAndroid && config.EnableTouchOverlay;

    public void SetMode(OverlayMode mode)
    {
        _mode = mode;
        _isVisible = mode != OverlayMode.None && IsAndroid;
        BuildButtons();
        _monitor.Log($"[TouchOverlay] SetMode={mode} isAndroid={IsAndroid} visible={_isVisible} buttons={_buttons.Count}", LogLevel.Debug);
    }

    public void Update()
    {
        if (!_isVisible) return;

        bool isTouched = Game1.input.GetMouseState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;

        if (isTouched && !_wasTouched)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();
            HandleTouchInput(new Vector2(mx, my));
        }

        _wasTouched = isTouched;
    }

    public void Draw(SpriteBatch b)
    {
        if (!_isVisible) return;

        foreach (var btn in _buttons)
        {
            Color bgColor = Color.Black * 0.5f;
            b.Draw(Game1.staminaRect, btn.Bounds, bgColor);
            b.Draw(Game1.staminaRect, new Rectangle(btn.Bounds.X, btn.Bounds.Y, btn.Bounds.Width, 2), btn.BorderColor);
            b.Draw(Game1.staminaRect, new Rectangle(btn.Bounds.X, btn.Bounds.Y + btn.Bounds.Height - 2, btn.Bounds.Width, 2), btn.BorderColor);
            b.Draw(Game1.staminaRect, new Rectangle(btn.Bounds.X, btn.Bounds.Y, 2, btn.Bounds.Height), btn.BorderColor);
            b.Draw(Game1.staminaRect, new Rectangle(btn.Bounds.X + btn.Bounds.Width - 2, btn.Bounds.Y, 2, btn.Bounds.Height), btn.BorderColor);

            Vector2 labelSize = Game1.smallFont.MeasureString(btn.Label);
            float x = btn.Bounds.X + (btn.Bounds.Width - labelSize.X) / 2;
            float y = btn.Bounds.Y + (btn.Bounds.Height - labelSize.Y) / 2;
            Utility.drawTextWithShadow(b, btn.Label, Game1.smallFont, new Vector2(x, y), Color.White);
        }
    }

    private void HandleTouchInput(Vector2 touchPos)
    {
        foreach (var btn in _buttons)
        {
            if (!btn.Bounds.Contains(touchPos.X, touchPos.Y)) continue;

            _monitor.Log($"[TouchOverlay] Touch: '{btn.Label}' action={btn.SpecialAction}", LogLevel.Debug);

            if (btn.IsSpecial)
            {
                switch (btn.SpecialAction)
                {
                    case "Confirm": OnConfirm?.Invoke(); break;
                    case "Cancel": OnCancel?.Invoke(); break;
                    case "TabLeft": OnTabLeft?.Invoke(); break;
                    case "TabRight": OnTabRight?.Invoke(); break;
                    case "ScrollUp": OnScroll?.Invoke(1); break;
                    case "ScrollDown": OnScroll?.Invoke(-1); break;
                }
            }
            else if (btn.KeyChar != '\0')
            {
                OnKeyPressed?.Invoke(btn.KeyChar);
            }
            else
            {
                btn.OnClick?.Invoke();
            }

            Game1.playSound("smallSelect");
            return;
        }
    }

    private void BuildButtons()
    {
        _buttons.Clear();
        if (_mode == OverlayMode.None || !IsAndroid) return;

        int vpW = Game1.uiViewport.Width;
        int vpH = Game1.uiViewport.Height;
        int btnSize = _config.TouchButtonSize;
        int gap = 4;

        if (_mode == OverlayMode.Numeric)
            BuildNumericLayout(vpW, vpH, btnSize, gap);
        else
            BuildOperationLayout(vpW, vpH, btnSize, gap);
    }

    private void BuildNumericLayout(int vpW, int vpH, int btnSize, int gap)
    {
        bool isChinese = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
        int confirmW = isChinese ? 80 : 100;
        int allW = isChinese ? 80 : 100;
        int backW = btnSize;

        int baseX = vpW - (btnSize * 5 + gap * 4 + confirmW + gap + allW + gap + backW + gap + 20);
        if (baseX < 10) baseX = 10;
        int baseY = vpH - (btnSize * 4 + gap * 3 + 20);

        string confirmLabel = isChinese ? "确认" : "OK";
        string cancelLabel = isChinese ? "取消" : "Cancel";
        string maxLabel = isChinese ? "全部" : "Max";
        string clearLabel = isChinese ? "清空" : "CLR";
        string backLabel = "<-";

        // Row 0: 1 2 3 <- [Confirm]
        int r0y = baseY;
        AddDigitButton('1', baseX, r0y, btnSize);
        AddDigitButton('2', baseX + (btnSize + gap), r0y, btnSize);
        AddDigitButton('3', baseX + (btnSize + gap) * 2, r0y, btnSize);
        AddKeyButton('\b', backLabel, baseX + (btnSize + gap) * 3, r0y, backW, Color.Gray);
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(baseX + (btnSize + gap) * 3 + backW + gap, r0y, confirmW, btnSize),
            Label = confirmLabel, BorderColor = Color.DarkGreen, IsSpecial = true, SpecialAction = "Confirm"
        });

        // Row 1: 4 5 6 [+100] [Max]
        int r1y = r0y + btnSize + gap;
        AddDigitButton('4', baseX, r1y, btnSize);
        AddDigitButton('5', baseX + (btnSize + gap), r1y, btnSize);
        AddDigitButton('6', baseX + (btnSize + gap) * 2, r1y, btnSize);
        AddKeyButton('\0', "+100", baseX + (btnSize + gap) * 3, r1y, backW, Color.SteelBlue, () => OnKeyPressed?.Invoke('+'));
        AddKeyButton('\0', maxLabel, baseX + (btnSize + gap) * 3 + backW + gap, r1y, allW, Color.SteelBlue, () => OnKeyPressed?.Invoke('M'));

        // Row 2: 7 8 9 [+1000]
        int r2y = r1y + btnSize + gap;
        AddDigitButton('7', baseX, r2y, btnSize);
        AddDigitButton('8', baseX + (btnSize + gap), r2y, btnSize);
        AddDigitButton('9', baseX + (btnSize + gap) * 2, r2y, btnSize);
        AddKeyButton('\0', "+1k", baseX + (btnSize + gap) * 3, r2y, backW, Color.SteelBlue, () => OnKeyPressed?.Invoke('P'));

        // Row 3: 0 [CLR] <- [Cancel]
        int r3y = r2y + btnSize + gap;
        AddDigitButton('0', baseX, r3y, btnSize);
        AddKeyButton('\0', clearLabel, baseX + (btnSize + gap), r3y, btnSize, Color.DarkGray, () => OnKeyPressed?.Invoke('C'));
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(baseX + (btnSize + gap) * 2, r3y, backW, btnSize),
            Label = backLabel, BorderColor = Color.Gray, KeyChar = '\b'
        });
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(baseX + (btnSize + gap) * 3, r3y, confirmW, btnSize),
            Label = cancelLabel, BorderColor = Color.DarkRed, IsSpecial = true, SpecialAction = "Cancel"
        });
    }

    private void BuildOperationLayout(int vpW, int vpH, int btnSize, int gap)
    {
        bool isChinese = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
        int actionW = isChinese ? 80 : 100;
        int baseX = vpW - (actionW * 2 + gap + 20);
        if (baseX < 10) baseX = 10;
        int baseY = vpH - (btnSize * 3 + gap * 2 + 20);

        string tabLeftLabel = isChinese ? "公司<" : "Tab<";
        string tabRightLabel = isChinese ? ">公司" : ">Tab";
        string scrollUpLabel = isChinese ? "上翻" : "Up";
        string scrollDownLabel = isChinese ? "下翻" : "Down";
        string cancelLabel = isChinese ? "取消" : "Cancel";

        // Row 0: Tab Left, Tab Right
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(baseX, baseY, actionW, btnSize),
            Label = tabLeftLabel, BorderColor = Color.SteelBlue, IsSpecial = true, SpecialAction = "TabLeft"
        });
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(baseX + actionW + gap, baseY, actionW, btnSize),
            Label = tabRightLabel, BorderColor = Color.SteelBlue, IsSpecial = true, SpecialAction = "TabRight"
        });

        // Row 1: Scroll Up, Scroll Down
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(baseX, baseY + btnSize + gap, actionW, btnSize),
            Label = scrollUpLabel, BorderColor = Color.Cyan, IsSpecial = true, SpecialAction = "ScrollUp"
        });
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(baseX + actionW + gap, baseY + btnSize + gap, actionW, btnSize),
            Label = scrollDownLabel, BorderColor = Color.Cyan, IsSpecial = true, SpecialAction = "ScrollDown"
        });

        // Row 2: Cancel
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(baseX, baseY + (btnSize + gap) * 2, actionW * 2 + gap, btnSize),
            Label = cancelLabel, BorderColor = Color.DarkRed, IsSpecial = true, SpecialAction = "Cancel"
        });
    }

    private void AddDigitButton(char digit, int x, int y, int size)
    {
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(x, y, size, size),
            Label = digit.ToString(),
            BorderColor = Color.White * 0.6f,
            KeyChar = digit
        });
    }

    private void AddKeyButton(char keyChar, string label, int x, int y, int size, Color borderColor, Action? onClick = null)
    {
        _buttons.Add(new TouchButton
        {
            Bounds = new Rectangle(x, y, size, size),
            Label = label,
            BorderColor = borderColor,
            KeyChar = keyChar,
            OnClick = onClick
        });
    }
}
