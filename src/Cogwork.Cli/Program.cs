global using static Cogwork.Core.CogworkCoreLogger;
global using static Cogwork.Core.PackageSource;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using FuzzySharp;
using FuzzySharp.Extractor;
using FuzzySharp.SimilarityRatio;
using FuzzySharp.SimilarityRatio.Scorer.Composite;
using FuzzySharp.SimilarityRatio.Scorer.StrategySensitive;
using Gameloop.Vdf;
using Gameloop.Vdf.Linq;
using Spectre.Console;
using ZLinq;

namespace Cogwork.Cli;

static class Program
{
    static readonly Option<string> optionGameOverride = new("--game", "-g")
    {
        Description = "Override the active game to work on",
        Recursive = true,
    };
    static readonly Option<string> optionProfileOverride = new("--profile", "-p")
    {
        Description = "Override the active mod profile to work on",
        Recursive = true,
    };
    static readonly Option<bool> optionAssumeYes = new("--assume-yes", "-y")
    {
        Description = "Assumes yes on boolean questions",
    };
    static readonly Option<bool> optionAssumeNo = new("--assume-no", "-n")
    {
        Description = "Assumes no on boolean questions",
    };
    static readonly Option<bool> optionNoInteractive = new("--no-interactive", "-N")
    {
        Description = "Prevent requesting user input and fail if a prompt can't be answered",
        Recursive = true,
    };
    static readonly Option<bool> optionExactMatching = new("--exact-matching", "-E")
    {
        Description = "Only an exact match is picked implicitly",
    };

    static readonly Option<bool> optionDirectLaunch = new("--direct", "-d")
    {
        Description = "Launch game executable directly instead of via store app",
    };

    static readonly Option<bool> optionAttachedLaunch = new("--attached", "-a")
    {
        Description = "Keep game instance attached to the current process. Requires --direct",
    };

    static readonly Option<bool> optionDry = new("--dry")
    {
        Description = "Get output of a command without performing any task with consequences",
    };

    static async Task<int> Main(string[] args)
    {
        try
        {
            return await Initialize(args);
        }
        catch (Exception ex)
        {
            Cog.Fatal("Unhandled exception occurred:\n" + ex.ToString());
            return -1;
        }
    }

    /// <summary>
    /// Gets assumed boolean value or null and returns true,
    /// or adds an error and returns false if configuration is invalid.
    /// </summary>
    public static bool Assume(this SymbolResult? result, out bool? assumption)
    {
        var yes = result?.GetValue(optionAssumeYes) ?? false;
        var no = result?.GetValue(optionAssumeNo) ?? false;

        if (yes && no)
        {
            result?.AddError($"Options --assume-yes and --assume-no are mutually exclusive");
            assumption = null;
            return false;
        }

        if (yes)
            assumption = true;
        else if (no)
            assumption = false;
        else
            assumption = null;

        return true;
    }

    static async Task<int> Initialize(string[] argsFull)
    {
        Cog.Information($"CLI arguments: \"{string.Join("\", \"", argsFull)}\"");

        // I don't know how the fuck I'm supposed to do this with System.CommandLine
        var args = argsFull.AsValueEnumerable().TakeWhile(x => x is not "--").ToArray();
        var passthroughArgs = argsFull.AsValueEnumerable().Skip(args.Length + 1).ToArray();

        RootCommand rootCommand = new("Cogwork Manager CLI - mod package manager");

        Command game = new("game", "Select active game to mod or list available games");
        game.Aliases.Add("g");
        rootCommand.Subcommands.Add(game);
        {
            Command gameSelect = new("select", "Select active game to mod");
            gameSelect.Aliases.Add("s");
            gameSelect.Options.Add(optionExactMatching);
            gameSelect.Options.Add(optionAssumeYes);
            gameSelect.Options.Add(optionAssumeNo);
            game.Subcommands.Add(gameSelect);
            {
                Argument<string> gameSelectArgument = new("game")
                {
                    Description = "Name of a game which is supported",
                    // This is to not require quotation marks.
                    Arity = ArgumentArity.OneOrMore,
                    CustomParser = r => string.Join(' ', r.Tokens.Select(t => t.Value)),
                };
                gameSelectArgument.Validators.Add(SelectGame);
                gameSelect.Arguments.Add(gameSelectArgument);
            }

            Command gameList = new("list", "List all supported games");
            gameList.Aliases.Add("l");
            game.Subcommands.Add(gameList);
            {
                gameList.SetAction(parse =>
                {
                    foreach (var game in Game.SupportedGames)
                    {
                        Console.WriteLine($"{game.Name}");
                    }
                });
            }
        }

        Command status = new("status", "Display active game and mod profile");
        status.Aliases.Add("st");
        rootCommand.Subcommands.Add(status);
        {
            status.SetAction(parse =>
            {
                PrintGameAndProfile();
            });
        }

        Command profile = new("profile", "Manage mod profiles");
        profile.Aliases.Add("p");
        profile.Options.Add(optionGameOverride);
        rootCommand.Subcommands.Add(profile);
        {
            Command profileSelect = new(
                "select",
                "Select mod profile for the active game. Leave argument empty to select from a list"
            );
            profileSelect.Aliases.Add("s");
            profileSelect.Options.Add(optionExactMatching);
            profileSelect.Options.Add(optionAssumeYes);
            profileSelect.Options.Add(optionAssumeNo);
            profile.Subcommands.Add(profileSelect);
            {
                Argument<string> profileSelectArgument = new("profile?")
                {
                    Description = "Name of profile or empty to select from a list",
                    Arity = ArgumentArity.ZeroOrMore,
                    CustomParser = r => string.Join(' ', r.Tokens.Select(t => t.Value)),
                };
                profileSelect.Arguments.Add(profileSelectArgument);
                profileSelect.Validators.Add(
                    (Action<CommandResult>)(
                        result =>
                        {
                            if (!TryGetActiveGame(result, out Game? game))
                                return;

                            AnsiConsole.MarkupLineInterpolated(
                                CultureInfo.InvariantCulture,
                                $"Performing for game: [purple]{game.Name}[/]"
                            );

                            var profiles = game.GetProfiles().Select(x => x.profile).ToArray();
                            LazyModList? selected;

                            if (result.GetValue(profileSelectArgument) is { } argument)
                            {
                                if (!TryGetModList(result, argument, profiles, out selected))
                                {
                                    return;
                                }
                            }
                            else
                            {
                                var choice = AnsiConsole.Prompt(
                                    new SelectionPrompt<string>()
                                        .Title("Select a [green]profile[/]:")
                                        .AddChoices(
                                            profiles.Select(x => x.DisambiguatedDisplayName)
                                        )
                                );

                                selected = profiles.First(x =>
                                    x.DisambiguatedDisplayName == choice
                                );
                            }

                            game.Config.ActiveProfile = selected;
                            game.Config.Save(game.GameConfigLocation, JsonGen.Default.GameConfig);

                            Cog.Debug($"selected {selected.DisambiguatedDisplayName}");
                            AnsiConsole.MarkupLineInterpolated(
                                CultureInfo.InvariantCulture,
                                $"Selected profile: [blue]{selected.DisambiguatedDisplayName}[/]"
                            );
                        }
                    )
                );
            }

            Command profileList = new("list", "List all your profiles for the active game");
            profileList.Aliases.Add("l");
            profile.Subcommands.Add(profileList);
            {
                profileList.Validators.Add(result =>
                {
                    Game? game;
                    if (!TryGetActiveGame(result, out game))
                        return;

                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[green]Listing profiles for:[/] [blue]{game.Name}[/]"
                    );

                    var activeProfile = game.Config.ActiveProfile;

                    foreach (var (profile, nameCollision) in game.GetProfiles())
                    {
                        if (nameCollision)
                        {
                            AnsiConsole.MarkupInterpolated(
                                CultureInfo.InvariantCulture,
                                $"[gray]{profile.DisplayName}[/] [gray]([/]{profile.Id}[gray])[/]"
                            );
                        }
                        else
                        {
                            AnsiConsole.MarkupInterpolated(
                                CultureInfo.InvariantCulture,
                                $"{profile.DisplayName}"
                            );
                        }

                        if (profile == activeProfile)
                        {
                            AnsiConsole.MarkupLineInterpolated(
                                CultureInfo.InvariantCulture,
                                $" [blue][[Active]][/]"
                            );
                        }
                        else
                        {
                            AnsiConsole.WriteLine();
                        }
                    }
                });
            }
        }

        Command mods = new("mods", "Manage mods on a mod profile");
        mods.Aliases.Add("m");
        mods.Options.Add(optionGameOverride);
        mods.Options.Add(optionProfileOverride);
        rootCommand.Subcommands.Add(mods);
        {
            Command modsAdd = new("add", "Add mods to a profile");
            modsAdd.Aliases.Add("a");
            mods.Subcommands.Add(modsAdd);
            Argument<string> modsAddArgument = new("package")
            {
                Description = "Names of packages separated by space",
                Arity = ArgumentArity.OneOrMore,
                CustomParser = r => string.Join(' ', r.Tokens.Select(t => t.Value)),
            };
            modsAdd.Arguments.Add(modsAddArgument);
            modsAdd.Validators.Add(result =>
            {
                if (!result.Assume(out var assumption))
                    return;

                if (!TryGetActiveGameAndProfile(result, out var game, out var lazyProfile))
                    return;

                var searches = result.GetValue(modsAddArgument)?.Split(' ');
                if (searches is null || searches.Length != 1)
                {
                    result.AddError("Packages to search must be 1 for now");
                    return;
                }
                var profile = lazyProfile.LoadAsync().Result;
                var packages = profile.SourceIndex.GetAllPackagesAsync().Result;
                if (
                    !TryFuzzySearch(
                        result,
                        searches[0],
                        packages.Select(x => x.FullName),
                        out var selected
                    )
                )
                {
                    return;
                }

                if (
                    !Package.TryGetPackage(
                        profile.SourceIndex,
                        selected,
                        out var package,
                        hasVersion: false,
                        out _,
                        out _
                    )
                )
                {
                    throw new UnreachableException("Package name wasn't found.");
                }

                if (profile.Add(package))
                {
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"Added package [green]{package.Latest}[/]"
                    );
                }
                else
                {
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"Package [blue]{package.Latest}[/] is already added"
                    );
                }
            });

            Command modsRemove = new("remove", "Remove mods from a profile");
            modsRemove.Aliases.Add("r");
            mods.Subcommands.Add(modsRemove);
            Argument<string> modsRemoveArgument = new("packages?")
            {
                Description = "Limit selection of packages to remove by name separated by spaces",
                Arity = ArgumentArity.ZeroOrMore,
                CustomParser = r => string.Join(' ', r.Tokens.Select(t => t.Value)),
            };
            modsRemove.Arguments.Add(modsRemoveArgument);
            modsRemove.Validators.Add(result =>
            {
                if (!result.Assume(out var assumption))
                    return;

                if (!TryGetActiveGameAndProfile(result, out var game, out var lazyProfile))
                    return;

                var profile = lazyProfile.LoadAsync().Result;
                var added = profile.Added.Select(x => x.Key).ToList();

                var searches = result.GetValue(modsRemoveArgument)?.Split(' ');

                List<Package> removable = searches is null ? added : [];
                List<string> matches = [];
                foreach (var search in searches ?? [])
                {
                    var score = FilterBestResults(
                        FuzzySharp.Process.ExtractTop(
                            search,
                            added.Select(x => x.FullName),
                            processor: s => s,
                            cutoff: 60,
                            scorer: ScorerCache.Get<WeightedRatioScorer>()
                        ),
                        ref matches
                    );

                    foreach (var m in matches)
                    {
                        if (
                            !Package.TryGetPackage(
                                profile.SourceIndex,
                                m,
                                out var package,
                                hasVersion: false,
                                out _,
                                out _
                            )
                        )
                        {
                            throw new UnreachableException();
                        }
                        removable.Add(package);
                    }
                }

                if (removable.Count == 0)
                {
                    result.AddError("No matching packages");
                    return;
                }

                var packagesToRemove = AnsiConsole.Prompt(
                    new MultiSelectionPrompt<Package>()
                        .Title("Select [green]packages[/] to remove")
                        .UseConverter(x => x.FullName)
                        .AddChoices(removable)
                );

                profile.Remove(packagesToRemove);

                AnsiConsole.WriteLine("Removed:");
                foreach (var item in packagesToRemove)
                {
                    AnsiConsole.WriteLine($"- {item.ToStringSimpleWithSource()}");
                }
                return;
            });

            Command modsList = new("list", "List mods on a profile");
            modsList.Aliases.Add("l");
            mods.Subcommands.Add(modsList);
            modsList.Validators.Add(result =>
            {
                if (!TryGetActiveGameAndProfile(result, out _, out var lazyProfile))
                    return;

                if (lazyProfile.ResolvedAdded is null || lazyProfile.ResolvedDependencies is null)
                {
                    _ = lazyProfile.LoadAsync().Result;
                }

                AnsiConsole.MarkupLine($"[gray][[Context]][/]");
                PrintGameAndProfile(hideModListHelp: true);

                AnsiConsole.MarkupLine($"\n[blue][[Added Mods]][/]");

                foreach (
                    var added in lazyProfile
                        .ResolvedAdded!.AsValueEnumerable()
                        .Select(x => new VisualPackageVersion(x))
                )
                {
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"- [white]{added}[/]"
                    );
                }

                AnsiConsole.MarkupLine($"\n[blue][[Dependencies of Added Mods]][/]");

                foreach (
                    var added in lazyProfile
                        .ResolvedDependencies!.AsValueEnumerable()
                        .Select(x => new VisualPackageVersion(x))
                )
                {
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"- [gray]{added}[/]"
                    );
                }
            });

            Command modsUpdate = new("update", "Update mods on a profile");
            modsUpdate.Aliases.Add("u");
            mods.Subcommands.Add(modsUpdate);
            modsUpdate.Validators.Add(result =>
            {
                if (!TryGetActiveGameAndProfile(result, out var game, out var lazyProfile))
                    return;

                var progress = AnsiConsole.Progress();
                var progressResult = progress
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new DownloadedColumn(),
                        new TransferSpeedColumn(),
                        new RemainingTimeColumn()
                    )
                    .StartAsync(async ctx =>
                    {
                        bool fetchedAny = false;

                        var profile = await lazyProfile.LoadAsync(packageSource =>
                            new(
                                ctx.AddTask($"Fetching {packageSource}", maxValue: 0)
                                    .IsIndeterminate(),
                                (task, contentLength) =>
                                {
                                    fetchedAny = true;
                                    if (contentLength is null || task is not ProgressTask pTask)
                                        return;

                                    pTask.IsIndeterminate(false).MaxValue = (double)contentLength;
                                }
                            )
                        );

                        // This is for printing output after everything is downloaded.
                        var whatHappened = profile
                            .AllPackages.AsValueEnumerable()
                            .Select(x =>
                                (
                                    oldVersion: x.Value,
                                    newVersion: x.Key.Latest,
                                    newWasAlreadyDownloaded: x.Key.Latest.IsDownloaded()
                                )
                            )
                            .ToArray();

                        profile.UpdatePackages();

                        var toDownload = profile
                            .AllPackages.AsValueEnumerable()
                            .Where(x => !x.Value.IsDownloaded());

                        var downloadTasks = toDownload
                            .Select(x =>
                            {
                                var task = ctx.AddTask(x.Value.ToString(), maxValue: 0)
                                    .IsIndeterminate();

                                // The C# compiler kinda dies if this is a direct lambda that is returned I think.
                                async Task<bool> Download()
                                {
                                    var isSuccess = await x.Key.Source.Service.DownloadPackageAsync(
                                        x.Value,
                                        new(
                                            task,
                                            (task, contentLength) =>
                                            {
                                                if (
                                                    contentLength is null
                                                    || task is not ProgressTask pTask
                                                )
                                                    return;

                                                pTask.IsIndeterminate(false).MaxValue =
                                                    (double)contentLength;
                                            }
                                        )
                                    );

                                    return isSuccess;
                                }
                                return Download();
                            })
                            .ToArray();

                        Task.WaitAll(downloadTasks);

                        if (downloadTasks.Length == 0 && !fetchedAny)
                        {
                            progress.AutoClear(true);
                        }

                        return (
                            tasksCount: downloadTasks.Length,
                            allSuccess: downloadTasks.Select(x => x.Result).All(x => x is true),
                            oldUpdatedToLatest: whatHappened
                        );
                    })
                    .Result;

                bool printedAny = false;

                if (!progressResult.allSuccess)
                {
                    printedAny = true;
                    AnsiConsole.MarkupLine("[red]Some updates failed[/]");
                }

                var updated = progressResult
                    .oldUpdatedToLatest.AsValueEnumerable()
                    .Where(x => x.oldVersion.Version != x.newVersion.Version);

                if (updated.Any())
                {
                    printedAny = true;
                    AnsiConsole.MarkupLine("[green]Updated mods:[/]");
                    foreach (var (oldVersion, newVersion, _) in updated)
                    {
                        var packageId = oldVersion.Package.ToStringSimpleWithSource();
                        AnsiConsole.MarkupLineInterpolated(
                            CultureInfo.InvariantCulture,
                            $"- {packageId} {oldVersion.Version} [yellow]→[/] [white]{newVersion.Version}[/]"
                        );
                    }
                }

                var downloaded = progressResult
                    .oldUpdatedToLatest.AsValueEnumerable()
                    .Where(x =>
                        !x.newWasAlreadyDownloaded && x.oldVersion.Version == x.newVersion.Version
                    );

                if (downloaded.Any())
                {
                    printedAny = true;
                    AnsiConsole.MarkupLine("[green]Downloaded mods:[/]");
                    foreach (var (_, newVersion, _) in downloaded)
                    {
                        AnsiConsole.MarkupLineInterpolated(
                            CultureInfo.InvariantCulture,
                            $"- [white]{newVersion}[/]"
                        );
                    }
                }

                if (!printedAny)
                {
                    AnsiConsole.MarkupLine("[green]Everything is already up-to-date[/]");
                }
            });

            // For testing mostly, functionality is temporary
            Command modsSync = new("sync", "Sync mods on a profile");
            mods.Subcommands.Add(modsSync);
            modsSync.Validators.Add(result =>
            {
                _ = SyncProfilePackages(result).Result;
            });
        }

        Command source = new("sources", "Manage package sources");
        source.Aliases.Add("so");
        source.Options.Add(optionGameOverride);
        source.Options.Add(optionProfileOverride);
        rootCommand.Subcommands.Add(source);
        {
            Command sourceAdd = new("add", "Add package source");
            sourceAdd.Aliases.Add("a");
            source.Subcommands.Add(sourceAdd);

            Command sourceRemove = new("remove", "Remove package source");
            sourceRemove.Aliases.Add("r");
            source.Subcommands.Add(sourceRemove);
        }

        Command launch = new("launch", "Launch current game with active mod profile");
        launch.Options.Add(optionGameOverride);
        launch.Options.Add(optionProfileOverride);
        launch.Options.Add(optionDirectLaunch);
        launch.Options.Add(optionAttachedLaunch);
        launch.Options.Add(optionDry);
        rootCommand.Subcommands.Add(launch);
        {
            launch.Validators.Add(result =>
            {
                if (!TryGetActiveGameAndProfile(result, out var game, out var lazyProfile))
                    return;

                if (!result.GetValue(optionDry))
                {
                    _ = SyncProfilePackages(result).Result;
                }

                var error = lazyProfile.PrepareModLoader(game);
                if (error is { })
                {
                    result.AddError(error);
                    return;
                }

                if (result.GetValue(optionAttachedLaunch) && !result.GetValue(optionDirectLaunch))
                {
                    result.AddError(
                        $"Option '{optionAttachedLaunch.Name}' requires '{optionDirectLaunch.Name}'"
                    );
                    return;
                }
            });

            launch.SetAction(
                async Task<int> (result, ct) =>
                {
                    if (!TryGetActiveGameAndProfile(null, out var game, out var lazyProfile))
                        return 1;

                    var steamExePath = GetExecutablePath("steam");
                    if (steamExePath is not { } s)
                    {
                        AnsiConsole.WriteLine("Steam not found");
                        return 1;
                    }
                    if (game.Platforms.Steam is not { } steam)
                    {
                        AnsiConsole.WriteLine($"Game '{game.Name}' is not on steam.");
                        return 1;
                    }

                    AnsiConsole.MarkupLine("[green]Launching game[/]");

                    var args = game.InstallRules.GetLaunchArguments(lazyProfile);
                    bool waitForProcess = false;

                    ProcessStartInfo startInfo;
                    if (result.GetValue(optionDirectLaunch))
                    {
                        var isProton = lazyProfile.IsProton();
                        string? umuPath = null;
                        if (isProton)
                        {
                            umuPath = GetExecutablePath("umu-run");
                            if (umuPath is null)
                            {
                                AnsiConsole.MarkupLine(
                                    """
                                    [red]umu-launcher must be installed to launch Windows games![/]
                                    [yellow bold]TO FIX:[/]
                                    [white]1. Download [/][blue]https://github.com/Open-Wine-Components/umu-launcher[/]
                                    [white]2. Extract it and add 'umu-run' to your PATH[/]
                                    """
                                );
                                return 1;
                            }
                        }

                        var attached = result.GetValue(optionAttachedLaunch);
                        if (isProton && !attached)
                        {
                            attached = true;
                            AnsiConsole.MarkupLine(
                                "[yellow]Force-attaching (--attached) process because umu-run is used[/]"
                            );
                        }
                        if (attached)
                        {
                            waitForProcess = true;

                            if (isProton)
                                startInfo = new(umuPath!, args);
                            else
                                startInfo = new(args[0], args.Skip(1));

                            startInfo.WorkingDirectory = lazyProfile.GetGamePathOrThrow();
                        }
                        else
                        {
                            var setsid = GetExecutablePath("setsid");
                            if (setsid is null)
                            {
                                AnsiConsole.WriteLine("setsid not found");
                                return 1;
                            }

                            startInfo = new(setsid, args)
                            {
                                WorkingDirectory = lazyProfile.GetGamePathOrThrow(),
                                RedirectStandardInput = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            };
                        }
                    }
                    else
                    {
                        var setsid = GetExecutablePath("setsid");
                        if (setsid is null)
                        {
                            AnsiConsole.WriteLine("setsid not found");
                            return 1;
                        }

                        AnsiConsole.MarkupLineInterpolated(
                            CultureInfo.InvariantCulture,
                            $"[green]Note: Steam may take a moment to start[/]"
                        );

                        startInfo = new(setsid, [steamExePath])
                        {
                            WorkingDirectory = lazyProfile.GetGamePathOrThrow(),
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        startInfo.ArgumentList.Add("-applaunch");
                        startInfo.ArgumentList.Add(steam.Id.ToString(CultureInfo.InvariantCulture));

                        foreach (var arg in args)
                        {
                            startInfo.ArgumentList.Add(arg);
                        }
                    }

                    if (lazyProfile.IsProton())
                    {
                        var kvp = ("WINEDLLOVERRIDES", "winhttp=n,b");
                        startInfo.EnvironmentVariables.Add(kvp.Item1, kvp.Item2);
                        AnsiConsole.MarkupLineInterpolated(
                            CultureInfo.InvariantCulture,
                            $"[blue]Set environment variable:[/] {kvp.Item1}=\"{kvp.Item2}\""
                        );
                    }

                    foreach (var arg in passthroughArgs)
                    {
                        startInfo.ArgumentList.Add(arg);
                    }

                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[blue]Launch arguments:[/] {startInfo.FileName} \"{string.Join("\" \"", startInfo.ArgumentList)}\""
                    );

                    if (result.GetValue(optionDry))
                    {
                        WriteDryRunMessage();
                        return 0;
                    }

                    var process = System.Diagnostics.Process.Start(startInfo);
                    if (process is null)
                    {
                        AnsiConsole.WriteLine("Game process could not be started.");
                        return 1;
                    }
                    if (waitForProcess)
                    {
                        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                        {
                            process.Kill();
                        };

                        await process.WaitForExitAsync(ct);
                    }

                    return 0;
                }
            );
        }

        rootCommand.Add(optionNoInteractive);

        var result = rootCommand.Parse(args);
        return await result.InvokeAsync();
    }

    static void WriteDryRunMessage()
    {
        AnsiConsole.WriteLine("Dry run complete.");
    }

    static async Task<bool> SyncProfilePackages(CommandResult result)
    {
        if (!TryGetActiveGameAndProfile(result, out var game, out var lazyProfile))
            return false;

        var progress = AnsiConsole.Progress();
        await progress
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn()
            )
            .StartAsync(async ctx =>
            {
                bool doneAnything = false;

                var profile = await lazyProfile.LoadAsync(packageSource =>
                    new(
                        ctx.AddTask($"Fetching {packageSource}", maxValue: 0).IsIndeterminate(),
                        (task, contentLength) =>
                        {
                            doneAnything = true;
                            if (contentLength is null || task is not ProgressTask pTask)
                                return;

                            pTask.IsIndeterminate(false).MaxValue = (double)contentLength;
                        }
                    )
                );

                _ = await profile.DownloadPackagesAsync(packageVersion =>
                {
                    return new(
                        null,
                        (task, contentLength) =>
                        {
                            if (contentLength is null || task is not ProgressTask pTask)
                                return;

                            pTask.IsIndeterminate(false).MaxValue = (double)contentLength;
                        },
                        () =>
                        {
                            doneAnything = true;
                            return ctx.AddTask(packageVersion.ToString(), maxValue: 0)
                                .IsIndeterminate();
                        }
                    );
                });

                _ = profile.InstallPackagesAsync().Result;

                if (!doneAnything)
                {
                    progress.AutoClear = true;
                }
            });

        return true;
    }

    private static bool TryGetModList(
        SymbolResult? result,
        string argument,
        LazyModList[] profiles,
        [NotNullWhen(true)] out LazyModList? selected
    )
    {
        selected = profiles.FirstOrDefault(x => x.DisambiguatedDisplayName == argument);
        if (selected == default)
        {
            var names = profiles.Select(x => x.DisambiguatedDisplayName).ToArray();

            if (!TryVeryFuzzySearch(result, argument, names, out var name))
                return false;

            selected = profiles.First(x => x.DisambiguatedDisplayName == name);
        }

        return true;
    }

    public static string? GetExecutablePath(string exeName)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, exeName);

            if (!File.Exists(fullPath))
                continue;

            return exeName;
        }

        return null;
    }

    private static void PrintGameAndProfile(bool hideModListHelp = false)
    {
        var activeGame = Game.GlobalConfig.ActiveGame;
        var activeGameName = activeGame?.Name ?? "<none>";

        var activeProfile = activeGame?.Config.ActiveProfile;
        // Ensure disambiguated name is accurate.
        _ = activeGame?.GetProfiles();
        var modProfileName = activeProfile?.DisambiguatedDisplayName ?? "<none>";

        AnsiConsole.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"""
            Active game: [blue]{activeGameName}[/] [gray]?: cogman game (select | list)[/]
            Mod profile: [blue]{modProfileName}[/] [gray]?: cogman profile (select | list)[/]
            """
        );

        if (activeProfile is { } profile)
        {
            if (profile.ResolvedAdded is null || profile.ResolvedDependencies is null)
            {
                _ = profile.LoadAsync().Result;
            }
            var added = profile.ResolvedAdded!.Count;
            var deps = profile.ResolvedDependencies!.Count;
            AnsiConsole.MarkupInterpolated(
                CultureInfo.InvariantCulture,
                $"""
                Mods: {added} Added, {deps} Dependencies | {added
                    + deps} Total
                """
            );

            if (!hideModListHelp)
            {
                AnsiConsole.Markup(" [gray]?: cogman mods (add | remove | list)[/]");
            }

            AnsiConsole.WriteLine();
        }
    }

    private static bool TryGetActiveGameAndProfile(
        CommandResult? result,
        [NotNullWhen(true)] out Game? game,
        [NotNullWhen(true)] out LazyModList? profile
    )
    {
        profile = default;

        if (!TryGetActiveGame(result, out game))
            return false;

        if (!TryGetActiveProfile(result, game, out profile))
            return false;

        return true;
    }

    private static bool TryGetActiveGame(
        this SymbolResult? result,
        [NotNullWhen(true)] out Game? game
    )
    {
        if (result?.GetValue(optionGameOverride) is { } overrideGame)
        {
            if (!result.TryGetGame(overrideGame, out game))
            {
                result.AddError($"Overridden game '{overrideGame}' is not found.");
                return false;
            }
            return true;
        }
        if (Game.GlobalConfig.ActiveGame is not { } activeGame)
        {
            game = default;
            result?.AddError("An active game is not selected. Use 'cogman game select <game>'.");
            return false;
        }

        game = activeGame;
        return true;
    }

    private static bool TryGetActiveProfile(
        this SymbolResult? result,
        Game game,
        [NotNullWhen(true)] out LazyModList? profile
    )
    {
        if (result?.GetValue(optionProfileOverride) is { } overrideProfile)
        {
            var profiles = game.GetProfiles().Select(x => x.profile).ToArray();

            if (!TryGetModList(result, overrideProfile, profiles, out profile))
            {
                result.AddError($"Overridden profile '{overrideProfile}' is not found.");
                return false;
            }

            return true;
        }
        if (game.Config.ActiveProfile is not { } activeProfile)
        {
            profile = default;
            result?.AddError(
                "An active profile is not selected. Use 'cogman profile select <profile?>'."
            );
            return false;
        }

        profile = activeProfile;
        return true;
    }

    static List<(LazyModList profile, bool hasCollision)> GetProfiles(this Game game)
    {
        LazyModList[] profiles = [.. game.EnumerateProfiles()];
        profiles.Sort(
            static (a, b) => StringComparer.Ordinal.Compare(a.DisplayName, b.DisplayName)
        );

        Dictionary<string, int> displayNameToProfileCount = [];

        foreach (var profile in profiles)
        {
            ref var num = ref CollectionsMarshal.GetValueRefOrAddDefault(
                displayNameToProfileCount,
                profile.DisplayName,
                out var exists
            );
            num++;
        }

        List<(LazyModList profile, bool hasCollision)> values = [];
        foreach (var profile in profiles)
        {
            var nameCollision = displayNameToProfileCount[profile.DisplayName] > 1;
            if (nameCollision)
            {
                profile.DisambiguatedDisplayName = $"{profile.DisplayName} ({profile.Id})";
            }
            values.Add((profile, nameCollision));
        }
        return values;
    }

    private static void SelectGame(ArgumentResult result)
    {
        var search = result.GetValueOrDefault<string>();
        if (!TryGetGame(result, search, out var selectedGame))
            return;

        AnsiConsole.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"Selected game: [blue]{selectedGame.Name}[/]"
        );

        Game.GlobalConfig.ActiveGame = selectedGame;
        if (!selectedGame.EnumerateProfiles().Any())
        {
            selectedGame.Config.ActiveProfile = ModList.CreateNew(selectedGame, "Default");
            selectedGame.Config.Save(selectedGame.GameConfigLocation, JsonGen.Default.GameConfig);
        }
        return;
    }

    private static bool TryGetGame(
        this SymbolResult result,
        string search,
        [NotNullWhen(true)] out Game? selectedGame
    )
    {
        if (!Game.NameToGame.TryGetValue(search, out selectedGame))
        {
            var games = Game.SupportedGames.Select(x => x.Name.ToLowerInvariant()).ToArray();
            if (!TryVeryFuzzySearch(result, search, games, out var selected))
                return false;

            selectedGame = Game.NameToGame[selected];
        }

        return true;
    }

    private static bool TryFuzzySearch(
        SymbolResult result,
        string toSelect,
        IEnumerable<string> searchList,
        [NotNullWhen(true)] out string? selectedValue
    )
    {
        selectedValue = default;

        if (!result.Assume(out var assumption))
            return false;

        var noInteractive = result.GetValue(optionNoInteractive);
        var exactMatching = result.GetValue(optionExactMatching);

        List<string> best = [];
        var score = FilterBestResults(
            FuzzySharp.Process.ExtractTop(
                toSelect,
                searchList,
                processor: s => s,
                cutoff: 60,
                scorer: ScorerCache.Get<WeightedRatioScorer>()
            ),
            ref best
        );

        return SelectFuzzySearch(
            result,
            toSelect,
            out selectedValue,
            assumption,
            best,
            best,
            score
        );
    }

    private static bool TryVeryFuzzySearch(
        SymbolResult? result,
        string toSelect,
        IEnumerable<string> searchList,
        [NotNullWhen(true)] out string? selected
    )
    {
        if (!Assume(result, out var assumption))
        {
            selected = default;
            return false;
        }

        List<string> best = [];
        IEnumerable<string>? common = null;
        var score = FilterBestResults(
            FuzzySharp.Process.ExtractTop(
                toSelect,
                searchList,
                processor: s => s,
                cutoff: 40,
                scorer: ScorerCache.Get<WeightedRatioScorer>()
            ),
            ref best
        );
        if (best.Count != 1 || !IsAutoAccept(score))
        {
            List<string> best2 = [];

            var score2 = FilterBestResults(
                FuzzySharp.Process.ExtractTop(
                    toSelect,
                    searchList,
                    processor: s => s,
                    scorer: ScorerCache.Get<PartialTokenAbbreviationScorer>()
                ),
                ref best2
            );

            if (best2.Count == 1 && IsAutoAccept(score2))
            {
                score = score2;
                best = best2;
            }
            else
            {
                List<string> best3 = [];

                var score3 = FilterBestResults(
                    FuzzySharp.Process.ExtractTop(
                        toSelect,
                        searchList,
                        processor: s => s,
                        cutoff: 60,
                        scorer: ScorerCache.Get<TokenInitialismScorer>()
                    ),
                    ref best3
                );

                if (best3.Count == 1 && IsAutoAccept(score3))
                {
                    score = score3;
                    best = best3;
                }
                else
                {
                    var common2 = best.Intersect(best2);
                    if (common2.Any())
                    {
                        score = Math.Max(score, score2);
                        common = common2.Union(best3);
                    }
                    else
                    {
                        common = best.Union(best3);
                    }
                    score = Math.Max(score, score3);

                    if (!common.Any())
                    {
                        Cog.Debug("No matches in common, using first match.");
                        common = best;
                    }
                    else if (common.Count() == 1)
                    {
                        Cog.Debug("Matches in common is 1, using that.");
                        best = [.. common];
                    }
                }
            }
        }

        return SelectFuzzySearch(
            result,
            toSelect,
            out selected,
            assumption,
            best,
            common ?? best,
            score
        );
    }

    private static bool SelectFuzzySearch(
        SymbolResult? result,
        string toSelect,
        [NotNullWhen(true)] out string? selectedValue,
        bool? assumption,
        List<string> best,
        IEnumerable<string> common,
        int score
    )
    {
        var noInteractive = result?.GetValue(optionNoInteractive) ?? false;
        var exactMatching = result?.GetValue(optionExactMatching) ?? false;
        selectedValue = default;

        string selected;

        if (best.Count == 0)
        {
            result?.AddError("Match not found: " + toSelect);
            return false;
        }
        else if (best.Count > 1)
        {
            if (noInteractive)
            {
                AnsiConsole.MarkupLine($"[yellow]Ambiguous match found:[/]");
                Console.WriteLine();
                foreach (var match in common!)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"[yellow]- {match}[/]"
                    );
                }
                result?.AddError("Ambiguous match.");
                return false;
            }
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(
                        "[yellow]Ambiguous match. Please select the option you are looking for:[/]"
                    )
                    .AddChoices(best)
            );
            selected = choice;
        }
        else
        {
            selected = best[0];

            if ((!IsAutoAccept(score) || exactMatching) && assumption is not true)
            {
                if (noInteractive && assumption is null)
                {
                    result?.AddError($"Match not found: {toSelect}\nBest match: {selected}");
                    return false;
                }

                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"Selecting: [purple]{selected}[/]"
                );

                if (assumption is false || !AnsiConsole.Confirm("Is this ok?", defaultValue: true))
                {
                    AnsiConsole.MarkupLine($"[yellow]The value was not selected.[/]");
                    return false;
                }
            }
        }

        selectedValue = selected;
        return true;
    }

    static bool IsAutoAccept(int score) => score >= 90;

    private static int FilterBestResults(
        IEnumerable<ExtractedResult<string>> res,
        ref List<string> best
    )
    {
        int bestScore = -100;
        Cog.Debug($"Filtering started");

        foreach (var result in res)
        {
            if (result.Score > bestScore)
            {
                Cog.Debug($"{result.Score}: {result.Index} {result.Value}");
                best.Clear();
                best.Add(result.Value);
                bestScore = result.Score;
            }
            else if (bestScore == 100 ? result.Score == bestScore : bestScore - result.Score <= 4)
            {
                best.Add(result.Value);
                Cog.Debug($"{result.Score}: {result.Index} {result.Value}");
            }
            else
            {
                Cog.Debug($"{result.Score} (dropped): {result.Index} {result.Value}");
            }
        }

        return bestScore;
    }
}
