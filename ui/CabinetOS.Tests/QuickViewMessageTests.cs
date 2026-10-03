using System.Text.Json;
using CabinetOS.Core.Tools;
using CabinetOS.Tests.Support;
using Json.Schema;

namespace CabinetOS.Tests;

/// <summary>
/// The web messages between the window and a viewer page in the Quick View panel (ADR 0023, item W2): each message's
/// fields, the size limits, malformed messages dropped, a panel page's command and subscribe refused, and every message
/// the window writes checked against the one schema, <c>sdk/tools/quickview-messages.schema.json</c>.
/// </summary>
public class QuickViewMessageTests
{
    private static readonly QuickViewLook Dark = new("dark", "#202020", "#FFFFFF", "#C5C5C5", "#60CDFF", "Segoe UI Variable");

    private static void AssertSchemaValid(string json)
    {
        var result = Schemas.QuickViewMessages.Evaluate(JsonDocument.Parse(json).RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, $"not valid against quickview-messages.schema.json: {json}");
    }

    private static bool SchemaValid(string json) => Schemas.QuickViewMessages.Evaluate(JsonDocument.Parse(json).RootElement).IsValid;

    [Fact]
    public void Shown_carries_its_token_details_and_keys()
    {
        var shown = QuickViewMessages.Parse("""{"type":"quickview-shown","token":42,"keys":["left","right","plus","minus"],"details":"4032 × 3024"}""");
        Assert.NotNull(shown);
        Assert.Equal(("quickview-shown", 42L, "4032 × 3024"), (shown.Type, shown.Token, shown.Details));
        Assert.Equal(["left", "right", "plus", "minus"], shown.Keys);

        var bare = QuickViewMessages.Parse("""{"type":"quickview-shown","token":0}""");
        Assert.Equal((0L, (string?)null, (IReadOnlyList<string>?)null), (bare?.Token, bare?.Details, bare?.Keys));
    }

    [Fact]
    public void Failed_keys_render_and_system_preview_parse_with_their_fields()
    {
        var failed = QuickViewMessages.Parse("""{"type":"quickview-failed","token":42,"reason":"unsupported","message":"This JPEG uses arithmetic coding, which the browser cannot read."}""");
        Assert.Equal(("unsupported", "This JPEG uses arithmetic coding, which the browser cannot read."), (failed?.Reason, failed?.Message));
        Assert.Equal(["k", "j", "l"], QuickViewMessages.Parse("""{"type":"quickview-keys","token":42,"keys":["k","j","l"]}""")?.Keys);
        var render = QuickViewMessages.Parse("""{"type":"quickview-render","token":42,"width":1380,"height":915}""");
        Assert.Equal((1380, 915), (render?.Width, render?.Height));
        Assert.Equal("quickview-system-preview", QuickViewMessages.Parse("""{"type":"quickview-system-preview","token":42}""")?.Type);
        Assert.Equal("ready", QuickViewMessages.Parse("""{"type":"ready"}""")?.Type);
    }

    [Theory]
    [InlineData("""{"type":"command","id":"file.delete"}""", "command")]
    [InlineData("""{"type":"subscribe","plugin":"agent"}""", "subscribe")]
    [InlineData("""{"type":"unsubscribe","plugin":"agent"}""", "unsubscribe")]
    public void A_panel_page_s_command_and_subscribe_are_refused(string json, string type)
    {
        var refused = QuickViewMessages.Parse(json);
        Assert.Equal((QuickViewPageMessage.Refused, type), (refused?.Type, refused?.RefusedType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"token":1}""")]
    [InlineData("""{"type":"open","path":"C:\\a"}""")]
    [InlineData("""{"type":"key","keys":"ctrl+p"}""")]
    [InlineData("""{"type":"quickview-unknown","token":1}""")]
    [InlineData("""{"type":"quickview-shown"}""")]
    [InlineData("""{"type":"quickview-shown","token":-1}""")]
    [InlineData("""{"type":"quickview-shown","token":"42"}""")]
    [InlineData("""{"type":"quickview-shown","token":4.5}""")]
    [InlineData("""{"type":"quickview-shown","token":1,"details":7}""")]
    [InlineData("""{"type":"quickview-shown","token":1,"keys":"left"}""")]
    [InlineData("""{"type":"quickview-shown","token":1,"keys":["left","left"]}""")]
    [InlineData("""{"type":"quickview-shown","token":1,"keys":[1]}""")]
    [InlineData("""{"type":"quickview-failed","token":1,"reason":"broken","message":"x"}""")]
    [InlineData("""{"type":"quickview-failed","token":1,"reason":"other"}""")]
    [InlineData("""{"type":"quickview-keys","token":1}""")]
    [InlineData("""{"type":"quickview-render","token":1,"width":0,"height":10}""")]
    [InlineData("""{"type":"quickview-render","token":1,"width":2561,"height":10}""")]
    [InlineData("""{"type":"quickview-render","token":1,"width":100}""")]
    public void A_malformed_message_is_dropped(string json) => Assert.Null(QuickViewMessages.Parse(json));

    [Fact]
    public void The_size_limits_drop_a_message_that_is_over_them()
    {
        Assert.NotNull(QuickViewMessages.Parse($$"""{"type":"quickview-shown","token":1,"details":"{{new string('d', 80)}}"}"""));
        Assert.Null(QuickViewMessages.Parse($$"""{"type":"quickview-shown","token":1,"details":"{{new string('d', 81)}}"}"""));
        Assert.NotNull(QuickViewMessages.Parse($$"""{"type":"quickview-failed","token":1,"reason":"other","message":"{{new string('m', 200)}}"}"""));
        Assert.Null(QuickViewMessages.Parse($$"""{"type":"quickview-failed","token":1,"reason":"other","message":"{{new string('m', 201)}}"}"""));
        var keys = string.Join(",", Enumerable.Range(0, 65).Select(i => $"\"k{i}\""));
        Assert.Null(QuickViewMessages.Parse($$"""{"type":"quickview-keys","token":1,"keys":[{{keys}}]}"""));
        Assert.Null(QuickViewMessages.Parse($$"""{"type":"quickview-shown","token":1,"details":"{{new string('x', ToolMessages.MaxIncomingLength)}}"}"""));
    }

    [Fact]
    public void A_key_a_page_may_not_ask_for_keeps_the_message_and_is_simply_never_granted()
    {
        var keys = QuickViewMessages.Parse("""{"type":"quickview-keys","token":3,"keys":["space","k"]}""")?.Keys;
        Assert.Equal(["space", "k"], keys);
        Assert.Equal(["k"], CabinetOS.Core.QuickView.QuickViewKeys.Grant(keys!, CabinetOS.Core.Keys.Keymap.Empty));
    }

    [Fact]
    public void Every_message_the_window_writes_follows_the_schema()
    {
        var show = QuickViewMessages.Show(42, @"C:\photos\IMG_0412.jpg", "https://f17.image-viewer.cabinetos.example/IMG_0412.jpg", "IMG_0412.jpg", ".jpg", "*.jpg",
            4718230, new DateTime(2026, 9, 30, 14, 2, 11, DateTimeKind.Utc), new QuickViewThumbnail("data:image/png;base64,iVBORw0KGgo+/=", 256, 192), Dark, 920, 610, 1.5);
        AssertSchemaValid(show);
        using (var parsed = JsonDocument.Parse(show))
        {
            var root = parsed.RootElement;
            Assert.Equal("2026-09-30T14:02:11Z", root.GetProperty("modified").GetString());
            Assert.Equal("data:image/png;base64,iVBORw0KGgo+/=", root.GetProperty("thumbnail").GetProperty("dataUrl").GetString());
            Assert.Equal(1.5, root.GetProperty("panel").GetProperty("scale").GetDouble());
        }
        AssertSchemaValid(QuickViewMessages.Show(43, @"C:\data\README", "https://f18.quickview-fixture.cabinetos.example/README", "README", "", "readme",
            0, DateTime.UtcNow, null, Dark with { Appearance = "light" }, 300.5, 200.25, 1));
        AssertSchemaValid(QuickViewMessages.Theme(42, Dark));
        AssertSchemaValid(QuickViewMessages.KeysGranted(42, ["left", "shift+plus", "k"]));
        AssertSchemaValid(QuickViewMessages.KeysGranted(42, []));
        AssertSchemaValid(QuickViewMessages.Key(42, "right", repeat: false));
        AssertSchemaValid(QuickViewMessages.Rendered(42, "https://r4.image-viewer.cabinetos.example/image.png", 1380, 1035));
        AssertSchemaValid(QuickViewMessages.RenderFailed(42, new string('x', 400)));
        AssertSchemaValid(QuickViewMessages.SystemPreviewFailed(42, "No preview handler runs outside the window for .docx files."));
    }

    [Fact]
    public void The_page_messages_the_schema_accepts_parse_and_those_it_refuses_are_dropped()
    {
        string[] good =
        [
            """{"type":"quickview-shown","token":42,"keys":["left","right","plus","minus"],"details":"4032 × 3024"}""",
            """{"type":"quickview-failed","token":42,"reason":"too-large","message":"Too big."}""",
            """{"type":"quickview-keys","token":42,"keys":["k","j","l","shift+period"]}""",
            """{"type":"quickview-render","token":42,"width":2560,"height":1}""",
            """{"type":"quickview-system-preview","token":42}""",
        ];
        foreach (var json in good)
        {
            Assert.True(SchemaValid(json), json);
            Assert.NotNull(QuickViewMessages.Parse(json));
        }
        string[] bad =
        [
            """{"type":"quickview-shown","token":-1}""",
            $$"""{"type":"quickview-shown","token":1,"details":"{{new string('d', 81)}}"}""",
            """{"type":"quickview-failed","token":1,"reason":"broken","message":"x"}""",
            """{"type":"quickview-render","token":1,"width":2561,"height":1}""",
        ];
        foreach (var json in bad)
        {
            Assert.False(SchemaValid(json), json);
            Assert.Null(QuickViewMessages.Parse(json));
        }
    }

    [Fact]
    public void The_fixture_viewer_is_a_valid_tool_for_the_window_and_the_schema()
    {
        var folder = Path.Combine(Repo.Root, "sdk", "fixtures", "tools", "quickview-fixture");
        var json = File.ReadAllText(Path.Combine(folder, "tool.json"));
        var manifest = ToolManifest.Parse(json, "quickview-fixture");
        Assert.Empty(manifest.Accepts);
        Assert.True(Schemas.Tool.Evaluate(JsonDocument.Parse(json).RootElement).IsValid);
        Assert.True(File.Exists(Path.Combine(folder, "quickview.html")));
    }

    [Fact]
    public void A_render_gets_a_host_of_its_own_beside_the_file_hosts()
    {
        Assert.Equal("r4.image-viewer.cabinetos.example", ToolFileUrls.RenderHost("image-viewer", 4, "cabinetos.example"));
        Assert.Equal("f17.image-viewer.cabinetos.example", ToolFileUrls.Host("image-viewer", 17, "cabinetos.example"));
    }
}
