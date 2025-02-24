﻿using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using Mirror.OculusP2P;
using Oculus.Platform;
using Oculus.Platform.Models;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.Management;
using mixpanel;
using static TableBrowserMenu;
using Application = UnityEngine.Application;
#if UNITY_EDITOR
using UnityEditor;

#endif

public class NetworkManagerGame : NetworkManager
{
    public bool connecting;

    public delegate void LoggedInHandler(User user);

    public delegate void ServerAddPlayerHandler(NetworkConnection conn, bool isLanOnly);

    private const float HardKickDelay = 0.5f;
    private const float PingInternetEvery = 2.0f;
    private const int PingInternetRequestTimeout = 4;
    private const float PingInternetTimeoutBeforeDisconnect = 10f;
    private const float UnpauseGracePeriod = 5.0f;
    public static EventHandler<NetworkConnection> ServerDisconnect;
    public static EventHandler<NetworkConnection> ClientConnect;
    public static EventHandler<NetworkConnection> ClientDisconnect;
    public static EventHandler InternetTimeout;
    public static EventHandler InternetReconnect;

    public bool RoomFull => PlayerInfos.Count >= maxConnections;

    public OculusTransport oculusTransport;
    public bool roomOpen;
    public bool publicRoom;

    public GameObject networkHandLeftPrefab;
    public GameObject networkHandRightPrefab;

    public bool visualizeAuthority;

    public bool serverOnlyIfEditor;

    public int BuildId;

    public readonly Dictionary<NetworkConnection, PlayerInfo> PlayerInfos = new Dictionary<NetworkConnection, PlayerInfo>();
    private int _internetBadMessageId;

    private double _lastGoodPingReceived = Mathf.NegativeInfinity;
    private float _lastPingNet = Mathf.NegativeInfinity;
    private float _lastUnpaused = Mathf.NegativeInfinity;

    private bool _roomJoinInProgress;
    private bool _shouldDisconnect;

    public EventHandler InfoChange;
    public User User;

    private Action<NetworkConnection, UserInfoMessage> UserInfoEvent;

    public string FullVersion => Version + "b" + BuildId;

    public string Version => Application.version;

    public bool Online => IsInternetGood();

    public static NetworkManagerGame Singleton { get; private set; }

    public override void Awake()
    {
        base.Awake();

        if (Singleton == null)
        {
            Singleton = this;
        }
    }

    public override void Start()
    {
        base.Start();

        // todo make these into EventHandler
        TableBrowserMenu.JoinRoom += HandleJoinRoom;
        LeaveRoom += HandleLeaveRoom;
        KickConnectionId += HandleKickConnectionId;
        OpenRoom += HandleOpenRoom;
        CloseRoom += HandleCloseRoom;

#if UNITY_EDITOR
        EditorApplication.pauseStateChanged += HandleEditorPauseChange;
#endif

        if (Application.isEditor && !serverOnlyIfEditor)
        {
            StartCoroutine(StartXR());
        }

        Core.AsyncInitialize().OnComplete(InitCallback);

        _lastUnpaused = Time.realtimeSinceStartup;
    }

    public void Update()
    {
        HandlePingUpdate();

        if (!UnpausedRecently() && !IsInternetGood() && _internetBadMessageId == 0)
        {
            InternetTimeout?.Invoke(this, new EventArgs());
            _internetBadMessageId = MessageStack.Singleton.AddMessage("Internet Disconnected", MessageStack.MessageType.Bad, Mathf.Infinity);
        }

        if (User != null && mode == NetworkManagerMode.Offline && !connecting)
        {
            BonsaiLog("StartHost");
            //StartHost();
            StartCoroutine(DelayStartHost());
        }
    }

    private IEnumerator DelayStartHost()
    {
        connecting = true;
        InfoChange?.Invoke(this, new EventArgs());
        yield return new WaitForSeconds(1f);
        StartHost();
        connecting = false;
        InfoChange?.Invoke(this, new EventArgs());
    }

    public override void LateUpdate()
    {
        if (_shouldDisconnect)
        {
            _shouldDisconnect = false;
            StopClient();
        }

        base.LateUpdate();
    }

    public void OnApplicationPause(bool pause)
    {
        HandlePause(pause);
        if (pause)
        {
            Mixpanel.Track("Session Stop or Pause");
            Mixpanel.Flush();
        }
        else
        {
            Mixpanel.Track("Unpause");
            Mixpanel.StartTimedEvent("Session Stop or Pause");
        }
    }

    public override void OnApplicationQuit()
    {
        Mixpanel.Track("Application Quit");
        Mixpanel.Track("Session Stop or Pause");
        Mixpanel.Flush();
        Mixpanel.Reset();
        base.OnApplicationQuit();
        StopXR();
    }

    public string UserName()
    {
        if (User != null)
        {
            return User.OculusID;
        }

        return "Player";
    }

#if UNITY_EDITOR
    private void HandleEditorPauseChange(PauseState pause)
    {
        switch (pause)
        {
            case PauseState.Paused:
                HandlePause(true);
                break;
            case PauseState.Unpaused:
                HandlePause(false);
                break;
        }
    }
#endif

    private void HandlePause(bool pause)
    {
        if (pause)
        {
            switch (mode)
            {
                case NetworkManagerMode.ClientOnly:
#if !UNITY_EDITOR
                    HandleLeaveRoom();
#endif
                    break;
                case NetworkManagerMode.ServerOnly:
                case NetworkManagerMode.Host:
                    roomOpen = false;
                    DisconnectAllClients();
                    break;
            }
        }
        else
        {
            _lastUnpaused = Time.realtimeSinceStartup;
        }
    }

    private bool UnpausedRecently()
    {
        return Time.realtimeSinceStartup - _lastUnpaused < UnpauseGracePeriod;
    }

    public event ServerAddPlayerHandler ServerAddPlayer;

    private void HandleCloseRoom()
    {
        BonsaiLog("CloseRoom");
        roomOpen = false;
        StartCoroutine(KickClients());
        InfoChange?.Invoke(this, new EventArgs());
    }

    private void HandleOpenRoom(bool isPublicRoom)
    {
        BonsaiLog("OpenRoom");
        roomOpen = true;
        publicRoom = isPublicRoom;
        InfoChange?.Invoke(this, new EventArgs());
        var roomType = publicRoom ? "Public" : "Private";
        Mixpanel.Track($"Open {roomType} Room");
    }

    private void HandleKickConnectionId(int id)
    {
        StartCoroutine(KickClient(id));
    }

    private void HandleLeaveRoom()
    {
        BonsaiLog("HandleLeaveRoom");
        StopClient();
        InfoChange?.Invoke(this, new EventArgs());
    }

    private void HandleJoinRoom(RoomData roomData)
    {
        JoinRoom(roomData);
    }

    private IEnumerator StartXR()
    {
        BonsaiLog("Initializing XR");
        yield return XRGeneralSettings.Instance.Manager.InitializeLoader();

        if (XRGeneralSettings.Instance.Manager.activeLoader == null)
        {
            BonsaiLogError("Initializing XR Failed. Check Editor or Player log for details.");
        }
        else
        {
            BonsaiLog("Starting XR");
            XRGeneralSettings.Instance.Manager.StartSubsystems();
        }
    }

    private IEnumerator DelayStartCLient()
    {
        connecting = true;
        InfoChange?.Invoke(this, new EventArgs());
        yield return new WaitForSeconds(1f);
        StartClient();
        connecting = false;
        InfoChange?.Invoke(this, new EventArgs());
    }

    private void JoinRoom(RoomData roomData)
    {
        Mixpanel.Track("JoinRoom Begin");
        roomOpen = false;
        connecting = true;
        InfoChange?.Invoke(this, new EventArgs());
        BonsaiLog($"JoinRoom ({roomData.network_address})");
        if (!OculusCommon.CanParseId(roomData.network_address))
        {
            BonsaiLogError("Can't parse network address");
        }

        if (mode == NetworkManagerMode.Host)
        {
            BonsaiLog("Stopping host before joining room");
            StopHost();
        }

        if (mode == NetworkManagerMode.ClientOnly)
        {
            BonsaiLog("Stopping client before joining room");
            StopClient();
        }

        if (mode == NetworkManagerMode.Offline)
        {
            Mixpanel.Track("JoinRoom StartClient");
            networkAddress = roomData.network_address;
            BonsaiLog("StartClient");
            //StartClient();
            StartCoroutine(DelayStartCLient());
        }
        else
        {
            Mixpanel.Track("JoinRoom Error");
            connecting = false;
            BonsaiLogError($"Did not get into offline state before joining room ({mode})");
        }

        InfoChange?.Invoke(this, new EventArgs());
    }

    private void StopXR()
    {
        if (XRGeneralSettings.Instance.Manager.isInitializationComplete)
        {
            BonsaiLog("Stopping XR");
            XRGeneralSettings.Instance.Manager.StopSubsystems();
            XRGeneralSettings.Instance.Manager.DeinitializeLoader();
        }
    }

    private void BonsaiLog(string msg)
    {
        global::BonsaiLog.Log(msg, "BonsaiNetwork");
    }

    private void BonsaiLogWarning(string msg)
    {
        global::BonsaiLog.LogWarning(msg, "BonsaiNetwork");
    }

    private void BonsaiLogError(string msg)
    {
        global::BonsaiLog.LogError(msg, "BonsaiNetwork");
    }

    private IEnumerator KickClients()
    {
        RequestDisconnectAllClients();

        yield return new WaitForSeconds(HardKickDelay);

        DisconnectAllClients();
    }

    private void RequestDisconnectAllClients()
    {
        foreach (var conn in NetworkServer.connections.Values)
        {
            if (conn.connectionId != NetworkConnection.LocalConnectionId)
            {
                RequestDisconnectClient(conn);
            }
        }
    }

    private void DisconnectAllClients()
    {
        foreach (var conn in NetworkServer.connections.Values)
        {
            if (conn.connectionId != NetworkConnection.LocalConnectionId)
            {
                DisconnectClient(conn);
            }
        }
    }

    private void DisconnectClient(NetworkConnectionToClient conn)
    {
        conn.Disconnect();
    }

    private void RequestDisconnectClient(NetworkConnectionToClient conn)
    {
        conn.Send(new ShouldDisconnectMessage());
    }

    private bool IsInternetGood()
    {
        return Time.realtimeSinceStartup - _lastGoodPingReceived < PingInternetTimeoutBeforeDisconnect;
    }

    private IEnumerator CheckInternetAccess()
    {
        var uwr = new UnityWebRequest("http://google.com/generate_204") {timeout = PingInternetRequestTimeout};
        yield return uwr.SendWebRequest();
        if (uwr.responseCode == 204 && Time.realtimeSinceStartup > _lastGoodPingReceived)
        {
            _lastGoodPingReceived = Time.realtimeSinceStartup;
            if (_internetBadMessageId != 0)
            {
                MessageStack.Singleton.PruneMessageID(_internetBadMessageId);
                MessageStack.Singleton.AddMessage("Internet Good", MessageStack.MessageType.Good);
                _internetBadMessageId = 0;
                InternetReconnect?.Invoke(this, new EventArgs());
            }
        }
    }

    private void HandlePingUpdate()
    {
        if (Time.realtimeSinceStartup - _lastPingNet > PingInternetEvery)
        {
            _lastPingNet = Time.realtimeSinceStartup;
            StartCoroutine(CheckInternetAccess());
        }
    }

    public override void OnClientConnect(NetworkConnection conn)
    {
        base.OnClientConnect(conn);

        BonsaiLog($"OnClientConnect (id={conn.connectionId}) {conn.isReady}");

        NetworkClient.RegisterHandler<ShouldDisconnectMessage>(OnShouldDisconnect);

        if (User != null)
        {
            NetworkClient.Send(new UserInfoMessage {ID = User.ID, OculusId = User.OculusID});
        }
        else
        {
            BonsaiLogWarning("Did not fetch oculus id before joining room");
        }

        if (!NetworkServer.active)
        {
            Mixpanel.Track("OnClientConnect");
        }
        ClientConnect?.Invoke(this, conn);
        InfoChange?.Invoke(this, new EventArgs());
    }

    public override void OnClientDisconnect(NetworkConnection conn)
    {
        BonsaiLog("OnClientDisconnect");

        NetworkClient.UnregisterHandler<ShouldDisconnectMessage>();

        ClientDisconnect?.Invoke(this, conn);

        base.OnClientDisconnect(conn);
        InfoChange?.Invoke(this, new EventArgs());
    }

    public override void OnStartServer()
    {
        NetworkTime.Reset();
        base.OnStartServer();
        PlayerInfos.Clear();
        UserInfoEvent = (conn, userInfo) =>
        {
            if (PlayerInfos.TryGetValue(conn, out _))
            {
                BonsaiLog($"Update connection ({conn.connectionId}) with ({userInfo.OculusId}) ({userInfo.ID})");
                PlayerInfos[conn].OculusId = userInfo.OculusId;
                PlayerInfos[conn].ID = userInfo.ID;
                PlayerInfos[conn].Ok = true;
            }
            else
            {
                BonsaiLogWarning($"No connection in PlayerInfos to update with with ({userInfo.OculusId}) ({userInfo.ID})");
            }
        };
        NetworkServer.RegisterHandler(UserInfoEvent, false);
    }

    public override void OnStopServer()
    {
        base.OnStopServer();
        NetworkServer.UnregisterHandler<UserInfoMessage>();
    }

    private void OnShouldDisconnect(ShouldDisconnectMessage _)
    {
        BonsaiLog("ShouldDisconnect");
        _shouldDisconnect = true;
    }

    private IEnumerator DelayKickIfNotReport(NetworkConnection conn)
    {
        yield return new WaitForSeconds(2);

        if (PlayerInfos.TryGetValue(conn, out PlayerInfo playerInfo))
        {
            if (playerInfo.Ok)
            {
                yield break;
            }
        }

        BonsaiLogWarning($"Kicking connection ({conn.connectionId}) for not reporting after joining");
        conn.Disconnect();
        PlayerInfos.Remove(conn);
    }


    public override void OnServerAddPlayer(NetworkConnection conn)
    {
        BonsaiLog("ServerAddPlayer");

        var openSpot = OpenSpotId();
        var playerInfo = new PlayerInfo
        {
            Spot = openSpot, OculusId = "None", ID = 0
        };
        PlayerInfos.Add(conn, playerInfo);
        DelayKickIfNotReport(conn);

        SpawnPlayer(conn, openSpot);

        // todo
        var isLanOnly = false;
        ServerAddPlayer?.Invoke(conn, isLanOnly);
        InfoChange?.Invoke(this, new EventArgs());
    }

    private int OpenSpotId()
    {
        var spots = new List<int>();
        for (var i = 0; i < SpotManager.Instance.TotalSpots; i++)
        {
            spots.Add(i);
        }

        foreach (var info in PlayerInfos.Values)
        {
            spots.Remove(info.Spot);
        }

        if (spots.Count > 0)
        {
            return spots[0];
        }

        BonsaiLogError("No open spot");
        return 0;
    }

    private void SpawnPlayer(NetworkConnection conn, int spot)
    {
        //instantiate player
        var startPos = GetStartPosition();
        var player = startPos != null ? Instantiate(playerPrefab, startPos.position, startPos.rotation) : Instantiate(playerPrefab);

        //setup player and spawn hands
        var networkVRPlayer = player.GetComponent<NetworkVRPlayer>();

        var leftHand = Instantiate(networkHandLeftPrefab, startPos.position, startPos.rotation);
        var lid = leftHand.GetComponent<NetworkIdentity>();

        var rightHand = Instantiate(networkHandRightPrefab, startPos.position, startPos.rotation);
        var rid = rightHand.GetComponent<NetworkIdentity>();

        NetworkServer.Spawn(leftHand, conn);
        NetworkServer.Spawn(rightHand, conn);
        networkVRPlayer.SetHandIdentities(new NetworkIdentityReference(lid), new NetworkIdentityReference(rid));
        networkVRPlayer.SetSpot(spot);

        NetworkServer.AddPlayerForConnection(conn, player);
        var pid = player.GetComponent<NetworkIdentity>();
        leftHand.GetComponent<NetworkHand>().ownerIdentity = new NetworkIdentityReference(pid);
        rightHand.GetComponent<NetworkHand>().ownerIdentity = new NetworkIdentityReference(pid);
    }

    public override void OnServerDisconnect(NetworkConnection conn)
    {
        var tmp = new HashSet<NetworkIdentity>(conn.clientOwnedObjects);
        foreach (var identity in tmp)
        {
            var autoAuthority = identity.GetComponent<AutoAuthority>();
            if (autoAuthority != null)
            {
                if (autoAuthority.InUse)
                {
                    autoAuthority.SetInUseBy(0);
                }

                autoAuthority.ServerForceNewOwner(uint.MaxValue, NetworkTime.time);
                //identity.RemoveClientAuthority();
            }
            else if (!identity.gameObject.CompareTag("NetworkHand") && !identity.gameObject.CompareTag("NetworkHead"))
            {
                identity.RemoveClientAuthority();
            }
        }

        if (!conn.isAuthenticated)
        {
            return;
        }

        ServerDisconnect?.Invoke(this, conn);

        PlayerInfos.Remove(conn);

        // call the base after the ServerDisconnect event otherwise null reference gets passed to subscribers
        base.OnServerDisconnect(conn);

        InfoChange?.Invoke(this, new EventArgs());
    }

    private void InitCallback(Message<PlatformInitialize> msg)
    {
        if (msg.IsError)
        {
            TerminateWithError(msg);
            return;
        }

        Users.GetLoggedInUser().OnComplete(HandleGetLoggedInUser);
    }

    private void TerminateWithError(Message msg)
    {
        BonsaiLogError($"Error {msg.GetError().Code} {msg.GetError().HttpCode} {msg.GetError().Message}");
        Application.Quit();
    }

    private void HandleGetLoggedInUser(Message<User> msg)
    {
        if (msg.IsError)
        {
            TerminateWithError(msg);
            return;
        }

        User = msg.Data;
        oculusTransport.LoggedIn(User);
        LoggedIn?.Invoke(User);

        var oculusId = User.OculusID;
        Mixpanel.Reset();
        Mixpanel.Identify(oculusId);
        Mixpanel.People.Name = oculusId;
        Mixpanel.People.Email = oculusId + "@BonsaiDesk.com";
        Mixpanel.Track("Login");

        Users.GetUserProof().OnComplete(HandleProofMessage);
    }
    
    private void HandleProofMessage(Message<UserProof> msg)
    {
        if (msg.IsError)
        {
            TerminateWithError(msg);
            return;
        }

        var build = "mobile";
        
        #if UNITY_EDITOR
        build = "desktop";
        #elif UNITY_ANDROID && !UNITY_EDITOR
        build = "mobile";
        #endif
            
        var release = "PRODUCTION";
        #if UNITY_EDITOR || DEVELOPMENT_BUILD
        release = "DEVELOPMENT";
        #endif
            

        var authInfo = new AuthInfo()
        {
            UserId = User.ID,
            Nonce = msg.Data.Value,
            Build = build,
            Release = release
        };
        
        TableBrowserMenu.Singleton.PostAuthInfo(authInfo);
    }

    public event LoggedInHandler LoggedIn;

    public string GetMyNetworkAddress()
    {
        if (User != null)
        {
            return User.ID.ToString();
        }

        return "";
    }

    public string GetNetworkAddress()
    {
        if (mode == NetworkManagerMode.ClientOnly)
        {
            return networkAddress;
        }

        if (User != null)
        {
            return User.ID.ToString();
        }

        return "";
    }

    private IEnumerator KickClient(int id)
    {
        foreach (var conn in NetworkServer.connections.Values)
        {
            if (conn.connectionId == id)
            {
                RequestDisconnectClient(conn);
            }
        }

        yield return new WaitForSeconds(HardKickDelay);

        foreach (var conn in NetworkServer.connections.Values)
        {
            if (conn.connectionId == id)
            {
                DisconnectClient(conn);
            }
        }
    }

    [Serializable]
    public class PlayerInfo
    {
        public int Spot;
        public string OculusId;
        public ulong ID;
        public bool Ok;
    }

    private struct ShouldDisconnectMessage : NetworkMessage
    {
    }

    private struct UserInfoMessage : NetworkMessage
    {
        public string OculusId;
        public ulong ID;
    }
}