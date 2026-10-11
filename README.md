# GamePinned Windows

Windows service and WPF app source for GamePinned, created by [sudoosama](https://sudoosama.com/). GamePinned development ended on September 19, 2026. This is the v2.0.5 development source, not a tested release. The last production version was v2.0.4.

GamePinned was built to protect selected Steam, Epic Games, and Riot games from uninstall attempts by blocked Windows accounts. Automatic detection of every launcher update and uninstall, with the required unlock and relock, was not completed. Do not treat this source as a complete protection guarantee.

## Build

Requires Windows and the .NET 10 SDK. From the repository root:

```powershell
dotnet publish .\GamePinnedService\GamePinnedService.csproj -c Release -r win-x64 -o .\publish\service
dotnet publish .\GamePinnedGUI\GamePinnedGUI.csproj -c Release -r win-x64 -o .\publish\gui --self-contained true
```

`GamePinnedService/` contains the Windows service, `GamePinnedGUI/` the WPF app, `Shared/` their shared code, and `tests/` deterministic regression projects. `installer.iss` is the Inno Setup script. No installer, credentials, production configuration, machine data, or website source is included.

Before using licensing, replace the signing key placeholder in `GamePinnedService/Core/LicenseManagerInternal.cs` with your own key and configure the same value as `SIGNING_KEY` in your backend. Replace the debug log public key placeholder in `GamePinnedService/Program.cs` with your own public key. The desktop client uses https://api.example.com as a placeholder. Point it at a backend you control before running it.

Copyright (c) 2026 sudoosama. Licensed under the MIT License. See [LICENSE](LICENSE). Steam, Epic Games, Riot Games, and Nvidia names and marks belong to their respective owners.
