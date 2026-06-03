namespace BankMod.Services.Abstractions;

/// <summary>Builds Stardew Valley event scripts from .txt DSL files for route cutscenes.</summary>
public interface IEventScriptService
{
    /// <summary>Build AND start the event at the current location. Returns true if started.</summary>
    bool StartRouteEvent(string fileName, bool isJojaRoute);
}
