using System.Diagnostics;

namespace DeskProfiles;

// Instrumentation only. Does not mutate desktop state or change switch behavior.
internal sealed class SwitchTrace
{
    private static long nextId;
    private readonly long id = System.Threading.Interlocked.Increment(ref nextId);
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly Stopwatch stage = Stopwatch.StartNew();
    private string current = "UI overlay initialization";
    private readonly List<string> records = new();
    private bool finished;

    public SwitchTrace(string from, string to, string topology)
    {
        Diagnostics.Info($"[SWITCH #{id}] BEGIN {from} -> {to}; topology={topology}");
    }

    public void Next(string stageName)
    {
        if (finished) return;
        records.Add($"{current}={stage.ElapsedMilliseconds}ms");
        Diagnostics.Info($"[SWITCH #{id}] {current}: {stage.ElapsedMilliseconds}ms");
        current = stageName;
        stage.Restart();
    }

    public void Note(string message) => Diagnostics.Info($"[SWITCH #{id}] {message}");

    public void Finish()
    {
        if (finished) return;
        finished = true;
        records.Add($"{current}={stage.ElapsedMilliseconds}ms");
        Diagnostics.Info($"[SWITCH #{id}] {current}: {stage.ElapsedMilliseconds}ms");
        Diagnostics.Info($"[SWITCH #{id}] END total={total.ElapsedMilliseconds}ms; {string.Join("; ", records)}");
    }
}
