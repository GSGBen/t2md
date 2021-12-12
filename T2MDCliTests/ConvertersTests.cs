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

    [TestClass]
    public class TrelloStringJsonConverterTests
    {
        private JsonSerializerOptions _jsonDeserializeOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters =
            {
                new TrelloStringJsonConverter()
            }
        };

        // runs before each test
        [TestInitialize]
        public void Initialize()
        {
        }

        // ensure the default string handling still works
        [TestMethod]
        public void Read_JsonString_ReturnsString()
        {
            var stringValue = "Standard json string";
            var trelloCardJson = $"{{\"desc\": \"{stringValue}\"}}";
            var trelloCard = JsonSerializer.Deserialize<TrelloCardModel>(trelloCardJson, _jsonDeserializeOptions);

            Assert.AreEqual(stringValue, trelloCard.Desc);
        }

        // ensure the bools where there should be strings come through as bools
        [TestMethod]
        public void Read_JsonBoolInPlaceOfString_ReturnsString()
        {
            var trelloCardJson = "{\"desc\": true}";
            var trelloCard = JsonSerializer.Deserialize<TrelloCardModel>(trelloCardJson, _jsonDeserializeOptions);

            Assert.AreEqual("true", trelloCard.Desc);
        }

        // ensure number conversion works
        [TestMethod]
        public void Read_JsonNumber_ReturnsString()
        {
            double numberValue = 123.45;
            var trelloCardJson = $"{{\"desc\": {numberValue}}}";
            var trelloCard = JsonSerializer.Deserialize<TrelloCardModel>(trelloCardJson, _jsonDeserializeOptions);

            Assert.AreEqual(numberValue.ToString(), trelloCard.Desc);
        }
    }
}
