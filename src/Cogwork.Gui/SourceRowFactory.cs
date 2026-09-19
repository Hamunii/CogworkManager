using System;
using Adw;
using Gdk;
using Gtk;

namespace Cogwork.Gui;

public static class SourceRowFactory
{
    public static ExpanderRow Create(
        UserSource source,
        Action<ExpanderRow, SourceDominanceStrategy, SourceDominanceEntry, bool> onValuesChanged,
        Action<ExpanderRow, int> onReorderRequested
    )
    {
        var expanderRow = ExpanderRow.New();
        expanderRow.SetTitle(source.Source.Id);
        expanderRow.SetShowEnableSwitch(true);

        var dragHandle = Image.NewFromIconName("list-drag-handle-symbolic");
        dragHandle.AddCssClass("dim-label");
        expanderRow.AddPrefix(dragHandle);

        var entryCombo = ComboRow.New();
        entryCombo.SetTitle("Look For Dependencies From This Source");
        entryCombo.SetModel(StringList.New(["Always", "When Referenced"]));
        entryCombo.SetSelected((uint)source.DominanceEntry);
        expanderRow.AddRow(entryCombo);

        var strategyCombo = ComboRow.New();
        strategyCombo.SetTitle("Prefer This Source When It Has");
        strategyCombo.SetModel(StringList.New(["Dependency", "Latest Dependency"]));
        strategyCombo.SetSelected((uint)source.DominanceStrategy);
        expanderRow.AddRow(strategyCombo);

        void FireNotification()
        {
            var strategy = (SourceDominanceStrategy)strategyCombo.GetSelected();
            var entry = (SourceDominanceEntry)entryCombo.GetSelected();
            bool isEnabled = expanderRow.GetEnableExpansion();

            onValuesChanged(expanderRow, strategy, entry, isEnabled);
        }

        bool isVisible = source.Visible;
        expanderRow.SetEnableExpansion(isVisible);

        expanderRow.OnNotify += (s, e) =>
        {
            if (e.Pspec.GetName() == "enable-expansion")
            {
                bool active = expanderRow.GetEnableExpansion();
                expanderRow.SetExpanded(active);
                FireNotification();
            }
        };

        strategyCombo.OnNotify += (s, e) =>
        {
            if (e.Pspec.GetName() == "selected")
                FireNotification();
        };

        entryCombo.OnNotify += (s, e) =>
        {
            if (e.Pspec.GetName() == "selected")
            {
                uint selectedIdx = entryCombo.GetSelected();
                expanderRow.SetEnableExpansion(selectedIdx != 2u);
                FireNotification();
            }
        };

        SetupDragAndDrop(expanderRow, onReorderRequested);
        return expanderRow;
    }

    private static void SetupDragAndDrop(
        ExpanderRow rowContext,
        Action<ExpanderRow, int> onReorderRequested
    )
    {
        var stringValue = new GObject.Value(GObject.Type.String);
        stringValue.SetString(rowContext.GetTitle());
        var contentProvider = ContentProvider.NewForValue(stringValue);

        var dragSource = DragSource.New();
        dragSource.SetContent(contentProvider);
        dragSource.SetActions(DragAction.Move);

        dragSource.PropagationPhase = PropagationPhase.Bubble;
        rowContext.AddController(dragSource);

        dragSource.OnDragBegin += (s, e) =>
        {
            dragSource.SetIcon(WidgetPaintable.New(rowContext), 0, 0);
        };

        var dropTarget = DropTarget.New(GObject.Type.String, DragAction.Move);
        rowContext.AddController(dropTarget);

        dropTarget.OnDrop += (s, e) =>
        {
            string draggedId = e.Value.GetString()!;

            if (!string.IsNullOrEmpty(draggedId) && rowContext.Parent is ListBox parentListBox)
            {
                Widget? currentChild = parentListBox.GetFirstChild();
                while (currentChild is not null)
                {
                    if (currentChild is ExpanderRow checkRow && checkRow.GetTitle() == draggedId)
                    {
                        int targetIndex = rowContext.GetIndex();
                        onReorderRequested(checkRow, targetIndex);
                        return true;
                    }
                    currentChild = currentChild.GetNextSibling();
                }
            }
            return false;
        };
    }
}
