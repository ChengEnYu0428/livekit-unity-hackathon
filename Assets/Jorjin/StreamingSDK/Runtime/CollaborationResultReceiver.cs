using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jorjin.Streaming
{
    [Serializable]
    public sealed class CollaborationPacket
    {
        public string type, session_id, request_id, message, data, sha256, kind;
        public int index, count;
    }

    [Serializable]
    public sealed class CollaborationResult
    {
        public string kind, question, answer, current_status, display_text, model;
        // Summary language chosen from the conversation: "zh" or "en".
        public string language;
        // Photo text recognition ("ocr"): source/target are "zh" or "en".
        public string original_text, translated_text, source_language, target_language;
        public string[] problem_summary, performed_actions, action_items, next_steps;
        public int context_segments, omitted_segments;
        // Meeting action items ("tasks").
        public string message;
        public CollaborationTask[] tasks;
        public CollaborationCalendarEvent[] calendar_events;
    }

    [Serializable]
    public sealed class CollaborationTask
    {
        public string id, title, owner, deadline, deadline_date, start_time, end_time, created_at, updated_at;
    }

    [Serializable]
    public sealed class CollaborationCalendarEvent
    {
        public string id, task_id, title, owner, deadline, date, start_time, end_time;
    }

    /// <summary>Bounded, SHA-256-verified reassembly, isolated by sender and request.</summary>
    public sealed class CollaborationResultReceiver
    {
        private sealed class Transfer
        {
            public readonly Dictionary<int, byte[]> Parts = new();
            public int Count, Bytes;
            public string Digest;
            public DateTime Created = DateTime.UtcNow;
        }
        private readonly Dictionary<string, Transfer> transfers = new();
        private readonly HashSet<string> completed = new();

        public void Clear() { transfers.Clear(); completed.Clear(); }

        public string Accept(string sender, CollaborationPacket packet)
        {
            foreach (string expired in transfers.Where(p =>
                (DateTime.UtcNow - p.Value.Created).TotalSeconds > 90).Select(p => p.Key).ToArray())
                transfers.Remove(expired);
            if (packet.count < 1 || packet.count > 8 || string.IsNullOrEmpty(packet.request_id) ||
                packet.request_id.Length > 64 || string.IsNullOrEmpty(packet.sha256) || packet.sha256.Length != 64)
                throw new InvalidDataException("Invalid AI transfer metadata.");
            string key = sender + ":" + packet.session_id + ":" + packet.request_id;
            if (completed.Contains(key)) return null;
            if (!transfers.TryGetValue(key, out Transfer transfer))
            {
                if (transfers.Count >= 4) throw new InvalidDataException("Too many AI transfers.");
                transfer = new Transfer { Count = packet.count, Digest = packet.sha256 };
                transfers.Add(key, transfer);
            }
            if (transfer.Count != packet.count || transfer.Digest != packet.sha256)
            { transfers.Remove(key); throw new InvalidDataException("AI transfer metadata changed."); }
            if (packet.type == "result_chunk")
            {
                if (packet.index < 0 || packet.index >= transfer.Count || packet.data == null || packet.data.Length > 11000)
                    throw new InvalidDataException("Invalid AI chunk.");
                byte[] bytes = Convert.FromBase64String(packet.data);
                if (bytes.Length > 8000) throw new InvalidDataException("AI chunk too large.");
                if (transfer.Parts.TryGetValue(packet.index, out byte[] previous))
                {
                    if (!previous.SequenceEqual(bytes)) throw new InvalidDataException("AI chunk changed.");
                }
                else { transfer.Parts.Add(packet.index, bytes); transfer.Bytes += bytes.Length; }
                return null;
            }
            if (packet.type != "result_complete") return null;
            transfers.Remove(key);
            if (transfer.Parts.Count != transfer.Count || transfer.Bytes > 64000)
                throw new InvalidDataException("AI result incomplete. Please retry.");
            using var stream = new MemoryStream();
            for (int i = 0; i < transfer.Count; i++) stream.Write(transfer.Parts[i], 0, transfer.Parts[i].Length);
            byte[] content = stream.ToArray();
            using var hash = SHA256.Create();
            string digest = BitConverter.ToString(hash.ComputeHash(content)).Replace("-", "").ToLowerInvariant();
            if (digest != transfer.Digest) throw new InvalidDataException("AI result integrity check failed.");
            if (completed.Count >= 128) completed.Clear();
            completed.Add(key);
            return new UTF8Encoding(false, true).GetString(content);
        }
    }
}
