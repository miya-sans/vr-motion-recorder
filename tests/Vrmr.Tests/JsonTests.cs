using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace VRMR.Tests
{
    public class JsonTests
    {
        [Fact]
        public void RoundTripKeepsNestedValues()
        {
            var o = new Dictionary<string, object>
            {
                ["name"] = "take \"one\"", ["n"] = 3.5, ["on"] = true, ["none"] = null,
                ["list"] = new List<object> { 1.0, "two", new Dictionary<string, object> { ["k"] = -0.25 } }
            };
            var back = Json.ParseObject(Json.Serialize(o));
            Assert.Equal("take \"one\"", back.GetStr("name", ""));
            Assert.Equal(3.5f, back.GetF("n"), 5);
            Assert.True(back.GetBool("on"));
            var list = back.GetList("list");
            Assert.Equal(3, list.Count);
            Assert.Equal(-0.25f, ((Dictionary<string, object>)list[2]).GetF("k"), 5);
        }

        [Fact]
        public void NumbersUseInvariantCulture()
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma
                string s = Json.Serialize(new Dictionary<string, object> { ["v"] = 1.5 });
                Assert.Contains("1.5", s);
                Assert.Equal(1.5f, Json.ParseObject(s).GetF("v"), 5);
            }
            finally { CultureInfo.CurrentCulture = old; }
        }
    }
}
