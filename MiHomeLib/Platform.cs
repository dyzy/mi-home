﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiHomeLib.Commands;
using MiHomeLib.Devices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MiHomeLib
{
    public class Platform : IDisposable
    {
        private Gateway _gateway;
        private readonly string _gatewaySid;
        private static UdpTransport _transport;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly List<MiHomeDevice> _devicesList = new List<MiHomeDevice>();

        private readonly Dictionary<string, Func<string, MiHomeDevice>> _devicesMap = new Dictionary<string, Func<string, MiHomeDevice>>
        {
            {"sensor_ht", sid => new ThSensor(sid)},
            {"motion", sid => new MotionSensor(sid)},
            {"plug", sid => new SocketPlug(sid, _transport)},
            {"magnet", sid => new DoorWindowSensor(sid)},
        };

        private readonly Dictionary<string, Action<ResponseCommand>> _commandsToActions;

        public Platform(string gatewayPassword = null, string gatewaySid = null)
        {
            _commandsToActions = new Dictionary<string, Action<ResponseCommand>>
            {
                { "get_id_list_ack", DiscoverGatewayAndDevices},
                { "read_ack", UpdateDevicesList},
                { "heartbeat", ProcessHeartbeat},
                { "report", ProcessReport},
            };

            _gatewaySid = gatewaySid;

            _transport = new UdpTransport(gatewayPassword);

            Task.Run(() => StartReceivingMessages(_cts.Token), _cts.Token);

            _transport.SendCommand(new DiscoverGatewayCommand());
        }

        public List<MiHomeDevice> GetDevices()
        {
            return _devicesList;
        }

        public Gateway GetGateway()
        {
            return _gateway;
        }

        public T GetDeviceBySid<T>(string sid) where T : MiHomeDevice
        {
            var device = _devicesList.FirstOrDefault(x => x.Sid == sid);

            if (device == null) return null;

            if (device is T)
            {
                return _devicesList.First(x => x.Sid == sid) as T;
            }

            throw new InvalidCastException($"Device with sid '{sid}' cannot be converted to {nameof(T)}");
        }

        public IEnumerable<T> GetDevicesByType<T>() where T : MiHomeDevice
        {
            return _devicesList.OfType<T>();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _transport?.Dispose();
        }

        private async Task StartReceivingMessages(CancellationToken ct)
        {
            // Receive messages
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var str = await _transport.ReceiveAsync().ConfigureAwait(false);
                
                    //Console.WriteLine(str);

                    var respCmd = JsonConvert.DeserializeObject<ResponseCommand>(str);

                    if (_commandsToActions.ContainsKey(respCmd.Cmd))
                    {
                        _commandsToActions[respCmd.Cmd](respCmd);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    Console.WriteLine(e.StackTrace);
                }
            }
        }

        private void ProcessReport(ResponseCommand command)
        {
            _devicesList.FirstOrDefault(x => x.Sid == command.Sid)?.ParseData(command.Data);
        }

        private void ProcessHeartbeat(ResponseCommand command)
        {
            if (_gateway != null && command.Sid == _gateway.Sid)
            {
                _transport.SetToken(command.Token);
            }
            else
            {
                _devicesList.FirstOrDefault(x => x.Sid == command.Sid)?.ParseData(command.Data);
            }
        }

        private void UpdateDevicesList(ResponseCommand cmd)
        {
            var device = _devicesList.FirstOrDefault(x => x.Sid == cmd.Sid);

            if (device != null) return;

            device = _devicesMap[cmd.Model](cmd.Sid);

            if (cmd.Data != null) device.ParseData(cmd.Data);

            _devicesList.Add(device);
        }

        private void DiscoverGatewayAndDevices(ResponseCommand cmd)
        {
            if (_gatewaySid == null)
            {
                if (_gateway == null)
                {
                    _gateway = new Gateway(cmd.Sid, _transport);
                }

                _transport.SetToken(cmd.Token);
            }
            else if (_gatewaySid == cmd.Sid)
            {
                _gateway = new Gateway(cmd.Sid, _transport);
                _transport.SetToken(cmd.Token);
            }

            if (_gateway == null) return;

            foreach (var sid in JArray.Parse(cmd.Data))
            {
                _transport.SendCommand(new ReadDeviceCommand(sid.ToString()));
                Thread.Sleep(100); // need some time in order not to loose message
            }

            //TODO: if device was removed we need to know it somehow
        }
    }
}