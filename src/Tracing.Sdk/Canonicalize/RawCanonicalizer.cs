namespace Tracing.Sdk.Canonicalize;

/// <summary>
/// Identity canonicalizer for <see cref="DataType.Raw"/> — the caller is
/// asserting their data is already in a single, deterministic representation,
/// so it's hashed byte-for-byte as given, with no parsing or normalization.
/// </summary>
public sealed class RawCanonicalizer : ICanonicalizer
{
    public byte[] Canonicalize(byte[] rawData) => rawData;
}
