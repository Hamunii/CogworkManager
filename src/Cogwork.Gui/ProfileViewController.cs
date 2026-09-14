using Adw;
using Gtk;

namespace Cogwork.Gui;

public class ProfilesViewController
{
    // --- Dependencies and State Layers ---
    private readonly NavigationView _navView;
    private readonly NavigationPage _configPage;
    private readonly Action<LazyModList> _onProfileSelected;
    private Game? _selectedGame;

    // --- Core UI Handle Outputs ---
    public NavigationPage Page { get; }
    private readonly WindowTitle _windowTitle;
    private readonly ListBox _profileListBox;

    public ProfilesViewController(
        NavigationView navView,
        NavigationPage configPage,
        Action<LazyModList> onProfileSelected
    )
    {
        _navView = navView ?? throw new ArgumentNullException(nameof(navView));
        _configPage = configPage ?? throw new ArgumentNullException(nameof(configPage));
        _onProfileSelected =
            onProfileSelected ?? throw new ArgumentNullException(nameof(onProfileSelected));

        // 1. Root Layout Wrapper
        var layoutBox = Box.New(Orientation.Vertical, 0);

        var header = Adw.HeaderBar.New();
        _windowTitle = WindowTitle.New("Loading Profiles...", "");
        header.SetTitleWidget(_windowTitle);
        layoutBox.Append(header);

        AddHeaderSettingsButton(header);

        var scroll = ScrolledWindow.New();
        scroll.SetVexpand(true);
        layoutBox.Append(scroll);

        var clamp = Clamp.New();
        clamp.SetMaximumSize(800);
        scroll.SetChild(clamp);

        var contentStack = Box.New(Orientation.Vertical, 24);
        contentStack.SetMarginTop(24);
        contentStack.SetMarginBottom(24);
        contentStack.SetMarginStart(24);
        contentStack.SetMarginEnd(24);
        clamp.SetChild(contentStack);

        // 2. Section Instantiation
        var sectionProfiles = new Section(contentStack, "Profiles");
        _profileListBox = sectionProfiles.Content;

        // 3. Package Root Assembly Wrapper
        Page = NavigationPage.New(layoutBox, "profiles");
    }

    /// <summary>
    /// Serves as the implementation callback point for 'out Action<Game> updateContentCallback'
    /// </summary>
    public void UpdatePage(Game selectedGame)
    {
        _selectedGame = selectedGame ?? throw new ArgumentNullException(nameof(selectedGame));

        _windowTitle.SetTitle(_selectedGame.Name);
        _windowTitle.SetSubtitle("Select mod profile");

        // Clean out stale rows completely
        ClearListBox(_profileListBox);

        // Populate existing user profiles
        foreach (var profile in _selectedGame.EnumerateProfiles())
        {
            var addedCount = profile.ResolvedAdded?.Count ?? 0;
            var depCount = profile.ResolvedDependencies?.Count ?? 0;

            var row = ActionRow.New();
            row.SetTitle(profile.DisplayName);
            row.SetSubtitle(
                $"{addedCount} added, {depCount} {(depCount == 1 ? "dependency" : "dependencies")}"
            );
            row.SetActivatable(true);

            row.OnActivated += (s, e) =>
            {
                _onProfileSelected(profile);
                _navView.Push(_configPage);
            };

            // Left Launch Button Action
            var launchButton = Button.NewFromIconName("media-playback-start-symbolic");
            launchButton.SetValign(Align.Center);
            launchButton.SetTooltipText($"Launch with {profile.DisplayName}");
            launchButton.OnClicked += (s, e) =>
            {
                _ = Cli.Program.Main([
                    "launch",
                    "--game",
                    profile.Game.Slug,
                    "--profile",
                    profile.Id,
                ]);
            };
            row.AddSuffix(launchButton);

            // --- FIXED: Added the Multi-Option Context Action Dropdown Menu ---

            // Localized row-safe prefix action tagging namespace
            var actionGroup = Gio.SimpleActionGroup.New();
            row.InsertActionGroup("row", actionGroup);

            // Row-bound Rename Action Definition
            var renameAction = Gio.SimpleAction.New("rename", null);
            renameAction.OnActivate += (s, e) =>
            {
                OnRenameProfileTriggered(profile);
            };
            actionGroup.AddAction(renameAction);

            // Row-bound Delete Action Definition
            var deleteAction = Gio.SimpleAction.New("delete", null);
            deleteAction.OnActivate += (s, e) =>
            {
                OnDeleteProfileTriggered(profile);
            };
            actionGroup.AddAction(deleteAction);

            // Map action handlers to UI elements structurally via Gio.Menu template
            var menuModel = Gio.Menu.New();
            menuModel.Append("Rename Profile", "row.rename");
            menuModel.Append("Delete Profile", "row.delete");

            // Build structural Menu Trigger Button and overlay as flat item
            var menuButton = MenuButton.New();
            menuButton.SetIconName("view-more-symbolic");
            menuButton.SetValign(Align.Center);
            menuButton.SetMenuModel(menuModel);
            menuButton.AddCssClass("flat");

            row.AddSuffix(menuButton);
            _profileListBox.Append(row);
        }

        // 4. Build fresh, isolated "Add Profile Row" at the bottom of the list
        AppendAddProfileRow();
    }

    void AddHeaderSettingsButton(Adw.HeaderBar header)
    {
        var settingsButton = Button.NewFromIconName("emblem-system-symbolic");
        settingsButton.SetValign(Align.Center);
        settingsButton.AddCssClass("flat");
        settingsButton.SetTooltipText("Game Settings");

        settingsButton.OnClicked += (s, e) =>
        {
            OpenGameSettings(_selectedGame!);
        };

        header.PackEnd(settingsButton);
    }

    void OpenGameSettings(Game game)
    {
        if (Page.GetRoot() is not Gtk.Window rootWindow)
            return;

        var prefWindow = PreferencesWindow.New();
        prefWindow.SetTransientFor(rootWindow);
        prefWindow.SetDefaultSize(800, 500);
        prefWindow.SetModal(true);
        prefWindow.SetTitle("Game Settings");

        var gamePreferences = CreateSettingsPage(game, prefWindow);
        prefWindow.Add(gamePreferences);

        prefWindow.Present();
    }

    public static PreferencesPage CreateSettingsPage(
        Game selectedGame,
        PreferencesWindow preferencesWindow
    )
    {
        ArgumentNullException.ThrowIfNull(selectedGame);
        ArgumentNullException.ThrowIfNull(preferencesWindow);

        var page = PreferencesPage.New();
        page.SetTitle($"Game");
        page.SetIconName("input-gaming-symbolic");

        var configGroup = PreferencesGroup.New();
        configGroup.SetTitle("Game Path");
        page.Add(configGroup);

        var gamePath = EntryRow.New();
        gamePath.SetTitle("Path to game root directory");
        gamePath.SetText(selectedGame.Config.PreferredPath ?? "");
        configGroup.Add(gamePath);

        var browseButton = Button.NewFromIconName("folder-open-symbolic");
        browseButton.SetValign(Align.Center);
        browseButton.AddCssClass("flat");
        browseButton.SetTooltipText("Browse for directory...");
        gamePath.AddSuffix(browseButton);

        ConfigureProfileViewController.ButtonOnClickedSelectGameRootDirectory(
            browseButton,
            gamePath,
            preferencesWindow
        );

        preferencesWindow.OnCloseRequest += (s, e) =>
        {
            string finalGamePath = gamePath.GetText().Trim();
            if (selectedGame.Config.PreferredPath != finalGamePath)
            {
                selectedGame.Config.PreferredPath = finalGamePath;
                selectedGame.Config.Save(selectedGame.GameConfigLocation);
            }
            return false;
        };

        return page;
    }

    private void AppendAddProfileRow()
    {
        var addProfileRow = ActionRow.New();
        addProfileRow.SetTitle("Create New Profile...");
        addProfileRow.SetActivatable(true);

        var plusIcon = Image.NewFromIconName("list-add-symbolic");
        addProfileRow.AddPrefix(plusIcon);
        _profileListBox.Append(addProfileRow);

        addProfileRow.OnActivated += OnAddProfileRowActivated;
    }

    private void OnAddProfileRowActivated(object? sender, EventArgs e)
    {
        if (Page.GetRoot() is not Gtk.Window rootWindow || _selectedGame == null)
            return;

        var dialog = Adw.AlertDialog.New(
            "Create Profile",
            "Enter a name for your new mod profile."
        );

        var entryRow = EntryRow.New();
        entryRow.SetTitle("Profile Name");
        entryRow.SetMaxLength(50);
        entryRow.SetActivatesDefault(true);

        var dialogListBox = ListBox.New();
        dialogListBox.SetSelectionMode(SelectionMode.None);
        dialogListBox.AddCssClass("boxed-list");
        dialogListBox.Append(entryRow);
        dialog.SetExtraChild(dialogListBox);

        dialog.AddResponse("cancel", "Cancel");
        dialog.AddResponse("create", "Create");
        dialog.SetDefaultResponse("create");
        dialog.SetCloseResponse("cancel");
        dialog.SetResponseAppearance("create", ResponseAppearance.Suggested);

        dialog.OnResponse += (dialogSender, responseArgs) =>
        {
            if (responseArgs.Response == "create")
            {
                string profileName = entryRow.GetText().Trim();
                if (string.IsNullOrWhiteSpace(profileName))
                {
                    var profileCount = _selectedGame.EnumerateProfiles().Count();
                    profileName = $"New Profile {profileCount + 1}";
                }

                Console.WriteLine($"Creating profile: {profileName} for {_selectedGame.Name}");
                ModList.CreateNew(_selectedGame, profileName);
                UpdatePage(_selectedGame);
            }
        };

        dialog.Present(rootWindow);
        entryRow.GrabFocus();
    }

    private void OnRenameProfileTriggered(LazyModList profile)
    {
        if (Page.GetRoot() is not Gtk.Window rootWindow || _selectedGame == null)
            return;

        var dialog = Adw.AlertDialog.New(
            "Rename Profile",
            $"Enter a new name for '{profile.DisplayName}'."
        );
        dialog.SetPreferWideLayout(true);

        var entryRow = EntryRow.New();
        entryRow.SetTitle("New Name");
        entryRow.SetText(profile.DisplayName);
        entryRow.SetMaxLength(50);
        entryRow.SetActivatesDefault(true);

        var dialogListBox = ListBox.New();
        dialogListBox.SetSelectionMode(SelectionMode.None);
        dialogListBox.AddCssClass("boxed-list");
        dialogListBox.Append(entryRow);
        dialog.SetExtraChild(dialogListBox);

        dialog.AddResponse("cancel", "Cancel");
        dialog.AddResponse("rename", "Rename");
        dialog.SetDefaultResponse("rename");
        dialog.SetCloseResponse("cancel");
        dialog.SetResponseAppearance("rename", ResponseAppearance.Suggested);

        dialog.OnResponse += (ds, ra) =>
        {
            if (ra.Response == "rename")
            {
                string newName = entryRow.GetText().Trim();
                if (!string.IsNullOrWhiteSpace(newName) && newName != profile.DisplayName)
                {
                    profile.Rename(newName);
                    UpdatePage(_selectedGame);
                }
            }
        };

        dialog.Present(rootWindow);
        entryRow.GrabFocus();
    }

    private void OnDeleteProfileTriggered(LazyModList profile)
    {
        if (Page.GetRoot() is not Gtk.Window rootWindow || _selectedGame == null)
            return;

        var dialog = Adw.AlertDialog.New(
            "Delete Profile?",
            $"Are you sure you want to permanently delete '{profile.DisplayName}'? This action cannot be undone."
        );
        dialog.SetPreferWideLayout(true);

        dialog.AddResponse("cancel", "Cancel");
        dialog.AddResponse("delete", "Delete");
        dialog.SetDefaultResponse("cancel");
        dialog.SetCloseResponse("cancel");
        dialog.SetResponseAppearance("delete", ResponseAppearance.Destructive);

        dialog.OnResponse += (ds, ra) =>
        {
            if (ra.Response == "delete")
            {
                profile.Delete();
                UpdatePage(_selectedGame);
            }
        };

        dialog.Present(rootWindow);
    }

    private static void ClearListBox(ListBox listBox)
    {
        while (listBox.GetFirstChild() != null)
        {
            listBox.Remove(listBox.GetFirstChild()!);
        }
    }
}
