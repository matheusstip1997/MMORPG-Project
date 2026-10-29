#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace Game.Dev.Qa.EditorTests
{
    public sealed class QaScreenshotsTests
    {
        [Test]
        public void Screenshots_are_written_opaque_with_the_blended_color_kept()
        {
            // QA wave 6 F4: a telegraph pixel kept the scene's A = 47 and showed almost white in image viewers.
            var image = new Texture2D(2, 1, TextureFormat.RGBA32, false);
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                image.SetPixels32(new[] { new Color32(0x77, 0x7c, 0x77, 47), new Color32(10, 20, 30, 255) });
                image.Apply();

                Assert.That(decoded.LoadImage(QaScreenshots.OpaquePng(image)), Is.True);

                var pixels = decoded.GetPixels32();
                Assert.That(pixels[0], Is.EqualTo(new Color32(0x77, 0x7c, 0x77, 255)));
                Assert.That(pixels[1], Is.EqualTo(new Color32(10, 20, 30, 255)));
            }
            finally
            {
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(decoded);
            }
        }
    }
}
