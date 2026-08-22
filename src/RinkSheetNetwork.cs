// RinkSheetNetwork.cs - Tells connected clients which practice sheets are standing.
//
// A sheet is server-side colliders plus client-side visuals, and netcode replicates
// neither: the server has to name the sheets and let each client build its own copy.
//
// Like PropNetwork, the server re-broadcasts the whole list on an interval instead of
// tracking per-client acks. The payload is a handful of bytes, and a periodic resend
// transparently covers late joiners and anyone whose level hadn't finished loading
// when the first message landed.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    internal static class RinkSheetNetwork
    {
        private const string MessageName = "MaxPractice.Sheets";
        private const int MaxSheetsPerMessage = 16;
        private const float BroadcastInterval = 5f;

        private static bool _handlerRegistered;
        private static float _nextBroadcast;
        private static int _lastClientCount;

        private static readonly List<RinkSheetSlot> _openSheets = new List<RinkSheetSlot>();
        private static readonly List<RinkSheetSlot> _received = new List<RinkSheetSlot>();
        private static readonly List<ulong> _targetClients = new List<ulong>();

        /// <summary>Server-side: the sheet list changed, push it out on the next tick.</summary>
        internal static void Announce()
        {
            _nextBroadcast = 0f;
        }

        internal static void Tick()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
            {
                _handlerRegistered = false;
                _nextBroadcast = 0f;
                _lastClientCount = 0;
                return;
            }

            if (!_handlerRegistered && nm.CustomMessagingManager != null)
            {
                try
                {
                    nm.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, OnMessage);
                    _handlerRegistered = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MaxPractice] Could not register the sheet message handler: {ex.Message}");
                }
            }

            if (!nm.IsServer) return;

            // Someone just joined — announce now rather than letting them skate around a
            // sheet nobody has drawn for them yet.
            int clientCount = nm.ConnectedClientsIds != null ? nm.ConnectedClientsIds.Count : 0;
            if (clientCount > _lastClientCount) _nextBroadcast = 0f;
            _lastClientCount = clientCount;

            if (Time.unscaledTime < _nextBroadcast) return;
            _nextBroadcast = Time.unscaledTime + BroadcastInterval;
            Broadcast(nm);
        }

        internal static void Shutdown()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (_handlerRegistered && nm != null && nm.CustomMessagingManager != null)
                    nm.CustomMessagingManager.UnregisterNamedMessageHandler(MessageName);
            }
            catch { }

            _handlerRegistered = false;
            _nextBroadcast = 0f;
            _lastClientCount = 0;
            _openSheets.Clear();
            _received.Clear();
        }

        // ------------------------------------------------------------------

        private static void Broadcast(NetworkManager nm)
        {
            try
            {
                if (nm.CustomMessagingManager == null) return;

                // A host has already built its own visuals locally, so sending it a copy
                // of its own announcement is pure noise.
                _targetClients.Clear();
                var connected = nm.ConnectedClientsIds;
                if (connected != null)
                {
                    for (int i = 0; i < connected.Count; i++)
                    {
                        if (nm.IsHost && connected[i] == nm.LocalClientId) continue;
                        _targetClients.Add(connected[i]);
                    }
                }
                if (_targetClients.Count == 0) return;

                RinkSheets.CollectOpenSheets(_openSheets);
                int count = Mathf.Min(_openSheets.Count, MaxSheetsPerMessage);

                int size = sizeof(int) + sizeof(byte) + count * (sizeof(byte) + sizeof(float) * 3);
                using (var writer = new FastBufferWriter(size, Allocator.Temp))
                {
                    writer.WriteValueSafe(count);
                    // Whether the server is ACTUALLY chunk-encoding right now - not
                    // whether its configured layout would need it.
                    //
                    // ChunkSyncRequired alone is derived from the layout and is true for any
                    // horizontal config, even with EnableRinkSheets off and no sheet ever
                    // built. The server only arms its ENCODER lazily, from EnsureOpen, the
                    // first time somebody runs /rink. Announcing the flag early made every
                    // client arm its decoder against a chunk table the server had not begun
                    // to populate, and Expand() holds an object with no slot at its spawn
                    // transform - so every puck and remote player froze in place for the
                    // whole session, from config alone, until someone opened a sheet.
                    writer.WriteValueSafe((byte)(RinkSheets.ChunkSyncRequired && RinkChunkSync.ServerActive ? 1 : 0));
                    for (int i = 0; i < count; i++)
                    {
                        RinkSheetSlot slot = _openSheets[i];
                        writer.WriteValueSafe((byte)slot.Index);
                        writer.WriteValueSafe(slot.Origin.x);
                        writer.WriteValueSafe(slot.Origin.y);
                        writer.WriteValueSafe(slot.Origin.z);
                    }

                    nm.CustomMessagingManager.SendNamedMessage(
                        MessageName, _targetClients, writer, NetworkDelivery.ReliableSequenced);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Failed to broadcast practice sheets: {ex.Message}");
            }
        }

        private static void OnMessage(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                // Only the server announces sheets. Ignore anything a client sends.
                if (senderClientId != NetworkManager.ServerClientId) return;

                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer) return;

                reader.ReadValueSafe(out int count);
                if (count < 0 || count > MaxSheetsPerMessage) return;

                reader.ReadValueSafe(out byte chunkEncoded);

                _received.Clear();
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out byte index);
                    reader.ReadValueSafe(out float x);
                    reader.ReadValueSafe(out float y);
                    reader.ReadValueSafe(out float z);

                    _received.Add(new RinkSheetSlot
                    {
                        Index = index,
                        Id = "rink" + (index + 1),
                        Label = "Rink " + (index + 1),
                        Origin = new Vector3(x, y, z),
                    });
                }

                // Only a horizontal layout encodes chunk-locally. A vertical stack stays
                // inside the vanilla envelope, so the decoder is never armed for it.
                if (chunkEncoded != 0) RinkChunkSync.EnableClient();

                RinkSheets.ApplyRemoteSheets(_received);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Malformed sheet message: {ex.Message}");
            }
        }
    }
}
