namespace Cogwork.Core;

public class Repository
{
    static readonly Package[] packages =
    [
        new(
            "MonoDetour-MonoDetour",
            x => [new(x, new(0, 7, 9), ["AuthorName-ItemMod-1.0.0"]), new(x, new(0, 7, 8), [])]
        ),
        new(
            "MonoDetour-MonoDetour_BepInEx_5",
            x =>
                [
                    new(x, new(0, 7, 9), ["MonoDetour-MonoDetour-0.7.9"]),
                    new(x, new(0, 7, 8), ["MonoDetour-MonoDetour-0.7.8"]),
                ]
        ),
        new("Hamunii-AutoHookGenPatcher", x => [new(x, new(1, 0, 0), [])]),
        new(
            "PEAKLib-PEAKLib.Core",
            x =>
                [
                    new(x, new(1, 0, 1), ["MonoDetour-MonoDetour_BepInEx_5-0.7.9"]),
                    new(x, new(1, 0, 0), ["Hamunii-AutoHookGenPatcher-1.0.0"]),
                ]
        ),
        new(
            "PEAKLib-PEAKLib.Items",
            x =>
                [
                    new(x, new(1, 0, 1), ["PEAKLib-PEAKLib.Core-1.0.1"]),
                    new(x, new(1, 0, 0), ["PEAKLib-PEAKLib.Core-1.0.0"]),
                ]
        ),
    ];

    public static Package[] GetAllPackages() => packages;
}
