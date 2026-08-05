global using static Cogwork.Core.CogworkCoreLogger;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("Linux")]

namespace Cogwork.Gui;

class Program
{
    public static int Main(string[] args)
    {
        NativeLibrary.SetDllImportResolver(
            typeof(JavaScriptCore.Context).Assembly,
            (libraryName, assembly, searchPath) =>
            {
                Console.WriteLine(libraryName);
                if (libraryName.Equals("JavaScriptCore", StringComparison.OrdinalIgnoreCase))
                {
                    if (NativeLibrary.TryLoad("libjavascriptcoregtk-6.0.so", out var handle))
                    {
                        return handle;
                    }

                    if (NativeLibrary.TryLoad("libjavascriptcoregtk-6.0.so.1", out handle))
                    {
                        return handle;
                    }
                }
                return IntPtr.Zero;
            }
        );
        NativeLibrary.SetDllImportResolver(
            typeof(WebKit.WebView).Assembly,
            (libraryName, assembly, searchPath) =>
            {
                Console.WriteLine(libraryName);
                if (libraryName.Equals("WebKit", StringComparison.OrdinalIgnoreCase))
                {
                    if (NativeLibrary.TryLoad("libwebkitgtk-6.0.so", out var handle))
                    {
                        return handle;
                    }
                    if (NativeLibrary.TryLoad("libwebkitgtk-6.0.so.4", out handle))
                    {
                        return handle;
                    }
                }
                else if (libraryName.Equals("JavaScriptCore", StringComparison.OrdinalIgnoreCase))
                {
                    if (NativeLibrary.TryLoad("libjavascriptcoregtk-6.0.so", out var handle))
                    {
                        return handle;
                    }

                    if (NativeLibrary.TryLoad("libjavascriptcoregtk-6.0.so.1", out handle))
                    {
                        return handle;
                    }
                }
                return IntPtr.Zero;
            }
        );

        var app = Adw.Application.New("io.github.hamunii.cogwork", Gio.ApplicationFlags.FlagsNone);

        app.OnActivate += (sender, e) =>
        {
            var games = Game.SupportedGames;

            var window = Adw.ApplicationWindow.New(app);
            window.SetDefaultSize(1000, 700);

            // 1. Swap Adw.ViewStack for Adw.NavigationView
            var navView = Adw.NavigationView.New();

            // Local state pointer to track what game layout we are viewing
            Game? currentActiveGame = null;

            // Declare our output delegates so they are scoped for the whole block
            Action<Game>? updateProfileContent = null;
            Action<LazyModList>? updateConfigContent = null;

            // 2. Adjust the refresh callback action to look at active context
            Action refreshProfilesCallback = () =>
            {
                if (currentActiveGame != null && updateProfileContent != null)
                {
                    updateProfileContent(currentActiveGame);
                }
            };

            // 1. Construct View 3 (Config), which spits out 'updateConfigContent'
            var configPage = CreateConfigureProfileView(
                navView,
                refreshProfilesCallback,
                out updateConfigContent
            );

            // 2. Construct View 2 (Profiles), PASSING the configPage reference explicitly
            var profilePage = CreateProfileView(
                navView,
                configPage, // Passed destination object
                updateConfigContent, // Target configuration trigger hook
                out updateProfileContent
            );

            // 3. Construct View 1 (Dashboard) with a tracking lambda interceptor
            var dashboardPage = CreateDashboardView(
                navView,
                games,
                profilePage, // Passed destination object
                (selectedGame) =>
                {
                    currentActiveGame = selectedGame;
                    updateProfileContent(selectedGame);
                }
            );

            // 4. Seed the container view with our starting root dashboard node layout
            navView.Push(dashboardPage);

            window.SetContent(navView);
            window.Present();
        };

        return app.Run(args);
    }

    // ================= VIEW 1: GAME GRID DASHBOARD =================
    private static Adw.NavigationPage CreateDashboardView(
        Adw.NavigationView navView,
        List<Game> games,
        Adw.NavigationPage profilePage, // Accepted target reference
        Action<Game> onGameSelected
    )
    {
        var layoutBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(Adw.WindowTitle.New("Cogwork", "Select game to mod"));
        layoutBox.Append(header);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetVexpand(true);
        layoutBox.Append(scroll);

        var grid = Gtk.FlowBox.New();
        grid.SetHomogeneous(false);
        grid.UnselectAll();
        grid.SetHalign(Gtk.Align.Center);
        grid.SetValign(Gtk.Align.Start);
        grid.SetMinChildrenPerLine(2);
        grid.SetMaxChildrenPerLine(5);
        grid.SetSelectionMode(Gtk.SelectionMode.None);
        grid.SetColumnSpacing(16);
        grid.SetRowSpacing(16);
        grid.SetMarginTop(16);
        grid.SetMarginBottom(16);
        grid.SetMarginStart(16);
        grid.SetMarginEnd(16);

        scroll.SetChild(grid);

        var childToGameMap = new Dictionary<Gtk.FlowBoxChild, Game>();

        foreach (var game in games)
        {
            var childContainer = Gtk.FlowBoxChild.New();
            childContainer.AddCssClass("card");
            childContainer.SetSizeRequest(140, 180);
            childContainer.SetHalign(Gtk.Align.Center);
            childContainer.SetValign(Gtk.Align.Center);

            var cardContent = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
            cardContent.SetMarginTop(12);
            cardContent.SetMarginBottom(12);

            var coverArtPlaceholder = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            coverArtPlaceholder.SetSizeRequest(100, 100);
            coverArtPlaceholder.SetHalign(Gtk.Align.Center);
            coverArtPlaceholder.AddCssClass("thumbnail");
            coverArtPlaceholder.SetTooltipText(game.Name);
            cardContent.Append(coverArtPlaceholder);

            var titleLabel = Gtk.Label.New(game.Name);
            titleLabel.AddCssClass("bold");
            titleLabel.SetWrap(true);
            titleLabel.SetWrapMode(Pango.WrapMode.WordChar);
            titleLabel.SetJustify(Gtk.Justification.Center);
            titleLabel.SetMaxWidthChars(14);
            titleLabel.SetWidthChars(14);
            cardContent.Append(titleLabel);

            childContainer.SetChild(cardContent);
            grid.Insert(childContainer, -1);

            childToGameMap[childContainer] = game;
        }

        grid.OnChildActivated += (senderGrid, args) =>
        {
            if (childToGameMap.TryGetValue(args.Child, out var clickedGame))
            {
                onGameSelected(clickedGame);
                // 5. Navigate forward using explicit Push instead of visibility names
                navView.Push(profilePage);
            }
        };

        // 6. Return the layout boxed directly into a clean NavigationPage wrapper
        return Adw.NavigationPage.New(layoutBox, "dashboard");
    }

    // ================= VIEW 2: MOD PROFILES LIST =================
    // 1. Change return type to Adw.NavigationPage, accept Adw.NavigationView,
    // and pass the target configPage down into the initialization lifecycle.
    private static Adw.NavigationPage CreateProfileView(
        Adw.NavigationView navView,
        Adw.NavigationPage configPage, // Accept the destination page reference
        Action<LazyModList> onProfileSelected,
        out Action<Game> updateContentCallback
    )
    {
        var layoutBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        var header = Adw.HeaderBar.New();
        var windowTitle = Adw.WindowTitle.New("Loading Profiles...", "");
        header.SetTitleWidget(windowTitle);

        // Adw.HeaderBar will now automatically inject a back arrow button
        // when this page is pushed onto an Adw.NavigationView stack.
        // It also handles trackpad/touchscreen swipe-to-back gestures natively.

        layoutBox.Append(header);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetVexpand(true);
        layoutBox.Append(scroll);

        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(800);
        scroll.SetChild(clamp);

        var contentStack = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
        contentStack.SetMarginTop(24);
        contentStack.SetMarginBottom(24);
        contentStack.SetMarginStart(24);
        contentStack.SetMarginEnd(24);
        clamp.SetChild(contentStack);

        var listBox = CreateSection(contentStack, "Profiles", out _);

        updateContentCallback = (selectedGame) =>
        {
            windowTitle.SetTitle(selectedGame.Name);
            windowTitle.SetSubtitle("Select mod profile");

            while (listBox.GetFirstChild() != null)
            {
                listBox.Remove(listBox.GetFirstChild()!);
            }

            foreach (var profile in selectedGame.EnumerateProfiles())
            {
                var addedCount = profile.ResolvedAdded?.Count ?? 0;
                var depCount = profile.ResolvedDependencies?.Count ?? 0;
                var row = Adw.ActionRow.New();
                row.SetTitle(profile.DisplayName);
                row.SetSubtitle(
                    $"{addedCount} added, {depCount} {(depCount == 1 ? "dependency" : "dependencies")}"
                );

                // Make the row itself mimic a giant button
                row.SetActivatable(true);

                // Trigger view swap to config page when clicking the row body
                row.OnActivated += (s, e) =>
                {
                    onProfileSelected(profile);

                    // 2. REPLACED: Push the page target onto the view stack natively
                    // instead of calling string-based view switching.
                    navView.Push(configPage);
                };

                // Add quick button to launch the game on the right side
                var launchButton = Gtk.Button.NewFromIconName("media-playback-start-symbolic");
                launchButton.SetValign(Gtk.Align.Center);
                launchButton.SetTooltipText($"Launch with {profile.DisplayName}");

                launchButton.OnClicked += (s, e) =>
                {
                    // TODO: Proper API
                    _ = Cli.Program.Main([
                        "launch",
                        "--game",
                        profile.Game.Slug,
                        "--profile",
                        profile.Id,
                    ]);
                };

                row.AddSuffix(launchButton);
                listBox.Append(row);
            }
        };

        // 3. Return the entire layout wrapped inside a clean NavigationPage instance
        return Adw.NavigationPage.New(layoutBox, "profiles");
    }

    // ================= VIEW 3: PROFILE CONFIGURATION VIEW =================
    // 1. Change return type to Adw.NavigationPage and accept Adw.NavigationView instead of Adw.ViewStack
    private static Adw.NavigationPage CreateConfigureProfileView(
        Adw.NavigationView navView,
        Action onBackNavigated,
        out Action<LazyModList> updateConfigCallback
    )
    {
        var layoutBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        // Top navigation and header setup
        var header = Adw.HeaderBar.New();
        var windowTitle = Adw.WindowTitle.New("Manage Profile", "");
        header.SetTitleWidget(windowTitle);

        // 2. REMOVED: Manual back button configuration.
        // Adw.HeaderBar will now natively render a back button and handle popping the view.

        // GNOME SOFTWARE STYLE: Create search toggle button in the header
        var searchToggleButton = Gtk.ToggleButton.New();
        searchToggleButton.SetIconName("edit-find-symbolic");
        header.PackStart(searchToggleButton);

        layoutBox.Append(header);

        // GNOME Software Style Search Bar Container
        var searchBar = Gtk.SearchBar.New();
        var searchEntry = Gtk.SearchEntry.New();
        searchEntry.SetPlaceholderText("Search mods...");
        searchEntry.SetHexpand(true);
        searchEntry.SetHalign(Gtk.Align.Center);
        searchEntry.SetSizeRequest(400, -1);

        searchBar.SetChild(searchEntry);
        searchBar.ConnectEntry(searchEntry);

        searchEntry.SetKeyCaptureWidget(layoutBox);
        searchEntry.SetSearchDelay(0);

        layoutBox.Append(searchBar);

        // Tab Stack Content Setup (NOTE: This is internal to the page, so it stays an Adw.ViewStack)
        var internalTabsStack = Adw.ViewStack.New();
        internalTabsStack.SetEnableTransitions(false);
        internalTabsStack.SetVexpand(true);

        ModList? profile = null;
        Action<LazyModList>? updateConfig = null;

        // Dynamic State Switching Hooks
        searchToggleButton.OnToggled += (s, e) =>
        {
            bool isActive = searchToggleButton.GetActive();
            searchBar.SetSearchMode(isActive);

            if (isActive)
            {
                searchEntry.GrabFocus();
            }
            else
            {
                searchEntry.SetText("");
            }
        };

        layoutBox.Append(internalTabsStack);

        // ================= TAB 1: MANAGE MODS (CURRENT VIEW) =================
        var manageTabBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        var scrollManage = Gtk.ScrolledWindow.New();
        scrollManage.SetVexpand(true);
        manageTabBox.Append(scrollManage);

        var clampManage = Adw.Clamp.New();
        clampManage.SetMaximumSize(800);
        scrollManage.SetChild(clampManage);

        var contentStack = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
        contentStack.SetMarginTop(24);
        contentStack.SetMarginBottom(24);
        contentStack.SetMarginStart(24);
        contentStack.SetMarginEnd(24);
        clampManage.SetChild(contentStack);

        var addedListBox = CreateSection(
            contentStack,
            "Added",
            "No added mods. Type to search mods.",
            out var addedSectionLabel,
            out var addedEmptyLabel
        );
        var depsListBox = CreateSection(contentStack, "Dependencies", out var depsSectionLabel);
        var recentListBox = CreateSection(
            contentStack,
            "Recently Removed",
            out var recentSectionLabel
        );

        var managePage = internalTabsStack.AddNamed(manageTabBox, "manage_tab");
        managePage.SetTitle("Manage");
        managePage.SetIconName("emblem-system-symbolic");

        // ================= STATE PERSISTENCE HOOKS =================
        Action<ModList>? rebuildDependenciesAction = null;
        Action<PackageVersion, ModList>? appendDirectRowAction = null;

        // ================= TAB 2: INSTALL MODS (NEW VIEW) =================
        var installTabBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        var scrollInstall = Gtk.ScrolledWindow.New();
        scrollInstall.SetVexpand(true);
        installTabBox.Append(scrollInstall);

        var clampInstall = Adw.Clamp.New();
        clampInstall.SetMaximumSize(800);
        scrollInstall.SetChild(clampInstall);

        var stackInstall = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
        stackInstall.SetMarginTop(24);
        stackInstall.SetMarginBottom(24);
        stackInstall.SetMarginStart(24);
        stackInstall.SetMarginEnd(24);
        clampInstall.SetChild(stackInstall);

        var installListBox = CreateSection(
            stackInstall,
            "Search Results",
            "No matches.",
            out var resultsLabel,
            out var noMatchesLabel
        );

        var shortcutController = Gtk.ShortcutController.New();

        uint escapeKeyval = Gdk.Functions.KeyvalFromName("Escape");
        var escapeTrigger = Gtk.KeyvalTrigger.New(escapeKeyval, 0);

        var backAction = Gtk.CallbackAction.New(
            (widget, args) =>
            {
                internalTabsStack.SetVisibleChildName("manage_tab");
                searchToggleButton.SetActive(false);

                if (profile is { })
                    updateConfig?.Invoke(profile.Lazy);

                return true;
            }
        );

        var escapeShortcut = Gtk.Shortcut.New(escapeTrigger, backAction);

        shortcutController.AddShortcut(escapeShortcut);
        stackInstall.AddController(shortcutController);

        var modBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        var modHeader = Adw.HeaderBar.New();
        modHeader.SetShowTitle(false);
        modHeader.AddCssClass("flat");
        modBox.Append(modHeader);

        var scrollMod = Gtk.ScrolledWindow.New();
        scrollMod.SetVexpand(true);
        modBox.Append(scrollMod);

        var clampMod = Adw.Clamp.New();
        clampMod.SetMaximumSize(800);
        scrollMod.SetChild(clampMod);

        var modContent = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
        modContent.SetMarginTop(24);
        modContent.SetMarginBottom(24);
        modContent.SetMarginStart(24);
        modContent.SetMarginEnd(24);
        clampMod.SetChild(modContent);

        var modDependant = CreateSection(modContent, "Dependant", out var modDependantLabel);

        var modLabel = Gtk.Label.New("mod_name");
        modLabel.AddCssClass("heading");
        modLabel.SetHalign(Gtk.Align.Start);
        modLabel.SetWrapMode(Pango.WrapMode.Word);
        modLabel.SetWrap(true);
        modContent.Append(modLabel);

        var modDescriptionLabel = Gtk.Label.New("mod_description");
        modDescriptionLabel.SetHalign(Gtk.Align.Start);
        modDescriptionLabel.SetWrapMode(Pango.WrapMode.Word);
        modDescriptionLabel.SetWrap(true);
        modContent.Append(modDescriptionLabel);

        var modSourceLabel = Gtk.Label.New("mod_source");
        modSourceLabel.AddCssClass("dim-label");
        modSourceLabel.SetHalign(Gtk.Align.Start);
        modSourceLabel.SetWrapMode(Pango.WrapMode.Word);
        modSourceLabel.SetWrap(true);
        modContent.Append(modSourceLabel);

        var markdownPreviewer = MarkdownPreviewer.NewWithProperties([]);
        markdownPreviewer.SetSizeRequest(-1, 100);
        modContent.Append(markdownPreviewer);

        var modDependencies = CreateSection(
            modContent,
            "Dependencies",
            "None.",
            out var modDepLabel,
            out var modNoneLabel
        );

        var modPage = Adw.NavigationPage.New(modBox, "mod_page");
        modPage.OnHiding += (navPage, args) =>
        {
            updateConfig!(profile!.Lazy);
        };

        Stack<PackageVersion> dependants = [];

        modPage.OnHidden += (navPage, args) =>
        {
            if (internalTabsStack.GetVisibleChildName() == "install_tab")
            {
                searchEntry.GrabFocus();
            }
            else
            {
                // FIXME: Temporary fix for if no focus.
                // Optimally keep track which element should be focused.
                searchToggleButton.GrabFocus();
            }
            dependants.Clear();
        };

        void OnClicked2(PackageVersion packageVersion)
        {
            ClearList(modDependant);

            if (dependants.Count == 0)
            {
                ToggleSectionVisibility(modDependantLabel, modDependant, false);
            }
            else
            {
                ToggleSectionVisibility(modDependantLabel, modDependant, true);

                var dep = dependants.Peek();
                var row = CreateBaseRow(
                    dep,
                    pk =>
                    {
                        dependants.Pop();
                        OnClicked2(pk);
                    }
                );
                var btn = CreateAddOrRemoveButton(profile, dep.Package);
                row.AddSuffix(btn);
                modDependant.Append(row);
            }

            modLabel.SetText(packageVersion.Package.FullName);
            modDescriptionLabel.SetText(packageVersion.Description);
            modSourceLabel.SetText($"Source: {packageVersion.Package.Source.Id}");
            markdownPreviewer.Render(packageVersion.GetReadmeAsync().Result);

            ClearList(modDependencies);
            if (packageVersion.MarkedDependencies.Length == 0)
            {
                ToggleSectionVisibility(modDepLabel, modNoneLabel, modDependencies, false);
            }
            else
            {
                ToggleSectionVisibility(modDepLabel, modNoneLabel, modDependencies, true);

                foreach (var dep in packageVersion.MarkedDependencies)
                {
                    var row = CreateBaseRow(
                        dep,
                        (pk) =>
                        {
                            dependants.Push(packageVersion);
                            OnClicked2(pk);
                        }
                    );
                    var btn = CreateAddOrRemoveButton(profile, dep.Package);
                    row.AddSuffix(btn);
                    modDependencies.Append(row);
                }
            }
        }

        void OnClicked(PackageVersion packageVersion)
        {
            // dependants.Push(packageVersion);
            OnClicked2(packageVersion);
            navView.Push(modPage);
        }

        CancellationTokenSource? searchCts = null;

        searchEntry.OnStopSearch += (searchEntry, e) =>
        {
            searchToggleButton.SetActive(false);
        };

        searchEntry.OnSearchChanged += (searchEntry, e) =>
        {
            string currentText = searchEntry.GetText();
            if (!searchEntry.HasFocus)
            {
                searchEntry.GrabFocus();
            }

            if (string.IsNullOrEmpty(currentText))
            {
                internalTabsStack.SetVisibleChildName("manage_tab");

                if (profile is { })
                    updateConfig?.Invoke(profile.Lazy);
            }
            else
            {
                if (internalTabsStack.GetVisibleChildName() != "install_tab")
                {
                    internalTabsStack.SetVisibleChildName("install_tab");
                }

                if (!searchToggleButton.GetActive())
                {
                    searchToggleButton.SetActive(true);
                }
            }

            if (profile == null)
                return;

            string query = currentText.Trim().Replace(' ', '_');
            if (string.IsNullOrEmpty(query))
                return;

            searchCts?.Cancel();
            searchCts = new CancellationTokenSource();
            var token = searchCts.Token;

            Task.Run(
                async () =>
                {
                    try
                    {
                        var packages = await profile.SourceIndex.GetAllPackagesAsync(
                            progressFactory: null,
                            cancellationToken: default
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
                                ClearList(installListBox);

                                if (searchResults.Length == 0)
                                {
                                    ToggleSectionVisibility(
                                        resultsLabel,
                                        noMatchesLabel,
                                        installListBox,
                                        false
                                    );
                                    return false;
                                }
                                ToggleSectionVisibility(
                                    resultsLabel,
                                    noMatchesLabel,
                                    installListBox,
                                    true
                                );

                                foreach (var package in searchResults)
                                {
                                    var row = CreateBaseRow(package.Latest, OnClicked);
                                    var btn = CreateAddOrRemoveButton(profile, package);
                                    row.AddSuffix(btn);
                                    installListBox.Append(row);
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
        };

        var installPage = internalTabsStack.AddNamed(installTabBox, "install_tab");
        installPage.SetTitle("Browse");
        installPage.SetIconName("list-add-symbolic");

        // ================= FINAL WRAPPING AND ASSEMBLY =================
        // 1. Pack the top-level layout box layout inside our new container type
        var navigationPage = Adw.NavigationPage.New(layoutBox, "configure_profile");

        // 2. Trigger the refresh action handler dynamically when the page gets popped
        navigationPage.OnHiding += (s, e) =>
        {
            onBackNavigated();
        };

        // ================= POPULATE LOGIC LOOP =================
        updateConfigCallback = (lazyProfile) =>
        {
            profile = lazyProfile.GetModListAsync().Result;

            windowTitle.SetTitle(GLib.Markup.EscapeText(lazyProfile.DisplayName));
            windowTitle.SetSubtitle(GLib.Markup.EscapeText(lazyProfile.Game.Name));

            ClearList(addedListBox);

            // --- Helper Action: Build Direct/Added Mod Row ---
            appendDirectRowAction = (mod, currentProfile) =>
            {
                var row = CreateBaseRow(mod, OnClicked);
                // ... remainder of file continues safely ...

                var removeButton = CreateActionButton(
                    "list-remove-symbolic",
                    $"Remove {mod.Package.FullName}",
                    "destructive-action"
                );

                removeButton.OnClicked += (s, e) =>
                {
                    currentProfile.Remove([mod.Package]);
                    addedListBox.Remove(row);
                    rebuildDependenciesAction?.Invoke(currentProfile);
                    if (profile.Added.Count == 0)
                    {
                        ToggleSectionVisibility(
                            addedSectionLabel,
                            addedEmptyLabel,
                            addedListBox,
                            false
                        );
                        // Ensure focus doesn't disappear by moving it where there are elements
                        recentListBox.GrabFocus();
                    }
                };

                row.AddSuffix(removeButton);
                addedListBox.Append(row);
                rebuildDependenciesAction?.Invoke(currentProfile);
                ToggleSectionVisibility(addedSectionLabel, addedEmptyLabel, addedListBox, true);
            };

            // --- Populate Added Mods ---
            if (profile.Added.Count > 0)
            {
                foreach (var mod in profile.Added.Values)
                    appendDirectRowAction(mod, profile);
            }
            else
            {
                ToggleSectionVisibility(addedSectionLabel, addedEmptyLabel, addedListBox, false);
            }

            // --- Rebuild Loop for Dependencies ---
            rebuildDependenciesAction = (activeProfile) =>
            {
                ClearList(depsListBox);

                if (activeProfile.Dependencies.Count > 0)
                {
                    foreach (var dep in activeProfile.Dependencies.Values)
                    {
                        // FIXED: Passing dep.Value directly down into CreateBaseRow configuration
                        var row = CreateBaseRow(dep, OnClicked);
                        var addButton = CreateActionButton(
                            "go-up-symbolic",
                            $"Add {dep.Package.FullName}"
                        );

                        addButton.OnClicked += (s, e) =>
                        {
                            activeProfile.Add(dep, DependencyVersionResolution.Latest);
                            appendDirectRowAction?.Invoke(dep, activeProfile);

                            if (activeProfile.Dependencies.Count == 0)
                            {
                                if (profile.RecentlyRemoved.Count > 0)
                                {
                                    recentListBox.GrabFocus();
                                }
                                else
                                {
                                    addedListBox.GrabFocus();
                                }
                            }
                        };

                        row.AddSuffix(addButton);
                        depsListBox.Append(row);
                    }
                    ToggleSectionVisibility(depsSectionLabel, depsListBox, true);
                }
                else
                {
                    ToggleSectionVisibility(depsSectionLabel, depsListBox, false);
                }

                ClearList(recentListBox);

                if (activeProfile.RecentlyRemoved.Count > 0)
                {
                    foreach (var dep in activeProfile.RecentlyRemoved.Values)
                    {
                        // FIXED: Passing dep.Value directly down into CreateBaseRow configuration
                        var row = CreateBaseRow(dep, OnClicked);
                        var addButton = CreateActionButton(
                            "list-add-symbolic",
                            $"Add {dep.Package.FullName}"
                        );

                        addButton.OnClicked += (s, e) =>
                        {
                            activeProfile.Add(dep, DependencyVersionResolution.Latest);
                            // Update whole thing because this package may swap
                            // with a package from added if the package exists in
                            // multiple sources. Easiest way to avoid desync.
                            updateConfig?.Invoke(activeProfile.Lazy);

                            if (profile.RecentlyRemoved.Count == 0)
                            {
                                if (profile.Dependencies.Count > 0)
                                {
                                    depsListBox.GrabFocus();
                                }
                                else
                                {
                                    addedListBox.GrabFocus();
                                }
                            }
                        };

                        row.AddSuffix(addButton);
                        recentListBox.Append(row);
                    }
                    ToggleSectionVisibility(recentSectionLabel, recentListBox, true);
                }
                else
                {
                    ToggleSectionVisibility(recentSectionLabel, recentListBox, false);
                }
            };

            rebuildDependenciesAction(profile);
        };

        updateConfig = updateConfigCallback;

        return navigationPage;
    }

    private static Gtk.Button CreateAddOrRemoveButton(ModList profile, Package package)
    {
        Gtk.Button? btn = null;
        if (!profile.Added.ContainsKey(package))
            btn = CreateActionButton("list-add-symbolic", "Add");
        else
            btn = CreateActionButton("list-remove-symbolic", "Remove", "destructive-action");

        btn.OnClicked += (btnSender, btnArgs) =>
        {
            if (!profile.Added.ContainsKey(package))
            {
                profile.Add(package, DependencyVersionResolution.Latest);
                btn.SetIconName("list-remove-symbolic");
                btn.SetCssClasses(["destructive-action"]);
                btn.SetTooltipText("Remove");
            }
            else
            {
                profile.Remove([package]);
                btn.SetIconName("list-add-symbolic");
                btn.SetCssClasses([]);
                btn.SetTooltipText("Add");
            }
        };
        return btn;
    }

    // ================= STATIC UI HELPERS TO PREVENT DUPLICATION =================

    private static Gtk.ListBox CreateSection(
        Gtk.Box parent,
        string headingText,
        out Gtk.Label labelWidget
    )
    {
        labelWidget = Gtk.Label.New(headingText);
        labelWidget.SetHalign(Gtk.Align.Start);
        labelWidget.AddCssClass("heading");

        var listBox = Gtk.ListBox.New();
        listBox.AddCssClass("boxed-list");
        listBox.SetSelectionMode(Gtk.SelectionMode.None);

        parent.Append(labelWidget);
        parent.Append(listBox);
        return listBox;
    }

    private static Gtk.ListBox CreateSection(
        Gtk.Box parent,
        string headingText,
        string emptyText,
        out Gtk.Label labelWidget,
        out Gtk.Label emptyLabelWidget
    )
    {
        labelWidget = Gtk.Label.New(headingText);
        labelWidget.SetHalign(Gtk.Align.Start);
        labelWidget.AddCssClass("heading");

        emptyLabelWidget = Gtk.Label.New(emptyText);
        emptyLabelWidget.SetHalign(Gtk.Align.Start);
        emptyLabelWidget.AddCssClass("dim-label");
        emptyLabelWidget.Hide();

        var listBox = Gtk.ListBox.New();
        listBox.AddCssClass("boxed-list");
        listBox.SetSelectionMode(Gtk.SelectionMode.None);

        parent.Append(labelWidget);
        parent.Append(emptyLabelWidget);
        parent.Append(listBox);
        return listBox;
    }

    private static Adw.ActionRow CreateBaseRow(
        PackageVersion packageVersion,
        Action<PackageVersion> onClicked
    )
    {
        var package = packageVersion.Package;

        var row = Adw.ActionRow.New();
        row.SetTitle($"{GLib.Markup.EscapeText(package.FullName)} v{packageVersion.Version}");
        row.SetSubtitle(GLib.Markup.EscapeText(packageVersion.Description));
        row.SetActivatable(true);

        row.OnActivated += (s, e) =>
        {
            onClicked(packageVersion);
        };
        return row;
    }

    private static Gtk.Button CreateActionButton(
        string iconName,
        string tooltip,
        string? extraClass = null
    )
    {
        var button = Gtk.Button.NewFromIconName(iconName);
        button.AddCssClass("flat");
        if (!string.IsNullOrEmpty(extraClass))
            button.AddCssClass(extraClass);
        button.SetValign(Gtk.Align.Center);
        button.SetTooltipText(tooltip);
        return button;
    }

    private static void ClearList(Gtk.ListBox listBox)
    {
        while (listBox.GetFirstChild() != null)
            listBox.Remove(listBox.GetFirstChild()!);
    }

    private static void ToggleSectionVisibility(Gtk.Label label, Gtk.ListBox list, bool visible)
    {
        if (visible)
        {
            label.Show();
            list.Show();
        }
        else
        {
            label.Hide();
            list.Hide();
        }
    }

    private static void ToggleSectionVisibility(
        Gtk.Label label,
        Gtk.Label hiddenLabel,
        Gtk.ListBox list,
        bool visible
    )
    {
        if (visible)
        {
            label.Show();
            hiddenLabel.Hide();
            list.Show();
        }
        else
        {
            label.Show();
            hiddenLabel.Show();
            list.Hide();
        }
    }
}
