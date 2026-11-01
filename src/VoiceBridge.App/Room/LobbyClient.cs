using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using VoiceBridge.Core;
namespace VoiceBridge.App.Room;
public sealed record LobbyRoom(string Id, string Name, string OwnerName, int Count, int Capacity, bool Public, string? OwnerId);
public sealed record RoomMember(string Id, string Name, bool Desktop, bool Connected, bool Microphone, bool Music, string Source);
public static class LobbyClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    public static async Task<LobbyRoom[]> ListAsync(CancellationToken token = default)
    {
        using var json = await Http.GetFromJsonAsync<JsonDocument>(RoomService.DefaultOrigin + "/api/lobby/rooms", token);
        return json?.RootElement.GetProperty("rooms").Deserialize<LobbyRoom[]>(RoomCipher.JsonOptions) ?? [];
    }
    public static async Task<RoomInvite> ResolveAsync(string id, CancellationToken token = default, string? origin = null)
    {
        if (!RoomCipher.IsHex(id, 32)) throw new ArgumentException("房间编号无效");
        origin ??= RoomService.DefaultOrigin;
        using var response = await Http.PostAsync(origin + "/api/lobby/rooms/" + id + "/join", new StringContent("{}"), token);
        if ((int)response.StatusCode == 404) throw new InvalidOperationException("房间已关闭或不再公开");
        if ((int)response.StatusCode == 409) throw new InvalidOperationException("房间已满，请稍后重试");
        response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); var root = json.RootElement;
        return new(origin, id, root.GetProperty("token").GetString()!, Convert.FromHexString(root.GetProperty("key").GetString()!), true);
    }
    public static string Normalize(string value)
    { if (Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.Scheme=="voicebridge" && uri.Host=="invite") { string invitation=Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')); var parsed=RoomInvite.Parse(invitation); if(parsed.Origin!=RoomService.DefaultOrigin)throw new ArgumentException("软件加入链接的服务器无效");return parsed.Link; } return value; }
    public static string? PublicId(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == "voicebridge" && uri.Host == "join" && RoomCipher.IsHex(uri.AbsolutePath.Trim('/'), 32) && uri.Query.Length == 0 && uri.Fragment.Length == 0) return uri.AbsolutePath.Trim('/');
        if (uri.GetLeftPart(UriPartial.Authority) != RoomService.DefaultOrigin || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path.Length == 2 && path[0] == "rooms" && RoomCipher.IsHex(path[1], 32) ? path[1] : null;
    }
}
