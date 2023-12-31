using System;
using System.Collections.Generic;

namespace Best.SocketIO.Transports
{
    using Best.HTTP.Hosts.Connections;
    using Best.HTTP.Shared;
    using Best.HTTP.Shared.Extensions;
    using Best.HTTP.Shared.PlatformSupport.Memory;
    using Best.WebSockets;

    /// <summary>
    /// A transport implementation that can communicate with a SocketIO server.
    /// </summary>
    internal sealed class WebSocketTransport : ITransport
    {
        public TransportTypes Type { get { return TransportTypes.WebSocket; } }
        public TransportStates State { get; private set; }
        public SocketManager Manager { get; private set; }
        public bool IsRequestInProgress { get { return false; } }
        public bool IsPollingInProgress { get { return false; } }
        public WebSocket Implementation { get; private set; }

        private Packet PacketWithAttachment;
        private byte[] Buffer;

        public WebSocketTransport(SocketManager manager)
        {
            State = TransportStates.Closed;
            Manager = manager;
        }

        #region Some ITransport Implementation

        public void Open()
        {
            if (State != TransportStates.Closed)
                return;

            Uri uri = null;
            string baseUrl = new UriBuilder(HTTPProtocolFactory.IsSecureProtocol(Manager.Uri) ? "wss" : "ws",
                                                            Manager.Uri.Host,
                                                            Manager.Uri.Port,
                                                            Manager.Uri.GetRequestPathAndQueryURL()).Uri.ToString();
            string format = "{0}?EIO={1}&transport=websocket{3}";
            if (Manager.Handshake != null)
                format += "&sid={2}";

            bool sendAdditionalQueryParams = !Manager.Options.QueryParamsOnlyForHandshake || (Manager.Options.QueryParamsOnlyForHandshake && Manager.Handshake == null);

            uri = new Uri(string.Format(format,
                                        baseUrl,
                                        Manager.ProtocolVersion,
                                        Manager.Handshake != null ? Manager.Handshake.Sid : string.Empty,
                                        sendAdditionalQueryParams ? Manager.Options.BuildQueryParams() : string.Empty));

            Implementation = new WebSocket(uri);

#if !UNITY_WEBGL || UNITY_EDITOR
            Implementation.SendPings = true;

            if (this.Manager.Options.HTTPRequestCustomizationCallback != null)
                Implementation.OnInternalRequestCreated = (ws, internalRequest) => this.Manager.Options.HTTPRequestCustomizationCallback(this.Manager, internalRequest);
#endif

            Implementation.OnOpen = OnOpen;
            Implementation.OnMessage = OnMessage;
            Implementation.OnBinary = OnBinary;
            Implementation.OnClosed = OnClosed;

            Implementation.Open();

            State = TransportStates.Connecting;
        }

        /// <summary>
        /// Closes the transport and cleans up resources.
        /// </summary>
        public void Close()
        {
            if (State == TransportStates.Closed)
                return;

            State = TransportStates.Closed;

            if (Implementation != null)
                Implementation.Close();
            else
                HTTPManager.Logger.Warning("WebSocketTransport", "Close - WebSocket Implementation already null!");
            Implementation = null;
        }

        /// <summary>
        /// Polling implementation. With WebSocket it's just a skeleton.
        /// </summary>
        public void Poll()
        {
        }

        #endregion

        #region WebSocket Events

        /// <summary>
        /// WebSocket implementation OnOpen event handler.
        /// </summary>
        private void OnOpen(WebSocket ws)
        {
            if (ws != Implementation)
                return;

            HTTPManager.Logger.Information("WebSocketTransport", "OnOpen");

            State = TransportStates.Opening;

            // Send a Probe packet to test the transport. If we receive back a pong with the same payload we can upgrade
            if (Manager.UpgradingTransport == this)
                Send(new Packet(TransportEventTypes.Ping, SocketIOEventTypes.Unknown, "/", "probe"));
        }

        /// <summary>
        /// WebSocket implementation OnMessage event handler.
        /// </summary>
        private void OnMessage(WebSocket ws, string message)
        {
            if (ws != Implementation)
                return;

            if (HTTPManager.Logger.IsDiagnostic)
                HTTPManager.Logger.Verbose("WebSocketTransport", "OnMessage: " + message);

            Packet packet = null;
            try
            {
                packet = new Packet(message);
            }
            catch (Exception ex)
            {
                HTTPManager.Logger.Exception("WebSocketTransport", "OnMessage Packet parsing", ex);
            }

            if (packet == null)
            {
                HTTPManager.Logger.Error("WebSocketTransport", "Message parsing failed. Message: " + message);
                return;
            }

            try
            {

                if (packet.AttachmentCount == 0)
                    OnPacket(packet);
                else
                    PacketWithAttachment = packet;
            }
            catch (Exception ex)
            {
                HTTPManager.Logger.Exception("WebSocketTransport", "OnMessage OnPacket", ex);
            }
        }

        /// <summary>
        /// WebSocket implementation OnBinary event handler.
        /// </summary>
        private void OnBinary(WebSocket ws, BufferSegment buffer)
        {
            if (ws != Implementation)
                return;

            if (HTTPManager.Logger.IsDiagnostic)
                HTTPManager.Logger.Verbose("WebSocketTransport", "OnBinary");

            if (PacketWithAttachment != null)
            {
                var data = new byte[buffer.Count];
                buffer.CopyTo(data);
                
                switch(this.Manager.Options.ServerVersion)
                {
                    case SupportedSocketIOVersions.v2: PacketWithAttachment.AddAttachmentFromServer(data, false); break;
                    case SupportedSocketIOVersions.v3: PacketWithAttachment.AddAttachmentFromServer(data, true); break;
                    default:
                        HTTPManager.Logger.Warning("WebSocketTransport", "Binary packet received while the server's version is Unknown. Set SocketOption's ServerVersion to the correct value to avoid packet mishandling!");

                        // Fall back to V2 by default.
                        this.Manager.Options.ServerVersion = SupportedSocketIOVersions.v2;
                        goto case SupportedSocketIOVersions.v2;
                }

                if (PacketWithAttachment.HasAllAttachment)
                {
                    try
                    {
                        OnPacket(PacketWithAttachment);
                    }
                    catch (Exception ex)
                    {
                        HTTPManager.Logger.Exception("WebSocketTransport", "OnBinary", ex);
                    }
                    finally
                    {
                        PacketWithAttachment = null;
                    }
                }
            }
            else
            {
                // Room for improvement: we received an unwanted binary message?
            }
        }

        /// <summary>
        /// WebSocket implementation OnClosed event handler.
        /// </summary>
        private void OnClosed(WebSocket ws, WebSocketStatusCodes code, string message)
        {
            if (ws != Implementation)
              return;

            HTTPManager.Logger.Information("WebSocketTransport", $"OnClosed({code}, {message})");

            if (code == WebSocketStatusCodes.NormalClosure)
            {
                Close();

                if (Manager.UpgradingTransport != this)
                    (Manager as IManager).TryToReconnect();
                else
                    Manager.UpgradingTransport = null;
            }
            else
            {
                if (Manager.UpgradingTransport != this)
                    (Manager as IManager).OnTransportError(this, message);
                else
                    Manager.UpgradingTransport = null;
            }
        }

#endregion

#region Packet Sending Implementation

        /// <summary>
        /// A WebSocket implementation of the packet sending.
        /// </summary>
        public void Send(Packet packet)
        {
            if (State == TransportStates.Closed ||
                State == TransportStates.Paused)
            {
                HTTPManager.Logger.Information("WebSocketTransport", string.Format("Send - State == {0}, skipping packet sending!", State));
                return;
            }

            string encoded = packet.Encode();

            if (HTTPManager.Logger.IsDiagnostic)
                HTTPManager.Logger.Verbose("WebSocketTransport", "Send: " + encoded);

            if (packet.AttachmentCount != 0 || (packet.Attachments != null && packet.Attachments.Count != 0))
            {
                if (packet.Attachments == null)
                    throw new ArgumentException("packet.Attachments are null!");

                if (packet.AttachmentCount != packet.Attachments.Count)
                    throw new ArgumentException("packet.AttachmentCount != packet.Attachments.Count. Use the packet.AddAttachment function to add data to a packet!");
            }

            Implementation.Send(encoded);

            if (packet.AttachmentCount != 0)
            {
                int maxLength = packet.Attachments[0].Length + 1;
                for (int cv = 1; cv < packet.Attachments.Count; ++cv)
                    if ((packet.Attachments[cv].Length + 1) > maxLength)
                        maxLength = packet.Attachments[cv].Length + 1;

                if (Buffer == null || Buffer.Length < maxLength)
                    Array.Resize(ref Buffer, maxLength);

                for (int i = 0; i < packet.AttachmentCount; i++)
                {
                    Buffer[0] = (byte)TransportEventTypes.Message;

                    Array.Copy(packet.Attachments[i], 0, Buffer, 1, packet.Attachments[i].Length);

                    Implementation.Send(Buffer, 0, (ulong)packet.Attachments[i].Length + 1UL);
                }
            }
        }

        /// <summary>
        /// A WebSocket implementation of the packet sending.
        /// </summary>
        public void Send(List<Packet> packets)
        {
            for (int i = 0; i < packets.Count; ++i)
                Send(packets[i]);

            packets.Clear();
        }

#endregion

#region Packet Handling

        /// <summary>
        /// Will only process packets that need to upgrade. All other packets are passed to the Manager.
        /// </summary>
        private void OnPacket(Packet packet)
        {
            switch (packet.TransportEvent)
            {
                case TransportEventTypes.Open:
                    if (this.State != TransportStates.Opening)
                        HTTPManager.Logger.Warning("WebSocketTransport", "Received 'Open' packet while state is '" + State.ToString() + "'");
                    else
                        State = TransportStates.Open;
                    goto default;

                case TransportEventTypes.Pong:
                    // Answer for a Ping Probe.
                    if (packet.Payload == "probe")
                    {
                        State = TransportStates.Open;
                        (Manager as IManager).OnTransportProbed(this);
                    }

                    goto default;

                default:
                    if (Manager.UpgradingTransport != this)
                        (Manager as IManager).OnPacket(packet);
                    break;
            }
        }

#endregion
    }
}
