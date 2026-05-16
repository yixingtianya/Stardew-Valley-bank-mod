# 动态金融公司系统 (Dynamic Financial System)

> 星露谷物语 1.6 经济深度扩展模组 — 接口驱动 · 分层架构 · Harmony 注入

[![.NET 6.0](https://img.shields.io/badge/.NET-6.0-512BD4)](https://dotnet.microsoft.com/)
[![SMAPI 4.0](https://img.shields.io/badge/SMAPI-4.0+-green)](https://smapi.io/)
[![Stardew 1.6](https://img.shields.io/badge/SDV-1.6+-orange)](https://www.stardewvalley.net/)

**V2.8.0** | 阶段十完成 | 电视金融频道 FBN | 入股救市 | 破产保护 | 贷款独立化

---

## 简介

将星露谷物语的出货箱经济扩展为**动态金融投资系统**。每种作物背后都可能诞生一家公司——存款赚利息、贷款加杠杆、燃料供给维持运营状态。利率随天气、运气、连续售卖实时浮动。电视新增 FBN 金融频道。皮埃尔和 Joja 作为固定银行提供稳定利率。

---

## 核心机制

### 公司体系
| 类型 | 公司 | 特点 |
|------|------|------|
| 固定银行 | 皮埃尔杂货店 / Joja超市 | 稳定利率，存贷上限可配 |
| 动态公司 | 按作物自动生成（出货≥50） | 利率锚定作物 R 值，受玩家供求影响 |

### 利率系统
- **基础利率**：固定公司配置读取，动态公司按"等效支出公式"（R = 等效日收益 / 等效支出）
- **三段式折扣**：R>20% 存款五折，0%<R≤20% 不打折，R≤0% 归零
- **状态乘数**：繁荣×1.2 稳定×0.9 饥饿×0.7 濒死×0.4
- **浮动因子**：天气+运气+连续售卖加成+停售衰减+竞争对手打压

### 公司状态与燃料
```
繁荣 >150% | 稳定 80-150% | 饥饿 30-80%（限取款禁借贷） | 濒死 <30%（可入股救市）
```

### 破产保护
- 强制划扣失败触发→贷款冻结（停息无限期）
- 收入 50% 强制偿债，禁止借款
- 右下角常驻标语，还款完成弹出祝贺

### 入股救市（Stage 9）
- 濒死期百股购买重组天数（每百股=有效贷款上限/7）
- +/- UI 弹窗，累计上限 7 百股
- 复活翻倍计入存款，倒闭清零

### 电视金融频道 FBN（Stage 10）
- Harmony 注入 TV 频道，内容按优先级：献祭>特殊日期>公司诞生>全局事件>每日预测>通用节目
- 40 种作物微讯库，TV3.png 屏幕动画

---

## 项目结构

```
BankMod/
├── Domain/                    值对象
├── Data/                      数据模型 + ModConfig + CropDataProvider
├── Services/
│   ├── Abstractions/          10 接口（ICompanyManager, ILoanService, IFuelService 等）
│   └── Core/                  12 实现 + Interest/ 策略
├── UI/                        BankMenu, NumberInputMenu, RescueInvestMenu
├── Patches/                   TVPatch, FbnNewsGenerator
├── BankMod.cs                 入口（组合根）
└── manifest.json
```

### 架构原则
- **接口驱动**：ModServices 手动 DI
- **Harmony 最小化**：仅 TV 频道使用，其余 SMAPI 原生事件
- **持久化安全**：调用方统一 Load/Save，杜绝 stale 覆盖
- **策略模式**：固定/动态公司双利率策略

---



## 安装

1. 安装 [SMAPI 4.0+](https://smapi.io/)
2. 将 `BankMod` 放入 `Stardew Valley/Mods/`
3. （可选）安装 [GMCM](https://www.nexusmods.com/stardewvalley/mods/5098) 使用游戏内配置

## 构建

```bash
dotnet build -c Release
```

输出：`bin/Release/net6.0/BankModRefactored.dll`
要求：.NET 6.0, SMAPI 4.0+, Harmony（SMAPI 内置）

## 许可

MIT License
