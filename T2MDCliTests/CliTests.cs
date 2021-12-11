using Microsoft.VisualStudio.TestTools.UnitTesting;
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

            await Cli.DownloadTrelloCardAttachmentAsync(attachment, "C:\\FakePath", "C:\\FakePath", ignoreFailedAttachmentDownloads, "Fake Board", "Fake Card");
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
    }
}
