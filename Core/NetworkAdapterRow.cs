using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Valve.Core;

public sealed class NetworkAdapterRow : Observable
{
    private string _status = "Down";
    private string _ip = "-";
    private string _gateway = "-";
    private string _speed = "-";
    private double _inRate;
    private double _outRate;
    private long _bytesIn;
    private long _bytesOut;

    public NetworkAdapterRow(NetworkInterface nic)
    {
        Id = nic.Id;
        Name = nic.Name;
        Description = nic.Description;
        Type = nic.NetworkInterfaceType.ToString();
        Update(nic, 1.0);
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Type { get; }

    public string Status { get => _status; set => Set(ref _status, value); }
    public string Ip { get => _ip; set => Set(ref _ip, value); }
    public string Gateway { get => _gateway; set => Set(ref _gateway, value); }
    public string Speed { get => _speed; set => Set(ref _speed, value); }

    public double InRate { get => _inRate; set { if (Set(ref _inRate, value)) Raise(nameof(InRateText)); } }
    public double OutRate { get => _outRate; set { if (Set(ref _outRate, value)) Raise(nameof(OutRateText)); } }

    public string InRateText => Fmt.Rate(_inRate);
    public string OutRateText => Fmt.Rate(_outRate);

    public string BytesInText => Fmt.Bytes(_bytesIn);
    public string BytesOutText => Fmt.Bytes(_bytesOut);

    public void Update(NetworkInterface nic, double dt)
    {
        Status = nic.OperationalStatus.ToString();

        try
        {
            var ipProps = nic.GetIPProperties();
            var unicast = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            Ip = unicast?.Address.ToString() ?? "-";

            var gatewayAddr = ipProps.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            Gateway = gatewayAddr?.Address.ToString() ?? "-";

            long speedBits = nic.Speed;
            Speed = speedBits > 0 ? $"{speedBits / 1_000_000} Mbps" : "-";

            var stats = nic.GetIPv4Statistics();
            long newBytesIn = stats.BytesReceived;
            long newBytesOut = stats.BytesSent;

            if (_bytesIn > 0 && newBytesIn >= _bytesIn && dt > 0)
            {
                InRate = (newBytesIn - _bytesIn) / dt;
            }
            if (_bytesOut > 0 && newBytesOut >= _bytesOut && dt > 0)
            {
                OutRate = (newBytesOut - _bytesOut) / dt;
            }

            _bytesIn = newBytesIn;
            _bytesOut = newBytesOut;

            Raise(nameof(BytesInText));
            Raise(nameof(BytesOutText));
        }
        catch
        {
            // Virtual / disabled adapters may throw when querying IPv4 statistics
        }
    }
}
