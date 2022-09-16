using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GoldenSyrupGames.T2MD.Tests
{
    [TestClass]
    public class EmojiTests
    {
        [TestMethod]
        public void ReplaceEmoji_EmojiInMiddleDefaultReplacement_ReplacesEmojiWithUnderscore()
        {
            var input = "start 💪☂ end";
            var expectedOutput = "start __ end";

            string actualOutput = Emoji.ReplaceEmoji(input);

            Assert.AreEqual(expectedOutput, actualOutput);
        }

        [TestMethod]
        public void ReplaceEmoji_EmojiInMiddleBlankReplacement_RemovesEmoji()
        {
            var input = "start 💪☂ end";
            var expectedOutput = "start __ end";

            string actualOutput = Emoji.ReplaceEmoji(input);

            Assert.AreEqual(expectedOutput, actualOutput);
        }
    }
}
