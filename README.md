# DMX Core 100 — Home Assistant Plugin

Two-way Home Assistant integration:

1. **The Core appears in Home Assistant** — presets, cues, dimmers, and
   switches are published with
   [MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery).
   No custom component and no YAML. openHAB, ioBroker, and Domoticz consume
   the same discovery format.
2. **Home Assistant scenes appear on the Core** — the plugin lists HA
   `scene.*` entities and can fire them when you start a matching cue or
   preset, when playback stops, or via MQTT.

## User guide

*(Source for the docs.dmxcore.com "Home Assistant" section.)*

### What you get in Home Assistant

Once MQTT discovery is connected, Home Assistant shows one **DMX Core 100
device** with:

| In Home Assistant | From the DMX Core | What you can do |
|---|---|---|
| Scenes | Presets, cues, timelines | Activate from dashboards, automations, scripts, voice assistants |
| Number sliders (%) | Master dimmer, zone intensities, level Control Values, audio volume | Dim from a dashboard slider or an automation |
| Switches | Audio mute, output mute, schedules, toggle Control Values | Flip on/off; enable or disable schedules remotely |
| Selects | Selector Control Values (e.g. audio source) | Pick a source from a dropdown |
| Buttons | Stop playback | One-tap stop |
| Sensors | Now playing | Show the running cue; trigger automations when playback changes |

Everything updates live in both directions: move a fader on the device and
the HA slider follows; change it in HA and the device follows. If the device
goes offline (or this plugin is disabled) the entities show as *unavailable*.

### What you get on the Core

With a Home Assistant URL and long-lived access token, the plugin polls HA
every 30 seconds and keeps a list of scenes. The Plugins page shows how
many were found (for example `12 HA scenes`).

You can fire those scenes from the Core:

- **Name match** (on by default) — start a cue or preset whose **display
  name** matches the HA scene. A Core preset called Movie Night fires HA
  `scene.movie_night`. The Core shortcode does not have to match. Cues are
  matched when they start; presets are matched from Now Playing as well.
- **When playback stops** — optional. Set **HA scene when playback stops**
  to a friendly name or entity id (for example `All Off` or
  `scene.all_off`). Fired after a short settle so a cue that ends into
  another cue does not flash the stop look first. Independent of name-match
  on cue start.
- **MQTT** — publish `ON` to
  `dmxcore/{serial}/ha-scene/{objectId}/set`
  (`scene.movie_night` → object id `scene_movie_night`). Use a Core MQTT
  output event if a dashboard or schedule should fire it.

### Setup: Core entities in Home Assistant

Prerequisites: an MQTT broker that both Home Assistant and the DMX Core can
reach, and Home Assistant's **MQTT integration** connected to it
(Settings → Devices & Services → Add Integration → MQTT). Most HA
installs use the Mosquitto add-on.

On the DMX Core:

1. Web UI → **Settings → Remote Control**: enable the external MQTT server
   and enter the broker's address, port, and credentials.
2. **Settings → Plugins**: make sure the Home Assistant plugin is enabled
   (it is by default). Optional: discovery prefix (leave at `homeassistant`
   unless you changed it in HA) and the expose settings below.

Within a few seconds the device appears in Home Assistant under
Settings → Devices & Services → MQTT.

#### Choosing which looks appear in Home Assistant

By default every preset, cue, and timeline is published as an HA scene.
Use **Settings → Plugins → Home Assistant**:

1. Turn **Expose presets, cues, and timelines** off to hide the whole
   category (schedules, zones, Control Values, and system entities have
   their own toggles).
2. Leave it on and fill **Only these presets, cues, and timelines** to
   publish a subset. Separate entries with commas or new lines. Each
   entry can be a display name, a full code, or a shortcode:

   ```
   Movie Night, preset.PARTY
   cue.SUNSET
   ```

   Leave the list empty to publish all looks. Names are case-insensitive.
   `PARTY` matches `preset.PARTY`; `cue.SUNSET` does not match
   `preset.SUNSET`. Looks you remove from the list (or the catalog) are
   unpublished on the next settings or catalog change.

#### Optional: a separate Home Assistant MQTT broker

The Core has one shared MQTT connection (Remote Control). If that broker
**is** Home Assistant's Mosquitto, you are done — leave the plugin MQTT
host empty.

If the Core already uses a **different** broker (lighting, DSP, etc.) and
HA has its own Mosquitto, fill in the plugin settings:

| Setting | Typical value |
|---|---|
| Home Assistant MQTT broker | `homeassistant.local` (or the HA host IP) |
| Home Assistant MQTT port | `1883` (`8883` if you enable TLS) |
| Home Assistant MQTT username / password | Mosquitto add-on user |
| Home Assistant MQTT TLS | off unless the broker requires it |

Discovery, state, commands, and availability are then published to **both**
brokers. Do not point this at the same server as Remote Control MQTT — that
would open two connections to one broker.

### Setup: Home Assistant scenes on the Core

1. In Home Assistant, create a **long-lived access token** (user profile →
   Long-Lived Access Tokens).
2. On the Core, **Settings → Plugins → Home Assistant**:
   - **Home Assistant URL** — `http://homeassistant.local:8123`. Leave empty
     to pick up a server advertised on the LAN via mDNS.
   - **Long-lived access token** — paste the token.
   - **Ignore TLS certificate errors** — only if you use HTTPS with a
     self-signed certificate.
   - **Activate matching HA scenes when a cue starts** — on by default.
   - **HA scene when playback stops** — optional look to restore when the
     Core is idle (friendly name or `scene.*` id).
3. Confirm the Plugins page reports HA scenes, not `HA unreachable`.

Name matching uses the HA friendly name and the usual `scene.*` slug. Spaces
become underscores: "Movie Night" matches `scene.movie_night`.

### Plugin settings

| Setting | Purpose |
|---|---|
| Home Assistant URL | REST/Web API for listing and activating HA scenes |
| Long-lived access token | Required for scene control |
| Ignore TLS certificate errors | HTTPS with an untrusted cert |
| Home Assistant MQTT broker / port / user / password / TLS | Optional second broker for discovery |
| Activate matching HA scenes when a cue starts | Name-match playback to HA scenes |
| HA scene when playback stops | Scene to activate when cues end / Now Playing is idle |
| Discovery prefix | MQTT discovery prefix (`homeassistant` unless you changed it in HA) |
| Expose presets, cues, and timelines | Master switch for publishing Core looks as HA scenes |
| Only these presets, cues, and timelines | Allow-list (names or codes, comma/newline separated). Empty = all looks when the toggle is on |
| Expose schedules / zones / Control Values / system | Per-category toggles |

### Ideas

- **Sunset ambiance:** HA automation at sunset → activate the `Evening`
  preset on the Core (or let the Core's own sunrise/sunset schedules do it
  and just flip the schedule switch from HA when you're on vacation).
- **Movie night:** one HA script that dims living-room lights *and* a Core
  preset named Movie Night that fires the matching HA scene the other way.
- **Party button:** a dashboard button that fires a cue, sets the bar zone
  to full, and switches the audio source Control Value to Spotify.
- **Presence:** when the alarm arms (everyone left), stop playback and
  disable the schedules.

### Troubleshooting

- No device in HA: the MQTT integration must use the *same* broker the
  Core publishes to (Remote Control MQTT, or the plugin's HA MQTT broker
  if you filled that in). The Plugins page should show the plugin as
  connected.
- Entities show unavailable: the broker lost the device — check network
  and MQTT settings. The device reconnects automatically.
- Too many Core looks in HA: fill **Only these presets, cues, and
  timelines**, or turn the category expose toggle off. Unlisted looks are
  removed from HA on save.
- A listed look never appears: the expose toggle must be on, and the
  entry must match a display name, full code (`preset.PARTY`), or
  shortcode (`PARTY`). Reload the MQTT integration if HA cached an old
  discovery set.
- Deleted a preset but it lingers in HA: it is removed automatically on
  the next catalog change; if HA cached it, reload the MQTT integration.
- Plugins page never shows HA scenes: URL, token, and network to HA port
  8123. Leave URL empty only if HA is on the same LAN and advertises
  mDNS. Check device logs for `HA: …` errors.
- Cue or preset does not fire an HA scene: names must match (display name,
  not the shortcode). Confirm the scene is in the discovered list. Device
  logs list the HA scenes that were compared when nothing matched.
- Stop scene never fires: fill in **HA scene when playback stops** (name or
  `scene.*` id). It waits a fraction of a second so back-to-back cues do
  not flash it. Check the discovered list if you used a friendly name.
- Dual MQTT but HA still sees nothing: HA's MQTT integration must be
  connected to the broker you entered on the plugin, not only the Core's
  Remote Control broker.

## Development

```
dotnet test tests/DMXCore100.HomeAssistantPlugin.Tests
./pack.sh            # or pack.ps1 — produces artifacts/home-assistant-plugin.dmxplugin
```

`tools/DMXCore100.HomeAssistantPlugin.DevHost` is an interactive console
harness (F5 in Visual Studio) against an in-memory host — simulate HA birth
messages, commands, state changes, and (with `ha <url> <token>`) a real
Home Assistant scene list without a device.

Topic layout (`serial` = device hardware id, lowercase):

```
{prefix}/{component}/dmxcore-{serial}/{objectId}/config   retained discovery config
dmxcore/{serial}/{objectId}/state                         retained state
dmxcore/{serial}/{objectId}/set                           commands from HA
dmxcore/{serial}/ha-scene/{objectId}/set                  activate an HA scene from the Core
dmxcore/{serial}/availability                             device online/offline (host-managed last will)
dmxcore/{serial}/plugin/home-assistant/availability       plugin online/offline (host-managed)
```

On the optional Home Assistant MQTT broker, the same topics are published
and subscribed (availability included) so HA can see the device there too.

## Credits

- [AlexWHughes](https://github.com/AlexWHughes) — bidirectional scene control,
  optional Home Assistant MQTT broker, and expose allow-list.
- [HakanL](https://github.com/HakanL) (Hakan Lindestaf) — original MQTT
  Discovery plugin that publishes the Core into Home Assistant.
- [Home Assistant MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery)
  — config, state, command, and availability topic layout.
- [MQTTnet](https://github.com/dotnet/MQTTnet) — optional second MQTT client
  when the Core Remote Control broker is not Home Assistant's.

