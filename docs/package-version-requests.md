# Package Version Requests

This document defines the intended behavior of requesting specific package versions in a mod profile.

## Version Request Types

Package versions can be specified in multiple ways:

- `>=` — require in range higher or equal, prefer highest
  - Used implicitly if no request type is defined
- `=` — require exact
- `%` — inherited from dependant

Additionally, wildcards exist as `*` and evalute to the highest version available.

### Version Request Examples

- `>=*` — require any version, prefer highest
- `>=1.0.0` — require version `1.0.0` or higher, prefer highest
  - Dependencies in Thunderstore packages are evaluated as this
- `=1.0.0` — require version `1.0.0`
- `=*` — require highest version
  - Dependencies inherit the evaluated exact version, e.g. `=1.0.0`
- `%` — inherited from dependant, otherwise `>=*`

A warning should be displayed for packages whose dependency version requirements can't be satisfied.

## Simple Examples

The following packages have the available versions and dependencies:

- `Author-Foo`
  - `1.0.0` -> `Author-Bar-1.0.0`
  - `1.1.0` -> `Author-Bar-1.2.0`
  - `2.0.0` -> `Author-Bar-3.0.0`
- `Author-Bar`, versions: `1.0.0`, `1.2.0`, `3.0.0`, `4.0.0`

We will represent syntax for a version request with `<package-name>@<version-request>`, and the evaluated value in a comment `# <inherited-request-symbol> -> <evaluated-value>`:

### Simple Example: Wildcard

Note that `@*` is the same as `@>=*`.

```ini
[Added]
Author-Foo@* # >=2.0.0 -> 2.0.0 (Author-Bar-3.0.0)
[Dependencies]
Author-Bar@% # >=3.0.0 -> 4.0.0
```

### Simple Example: Exact

```ini
[Added]
Author-Foo@=1.1.0 # =1.1.0 (Author-Bar-1.2.0)
[Dependencies]
Author-Bar@% # =1.2.0
```

## Advanced Examples

The following packages have the available versions and dependencies:

- `Author-ModPackAlpha`
  - `1.0.0` -> `Author-Foo-1.1.0`, `Author-Bar-1.2.0`
  - `2.0.0` -> `Author-Foo-2.0.0`, `Author-Bar-3.0.0`
- `Author-ModPackBeta`
  - `1.0.0` -> `Author-Foo-1.0.0`, `Author-Bar-1.0.0`
  - `2.0.0` -> `Author-Foo-1.1.0`, `Author-Bar-4.0.0`
- `Author-Foo`
  - `1.0.0` -> `Author-Bar-1.0.0`
  - `1.1.0` -> `Author-Bar-1.2.0`
  - `2.0.0` -> `Author-Bar-3.0.0`
- `Author-Bar`, versions: `1.0.0`, `1.2.0`, `3.0.0`, `4.0.0`

### Advanced Example: Exact Wildcard

This example demonstrates how modpacks that require exact package versions for stability are completely supported.

```ini
[Added]
Author-ModPackAlpha@=* # =2.0.0 (Author-Foo-2.0.0, Author-Bar-3.0.0)
[Dependencies]
Author-Foo@% # =2.0.0
Author-Bar@% # =3.0.0
```

### Advanced Example: Conflict Resolution

We prefer the higher version if an exact version is required multiple times.

We will use `|` to show choosing between the available options.

```ini
[Added]
Author-ModPackBeta@=1.0.0 # =1.0.0 (Author-Foo-1.0.0, Author-Bar-1.0.0)
Author-ModPackAlpha@=* # =2.0.0 (Author-Foo-2.0.0, Author-Bar-3.0.0)
[Dependencies]
Author-Foo@% # =2.0.0 (=1.0.0 | =2.0.0)
Author-Bar@% # =3.0.0 (=1.0.0 | =3.0.0)
```

### Advanced Example: Conflict Resolution 2

When evaluating between `>=` and `=`, we decide with the following logic:

If `=` is in range of `>=`, use `=`. Otherwise pick maximum value of `>=` ([Min Vs Max value of >=](#min-vs-max-value-of-)).

```ini
[Added]
Author-ModPackBeta@* # >=2.0.0 -> 2.0.0 (Author-Foo-1.1.0, Author-Bar-4.0.0)
Author-ModPackAlpha@=* # =2.0.0 (Author-Foo-2.0.0, Author-Bar-3.0.0)
[Dependencies]
Author-Foo@% # =2.0.0 (>=1.1.0 | =2.0.0)
Author-Bar@% # >=4.0.0 -> 4.0.0 (>=4.0.0 | =3.0.0)
```

#### Min Vs Max value of >=

The end goal is to resolve a list of mods that are most likely to work best, together. This can be important for modpacks containing a large amount of packages.

However, as soon as we start adding other packages to a mod profile, we won't have a mod profile that has been tested to work like the modpack alone is. While the minimum of `>=` would be closer to the `=` required exact version, the minimum value of `>=` has not been tested to work well with the modpack.

Therefore there is little reason to do something other than resolve the highest version of a `>=` requested version range.
