global using static Cogwork.Core.CogworkCoreLogger;
global using static Cogwork.Core.PackageSource;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    };
    static readonly Option<bool> optionExactMatching = new("--exact-matching", "-E")
    {
        Description = "Only an exact match is picked implicitly",
    };

    static int Main(string[] args)
    {
        try
        {
            return Initialize(args);
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
    public static bool Assume(this ArgumentResult result, out bool? assumption)
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

    static int Initialize(string[] args)
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
                var activeGame = Game.GlobalConfig.ActiveGame;
                var activeGameName = activeGame?.Name ?? "<none>";
                var modProfileName = activeGame?.Config.ActiveProfileName ?? "<none>";

                AnsiConsole.MarkupInterpolated(
                    CultureInfo.InvariantCulture,
                    $"""
                    Active game: [blue]{activeGameName}[/]
                    Mod profile: [blue]{modProfileName}[/]

                    """
                );
            });
        }

        Command profile = new("profile", "Manage mod profiles");
        profile.Aliases.Add("p");
        rootCommand.Subcommands.Add(profile);
        {
            // TODO
        }
        AddOptionRecursive(profile, optionGameOverride);

        Command mod = new("mod", "Manage mods on a mod profile");
        mod.Aliases.Add("m");
        rootCommand.Subcommands.Add(mod);
        {
            Command modAdd = new("add", "Add mods to a profile");
            modAdd.Aliases.Add("a");
            mod.Subcommands.Add(modAdd);
            rootCommand.Subcommands.Add(modAdd);

            Command modRemove = new("remove", "Remove mods from a profile");
            modRemove.Aliases.Add("r");
            mod.Subcommands.Add(modRemove);
            rootCommand.Subcommands.Add(modRemove);
        }
        AddOptionRecursive(mod, optionGameOverride);
        AddOptionRecursive(mod, optionProfileOverride);

        Command source = new("source", "Manage package sources");
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

        AddOptionRecursive(rootCommand, optionNoInteractive);

        var result = rootCommand.Parse(args);
        return result.Invoke();
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

    private static void SelectGame(ArgumentResult result)
    {
        var gameToSelect = result.GetValueOrDefault<string>();

        if (!Game.NameToGame.TryGetValue(gameToSelect, out var selectedGame))
        {
            if (!TryVeryFuzzySearchGame(result, gameToSelect, out selectedGame))
            {
                return;
            }
        }

        AnsiConsole.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"Selected game: [blue]{selectedGame.Name}[/]"
        );

        Game.GlobalConfig.ActiveGame = selectedGame;
        return;
    }

    private static bool TryVeryFuzzySearchGame(
        ArgumentResult result,
        string gameToSelect,
        [NotNullWhen(true)] out Game? selectedGame
    )
    {
        selectedGame = default;

        if (!result.Assume(out var assumption))
            return false;

        var noInteractive = result.GetValue(optionNoInteractive);
        var exactMatching = result.GetValue(optionExactMatching);

        var games = Game.SupportedGames.Select(x => x.Name);

        List<string> best = [];
        IEnumerable<string>? common = null;
        var score = FilterBestResults(
            Process.ExtractTop(
                gameToSelect,
                games,
                cutoff: 40,
                scorer: ScorerCache.Get<WeightedRatioScorer>()
            ),
            ref best
        );
        if (best.Count != 1 || !IsAutoAccept(score))
        {
            List<string> best2 = [];

            var score2 = FilterBestResults(
                Process.ExtractTop(
                    gameToSelect,
                    games,
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
                    Process.ExtractTop(
                        gameToSelect,
                        games,
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

        string selected;

        if (best.Count == 0)
        {
            result.AddError("Game not found: " + gameToSelect);
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
                result.AddError("Ambiguous match for game name.");
                return false;
            }
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title(
                        "[yellow]Ambiguous match. Please select the game you are looking for:[/]"
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
                    result.AddError($"Game not found: {gameToSelect}\nBest match: {selected}");
                    return false;
                }

                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"Selecting game: [purple]{selected}[/]"
                );

                if (assumption is false || !AnsiConsole.Confirm("Is this ok?", defaultValue: true))
                {
                    AnsiConsole.MarkupLine($"[yellow]The game was not selected.[/]");
                    return false;
                }
            }
        }

        selected = selected.ToLowerInvariant();
        selectedGame = Game.NameToGame[selected];

        return true;
        static bool IsAutoAccept(int score) => score >= 90;
    }

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
