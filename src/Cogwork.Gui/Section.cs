
namespace Cogwork.Gui;

record class Section
{
    public Gtk.ListBox Content { get; }
    readonly Gtk.Label heading;
    readonly Gtk.Label? headingEmpty;

    public Section(Gtk.Box parent, string heading)
    {
        Content = CreateSection(parent, heading, out var labelHeading);
        this.heading = labelHeading;
    }

    public Section(Gtk.Box parent, string heading, string headingEmpty)
    {
        Content = CreateSection(
            parent,
            heading,
            headingEmpty,
            out var labelHeading,
            out var labelEmpty
        );
        this.heading = labelHeading;
        this.headingEmpty = labelEmpty;
    }

    public void ToggleVisibility(bool visible)
    {
        if (headingEmpty is { })
            ToggleSectionVisibility(heading, headingEmpty, Content, visible);
        else
            ToggleSectionVisibility(heading, Content, visible);
    }

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
