# DMX Core 100 — Home Assistant Plugin

Built-in plugin that connects the device and Home Assistant in both
directions:

- **HA → DMX Core:** publishes the device's entities to Home Assistant via
  [MQTT Discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery).
  No custom component and no YAML on the Home Assistant side — entities
  appear automatically, grouped under one device. openHAB, ioBroker, and
  Domoticz consume the same discovery format.
- **DMX Core → HA:** fire Home Assistant scenes, scripts, and automations
  from the device — from a Stream Deck key, a touchscreen custom menu, an
  input trigger, a timeline, or a script — through HA's REST API.

## User guide

*(Source for the docs.dmxcore.com "Home Assistant" section.)*

### What you get

Once connected, Home Assistant shows one **DMX Core 100 device** with:

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

And in the other direction, the DMX Core can trigger Home Assistant:

| On the DMX Core | In Home Assistant |
|---|---|
| An **Output Event** of type *Home Assistant*, bound to any button (Stream Deck, MIDI, custom menu), input trigger, timeline event, or script | Activates a **scene**, runs a **script**, or triggers an **automation** — picked from a live list, no ids to type |

### Setup

Prerequisites: an MQTT broker that both Home Assistant and the DMX Core can
reach (most HA installs use the Mosquitto add-on), and Home Assistant's
**MQTT integration** connected to it (Settings → Devices & Services → Add
Integration → MQTT).

On the DMX Core:

1. Web UI → **Settings → Remote Control**: enable the external MQTT server
   and enter the broker's address, port, and credentials.
2. **Settings → Plugins**: make sure the Home Assistant plugin is enabled
   (it is by default). Optional settings: the discovery prefix (leave at
   `homeassistant` unless you changed it in HA) and per-category expose
   toggles if you don't want e.g. every cue showing up in HA.

That's it — within a few seconds the device appears in Home Assistant under
Settings → Devices & Services → MQTT.

#### Triggering Home Assistant from the DMX Core (optional)

This direction uses Home Assistant's REST API, so the device needs to know
where HA is and have a token:

1. In Home Assistant, open your **profile → Security** and create a
   **Long-lived access token** (name it e.g. `DMX Core`). Copy it — HA shows
   it only once.
2. On the DMX Core web UI, **Settings → Plugins → Home Assistant**: enter
   the **Home Assistant URL** (e.g. `http://homeassistant.local:8123`) and
   paste the token into **Long-lived access token**. The plugin status shows
   *HA API ok* once the token is accepted.
3. **Input/Output → Output Events → New**: set the type to **Home
   Assistant** and pick the scene, script, or automation from the **Target**
   list. Save, and use **Test** to fire it once.
4. Bind it anywhere an action can be configured — a control surface button,
   a custom menu item, an input trigger, a timeline event, or
   `fireOutputEvent("<code>")` in a script — using the **Fire Output Event**
   action.

For scripts that take variables, put a JSON object in the Output Event's
payload (e.g. `{"variables": {"level": 50}}`); it is merged into the service
call. Anything else HA can do can be wrapped in an HA script, which then
shows up in the target list.

If HA is unreachable when an event fires, the rest of the button's work
(playing a cue, etc.) still happens; the failure is logged and shown by the
Test button.

### Ideas

- **Sunset ambiance:** HA automation at sunset → activate the `Evening`
  preset scene (or let the DMX Core's own sunrise/sunset schedules do it and
  just flip the schedule switch from HA when you're on vacation).
- **Movie night:** one HA script that dims your living room Hue lights *and*
  sets the DMX Core master dimmer to 20%.
- **Party button:** a dashboard button that fires a cue, sets the bar zone to
  full, and switches the audio source Control Value to Spotify.
- **Presence:** when the alarm arms (everyone left), stop playback and
  disable the schedules.
- **Movie night, from the wall:** a Stream Deck key or touchscreen menu item
  that plays the `Movie` preset *and* fires the HA scene that closes the
  blinds and dims the Hue lights (two actions on one button, or one HA
  script that does both).

### Troubleshooting

- No device in HA: check the MQTT integration is connected to the *same*
  broker as the DMX Core, and that the DMX Core web UI shows the plugin as
  connected (Settings → Plugins).
- Entities show unavailable: the broker lost the device — check network and
  the Remote Control MQTT settings. The device reconnects automatically.
- Deleted a preset but it lingers in HA: it is removed automatically on the
  next catalog change; if HA itself cached it, reload the MQTT integration.
- Plugin status says *HA API: ... rejected the access token*: the token was
  revoked or pasted incompletely — create a new one in your HA profile.
- Output Event target list is empty / shows "Could not load targets": the
  device can't reach the HA URL (check it opens from a browser on the same
  network, including the port) or the plugin is disabled. You can still
  type the entity id (e.g. `scene.movie_night`) by hand.

## Development

```
dotnet test tests/DMXCore100.HomeAssistantPlugin.Tests
./pack.sh            # or pack.ps1 — produces artifacts/home-assistant-plugin.dmxplugin
```

`tools/DMXCore100.HomeAssistantPlugin.DevHost` is an interactive console
harness (F5 in Visual Studio) against an in-memory host — simulate HA birth
messages, commands, and state changes without a device or broker. Pass
`<url> <token>` (or use the `ha` command) to point the Core→HA side at a
real Home Assistant and try `targets` / `fire scene.xyz`.

Requires SDK contract **1.7** (`IPluginHost.Actions`). Until that SDK
version is on nuget.org, pack it from the Software repo into `local-feed/`
(see `nuget.config`).

Topic layout (`serial` = device hardware id, lowercase):

```
{prefix}/{component}/dmxcore-{serial}/{objectId}/config   retained discovery config
dmxcore/{serial}/{objectId}/state                         retained state
dmxcore/{serial}/{objectId}/set                           commands from HA
dmxcore/{serial}/availability                             device online/offline (host-managed last will)
dmxcore/{serial}/plugin/home-assistant/availability       plugin online/offline (host-managed)
```

Every push to `main` recreates the rolling `latest` release carrying the
packed `.dmxplugin`; the DMX Core 100 product build downloads it from there
and bundles it as a built-in plugin.
