using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

var builder = WebApplication.CreateBuilder(args);

var connectionString = "Data Source=myimagedatabase.db";

builder.Services.AddAntiforgery();
builder.Services.AddSingleton<TravelyRepository>(sp =>
{
    return new TravelyRepository(connectionString);
});

var app = builder.Build();
app.UseStaticFiles();

var travelRepository = app.Services.GetRequiredService<TravelyRepository>();
await travelRepository.CreateDatabaseTable();

app.UseAntiforgery();

app.MapGet("/", async (HttpContext context, TravelyRepository repository) =>
{
    var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
    var token = antiforgery.GetAndStoreTokens(context);
    var imagesTask = repository.GetLatestImages(); 
    var images = await imagesTask;
    var tasks = images.Select(image => GetImageHtml(image));
    var imageHtmls = await Task.WhenAll(tasks);
    var imageHtml = string.Join("", imageHtmls);
    var htmlContent = await GetHtmlContent("HomePage.html");
    htmlContent = htmlContent.Replace("<!-- Images will be dynamically inserted here -->", imageHtml)
                             .Replace("{token.FormFieldName}", token.FormFieldName)
                             .Replace("{token.RequestToken}", token.RequestToken);

    return Results.Content(htmlContent, "text/html");
});
async Task<string> GetImageHtml(Image image)
{
    return $@"
        <div class='col-lg-4 mb-4' style='padding: 20px;'>
            <div>
                <img src='/picture/{image.Id}' alt='{image.Title}'>
                <div class='card-body'>
                    <h5 class='card-title'>{image.Title}</h5>
                </div>
            </div>
        </div>";
}
app.MapPost("/upload", async (HttpContext context, IFormFile file, [FromForm] string title, TravelyRepository repository) =>
{
    var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
    await antiforgery.ValidateRequestAsync(context);
    var id = Guid.NewGuid().ToString();
    using var memoryStream = new MemoryStream();
    await file.CopyToAsync(memoryStream);
    var imageData = memoryStream.ToArray();
    await repository.SaveImage(id, title, imageData,60,60);
    return Results.Redirect("/");
});
app.MapGet("/picture/{id}", async (HttpContext context, string id, TravelyRepository repository) =>
{
    var image = await repository.GetImage(id);

    if (image != null)
    {
        context.Response.ContentType = image.ContentType;
        await context.Response.Body.WriteAsync(image.ImageData);
    }
    else
    {
        context.Response.StatusCode = 404;
    }
});
app.Run();

async Task<string> GetHtmlContent(string fileName)
{
    try
    {
        var fileProvider = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
        var fileInfo = fileProvider.GetFileInfo(fileName);

        if (fileInfo.Exists)
        {
            using var reader = new StreamReader(fileInfo.CreateReadStream());
            return await reader.ReadToEndAsync();
        }
        else
        {
            return "<html><body><h1>Error: HTML file not found</h1></body></html>";
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading HTML content: {ex.Message}");
        return "<html><body><h1>Error: Failed to read HTML content</h1></body></html>";
    }
}
string InjectImageDetails(string htmlContent, List<Image> images)
{
    foreach (var image in images)
    {
        htmlContent = htmlContent.Replace("{image.Title}", image.Title)
                                 .Replace("{id}", image.Id);
    }
    return htmlContent;
}
string InjectAntiforgeryToken(string htmlContent, AntiforgeryTokenSet token)
{
    return htmlContent.Replace("{token.FormFieldName}", token.FormFieldName)
                     .Replace("{token.RequestToken}", token.RequestToken);
}
public class Image
{
    public string Id { get; set; }
    public string Title { get; set; }
    public byte[] ImageData { get; set; }
    public string ContentType { get; set; }
}
public class TravelyRepository
{
    private readonly string _connectionString;
    public TravelyRepository(string connectionString)
    {
        _connectionString = connectionString;
    }
    public async Task CreateDatabaseTable()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var createTableCommand = @"
        CREATE TABLE IF NOT EXISTS Images (
            Id TEXT PRIMARY KEY,
            Title TEXT NOT NULL,
            ImageData BLOB NOT NULL,
            ContentType TEXT NOT NULL
        )";

        using var command = new SqliteCommand(createTableCommand, connection);
        await command.ExecuteNonQueryAsync();
    }
    public async Task SaveImage(string id, string title, byte[] imageData, int maxWidth, int maxHeight)
    {
        using (var image = SixLabors.ImageSharp.Image.Load(imageData)) 
        {
            image.Mutate(x => x.Resize(maxWidth, maxHeight));

            using (var outputStream = new MemoryStream())
            {
                image.Save(outputStream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
                imageData = outputStream.ToArray();
            }
        }
        var contentType = GetImageContentType(imageData);
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var query = @"
        INSERT INTO Images (Id, Title, ImageData, ContentType) VALUES (@Id, @Title, @ImageData, @ContentType)";
        await connection.ExecuteAsync(query, new { Id = id, Title = title, ImageData = imageData, ContentType = contentType });
    }
    public async Task<List<Image>> GetLatestImages(int count = 5)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var query = $"SELECT Id, Title FROM Images ORDER BY Id DESC LIMIT {count}";

        return (await connection.QueryAsync<Image>(query)).ToList();
    }
    public async Task<Image> GetImage(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var query = "SELECT Id, Title, ImageData, ContentType FROM Images WHERE Id = @Id";
        return await connection.QueryFirstOrDefaultAsync<Image>(query, new { Id = id });
    }
    private string GetImageContentType(byte[] imageData)
    {
        return "image/jpeg";
    }
}
