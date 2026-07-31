global using static Cogwork.Core.CogworkCoreLogger;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cogwork.Gui;

class Program
{
    public static int Main(string[] args)
    {
        var app = Adw.Application.New("io.github.hamunii.cogwork", Gio.ApplicationFlags.FlagsNone);

        app.OnActivate += (sender, e) =>
        {
            var games = Game.SupportedGames;

            var window = Adw.ApplicationWindow.New(app);
            window.SetDefaultSize(1000, 700);

            var viewStack = Adw.ViewStack.New();
            viewStack.SetEnableTransitions(false);

            // 1. Local state pointer to track what game layout we are viewing
            Game? currentActiveGame = null;

            // 2. Declare our output delegates so they are scoped for the whole block
            Action<Game>? updateProfileContent = null;
            Action<LazyModList>? updateConfigContent = null;

            // 3. Define the refresh callback action that runs when returning from config view
            Action refreshProfilesCallback = () =>
            {
                if (currentActiveGame != null && updateProfileContent != null)
                {
                    updateProfileContent(currentActiveGame);
                }
            };

            // 4. Construct View 3 (Config), which spits out 'updateConfigContent'
            var configPage = CreateConfigureProfileView(
                viewStack,
                refreshProfilesCallback,
                out updateConfigContent
            );

            // 5. Construct View 2 (Profiles), passing the config action and catching 'updateProfileContent'
            var profilePage = CreateProfileView(
                viewStack,
                updateConfigContent, // Target configuration trigger hook
                out updateProfileContent
            );

            // 6. Construct View 1 (Dashboard) with a tracking lambda interceptor
            var dashboardPage = CreateDashboardView(
                viewStack,
                games,
                (selectedGame) =>
                {
                    currentActiveGame = selectedGame; // Track active choice pointer
                    updateProfileContent(selectedGame); // Initial population run
                }
            );

            // 7. Register everything onto the view stack container
            viewStack.AddNamed(dashboardPage, "dashboard");
            viewStack.AddNamed(profilePage, "profiles");
            viewStack.AddNamed(configPage, "configure_profile");

            viewStack.SetVisibleChildName("dashboard");
            window.SetContent(viewStack);
            window.Present();
        };

        return app.Run(args);
    }

    // ================= VIEW 1: GAME GRID DASHBOARD =================
    private static Gtk.Box CreateDashboardView(
        Adw.ViewStack stack,
        List<Game> games,
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

        // Center the grid block horizontally inside the scroll area
        grid.SetHalign(Gtk.Align.Center);
        grid.SetValign(Gtk.Align.Start);

        grid.SetMinChildrenPerLine(2);
        grid.SetMaxChildrenPerLine(5);

        // FIXED 1: Turn selection mode back on so rows handle hover/clicks natively
        grid.SetSelectionMode(Gtk.SelectionMode.None);

        grid.SetColumnSpacing(16);
        grid.SetRowSpacing(16);
        grid.SetMarginTop(16);
        grid.SetMarginBottom(16);
        grid.SetMarginStart(16);
        grid.SetMarginEnd(16);

        scroll.SetChild(grid);

        // Dictionary to tie the auto-generated UI wrapper child back to our Game data reference
        var childToGameMap = new Dictionary<Gtk.FlowBoxChild, Game>();

        foreach (var game in games)
        {
            // FIXED 2: Create a native FlowBoxChild container instead of a Gtk.Button
            var childContainer = Gtk.FlowBoxChild.New();
            childContainer.AddCssClass("card"); // Applies the clean card-shape and uniform hover styling
            childContainer.SetSizeRequest(140, 180);
            childContainer.SetHalign(Gtk.Align.Center);
            childContainer.SetValign(Gtk.Align.Center);

            var cardContent = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
            cardContent.SetMarginTop(12);
            cardContent.SetMarginBottom(12);

            var coverArtPlaceholder = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            coverArtPlaceholder.SetSizeRequest(100, 100);
            coverArtPlaceholder.SetHalign(Gtk.Align.Center);
            coverArtPlaceholder.AddCssClass("thumbnail"); // Safe to use now without duplicate glowing
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

            // Assign content straight to our container row child
            childContainer.SetChild(cardContent);
            grid.Insert(childContainer, -1);

            // Save relationship reference for the grid activation lookup
            childToGameMap[childContainer] = game;
        }

        // FIXED 3: Handle single-click navigation globally on the grid wrapper
        grid.OnChildActivated += (senderGrid, args) =>
        {
            // args.Child tells us exactly which FlowBoxChild container wrapper was clicked
            if (childToGameMap.TryGetValue(args.Child, out var clickedGame))
            {
                onGameSelected(clickedGame);
                stack.SetVisibleChildName("profiles");
            }
        };

        return layoutBox;
    }

    // ================= VIEW 2: MOD PROFILES LIST =================
    private static Gtk.Box CreateProfileView(
        Adw.ViewStack stack,
        Action<LazyModList> onProfileSelected, // Callback pointing to the config view logic (accepting your custom profile type)
        out Action<Game> updateContentCallback
    )
    {
        var layoutBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        var header = Adw.HeaderBar.New();
        var windowTitle = Adw.WindowTitle.New("Loading Profiles...", "");
        header.SetTitleWidget(windowTitle);

        var backButton = Gtk.Button.NewFromIconName("go-previous-symbolic");
        backButton.OnClicked += (s, e) => stack.SetVisibleChildName("dashboard");
        header.PackStart(backButton);

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

                // 1. Make the row itself mimic a giant button
                row.SetActivatable(true);

                // Trigger view swap to config page when clicking the row body
                row.OnActivated += (s, e) =>
                {
                    onProfileSelected(profile);
                    stack.SetVisibleChildName("configure_profile");
                };

                // 2. Add quick button to launch the game on the right side
                var launchButton = Gtk.Button.NewFromIconName("media-playback-start-symbolic");
                launchButton.SetValign(Gtk.Align.Center);
                launchButton.SetTooltipText($"Launch with {profile.DisplayName}");

                launchButton.OnClicked += (s, e) =>
                {
                    // TODO: Connect this to your launching function mechanism
                    Console.WriteLine($"Quick launching game profile: {profile.DisplayName}");
                };

                row.AddSuffix(launchButton);
                listBox.Append(row);
            }
        };

        return layoutBox;
    }

    // ================= VIEW 3: PROFILE CONFIGURATION VIEW =================
    private static Gtk.Box CreateConfigureProfileView(
        Adw.ViewStack stack,
        Action onBackNavigated,
        out Action<LazyModList> updateConfigCallback
    )
    {
        var layoutBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        // 1. Top navigation and header setup
        var header = Adw.HeaderBar.New();
        var windowTitle = Adw.WindowTitle.New("Manage Profile", "");
        header.SetTitleWidget(windowTitle);

        var backButton = Gtk.Button.NewFromIconName("go-previous-symbolic");
        backButton.OnClicked += (s, e) =>
        {
            onBackNavigated();
            stack.SetVisibleChildName("profiles");
        };
        header.PackStart(backButton);

        // GNOME SOFTWARE STYLE: Create search toggle button in the header
        var searchToggleButton = Gtk.ToggleButton.New();
        searchToggleButton.SetIconName("edit-find-symbolic");
        header.PackStart(searchToggleButton);

        layoutBox.Append(header);

        // 2. GNOME Software Style Search Bar Container
        var searchBar = Gtk.SearchBar.New();
        var searchEntry = Gtk.SearchEntry.New();
        searchEntry.SetPlaceholderText("Search mods...");
        searchEntry.SetHexpand(true);
        searchEntry.SetHalign(Gtk.Align.Center);
        searchEntry.SetSizeRequest(400, -1);

        searchBar.SetChild(searchEntry);
        searchBar.ConnectEntry(searchEntry);

        // FIX: Force the bar container layout to expand and display
        searchEntry.SetKeyCaptureWidget(layoutBox);
        searchEntry.SetSearchDelay(0);

        layoutBox.Append(searchBar);

        // 3. Tab Stack Content Setup
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

        // Append the main content stack container underneath the search bar control panel
        layoutBox.Append(internalTabsStack);

        // ================= TAB 1: MANAGE MODS (CURRENT VIEW) =================
        var manageTabBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

        // 1. Structural outer scroller
        var scrollManage = Gtk.ScrolledWindow.New();
        scrollManage.SetVexpand(true);
        manageTabBox.Append(scrollManage);

        // 2. Sizing clamp container placed inside the scroller to restrict extreme widths
        var clampManage = Adw.Clamp.New();
        clampManage.SetMaximumSize(800);
        scrollManage.SetChild(clampManage); // Links clamp directly to viewport

        // 3. Your content box placed cleanly inside the clamp
        var contentStack = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
        contentStack.SetMarginTop(24);
        contentStack.SetMarginBottom(24);
        contentStack.SetMarginStart(24);
        contentStack.SetMarginEnd(24);
        clampManage.SetChild(contentStack); // Fixed: Target content box to clamp

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

        // 1. Structural outer scroller
        var scrollInstall = Gtk.ScrolledWindow.New();
        scrollInstall.SetVexpand(true);
        installTabBox.Append(scrollInstall);

        // 2. Sizing clamp container
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

        // 1. Create a top-level keyboard shortcut controller
        var shortcutController = Gtk.ShortcutController.New();

        // 2. Set up the trigger wrapper configured to look strictly for the Escape key
        uint escapeKeyval = Gdk.Functions.KeyvalFromName("Escape");
        var escapeTrigger = Gtk.KeyvalTrigger.New(escapeKeyval, 0);

        // 3. Define the action to take (using a callback to execute your transition)
        var backAction = Gtk.CallbackAction.New(
            (widget, args) =>
            {
                // Execute your structural navigation state change
                internalTabsStack.SetVisibleChildName("manage_tab");

                searchToggleButton.SetActive(false);

                if (profile is { })
                    updateConfig?.Invoke(profile.Lazy);

                return true; // Tells GTK the shortcut was handled completely
            }
        );

        // 4. Combine trigger and action into a unified shortcut definition
        var escapeShortcut = Gtk.Shortcut.New(escapeTrigger, backAction);

        // 5. Add to the controller and hook it directly onto the layout view container
        shortcutController.AddShortcut(escapeShortcut);
        stackInstall.AddController(shortcutController);

        // Declare a token source outside the handler to track and cancel stale typing actions
        CancellationTokenSource? searchCts = null;

        // This OnStopSearch event seems to always cause critical asserts just by existing.
        // I'm going to ignore it since there doesn't seem to be a good way to avoid it.
        searchEntry.OnStopSearch += (searchEntry, e) =>
        {
            searchToggleButton.SetActive(false);
        };

        searchEntry.OnSearchChanged += (searchEntry, e) =>
        {
            string currentText = searchEntry.GetText();
            // 1. MANAGE VIEW SHEET TOGGLE LOGIC
            // Only drop back to the manage tab if the search string is completely empty
            // AND the user has explicitly clicked away or hit escape to unfocus the search bar.
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
                // If there is text, OR if it's empty but still focused, keep the install/search view active
                if (internalTabsStack.GetVisibleChildName() != "install_tab")
                {
                    internalTabsStack.SetVisibleChildName("install_tab");
                }

                if (!searchToggleButton.GetActive())
                {
                    searchToggleButton.SetActive(true);
                }
            }

            // SAFETY CHECK: Ensure a profile has actually loaded first
            if (profile == null)
                return;

            string query = currentText.Trim().Replace(' ', '_');
            if (string.IsNullOrEmpty(query))
                return;

            // Cancel any pending search task immediately because the user is still actively typing
            searchCts?.Cancel();
            searchCts = new CancellationTokenSource();
            var token = searchCts.Token;

            // Launch the async pipeline off of the main user interface thread
            Task.Run(
                async () =>
                {
                    try
                    {
                        // 1. RUN THE SLOW SEARCH ON THE WORKER THREAD
                        // Executing this block in the background keeps your text typing inputs butter-smooth
                        var searchResults = (await profile.Search(query, token)).ToArray();

                        if (token.IsCancellationRequested)
                            return;

                        // 2. DISPATCH THE VISUAL UI POPULATION BACK TO THE MAIN GTK THREAD
                        // GTK4 requires all widget rendering updates to happen safely on the primary loop
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
                                    if (token.IsCancellationRequested)
                                        return false;

                                    var row = CreateBaseRow(package.Latest);

                                    Gtk.Button? btn = null;
                                    if (!profile.Added.ContainsKey(package))
                                        btn = CreateActionButton("list-add-symbolic", "Add");
                                    else
                                        btn = CreateActionButton(
                                            "list-remove-symbolic",
                                            "Remove",
                                            "destructive-action"
                                        );

                                    btn.OnClicked += (btnSender, btnArgs) =>
                                    {
                                        if (!profile.Added.ContainsKey(package))
                                        {
                                            profile.Add(
                                                package,
                                                DependencyVersionResolution.Latest
                                            );
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

                                    row.AddSuffix(btn);
                                    installListBox.Append(row);
                                }
                                return false;
                            }
                        );
                    }
                    catch (TaskCanceledException)
                    {
                        // Graceful exit path when a user types another character mid-flight
                    }
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

        // ================= POPULATE LOGIC LOOP =================
        updateConfigCallback = (lazyProfile) =>
        {
            profile = lazyProfile.LoadAsync().Result;

            windowTitle.SetTitle(lazyProfile.DisplayName);
            windowTitle.SetSubtitle(lazyProfile.Game.Name);

            internalTabsStack.SetVisibleChildName("manage_tab");
            // searchEntry.SetText("");
            // searchToggleButton.SetActive(false);

            ClearList(addedListBox);
            ClearList(installListBox);

            // --- Helper Action: Build Direct/Added Mod Row ---
            appendDirectRowAction = (mod, currentProfile) =>
            {
                var row = CreateBaseRow(mod);
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
                        var row = CreateBaseRow(dep);
                        var addButton = CreateActionButton(
                            "go-up-symbolic",
                            $"Add {dep.Package.FullName}"
                        );

                        addButton.OnClicked += (s, e) =>
                        {
                            activeProfile.Add(dep, DependencyVersionResolution.Latest);
                            appendDirectRowAction?.Invoke(dep, activeProfile);
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
                        var row = CreateBaseRow(dep);
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

                // Hacky fix for if focused element is removed and no other elements
                // are in the list, so focus is lost and typing to search doesn't just work.
                if (activeProfile.Added.Count > 0)
                {
                    addedListBox.GrabFocus();
                }
                else if (activeProfile.Dependencies.Count > 0)
                {
                    depsListBox.GrabFocus();
                }
                else if (activeProfile.RecentlyRemoved.Count > 0)
                {
                    recentListBox.GrabFocus();
                }
                else
                {
                    searchToggleButton.GrabFocus();
                }
            };

            rebuildDependenciesAction(profile);
        };

        updateConfig = updateConfigCallback;

        return layoutBox;
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

    private static Adw.ActionRow CreateBaseRow(PackageVersion packageVersion)
    {
        var package = packageVersion.Package;

        var row = Adw.ActionRow.New();
        row.SetTitle($"{package.FullName} v{packageVersion.Version}");
        row.SetSubtitle(package.Source.Id ?? "");
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
