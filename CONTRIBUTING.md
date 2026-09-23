# Contributing to HashLynx

Use Windows and the .NET 10 SDK. Clone the repository and run:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/HashLynx.UI
```

The solution and normal tests do not require Hashcat or a GPU. For runtime work, separately extract the official release into `HashLynx\hashcat\`; do not edit or commit that directory. Keep integration checks separate and use disposable copies where Hashcat may write caches.

Keep domain models/validation in Core, Hashcat interaction in Hashcat, extractor adapters in Extractors, local JSON in Persistence, and view interaction in UI. UI code uses MVVM. All child processes must use separated arguments with no command shell. Add meaningful tests for command construction, parsing, validation, and adapters. Include the build/test result in your pull request.

Update CHANGELOG.md for material changes and README.md for changed setup or user-visible behavior. Maintain backward compatibility for saved configuration or provide a migration. Preserve licensing notices and never include third-party extractor source without an explicit compatible licensing decision.

Contributions are provided under the repository's MIT license. Submit small, reviewable pull requests describing the concrete problem and resulting behavior. Do not include real hashes, recovered passwords, private paths, or other secrets in issues, tests, screenshots, logs, or pull requests. Use synthetic test inputs.
