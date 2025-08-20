using ChatbotApi.Application.Common.Interfaces;
using ChatbotApi.Application.Common.Models;
using ChatbotApi.Application.Common.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Net.Http;
using ChatbotApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Pgvector.EntityFrameworkCore;

namespace ChatbotApi.Infrastructure.Processors.TrackFileProcessor
{
    public class TrackFileProcessor : ILineMessageProcessor, IProcessorCancellationHandler
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ISystemService _systemService;
        private readonly ILogger<TrackFileProcessor> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IApplicationDbContext _context;
        private readonly IOpenAiService _openAiService;

        private const string SessionKeyPrefix = "trackfile_";
        public string Name => ChatbotApi.Domain.Constants.Systems.TrackFile;

        public TrackFileProcessor(
            IMemoryCache memoryCache,
            ISystemService systemService,
            ILogger<TrackFileProcessor> logger,
            IWebHostEnvironment environment,
            IHttpClientFactory httpClientFactory,
            IApplicationDbContext context,
            IOpenAiService openAiService)
        {
            _memoryCache = memoryCache;
            _systemService = systemService;
            _logger = logger;
            _environment = environment;
            _httpClientFactory = httpClientFactory;
            _context = context;
            _openAiService = openAiService;
        }

        public async Task<LineReplyStatus> ProcessLineAsync(LineEvent evt, int chatbotId, string message, string userId,
            string replyToken, CancellationToken cancellationToken = default)
        {
            // Check if user is in the middle of an upload session
            var sessionKey = SessionKeyPrefix + userId;
            if (_memoryCache.TryGetValue(sessionKey, out PendingUpload? pendingUpload))
            {
                // User is expected to provide a description for the file
                var description = message?.Trim();
                if (string.IsNullOrWhiteSpace(description))
                {
                    return new LineReplyStatus
                    {
                        Status = 400,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new List<LineMessage> { new LineTextMessage("กรุณาระบุคำอธิบายไฟล์") }
                        }
                    };
                }

                try
                {
                    // Get chatbot for LLM key
                    var chatbot =
                        await _context.Chatbots.FirstOrDefaultAsync(c => c.Id == chatbotId, cancellationToken);
                    if (chatbot?.LlmKey == null)
                    {
                        return new LineReplyStatus
                        {
                            Status = 500,
                            ReplyMessage = new LineReplyMessage
                            {
                                ReplyToken = replyToken,
                                Messages = new List<LineMessage>
                                {
                                    new LineTextMessage("ระบบยังไม่ได้ตั้งค่า API Key")
                                }
                            }
                        };
                    }

                    // Generate embedding for the description
                    var embedding =
                        await _openAiService.CallEmbeddingsAsync(description, chatbot.LlmKey, cancellationToken);
                    if (embedding == null)
                    {
                        return new LineReplyStatus
                        {
                            Status = 500,
                            ReplyMessage = new LineReplyMessage
                            {
                                ReplyToken = replyToken,
                                Messages = new List<LineMessage>
                                {
                                    new LineTextMessage("ไม่สามารถประมวลผลคำอธิบายได้ กรุณาลองใหม่อีกครั้ง")
                                }
                            }
                        };
                    }

                    // Create filename with timestamp
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var extension = MimeTypes.GetMimeTypeExtensions(pendingUpload?.FileExtension ?? string.Empty).LastOrDefault() ??
                                    "bin";
                    var fileName = $"{timestamp}_{userId}_{description.MakeValidFileName()}.{extension}";

                    // Create file path
                    var userDir = Path.Combine(_environment.ContentRootPath, "storage", userId);
                    if (!Directory.Exists(userDir))
                        Directory.CreateDirectory(userDir);
                    var filePath = Path.Combine(userDir, fileName);

                    // Save file to disk
                    await File.WriteAllBytesAsync(filePath, pendingUpload?.FileContent!, cancellationToken);

                    // Calculate file hash
                    var fileHash = CalculateFileHash(pendingUpload?.FileContent ?? Array.Empty<byte>());

                    // Save file information to database with embedding
                    var trackFile = new TrackFile
                    {
                        FileName = fileName,
                        FilePath = filePath,
                        ContentType = pendingUpload?.FileExtension ?? "application/octet-stream",
                        FileSize = pendingUpload?.FileContent?.Length ?? 0,
                        UserId = userId,
                        Description = description,
                        Embedding = embedding,
                        FileHash = fileHash,
                        ChatbotId = chatbotId
                    };

                    await _context.TrackFiles.AddAsync(trackFile, cancellationToken);
                    await _context.SaveChangesAsync(cancellationToken);

                    _memoryCache.Remove(sessionKey);

                    var fileUrl = $"{_systemService.FullHostName}/information/getfile?id={trackFile.Id}";
                    return new LineReplyStatus
                    {
                        Status = 200,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new List<LineMessage>
                            {
                                new LineTextMessage(
                                    $"บันทึกไฟล์ '{description}' เรียบร้อยแล้ว ดาวน์โหลด: {fileUrl}")
                            }
                        }
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save file for user {UserId}", userId);
                    return new LineReplyStatus
                    {
                        Status = 500,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new List<LineMessage>
                            {
                                new LineTextMessage("ไม่สามารถบันทึกไฟล์ได้ กรุณาลองใหม่อีกครั้ง")
                            }
                        }
                    };
                }
            }

            // Handle "obtain ..." command with vector search
            if (message != null && message.StartsWith("obtain ", StringComparison.OrdinalIgnoreCase))
            {
                var searchQuery = message.Substring(7).Trim();

                try
                {
                    // Get chatbot for LLM key
                    var chatbot =
                        await _context.Chatbots.FirstOrDefaultAsync(c => c.Id == chatbotId, cancellationToken);
                    if (chatbot?.LlmKey == null)
                    {
                        return new LineReplyStatus
                        {
                            Status = 500,
                            ReplyMessage = new LineReplyMessage
                            {
                                ReplyToken = replyToken,
                                Messages = new List<LineMessage>
                                {
                                    new LineTextMessage("ระบบยังไม่ได้ตั้งค่า API Key")
                                }
                            }
                        };
                    }

                    // Generate embedding for search query
                    var searchEmbedding =
                        await _openAiService.CallEmbeddingsAsync(searchQuery, chatbot.LlmKey, cancellationToken);
                    if (searchEmbedding == null)
                    {
                        return new LineReplyStatus
                        {
                            Status = 500,
                            ReplyMessage = new LineReplyMessage
                            {
                                ReplyToken = replyToken,
                                Messages = new List<LineMessage>
                                {
                                    new LineTextMessage("ไม่สามารถค้นหาได้ กรุณาลองใหม่อีกครั้ง")
                                }
                            }
                        };
                    }

                    // Search for closest files using vector similarity
                    var maxDistance = chatbot.MaximumDistance ?? 2.0;
                    var topK = chatbot.TopKDocument ?? 5;

                    var closestFiles = await _context.TrackFiles
                        .Where(f => f.UserId == userId && f.Embedding != null)
                        .Select(f => new { File = f, Distance = f.Embedding!.L2Distance(searchEmbedding) })
                        .Where(f => f.Distance <= maxDistance)
                        .OrderBy(f => f.Distance)
                        .Take(topK)
                        .ToListAsync(cancellationToken);

                    if (closestFiles.Count == 0)
                    {
                        return new LineReplyStatus
                        {
                            Status = 404,
                            ReplyMessage = new LineReplyMessage
                            {
                                ReplyToken = replyToken,
                                Messages = new List<LineMessage>
                                {
                                    new LineTextMessage($"ไม่พบไฟล์ที่ตรงกับ '{searchQuery}'")
                                }
                            }
                        };
                    }

                    // Return the best match
                    var bestMatch = closestFiles.First();
                    var fileUrl = $"{_systemService.FullHostName}/information/getfile?id={bestMatch.File.Id}";

                    string responseMessage;
                    if (closestFiles.Count == 1)
                    {
                        responseMessage = $"พบไฟล์: {bestMatch.File.Description}\nดาวน์โหลด: {fileUrl}";
                    }
                    else
                    {
                        responseMessage =
                            $"พบไฟล์ที่เกี่ยวข้อง {closestFiles.Count} ไฟล์\nไฟล์ที่ตรงที่สุด: {bestMatch.File.Description}\nดาวน์โหลด: {fileUrl}";
                    }

                    return new LineReplyStatus
                    {
                        Status = 200,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new List<LineMessage> { new LineTextMessage(responseMessage) }
                        }
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to search files for user {UserId} with query {Query}", userId,
                        searchQuery);
                    return new LineReplyStatus
                    {
                        Status = 500,
                        ReplyMessage = new LineReplyMessage
                        {
                            ReplyToken = replyToken,
                            Messages = new List<LineMessage>
                            {
                                new LineTextMessage("เกิดข้อผิดพลาดในการค้นหา กรุณาลองใหม่อีกครั้ง")
                            }
                        }
                    };
                }
            }

            // Default: ask user to upload a file
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage> { new LineTextMessage("กรุณาส่งไฟล์ที่ต้องการอัปโหลด") }
                }
            };
        }

        public async Task<LineReplyStatus> ProcessLineImageAsync(LineEvent evt, int chatbotId, string messageId,
            string userId, string replyToken, string accessToken, CancellationToken cancellationToken = default)
        {
            // This method should be called when a file is sent from LINE
            // For this example, assume we have a way to get the file bytes from messageId and accessToken
            var (fileContent, contentType) = await TryDownloadLineFileAsync(messageId, accessToken, cancellationToken);
            if (fileContent == null)
            {
                return new LineReplyStatus
                {
                    Status = 400,
                    ReplyMessage = new LineReplyMessage
                    {
                        ReplyToken = replyToken,
                        Messages = new List<LineMessage>
                        {
                            new LineTextMessage("Failed to download file from LINE.")
                        }
                    }
                };
            }

            // Store file content in cache, wait for user to provide description
            var sessionKey = SessionKeyPrefix + userId;
            _memoryCache.Set(sessionKey, new PendingUpload { FileContent = fileContent, FileExtension = contentType },
                TimeSpan.FromMinutes(10));
            return new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage> { new LineTextMessage("กรุณาระบุคำอธิบายไฟล์") }
                }
            };
        }

        // Not used for now
        // If user sends multiple images, ask them to send one by one in Thai
        public Task<LineReplyStatus> ProcessLineImagesAsync(LineEvent mainEvent, int chatbotId,
            List<LineEvent> imageEvents, string userId, string replyToken, string accessToken,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LineReplyStatus
            {
                Status = 200,
                ReplyMessage = new LineReplyMessage
                {
                    ReplyToken = replyToken,
                    Messages = new List<LineMessage> { new LineTextMessage("กรุณาส่งไฟล์ทีละไฟล์เท่านั้น") }
                }
            });
        }

        public Task<bool> PostProcessLineAsync(string role, string? sourceMessageId, LineSendResponse response,
            bool isForce = false, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        // Updated: Use ReceiptProcessor's GetContentAsync pattern for downloading file from LINE
        private async Task<(byte[]?, string?)> TryDownloadLineFileAsync(string messageId, string accessToken,
            CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("resilient_nocompress");
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                var url = $"https://api-data.line.me/v2/bot/message/{messageId}/content";
                var response = await client.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    string errorDetail = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError(
                        "Failed to get content from LINE API for messageId {MessageId}. Status: {StatusCode}. Body: {Body}",
                        messageId, response.StatusCode, errorDetail);
                    return (null, null);
                }

                byte[] contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                string? contentType = response.Content.Headers.ContentType?.MediaType;

                if (string.IsNullOrEmpty(contentType) || contentBytes.Length == 0)
                {
                    _logger.LogWarning("LINE API returned empty content or no content type for messageId: {MessageId}",
                        messageId);
                    return (null, null);
                }

                return (contentBytes, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while downloading file from LINE API");
                return (null, null);
            }
        }

        private static string CalculateFileHash(byte[] fileContent)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(fileContent);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private class PendingUpload
        {
            public byte[]? FileContent { get; set; }
            public string? FileExtension { get; set; }
        }
        
        /// <summary>
        /// Cancels any ongoing operations for a specific user by removing session data
        /// </summary>
        /// <param name="userId">The user ID for which to cancel operations</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>A task representing the asynchronous operation</returns>
        public async Task CancelOperationsAsync(string userId, CancellationToken cancellationToken = default)
        {
            try
            {
                // Remove pending upload from memory cache
                var sessionKey = SessionKeyPrefix + userId;
                _memoryCache.Remove(sessionKey);
                
                _logger.LogInformation("Cancelled TrackFile operations for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling TrackFile operations for user {UserId}", userId);
            }
        }
    }
}
