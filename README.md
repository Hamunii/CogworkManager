# Cogwork Manager

A WIP concept of a generic mod package manager with:

- Dependency management where mods you explicitly add are separate from their dependencies
- Support for multiple package sources which are freely configurable (with [Thunderstore](<https://thunderstore.io/>) included by default)

> [!NOTE]  
> This may or may not ever become anything, depending on how useful it will be for me.

## Why

### Introduction

As a mod developer, I add dependencies to mods (along other things). What users see, is bloat added to their mod profile. Some may say that it's the users' fault for seeing the bloat. It is however certain that there is *something* here which manifests as "bloat".

Perhaps the "bloat" can't be entirely removed, but I'm sure that mod managers could weaken the manifestation of it with more sophisticated package management.

### Package Management Logic

NuGet is the package manager which is most familiar to me, so I'll use it as an example. In modern .NET or whatever, you list package references in your project configuration files, and those packages will be referenced in your project.

However, packages may have their own dependencies, so they will also be transitively referenced in your project. Here is though the important bit: the dependencies of the packages you add don't need to be added to your project configuration file.

The way this works means that even if you add a dependency to your project with has a ton of dependencies of its own, your project dependency configuration still stays clean and easily readable for you!

You know which packages you referenced yourself, and you can easily remove a package you added, and all orphaned dependencies are simply just removed. This is the experience I want.

### Current Situation

Ever since I realized the package management issues with mod managers, it has been on my mind that this should be improved. I have mentioned it to a few people, some developing mod managers, but there hasn't been a ton of interest in actually implementing package management as I've envisioned.

So, I've decided that I should just write a reference mod package manager, and if it's proven to work great, perhaps this way of handling packages in mod manager becomes more widely adopted.

A plus is that I find this a fun project to work on and it gives me experience writing apps like this, so I don't mind the wheel-reinventing that is going on in here.

### Other Reasons

Good package management is one thing. I may also solve some other problems I've had, like being dependant on Thunderstore. Sometimes there are obscure games that I just want to mod, but I need to install all my generic packages manually, which sucks.

With my own package manager, I can have my own package repositories where I could also have global packages for everything I need. Some of these being my .NET detouring library, [MonoDetour](<https://github.com/MonoDetour/MonoDetour>). This would also simplify sharing my mods for those obscure games.

The reason why I'd rather just create my own solution than use Thunderstore where I could use it, is that I don't think Thunderstore is a particularly good platform. However, it's generally less bad than the competition. I'm unlikely to create a competitor though, as this is mostly for fun.

## Architecture

This project will be split into 3 parts:

- **Cogwork.Core:** The package manager library (WIP)
- **Cogwork.Cli:** A command line interface (TODO)
- **Cogwork.Gui:** A graphical interface (TODO; with [Adwaita](<https://gnome.pages.gitlab.gnome.org/libadwaita/>))

### Cogwork.Core

This is where the shared backend logic lives for Cogwork Manager. I want as much as possible of the app to live here so that writing user interfaces for it is easy, and so that it can potentially be used as a library to write alternate mod manager UIs. Because, I'm not that into developing user interfaces, even if I like designing them.

This is also because I want a good CLI mod package manager for when I don't want to leave the terminal while developing mods, but I also want a GUI app for everything else.

This library is heavily WIP so everything about the implementation may change, and will probably be rewritten and documented once I have the full model figured out and working first.
