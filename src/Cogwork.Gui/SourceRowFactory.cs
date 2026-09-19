using System;
using Adw;
using Gdk;
using Gtk;

namespace Cogwork.Gui;

public static class SourceRowFactory
{
    public static ExpanderRow Create(
        UserSource source,
        Action<ExpanderRow, SourceDominanceStrategy, SourceDominanceEntry> onValuesChanged,
        Action<ExpanderRow, int> onReorderRequested
    )
    {
        var expanderRow = ExpanderRow.New();
        expanderRow.SetTitle(source.Source.Id);
        expanderRow.SetSubtitle("Configure dependency resolution rules");
        expanderRow.SetShowEnableSwitch(true);

        var dragHandle = Image.NewFromIconName("list-drag-handle-symbolic");
        dragHandle.AddCssClass("dim-label");
        expanderRow.AddPrefix(dragHandle);

        var entryCombo = ComboRow.New();
        entryCombo.SetTitle("Evaluate For Dependencies");
        entryCombo.SetModel(StringList.New(["Always", "When Referenced", "Hidden"]));
        entryCombo.SetSelected((uint)source.DominanceEntry);
        expanderRow.AddRow(entryCombo);

        var strategyCombo = ComboRow.New();
        strategyCombo.SetTitle("Prefer When");
        strategyCombo.SetModel(StringList.New(["Has Package", "Has Latest Package"]));
        strategyCombo.SetSelected((uint)source.DominanceStrategy);
        expanderRow.AddRow(strategyCombo);

        // =========================================================
        // EVENT NOTIFICATION AND EXPANSION SYNC HANDLERS
        // =========================================================
        void FireNotification()
        {
            var strategy = (SourceDominanceStrategy)strategyCombo.GetSelected();
            var entry = (SourceDominanceEntry)entryCombo.GetSelected();
            onValuesChanged(expanderRow, strategy, entry);
        }

        bool isCurrentlyVisible = source.IsVisible();
        expanderRow.SetEnableExpansion(isCurrentlyVisible);
        expanderRow.SetExpanded(isCurrentlyVisible);

        expanderRow.OnNotify += (s, e) =>
        {
            if (e.Pspec.GetName() == "enable-expansion")
            {
                bool active = expanderRow.GetEnableExpansion();
                expanderRow.SetExpanded(active);
                entryCombo.SetSelected(active ? 0u : 2u);
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

        SetupDragAndDrop(expanderRow, dragHandle, onReorderRequested);
        return expanderRow;
    }

    private static void SetupDragAndDrop(
        ExpanderRow rowContext,
        Widget dragHandle,
        Action<ExpanderRow, int> onReorderRequested
    )
    {
        // 1. Pack the Row's Title ID text directly inside the GValue container block
        var stringValue = new GObject.Value(GObject.Type.String);
        stringValue.SetString(rowContext.GetTitle());
        var contentProvider = ContentProvider.NewForValue(stringValue);

        var dragSource = DragSource.New();
        dragSource.SetContent(contentProvider);
        dragSource.SetActions(DragAction.Move);

        // Enables whole row dragging using the clean bubbling sequence we set up earlier
        dragSource.PropagationPhase = PropagationPhase.Bubble;
        rowContext.AddController(dragSource);

        dragSource.OnDragBegin += (s, e) =>
        {
            dragSource.SetIcon(WidgetPaintable.New(rowContext), 0, 0);
        };

        // 2. Instruct the DropTarget specifically to watch for Type.String values
        var dropTarget = DropTarget.New(GObject.Type.String, DragAction.Move);
        rowContext.AddController(dropTarget);

        dropTarget.OnDrop += (s, e) =>
        {
            // 3. FIX: Fetch the String ID payload safely across the clipboard stream
            string draggedId = e.Value.GetString()!;

            if (!string.IsNullOrEmpty(draggedId) && rowContext.Parent is ListBox parentListBox)
            {
                // Traverse the physical widget tree to locate the moving row instance reference
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
