namespace BankMod.Domain;

/// <summary>Strongly-typed identifier for a financial company.</summary>
public readonly struct CompanyId : IEquatable<CompanyId>
{
    public string Value { get; }

    public CompanyId(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool Equals(CompanyId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is CompanyId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;

    public static bool operator ==(CompanyId left, CompanyId right) => left.Equals(right);
    public static bool operator !=(CompanyId left, CompanyId right) => !left.Equals(right);

    public static implicit operator string(CompanyId id) => id.Value;
    public static explicit operator CompanyId(string value) => new(value);
}
