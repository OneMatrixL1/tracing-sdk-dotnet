namespace Tracing.Sdk.Canonicalize;

public interface ICanonicalizer
{
    /// <summary>
    /// Convert raw input into a single, deterministic byte representation so
    /// that semantically identical payloads always produce the same output,
    /// regardless of field order, whitespace, or encoding.
    /// </summary>
    /// <exception cref="CanonicalizationException">the input cannot be canonicalized</exception>
    byte[] Canonicalize(byte[] rawData);
}
