#nullable enable
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Overlays;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Outpost;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// Play findings of 2026-10-28 on a real panel: the resource panel's stats readout opens at its own <c>stats_readout</c>
    /// row, clear of the gold under the panel (it was drawn over "590 gold"); the world prompts are absolute boxes whose text
    /// has no outline (the HUD's legacy font drew the outline as offset copies: "Press T to buy" looked doubled).
    /// </summary>
    public sealed class HudTopLeftAndPromptTests
    {
        private static HudConfig Committed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return HudConfig.From(set);
        }

        [UnityTest]
        public System.Collections.IEnumerator The_stats_readout_opens_under_the_gold_without_covering_it()
        {
            var hud = Committed();
            var panel = new HudTestPanel("hud-top-left-test");
            ResourcePanelPresenter? resources = null;
            GoldPresenter? gold = null;
            try
            {
                yield return null;
                panel.Ready(hud);
                var view = new HudView(hud);
                panel.Root.Add(view.Root);
                var models = new HudViewModels(4, 4, 3, 20);
                var goldModel = new GoldViewModel();
                resources = new ResourcePanelPresenter(hud, view, models.Resources, models.Progress);
                gold = new GoldPresenter(hud, view, goldModel);
                resources.Bind();
                gold.Bind();
                models.Resources.SetStats(new StatsReadout(8, 48, 9, 21, 4, 9, 11, 28));
                goldModel.Set(590);
                panel.Layout();

                panel.PointTo(view.Root.Q(HudElementIds.ResourcePanel));
                var readout = resources.Tooltip;
                Assert.That(readout.style.display.value, Is.EqualTo(DisplayStyle.Flex), "hovering the panel shows the readout");
                var goldBox = view.Root.Q(HudElementIds.Gold).worldBound;
                var panelBox = view.Root.Q(HudElementIds.ResourcePanel).worldBound;
                Assert.That(readout.worldBound.Overlaps(goldBox), Is.False, $"readout {readout.worldBound} covers the gold {goldBox}");
                Assert.That(readout.worldBound.Overlaps(panelBox), Is.False, $"readout {readout.worldBound} covers the panel {panelBox}");
                Assert.That(readout.worldBound.y, Is.GreaterThanOrEqualTo(goldBox.yMax), "the readout is under the gold");
                Assert.That(gold.Text, Is.EqualTo(hud.Text.Get("hud.gold", new TextArg("gold", "590"))));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                resources?.Dispose();
                gold?.Dispose();
                panel.Dispose();
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator The_npc_prompt_is_an_absolute_box_with_plain_text()
        {
            var hud = Committed();
            var panel = new HudTestPanel("hud-prompt-test");
            NpcPromptOverlay? prompt = null;
            try
            {
                yield return null;
                panel.Ready(hud);
                var layer = new VisualElement { name = "hud_overlays", pickingMode = PickingMode.Ignore };
                layer.AddToClassList("hud-layer");
                panel.Root.Add(layer);
                layer.Add(new VisualElement { style = { height = 40 } }); // another overlay element before it in the layer
                var model = new NpcPromptViewModel();
                prompt = new NpcPromptOverlay(hud, layer, model);
                model.Set(7, "hud.npc.prompt.vendor", "T");
                var box = layer.Q(HudElementIds.NpcPrompt);
                box.style.display = DisplayStyle.Flex;
                panel.Layout();

                Assert.That(box.resolvedStyle.position, Is.EqualTo(Position.Absolute), "placed by left and top like the pickup prompt");
                var label = box.Q<Label>();
                Assert.That(label.text, Is.EqualTo("Press T to buy"));
                Assert.That(label.resolvedStyle.unityTextOutlineWidth, Is.EqualTo(0f), "no outline: the box gives the contrast");
            }
            finally
            {
                prompt?.Dispose();
                panel.Dispose();
            }
        }
    }
}
