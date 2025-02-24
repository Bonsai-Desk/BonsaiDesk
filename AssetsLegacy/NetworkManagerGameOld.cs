﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dissonance;
using Mirror;
using NobleConnect.Mirror;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.XR.Management;

public class NetworkManagerGameOld : NobleNetworkManager {
	public enum ConnectionState {
		RelayError,
		Loading,
		Hosting,
		ClientConnecting,
		ClientConnected
	}

	private const float PostRoomInfoEvery = 1f;
	private const float FadeTime = 2.0f;

	public static NetworkManagerGameOld Singleton;

	public bool serverOnlyIfEditor;

	public bool visualizeAuthority;
	public TogglePause togglePause;

	[HideInInspector] public bool roomOpen;

	private readonly bool[] _spotInUse = new bool[2];

	public readonly Dictionary<NetworkConnection, PlayerInfo> PlayerInfos =
		new Dictionary<NetworkConnection, PlayerInfo>();

	private Camera _camera;
	private DissonanceComms _comms;
	private ConnectionState _connectionState = ConnectionState.RelayError;
	private float _postRoomInfoLast;

	public ConnectionState State {
		get => _connectionState;
		set {
			if (_connectionState == value) {
				Debug.LogWarning("[BONSAI] Trying to set state to itself: " + State);
			}

			Debug.Log("[BONSAI] HandleState Cleanup " + _connectionState);
			HandleState(_connectionState, Work.Cleanup);

			_connectionState = value;

			Debug.Log("[BONSAI] HandleState Setup " + value);
			HandleState(value, Work.Setup);

			PostInfo();
		}
	}

	public override void Awake() {
		base.Awake();

		if (Singleton == null) {
			Singleton = this;
		}
	}

	public override void Start() {
		base.Start();

		TableBrowserMenu.Singleton.JoinRoom         += HandleJoinRoom;
		TableBrowserMenu.Singleton.LeaveRoom        += HandleLeaveRoom;
		TableBrowserMenu.Singleton.KickConnectionId += HandleKickConnectionId;

		TableBrowserMenu.Singleton.OpenRoom  += HandleOpenRoom;
		TableBrowserMenu.Singleton.CloseRoom += HandleCloseRoom;

		_camera = GameObject.Find("CenterEyeAnchor").GetComponent<Camera>();

		for (var i = 0; i < _spotInUse.Length; i++) {
			_spotInUse[i] = false;
		}

		State = ConnectionState.Loading;

		if (!Permission.HasUserAuthorizedPermission(Permission.Microphone)) {
			Permission.RequestUserPermission(Permission.Microphone);
		}

		_comms = GetComponent<DissonanceComms>();
		SetCommsActive(_comms, false);

		OVRManager.HMDUnmounted += VoidAndDeafen;

		MoveToDesk.OrientationChanged += HandleOrientationChange;

		if (Application.isEditor && !serverOnlyIfEditor) {
			StartCoroutine(StartXR());
		}
	}

	public override void Update() {
		base.Update();

		if (Time.time - _postRoomInfoLast > PostRoomInfoEvery) {
			PostInfo();
		}
	}

	private void OnApplicationFocus(bool focus) {
		if (MoveToDesk.Singleton.oriented) {
			SetCommsActive(_comms, focus);
		}
	}

	private void OnApplicationPause(bool pause) {
		if (!pause) {
			return;
		}

		SetCommsActive(_comms, false);
		MoveToDesk.Singleton.ResetPosition();
	}

	public override void OnApplicationQuit() {
		base.OnApplicationQuit();
		StopXR();
	}

	private void PostInfo() {
		if (TableBrowserMenu.Singleton.canPost) {
			_postRoomInfoLast = Time.time;
		#if UNITY_EDITOR || DEVELOPMENT_BUILD
			var build = "DEVELOPMENT";
		#else
			var build = "PRODUCTION";
		#endif

			TableBrowserMenu.Singleton.PostKvs(new[] {
				new TableBrowserMenu.KeyVal {Key = "build", Val = build}
			});
			TableBrowserMenu.Singleton.PostNetworkState(State.ToString());
			// todo TableBrowserMenu.Singleton.PostPlayerInfo(PlayerInfos);
			TableBrowserMenu.Singleton.PostRoomOpen(roomOpen);
			if (HostEndPoint != null) {
				TableBrowserMenu.Singleton.PostRoomInfo(HostEndPoint.Address.ToString(), HostEndPoint.Port.ToString());
			}
			else {
				TableBrowserMenu.Singleton.PostRoomInfo("", "");
			}
		}
	}

	private void HandleCloseRoom() {
		roomOpen = false;
		State    = ConnectionState.Loading;
		PostInfo();
	}

	private void HandleOpenRoom() {
		roomOpen = true;
		PostInfo();
	}

	private void HandleKickConnectionId(int id) {
		Debug.Log($"[BONSAI] Kick Id {id}");
		StartCoroutine(KickClient(id));
	}

	private void OnShouldDisconnect(ShouldDisconnectMessage _) {
		StartCoroutine(FadeThenReturnToLoading());
	}

	private static void SetCommsActive(DissonanceComms comms, bool active) {
		if (comms == null) {
			return;
		}

		if (active) {
			comms.IsMuted    = false;
			comms.IsDeafened = false;
		}
		else {
			comms.IsMuted    = true;
			comms.IsDeafened = true;
		}
	}

	private void HandleState(ConnectionState state, Work work) {
		switch (state) {
			case ConnectionState.RelayError:
				// set to loading to get out of here
				// you will get bounced back if there is no internet
				if (work == Work.Setup) {
					isLANOnly = true;
					Debug.Log("[BONSAI] RelayError Setup");
				}

				break;

			// Waiting for a HostEndPoint
			case ConnectionState.Loading:
				if (work == Work.Setup) {
					Debug.Log("[BONSAI] Loading Setup isLanOnly " + isLANOnly);

					if (client != null) {
						StopClient();
					}

					MoveToDesk.Singleton.SetTableEdge(GameObject.Find("DefaultEdge").transform);
					SetCommsActive(_comms, false);
					StartCoroutine(StartHostAfterDisconnect());
				}

				break;

			// Has a client connected
			case ConnectionState.Hosting:
				if (work == Work.Setup) {
					SetCommsActive(_comms, true);
				}
				else {
					StartCoroutine(KickClients());
				}

				break;

			// Client connected to a host
			case ConnectionState.ClientConnected:
				if (work == Work.Setup) {
					SetCommsActive(_comms, true);
				}
				else {
					client?.Disconnect();
					StopClient();
				}

				break;

			default:
				Debug.LogWarning($"[BONSAI] HandleState not handled {State}");
				break;
		}
	}

	private IEnumerator StartHostAfterDisconnect() {
		while (isDisconnecting) {
			yield return null;
		}

		if (HostEndPoint == null || isLANOnly) {
			Debug.Log("[BONSAI] StartHostAfterDisconnect StartHost ");
			isLANOnly = false;
			if (serverOnlyIfEditor && Application.isEditor) {
				roomOpen = true;
				StartServer();
			}
			else {
				StartHost();
			}
		}
		else {
			State = ConnectionState.Hosting;
		}
	}

	private IEnumerator SmoothStartClient() {
		State = ConnectionState.ClientConnecting;
		yield return new WaitForSeconds(FadeTime);
		Debug.Log("[BONSAI] SmoothStartClient StopHost");
		StopHost();
		if (HostEndPoint != null) {
			yield return null;
		}

		Debug.Log("[BONSAI] HostEndPoint == null");
		Debug.Log("[BONSAI] StartClient");
		StartClient();
	}

	private IEnumerator FadeThenReturnToLoading() {
		yield return new WaitForSeconds(FadeTime);
		State = ConnectionState.Loading;
	}

	private IEnumerator KickClients() {
		foreach (var conn in NetworkServer.connections.Values.ToList()
		                                  .Where(conn => conn.connectionId != NetworkConnection.LocalConnectionId)) {
			conn.Send(new ShouldDisconnectMessage());
		}

		yield return new WaitForSeconds(FadeTime + 0.15f);
		foreach (var conn in NetworkServer.connections.Values.ToList()
		                                  .Where(conn => conn.connectionId != NetworkConnection.LocalConnectionId)) {
			conn.Disconnect();
		}
	}

	private IEnumerator KickClient(int id) {
		NetworkConnectionToClient connToKick = null;
		foreach (var conn in NetworkServer.connections.Values.ToList()) {
			if (conn.connectionId != NetworkConnection.LocalConnectionId && conn.connectionId == id) {
				connToKick = conn;
				break;
			}
		}

		if (connToKick != null) {
			connToKick.Send(new ShouldDisconnectMessage());
			yield return new WaitForSeconds(FadeTime + 0.15f);
			connToKick.Disconnect();
		}

		PostInfo();
	}

	private void VoidAndDeafen() {
		MoveToDesk.Singleton.ResetPosition();
		SetCommsActive(_comms, false);
	}

	private void HandleOrientationChange(bool oriented) {
		if (oriented) {
			_camera.cullingMask |= 1 << LayerMask.NameToLayer("networkPlayer");
		}
		else {
			_camera.cullingMask &= ~(1 << LayerMask.NameToLayer("networkPlayer"));
		}

		if (State != ConnectionState.ClientConnected && State != ConnectionState.Hosting) {
			return;
		}

		SetCommsActive(_comms, oriented);
	}

	private void HandleJoinRoom(TableBrowserMenu.RoomData roomData) {
		Debug.Log($"[Bonsai] NetworkManager Join Room {roomData.ip_address} {roomData.port}");
		networkAddress = roomData.ip_address;
		networkPort    = roomData.port;
		StartCoroutine(SmoothStartClient());
	}

	private void HandleLeaveRoom() {
		StartCoroutine(FadeThenReturnToLoading());
	}

	public event Action<NetworkConnection> ServerAddPlayer;

	public event Action<NetworkConnection> ServerDisconnect;

	private IEnumerator StartXR() {
		Debug.Log("Initializing XR...");
		yield return XRGeneralSettings.Instance.Manager.InitializeLoader();

		if (XRGeneralSettings.Instance.Manager.activeLoader == null) {
			Debug.LogError("Initializing XR Failed. Check Editor or Player log for details.");
		}
		else {
			Debug.Log("Starting XR...");
			XRGeneralSettings.Instance.Manager.StartSubsystems();
		}
	}

	private void StopXR() {
		if (XRGeneralSettings.Instance.Manager.isInitializationComplete) {
			Debug.Log("Stopping XR...");

			XRGeneralSettings.Instance.Manager.StopSubsystems();
			XRGeneralSettings.Instance.Manager.DeinitializeLoader();
			Debug.Log("XR stopped completely.");
		}
	}

	public override void OnFatalError(string error) {
		base.OnFatalError(error);
		Debug.Log("[BONSAI] OnFatalError");
		Debug.Log(error);
		State = ConnectionState.RelayError;
	}

	public override void OnServerPrepared(string hostAddress, ushort hostPort) {
		Debug.Log($"[BONSAI] OnServerPrepared ({hostAddress} : {hostPort}) isLanOnly={isLANOnly}");
		State = !isLANOnly ? ConnectionState.Hosting : ConnectionState.RelayError;
	}

	public override void OnServerConnect(NetworkConnection conn) {
		Debug.Log("[BONSAI] OnServerConnect");

		base.OnServerConnect(conn);

		var openSpotId = -1;
		for (var i = 0; i < _spotInUse.Length; i++) {
			if (!_spotInUse[i]) {
				openSpotId = i;
				break;
			}
		}

		if (openSpotId == -1) {
			Debug.LogError("No open spot.");
			openSpotId = 0;
		}

		_spotInUse[openSpotId] = true;
		PlayerInfos.Add(conn, new PlayerInfo(openSpotId));

		// triggers when client joins
		if (NetworkServer.connections.Count > 1 && State != ConnectionState.Hosting) {
			State = ConnectionState.Hosting;
		}
	}

	public override void OnServerAddPlayer(NetworkConnection conn) {
		var connNetId = conn.identity ? conn.identity.netId.ToString() : "";
		Debug.Log($"[BONSAI] OnServerAddPlayer {conn.connectionId} {connNetId}");
		conn.Send(new SpotMessage {
			SpotId = PlayerInfos[conn].spot
		});

		base.OnServerAddPlayer(conn);

		if (State != ConnectionState.Hosting) {
			State = ConnectionState.Hosting;
		}

		ServerAddPlayer?.Invoke(conn);
	}

	public override void OnServerDisconnect(NetworkConnection conn) {
		Debug.Log("[BONSAI] OnServerDisconnect");

		if (!conn.isAuthenticated) {
			return;
		}

		ServerDisconnect?.Invoke(conn);

		var spotId = PlayerInfos[conn].spot;

		var spotUsedCount = 0;
		foreach (var player in PlayerInfos) {
			if (player.Value.spot == spotId) {
				spotUsedCount++;
			}
		}

		if (spotUsedCount <= 1) {
			_spotInUse[spotId] = false;
		}

		PlayerInfos.Remove(conn);

		var tmp = new HashSet<NetworkIdentity>(conn.clientOwnedObjects);
		foreach (var identity in tmp) {
			var autoAuthority = identity.GetComponent<AutoAuthority>();
			if (autoAuthority != null) {
				if (autoAuthority.InUse) {
					autoAuthority.SetInUse(false);
				}

				identity.RemoveClientAuthority();
			}
		}

		if (conn.identity != null && togglePause.AuthorityIdentityId == conn.identity.netId) {
			togglePause.RemoveClientAuthority();
		}

		base.OnServerDisconnect(conn);

		// triggers when last client leaves
		if (NetworkServer.connections.Count == 1) {
			State = ConnectionState.Loading;
		}
	}

	public override void OnClientConnect(NetworkConnection conn) {
		Debug.Log("[BONSAI] OnClientConnect");

		// For some reason OnClientConnect triggers twice occasionally. This is a hack to ignore the second trigger.
		if (conn.isReady) {
			return;
		}

		base.OnClientConnect(conn);

		NetworkClient.RegisterHandler<SpotMessage>(OnSpot);
		NetworkClient.RegisterHandler<ShouldDisconnectMessage>(OnShouldDisconnect);

		// triggers when client connects to remote host
		if (NetworkServer.connections.Count == 0) {
			State = ConnectionState.ClientConnected;
		}
	}

	public override void OnClientDisconnect(NetworkConnection conn) {
		Debug.Log("[BONSAI] OnClientDisconnect");

		NetworkClient.UnregisterHandler<SpotMessage>();
		NetworkClient.UnregisterHandler<ShouldDisconnectMessage>();

		switch (State) {
			case ConnectionState.ClientConnected:
				// this happens on client when the host exits rudely (power off, etc)
				// base method stops client with a delay so it can gracefully disconnect
				// since the client is getting booted here, we don't need to wait (which introduces bugs)

				State = ConnectionState.Loading;
				break;
			case ConnectionState.ClientConnecting:
				//this should happen on client trying to connect to a paused host
				StopClient();
				State = ConnectionState.Loading;
				break;
			case ConnectionState.RelayError:
				break;
			case ConnectionState.Loading:
				break;
			case ConnectionState.Hosting:
				break;
			default:
				base.OnClientDisconnect(conn);
				break;
		}
	}

	private static void OnSpot(NetworkConnection conn, SpotMessage msg) {
		switch (msg.SpotId) {
			case 0:
				GameObject.Find("GameManager").GetComponent<MoveToDesk>()
				          .SetTableEdge(GameObject.Find("DefaultEdge").transform);
				break;
			case 1:
				GameObject.Find("GameManager").GetComponent<MoveToDesk>()
				          .SetTableEdge(GameObject.Find("AcrossEdge").transform);
				break;
		}
	}

	public void UpdateUserInfo(uint netId, UserInfo userInfo) {
		foreach (var conn in PlayerInfos.Keys.ToList()) {
			if (netId == conn.identity.netId) {
				PlayerInfos[conn].User = userInfo;
			}
		}
	}

	[Serializable]
	public class PlayerInfo {
		public int spot;
		public UserInfo User;

		public PlayerInfo(int spot) {
			this.spot = spot;
			User      = new UserInfo("User");
		}
	}

	private enum Work {
		Setup,
		Cleanup
	}

	private struct ShouldDisconnectMessage : NetworkMessage { }

	private struct SpotMessage : NetworkMessage {
		public int SpotId;
	}

	public readonly struct UserInfo {
		public readonly string DisplayName;

		public UserInfo(string displayName) {
			DisplayName = displayName;
		}
	}
}