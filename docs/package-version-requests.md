# Package Version Requests

This document defines the intended behavior of requesting specific package versions in a mod profile.

## Version Request Types

Package versions can be specified in multiple ways:

- `>=` — require in range higher or equal, prefer highest
  - Used implicitly if no request type is defined
- `=` — require exact
- `%` — inherited from dependant

Additionally, wildcards exist as `*` and evalutes to the highest or lowest version available depending on the context.

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

```toml
[Added]
Author-Foo@* # 2.0.0 (Author-Bar-3.0.0)
[Dependencies]
Author-Bar@% # >= 3.0.0 -> 4.0.0
```

### Simple Example: Exact

```toml
[Added]
Author-Foo@=1.1.0 # 1.1.0 (Author-Bar-1.2.0)
[Dependencies]
Author-Bar@% # = -> 1.2.0
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

```toml
[Added]
Author-ModPackAlpha@=* # 2.0.0 (Author-Foo-2.0.0, Author-Bar-3.0.0)
[Dependencies]
Author-Foo@% # = -> 2.0.0
Author-Bar@% # = -> 3.0.0
```

### Advanced Example: Conflict Resolution

We prefer the higher version if an exact version is required multiple times.

We will use `|` to show choosing between the available options.

```toml
[Added]
Author-ModPackBeta@=1.0.0 # (Author-Foo-1.0.0, Author-Bar-1.0.0)
Author-ModPackAlpha@=* # 2.0.0 (Author-Foo-2.0.0, Author-Bar-3.0.0)
[Dependencies]
Author-Foo@% # = -> 2.0.0 (=1.0.0 | =2.0.0)
Author-Bar@% # = -> 3.0.0 (=1.0.0 | =3.0.0)
```

### Advanced Example: Conflict Resolution 2

When evaluating between `>=` and `=`, we decide with the following logic:

If `=` is in range of `>=`, use `=`. Otherwise pick minimum value of `>=`.

```toml
[Added]
Author-ModPackBeta@* # 2.0.0 (Author-Foo-1.1.0, Author-Bar-4.0.0)
Author-ModPackAlpha@=* # 2.0.0 (Author-Foo-2.0.0, Author-Bar-3.0.0)
[Dependencies]
Author-Foo@% # * -> 2.0.0 (>=1.1.0 | =2.0.0)
Author-Bar@% # * -> 4.0.0 (>=4.0.0 | =3.0.0)
```
