using StardewModdingAPI;

namespace BankMod;

/// <summary>Access translations for this mod.</summary>
internal static class I18n
{
    /*********
    ** Fields
    *********/
    private static ITranslationHelper? Translations { get; set; }

    /*********
    ** Public methods
    *********/
    /// <summary>Initialize the translation helper.</summary>
    /// <param name="translations">The translation helper from <c>helper.Translation</c>.</param>
    public static void Init(ITranslationHelper translations)
    {
        Translations = translations;
    }

    /// <summary>Get a translation by key (for programmatic access).
    /// Returns the key itself if translations are not yet initialized.</summary>
    /// <param name="key">The i18n key.</param>
    public static string Get(string key)
    {
        return Translations?.Get(key).ToString() ?? key;
    }

    /// <summary>Get a translation with token substitution.
    /// Replaces {propertyName} tokens with values from the anonymous object.</summary>
    /// <param name="key">The i18n key.</param>
    /// <param name="tokens">Anonymous object whose properties map to {propertyName} tokens.</param>
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

    /// <summary>Get the mod loaded confirmation message.</summary>
    public static string Mod_Loaded()
    {
        return Translations?.Get("mod.loaded").ToString() ?? "mod.loaded";
    }

    /// <summary>Description for the spawn_phone command.</summary>
    public static string Command_SpawnPhone_Desc()
    {
        return Translations!.Get("command.spawn_phone.desc");
    }

    /// <summary>Data/Objects entry for the phone item.</summary>
    public static string Phone_ObjectData()
    {
        return Translations!.Get("phone.object_data");
    }

    /// <summary>Error when no save is loaded.</summary>
    public static string Command_Error_NoWorld()
    {
        return Translations!.Get("command.error.no_world");
    }

    /// <summary>Success message when phone is spawned.</summary>
    public static string Command_SpawnPhone_Success()
    {
        return Translations!.Get("command.spawn_phone.success");
    }

    /// <summary>Error when inventory is full.</summary>
    public static string Command_Error_InventoryFull()
    {
        return Translations!.Get("command.error.inventory_full");
    }

    /// <summary>Welcome dialogue when using the phone.</summary>
    public static string Phone_Dialogue_Welcome()
    {
        return Translations!.Get("phone.dialogue.welcome");
    }

    /// <summary>Choice prompt text.</summary>
    public static string Phone_Dialogue_Choice()
    {
        return Translations!.Get("phone.dialogue.choice");
    }

    /// <summary>"Enter Bank" choice label.</summary>
    public static string Phone_Choice_Enter()
    {
        return Translations!.Get("phone.choice.enter");
    }

    /// <summary>"Maybe Later" choice label.</summary>
    public static string Phone_Choice_Later()
    {
        return Translations!.Get("phone.choice.later");
    }

    /// <summary>Log when question is answered.</summary>
    public static string Log_QuestionAnswered()
    {
        return Translations!.Get("log.question_answered");
    }

    /// <summary>Chat message when entering bank (temp for step 1.4).</summary>
    public static string Chat_EnterBank()
    {
        return Translations!.Get("chat.enter_bank");
    }

    /// <summary>Chat notification when welcome mail is queued.</summary>
    public static string Chat_MailSent()
    {
        return Translations!.Get("chat.mail_sent");
    }

    /// <summary>Mail content for the welcome mail.</summary>
    public static string Mail_Welcome()
    {
        return Translations!.Get("mail.welcome");
    }

    /// <summary>Chat notification when phone is received directly.</summary>
    public static string Chat_PhoneReceived()
    {
        return Translations!.Get("chat.phone_received");
    }

    /// <summary>Phone item display name.</summary>
    public static string Phone_Name()
    {
        return Translations!.Get("phone.name");
    }

    /// <summary>Phone item description.</summary>
    public static string Phone_Description()
    {
        return Translations!.Get("phone.description");
    }

    /// <summary>Phone item category name.</summary>
    public static string Phone_Category()
    {
        return Translations!.Get("phone.category");
    }

    /*********
    ** 配置菜单翻译
    *********/

    /// <summary>Enable compound interest option.</summary>
    public static string Config_EnableCompoundInterest_Name() => Translations!.Get("config.enable_compound_interest.name");

    /// <summary>Enable compound interest tooltip.</summary>
    public static string Config_EnableCompoundInterest_Tooltip() => Translations!.Get("config.enable_compound_interest.tooltip");

    /// <summary>Enable simple interest option.</summary>
    public static string Config_EnableSimpleInterest_Name() => Translations!.Get("config.enable_simple_interest.name");

    /// <summary>Enable simple interest tooltip.</summary>
    public static string Config_EnableSimpleInterest_Tooltip() => Translations!.Get("config.enable_simple_interest.tooltip");

    /// <summary>Enable negative interest option.</summary>
    public static string Config_EnableNegativeInterest_Name() => Translations!.Get("config.enable_negative_interest.name");

    /// <summary>Enable negative interest tooltip.</summary>
    public static string Config_EnableNegativeInterest_Tooltip() => Translations!.Get("config.enable_negative_interest.tooltip");

    /// <summary>Current interest type display name.</summary>
    public static string Config_CurrentInterestType_Name() => Translations!.Get("config.current_interest_type.name");

    /// <summary>Interest type hint text.</summary>
    public static string Config_InterestTypeHint() => Translations!.Get("config.interest_type_hint");

    /// <summary>Compound interest type label.</summary>
    public static string Config_InterestType_Compound() => Translations!.Get("config.interest_type.compound");

    /// <summary>Simple interest type label.</summary>
    public static string Config_InterestType_Simple() => Translations!.Get("config.interest_type.simple");

    /// <summary>Enable luck influence option.</summary>
    public static string Config_EnableLuckInfluence_Name() => Translations!.Get("config.enable_luck_influence.name");

    /// <summary>Enable luck influence tooltip.</summary>
    public static string Config_EnableLuckInfluence_Tooltip() => Translations!.Get("config.enable_luck_influence.tooltip");

    /// <summary>Luck multiplier name.</summary>
    public static string Config_LuckMultiplier_Name() => Translations!.Get("config.luck_multiplier.name");

    /// <summary>Luck multiplier tooltip.</summary>
    public static string Config_LuckMultiplier_Tooltip() => Translations!.Get("config.luck_multiplier.tooltip");

    /// <summary>Weather section header.</summary>
    public static string Config_WeatherSection() => Translations!.Get("config.weather_section");

    /// <summary>Enable weather influence option.</summary>
    public static string Config_EnableWeatherInfluence_Name() => Translations!.Get("config.enable_weather_influence.name");

    /// <summary>Enable weather influence tooltip.</summary>
    public static string Config_EnableWeatherInfluence_Tooltip() => Translations!.Get("config.enable_weather_influence.tooltip");

    /// <summary>Sunny day bonus name.</summary>
    public static string Config_SunBonus_Name() => Translations!.Get("config.sun_bonus.name");

    /// <summary>Sunny day bonus tooltip.</summary>
    public static string Config_SunBonus_Tooltip() => Translations!.Get("config.sun_bonus.tooltip");

    /// <summary>Rainy day bonus name.</summary>
    public static string Config_RainBonus_Name() => Translations!.Get("config.rain_bonus.name");

    /// <summary>Rainy day bonus tooltip.</summary>
    public static string Config_RainBonus_Tooltip() => Translations!.Get("config.rain_bonus.tooltip");

    /// <summary>Snowy day bonus name.</summary>
    public static string Config_SnowBonus_Name() => Translations!.Get("config.snow_bonus.name");

    /// <summary>Snowy day bonus tooltip.</summary>
    public static string Config_SnowBonus_Tooltip() => Translations!.Get("config.snow_bonus.tooltip");

    /// <summary>Lightning day bonus name.</summary>
    public static string Config_LightningBonus_Name() => Translations!.Get("config.lightning_bonus.name");

    /// <summary>Lightning day bonus tooltip.</summary>
    public static string Config_LightningBonus_Tooltip() => Translations!.Get("config.lightning_bonus.tooltip");
}
