#!/usr/bin/env bash
flatpak-builder build-dir --user --download-only flatpak/io.github.hamunii.Cogwork.json --install-deps-from=flathub
python3 flatpak-dotnet-generator.py --dotnet 10 --freedesktop 26.08 flatpak/nuget-sources.json Cogwork.slnx
flatpak-builder build-dir --user --install flatpak/io.github.hamunii.Cogwork.json --install-deps-from=flathub --force-clean --ccache
