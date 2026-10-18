#nullable enable
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// Sits on the root of a visual prefab (made by the visual prefab builder, or by hand for our own art): which
    /// Animator to drive and with which clips. A prefab without it is shown static.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EntityVisualRig : MonoBehaviour
    {
        [SerializeField] private Animator? animator;
        [SerializeField] private AnimationSet? animationSet;

        public Animator? Animator => animator != null ? animator : animator = GetComponentInChildren<Animator>();

        public AnimationSet? AnimationSet => animationSet;

        public void Configure(Animator? body, AnimationSet? set)
        {
            animator = body;
            animationSet = set;
        }
    }
}
