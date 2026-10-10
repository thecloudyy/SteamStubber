# Steam Stubber

A tiny Windows tool that removes SteamStub DRM from your installed Steam games using [Steamless](https://github.com/atom0s/Steamless).

Pick an installed game from the dropdown (or type an AppID), hit **Unpack**, and every game `.exe` is run through Steamless. Files without DRM are left untouched. Redistributables, launchers, crash handlers, and anti-cheat executables are skipped automatically.

## Run

Requires Windows + admin rights (the app self-elevates via its manifest, since game files under `Program Files` need it). Download `SteamStubber.exe` from the latest release and run it.

## Build

```sh
dotnet build SteamStubber.csproj
```

Single-file exe:

```sh
dotnet publish SteamStubber.csproj -c Release -o publish
```
