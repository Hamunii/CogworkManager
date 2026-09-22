# Cogwork Manager

A WIP generic mod package manager built for mod developers.

> [!CAUTION]  
> This project is not fit for consumption yet, and likely will not be for a long time. While this is technically very usable\*, I provide no support or promises in order to not make development harder. (if the app is broken for you, see [#Troubleshooting](#troubleshooting))
>
> \*Currently, this project only supports Linux and a few games on Thunderstore.  
> Launching games modded only works in specific scenarios, here's how to make it work for now:
>
> - Proton from Steam: Manually set `WINEDLLOVERRIDES="winhttp.dll=n,b" %command%`
> - Proton from direct: Manually download `https://github.com/Open-Wine-Components/umu-launcher` and add `umu-run` to your PATH
> - Native from Steam: Manually set `./run_bepinex.sh %command%`
> - Native from direct: Should work out of the box, assuming the game runs without Steam's Linux runtime
>
> Note that for now "direct" launch is only possible from the cli, via argument `--direct` for `cogman launch`.  
> See [TODO.md](./TODO.md) for more info on what's missing.

<details>
<summary>Why yet another mod manager?</summary>

## Why

### Introduction

As a mod developer, I add dependencies to mods (along other things). What users see, is bloat added to their mod profile. Some may say that it's the users' fault for seeing the bloat. It is however certain that there is *something* here which manifests as "bloat".

Perhaps the "bloat" can't be entirely removed, but I'm sure that mod managers could weaken the manifestation of it with more sophisticated package management.

### Package Management Logic

NuGet is the package manager which is most familiar to me, so I'll use it as an example. In modern .NET or whatever, you list package references in your project configuration files, and those packages will be referenced in your project.

However, packages may have their own dependencies, so they will also be transitively referenced in your project. Here is though the important bit: the dependencies of the packages you add don't need to be added to your project configuration file.

The way this works means that even if you add a dependency to your project with has a ton of dependencies of its own, your project dependency configuration still stays clean and easily readable for you!

You know which packages you referenced yourself, and you can easily remove a package you added, and all orphaned dependencies are simply just removed. This is the experience I want.

### Building a Package Manager

Ever since I realized the package management issues with mod managers, it has been on my mind that this should be improved. I have mentioned it to a few people, some developing mod managers, but there hasn't been a ton of interest in actually implementing package management as I've envisioned.

So, I've decided that I should just write a reference mod package manager, and if it's proven to work great, perhaps this way of handling packages in mod manager becomes more widely adopted.

A plus is that I find this a fun project to work on and it gives me experience writing apps like this, so I don't mind the wheel-reinventing that is going on in here.

### Other Reasons

Good package management is one thing. I may also solve some other problems I've had, like being dependant on Thunderstore. Sometimes there are obscure games that I just want to mod, but I need to install all my generic packages manually, which sucks.

With my own package manager, I can have my own package repositories where I could also have global packages for everything I need. Some of these being my .NET detouring library, [MonoDetour](<https://github.com/MonoDetour/MonoDetour>). This would also simplify sharing my mods for those obscure games.

The reason why I'd rather just create my own solution than use Thunderstore where I could use it, is that I don't think Thunderstore is a particularly good platform. However, it's generally less bad than the competition. I'm unlikely to create a competitor though, as this is mostly for fun.

</details>

## Features

- Dependencies are distinct from explicitly added packages
- Comprehensive command line interface for developers: `cogman`
- Support for multiple mod package sources
  - [Thunderstore](<https://thunderstore.io/>) is the default package source
  - Local package source for mod developers: [let `cogman` handle installing your mods](#local-package-source)
- Data is stored in small human-readable json files
  - No central database to break or desync by manually tampering with profile directories

### Local Package Source

Let Cogwork Manager handle installing your mods for you.

As a part of e.g. MSBuild, you can import your Thunderstore package to your local package source to be able to be installed like any other mod into any profile:

```sh
cogman sources local import "/path/to/package/root-or-zip"
```

> [!NOTE]  
> A proper MSBuild integration example will be provided at some point.

To use the local package source, enable it for your current profile:

```sh
cogman sources add local
```

Then add the package to your current profile:

```sh
cogman add MyTeam-MyMod
```

And select the package source to install from if it exists in multiple enabled sources:

```txt
  https://thunderstore.io/c/hollow-knight-silksong/
> cogman:sources/local
```

## Project Overview

Cogwork Manager is a project which is currently split into 3 parts:

- **Cogwork.Core:** The package manager library
- **Cogwork.Gui:** A GUI app named "Cogwork"
- **Cogwork.Cli:** A command line interface named "`cogman`"

### Cogwork.Core

This is the library that does all the package management magic. Both Cogwork.Gui and Cogwork.Cli use this backend and share the same data. This means that you can use whichever app you feel like, whenever.

This library is heavily WIP still so everything about the implementation may change, and will probably be rewritten and documented once I have the full model figured out and working first. So please do not attempt to use it yet.

### Cogwork.Gui (Cogwork)

A GUI for the mod manger, built for [GNOME](<https://www.gnome.org/>). It aims to be simple, intuitive, and effective for both mod developers and casual mod users. It tries to achieve this by following the [GNOME Human Interface Guidelines](<https://developer.gnome.org/hig/index.html>).

I can't make proper comparisons to other Thunderstore compatible mod managers like [r2modman](<https://github.com/ebkr/r2modmanPlus>) or [Gale](<https://github.com/Kesomannen/gale>) yet because Cogwork is missing some core features but currently Cogwork appears much simpler, and in theory it should continue this way.

### Cogwork.Cli (cogman)

This a CLI tool that focuses a lot on the user experience to try and make it easy-ish to use even for people who are not used to CLI tools.

The goal of cogman is to be a capable enough mod package manager so that you don't need to open a GUI app for managing mods. As of right now it's not quite there, and stuff like browsing for mods to install will be better done in a GUI app.

## Troubleshooting

If you have installed and run this previously and the app is broken, it's because of breaking changes in save data. To solve this, delete EVERYTHING from the following directories (note that this deletes all your data from the app):

- `~/.local/share/Hamunii.Cogwork/` - Profile save data & package install cache
- `~/.cache/Hamunii.Cogwork/` - External package source index caches & logs

It's also possible that sometimes I push code that doesn't work. This should be relatively rare, but things do break from time to time.

## AI Disclosure

Most of the code for **Cogwork.Gui** is AI generated, implementing exactly the UI and UX I've designed. Outside of this, there is very little AI use.

And do not fret: **Cogwork.Gui** is just an interface for **Cogwork.Core** which is the library where all the important backend logic lives. I've spent a lot of time writing and designing it using my brain.
