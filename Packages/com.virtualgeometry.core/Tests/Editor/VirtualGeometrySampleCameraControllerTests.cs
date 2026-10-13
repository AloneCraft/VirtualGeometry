using NUnit.Framework;
using UnityEngine;

namespace VirtualizedGeometry.Tests
{
    public sealed class VirtualGeometrySampleCameraControllerTests
    {
        [Test]
        public void FramingDistance()
        {
            {
                // 半径 10 の範囲(extents (6,8,0) → magnitude 10)、垂直画角 60° → 距離 10 / sin 30° = 20
                var bounds = new Bounds(Vector3.zero, new Vector3(12f, 16f, 0f));
                Assert.That(VirtualGeometrySampleCameraController.FramingDistance(bounds, 60f), Is.EqualTo(20f).Within(1e-3f));
            }
            {
                // 画角が狭いほど遠くなる
                var bounds = new Bounds(Vector3.zero, new Vector3(12f, 16f, 0f));
                float narrow = VirtualGeometrySampleCameraController.FramingDistance(bounds, 30f);
                float wide = VirtualGeometrySampleCameraController.FramingDistance(bounds, 90f);
                Assert.That(narrow, Is.GreaterThan(wide));
            }
        }

        [Test]
        public void FramingPosition()
        {
            // 位置は中心から forward の逆へ距離だけ離れ、そこから forward を向くと中心を通る
            var bounds = new Bounds(new Vector3(1f, 2f, 3f), new Vector3(12f, 16f, 0f));
            Vector3 forward = new Vector3(0.3f, -0.2f, 1f).normalized;
            Vector3 position = VirtualGeometrySampleCameraController.FramingPosition(bounds, 60f, forward);
            Vector3 toCenter = (bounds.center - position).normalized;
            Assert.That(Vector3.Dot(toCenter, forward), Is.EqualTo(1f).Within(1e-5f));
            Assert.That((bounds.center - position).magnitude, Is.EqualTo(20f).Within(1e-3f));
        }

        [Test]
        public void ClampPitch()
        {
            Assert.That(VirtualGeometrySampleCameraController.ClampPitch(120f), Is.EqualTo(89f));
            Assert.That(VirtualGeometrySampleCameraController.ClampPitch(-120f), Is.EqualTo(-89f));
            Assert.That(VirtualGeometrySampleCameraController.ClampPitch(30f), Is.EqualTo(30f));
        }
    }
}
