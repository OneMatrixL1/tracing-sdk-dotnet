# Third-party notices

This package includes third-party code, distributed under its own license.

## ES6 number serializer (json-canonicalization)

`src/Tracing.Sdk/Vendor/Es6NumberSerializer/` — ECMAScript `Number::toString` formatting for JSON numbers, by Anders Rundgren (WebPKI.org), a C# port of algorithms from the Mozilla Rhino and V8 projects.

- Source: https://github.com/cyberphone/json-canonicalization/tree/19d51d7fe467d4706a3ff08adf8a748f29fc21e0/dotnet/es6numberserializer
- Modification: `NumberToJson` is `internal` instead of `public`.
- License: Apache License, Version 2.0

```
   Copyright 2018 Anders Rundgren

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       https://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```
