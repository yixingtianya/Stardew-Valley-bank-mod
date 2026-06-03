# BankMod GMCM 架构重构报告 V2.0

## 核心问题与最终解决方案

### 问题演进

| 阶段 | 方案 | 问题 |
|------|------|------|
| V0 | `setValue` 钳制 + 保存时弹 `DialogueBox` | `DialogueBox` 被 GMCM 关闭覆盖 |
| V1 | `setValue` 钳制 + 延迟帧弹 `DialogueBox` | `titleInPosition` 反射崩溃 |
| V2 | 无钳制 + 保存拒绝 + `drawObjectDialogue` | 同上崩溃 |
| V3 | 保存拒绝 + `HUDMessage`/`showRedMessage`/`chatBox` | 消息被 GMCM 菜单层吞没 |
| V4 | 保存拒绝 + `MenuChanged` 事件触发 `drawObjectDialogue` | 同上崩溃 |
| V5 | 保存拒绝 + `exitActiveMenu` 主动关闭 + `SafeDialogueBox` | 同上崩溃（SafeDialogueBox 未被识别） |
| **V6** | **Harmony 补丁修复 GMCM 反射崩溃 + SafeDialogueBox** | ✅ 解决 |

### 根因分析

GMCM 的 `OnUpdateTicked` 处理器**持久订阅** SMAPI 的 `UpdateTicking` 事件，每帧对 `Game1.activeClickableMenu` 进行反射：

```
activeClickableMenu.GetType().GetField("titleInPosition")
```

SMAPI 的 `Reflector.GetField<T>()` 使用 `required: true`，当字段不存在时抛出异常。`DialogueBox` 类型**没有** `titleInPosition` 字段，导致：

```
InvalidOperationException: The StardewValley.Menus.DialogueBox object
doesn't have a 'titleInPosition' instance field.
ArgumentNullException: Can't get a instance field from a null object.
```

### V6 解决方案：Harmony Prefix 补丁

**两层防护：**

#### 1. GmcmSafetyPatch（Harmony Prefix）
- 在 GMCM 的 `OnUpdateTicking` 方法上打 Prefix
- 检查 `Game1.activeClickableMenu` 是否有 `titleInPosition` 字段
- 无字段 → `return false` 跳过 GMCM 原始处理，防止崩溃
- 有字段 → `return true` 正常执行 GMCM 逻辑

#### 2. SafeDialogueBox（DialogueBox 子类）
- 添加 `titleInPosition` 字段满足 GMCM 反射
- 现在安全了：即使 Harmony 补丁未命中，SafeDialogueBox 也能通过反射检查

## 最终架构流程

```
用户拖动滑块 → setValue 直接赋值（无钳制）
                    ↓
          段落实时检测 HasIllegalConfig()
          - 合法: 空字符串
          - 非法: 一句话警告（Func<string> 每帧重算）
                    ↓
用户点"保存"/"保存并退出"
                    ↓
          save 回调检测 HasIllegalConfig()
          ├─ 合法 → Helper.WriteConfig → 正常保存
          └─ 非法 → _config 回退为磁盘版本
                    _postSaveStep = 1（主动关闭）
                    不写盘，return
                    ↓
          OnUpdateTicked (帧 N+1)
          _postSaveStep == 1
          → Game1.exitActiveMenu()  // 主动关闭GMCM
          → _postSaveStep = 2
                    ↓
          OnUpdateTicked (帧 N+2)
          _postSaveStep == 2
          → Game1.activeClickableMenu = new SafeDialogueBox(msg)
          → 玩家看到警告对话气泡（有titleInPosition，GMCM不崩溃）
          → 点掉对话 → 回到游戏，数据保持修改前状态
```

**同时：Harmony Prefix 在每帧保护**
```
GMCM.OnUpdateTicking 被调用
  → Prefix: activeClickableMenu 有 titleInPosition 字段？
    → 无 → return false（跳过，不崩溃）
    → 有 → return true（正常执行）
```

## 关键设计决策

### 1. 放弃滑块钳制
`setValue` 中不做 `Math.Min`/`Math.Max` 限制，让滑块完全自由拖动。理由：
- 钳制在 `setValue` 中生效，但 GMCM 不实时反馈钳制结果
- 玩家拖到边界时滑块视觉不动，会认为是 Bug
- 不如让滑块自由，在保存时检测并拒绝

### 2. 放弃保存时弹出对话框（直接调用）
`HUDMessage`、`showRedMessage`、`chatBox.addErrorMessage` 在 GMCM 保存回调上下文中**均不渲染**。GMCM 的菜单层阻塞了这些 UI 通道。

### 3. 三步延迟机制
不从 save 回调直接操作菜单。分三步：
1. **save 回调**：仅设置标志 + 回退数据
2. **下一帧**：`exitActiveMenu()` 关闭 GMCM
3. **再下一帧**：展示 SafeDialogueBox

### 4. Harmony Patch 修复 GMCM 反射崩溃
不修改 GMCM 源码，通过 Harmony Prefix 在 GMCM 的 `OnUpdateTicked` 执行前检查字段是否存在。这是最干净的方案，不侵入 GMCM 内部逻辑。

### 5. SafeDialogueBox 双保险
即使 Harmony 补丁因某种原因未生效，SafeDialogueBox 的 `titleInPosition` 字段也能直接满足 GMCM 反射，提供兜底保护。

## 校验规则

跨公司套利保护（仅 Joja + 皮埃尔固定公司）：

```
所有贷款利率 >= 所有存款利率
```

即以下 4 个条件必须同时满足：
- Joja贷款利率 >= Joja存款利率
- Joja贷款利率 >= 皮埃尔存款利率
- 皮埃尔贷款利率 >= Joja存款利率
- 皮埃尔贷款利率 >= 皮埃尔存款利率

## 相关文件

| 文件 | 职责 |
|------|------|
| `BankMod.cs` | GMCM 注册、save 回调、OnUpdateTicked 三步序列、SafeDialogueBox 子类 |
| `GmcmSafetyPatch.cs` | Harmony Prefix 补丁：防止 GMCM 对非 GMCM 菜单反射 titleInPosition 崩溃 |
| `IGenericModConfigMenuApi.cs` | GMCM API 接口定义 |
| `Data/ModConfig.cs` | 配置数据模型（CompanyDefinition、ModConfig） |
| `ARCHITECTURE_V2.md` | 本报告 |
