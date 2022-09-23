using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GoldenSyrupGames.T2MD.Tests
{
    [TestClass]
    public class CliTests
    {
        // shared code for the tests
        public async Task DownloadFailingAttachment(bool ignoreFailedAttachmentDownloads)
        {
            var attachment = new TrelloAttachmentModel()
            {
                Url = "http://127.0.0.1/ShouldFail.png",
                Name = "ShouldFail",
                FileName = "ShouldFail"
            };

            var options = new CliOptions();
            await Cli.DownloadTrelloCardAttachmentAsync(
                attachment,
                "C:\\FakePath",
                "C:\\FakePath",
                ignoreFailedAttachmentDownloads,
                false,
                "Fake Board",
                "Fake Card",
                options
            );
        }

        [TestMethod]
        public async Task DownloadAttachment_FailedWithIgnoreSpecified_DoesntThrow()
        {
            await DownloadFailingAttachment(true);
        }

        [TestMethod]
        public async Task DownloadAttachment_FailedWithoutIgnoreSpecified_Throws()
        {
            bool threwException = true;
            // like this because Assert.ThrowsException wants a specific exception and we want any
            try
            {
                await DownloadFailingAttachment(false);
                // should have thrown above, fail
                threwException = false;
            }
            catch (System.Exception)
            {
                // success, pass
            }

            // Assert outside otherwise the catch will catch it
            if (!threwException)
            {
                Assert.Fail("No exception thrown.");
            }
        }

        [TestMethod]
        public void GetDuplicateSuffixes_CardsSameList_ReturnsCorrectSuffixes()
        {
            var card1 = new TrelloCardModel() { Name = "Card 1", IDList = "1" };
            var card2 = new TrelloCardModel() { Name = "Card 2", IDList = "1" };
            var card3 = new TrelloCardModel() { Name = "Card 1", IDList = "1" };

            var input = new TrelloCardModel[] { card1, card2, card3, };

            var output = new Dictionary<ITrelloCommon, string>()
            {
                { card1, "1" },
                { card2, "" },
                { card3, "2" }
            };

            var options = new CliOptions() { MaxCardFilenameTitleLength = 20 };
            var cardSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(cardSuffixes, output);
        }

        /// <summary>
        /// Test the issue of cards that are unique when full length but the same when truncated.
        /// </summary>
        [TestMethod]
        public void GetDuplicateSuffixes_CardsTruncated_ReturnsCorrectSuffixes()
        {
            var card1 = new TrelloCardModel() { Name = "Card 1", IDList = "1" };
            var card2 = new TrelloCardModel() { Name = "Card 2", IDList = "1" };
            var card3 = new TrelloCardModel() { Name = "Card 1", IDList = "1" };

            var input = new TrelloCardModel[] { card1, card2, card3, };

            var output = new Dictionary<ITrelloCommon, string>()
            {
                { card1, "1" },
                { card2, "2" },
                { card3, "3" }
            };

            var options = new CliOptions() { MaxCardFilenameTitleLength = 2 };
            var cardSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(cardSuffixes, output);
        }

        /// <summary>
        /// Test the issue of cards that are unique when full length but the same when truncated.
        /// </summary>
        [TestMethod]
        public void GetDuplicateSuffixes_DuplicateCase_ReturnsCorrectSuffixes()
        {
            var card1 = new TrelloCardModel() { Name = "Card 1", IDList = "1" };
            var card2 = new TrelloCardModel() { Name = "CARD 1", IDList = "1" };

            var input = new TrelloCardModel[] { card1, card2, };

            var output = new Dictionary<ITrelloCommon, string>() { { card1, "1" }, { card2, "2" } };

            var options = new CliOptions() { MaxCardFilenameTitleLength = 20 };
            var cardSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(cardSuffixes, output);
        }

        [TestMethod]
        public void GetDuplicateSuffixes_CardsEmojiRemoveEmoji_ReturnsCorrectSuffixes()
        {
            var card1 = new TrelloCardModel() { Name = "Card 💪", IDList = "1" };
            var card2 = new TrelloCardModel() { Name = "Card ❤", IDList = "1" };

            var input = new TrelloCardModel[] { card1, card2, };

            var output = new Dictionary<ITrelloCommon, string>() { { card1, "1" }, { card2, "2" } };

            var options = new CliOptions() { MaxCardFilenameTitleLength = 20, RemoveEmoji = true };
            var cardSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(cardSuffixes, output);
        }

        [TestMethod]
        public void GetDuplicateSuffixes_Lists_ReturnsCorrectSuffixes()
        {
            var List1 = new TrelloListModel() { Name = "List 1" };
            var List2 = new TrelloListModel() { Name = "List 2" };
            var List3 = new TrelloListModel() { Name = "List 1" };

            var input = new TrelloListModel[] { List1, List2, List3, };

            var output = new Dictionary<ITrelloCommon, string>()
            {
                { List1, "1" },
                { List2, "" },
                { List3, "2" }
            };

            var options = new CliOptions() { MaxCardFilenameTitleLength = 20 };
            var ListSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(ListSuffixes, output);
        }

        /// <summary>
        /// Test the issue of Lists that are unique when full length but the same when truncated.
        /// </summary>
        [TestMethod]
        public void GetDuplicateSuffixes_ListsTruncated_ReturnsCorrectSuffixes()
        {
            var List1 = new TrelloListModel() { Name = "List 1" };
            var List2 = new TrelloListModel() { Name = "List 2" };
            var List3 = new TrelloListModel() { Name = "List 1" };

            var input = new TrelloListModel[] { List1, List2, List3, };

            var output = new Dictionary<ITrelloCommon, string>()
            {
                { List1, "1" },
                { List2, "" },
                { List3, "2" }
            };

            var options = new CliOptions() { MaxCardFilenameTitleLength = 2 };
            var ListSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(ListSuffixes, output);
        }

        /// <summary>
        /// Test the issue of Lists that are unique when full length but the same when truncated.
        /// </summary>
        [TestMethod]
        public void GetDuplicateSuffixes_ListsDuplicateCase_ReturnsCorrectSuffixes()
        {
            var List1 = new TrelloListModel() { Name = "List 1" };
            var List2 = new TrelloListModel() { Name = "LIST 1" };

            var input = new TrelloListModel[] { List1, List2, };

            var output = new Dictionary<ITrelloCommon, string>() { { List1, "1" }, { List2, "2" } };

            var options = new CliOptions() { MaxCardFilenameTitleLength = 20 };
            var ListSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(ListSuffixes, output);
        }

        [TestMethod]
        public void GetDuplicateSuffixes_ListsEmojiRemoveEmoji_ReturnsCorrectSuffixes()
        {
            var list1 = new TrelloListModel() { Name = "List 💪" };
            var list2 = new TrelloListModel() { Name = "List ❤" };

            var input = new TrelloListModel[] { list1, list2, };

            var output = new Dictionary<ITrelloCommon, string>() { { list1, "1" }, { list2, "2" } };

            var options = new CliOptions() { MaxCardFilenameTitleLength = 40, RemoveEmoji = true };
            var listSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(listSuffixes, output);
        }

        [TestMethod]
        public void GetUsableCardName_PrecedingTrailingInnerDoubleSpaces_TrimmedAndRemoved()
        {
            var card = new TrelloCardModel() { Name = " card  title " };

            string expectedOutput = "card title";

            var options = new CliOptions() { MaxCardFilenameTitleLength = 40 };
            string actualOutput = Cli.GetUsableCardName(card, options);

            Assert.AreEqual(expectedOutput, actualOutput);
        }

        [TestMethod]
        public void GetUsableListName_PrecedingTrailingInnerDoubleSpaces_TrimmedAndRemoved()
        {
            var list = new TrelloListModel() { Name = " list  title " };

            string expectedOutput = "list title";

            var options = new CliOptions();
            string actualOutput = Cli.GetUsableListName(list, options);

            Assert.AreEqual(expectedOutput, actualOutput);
        }

        [TestMethod]
        public void GetUsableBoardName_PrecedingTrailingInnerDoubleSpaces_TrimmedAndRemoved()
        {
            var board = new TrelloApiBoardModel() { Name = " board  title " };

            string expectedOutput = "board title";

            var options = new CliOptions();
            string actualOutput = Cli.GetUsableBoardName(board, options);

            Assert.AreEqual(expectedOutput, actualOutput);
        }

        [TestMethod]
        public void GetTrelloCardUrlsFromText_AllUrlTypes_AllUrlsAreFound()
        {
            string input =
                @"
                    Document in a card somewhere (https://trello.com/c/aa11BB22/146-animating-in-blender)
                    complete https://trello.com/c/aa11BB22
                    complete https://trello.com/c/aa11BB22  
                    complete https://trello.com/c/Aa11BB22.
                    https://trello.com/c/aa11BB22
                    https://trello.com/c/aa11BB22/
                    complete https://trello.com/c/aa11BB22/
                    https://trello.com/c/aa11BB22/146-animating-in-blender/
                    https://trello.com/c/aa11BB22/146-animating-in-blender/. This sentence continues.
                    https://trello.com/c/aa11BB22/7-test-card-%E2%9D%A4-%F0%9F%92%A4
                    https://trello.com/c/aa11BB22/7-test-card-%E2%9D%A4-%F0%9F%92%A4-%E2%9A%A1
                    https://trello.com/c/aa11BB22/7-test-card-%E2%9D%A4-%F0%9F%92%A4-%E2%9A%A1-%F0%9F%9A%AB
                ";

            var expectedOutput = new List<string>()
            {
                "https://trello.com/c/aa11BB22/146-animating-in-blender",
                "https://trello.com/c/aa11BB22",
                "https://trello.com/c/aa11BB22",
                "https://trello.com/c/Aa11BB22",
                "https://trello.com/c/aa11BB22",
                "https://trello.com/c/aa11BB22/",
                "https://trello.com/c/aa11BB22/",
                "https://trello.com/c/aa11BB22/146-animating-in-blender/",
                "https://trello.com/c/aa11BB22/146-animating-in-blender/",
                "https://trello.com/c/aa11BB22/7-test-card-%E2%9D%A4-%F0%9F%92%A4",
                "https://trello.com/c/aa11BB22/7-test-card-%E2%9D%A4-%F0%9F%92%A4-%E2%9A%A1",
                "https://trello.com/c/aa11BB22/7-test-card-%E2%9D%A4-%F0%9F%92%A4-%E2%9A%A1-%F0%9F%9A%AB"
            };

            var actualOutput = Cli.GetTrelloCardUrlsFromText(input).ToList();

            CollectionAssert.AreEquivalent(expectedOutput, actualOutput);
        }
    }
}
