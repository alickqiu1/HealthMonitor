using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using HealthMonitor.Application.AiQueries;
using HealthMonitor.Application.AiQueries.Ports;

namespace HealthMonitor.Infrastructure.AiQueries;

/// <summary>Owns one fixed-profile, no-retry structured-output transport with bounded response bytes and per-call cancellation.</summary>
/// <remarks>Construction reads no configuration and opens no connection. Production registration remains disabled.
/// The ten-second budget is cooperative, covers headers/body/parsing, and cannot force uncooperative code or stop billing.
/// Owners must drain calls before disposing this shared client; concurrent Dispose is not an ownership guarantee.</remarks>
public sealed class ChatCompletionsStructuredQueryGenerator : IStructuredQueryGenerator, IDisposable
{
    private readonly StructuredQueryProviderProfile profile;
    private readonly HttpClient client;
    private readonly string apiKey;
    private readonly TimeProvider clock;
    private bool disposed;
    private const int MaximumBytes = 8192;

    /// <summary>Creates the private fixed-policy client without resolving a credential store or sending a request.</summary>
    /// <param name="profile">Reviewed installation profile; arbitrary endpoints/models cannot be constructed.</param>
    /// <param name="apiKey">Header-safe provider credential supplied by future composition; never logged.</param>
    /// <exception cref="ArgumentNullException">The selected profile is absent.</exception>
    /// <exception cref="ArgumentException">The supplied credential cannot safely form a Bearer token.</exception>
    public ChatCompletionsStructuredQueryGenerator(StructuredQueryProviderProfile profile, string apiKey)
        : this(RequireProfile(profile), ValidateKey(apiKey), CreateHandler(), TimeProvider.System) { }

    /// <summary>Owns controlled test resources without exposing an unsafe client/handler injection API to production.</summary>
    internal ChatCompletionsStructuredQueryGenerator(StructuredQueryProviderProfile profile, string apiKey, HttpMessageHandler handler, TimeProvider clock)
    {
        this.profile = RequireProfile(profile);
        this.apiKey = ValidateKey(apiKey);
        ArgumentNullException.ThrowIfNull(handler); ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Creates production handler policy; normal TLS validation is deliberately retained.</summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
        // Rejecting an error/oversized body must not trigger background draining merely to reuse the connection.
        MaxResponseDrainSize = 0
    };

    /// <summary>Generates once and returns only a strictly decoded untrusted proposal.</summary>
    /// <param name="prompt">Already trimmed Application-validated text.</param>
    /// <param name="vocabulary">Owned immutable seed vocabulary.</param>
    /// <param name="cancellationToken">Caller cancellation; takes precedence over provider timeout/failure.</param>
    /// <returns>A detached candidate for independent Application semantic validation.</returns>
    /// <exception cref="InvalidAiPromptException">The prompt violates the bounded contract.</exception>
    /// <exception cref="InvalidStructuredQueryException">Successful HTTP output violates media, byte, or protocol rules.</exception>
    /// <exception cref="AiProviderUnavailableException">Expected transport/status or otherwise successful cleanup failed.</exception>
    /// <exception cref="AiProviderTimeoutException">The owned ten-second budget expired.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<StructuredQueryProposal?> GenerateAsync(string prompt, QueryVocabulary vocabulary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        var requestBytes = ChatCompletionsQueryProtocol.CreateRequestBody(profile, prompt, vocabulary);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        HttpRequestMessage? request = null; HttpResponseMessage? response = null; Stream? stream = null;
        StructuredQueryProposal? proposal = null; Exception? failure = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            request = new(HttpMethod.Post, profile.Endpoint) { Content = new ByteArrayContent(requestBytes) };
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            request.Headers.Accept.Add(new("application/json"));
            request.Headers.Authorization = new("Bearer", apiKey);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            // Do not read provider error bodies: they can contain request text, credentials, and diagnostics.
            if (!response.IsSuccessStatusCode) throw new AiProviderUnavailableException();
            long? declared = ValidateHeaders(response.Content.Headers);
            stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var bytes = new byte[MaximumBytes + 1]; int length = 0;
            while (length <= MaximumBytes)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(length, bytes.Length - length), linked.Token);
                if (read == 0) break;
                length += read;
            }
            linked.Token.ThrowIfCancellationRequested();
            if (length > MaximumBytes || (declared.HasValue && declared.Value != length)) throw new InvalidStructuredQueryException();
            proposal = ChatCompletionsQueryProtocol.ParseEnvelope(profile, bytes.AsMemory(0, length));
            linked.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            // Retain the primary classification through disposal; unexpected programming faults keep their original stack.
            failure = error is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException
                ? new AiProviderUnavailableException() : error;
        }
        finally
        {
            DisposeOwned(stream, ref failure); DisposeOwned(response, ref failure); DisposeOwned(request, ref failure);
        }
        // These final checks also cover cancellation racing with HTTP, protocol, or cleanup failure.
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.IsCancellationRequested) throw new AiProviderTimeoutException();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return proposal!;
    }

    /// <summary>Validates success metadata before allocating/reading a body, retaining one UTF-8 decoding interpretation.</summary>
    private static long? ValidateHeaders(HttpContentHeaders headers)
    {
        try
        {
            var media = headers.ContentType;
            if (headers.ContentEncoding.Count != 0 || media is null
                || !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)) throw new InvalidStructuredQueryException();
            var charsets = media.Parameters.Where(parameter => string.Equals(parameter.Name, "charset", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (charsets.Length > 1) throw new InvalidStructuredQueryException();
            if (charsets.Length == 1)
            {
                string? value = charsets[0].Value;
                if (value?.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                if (!string.Equals(value, "utf-8", StringComparison.OrdinalIgnoreCase)) throw new InvalidStructuredQueryException();
            }
            long? length = headers.ContentLength;
            if (length is < 0 or > MaximumBytes) throw new InvalidStructuredQueryException();
            return length;
        }
        catch (FormatException) { throw new InvalidStructuredQueryException(); }
    }

    /// <summary>Releases all owned resources even if cleanup fails; cleanup cannot replace a primary error or leak raw diagnostics.</summary>
    private static void DisposeOwned(IDisposable? resource, ref Exception? failure)
    {
        try { resource?.Dispose(); }
        catch (Exception) { failure ??= new AiProviderUnavailableException(); }
    }

    /// <summary>Checks Bearer-token header safety without attempting provider authentication or echoing the supplied value.</summary>
    private static string ValidateKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Any(value => !char.IsAsciiLetterOrDigit(value) && !"-._~+/=".Contains(value)))
            throw new ArgumentException("A header-safe provider API key is required.", nameof(apiKey));
        return apiKey;
    }

    /// <summary>Rejects a missing profile before constructing transport resources.</summary>
    private static StructuredQueryProviderProfile RequireProfile(StructuredQueryProviderProfile profile)
    { ArgumentNullException.ThrowIfNull(profile); return profile; }

    /// <summary>Disposes the owned client/handler after the owner has drained its calls; safe to call repeatedly.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; client.Dispose();
    }
}
