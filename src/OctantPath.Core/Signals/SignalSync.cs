using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Connection;
using OctantPath.Core.Languages;

namespace OctantPath.Core.Signals
{
    // 正本を OctantCompass に送る（契約3（OctantPath to OctantCompass）2.0）。OctantCompass は signal を動いている間だけ持つので、
    // その起動にまだ送っていなければ正本を丸ごと送って置き換え（PUT）、あとは足した分だけ送る（POST）
    public static class SignalSync
    {
        // sent: OctantCompass のこの起動が持っている件数（正本の先頭から）。null なら丸ごと送る
        public static async Task<SyncResult> SyncAsync(SignalLog log, HostConnection connection, int? sent, CancellationToken cancellationToken)
        {
            var events = log.Events.ToList();
            var replace = sent == null;
            var sending = replace ? events : events.Skip(sent.Value).ToList();
            if (!replace && sending.Count == 0)
            {
                return SyncResult.Done(sent.Value, 0, false);
            }
            var result = await connection.SendSignalsAsync(new SignalsRequest { Events = sending }, replace, cancellationToken).ConfigureAwait(false);
            if (result == null || result.Kind != HostResultKind.Ok)
            {
                return SyncResult.Failed(result?.Detail ?? Lang.T("connection.notConnected"));
            }
            var expected = (replace ? 0 : sent.Value) + sending.Count;
            if (result.Body.Count != expected)
            {
                // 数が合わないなら、どこまで届いたか分からない。次は丸ごと送り直す
                return SyncResult.Failed(Lang.T("signals.countMismatch", new { host = result.Body.Count, sent = expected }));
            }
            return SyncResult.Done(expected, sending.Count, replace, result.Body.Rejected ?? new System.Collections.Generic.List<RejectedSignal>());
        }
    }

    public sealed class SyncResult
    {
        public bool Succeeded { get; private set; }
        // OctantCompass のこの起動が持つ件数
        public int Sent { get; private set; }
        // この回に送った件数と、丸ごと送ったかどうか
        public int Added { get; private set; }
        public bool Replaced { get; private set; }
        public System.Collections.Generic.List<RejectedSignal> RejectedEvents { get; private set; }
        public string Error { get; private set; }

        public static SyncResult Done(int sent, int added, bool replaced, System.Collections.Generic.List<RejectedSignal> rejectedEvents = null) =>
            new SyncResult { Succeeded = true, Sent = sent, Added = added, Replaced = replaced, RejectedEvents = rejectedEvents ?? new System.Collections.Generic.List<RejectedSignal>() };

        public static SyncResult Failed(string error) => new SyncResult { Error = error };
    }
}
