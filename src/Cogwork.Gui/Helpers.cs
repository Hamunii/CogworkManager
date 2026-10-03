using Adw;
using Gtk;

namespace Cogwork.Gui;

internal static class Helpers
{
    internal static void AddGamePathValidationSuffixIcon(EntryRow gamePath)
    {
        var statusIcon = Image.New();
        gamePath.AddSuffix(statusIcon);

        UpdateValidationState(gamePath.GetText());

        gamePath.OnChanged += (editable, e) =>
        {
            UpdateValidationState(editable.GetText());
        };

        void UpdateValidationState(string textGamePath)
        {
            if (string.IsNullOrWhiteSpace(textGamePath))
            {
                statusIcon.SetVisible(false);
                return;
            }

            statusIcon.SetVisible(true);

            if (Game.IsGamePathValid(textGamePath, out var err))
            {
                statusIcon.SetFromIconName("checkmark-symbolic");
                statusIcon.SetTooltipText("Path is valid");
                statusIcon.SetCssClasses(["success"]);
            }
            else
            {
                statusIcon.SetFromIconName("dialog-warning-symbolic");
                statusIcon.SetTooltipText(err);
                statusIcon.SetCssClasses(["warning"]);
            }
        }
    }
}
