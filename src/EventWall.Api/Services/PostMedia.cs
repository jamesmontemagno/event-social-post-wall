using System.Text.Json;
using EventWall.Api.Models;

namespace EventWall.Api.Services;

public static class PostMedia
{
    public static IReadOnlyList<PostImage> BlueskyRecordImages(JsonElement record, string did)
    {
        if (!record.TryGetProperty("embed", out var embed) || embed.ValueKind != JsonValueKind.Object) return [];
        if (embed.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object) embed = media;
        if (!embed.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return [];

        var result = new List<PostImage>();
        foreach (var image in images.EnumerateArray().Take(4))
        {
            if (image.ValueKind != JsonValueKind.Object ||
                !image.TryGetProperty("image", out var blob) ||
                blob.ValueKind != JsonValueKind.Object ||
                !blob.TryGetProperty("ref", out var reference) ||
                reference.ValueKind != JsonValueKind.Object ||
                !reference.TryGetProperty("$link", out var cidValue) ||
                cidValue.ValueKind != JsonValueKind.String ||
                !blob.TryGetProperty("mimeType", out var mimeTypeValue) ||
                mimeTypeValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var extension = mimeTypeValue.GetString() switch
            {
                "image/jpeg" => "jpeg",
                "image/png" => "png",
                "image/webp" => "webp",
                _ => null
            };
            if (extension is null) continue;

            var cid = cidValue.GetString();
            if (string.IsNullOrWhiteSpace(cid)) continue;
            var url = $"https://cdn.bsky.app/img/feed_fullsize/plain/{Uri.EscapeDataString(did)}/{Uri.EscapeDataString(cid)}@{extension}";
            var alt = image.TryGetProperty("alt", out var altValue) && altValue.ValueKind == JsonValueKind.String
                ? altValue.GetString()
                : null;
            result.Add(new PostImage(url, alt));
        }

        return result;
    }

    public static IReadOnlyList<PostImage> BlueskyImages(JsonElement post)
    {
        if (!post.TryGetProperty("embed", out var embed)) return [];
        if (embed.TryGetProperty("media", out var media)) embed = media;
        if (!embed.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return [];
        return images.EnumerateArray()
            .Where(image => image.TryGetProperty("fullsize", out var url) && url.ValueKind == JsonValueKind.String)
            .Select(image => new PostImage(image.GetProperty("fullsize").GetString()!,
                image.TryGetProperty("alt", out var alt) && alt.ValueKind == JsonValueKind.String ? alt.GetString() : null))
            .Take(4).ToList();
    }

    public static IReadOnlyList<PostImage> XImages(JsonElement post, JsonElement root)
    {
        if (!post.TryGetProperty("attachments", out var attachments) ||
            !attachments.TryGetProperty("media_keys", out var keys) ||
            !root.TryGetProperty("includes", out var includes) ||
            !includes.TryGetProperty("media", out var media) ||
            keys.ValueKind != JsonValueKind.Array || media.ValueKind != JsonValueKind.Array) return [];
        var attached = keys.EnumerateArray().Where(key => key.ValueKind == JsonValueKind.String)
            .Select(key => key.GetString()).ToHashSet();
        return media.EnumerateArray().Where(item =>
                item.TryGetProperty("media_key", out var key) && attached.Contains(key.GetString()) &&
                item.TryGetProperty("type", out var type) && type.GetString() == "photo" &&
                item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            .Select(item => new PostImage(item.GetProperty("url").GetString()!,
                item.TryGetProperty("alt_text", out var alt) && alt.ValueKind == JsonValueKind.String ? alt.GetString() : null))
            .Take(4).ToList();
    }
}
