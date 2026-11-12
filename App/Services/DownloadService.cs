using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
namespace HonorControl;
public sealed record DownloadProgress(long Completed, long Total, double BytesPerSecond, int Connections, int NetworkChannels);
public sealed record DownloadResult(long Bytes, int Connections, int NetworkChannels, bool UsedRanges);
public static class DownloadService
{
    public static int AvailableChannels => Channels().Count;
    private static List<IPAddress> Channels() => NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback).SelectMany(x => { try { var info = x.GetIPProperties(); return info.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) ? info.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address) : []; } catch { return []; } }).Distinct().ToList();
    private static HttpClient Client(IPAddress? local = null)
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(15), MaxConnectionsPerServer = 8 };
        if (local is not null)
            handler.ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                Exception? last = null;
                foreach (var address in addresses.Where(x => x.AddressFamily == local.AddressFamily))
                {
                    var socket = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { socket.Bind(new IPEndPoint(local, 0)); await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, true); }
                    catch (Exception ex) { socket.Dispose(); last = ex; }
                }
                throw last ?? new IOException(L.T("此网络通道没有可用地址。"));
            };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity"); return client;
    }
    public static async Task<DownloadResult> DownloadAsync(Uri uri, string destination, bool multipleNetworks, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        if (uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException(L.T("请使用不含账号密码的 HTTP 或 HTTPS 下载链接。"));
        string target = Path.GetFullPath(destination), part = target + "." + Guid.NewGuid().ToString("N") + ".honorcontrol.part";
        using var defaultClient = Client();
        using var probeRequest = new HttpRequestMessage(HttpMethod.Get, uri); probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
        using var probe = await defaultClient.SendAsync(probeRequest, HttpCompletionOption.ResponseHeadersRead, cancellation); probe.EnsureSuccessStatusCode();
        bool ranges = probe.StatusCode == HttpStatusCode.PartialContent && probe.Content.Headers.ContentRange is { From: 0, To: 0, Length: > 0 };
        long total = ranges ? probe.Content.Headers.ContentRange!.Length!.Value : probe.Content.Headers.ContentLength ?? -1;
        bool hasValidator = probe.Headers.ETag is { IsWeak: false } || probe.Content.Headers.LastModified.HasValue;
        int count = ranges && hasValidator && total >= 1024 * 1024 ? 4 : 1;
        var channels = multipleNetworks ? Channels() : [];
        if (channels.Count < 2) channels.Clear();
        var clock = Stopwatch.StartNew(); long completed = 0, lastReport = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        void Report(int connections, int networks)
        {
            long now = clock.ElapsedMilliseconds;
            if (now - Interlocked.Read(ref lastReport) < 150 && Interlocked.Read(ref completed) != total) return;
            Interlocked.Exchange(ref lastReport, now);
            long bytes = Interlocked.Read(ref completed);
            progress?.Report(new(bytes, total, bytes / Math.Max(.001, clock.Elapsed.TotalSeconds), connections, networks));
        }
        try
        {
            using (SafeFileHandle file = File.OpenHandle(part, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous))
            {
                if (ranges)
                {
                    var tasks = Enumerable.Range(0, count).Select(async index =>
                    {
                        try
                        {
                            long start = total / count * index, end = index == count - 1 ? total - 1 : total / count * (index + 1) - 1;
                            using var client = Client(channels.Count > 0 ? channels[index % channels.Count] : null);
                            using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.Range = new RangeHeaderValue(start, end);
                            if (probe.Headers.ETag is { IsWeak: false } tag) request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                            else if (probe.Content.Headers.LastModified is { } modified) request.Headers.IfRange = new RangeConditionHeaderValue(modified);
                            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token); response.EnsureSuccessStatusCode();
                            var range = response.Content.Headers.ContentRange;
                            if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.To != end || range.Length != total) throw new IOException(L.T("服务器分段响应不一致，已停止下载以避免文件损坏。"));
                            if (probe.Headers.ETag is { } expected && response.Headers.ETag is { } actual && expected.ToString() != actual.ToString()) throw new IOException(L.T("下载期间文件版本变化，请重试。"));
                            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token); byte[] buffer = new byte[128 * 1024]; long offset = start;
                            while (true)
                            {
                                int read = await stream.ReadAsync(buffer, linked.Token); if (read == 0) break;
                                if (offset + read > end + 1) throw new IOException(L.T("分段长度超出预期。"));
                                await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), offset, linked.Token); offset += read; Interlocked.Add(ref completed, read); Report(count, Math.Max(1, Math.Min(channels.Count, count)));
                            }
                            if (offset != end + 1) throw new IOException(L.T("服务器提前结束了分段传输。"));
                        }
                        catch { linked.Cancel(); throw; }
                    }).ToArray();
                    await Task.WhenAll(tasks);
                }
                else
                {
                    using var response = probe.StatusCode == HttpStatusCode.OK
                        ? probe
                        : await defaultClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
                    response.EnsureSuccessStatusCode();
                    total = response.Content.Headers.ContentLength ?? -1;
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellation); byte[] buffer = new byte[128 * 1024]; long offset = 0;
                    while (true) { int read = await stream.ReadAsync(buffer, cancellation); if (read == 0) break; await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), offset, cancellation); offset += read; Interlocked.Add(ref completed, read); Report(1, 1); }
                    if (total >= 0 && offset != total) throw new IOException(L.T("下载长度与服务器声明不一致。"));
                }
                RandomAccess.FlushToDisk(file);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(part, target, true);
            return new(completed, count, Math.Max(1, Math.Min(channels.Count, count)), ranges);
        }
        finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
    }
}
