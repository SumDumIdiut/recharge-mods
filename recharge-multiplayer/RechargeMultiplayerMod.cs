using System;
using Recharge.ModApi;

public class RechargeMultiplayerMod : IRechargeMod
{
    public string Id => "recharge.multiplayer";
    public string DisplayName => "DOTnet";
    public Version Version => new Version(1, 7, 9);

    public void OnLoad(IRechargeHost host)
    {
        MpNetworkManager.Host = host;
        MpMenuBuilder.Install(host.PauseMenu);

        var hostPanelGo = new UnityEngine.GameObject("HostPanelController");
        UnityEngine.Object.DontDestroyOnLoad(hostPanelGo);
        var hostPanel = hostPanelGo.AddComponent<HostPanelController>();
        hostPanel.Init(host);

        PauseMenuHelper.OnMenuReady(host, menu =>
        {
            MpMenuBuilder.Install(menu);
            hostPanel.InstallMenuRow(menu);
        });
    }

    public void OnUnload() { }
}
