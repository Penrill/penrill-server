using System.Text.Json.Nodes;
using Penrill.Domain.Boards;

namespace Penrill.Domain.UnitTests.Boards;

public class ChangeTests
{
    private static Change Parse(string json)
    {
        ChangeCodec.TryParse(JsonNode.Parse(json), out var change).ShouldBeTrue();
        return change!;
    }

    [TestCase("""{"id":"c1","ops":[]}""")]
    [TestCase("""{"id":"c1","ops":[{"op":"put","item":{"type":"stroke"}}]}""")]
    [TestCase("""{"id":"c1","ops":[{"op":"patch","id":"a","set":{"id":"b"}}]}""")]
    [TestCase("""{"id":"c1","ops":[{"op":"patch","id":"a"}]}""")]
    [TestCase("""{"id":"c1","ops":[{"op":"meta"}]}""")]
    [TestCase("""{"id":"c1","ops":[{"op":"delete","id":"a"}],"extra":1}""")]
    [TestCase("""{"ops":[{"op":"delete","id":"a"}]}""")]
    [TestCase("""{"id":"c1","ops":[{"op":"move","id":"a"}]}""")]
    public void RefusesMalformedChanges(string json) => ChangeCodec.TryParse(JsonNode.Parse(json), out _).ShouldBeFalse();

    [Test]
    public void AppliesOperationsInOrder()
    {
        var document = new BoardDocument("b", 0, "Board", "dots", []);
        document.Apply(1, Parse("""
            {"id":"c1","ops":[
              {"op":"put","item":{"id":"a","z":1,"color":"red","rot":1}},
              {"op":"put","item":{"id":"b","z":2}},
              {"op":"patch","id":"a","set":{"color":"blue"},"unset":["rot"]},
              {"op":"patch","id":"gone","set":{"x":1}},
              {"op":"delete","id":"b"},
              {"op":"meta","title":"Physics"}]}
            """));
        document.Seq.ShouldBe(1);
        document.Title.ShouldBe("Physics");
        document.Background.ShouldBe("dots");
        var item = BoardDocument.ReadItems(document.SerializeItems()).ShouldHaveSingleItem();
        item.ToJsonString().ShouldBe("""{"id":"a","z":1,"color":"blue"}""");
    }

    [Test]
    public void ReplayingAChangeGivesTheSameBoard()
    {
        var change = Parse("""{"id":"c1","ops":[{"op":"put","item":{"id":"a","x":1}},{"op":"patch","id":"a","set":{"x":2}},{"op":"delete","id":"z"}]}""");
        var once = new BoardDocument("b", 0, "Board", "dots", []);
        once.Apply(1, change);
        var twice = new BoardDocument("b", 0, "Board", "dots", []);
        twice.Apply(1, change);
        twice.Apply(2, change);
        twice.SerializeItems().ShouldBe(once.SerializeItems());
    }

    [Test]
    public void RefusesBoardsWithDuplicateItems() =>
        BoardContent.TryParse(JsonNode.Parse("""{"title":"T","background":"dots","items":[{"id":"a"},{"id":"a"}]}"""), out _).ShouldBeFalse();
}
