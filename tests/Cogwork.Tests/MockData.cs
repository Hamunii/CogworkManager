using static Cogwork.Core.GamePackageRepo;

namespace Cogwork.Tests;

static class MockData
{
    static readonly string data = """
[{
    "full_name": "MonoDetour-MonoDetour",
    "versions": [
        {
            "description": "Easy and convenient .NET detouring library, powered by MonoMod.RuntimeDetour.",
            "version_number": "0.7.9",
            "dependencies": [],
        }
    ]
},
{
    "full_name": "MonoDetour-MonoDetour_BepInEx_5",
    "versions": [
        {
            "description": "HarmonyX interop & BepInEx 5 logger integration for MonoDetour. Initializes MonoDetour early as a side effect.",
            "version_number": "0.7.9",
            "dependencies": [
                "BepInEx-BepInExPack-5.4.2100",
                "MonoDetour-MonoDetour-0.7.9"
            ],
        }
    ],
},
{
    "full_name": "Hamunii-AutoHookGenPatcher",
    "versions": [
        {
            "description": "Automatically generates MonoMod.RuntimeDetour.HookGen's MMHOOK files during the BepInEx preloader phase.",
            "version_number": "1.0.9",
            "dependencies": [
                "BepInEx-BepInExPack-5.4.2100",
                "Hamunii-DetourContext_Dispose_Fix-1.0.5"
            ],
        }
    ]
},
{
    "full_name": "PEAKModding-PEAKLib.Core",
    "versions": [
        {
            "description": "PEAKLib.Core.",
            "version_number": "1.0.1",
            "dependencies": [
                "MonoDetour-MonoDetour_BepInEx_5-0.7.9"
            ],
        },
        {
            "description": "PEAKLib.Core.",
            "version_number": "1.0.0",
            "dependencies": [
                "Hamunii-AutoHookGenPatcher-1.0.9"
            ],
        }
    ]
},
{
    "full_name": "PEAKModding-PEAKLib.Items",
    "versions": [
        {
            "description": "PEAKLib.Items.",
            "version_number": "1.0.1",
            "dependencies": [
                "PEAKModding-PEAKLib.Core-1.0.1"
            ],
        },
        {
            "description": "PEAKLib.Items.",
            "version_number": "1.0.0",
            "dependencies": [
                "PEAKModding-PEAKLib.Core-1.0.0"
            ],
        }
    ]
}]
""";

    public static GamePackageRepoList Test { get; } =
        new([
            new GamePackageRepo(
                new RepoThunderstoreHandler(
                    new Game()
                    {
                        Name = "test",
                        Slug = "test",
                        Platforms = new(),
                    }
                )
            ),
        ]);

    public static IEnumerable<Package> GetAllPackages()
    {
        if (!Test.Default.Import(data))
            throw new InvalidOperationException("This should not fail.");

        return Test.AllPackages;
    }
}
