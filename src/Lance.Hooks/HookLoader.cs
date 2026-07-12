using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lance.Hooks;

// Loads and parses hook files from a set of references, skipping inactive or
// unreadable ones and recording each file's directory and load order. Assembling the
// reference list (client `--hook` + config, agent config) is each side's job.
public sealed class HookLoader
{
    private readonly ILogger<HookLoader> _logger;

    public HookLoader(ILogger<HookLoader> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<LoadedHook> Load(IEnumerable<HookFileRef> references)
    {
        List<LoadedHook> loaded = [];
        int loadOrder = 0;
        foreach (HookFileRef reference in references)
        {
            if (!reference.Active)
            {
                continue;
            }

            HookFile? file = TryParse(reference.Path);
            if (file is null)
            {
                continue;
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(reference.Path)) ?? ".";
            loaded.Add(new LoadedHook { File = file, Directory = directory, LoadOrder = loadOrder });
            loadOrder++;
        }

        return loaded;
    }

    private HookFile? TryParse(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            HookFileDto? dto = JsonSerializer.Deserialize(json, HookJsonContext.Default.HookFileDto);
            return dto is null ? null : Map(dto);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Skipping a hook file that could not be read: {Path}.", path);
            return null;
        }
    }

    // Map the raw JSON shape to the clean domain model, applying SPEC defaults for any
    // property the file omitted (STJ leaves those at the CLR default, not the C# one).
    private HookFile Map(HookFileDto dto)
    {
        Dictionary<string, HookEventDefinition> events = [];
        if (dto.Events is not null)
        {
            foreach (KeyValuePair<string, HookEventDto> entry in dto.Events)
            {
                events[entry.Key] = new HookEventDefinition
                {
                    Priority = entry.Value.Priority ?? 1000,
                    Commands = MapCommands(entry.Value.Commands)
                };
            }
        }

        return new HookFile { Name = dto.Name, Events = events };
    }

    private HookCommand[] MapCommands(HookCommandDto[]? commands)
    {
        if (commands is null)
        {
            return [];
        }

        List<HookCommand> mapped = [];
        foreach (HookCommandDto command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.Command))
            {
                _logger.LogWarning("Skipping a hook command with no 'command' value.");
                continue;
            }

            mapped.Add(new HookCommand
            {
                Command = command.Command,
                Args = command.Args ?? [],
                Async = command.Async ?? false,
                OnError = command.OnError ?? "terminate",
                TimeoutSeconds = command.TimeoutSeconds ?? 30,
                WorkingDir = command.WorkingDir
            });
        }

        return mapped.ToArray();
    }
}
