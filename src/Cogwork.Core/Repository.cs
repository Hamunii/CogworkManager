namespace Cogwork.Core;

public class Repository
{
    static readonly Package[] packages =
    [
        new("MonoDetour", "MonoDetour", [new(new(0, 7, 9), []), new(new(0, 7, 8), [])]),
        new(
            "MonoDetour",
            "MonoDetour_BepInEx_5",
            [
                new(new(0, 7, 9), ["MonoDetour-MonoDetour-0.7.9"]),
                new(new(0, 7, 8), ["MonoDetour-MonoDetour-0.7.8"]),
            ]
        ),
        new(
            "PEAKLib",
            "PEAKLib.Core",
            [
                new(new(1, 0, 1), ["MonoDetour-MonoDetour_BepInEx_5-0.7.9"]),
                new(new(1, 0, 0), ["MonoDetour-MonoDetour_BepInEx_5-0.7.8"]),
            ]
        ),
        new(
            "PEAKLib",
            "PEAKLib.Items",
            [
                new(new(1, 0, 1), ["PEAKLib-PEAKLib.Core-1.0.1"]),
                new(new(1, 0, 0), ["PEAKLib-PEAKLib.Core-1.0.0"]),
            ]
        ),
        new("AuthorName", "ItemMod", [new(new(1, 0, 0), ["PEAKLib-PEAKLib.Items-1.0.0"])]),
    ];

    public static Package[] GetAllPackages() => packages;
}
