using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure.Storage;

namespace AzureTraining.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;
        private readonly IConfiguration _config;

        public IndexModel(ILogger<IndexModel> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        public void OnGet()
        {
        }

        // Simple sync health check handler
        public IActionResult OnGetCheckHealth()
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            using (var client = new System.Net.Http.HttpClient { BaseAddress = new Uri(baseUrl) })
            {
                try
                {
                    var resp = client.GetAsync("/api/health").Result;
                    var body = resp.Content.ReadAsStringAsync().Result;
                    return new JsonResult(new { status = (int)resp.StatusCode, body });
                }
                catch (Exception ex)
                {
                    return new JsonResult(new { status = 0, error = ex.Message });
                }
            }
        }

        // Simple, synchronous DB reader. Returns list of dictionary rows (no table classes).
        public IActionResult OnGetReadUsers()
        {
            var connStr = _config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connStr))
            {
                return new JsonResult(new { error = "Connection string 'DefaultConnection' not configured." });
            }

            try
            {
                var rows = new List<Dictionary<string, object>>();

                using (var conn = new SqlConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT * FROM USERS";
                        using (var rdr = cmd.ExecuteReader())
                        {
                            while (rdr.Read())
                            {
                                var row = new Dictionary<string, object>();
                                for (int i = 0; i < rdr.FieldCount; i++)
                                {
                                    var name = rdr.GetName(i);
                                    var val = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                                    row[name] = val;
                                }
                                rows.Add(row);
                            }
                        }
                    }
                }

                return new JsonResult(new { count = rows.Count, items = rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnGetReadUsers failed");
                return new JsonResult(new { error = ex.Message });
            }
        }

        // Return a short-lived SAS URL for a private blob (keeps client-side simple)
        public IActionResult OnGetGetImageSas(string blobName)
        {
            try
            {
                var accountName = _config["StorageAccountName"];
                var accountKey = _config["StorageAccountKey"];
                var container = _config["BlobContainer"];
                if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(accountKey) || string.IsNullOrWhiteSpace(container))
                {
                    return new JsonResult(new { error = "StorageAccountName, StorageAccountKey or BlobContainer not configured." });
                }

                if (string.IsNullOrWhiteSpace(blobName))
                {
                    blobName = _config["DefaultBlobName"] ?? "sample.jpg";
                }

                var blobUri = new Uri($"https://{accountName}.blob.core.windows.net/{container}/{Uri.EscapeDataString(blobName)}");

                var sasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = container,
                    BlobName = blobName,
                    Resource = "b",
                    ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(10) // short-lived
                };
                sasBuilder.SetPermissions(BlobSasPermissions.Read);

                var credential = new StorageSharedKeyCredential(accountName, accountKey);
                var sasQuery = sasBuilder.ToSasQueryParameters(credential).ToString();

                var uriWithSas = new UriBuilder(blobUri) { Query = sasQuery }.ToString();
                return new JsonResult(new { url = uriWithSas });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnGetGetImageSas failed");
                return new JsonResult(new { error = ex.Message });
            }
        }

        // Call an Azure Function HTTP endpoint with POST parameters orderId and status.
        // This handler accepts both form-encoded and JSON bodies.
        public async Task<IActionResult> OnPostCallFunction()
        {
            string orderId = string.Empty;
            string status = string.Empty;

            try
            {
                // Prefer form data if present (keeps backward compatibility)
                if (Request.HasFormContentType)
                {
                    orderId = Request.Form["orderId"];
                    status = Request.Form["status"];
                }
                else
                {
                    // Async read of request body to avoid InvalidOperationException
                    using (var sr = new StreamReader(Request.Body))
                    {
                        var body = await sr.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(body);
                                var root = doc.RootElement;
                                if (root.TryGetProperty("orderId", out var o)) orderId = o.GetString() ?? string.Empty;
                                if (root.TryGetProperty("status", out var s)) status = s.GetString() ?? string.Empty;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to parse JSON body for OnPostCallFunction");
                            }
                        }
                    }
                }

                var funcUrl = _config["AzureFunctionUrl"];

                using (var client = new HttpClient())
                {
                    var payload = new { orderId = orderId, status = status };
                    var response = await client.PostAsync(funcUrl, new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"));

                    var respBody = await response.Content.ReadAsStringAsync();
                    return new JsonResult(new { status = (int)response.StatusCode, body = respBody });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnPostCallFunction failed");
                return new JsonResult(new { error = ex.Message });
            }
        }
    }
}
