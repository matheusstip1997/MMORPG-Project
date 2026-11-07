#nullable enable
using System;
using System.Collections.Generic;

namespace Game.Dev.Qa
{
    /// <summary>
    /// Which step a run goes on with after a failed step (QA wave 2 F2 and F3: one class's crafting or boss failure left
    /// <c>wait-respawn</c>, <c>dev-tools</c>, <c>gather-ore</c> and <c>tutorial-gate</c> unplayed). Up to the crafting steps each step
    /// builds on the one before, so a failure ends the run. The crafting steps build on each other and on nothing after them, and
    /// the steps after them stand alone. The run still fails. Pure: testable in EditMode.
    /// </summary>
    public static class QaStepFlow
    {
        /// <summary>The steps that build on each other from the forge to the socket: a failure skips the rest of them.</summary>
        private static readonly string[] CraftingChain =
        {
            "forge-sword", "equip-crafted", "enchant-sword", "cut-gem", "socket-gem",
        };

        /// <summary>The steps that need nothing from the crafting steps or from each other.</summary>
        private static readonly string[] StandAlone =
        {
            "arrow-wall", "wait-respawn", "dev-tools", "parry-boss", "gather-ore", "tutorial-gate",
        };

        /// <summary>The index in <paramref name="steps"/> to go on with after step <paramref name="failed"/> failed; -1: the run ends.</summary>
        public static int NextAfterFailure(IReadOnlyList<string> steps, int failed)
        {
            string name = steps[failed];
            if (Array.IndexOf(CraftingChain, name) >= 0)
                return IndexOf(steps, StandAlone[0]);
            int standAlone = Array.IndexOf(StandAlone, name);
            return standAlone >= 0 && standAlone + 1 < StandAlone.Length ? IndexOf(steps, StandAlone[standAlone + 1]) : -1;
        }

        private static int IndexOf(IReadOnlyList<string> steps, string name)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] == name)
                    return i;
            }
            return -1;
        }
    }
}
