using System.Net.NetworkInformation;

namespace Lance.Agent.Services;

internal interface ITcpProbe
{
    bool HasEstablishedConnection(int port);
}

internal sealed class TcpProbe : ITcpProbe
{
    public bool HasEstablishedConnection(int port)
    {
        TcpConnectionInformation[] connections =
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();

        foreach (TcpConnectionInformation conn in connections)
        {
            if (conn.LocalEndPoint.Port == port && conn.State == TcpState.Established)
            {
                return true;
            }
        }

        return false;
    }
}
