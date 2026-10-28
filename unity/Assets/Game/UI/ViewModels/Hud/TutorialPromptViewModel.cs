#nullable enable
using System;

namespace Game.UI.ViewModels.Hud
{
    /// <summary>
    /// The tutorial prompt panel (spec 12.3, 9.12 <c>tutorial_prompt</c>; WP-M2-10): one prompt at a time, either the
    /// scheduled prompt the <c>ClientTutorial</c> shows now (a <c>UiStrings</c> id) or, while the player pushes into a level
    /// gate, the gate's refusal (the <c>RegionLevelTooLow</c> reason with the level it needs). The gate takes the panel over
    /// and the scheduled prompt returns after it.
    /// </summary>
    public sealed class TutorialPromptViewModel : ViewModel
    {
        /// <summary>The scheduled prompt's <c>UiStrings</c> id, empty for none.</summary>
        public string PromptId { get; private set; } = "";

        /// <summary>The level the gate in front of the player needs, 0 while no gate refusal shows.</summary>
        public int GateLevel { get; private set; }

        /// <summary>The dismiss key as the player reads it.</summary>
        public string DismissKey { get; private set; } = "";

        public bool Visible => GateLevel > 0 || PromptId.Length > 0;

        /// <summary>True while the gate's refusal shows (it cannot be dismissed; it ends when the push ends).</summary>
        public bool ShowsGate => GateLevel > 0;

        public void Set(string promptId, int gateLevel, string dismissKey)
        {
            promptId ??= "";
            dismissKey ??= "";
            if (promptId == PromptId && gateLevel == GateLevel && dismissKey == DismissKey)
                return;
            PromptId = promptId;
            GateLevel = Math.Max(0, gateLevel);
            DismissKey = dismissKey;
            NotifyChanged();
        }
    }
}
