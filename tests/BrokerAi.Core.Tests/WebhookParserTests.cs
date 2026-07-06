using BrokerAi.Core.Webhook;
using FluentAssertions;
using Xunit;

namespace BrokerAi.Core.Tests;

public class WebhookParserTests
{
    private const string PhoneNumberId = "1234567890";

    private static string EnvelopeWithMessage(string messageJson) =>
        $$"""
        {
          "entry": [{
            "changes": [{
              "value": {
                "metadata": { "phone_number_id": "{{PhoneNumberId}}" },
                "messages": [{{messageJson}}]
              }
            }]
          }]
        }
        """;

    [Fact]
    public void Parse_TextMessage_ReturnsIncomingMessage()
    {
        var body = EnvelopeWithMessage("""
            { "from": "529981234567", "id": "wamid.abc", "type": "text", "timestamp": "1700000000",
              "text": { "body": "  hola  " } }
            """);

        var result = WebhookParser.Parse(body);

        result.Skip.Should().BeFalse();
        result.Message!.From.Should().Be("529981234567");
        result.Message.Text.Should().Be("hola");
        result.Message.Type.Should().Be("text");
    }

    [Fact]
    public void Parse_ImageWithCaption_ExtractsMediaIdAndCaption()
    {
        var body = EnvelopeWithMessage("""
            { "from": "529981234567", "id": "wamid.img", "type": "image", "timestamp": "1700000000",
              "image": { "id": "media123", "caption": "Casa bonita" } }
            """);

        var result = WebhookParser.Parse(body);

        result.Skip.Should().BeFalse();
        result.Message!.MediaId.Should().Be("media123");
        result.Message.Text.Should().Be("Casa bonita");
    }

    [Fact]
    public void Parse_VoiceNote_SetsIsVoiceFlag()
    {
        var body = EnvelopeWithMessage("""
            { "from": "529981234567", "id": "wamid.voice", "type": "audio", "timestamp": "1700000000" }
            """);

        var result = WebhookParser.Parse(body);

        result.Skip.Should().BeFalse();
        result.Message!.IsVoice.Should().BeTrue();
    }

    [Theory]
    [InlineData("sticker")]
    [InlineData("document")]
    [InlineData("location")]
    public void Parse_UnsupportedType_Skips(string type)
    {
        var body = EnvelopeWithMessage($$"""
            { "from": "529981234567", "id": "wamid.x", "type": "{{type}}", "timestamp": "1700000000" }
            """);

        var result = WebhookParser.Parse(body);

        result.Skip.Should().BeTrue();
        result.Reason.Should().Contain(type);
    }

    [Fact]
    public void Parse_StatusUpdate_NoMessagesArray_Skips()
    {
        const string body = """
            {
              "entry": [{
                "changes": [{
                  "value": {
                    "metadata": { "phone_number_id": "1234567890" },
                    "statuses": [{ "id": "wamid.x", "status": "delivered" }]
                  }
                }]
              }]
            }
            """;

        var result = WebhookParser.Parse(body);

        result.Skip.Should().BeTrue();
        result.Reason.Should().Be("no_message");
    }

    [Fact]
    public void Parse_TextWithoutBody_DoesNotThrow_SkipsInstead()
    {
        // Guards the old .js bug: message.text.body.trim() would throw on malformed payloads.
        var body = EnvelopeWithMessage("""
            { "from": "529981234567", "id": "wamid.bad", "type": "text", "timestamp": "1700000000" }
            """);

        var act = () => WebhookParser.Parse(body);

        act.Should().NotThrow();
        WebhookParser.Parse(body).Skip.Should().BeTrue();
    }

    [Fact]
    public void Parse_InvalidJson_Skips()
    {
        var result = WebhookParser.Parse("{not json");

        result.Skip.Should().BeTrue();
        result.Reason.Should().Be("invalid_json");
    }
}
