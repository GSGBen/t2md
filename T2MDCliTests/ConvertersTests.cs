using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace GoldenSyrupGames.T2MD.Tests
{
    [TestClass]
    public class TrelloDoubleJsonConverterTests
    {
        private JsonSerializerOptions _jsonDeserializeOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters =
            {
                new TrelloDoubleJsonConverter()
            }
        };

        // runs before each test
        [TestInitialize]
        public void Initialize()
        {
        }

        // ensure the default number handling still works (double)
        [TestMethod]
        public void Read_JsonDouble_ReturnsDouble()
        {
            double number = 65535.0;
            var trelloCardJson = $"{{\"pos\": {number}}}";
            var trelloCard = JsonSerializer.Deserialize<TrelloCardModel>(trelloCardJson, _jsonDeserializeOptions);

            Assert.AreEqual(number, trelloCard.Pos);
        }

        // ensure the default number handling still works (int)
        [TestMethod]
        public void Read_JsonInt_ReturnsDouble()
        {
            int number = 65535;
            var trelloCardJson = $"{{\"pos\": {number}}}";
            var trelloCard = JsonSerializer.Deserialize<TrelloCardModel>(trelloCardJson, _jsonDeserializeOptions);

            Assert.AreEqual(number, trelloCard.Pos);
        }

        // ensure converting from string works
        [TestMethod]
        public void Read_JsonNumberInString_ReturnsDouble()
        {
            double number = 65535.0;
            var trelloCardJson = $"{{\"pos\": \"{number}\"}}";
            var trelloCard = JsonSerializer.Deserialize<TrelloCardModel>(trelloCardJson, _jsonDeserializeOptions);

            Assert.AreEqual(number, trelloCard.Pos);
        }

        // ensure converting Trello's custom positions works
        [TestMethod]
        public void Read_Bottom_ReturnsDouble()
        {
            var trelloCardJson = "{\"pos\": \"bottom\"}";
            var trelloCard = JsonSerializer.Deserialize<TrelloCardModel>(trelloCardJson, _jsonDeserializeOptions);

            Assert.IsInstanceOfType(trelloCard.Pos, typeof(double));
        }

    }
}
