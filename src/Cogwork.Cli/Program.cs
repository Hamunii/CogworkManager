global using static Cogwork.Core.CogworkCoreLogger;
global using static Cogwork.Core.PackageSource;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using FuzzySharp;
using FuzzySharp.Extractor;
using FuzzySharp.SimilarityRatio;
using FuzzySharp.SimilarityRatio.Scorer.Composite;
using FuzzySharp.SimilarityRatio.Scorer.StrategySensitive;

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
        Description = "Require exact case-insensitive string matching",
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
                    Description = "Name of a game which is supported by Cogwork Manager",
                    // This is to not require quotation marks.
                    Arity = ArgumentArity.OneOrMore,
                    CustomParser = r => string.Join(' ', r.Tokens.Select(t => t.Value)),
                };
                gameSelectArgument.Validators.Add(SelectGame);
                gameSelect.Arguments.Add(gameSelectArgument);
            }

            Command gameList = new("list", "List all games supported by Cogwork Manager");
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

        Command mods = new("mods", "Manage mods on a mod profile");
        mods.Aliases.Add("m");
        rootCommand.Subcommands.Add(mods);
        {
            Command modsAdd = new("add", "Add mods to a profile");
            modsAdd.Aliases.Add("a");
            mods.Subcommands.Add(modsAdd);
            rootCommand.Subcommands.Add(modsAdd);

            Command modsRemove = new("remove", "Remove mods from a profile");
            modsRemove.Aliases.Add("r");
            mods.Subcommands.Add(modsRemove);
            rootCommand.Subcommands.Add(modsRemove);
        }
        AddOptionRecursive(mods, optionGameOverride);
        AddOptionRecursive(mods, optionProfileOverride);

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
        bool requireExactMatching = result.GetValue(optionExactMatching);

        if (!Game.NameToGame.TryGetValue(gameToSelect, out var selectedGame))
        {
            if (!TryVeryFuzzySearchGame(result, gameToSelect, out selectedGame))
            {
                // result.AddError("Game not found: " + gameToSelect);
                return;
            }

            if (requireExactMatching)
            {
                result.AddError($"Game not found: {gameToSelect}\nBest match: {selectedGame.Name}");
                return;
            }
        }

        // TODO: Actual logic.
        Console.WriteLine("Selected game: " + selectedGame.Name);
        return;
    }

    private static bool TryVeryFuzzySearchGame(
        ArgumentResult result,
        string gameToSelect,
        [NotNullWhen(true)] out Game? selectedGame
    )
    {
        var games = Game.SupportedGames.Select(x => x.Name);
        selectedGame = default;

        List<string> best = [];
        IEnumerable<string>? common = null;
        FilterBestResults(
            Process.ExtractTop(
                gameToSelect,
                games,
                cutoff: 40,
                scorer: ScorerCache.Get<WeightedRatioScorer>()
            ),
            ref best
        );
        if (best.Count != 1)
        {
            List<string> best2 = [];

            FilterBestResults(
                Process.ExtractTop(
                    gameToSelect,
                    games,
                    scorer: ScorerCache.Get<PartialTokenAbbreviationScorer>()
                ),
                ref best2
            );

            if (best2.Count == 1)
            {
                best = best2;
            }
            else
            {
                List<string> best3 = [];

                FilterBestResults(
                    Process.ExtractTop(
                        gameToSelect,
                        games,
                        cutoff: 40,
                        scorer: ScorerCache.Get<TokenInitialismScorer>()
                    ),
                    ref best3
                );
                if (best3.Count == 1)
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
            if (result.GetValue(optionExactMatching))
            {
                // If exact matching is enabled here, it means this method is used for
                // giving more feedback to the user, but ambiguous matches are irrelevant.
                var sb = new StringBuilder();
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"Game not found: {gameToSelect}\nBest matches:"
                );
                foreach (var match in common!)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- {match}");
                }
                result.AddError(sb.ToString());
                return false;
            }

            int i = 0;
            Console.WriteLine($"Ambiguous match found:");
            Console.WriteLine();
            foreach (var match in common!)
            {
                i++;
                Console.WriteLine($"({i}): " + match);
            }
            if (result.GetValue(optionNoInteractive))
            {
                result.AddError("Ambiguous match for game name.");
                return false;
            }
            Console.WriteLine();
            Console.Write($"Select game by index (1-{best.Count}): ");
            var input = Console.ReadLine();
            if (!int.TryParse(input, out var index) || index < 1 || index > best.Count)
            {
                result.AddError("Invalid index for disambiguating game.");
                return false;
            }
            selected = best[index - 1];
        }
        else
        {
            selected = best[0];
        }

        selected = selected.ToLowerInvariant();
        selectedGame = Game.NameToGame[selected];
        return true;
    }

    private static void FilterBestResults(
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

        return;
    }
}
