using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Cord.WebShare;

public interface ITelegramBotActions
{
    WebAdminSnapshot GetSnapshot();

    string? ShareUrl { get; }

    string? AdminUrl { get; }

    Task<WebAdminActionResult> ExecuteActionAsync(string action, string? targetId, string? value, CancellationToken cancellationToken);
}

/// <summary>
/// Pure-.NET Telegram Bot long-polling client for owner security alerts (access requests with
/// IP/location + inline Approve/Deny buttons) and remote console management from a phone.
/// </summary>
public sealed class TelegramOwnerBot : IAsyncDisposable
{
    private readonly string _baseUrl;
    private readonly long _ownerChatId;
    private readonly ITelegramBotActions _actions;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pollTask;
    private long _offset;

    public TelegramOwnerBot(
        string botToken,
        long ownerChatId,
        ITelegramBotActions actions,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        _baseUrl = $"https://api.telegram.org/bot{botToken.Trim()}";
        _ownerChatId = ownerChatId;
        _actions = actions;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        _pollTask = Task.Run(() => PollLoopAsync(_stop.Token), CancellationToken.None);
    }

    public static async Task<(bool Ok, string Message)> SendTestMessageAsync(
        string botToken,
        string chatIdText,
        string? adminUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            return (false, "Informe o Token do Bot (@BotFather).");
        if (!long.TryParse(chatIdText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId) || chatId == 0)
            return (false, "Chat ID inválido (use apenas números, ex: obtido no @userinfobot).");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var text = "🎮 <b>Cord Web Share conectado!</b>\nVocê receberá aqui pedidos de entrada com IP e localização, alertas do jogo e comandos de controle.";
        if (!string.IsNullOrWhiteSpace(adminUrl))
            text += $"\n\n📱 Painel admin celular:\n<code>{EscapeHtml(adminUrl)}</code>";

        try
        {
            using var response = await http.PostAsJsonAsync(
                new Uri($"https://api.telegram.org/bot{botToken.Trim()}/sendMessage"),
                new { chat_id = chatId, text, parse_mode = "HTML" },
                cancellationToken);
            if (response.IsSuccessStatusCode)
                return (true, "Mensagem de teste enviada! Confira seu Telegram.");

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return (false, $"Erro do Telegram ({(int)response.StatusCode}): {body}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (false, $"Falha de rede: {e.Message}");
        }
    }

    public Task NotifyPendingRequestAsync(WebPendingClient pending)
    {
        var text =
            $"🔔 <b>Pedido para entrar no PS5</b>\n" +
            $"👤 <b>Nome:</b> {EscapeHtml(pending.Name)}\n" +
            $"🌐 <b>IP:</b> <code>{EscapeHtml(pending.Ip)}</code>\n" +
            $"📍 <b>Local:</b> {EscapeHtml(pending.Location)}";

        var keyboard = new
        {
            inline_keyboard = new[]
            {
                new[]
                {
                    new { text = "✅ Aceitar", callback_data = $"approve:{pending.Id}" },
                    new { text = "❌ Recusar", callback_data = $"deny:{pending.Id}" },
                },
            },
        };

        return SendSafeAsync(text, keyboard);
    }

    public Task NotifyClientJoinedAsync(WebRoomMember member)
    {
        var text =
            $"✅ <b>Entrou na sala:</b> {EscapeHtml(member.Name)}\n" +
            $"🌐 IP: <code>{EscapeHtml(member.Ip ?? "desconhecido")}</code> · {EscapeHtml(member.Location ?? "")}";
        var keyboard = new
        {
            inline_keyboard = new[]
            {
                new[]
                {
                    new { text = "🚫 Expulsar", callback_data = $"kick:{member.Id}" },
                    new { text = member.HasControl ? "🎮 Com controle" : "Passar controle", callback_data = $"pass:{member.Id}" },
                },
            },
        };
        return SendSafeAsync(text, keyboard);
    }

    public Task NotifyClientLeftAsync(string name, string reason) =>
        SendSafeAsync($"👋 <b>{EscapeHtml(name)}</b> saiu da sala ({EscapeHtml(reason)}).");

    public Task NotifyPsUnlockedAsync(WebRoomMember member) =>
        SendSafeAsync($"🔓 <b>{EscapeHtml(member.Name)}</b> desbloqueou o <b>Botão PS</b> usando a senha separada.");

    public Task NotifyGameExitAutoLockAsync(string previousGame, string? newApp)
    {
        var nowApp = string.IsNullOrWhiteSpace(newApp) ? "Menu do PS5 / Tela Inicial" : newApp;
        var text =
            $"⚠️ <b>Alerta de segurança: o jogo foi fechado!</b>\n" +
            $"🎮 Jogo anterior: <b>{EscapeHtml(previousGame)}</b>\n" +
            $"📺 Agora no console: <b>{EscapeHtml(nowApp)}</b>\n\n" +
            $"O controle do amigo foi <b>travado automaticamente</b> para proteger suas configurações.";

        var keyboard = new
        {
            inline_keyboard = new[]
            {
                new[]
                {
                    new { text = "🔓 Liberar controle", callback_data = "ctrl:on" },
                    new { text = "💤 Colocar PS5 em Repouso", callback_data = "rest:now" },
                },
            },
        };
        return SendSafeAsync(text, keyboard);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _pollTask;
        }
        catch (OperationCanceledException)
        {
        }

        if (_ownsHttp)
            _http.Dispose();
        _stop.Dispose();
    }

    internal async Task ProcessUpdateJsonAsync(JsonElement update, CancellationToken cancellationToken)
    {
        if (update.TryGetProperty("callback_query", out var callback))
        {
            await HandleCallbackAsync(callback, cancellationToken);
            return;
        }

        if (update.TryGetProperty("message", out var message))
            await HandleMessageAsync(message, cancellationToken);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var delaySeconds = 2;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var url = $"{_baseUrl}/getUpdates?offset={_offset.ToString(CultureInfo.InvariantCulture)}&timeout=25&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D";
                using var response = await _http.GetAsync(new Uri(url), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    delaySeconds = Math.Min(30, delaySeconds * 2);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (doc.RootElement.TryGetProperty("result", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        if (item.TryGetProperty("update_id", out var idProp) && idProp.TryGetInt64(out var updateId))
                            _offset = Math.Max(_offset, updateId + 1);
                        await ProcessUpdateJsonAsync(item, cancellationToken);
                    }
                }

                delaySeconds = 2;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    delaySeconds = Math.Min(30, delaySeconds * 2);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("chat", out var chat)
            || !chat.TryGetProperty("id", out var chatIdProp)
            || !chatIdProp.TryGetInt64(out var chatId)
            || chatId != _ownerChatId)
        {
            return;
        }

        var text = message.TryGetProperty("text", out var textProp) ? textProp.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(text))
            return;

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var at = cmd.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
            cmd = cmd[..at];
        var arg = parts.Length > 1 ? parts[1] : null;

        switch (cmd)
        {
            case "/start":
            case "/ajuda":
            case "/help":
                await SendSafeAsync(
                    "🎮 <b>Comandos do Cord PS5:</b>\n" +
                    "/status — Ver estado do PS5, jogo e sala\n" +
                    "/senha — Mostrar senha do dia atual\n" +
                    "/link — Ver link de convidado e painel celular\n" +
                    "/novolink — Gerar novo link (invalida o antigo)\n" +
                    "/kick <code>nome ou id</code> — Expulsar jogador\n" +
                    "/travar ou /liberar — Travar/liberar controle do amigo\n" +
                    "/pslock ou /psunlock — Travar/liberar botão PS\n" +
                    "/repouso — Colocar PS5 em Modo de Repouso");
                break;

            case "/status":
                await SendSafeAsync(FormatStatusText(_actions.GetSnapshot()));
                break;

            case "/senha":
                var snapPass = _actions.GetSnapshot();
                await SendSafeAsync($"🔑 <b>Senha do dia:</b> <code>{EscapeHtml(snapPass.DailyPassword ?? "-")}</code>\nExigir senha: {(snapPass.RequireDailyPassword ? "SIM" : "NÃO")}");
                break;

            case "/link":
                await SendSafeAsync(
                    $"🔗 <b>Convidado:</b>\n<code>{EscapeHtml(_actions.ShareUrl ?? "(túnel iniciando)")}</code>\n\n" +
                    $"📱 <b>Seu painel admin:</b>\n<code>{EscapeHtml(_actions.AdminUrl ?? "(túnel iniciando)")}</code>");
                break;

            case "/novolink":
                var newLinkRes = await _actions.ExecuteActionAsync("rotateGuestToken", null, null, cancellationToken);
                await SendSafeAsync($"♻️ {EscapeHtml(newLinkRes.Message)}\n🔗 <code>{EscapeHtml(_actions.ShareUrl ?? "")}</code>");
                break;

            case "/travar":
                await _actions.ExecuteActionAsync("setControllerAllowed", null, "false", cancellationToken);
                await SendSafeAsync("🔒 Controle remoto <b>travado</b>.");
                break;

            case "/liberar":
                await _actions.ExecuteActionAsync("setControllerAllowed", null, "true", cancellationToken);
                await SendSafeAsync("🎮 Controle remoto <b>liberado</b>.");
                break;

            case "/pslock":
                await _actions.ExecuteActionAsync("setPsLock", null, "true", cancellationToken);
                await SendSafeAsync("🛑 Botão PS <b>travado</b> para convidados.");
                break;

            case "/psunlock":
                await _actions.ExecuteActionAsync("setPsLock", null, "false", cancellationToken);
                await SendSafeAsync("🔓 Botão PS <b>liberado</b>.");
                break;

            case "/kick":
                await HandleKickCommandAsync(arg, cancellationToken);
                break;

            case "/repouso":
                var restRes = await _actions.ExecuteActionAsync("restMode", null, null, cancellationToken);
                await SendSafeAsync(restRes.Ok ? $"💤 {EscapeHtml(restRes.Message)}" : $"⚠️ {EscapeHtml(restRes.Message)}");
                break;
        }
    }

    private async Task HandleKickCommandAsync(string? arg, CancellationToken cancellationToken)
    {
        var snap = _actions.GetSnapshot();
        if (snap.Members.Count == 0)
        {
            await SendSafeAsync("Não há ninguém conectado na sala.");
            return;
        }

        WebRoomMember? match = null;
        if (string.IsNullOrWhiteSpace(arg))
        {
            if (snap.Members.Count == 1)
                match = snap.Members[0];
        }
        else
        {
            match = snap.Members.FirstOrDefault(m =>
                string.Equals(m.Id, arg, StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
        {
            var names = string.Join(", ", snap.Members.Select(m => $"{EscapeHtml(m.Name)} (<code>{EscapeHtml(m.Id)}</code>)"));
            await SendSafeAsync($"Informe quem expulsar: <code>/kick nome</code>\nNa sala: {names}");
            return;
        }

        var res = await _actions.ExecuteActionAsync("kick", match.Id, null, cancellationToken);
        await SendSafeAsync($"🚫 {EscapeHtml(match.Name)}: {EscapeHtml(res.Message)}");
    }

    private async Task HandleCallbackAsync(JsonElement callback, CancellationToken cancellationToken)
    {
        if (!callback.TryGetProperty("from", out var from)
            || !callback.TryGetProperty("message", out var msg)
            || !msg.TryGetProperty("chat", out var chat)
            || !chat.TryGetProperty("id", out var chatIdProp)
            || !chatIdProp.TryGetInt64(out var chatId)
            || chatId != _ownerChatId)
        {
            return;
        }

        var callbackId = callback.TryGetProperty("id", out var cid) ? cid.GetString() : null;
        var data = callback.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
        var messageId = msg.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out var m) ? m : 0;

        var (action, target, val) = data switch
        {
            _ when data.StartsWith("approve:", StringComparison.Ordinal) => ("approve", data[8..], (string?)null),
            _ when data.StartsWith("deny:", StringComparison.Ordinal) => ("deny", data[5..], null),
            _ when data.StartsWith("kick:", StringComparison.Ordinal) => ("kick", data[5..], null),
            _ when data.StartsWith("pass:", StringComparison.Ordinal) => ("passControl", data[5..], null),
            "ctrl:on" => ("setControllerAllowed", null, "true"),
            "ctrl:off" => ("setControllerAllowed", null, "false"),
            "rest:now" => ("restMode", null, null),
            _ => ("", null, null),
        };

        if (action.Length == 0)
            return;

        var res = await _actions.ExecuteActionAsync(action, target, val, cancellationToken);
        if (!string.IsNullOrEmpty(callbackId))
            await AnswerCallbackSafeAsync(callbackId, res.Message);

        if (messageId > 0)
            await SendSafeAsync($"{(res.Ok ? "✅" : "⚠️")} {EscapeHtml(res.Message)}");
    }

    private static string FormatStatusText(WebAdminSnapshot snap)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"🎮 <b>{EscapeHtml(snap.Status.ConsoleName)}</b> ({EscapeHtml(snap.Status.ConsoleState)} / {snap.Status.StreamState})\n");
        if (!string.IsNullOrWhiteSpace(snap.Status.RunningApp))
            sb.Append(CultureInfo.InvariantCulture, $"🕹 <b>Jogo aberto:</b> {EscapeHtml(snap.Status.RunningApp)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"👥 <b>Na sala:</b> {snap.Members.Count}/{snap.MaxClients} · Upload ~{snap.Status.EstimatedUploadMbps:0.0} Mbps\n");
        sb.Append(CultureInfo.InvariantCulture, $"🛡 Controle: {(snap.Status.ControllerAllowed ? "Liberado" : "Travado")} · Botão PS: {(snap.LockPsButton ? "Travado" : "Livre")}\n");
        sb.Append(CultureInfo.InvariantCulture, $"🔑 Senha do dia: <code>{EscapeHtml(snap.DailyPassword ?? "-")}</code>\n");

        if (snap.Pending.Count > 0)
        {
            sb.Append("\n⏳ <b>Pendentes:</b>\n");
            foreach (var p in snap.Pending)
                sb.Append(CultureInfo.InvariantCulture, $"• {EscapeHtml(p.Name)} (<code>{EscapeHtml(p.Ip)}</code>, {EscapeHtml(p.Location)})\n");
        }

        if (snap.Members.Count > 0)
        {
            sb.Append("\n<b>Jogadores conectados:</b>\n");
            foreach (var m in snap.Members)
                sb.Append(CultureInfo.InvariantCulture, $"• {EscapeHtml(m.Name)}{(m.HasControl ? " [Controle]" : "")} — <code>{EscapeHtml(m.Ip ?? "")}</code>\n");
        }

        return sb.ToString();
    }

    private async Task SendSafeAsync(string text, object? replyMarkup = null)
    {
        try
        {
            object payload = replyMarkup is null
                ? new { chat_id = _ownerChatId, text, parse_mode = "HTML" }
                : new { chat_id = _ownerChatId, text, parse_mode = "HTML", reply_markup = replyMarkup };
            using var resp = await _http.PostAsJsonAsync(new Uri($"{_baseUrl}/sendMessage"), payload, _stop.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
        }
    }

    private async Task AnswerCallbackSafeAsync(string callbackQueryId, string text)
    {
        try
        {
            await _http.PostAsJsonAsync(
                new Uri($"{_baseUrl}/answerCallbackQuery"),
                new { callback_query_id = callbackQueryId, text },
                _stop.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
        }
    }

    private static string EscapeHtml(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
