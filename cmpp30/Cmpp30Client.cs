﻿using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Reefoo.CMPP30.Message;

namespace Reefoo.CMPP30
{
    /// <summary>
    /// Cmpp 3.0 client
    /// </summary>
    public class Cmpp30Client : BackgroundThread
    {
        private const int MaxMessageByteLength = 140;
        private const int LongMessageHeadLength = 6;

        private const int WindowSize = 16;
        private uint _sequenceId;
        private readonly Cmpp30Configuration _config;
        private readonly Cmpp30Transport _transport;
        private DateTime _lastTransferTime;
        private readonly Queue<CmppWindow> _pendingMessages = new Queue<CmppWindow>();
        private readonly Dictionary<uint, CmppWindow> _cmppWindows = new Dictionary<uint, CmppWindow>();

        /// <summary>
        /// Initialize with cmpp 3.0 config
        /// </summary>
        /// <param name="config"></param>
        public Cmpp30Client(Cmpp30Configuration config)
        {
            _config = config;
            _transport = new Cmpp30Transport(config);
            Status = Cmpp30ClientStatus.Disconnected;
            _transport.OnCmppMessageReceive += OnCmppMessageReceive;
            _transport.OnDisconnected += OnTransportDisconnected;
        }

        void OnTransportDisconnected(object sender, EventArgs e)
        {
            Status = Cmpp30ClientStatus.Disconnected;
        }

        /// <summary>
        /// Send an sms.
        /// </summary>
        /// <param name="extendedCode">code to extend sp code</param>
        /// <param name="receiver">to whom to send to</param>
        /// <param name="content">message content to send</param>
        /// <param name="messageIdList">message id list generated by cmpp gateway</param>
        /// <param name="needStatusReport">request for message status report or not</param>
        /// <returns>the send status</returns>
        public Cmpp30SendStatus Send(string extendedCode, string receiver, string content, out List<long> messageIdList, bool needStatusReport = true)
        {
            return Send(extendedCode, new[] { receiver }, content, out messageIdList, needStatusReport);
        }

        /// <summary>
        /// Send an sms.
        /// </summary>
        /// <param name="extendedCode">code to extend sp code</param>
        /// <param name="receiver">to whom to send to</param>
        /// <param name="content">message content to send</param>
        /// <param name="messageIdList">message id list generated by cmpp gateway</param>
        /// <param name="needStatusReport">request for message status report or not</param>
        /// <returns>the send status</returns>
        public Cmpp30SendStatus Send(string extendedCode, string[] receiver, string content, out List<long> messageIdList, bool needStatusReport = true)
        {
            var messageCount = _SentMessageCount(content);
            messageIdList = new List<long>(messageCount);

            if (Status == Cmpp30ClientStatus.AuthenticationFailed) return Cmpp30SendStatus.ConfigError;
            if (Status == Cmpp30ClientStatus.Authenticating || Status == Cmpp30ClientStatus.Connecting ||
                Status == Cmpp30ClientStatus.Disconnected || _pendingMessages.Count >= WindowSize)
                return Cmpp30SendStatus.Congested;
            if (Status == Cmpp30ClientStatus.Disposed) return Cmpp30SendStatus.NotConnected;
            if (messageCount == 0) return Cmpp30SendStatus.Unknown;
            if (messageCount > 8 || (messageCount > 1 && _config.DisableLongMessage))
                return Cmpp30SendStatus.MessageTooLong;

            if (messageCount == 1 || !_config.SendLongMessageAsShortMessages)
                return _SendInternal(extendedCode, receiver, content, messageIdList, needStatusReport);
            foreach (var msg in _SplitLongMessage(content))
            {
                while (true)
                {
                    var status = _SendInternal(extendedCode, receiver, msg, messageIdList, needStatusReport);
                    if (status == Cmpp30SendStatus.Success) break;
                    if (status != Cmpp30SendStatus.Congested) return status;
                    Thread.Sleep(100);
                }
            }
            return Cmpp30SendStatus.Success;
        }

        private Cmpp30SendStatus _SendInternal(string extendedCode, string[] receiver, string content, ICollection<long> messageIdList, bool needStatusReport)
        {
            var messageUCS2 = Encoding.BigEndianUnicode.GetBytes(content);
            var messageUCS2Len = messageUCS2.Length;
            var signatureLen = _config.AttemptRemoveSignature ? 0 : Encoding.BigEndianUnicode.GetBytes(_config.GatewaySignature).Length;

            if (messageUCS2Len + signatureLen <= MaxMessageByteLength)
            {
                // Send single message.
                var resp = _SendAsync(extendedCode, receiver, content, false, needStatusReport);
                if (!resp.WaitHandle.WaitOne(30000) || !(resp.Result is CmppSubmitResp)) return Cmpp30SendStatus.Timeout;
                var submitReponse = ((CmppSubmitResp)resp.Result);
                switch (submitReponse.Result)
                {
                    case 8:
                        return Cmpp30SendStatus.Congested;
                    case 10:
                    case 11:
                    case 12:
                    case 13:
                        return Cmpp30SendStatus.ConfigError;
                    case 4:
                        return Cmpp30SendStatus.MessageTooLong;
                    case 0:
                        messageIdList.Add(BitConverter.ToInt64(BitConverter.GetBytes(submitReponse.MsgId), 0));
                        return Cmpp30SendStatus.Success;
                    default:
                        return Cmpp30SendStatus.Unknown;
                }
            }

            // long message amount
            var messageUCS2Count = (messageUCS2Len - 1) / (MaxMessageByteLength - LongMessageHeadLength) + 1;
            var tpUdhiHead = new byte[6];
            tpUdhiHead[0] = 0x05;
            tpUdhiHead[1] = 0x00;
            tpUdhiHead[2] = 0x03;
            tpUdhiHead[3] = (byte)(_sequenceId % 256);
            tpUdhiHead[4] = (byte)(messageUCS2Count);

            for (var i = 0; i < messageUCS2Count; i++)
            {
                // message sequence
                tpUdhiHead[5] = (byte)(i + 1);

                byte[] msgContent;
                if (i != messageUCS2Count - 1)
                {
                    // not the last message
                    msgContent = new byte[MaxMessageByteLength];
                    Array.Copy(tpUdhiHead, msgContent, LongMessageHeadLength);
                    Array.Copy(messageUCS2, i * (MaxMessageByteLength - LongMessageHeadLength), msgContent, LongMessageHeadLength, MaxMessageByteLength - LongMessageHeadLength);
                }
                else
                {
                    // the last message
                    msgContent = new byte[tpUdhiHead.Length + messageUCS2Len - i * (MaxMessageByteLength - LongMessageHeadLength)];
                    Array.Copy(tpUdhiHead, msgContent, LongMessageHeadLength);
                    Array.Copy(messageUCS2, i * (MaxMessageByteLength - LongMessageHeadLength), msgContent, LongMessageHeadLength, messageUCS2Len - i * (MaxMessageByteLength - LongMessageHeadLength));
                }
                while (true)
                {
                    var resp = _SendAsync(extendedCode, receiver, Encoding.BigEndianUnicode.GetString(msgContent), true, needStatusReport);
                    if (!resp.WaitHandle.WaitOne(30000) || !(resp.Result is CmppSubmitResp)) return Cmpp30SendStatus.Timeout;
                    var submitReponse = ((CmppSubmitResp)resp.Result);
                    if (submitReponse.Result == 0)
                    {
                        messageIdList.Add(BitConverter.ToInt64(BitConverter.GetBytes(submitReponse.MsgId), 0));
                        break;
                    }
                    switch (submitReponse.Result)
                    {
                        case 8:
                            if (i == 0) return Cmpp30SendStatus.Congested;
                            Thread.Sleep(100);
                            continue;
                        case 10:
                        case 11:
                        case 12:
                        case 13:
                            return Cmpp30SendStatus.ConfigError;
                        case 4:
                            return Cmpp30SendStatus.MessageTooLong;
                        default:
                            return Cmpp30SendStatus.Unknown;
                    }
                }
            }
            return Cmpp30SendStatus.Success;
        }

        private CmppWindow _SendAsync(
            string extendedCode,
            string[] destinations,
            string text,
            bool isLongMessage, bool needStatusReport)
        {
            var submit = new CmppSubmit
            {
                // 信息内容。
                MsgContent = text,
                // 信息编码。
                MsgFmt = (byte)(_config.AttemptRemoveSignature ? CmppEncoding.Special : CmppEncoding.UCS2),
                // SP的服务代码，将显示在最终用户手机上的短信主叫号码。
                SrcId = _config.SpCode + extendedCode,
                // 接收短信的电话号码列表。
                DestTerminalId = destinations,
                // 业务标识（如：woodpack）。
                ServiceId = _config.ServiceId,
                // 是否要求返回状态报告。
                RegisteredDelivery = (byte)(needStatusReport ? 1 : 0),
                // 资费类别。
                FeeType = string.Format("{0:D2}", (int)FeeType.Free),
                // 计费用户。
                FeeUserType = (byte)FeeUserType.SP,
                // 被计费的号码（feeUserType 值为 FeeUser 时有效）。
                FeeTerminalId = _config.SpCode,
                // 被计费号码的真实身份（“真实号码”或“伪码”）。
                FeeTerminalType = 0,
                // 信息费（以“分”为单位，如：10 分代表 1角）。
                FeeCode = "05",
                // 点播业务的 linkId。
                LinkId = "",
                MsgLevel = 0,
                TPPId = 0,
                TPUdhi = (byte)(isLongMessage ? 1 : 0),
                MsgSrc = _config.GatewayUsername,
                ValidTime = "",
                AtTime = ""
            };
            var window = new CmppWindow
            {
                Message = submit,
                WaitHandle = new ManualResetEvent(false)
            };
            lock (_pendingMessages)
                _pendingMessages.Enqueue(window);
            return window;
        }

        private int _SentMessageCount(string content)
        {
            if (string.IsNullOrEmpty(content)) return 0;
            if (!string.IsNullOrEmpty(_config.GatewaySignature) && !_config.AttemptRemoveSignature)
                content += _config.GatewaySignature;
            if (_config.SendLongMessageAsShortMessages) return _SplitLongMessage(content).Count;
            var messageByteLen = Encoding.BigEndianUnicode.GetBytes(content).Length;
            return messageByteLen <= 140 ? 1 :
                (Encoding.BigEndianUnicode.GetBytes(content).Length - 1) / (MaxMessageByteLength - LongMessageHeadLength) + 1;
        }

        private List<string> _SplitLongMessage(string content)
        {
            var msgLen = MaxMessageByteLength;
            if (!_config.AttemptRemoveSignature && _config.PrepositiveGatewaySignature)
                msgLen -= Encoding.BigEndianUnicode.GetBytes(_config.GatewaySignature).Length;
            var result = new List<string>();
            var len = 0;
            var sb = new StringBuilder(150);
            foreach (var ch in content)
            {
                var charLen = Encoding.BigEndianUnicode.GetBytes(new[] { ch }).Length;
                if (len + charLen > msgLen)
                {
                    result.Add(sb.ToString());
                    len = 0;
                    sb = new StringBuilder(150);
                }
                len += charLen;
                sb.Append(ch);
            }
            return result;
        }

        private uint NextSequenceId { get { return _sequenceId++; } }

        private void OnCmppMessageReceive(object sender, CmppMessageReceiveEvent e)
        {
            _lastTransferTime = DateTime.Now;
            if (Status == Cmpp30ClientStatus.Authenticating)
            {
                if (e.Header.CommandId != CmppConstants.CommandCode.ConnectResp || !(e.Message is CmppConnectResp))
                {
                    Status = Cmpp30ClientStatus.Disconnected;
                    StatusText = "Unexpected response";
                    return;
                }
                var response = (CmppConnectResp)e.Message;
                if (response.Status != 0)
                {
                    Status = Cmpp30ClientStatus.AuthenticationFailed;
                    switch (response.Status)
                    {
                        case 1:
                            StatusText = "消息结构错误";
                            return;
                        case 2:
                            StatusText = "非法源地址";
                            return;
                        case 3:
                            StatusText = "认证失败";
                            return;
                        case 4:
                            StatusText = "版本错误";
                            return;
                        default:
                            StatusText = string.Format("其它错误({0})", response.Status);
                            return;
                    }
                }
                Status = Cmpp30ClientStatus.Connected;
                StatusText = "";
                _transport.Send(NextSequenceId, new CmppActiveTest());
                return;
            }

            try
            {
                switch (e.Header.CommandId)
                {
                    case CmppConstants.CommandCode.Deliver:
                        var deliver = (CmppDeliver)e.Message;
                        _transport.Send(e.Header.SequenceId, new CmppDeliverResp
                        {
                            MsgId = deliver.MsgId,
                            Result = 0
                        });
                        switch (deliver.RegisteredDelivery)
                        {
                            case 0:
                                // mo message
                                if (OnMessageReceive != null) OnMessageReceive(this, new ReceiveEventArgs
                                {
                                    Content = deliver.MsgContent,
                                    Source = deliver.SrcTerminalId,
                                    MessageId = BitConverter.ToInt64(BitConverter.GetBytes(deliver.MsgId), 0),
                                    Destination = deliver.DestId
                                });
                                break;
                            case 1:
                                // message report
                                var report = deliver.GetReport();
                                if (OnMessageReport != null) OnMessageReport(this, new ReportEventArgs
                                {
                                    MessageId = (long)report.MsgId,
                                    StatusText = report.Stat,
                                    Destination = report.DestTerminalId,
                                });
                                break;
                        }
                        break;
                    case CmppConstants.CommandCode.ActiveTest:
                        _transport.Send(e.Header.SequenceId, new CmppActiveTestResp());
                        break;
                    case CmppConstants.CommandCode.Error:
                    case CmppConstants.CommandCode.Terminate:
                        Status = Cmpp30ClientStatus.Disconnected;
                        StatusText = "Server respond with error";
                        _Disconnect();
                        break;
                    case CmppConstants.CommandCode.SubmitResp:
                        lock (_cmppWindows)
                        {
                            if (_cmppWindows.ContainsKey(e.Header.SequenceId))
                            {
                                _cmppWindows[e.Header.SequenceId].Result = e.Message;
                                _cmppWindows[e.Header.SequenceId].WaitHandle.Set();
                                _cmppWindows.Remove(e.Header.SequenceId);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[{0}] Unable to handle CMPP receive package. Error: {1}.", _config.SpCode, ex);
            }
        }

        /// <summary>
        /// Get the current status of the client.
        /// </summary>
        public Cmpp30ClientStatus Status { get; private set; }
        /// <summary>
        /// Get the current status text of the client.
        /// </summary>
        public string StatusText { get; private set; }

        private void _ConnectInternal()
        {
            if (Status != Cmpp30ClientStatus.Disconnected) return;
            if (_transport.Connected) _transport.Disconnect();

            Status = Cmpp30ClientStatus.Connecting;
            StatusText = "";

            _transport.Connect();
            if (!_transport.Connected)
            {
                Status = Cmpp30ClientStatus.Disconnected;
                StatusText = "Fail to connect";
            }

            Status = Cmpp30ClientStatus.Authenticating;
            try
            {
                var timestamp = DateTime.Now;
                var connect = new CmppConnect
                {
                    TimeStamp = uint.Parse(string.Format("{0:MMddhhmmss}", timestamp)),
                    AuthenticatorSource = CreateAuthenticatorSource(timestamp),
                    Version = CmppConstants.Version,
                    SourceAddress = _config.GatewayUsername,
                };

                _lastTransferTime = DateTime.Now;
                if (_transport.Send(NextSequenceId, connect)) return;

                _Disconnect();
                StatusText = "Fail to send";
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error sending authenticating. error: {0}.", ex);
                _Disconnect();
                StatusText = ex.Message;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        protected override void NextLoop()
        {
            switch (Status)
            {
                case Cmpp30ClientStatus.Disconnected:
                    _ConnectInternal();
                    if (Status == Cmpp30ClientStatus.Disconnected)
                        Thread.Sleep(3000);
                    break;
                case Cmpp30ClientStatus.Authenticating:
                    if ((DateTime.Now - _lastTransferTime).TotalSeconds > 10)
                    {
                        _Disconnect();
                        StatusText = "认证超时";
                        Thread.Sleep(3000);
                    }
                    break;
                case Cmpp30ClientStatus.Connected:
                    _Send();
                    break;
                default:
                    Thread.Sleep(1000);
                    break;
            }
        }

        private void _Send()
        {
            // remove timeout waiting packages.
            lock (_cmppWindows)
            {
                var removeList = new List<uint>();
                foreach (var cmppWindow in _cmppWindows)
                {
                    if ((DateTime.Now - cmppWindow.Value.SendTime).TotalSeconds <= 30) continue;
                    cmppWindow.Value.WaitHandle.Set();
                    removeList.Add(cmppWindow.Key);
                }
                foreach (var id in removeList) _cmppWindows.Remove(id);

                // reset connection if connection is suspended.
                if (removeList.Count > 0 && (DateTime.Now - _lastTransferTime).TotalSeconds > 10)
                {
                    foreach (var cmppWindow in _cmppWindows) cmppWindow.Value.WaitHandle.Set();
                    _cmppWindows.Clear();
                    _Disconnect();
                    return;
                }
            }
            if (_cmppWindows.Count == 0 && _pendingMessages.Count == 0)
            {
                if ((DateTime.Now - _lastTransferTime).TotalSeconds > 10)
                {
                    _transport.Send(NextSequenceId, new CmppActiveTest());
                    _lastTransferTime = DateTime.Now;
                }
                Thread.Sleep(100);
                return;
            }
            if (_cmppWindows.Count >= WindowSize || _pendingMessages.Count == 0)
            {
                Thread.Sleep(50);
                return;
            }
            // continue sending messages
            while (true)
            {
                CmppWindow window;
                lock (_cmppWindows)
                {
                    if (_cmppWindows.Count >= WindowSize) break;
                    lock (_pendingMessages)
                    {
                        if (_pendingMessages.Count == 0) break;
                        window = _pendingMessages.Dequeue();
                    }
                    window.SequenceId = NextSequenceId;
                    window.SendTime = DateTime.Now;
                    _cmppWindows.Add(window.SequenceId, window);
                }
                if (!_transport.Send(window.SequenceId, window.Message))
                {
                    // transfer error.
                    lock (_cmppWindows)
                        _cmppWindows.Remove(window.SequenceId);
                    _Disconnect();
                    return;
                }
            }
        }

        /// <summary>
        /// Dispose client
        /// </summary>
        protected override void _OnStop()
        {
            base._OnStop();
            _transport.Dispose();
            Status = Cmpp30ClientStatus.Disposed;
        }

        private void _Disconnect()
        {
            _transport.Disconnect();
            Status = Cmpp30ClientStatus.Disconnected;

            lock (_cmppWindows)
            {
                if (_cmppWindows.Count > 0)
                    foreach (var cmppWindow in _cmppWindows)
                    {
                        _pendingMessages.Enqueue(cmppWindow.Value);
                    }
                _cmppWindows.Clear();
            }
        }


        /// <summary>
        /// On receive message report event.
        /// </summary>
        public event EventHandler<ReportEventArgs> OnMessageReport;

        /// <summary>
        /// On receive mo message event.
        /// </summary>
        public event EventHandler<ReceiveEventArgs> OnMessageReceive;
        /// <summary>
        /// 计算 CMPP_CONNECT 包的 AuthenticatorSource 字段。
        /// </summary>
        /// <remarks>
        /// MD5(Source_Addr + 9字节的0 + shared secret + timestamp);
        /// </remarks>
        private byte[] CreateAuthenticatorSource(DateTime timestamp)
        {
            var btContent = new byte[25 + _config.GatewayPassword.Length];
            Array.Clear(btContent, 0, btContent.Length);

            // Source_Addr，SP的企业代码（6位）。
            var iPos = 0;
            foreach (var ch in _config.GatewayUsername)
            {
                btContent[iPos] = (byte)ch;
                iPos++;
            }

            // 9字节的0。
            iPos += 9;

            // password，由 China Mobile 提供（长度不固定）。
            foreach (var ch in _config.GatewayPassword)
            {
                btContent[iPos] = (byte)ch;
                iPos++;
            }

            // 时间戳（10位）。
            foreach (var ch in string.Format("{0:MMddhhmmss}", timestamp))
            {
                btContent[iPos] = (byte)ch;
                iPos++;
            }
            return new MD5CryptoServiceProvider().ComputeHash(btContent);
        }

        private class CmppWindow
        {
            public uint SequenceId { get; set; }
            public DateTime SendTime { get; set; }
            public ICmppMessage Message { get; set; }
            public EventWaitHandle WaitHandle { get; set; }
            public ICmppMessage Result { get; set; }
        }
    }
}
