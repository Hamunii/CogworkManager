namespace Cogwork.Tests.Management;

public class ModListTests
{
    [Fact]
    public async Task PrintModListAsync()
    {
        /*
        foreach (var package in MockData.GetAllPackages())
        {
            Console.WriteLine(package);
        }

        var allPackages = MockData.GetAllPackages();

        var peaklibItems = allPackages.First(x => x is { Name: "PEAKLib.Items" });
        var peaklibCore = allPackages.First(x => x is { Name: "PEAKLib.Core" });

        var modList = PackageSource.ThunderstoreSilksong.GetModList("test");

        modList.Add(peaklibItems.Versions[^1]);
        Console.WriteLine(modList);

        modList.Add(peaklibCore);
        Console.WriteLine(modList);

        modList.Add(peaklibItems);
        Console.WriteLine(modList);

        var modList2 = PackageSource.ThunderstoreSilksong.GetModList("test2");
        var silksongPackages = await PackageSource.ThunderstoreSilksong.GetPackagesAsync();
        var bingoUI = silksongPackages.First(x => x is { Name: "BingoUI" });

        modList2.Add(bingoUI);
        Console.WriteLine(modList2);
        */
    }
}
