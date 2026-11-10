using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace LedBarCam;

public sealed class Settings
{
    public string BrokerHost { get; set; } = "";
    public int BrokerPort { get; set; } = 1883;
    public string Username { get; set; } = "";
    public string ClientId { get; set; } = "webcam-mqtt";
    public string TopicBase { get; set; } = "webcam-mqtt";
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    public string DeviceIdRegex { get; set; } = @"^USB\\VID_046D&PID_[0-9A-F]{4}&MI_00\\";
    public int PollMilliseconds { get; set; } = 1000;
    public int OffDebounceSeconds { get; set; } = 5;
    public int KeepAliveSeconds { get; set; } = 15;
    public string EntityId { get; set; } = "binary_sensor.webcam_power";
    public string DeviceName { get; set; } = "Webcam Monitor";
}

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private readonly Settings _s;
    private readonly CameraProbe _probe;
    private readonly MqttFactory _factory = new();
    private readonly IMqttClient _mqtt;

    private string StateTopic => $"{_s.TopicBase}/webcam/state";
    private string AvailTopic => $"{_s.TopicBase}/status";
    private string DiscoveryTopic => $"{_s.DiscoveryPrefix}/binary_sensor/{_s.ClientId.Replace('-', '_')}_webcam/config";
    private string HaStatusTopic => $"{_s.DiscoveryPrefix}/status";

    private volatile bool _republish;
    private bool _active;
    private DateTime _lastSeenOn = DateTime.MinValue;
    private DateTime _nextConnectAttempt = DateTime.MinValue;
    private int _failedConnects;
    private string? _lastProbeError;

    public Worker(ILogger<Worker> log, IOptions<Settings> settings)
    {
        _log = log;
        _s = settings.Value;
        _probe = new CameraProbe(_s.DeviceIdRegex);
        _mqtt = _factory.CreateMqttClient();
        _mqtt.ApplicationMessageReceivedAsync += e =>
        {
            // Home Assistant restarted: re-announce discovery + current state.
            if (e.ApplicationMessage.Topic == HaStatusTopic && e.ApplicationMessage.ConvertPayloadToString() == "online")
                _republish = true;
            return Task.CompletedTask;
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cams = SafeRead();
        foreach (var c in cams) _log.LogInformation("Watching {Name} [{Id}] currently D{D}", c.Name, c.InstanceId, c.DState);
        if (cams.Count == 0) _log.LogWarning("No camera matches {Regex}; reporting OFF until one appears", _s.DeviceIdRegex);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var raw = SafeRead().Any(c => c.DState == 0);
            if (raw) _lastSeenOn = now;
            var wanted = raw || (_active && now - _lastSeenOn < TimeSpan.FromSeconds(_s.OffDebounceSeconds));

            var changed = wanted != _active;
            _active = wanted;
            if (changed) _log.LogInformation("Webcam {State}", _active ? "ON" : "OFF");

            if (!_mqtt.IsConnected)
            {
                if (now >= _nextConnectAttempt && await TryConnectAsync(ct)) _republish = true;
            }
            if (_mqtt.IsConnected)
            {
                try
                {
                    if (_republish) { _republish = false; await AnnounceAsync(ct); }
                    else if (changed) await PublishAsync(StateTopic, _active ? "ON" : "OFF", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Publish failed: {Msg}", ex.Message);
                    _republish = true;
                }
            }

            try { await Task.Delay(_s.PollMilliseconds, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        if (_mqtt.IsConnected)
        {
            try
            {
                // Graceful stop: mark unavailable (a normal disconnect suppresses the will message).
                await PublishAsync(AvailTopic, "offline", ct);
                await _mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
                    .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection).Build(), ct);
            }
            catch (Exception ex) { _log.LogWarning("Clean disconnect failed: {Msg}", ex.Message); }
        }
        _log.LogInformation("Stopped");
    }

    private IReadOnlyList<CameraProbe.CameraState> SafeRead()
    {
        try
        {
            var r = _probe.Read();
            _lastProbeError = null;
            return r;
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastProbeError) _log.LogError("Camera probe failed: {Msg}", ex.Message);
            _lastProbeError = ex.Message;
            return Array.Empty<CameraProbe.CameraState>();
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        string password;
        try { password = Secrets.ReadPassword(); }
        catch (Exception ex)
        {
            _log.LogError("Cannot read MQTT password from {File}: {Msg}. Run 'LedBarCam.exe --set-password' as admin.",
                Secrets.PasswordFile, ex.GetType().Name);
            _nextConnectAttempt = DateTime.UtcNow.AddSeconds(60);
            return false;
        }

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_s.BrokerHost, _s.BrokerPort)
            .WithClientId(_s.ClientId)
            .WithCredentials(_s.Username, password)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_s.KeepAliveSeconds))
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithWillTopic(AvailTopic)
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await _mqtt.ConnectAsync(options, ct);
            await _mqtt.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(HaStatusTopic)).Build(), ct);
            _log.LogInformation("Connected to {Host}:{Port} as {User}", _s.BrokerHost, _s.BrokerPort, _s.Username);
            _failedConnects = 0;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failedConnects++;
            var wait = Math.Min(60, 5 * _failedConnects);
            if (_failedConnects <= 3 || _failedConnects % 20 == 0)
                _log.LogWarning("MQTT connect failed ({N}x): {Msg}; retry in {Wait}s", _failedConnects, ex.Message, wait);
            _nextConnectAttempt = DateTime.UtcNow.AddSeconds(wait);
            return false;
        }
    }

    private async Task AnnounceAsync(CancellationToken ct)
    {
        var id = _s.ClientId.Replace('-', '_');
        var config = new Dictionary<string, object>
        {
            ["name"] = "Webcam",
            ["unique_id"] = $"{id}_webcam_power",
            ["default_entity_id"] = _s.EntityId,
            ["icon"] = "mdi:webcam",
            ["state_topic"] = StateTopic,
            ["payload_on"] = "ON",
            ["payload_off"] = "OFF",
            ["availability_topic"] = AvailTopic,
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["device"] = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { _s.ClientId },
                ["name"] = _s.DeviceName,
                ["manufacturer"] = "LedBarCam",
                ["model"] = "LedBarCam",
                ["sw_version"] = typeof(Worker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            },
        };
        await PublishAsync(DiscoveryTopic, JsonSerializer.Serialize(config), ct);
        await PublishAsync(AvailTopic, "online", ct);
        await PublishAsync(StateTopic, _active ? "ON" : "OFF", ct);
    }

    private Task PublishAsync(string topic, string payload, CancellationToken ct) =>
        _mqtt.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);
}
