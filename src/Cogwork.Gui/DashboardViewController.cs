using Adw;
using Gtk;

namespace Cogwork.Gui;

public class DashboardViewController
{
    private readonly NavigationView _navView;
    private readonly NavigationPage _profilePage;
    private readonly Action<Game> _onGameSelected;

    public NavigationPage Page { get; }

    public DashboardViewController(
        NavigationView navView,
        List<Game> games,
        NavigationPage profilePage,
        Action<Game> onGameSelected
    )
    {
        _navView = navView ?? throw new ArgumentNullException(nameof(navView));
        _profilePage = profilePage ?? throw new ArgumentNullException(nameof(profilePage));
        _onGameSelected = onGameSelected ?? throw new ArgumentNullException(nameof(onGameSelected));

        var layoutBox = Box.New(Orientation.Vertical, 0);

        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(WindowTitle.New("Cogwork", "Select game to mod"));
        layoutBox.Append(header);

        var scroll = ScrolledWindow.New();
        scroll.SetVexpand(true);
        layoutBox.Append(scroll);

        var grid = FlowBox.New();
        grid.SetHomogeneous(false);
        grid.UnselectAll();
        grid.SetHalign(Align.Center);
        grid.SetValign(Align.Start);
        grid.SetMinChildrenPerLine(2);
        grid.SetMaxChildrenPerLine(5);
        grid.SetSelectionMode(SelectionMode.None);
        grid.SetColumnSpacing(16);
        grid.SetRowSpacing(16);
        grid.SetMarginTop(16);
        grid.SetMarginBottom(16);
        grid.SetMarginStart(16);
        grid.SetMarginEnd(16);
        scroll.SetChild(grid);

        foreach (var game in games)
        {
            var cardChild = GameCardChild.New(game);
            grid.Insert(cardChild, -1);
        }

        grid.OnChildActivated += OnCardActivated;

        Page = NavigationPage.New(layoutBox, "dashboard");
    }

    private void OnCardActivated(object? sender, FlowBox.ChildActivatedSignalArgs args)
    {
        var clickedCard = (GameCardChild)args.Child;
        _onGameSelected(clickedCard.AssociatedGame);
        _navView.Push(_profilePage);
    }
}

[GObject.Subclass<FlowBoxChild>]
public partial class GameCardChild
{
    public Game AssociatedGame { get; private set; } = null!;

    public static GameCardChild New(Game game)
    {
        var card = NewWithProperties([]);
        card.AssociatedGame = game ?? throw new ArgumentNullException(nameof(game));
        card.SetupLayout();
        return card;
    }

    partial void Initialize()
    {
        AddCssClass("card");
        SetSizeRequest(140, 180);
        SetHalign(Align.Center);
        SetValign(Align.Center);
    }

    private void SetupLayout()
    {
        var cardContent = Box.New(Orientation.Vertical, 8);
        cardContent.SetMarginTop(12);
        cardContent.SetMarginBottom(12);

        var coverArtPlaceholder = Box.New(Orientation.Vertical, 0);
        coverArtPlaceholder.SetSizeRequest(100, 100);
        coverArtPlaceholder.SetHalign(Align.Center);
        coverArtPlaceholder.AddCssClass("thumbnail");
        coverArtPlaceholder.SetTooltipText(AssociatedGame.Name);
        cardContent.Append(coverArtPlaceholder);

        var titleLabel = Label.New(AssociatedGame.Name);
        titleLabel.AddCssClass("bold");
        titleLabel.SetWrap(true);
        titleLabel.SetWrapMode(Pango.WrapMode.WordChar);
        titleLabel.SetJustify(Justification.Center);
        titleLabel.SetMaxWidthChars(14);
        titleLabel.SetWidthChars(14);
        cardContent.Append(titleLabel);

        SetChild(cardContent);
    }
}
