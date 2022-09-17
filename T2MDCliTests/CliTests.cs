using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
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

            await Cli.DownloadTrelloCardAttachmentAsync(
                attachment,
                "C:\\FakePath",
                "C:\\FakePath",
                ignoreFailedAttachmentDownloads,
                false,
                "Fake Board",
                "Fake Card"
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

            var options = new CliOptions() { RemoveEmoji = true };
            var listSuffixes = Cli.GetDuplicateSuffixes(input, options);

            CollectionAssert.AreEquivalent(listSuffixes, output);
        }
    }
}
