# webcam-power-mqtt (LedBarCam)

A small Windows service that publishes **"webcam in use"** to MQTT, with Home Assistant discovery.
It detects the camera from the device's **power state** (D0 = streaming, D3 = idle). It does not read the
`CapabilityAccessManager\ConsentStore` registry keys.

## Why

On Windows 11 26H2 (and on some 25H2 machines after the October 2026 updates), Windows no longer updates
`ConsentStore\webcam\…\LastUsedTimeStop`. Tools that rely on it, such as HASS.Agent's `WebcamActive`
sensor, stay `off` during calls. See [hass-agent/HASS.Agent#498](https://github.com/hass-agent/HASS.Agent/issues/498).

The camera's `PD_MostRecentPowerState` (from `DEVPKEY_Device_PowerData`) still changes reliably when an app
such as Teams, Zoom or the browser streams from it. This is a machine-wide property, so it can be read from a
service in session 0 without a logged-on user. The service needs no admin rights.

## What it does

- Polls the matching camera(s) every second via `CM_Get_DevNode_PropertyW` (cfgmgr32). Each read takes milliseconds.
- Reports `ON` as soon as any matching camera is in D0, and `OFF` after it has been out of D0 for `OffDebounceSeconds` (default 5 s).
  Cameras can drop to D3 for a few seconds while a call starts.
- Publishes, all retained:
  - `<TopicBase>/webcam/state`: `ON` / `OFF`
  - `<TopicBase>/status`: `online` / `offline` (last-will message, so a sleeping or crashed PC becomes *unavailable*)
  - `<DiscoveryPrefix>/binary_sensor/<ClientId>_webcam/config`: Home Assistant discovery
- Re-announces discovery and state when Home Assistant publishes `online` on `<DiscoveryPrefix>/status`.
- Logs connections, ON/OFF transitions and errors to the Windows **Application** event log under source `LedBarCam`.

## Requirements

- Windows 10/11 x64.
- An MQTT broker, such as the Home Assistant Mosquitto add-on, and a login for this service.
- To build: the .NET 8 SDK on Windows or Linux. The build produces a self-contained single-file exe, so no .NET runtime is needed on the target.

## 1. Find your camera

```powershell
.\LedBarCam.exe --probe-all
```
Run it once with the camera idle and once while it is in use in a call. Check that your webcam's line
changes from `D3` to `D0`. Scanners and printers in the `Image` class may show D0 all the time; ignore them.
Then set `DeviceIdRegex` to match your camera's instance ID. The default matches Logitech USB cameras
(`VID_046D`, interface `MI_00`). Check the match with `--probe`.

## 2. Build

```bash
cd src/LedBarCam
dotnet publish -c Release -o ../../deploy/bin
```

## 3. Configure

Edit `deploy/bin/appsettings.json`:

| Key | Default | Meaning |
|---|---|---|
| `BrokerHost` / `BrokerPort` | `homeassistant.local` / `1883` | MQTT broker |
| `Username` | `webcam-mqtt` | MQTT login. The password is not stored here; see step 4 |
| `ClientId` / `TopicBase` | `webcam-mqtt` | MQTT client id and topic prefix |
| `DiscoveryPrefix` | `homeassistant` | HA discovery prefix |
| `DeviceIdRegex` | Logitech `VID_046D … MI_00` | Which USB Camera/Image devices to watch |
| `PollMilliseconds` | `1000` | Poll interval |
| `OffDebounceSeconds` | `5` | How long the camera must be idle before `OFF` is reported |
| `KeepAliveSeconds` | `15` | MQTT keep-alive (the last-will fires after ~1.5×) |
| `EntityId` / `DeviceName` | `binary_sensor.webcam_power` / `Webcam Monitor` | HA entity id and device name |

## 4. Install (elevated PowerShell)

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\install.ps1
```

The script:
- copies the exe to `C:\Program Files\LedBarCam\`, plus `appsettings.json` if none exists there yet;
- creates the service: delayed auto-start, restart on failure, running as **`NT AUTHORITY\LocalService`**
  with service SID type `unrestricted`;
- creates `C:\ProgramData\LedBarCam\`, readable only by SYSTEM, Administrators and **this service's SID**;
- prompts (without echo) for the MQTT password and stores it **DPAPI-encrypted** (LocalMachine scope) in `mqtt.pwd`.
  The password never appears in a file in plain text, on a command line or in the logs;
- starts the service and shows the first event-log entries.

To change the password later: `& "C:\Program Files\LedBarCam\LedBarCam.exe" --set-password` (elevated),
then `Restart-Service LedBarCam`. To remove everything: `deploy\uninstall.ps1`.

## 5. Home Assistant automation example

```yaml
- alias: Lights on while on camera
  triggers:
    - trigger: state
      entity_id: binary_sensor.webcam_power
      to: 'on'
  actions:
    - action: light.turn_on
      target: { entity_id: light.desk_led_bar }
- alias: Lights off after camera
  triggers:
    - trigger: state
      entity_id: binary_sensor.webcam_power
      from: 'on'            # ignore unavailable → off after restarts
      to: ['off', unavailable]
  actions:
    - action: light.turn_off
      target: { entity_id: light.desk_led_bar }
```

## Limitations

- Tested on one PC only: Windows 11 Pro build 26300, a Logitech BRIO on USB, Teams (new) and Zoom.
  Built-in laptop cameras may manage power differently, so always check with `--probe-all` first.
- Detects whether the camera is streaming, not which app is using it.
- MQTT without TLS by default (MQTTnet supports TLS; it isn't wired up here). Use it on a trusted LAN only.
- The exe is unsigned. Managed PCs with app control (WDAC/AppLocker) may need it signed or allow-listed.

## Disclosure

This code was written with the help of an AI assistant (Claude) and reviewed and tested by the author on the setup above.
It is **not** affiliated with HASS.Agent, and it is not a contribution to that project.

## License

MIT — see `LICENSE`.
