# 移动端输入适配方案

## 1. 问题背景

玩家反馈 Android 端运行正常，但按键无法触发，特别是右键（`SButton.MouseRight`）无法实现。

### 根因分析

| 功能 | 桌面端 | Android |
|------|--------|---------|
| 右键打开银行 | `SButton.MouseRight` ✓ | 触摸无右键概念 ✗ |
| 数字输入 | 物理键盘 ✓ | 无虚拟键盘，SMAPI 未集成系统键盘 ✗ |
| 滚动列表 | 鼠标滚轮 ✓ | 无滚轮事件 ✗ |
| 按钮点击 | 鼠标左键 ✓ | 触摸映射为 `MouseLeft` ✓ |

**核心问题**：SMAPI Android 版将触摸转换为 `SButton.MouseLeft`，但没有提供虚拟键盘或右键替代方案。

## 2. 设计原则

1. **通用性**：所有改进对 PC 和移动端都自然受益，不为安卓打补丁
2. **配置驱动**：通过 `ModConfig` 让玩家自定义按键绑定
3. **最小侵入**：不修改游戏核心逻辑，仅在 UI 层添加输入代理

## 3. 方案概览

```
┌─────────────────────────────────────────────────────────┐
│                    虚拟输入层 (TouchOverlay)              │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐     │
│  │ 数字键盘     │  │ 操作按钮     │  │ 滚动控制     │     │
│  │ (0-9, BS,   │  │ (Action,    │  │ (Up/Down,   │     │
│  │  Enter, All)│  │  Cancel)    │  │  +/-)       │     │
│  └─────────────┘  └─────────────┘  └─────────────┘     │
└─────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────┐
│                    输入路由层                             │
│  • 触摸事件 → 虚拟按键 → receiveKeyPress / receiveLeftClick │
│  • 配置项 OpenBankKey 绑定触发键                          │
└─────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────┐
│                    现有 UI 组件                           │
│  BankMenu / NumberInputMenu / BankChoiceMenu             │
└─────────────────────────────────────────────────────────┘
```

## 4. 详细设计

### 4.1 ModConfig 新增配置项

```csharp
// ModConfig.cs 新增

/// <summary>打开银行菜单的按键（桌面端默认右键，移动端可改为其他键）</summary>
public SButton OpenBankKey { get; set; } = SButton.MouseRight;

/// <summary>启用触摸友好的虚拟输入覆盖层</summary>
public bool EnableTouchOverlay { get; set; } = true;

/// <summary>虚拟键盘按钮大小（像素，移动端建议 60-80）</summary>
public int TouchButtonSize { get; set; } = 64;

/// <summary>虚拟键盘显示位置：Bottom, Right, Auto</summary>
public string TouchOverlayPosition { get; set; } = "Auto";
```

### 4.2 虚拟输入覆盖层 (TouchOverlay)

创建新文件 `UI/TouchOverlay.cs`：

```csharp
namespace BankMod.UI;

/// <summary>
/// 触摸友好的虚拟输入覆盖层，提供数字键盘和操作按钮。
/// 在 EnableTouchOverlay 开启时自动显示。
/// </summary>
internal class TouchOverlay
{
    // 输入模式
    public enum OverlayMode
    {
        None,           // 隐藏
        Numeric,        // 数字输入模式（NumberInputMenu 使用）
        Operation       // 操作模式（BankMenu 等使用）
    }

    // 按钮定义
    private readonly List<TouchButton> _buttons = new();
    private OverlayMode _mode = OverlayMode.None;
    private bool _isVisible;

    // 事件回调
    public event Action<char>? OnKeyPressed;    // 数字键、退格
    public event Action? OnConfirm;             // 确认
    public event Action? OnCancel;              // 取消
    public event Action? OnAction;              // 右键替代（Action 键）
    public event Action<int>? OnScroll;         // 滚动 (+1/-1)

    /// <summary>根据模式显示/隐藏覆盖层</summary>
    public void SetMode(OverlayMode mode) { ... }

    /// <summary>在每帧更新中调用，处理触摸输入</summary>
    public void Update() { ... }

    /// <summary>在 draw 中调用，渲染虚拟按钮</summary>
    public void Draw(SpriteBatch b) { ... }

    /// <summary>检测触摸点是否命中按钮</summary>
    private bool HandleTouchInput(Vector2 touchPos) { ... }
}
```

#### 数字键盘模式布局

```
┌─────────────────────────────────────┐
│           数字显示框                 │
│              12,345                 │
├─────────────────────────────────────┤
│  [1]  [2]  [3]  [←]  [确认]        │
│  [4]  [5]  [6]  [±100] [全部]      │
│  [7]  [8]  [9]  [±1000]           │
│  [0]  [.]  [清空]                   │
└─────────────────────────────────────┘
```

#### 操作模式布局

```
┌─────────────────────────────────────┐
│                                     │
│  [▲]                               │
│  [◀]  [Action]  [▶]               │
│  [▼]                               │
│                                     │
│  [确认]  [取消]  [滚动↑] [滚动↓]   │
└─────────────────────────────────────┘
```

### 4.3 入口触发适配

修改 `BankMod.cs` 的 `OnButtonPressed` 方法：

```csharp
private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
{
    if (!Context.IsWorldReady) return;

    // 支持配置的按键 + 默认的右键/手柄
    bool isTriggerKey = e.Button == _config.OpenBankKey
                     || e.Button == SButton.ControllerA
                     || e.Button == SButton.MouseRight;

    if (!isTriggerKey) return;

    // ... 后续逻辑不变
}
```

### 4.4 NumberInputMenu 适配

在 `NumberInputMenu.cs` 中集成虚拟键盘：

```csharp
private TouchOverlay? _touchOverlay;

public NumberInputMenu(...)
{
    // ... 现有构造逻辑

    // 移动端启用虚拟键盘
    if (_config.EnableTouchOverlay)
    {
        _touchOverlay = new TouchOverlay();
        _touchOverlay.SetMode(TouchOverlay.OverlayMode.Numeric);
        _touchOverlay.OnKeyPressed += OnVirtualKeyPressed;
        _touchOverlay.OnConfirm += Confirm;
        _touchOverlay.OnCancel += Cancel;
    }
}

private void OnVirtualKeyPressed(char key)
{
    if (key == '\b') // 退格
    {
        if (_input.Length > 0)
        {
            _input.Remove(_input.Length - 1, 1);
            Game1.playSound("tinyWhip");
        }
    }
    else if (char.IsDigit(key) && _input.Length < 9)
    {
        if (_input.Length == 0 && key == '0') return;
        _input.Append(key);
        Game1.playSound("tinyWhip");
    }
}

public override void draw(SpriteBatch b)
{
    // ... 现有绘制逻辑

    // 绘制虚拟键盘
    _touchOverlay?.Draw(b);
}
```

### 4.5 BankMenu 适配

在 `BankMenu.cs` 中添加操作模式虚拟键盘：

```csharp
private TouchOverlay? _touchOverlay;

public BankMenu(...)
{
    // ... 现有构造逻辑

    if (_config.EnableTouchOverlay)
    {
        _touchOverlay = new TouchOverlay();
        _touchOverlay.SetMode(TouchOverlay.OverlayMode.Operation);
        _touchOverlay.OnAction += OnActionPressed;
        _touchOverlay.OnScroll += OnVirtualScroll;
    }
}

private void OnActionPressed()
{
    // 模拟右键：关闭菜单或触发上下文操作
    receiveRightClick(
        Game1.getMouseX(),
        Game1.getMouseY(),
        true
    );
}

private void OnVirtualScroll(int direction)
{
    receiveScrollWheelAction(direction);
}

public override void draw(SpriteBatch b)
{
    // ... 现有绘制逻辑

    // 绘制操作按钮
    _touchOverlay?.Draw(b);
}
```

### 4.6 按钮尺寸优化

为移动端，将 BankMenu 的按钮尺寸适当放大：

```csharp
// BankMenu.cs 构造函数中
int btnW = _isChinese ? 130 : 150;
int btnH = _config.EnableTouchOverlay ? 50 : 40;  // 触摸模式下放大
```

### 4.7 滚动控制

在 `BankMenu.cs` 的贷款详情滚动区域添加 +/- 按钮：

```csharp
// 贷款详情滚动条附近
if (_showLoanDetail && _config.EnableTouchOverlay)
{
    // 绘制 +/- 按钮
    var scrollUpBtn = new Rectangle(scrollBarX - 30, listY, 24, 24);
    var scrollDownBtn = new Rectangle(scrollBarX - 30, listY + trackH - 24, 24, 24);

    // 检测点击
    if (scrollUpBtn.Contains(x, y))
        _loanScrollOffset = Math.Max(0, _loanScrollOffset - 1);
    if (scrollDownBtn.Contains(x, y))
        _loanScrollOffset = Math.Min(maxOffset, _loanScrollOffset + 1);
}
```

## 5. 中英文 UI 区别处理

### 5.1 虚拟键盘布局

| 元素 | 中文 | 英文 |
|------|------|------|
| 确认按钮 | 确认 | Confirm |
| 取消按钮 | 取消 | Cancel |
| 全部按钮 | 全部 | Max |
| Action 按钮 | 操作 | Action |
| 按钮尺寸 | 130px 宽 | 150px 宽 |

### 5.2 触摸区域大小

```csharp
private int GetTouchButtonWidth()
{
    bool isChinese = LocalizedContentManager.CurrentLanguageCode
        == LocalizedContentManager.LanguageCode.zh;
    int baseWidth = isChinese ? 130 : 150;
    return _config.EnableTouchOverlay ? baseWidth + 20 : baseWidth;
}
```

### 5.3 按钮文字截断

移动端按钮可能需要更激进的截断策略：

```csharp
// 超出宽度时截断并添加省略号
if (labelSize.X > btn.bounds.Width - 8)
{
    while (drawLabel.Length > 1 &&
           Game1.smallFont.MeasureString(drawLabel + "...").X > btn.bounds.Width - 8)
        drawLabel = drawLabel[..^1];
    drawLabel += "...";
}
```

## 6. 实施计划

### 阶段一：基础框架（2-3h）

1. 在 `ModConfig.cs` 添加配置项
2. 创建 `TouchOverlay.cs` 基础框架
3. 实现数字键盘模式

### 阶段二：入口触发（1h）

1. 修改 `BankMod.cs` 的 `OnButtonPressed`
2. 添加 `OpenBankKey` 配置支持

### 阶段三：UI 适配（2-3h）

1. `NumberInputMenu` 集成虚拟键盘
2. `BankMenu` 添加操作模式
3. 按钮尺寸优化
4. 滚动控制按钮

### 阶段四：中英文适配（1h）

1. 按钮文字本地化
2. 布局微调

### 阶段五：测试与优化（2h）

1. 桌面端回归测试
2. Android 端测试
3. 性能优化

## 7. 风险与注意事项

1. **输入冲突**：虚拟键盘不能干扰游戏原有操作（如攻击、使用工具）
2. **性能影响**：每帧检测触摸输入可能有性能开销，需优化
3. **屏幕适配**：不同分辨率下按钮位置需要自适应
4. **SMAPI 版本兼容**：确保 `SButton` 配置在所有 SMAPI 版本上可用

## 8. 测试用例

| 场景 | 桌面端 | Android |
|------|--------|---------|
| 右键打开银行 | 鼠标右键 ✓ | 触摸 Action 按钮 ✓ |
| 数字输入 | 键盘数字 ✓ | 虚拟键盘 ✓ |
| 列表滚动 | 鼠标滚轮 ✓ | +/- 按钮 ✓ |
| 按钮点击 | 鼠标左键 ✓ | 触摸 ✓ |
| 取消操作 | 右键/ESC ✓ | Action/取消按钮 ✓ |

## 9. 相关文件

| 文件 | 修改内容 |
|------|----------|
| `Data/ModConfig.cs` | 新增配置项 |
| `UI/TouchOverlay.cs` | 新建：虚拟输入覆盖层 |
| `UI/NumberInputMenu.cs` | 集成数字键盘 |
| `UI/BankMenu.cs` | 集成操作模式 + 按钮放大 |
| `BankMod.cs` | 入口触发适配 |
