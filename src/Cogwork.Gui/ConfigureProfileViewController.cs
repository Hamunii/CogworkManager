using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Adw;
using GLib;
using Gtk;

namespace Cogwork.Gui;

public record QuickActions(
    Button LaunchButton,
    Label LaunchLabel,
    DropDown Platform,
    DropDown LaunchType,
    Button OpenModLog,
    Button CopyDebugInfo
);

public class ConfigureProfileViewController : IDisposable
{
    // --- State & Context Data Layer ---
    private readonly NavigationView _navView;
    private readonly Action _onBackNavigated;
    private LazyModList _lazyProfile = null!;
    private ModList? _currentProfile;
    private CancellationTokenSource? _searchCts;
    private readonly Stack<PackageVersion> _dependants = new();

    // --- Critical UI Controls (Accessed Across View Cycles) ---
    public NavigationPage Page { get; }
    private readonly WindowTitle _windowTitle;
    private readonly ToggleButton _searchToggleButton;
    private readonly SearchEntry _searchEntry;
    private readonly SearchBar _searchBar;
    private readonly ViewStack _internalTabsStack;
    private readonly ToastOverlay _toastOverlay;

    // --- Component Visibility & Layout Handles (Section Toggles) ---
    private readonly QuickActions _quickActions;
    private readonly Section _sectionAdded;
    private readonly Section _sectionDeps;
    private readonly Section _sectionRecent;
    private readonly Section _sectionSearchResults;
    private readonly Section _sectionDependant;
    private readonly Section _sectionModDeps;

    // --- Mod Profile Info Targets ---
    private readonly NavigationPage _modPage;
    private readonly Label _modLabel;
    private readonly Label _modDescriptionLabel;
    private readonly Label _modSourceLabel;
    private readonly MarkdownPreviewer _markdownPreviewer;

    // other stuff
    readonly string[] sourceColors =
    [
        "#3584e4",
        "#2ec27e",
        "#f5c211",
        "#e66100",
        "#9141ac",
        "#1a5fb4",
    ];

    public ConfigureProfileViewController(NavigationView navView, Action onBackNavigated)
    {
        ArgumentNullException.ThrowIfNull(navView);
        ArgumentNullException.ThrowIfNull(onBackNavigated);

        _navView = navView;
        _onBackNavigated = onBackNavigated;

        // 1. Root Layout Wrapper
        var layoutBox = Box.New(Orientation.Vertical, 0);

        var header = Adw.HeaderBar.New();
        _windowTitle = WindowTitle.New("Manage Profile", "");
        header.SetTitleWidget(_windowTitle);

        _searchToggleButton = ToggleButton.New();
        _searchToggleButton.SetIconName("edit-find-symbolic");
        header.PackStart(_searchToggleButton);
        layoutBox.Append(header);

        AddHeaderSettingsButton(header);

        // 2. Search Box Setup
        _searchBar = SearchBar.New();
        _searchEntry = SearchEntry.New();
        _searchEntry.SetPlaceholderText("Search mods...");
        _searchEntry.SetHexpand(true);
        _searchEntry.SetHalign(Align.Center);
        _searchEntry.SetSizeRequest(400, -1);

        _searchBar.SetChild(_searchEntry);
        _searchBar.ConnectEntry(_searchEntry);
        _searchEntry.SetKeyCaptureWidget(layoutBox);
        _searchEntry.SetSearchDelay(0);
        layoutBox.Append(_searchBar);

        // 3. Tab Stack Manager
        _internalTabsStack = ViewStack.New();
        _internalTabsStack.SetEnableTransitions(false);
        _internalTabsStack.SetVexpand(true);
        layoutBox.Append(_internalTabsStack);

        // --- Build Manage Tab Layout ---
        var manageTabBox = CreateManageTab(
            out _quickActions,
            out _sectionAdded,
            out _sectionDeps,
            out _sectionRecent
        );
        var managePage = _internalTabsStack.AddNamed(manageTabBox, "manage_tab");
        managePage.SetTitle("Manage");
        managePage.SetIconName("emblem-system-symbolic");

        // --- Build Browse Tab Layout ---
        var installTabBox = CreateInstallTab(out _sectionSearchResults);
        var installPage = _internalTabsStack.AddNamed(installTabBox, "install_tab");
        installPage.SetTitle("Browse");
        installPage.SetIconName("list-add-symbolic");

        // --- Build Mod Sub-view Details Sheet Layout ---
        var modBox = CreateModDetailsBox(
            out _sectionDependant,
            out _sectionModDeps,
            out _modLabel,
            out _modDescriptionLabel,
            out _modSourceLabel,
            out _markdownPreviewer
        );
        _modPage = NavigationPage.New(modBox, "mod_page");

        // 4. Bind Core Engine Handlers
        _searchToggleButton.OnToggled += OnSearchToggled;
        _searchEntry.OnStopSearch += OnStopSearch;
        _searchEntry.OnSearchChanged += OnSearchChanged;

        _modPage.OnHiding += (s, e) => FireConfigRefresh();
        _modPage.OnHidden += OnModPageHidden;

        SetupKeyboardShortcuts(installTabBox);

        _toastOverlay = ToastOverlay.New();
        _toastOverlay.SetChild(layoutBox);

        // 5. Package Root Assembly Wrapper
        Page = NavigationPage.New(_toastOverlay, "configure_profile");
        Page.OnHiding += (s, e) => _onBackNavigated();
        Page.OnHidden += (s, e) => _currentProfile?.SetDirty();
    }

    void AddHeaderSettingsButton(Adw.HeaderBar header)
    {
        var settingsButton = Button.NewFromIconName("emblem-system-symbolic");
        settingsButton.SetValign(Align.Center);
        settingsButton.AddCssClass("flat");
        settingsButton.SetTooltipText("Profile and Game Settings");

        settingsButton.OnClicked += (s, e) =>
        {
            OpenProfileSettings(_lazyProfile);
        };

        header.PackEnd(settingsButton);
    }

    void OpenProfileSettings(LazyModList lazyProfile)
    {
        if (Page.GetRoot() is not Gtk.Window rootWindow)
            return;

        var prefWindow = PreferencesWindow.New();
        prefWindow.SetTransientFor(rootWindow);
        prefWindow.SetDefaultSize(800, 500);
        prefWindow.SetModal(true);

        var prefPage = PreferencesPage.New();
        prefPage.SetTitle("Profile");
        prefPage.SetIconName("emblem-system-symbolic");
        prefWindow.Add(prefPage);

        var gamePreferences = ProfilesViewController.CreateSettingsPage(
            lazyProfile.Game,
            prefWindow
        );
        prefWindow.Add(gamePreferences);

        var prefSourcesGroup = PreferencesGroup.New();
        prefSourcesGroup.SetTitle("Package Sources");
        prefSourcesGroup.SetDescription("Sources are evaluated according to the list order");
        prefPage.Add(prefSourcesGroup);

        var sourcesListBox = ListBox.New();
        sourcesListBox.SelectionMode = SelectionMode.None;
        sourcesListBox.AddCssClass("boxed-list");
        prefSourcesGroup.Add(sourcesListBox);

        var sources = lazyProfile.SourceIndex.Sources;

        for (int i = 0; i < sources.Count; i++)
        {
            var uiExpanderRow = SourceRowFactory.Create(
                source: sources[i],
                onValuesChanged: (row, updatedStrategy, updatedEntry, visible) =>
                {
                    int index = row.GetIndex();
                    if (index == -1)
                        return;

                    lazyProfile.SourceIndex.Reinsert(
                        sources[index] with
                        {
                            DominanceStrategy = updatedStrategy,
                            DominanceEntry = updatedEntry,
                            Visible = visible,
                        },
                        index
                    );

                    _currentProfile?.SetDirty();
                },
                onReorderRequested: (row, targetIndex) =>
                {
                    int itemIndex = row.GetIndex();
                    if (itemIndex == targetIndex || itemIndex == -1)
                        return;

                    sourcesListBox.Remove(row);
                    sourcesListBox.Insert(row, targetIndex);

                    var item = sources[itemIndex];
                    lazyProfile.SourceIndex.Reinsert(item, targetIndex);

                    _currentProfile?.SetDirty();
                }
            );

            sourcesListBox.Append(uiExpanderRow);
        }

        var prefOverridesGroup = PreferencesGroup.New();
        prefOverridesGroup.SetTitle("Profile Overrides");
        prefPage.Add(prefOverridesGroup);

        ExpanderRow expanderRow = CreateOverrideGamePathSetting(
            lazyProfile,
            prefWindow,
            prefOverridesGroup,
            out var entryRow
        );

        // Auto-save logic on window close sequence
        prefWindow.OnCloseRequest += (s, e) =>
        {
            string? finalPath = null;

            var isExpand = expanderRow.GetEnableExpansion();
            finalPath = entryRow.GetText().Trim();
            if (finalPath == string.Empty)
                finalPath = null;

            if (
                lazyProfile.OverrideGamePath != finalPath
                || lazyProfile.IsOverrideGamePathEnabled != isExpand
            )
            {
                lazyProfile.OverrideGamePath = finalPath;
                lazyProfile.IsOverrideGamePathEnabled = isExpand;
                lazyProfile.SaveData();
            }

            if (_currentProfile is { } && _currentProfile.PeekIsDirty())
            {
                // Hack: trigger never loaded but now enabled sources to load
                // so that dependencies are evaluated correctly.
                _ = lazyProfile.LoadAsync().Result;

                _currentProfile.DirtyRebuildDependencies(DependencyVersionResolution.Requested);
                UpdateConfiguration(lazyProfile);
            }

            return false;
        };

        prefWindow.Present();
    }

    private ExpanderRow CreateOverrideGamePathSetting(
        LazyModList lazyProfile,
        PreferencesWindow prefWindow,
        PreferencesGroup prefGroup,
        out EntryRow entryRow
    )
    {
        var expanderRow = ExpanderRow.New();
        expanderRow.SetTitle("Override Game Path");
        expanderRow.SetSubtitle("Provide a custom directory path for this profile");
        expanderRow.SetShowEnableSwitch(true);

        entryRow = EntryRow.New();
        entryRow.SetTitle("Path to game root directory");
        entryRow.SetText(
            _lazyProfile.OverrideGamePath ?? _lazyProfile.Game.Config.PreferredPath ?? ""
        );
        Helpers.AddGamePathValidationSuffixIcon(entryRow);
        expanderRow.AddRow(entryRow);
        prefGroup.Add(expanderRow);

        var browseButton = Button.NewFromIconName("folder-open-symbolic");
        browseButton.SetValign(Align.Center);
        browseButton.AddCssClass("flat");
        browseButton.SetTooltipText("Browse for directory...");
        entryRow.AddSuffix(browseButton);
        ButtonOnClickedSelectGameRootDirectory(browseButton, entryRow, prefWindow);

        // Seed the initial configuration state
        bool hasOverride = lazyProfile.IsOverrideGamePathEnabled;

        expanderRow.SetEnableExpansion(hasOverride);
        expanderRow.SetExpanded(hasOverride);

        // Sync toggle clicks to immediately open/close the container rows
        expanderRow.OnNotify += (s, e) =>
        {
            // When user interacts with the toggle, sync expansion states
            if (e.Pspec.GetName() == "enable-expansion")
            {
                expanderRow.SetExpanded(expanderRow.GetEnableExpansion());
            }
        };
        return expanderRow;
    }

    internal static void ButtonOnClickedSelectGameRootDirectory(
        Button browseButton,
        EntryRow entryRow,
        PreferencesWindow prefWindow
    )
    {
        // --- FIXED: Modern, clean C# async/await Folder Dialog ---
        browseButton.OnClicked += async (s, e) =>
        {
            var fileDialog = FileDialog.New();
            fileDialog.SetTitle("Select Game Root Directory");

            // Seed the initial folder if the typed path is already valid on disk
            string currentText = entryRow.GetText().Trim();
            if (!string.IsNullOrEmpty(currentText) && Directory.Exists(currentText))
            {
                try
                {
                    var initialFolderFile = Gio.FileHelper.NewForPath(currentText);
                    fileDialog.SetInitialFolder(initialFolderFile);
                }
                catch
                { /* Fall back gracefully if path parsing fails */
                }
            }

            try
            {
                // Await the task wrapper natively on the UI thread execution loop
                Gio.File? chosenFile = await fileDialog.SelectFolderAsync(prefWindow);

                if (chosenFile != null)
                {
                    string selectedDirectoryPath = chosenFile.GetPath() ?? string.Empty;
                    entryRow.SetText(selectedDirectoryPath);
                }
            }
            catch (Exception ex)
            {
                // GirCore throws an exception here if the user cancels or closes the dialog box
                Console.WriteLine($"Folder selection cancelled or failed: {ex.Message}");
            }
        };
    }

    public void UpdateConfiguration(LazyModList lazyProfile)
    {
        _lazyProfile = lazyProfile;
        _currentProfile = lazyProfile.GetModListAsync().Result;

        _windowTitle.SetTitle(Markup.EscapeText(lazyProfile.DisplayName));
        _windowTitle.SetSubtitle(Markup.EscapeText(lazyProfile.Game.Name));

        if (!_currentProfile.ConsumeIsDirty())
            return;

        string[] platforms =
        [
            .. lazyProfile.Game.Platforms.GetAvailablePlatforms().Select(x => x.ToString()),
        ];
        var launchButton = _quickActions.LaunchButton;
        var (launchConfigSnapshot, _) = lazyProfile.GetGameLaunchRequest();
        var platformSnapshot = launchConfigSnapshot.Platform;

        void OnNotify(GObject.Object sender, GObject.Object.NotifySignalArgs args)
        {
            if (args.Pspec.GetName() != "selected")
                return;

            _lazyProfile.Game.Config.LaunchConfig = new(
                (Game.Platform)_quickActions.Platform.GetSelected(),
                (Game.LaunchType)_quickActions.LaunchType.GetSelected()
            );
            _lazyProfile.Game.Config.Save();

            UpdateLabels();
        }
        void UpdateLabels()
        {
            var (launchConfig, _) = _lazyProfile.GetGameLaunchRequest();
            var platform = launchConfig.Platform;

            string modsCount =
                (
                    _lazyProfile.ResolvedAdded?.Count + _lazyProfile.ResolvedDependencies?.Count
                )?.ToString(CultureInfo.InvariantCulture) ?? "<?>";
            _quickActions.LaunchLabel.SetLabel($"Launch with {modsCount} mods");
        }

        UpdateLabels();

        _quickActions.Platform.SetModel(StringList.New(platforms));
        _quickActions.Platform.SetSelected((uint)platforms.IndexOf(platformSnapshot.ToString()));
        _quickActions.Platform.OnNotify -= OnNotify;
        _quickActions.Platform.OnNotify += OnNotify;

        _quickActions.LaunchType.SetModel(StringList.New([.. Enum.GetNames<Game.LaunchType>()]));
        _quickActions.LaunchType.SetSelected((uint)launchConfigSnapshot.Launch);
        _quickActions.LaunchType.OnNotify -= OnNotify;
        _quickActions.LaunchType.OnNotify += OnNotify;

        void Launch(Button s, EventArgs e)
        {
            List<string> args =
            [
                "launch",
                "--game",
                _lazyProfile.Game.Slug,
                "--profile",
                _lazyProfile.Id,
            ];

            var (launchConfig, _) = _lazyProfile.GetGameLaunchRequest();
            if (launchConfig.Launch is Game.LaunchType.Direct)
            {
                args.Add("--direct");
            }
            _ = Cli.Core.Program.Main([.. args]);
        }
        launchButton.OnClicked -= Launch;
        launchButton.OnClicked += Launch;

        _quickActions.OpenModLog.OnClicked -= OnOpenModLog;
        _quickActions.OpenModLog.OnClicked += OnOpenModLog;
        if (_lazyProfile.Game.InstallRules.GetLogPath(_lazyProfile) is null)
        {
            _quickActions.OpenModLog.SetSensitive(false);
            _quickActions.OpenModLog.SetTooltipText(
                "The mod loader for this game doesn't provide a log file"
            );
        }
        else
        {
            _quickActions.OpenModLog.SetSensitive(true);
            _quickActions.OpenModLog.SetTooltipText(string.Empty);
        }

        async void OnOpenModLog(Button sender, EventArgs args)
        {
            var path = _lazyProfile.Game.InstallRules.GetLogPath(_lazyProfile);
            if (!File.Exists(path))
            {
                var toast = Toast.New(
                    "Log file not found; game has not launched with this profile yet"
                );
                _toastOverlay.AddToast(toast);
                return;
            }
            if (Page.GetRoot() is not Gtk.Window rootWindow)
                throw new UnreachableException();

            var fileToOpen = Gio.FileHelper.NewForPath(path);
            FileLauncher launcher = FileLauncher.New(fileToOpen);

            try
            {
                await launcher.LaunchAsync(rootWindow);
            }
            catch (GException)
            {
                // The portal dialog was dismissed by the user or something like that
            }
        }

        _quickActions.CopyDebugInfo.OnClicked -= OnCopyDebugInfo;
        _quickActions.CopyDebugInfo.OnClicked += OnCopyDebugInfo;
        async void OnCopyDebugInfo(Button sender, EventArgs args)
        {
            var assembly = typeof(CogworkCoreLogger).Assembly;

            var appName = (Page.GetRoot() as Gtk.Window)!.Title;
            var appVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion;

            var (launchConfig, _) = _lazyProfile.GetGameLaunchRequest();
            var path = _lazyProfile.Game.InstallRules.GetLogPath(_lazyProfile);
            var configExists = File.Exists(path);
            var modsCount =
                _lazyProfile.ResolvedAdded?.Count + _lazyProfile.ResolvedDependencies?.Count;

            var mods =
                "- " + string.Join("\n- ", _lazyProfile.GetResolved().Select(x => x.ToString()));

            var gameDir = _lazyProfile.GamePath;
            var gameDirRootFiles =
                "- "
                + string.Join(
                    "\n- ",
                    Directory
                        .GetFileSystemEntries(gameDir!)
                        .Select(x =>
                            Directory.Exists(x) ? $"{Path.GetFileName(x)}/" : Path.GetFileName(x)
                        )
                        .OrderByDescending(x => x.EndsWith('/'))
                        .ThenBy(x => x)
                );

            StringBuilder sb = new();
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"""
                Quick Info
                ```
                Game: {_lazyProfile.Game.Name} ({launchConfig.Platform}) (launch type: {launchConfig.Launch})
                Profile: {_lazyProfile.DisplayName}
                OS: {RuntimeInformation.RuntimeIdentifier}
                App: {appName} {appVersion}
                Log File: {(
                    path is { }
                        ? (configExists ? "✅ exists" : "❌ not generated")
                        : "not supported by modloader"
                )}
                Mod Count: {modsCount}
                Mods:
                {mods}
                Game Dir: {gameDir}
                Game Dir Root Files:
                {gameDirRootFiles}
                ```
                """
            );

            if (configExists)
            {
                var logs = await File.ReadAllTextAsync(path!);

                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"""
                    Logs
                    ```
                    {logs}
                    ```
                    """
                );
            }

            var display = Page.GetDisplay();
            var clipboard = display.GetClipboard();

            var debugInfo = sb.ToString();
            clipboard.SetText(debugInfo);

            var toast = Toast.New("Copied debug info to clipboard");
            _toastOverlay.AddToast(toast);
        }

        ClearList(_sectionAdded.Content);

        if (_currentProfile.Added.Count > 0)
        {
            foreach (var mod in _currentProfile.Added.Values)
                AddPackageRowToAdded(mod);
        }
        else
        {
            _sectionAdded.ToggleVisibility(false);
        }

        RebuildDependencies();
    }

    private void AddPackageRowToAdded(PackageVersionReference mod)
    {
        if (_currentProfile == null)
            return;

        var wrappedRow = CreatePackageRow(mod, PackageVersionRow.Context.Added);

        _sectionAdded.Content.Append(wrappedRow.Row);
        RebuildDependencies();
        _sectionAdded.ToggleVisibility(true);
    }

    private void RebuildDependencies()
    {
        if (_currentProfile == null)
            return;

        // --- Process Dependencies Section ---
        ClearList(_sectionDeps.Content);

        if (_currentProfile.Dependencies.Count > 0)
        {
            foreach (var dep in _currentProfile.Dependencies.Values)
            {
                var wrappedRow = CreatePackageRow(dep, PackageVersionRow.Context.Dependency);
                _sectionDeps.Content.Append(wrappedRow.Row);
            }
            _sectionDeps.ToggleVisibility(true);
        }
        else
        {
            _sectionDeps.ToggleVisibility(false);
        }

        // --- Process Recent / RecentlyRemoved Section ---
        ClearList(_sectionRecent.Content);

        if (_currentProfile.RecentlyRemoved.Count > 0)
        {
            foreach (var dep in _currentProfile.RecentlyRemoved.Values)
            {
                var wrappedRow = CreatePackageRow(dep, PackageVersionRow.Context.RecentlyRemoved);
                _sectionRecent.Content.Append(wrappedRow.Row);
            }
            _sectionRecent.ToggleVisibility(true);
        }
        else
        {
            _sectionRecent.ToggleVisibility(false);
        }

        _ = _currentProfile.ConsumeIsDirty();
    }

    private void FireConfigRefresh()
    {
        if (_currentProfile is not null)
        {
            UpdateConfiguration(_currentProfile.Lazy);
        }
    }

    private void OnModPageHidden(object? sender, EventArgs e)
    {
        if (_internalTabsStack.GetVisibleChildName() == "install_tab")
            _searchEntry.GrabFocus();
        else
            _searchToggleButton.GrabFocus();

        _dependants.Clear();
    }

    private void OnSearchToggled(object? sender, EventArgs e)
    {
        bool isActive = _searchToggleButton.GetActive();
        _searchBar.SetSearchMode(isActive);

        if (isActive)
        {
            _searchEntry.GrabFocus();
        }
        else
        {
            _searchEntry.SetText("");
        }
    }

    private void OnStopSearch(object? sender, EventArgs e)
    {
        _searchToggleButton.SetActive(false);
    }

    private void OnSearchChanged(object? sender, EventArgs e)
    {
        string currentText = _searchEntry.GetText();
        if (!_searchEntry.HasFocus)
        {
            _searchEntry.GrabFocus();
        }

        if (string.IsNullOrEmpty(currentText))
        {
            _internalTabsStack.SetVisibleChildName("manage_tab");
            FireConfigRefresh();
        }
        else
        {
            if (_internalTabsStack.GetVisibleChildName() != "install_tab")
            {
                _internalTabsStack.SetVisibleChildName("install_tab");
            }

            if (!_searchToggleButton.GetActive())
            {
                _searchToggleButton.SetActive(true);
            }
        }

        if (_currentProfile == null)
            return;

        string query = currentText.Trim().Replace(' ', '_');
        if (string.IsNullOrEmpty(query))
            return;

        // Reset and cancel previous search pipelines cleanly
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        Task.Run(
            async () =>
            {
                try
                {
                    var packages = await _currentProfile.SourceIndex.GetAllPackagesAsync(
                        null,
                        token
                    );
                    if (token.IsCancellationRequested)
                        return;

                    var searchResults = _currentProfile.Search(packages, query).Take(20).ToArray();
                    if (token.IsCancellationRequested)
                        return;

                    // Safely marshal the search result population loop back to the main UI loop thread
                    GLib.Functions.TimeoutAdd(
                        0,
                        0,
                        () =>
                        {
                            if (token.IsCancellationRequested)
                                return false;

                            ClearList(_sectionSearchResults.Content);

                            if (searchResults.Length == 0)
                            {
                                _sectionSearchResults.ToggleVisibility(false);
                                return false;
                            }

                            _sectionSearchResults.ToggleVisibility(true);

                            foreach (var package in searchResults)
                            {
                                var targetVersion = (PackageVersionReference)package.Latest;

                                var wrappedRow = CreatePackageRow(
                                    targetVersion,
                                    PackageVersionRow.Context.Search
                                );
                                _sectionSearchResults.Content.Append(wrappedRow.Row);
                            }

                            return false;
                        }
                    );
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Cog.Error($"Async search pipeline error: {ex}");
                }
            },
            token
        );
    }

    private void OnModRowClicked(PackageVersionReference packageVersion)
    {
        RenderModDetailsView(packageVersion);
        _navView.Push(_modPage);
    }

    private void RenderModDetailsView(PackageVersionReference reference)
    {
        ClearList(_sectionDependant.Content);
        if (_dependants.Count == 0)
        {
            _sectionDependant.ToggleVisibility(false);
        }
        else
        {
            _sectionDependant.ToggleVisibility(true);
            var dep = _dependants.Peek();
            var wrappedRow = CreatePackageRow(
                (PackageVersionReference)dep,
                PackageVersionRow.Context.ModDetailsDependant,
                pk =>
                {
                    _dependants.Pop();
                    RenderModDetailsView(pk);
                }
            );
            _sectionDependant.Content.Append(wrappedRow.Row);
        }
        var packageVersion = reference.Resolve();
        _modLabel.SetText(reference.FullName);
        _modDescriptionLabel.SetText(packageVersion.Description);
        _modSourceLabel.SetText($"Source: {reference.Source.Id}");
        _markdownPreviewer.Render(packageVersion.GetReadmeAsync().Result);
        ClearList(_sectionModDeps.Content);
        var targetDependencies = packageVersion.MarkedDependencies(_currentProfile!.SourceIndex);
        if (targetDependencies.Length == 0)
        {
            _sectionModDeps.ToggleVisibility(false);
        }
        else
        {
            _sectionModDeps.ToggleVisibility(true);
            foreach (var dep in targetDependencies)
            {
                var wrappedRow = CreatePackageRow(
                    (PackageVersionReference)dep,
                    PackageVersionRow.Context.ModDetailsDependency,
                    (pk) =>
                    {
                        _dependants.Push(packageVersion);
                        RenderModDetailsView(pk);
                    }
                );
                _sectionModDeps.Content.Append(wrappedRow.Row);
            }
        }
    }

    static Box CreateManageTab(
        out QuickActions quickActions,
        out Section secAdded,
        out Section secDeps,
        out Section secRecent
    )
    {
        var boxContainer = Box.New(Orientation.Vertical, 0);
        var scroll = ScrolledWindow.New();
        scroll.SetVexpand(true);
        boxContainer.Append(scroll);
        var clamp = Clamp.New();
        clamp.SetMaximumSize(800);
        scroll.SetChild(clamp);
        var content = Box.New(Orientation.Vertical, 24);
        content.SetMarginTop(24);
        content.SetMarginBottom(24);
        content.SetMarginStart(24);
        content.SetMarginEnd(24);
        clamp.SetChild(content);

        Box quickActionsBox = Box.New(Orientation.Vertical, 8);

        var launchBox = Box.New(Orientation.Horizontal, 8);
        quickActionsBox.Append(launchBox);

        var launchButton = Button.New();
        var launchLabel = Label.New("placeholder");
        {
            launchButton.SetValign(Align.Center);
            launchButton.SetHexpand(true);
            launchButton.SetCssClasses(["suggested-action", "pill"]);

            var box = Box.New(Orientation.Horizontal, 8);
            box.SetHalign(Align.Center);
            var icon = Image.NewFromIconName("media-playback-start-symbolic");
            box.Append(launchLabel);
            box.Append(icon);

            launchButton.SetChild(box);
            launchBox.Append(launchButton);
        }

        var platform = DropDown.New(null, null);
        launchBox.Append(platform);

        var launchType = DropDown.New(null, null);
        launchBox.Append(launchType);

        var debugBox = Box.New(Orientation.Horizontal, 8);
        quickActionsBox.Append(debugBox);

        var openModLog = Button.New();
        {
            openModLog.SetValign(Align.Center);
            openModLog.SetHexpand(true);
            openModLog.SetLabel("Open Logs");

            var box = Box.New(Orientation.Horizontal, 8);
            box.SetHalign(Align.Center);
            var icon = Image.NewFromIconName("folder-documents-symbolic");
            var label = Label.New("Open Logs");
            box.Append(icon);
            box.Append(label);

            openModLog.SetChild(box);
            debugBox.Append(openModLog);
        }

        var copyDebugInfo = Button.New();
        {
            copyDebugInfo.SetValign(Align.Center);
            copyDebugInfo.SetHexpand(true);

            var box = Box.New(Orientation.Horizontal, 8);
            box.SetHalign(Align.Center);
            var icon = Image.NewFromIconName("edit-copy-symbolic");
            var label = Label.New("Copy Debug Info");
            box.Append(icon);
            box.Append(label);

            copyDebugInfo.SetChild(box);
            debugBox.Append(copyDebugInfo);
        }

        content.Append(quickActionsBox);
        quickActions = new(
            launchButton,
            launchLabel,
            platform,
            launchType,
            openModLog,
            copyDebugInfo
        );

        secAdded = new Section(content, "Added", "No added mods. Type to search mods.");
        secDeps = new Section(content, "Dependencies");
        secRecent = new Section(content, "Recently Removed");
        return boxContainer;
    }

    static Box CreateInstallTab(out Section searchSection)
    {
        var box = Box.New(Orientation.Vertical, 0);
        var scroll = ScrolledWindow.New();
        scroll.SetVexpand(true);
        box.Append(scroll);
        var clamp = Clamp.New();
        clamp.SetMaximumSize(800);
        scroll.SetChild(clamp);
        var content = Box.New(Orientation.Vertical, 24);
        content.SetMarginTop(24);
        content.SetMarginBottom(24);
        content.SetMarginStart(24);
        content.SetMarginEnd(24);
        clamp.SetChild(content);
        searchSection = new Section(content, "Search Results", "No matches.");
        return box;
    }

    static Box CreateModDetailsBox(
        out Section sectionDependant,
        out Section sectionModDeps,
        out Label modLabel,
        out Label modDescriptionLabel,
        out Label modSourceLabel,
        out MarkdownPreviewer markdownPreviewer
    )
    {
        var box = Box.New(Orientation.Vertical, 0);
        var header = Adw.HeaderBar.New();
        header.SetShowTitle(false);
        header.AddCssClass("flat");
        box.Append(header);
        var scroll = ScrolledWindow.New();
        scroll.SetVexpand(true);
        box.Append(scroll);
        var clamp = Clamp.New();
        clamp.SetMaximumSize(800);
        scroll.SetChild(clamp);
        var content = Box.New(Orientation.Vertical, 24);
        content.SetMarginTop(24);
        content.SetMarginBottom(24);
        content.SetMarginStart(24);
        content.SetMarginEnd(24);
        clamp.SetChild(content);
        sectionDependant = new Section(content, "Dependant");
        modLabel = Label.New("mod_name");
        modLabel.AddCssClass("heading");
        modLabel.SetHalign(Align.Start);
        modLabel.SetWrapMode(Pango.WrapMode.Word);
        modLabel.SetWrap(true);
        content.Append(modLabel);
        modDescriptionLabel = Label.New("mod_description");
        modDescriptionLabel.SetHalign(Align.Start);
        modDescriptionLabel.SetWrapMode(Pango.WrapMode.Word);
        modDescriptionLabel.SetWrap(true);
        content.Append(modDescriptionLabel);
        modSourceLabel = Label.New("mod_source");
        modSourceLabel.AddCssClass("dim-label");
        modSourceLabel.SetHalign(Align.Start);
        modSourceLabel.SetWrapMode(Pango.WrapMode.Word);
        modSourceLabel.SetWrap(true);
        content.Append(modSourceLabel);
        markdownPreviewer = MarkdownPreviewer.NewWithProperties([]);
        markdownPreviewer.SetSizeRequest(-1, 100);
        content.Append(markdownPreviewer);
        sectionModDeps = new Section(content, "Dependencies", "None.");
        return box;
    }

    private void SetupKeyboardShortcuts(Widget targetControllerWidget)
    {
        var shortcutController = ShortcutController.New();
        uint escapeKeyval = Gdk.Functions.KeyvalFromName("Escape");
        var escapeTrigger = KeyvalTrigger.New(escapeKeyval, 0);
        var backAction = CallbackAction.New(
            (widget, args) =>
            {
                _internalTabsStack.SetVisibleChildName("manage_tab");
                _searchToggleButton.SetActive(false);
                FireConfigRefresh();
                return true;
            }
        );
        var escapeShortcut = Shortcut.New(escapeTrigger, backAction);
        shortcutController.AddShortcut(escapeShortcut);
        targetControllerWidget.AddController(shortcutController);
    }

    private static void ClearList(ListBox listBox)
    {
        while (listBox.GetFirstChild() != null)
        {
            listBox.Remove(listBox.GetFirstChild()!);
        }
    }

    public PackageVersionRow CreatePackageRow(
        PackageVersionReference versionReference,
        PackageVersionRow.Context context,
        Action<PackageVersionReference>? onClicked = null
    ) => new(this, versionReference, context, onClicked);

    public record class PackageVersionRow
    {
        public enum Context
        {
            Added,
            Dependency,
            RecentlyRemoved,
            Search,
            ModDetailsDependant,
            ModDetailsDependency,
        }

        internal ActionRow Row => _row;
        private readonly ActionRow _row;

        public PackageVersionRow(
            ConfigureProfileViewController parent,
            PackageVersionReference versionReference,
            Context context,
            Action<PackageVersionReference>? onClicked = null
        )
        {
            onClicked ??= parent.OnModRowClicked;
            _row = parent.CreateBaseRow(versionReference, context, onClicked);

            switch (context)
            {
                case Context.Added:
                    var removeButton = CreateActionButton(
                        "list-remove-symbolic",
                        $"Remove {versionReference.FullName}",
                        "destructive-action"
                    );

                    removeButton.OnClicked += (s, e) =>
                    {
                        parent._currentProfile!.Remove([versionReference.Package()]);
                        parent._sectionAdded.Content.Remove(_row);
                        parent.RebuildDependencies();

                        if (parent._currentProfile.Added.Count == 0)
                        {
                            parent._sectionAdded.ToggleVisibility(false);
                            parent._sectionRecent.Content.GrabFocus();
                        }
                    };

                    _row.AddSuffix(removeButton);
                    break;

                case Context.Dependency:
                    var promoteButton = CreateActionButton(
                        "go-up-symbolic",
                        $"Add {versionReference.FullName}"
                    );

                    promoteButton.OnClicked += (s, e) =>
                    {
                        parent._currentProfile!.Add(
                            versionReference,
                            DependencyVersionResolution.Latest
                        );
                        parent.AddPackageRowToAdded(versionReference);

                        if (parent._currentProfile.Dependencies.Count == 0)
                        {
                            if (parent._currentProfile.RecentlyRemoved.Count > 0)
                                parent._sectionRecent.Content.GrabFocus();
                            else
                                parent._sectionAdded.Content.GrabFocus();
                        }
                    };

                    _row.AddSuffix(promoteButton);
                    break;

                case Context.RecentlyRemoved:
                    var addButton = CreateAddOrRemoveButton(
                        parent._currentProfile!,
                        (PackageReference)versionReference
                    );
                    addButton.OnClicked += (s, e) =>
                    {
                        parent._currentProfile!.Add(
                            versionReference,
                            DependencyVersionResolution.Latest
                        );
                        parent.FireConfigRefresh();

                        if (parent._currentProfile!.RecentlyRemoved.Count == 0)
                        {
                            if (parent._currentProfile.Dependencies.Count > 0)
                                parent._sectionDeps.Content.GrabFocus();
                            else
                                parent._sectionAdded.Content.GrabFocus();
                        }
                    };

                    _row.AddSuffix(addButton);
                    break;

                case Context.Search:
                case Context.ModDetailsDependant:
                case Context.ModDetailsDependency:
                    var genericActionButton = CreateAddOrRemoveButton(
                        parent._currentProfile!,
                        (PackageReference)versionReference
                    );
                    _row.AddSuffix(genericActionButton);
                    break;
            }
        }
    }

    internal ActionRow CreateBaseRow(
        PackageVersionReference packageVersionReference,
        PackageVersionRow.Context context,
        Action<PackageVersionReference> onClicked
    )
    {
        var row = ActionRow.New();

        var menuButton = MenuButton.New();
        menuButton.SetIconName("mark-location-symbolic");
        menuButton.AddCssClass("flat");
        menuButton.AddCssClass("source-indicator");
        menuButton.SetValign(Align.Center);
        menuButton.SetHexpand(false);
        row.AddSuffix(menuButton);

        var actionGroup = Gio.SimpleActionGroup.New();
        row.InsertActionGroup("row-scope", actionGroup);

        PackageVersionReference verRef;
        PackageVersionReference[]? allRefs = null;
        Gio.SimpleAction? selectSourceAction = null;
        CssProvider? iconColorProvider = null;

        SetData(packageVersionReference);
        row.OnActivated += OnClicked;
        row.SetActivatable(true);
        return row;

        void SetData(PackageVersionReference reference)
        {
            verRef = reference;
            var allowConfig = context != PackageVersionRow.Context.Dependency;
            allRefs = [.. reference.GetFromAllAvailableSources(_currentProfile!)];
            var hasMultipleSources = allRefs.Length > 1;

            var fullName = Markup.EscapeText(reference.FullName);
            var version = Markup.EscapeText(reference.Version.ToString());
            var sourceId = Markup.EscapeText(reference.Source.ToString());

            row.SetTitle($"{fullName} <span alpha='60%'>{version}</span>");
            row.SetSubtitle(Markup.EscapeText(reference.Resolve().Description));
            menuButton.SetVisible(hasMultipleSources);

            if (!hasMultipleSources)
                return;

            menuButton.SetSensitive(allowConfig);
            menuButton.SetTooltipText(reference.Source.Id);

            var allSources = allRefs.Select(x => x.Source).ToArray();

            int index = _lazyProfile.SourceIndex.IndexOf(reference.Source);
            string chosenColor = sourceColors[index % sourceColors.Length];

            foreach (var cls in menuButton.GetCssClasses())
            {
                if (cls.StartsWith("src-clr-", StringComparison.Ordinal))
                {
                    menuButton.RemoveCssClass(cls);
                }
            }

            string uniqueClassName = $"src-clr-{index}";
            menuButton.AddCssClass(uniqueClassName);

            var display = menuButton.GetDisplay();
            if (iconColorProvider != null)
            {
                StyleContext.RemoveProviderForDisplay(display, iconColorProvider);
            }

            iconColorProvider = CssProvider.New();

            string cssData = $$"""
                .{{uniqueClassName}} image,
                .{{uniqueClassName}} button {
                    color: {{chosenColor}};
                }
                """;
            iconColorProvider.LoadFromData(cssData, cssData.Length);

            StyleContext.AddProviderForDisplay(
                display,
                iconColorProvider,
                Gtk.Constants.STYLE_PROVIDER_PRIORITY_USER
            );

            var menu = Gio.Menu.New();

            for (int i = 0; i < allSources.Length; i++)
            {
                var sourceName = allSources[i].ToString();
                var menuItem = Gio.MenuItem.New(sourceName, $"row-scope.select-source({i})");
                menu.AppendItem(menuItem);
            }

            menuButton.SetMenuModel(menu);

            if (selectSourceAction != null)
            {
                actionGroup.RemoveAction("select-source");
            }

            var parameterType = VariantType.New("i");
            var initialState = Variant.NewInt32(index);

            selectSourceAction = Gio.SimpleAction.NewStateful(
                "select-source",
                parameterType,
                initialState
            );

            selectSourceAction.OnActivate += (s, e) =>
            {
                if (e.Parameter is null)
                    return;

                int selectedIndex = e.Parameter.GetInt32();
                var versionRef = allRefs[selectedIndex];
                selectSourceAction.SetState(e.Parameter);

                if (_currentProfile!.Added.ContainsKey((PackageReference)reference))
                {
                    _currentProfile.Add(
                        versionRef.Resolve(),
                        DependencyVersionResolution.Requested
                    );
                    UpdateConfiguration(_lazyProfile);
                }
                // We don't need to run SetData if we are on the page
                // which updates on UpdateConfiguration since it reloads everything.
                if (context != PackageVersionRow.Context.Added)
                {
                    SetData(versionRef);
                }
            };

            actionGroup.AddAction(selectSourceAction);
        }

        void OnClicked(ActionRow s, EventArgs e) => onClicked(verRef);
    }

    private static Button CreateActionButton(
        string iconName,
        string tooltip,
        string? extraClass = null
    )
    {
        var button = Button.NewFromIconName(iconName);
        button.AddCssClass("flat");
        if (!string.IsNullOrEmpty(extraClass))
            button.AddCssClass(extraClass);
        button.SetValign(Align.Center);
        button.SetTooltipText(tooltip);
        return button;
    }

    private static Button CreateAddOrRemoveButton(ModList profile, PackageReference package)
    {
        Button btn = CreateActionButton("list-add-symbolic", "Placeholder");
        SetVisualButtonAddOrRemoveState(profile, package, btn);

        btn.OnClicked += (btnSender, btnArgs) =>
        {
            ToggleButtonAddOrRemoveState(profile, package, btn);
        };
        return btn;
    }

    private static bool SetVisualButtonAddOrRemoveState(
        ModList profile,
        PackageReference package,
        Button btn
    )
    {
        if (profile.Added.ContainsKey(package))
        {
            btn.SetIconName("list-remove-symbolic");
            btn.SetCssClasses(["destructive-action", "flat"]);
            btn.SetTooltipText($"Remove {package.FullName}");
            return false;
        }
        else
        {
            btn.SetIconName("list-add-symbolic");
            btn.SetCssClasses(["flat"]);
            btn.SetTooltipText($"Remove {package.FullName}");
            return true;
        }
    }

    private static void ToggleButtonAddOrRemoveState(
        ModList profile,
        PackageReference package,
        Button btn
    )
    {
        if (!profile.Added.ContainsKey(package))
        {
            profile.Add(package, DependencyVersionResolution.Latest);
        }
        else
        {
            profile.Remove([package]);
        }

        SetVisualButtonAddOrRemoveState(profile, package, btn);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Dispose(true);
    }

    void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchCts?.Dispose();
        }
    }
}
