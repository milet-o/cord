using System.Net;
using System.Text;
using System.Text.Json;

namespace Cord.WebShare.Tests;

public sealed class TelegramOwnerBotTests
{
    [Fact]
    public async Task IgnoresUnauthorizedChatIdAndExecutesAuthorizedCallbacksAndCommands()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var actions = new FakeBotActions();
        await using var bot = new TelegramOwnerBot("123:test-token", 99999L, actions, http);

        // Unauthorized chat id must be ignored completely
        using (var unauth = JsonDocument.Parse("""
            {
              "message": { "chat": { "id": 11111 }, "text": "/repouso" }
            }
            """))
        {
            await bot.ProcessUpdateJsonAsync(unauth.RootElement, CancellationToken.None);
        }

        Assert.Empty(actions.Executed);

        // Authorized inline approve callback
        using (var cbDoc = JsonDocument.Parse("""
            {
              "callback_query": {
                "id": "cb-1",
                "from": { "id": 99999 },
                "message": { "message_id": 42, "chat": { "id": 99999 } },
                "data": "approve:abc123"
              }
            }
            """))
        {
            await bot.ProcessUpdateJsonAsync(cbDoc.RootElement, CancellationToken.None);
        }

        Assert.Contains(actions.Executed, e => e == ("approve", "abc123", null));

        // Authorized /pslock & /repouso commands
        using (var cmdDoc = JsonDocument.Parse("""
            {
              "message": { "chat": { "id": 99999 }, "text": "/pslock" }
            }
            """))
        {
            await bot.ProcessUpdateJsonAsync(cmdDoc.RootElement, CancellationToken.None);
        }

        using (var restDoc = JsonDocument.Parse("""
            {
              "message": { "chat": { "id": 99999 }, "text": "/repouso" }
            }
            """))
        {
            await bot.ProcessUpdateJsonAsync(restDoc.RootElement, CancellationToken.None);
        }

        Assert.Contains(actions.Executed, e => e == ("setPsLock", null, "true"));
        Assert.Contains(actions.Executed, e => e == ("restMode", null, null));

        // Notification message contains IP, location and Approve/Deny buttons
        await bot.NotifyPendingRequestAsync(new WebPendingClient("c1", "Lucas_<tag>", "177.22.33.44", "Curitiba, PR (BR)", DateTimeOffset.UtcNow));
        var sentBody = handler.SentBodies.LastOrDefault(b => b.Contains("177.22.33.44", StringComparison.Ordinal));
        Assert.NotNull(sentBody);
        using (var sentDoc = JsonDocument.Parse(sentBody!))
        {
            Assert.Equal("HTML", sentDoc.RootElement.GetProperty("parse_mode").GetString());
            Assert.Contains("Lucas_&lt;tag&gt;", sentDoc.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        }

        Assert.Contains("approve:c1", sentBody, StringComparison.Ordinal);
        Assert.Contains("deny:c1", sentBody, StringComparison.Ordinal);

        // /link with underscore tokens uses HTML parse_mode cleanly
        using (var linkDoc = JsonDocument.Parse("""
            {
              "message": { "chat": { "id": 99999 }, "text": "/link" }
            }
            """))
        {
            await bot.ProcessUpdateJsonAsync(linkDoc.RootElement, CancellationToken.None);
        }

        var linkBody = handler.SentBodies.LastOrDefault(b => b.Contains("admin_key_with_underscore", StringComparison.Ordinal));
        Assert.NotNull(linkBody);
        Assert.Contains("\"parse_mode\":\"HTML\"", linkBody, StringComparison.Ordinal);
    }

    private sealed class FakeBotActions : ITelegramBotActions
    {
        public List<(string Action, string? TargetId, string? Value)> Executed { get; } = [];

        public string? ShareUrl => "https://example.trycloudflare.com/?t=guest_token_with_underscore";

        public string? AdminUrl => "https://example.trycloudflare.com/admin?k=admin_key_with_underscore";

        public WebAdminSnapshot GetSnapshot() => new(
            new WebShareStatus("PS5", "Ready", WebStreamState.Idle, true, 0, null, 0, 0, 48000, 2, null),
            [],
            [],
            "abc",
            "K7P9QM",
            true,
            true,
            true,
            true,
            true,
            null,
            3);

        public Task<WebAdminActionResult> ExecuteActionAsync(string action, string? targetId, string? value, CancellationToken cancellationToken)
        {
            Executed.Add((action, targetId, value));
            return Task.FromResult(new WebAdminActionResult(true, $"Executado: {action}"));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> SentBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                SentBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            var body = request.RequestUri?.AbsolutePath.EndsWith("getUpdates", StringComparison.Ordinal) == true
                ? """{"ok":true,"result":[]}"""
                : """{"ok":true,"result":{}}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
