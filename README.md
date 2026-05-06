# 动态金融公司系统 (Dynamic Financial System)

> 星露谷物语 1.6 经济深度扩展模组 — 接口驱动 · 分层架构 · 策略模式

[![.NET 6.0](https://img.shields.io/badge/.NET-6.0-512BD4)](https://dotnet.microsoft.com/)
[![SMAPI 4.0](https://img.shields.io/badge/SMAPI-4.0+-green)](https://smapi.io/)
[![Stardew 1.6](https://img.shields.io/badge/SDV-1.6+-orange)](https://www.stardewvalley.net/)

---

## 简介

将星露谷物语的出货箱经济扩展为**动态金融投资系统**。每种作物背后都可能诞生一家公司——你可以存款赚利息、贷款加杠杆、通过燃料供给维持公司运营状态。利率随天气、运气、连续售卖行为实时浮动。皮埃尔和 Joja 作为固定银行提供稳定利率。

**V2.4.0** | 阶段七完成 | Morris 过场动画 | JojaSupplyMenu 供应商售卖 | 竞争对手打压机制

---

## 核心机制

### 公司体系
| 类型 | 公司 | 特点 |
|------|------|------|
| 固定银行 | 皮埃尔杂货店 / Joja超市 | 稳定利率，存款/贷款上限可配置 |
| 动态公司 | 按作物自动生成 | 利率锚定作物经济价值，受玩家供求行为影响 |

### 利率系统
- **基础利率**：固定公司从配置读取，动态公司按"等效支出公式"计算（R = 等效日收益 / 等效支出）
- **三段式折扣**：R>20% 存款打五折，0%<R≤20% 不打折，R≤0% 钳位归零
- **贷款利率硬下限 +1%**：始终有利差，无纯套利空间
- **浮动因子**：天气 + 运气 + 连续售卖加成 + 停售衰减 + 竞争对手打压
- **V3.7 手动复利检测**：7 天滑动窗口检测本金异常增长，触发惩罚利率（×0.10）

### 公司状态与燃料
```
繁荣期 >150% ← 连续出货维持
稳定期 80%-150%
饥饿期 30%-80% ← 取款受限（资产池 ×0.6）
濒死期 <30% ← 可入股救市（翻倍回报），7 天保护期
```

### 贷款与破产
- 7/14 天还款周期，14 天享利率折扣
- 宽限期 → 强制划扣（现金 → 同公司存款 → 其他存款）
- 公司倒闭还款按状态阶梯返还（繁荣 80% / 稳定 50% / 饥饿 0% / 濒死 0%）

### Stage 7 竞争对手打压
- 在皮埃尔/Joja 商店卖出作物 → 对应动态公司利率下降
- 打压层数随卖出数量叠加（≥20 个 +2 层，≥50 个 +3 层）
- 每日自动衰减一层

### Morris 过场动画
- JojaMart 内首次点击供应商周转箱触发专属事件
- 事件结束自动打开 JojaSupplyMenu（后续点击直达）
- SMAPI 反射处理 Morris NPC 显示/隐藏

---

## 项目结构

```
BankMod/
├── Domain/              # 值对象、枚举（零依赖）
│   ├── CompanyStatus.cs
│   ├── CompanyDefinition.cs
│   └── InterestCalculationContext.cs
├── Data/                # 数据模型、配置
│   ├── BankAccountData.cs
│   ├── ModConfig.cs
│   ├── CropShipmentData.cs
│   └── DynamicCompanyData.cs
├── Services/
│   ├── Abstractions/    # 接口定义
│   │   ├── ICompanyManager.cs
│   │   ├── IInterestCalculator.cs
│   │   ├── IFuelService.cs
│   │   ├── ILoanService.cs
│   │   └── IBankruptyHandler.cs
│   └── Core/            # 实现
│       ├── CompanyManager.cs
│       ├── LoanService.cs
│       ├── FuelService.cs
│       ├── ShipmentTrackingService.cs
│       ├── Interest/
│       │   ├── FixedCompanyInterestStrategy.cs
│       │   └── DynamicCompanyInterestStrategy.cs
│       └── ModServices.cs         # 手动 DI 容器
├── UI/                  # 界面
│   ├── BankMenu.cs              # 手机银行主界面
│   └── JojaSupplyMenu.cs        # Joja 供应商界面
├── BankMod.cs           # 模组入口
└── manifest.json
```

### 架构原则
- **接口驱动**：所有服务通过 `ModServices` 手动 DI 注入
- **依赖纯洁性**：Interest 模块不反向调用 CompanyManager
- **持久化安全**：写入方法接收 `BankAccountData` 参数，调用方统一 `Load/Save`（杜绝 stale 覆盖）
- **策略模式**：固定公司和动态公司使用不同利率计算策略

---

## 技术栈

| 技术 | 说明 |
|------|------|
| .NET 6.0 / C# 10 | 运行时与语言 |
| SMAPI 4.0+ | 星露谷模组加载器 |
| Stardew Valley 1.6+ | 游戏版本 |
| Harmony | 内置 SMAPI，GMCM 反射崩溃防护 |
| GMCM (可选) | 通用模组配置菜单集成 |
| 零外部 NuGet 依赖 | 仅依赖 SMAPI 内置程序集 |

---

## 安装

1. 安装 [SMAPI 4.0+](https://smapi.io/)
2. 下载最新 release zip
3. 解压到 `Stardew Valley/Mods/BankMod/`
4. （可选）安装 [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) 以使用游戏内配置界面
5. 启动游戏

---

## 配置

通过 GMCM 游戏内配置，或直接编辑 `config.json`。核心参数：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| DepositLoanCoefficient | 5000 | 存款上限 = 作物成本 × 系数 |
| Pierre/Joja DepositRate | 1.5% / 2.5% | 固定公司存款日利率 |
| Pierre/Joja LoanRate | 2.5% / 4.0% | 固定公司贷款日利率 |
| CompanySpawnRequiredSellCount | 50 | 动态公司诞生所需累计出售量 |
| ConsecutiveSellDepositBonusPerDay | +0.5% | 连续售卖每日加成（上限 +5.0%） |
| SuppressMaxDropPerDay | -2.0% | 每层打压每日利率跌幅 |
| SuppressEffectDurationDays | 5 | 每层打压持续天数 |
| SuppressMaxStacks | 3 | 打压最大叠层数 |
| AllowNegativeInterest | false | 允许存款利率变负 |

---

## 开发

```bash
# 构建（自动部署到 Mods 目录 + 生成 zip）
dotnet build -c Release

# 构建输出
bin/Release/net6.0/BankModRefactored.dll
bin/Release/net6.0/BankMod 2.0.0.zip
```

---

## 许可

MIT License
