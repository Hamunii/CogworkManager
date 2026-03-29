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
using Spectre.Console;

namespace Cogwork.Cli;

static class Program
{
    static readonly Option<string> optionGameOverride = new("--game", "-g")
    {
        Description = "Override the active game to work on",
    };
    static readonly Option<string> optionProfileOverride = new("--profile", "-p")
    {
        Description = "Override the active mod profile to work on",
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
    public static bool Assume(this SymbolResult result, out bool? assumption)
    {
        var yes = result.GetValue(optionAssumeYes);
        var no = result.GetValue(optionAssumeNo);

        if (yes && no)
        {
            result.AddError($"Options --assume-yes and --assume-no are mutually exclusive");
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

    static async Task<int> Initialize(string[] args)
    {
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
                profileSelect.Validators.Add(result =>
                {
                    Game? game;
                    if (result.GetValue(optionGameOverride) is { } overrideGame)
                    {
                        if (!result.TryGetGame(overrideGame, out game))
                            return;
                    }
                    else if (!TryGetActiveGame(result, out game))
                        return;

                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"Performing for game: [purple]{game.Name}[/]"
                    );

                    var profiles = game.GetProfiles().Select(x => x.profile).ToArray();
                    ModList? selected;

                    if (result.GetValue(profileSelectArgument) is { } argument)
                    {
                        selected = profiles.FirstOrDefault(x =>
                            x.DisambiguatedDisplayName == argument
                        );
                        if (selected is null)
                        {
                            var names = profiles.Select(x => x.DisambiguatedDisplayName).ToArray();
                            if (!TryVeryFuzzySearch(result, argument, names, out var name))
                                return;

                            selected = profiles.First(x => x.DisambiguatedDisplayName == name);
                        }
                    }
                    else
                    {
                        var choice = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("Select a [green]profile[/]:")
                                .AddChoices(profiles.Select(x => x.DisambiguatedDisplayName))
                        );

                        selected = profiles.First(x => x.DisambiguatedDisplayName == choice);
                    }

                    game.Config.ActiveProfile = selected;
                    game.Config.Save(
                        game.GameConfigLocation,
                        SourceGenerationContext.Default.GameConfig
                    );

                    Cog.Debug($"selected {selected.DisambiguatedDisplayName}");
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"Selected profile: [blue]{selected.DisambiguatedDisplayName}[/]"
                    );
                });
            }

            Command profileList = new("list", "List all your profiles for the active game");
            profileList.Aliases.Add("l");
            profile.Subcommands.Add(profileList);
            {
                profileList.Validators.Add(result =>
                {
                    Game? game;
                    if (result.GetValue(optionGameOverride) is { } overrideGame)
                    {
                        if (!result.TryGetGame(overrideGame, out game))
                            return;
                    }
                    else if (!TryGetActiveGame(result, out game))
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
        AddOptionRecursive(profile, optionGameOverride);

        Command mods = new("mods", "Manage mods on a mod profile");
        mods.Aliases.Add("m");
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

                if (!TryGetActiveGameAndProfile(result, out var game, out var profile))
                    return;

                var searches = result.GetValue(modsAddArgument)?.Split(' ');
                if (searches is null || searches.Length != 1)
                {
                    result.AddError("Packages to search must be 1 for now");
                    return;
                }

                var packages = profile.SourceIndex.GetAllPackagesAsync().GetAwaiter().GetResult();
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

                if (!TryGetActiveGameAndProfile(result, out var game, out var profile))
                    return;

                profile.Initialize().GetAwaiter().GetResult();
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
                if (!TryGetActiveGameAndProfile(result, out _, out var profile))
                    return;

                AnsiConsole.MarkupLine($"[gray][[Context]][/]");
                PrintGameAndProfile(hideModListHelp: true);

                AnsiConsole.MarkupLine($"\n[blue][[Added Mods]][/]");

                foreach (var added in profile.Config.AddedPackages)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        CultureInfo.InvariantCulture,
                        $"- [white]{added}[/]"
                    );
                }

                AnsiConsole.MarkupLine($"\n[blue][[Dependencies of Added Mods]][/]");

                foreach (var added in profile.Config.DependencyPackages)
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
                if (!TryGetActiveGameAndProfile(result, out var game, out var profile))
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

                        await profile.Initialize(packageSource =>
                            new(
                                ctx.AddTask($"Fetching {packageSource}").IsIndeterminate(),
                                (task, contentLength) =>
                                {
                                    fetchedAny = true;
                                    if (contentLength is null || task is not ProgressTask pTask)
                                        return;

                                    pTask.IsIndeterminate(false).MaxValue = (double)contentLength;
                                }
                            )
                        );

                        profile.UpdatePackages();

                        var toDownload = profile.AllPackages.Where(x => !x.Value.IsDownloaded());
                        var downloadTasks = toDownload
                            .Select(x =>
                            {
                                var task = ctx.AddTask(x.Value.ToString()).IsIndeterminate();

                                return x.Key.Source.Service.DownloadPackage(
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
                            })
                            .ToArray();

                        Task.WaitAll(downloadTasks);

                        if (downloadTasks.Length == 0 && !fetchedAny)
                        {
                            progress.AutoClear(true);
                        }

                        return (
                            tasksCount: downloadTasks.Length,
                            allSuccess: downloadTasks.Select(x => x.Result).All(x => x is true)
                        );
                    })
                    .Result;

                if (progressResult.tasksCount == 0)
                {
                    AnsiConsole.MarkupLine("[green]Everything is already up-to-date[/]");
                }
                else if (progressResult.allSuccess)
                {
                    AnsiConsole.MarkupLine("[green]Mods updated successfully[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Some updates failed[/]");
                }
            });
        }
        AddOptionRecursive(mods, optionGameOverride);
        AddOptionRecursive(mods, optionProfileOverride);

        Command source = new("sources", "Manage package sources");
        source.Aliases.Add("so");
        rootCommand.Subcommands.Add(source);
        {
            Command sourceAdd = new("add", "Add package source");
            sourceAdd.Aliases.Add("a");
            source.Subcommands.Add(sourceAdd);

            Command sourceRemove = new("remove", "Remove package source");
            sourceRemove.Aliases.Add("r");
            source.Subcommands.Add(sourceRemove);
        }
        AddOptionRecursive(source, optionGameOverride);
        AddOptionRecursive(source, optionProfileOverride);

        rootCommand.Add(optionNoInteractive);

        var result = rootCommand.Parse(args);
        return await result.InvokeAsync();
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

        if (activeProfile is { })
        {
            var added = activeProfile.Config.AddedPackages.Count();
            var deps = activeProfile.Config.DependencyPackages.Count();
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

    private static void AddOptionRecursive(Command command, Option option)
    {
        if (command.Options.Contains(option))
        {
            return;
        }

        command.Options.Add(option);

        foreach (var child in command.Subcommands)
        {
            AddOptionRecursive(child, option);
        }
    }

    private static bool TryGetActiveGameAndProfile(
        CommandResult result,
        [NotNullWhen(true)] out Game? game,
        [NotNullWhen(true)] out ModList? profile
    )
    {
        profile = default;

        if (!result.TryGetActiveGame(out game))
            return false;

        if (!result.TryGetActiveProfile(game, out profile))
            return false;

        return true;
    }

    private static bool TryGetActiveGame(
        this SymbolResult result,
        [NotNullWhen(true)] out Game? game
    )
    {
        if (Game.GlobalConfig.ActiveGame is not { } activeGame)
        {
            game = default;
            result.AddError("An active game is not selected. Use 'cogman game select <game>'.");
            return false;
        }

        game = activeGame;
        return true;
    }

    private static bool TryGetActiveProfile(
        this SymbolResult result,
        Game game,
        [NotNullWhen(true)] out ModList? profile
    )
    {
        if (game.Config.ActiveProfile is not { } activeProfile)
        {
            profile = default;
            result.AddError(
                "An active profile is not selected. Use 'cogman profile select <profile?>'."
            );
            return false;
        }

        profile = activeProfile;
        return true;
    }

    static List<(ModList profile, bool hasCollision)> GetProfiles(this Game game)
    {
        ModList[] profiles = [.. game.EnumerateProfiles()];
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

        List<(ModList profile, bool hasCollision)> values = [];
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
        SymbolResult result,
        string toSelect,
        IEnumerable<string> searchList,
        [NotNullWhen(true)] out string? selected
    )
    {
        if (!result.Assume(out var assumption))
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
        SymbolResult result,
        string toSelect,
        [NotNullWhen(true)] out string? selectedValue,
        bool? assumption,
        List<string> best,
        IEnumerable<string> common,
        int score
    )
    {
        var noInteractive = result.GetValue(optionNoInteractive);
        var exactMatching = result.GetValue(optionExactMatching);
        selectedValue = default;

        string selected;

        if (best.Count == 0)
        {
            result.AddError("Match not found: " + toSelect);
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
                result.AddError("Ambiguous match.");
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
                    result.AddError($"Match not found: {toSelect}\nBest match: {selected}");
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
