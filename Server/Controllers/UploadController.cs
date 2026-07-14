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

    private const long MaxFileSizeBytes = 8 * 1024 * 1024; // 8 MB

    private readonly UploadsPathOptions _uploadsPath;
    public UploadController(UploadsPathOptions uploadsPath) => _uploadsPath = uploadsPath;

    [HttpPost]
    [RequestSizeLimit(MaxFileSizeBytes)]
    public async Task<ActionResult<UploadResponse>> Upload(IFormFile file)
    {
        if (file.Length == 0) return BadRequest("Empty file.");
        if (file.Length > MaxFileSizeBytes) return BadRequest("File too large (8 MB max).");

        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(ext)) return BadRequest("File type not allowed.");

        if (!await MatchesExpectedContentAsync(file, ext))
            return BadRequest("File content doesn't match its extension.");

        // Random file name to avoid path traversal / collisions / leaking original names.
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(_uploadsPath.Path, storedName);

        await using (var stream = System.IO.File.Create(fullPath))
        {
            await file.CopyToAsync(stream);
        }

        return Ok(new UploadResponse($"/uploads/{storedName}"));
    }

    // Extension allowlisting alone lets someone upload arbitrary bytes under
    // a trusted-looking extension (e.g. an HTML/script payload named
    // "x.png") - this checks the file's actual leading bytes match what the
    // extension claims. Not a full content sniff (no image decode), just
    // the standard magic-byte signatures - enough to catch "this isn't
    // really a PNG" without pulling in an image library this otherwise
    // dependency-light codebase doesn't need. IFormFile is already fully
    // buffered (in memory or a spooled temp file) by the time model binding
    // hands it to the action, so OpenReadStream() here and CopyToAsync()
    // afterward each independently read from the start - no seeking needed.
    private static async Task<bool> MatchesExpectedContentAsync(IFormFile file, string ext)
    {
        await using var stream = file.OpenReadStream();
        var header = new byte[12];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length));

        return ext.ToLowerInvariant() switch
        {
            ".png" => read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                                 && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A,
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".gif" => read >= 4 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F' && header[3] == '8',
            ".webp" => read >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                                   && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P',
            ".pdf" => read >= 5 && header[0] == '%' && header[1] == 'P' && header[2] == 'D' && header[3] == 'F' && header[4] == '-',
            ".zip" => read >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04,
            // No reliable magic bytes for plain text - reject an obvious
            // binary payload instead (a NUL byte never appears in valid
            // UTF-8/ASCII text).
            ".txt" => !header.AsSpan(0, read).Contains((byte)0),
            _ => false
        };
    }
}
