using LedBarCam;

// Modes:
//   LedBarCam.exe                 run (as Windows service, or in the console for testing)
//   LedBarCam.exe --probe         print matching cameras and their power state once, then exit
//   LedBarCam.exe --probe-all     same, ignoring DeviceIdRegex (lists every USB Camera/Image device)
//   LedBarCam.exe --set-password  prompt (no echo) and store the MQTT password DPAPI-encrypted
if (args.Contains("--set-password", StringComparer.OrdinalIgnoreCase))
    return Secrets.SetPasswordInteractive();

var probeAll = args.Contains("--probe-all", StringComparer.OrdinalIgnoreCase);
if (probeAll || args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
{
    var cfg = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();
    var s = cfg.GetSection("LedBarCam").Get<Settings>() ?? new Settings();
    var cams = new CameraProbe(probeAll ? "^USB\\\\" : s.DeviceIdRegex).Read();
    if (cams.Count == 0) Console.WriteLine($"No camera matches {s.DeviceIdRegex}");
    foreach (var c in cams)
        Console.WriteLine($"D{c.DState}  {(c.DState == 0 ? "IN USE" : "idle  ")}  {c.Name}  [{c.InstanceId}]");
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(o => o.ServiceName = "LedBarCam");
builder.Services.Configure<Settings>(builder.Configuration.GetSection("LedBarCam"));
builder.Services.AddHostedService<Worker>();
builder.Build().Run();
return 0;
