using FlowTrack.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowTrack.API.Controllers;

[ApiController]
[Authorize]
[Route("api/documents")]
public sealed class DocumentsController : ControllerBase
{
    [HttpPost("nfe/extract")]
    [DisableRequestSizeLimit]
    public async Task<ActionResult<PdfExtractionDto>> ExtractNfe(
        IFormFile file,
        [FromServices] IPdfExtractionService extraction,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0 || file.Length > 10_000_000 || !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Envie um PDF de até 10 MB." });
        }

        await using var stream = file.OpenReadStream();
        var result = await extraction.ExtractAsync(stream, cancellationToken);
        return Ok(result);
    }

    [HttpPost("nfe/consulta-danfe")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> ConsultarDanfe(
        [FromBody] ConsultaDanfeApiRequest request,
        [FromServices] IConsultaDanfeService consultaDanfe,
        CancellationToken cancellationToken)
    {
        var result = await consultaDanfe.ConsultarNfeAsync(request, cancellationToken);

        if (result.Success && result.PdfBytes is not null)
        {
            var fileName = string.IsNullOrWhiteSpace(request.ChaveAcesso)
                ? "danfe.pdf"
                : $"DANFE_{new string(request.ChaveAcesso.Where(char.IsLetterOrDigit).ToArray())}.pdf";

            return File(result.PdfBytes, "application/pdf", fileName);
        }

        if (result.Success)
        {
            return Ok(result);
        }

        return StatusCode(result.StatusCode > 0 ? result.StatusCode : 500, result);
    }
}
