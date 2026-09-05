namespace Cua.Hosting;

/// <summary>
/// Loads the nearest .env into the process environment so credential
/// references (env://CUA) resolve the same way for every host — CLI, MCP
/// server, or anything else that runs a capability. Real environment
/// variables always win, so CI and containers override the file.
/// </summary>
public static class DotEnv
{
    public static void Load(int maxDepth = 4)
    {
        var dir = Directory.GetCurrentDirectory();
        for (var depth = 0; dir is not null && depth < maxDepth; depth++, dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, ".env");
            if (!File.Exists(file)) continue;

            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim().Trim('"');
                if (Environment.GetEnvironmentVariable(key) is null)
                    Environment.SetEnvironmentVariable(key, value);
            }
            return; // nearest .env wins
        }
    }
}
