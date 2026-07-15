using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlowTrack.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowTrack.IoC;

internal sealed class ConsultaDanfeOptions
{
    public string BaseUrl { get; set; } = "https://consultadanfe.com";
    public string ConsultaPath { get; set; } = "/api/v1/consulta";
    public int TimeoutSeconds { get; set; } = 45;
}

internal sealed class ConsultaDanfeService(
    HttpClient httpClient,
    IOptions<ConsultaDanfeOptions> options,
    ILogger<ConsultaDanfeService> logger) : IConsultaDanfeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ConsultaDanfeApiResult> ConsultarNfeAsync(ConsultaDanfeApiRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ChaveAcesso))
        {
            return new ConsultaDanfeApiResult(false, 400, "application/json", "chave_obrigatoria", "A chave de acesso e obrigatoria.");
        }

        var normalizedChave = new string(request.ChaveAcesso.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var normalizedFormat = string.Equals(request.Format, "pdf", StringComparison.OrdinalIgnoreCase) ? "pdf" : "json";
        var endpoint = BuildConsultaUri();

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                chave = normalizedChave,
                format = normalizedFormat
            })
        };

        if (normalizedFormat == "pdf")
        {
            message.Headers.Accept.ParseAdd("application/pdf");
        }
        else
        {
            message.Headers.Accept.ParseAdd("application/json");
        }

        var startedAt = DateTime.UtcNow;
        try
        {
            using var response = await httpClient.SendAsync(message, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode && string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                var pdfBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                return new ConsultaDanfeApiResult(true, statusCode, contentType, null, null, PdfBytes: pdfBytes);
            }

            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = TryDeserializePayload(rawBody);
            var errorCode = response.Headers.TryGetValues("X-Error-Code", out var errorValues)
                ? errorValues.FirstOrDefault()
                : null;

            if (response.IsSuccessStatusCode)
            {
                return new ConsultaDanfeApiResult(
                    true,
                    statusCode,
                    contentType,
                    errorCode,
                    payload?.Info,
                    rawBody,
                    payload);
            }

            logger.LogWarning(
                "ConsultaDanfe retornou erro. StatusCode={StatusCode}, ErrorCode={ErrorCode}, DurationMs={DurationMs}, ChaveAcesso={ChaveAcesso}, Body={Body}",
                statusCode,
                errorCode,
                (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                MaskAccessKey(normalizedChave),
                rawBody);

            return new ConsultaDanfeApiResult(
                false,
                statusCode,
                contentType,
                errorCode ?? payload?.Status,
                payload?.Info ?? payload?.Status ?? "Falha ao consultar a NF-e.",
                rawBody,
                payload);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Timeout ao consultar ConsultaDanfe para a chave {ChaveAcesso}.", MaskAccessKey(normalizedChave));
            return new ConsultaDanfeApiResult(false, 408, null, "timeout", "Tempo limite excedido ao consultar a API da ConsultaDanfe.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Erro HTTP ao consultar ConsultaDanfe para a chave {ChaveAcesso}.", MaskAccessKey(normalizedChave));
            return new ConsultaDanfeApiResult(false, 503, null, "network_error", $"Erro de rede ao consultar a API da ConsultaDanfe: {exception.Message}");
        }
    }

    private Uri BuildConsultaUri()
    {
        var configured = options.Value;
        if (!Uri.TryCreate(configured.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("ConsultaDanfe:BaseUrl nao e uma URL valida.");
        }

        return new Uri(baseUri, configured.ConsultaPath);
    }

    private static ConsultaDanfeApiPayloadDto? TryDeserializePayload(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;

            var status = GetString(root, "status") ?? "unknown";
            return new ConsultaDanfeApiPayloadDto(
                status,
                GetString(root, "chave"),
                GetString(root, "tipo"),
                GetString(root, "pdf_base64"),
                GetString(root, "xml_base64"),
                GetString(root, "xml"),
                root.TryGetProperty("recovery", out var recoveryElement) && recoveryElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? recoveryElement.GetBoolean()
                    : null,
                GetString(root, "info") ?? GetString(root, "message"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static string MaskAccessKey(string chaveAcesso)
    {
        return chaveAcesso.Length <= 8
            ? "***"
            : $"{chaveAcesso[..4]}***{chaveAcesso[^4..]}";
    }
}
