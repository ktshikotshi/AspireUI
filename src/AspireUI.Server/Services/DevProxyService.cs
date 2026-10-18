using System.Text.Json;
using System.Text.RegularExpressions;

namespace AspireUI.Server.Services;

// Makes dev containers reachable off-box via socat sidecars (--network host) binding PublicHost:port.
public class DevProxyService(DeployService deploy)
{
    public const string Image = "alpine/socat";
    public static bool InContainer => File.Exists("/.dockerenv");
    private static string Prefix(string stackId) => $"aspireui-devfwd-{stackId[..Math.Min(8, stackId.Length)]}";

    // (resource-name, loopback port) for running dev containers matched by container name + 127.0.0.1:<port>.
    public List<(string Resource, int Port)> LoopbackPorts(IEnumerable<string> resourceNames) =>
        ParseLoopbackPorts(deploy.Docker(".", "ps --format \"{{json .}}\"").Log, resourceNames);

    // Parse docker ps JSON output → (resource, loopback port) for containers matching resource names.
    public static List<(string Resource, int Port)> ParseLoopbackPorts(string psOutput, IEnumerable<string> resourceNames)
    {
        var names = resourceNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var found = new List<(string, int)>();
        foreach (var line in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonElement e; try { e = JsonDocument.Parse(line).RootElement; } catch { continue; }
            var cname = e.TryGetProperty("Names", out var n) ? n.GetString() ?? "" : "";
            var ports = e.TryGetProperty("Ports", out var p) ? p.GetString() ?? "" : "";
            var res = names.FirstOrDefault(r => cname == r || cname.StartsWith(r + "-", StringComparison.OrdinalIgnoreCase));
            if (res is null) continue;
            var m = Regex.Match(ports, @"127\.0\.0\.1:(\d+)->");
            if (m.Success) found.Add((res, int.Parse(m.Groups[1].Value)));
        }
        return found;
    }

    private HashSet<string> RunningForwarders(string stackId) =>
        deploy.Docker(".", $"ps --format \"{{{{.Names}}}}\" --filter name={Prefix(stackId)}").Log
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    // Ensure socat forwarder for each port; idempotent; true if forwarding active.
    public bool Ensure(string stackId, string? publicHost, IEnumerable<int> ports)
    {
        if (!InContainer || string.IsNullOrWhiteSpace(publicHost)) return false;
        var running = RunningForwarders(stackId);
        foreach (var port in ports.Distinct())
        {
            var name = $"{Prefix(stackId)}-{port}";
            if (running.Contains(name)) continue;
            deploy.Docker(".", $"rm -f {name}");   // clear a dead one with the same name
            deploy.Docker(".", $"run -d --name {name} --network host --restart unless-stopped {Image} " +
                $"TCP-LISTEN:{port},bind={publicHost},fork,reuseaddr TCP:127.0.0.1:{port}");
        }
        return true;
    }

    /// <summary>
    /// A dev run's dashboard listens inside this container, and only the ports the container was
    /// started with reach the host. A socat sidecar on this container's network publishes
    /// <paramref name="port"/> on the host and passes it through. On the host's network nothing is
    /// needed. Returns false when it could not tell where this container is.
    /// </summary>
    public bool ForwardDashboard(string stackId, int port)
    {
        if (!InContainer) return false;
        var self = Environment.MachineName;
        var inspect = deploy.Docker(".", $"inspect --format \"{{{{.HostConfig.NetworkMode}}}}|{{{{range $k, $v := .NetworkSettings.Networks}}}}{{{{$k}}}}={{{{$v.IPAddress}}}};{{{{end}}}}\" {self}");
        if (!inspect.Ok) return false;
        var (network, ip) = ParseSelf(inspect.Log);
        if (network == "host") return true;
        if (network is null || ip is null) return false;
        var name = $"{Prefix(stackId)}-dash";
        deploy.Docker(".", $"rm -f {name}");
        return deploy.Docker(".", $"run -d --name {name} --network {network} -p {port}:{port} --restart unless-stopped {Image} " +
            $"TCP-LISTEN:{port},fork,reuseaddr TCP:{ip}:{port}").Ok;
    }

    /// <summary>`mode|net=ip;net=ip;` from docker inspect → the network to join and this container's address on it.</summary>
    public static (string? Network, string? Ip) ParseSelf(string inspectOutput)
    {
        var line = inspectOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        var parts = line.Split('|', 2);
        if (parts[0] == "host") return ("host", null);
        if (parts.Length < 2) return (null, null);
        foreach (var pair in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[1].Length > 0) return (kv[0], kv[1]);
        }
        return (null, null);
    }

    public void Teardown(string stackId)
    {
        if (!InContainer) return;
        var ids = deploy.Docker(".", $"ps -aq --filter name={Prefix(stackId)}").Log
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids) deploy.Docker(".", $"rm -f {id}");
    }
}
