using BankMod.Services.Abstractions;
using StardewModdingAPI;
using StardewValley;

namespace BankMod.Services.Core;

/// <summary>Builds and plays SDV event scripts from .txt DSL files for route cutscenes.</summary>
public class EventScriptService : IEventScriptService
{
    private readonly IModHelper _helper;
    private readonly IRouteService _routeService;
    private readonly IMonitor _monitor;

    public EventScriptService(IModHelper helper, IRouteService routeService, IMonitor monitor)
    {
        _helper = helper;
        _routeService = routeService;
        _monitor = monitor;
    }

    public bool StartRouteEvent(string fileName, bool isJojaRoute)
    {
        string path = Path.Combine(_helper.DirectoryPath, fileName);
        if (!File.Exists(path))
            path = Path.Combine(_helper.DirectoryPath, "assets", fileName);
        if (!File.Exists(path)) return false;

        var cmds = new List<string>();
        bool phoneJumped = false;

        foreach (var line in File.ReadAllLines(path))
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t) || t.StartsWith("//") || t.StartsWith("#")) continue;
            int ci = t.IndexOf("//");
            if (ci >= 0) t = t[..ci].TrimEnd();
            if (string.IsNullOrEmpty(t)) continue;

            if (t == "/end") { cmds.Add("end"); break; }
            else if (t.StartsWith("/text "))
            {
                string msg = t[6..].Trim('"', ' ');
                cmds.Add("message \"" + msg + "\"");
            }
            else if (t.StartsWith("/speak "))
            {
                string rest = t[7..].Trim();
                string npcName = rest;
                string text = rest;
                int sp = rest.IndexOf(' ');
                if (sp > 0) { npcName = rest[..sp]; text = rest[(sp + 1)..].Trim('"', ' '); }
                if (isJojaRoute)
                {
                    cmds.Add("speak " + npcName + " \"" + text + "\"");
                }
                else if (npcName == "Phone")
                {
                    if (!phoneJumped) { cmds.Add("jump Pierre"); cmds.Add("pause 400"); phoneJumped = true; }
                    cmds.Add("speak Lewis \"" + text + "\"");
                }
                else
                    cmds.Add("speak " + npcName + " \"" + text + "\"");
            }
            else if (t.StartsWith("/pause "))
                cmds.Add(t[1..]);
            else if (t.StartsWith("/emote "))
            {
                if (!isJojaRoute) cmds.Add(t[1..]);
            }
            else if (t.StartsWith("/move "))
                cmds.Add("warp " + t[6..]);
            else if (t.StartsWith("/cutscene "))
            {
                string sub = t[10..].Trim();
                if (sub == "spawnJunimo")
                {
                    // addTemporaryActor: spawn each Junimo one by one with a jump.
                    // Registered under both znmz and Characters/znmz in AssetRequested.
                    cmds.Add("addTemporaryActor znmz 16 16 7 7 2 true Character J1");
                    cmds.Add("jump J1");
                    cmds.Add("pause 400");
                    cmds.Add("addTemporaryActor znmh 16 16 9 7 2 true Character J2");
                    cmds.Add("jump J2");
                    cmds.Add("pause 400");
                    cmds.Add("addTemporaryActor znmf 16 16 8 6 2 true Character J3");
                    cmds.Add("jump J3");
                    cmds.Add("pause 400");
                    cmds.Add("addTemporaryActor znm  16 16 10 6 2 true Character J4");
                    cmds.Add("jump J4");
                    cmds.Add("pause 400");
                    cmds.Add("addTemporaryActor znmz 16 16 6 6 2 true Character J5");
                    cmds.Add("jump J5");
                }
            }
        }

        string header = isJojaRoute
            ? "continue/-1000 -1000/farmer -1000 -1000 0/globalFade/pause 100"
            : "continue/6 8/farmer 6 10 0 Pierre 6 8 2";
        string script = header + "/" + string.Join("/", cmds);
        _monitor.Log($"[EventScript] Script ({script.Length} chars)", LogLevel.Info);
        if (Game1.currentLocation is null)
        {
            _monitor.Log("[EventScript] currentLocation is null, cannot start event", LogLevel.Warn);
            return false;
        }
        _routeService.ModEventPlaying = true;
        Game1.currentLocation.startEvent(new Event(script, Game1.player));
        return true;
    }
}
