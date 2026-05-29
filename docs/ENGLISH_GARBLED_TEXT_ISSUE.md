# English Mode Garbled Text Issue — Handoff Document

## Problem

When the game language is set to English (Options → Language), the GMCM config menu and possibly other mod UI elements display garbled CJK characters (Chinese text rendered by the bitmap font that doesn't support CJK glyphs).

The garbled text is NOT missing translations — it is **Chinese translations being displayed when English is expected**. The game's bitmap font turns CJK characters into box/tofu glyphs.

## What Has Been Verified

### Translation Files (CORRECT)

- `i18n/default.json` — 909 keys, Chinese values (default/fallback)
- `i18n/en.json` — 909 keys, English values, **0 Chinese characters** verified via regex
- Both files are UTF-8 without BOM
- All 909 keys exist in both files (full coverage, no missing keys)
- SMAPI will overlay en.json on top of default.json when locale is `""` (English)

### I18n.cs (APPEARS CORRECT)

```csharp
public static string Get(string key)
{
    return Translations?.Get(key).ToString() ?? key;
}
```

- `ITranslationHelper` is stored as a static field via `I18n.Init(helper.Translation)` in `Entry()`
- Calls `Translations?.Get(key).ToString()` — uses SMAPI's standard API
- If `Translations` is null, returns the key itself (not garbled)

### BankMod.cs Locale Handling (APPEARS CORRECT)

- `Entry()` registers: `helper.Events.Content.LocaleChanged += OnLocaleChanged;`
- `OnLocaleChanged` logs locale change and calls `RefreshGmcm()`
- `RefreshGmcm()` calls `Unregister(ModManifest)` then `Register(...)` + `RegisterModConfigOptions(...)` to rebuild all GMCM labels
- All GMCM labels use `Func<string>` lambdas that call `I18n.Get(...)` — should re-evaluate at render time

### GMCM API (CORRECT)

- `IGenericModConfigMenuApi` has `Unregister(IManifest mod)` method
- All `Add*Option` methods accept `Func<string>` for name/tooltip — evaluated at render time

### Build Status

- 0 errors, 7 pre-existing nullable warnings
- DLL built and deployed to `D:\steam\steamapps\common\Stardew Valley\Mods\DynamicFinancialSystem`

## What Has Been Tried (ALL FAILED)

### Attempt 1: I18n.Get with token substitution bypass
Replaced SMAPI's `Tokens()` API with direct `string.Replace` for `{name}`, `{dep}`, etc.
→ Fixed crop table data display but did NOT fix English garbled text.

### Attempt 2: OnLocaleChanged → close/reopen config menu message
Added a log message telling users to close and reopen GMCM after language change.
→ Did NOT work. Labels still show garbled Chinese.

### Attempt 3: OnLocaleChanged → Unregister + Re-register GMCM (CURRENT CODE)
`RefreshGmcm()` unregisters and re-registers all GMCM options with fresh locale.
→ User reports this also did NOT work.

## Suspected Root Causes (INVESTIGATION NEEDED)

### Hypothesis A: `I18n.Init()` stores reference to old TranslationHelper

```csharp
public static void Init(ITranslationHelper translations)
{
    Translations = translations;  // stores reference
}
```

SMAPI may replace the internal `TranslationHelper` instance on locale change, but `I18n.Translations` still points to the old one. If SMAPI creates a NEW `TranslationHelper` on locale switch, our static reference would be stale.

**Test:** Add `I18n.Init(helper.Translation)` call inside `OnLocaleChanged` to refresh the reference.

### Hypothesis B: SMAPI `Translations.Get()` returns wrong locale even after locale change

The `ITranslationHelper` interface might cache the locale at `Init()` time. Even if `Locale` property updates, `Get(key)` might not switch to the new locale's file.

**Test:** In `OnLocaleChanged`, log `helper.Translation.Get("mod.36").ToString()` directly (bypassing I18n.cs) to verify SMAPI is returning English.

### Hypothesis C: `LocaleChanged` event is not firing on language switch

The game may change language without firing SMAPI's `Content.LocaleChanged` event (e.g., during title screen before save load, or through a code path that bypasses SMAPI's event hook).

**Test:** Add a console command that logs current locale and sample translation for diagnosis.

### Hypothesis D: GMCM caches labels before locale switch

GMCM might evaluate and cache the `Func<string>` lambdas at registration time, not at render time. If so, labels are frozen as Chinese until the next `Register` call.

**Test:** `RefreshGmcm()` (Unregister + Re-register) should fix this, but user reports it doesn't.

### Hypothesis E: SMAPI's locale is NOT `""` (English) when user thinks it's English

`ITranslationHelper.Locale` returns:
- `""` for English
- `"zh-CN"` for Chinese

If SMAPI detects the OS locale as Chinese and applies it regardless of in-game language setting, the locale might never actually become `""`.

**Test:** In `OnLocaleChanged`, also log `CultureInfo.CurrentCulture` and `CultureInfo.CurrentUICulture` to check system locale.

## Recommended Next Steps

1. **Add diagnostic console command** `i18n_debug` that logs:
   - `helper.Translation.Locale`
   - `I18n.Get("mod.36")` (via I18n.cs)
   - `helper.Translation.Get("mod.36").ToString()` (direct SMAPI call)
   - `CultureInfo.CurrentCulture.Name`
   - `CultureInfo.CurrentUICulture.Name`
   - Whether `en.json` exists at expected path

2. **Refresh TranslationHelper reference in OnLocaleChanged:**
   ```csharp
   private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e)
   {
       I18n.Init(Helper.Translation);  // Refresh static reference
       // ... rest of handler
   }
   ```

3. **Check if SMAPI locale is actually English:**
   If `helper.Translation.Locale` is NOT `""` when game is set to English, the problem is SMAPI-side, not mod-side.

4. **Consider direct file-based translation loading:**
   If SMAPI's `ITranslationHelper` is fundamentally broken for this use case, bypass it entirely by reading `en.json` directly using `File.ReadAllText` + `JsonSerializer` and doing our own locale detection and key lookup.

## File Locations

| File | Path |
|------|------|
| BankMod.cs | `Stardew Valley bank mod refactored/BankMod.cs` |
| I18n.cs | `Stardew Valley bank mod refactored/I18n.cs` |
| en.json | `Stardew Valley bank mod refactored/i18n/en.json` |
| default.json | `Stardew Valley bank mod refactored/i18n/default.json` |
| IGenericModConfigMenuApi.cs | `Stardew Valley bank mod refactored/IGenericModConfigMenuApi.cs` |
| manifest.json | `Stardew Valley bank mod refactored/manifest.json` |
| SMAPI log | User-provided: see `error.txt` in mod folder |
| Deployed mod | `D:\steam\steamapps\common\Stardew Valley\Mods\DynamicFinancialSystem` |

## Key Code: OnLocaleChanged (Current)

```csharp
private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e)
{
    var sampleVal = I18n.Get("mod.36");
    Monitor.Log($"[i18n] Locale changed: '{e.OldLocale}' -> '{e.NewLocale}' | mod.36='{sampleVal}'", LogLevel.Info);
    RefreshGmcm();
}

private void RefreshGmcm()
{
    if (_gmcmApi is null) return;
    try
    {
        _gmcmApi.Unregister(ModManifest);
        _gmcmApi.Register(ModManifest,
            reset: () => _config = new ModConfig(),
            save: () => { /* ... */ }
        );
        RegisterModConfigOptions(_gmcmApi);
        Monitor.Log("[i18n] GMCM labels refreshed for new locale.", LogLevel.Info);
    }
    catch (Exception ex)
    {
        Monitor.Log($"[i18n] Failed to refresh GMCM: {ex.Message}", LogLevel.Error);
    }
}
```

## Key Code: I18n.cs (Current)

```csharp
internal static class I18n
{
    private static ITranslationHelper? Translations { get; set; }

    public static void Init(ITranslationHelper translations)
    {
        Translations = translations;
    }

    public static string Get(string key)
    {
        return Translations?.Get(key).ToString() ?? key;
    }

    public static string Get(string key, object tokens)
    {
        var text = Translations?.Get(key).ToString() ?? key;
        if (tokens is null) return text;
        var props = tokens.GetType().GetProperties();
        foreach (var prop in props)
        {
            var value = prop.GetValue(tokens)?.ToString() ?? "";
            text = text.Replace("{" + prop.Name + "}", value);
        }
        return text;
    }
}
```

## SMAPI Log Reference

From user's error.txt:
```
[Dynamic Financial System] Config loaded: 2 companies
[Dynamic Financial System] 动态金融公司系统 v{0} 加载成功！
```

Note: `v{0}` shows the version token `{0}` was NOT substituted in the log message. This was fixed in a previous session by changing to `new { version = ... }` named tokens.

From prior session's `OnLocaleChanged` log:
```
[i18n] Locale changed: '' -> 'zh-CN' | mod.36='复利利息（存款/逾期）'
```

This shows the game switched from English (`''`) to Chinese (`'zh-CN'`), and `mod.36` returned Chinese text — which is correct for the Chinese locale. The garbled text issue occurs in the OPPOSITE direction: when the game is in English (`''`), the translations should be English but appear to be Chinese.
