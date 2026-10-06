# Flatpak

1. Install `flatpak-dotnet-generator.py` to repository root: `wget https://raw.githubusercontent.com/flatpak/flatpak-builder-tools/master/dotnet/flatpak-dotnet-generator.py`
2. Run `./flatpak/build-and-install.sh` from repository root

After the command finishes (it can take a while), Cogwork should be installed on your system and you should be able to find it.

> [!WARNING]  
> You may want to remove `./artifacts/.flatpak-builder/` and `./artifacts/flatpak/` after build because it seems that for example vscode starts doing whatever the fuck with these and starts using your CPU like crazy and stuff like the C# language server appear to be completely dead.
