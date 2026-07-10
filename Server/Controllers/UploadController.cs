using Voiceover.Server.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Voiceover.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UploadController : ControllerBase
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".txt", ".zip"
    };

    private const long MaxFileSizeBytes = 15 * 1024 * 1024; // 15 MB

    private readonly UploadsPathOptions _uploadsPath;
    public UploadController(UploadsPathOptions uploadsPath) => _uploadsPath = uploadsPath;

    [HttpPost]
    [RequestSizeLimit(MaxFileSizeBytes)]
    public async Task<ActionResult<UploadResponse>> Upload(IFormFile file)
    {
        if (file.Length == 0) return BadRequest("Empty file.");
        if (file.Length > MaxFileSizeBytes) return BadRequest("File too large (15 MB max).");

        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(ext)) return BadRequest("File type not allowed.");

        // Random file name to avoid path traversal / collisions / leaking original names.
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(_uploadsPath.Path, storedName);

        await using (var stream = System.IO.File.Create(fullPath))
        {
            await file.CopyToAsync(stream);
        }

        return Ok(new UploadResponse($"/uploads/{storedName}"));
    }
}
