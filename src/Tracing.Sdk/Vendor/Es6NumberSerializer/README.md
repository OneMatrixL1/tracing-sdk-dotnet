# Vendored: ES6 number serializer

ECMAScript `Number::toString` formatting for doubles, which RFC 8785 (JCS) requires for JSON numbers. Written by Anders Rundgren, co-author of RFC 8785, as a C# port of the Mozilla Rhino / V8 double-to-string algorithms.

- Source: https://github.com/cyberphone/json-canonicalization/tree/19d51d7fe467d4706a3ff08adf8a748f29fc21e0/dotnet/es6numberserializer
- Commit: `19d51d7fe467d4706a3ff08adf8a748f29fc21e0` (2024-12-13)
- License: Apache License 2.0 (`LICENSE` in this directory)

## Local modifications

- `NumberToJson.cs`: the class is `internal` instead of `public`, so it is not part of this SDK's public API.

Nothing else is changed. Only the number serializer is vendored. The repository's `JsonCanonicalizer` has its own JSON decoder, which rejects top-level scalars, throws on duplicate keys, and accepts lone surrogates, so it is not used. Parsing is done by System.Text.Json in `Canonicalize/JsonCanonicalizer.cs`.
