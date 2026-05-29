namespace BankMod.Domain;

/// <summary>
/// Centralized constants for Stardew Valley mail flags, event IDs, and save data keys.
/// All values sourced from https://stardewvalleywiki.com/Modding:Mail_data
/// </summary>
public static class MailFlags
{
    // Route Completion
    public const string CC_IsComplete = "ccIsComplete";
    public const string JojaMember = "JojaMember";
    public const string CC_MovieTheater = "ccMovieTheater";

    // Joja Development Projects
    public const string Joja_Minecart = "ccMinecart";
    public const string Joja_Bus = "ccBus";
    public const string Joja_Bridge = "ccBridge";
    public const string Joja_Greenhouse = "ccGreenhouse";
    public const string Joja_FishTank = "ccFishTank";

    // Bundle Unlocks
    public const string CC_Pantry = "ccPantry";
    public const string CC_Vault = "ccVault";

    // Travel Unlocks
    public const string WillyBoatFixed = "willyBoatFixed";
    public const string WillyBackRoomInvitation = "willyBackRoomInvitation";

    // Mod-Specific
    public const string BankMod_Letter = "BankMod_Letter";
    public const string BankMod_PierreThanks = "BankMod.PierreThanks";
    public const string BankMod_MorrisThanks = "BankMod.MorrisThanks";
    public const string BankMod_JojaInvitation = "BankMod.JojaInvitation";
    public const string BankMod_CommunityInvitation = "BankMod.CommunityInvitation";

    // Save Data Keys
    public const string Save_MorrisEventSeen = "bankmod_morris_event_seen";
    public const string Save_PhoneReceived = "bankmod_phone_status";

    // Vanilla Event IDs (documented in Data/Events)
    public const string Event_CC_Ceremony = "191393";
    public const string Event_Joja_Warehouse = "502261";
}
