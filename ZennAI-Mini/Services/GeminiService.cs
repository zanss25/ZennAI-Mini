using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;

namespace GeminiChat.Services
{
    // ─────────────────────────────────────────────
    //  Data Models (Gemini REST API schema)
    // ─────────────────────────────────────────────

    public enum ChatRole { User, Model }

    public record ChatMessage(ChatRole Role, string Text)
    {
        public string RoleString => Role == ChatRole.User ? "user" : "model";
    }

    // ── Request ──────────────────────────────────

    internal record GeminiRequest(
        [property: JsonPropertyName("contents")]          List<GeminiContent>       Contents,
        [property: JsonPropertyName("systemInstruction")] GeminiContent?            SystemInstruction = null,
        [property: JsonPropertyName("generationConfig")]  GeminiGenerationConfig?   GenerationConfig  = null
    );

    internal record GeminiContent(
        [property: JsonPropertyName("role")]  string            Role,
        [property: JsonPropertyName("parts")] List<GeminiPart>  Parts
    );

    internal record GeminiPart(
        [property: JsonPropertyName("text")] string Text
    );

    internal record GeminiGenerationConfig(
        [property: JsonPropertyName("temperature")]     float?  Temperature    = null,
        [property: JsonPropertyName("maxOutputTokens")] int?    MaxOutputTokens = null,
        [property: JsonPropertyName("topP")]            float?  TopP           = null,
        [property: JsonPropertyName("topK")]            int?    TopK           = null
    );

    // ── Response ─────────────────────────────────

    internal record GeminiResponse(
        [property: JsonPropertyName("candidates")]     List<GeminiCandidate>?  Candidates    = null,
        [property: JsonPropertyName("promptFeedback")] GeminiFeedback?         PromptFeedback = null,
        [property: JsonPropertyName("error")]          GeminiError?            Error          = null
    );

    internal record GeminiCandidate(
        [property: JsonPropertyName("content")]      GeminiContent?  Content     = null,
        [property: JsonPropertyName("finishReason")] string?         FinishReason = null
    );

    internal record GeminiFeedback(
        [property: JsonPropertyName("blockReason")] string? BlockReason = null
    );

    internal record GeminiError(
        [property: JsonPropertyName("code")]    int     Code    = 0,
        [property: JsonPropertyName("message")] string  Message = "",
        [property: JsonPropertyName("status")]  string  Status  = ""
    );

    // ── Streaming chunk ───────────────────────────

    internal record GeminiStreamChunk(
        [property: JsonPropertyName("candidates")] List<GeminiCandidate>? Candidates = null,
        [property: JsonPropertyName("error")]      GeminiError?           Error      = null
    );

    // ─────────────────────────────────────────────
    //  Service options
    // ─────────────────────────────────────────────

    public class GeminiServiceOptions
    {
        /// <summary>Gemini API key. Load from config, never hardcode.</summary>
        public required string ApiKey { get; init; }

        /// <summary>Model name. Default: gemini-2.5-flash</summary>
        public string Model { get; init; } = "gemini-2.5-flash";

        /// <summary>Optional system prompt sent on every request.</summary>
        public string? SystemPrompt { get; init; }

        /// <summary>Sampling temperature (0.0 – 2.0). Lower = more focused.</summary>
        public float Temperature { get; init; } = 0.9f;

        /// <summary>Maximum tokens in the reply.</summary>
        public int MaxOutputTokens { get; init; } = 2048;

        /// <summary>How many past messages to keep in context (0 = unlimited).</summary>
        public int ContextWindowMessages { get; init; } = 40;
    }

    // ─────────────────────────────────────────────
    //  Custom exceptions
    // ─────────────────────────────────────────────

    public class GeminiException : Exception
    {
        public int? StatusCode { get; }
        public GeminiException(string message, int? statusCode = null) : base(message)
            => StatusCode = statusCode;
    }

    // ─────────────────────────────────────────────
    //  Main service
    // ─────────────────────────────────────────────

    /// <summary>
    /// Lightweight wrapper around the Gemini REST API.
    /// Supports single-turn, multi-turn, and streaming responses.
    /// Thread-safe: conversation history is guarded by a lock.
    /// </summary>
    public sealed class GeminiService : IDisposable
    {
        // ── Constants ────────────────────────────

        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

        // ── Fields ───────────────────────────────

        private readonly HttpClient             _http;
        private readonly GeminiServiceOptions   _opts;
        private readonly List<ChatMessage>      _history = [];
        private readonly object                  _histLock = new();

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        };

        // ── Constructor ──────────────────────────

        public GeminiService(GeminiServiceOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(options.ApiKey))
                throw new ArgumentException("ApiKey must not be empty.", nameof(options));

            _opts = options;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        // ── Public API ───────────────────────────

        /// <summary>Read-only snapshot of the conversation history.</summary>
        public IReadOnlyList<ChatMessage> History
        {
            get { lock (_histLock) return [.. _history]; }
        }

        /// <summary>Removes all messages from history.</summary>
        public void ClearHistory()
        {
            lock (_histLock) _history.Clear();
        }

        /// <summary>
        /// Sends a user message and returns the full AI reply as a string.
        /// Automatically maintains conversation history.
        /// </summary>
        public async Task<string> SendMessageAsync(
            string userMessage,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                throw new ArgumentException("Message must not be empty.", nameof(userMessage));

            // Add user message to history
            lock (_histLock) _history.Add(new ChatMessage(ChatRole.User, userMessage));

            var url      = BuildUrl("generateContent");
            var request  = BuildRequest();
            var response = await PostAsync<GeminiResponse>(url, request, ct);

            var reply = ExtractText(response);

            // Add model reply to history
            lock (_histLock) _history.Add(new ChatMessage(ChatRole.Model, reply));

            return reply;
        }

        /// <summary>
        /// Streams the AI reply token-by-token via an IAsyncEnumerable.
        /// Automatically maintains conversation history.
        ///
        /// Usage:
        ///   await foreach (var chunk in service.StreamMessageAsync(text, ct))
        ///       myLabel.Text += chunk;
        /// </summary>
        public async IAsyncEnumerable<string> StreamMessageAsync(
            string userMessage,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                throw new ArgumentException("Message must not be empty.", nameof(userMessage));

            lock (_histLock) _history.Add(new ChatMessage(ChatRole.User, userMessage));

            var url     = BuildUrl("streamGenerateContent") + "&alt=sse";
            var request = BuildRequest();
            var json    = JsonSerializer.Serialize(request, _jsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _http.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new GeminiException("Request timed out.", null);
            }

            await using var stream = await httpResponse.Content.ReadAsStreamAsync(ct);
            using var reader       = new System.IO.StreamReader(stream);

            var fullReply = new StringBuilder();

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);

                // SSE lines are "data: {...json...}"
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                    continue;

                var jsonChunk = line["data: ".Length..];
                if (jsonChunk == "[DONE]") break;

                GeminiStreamChunk? chunk = null;
                try { chunk = JsonSerializer.Deserialize<GeminiStreamChunk>(jsonChunk, _jsonOpts); }
                catch { continue; }

                if (chunk?.Error is { } err)
                    throw new GeminiException($"[{err.Code}] {err.Message}", err.Code);

                var text = chunk?.Candidates?[0]?.Content?.Parts?[0]?.Text;
                if (string.IsNullOrEmpty(text)) continue;

                fullReply.Append(text);
                yield return text;
            }

            // Save complete reply to history
            lock (_histLock) _history.Add(new ChatMessage(ChatRole.Model, fullReply.ToString()));
        }

        // ── Private helpers ──────────────────────

        private string BuildUrl(string method)
            => $"{BaseUrl}/{_opts.Model}:{method}?key={_opts.ApiKey}";

        private GeminiRequest BuildRequest()
        {
            List<ChatMessage> snapshot;
            lock (_histLock)
            {
                var window = _opts.ContextWindowMessages > 0
                    ? Math.Min(_opts.ContextWindowMessages, _history.Count)
                    : _history.Count;

                snapshot = [.. _history[^window..]];
            }

            // Gemini API requires conversation to start with "user" role.
            // Strip any leading model messages (e.g. welcome message added by ViewModel).
            var filtered = snapshot.SkipWhile(m => m.Role == ChatRole.Model).ToList();

            var contents = filtered.ConvertAll(m => new GeminiContent(
                m.RoleString,
                [new GeminiPart(m.Text)]
            ));

            // Fallback: if filtered is somehow empty, send a dummy user content
            if (contents.Count == 0)
                contents.Add(new GeminiContent("user", [new GeminiPart("Halo")]));

            GeminiContent? systemInstruction = null;
            if (!string.IsNullOrWhiteSpace(_opts.SystemPrompt))
                systemInstruction = new GeminiContent("model", [new GeminiPart(_opts.SystemPrompt)]);

            var config = new GeminiGenerationConfig(
                Temperature:     _opts.Temperature,
                MaxOutputTokens: _opts.MaxOutputTokens
            );

            return new GeminiRequest(contents, systemInstruction, config);
        }

        private async Task<T> PostAsync<T>(
            string url,
            object payload,
            CancellationToken ct)
        {
            var json          = JsonSerializer.Serialize(payload, _jsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsync(url, content, ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new GeminiException("Request timed out.");
            }

            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Try to parse the Gemini error body
                try
                {
                    var errResp = JsonSerializer.Deserialize<GeminiResponse>(body, _jsonOpts);
                    if (errResp?.Error is { } err)
                        throw new GeminiException($"[{err.Code}] {err.Message}", err.Code);
                }
                catch (GeminiException) { throw; }
                catch { /* fall through */ }

                throw new GeminiException(
                    $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    (int)response.StatusCode);
            }

            return JsonSerializer.Deserialize<T>(body, _jsonOpts)
                ?? throw new GeminiException("Empty or null response from API.");
        }

        private static string ExtractText(GeminiResponse response)
        {
            if (response.Error is { } err)
                throw new GeminiException($"[{err.Code}] {err.Message}", err.Code);

            if (response.PromptFeedback?.BlockReason is { } reason)
                throw new GeminiException($"Prompt blocked by safety filter: {reason}");

            var text = response.Candidates?[0]?.Content?.Parts?[0]?.Text;
            if (string.IsNullOrEmpty(text))
                throw new GeminiException("No text content in API response.");

            return text;
        }

        // ── IDisposable ──────────────────────────

        public void Dispose() => _http.Dispose();
    }
}
