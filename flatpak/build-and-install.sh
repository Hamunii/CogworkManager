#!/usr/bin/env bash

# The C# language server or dev kit or whatever on vscode seems to completely
# give up if the '.flatpak-builder' cache exists in the root dir, so we do this.
cd ./artifacts

flatpak-builder flatpak --user --download-only ../flatpak/io.github.hamunii.Cogwork.yaml --install-deps-from=flathub
python3 flatpak-dotnet-generator.py --dotnet 10 --freedesktop 26.08 ../flatpak/nuget-sources.json ../Cogwork.slnx
flatpak-builder flatpak --user --install ../flatpak/io.github.hamunii.Cogwork.yaml --install-deps-from=flathub --force-clean --ccache
