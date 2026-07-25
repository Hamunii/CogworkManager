# Design

This document defines the intended behavior for how Cogwork Manager should behave in various situations and edge cases.

## Target Audience

Cogwork Manager exists mainly for mod developers, so it should offer a lot of freedom with package sources and will trust the user knows what they are doing.

Usage as a general mod manager for average users is a secondary goal of Cogwork Manager, and will likely result in a "restricted" mode without the ability freely modify sources. Such a mode should only exist in a GUI app when/if it's developed, but should be the default configuration, but a "developer mode" toggle should be available.

## Design Philosophy

Cogwork Manager aims to be as perfect as it can be. When other mod package managers exist, there is little point in developing a "good enough" package manager.

This means that nearly every edge case should be properly thought out. This is why this document exists; I had trouble developing the package manager further because I didn't know how to resolve the many edge cases.

## Architecture

- ModList
  - Game
    - IModInstallRules
  - PackageSourceIndex
    - PackageSource[]
      - Package[]
        - PackageVersion[]

On a higher level:

- Game[]
  - ModList[]

### Notable Types

#### VisualPackageVersion

Textual representation of a package id which can be resolved into a PackageVersion which holds more metadata about the package.

## Definitions

- ModList/Profile: a mod profile which contains a list of added packages, resolved dependencies, lock file, files, and other configuration such as added sources
- Added package: package which is explicitly added into a profile by user
- Dependency package: package which is not explicitly added into a profile by user
- Source: a mod package repository, such as Thunderstore or a local package source
- Line starting by `?`: exact behavior is yet to be decided

## Behavior

### Package is removed from profile

? If new data was generated which we can detect (non-config files):

1. Ask if it should be deleted or kept
2. Always keep data
   1. In profile files where it already was
   2. But moved into backup files, and ask if user wants to recover data if package is added again. User can choose to clear all backup data per profile whenever.
3. Always delete data

Current preference: 2.ii.  
Current solution: 2.i.

### Added Package is disabled by user

- [ ] Treat package as removed, but make it easy to add back (data must remain intact)

### Added Package is removed from a repo

A package which is removed from a repository is likely either malware, or the author wanted to completely wipe a mod off a platform.

- [ ] Offer removal of the package, and block launching game without confirmation that user trusts the package. Warn user about potential malware.

### Added PackageVersion is removed from a repo

If only a specific PackageVersion is removed from a package, that PackageVersion was very likely malware.

- [ ] Offer removal of the package version, block launching game without confirmation that user trusts the package. Warn user about potential malware.

### Package updates and is about to override user's config file

- [ ] Don't override, but in a non-blocking warning indicate to user that they can resolve this conflict. Warning stays until it's resolved. Conflict resolution should optimally have a diff.

### Added Packages A and B ship same-named config file

?

### Package A and B from separate sources depend on non-added package C

- [ ] Allow giving sources a dominance score
- [ ] In case of equal dominance, warn user because dominance is undefined (either A or B is dominant)
