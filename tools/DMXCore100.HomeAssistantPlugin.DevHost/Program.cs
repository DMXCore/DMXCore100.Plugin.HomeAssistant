using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using DMXCore100.HomeAssistantPlugin;

// Interactive dev harness: run this project in Visual Studio (F5) to exercise
// the plugin against an in-memory host without a device or a broker. Every
// publish the plugin makes (discovery configs, states) is echoed to the
// console, and HA-side behavior (birth message, commands) can be simulated.

var plugin = new HomeAssistantPlugin();
var host = new TestPluginHost(plugin.Info);

host.EntityCatalog.Add(new PluginEntity { Code = "preset.PARTY", Name = "Party Mode", Kind = PluginEntityKind.Scene });
host.EntityCatalog.Add(new PluginEntity { Code = "cue.SUNSET", Name = "Sunset Show", Kind = PluginEntityKind.Scene });
host.EntityCatalog.Add(new PluginEntity { Code = "system.masterdimmer", Name = "Master Dimmer", Kind = PluginEntityKind.Level });
host.EntityCatalog.Add(new PluginEntity { Code = "system.mute", Name = "Audio Mute", Kind = PluginEntityKind.Switch });
host.EntityCatalog.Add(new PluginEntity { Code = "schedule.EVENING", Name = "Evening Schedule", Kind = PluginEntityKind.Switch });
host.EntityCatalog.Add(new PluginEntity { Code = "zone.BAR", Name = "Bar", Kind = PluginEntityKind.Level });
host.EntityCatalog.Add(new PluginEntity { Code = "cv.SRC1", Name = "Bar Source", Kind = PluginEntityKind.Select, Choices = ["Spotify", "Mic", "Aux"] });
host.EntityCatalog.Add(new PluginEntity { Code = "system.nowplaying", Name = "Now Playing", Kind = PluginEntityKind.Sensor });
host.EntityCatalog.Add(new PluginEntity { Code = "system.stop", Name = "Stop Playback", Kind = PluginEntityKind.Button });

host.EntityStates["system.masterdimmer"] = new PluginEntityState { Code = "system.masterdimmer", Level = 1.0 };
host.EntityStates["system.mute"] = new PluginEntityState { Code = "system.mute", IsOn = false };

// Core->HA direction: point at a real Home Assistant to try the action
// provider (ha <url> <token>), then `targets` / `fire <entity_id>`
if (args.Length >= 2)
{
    host.SetSetting("ha-url", args[0]);
    host.SetSetting("ha-token", args[1]);
}

Console.WriteLine($"=== {plugin.Info.Name} {plugin.Info.Version} dev host ===");
Console.WriteLine();

await plugin.InitializeAsync(host, CancellationToken.None);

PrintHelp();

bool running = true;
while (running)
{
    Console.Write("> ");
    string? input = Console.ReadLine()?.Trim();
    if (input == null)
        break;

    try
    {
        string[] parts = input.Split(' ', 3);

        switch (parts[0].ToLowerInvariant())
        {
            case "b":
                await host.SimulateMqttMessageAsync("homeassistant/status", "online");
                break;

            case "cmd":
                if (parts.Length < 3)
                {
                    Console.WriteLine("usage: cmd <objectId> <payload>   e.g. cmd preset_party ON");

                    break;
                }
                await host.SimulateMqttMessageAsync($"dmxcore/{host.DeviceInfo.Serial}/{parts[1]}/set", parts[2]);
                break;

            case "v":
                double level = parts.Length > 1 && double.TryParse(parts[1], out double parsed) ? parsed : 0.5;
                await host.SimulateEntityStateAsync(new PluginEntityState { Code = "system.masterdimmer", Level = level });
                break;

            case "np":
                await host.SimulateEntityStateAsync(new PluginEntityState { Code = "system.nowplaying", Text = parts.Length > 1 ? input[3..] : "Cue: Sunset Show" });
                break;

            case "rm":
                host.EntityCatalog.RemoveAll(x => x.Code == "preset.PARTY");
                await host.SimulateEntityCatalogChangedAsync();
                break;

            case "x":
                await host.SimulateMqttConnectionChangedAsync(!host.MqttConnected);
                Console.WriteLine($"  MQTT connected: {host.MqttConnected}");
                break;

            case "ha":
                if (parts.Length < 3)
                {
                    Console.WriteLine("usage: ha <url> <token>   e.g. ha http://homeassistant.local:8123 eyJ...");

                    break;
                }
                host.SetSetting("ha-url", parts[1]);
                host.SetSetting("ha-token", parts[2]);
                await host.TriggerSettingsChangedAsync();
                Console.WriteLine($"  provider registered: {host.ActionProvider != null}; {host.ConnectionDetail}");
                break;

            case "targets":
                if (host.ActionProvider == null)
                {
                    Console.WriteLine("  no action provider (set ha <url> <token> first)");

                    break;
                }
                foreach (var target in await host.ActionProvider.GetTargetsAsync(CancellationToken.None))
                    Console.WriteLine($"  {target.Group,-12} {target.Id,-40} {target.Label}");
                break;

            case "fire":
                if (host.ActionProvider == null || parts.Length < 2)
                {
                    Console.WriteLine("usage: fire <entity_id> [json payload]   (needs ha <url> <token> first)");

                    break;
                }
                await host.ActionProvider.ExecuteAsync(parts[1], parts.Length > 2 ? parts[2] : null, CancellationToken.None);
                Console.WriteLine($"  fired {parts[1]}; {host.ConnectionDetail}");
                break;

            case "d":
                Console.WriteLine($"  published:   {host.PublishedMessages.Count} messages");
                Console.WriteLine($"  entity cmds: {string.Join(", ", host.ExecutedEntityCommands.Select(x => $"{x.EntityCode}:{x.Command.Type}"))}");
                break;

            case "q":
                running = false;
                break;

            case "?":
            case "help":
                PrintHelp();
                break;

            default:
                if (input.Length > 0)
                    Console.WriteLine("unknown command, ? for help");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  !! {ex.GetType().Name}: {ex.Message}");
    }
}

await plugin.ShutdownAsync(CancellationToken.None);
Console.WriteLine("shut down cleanly");

static void PrintHelp()
{
    Console.WriteLine("""
        Commands:
          b                    simulate the HA birth message (republish)
          cmd <objectId> <p>   simulate an HA command (cmd preset_party ON,
                               cmd system_masterdimmer 40, cmd cv_src1 Mic)
          v [level]            simulate a master dimmer state change (0..1)
          np [text]            simulate a now-playing sensor change
          rm                   remove preset.PARTY from the catalog (ghost cleanup)
          x                    toggle MQTT connection (republish on reconnect)
          ha <url> <token>     configure the HA REST API (Core->HA direction)
          targets              list HA scenes/scripts/automations via the provider
          fire <entity_id> [j] fire one (optional JSON payload merged into service data)
          d                    dump counters
          q                    quit (runs plugin shutdown)
        """);
}
