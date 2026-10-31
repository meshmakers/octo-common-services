// A handler replica in its own OS process, so that the measurement can kill it the way a node loss does (SIGKILL: no
// graceful close, no ack, TCP connection gone). Started and killed only by the measurement tests through the Process
// object they created. Protocol on stdout, one line each: READY, then EXEC <invocationId> <label> for every execution
// (the line is written before the handler's simulated work starts, so an execution cut short by the kill still counts).
using Meshmakers.Octo.Services.Methods.Measurements;

var arguments = args.Select(a => a.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
var host = arguments["host"];
var port = int.Parse(arguments["port"]);
var prefix = arguments["prefix"];
var label = arguments["label"];
var work = TimeSpan.FromMilliseconds(int.Parse(arguments["workMs"]));

var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
var gate = new object();
var probe = new MeasurementProbe(label, work, (id, instance) =>
{
    lock (gate)
    {
        stdout.WriteLine($"EXEC {id} {instance}");
    }
});

await using var replica = await MeasurementHost.StartHandlerAsync(host, port, prefix, probe);
lock (gate)
{
    stdout.WriteLine("READY");
}

await Task.Delay(Timeout.Infinite);
