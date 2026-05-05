namespace BankMod;

/// <summary>Phone item constants used across the mod.</summary>
internal static class PhoneItem
{
    /// <summary>The unqualified item ID for the phone (used with the (O) type prefix).</summary>
    public const string ItemId = "BankMod.Phone";

    /// <summary>Fully qualified item ID for ItemRegistry.Create.</summary>
    public const string QualifiedItemId = "(O)BankMod.Phone";

    /// <summary>Custom texture asset name loaded from phone.png (resized to 16x16).</summary>
    public const string TextureAssetName = "BankMod.PhoneSprite";

    /// <summary>Data/Objects entry for the phone (with custom texture reference).</summary>
    public const string ObjectData = "手机/0/-300/Basic -79/一部神奇的手机，可以联系银行和其他服务。/true/" + TextureAssetName + "/0";
}
