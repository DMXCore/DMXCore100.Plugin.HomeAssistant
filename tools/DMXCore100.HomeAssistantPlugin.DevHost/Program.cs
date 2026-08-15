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

            case "ha":
                if (parts.Length < 3)
                {
                    Console.WriteLine("usage: ha <url> <token>   e.g. ha http://homeassistant.local:8123 TOKEN");
                    break;
                }
                host.SetSetting("ha-url", parts[1]);
                host.SetSetting("ha-token", parts[2]);
                await host.TriggerSettingsChangedAsync();
                Console.WriteLine($"  {host.ConnectionDetail}");
                break;

            case "scenes":
                IReadOnlyList<HomeAssistantScene> scenes = plugin.DiscoveredScenes;
                if (scenes.Count == 0)
                {
                    Console.WriteLine("  no HA scenes (set url+token with `ha`, or wait for poll)");
                    break;
                }

                foreach (HomeAssistantScene scene in scenes)
                {
                    Console.WriteLine($"  {scene.EntityId}  {scene.Name}");
                }

                break;

            case "activate":
                if (parts.Length < 2)
                {
                    Console.WriteLine("usage: activate <entity_id|name>   e.g. activate scene.movie_night");
                    break;
                }

                string wanted = parts.Length > 2 ? input[("activate ".Length)..] : parts[1];
                HomeAssistantScene? match = plugin.DiscoveredScenes.FirstOrDefault(scene =>
                    string.Equals(scene.EntityId, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(scene.Name, wanted, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    Console.WriteLine($"  no scene matching '{wanted}'");
                    break;
                }

                await host.SimulateMqttMessageAsync(
                    $"dmxcore/{host.DeviceInfo.Serial}/ha-scene/{Discovery.ObjectId(match.EntityId)}/set",
                    "ON");
                Console.WriteLine($"  requested {match.EntityId}");
                break;

            case "poll":
                if (host.PeriodicTasks.Count == 0)
                {
                    Console.WriteLine("  no HA poll scheduled (set url+token with `ha`)");
                    break;
                }

                await host.RunPeriodicTaskAsync(0);
                Console.WriteLine($"  {host.ConnectionDetail}");
                break;

            case "cue":
                if (parts.Length < 2)
                {
                    Console.WriteLine("usage: cue <code>   e.g. cue cue.SUNSET");
                    break;
                }

                await host.SimulateCueStartedAsync(parts[1]);
                break;

            case "stop":
                await host.SimulateCueEndedAsync(parts.Length > 1 ? parts[1] : "cue.SUNSET");
                await host.SimulateEntityStateAsync(new PluginEntityState { Code = "system.nowplaying", Text = "" });
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
          ha <url> <token>     connect to Home Assistant and list scenes
          scenes               list discovered Home Assistant scenes
          activate <id|name>   activate a discovered HA scene
          poll                 run the HA scene poll once
          cue <code>           simulate a cue start (fires a matching HA scene)
          stop [code]          simulate cue end / idle now-playing (stop HA scene)
          v [level]            simulate a master dimmer state change (0..1)
          np [text]            simulate a now-playing sensor change
          rm                   remove preset.PARTY from the catalog (ghost cleanup)
          x                    toggle MQTT connection (republish on reconnect)
          d                    dump counters
          q                    quit (runs plugin shutdown)
        """);
}
