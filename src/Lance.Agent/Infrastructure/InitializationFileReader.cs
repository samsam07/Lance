namespace Lance.Agent.Infrastructure;

internal static class InitializationFileReader
{
    public static Dictionary<string, string> Read(string path)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in File.ReadLines(path))
        {
            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            string key = line[..equalsIndex].Trim();
            string value = line[(equalsIndex + 1)..].Trim();

            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }
}
