using System;
using System.Linq;
using NUnit.Framework;
using Graze.Presentation.Audio;

namespace Graze.Tests
{
    public sealed class MusicOddMeterTests
    {
        static string G(int[] groups) => string.Join("+", groups);

        [Test]
        public void DefaultGroupingsFollowTheUsualCounting()
        {
            Assert.AreEqual("2+2+3", G(MusicMeter.DefaultGrouping(7, 8)));
            Assert.AreEqual("4+4+3+3", G(MusicMeter.DefaultGrouping(14, 16)));
            Assert.AreEqual("4+3+3+3", G(MusicMeter.DefaultGrouping(13, 16)));
            Assert.AreEqual("3+2", G(MusicMeter.DefaultGrouping(5, 16)));
            Assert.AreEqual("2+3", G(MusicMeter.DefaultGrouping(5, 8)));
            Assert.AreEqual("3+3", G(MusicMeter.DefaultGrouping(6, 8)));
            Assert.AreEqual("3+3+3", G(MusicMeter.DefaultGrouping(9, 8)));
            Assert.AreEqual("2+2+2+2+3", G(MusicMeter.DefaultGrouping(11, 8)));
            Assert.AreEqual("2+3", G(MusicMeter.DefaultGrouping(5, 4)));
            Assert.AreEqual("1+1+1+1", G(MusicMeter.DefaultGrouping(4, 4)));
            Assert.IsTrue(MusicMeter.TryParseGrouping(" 3 + 3+4+4", 14, out var g)); Assert.AreEqual("3+3+4+4", G(g));
            Assert.IsFalse(MusicMeter.TryParseGrouping("3+3+4", 14, out _));
            Assert.Throws<ArgumentException>(() => new MusicMeter(4, 16, new[] { new MusicMeterChange(0, 7, 8, "3+3") }));
            var m = new MusicMeter(4, 32, new[] { new MusicMeterChange(8, 14, 16, "3+3+4+4") });
            Assert.AreEqual("3+3+4+4", G(m.GroupingAt(9, out bool authored))); Assert.IsTrue(authored);
            Assert.AreEqual("1+1+1+1", G(m.GroupingAt(1, out authored))); Assert.IsFalse(authored);
        }

    }
}
