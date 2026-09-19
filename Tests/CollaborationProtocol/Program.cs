using System.Security.Cryptography;
using System.Text;
using Jorjin.Streaming;

var receiver = new CollaborationResultReceiver();
byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("中文測試🙂", 1600)));
string digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
var pieces = data.Chunk(8000).ToArray();
CollaborationPacket Packet(string type, int i = 0, string id = "request") => new()
{
    type = type, session_id = "session", request_id = id, index = i, count = pieces.Length,
    data = type == "result_chunk" ? Convert.ToBase64String(pieces[i]) : null, sha256 = digest
};
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description);
}
void Reject(Action action, string description)
{
    try { action(); } catch (Exception e) when (e is InvalidDataException || e is FormatException)
    { Console.WriteLine("PASS " + description); return; }
    throw new Exception(description);
}
foreach (int i in Enumerable.Range(0, pieces.Length).Reverse())
    receiver.Accept("agent-a", Packet("result_chunk", i));
receiver.Accept("agent-a", Packet("result_chunk", 0));
Check(receiver.Accept("agent-a", Packet("result_complete")) == Encoding.UTF8.GetString(data), "UTF-8, out-of-order and duplicate chunks reassemble correctly");
Check(receiver.Accept("agent-a", Packet("result_complete")) == null, "duplicate completion is ignored");
Reject(() => receiver.Accept("agent-b", Packet("result_complete")), "other sender cannot complete a transfer");
receiver.Clear();
receiver.Accept("agent-a", Packet("result_chunk", 0));
Reject(() => receiver.Accept("agent-a", Packet("result_complete")), "missing chunks rejected");
receiver.Clear();
var oversized = Packet("result_chunk"); oversized.count = 999;
Reject(() => receiver.Accept("agent-a", oversized), "unbounded transfer rejected");
receiver.Clear();
foreach (int i in Enumerable.Range(0, pieces.Length))
{
    var chunk = Packet("result_chunk", i); chunk.sha256 = new string('0', 64); receiver.Accept("agent-a", chunk);
}
var badEnd = Packet("result_complete"); badEnd.sha256 = new string('0', 64);
Reject(() => receiver.Accept("agent-a", badEnd), "wrong SHA-256 rejected");
receiver.Clear();
receiver.Accept("agent-a", Packet("result_chunk", 0));
var changed = Packet("result_chunk", 0); changed.data = Convert.ToBase64String(new byte[] { 1 });
Reject(() => receiver.Accept("agent-a", changed), "conflicting duplicate rejected");
Console.WriteLine("7 protocol checks passed.");
