using Adw;
using Gtk;

namespace Cogwork.Gui;

public class ConfigureProfileViewController : IDisposable
{
    // --- State & Context Data Layer ---
    private readonly NavigationView _navView;
    private readonly Action _onBackNavigated;
    private ModList _currentProfile = null!;
    private CancellationTokenSource? _searchCts;
    private readonly Stack<PackageVersion> _dependants = new();

    // --- Critical UI Controls (Accessed Across View Cycles) ---
    public NavigationPage Page { get; }
    private readonly WindowTitle _windowTitle;
    private readonly ToggleButton _searchToggleButton;
    private readonly SearchEntry _searchEntry;
    private readonly SearchBar _searchBar;
    private readonly ViewStack _internalTabsStack;

    // --- Component Visibility & Layout Handles (Section Toggles) ---
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
        var manageTabBox = CreateManageTab(out _sectionAdded, out _sectionDeps, out _sectionRecent);
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

        // 5. Package Root Assembly Wrapper
        Page = NavigationPage.New(layoutBox, "configure_profile");
        Page.OnHiding += (s, e) => _onBackNavigated();
        Page.OnHidden += (s, e) => _currentProfile?.MarkDirty();
    }

    public void UpdateConfiguration(LazyModList lazyProfile)
    {
        _currentProfile = lazyProfile.GetModListAsync().Result;

        _windowTitle.SetTitle(GLib.Markup.EscapeText(lazyProfile.DisplayName));
        _windowTitle.SetSubtitle(GLib.Markup.EscapeText(lazyProfile.Game.Name));

        if (!_currentProfile.WasUpdated())
            return;

        ClearList(_sectionAdded.Content);

        if (_currentProfile.Added.Count > 0)
        {
            foreach (var mod in _currentProfile.Added.Values)
                AppendDirectRow(mod);
        }
        else
        {
            _sectionAdded.ToggleVisibility(false);
        }

        RebuildDependencies();
    }

    private void AppendDirectRow(PackageVersionReference mod)
    {
        if (_currentProfile == null)
            return;

        var row = CreateBaseRow(mod, OnModRowClicked);
        var removeButton = CreateActionButton(
            "list-remove-symbolic",
            $"Remove {mod.FullName}",
            "destructive-action"
        );

        removeButton.OnClicked += (s, e) =>
        {
            _currentProfile.Remove([mod.Package()]);
            _sectionAdded.Content.Remove(row);
            RebuildDependencies();

            if (_currentProfile.Added.Count == 0)
            {
                _sectionAdded.ToggleVisibility(false);
                _sectionRecent.Content.GrabFocus();
            }
        };

        row.AddSuffix(removeButton);
        _sectionAdded.Content.Append(row);
        RebuildDependencies();
        _sectionAdded.ToggleVisibility(true);
    }

    private void RebuildDependencies()
    {
        if (_currentProfile == null)
            return;

        ClearList(_sectionDeps.Content);

        if (_currentProfile.Dependencies.Count > 0)
        {
            foreach (var dep in _currentProfile.Dependencies.Values)
            {
                var row = CreateBaseRow(dep, OnModRowClicked);
                var addButton = CreateActionButton("go-up-symbolic", $"Add {dep.FullName}");

                addButton.OnClicked += (s, e) =>
                {
                    _currentProfile.Add(dep, DependencyVersionResolution.Latest);
                    AppendDirectRow(dep);

                    if (_currentProfile.Dependencies.Count == 0)
                    {
                        if (_currentProfile.RecentlyRemoved.Count > 0)
                            _sectionRecent.Content.GrabFocus();
                        else
                            _sectionAdded.Content.GrabFocus();
                    }
                };

                row.AddSuffix(addButton);
                _sectionDeps.Content.Append(row);
            }
            _sectionDeps.ToggleVisibility(true);
        }
        else
        {
            _sectionDeps.ToggleVisibility(false);
        }

        ClearList(_sectionRecent.Content);

        if (_currentProfile.RecentlyRemoved.Count > 0)
        {
            foreach (var dep in _currentProfile.RecentlyRemoved.Values)
            {
                var row = CreateBaseRow(dep, OnModRowClicked);
                var addButton = CreateActionButton("list-add-symbolic", $"Add {dep.FullName}");

                addButton.OnClicked += (s, e) =>
                {
                    _currentProfile.Add(dep, DependencyVersionResolution.Latest);
                    FireConfigRefresh();

                    if (_currentProfile.RecentlyRemoved.Count == 0)
                    {
                        if (_currentProfile.Dependencies.Count > 0)
                            _sectionDeps.Content.GrabFocus();
                        else
                            _sectionAdded.Content.GrabFocus();
                    }
                };

                row.AddSuffix(addButton);
                _sectionRecent.Content.Append(row);
            }
            _sectionRecent.ToggleVisibility(true);
        }
        else
        {
            _sectionRecent.ToggleVisibility(false);
        }

        _ = _currentProfile.WasUpdated();
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

        if (_searchCts is { })
        {
            _searchCts.Cancel();
            _searchCts.Dispose();
        }
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        Task.Run(
            async () =>
            {
                try
                {
                    var packages = await _currentProfile.SourceIndex.GetAllPackagesAsync(
                        null,
                        default
                    );
                    if (token.IsCancellationRequested)
                        return;
                    var searchResults = ModList.Search(packages, query).ToArray();
                    if (token.IsCancellationRequested)
                        return;
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
                                var row = CreateBaseRow(
                                    (PackageVersionReference)package.Latest,
                                    OnModRowClicked
                                );
                                var btn = CreateAddOrRemoveButton(
                                    _currentProfile,
                                    (PackageReference)package
                                );
                                row.AddSuffix(btn);
                                _sectionSearchResults.Content.Append(row);
                            }
                            return false;
                        }
                    );
                }
                catch (TaskCanceledException) { }
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
            var row = CreateBaseRow(
                (PackageVersionReference)dep,
                pk =>
                {
                    _dependants.Pop();
                    RenderModDetailsView(pk);
                }
            );
            var btn = CreateAddOrRemoveButton(_currentProfile, (PackageReference)dep.Package);
            row.AddSuffix(btn);
            _sectionDependant.Content.Append(row);
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
                var row = CreateBaseRow(
                    (PackageVersionReference)dep,
                    pk =>
                    {
                        _dependants.Push(packageVersion);
                        RenderModDetailsView(pk);
                    }
                );
                var btn = CreateAddOrRemoveButton(_currentProfile, (PackageReference)dep.Package);
                row.AddSuffix(btn);
                _sectionModDeps.Content.Append(row);
            }
        }
    } // --- Micro Layout Generation Helpers ---

    static Box CreateManageTab(out Section secAdded, out Section secDeps, out Section secRecent)
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
        secAdded = new Section(content, "Added", "No added mods. Type to search mods.");
        secDeps = new Section(content, "Dependencies");
        secRecent = new Section(content, "Recently Removed");
        return box;
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
    } // --- Placeholders for external utility stubs ---

    private static ActionRow CreateBaseRow(
        PackageVersionReference reference,
        Action<PackageVersionReference> onClicked
    )
    {
        var row = ActionRow.New();
        row.SetTitle($"{GLib.Markup.EscapeText(reference.FullName)} v{reference.Version}");
        row.SetSubtitle(GLib.Markup.EscapeText(reference.Resolve().Description));
        row.SetActivatable(true);

        row.OnActivated += (s, e) =>
        {
            onClicked(reference);
        };
        return row;
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
