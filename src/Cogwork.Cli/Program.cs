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
    static readonly Option<bool> optionNoInteractive = new("--no-interactive", "-N")
    {
        Description = "Prevent requesting user input and fail instead",
    };
    static readonly Option<bool> optionExactMatching = new("--exact-matching", "-E")
    {
        Description = "Only an exact match is picked implicitly",
    };

    static int Main(string[] args)
    {
        RootCommand rootCommand = new("Cogwork Manager CLI - mod package manager");

        Command game = new("game", "Select active game to mod or list available games");
        game.Aliases.Add("g");
        rootCommand.Subcommands.Add(game);
        {
            Command gameSelect = new("select", "Select active game to mod");
            gameSelect.Aliases.Add("s");
            gameSelect.Options.Add(optionExactMatching);
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

            Command gameList = new("list", "List all games supported");
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
                Console.WriteLine($"Active game: <todo>");
                Console.WriteLine($"Mod profile: <todo>");
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
        bool exactMatching = result.GetValue(optionExactMatching);

        if (!Game.NameToGame.TryGetValue(gameToSelect, out var selectedGame))
        {
            if (!TryVeryFuzzySearchGame(result, gameToSelect, out selectedGame))
            {
                return;
            }
        }

        // TODO: Actual logic.
        AnsiConsole.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"Selected game: [purple]{selectedGame.Name}[/]"
        );
        return;
    }

    private static bool TryVeryFuzzySearchGame(
        ArgumentResult result,
        string gameToSelect,
        [NotNullWhen(true)] out Game? selectedGame
    )
    {
        var noInteractive = result.GetValue(optionNoInteractive);
        var exactMatching = result.GetValue(optionExactMatching);

        var games = Game.SupportedGames.Select(x => x.Name);
        selectedGame = default;

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
            score = Math.Max(score, score2);

            if (best2.Count == 1 && IsAutoAccept(score))
            {
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
                score = Math.Max(score, score3);

                if (best3.Count == 1 && IsAutoAccept(score))
                {
                    best = best3;
                }
                else
                {
                    common = best.Intersect(best2).Union(best3);
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

            if (exactMatching || !IsAutoAccept(score))
            {
                if (noInteractive)
                {
                    result.AddError($"Game not found: {gameToSelect}\nBest match: {selected}");
                    return false;
                }

                AnsiConsole.MarkupLineInterpolated(
                    CultureInfo.InvariantCulture,
                    $"Selecting game: [purple]{selected}[/]"
                );

                if (!AnsiConsole.Confirm("Is this ok?", defaultValue: true))
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
        Cog.Information($"Filtering started");

        foreach (var result in res)
        {
            if (result.Score > bestScore)
            {
                Cog.Information($"{result.Score}: {result.Index} {result.Value}");
                best.Clear();
                best.Add(result.Value);
                bestScore = result.Score;
            }
            else if (bestScore == 100 ? result.Score == bestScore : bestScore - result.Score <= 4)
            {
                best.Add(result.Value);
                Cog.Information($"{result.Score}: {result.Index} {result.Value}");
            }
            else
            {
                Cog.Information($"{result.Score} (dropped): {result.Index} {result.Value}");
            }
        }

        return bestScore;
    }
}
