using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;

namespace BankMod.Patches;

[HarmonyPatch]
internal static class TVPatch
{
    private const int FbnChannel = 7;

    /// <summary>
    /// Prefix on TV.checkForAction: replaces the entire dialogue with one that includes FBN.
    /// Returns false to skip the original method.
    /// </summary>
    [HarmonyPatch(typeof(TV), nameof(TV.checkForAction))]
    [HarmonyPrefix]
    public static bool CheckForAction_Prefix(TV __instance, Farmer who, bool justCheckingForActivity)
    {
        if (justCheckingForActivity || who is null) return true;

        try
        {
            var channels = new List<Response>
            {
                new("Weather", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13105")),
                new("Fortune", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13107")),
                new("FBN", I18n.Get("tv.1"))
            };

            // Include original periodic channels
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

            if (Game1.player.mailReceived.Contains("pamNewChannel"))
                channels.Add(new Response("Fishing", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV_Fishing_Channel")));

            channels.Add(new Response("(Leave)", Game1.content.LoadString("Strings\\StringsFromCSFiles:TV.cs.13118")));

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
            Game1.chatBox?.addInfoMessage("[FBN] Error: " + ex.Message);
        }

        return false;
    }

    private static void StartFbnChannel(TV tv, Farmer who)
    {
        // Set channel
        var currentChannelField = AccessTools.Field(typeof(TV), "currentChannel");
        currentChannelField?.SetValue(tv, FbnChannel);

        // Set screen sprite (TemporaryAnimatedSprite — parameter order from SDV 1.6 decompiled TV.selectChannel)
        try
        {
            var screenField = AccessTools.Field(typeof(TV), "screen");
            if (screenField is not null)
            {
                var screenSprite = new TemporaryAnimatedSprite(
                    "Mods/BankMod/FBNScreen",                    // texture
                    new Rectangle(0, 0, 168, 112),               // sourceRect (one frame = 168×112)
                    150f,                                        // animationInterval
                    2,                                           // animationFrames
                    999999,                                      // animationLength
                    tv.getScreenPosition(),                      // position
                    flicker: false,                              // flicker
                    flipped: false,                              // flipped
                    layerDepth: (float)(tv.boundingBox.Bottom - 1) / 10000f + 1E-05f,
                    alphaFade: 0f,                               // alphaFade
                    color: Color.White,                           // color
                    scale: tv.getScreenSizeModifier() * 0.25f,   // scale (4× large → original)
                    scaleChange: 0f,                              // scaleChange
                    rotation: 0f,                                 // rotation
                    rotationChange: 0f                            // rotationChange
                );
                screenField.SetValue(tv, screenSprite);
            }
        }
        catch (Exception ex)
        {
            Game1.chatBox?.addInfoMessage("[FBN] Screen sprite error: " + ex.Message);
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
}
