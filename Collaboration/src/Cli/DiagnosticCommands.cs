using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Protobot.Collaboration;

internal static partial class Program {
    private static async Task<int> Diagnostics(Options options) {
        DocumentModel.Require(options.Has("--owner-file") != options.Has("--admin-file"), "use-owner-or-admin-file");
        string origin = ConnectionDoctor.Origin(options.Required("--url"), options.Has("--allow-lan-http"));
        string room = null, credential;
        if (options.Has("--owner-file")) { var owner = ReadOwnerFile(options.Get("--owner-file")); room = owner.room; credential = owner.credential; }
        else credential = ReadServerAdmin(options.Get("--admin-file")).credential;
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var network = await Network(options, stop.Token);
        var report = await DiagnosticExport.FetchAsync(origin, room, credential, stop.Token, network, retryProgress);
        if (options.Has("--output")) await DiagnosticExport.WriteAsync(options.Get("--output"), report, stop.Token);
        Console.WriteLine(Encoding.UTF8.GetString(DiagnosticExport.Encode(report))); return 0;
    }
}
