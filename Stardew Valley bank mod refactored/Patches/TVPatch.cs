using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace BankMod.Patches;

[HarmonyPatch]
internal static class TVPatch
{
    private const int FbnChannel = 7;

    /// <summary>
    /// Prefix on TV.checkForAction: intercepts TV interaction to inject FBN channel.
    /// Returns false to skip the original method and build a complete channel list.
    /// This avoids conflicts with other mods that also patch this method via Postfix.
    /// </summary>
    [HarmonyPatch(typeof(TV), nameof(TV.checkForAction))]
    [HarmonyPrefix]
    public static bool CheckForAction_Prefix(TV __instance, Farmer who, bool justCheckingForActivity)
    {
        if (justCheckingForActivity || who is null) return true;

        try
        {
            var channels = BuildChannelList();

            Game1.currentLocation.createQuestionDialogue(
                Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13120"),
                channels.ToArray(),
                (_, answer) =>
                {
                    if (answer == "FBN")
                    {
                        StartFbnChannel(__instance, who);
                    }
                    else
                    {
                        __instance.selectChannel(who, answer ?? "");
                    }
                }
            );
            Game1.player.Halt();
        }
        catch (Exception ex)
        {
            Monitor?.Log($"[FBN] Error in TV patch: {ex.Message}", LogLevel.Error);
        }

        return false;
    }

    /// <summary>
    /// Builds the complete TV channel list including vanilla channels and FBN.
    /// Mirrors the original TV.checkForAction channel logic to preserve all standard channels.
    /// </summary>
    private static List<Response> BuildChannelList()
    {
        var channels = new List<Response>
        {
            new("Weather", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13105")),
            new("Fortune", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13107"))
        };

        // Periodic channels (matching original TV.checkForAction logic)
        switch (Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth))
        {
            case "Mon":
            case "Thu":
                channels.Add(new Response("Livin'", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13111")));
                break;
            case "Sun":
                channels.Add(new Response("The", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13114")));
                break;
            case "Wed":
                if (Game1.stats.DaysPlayed > 7)
                    channels.Add(new Response("The", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13117")));
                break;
        }

        // Fishing channel (requires Pam's quest completion)
        if (Game1.player.mailReceived.Contains("pamNewChannel"))
            channels.Add(new Response("Fishing", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV_Fishing_Channel")));

        // FBN financial channel
        channels.Add(new Response("FBN", I18n.Get("tv.1")));

        channels.Add(new Response("(Leave)", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13118")));

        return channels;
    }

    private static void StartFbnChannel(TV tv, Farmer who)
    {
        // Set channel via AccessTools (field name verified against SDV 1.6 decompiled source)
        var currentChannelField = AccessTools.Field(typeof(TV), "currentChannel");
        if (currentChannelField is null)
        {
            Monitor?.Log("[FBN] TV.currentChannel field not found — game version may have changed", LogLevel.Error);
            return;
        }
        currentChannelField.SetValue(tv, FbnChannel);

        // Set screen sprite (TemporaryAnimatedSprite — parameter order from SDV 1.6 decompiled TV.selectChannel)
        try
        {
            var screenField = AccessTools.Field(typeof(TV), "screen");
            if (screenField is not null)
            {
                var screenSprite = new TemporaryAnimatedSprite(
                    "Mods/BankMod/FBNScreen",
                    new Rectangle(0, 0, 168, 112),
                    150f,
                    2,
                    999999,
                    tv.getScreenPosition(),
                    flicker: false,
                    flipped: false,
                    layerDepth: (float)(tv.boundingBox.Bottom - 1) / 10000f + 1E-05f,
                    alphaFade: 0f,
                    color: Color.White,
                    scale: tv.getScreenSizeModifier() * 0.25f,
                    scaleChange: 0f,
                    rotation: 0f,
                    rotationChange: 0f
                );
                screenField.SetValue(tv, screenSprite);
            }
            else
            {
                Monitor?.Log("[FBN] TV.screen field not found — game version may have changed", LogLevel.Error);
            }
        }
        catch (Exception ex)
        {
            Monitor?.Log($"[FBN] Screen sprite error: {ex.Message}", LogLevel.Warn);
        }

        // Show FBN content via the game's dialogue system
        var content = FbnNewsGenerator.Generate();
        if (content.Count > 1)
        {
            var pages = new List<string>();
            var page = new System.Text.StringBuilder();
            for (int i = 0; i < content.Count; i++)
            {
                page.AppendLine(content[i]);
                if ((i + 1) % 5 == 0 || i == content.Count - 1)
                {
                    pages.Add(page.ToString());
                    page.Clear();
                }
            }
            Game1.multipleDialogues(pages.ToArray());
        }
        else
        {
            Game1.drawObjectDialogue(Game1.parseText(I18n.Get("tv.2")));
        }

        Game1.afterDialogues = () => tv.turnOffTV();
    }

    private static IMonitor? Monitor;
    public static void Initialize(IMonitor monitor) => Monitor = monitor;
}
