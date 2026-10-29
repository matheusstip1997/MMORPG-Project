#nullable enable
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.Dev.Qa.Tests
{
    /// <summary>
    /// QA wave 6 F1: in a batch run (no window focus) UI Toolkit drops the QA mouse; <see cref="QaUiPointer"/> reaches a
    /// runtime panel through UI Toolkit's own dispatch: hover (enter and leave), press with pointer capture, a drag that
    /// leaves the element while captured, and the release.
    /// </summary>
    public sealed class QaUiPointerTests
    {
        [UnityTest]
        public IEnumerator Pointer_events_reach_a_runtime_panel_with_hover_capture_and_drag()
        {
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            var target = new RenderTexture(1280, 720, 24); // the smoke run redirects every panel into a texture
            settings.targetTexture = target;
            var host = new GameObject("qa-pointer-test");
            try
            {
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = settings;
                yield return null;
                var window = Box(100, 100, 200, 200);
                var slot = Box(10, 10, 50, 50);
                window.Add(slot);
                document.rootVisualElement.Add(window);
                int enters = 0, leaves = 0, slotEnters = 0, downs = 0, ups = 0, dragMoves = 0;
                window.RegisterCallback<PointerEnterEvent>(_ => enters++);
                window.RegisterCallback<PointerLeaveEvent>(_ => leaves++);
                slot.RegisterCallback<PointerEnterEvent>(_ => slotEnters++);
                slot.RegisterCallback<PointerDownEvent>(e =>
                {
                    downs++;
                    window.CapturePointer(e.pointerId); // as the inventory window does when a slot is pressed
                });
                window.RegisterCallback<PointerMoveEvent>(_ =>
                {
                    if (window.HasPointerCapture(PointerId.mousePointerId))
                        dragMoves++;
                });
                window.RegisterCallback<PointerUpEvent>(e =>
                {
                    ups++;
                    window.ReleasePointer(e.pointerId);
                });
                for (int i = 0; i < 3; i++)
                    yield return null;

                var pointer = new QaUiPointer();
                var panel = window.panel;
                pointer.Move(panel, new Vector2(5f, 5f));
                pointer.Move(panel, QaUiPointer.Center(slot));
                yield return null;
                Assert.That((enters, slotEnters), Is.EqualTo((1, 1)), "hover enters the window and the slot");

                pointer.Down(panel);
                yield return null;
                Assert.That(downs, Is.EqualTo(1));
                Assert.That(window.HasPointerCapture(PointerId.mousePointerId), Is.True, "the press captured the pointer");

                pointer.Move(panel, new Vector2(600f, 600f)); // dragged out of the window: still sent to it while captured
                yield return null;
                Assert.That(dragMoves, Is.GreaterThan(0));
                pointer.Up(panel);
                yield return null;
                Assert.That(ups, Is.EqualTo(1));
                Assert.That(window.HasPointerCapture(PointerId.mousePointerId), Is.False);
                pointer.Move(panel, new Vector2(700f, 600f));
                yield return null;
                Assert.That(leaves, Is.EqualTo(1), "the pointer left the window");
            }
            finally
            {
                Object.Destroy(host);
                Object.Destroy(settings);
                target.Release();
                Object.Destroy(target);
            }
        }

        private static VisualElement Box(float left, float top, float width, float height)
        {
            var box = new VisualElement();
            box.style.position = Position.Absolute;
            box.style.left = left;
            box.style.top = top;
            box.style.width = width;
            box.style.height = height;
            return box;
        }
    }
}
