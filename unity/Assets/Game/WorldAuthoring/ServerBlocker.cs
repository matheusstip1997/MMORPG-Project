using UnityEngine;

namespace Game.WorldAuthoring
{
    /// <summary>
    /// Marks a wall, building or big rock that blocks movement on the server (spec 11.6). UnityExporter exports the box
    /// (local <see cref="center"/> and <see cref="size"/>, like a BoxCollider) as a simplified blocker and marks every
    /// walkability cell it touches, grown by the agent radius, as not walkable.
    /// M1: the transform may only turn around the vertical axis in steps of 90 degrees (the validator refuses others).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ServerBlocker : MonoBehaviour
    {
        public Vector3 center = Vector3.zero;
        public Vector3 size = Vector3.one;

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.9f, 0.2f, 0.2f, 0.35f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(center, size);
        }
    }
}
